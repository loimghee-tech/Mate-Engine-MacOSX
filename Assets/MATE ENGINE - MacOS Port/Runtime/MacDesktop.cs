/*
 * MacDesktop.cs
 *
 * Unity-facing macOS desktop services. This is the layer the ported game code
 * should talk to instead of WinApi / DwmApi / uWindowCapture.
 *
 * Everything here is safe to call on any platform: on Windows, in the Editor, or
 * on a Mac without the native plugin present, the methods return empty results
 * and IsAvailable stays false. That lets the Avatar* handlers keep a single code
 * path with a `if (MacDesktop.IsAvailable)` check rather than preprocessor soup.
 *
 * Coordinates
 * -----------
 * All rects are in POINTS, top-left origin, +y down, exactly like Win32 RECTs.
 * Multiply by MacDisplay.scale for backing pixels. See macos-native/README.md.
 */

using System;
using UnityEngine;

namespace MateEngine.MacOS
{
    public struct MacDisplay
    {
        public int index;
        public uint displayId;
        public Rect frame;
        public Rect visibleFrame;   // excludes menu bar, Dock, notch
        public float scale;
        public bool isPrimary;
        public bool isBuiltin;

        /// <summary>
        /// Height of the menu bar / notch inset on this display, in points.
        /// Rects are top-left origin with +y down, so the inset is the gap
        /// between the frame's top edge and the visible area's top edge.
        /// </summary>
        public float MenuBarInset => Mathf.Max(0f, visibleFrame.y - frame.y);
    }

    public struct MacWindow
    {
        public uint id;
        public int ownerPid;
        public string ownerName;
        public string title;        // empty unless Screen Recording is granted
        public Rect bounds;
        public double alpha;

        public bool IsUsable => id != 0 && bounds.width >= 2f && bounds.height >= 2f;
    }

    public static class MacDesktop
    {
        private static bool _initialized;
        private static bool _available;
        private static string _lastError;

        /// <summary>True when the native plugin loaded and initialised successfully.</summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _available;
            }
        }

        /// <summary>Diagnostic string for a failed load, or null.</summary>
        public static string LastError
        {
            get
            {
                EnsureInitialized();
                return _lastError;
            }
        }

        /// <summary>
        /// Mirrors the Windows-only call sites. Safe to call repeatedly; the
        /// native side is idempotent.
        /// </summary>
        public static void Initialize()
        {
            EnsureInitialized();
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

#if UNITY_STANDALONE_OSX
            try
            {
                int api = MateMacNative.MMN_GetApiVersion();
                if (api != 1)
                {
                    _lastError = "MateMacNative ABI mismatch (plugin reports " + api + ")";
                    _available = false;
                    return;
                }

                int rc = MateMacNative.MMN_Initialize();
                if (rc != 0)
                {
                    _lastError = "MMN_Initialize returned " + rc;
                    _available = false;
                    return;
                }

                _available = true;
            }
            catch (DllNotFoundException e)
            {
                _lastError = "MateMacNative.bundle not found. Run macos-native/build.sh. " + e.Message;
                _available = false;
            }
            catch (EntryPointNotFoundException e)
            {
                _lastError = "MateMacNative ABI mismatch: " + e.Message;
                _available = false;
            }
            catch (Exception e)
            {
                _lastError = e.Message;
                _available = false;
            }
#else
            _lastError = "Native backend only available in the macOS standalone player.";
            _available = false;
#endif
        }

        // ------------------------------------------------------------------
        // Displays  (replaces MonitorHelper / EnumDisplayMonitors)
        // ------------------------------------------------------------------

        private static MacDisplay[] _displaysCache;
        private static float _displaysCacheTime;
        private const float DisplaysCacheSeconds = 2f;

        public static MacDisplay[] GetDisplays()
        {
            if (!IsAvailable) return Array.Empty<MacDisplay>();

            // Display topology rarely changes and the call allocates, so cache.
            if (_displaysCache != null && Time.unscaledTime - _displaysCacheTime < DisplaysCacheSeconds)
                return _displaysCache;

#if UNITY_STANDALONE_OSX
            try
            {
                int count = MateMacNative.MMN_GetDisplayCount();
                if (count <= 0) return Array.Empty<MacDisplay>();

                var raw = new MateMacNative.MMNDisplay[count];
                int written = MateMacNative.MMN_GetDisplays(raw, count);
                if (written <= 0) return Array.Empty<MacDisplay>();

                var result = new MacDisplay[written];
                for (int i = 0; i < written; i++)
                {
                    result[i] = new MacDisplay
                    {
                        index = i,
                        displayId = raw[i].displayId,
                        frame = ToRect(raw[i].frame),
                        visibleFrame = ToRect(raw[i].visibleFrame),
                        scale = (float)raw[i].scale,
                        isPrimary = raw[i].isPrimary != 0,
                        isBuiltin = raw[i].isBuiltin != 0,
                    };
                }

                _displaysCache = result;
                _displaysCacheTime = Time.unscaledTime;
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MacDesktop] GetDisplays failed: " + e.Message);
                return Array.Empty<MacDisplay>();
            }
