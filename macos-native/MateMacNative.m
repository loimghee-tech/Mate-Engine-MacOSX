/*
 * MateMacNative.m
 *
 * macOS backend for the MateEngine macOS port. See MateMacNative.h for the
 * contract and for the coordinate-system rules.
 *
 * Build with build.sh (produces a universal x86_64 + arm64 .bundle).
 * Must be called from the main thread; all UI-touching entry points are
 * expected to arrive there because Unity marshals everything to its main
 * thread, which is also the AppKit main thread.
 */

#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <ServiceManagement/ServiceManagement.h>

#include <string.h>
#include <stdlib.h>
#include <math.h>

#include "MateMacNative.h"

#pragma clang diagnostic ignored "-Wdeprecated-declarations"

static BOOL gInitialized = NO;
static id  gActivityToken = nil;   /* NSProcessInfoActivity keeping App Nap off */

/* ======================================================================== */
/* Small helpers                                                             */
/* ======================================================================== */

static double MMN_MainDisplayHeight(void)
{
    return (double)CGRectGetHeight(CGDisplayBounds(CGMainDisplayID()));
}

/* AppKit screens are bottom-left origin; the game wants top-left origin like
 * GetWindowRect(). Flip around the main display's height. */
static MMNRect MMN_RectFromNSRect(NSRect r)
{
    double h = MMN_MainDisplayHeight();
    MMNRect out;
    out.x = r.origin.x;
    out.y = h - (r.origin.y + r.size.height);
    out.width = r.size.width;
    out.height = r.size.height;
    return out;
}

static void MMN_CopyCString(char* dst, size_t dstSize, NSString* src)
{
    if (dstSize == 0) return;
    if (![src isKindOfClass:[NSString class]]) { dst[0] = '\0'; return; }
    const char* utf8 = [src UTF8String];
    if (!utf8) { dst[0] = '\0'; return; }
    strlcpy(dst, utf8, dstSize);
}

static NSScreen* MMN_ScreenForCGDisplay(uint32_t displayId)
{
    for (NSScreen* s in [NSScreen screens]) {
        NSNumber* num = s.deviceDescription[@"NSScreenNumber"];
        if (num && (uint32_t)[num unsignedIntValue] == displayId) return s;
    }
    return nil;
}

/* ======================================================================== */
/* Lifecycle                                                                 */
/* ======================================================================== */

int32_t MMN_GetApiVersion(void)
{
    return MMN_API_VERSION;
}

int32_t MMN_Initialize(void)
{
    if (gInitialized) return 0;
    if (![NSThread isMainThread]) {
        NSLog(@"[MateMacNative] MMN_Initialize must be called on the main thread");
        return -2;
    }
    @autoreleasepool {
        /* A desktop pet must keep animating while another app is focused and
         * must not be throttled when the window is not key. */
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
        gInitialized = YES;
    }
    return 0;
}

/* ======================================================================== */
/* Displays                                                                  */
/* ======================================================================== */

int32_t MMN_GetDisplayCount(void)
{
    uint32_t count = 0;
    if (CGGetActiveDisplayList(0, NULL, &count) != kCGErrorSuccess) return -1;
    return (int32_t)count;
}

int32_t MMN_GetDisplays(MMNDisplay* outDisplays, int32_t maxCount)
{
    if (!outDisplays || maxCount <= 0) return -1;

    uint32_t count = 0;
    if (CGGetActiveDisplayList((uint32_t)maxCount, NULL, &count) != kCGErrorSuccess) return -1;
    if (count == 0) return 0;

    CGDirectDisplayID ids[32];
    if (count > 32) count = 32;
    if (CGGetActiveDisplayList(count, ids, &count) != kCGErrorSuccess) return -1;

    int32_t written = 0;
    for (uint32_t i = 0; i < count; i++) {
        MMNDisplay* d = &outDisplays[written];
        memset(d, 0, sizeof(*d));
        d->displayId = ids[i];

        CGRect b = CGDisplayBounds(ids[i]);
        d->frame.x = b.origin.x;
        d->frame.y = b.origin.y;
        d->frame.width = b.size.width;
        d->frame.height = b.size.height;

        /* visibleFrame already excludes the menu bar, the Dock and the notch,
         * which is exactly what the "don't spawn under the menu bar / do sit on
         * the dock" logic wants. */
        NSScreen* screen = MMN_ScreenForCGDisplay(ids[i]);
        if (screen) {
            d->visibleFrame = MMN_RectFromNSRect([screen visibleFrame]);
            d->scale = (double)[screen backingScaleFactor];
        } else {
            d->visibleFrame = d->frame;
            d->scale = 1.0;
        }

        d->isPrimary = (ids[i] == CGMainDisplayID()) ? 1 : 0;
        d->isBuiltin = CGDisplayIsBuiltin(ids[i]) ? 1 : 0;
        written++;
    }
    return written;
}

