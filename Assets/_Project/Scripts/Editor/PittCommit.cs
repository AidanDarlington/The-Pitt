using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ThePitt.EditorTools
{
    /// <summary>
    /// The Pitt → Commit. Both options ask for a commit message, then commit everything to the dev branch and push it.
    /// "Commit to GitHub" also takes a patch notes .md and a build zip: the commit is tagged v&lt;version&gt; and a
    /// draft GitHub release is made from the notes and the zips (the chosen zip plus the other platform's zip of the same
    /// version). The release workflow (.github/workflows/release.yml) publishes drafts when dev is merged into main.
    /// "Without build" adds no tag, so the latest build tag stays the newest one.
    /// Uploading releases needs the GitHub CLI: sudo pacman -S github-cli, then gh auth login.
    /// </summary>
    public class PittCommit : EditorWindow
    {
        private const string Branch = "dev";

        [MenuItem("The Pitt/Commit/Commit to GitHub (without build)", priority = 2)]
        public static void CommitWithoutBuild() => Open(withBuild: false);

        [MenuItem("The Pitt/Commit/Commit to GitHub", priority = 3)]
        public static void CommitWithBuild() => Open(withBuild: true);

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string BuildsRoot => Path.Combine(ProjectRoot, "Builds");
        private static string PatchnotesRoot => Path.Combine(ProjectRoot, "Patchnotes");

        private bool withBuild;
        private string message = "";
        private string patchnotes = "";
        private string zip = "";

        private static void Open(bool withBuild)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Commit", "Stop play mode before committing.", "OK");
                return;
            }
            if (!Directory.Exists(Path.Combine(ProjectRoot, ".git")))
            {
                EditorUtility.DisplayDialog("Commit", "This project isn't a git repository.", "OK");
                return;
            }
            if (withBuild && !Run("gh", "auth status", out _, out _))
            {
                EditorUtility.DisplayDialog("Commit",
                    "Uploading a release needs the GitHub CLI, signed in.\n\nInstall it with: sudo pacman -S github-cli\nThen run: gh auth login",
                    "OK");
                return;
            }

            var window = CreateInstance<PittCommit>();
            window.withBuild = withBuild;
            window.titleContent = new GUIContent(withBuild ? "Commit to GitHub" : "Commit to GitHub (without build)");
            if (withBuild)
            {
                window.zip = LatestZip();
                string notes = Path.Combine(PatchnotesRoot, $"Version {VersionOf(window.zip)}.md");
                window.patchnotes = File.Exists(notes) ? notes : "";
            }
            window.minSize = new Vector2(480, withBuild ? 250 : 170);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Commit message");
            message = EditorGUILayout.TextArea(message, GUILayout.MinHeight(60));

            bool ready = !string.IsNullOrWhiteSpace(message);
            if (withBuild)
            {
                EditorGUILayout.Space();
                FileField("Patch notes", ref patchnotes, "md", PatchnotesRoot);
                FileField("Build zip", ref zip, "zip", BuildsRoot);
                ready &= File.Exists(patchnotes) && File.Exists(zip);
                if (File.Exists(zip))
                {
                    string version = VersionOf(zip);
                    EditorGUILayout.LabelField($"Tag v{version} · {string.Join(", ", ZipsFor(zip).Select(Path.GetFileName))}", EditorStyles.miniLabel);
                    if (Run("git", $"rev-parse --verify --quiet refs/tags/v{version}", out _, out _))
                    {
                        EditorGUILayout.HelpBox($"Tag v{version} already exists. Build a new version first.", MessageType.Error);
                        ready = false;
                    }
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", GUILayout.Width(90))) Close();
            using (new EditorGUI.DisabledScope(!ready))
            {
                if (GUILayout.Button("Commit", GUILayout.Width(90)))
                {
                    string version = withBuild ? VersionOf(zip) : null;
                    var zips = withBuild ? ZipsFor(zip) : null;
                    string text = message.Trim(), notes = patchnotes;
                    Close();
                    // After this frame's GUI, so the window is gone before the editor blocks on git.
                    EditorApplication.delayCall += () => Commit(text, version, notes, zips);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }

        private static void FileField(string label, ref string path, string extension, string folder)
        {
            EditorGUILayout.BeginHorizontal();
            path = EditorGUILayout.TextField(label, path);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string start = File.Exists(path) ? Path.GetDirectoryName(path) : folder;
                string picked = EditorUtility.OpenFilePanel(label, start, extension);
                if (!string.IsNullOrEmpty(picked))
                {
                    path = picked;
                    GUI.FocusControl(null);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Commits to dev and pushes; with a version, also tags it and uploads a draft release.</summary>
        private static void Commit(string message, string version, string notes, List<string> zips)
        {
            var results = new List<string>();
            bool ok = false;
            try
            {
                ok = CommitSteps(message, version, notes, zips, results);
            }
            catch (Exception e)
            {
                results.Add(e.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            string summary = string.Join("\n", results);
            if (ok) Debug.Log("PittCommit:\n" + summary);
            else Debug.LogError("PittCommit:\n" + summary);
            EditorUtility.DisplayDialog(ok ? "Committed" : "Commit problem", summary, "OK");
        }

        private static bool CommitSteps(string message, string version, string notes, List<string> zips, List<string> results)
        {
            EditorUtility.DisplayProgressBar("Commit", $"Switching to {Branch}…", 0.1f);
            AssetDatabase.SaveAssets();
            Run("git", "rev-parse --abbrev-ref HEAD", out string current, out _);
            if (current.Trim() != Branch)
            {
                bool exists = Run("git", $"rev-parse --verify --quiet refs/heads/{Branch}", out _, out _);
                if (!Run("git", exists ? $"switch {Branch}" : $"switch -c {Branch}", out _, out string switchError))
                {
                    results.Add($"Couldn't switch to {Branch}: {switchError}");
                    return false;
                }
                results.Add(exists ? $"Switched to {Branch}." : $"Created branch {Branch}.");
            }

            EditorUtility.DisplayProgressBar("Commit", "Committing…", 0.25f);
            Run("git", "add -A", out _, out _);
            bool changes = !Run("git", "diff --cached --quiet", out _, out _);
            if (changes)
            {
                string messageFile = Path.Combine(ProjectRoot, "Temp", "PittCommitMessage.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(messageFile));
                File.WriteAllText(messageFile, message + "\n");
                bool committed = Run("git", $"commit -q -F {Quote(messageFile)}", out _, out string commitError);
                File.Delete(messageFile);
                if (!committed)
                {
                    results.Add($"Commit failed: {commitError}");
                    return false;
                }
                results.Add($"Committed to {Branch}.");
            }
            else if (version == null)
            {
                results.Add("Nothing to commit.");
                return false;
            }
            else results.Add("No changes to commit; tagging the current commit.");

            string tag = version == null ? null : "v" + version;
            if (tag != null)
            {
                if (!Run("git", $"tag -a {tag} -m \"Build {version}\"", out _, out string tagError))
                {
                    results.Add($"Tag {tag} failed: {tagError}");
                    return false;
                }
                results.Add($"Tagged {tag}.");
            }

            EditorUtility.DisplayProgressBar("Commit", "Pushing to GitHub…", 0.45f);
            string refs = tag == null ? Branch : $"{Branch} refs/tags/{tag}";
            if (!Run("git", $"push -u origin {refs}", out _, out string pushError))
            {
                results.Add($"Push failed: {pushError}");
                return false;
            }
            results.Add(tag == null ? $"Pushed {Branch}." : $"Pushed {Branch} and {tag}.");
            if (tag == null) return true;

            EditorUtility.DisplayProgressBar("Commit", $"Uploading release {tag}…", 0.7f);
            string files = string.Join(" ", zips.Select(Quote));
            if (!Run("gh", $"release create {tag} --draft --verify-tag --title \"Version {version}\" --notes-file {Quote(notes)} {files}", out _, out string releaseError))
            {
                results.Add($"Release upload failed: {releaseError}");
                results.Add($"Retry from a terminal: gh release create {tag} --draft --verify-tag --title \"Version {version}\" --notes-file {Quote(notes)} {files}");
                return false;
            }
            results.Add($"Draft release {tag} uploaded ({zips.Count} zip(s)); it's published when {Branch} is merged into main.");
            return true;
        }

        /// <summary>The version in a build zip's name (ThePitt-0.1.3-Windows.zip → 0.1.3), else the project's.</summary>
        private static string VersionOf(string zipPath)
        {
            var match = Regex.Match(Path.GetFileName(zipPath ?? ""), @"^ThePitt-(.+)-[^-]+\.zip$");
            return match.Success ? match.Groups[1].Value : PlayerSettings.bundleVersion;
        }

        /// <summary>The chosen zip plus the other platforms' zips of the same version beside it.</summary>
        private static List<string> ZipsFor(string zipPath)
        {
            var zips = new List<string> { Path.GetFullPath(zipPath) };
            var match = Regex.Match(Path.GetFileName(zipPath), @"^(ThePitt-.+-)[^-]+\.zip$");
            if (match.Success)
                foreach (var sibling in Directory.GetFiles(Path.GetDirectoryName(zipPath), match.Groups[1].Value + "*.zip"))
                    if (!zips.Contains(Path.GetFullPath(sibling))) zips.Add(Path.GetFullPath(sibling));
            return zips;
        }

        /// <summary>A zip from the newest version folder under Builds/, or "" if there are none.</summary>
        private static string LatestZip()
        {
            if (!Directory.Exists(BuildsRoot)) return "";
            var newest = Directory.GetDirectories(BuildsRoot)
                .Select(dir => (dir, ok: Version.TryParse(Path.GetFileName(dir), out var v), v))
                .Where(d => d.ok && Directory.GetFiles(d.dir, "*.zip").Length > 0)
                .OrderByDescending(d => d.v)
                .FirstOrDefault();
            return newest.dir == null ? "" : Directory.GetFiles(newest.dir, "*.zip").OrderBy(f => f).First();
        }

        private static string Quote(string path) => "\"" + path + "\"";

        /// <summary>
        /// Runs git or gh in the project folder; true if it exited 0. Prompts are turned off so a missing login fails
        /// with a message instead of hanging the editor.
        /// </summary>
        private static bool Run(string program, string arguments, out string output, out string error)
        {
            var info = new ProcessStartInfo(program, arguments)
            {
                WorkingDirectory = ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            info.EnvironmentVariables["GH_PROMPT_DISABLED"] = "1";
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")))
                info.EnvironmentVariables["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
            try
            {
                using var process = Process.Start(info);
                var stdout = process.StandardOutput.ReadToEndAsync();
                error = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit();
                output = stdout.Result;
                return process.ExitCode == 0;
            }
            catch (Exception e)
            {
                output = "";
                error = $"{program}: {e.Message}";
                return false;
            }
        }
    }
}
