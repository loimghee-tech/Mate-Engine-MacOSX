/*
 * MateMacAudio.m
 *
 * System-audio level probe for the macOS port.
 *
 * STATUS: seam implemented, tap intentionally NOT implemented yet.
 * ------------------------------------------------------------------------
 * Why this file exists and why it is a stub
 * ------------------------------------------------------------------------
 * The Windows build decides "is the user playing music?" by walking WASAPI
 * audio sessions and reading `AudioMeterInformation.MasterPeakValue` per
 * session (AvatarAnimatorController.IsValidAppPlaying, AllowedAppsManager).
 * That is a Windows-only API with no macOS analogue in CoreAudio's public
 * surface -- `kAudioDevicePropertyDeviceIsRunningSomewhere` only tells you
 * *that* the device is running, not how loud, and not which process.
 *
 * The correct modern replacement is a CoreAudio *process tap* (macOS 14.2+):
 * CATapDescription + AudioHardwareCreateProcessTap, wrapped in a private
 * aggregate device, with an IOProc that copies the tap's float32 frames and
 * reduces them to a peak. That is ~200 lines of realtime-thread code that
 * cannot be verified without a Mac and a signed app, so it is deliberately
 * left out of this first cut rather than shipped untested.
 *
 * Until it lands, `MMN_AudioGetOutputPeak()` returns -1, and the C# side treats
 * -1 as "unknown" and does not auto-dance. Everything else in the port works.
 *
 * ------------------------------------------------------------------------
 * Implementation recipe (fill in MMN_AudioStart / Stop / GetOutputPeak)
 * ------------------------------------------------------------------------
 *  1. Availability gate
 *       if (@available(macOS 14.2, *)) { ... } else return -1;
 *
 *  2. Describe the tap, excluding our own process so the avatar never dances
 *     to her own voice lines:
 *       CATapDescription* desc =
 *           [[CATapDescription alloc] initStereoGlobalTapButExcludeProcesses:@[
 *               [[NSNumber alloc] initWithInt:getpid()]]];
 *       desc.muteBehavior = CATapUnmuted;
 *       desc.privateTap = YES;
 *
 *  3. Create it:
 *       AudioObjectID tapId = 0;
 *       OSStatus st = AudioHardwareCreateProcessTap(desc, &tapId);
 *     Worst case this returns kAudioHardwareIllegalOperationError when the user
 *     denied "System Audio Recording Only" (macOS 15+); surface that as -1 so
 *     the UI can prompt.
 *
 *  4. Wrap it in an aggregate device so it produces IO:
 *       NSDictionary* agg = @{
 *           @kAudioAggregateDeviceNameKey        : @"MateEngineTap",
 *           @kAudioAggregateDeviceUIDKey         : @"com.shinymoon.mateengine.tap",
 *           @kAudioAggregateDeviceMainSubDeviceKey : @"" /* no real device */,
 *           @kAudioAggregateDeviceIsPrivateKey   : @YES,
 *           @kAudioAggregateDeviceTapListKey     : @[ @{
 *               @kAudioSubTapUIDKey          : desc.UUID.UUIDString,
 *               @kAudioSubTapDriftCompensationKey : @YES,
 *           } ],
 *       };
 *       AudioHardwareCreateAggregateDevice((__bridge CFDictionaryRef)agg, &gAggId);
 *
 *  5. Install an IOProc on gAggId whose block converts float32 to a peak and
 *     stores it in an std::atomic-like double (use OSAtomic / a locking store;
 *     the game only reads it at 2 Hz so a relaxed store is fine):
 *       AudioDeviceCreateIOProcIDWithBlock(&gProc, gAggId, NULL,
 *           ^(AudioObjectID inDevice, const AudioTimeStamp* now,
 *             const AudioBufferList* inInputData, ...) {
 *               /* max |sample| across inInputData -> gPeak */
 *           });
 *       AudioDeviceStart(gAggId, gProc);
 *
 *  6. Tear down in MMN_AudioStop, in this order: AudioDeviceStop,
 *     AudioDeviceDestroyIOProcID, AudioHardwareDestroyAggregateDevice,
 *     AudioHardwareDestroyProcessTap.
 *
 *  7. Per-app filtering (the `allowedApps` list in the game): create one tap
 *     per app instead of a global tap, using
 *     `initStereoMixdownOfProcesses:` with the app's AudioObjectID, resolved
 *     from pid via kAudioHardwarePropertyTranslatePIDToProcessObject. Keep the
 *     number of simultaneous taps small -- the system caps them.
 *
 * Fallback for macOS 11-14.1: ship a loopback device (BlackHole) preset, or
 * simply hide the "dance to music" toggle on older systems.
 */

#import <Foundation/Foundation.h>
#import <CoreAudio/CoreAudio.h>
#import <AudioToolbox/AudioToolbox.h>

#include <unistd.h>

#include "MateMacNative.h"

int32_t MMN_AudioTapSupported(void)
{
#if defined(__MAC_OS_X_VERSION_MAX_ALLOWED) && __MAC_OS_X_VERSION_MAX_ALLOWED >= 140200
    if (@available(macOS 14.2, *)) return 1;
#endif
    return 0;
}

int32_t MMN_AudioStart(void)
{
    /* Not implemented -- see the recipe above. */
    return -1;
}

void MMN_AudioStop(void)
{
    /* Nothing to release yet. */
}

double MMN_AudioGetOutputPeak(void)
{
    /* -1 means "unknown"; the C# wrapper maps this to "do not auto-dance". */
    return -1.0;
}
