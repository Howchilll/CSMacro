using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Builds the HowMacro demo scene from code (menu: HowMacro / Create Demo Scene, or -executeMethod DemoSetup.CreateScene).</summary>
public static class DemoSetup
{
    public const string ScenePath = "Assets/Demo/HowMacroDemo.unity";

    [MenuItem("HowMacro/Create Demo Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
        camera.tag = "MainCamera";
        camera.orthographic = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
        camera.transform.position = new Vector3(0, 6, -13);
        camera.transform.LookAt(new Vector3(0, 1.5f, 0));

        var light = new GameObject("Directional Light", typeof(Light)).GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.shadows = LightShadows.Soft;
        light.transform.rotation = Quaternion.Euler(50, -30, 0);
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(1.4f, 1, 0.8f);

        new GameObject("HowMacro Demo", typeof(Demo.Game.DemoController));

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[demo] created " + ScenePath);
    }
}