#else
            return Array.Empty<MacDisplay>();
#endif
        }

        public static bool TryGetPrimaryDisplay(out MacDisplay display)
        {
            var all = GetDisplays();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].isPrimary) { display = all[i]; return true; }
            }
            display = all.Length > 0 ? all[0] : default;
            return all.Length > 0;
        }

        public static float MenuBarHeight()
        {
            if (!IsAvailable) return 0f;
#if UNITY_STANDALONE_OSX
            try { return (float)MateMacNative.MMN_GetMenuBarHeight(); }
            catch { return 0f; }
#else
            return 0f;
#endif
        }

        /// <summary>
        /// Dock footprint on a display, as a Rect. This is the macOS answer to
        /// "sit on the taskbar" (AvatarTaskbarController).
        /// Returns false when the Dock is auto-hidden or on another display.
        /// </summary>
        public static bool TryGetDockRect(int displayIndex, out Rect dockRect)
        {
            dockRect = default;
            if (!IsAvailable) return false;

#if UNITY_STANDALONE_OSX
            try
            {
                int edge;
                double thickness = MateMacNative.MMN_GetDockThickness(displayIndex, out edge);
                if (thickness <= 0.5 || edge == 3) return false;

                var displays = GetDisplays();
                if (displayIndex < 0 || displayIndex >= displays.Length) return false;
                Rect f = displays[displayIndex].frame;

                switch (edge)
                {
                    case 0: dockRect = new Rect(f.x, f.yMax - (float)thickness, f.width, (float)thickness); break;
                    case 1: dockRect = new Rect(f.x, f.y, (float)thickness, f.height); break;
                    case 2: dockRect = new Rect(f.xMax - (float)thickness, f.y, (float)thickness, f.height); break;
                    default: return false;
                }
                return true;
            }
            catch { return false; }
#else
            return false;
#endif
        }

        // ------------------------------------------------------------------
        // Windows  (replaces EnumWindows / GetWindowRect / DwmGetWindowAttribute)
        // ------------------------------------------------------------------

        private const int DefaultWindowBuffer = 128;

        /// <summary>
        /// Front-to-back list of normal application windows, matching the order
        /// the Windows build got from EnumWindows + GW_HWNDNEXT.
        /// Bounds and owner names need no permission; titles need Screen Recording.
        /// </summary>
        public static MacWindow[] GetWindows(int maxWindows = DefaultWindowBuffer)
        {
            if (!IsAvailable || maxWindows <= 0) return Array.Empty<MacWindow>();

#if UNITY_STANDALONE_OSX
            try
            {
                int count = MateMacNative.MMN_GetWindowCount();
                if (count <= 0) return Array.Empty<MacWindow>();

                int bufferSize = Mathf.Min(count, maxWindows);
                var raw = new MateMacNative.MMNWindow[bufferSize];
                int written = MateMacNative.MMN_GetWindows(raw, bufferSize);
                if (written <= 0) return Array.Empty<MacWindow>();

                var result = new MacWindow[written];
                for (int i = 0; i < written; i++)
                {
                    result[i] = new MacWindow
                    {
                        id = raw[i].windowId,
                        ownerPid = raw[i].ownerPid,
                        ownerName = raw[i].ownerName ?? string.Empty,
                        title = raw[i].title ?? string.Empty,
                        bounds = ToRect(raw[i].bounds),
                        alpha = raw[i].alpha,
                    };
                }
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MacDesktop] GetWindows failed: " + e.Message);
                return Array.Empty<MacWindow>();
            }
#else
            return Array.Empty<MacWindow>();
