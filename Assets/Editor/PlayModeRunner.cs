using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using TFM.App;

/// <summary>
/// Play-mode smoke test driven from the CLI:
///   Unity.exe -batchmode -projectPath . -executeMethod PlayModeRunner.Run
/// Enters Play Mode, drives the app for a few dozen frames, screenshots the Game view and
/// writes Temp/smoke-report.txt. Exits with code 0 when clean, 1 when anything went wrong.
/// </summary>
public static class PlayModeRunner
{
    const string ReportPath = "Temp/smoke-report.txt";
    const string ShotPath = "Temp/smoke.png";

    static readonly StringBuilder Log = new StringBuilder();
    static readonly List<string> Errors = new List<string>();
    static int _frame;
    static bool _entered;
    static bool _selected;
    static bool _shot;

    public static void Run()
    {
        // Batch mode starts on an empty default scene, so the app scene has to be opened first.
        try
        {
            const string scenePath = "Assets/Scenes/Main.unity";
            if (!File.Exists(scenePath)) { ProjectSetup.RunAll(); }
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath);
            Log.AppendLine("Opened scene " + scenePath);
        }
        catch (Exception e)
        {
            Errors.Add("Could not open the app scene: " + e);
        }

        Application.logMessageReceived += OnLog;
        EditorApplication.update += Tick;
        EditorApplication.isPlaying = true;
        Log.AppendLine("Play mode requested.");
    }

    static void OnLog(string condition, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            Errors.Add(type + ": " + condition + "\n    " + stack);
    }

    static void Tick()
    {
        if (!EditorApplication.isPlaying)
        {
            if (!_entered) return;
            EditorApplication.update -= Tick;
            Report();
            return;
        }

        if (!_entered)
        {
            _entered = true;
            _frame = 0;
            Log.AppendLine("Play mode entered.");
        }

        _frame++;

        if (_frame == 15 && !_selected) { _selected = true; DriveUI(); }
        if (_frame == 40 && !_shot) { _shot = true; Screenshot(); }
        if (_frame == 60)
        {
            Log.AppendLine("Leaving play mode.");
            EditorApplication.isPlaying = false;
        }
    }

    static void DriveUI()
    {
        try
        {
            // Both arguments are given explicitly: Unity 2023.1 has no single-argument
            // FindObjectsByType overload taking FindObjectsInactive, only the two-parameter
            // form. Passing both keeps this compiling on 2023.1 and on 6000.x alike.
            AppMain[] apps = UnityEngine.Object.FindObjectsByType<AppMain>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            Log.AppendLine("AppMain instances: " + apps.Length);
            if (apps.Length == 0) { Errors.Add("No AppMain found in the active scene."); return; }

            AppMain app = apps[0];
            FieldInfo fMods = typeof(AppMain).GetField("_modules", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fActive = typeof(AppMain).GetField("_activeModule", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fStatus = typeof(AppMain).GetField("_status", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fConn = typeof(AppMain).GetField("_ctx", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fMods == null) { Errors.Add("Could not reflect AppMain._modules."); return; }

            var mods = fMods.GetValue(app) as IList;
            if (mods == null) { Errors.Add("Module list was null."); return; }

            Log.AppendLine("Registered modules: " + mods.Count);
            int propsAt = -1;
            for (int i = 0; i < mods.Count; i++)
            {
                object m = mods[i];
                string title = "";
                PropertyInfo p = m.GetType().GetProperty("Title");
                if (p != null) title = (string)p.GetValue(m, null);
                Log.AppendLine("  [" + i + "] " + m.GetType().Name + "  title=\"" + title + "\"");
                if (m.GetType() == typeof(PropsModule)) propsAt = i;
            }

            if (propsAt < 0) { Errors.Add("PropsModule is not registered in AppMain."); return; }
            fActive.SetValue(app, propsAt);
            Log.AppendLine("Switched to Props module (index " + propsAt + ").");

            // Load a fully-decodable reference file through the module's own loader.
            object propsMod = mods[propsAt];
            MethodInfo load = propsMod.GetType().GetMethod("LoadFile",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (load == null) { Errors.Add("PropsModule.LoadFile not found."); return; }

            string file = Path.Combine(Directory.GetCurrentDirectory(),
                "Assets/prop-reference/obj_backpackClementine.prop");
            if (!File.Exists(file)) { Errors.Add("Reference prop missing: " + file); return; }

            load.Invoke(propsMod, new object[] { file });
            Log.AppendLine("Loaded " + Path.GetFileName(file) + " into the Props module.");

            FieldInfo fFile = propsMod.GetType().GetField("_file", BindingFlags.NonPublic | BindingFlags.Instance);
            object propFile = fFile != null ? fFile.GetValue(propsMod) : null;
            if (propFile == null) { Errors.Add("Props module did not keep the loaded file."); return; }

            PropertyInfo pFields = propFile.GetType().GetProperty("Fields");
            var fields = pFields.GetValue(propFile, null) as ICollection;
            Log.AppendLine("Fields decoded in the module: " + (fields != null ? fields.Count : -1));

            PropertyInfo pPartial = propFile.GetType().GetProperty("IsPartial");
            Log.AppendLine("IsPartial: " + pPartial.GetValue(propFile, null));

            // Select the first field so the inspector renders an editor for it.
            FieldInfo fSel = propsMod.GetType().GetField("_selected", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fSel != null) { fSel.SetValue(propsMod, 0); Log.AppendLine("Selected field 0 for the inspector."); }

            // Exercise the port tab with a donor.
            FieldInfo fDonorPath = propsMod.GetType().GetField("_donorPath", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo loadDonor = propsMod.GetType().GetMethod("LoadDonor",
                BindingFlags.NonPublic | BindingFlags.Instance);
            string donor = Path.Combine(Directory.GetCurrentDirectory(),
                "Assets/prop-reference/obj_groundplaneAlphasHardwareExteriorA.prop");
            if (loadDonor != null && File.Exists(donor))
            {
                fDonorPath.SetValue(propsMod, donor);
                loadDonor.Invoke(propsMod, new object[] { donor });
                FieldInfo fCmp = propsMod.GetType().GetField("_compare", BindingFlags.NonPublic | BindingFlags.Instance);
                object cmp = fCmp.GetValue(propsMod);
                Log.AppendLine("Donor compare: " + (cmp != null ? "populated" : "NULL"));
            }

            // Render the Port tab as well, so its drawing code is exercised.
            FieldInfo fTab = propsMod.GetType().GetField("_tab", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fTab != null) { fTab.SetValue(propsMod, Enum.ToObject(fTab.FieldType, 1)); Log.AppendLine("Switched Props tab to Port for a render pass."); }

            if (fStatus != null)
                Log.AppendLine("Status bar: \"" + fStatus.GetValue(app) + "\"");
        }
        catch (Exception e)
        {
            Errors.Add("DriveUI threw: " + e);
        }
    }

    static void Screenshot()
    {
        try
        {
            ScreenCapture.CaptureScreenshot(ShotPath);
            Log.AppendLine("Screenshot requested (" + Screen.width + "x" + Screen.height + ") -> " + ShotPath);
        }
        catch (Exception e) { Errors.Add("Screenshot threw: " + e); }
    }

    static void Report()
    {
        Log.AppendLine();
        Log.AppendLine("Errors / exceptions: " + Errors.Count);
        foreach (string e in Errors) Log.AppendLine("  ---- " + e);
        Log.AppendLine(Errors.Count == 0 ? "RESULT: CLEAN" : "RESULT: PROBLEMS");
        File.WriteAllText(ReportPath, Log.ToString());
        Debug.Log("[PlayModeRunner] " + (Errors.Count == 0 ? "CLEAN" : "PROBLEMS") + ", report at " + ReportPath);
        EditorApplication.Exit(Errors.Count == 0 ? 0 : 1);
    }
}
