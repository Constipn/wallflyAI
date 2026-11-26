#import "SafeAudio.h"
#import <os/log.h>
#import <math.h>

NSString *const MNAudioPhaseKey = @"MNAudioPhase";
static NSString *MNFourCC(OSStatus status) {
    uint32_t value = (uint32_t)status; char text[5] = {0};
    for (int i = 0; i < 4; i++) { char c = (char)(value >> (24 - 8 * i)); if (c < 32 || c > 126) return @""; text[i] = c; }
    return [NSString stringWithFormat:@" '%s'", text];
}
NSError *MNAudioErrorDetailed(NSString *phase, OSStatus status, NSString *description, NSError *underlying) {
    NSMutableDictionary *info = [@{MNAudioPhaseKey: phase, NSLocalizedDescriptionKey: description ?:
        [NSString stringWithFormat:@"%@ failed (Core Audio %d%@).", phase, (int)status, MNFourCC(status)]} mutableCopy];
    if (underlying) info[NSUnderlyingErrorKey] = underlying;
    return [NSError errorWithDomain:NSOSStatusErrorDomain code:status userInfo:info];
}
NSError *MNAudioError(NSString *phase, OSStatus status) { return MNAudioErrorDetailed(phase, status, nil, nil); }
OSStatus MNAudioStatusFromException(NSException *exception) {
    NSString *reason = exception.reason ?: @"";
    NSRegularExpression *pattern = [NSRegularExpression regularExpressionWithPattern:@"error[ =:]*(-?[0-9]+)" options:NSRegularExpressionCaseInsensitive error:nil];
    NSTextCheckingResult *match = [pattern firstMatchInString:reason options:0 range:NSMakeRange(0, reason.length)];
    if (match) return (OSStatus)[[reason substringWithRange:[match rangeAtIndex:1]] longLongValue];
    return kAudioHardwareUnspecifiedError;
}
/// Keeps the real status of AVFoundation/Core Audio errors; anything else becomes 'what'.
static NSError *MNWrap(NSString *phase, NSError *error) {
    BOOL status = [error.domain isEqual:NSOSStatusErrorDomain] || [error.domain isEqual:@"com.apple.coreaudio.avfaudio"];
    return MNAudioErrorDetailed(phase, status ? (OSStatus)error.code : kAudioHardwareUnspecifiedError, nil, error);
}