double MMN_GetMenuBarHeight(void)
{
    NSScreen* main = [NSScreen mainScreen];
    if (!main) return 0.0;
    /* frame - visibleFrame height difference is the menu bar (+ notch inset). */
    double delta = NSHeight([main frame]) - NSHeight([main visibleFrame]);
    return delta > 0 ? delta : 0.0;
}

double MMN_GetDockThickness(int32_t displayIndex, int32_t* outEdge)
{
    if (outEdge) *outEdge = 3;

    NSArray<NSScreen*>* screens = [NSScreen screens];
    if (displayIndex < 0 || (NSUInteger)displayIndex >= screens.count) return 0.0;

    NSScreen* screen = screens[displayIndex];
    NSRect frame = [screen frame];
    NSRect visible = [screen visibleFrame];

    double bottom = visible.origin.y - frame.origin.y;
    double left = visible.origin.x - frame.origin.x;
    double right = NSMaxX(frame) - NSMaxX(visible);

    /* Pick the largest inset; all three are ~0 when the Dock is auto-hidden or
     * docked on another display. */
    double best = 0.0;
    int32_t edge = 3;
    if (bottom > best) { best = bottom; edge = 0; }
    if (left > best)   { best = left;   edge = 1; }
    if (right > best)  { best = right;  edge = 2; }

    if (outEdge) *outEdge = edge;
    return edge == 3 ? 0.0 : best;
}

/* ======================================================================== */
/* Windows                                                                   */
/* ======================================================================== */

static CFArrayRef MMN_CopyWindowArray(void)
{
    return CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly |
                                      kCGWindowListExcludeDesktopElements,
                                      kCGNullWindowID);
}

static void MMN_FillWindow(MMNWindow* out, NSDictionary* info)
{
    memset(out, 0, sizeof(*out));

    NSNumber* num = info[(id)kCGWindowNumber];
    if (num) out->windowId = (uint32_t)[num unsignedIntValue];

    num = info[(id)kCGWindowOwnerPID];
    if (num) out->ownerPid = (int32_t)[num intValue];

    num = info[(id)kCGWindowLayer];
    if (num) out->layer = (int32_t)[num intValue];

    num = info[(id)kCGWindowAlpha];
    if (num) out->alpha = [num doubleValue];

    num = info[(id)kCGWindowIsOnscreen];
    if (num) out->isOnScreen = [num boolValue] ? 1 : 0;

    NSDictionary* bounds = info[(id)kCGWindowBounds];
    if ([bounds isKindOfClass:[NSDictionary class]]) {
        CGRect r = CGRectZero;
        if (CGRectMakeWithDictionaryRepresentation((__bridge CFDictionaryRef)bounds, &r)) {
            out->bounds.x = r.origin.x;
            out->bounds.y = r.origin.y;
            out->bounds.width = r.size.width;
            out->bounds.height = r.size.height;
        }
    }

    /* Owner name needs no TCC permission. Title does: it comes back absent
     * without Screen Recording, and we leave it empty in that case. */
    MMN_CopyCString(out->ownerName, sizeof(out->ownerName), info[(id)kCGWindowOwnerName]);
    MMN_CopyCString(out->title, sizeof(out->title), info[(id)kCGWindowName]);
}

int32_t MMN_GetWindowCount(void)
{
    CFArrayRef list = MMN_CopyWindowArray();
    if (!list) return -1;
    int32_t n = (int32_t)CFArrayGetCount(list);
    CFRelease(list);
    return n;
}

