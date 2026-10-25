/*
 * MacStartupReport.cs
 *
 * First-launch self-diagnostic for the macOS port.
 *
 * The point of this file
 * ----------------------
 * A first macOS build is going to be the first time any of this runs. When
 * something is wrong the failure mode is usually silent -- a transparent window
 * that never appears, an avatar with no idle animation, a button that does
 * nothing. Rather than making you guess, this logs one structured report at
 * startup saying exactly which subsystems came up and which did not, so a bug
 * report is one console line instead of a investigation.
 *
 * It never throws and never changes behaviour: it only reads state.
 *
 * Remove it (or set LogOnStartup to false) once the port is stable.
 */

#if UNITY_STANDALONE_OSX

using System.Text;
using UnityEngine;

namespace MateEngine.MacOS
{
    public static class MacStartupReport
    {
        /// <summary>Set false to stop the automatic log line.</summary>
        public static bool LogOnStartup = true;

        private static bool _reported;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnAfterSceneLoad()
        {
            if (!LogOnStartup || _reported) return;
            _reported = true;

            string report;
            try
            {
                report = Build();
            }
            catch (System.Exception e)
            {
                report = "MateEngine macOS startup report failed: " + e.Message;
            }

            // Debug.Log, not LogWarning: a healthy start should be quiet in the
            // console's warning view.
            Debug.Log(report);
        }

        /// <summary>Re-runs the checks and returns the report. Safe to call anytime.</summary>
        public static string Build()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== MateEngine macOS startup report ===");

            sb.AppendLine("  Unity            : " + Application.unityVersion);
            sb.AppendLine("  OS               : " + SystemInfo.operatingSystem);
            sb.AppendLine("  CPU              : " + SystemInfo.processorType);
            sb.AppendLine("  Graphics API     : " + SystemInfo.graphicsDeviceType);
            sb.AppendLine("  Architecture     : " + (System.IntPtr.Size == 8 ? "64-bit" : "32-bit"));
            sb.AppendLine("  .app bundle      : " + Application.dataPath);

            // --- Window control (UniWindowController / LibUniWinC) -----------
            bool geometry = Safe(() => DesktopGeometry.IsGeometryAvailable);
            sb.AppendLine("  Window geometry  : " + (geometry
                ? "OK  (LibUniWinC responding)"
                : "MISSING - LibUniWinC.bundle did not load. Window position," +
                  " cursor position and spring-bone drag physics will be inert."));

            // --- Optional native backend -------------------------------------
            sb.AppendLine("  Native backend   : " + (MacDesktop.IsAvailable
                ? "OK  (MateMacNative loaded)"
                : "not loaded - " + (MacDesktop.LastError ?? "unknown reason")));

            if (MacDesktop.IsAvailable)
            {
                sb.AppendLine("  Screen Recording : " + (MacDesktop.ScreenCapturePermissionGranted
                    ? "granted"
                    : "NOT granted (only needed for big screen / screensaver capture)"));
            }

            // --- Steam -------------------------------------------------------
            bool steam;
            try { steam = SteamManager.Initialized; }
            catch { steam = false; }
            sb.AppendLine("  Steam            : " + (steam
                ? "initialised"
                : "not initialised (expected outside the Steam edition; Workshop and" +
                  " DLC content will be unavailable)"));

            // --- What is switched off on macOS -------------------------------
            sb.AppendLine("  Feature status on macOS:");
            sb.AppendLine("    + VRM load, animations, spring bones, mouse tracking");
            sb.AppendLine("    + transparent / click-through / always-on-top window");
            sb.AppendLine("    - window sitting, Dock sitting, menu bar icon (not ported)");
            sb.AppendLine("    - big screen, screensaver, ambient probe (disabled)");
            sb.AppendLine("    - dance-to-music (CoreAudio tap not implemented)");
            sb.AppendLine("    - Discord Rich Presence (no macOS native library)");

            sb.AppendLine("=======================================");
            return sb.ToString();
        }

        private static bool Safe(System.Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }
    }
}

#endif
