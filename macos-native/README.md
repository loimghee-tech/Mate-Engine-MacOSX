# MateMacNative — macOS backend plugin

Source for the native plugin that gives the macOS build the desktop integration
Windows gets for free. The audit and the overall plan live in
[`../PORTING-macOS.md`](../PORTING-macOS.md).

## Build

On a Mac with the Xcode command line tools:

```bash
xcode-select --install     # once
./macos-native/build.sh
```

The script compiles a **universal** (`x86_64` + `arm64`) `.bundle` and installs
it to `Assets/Plugins/MacOS/` together with a `.meta` that enables it for the
macOS player only. Commit both files so contributors without a Mac can build.

Universal matters in both directions:

- an `arm64`-only plugin will not `dlopen` in an Intel player;
- an `x86_64`-only plugin will not `dlopen` in an Apple Silicon player, and
  Rosetta is being retired (heavily reduced from macOS 28), so relying on
  translation is not a plan.

## What it provides

| Area | C API | Replaces |
|---|---|---|
| Displays, work area, menu bar, Dock | `MMN_GetDisplays`, `MMN_GetMenuBarHeight`, `MMN_GetDockThickness` | `MonitorHelper`, `EnumDisplayMonitors`, `Shell_TrayWnd` |
| Window list, geometry, z-order | `MMN_GetWindows`, `MMN_GetWindow` | `EnumWindows`, `GetWindowRect`, `DwmGetWindowAttribute` |
| Cursor | `MMN_GetCursorPosition` | `GetCursorPos` |
| Capture | `MMN_CaptureWindow`, `MMN_CaptureScreenRegion`, `MMN_CaptureDisplay` | `uWindowCapture`, GDI `StretchBlt` |
| Dock tile / App Nap | `MMN_SetAccessoryActivationPolicy`, `MMN_SetAppNapEnabled` | `WS_EX_TOOLWINDOW` |
| Menu bar icon | `MMN_StatusItem*` | `Shell_NotifyIcon` |
| Start at login | `MMN_SetLaunchAtLogin`, `MMN_GetLaunchAtLogin` | `HKCU\...\Run` |
| System audio level | `MMN_Audio*` | WASAPI session meters — **stub, see below** |

## Design notes

**Coordinate system.** Points, top-left origin on the primary display, `+y`
down — deliberately identical to Win32 `RECT`s. This is why the port doesn't
have to rewrite the geometry in `AvatarWindowHandler`; it swaps the provider.
AppKit's bottom-left rects are flipped in the plugin.

**Threading.** Every entry point assumes the main thread. Unity marshals its
script execution there, and Unity's main thread is AppKit's main thread, so this
holds for `Start`/`Update` call sites. Do not call these from a `Task` or a
background thread — `NSStatusItem` and `setActivationPolicy:` will assert.

**Not loaded into the Editor.** The `.meta` disables the Editor platform.
`MMN_Initialize` sets the app's activation policy to `Accessory`, which would
hide the Unity Editor from the Dock. Menu-based editor testing would need a
separate guard; building the player is the intended loop.

**Permissions.** Window bounds, owner names, layer and alpha need no TCC
permission — so window-sitting works on a fresh install. Window *titles* and all
pixel capture need **Screen Recording**, and macOS 15+ also gates system audio
behind **System Audio Recording Only**. Every call degrades instead of throwing;
`MMN_HasScreenCapturePermission` / `MMN_RequestScreenCapturePermission` let the
UI ask politely. The grant only takes effect after a relaunch — design for that.

**Capture is deprecated but synchronous.** `CGWindowListCreateImage` has been
deprecated since macOS 14.4 in favour of ScreenCaptureKit. It still works and it
is synchronous, which is what a per-frame component wants. SCK is asynchronous
and stream-based; migrating is a performance/roadmap item, not a porting
prerequisite.

## The audio stub

`MateMacAudio.m` does not implement the tap. `MMN_AudioTapSupported()` reports
whether the OS could support one, `MMN_AudioStart()` returns `-1`, and
`MMN_AudioGetOutputPeak()` returns `-1` — meaning *unknown*, which the C# side
interprets as "do not auto-dance". The file contains the full implementation
recipe: `CATapDescription` (stereo global tap excluding self), `AudioHardwareCreateProcessTap`,
a private aggregate device with `kAudioAggregateDeviceTapListKey`, an IOProc
reducing float32 frames to a peak, and the per-process variant for the
`allowedApps` filter.

It is left unimplemented on purpose: realtime audio code that has never been
compiled or run is worse than a documented hole, and this is the only feature
that is genuinely hard to replace on macOS.

## Adding a new function

1. Declare it in `MateMacNative.h` and bump `MMN_API_VERSION` if you change an
   existing signature.
2. Implement it in `MateMacNative.m`.
3. Add the matching `[DllImport]` in
   `Assets/MATE ENGINE - MacOS Port/Runtime/MateMacNative.cs`, and a
   platform-safe wrapper in `MacDesktop.cs` (the wrapper must return a sensible
   default on Windows and in the Editor).
4. Re-run `build.sh` and `MateEngine → Build → Validate macOS setup`.