int32_t MMN_GetWindows(MMNWindow* outWindows, int32_t maxCount)
{
    if (!outWindows || maxCount <= 0) return -1;

    CFArrayRef list = MMN_CopyWindowArray();
    if (!list) return -1;

    CFIndex total = CFArrayGetCount(list);
    int32_t written = 0;
    for (CFIndex i = 0; i < total && written < maxCount; i++) {
        NSDictionary* info = (__bridge NSDictionary*)CFArrayGetValueAtIndex(list, i);
        if (![info isKindOfClass:[NSDictionary class]]) continue;

        /* Keep only real application windows. Layer 0 excludes the menu bar,
         * Dock, notification banners and screen-saver shields; a positive
         * alpha excludes fully-faded helper windows. The Windows code applied
         * an equivalent filter via IsWindowVisible + GetLayeredWindowAttributes. */
        NSNumber* layer = info[(id)kCGWindowLayer];
        if (!layer || [layer intValue] != 0) continue;

        NSNumber* alpha = info[(id)kCGWindowAlpha];
        if (alpha && [alpha doubleValue] <= 0.01) continue;

        NSDictionary* bounds = info[(id)kCGWindowBounds];
        if (![bounds isKindOfClass:[NSDictionary class]]) continue;

        MMN_FillWindow(&outWindows[written], info);
        if (outWindows[written].bounds.width < 2.0 ||
            outWindows[written].bounds.height < 2.0) continue;
        if (outWindows[written].ownerPid <= 0) continue;

        written++;
    }

    CFRelease(list);
    return written;
}

int32_t MMN_GetWindow(uint32_t windowId, MMNWindow* outWindow)
{
    if (!outWindow) return -1;

    CFArrayRef list = CGWindowListCopyWindowInfo(kCGWindowListOptionIncludingWindow,
                                                 (CGWindowID)windowId);
    if (!list) return -1;

    int32_t result = -1;
    if (CFArrayGetCount(list) > 0) {
        NSDictionary* info = (__bridge NSDictionary*)CFArrayGetValueAtIndex(list, 0);
        if ([info isKindOfClass:[NSDictionary class]]) {
            MMN_FillWindow(outWindow, info);
            result = 0;
        }
    }
    CFRelease(list);
    return result;
}

int32_t MMN_GetCursorPosition(double* outX, double* outY)
{
    /* NSEvent.mouseLocation is bottom-left origin, flip like everything else.
     * The top-left convention makes this a drop-in for WinApi.GetCursorPos. */
    NSPoint p = [NSEvent mouseLocation];
    if (outX) *outX = p.x;
    if (outY) *outY = MMN_MainDisplayHeight() - p.y;
    return 0;
}

/* ======================================================================== */
/* Capture                                                                   */
/* ======================================================================== */

int32_t MMN_HasScreenCapturePermission(void)
{
    if (@available(macOS 10.15, *)) {
        return CGPreflightScreenCaptureAccess() ? 1 : 0;
    }
    return 1;
}

int32_t MMN_RequestScreenCapturePermission(void)
{
    if (@available(macOS 10.15, *)) {
        if (CGPreflightScreenCaptureAccess()) return 1;
        CGRequestScreenCaptureAccess();
        /* The user has to toggle it in System Settings; the grant only becomes
         * effective for a freshly launched process. */
        return CGPreflightScreenCaptureAccess() ? 1 : 0;
    }
    return 1;
}

/* Converts a CGImage into a freshly allocated RGBA8 buffer.
 * flipVertical != 0 gives bottom-up rows (Unity's Texture2D layout). */
