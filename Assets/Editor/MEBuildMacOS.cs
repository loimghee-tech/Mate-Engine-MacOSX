/*
 * MEBuildMacOS.cs
 *
 * Build entry points for the macOS port.
 *
 * EditorBuildSettings.asset ships with an empty scene list in this repo, so a
 * plain "Build" from the Build Profiles window produces an app with no scenes.
 * These methods build an explicit scene list, force the settings a desktop pet
 * needs, and never touch the Windows settings that are already committed.
 *
 * Menu: MateEngine/Build/...
 * CI:   Unity -quit -batchmode -projectPath . \
 *          -executeMethod MEBuildMacOS.BuildUniversalCI
 */

#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class MEBuildMacOS
{
    private const string MainScene = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";
    private const string PluginBundle = "Assets/Plugins/MacOS/MateMacNative.bundle";
    private const string DefaultOutputRoot = "Builds/macOS";

    // ------------------------------------------------------------------
    // Menu entries
    // ------------------------------------------------------------------

    [MenuItem("MateEngine/Build/macOS Universal (Intel + Apple silicon)", priority = 100)]
    public static void BuildUniversal()
    {
        Build(UnityEditor.OSXStandalone.OSArchitecture.x64ARM64,
              Path.Combine(DefaultOutputRoot, "Universal", "MateEngineX.app"));
    }

    [MenuItem("MateEngine/Build/macOS Intel only (x86_64)", priority = 101)]
    public static void BuildIntelOnly()
    {
        Build(UnityEditor.OSXStandalone.OSArchitecture.x64,
              Path.Combine(DefaultOutputRoot, "Intel", "MateEngineX.app"));
    }

    [MenuItem("MateEngine/Build/macOS Apple silicon only (arm64)", priority = 102)]
    public static void BuildAppleSiliconOnly()
    {
        Build(UnityEditor.OSXStandalone.OSArchitecture.ARM64,
              Path.Combine(DefaultOutputRoot, "AppleSilicon", "MateEngineX.app"));
    }

    [MenuItem("MateEngine/Build/Validate macOS setup", priority = 120)]
    public static void ValidateSetup()
    {
        bool ok = true;

        void Check(string label, bool condition, string hint)
        {
            if (condition)
            {
                Debug.Log("[ME macOS] OK   " + label);
            }
            else
            {
                ok = false;
                Debug.LogError("[ME macOS] FAIL " + label + "\n         -> " + hint);
            }
        }

        Check("Native plugin present",
              Directory.Exists(PluginBundle),
              "Run ./macos-native/build.sh on a Mac. Without it the app builds but no window," +
              " dock, menu-bar or capture features work.");

        if (Directory.Exists(PluginBundle))
        {
            string binary = Path.Combine(PluginBundle, "Contents/MacOS/MateMacNative");
            string archs = DescribeMachOArchitectures(binary);
            Check("Native plugin is universal",
                  archs.Contains("x86_64") && archs.Contains("arm64"),
                  "Plugin slices found: " + (archs.Length == 0 ? "<none>" : archs) +
                  ". Rebuild with build.sh, which passes both -arch flags.");
            Debug.Log("[ME macOS]      plugin architectures: " + archs);
        }

        Check("Main scene exists",
              File.Exists(MainScene),
              "Expected " + MainScene);

        Check("Editor scene list is populated",
              EditorBuildSettings.scenes.Length > 0,
              "Empty scene list in ProjectSettings; the build methods here inject the scene" +
              " list at build time instead.");

        Check("Steamworks defines enabled",
              PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone)
                           .Contains("STEAMWORKS_NET"),
              "ProjectSettings has STEAMWORKS_NET for Standalone; keep it or Steam init is" +
              " compiled out.");

        if (File.Exists("macos-native/MateMacAudio.m"))
        {
            Debug.Log("[ME macOS] note: the CoreAudio process tap (dance-to-music) is a stub." +
                      " MMN_AudioGetOutputPeak() returns -1, so auto-dance stays off.");
        }

        Debug.Log(ok
            ? "[ME macOS] Validation passed. Ready to build."
            : "[ME macOS] Validation found problems; see the errors above.");
    }

    // ------------------------------------------------------------------
    // CI
    // ------------------------------------------------------------------

    /// <summary>Batchmode entry point. Exits non-zero on a failed build.</summary>
    public static void BuildUniversalCI()
    {
        string output = Environment.GetEnvironmentVariable("ME_BUILD_OUTPUT");
        if (string.IsNullOrEmpty(output))
            output = Path.Combine(DefaultOutputRoot, "Universal", "MateEngineX.app");

        if (!Build(UnityEditor.OSXStandalone.OSArchitecture.x64ARM64, output))
            EditorApplication.Exit(1);
        EditorApplication.Exit(0);
    }

    // ------------------------------------------------------------------
    // Core
    // ------------------------------------------------------------------

    private static bool Build(UnityEditor.OSXStandalone.OSArchitecture architecture, string outputPath)
    {
        if (!File.Exists(PluginBundle + "/Contents/MacOS/MateMacNative"))
        {
            Debug.LogError("[ME macOS] MateMacNative.bundle is missing. Run ./macos-native/build.sh" +
                           " first, or the build will start but every desktop feature will be dead.");
            return false;
        }

        // Apple silicon requires IL2CPP (Mono is x86_64 only on macOS), so a
        // universal or arm64 build must use it. That means Xcode has to be
        // installed; Unity shells out to it for the final link.
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        UnityEditor.OSXStandalone.UserBuildSettings.architecture = architecture;

        // Desktop-pet friendly player settings.
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.usePlayerLog = true;

        // macOS specifics: the app is unsigned by default, which is fine for
        // local testing but must be replaced by Developer ID signing and
        // notarisation for distribution. See PORTING-macOS.md.
        PlayerSettings.macOS.buildNumber = DateTime.UtcNow.ToString("yyyyMMdd");
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "com.Shinymoon.MateEngineX");

        string directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var scenes = BuildScenes();
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

        Debug.LogError("[ME macOS] Build " + summary.result + " with " + summary.totalErrors + " errors.");
        return false;
    }

    private static string[] BuildScenes()
    {
        // Prefer the committed scene list when someone populates it; otherwise
        // fall back to the known main scene so builds are never empty.
        var configured = new List<string>();
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (scene.enabled && File.Exists(scene.path)) configured.Add(scene.path);
        }
        if (configured.Count > 0) return configured.ToArray();

        return File.Exists(MainScene) ? new[] { MainScene } : Array.Empty<string>();
    }

    /// <summary>
    /// Reads the Mach-O / fat header to report which slices a plugin contains.
    /// Purely diagnostic: it lets ValidateSetup catch a plugin built with a
    /// single -arch before Unity produces an app that dies in dlopen.
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

            bool littleEndian = buffer[0] != 0xca && buffer[0] != 0xbe;
            uint magic = ReadUInt32(buffer, 0, littleEndian);

            // Fat binary: 0xcafebabe / 0xbebafeca, then (cputype, cpusubtype, ...) triples.
            if (magic == 0xcafebabe || magic == 0xbebafeca)
            {
                uint count = ReadUInt32(buffer, 4, magic == 0xcafebabe);
                var names = new List<string>();
                for (int i = 0; i < count && 8 + (i + 1) * 20 <= read; i++)
                {
                    int offset = 8 + i * 20;
                    uint cpuType = ReadUInt32(buffer, offset, true);
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

            // Thin Mach-O 64: 0xfeedfacf.
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
