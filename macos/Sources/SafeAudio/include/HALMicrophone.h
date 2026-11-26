#import <AVFoundation/AVFoundation.h>
#import <CoreAudio/CoreAudio.h>

NS_ASSUME_NONNULL_BEGIN
/// Averages `count` channels into `output`. AVAudioConverter cannot downmix discrete layouts with
/// more than two channels, so the AUHAL drain folds such devices to mono itself.
FOUNDATION_EXPORT void MNAudioDownmix(const float *_Nonnull const *_Nonnull channels, uint32_t count, uint32_t frames, float *output);
typedef void (^MNHALMicrophoneHandler)(AVAudioPCMBuffer *buffer, double hostSeconds);
typedef void (^MNHALMicrophoneFailure)(NSError *error);

/// Input-only AUHAL in Chromium's order: EnableIO in=1/out=0, device after EnableIO, client format
/// Float32 non-interleaved at the hardware rate and channel count, AudioUnitRender in the input
/// callback. No AVAudioEngine default-device aggregate and no Bluetooth format negotiation.
/// The IO thread only renders into preallocated buffers and a lock-free ring; a 10ms drain on the
/// caller's queue delivers `AVAudioPCMBuffer`s with the host time of their first sample.
/// All methods except the counters run on that queue, including disposal via `stop`.
@interface MNHALMicrophone : NSObject
- (instancetype)initWithDevice:(AudioDeviceID)device NS_DESIGNATED_INITIALIZER;
- (instancetype)init NS_UNAVAILABLE;
@property(nonatomic, readonly) AudioDeviceID device;
/// Delivery format: the hardware rate and channel count, folded to mono above two channels.
@property(nonatomic, readonly, nullable) AVAudioFormat *format;
/// Thread-safe counters.
@property(nonatomic, readonly) uint64_t renderErrors;
@property(nonatomic, readonly) uint64_t overruns;
@property(nonatomic, readonly) uint64_t callbacks;
@property(nonatomic, readonly) uint32_t epoch;
/// Called on the queue when a format change, stop or device loss cannot be repaired in place.
@property(nonatomic, copy, nullable) MNHALMicrophoneFailure failureHandler;
- (BOOL)startWithQueue:(dispatch_queue_t)queue handler:(MNHALMicrophoneHandler)handler error:(NSError **)error;
/// Stop → Uninitialize → new client format → Initialize → Start when the hardware format changed.
- (BOOL)reconfigureIfFormatChanged:(nullable BOOL *)changed error:(NSError **)error;
- (BOOL)restartIfStopped:(nullable BOOL *)restarted error:(NSError **)error;
- (void)stop;
@end
NS_ASSUME_NONNULL_END