static int32_t MMN_ImageFromCGImage(CGImageRef image, int32_t maxWidth, int32_t maxHeight,
                                    int32_t flipVertical, MMNImage* out)
{
    if (!image || !out) return -1;

    size_t w = CGImageGetWidth(image);
    size_t h = CGImageGetHeight(image);
    if (w == 0 || h == 0) return -2;

    if (maxWidth > 0 && maxHeight > 0 &&
        (w > (size_t)maxWidth || h > (size_t)maxHeight)) {
        double s = fmin((double)maxWidth / (double)w, (double)maxHeight / (double)h);
        w = (size_t)fmax(1.0, floor((double)w * s));
        h = (size_t)fmax(1.0, floor((double)h * s));
    }

    size_t stride = w * 4;
    void* buffer = calloc(1, stride * h);
    if (!buffer) return -3;

    CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
    CGContextRef ctx = CGBitmapContextCreate(buffer, w, h, 8, stride, cs,
                                             kCGBitmapByteOrder32Big |
                                             kCGImageAlphaPremultipliedLast);
    CGColorSpaceRelease(cs);
    if (!ctx) { free(buffer); return -4; }

    /* Without this the bitmap comes out top-down, which is the opposite of
     * what LoadRawTextureData wants. */
    if (flipVertical) {
        CGContextTranslateCTM(ctx, 0, (CGFloat)h);
        CGContextScaleCTM(ctx, 1.0, -1.0);
    }
    CGContextSetInterpolationQuality(ctx, kCGInterpolationMedium);
    CGContextDrawImage(ctx, CGRectMake(0, 0, (CGFloat)w, (CGFloat)h), image);
    CGContextRelease(ctx);

    out->width = (int32_t)w;
    out->height = (int32_t)h;
    out->stride = (int32_t)stride;
    out->pixels = buffer;
    return 0;
}

int32_t MMN_CaptureWindow(uint32_t windowId, int32_t maxWidth, int32_t maxHeight,
                          int32_t flipVertical, MMNImage* outImage)
{
    if (!outImage) return -1;
    memset(outImage, 0, sizeof(*outImage));

    /* CGWindowListCreateImage still works on current macOS but is deprecated in
     * favour of ScreenCaptureKit. It is kept here because it is synchronous and
     * trivially callable from the render thread, which matters for a component
     * refreshed every frame. See macos-native/README.md for the SCK migration. */
    CGImageRef image = CGWindowListCreateImage(CGRectNull,
                                               kCGWindowListOptionIncludingWindow,
                                               (CGWindowID)windowId,
                                               kCGWindowImageBoundsIgnoreFraming |
                                               kCGWindowImageNominalResolution);
    if (!image) return -2;

    int32_t rc = MMN_ImageFromCGImage(image, maxWidth, maxHeight, flipVertical, outImage);
    CGImageRelease(image);
    return rc;
}

int32_t MMN_CaptureScreenRegion(MMNRect region, int32_t maxWidth, int32_t maxHeight,
                                int32_t flipVertical, MMNImage* outImage)
{
    if (!outImage) return -1;
    memset(outImage, 0, sizeof(*outImage));
    if (region.width < 1.0 || region.height < 1.0) return -3;

    CGRect r = CGRectMake(region.x, region.y, region.width, region.height);
    CGImageRef image = CGWindowListCreateImage(r,
                                               kCGWindowListOptionOnScreenOnly,
                                               kCGNullWindowID,
                                               kCGWindowImageDefault);
    if (!image) return -2;

    int32_t rc = MMN_ImageFromCGImage(image, maxWidth, maxHeight, flipVertical, outImage);
    CGImageRelease(image);
    return rc;
}

int32_t MMN_CaptureDisplay(int32_t displayIndex, int32_t flipVertical, MMNImage* outImage)
{
    if (!outImage) return -1;
    memset(outImage, 0, sizeof(*outImage));

    uint32_t count = 0;
    CGDirectDisplayID ids[32];
    if (CGGetActiveDisplayList(32, ids, &count) != kCGErrorSuccess) return -1;
    if (displayIndex < 0 || (uint32_t)displayIndex >= count) return -2;

    CGImageRef image = CGDisplayCreateImage(ids[displayIndex]);
    if (!image) return -3;

    int32_t rc = MMN_ImageFromCGImage(image, 0, 0, flipVertical, outImage);
    CGImageRelease(image);
    return rc;
}

void MMN_FreeImage(MMNImage* image)
{
    if (!image) return;
    if (image->pixels) free(image->pixels);
    image->pixels = NULL;
    image->width = image->height = image->stride = 0;
}

/* ======================================================================== */
/* Own app / window                                                          */
/* ======================================================================== */

int32_t MMN_SetAccessoryActivationPolicy(int32_t enabled)
{
    if (![NSThread isMainThread]) return -2;
    NSApplicationActivationPolicy policy = enabled
        ? NSApplicationActivationPolicyAccessory
        : NSApplicationActivationPolicyRegular;
    return [NSApp setActivationPolicy:policy] ? 0 : -1;
}

