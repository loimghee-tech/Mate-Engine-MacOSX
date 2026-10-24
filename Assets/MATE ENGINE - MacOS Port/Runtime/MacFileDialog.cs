/*
 * MacFileDialog.cs
 *
 * File picker used by the macOS port for "upload a VRM".
 *
 * Why this exists
 * ---------------
 * VRMLoader and MEModHandler call StandaloneFileBrowser, which on macOS goes
 * through Plugins/StandaloneFileBrowser.bundle. I inspected that binary: it is
 * a **thin x86_64** Mach-O, not universal. So on an Intel Mac the picker works
 * exactly as it does on Windows, but on Apple silicon the DllImport fails and
 * the "upload model" button dies with a DllNotFoundException that nothing
 * catches.
 *
 * This wrapper keeps the working Intel path and turns the Apple silicon case
 * into a clear, actionable log line instead of an exception. Rebuilding that
 * bundle for arm64 (or replacing it with UniWindowController's FilePanel, whose
 * LibUniWinC bundle is already universal) makes both work; see PORTING-macOS.md.
 */

#if UNITY_STANDALONE_OSX

using System;
using System.IO;
using UnityEngine;

namespace MateEngine.MacOS
{
    public static class MacFileDialog
    {
        private static bool _toldUserAboutMissingBundle;

        /// <summary>
        /// Opens a file picker and returns the selected path, or null when the
        /// user cancelled or the native dialog could not be loaded.
        /// Never throws.
        /// </summary>
        public static string OpenFile(string title, SFB.ExtensionFilter[] extensions)
        {
            try
            {
                string[] paths = SFB.StandaloneFileBrowser.OpenFilePanel(title, "", extensions, false);
                if (paths == null || paths.Length == 0) return null;

                string path = paths[0];
                if (string.IsNullOrEmpty(path)) return null;

                // StandaloneFileBrowser can hand back a URL on some platforms.
                if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    try { path = new Uri(path).LocalPath; }
                    catch { /* keep the raw string */ }
                }

                return File.Exists(path) ? path : null;
            }
            catch (DllNotFoundException)
            {
                ReportMissingBundle();
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                ReportMissingBundle();
                return null;
            }
            catch (Exception e)
            {
                Debug.LogError("[MacFileDialog] File picker failed: " + e);
                return null;
            }
        }

        private static void ReportMissingBundle()
        {
            if (_toldUserAboutMissingBundle) return;
            _toldUserAboutMissingBundle = true;

            Debug.LogError(
                "[MacFileDialog] The native file dialog bundle could not be loaded.\n" +
                "  On macOS this is expected on Apple silicon: " +
                "Plugins/StandaloneFileBrowser.bundle is built for x86_64 only.\n" +
                "  Model loading itself is unaffected - a model can still be loaded " +
                "from the saved model path or dragged in.\n" +
                "  Fix: rebuild StandaloneFileBrowser.bundle as a universal binary, or " +
                "switch the picker to Kirurobo.FilePanel. See PORTING-macOS.md.");
        }
    }
}

#endif
