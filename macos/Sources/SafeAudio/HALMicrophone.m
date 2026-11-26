#import "HALMicrophone.h"
#import "MNAudioRing.h"
#import "SafeAudio.h"
#import <AudioToolbox/AudioToolbox.h>
#import <mach/mach_time.h>
#import <os/log.h>
#import <stdatomic.h>

/// One configuration's IO buffers; replaced only while the unit is stopped and uninitialized.
typedef struct {
    uint32_t channels, maxFrames;
    float **data;
    AudioBufferList *list;
} MNHALBuffers;

/// Everything the IO thread touches. Lives as long as the MNHALMicrophone object.
typedef struct {
    AudioUnit unit;
    _Atomic(MNHALBuffers *) buffers;
    _Atomic(MNAudioRing *) ring;
    _Atomic uint32_t epoch;
    _Atomic uint64_t renderErrors, overruns, callbacks;
    _Atomic int32_t lastRenderStatus;
} MNHALShared;

static MNHALBuffers *MNHALBuffersCreate(uint32_t channels, uint32_t maxFrames) {
    MNHALBuffers *buffers = calloc(1, sizeof(MNHALBuffers));
    if (!buffers) return NULL;
    buffers->channels = channels; buffers->maxFrames = maxFrames;
    buffers->data = calloc(channels, sizeof(float *));
    buffers->list = calloc(1, offsetof(AudioBufferList, mBuffers) + sizeof(AudioBuffer) * channels);
    bool ok = buffers->data && buffers->list;
    for (uint32_t c = 0; ok && c < channels; c++) ok = (buffers->data[c] = calloc(maxFrames, sizeof(float))) != NULL;
    if (!ok) {
        if (buffers->data) for (uint32_t c = 0; c < channels; c++) free(buffers->data[c]);
        free(buffers->data); free(buffers->list); free(buffers); return NULL;
    }
    buffers->list->mNumberBuffers = channels;
    return buffers;
}
static void MNHALBuffersDestroy(MNHALBuffers *buffers) {
    if (!buffers) return;
    for (uint32_t c = 0; c < buffers->channels; c++) free(buffers->data[c]);
    free(buffers->data); free(buffers->list); free(buffers);
}

// Real-time IO thread: no allocation, locks or Objective-C.
static OSStatus MNHALInput(void *refCon, AudioUnitRenderActionFlags *flags, const AudioTimeStamp *time,
                           UInt32 bus, UInt32 frames, AudioBufferList *unused) {
    MNHALShared *shared = refCon;
    MNHALBuffers *buffers = atomic_load_explicit(&shared->buffers, memory_order_acquire);
    MNAudioRing *ring = atomic_load_explicit(&shared->ring, memory_order_acquire);
    if (!buffers || !ring) return noErr;
    if (frames == 0 || frames > buffers->maxFrames) {
        atomic_fetch_add_explicit(&shared->renderErrors, 1, memory_order_relaxed);
        return noErr;
    }
    for (uint32_t c = 0; c < buffers->channels; c++) {
        buffers->list->mBuffers[c] = (AudioBuffer){1, frames * (UInt32)sizeof(float), buffers->data[c]};
    }
    OSStatus status = AudioUnitRender(shared->unit, flags, time, bus, frames, buffers->list);
    if (status != noErr) {
        atomic_fetch_add_explicit(&shared->renderErrors, 1, memory_order_relaxed);
        atomic_store_explicit(&shared->lastRenderStatus, status, memory_order_relaxed);
        return noErr;
    }
    uint64_t host = time && (time->mFlags & kAudioTimeStampHostTimeValid) ? time->mHostTime : mach_absolute_time();
    if (!MNAudioRingWrite(ring, (const float *const *)buffers->data, frames, host, atomic_load_explicit(&shared->epoch, memory_order_relaxed)))
        atomic_fetch_add_explicit(&shared->overruns, 1, memory_order_relaxed);
    atomic_fetch_add_explicit(&shared->callbacks, 1, memory_order_relaxed);
    return noErr;
}

@interface MNHALMicrophone ()
- (void)unitPropertyChanged:(AudioUnitPropertyID)property;
@end

/// AU listener context; it outlives listener removal so a late callback never touches freed memory.
@interface MNHALListenerBox : NSObject
@property(nonatomic, weak) MNHALMicrophone *owner;
@property(atomic, strong, nullable) dispatch_queue_t queue;
@end
@implementation MNHALListenerBox
@end

