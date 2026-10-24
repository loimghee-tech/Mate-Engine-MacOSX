/*
 * DesktopGeometry.cs
 *
 * Cross-platform window/cursor geometry for the macOS port.
 *
 * Why this exists
 * ---------------
 * The Windows code reads the window rect and the cursor through `user32.dll`.
 * Both values are also available from UniWindowController, whose native library
 * (LibUniWinC) ships as a **universal** macOS .bundle — x86_64 and arm64 — so
 * these two features need no extra native code at all. That is why the port's
 * "basics" can land before anything is compiled.
 *
 * Convention: points, top-left origin, +y down — same as a Win32 RECT. Every
 * consumer of these values only uses deltas (spring-bone velocity when the
 * window is dragged), so a consistent origin is what matters.
 *
 * On Windows this class is unused: the original user32 code paths are kept
 * under `#if UNITY_STANDALONE_WIN` in each caller, untouched.
 */

using UnityEngine;

namespace MateEngine.MacOS
{
    public static class DesktopGeometry
    {
        /// <summary>
        /// Top-left corner of our own window in desktop points.
        /// Returns Vector2Int.zero when the window controller is not ready yet.
        /// </summary>
        public static Vector2Int WindowTopLeft()
        {
            var controller = Kirurobo.UniWindowController.current;
            if (controller == null) return Vector2Int.zero;

            Vector2 pos = controller.windowPosition;
            return new Vector2Int(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.y));
        }

        /// <summary>Size of our own window in points.</summary>
        public static Vector2Int WindowSize()
        {
            var controller = Kirurobo.UniWindowController.current;
            if (controller == null) return Vector2Int.zero;

            Vector2 size = controller.windowSize;
            return new Vector2Int(Mathf.RoundToInt(size.x), Mathf.RoundToInt(size.y));
        }

        /// <summary>
        /// Our own window as a Rect in desktop points (top-left origin).
        /// </summary>
        public static Rect WindowRect()
        {
            Vector2Int pos = WindowTopLeft();
            Vector2Int size = WindowSize();
            return new Rect(pos.x, pos.y, size.x, size.y);
        }

        /// <summary>
        /// Global cursor position in desktop points, top-left origin — the same
        /// convention as Win32 GetCursorPos. Cheap enough to call per frame.
        /// </summary>
        public static Vector2 CursorPosition()
        {
            return Kirurobo.UniWindowController.GetCursorPosition();
        }

        /// <summary>
        /// True when LibUniWinC is loaded and able to report geometry. Used to
        /// keep features inert instead of throwing when the plugin is missing.
        /// </summary>
        public static bool IsGeometryAvailable
        {
            get
            {
                var controller = Kirurobo.UniWindowController.current;
                if (controller == null) return false;

                Vector2 size = controller.windowSize;
                return size.x > 1f && size.y > 1f;
            }
        }
    }
}