int32_t MMN_SetAppNapEnabled(int32_t enabled)
{
    if (enabled) {
        if (gActivityToken) return 0;
        gActivityToken = [[NSProcessInfo processInfo]
            beginActivityWithOptions:(NSActivityUserInitiated |
                                     NSActivityLatencyCritical |
                                     NSActivityIdleSystemSleepDisabled)
                              reason:@"MateEngine desktop avatar animation"];
        return gActivityToken ? 0 : -1;
    }
    if (gActivityToken) {
        [[NSProcessInfo processInfo] endActivity:gActivityToken];
        gActivityToken = nil;
    }
    return 0;
}

int32_t MMN_ActivateApp(void)
{
    if (![NSThread isMainThread]) return -2;
    [NSApp activateIgnoringOtherApps:YES];
    return 0;
}

/* ======================================================================== */
/* Menu bar item                                                             */
/* ======================================================================== */

#define MMN_MAX_STATUS_ITEMS 4
#define MMN_MAX_MENU_ENTRIES 32

@interface MMNStatusItem : NSObject
@property (nonatomic, strong) NSStatusItem* item;
@property (nonatomic, strong) NSImage* image;
@end

@implementation MMNStatusItem
@end

static MMNStatusItem* gStatusItems[MMN_MAX_STATUS_ITEMS];
static MMNStatusItemCallback gStatusCallback = NULL;

static void MMN_StatusMenuTarget(int32_t handle, int32_t index)
{
    if (gStatusCallback) gStatusCallback(handle, index);
}

/* Flat menus are enough for MateEngine's tray (a handful of toggles) and keep
 * the C ABI trivial. */
@interface MMNMenuTarget : NSObject
@property (nonatomic, assign) int32_t handle;
- (void)menuClicked:(NSMenuItem*)sender;
@end

@implementation MMNMenuTarget
- (void)menuClicked:(NSMenuItem*)sender
{
    MMN_StatusMenuTarget(self.handle, (int32_t)sender.tag);
}
@end

static MMNMenuTarget* gMenuTargets[MMN_MAX_STATUS_ITEMS];

int32_t MMN_StatusItemCreate(const char* titleUtf8, const char* iconPathUtf8)
{
    if (![NSThread isMainThread]) return -2;

    int32_t handle = -1;
    for (int i = 0; i < MMN_MAX_STATUS_ITEMS; i++) {
        if (!gStatusItems[i]) { handle = i; break; }
    }
    if (handle < 0) return -3;

    @autoreleasepool {
        MMNStatusItem* holder = [MMNStatusItem new];
        holder.item = [[NSStatusBar systemStatusBar]
            statusItemWithLength:NSSquareStatusItemLength];

        if (iconPathUtf8 && iconPathUtf8[0] != '\0') {
            NSString* path = [NSString stringWithUTF8String:iconPathUtf8];
            NSImage* img = [[NSImage alloc] initWithContentsOfFile:path];
            if (img) {
                /* Template images adapt to light/dark menu bars automatically. */
                [img setTemplate:YES];
                holder.image = img;
                holder.item.button.image = img;
            }
        }
        if (!holder.item.button.image && titleUtf8) {
            holder.item.button.title = [NSString stringWithUTF8String:titleUtf8];
        }

        gStatusItems[handle] = holder;
    }
    return handle;
}

int32_t MMN_StatusItemSetMenu(int32_t handle, const char* const* titlesUtf8,
                              const int32_t* enabled, int32_t count)
{
    if (handle < 0 || handle >= MMN_MAX_STATUS_ITEMS) return -1;
    MMNStatusItem* holder = gStatusItems[handle];
    if (!holder) return -2;
    if (count < 0) count = 0;
    if (count > MMN_MAX_MENU_ENTRIES) count = MMN_MAX_MENU_ENTRIES;

    @autoreleasepool {
        MMNMenuTarget* target = [MMNMenuTarget new];
        target.handle = handle;
        gMenuTargets[handle] = target;

        NSMenu* menu = [[NSMenu alloc] initWithTitle:@""];
        [menu setAutoenablesItems:NO];

        for (int32_t i = 0; i < count; i++) {
            const char* raw = titlesUtf8 ? titlesUtf8[i] : NULL;
            if (!raw) continue;

            if (raw[0] == '-') {
                [menu addItem:[NSMenuItem separatorItem]];
                continue;
            }

            NSMenuItem* entry = [[NSMenuItem alloc]
                initWithTitle:[NSString stringWithUTF8String:raw]
                       action:@selector(menuClicked:)
                keyEquivalent:@""];
            entry.target = target;
            entry.tag = i;
            entry.enabled = enabled ? (enabled[i] != 0) : YES;
            [menu addItem:entry];
        }

        holder.item.menu = menu;
        holder.item.button.needsDisplay = YES;
    }
    return 0;
}

