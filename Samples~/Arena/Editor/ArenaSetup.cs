using System.Linq;
using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Arena sample: menu HowMacro / Create Arena Scene, plus headless entry points:
///   -executeMethod ArenaSetup.RunChecks                      editor checks, written to the log
///   -executeMethod ArenaSetup.BuildWindows -backend mono|il2cpp [-release]   Windows x64 player in Build/arena-...
/// </summary>
public static class ArenaSetup
{
    /// <summary>The scene is saved next to the sample (an imported sample lives under Assets/Samples/...).</summary>
    public static string ScenePath
    {
        get
        {
            var script = AssetDatabase.FindAssets("ArenaSetup t:MonoScript").Select(AssetDatabase.GUIDToAssetPath)
                .First(p => System.IO.Path.GetFileName(p) == "ArenaSetup.cs");
            return System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(script)).Replace('\\', '/') + "/Arena.unity";
        }
    }

    [MenuItem("HowMacro/Create Arena Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
        camera.tag = "MainCamera";
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.1f, 0.12f, 0.15f);
        camera.transform.position = new Vector3(0, 6, -12);
        camera.transform.LookAt(new Vector3(0, 1.5f, 0));

        var light = new GameObject("Directional Light", typeof(Light)).GetComponent<Light>();
        light.type = LightType.Directional;
        light.shadows = LightShadows.Soft;
        light.transform.rotation = Quaternion.Euler(50, -30, 0);
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.layer = LayerMask.NameToLayer("Ground");
        ground.transform.localScale = new Vector3(1.2f, 1, 0.6f);

        new GameObject("Arena", typeof(Arena.Game.ArenaGame));

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[arena] created " + ScenePath);
    }

    public static void RunChecks()
    {
        var result = Arena.Game.ArenaChecks.Run();
        Debug.Log("[arena] editor\n" + result);
        EditorApplication.Exit(result.StartsWith("ALL PASSED") ? 0 : 1);
    }

    public static void BuildWindows()
    {
        var args = Environment.GetCommandLineArgs();
        var il2cpp = Array.IndexOf(args, "il2cpp") >= 0;
        var dev = Array.IndexOf(args, "-release") < 0;
        if (!System.IO.File.Exists(ScenePath)) CreateScene();

        var target = NamedBuildTarget.Standalone;
        PlayerSettings.SetScriptingBackend(target, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;

        var name = "arena-" + (il2cpp ? "il2cpp" : "mono") + (dev ? "-dev" : "-release");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            target = BuildTarget.StandaloneWindows64,
            locationPathName = "Build/" + name + "/Arena.exe",
            options = dev ? BuildOptions.Development : BuildOptions.None,
        });
        Debug.Log("[arena] " + name + " build: " + report.summary.result);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
