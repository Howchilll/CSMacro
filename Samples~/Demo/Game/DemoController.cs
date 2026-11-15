using System.Collections;
using System.Collections.Generic;
using Demo.Macros;
using UnityEngine;
using static Demo.Macros.DemoMacros;

namespace Demo.Game
{
    /// <summary>
    /// The HowMacro demo scene. Two pieces of debug tooling that usually need hand-kept registration code:
    ///   [@Command("...")] on a static method  -> a button in the debug console (registered by generated startup code)
    ///   [@Watch] on a field or property        -> a live row in the watch overlay (@WATCH_PANEL() lists them)
    /// plus @ASSERT, which is compiled out of release builds, and @LOG_VALUE, which logs an expression with its source text.
    /// </summary>
    public sealed class DemoController : MonoBehaviour
    {
        static DemoController s_instance;
        readonly List<GameObject> _spawned = new List<GameObject>();

        [@Watch("Objects")] int ObjectCount => _spawned.Count;
        [@Watch("Cubes")] public int cubes;
        [@Watch("Spheres")] public int spheres;
        [@Watch("Last spawn (s)")] float _lastSpawn;
        [@Watch("FPS")] int Fps => Mathf.RoundToInt(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f));

        // ---- debug commands: each attribute is the whole registration ----

        [@Command("Spawn cube")]
        internal static void SpawnCube()
        {
            s_instance.Spawn(PrimitiveType.Cube);
            s_instance.cubes++;
        }

        [@Command("Spawn sphere")]
        internal static void SpawnSphere()
        {
            s_instance.Spawn(PrimitiveType.Sphere);
            s_instance.spheres++;
        }

        [@Command("Rain x20")]
        internal static void Rain()
        {
            for (var i = 0; i < 20; i++)
                if (i % 2 == 0) SpawnCube(); else SpawnSphere();
        }

        [@Command("Clear")]
        internal static void Clear()
        {
            var removed = s_instance._spawned.Count;
            @LOG_VALUE(removed);
            foreach (var go in s_instance._spawned) Destroy(go);
            s_instance._spawned.Clear();
            s_instance.cubes = s_instance.spheres = 0;
        }

        void Awake() => s_instance = this;

        void Spawn(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            go.transform.position = new Vector3(Random.Range(-4f, 4f), Random.Range(6f, 10f), Random.Range(-2f, 2f));
            go.transform.rotation = Random.rotation;
            go.GetComponent<Renderer>().material.color = Color.HSVToRGB(Random.value, 0.65f, 0.95f);
            go.AddComponent<Rigidbody>();
            _spawned.Add(go);
            _lastSpawn = Time.time;
            @ASSERT(_spawned.Count <= 300);
        }

        void OnGUI()
        {
            GUI.skin.label.fontSize = GUI.skin.button.fontSize = 16;

            GUILayout.BeginArea(new Rect(16, 16, 230, Screen.height - 32), GUI.skin.box);
            GUILayout.Label("<b>Commands</b>  [@Command]");
            foreach (var name in CommandConsole.Names)
                if (GUILayout.Button(name, GUILayout.Height(32))) CommandConsole.Run(name);
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(Screen.width - 266, 16, 250, 200), GUI.skin.box);
            GUILayout.Label("<b>Watch</b>  [@Watch]");
            foreach (var entry in @WATCH_PANEL())
                GUILayout.Label(entry.Name + ":  " + FormatValue(entry.Value()));
            GUILayout.EndArea();

            GUI.Label(new Rect(262, Screen.height - 40, Screen.width - 524, 30),
                "HowMacro in Unity: the buttons and the watch list are generated at compile time from attributes.");
        }

        static string FormatValue(object value) => value is float f ? f.ToString("0.0") : value.ToString();

        // `-howmacro-screenshot <file>`: spawn a few things, capture the screen, quit (used to check a player build)
        IEnumerator Start()
        {
            var args = System.Environment.GetCommandLineArgs();
            var at = System.Array.IndexOf(args, "-howmacro-screenshot");
            if (at < 0 || at + 1 >= args.Length) yield break;
            Application.runInBackground = true; // the window may not have focus
            Rain();
            yield return new WaitForSeconds(1.2f);
            SpawnCube();
            SpawnSphere();
            yield return new WaitForSeconds(0.6f);
            ScreenCapture.CaptureScreenshot(args[at + 1]);
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }
    }
}