#endif
        }

        public static bool TryGetWindow(uint windowId, out MacWindow window)
        {
            window = default;
            if (!IsAvailable || windowId == 0) return false;

#if UNITY_STANDALONE_OSX
            try
            {
                MateMacNative.MMNWindow raw;
                if (MateMacNative.MMN_GetWindow(windowId, out raw) != 0) return false;

                window = new MacWindow
                {
                    id = raw.windowId,
                    ownerPid = raw.ownerPid,
                    ownerName = raw.ownerName ?? string.Empty,
                    title = raw.title ?? string.Empty,
                    bounds = ToRect(raw.bounds),
                    alpha = raw.alpha,
                };
                return true;
            }
            catch { return false; }
#else
            return false;
#endif
        }

        /// <summary>
        /// Global cursor position, same convention as WinApi.GetCursorPos and as
        /// AvatarMouseTracking expects. Falls back to Input.mousePosition outside
        /// the native player.
        /// </summary>
        public static Vector2 GetCursorPosition()
        {
#if UNITY_STANDALONE_OSX
            if (IsAvailable)
            {
                try
                {
                    double x, y;
                    if (MateMacNative.MMN_GetCursorPosition(out x, out y) == 0)
                        return new Vector2((float)x, (float)y);
                }
                catch { /* fall through */ }
            }
#endif
            // Unity's own value is bottom-left origin; convert to top-left so the
            // caller sees one convention everywhere.
            Vector2 mp = Input.mousePosition;
            return new Vector2(mp.x, UnityEngine.Screen.height - mp.y);
        }

        // ------------------------------------------------------------------
        // Capture  (replaces uWindowCapture, unused on Windows anyway)
        // ------------------------------------------------------------------

        public static bool ScreenCapturePermissionGranted
        {
            get
            {
                if (!IsAvailable) return false;
#if UNITY_STANDALONE_OSX
                try { return MateMacNative.MMN_HasScreenCapturePermission() == 1; }
                catch { return false; }
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Triggers the TCC prompt. The grant only takes effect after a relaunch,
        /// so treat a false return as "ask the user to restart the app".
        /// </summary>
        public static bool RequestScreenCapturePermission()
        {
            if (!IsAvailable) return false;
#if UNITY_STANDALONE_OSX
            try { return MateMacNative.MMN_RequestScreenCapturePermission() == 1; }
            catch { return false; }
#else
            return false;
#endif
        }

        /// <summary>
        /// Captures a window into a Texture2D, reusing/resizing the target as
        /// needed. Pass a zero-sized texture to let it size itself.
        /// Returns false on failure or when permission is missing.
        /// </summary>
        public static bool CaptureWindowToTexture(uint windowId, ref Texture2D target,
                                                  int maxWidth = 0, int maxHeight = 0)
        {
            if (!IsAvailable || windowId == 0) return false;

#if UNITY_STANDALONE_OSX
            try
            {
                MateMacNative.MMNImage image;
                // flipVertical = 1 gives bottom-up rows, which is what
                // LoadRawTextureData expects.
                if (MateMacNative.MMN_CaptureWindow(windowId, maxWidth, maxHeight, 1, out image) != 0)
                    return false;

                bool ok = Upload(image, ref target);
                MateMacNative.MMN_FreeImage(ref image);
                return ok;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MacDesktop] CaptureWindow failed: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        public static bool CaptureRegionToTexture(Rect region, ref Texture2D target,
                                                  int maxWidth = 0, int maxHeight = 0)
        {
            if (!IsAvailable) return false;

#if UNITY_STANDALONE_OSX
            try
            {
                var native = new MateMacNative.MMNRect(region.x, region.y, region.width, region.height);
                MateMacNative.MMNImage image;
                if (MateMacNative.MMN_CaptureScreenRegion(native, maxWidth, maxHeight, 1, out image) != 0)
                    return false;

                bool ok = Upload(image, ref target);
                MateMacNative.MMN_FreeImage(ref image);
                return ok;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MacDesktop] CaptureRegion failed: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        public static bool CaptureDisplayToTexture(int displayIndex, ref Texture2D target)
        {
            if (!IsAvailable) return false;

#if UNITY_STANDALONE_OSX
            try
            {
                MateMacNative.MMNImage image;
                if (MateMacNative.MMN_CaptureDisplay(displayIndex, 1, out image) != 0)
                    return false;

                bool ok = Upload(image, ref target);
                MateMacNative.MMN_FreeImage(ref image);
                return ok;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MacDesktop] CaptureDisplay failed: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        // ------------------------------------------------------------------
        // Own app / window  (replaces RemoveTaskbarApp, App Nap, SetForegroundWindow)
        // ------------------------------------------------------------------

        /// <summary>
        /// Hides the app from the Dock and Cmd-Tab. macOS equivalent of the
        /// WS_EX_TOOLWINDOW dance in RemoveTaskbarApp.cs.
        /// </summary>
        public static void SetDockIconHidden(bool hidden)
        {
            if (!IsAvailable) return;
#if UNITY_STANDALONE_OSX
            try { MateMacNative.MMN_SetAccessoryActivationPolicy(hidden ? 1 : 0); }
            catch { }
#endif
        }

        /// <summary>
        /// Disables App Nap so the avatar keeps animating in the background.
        /// Call once at startup; there is no reason to turn it back on.
        /// </summary>
        public static void DisableAppNap()
        {
            if (!IsAvailable) return;
#if UNITY_STANDALONE_OSX
            try { MateMacNative.MMN_SetAppNapEnabled(1); }
            catch { }
#endif
        }

        public static void ActivateApp()
        {
            if (!IsAvailable) return;
#if UNITY_STANDALONE_OSX
            try { MateMacNative.MMN_ActivateApp(); }
            catch { }
#endif
        }

        // ------------------------------------------------------------------
        // Launch at login  (replaces the HKCU Run key in SystemStartHandler)
        // ------------------------------------------------------------------

        public static bool GetLaunchAtLogin()
        {
            if (!IsAvailable) return false;
#if UNITY_STANDALONE_OSX
            try { return MateMacNative.MMN_GetLaunchAtLogin() == 1; }
            catch { return false; }
#else
            return false;
#endif
        }

        public static bool SetLaunchAtLogin(bool enabled)
        {
            if (!IsAvailable) return false;
#if UNITY_STANDALONE_OSX
            try { return MateMacNative.MMN_SetLaunchAtLogin(enabled ? 1 : 0) == 0; }
            catch { return false; }
#else
            return false;
#endif
        }

        // ------------------------------------------------------------------
        // Audio  (replaces NAudio WASAPI session meters)
        // ------------------------------------------------------------------

        public static bool AudioTapSupported
        {
            get
            {
                if (!IsAvailable) return false;
#if UNITY_STANDALONE_OSX
                try { return MateMacNative.MMN_AudioTapSupported() == 1; }
                catch { return false; }
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Master output peak in 0..1, or a negative value when the tap is not
        /// running. Negative means "unknown" -- do not auto-dance on it. See
        /// macos-native/MateMacAudio.m for the remaining work on this one.
        /// </summary>
        public static float GetOutputPeak()
        {
            if (!IsAvailable) return -1f;
#if UNITY_STANDALONE_OSX
            try { return (float)MateMacNative.MMN_AudioGetOutputPeak(); }
            catch { return -1f; }
#else
            return -1f;
#endif
        }

        public static bool StartAudioTap()
        {
            if (!IsAvailable) return false;
#if UNITY_STANDALONE_OSX
            try { return MateMacNative.MMN_AudioStart() == 0; }
            catch { return false; }
#else
            return false;
#endif
        }

        public static void StopAudioTap()
        {
            if (!IsAvailable) return;
#if UNITY_STANDALONE_OSX
            try { MateMacNative.MMN_AudioStop(); }
            catch { }
#endif
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

#if UNITY_STANDALONE_OSX
        private static Rect ToRect(MateMacNative.MMNRect r)
        {
            return new Rect((float)r.x, (float)r.y, (float)r.width, (float)r.height);
        }

        private static bool Upload(MateMacNative.MMNImage image, ref Texture2D target)
        {
            if (image.pixels == IntPtr.Zero || image.width <= 0 || image.height <= 0) return false;

            if (target == null)
            {
                target = new Texture2D(image.width, image.height, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }
            else if (target.width != image.width || target.height != image.height ||
                     target.format != TextureFormat.RGBA32)
            {
                target.Reinitialize(image.width, image.height, TextureFormat.RGBA32, false);
            }

            int byteCount = image.stride * image.height;
            target.LoadRawTextureData(image.pixels, byteCount);
            target.Apply(false);
            return true;
        }
#endif

        // ------------------------------------------------------------------
        // Bootstrap
        // ------------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
#if UNITY_STANDALONE_OSX
            EnsureInitialized();
            if (!_available) return;

            // A desktop pet must never be App-Napped and, by default, should not
            // take a Dock slot. RemoveTaskbarApp does the same thing on Windows.
            DisableAppNap();
            SetDockIconHidden(true);
#endif
        }
    }
}