static void MNHALPropertyChanged(void *refCon, AudioUnit unit, AudioUnitPropertyID property, AudioUnitScope scope, AudioUnitElement element) {
    // Only the hardware (device) side of the input element matters; our own client-format changes do not.
    if (property == kAudioUnitProperty_StreamFormat && !(scope == kAudioUnitScope_Input && element == 1)) return;
    MNHALListenerBox *box = (__bridge MNHALListenerBox *)refCon;
    dispatch_queue_t queue = box.queue;
    if (!queue) return;
    __weak MNHALMicrophone *owner = box.owner;
    dispatch_async(queue, ^{ [owner unitPropertyChanged:property]; });
}

void MNAudioDownmix(const float *const *channels, uint32_t count, uint32_t frames, float *output) {
    float scale = count > 0 ? 1.0f / (float)count : 0;
    for (uint32_t i = 0; i < frames; i++) {
        float sum = 0;
        for (uint32_t c = 0; c < count; c++) sum += channels[c][i];
        output[i] = sum * scale;
    }
}

static AVAudioFormat *MNHALFormat(double rate, uint32_t channels) {
    if (channels <= 2) return [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32 sampleRate:rate channels:channels interleaved:NO];
    AVAudioChannelLayout *layout = [AVAudioChannelLayout layoutWithLayoutTag:kAudioChannelLayoutTag_DiscreteInOrder | channels];
    return [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32 sampleRate:rate interleaved:NO channelLayout:layout];
}

@implementation MNHALMicrophone {
    MNHALShared *_shared;
    MNHALListenerBox *_box;
    dispatch_queue_t _queue;
    MNHALMicrophoneHandler _handler;
    dispatch_source_t _drain;
    NSMutableArray *_deviceListeners; // @[NSValue(address), block]
    AVAudioFormat *_captureFormat; // the AU client format; `format` is what the handler receives
    double _rate;
    uint32_t _channels;
    BOOL _started, _stopped, _initialized, _reconfigurePending, _failed;
}

- (instancetype)initWithDevice:(AudioDeviceID)device {
    if ((self = [super init])) {
        _device = device;
        _shared = calloc(1, sizeof(MNHALShared));
        _box = [MNHALListenerBox new]; _box.owner = self;
        _deviceListeners = [NSMutableArray array];
    }
    return self;
}
- (void)dealloc {
    [self stop];
    free(_shared);
}
- (uint64_t)renderErrors { return _shared ? atomic_load_explicit(&_shared->renderErrors, memory_order_relaxed) : 0; }
- (uint64_t)overruns { return _shared ? atomic_load_explicit(&_shared->overruns, memory_order_relaxed) : 0; }
- (uint64_t)callbacks { return _shared ? atomic_load_explicit(&_shared->callbacks, memory_order_relaxed) : 0; }
- (uint32_t)epoch { return _shared ? atomic_load_explicit(&_shared->epoch, memory_order_relaxed) : 0; }

- (BOOL)fail:(NSError **)error phase:(NSString *)phase status:(OSStatus)status {
    os_log_error(OS_LOG_DEFAULT, "HAL microphone failure: phase=%{public}@ device=%u status=%d", phase, _device, (int)status);
    if (error) *error = MNAudioError(phase, status);
    return NO;
}

