# Porting MateEngine to macOS (Intel + Apple Silicon)

**Short answer: yes, it's feasible.** Nothing in MateEngine is architecturally
tied to Windows. Roughly two thirds of the game — VRM loading, the avatar rig,
animations, shaders, menus, mods, the LLM, the dance player — is plain Unity and
already runs on macOS unmodified. The Windows-specific part is a *shim* of
Win32 calls bolted on around it: window enumeration, system tray, desktop
capture, audio meters. Every one of those has a macOS equivalent.

This document is the audit and the plan. Alongside it, this branch adds the
first slice of the actual port: a native macOS plugin, the C# bindings for it,
and a build script. See [What's already here](#whats-already-here).

> **Basis of these claims.** Everything below comes from reading this repository
> at commit `2c5ea6b` ("Prepare 3.4 Features"). No Unity editor and no Mac were
> available, so nothing has been compiled or run. Treat the line numbers and
> numbers as accurate and the runtime behaviour as *to be verified*.

---

## Contents

- [Why it doesn't run on macOS today](#why-it-doesnt-run-on-macos-today)
- [What already works](#what-already-works)
- [The four real blockers](#the-four-real-blockers)
- [Feature-by-feature verdict](#feature-by-feature-verdict)
- [Intel Macs specifically](#intel-macs-specifically)
- [Build, sign, ship](#build-sign-ship)
- [Plan and effort](#plan-and-effort)
- [What's already here](#whats-already-here)
- [Next steps, in order](#next-steps-in-order)
- [Risk register](#risk-register)

---

## Why it doesn't run on macOS today

Two independent reasons.

**1. There is no macOS build target configured, and the code would crash
immediately if there were.** Nineteen first-party scripts declare **157 raw
`DllImport`s** into `user32.dll`, `dwmapi.dll`, `gdi32.dll`, `shell32.dll`,
`psapi.dll`, `kernel32.dll` and `shcore.dll`. None of those libraries exist on
macOS. The DllImports compile fine (they're strings), so the app builds and then
dies on the first call with `DllNotFoundException`.

| File | Win32 imports |
|---|---|
| `MATE ENGINE - Scripts/APIs/WinApi.cs` | 47 |
| `MATE ENGINE - Scripts/AvatarHandlers/AvatarWindowHandler.cs` | 20 |
| `MATE ENGINE - System Tray/SystemTray/WinAPI.cs` | 18 |
| `MATE ENGINE - Scripts/AvatarHandlers/AvatarLocomotionController.cs` | 13 |
| `MATE ENGINE - Scripts/Tools/DesktopAmbientProbe.cs` | 13 |
| `MATE ENGINE - Scripts/AvatarHandlers/AvatarHideHandler.cs` | 9 |
| `MATE ENGINE - Scripts/APIs/DwmApi.cs` | 8 |
| `MATE ENGINE - Scripts/AvatarHandlers/PetVoiceReactionHandler.cs` | 5 |
| `MATE ENGINE - System Tray/RemoveTaskbarApp.cs` | 5 |
| `AvatarBigScreenHandler`, `MonitorHelper`, `AvatarBigScreenTimer`, `AvatarBigScreenScreenSaver`, `AvatarTaskbarController`, `SettingsMenuPosition`, `MoveToPrimaryScreen` | 2–3 each |
| `AvatarSwayController`, `AvatarGravityController`, `MemoryTrim` | 1 each |

Some of these are already guarded — `MoveToPrimaryScreen.cs` is wrapped in
`#if UNITY_STANDALONE_WIN`, `DesktopAmbientProbe.cs` uses stage-level `#if`s.
Most are not, and those are the ones that crash.

**2. The Windows-only native dependencies are not present for macOS.**

| Dependency | Ships for macOS? | Notes |
|---|---|---|
| `NAudio` (+ `NAudio.Wasapi`) | ✗ Windows-only by design | Used for audio session meters → dance-to-music |
| `uWindowCapture` (`UwcLib`) | ✗ only `x86`/`x86_64` DLLs | **Not referenced by any scene, prefab or script** — can be dropped from a Mac build entirely |
| `LibUniWinC` | ✓ **universal (x86_64 + arm64)** | Verified by reading the Mach-O fat header |
| `steam_api.bundle` | ✓ x86 + x86_64 + arm64 | Universal but includes a dead 32-bit i386 slice |
| `libusearch_c.dylib` (LLMUnity) | ✓ universal | RAG index backend |
| `DiscordRPC` native pipe | ✗ only `.dll` and `.so` | **Discord Rich Presence will fail on macOS** — needs a `NativeNamedPipe.dylib` |
| `StandaloneFileBrowser.bundle` | ⚠ **thin x86_64 only** | Works on Intel; must be rebuilt for arm64 |
| `UMotion` FBX editor bundle | ✓ (editor-only) | Doesn't affect players |

That is a short list. The most important entry is the third one: **the library
that gives you the transparent, click-through, always-on-top window already
works on macOS, on both architectures.** That's the single hardest thing to get
right for a desktop pet and MateEngine already has it.

---

## What already works

- **Transparent, borderless, click-through, always-on-top window.**
  `Kirurobo/UniWindowController` supports macOS natively via
  `LibUniWinC.bundle`, and that bundle is a universal binary (verified:
  `FAT/Universal: x86_64, arm64`). Transparency, click-through, top-most,
  positioning, cursor read/write, and file-drop are all available.
- **Everything Unity-native**: VRM/VRM10 loading (UniVRM), SpringBone/DynamicBone,
  lilToon and Poiyomi shaders, the animator graph, blend shapes, particle
  effects, post-processing, TextMeshPro, localization.
- **Mouse/head/eye/spine tracking.** `AvatarMouseTracking.cs` is cursor-driven,
  not camera-driven. It reads a position and points the rig at it — portable.
- **Mods.** Mods carry no C# assemblies at runtime — `MEModHandler`, `VRMLoader` and `AvatarDancePlayer` all go through `AssetBundle.LoadFromFile`, plus raw VRM/GLB, audio and JSON next to it. Nothing here needs recompiling for macOS. But see [AssetBundles are platform-locked](#assetbundles-are-platform-locked) — the *content* still has to be rebuilt.
- **The LLM.** `LLMUnity` already has `RuntimePlatform.OSXEditor/OSXPlayer`
  branches for `dlopen`/`dlsym`, and the `usearch` dylib is universal.
- **Steamworks.NET.** The managed layer is already guarded for
  `UNITY_STANDALONE_OSX`, and the native bundle is universal.
- **Screenshots, save data, settings, input.** `Application.persistentDataPath`,
  `ScreenCapture`, the old Input Manager (`activeInputHandler: 0`) — all portable.

---

## The four real blockers

### 1. Window enumeration and occlusion — `AvatarWindowHandler.cs` (994 lines)

This is the biggest single piece of porting. The avatar sits on other apps'
windows and is occluded by them, which on Windows means `EnumWindows` +
`GetWindowRect` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` +
`GetLayeredWindowAttributes` to filter out other transparent overlays, plus
`GetWindow(GW_HWNDNEXT)` for z-order.

**macOS equivalent: `CGWindowListCopyWindowInfo`.** `MMN_GetWindows()` in this
branch returns exactly the shape the handler wants: window id, owner pid, owner
name, bounds in top-left global points, layer, alpha, front-to-back order.

Three things to know:

- **No permission needed for geometry or owner names.** Only window *titles*
  and pixel capture require Screen Recording. So "sit on the window" works on a
  fresh install with zero prompts — better than you'd expect.
- **Z-order is a real difference.** macOS orders normal windows front-to-back
  within layer 0, which maps well onto the existing occluder-quad logic. But
  Spaces/fullscreen apps live on separate spaces, and `CGWindowListCopyWindowInfo`
  returns windows from the active space only. The handler's
  `windowEnumFPS`/`windowEnumIdleFPS` throttling and `snapGuardFrames` hysteresis
  will need retuning; expect a week of feel-tuning on the occlusion, not a
  weekend.
- `GetWindowRect` includes the shadow on macOS only if you ask for
  `kCGWindowImageBoundsIgnoreFraming` — for sitting, use the *bounds* value
  straight from the window list (already shadow-excluded), not a capture rect.

**Effort:** 1–2 weeks. Risk: medium-high, mostly in z-order edge cases.

### 2. System audio level — dance to music (`AvatarAnimatorController.cs`)

`IsValidAppPlaying()` walks WASAPI sessions and reads
`AudioMeterInformation.MasterPeakValue` **per process**, so the avatar only
dances to apps on the `allowedApps` list. `AllowedAppsManager` does the same to
populate its dropdown.

There is no public CoreAudio equivalent that reports per-process loudness.
`kAudioDevicePropertyDeviceIsRunningSomewhere` tells you only *that* something is
playing.

**macOS 14.2+ answer: a CoreAudio process tap** (`CATapDescription` +
`AudioHardwareCreateProcessTap`, wrapped in a private aggregate device, with an
IOProc reducing float32 frames to a peak). It supports per-process taps via
`initStereoMixdownOfProcesses:`, so the `allowedApps` semantics can be preserved.
macOS 15 additionally gates this behind the "System Audio Recording Only"
permission.

**This is the one item I deliberately did not implement.** `MateMacAudio.m`
carries the seam, the honest "returns -1 / unknown" behaviour, and a step-by-step
recipe with the exact API calls. A hundred lines of realtime-thread audio code
that has never been run is worse than a clearly-marked hole.

**Interim behaviour:** `-1` means "unknown", so the avatar simply never
auto-dances. Every other feature is unaffected, and the "dance" button still
works. Ship the port with the toggle hidden or disabled and fill it in after.

**Effort:** ~1 week plus testing. Risk: high — this is the most likely thing to
need a fallback (BlackHole virtual device, or ScreenCaptureKit audio, which is
easier but still needs Screen Recording).

### 3. Taskbar → Dock, and the system tray

`AvatarTaskbarController.cs` reads the Windows taskbar rect (`Shell_TrayWnd`)
and sits the avatar on it. The macOS analogue is the Dock, whose footprint is
`NSScreen.visibleFrame` minus `NSScreen.frame` — already exposed as
`MMN_GetDockThickness()` / `MacDesktop.TryGetDockRect()`. Autohide returns 0 and
the feature quietly no-ops, which is the right behaviour.

`System Tray/SystemTray/TrayIcon.cs` is a `Shell_NotifyIcon` wrapper. macOS uses
an `NSStatusItem`: `MMN_StatusItemCreate` / `MMN_StatusItemSetMenu` /
`MMN_StatusItemSetCallback` are implemented here, with template images so the
icon adapts to light/dark menu bars and to the Tahoe "Liquid Glass" menu bar.

`RemoveTaskbarApp.cs` hides the app from the taskbar with `WS_EX_TOOLWINDOW`.
The macOS equivalent is `NSApplicationActivationPolicyAccessory`, wired to
`MacDesktop.SetDockIconHidden()`. Do this at startup or the pet gets a Dock tile.

**Effort:** 3–5 days for both.

### 4. Screen capture — big screen mode, screensaver, ambient probe

`AvatarBigScreenHandler` / `AvatarBigScreenScreenSaver` / `DesktopAmbientProbe`
capture the desktop (the last one via GDI `StretchBlt` into a 1×1 DIB to get an
average colour for ambient lighting).

`MMN_CaptureScreenRegion` / `MMN_CaptureDisplay` / `MMN_CaptureWindow` cover
this, returning RGBA8 straight into a `Texture2D`. The ambient probe becomes a
tiny downscale instead of a `StretchBlt`.

**Caveat:** these need Screen Recording permission, which means a TCC prompt and
a relaunch before the grant applies. Design the UI so that declining degrades to
"no big screen mode" rather than a broken app. Note also that
`CGWindowListCreateImage` is deprecated since macOS 14.4 in favour of
ScreenCaptureKit; it still works, and it's synchronous, which matters here.
Migrating to SCK is a later optimisation, not a porting prerequisite.

**Effort:** 3–5 days.

---

## Feature-by-feature verdict

| Feature | Status | Work |
|---|---|---|
| Transparent / click-through / top-most window | ✅ Free | `LibUniWinC.bundle` is universal |
| VRM avatars, shaders, animations, dances | ✅ Free | Pure Unity |
| Mods (`.ME`), custom dances, accessories | ⚠ Content rebuild | Bundles are platform-locked; VRM/JSON-only mods are fine |
| Mouse → head / eye / spine tracking | ✅ Free | Cursor via `MacDesktop.GetCursorPosition()` |
| Idle / drag / sleep / food | ✅ Free | Pure Unity |
| Multi-avatar (up to 9) | ✅ Free | Pure Unity |
| Window sitting + occlusion | 🔧 1–2 weeks | `CGWindowList` + retune occlusion |
| Dock sitting | 🔧 3 days | `visibleFrame` |
| Menu bar icon + menus | 🔧 2 days | `NSStatusItem` (implemented) |
| Start at login | 🔧 Done | `SMAppService` / LaunchAgent (implemented) |
| Multi-monitor / DPI | 🔧 3 days | `NSScreen` + `backingScaleFactor` |
| Big screen / screensaver / ambient | 🔧 3–5 days | Capture + TCC prompt |
| Screenshots | ✅ Free | `ScreenCapture` |
| AI chat (local LLM) | ⚠ Verify | `LLMUnity` has macOS branches; confirm the `undreamai` server binary ships for macOS x86_64 |
| Dance to music | ❌ Blocked | Process tap, ~1 week |
| Discord Rich Presence | ❌ Broken | Needs a `NativeNamedPipe.dylib`; gate the feature off |
| Steam Workshop | ⚠ N/A | Needs Valve to create macOS depots; GitHub edition works without Steam |
| Global hotkeys | ⚠ Same as Windows | `Input.GetKeyDown` needs focus; unchanged behaviour |
| File dialogs (VRM import) | ⚠ Works on Intel | `StandaloneFileBrowser.bundle` is thin x86_64 — rebuild for arm64 |
| Auto-update | ❌ N/A | Steam handles updates; GitHub edition has none |

---

## Intel Macs specifically

This is the part of your question with the shortest shelf life, so be deliberate
about it.

### You can build for Intel today, but build **Universal**, not Intel-only

- This project is on **Unity 6000.2.6f2**, which fully supports macOS x86_64
  players. Intel targets get a deprecation warning starting in **Unity 6.6** and
  are **removed in Unity 6.8**; **6.7 LTS is the last release that includes
  them** and keeps supporting them for two years.
- So: pin the editor at 6000.2 (or at most 6.7 LTS), select
  **Intel 64-bit + Apple silicon**, and you ship one `.app` that runs natively on
  both.
- **Do not ship an x86_64-only build.** macOS 27 removed Intel support and
  Apple is retiring Rosetta 2 (heavily reduced from macOS 28, kept only for
  legacy games). An Intel-only `.app` will look increasingly broken on modern
  Apple Silicon Macs, while an Intel-only *slice* inside a universal `.app` costs
  you nothing.

### The Intel Mac you're targeting runs macOS 26 at most

- **macOS 26 Tahoe is the last release that boots on Intel Macs**, and only on
  four models: MacBook Pro 16" (2019), MacBook Pro 13" 4-port (2020), iMac 27"
  (2020), Mac Pro (2019). Those get security updates into roughly 2028.
- Every other Intel Mac stopped at macOS 15 Sequoia or earlier.
- Practically: **target macOS 11.0 minimum** (which `ProjectSettings.asset`
  already declares as `macOSTargetOSVersion: 11.0`), use the **Metal** graphics
  API, and don't rely on anything newer than macOS 13 without an
  `@available` guard. The plugin here does exactly that, with `SMAppService`
  (13.0+) falling back to a LaunchAgent plist on 11–12.
- Both Intel Macs and Apple Silicon run Metal, so `Graphics API = Metal` is safe
  and is the only sane choice.

### Performance on Intel

The GPU work is the same as Windows (Metal vs D3D11, roughly comparable). The
Intel Macs that can run Tahoe are all quad-core-and-up with discrete or Iris
graphics; an avatar with bloom + AO at 60 fps is fine. The LLM (qwen2.5 1.5B)
will be slower than on a discrete PC GPU but already runs on CPU.

---

## Build, sign, ship

1. **Editor**: Unity 6000.2.6f2 with the *Mac Build Support (IL2CPP)* and
   *Mac Build Support (Mono)* modules, plus Xcode 15+.
2. **Scripting backend: IL2CPP.** Not optional — Unity's Mono backend on macOS
   is x86_64-only, so an Apple Silicon or universal player requires IL2CPP. That
   in turn requires Xcode for the final link. Keep the Windows build on whatever
   backend it uses today; `MEBuildMacOS.cs` sets IL2CPP for the Standalone
   target only when you build from its menu.
3. **Player settings that matter for a desktop pet:**
   - Windowed, resizable, `runInBackground` and `visibleInBackground` on.
   - `LSUIElement` / accessory activation policy so there's no Dock tile.
   - Minimum macOS 11.0.
   - **No App Sandbox.** Sandboxed apps cannot enumerate or read other windows,
     which kills window-sitting. (It also means no Mac App Store distribution
     unless you drop that feature.)
4. **Signing.** Developer ID Application certificate, hardened runtime, and
   `--deep`-less nested signing: sign `MateMacNative.bundle` **before** the
   outer `.app`, with the same Team ID, or `dlopen` fails under the hardened
   runtime.
5. **Notarisation.** `notarytool submit --wait` then `stapler staple`. Without
   it, Gatekeeper shows "damaged and can't be opened" for anyone downloading a
   zip. This is the step people forget and then blame the port.
6. **Permissions to explain in your docs / onboarding:**
   - *Screen Recording* — only for big screen mode, screensaver, window capture.
   - *System Audio Recording Only* (macOS 15+) — only for dance-to-music.
   - Nothing else. Accessibility is **not** required unless you later want to move
     other apps' windows.
7. **Steam.** The macOS app works outside Steam as-is (the GitHub edition).
   A Steam macOS depot needs Valve-side app/depot config, plus the
   `steam_api.bundle` (already universal) and `steam_appid.txt` removed from the
   depot — same as Windows today.

---

## Plan and effort

Estimates for someone comfortable with Unity who has a Mac to test on.

| Milestone | Outcome | Estimate |
|---|---|---|
| **M0 — It launches** | macOS build target set, all Win32 call sites guarded so nothing throws, window appears, avatar loads and idles | 2–4 days |
| **M1 — It's a desktop pet** | Transparent, click-through, always-on-top, drag, cursor tracking, no Dock tile, App Nap disabled | 3–5 days |
| **M2 — It sits on things** | Window list via native plugin, occluders reworked, Dock sitting, multi-display | 1.5–2 weeks |
| **M3 — It behaves like an app** | Menu bar icon + menus, start at login, settings persistence, permission onboarding | 1 week |
| **M4 — The extras** | Big screen, screensaver, ambient probe, screenshots | 1 week |
| **M5 — Dance to music** | CoreAudio process tap, `allowedApps` preserved | 1 week + risk |
| **M6 — Shippable** | Signing, notarisation, universal build, CI, docs, Discord disabled or fixed | 1 week |

- **MVP a user could actually use (M0–M3): ~3–4 weeks.**
- **Full parity minus audio: ~6 weeks.**
- **With audio and packaging: ~7–8 weeks.**

The Linux port is the useful precedent: that was a real fork with a maintained
build, not a patch, and the shape of the work here is similar.

---

## What's already here

This branch adds the foundation. All of it is additive — nothing existing was
modified, so the Windows build is untouched.

```
macos-native/                       Native plugin source (NOT compiled by Unity)
  MateMacNative.h                   C ABI: displays, windows, capture, tray, login
  MateMacNative.m                   Objective-C / CoreGraphics / AppKit implementation
  MateMacAudio.m                    Audio seam, documented, intentionally a stub
  build.sh                          Universal (x86_64+arm64) .bundle + Unity .meta

Assets/MATE ENGINE - MacOS Port/Runtime/
  MateMacNative.cs                  P/Invoke layer (macOS standalone only)
  MacDesktop.cs                     Platform-safe services for game code to call

Assets/Editor/
  MEBuildMacOS.cs                   Build menu + CI entry point + setup validator
```

Highlights:

- **Coordinates match Windows.** Everything is in points, top-left origin, +y
  down — the same convention as `GetWindowRect`, so the existing `RECT` math in
  the Avatar handlers carries over instead of being rewritten.
- **No-plugin safety.** `MacDesktop` compiles and runs on Windows and in the
  Editor; every accessor returns empty/false and `IsAvailable` stays false. No
  preprocessor soup needed at call sites.
- **`MMN_GetWindows` is a drop-in for the `EnumWindows` loop** in
  `AvatarWindowHandler.cs`: same fields, same front-to-back ordering, layer-0
  filtering already applied.
- **`MateEngine/Build/Validate macOS setup`** checks the plugin is present and
  actually universal before you waste a build, and warns about the known gaps.
- **`MateEngine/Build/macOS Universal`** injects the scene list (this repo ships
  an empty `EditorBuildSettings.scenes`, so a stock build has no scenes at all),
  forces IL2CPP, and sets the pet-friendly player settings.

### To build the plugin

On a Mac, once:

```bash
xcode-select --install
./macos-native/build.sh          # installs into Assets/Plugins/MacOS/
```

Then in Unity: **MateEngine → Build → Validate macOS setup**, then
**MateEngine → Build → macOS Universal**.

Commit `Assets/Plugins/MacOS/MateMacNative.bundle` (and its `.meta` from
`build.sh`) so contributors without a Mac can still build.

---

## Next steps, in order

1. **Guard the crashes (M0).** Wrap the 12 unguarded Win32 call sites in
   `#if UNITY_STANDALONE_WIN`, and give `AvatarWindowHandler.Start()` a
   macOS path that calls `Process.GetCurrentProcess()` without
   `MainWindowHandle` (it's `IntPtr.Zero` on macOS). This alone gets a build to
   launch.
2. **Port `AvatarWindowHandler` to `MacDesktop.GetWindows()`** behind the same
   interface it already uses for `WindowEntry`. This is the single highest-value
   change in the whole port.
3. **Swap the three small ones**: `MonitorHelper` → `MacDesktop.GetDisplays()`,
   `AvatarTaskbarController` → `TryGetDockRect()`, `SystemStartHandler` →
   `MacDesktop.SetLaunchAtLogin()`.
4. **Port the tray** to `MMN_StatusItem*` and delete `RemoveTaskbarApp` in favour
   of `SetDockIconHidden(true)`.
5. **Get the capture paths working** and write the TCC onboarding screen.
6. **Only then** tackle the audio tap.
7. **Fix or hide Discord RPC** — build a `NativeNamedPipe.dylib`, or gate the
   feature off on macOS.
8. **Sign + notarise**, and add a GitHub Actions macOS job
   (`macos-15-intel` for Intel coverage, `macos-14` arm64) — note that free
   runners lose Intel images in 2027.

---

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| Occlusion/z-order feels wrong vs Windows | High | Budget the 1–2 weeks; add an in-game debug overlay showing the window list |
| Screen Recording prompt scares users away | High | Make every capture feature opt-in and explain it in the settings screen |
| Audio tap infeasible or capped by the system | Medium | Ship without auto-dance; offer a BlackHole preset |
| Existing mods/Workshop content won't load | **High** | See [AssetBundles are platform-locked](#assetbundles-are-platform-locked) — plan a content rebuild before launch |
| Unity drops Intel targets (6.8+) | Certain, eventually | Stay on 6.7 LTS; universal binaries keep working either way |
| Intel Macs go end-of-life (macOS 26 is the last) | Certain | Accept it: this port serves existing Intel hardware until ~2028 |
| Steam has no macOS depots | Certain | Ship the GitHub edition; Steam edition is Valve's call |

### AssetBundles are platform-locked

This is the sharpest surprise in the audit, and it isn't a code problem — it's a
content problem. MateEngine mods are **AssetBundles**
(`MEModHandler.cs:210`, `VRMLoader.cs:221`, `AvatarDancePlayer.cs:747`). Unity
builds bundles *per target platform*: a bundle built for Windows Standalone will
not load in a macOS player. The same applies to the bundles already committed
here (`AssetBundles/dragging_animation`, and everything under
`Assets/StreamingAssets/Mods`).

Consequences:

- Mods that are pure VRM/GLB + MP3/OGG + JSON keep working, because the loader
  reads those directly.
- Any mod or shipped bundle containing a `.bundle` needs a macOS rebuild and a
  way for the game to pick the right one. Suggested convention: `Mods/<name>/mac/`
  alongside the existing layout, selected at load time, or ship the Workshop
  item as two platform variants.
- The in-repo `AssetBundles/dragging_animation` must be rebuilt with macOS as the
  active target before a Mac build, or the drag animation silently disappears.

> ⚠️ **The one thing to decide first:** mod distribution. A port that launches
> but shows an empty Workshop feels broken even if it's technically correct.
> Either commit to rebuilding content per platform, or move mods to
> platform-neutral formats (VRM + JSON + audio) before announcing the port.
