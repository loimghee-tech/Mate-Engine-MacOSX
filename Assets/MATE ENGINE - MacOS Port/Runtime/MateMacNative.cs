/*
 * MateMacNative.cs
 *
 * Raw P/Invoke surface for the macOS backend plugin (macos-native/MateMacNative.h).
 *
 * This file is the direct analogue of "MATE ENGINE - Scripts/APIs/WinApi.cs":
 * same shapes, same coordinate convention (top-left origin, +y down, points),
 * so the math in the Avatar* handlers carries over unchanged.
 *
 * Only compiled into the macOS standalone player. The plugin is deliberately
 * NOT loaded into the Unity Editor (it changes NSApplication's activation
 * policy, which would hide the Editor from the Dock), so nothing here may be
 * called from editor code paths.
 */

#if UNITY_STANDALONE_OSX && !UNITY_EDITOR

using System;
using System.Runtime.InteropServices;

namespace MateEngine.MacOS
{
    public static class MateMacNative
    {
        private const string LibName = "MateMacNative";

        public const int MaxOwnerName = 256;
        public const int MaxWindowTitle = 512;

        // Keep a reference so the GC does not collect the thunk.
        private static MMNStatusItemCallback _statusCallbackKeepAlive;

        [StructLayout(LayoutKind.Sequential)]
        public struct MMNRect
        {
            public double x, y, width, height;

            public MMNRect(double x, double y, double width, double height)
            {
                this.x = x;
                this.y = y;
                this.width = width;
                this.height = height;
            }

            public override string ToString() =>
                string.Format("({0:0.#}, {1:0.#}) {2:0.#}x{3:0.#}", x, y, width, height);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MMNDisplay
        {
            public uint displayId;
            public MMNRect frame;
            public MMNRect visibleFrame;
            public double scale;
            public int isPrimary;
            public int isBuiltin;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct MMNWindow
        {
            public uint windowId;
            public int ownerPid;
            public int layer;
            public double alpha;
            public int isOnScreen;
            public MMNRect bounds;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxOwnerName)]
            public string ownerName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxWindowTitle)]
            public string title;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MMNImage
        {
            public int width;
            public int height;
            public int stride;
            public IntPtr pixels;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MMNStatusItemCallback(int handle, int itemIndex);

        #region Lifecycle

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_Initialize();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_GetApiVersion();

        #endregion

        #region Displays

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_GetDisplayCount();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_GetDisplays([Out] MMNDisplay[] outDisplays, int maxCount);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double MMN_GetMenuBarHeight();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double MMN_GetDockThickness(int displayIndex, out int edge);

        #endregion

        #region Windows

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_GetWindowCount();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_GetWindows([Out] MMNWindow[] outWindows, int maxCount);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_GetWindow(uint windowId, out MMNWindow outWindow);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_GetCursorPosition(out double x, out double y);

        #endregion

        #region Capture

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_HasScreenCapturePermission();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_RequestScreenCapturePermission();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_CaptureWindow(uint windowId, int maxWidth, int maxHeight,
                                                   int flipVertical, out MMNImage outImage);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_CaptureScreenRegion(MMNRect region, int maxWidth, int maxHeight,
                                                         int flipVertical, out MMNImage outImage);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_CaptureDisplay(int displayIndex, int flipVertical,
                                                    out MMNImage outImage);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void MMN_FreeImage(ref MMNImage image);

        #endregion

        #region Own app

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_SetAccessoryActivationPolicy(int enabled);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_SetAppNapEnabled(int enabled);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_ActivateApp();

        #endregion

        #region Menu bar item

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_StatusItemCreate(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string iconPath);

        // char** on the native side marshals as a managed string[] of LPUTF8Str.
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_StatusItemSetMenu(
            int handle,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)]
            string[] titles,
            int[] enabled,
            int count);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void MMN_StatusItemSetCallback(MMNStatusItemCallback callback);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void MMN_StatusItemDestroy(int handle);

        public static void SetStatusItemCallback(MMNStatusItemCallback callback)
        {
            _statusCallbackKeepAlive = callback;
            MMN_StatusItemSetCallback(callback);
        }

        #endregion

        #region Launch at login

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_SetLaunchAtLogin(int enabled);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_GetLaunchAtLogin();

        #endregion

        #region Audio

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_AudioTapSupported();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MMN_AudioStart();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void MMN_AudioStop();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double MMN_AudioGetOutputPeak();

        #endregion
    }
}

#endif