- (BOOL)startWithQueue:(dispatch_queue_t)queue handler:(MNHALMicrophoneHandler)handler error:(NSError **)error {
    if (_started || _stopped || !_shared) return [self fail:error phase:@"Microphone start" status:kAudioHardwareIllegalOperationError];
    _started = YES; _queue = queue; _handler = [handler copy]; _box.queue = queue;
    AudioComponentDescription description = {kAudioUnitType_Output, kAudioUnitSubType_HALOutput, kAudioUnitManufacturer_Apple, 0, 0};
    AudioComponent component = AudioComponentFindNext(NULL, &description);
    if (!component) { [self stop]; return [self fail:error phase:@"Opening the microphone" status:kAudioHardwareUnspecifiedError]; }
    OSStatus status = AudioComponentInstanceNew(component, &_shared->unit);
    if (status != noErr) { _shared->unit = NULL; [self stop]; return [self fail:error phase:@"Opening the microphone" status:status]; }
    AudioUnit unit = _shared->unit;
    UInt32 enable = 1, disable = 0;
    status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1, &enable, sizeof(enable));
    if (status == noErr) status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0, &disable, sizeof(disable));
    if (status != noErr) { [self stop]; return [self fail:error phase:@"Enabling input" status:status]; }
    // The device is set only after EnableIO, as Chromium does; before, AUHAL binds the default output.
    AudioDeviceID device = _device;
    status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &device, sizeof(device));
    if (status != noErr) { [self stop]; return [self fail:error phase:@"Device selection" status:status]; }
    AURenderCallbackStruct callback = {MNHALInput, _shared};
    status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 0, &callback, sizeof(callback));
    if (status != noErr) { [self stop]; return [self fail:error phase:@"Input callback" status:status]; }
    AudioUnitAddPropertyListener(unit, kAudioUnitProperty_StreamFormat, MNHALPropertyChanged, (__bridge void *)_box);
    AudioUnitAddPropertyListener(unit, kAudioOutputUnitProperty_IsRunning, MNHALPropertyChanged, (__bridge void *)_box);
    [self observeDevice];
    if (![self configureAndStart:error]) { [self stop]; return NO; }
    __weak MNHALMicrophone *weakSelf = self;
    _drain = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, queue);
    dispatch_source_set_timer(_drain, dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_MSEC), 10 * NSEC_PER_MSEC, 2 * NSEC_PER_MSEC);
    dispatch_source_set_event_handler(_drain, ^{ [weakSelf drain]; });
    dispatch_resume(_drain);
    return YES;
}

- (void)observeDevice {
    __weak MNHALMicrophone *weakSelf = self;
    AudioObjectPropertyListenerBlock format = ^(UInt32 count, const AudioObjectPropertyAddress *addresses) { [weakSelf scheduleReconfigure]; };
    AudioObjectPropertyListenerBlock alive = ^(UInt32 count, const AudioObjectPropertyAddress *addresses) { [weakSelf checkAlive]; };
    AudioObjectPropertyAddress addresses[] = {
        {kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain},
        {kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeInput, kAudioObjectPropertyElementMain},
        {kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain},
    };
    for (int i = 0; i < 3; i++) {
        AudioObjectPropertyListenerBlock block = i == 2 ? alive : format;
        if (AudioObjectAddPropertyListenerBlock(_device, &addresses[i], _queue, block) == noErr)
            [_deviceListeners addObject:@[[NSValue valueWithBytes:&addresses[i] objCType:@encode(AudioObjectPropertyAddress)], block]];
    }
}

- (BOOL)hardwareRate:(double *)rate channels:(uint32_t *)channels error:(NSError **)error {
    AudioStreamBasicDescription hardware = {0};
    UInt32 size = sizeof(hardware);
    OSStatus status = AudioUnitGetProperty(_shared->unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 1, &hardware, &size);
    if (status != noErr) return [self fail:error phase:@"Audio format" status:status];
    if (!isfinite(hardware.mSampleRate) || hardware.mSampleRate <= 0 || hardware.mChannelsPerFrame == 0)
        return [self fail:error phase:@"Audio format" status:kAudioUnitErr_FormatNotSupported];
    *rate = hardware.mSampleRate; *channels = hardware.mChannelsPerFrame;
    return YES;
}

