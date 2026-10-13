using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ThePitt.EditorTools
{
    /// <summary>
    /// One-click versioned builds: The Pitt → Build → Windows + Linux.
    /// Each run bumps the patch version (Project Settings → Player → Version: 0.1.0 → 0.1.1), builds each platform
    /// (one after the other; Unity builds one at a time) into Builds/&lt;version&gt;/&lt;Platform&gt;, zips each into
    /// Builds/&lt;version&gt;/ThePitt-&lt;version&gt;-&lt;Platform&gt;.zip. Earlier versions are never overwritten. The editor
    /// switches back to whichever platform it was on. Committing, tagging v&lt;version&gt; and the GitHub release happen
    /// afterwards from The Pitt → Commit → Commit to GitHub (see PittCommit).
    /// Also runnable from the command line with the editor closed:
    /// Unity -batchmode -quit -projectPath "…" -executeMethod ThePitt.EditorTools.PittBuilds.BuildAllFromCommandLine
    /// </summary>
    public static class PittBuilds
    {
        /// <summary>
        /// The scenes ticked in the project's scene list (File → Build Profiles → Scene List), in order: Menu, World,
        /// Slice. Read at build time so a new scene can't be left out of builds.
        /// </summary>
        private static string[] Scenes
        {
            get
            {
                var list = new List<string>();
                foreach (var scene in EditorBuildSettings.scenes)
                    if (scene.enabled) list.Add(scene.path);
                return list.ToArray();
            }
        }

        [MenuItem("The Pitt/Build/Windows + Linux", priority = 1)]
        public static void BuildAll() => Run(new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64 }, interactive: true);

        [MenuItem("The Pitt/Build/Windows", priority = 20)]
        public static void BuildWindows() => Run(new[] { BuildTarget.StandaloneWindows64 }, interactive: true);

        [MenuItem("The Pitt/Build/Linux", priority = 21)]
        public static void BuildLinux() => Run(new[] { BuildTarget.StandaloneLinux64 }, interactive: true);

        [MenuItem("The Pitt/Build/Open Builds Folder", priority = 40)]
        public static void OpenFolder()
        {
            Directory.CreateDirectory(BuildsRoot);
            EditorUtility.RevealInFinder(BuildsRoot);
        }

        /// <summary>For -executeMethod: builds both and exits non-zero if either fails.</summary>
        public static void BuildAllFromCommandLine()
        {
            bool ok = Run(new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64 }, interactive: false);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string BuildsRoot => Path.Combine(ProjectRoot, "Builds");

        private static bool Run(BuildTarget[] targets, bool interactive)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("PittBuilds: stop play mode before building.");
                return false;
            }
            var scenes = Scenes;
            if (scenes.Length == 0)
            {
                Debug.LogError("PittBuilds: no scenes are ticked in the scene list.");
                return false;
            }

            // A new version for every build, so none is ever overwritten.
            string previousVersion = PlayerSettings.bundleVersion;
            string version = NextVersion(previousVersion);
            PlayerSettings.bundleVersion = version;
            AssetDatabase.SaveAssets();
            string versionFolder = Path.Combine(BuildsRoot, version);

            var startTarget = EditorUserBuildSettings.activeBuildTarget;
            var results = new List<string> { $"Version {version}" };
            bool allOk = true, anyOk = false;
            foreach (var target in targets)
            {
                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                {
                    results.Add($"{Name(target)}: build support isn't installed (add it in Unity Hub).");
                    allOk = false;
                    continue;
                }
                string folder = Path.Combine(versionFolder, Name(target));
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                Directory.CreateDirectory(folder);
                string exe = Path.Combine(folder, target == BuildTarget.StandaloneWindows64 ? "ThePitt.exe" : "ThePitt.x86_64");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = exe,
                    target = target,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,
                });
                var summary = report.summary;
                bool ok = summary.result == BuildResult.Succeeded;
                allOk &= ok;
                anyOk |= ok;
                if (!ok)
                {
                    results.Add($"{Name(target)}: {summary.result} with {summary.totalErrors} error(s), see the Console.");
                    continue;
                }
                string zip = Zip(folder, Path.Combine(versionFolder, $"ThePitt-{version}-{Name(target)}.zip"));
                results.Add($"{Name(target)}: built in {summary.totalTime.TotalSeconds:0}s, {summary.totalSize / (1024f * 1024f):0} MB → {zip}");
            }

            // Back to the platform the editor was on.
            if (EditorUserBuildSettings.activeBuildTarget != startTarget)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(startTarget), startTarget);

            if (!anyOk)
            {
                // Nothing built: the version goes back, as if this run never happened.
                PlayerSettings.bundleVersion = previousVersion;
                AssetDatabase.SaveAssets();
                results.Add($"Nothing built; version left at {previousVersion}.");
            }

            string message = string.Join("\n", results);
            if (allOk) Debug.Log("PittBuilds:\n" + message);
            else Debug.LogError("PittBuilds:\n" + message);
            if (interactive && EditorUtility.DisplayDialog(allOk ? "Builds finished" : "Build problem", message, "Open folder", "Close"))
                EditorUtility.RevealInFinder(Directory.Exists(versionFolder) ? versionFolder : BuildsRoot);
            return allOk;
        }

        /// <summary>1.2.3 → 1.2.4 (the last number goes up; a version without one gets ".1").</summary>
        public static string NextVersion(string version)
        {
            var parts = (string.IsNullOrWhiteSpace(version) ? "0.1.0" : version.Trim()).Split('.');
            if (int.TryParse(parts[parts.Length - 1], out int last)) parts[parts.Length - 1] = (last + 1).ToString();
            else return version.Trim() + ".1";
            return string.Join(".", parts);
        }

        /// <summary>Zips a platform's build into one file to keep or hand out; returns its path.</summary>
        private static string Zip(string folder, string zipPath)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            // The folder Unity says not to ship (debug symbols) stays out of the zip.
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (relative.Contains("_BackUpThisFolder_ButDontShipItWithYourGame") || relative.Contains("_BurstDebugInformation_DoNotShip")) continue;
                archive.CreateEntryFromFile(file, "ThePitt/" + relative, System.IO.Compression.CompressionLevel.Optimal);
            }
            return zipPath;
        }

        private static string Name(BuildTarget target) => target == BuildTarget.StandaloneWindows64 ? "Windows" : "Linux";
    }
}