void MMN_StatusItemSetCallback(MMNStatusItemCallback callback)
{
    gStatusCallback = callback;
}

void MMN_StatusItemDestroy(int32_t handle)
{
    if (handle < 0 || handle >= MMN_MAX_STATUS_ITEMS) return;
    if (![NSThread isMainThread]) return;

    if (gStatusItems[handle]) {
        [[NSStatusBar systemStatusBar] removeStatusItem:gStatusItems[handle].item];
        gStatusItems[handle] = nil;
    }
    gMenuTargets[handle] = nil;
}

/* ======================================================================== */
/* Launch at login                                                           */
/* ======================================================================== */

static NSString* MMN_LaunchAgentPath(void)
{
    NSString* bundleId = [[NSBundle mainBundle] bundleIdentifier];
    if (bundleId.length == 0) bundleId = @"com.Shinymoon.MateEngineX";
    NSString* agents = [@"~/Library/LaunchAgents" stringByExpandingTildeInPath];
    return [agents stringByAppendingPathComponent:
            [NSString stringWithFormat:@"%@.launch-at-login.plist", bundleId]];
}

int32_t MMN_SetLaunchAtLogin(int32_t enabled)
{
    @autoreleasepool {
#if defined(__MAC_OS_X_VERSION_MAX_ALLOWED) && __MAC_OS_X_VERSION_MAX_ALLOWED >= 130000
        if (@available(macOS 13.0, *)) {
            /* The modern path: no leftovers if the app is moved or deleted. */
            SMAppService* service = [SMAppService mainAppService];
            NSError* error = nil;
            BOOL ok = enabled ? [service registerAndReturnError:&error]
                              : [service unregisterAndReturnError:&error];
            if (!ok) {
                NSLog(@"[MateMacNative] SMAppService failed: %@", error);
            } else {
                return 0;
            }
        }
#endif
        /* Fallback for macOS 11 and 12, plus anything SMAppService rejects
         * (for example when running from a build folder rather than
         * /Applications). Mirrors what the registry Run key used to do. */
        NSString* path = MMN_LaunchAgentPath();
        NSFileManager* fm = [NSFileManager defaultManager];

        if (!enabled) {
            if ([fm fileExistsAtPath:path]) [fm removeItemAtPath:path error:nil];
            return 0;
        }

        NSString* agents = [path stringByDeletingLastPathComponent];
        [fm createDirectoryAtPath:agents withIntermediateDirectories:YES
                       attributes:nil error:nil];

        NSString* executable = [[NSBundle mainBundle] executablePath];
        if (executable.length == 0) return -2;

        NSDictionary* plist = @{
            @"Label" : [[path lastPathComponent] stringByDeletingPathExtension],
            @"ProgramArguments" : @[ executable ],
            @"RunAtLoad" : @YES,
            @"KeepAlive" : @NO,
        };
        return [plist writeToFile:path atomically:YES] ? 0 : -3;
    }
}

int32_t MMN_GetLaunchAtLogin(void)
{
    @autoreleasepool {
#if defined(__MAC_OS_X_VERSION_MAX_ALLOWED) && __MAC_OS_X_VERSION_MAX_ALLOWED >= 130000
        if (@available(macOS 13.0, *)) {
            if ([SMAppService mainAppService].status == SMAppServiceStatusEnabled) return 1;
        }
#endif
        return [[NSFileManager defaultManager] fileExistsAtPath:MMN_LaunchAgentPath()] ? 1 : 0;
    }
}
