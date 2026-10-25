/*
 * MateMacNative.h
 *
 * Native macOS backend for the MateEngine macOS port.
 *
 * This plugin replaces the three Windows-only subsystems that the game leans on:
 *
 *   WinApi.cs / DwmApi.cs        -> displays, window enumeration, geometry, z-order
 *   System Tray / SystemTray     -> NSStatusItem (menu bar icon)
 *   Registry "start with Windows"-> SMAppService / LaunchAgent
 *   uWindowCapture (unused)      -> CGWindowList image capture
 *   NAudio WASAPI session meters -> CoreAudio process tap (see MMN_AudioTapSupported)
 *
 * ---------------------------------------------------------------------------
 * COORDINATE SYSTEM (important)
 * ---------------------------------------------------------------------------
 * Every rect returned by this API uses CoreGraphics *global display* coordinates:
 *
 *   - origin (0,0) is the TOP-LEFT corner of the primary display
 *   - +x to the right, +y DOWN
 *   - units are points (not backing pixels); multiply by MMNDisplay.scale for pixels
 *
 * This is deliberately the same convention as the Windows virtual-screen
 * coordinates the existing game code already uses (`RECT` from GetWindowRect).
 * That means the porting work on the C# side is mostly "swap the provider",
 * not "rewrite the math", and displays to the left/above the primary one keep
 * the negative offsets the game already expects.
 *
 * ---------------------------------------------------------------------------
 * PERMISSIONS
 * ---------------------------------------------------------------------------
 * Window *bounds*, owner process, layer and alpha need NO permission.
 * Window *titles* and all pixel capture need Screen Recording permission
 * (TCC). Everything degrades gracefully instead of throwing.
 *
 * License: CC0 (matches WinApi.cs provenance)
 */

#ifndef MATE_MAC_NATIVE_H
#define MATE_MAC_NATIVE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MMN_API_VERSION 1

#define MMN_MAX_OWNER_NAME 256
#define MMN_MAX_WINDOW_TITLE 512

/* ------------------------------------------------------------------------ */
/* Types                                                                     */
/* ------------------------------------------------------------------------ */

typedef struct MMNRect {
    double x;
    double y;
    double width;
    double height;
} MMNRect;

typedef struct MMNDisplay {
    uint32_t displayId;      /* CGDirectDisplayID                                  */
    MMNRect  frame;          /* full display frame, global coords                  */
    MMNRect  visibleFrame;   /* frame minus menu bar, Dock and notch               */
    double   scale;          /* backing scale factor; 2.0 on Retina, 1.0 otherwise */
    int32_t  isPrimary;
    int32_t  isBuiltin;
} MMNDisplay;

typedef struct MMNWindow {
    uint32_t windowId;       /* CGWindowID                                         */
    int32_t  ownerPid;
    int32_t  layer;          /* 0 = normal application window                      */
    double   alpha;          /* 0..1                                               */
    int32_t  isOnScreen;
    MMNRect  bounds;
    char     ownerName[MMN_MAX_OWNER_NAME];   /* always available                  */
    char     title[MMN_MAX_WINDOW_TITLE];     /* "" unless Screen Recording granted */
} MMNWindow;

typedef struct MMNImage {
    int32_t width;
    int32_t height;
    int32_t stride;          /* bytes per row; always width * 4                    */
    void*   pixels;          /* RGBA8, allocated by native, free with MMN_FreeImage */
} MMNImage;

/* Signature for the menu-bar item callback. itemIndex is the index of the
 * clicked entry passed to MMN_StatusItemSetMenu, or -1 if the icon itself
 * was clicked and no menu was configured. Called on the main thread. */
typedef void (*MMNStatusItemCallback)(int32_t handle, int32_t itemIndex);

/* ------------------------------------------------------------------------ */
/* Lifecycle                                                                 */
/* ------------------------------------------------------------------------ */

/* Must be called from the Unity main thread before anything else.
 * Returns 0 on success, negative on failure. Idempotent. */
int32_t MMN_Initialize(void);

/* Returns MMN_API_VERSION the plugin was built against, or -1 if the plugin
 * could not be loaded into the process. Use it for a cheap ABI sanity check. */
int32_t MMN_GetApiVersion(void);

/* ------------------------------------------------------------------------ */
/* Displays                                                                  */
/* ------------------------------------------------------------------------ */

int32_t MMN_GetDisplayCount(void);

/* Fills up to maxCount entries, ordered primary-first then left-to-right.
 * Returns the number written, or negative on error. */
int32_t MMN_GetDisplays(MMNDisplay* outDisplays, int32_t maxCount);

/* Menu bar height in points on the display that currently owns the menu bar. */
double MMN_GetMenuBarHeight(void);

/* Dock footprint on a given display, expressed as a thickness along the docked
 * edge. Lets "sit on the taskbar" map to "sit on the Dock".
 * edge: 0 = bottom, 1 = left, 2 = right, 3 = hidden/unknown
 * Returns the thickness in points, or 0 if the Dock is auto-hidden. */
double MMN_GetDockThickness(int32_t displayIndex, int32_t* outEdge);

/* ------------------------------------------------------------------------ */
/* Windows                                                                   */
/* ------------------------------------------------------------------------ */

