using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Regenerates the single app scene and the player/build settings from code, so the project can be
// rebuilt on another Unity version (e.g. 2022.3 LTS for a Windows 7 build) without porting scene files.
// Runs automatically once if the scene is missing; can be re-run from the Tools menu at any time.
[InitializeOnLoad]
public static class ProjectSetup
{
    public const string ScenePath = "Assets/Scenes/Main.unity";
    public const string HashDbPath = "Assets/Data/BoneNames.HashDB.bytes";
    const string MenuRoot = "Tools/Telltale Format Manipulator/";

    static ProjectSetup()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists(ScenePath) && !EditorApplication.isPlayingOrWillChangePlaymode)
                RunAll();
        };
    }

    [MenuItem(MenuRoot + "Regenerate Scene + Settings")]
    public static void RunAll()
    {
        ApplyPlayerSettings();
        GenerateScene();
        Debug.Log("[ProjectSetup] Scene and player settings regenerated.");
    }

    [MenuItem(MenuRoot + "Apply Player Settings Only")]
    public static void ApplyPlayerSettings()
    {
        PlayerSettings.productName = "Telltale Format Manipulator";

        // DX11 first, OpenGL Core as fallback. Both are available on Windows 7.
        var apis = new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore };
        SetApis(BuildTarget.StandaloneWindows64, apis);
        SetApis(BuildTarget.StandaloneWindows, apis);

        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1180;
        PlayerSettings.defaultScreenHeight = 700;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.allowFullscreenSwitch = true;
        PlayerSettings.forceSingleInstance = false;

        try { PlayerSettings.SplashScreen.show = false; } catch (Exception) { /* not allowed on some licenses/versions */ }
    }

    static void SetApis(BuildTarget target, GraphicsDeviceType[] apis)
    {
        try
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, apis);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ProjectSetup] Could not set graphics APIs for " + target + ": " + e.Message);
        }
    }

    [MenuItem(MenuRoot + "Regenerate Scene Only")]
    public static void GenerateScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f);
        cam.orthographic = true;
        cam.cullingMask = 0; // nothing 3D to render; IMGUI draws on top

        var appGo = new GameObject("App");
        var app = appGo.AddComponent<TFM.App.AppMain>();
        var db = AssetDatabase.LoadAssetAtPath<TextAsset>(HashDbPath);
        if (db == null)
            Debug.LogError("[ProjectSetup] Hash database not found at " + HashDbPath + " (must be a .bytes TextAsset).");
        app.boneHashDatabase = db;

        app.propTypeNames = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/PropsTypeNames.bytes");
        app.propKeyNames = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/PropsKeyNames.bytes");
        app.propTypeLayouts = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/PropsTypeLayouts.bytes");
        app.metaLayouts = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/MetaLayouts.bytes");
        app.logicNames = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/LogicNames.bytes");
        if (app.metaLayouts == null)
            Debug.LogWarning("[ProjectSetup] Assets/Data/MetaLayouts.bytes is missing. Chores and dialogues cannot be opened without it.");
        if (app.propTypeNames == null || app.propKeyNames == null)
            Debug.LogWarning("[ProjectSetup] Props name databases missing under Assets/Data (PropsTypeNames.bytes, PropsKeyNames.bytes). Prop type and key names will show as hashes.");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
    }
}