- (BOOL)configureAndStart:(NSError **)error {
    AudioUnit unit = _shared->unit;
    double rate = 0; uint32_t channels = 0;
    if (![self hardwareRate:&rate channels:&channels error:error]) return NO;
    AVAudioFormat *format = MNHALFormat(rate, channels);
    AVAudioFormat *delivery = channels > 2 ? MNHALFormat(rate, 1) : format;
    if (!format || !delivery) return [self fail:error phase:@"Audio format" status:kAudioUnitErr_FormatNotSupported];
    AudioStreamBasicDescription client = {rate, kAudioFormatLinearPCM,
        kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved, 4, 1, 4, channels, 32, 0};
    OSStatus status = AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1, &client, sizeof(client));
    if (status != noErr) return [self fail:error phase:@"Audio format" status:status];
    UInt32 sliceFrames = 0, deviceFrames = 0, size = sizeof(UInt32);
    AudioUnitGetProperty(unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, &sliceFrames, &size);
    AudioObjectPropertyAddress bufferSize = {kAudioDevicePropertyBufferFrameSize, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    size = sizeof(UInt32);
    AudioObjectGetPropertyData(_device, &bufferSize, 0, NULL, &size, &deviceFrames);
    uint32_t maxFrames = MAX(4096u, MAX(sliceFrames, deviceFrames));
    MNHALBuffers *buffers = MNHALBuffersCreate(channels, maxFrames);
    // About 2s of audio; the drain empties it every 10ms.
    MNAudioRing *ring = MNAudioRingCreate(channels, (uint32_t)MAX(ceil(rate * 2), maxFrames * 2.0), 1024);
    if (!buffers || !ring) { MNHALBuffersDestroy(buffers); MNAudioRingDestroy(ring); return [self fail:error phase:@"Audio buffers" status:kAudio_MemFullError]; }
    atomic_fetch_add_explicit(&_shared->epoch, 1, memory_order_relaxed);
    atomic_store_explicit(&_shared->buffers, buffers, memory_order_release);
    atomic_store_explicit(&_shared->ring, ring, memory_order_release);
    _captureFormat = format; _format = delivery; _rate = rate; _channels = channels;
    status = AudioUnitInitialize(unit);
    if (status != noErr) return [self fail:error phase:@"Initialization" status:status];
    _initialized = YES;
    status = AudioOutputUnitStart(unit);
    if (status != noErr) return [self fail:error phase:@"Microphone start" status:status];
    return YES;
}

/// Stops the unit, delivers what the ring still holds and releases this configuration's buffers.
- (void)teardownConfiguration {
    if (_shared->unit) {
        AudioOutputUnitStop(_shared->unit);
        if (_initialized) AudioUnitUninitialize(_shared->unit);
    }
    _initialized = NO;
    if (!_stopped) [self drain];
    MNHALBuffersDestroy(atomic_exchange_explicit(&_shared->buffers, NULL, memory_order_acq_rel));
    MNAudioRingDestroy(atomic_exchange_explicit(&_shared->ring, NULL, memory_order_acq_rel));
}

- (BOOL)isUnitRunning {
    UInt32 running = 0, size = sizeof(running);
    if (!_shared->unit || AudioUnitGetProperty(_shared->unit, kAudioOutputUnitProperty_IsRunning, kAudioUnitScope_Global, 0, &running, &size) != noErr) return NO;
    return running != 0;
}

- (BOOL)reconfigureIfFormatChanged:(BOOL *)changed error:(NSError **)error {
    if (changed) *changed = NO;
    if (!_started || _stopped || !_shared->unit) return YES;
    double rate = 0; uint32_t channels = 0;
    if (![self hardwareRate:&rate channels:&channels error:error]) return NO;
    if (rate == _rate && channels == _channels && _initialized) return YES;
    os_log(OS_LOG_DEFAULT, "HAL microphone format change device=%u %.0fHz/%u -> %.0fHz/%u", _device, _rate, _channels, rate, channels);
    [self teardownConfiguration];
    if (changed) *changed = YES;
    return [self configureAndStart:error];
}

- (BOOL)restartIfStopped:(BOOL *)restarted error:(NSError **)error {
    if (restarted) *restarted = NO;
    if (!_started || _stopped || !_shared->unit || [self isUnitRunning]) return YES;
    BOOL changed = NO;
    if (![self reconfigureIfFormatChanged:&changed error:error]) return NO;
    if (!changed) {
        OSStatus status = AudioOutputUnitStart(_shared->unit);
        if (status != noErr) return [self fail:error phase:@"Microphone restart" status:status];
    }
    if (restarted) *restarted = YES;
    return YES;
}

- (void)report:(NSError *)error {
    if (_failed || _stopped) return;
    _failed = YES;
    MNHALMicrophoneFailure handler = _failureHandler;
    if (handler) handler(error);
}

- (void)unitPropertyChanged:(AudioUnitPropertyID)property {
    if (!_started || _stopped || _failed) return;
    if (property == kAudioUnitProperty_StreamFormat) { [self scheduleReconfigure]; return; }
    if (property != kAudioOutputUnitProperty_IsRunning || [self isUnitRunning]) return;
    NSError *error; BOOL restarted = NO;
    if (![self restartIfStopped:&restarted error:&error]) [self report:error];
    else if (restarted) os_log(OS_LOG_DEFAULT, "HAL microphone restarted after unexpected stop device=%u", _device);
}

