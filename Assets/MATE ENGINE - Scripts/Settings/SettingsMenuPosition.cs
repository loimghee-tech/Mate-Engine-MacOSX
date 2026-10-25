using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

public class SettingsMenuPosition : MonoBehaviour
{
    [Serializable]
    public class MenuEntry
    {
        public RectTransform settingsMenu;
        [HideInInspector] public float originalX;
        [HideInInspector] public float originalY;
        [HideInInspector] public Vector2 lastApplied;
    }

    [Header("Menus to track")]
    public List<MenuEntry> menus = new List<MenuEntry>();

    [Header("Edge margin in Pixels")]
    public float edgeMargin = 50f;

    [Header("Checks per second")]
    public float checkFPS = 20f;

    [Header("Monitor refresh (sec)")]
    public float monitorRefreshInterval = 2f;

    private IntPtr unityHWND;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private readonly List<RECT> monitorRects = new List<RECT>();
    private MonitorEnumProc enumProc;
    private float checkTimer;
    private float monitorTimer;
    private bool lastAtRightEdge;
    private bool initedEdge;

    void Start()
    {
#if !UNITY_STANDALONE_OSX
        unityHWND = Process.GetCurrentProcess().MainWindowHandle;
        enumProc = EnumProc;
        RefreshMonitors();
#endif
        foreach (var menu in menus)
        {
            if (!menu.settingsMenu) continue;
            menu.originalX = menu.settingsMenu.anchoredPosition.x;
            menu.originalY = menu.settingsMenu.anchoredPosition.y;
            menu.lastApplied = menu.settingsMenu.anchoredPosition;
        }
    }

    void Update()
    {
#if UNITY_STANDALONE_OSX
        UpdateMacOS();
        return;
#endif
        if (unityHWND == IntPtr.Zero) return;

        monitorTimer += Time.unscaledDeltaTime;
        if (monitorTimer >= Mathf.Max(0.1f, monitorRefreshInterval))
        {
            monitorTimer = 0f;
            RefreshMonitors();
        }

        checkTimer += Time.unscaledDeltaTime;
        float step = 1f / Mathf.Max(1f, checkFPS);
        if (checkTimer < step) return;
        checkTimer = 0f;

        RECT winRect;
        if (!GetWindowRect(unityHWND, out winRect)) return;

        RECT screen = monitorRects.Count > 0 ? GetBestMonitor(winRect) : new RECT { left = 0, top = 0, right = Screen.currentResolution.width, bottom = Screen.currentResolution.height };

        bool atRightEdge = winRect.right >= (screen.right - edgeMargin);
        if (!initedEdge) { lastAtRightEdge = atRightEdge; initedEdge = true; }

        if (atRightEdge != lastAtRightEdge)
        {
            lastAtRightEdge = atRightEdge;
            for (int i = 0; i < menus.Count; i++)
            {
                var m = menus[i];
                if (!m.settingsMenu) continue;
                Vector2 target = new Vector2(atRightEdge ? -m.originalX : m.originalX, m.originalY);
                if (m.lastApplied != target)
                {
                    m.settingsMenu.anchoredPosition = target;
                    m.lastApplied = target;
                }
            }
        }
    }

#if UNITY_STANDALONE_OSX
    /// <summary>
    /// macOS equivalent of the RECT edge test below: flip the menus when the
    /// window's right edge is within `edgeMargin` of the right edge of the
    /// display it is on. Uses UniWindowController for the window rect and the
    /// native plugin for display frames; stays inert when either is missing.
    /// </summary>
    void UpdateMacOS()
    {
        float now = Time.unscaledTime;
        if (now - _lastMacCheck < 1f / Mathf.Max(1f, checkFPS)) return;
        _lastMacCheck = now;

        if (!MateEngine.MacOS.DesktopGeometry.IsGeometryAvailable) return;

        var displays = MateEngine.MacOS.MacDesktop.GetDisplays();
        if (displays.Length == 0) return;

        Rect win = MateEngine.MacOS.DesktopGeometry.WindowRect();

        // Pick the display the window overlaps most.
        Rect screen = displays[0].frame;
        float best = -1f;
        for (int i = 0; i < displays.Length; i++)
        {
            Rect f = displays[i].frame;
            float w = Mathf.Min(win.xMax, f.xMax) - Mathf.Max(win.x, f.x);
            float h = Mathf.Min(win.yMax, f.yMax) - Mathf.Max(win.y, f.y);
            float area = (w > 0f && h > 0f) ? w * h : 0f;
            if (area > best) { best = area; screen = f; }
        }

        bool atRightEdge = win.xMax >= (screen.xMax - edgeMargin);
        if (!initedEdge) { lastAtRightEdge = atRightEdge; initedEdge = true; }

        if (atRightEdge == lastAtRightEdge) return;
        lastAtRightEdge = atRightEdge;

        for (int i = 0; i < menus.Count; i++)
        {
            var m = menus[i];
            if (!m.settingsMenu) continue;
            Vector2 target = new Vector2(atRightEdge ? -m.originalX : m.originalX, m.originalY);
            if (m.lastApplied != target)
            {
                m.settingsMenu.anchoredPosition = target;
                m.lastApplied = target;
            }
        }
    }

    float _lastMacCheck;
#endif

    bool EnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data)
    {
        monitorRects.Add(lprc);
        return true;
    }

    void RefreshMonitors()
    {
        monitorRects.Clear();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, enumProc, IntPtr.Zero);
    }

    RECT GetBestMonitor(RECT win)
    {
        int idx = 0, maxArea = 0;
        for (int i = 0; i < monitorRects.Count; i++)
        {
            int a = OverlapArea(win, monitorRects[i]);
            if (a > maxArea) { maxArea = a; idx = i; }
        }
        return monitorRects[idx];
    }

    int OverlapArea(RECT a, RECT b)
    {
        int x1 = Math.Max(a.left, b.left);
        int x2 = Math.Min(a.right, b.right);
        int y1 = Math.Max(a.top, b.top);
        int y2 = Math.Min(a.bottom, b.bottom);
        int w = x2 - x1;
        int h = y2 - y1;
        return (w > 0 && h > 0) ? w * h : 0;
    }
}
