#import <AVFoundation/AVFoundation.h>
#import <CoreAudio/CoreAudio.h>

NS_ASSUME_NONNULL_BEGIN
/// userInfo key of the failing step ("Device selection", "Initialization", …).
FOUNDATION_EXPORT NSString *const MNAudioPhaseKey;
/// NSOSStatusErrorDomain error carrying the real Core Audio status and the failing phase.
FOUNDATION_EXPORT NSError *MNAudioError(NSString *phase, OSStatus status);
/// Same, with a user-facing description and the original error (e.g. AVAudioEngine's).
FOUNDATION_EXPORT NSError *MNAudioErrorDetailed(NSString *phase, OSStatus status, NSString *_Nullable description, NSError *_Nullable underlying);
/// Status embedded in an AVFoundation exception reason ("… error -10868 …"), otherwise 'what'.
FOUNDATION_EXPORT OSStatus MNAudioStatusFromException(NSException *exception);

/// All operations, including disposal, belong to the caller's serial hardware queue.
@interface MNSafeMicrophone : NSObject
@property(nonatomic, readonly) AVAudioEngine *engine;
- (instancetype)initWithEngine:(AVAudioEngine *)engine;
- (BOOL)startWithDevice:(AudioDeviceID)device block:(AVAudioNodeTapBlock)block error:(NSError **)error;
/// Restarts a stopped engine on the originally selected device with a fresh native-format
/// tap. No-op while running or after stop; `restarted` reports whether a restart happened.
- (BOOL)restartIfStopped:(nullable BOOL *)restarted error:(NSError **)error;
- (void)stop;
@end
NS_ASSUME_NONNULL_END
#import "HALMicrophone.h"
#import "MNAudioRing.h"