int32_t MMN_GetWindowCount(void);

/* Fills up to maxCount entries in front-to-back z-order (index 0 is frontmost),
 * mirroring the order EnumWindows + GetWindow(GW_HWNDNEXT) produced on Windows.
 * On-screen normal windows only; desktop and menu-bar helper windows excluded.
 * Returns the number written, or negative on error. */
int32_t MMN_GetWindows(MMNWindow* outWindows, int32_t maxCount);

/* Looks up a single window by id. Returns 0 on success, -1 if it vanished. */
int32_t MMN_GetWindow(uint32_t windowId, MMNWindow* outWindow);

/* Global cursor position, same convention as WinApi.GetCursorPos(). */
int32_t MMN_GetCursorPosition(double* outX, double* outY);

/* ------------------------------------------------------------------------ */
/* Capture                                                                   */
/* ------------------------------------------------------------------------ */

/* 1 if Screen Recording permission is already granted, 0 if not. */
int32_t MMN_HasScreenCapturePermission(void);

/* Triggers the TCC prompt. Returns 1 if already granted, 0 if the user must
 * grant it in System Settings and relaunch. */
int32_t MMN_RequestScreenCapturePermission(void);

/* Captures one window's contents. Pass maxWidth/maxHeight <= 0 for native
 * resolution; the result is downscaled to fit while keeping aspect ratio.
 * flipVertical != 0 emits bottom-up rows, which is what Unity's
 * Texture2D.LoadRawTextureData expects.
 * Returns 0 on success. The caller must call MMN_FreeImage afterwards. */
int32_t MMN_CaptureWindow(uint32_t windowId, int32_t maxWidth, int32_t maxHeight,
                          int32_t flipVertical, MMNImage* outImage);

/* Captures a rectangle of the desktop in global coords (for the screensaver /
 * desktop ambient probe, and for screenshots). Same buffer rules as above. */
int32_t MMN_CaptureScreenRegion(MMNRect region, int32_t maxWidth, int32_t maxHeight,
                                int32_t flipVertical, MMNImage* outImage);

/* Captures an entire display. */
int32_t MMN_CaptureDisplay(int32_t displayIndex, int32_t flipVertical, MMNImage* outImage);

void MMN_FreeImage(MMNImage* image);

/* ------------------------------------------------------------------------ */
/* Own app / window                                                          */
/* ------------------------------------------------------------------------ */

/* Hides the app from the Dock and Cmd-Tab, the macOS equivalent of
 * WS_EX_TOOLWINDOW (see RemoveTaskbarApp.cs). Pass 0 to restore. */
int32_t MMN_SetAccessoryActivationPolicy(int32_t enabled);

/* Keeps the app responsive while it is in the background (macOS App Nap would
 * otherwise throttle it, which is fatal for a desktop pet). */
int32_t MMN_SetAppNapEnabled(int32_t enabled);

/* Brings our own window to the front, like SetForegroundWindow. */
int32_t MMN_ActivateApp(void);

/* ------------------------------------------------------------------------ */
/* Menu bar item                                                             */
/* ------------------------------------------------------------------------ */

/* Creates a menu bar (status) item. iconPathUtf8 may be NULL for a text item;
 * when provided it should point at a template PNG shipped in StreamingAssets.
 * Returns a handle >= 0, or negative on failure. */
int32_t MMN_StatusItemCreate(const char* titleUtf8, const char* iconPathUtf8);

/* Replaces the item's menu with a flat list. titlesUtf8 entries beginning with
 * "-" are rendered as separators. enabled may be NULL (all enabled). */
int32_t MMN_StatusItemSetMenu(int32_t handle, const char* const* titlesUtf8,
                              const int32_t* enabled, int32_t count);

/* Single global callback for all status items; pass NULL to clear. */
void MMN_StatusItemSetCallback(MMNStatusItemCallback callback);

void MMN_StatusItemDestroy(int32_t handle);

/* ------------------------------------------------------------------------ */
/* Launch at login (replaces the HKCU\...\Run registry key)                   */
/* ------------------------------------------------------------------------ */

int32_t MMN_SetLaunchAtLogin(int32_t enabled);
int32_t MMN_GetLaunchAtLogin(void);

/* ------------------------------------------------------------------------ */
/* System audio level  (replaces NAudio WASAPI session meters)               */
/* ------------------------------------------------------------------------ */

/* 1 when the running macOS exposes CATapDescription (macOS 14.2+), 0 otherwise. */
int32_t MMN_AudioTapSupported(void);

/* Starts/stops a global output tap that EXCLUDES this process' own audio.
 * Returns 0 on success, negative if unsupported or permission was denied
 * (System Audio Recording / Screen & System Audio Recording, macOS 15+). */
int32_t MMN_AudioStart(void);
void    MMN_AudioStop(void);

/* Most recent master peak in the range 0..1, or -1 when the tap is not running.
 * Games poll this at ~2 Hz, matching AvatarAnimatorController.CheckSoundContinuously(). */
double MMN_AudioGetOutputPeak(void);

#ifdef __cplusplus
}
#endif

#endif /* MATE_MAC_NATIVE_H */
