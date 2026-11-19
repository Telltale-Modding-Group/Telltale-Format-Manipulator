using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Player build entry point for releases and for scripted smoke tests.
// CLI: Unity.exe -quit -batchmode -projectPath . -executeMethod BuildScript.BuildWindows64
public static class BuildScript
{
    const string OutputDir = "Builds/Windows64";
    const string ExeName = "TelltaleFormatManipulator.exe";

    [MenuItem("Tools/Telltale Format Manipulator/Build Windows 64-bit")]
    public static void BuildWindows64()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string outDir = Path.Combine(root, OutputDir);
        Directory.CreateDirectory(outDir);

        if (!File.Exists(ProjectSetup.ScenePath))
        {
            Debug.Log("[Build] Scene missing - regenerating project scene and settings.");
            ProjectSetup.RunAll();
        }

        var scenes = new[] { ProjectSetup.ScenePath };
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(outDir, ExeName),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary s = report.summary;
        Debug.Log(string.Format("[Build] {0} - {1:0.0} MB - {2} errors, {3} warnings\n{4}",
            s.result, s.totalSize / 1048576.0, s.totalErrors, s.totalWarnings, s.outputPath));

        if (s.result != BuildResult.Succeeded)
        {
            if (Application.isBatchMode) EditorApplication.Exit(1);
            return;
        }

        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    [MenuItem("Tools/Telltale Format Manipulator/Open Build Folder")]
    public static void OpenBuildFolder()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string outDir = Path.Combine(root, OutputDir);
        if (Directory.Exists(outDir)) EditorUtility.RevealInFinder(outDir);
    }

    // The archive name dumps are read from disk at runtime, so they have to sit next to the
    // player. A player only ships StreamingAssets as loose files, and the dumps intentionally do
    // not live there in the project, so every build - from this menu or File > Build - copies
    // them into <exe>_Data/StreamingAssets/Names.
    const string NameDumpSource = "Assets/Resources/Names";

    [UnityEngine.Scripting.Preserve]
    class CopyNameDumpsStep : UnityEditor.Build.IPostprocessBuildWithReport
    {
        public int callbackOrder { get { return 0; } }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.result != BuildResult.Succeeded) return;
            CopyNameDumps(report.summary.outputPath);
        }
    }

    static void CopyNameDumps(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;

        string root = Directory.GetParent(Application.dataPath).FullName;
        string src = Path.Combine(root, NameDumpSource);
        if (!Directory.Exists(src))
        {
            Debug.LogWarning("[Build] No name dumps at " + NameDumpSource + "; file references will show as hashes.");
            return;
        }

        string dataDir = Path.Combine(Path.GetDirectoryName(exePath),
            Path.GetFileNameWithoutExtension(exePath) + "_Data");
        string dst = Path.Combine(Path.Combine(dataDir, "StreamingAssets"), "Names");

        int copied = 0;
        foreach (string file in Directory.GetFiles(src, "*.bin", SearchOption.AllDirectories))
        {
            string rel = file.Substring(src.Length).TrimStart('\\', '/');
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
            copied++;
        }
        Debug.Log("[Build] Copied " + copied + " name dumps to " + dst);
    }
}
