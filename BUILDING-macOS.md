# Building MateEngine for macOS

Step-by-step from a clean checkout to a running `.app`. For the *why* — what
works, what doesn't, and what each component needs — read
[`PORTING-macOS.md`](PORTING-macOS.md).

> **Status.** The code in this repo has been read, patched and syntax-checked,
> but **never compiled**. No Unity install and no Mac were available while it was
> written. Expect a short debugging pass on your first build; the troubleshooting
> section below lists the failures most likely to actually happen.

---

## 1. What you need

| Requirement | Notes |
|---|---|
| **Unity 6000.2.6f2** | Exactly the version in `ProjectSettings/ProjectVersion.txt`. Newer is fine up to 6.7 LTS; **6.8+ removes Intel macOS support entirely**. |
| **macOS Build Support (IL2CPP)** module | Unity Hub → Installs → the gear icon → Add Modules. Without it there is no macOS target. |
| **macOS Build Support (Mono)** module | Optional, but keeps editor iteration fast. |
| **Xcode 15+** and the command line tools | Required by IL2CPP for the final link. `xcode-select --install` |
| **A Mac to build on** | Unity cannot cross-compile a macOS player from Windows or Linux. |

You do **not** need to build the native plugin to try the basics. Window
position, cursor position and the transparent window come from
UniWindowController, which ships a universal binary already.

---

## 2. Build it

1. **Open the project** in Unity 6000.2.6f2. Let it import (first import takes a
   while — there are ~1,400 scripts and several shader packs).

2. **Check the console is clean.** If you see compile errors *before* touching
   anything, stop and read them: that's the thing to fix first, not the build.

3. **Switch the platform**: File → Build Profiles → macOS → *Switch Platform*.
   (This menu is called Build Settings on older Unity versions.)

4. **Run the validator**: `MateEngine ▸ Build ▸ Validate macOS setup`.

   It checks that the macOS module is installed, the scene exists, the
   Steamworks define survived, and — if you built the native plugin — that it is
   genuinely universal. Fix anything it reports as `FAIL`.

5. **Build**: `MateEngine ▸ Build ▸ macOS Universal (Intel + Apple silicon)`.

   Output lands in `Builds/macOS/Universal/MateEngineX.app` (gitignored).

6. **Run it from Finder**, not from the Unity Editor. See the warning below.

### Why you must test a build, not the Editor

Two features only exist in a player:

- **Window transparency does nothing in the Editor.** UniWindowController states
  this explicitly. In the Editor you'll see a normal, opaque Game view; that is
  not a bug.
