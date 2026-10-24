/*
 * MEBuildMacOS.cs
 *
 * Build entry points for the macOS port.
 *
 * Two constraints shaped this file:
 *
 * 1. EditorBuildSettings.asset ships with an empty scene list in this repo, so a
 *    plain Build from the Build Profiles window produces an app with no scenes.
 *    These methods build an explicit scene list instead.
 *
 * 2. This project is Windows-first. `UnityEditor.OSXStandalone.UserBuildSettings`
 *    only exists when the **macOS Build Support module** is installed, so
 *    referencing it directly breaks compilation for anyone on a Windows-only or
 *    Linux-only editor install ("error CS0234"). Everything macOS-specific is
 *    therefore resolved by reflection at call time, and every failure is a clear
 *    log line rather than a compile error.
 *
 * Menu: MateEngine/Build/...
 * CI:   Unity -quit -batchmode -projectPath . -executeMethod MEBuildMacOS.BuildUniversalCI
 */

#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class MEBuildMacOS
{
    private const string MainScene = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";
    private const string PluginBinary = "Assets/Plugins/MacOS/MateMacNative.bundle/Contents/MacOS/MateMacNative";
    private const string DefaultOutputRoot = "Builds/macOS";
    private const string BundleId = "com.Shinymoon.MateEngineX";

    public enum MacArchitecture
    {
        Intel,
        AppleSilicon,
        Universal,
    }

    // ------------------------------------------------------------------
    // Menu entries
    // ------------------------------------------------------------------

    [MenuItem("MateEngine/Build/macOS Universal (Intel + Apple silicon)", priority = 100)]
    public static void BuildUniversal()
    {
        Build(MacArchitecture.Universal, Path.Combine(DefaultOutputRoot, "Universal", "MateEngineX.app"));
    }

    [MenuItem("MateEngine/Build/macOS Intel only (x86_64)", priority = 101)]
    public static void BuildIntelOnly()
    {
        Build(MacArchitecture.Intel, Path.Combine(DefaultOutputRoot, "Intel", "MateEngineX.app"));
    }

    [MenuItem("MateEngine/Build/macOS Apple silicon only (arm64)", priority = 102)]
    public static void BuildAppleSiliconOnly()
    {
        Build(MacArchitecture.AppleSilicon, Path.Combine(DefaultOutputRoot, "AppleSilicon", "MateEngineX.app"));
    }

    [MenuItem("MateEngine/Build/Validate macOS setup", priority = 120)]
    public static void ValidateSetup()
    {
        bool ok = true;

        Action<string, bool, string> check = (label, condition, hint) =>
        {
            if (condition)
            {
                Debug.Log("[ME macOS] OK   " + label);
                return;
            }
            ok = false;
            Debug.LogError("[ME macOS] FAIL " + label + "\n         -> " + hint);
        };

        check("macOS Build Support module installed",
              HasMacOSBuildModule(),
              "Install it via Unity Hub > Installs > Add Modules. Without it this " +
              "editor cannot produce a .app at all.");

        check("Native backend plugin present",
              File.Exists(PluginBinary),
              "Optional for the basics. Only needed for Dock sitting, the menu bar " +
              "icon and window enumeration. Build it on a Mac with " +
              "./macos-native/build.sh.");

        if (File.Exists(PluginBinary))
        {
            string archs = DescribeMachOArchitectures(PluginBinary);
            check("Native plugin is universal",
                  archs.Contains("x86_64") && archs.Contains("arm64"),
                  "Slices found: " + archs + ". Rebuild with build.sh, which passes " +
                  "both -arch flags; a single-slice plugin will not load on the other " +
                  "architecture.");
            Debug.Log("[ME macOS]      plugin architectures: " + archs);
        }

        check("Main scene exists", File.Exists(MainScene), "Expected " + MainScene);

        check("Steamworks define enabled",
              PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone)
                           .Contains("STEAMWORKS_NET"),
              "ProjectSettings has STEAMWORKS_NET for Standalone; keep it, or Steam " +
              "initialisation is compiled out.");

        // Known gaps, surfaced so nobody debugs them later.
        Debug.Log("[ME macOS] note: window sitting (CGWindowList), Dock sitting, the " +
                  "menu bar icon, big screen mode and dance-to-music are switched off " +
                  "on macOS. VRM loading, animations, spring bones, mouse tracking and " +
                  "the transparent window all work. See PORTING-macOS.md.");

        Debug.Log(ok
            ? "[ME macOS] Validation passed. Ready to build."
            : "[ME macOS] Validation found problems; see the errors above.");
    }

    // ------------------------------------------------------------------
    // CI
    // ------------------------------------------------------------------

    /// <summary>Batchmode entry point. Exits non-zero on failure.</summary>
    public static void BuildUniversalCI()
    {
        string output = Environment.GetEnvironmentVariable("ME_BUILD_OUTPUT");
        if (string.IsNullOrEmpty(output))
            output = Path.Combine(DefaultOutputRoot, "Universal", "MateEngineX.app");

        bool success = Build(MacArchitecture.Universal, output);
        EditorApplication.Exit(success ? 0 : 1);
    }

    // ------------------------------------------------------------------
    // Core
    // ------------------------------------------------------------------

    private static bool Build(MacArchitecture architecture, string outputPath)
    {
        if (!HasMacOSBuildModule())
        {
            Debug.LogError("[ME macOS] The macOS Build Support module is not installed in " +
                           "this editor. Add it via Unity Hub > Installs > Add Modules, then " +
                           "reopen the project.");
            return false;
        }

        if (!File.Exists(PluginBinary))
        {
            Debug.LogWarning("[ME macOS] MateMacNative.bundle is missing, so Dock sitting, the " +
                             "menu bar icon and window enumeration will be unavailable. The " +
                             "basics (VRM, animations, transparent window) do not need it. " +
                             "Build it on a Mac with ./macos-native/build.sh.");
        }

        // Apple silicon requires IL2CPP -- Unity's macOS Mono player is x86_64
        // only -- so arm64 and universal builds must use it. Intel-only could stay
        // on Mono, but IL2CPP is used for every architecture so that what you test
        // locally is what ships. That means Xcode must be installed: Unity shells
        // out to it for the final link.
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);

        if (!TryApplyArchitecture(architecture))
        {
            Debug.LogError("[ME macOS] Could not set the macOS build architecture. Set it " +
                           "manually in File > Build Profiles > macOS > Architecture, then " +
                           "build from there.");
            return false;
        }

        ApplyDesktopPetPlayerSettings();

        string directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string[] scenes = BuildScenes();
        if (scenes.Length == 0)
        {
            Debug.LogError("[ME macOS] No scenes to build. Expected " + MainScene);
            return false;
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None,
        };

        Debug.Log("[ME macOS] Building " + architecture + " -> " + outputPath);
        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log(string.Format("[ME macOS] Build succeeded in {0:0.0}s, {1:0.0} MB -> {2}",
                                    summary.totalTime.TotalSeconds,
                                    summary.totalSize / (1024.0 * 1024.0),
                                    outputPath));
            return true;
        }

        Debug.LogError("[ME macOS] Build " + summary.result + " with " +
                       summary.totalErrors + " error(s).");
        return false;
    }

    /// <summary>
    /// Player settings a desktop pet needs. All core PlayerSettings members, so
    /// they compile without the macOS module.
    /// </summary>
    private static void ApplyDesktopPetPlayerSettings()
    {
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.usePlayerLog = true;

        try
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, BundleId);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ME macOS] Could not set the bundle identifier: " + e.Message);
        }
    }

    // ------------------------------------------------------------------
    // Reflection helpers (the macOS module may be absent)
    // ------------------------------------------------------------------

    private static Type FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }
        return null;
    }

    /// <summary>True when UnityEditor.OSXStandalone exists, i.e. macOS support is installed.</summary>
    private static bool HasMacOSBuildModule()
    {
        return FindType("UnityEditor.OSXStandalone.UserBuildSettings") != null
            || FindType("UnityEditor.OSXStandalone.UserBuildSettings+Architecture") != null;
    }

    /// <summary>
    /// Sets UnityEditor.OSXStandalone.UserBuildSettings.architecture by reflection.
    /// The property's enum is UnityEditor.Build.OSArchitecture with members
    /// x64 / ARM64 / x64ARM64, but names are matched case-insensitively so a
    /// rename in a future Unity version degrades to a warning.
    /// </summary>
    private static bool TryApplyArchitecture(MacArchitecture architecture)
    {
        string wantedName = architecture == MacArchitecture.Intel ? "x64"
                          : architecture == MacArchitecture.AppleSilicon ? "ARM64"
                          : "x64ARM64";

        try
        {
            Type settings = FindType("UnityEditor.OSXStandalone.UserBuildSettings");
            if (settings == null)
            {
                Debug.LogWarning("[ME macOS] UnityEditor.OSXStandalone.UserBuildSettings not found.");
                return false;
            }

            PropertyInfo property = settings.GetProperty(
                "architecture",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (property == null || !property.CanWrite)
            {
                Debug.LogWarning("[ME macOS] UserBuildSettings.architecture is not writable " +
                                 "in this Unity version.");
                return false;
            }

            Type enumType = property.PropertyType;
            object value = null;
            foreach (string candidate in Enum.GetNames(enumType))
            {
                if (string.Equals(candidate, wantedName, StringComparison.OrdinalIgnoreCase))
                {
                    value = Enum.Parse(enumType, candidate);
                    break;
                }
            }

            if (value == null)
            {
                Debug.LogWarning("[ME macOS] '" + wantedName + "' is not a member of " +
                                 enumType.FullName + " (members: " +
                                 string.Join(", ", Enum.GetNames(enumType)) + ").");
                return false;
            }

            property.SetValue(null, value);
            Debug.Log("[ME macOS] Architecture set to " + wantedName + " (" + enumType.Name + ").");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ME macOS] Setting the architecture failed: " + e.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Scenes
    // ------------------------------------------------------------------

    private static string[] BuildScenes()
    {
        // Prefer a configured scene list when one exists; otherwise fall back to
        // the known main scene so builds are never empty.
        var configured = new List<string>();
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (scene.enabled && File.Exists(scene.path)) configured.Add(scene.path);
        }
        if (configured.Count > 0) return configured.ToArray();

        return File.Exists(MainScene) ? new[] { MainScene } : Array.Empty<string>();
    }

    // ------------------------------------------------------------------
    // Mach-O inspection (diagnostics only)
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads the Mach-O / fat header to report which slices a plugin contains, so
    /// ValidateSetup can catch a single-arch plugin before Unity produces an app
    /// that dies inside dlopen.
    /// </summary>
    private static string DescribeMachOArchitectures(string path)
    {
        if (!File.Exists(path)) return string.Empty;

        try
        {
            byte[] buffer = new byte[4096];
            int read;
            using (var stream = File.OpenRead(path))
                read = stream.Read(buffer, 0, buffer.Length);
            if (read < 8) return string.Empty;

            uint magic = ReadUInt32(buffer, 0, true);

            if (magic == 0xcafebabe || magic == 0xbebafeca)
            {
                bool bigEndian = magic == 0xcafebabe;
                uint count = ReadUInt32(buffer, 4, bigEndian);
                var names = new List<string>();
                for (int i = 0; i < count && 8 + (i + 1) * 20 <= read; i++)
                {
                    uint cpuType = ReadUInt32(buffer, 8 + i * 20, bigEndian);
                    switch (cpuType)
                    {
                        case 0x01000007: names.Add("x86_64"); break;
                        case 7: names.Add("i386"); break;
                        case 0x0100000c: names.Add("arm64"); break;
                        case 12: names.Add("arm"); break;
                        default: names.Add("cpu:" + cpuType.ToString("x")); break;
                    }
                }
                return string.Join(", ", names);
            }

            if (magic == 0xfeedfacf || magic == 0xcffaedfe)
            {
                uint cpuType = ReadUInt32(buffer, 4, magic == 0xfeedfacf);
                if (cpuType == 0x01000007) return "x86_64 (thin)";
                if (cpuType == 0x0100000c) return "arm64 (thin)";
                return "thin cpu:" + cpuType.ToString("x");
            }

            return "unrecognised (0x" + magic.ToString("x8") + ")";
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }

    private static uint ReadUInt32(byte[] buffer, int offset, bool littleEndian)
    {
        if (littleEndian)
        {
            return (uint)(buffer[offset]
                          | (buffer[offset + 1] << 8)
                          | (buffer[offset + 2] << 16)
                          | (buffer[offset + 3] << 24));
        }
        return (uint)((buffer[offset] << 24)
                      | (buffer[offset + 1] << 16)
                      | (buffer[offset + 2] << 8)
                      | buffer[offset + 3]);
    }
}

#endif
