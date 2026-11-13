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
}