- **The native plugin is deliberately not loaded in the Editor** (it changes the
  app's activation policy and would hide the Unity Editor from the Dock). So
  `MacDesktop.IsAvailable` is always false while editing.

---

## 3. What you should see on first launch

In order:

1. A **transparent, borderless window** with the avatar visible and the desktop
   showing through the background.
2. The avatar playing an **idle animation**, breathing, with spring bones settled.
3. **Drag the avatar** — it should follow, play the drag animation, and the hair
   and clothes should swing against the movement.
4. **Move the window** (drag a non-avatar area, or use the DragMove handle if the
   prefab has one) — spring bones should react to the acceleration.
5. **Move your mouse around the avatar** — head, then eyes, then a little spine
   motion should follow the cursor.
6. **Open the menu → load model → pick a `.vrm`.** The avatar should swap.

If 1–5 all work, the port's core is good. Step 6 is the one with a platform
caveat (see below).

### Quitting

There's no Dock icon in a full build (accessory activation policy) and the menu
bar icon isn't ported yet, so **Cmd+Q** is the way to quit. If the app seems
"invisible", check Activity Monitor for `MateEngineX`.

---

## 3b. Read the startup report first

On launch the app writes one block to the log telling you which subsystems came
up. Find it with **Console.app** (filter for `MateEngineX`) or by running the
binary directly:

```bash
./MateEngineX.app/Contents/MacOS/MateEngineX
```

```
=== MateEngine macOS startup report ===
  Unity            : 6000.2.6f2
  OS               : macOS 14.5
  Graphics API     : Metal
  Window geometry  : OK  (LibUniWinC responding)
  Native backend   : not loaded - MateMacNative.bundle not found. Run macos-native/build.sh.
  Steam            : not initialised (expected outside the Steam edition; ...)
  Feature status on macOS:
    + VRM load, animations, spring bones, mouse tracking
    ...
```

What to look for:

- **`Window geometry : OK`** — the important one. Without it, dragging, cursor
  tracking and spring-bone physics are inert.
- **`Native backend : not loaded`** — expected if you skipped
  `macos-native/build.sh`. Only affects Dock sitting, the menu bar icon and
  window enumeration, none of which are wired to UI yet.
- **`Steam : not initialised`** — normal. The free build has no Steam edition.

The report comes from `Assets/MATE ENGINE - MacOS Port/Runtime/MacStartupReport.cs`.
Set `MacStartupReport.LogOnStartup = false` to silence it once things are stable.

## 4. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `DllNotFoundException: user32` or `kernel32` at startup | A Windows-only call site is still reachable | Read the stack trace — it names the component. Every known one is guarded; this would be a missed path, so report it with the trace. |
| `DllNotFoundException: LibUniWinC` | UniWindowController's bundle missing from the build | Check `Assets/MATE ENGINE - Packages/Kirurobo/UniWindowController/Runtime/Plugins/MacOS/LibUniWinC.bundle` still exists and is enabled for the macOS player. |
| Window is opaque black/white instead of transparent | Player settings, or running in the Editor | Build and run the `.app`. If it's still opaque in the player, check the UniWindowController component's *Is Transparent* toggle in the scene. |
| Avatar invisible, window fine | Camera background not being cleared to transparent, or the VRM failed to load | Check the console for `[VRMLoader]` errors. Try the default model first via the menu. |
| Clicking the avatar does nothing | Click-through enabled, or the window ignores hit tests | Disable *Is Click Through* on the UniWindowController component; make sure an EventSystem exists in the scene. |
| **"Load model" does nothing on Apple silicon** | `StandaloneFileBrowser.bundle` is **x86_64-only** | Expected and logged. Rebuild that bundle as universal, or load a model path that's already saved. Not an issue on Intel. |
| **App closes immediately after launch** | Something called `Application.Quit()` in startup | Check the log. Steam no longer does this on macOS, so if it happens, read the last log line for the real exception. |
| Log says `Native backend : not loaded` | `build.sh` never run | Harmless for the basics. Run `./macos-native/build.sh` on a Mac if you want the plugin machinery present. |
| No sound / never dances to music | By design — dance-to-music is off | The CoreAudio tap is unimplemented; animations themselves are unaffected. |
| Avatar doesn't sit on windows | By design | Window sitting needs `CGWindowList` and isn't ported. |
| App is "damaged and can't be opened" | Unsigned + quarantined download | `xattr -dr com.apple.quarantine MateEngineX.app`, or sign/notarise properly (step 6). |
| Build fails in Xcode link stage | IL2CPP needs the Xcode toolchain | `sudo xcode-select -s /Applications/Xcode.app` and accept the license. |
| Editor won't open the project at all | You're on a Windows/Linux-only Unity install | Expected: no macOS module, so no macOS target. The editor script resolves macOS APIs by reflection precisely so the *project* still compiles. |

---

## 5. Permissions

The basics need **none**. macOS will not prompt for anything to run a
transparent window, animate an avatar or load a VRM from a file you pick.

This matters because the features that *do* need permission are all switched off
in this cut:

- *Screen Recording* — big screen mode, screensaver, window capture.
- *System Audio Recording Only* — dance-to-music.

When those get ported, the app must ask for permission at the point of use and
be relaunched afterwards; a TCC grant does not apply to the already-running
process.

---

## 6. Shipping it (for later)

Not needed to test, but required before distributing to anyone else:

```bash
# 1. Sign the *inner* bundle first, then the app. Same Team ID, hardened runtime.
codesign --force --options runtime --timestamp \
  --sign "Developer ID Application: Your Name (TEAMID)" \
  MateEngineX.app/Contents/PlugIns/MateMacNative.bundle
codesign --force --options runtime --timestamp --deep \
  --sign "Developer ID Application: Your Name (TEAMID)" \
  MateEngineX.app

# 2. Notarise, then staple. Without this, Gatekeeper says "damaged".
ditto -c -k --keepParent MateEngineX.app MateEngineX.zip
xcrun notarytool submit MateEngineX.zip --keychain-profile "AC_PROFILE" --wait
xcrun stapler staple MateEngineX.app
```

Notes:

- **Do not enable the App Sandbox.** A sandboxed app cannot enumerate or read
  other applications' windows, which rules out window sitting later.
- `--deep` on the outer app is convenient but sloppy; explicitly signing every
  nested `.bundle` and `.dylib` first is the reliable route.
- The Steam edition needs Valve-side macOS depots; the GitHub edition works
  standalone.

---

## 6b. Building without a Mac (CI)

`.github/workflows/macos-build.yml` builds the universal `.app` on a GitHub
Actions macOS runner and uploads it as an artifact. It needs three repository
secrets — `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD` — from the GameCI
activation flow. Without them the job stops at activation, not compilation.

This is the only way to get an actual `.app` out of this repo without a Mac in
front of you. The workflow has never been run, so treat its first execution as
part of the debugging rather than a trusted result.

## 7. Optional: build the native plugin

Only needed for Dock sitting, the menu bar icon and window enumeration — none of
which are wired up to UI yet. But if you want the machinery present:

```bash
xcode-select --install          # once
./macos-native/build.sh         # produces a universal .bundle + .meta
```

Then re-run `MateEngine ▸ Build ▸ Validate macOS setup`; it will report the
plugin's actual architectures by parsing the Mach-O header.

---

## 8. Known gaps in this cut

| Feature | State |
|---|---|
| VRM upload, animations, spring bones, mouse tracking, transparent window | ✅ In |
| Menu edge-flipping on multi-display | ✅ In (needs the plugin to know display frames) |
| Window sitting | ❌ Needs `CGWindowList` + occlusion retuning |
| Dock sitting | ❌ Plugin has the API; UI not wired |
| Menu bar icon / quit menu | ❌ Plugin has the API; UI not wired |
| Big screen, screensaver, ambient probe | ❌ Disabled on macOS |
| Dance-to-music | ❌ CoreAudio tap unimplemented (recipe in `macos-native/MateMacAudio.m`) |
| Local LLM | ❌ Windows-only server binary |
| Discord Rich Presence | ❌ No macOS native library ships |

Full detail, including effort estimates for the gaps:
[`PORTING-macOS.md`](PORTING-macOS.md).