- (void)scheduleReconfigure {
    if (!_started || _stopped || _failed || _reconfigurePending) return;
    _reconfigurePending = YES;
    __weak MNHALMicrophone *weakSelf = self;
    // Coalesce the burst of rate/stream/AU notifications one device change produces.
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 50 * NSEC_PER_MSEC), _queue, ^{
        MNHALMicrophone *strong = weakSelf;
        if (!strong) return;
        strong->_reconfigurePending = NO;
        if (strong->_stopped || strong->_failed) return;
        NSError *error;
        if (![strong reconfigureIfFormatChanged:NULL error:&error]) [strong report:error];
    });
}

- (void)checkAlive {
    if (!_started || _stopped || _failed) return;
    UInt32 alive = 1, size = sizeof(alive);
    AudioObjectPropertyAddress address = {kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    OSStatus status = AudioObjectGetPropertyData(_device, &address, 0, NULL, &size, &alive);
    if (status != noErr || alive == 0) [self report:MNAudioError(@"Device connection", status != noErr ? status : kAudioHardwareBadDeviceError)];
}

- (void)drain {
    if (!_shared) return;
    MNAudioRing *ring = atomic_load_explicit(&_shared->ring, memory_order_acquire);
    MNHALMicrophoneHandler handler = _handler;
    if (!ring) return;
    uint32_t epoch = atomic_load_explicit(&_shared->epoch, memory_order_relaxed);
    MNAudioRingBlock block;
    while (MNAudioRingPeek(ring, &block)) {
        AVAudioPCMBuffer *buffer = handler && _captureFormat && block.epoch == epoch && block.frames > 0
            ? [[AVAudioPCMBuffer alloc] initWithPCMFormat:_captureFormat frameCapacity:block.frames] : nil;
        if (!buffer || buffer.format.channelCount != MNAudioRingChannels(ring)) { MNAudioRingRead(ring, NULL, 0, &block); continue; }
        MNAudioRingRead(ring, buffer.floatChannelData, block.frames, &block);
        buffer.frameLength = block.frames;
        if (_format != _captureFormat) {
            AVAudioPCMBuffer *mono = [[AVAudioPCMBuffer alloc] initWithPCMFormat:_format frameCapacity:block.frames];
            if (!mono) continue;
            MNAudioDownmix((const float *const *)buffer.floatChannelData, buffer.format.channelCount, block.frames, mono.floatChannelData[0]);
            mono.frameLength = block.frames;
            buffer = mono;
        }
        handler(buffer, [AVAudioTime secondsForHostTime:block.hostTime]);
    }
}

- (void)stop {
    if (_stopped) return;
    _stopped = YES; _box.queue = nil;
    if (_drain) { dispatch_source_cancel(_drain); _drain = nil; }
    for (NSArray *entry in _deviceListeners) {
        AudioObjectPropertyAddress address; [entry[0] getValue:&address];
        AudioObjectRemovePropertyListenerBlock(_device, &address, _queue, entry[1]);
    }
    [_deviceListeners removeAllObjects];
    if (_shared && _shared->unit) {
        AudioUnitRemovePropertyListenerWithUserData(_shared->unit, kAudioUnitProperty_StreamFormat, MNHALPropertyChanged, (__bridge void *)_box);
        AudioUnitRemovePropertyListenerWithUserData(_shared->unit, kAudioOutputUnitProperty_IsRunning, MNHALPropertyChanged, (__bridge void *)_box);
        // A callback already in flight on a HAL thread may still read the box: keep it alive well
        // past removal and disposal instead of releasing it with this object.
        MNHALListenerBox *box = _box;
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC), dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{ (void)box; });
        [self teardownConfiguration];
        AudioComponentInstanceDispose(_shared->unit);
        _shared->unit = NULL;
    } else if (_shared) {
        MNHALBuffersDestroy(atomic_exchange_explicit(&_shared->buffers, NULL, memory_order_acq_rel));
        MNAudioRingDestroy(atomic_exchange_explicit(&_shared->ring, NULL, memory_order_acq_rel));
    }
    _handler = nil; _failureHandler = nil;
}
@end