@implementation MNSafeMicrophone {
    BOOL _tapInstalled;
    BOOL _used;
    BOOL _stopped;
    AudioDeviceID _device;
    AVAudioNodeTapBlock _block;
}
- (instancetype)initWithEngine:(AVAudioEngine *)engine {
    if ((self = [super init])) _engine = engine;
    return self;
}
- (OSStatus)configureDevice:(AudioDeviceID)device input:(AVAudioInputNode *)input {
    AudioUnit unit = input.audioUnit;
    return unit ? AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice,
        kAudioUnitScope_Global, 0, &device, sizeof(device)) : kAudio_ParamError;
}
- (AudioDeviceID)currentDeviceOfInput:(AVAudioInputNode *)input {
    AudioUnit unit = input.audioUnit;
    AudioDeviceID device = kAudioObjectUnknown; UInt32 size = sizeof(device);
    if (!unit || AudioUnitGetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &device, &size) != noErr) return kAudioObjectUnknown;
    return device;
}
- (BOOL)startWithDevice:(AudioDeviceID)device block:(AVAudioNodeTapBlock)block error:(NSError **)error {
    NSString *phase = @"Device selection";
    double rate = 0; unsigned channels = 0;
    @try {
        if (_used) {
            if (error) *error = MNAudioErrorDetailed(@"Microphone start", kAudioHardwareIllegalOperationError, @"The microphone was already started or stopped.", nil);
            return NO;
        }
        _used = YES; _device = device; _block = [block copy];
        AVAudioInputNode *input = _engine.inputNode;
        OSStatus status = [self configureDevice:device input:input];
        if (status != noErr) {
            if (error) *error = MNAudioErrorDetailed(phase, status, @"Could not select the microphone.", nil);
            [self stop]; return NO;
        }
        phase = @"Audio format";
        AVAudioFormat *format = [input outputFormatForBus:0];
        rate = format.sampleRate; channels = format.channelCount;
        if (!isfinite(rate) || rate <= 0 || channels == 0) {
            if (error) *error = MNAudioErrorDetailed(phase, kAudioUnitErr_FormatNotSupported, @"No valid microphone signal. Check the selected microphone.", nil);
            [self stop]; return NO;
        }
        phase = @"Microphone tap";
        // nil keeps the current native bus format; never reapply a stale format snapshot.
        AVAudioFrameCount frames = (AVAudioFrameCount)ceil(rate * 0.1);
        if (@available(macOS 27.0, *)) {
            NSError *tapError;
            if (![input installTapOnBus:0 bufferSize:frames format:nil error:&tapError block:block]) {
                if (error) *error = MNWrap(phase, tapError);
                [self stop]; return NO;
            }
        } else {
            [input installTapOnBus:0 bufferSize:frames format:nil block:block];
        }
        _tapInstalled = YES;
        phase = @"Microphone start";
        [_engine prepare];
        NSError *startError;
        if (![_engine startAndReturnError:&startError]) { if (error) *error = MNWrap(phase, startError); [self stop]; return NO; }
        return YES;
    } @catch (NSException *exception) {
        os_log_error(OS_LOG_DEFAULT, "Microphone failure: phase=%{public}@ device=%u rate=%f channels=%u exception=%{public}@ reason=%{public}@", phase, device, rate, channels, exception.name, exception.reason);
        if (error) *error = MNAudioErrorDetailed(phase, MNAudioStatusFromException(exception),
            [NSString stringWithFormat:@"%@ failed. Check the microphone and start recording again.", phase], nil);
        [self stop]; return NO;
    }
}
// A configuration change can stop the engine before its first buffer. Restart the same
// engine on its current device; the tap is reinstalled because the native format may differ.
- (BOOL)restartIfStopped:(BOOL *)restarted error:(NSError **)error {
    if (restarted) *restarted = NO;
    if (_stopped || !_block || _engine.isRunning) return YES;
    NSString *phase = @"Microphone tap";
    @try {
        AVAudioInputNode *input = _engine.inputNode;
        if (_tapInstalled) { _tapInstalled = NO; [input removeTapOnBus:0]; }
        phase = @"Device selection";
        if ([self currentDeviceOfInput:input] != _device) {
            OSStatus status = [self configureDevice:_device input:input];
            if (status != noErr) {
                if (error) *error = MNAudioErrorDetailed(phase, status, @"Could not select the microphone.", nil);
                return NO;
            }
        }
        phase = @"Microphone tap";
        double rate = [input outputFormatForBus:0].sampleRate;
        if (!isfinite(rate) || rate <= 0) {
            if (error) *error = MNAudioErrorDetailed(phase, kAudioUnitErr_FormatNotSupported, @"No valid microphone signal. Check the selected microphone.", nil);
            return NO;
        }
        AVAudioFrameCount frames = (AVAudioFrameCount)ceil(rate * 0.1);
        if (@available(macOS 27.0, *)) {
            NSError *tapError;
            if (![input installTapOnBus:0 bufferSize:frames format:nil error:&tapError block:_block]) { if (error) *error = MNWrap(phase, tapError); return NO; }
        } else {
            [input installTapOnBus:0 bufferSize:frames format:nil block:_block];
        }
        _tapInstalled = YES;
        phase = @"Microphone restart";
        [_engine prepare];
        NSError *startError;
        if (![_engine startAndReturnError:&startError]) { if (error) *error = MNWrap(phase, startError); return NO; }
        if (restarted) *restarted = YES;
        return YES;
    } @catch (NSException *exception) {
        os_log_error(OS_LOG_DEFAULT, "Microphone restart failure: phase=%{public}@ device=%u exception=%{public}@ reason=%{public}@", phase, _device, exception.name, exception.reason);
        if (error) *error = MNAudioErrorDetailed(phase, MNAudioStatusFromException(exception),
            [NSString stringWithFormat:@"%@ failed. Check the microphone and start recording again.", phase], nil);
        return NO;
    }
}
- (void)stop {
    _used = YES; _stopped = YES;
    @try { [_engine stop]; } @catch (NSException *e) { os_log_error(OS_LOG_DEFAULT, "Microphone stop: %{public}@", e.reason); }
    if (_tapInstalled) {
        _tapInstalled = NO;
        @try { [_engine.inputNode removeTapOnBus:0]; } @catch (NSException *e) { os_log_error(OS_LOG_DEFAULT, "Microphone remove tap: %{public}@", e.reason); }
    }
}
@end
