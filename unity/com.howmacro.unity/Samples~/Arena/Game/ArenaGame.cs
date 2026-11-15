using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using static Arena.Macros.ArenaMacros;

namespace Arena.Game
{
    /// <summary>
    /// The Arena scene: enemies fall onto the ground; click one to defeat it for gold, or let it touch you and lose HP.
    /// Tags and layers are written as names but checked at compile time against ProjectSettings/TagManager.asset:
    ///   @TAG("Enemy"), @LAYER("Enemy"), @LAYER_MASK("Ground")  -- a typo is a compile error, not a silent runtime miss.
    /// </summary>
    public sealed class ArenaGame : MonoBehaviour
    {
        const string SaveSlot = "arena.";
        ArenaPlayer _player;
        string _status = "";

        void Awake()
        {
            _player = gameObject.AddComponent<ArenaPlayer>();
            _player.LoadFrom(SaveSlot);
        }

        public static GameObject SpawnEnemy(Vector3 at)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Enemy";
            go.tag = @TAG("Enemy");
            go.layer = @LAYER("Enemy");
            go.transform.position = at;
            go.GetComponent<Renderer>().material.color = new Color(0.9f, 0.3f, 0.3f);
            go.AddComponent<Rigidbody>();
            return go;
        }

        public static int EnemyCount => GameObject.FindGameObjectsWithTag(@TAG("Enemy")).Length;

        /// <summary>Height of the ground under a point, found with a raycast that only sees the Ground layer.</summary>
        public static float? GroundHeight(Vector3 from) =>
            Physics.Raycast(from, Vector3.down, out var hit, 100f, @LAYER_MASK("Ground")) ? hit.point.y : (float?)null;

        void Update()
        {
            if (!Input.GetMouseButtonDown(0) || Camera.main == null) return;
            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 100f, @LAYER_MASK("Enemy")))
            {
                Destroy(hit.collider.gameObject);
                _player.Gold += 5;
                _status = "+5 gold";
            }
        }

        void FixedUpdate()
        {
            // Enemies that reach the ground near the centre hurt the player once, then disappear.
            foreach (var enemy in GameObject.FindGameObjectsWithTag(@TAG("Enemy")))
            {
                var p = enemy.transform.position;
                if (p.y < 0.6f && Mathf.Abs(p.x) < 1.5f) { _player.Hp -= 15; Destroy(enemy); _status = "-15 hp"; }
            }
        }

        void OnGUI()
        {
            GUI.skin.label.fontSize = GUI.skin.button.fontSize = 16;
            GUI.skin.label.richText = true;

            GUILayout.BeginArea(new Rect(16, 16, 260, Screen.height - 32), GUI.skin.box);
            GUILayout.Label("<b>Arena</b>");
            if (GUILayout.Button("Spawn 5 enemies", GUILayout.Height(30)))
                for (var i = 0; i < 5; i++) SpawnEnemy(new Vector3(Random.Range(-4f, 4f), Random.Range(6f, 10f), Random.Range(-1.5f, 1.5f)));
            if (GUILayout.Button("Hit me (-15 hp)", GUILayout.Height(30))) _player.Hp -= 15;
            if (GUILayout.Button("Heal (+40 hp)", GUILayout.Height(30))) _player.Hp += 40;
            if (GUILayout.Button("Volume +0.3", GUILayout.Height(30))) _player.Volume += 0.3f;
            if (GUILayout.Button("Toggle music", GUILayout.Height(30))) _player.Music = !_player.Music;
            GUILayout.Space(8);
            if (GUILayout.Button("Save", GUILayout.Height(30))) { _player.SaveTo(SaveSlot); _status = "saved: " + string.Join(", ", ArenaPlayer.SaveKeys); }
            if (GUILayout.Button("Load", GUILayout.Height(30))) { _player.LoadFrom(SaveSlot); _status = "loaded"; }
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(Screen.width - 296, 16, 280, 260), GUI.skin.box);
            GUILayout.Label("<b>Player</b>  (@CLAMPED, [@Save])");
            GUILayout.Label("HP: " + _player.Hp + " / 100");
            GUILayout.Label("Gold: " + _player.Gold);
            GUILayout.Label("Volume: " + _player.Volume.ToString("0.0") + "   Music: " + (_player.Music ? "on" : "off"));
            GUILayout.Label("Enemies: " + EnemyCount);
            GUILayout.Label("Changed: " + string.Join(", ", Last(_player.Changes, 4)));
            GUILayout.Label(_status);
            GUILayout.EndArea();

            GUI.Label(new Rect(292, Screen.height - 40, Screen.width - 600, 30),
                "Click an enemy for gold. Clamping, saving and tag/layer checks are generated at compile time.");
        }

        static IEnumerable<string> Last(List<string> list, int n) => list.GetRange(Mathf.Max(0, list.Count - n), Mathf.Min(n, list.Count));

        // `-arena-screenshot <file>`: spawn enemies, change some stats, capture the screen, quit (checks a player build)
        IEnumerator Start()
        {
            var args = System.Environment.GetCommandLineArgs();
            var at = System.Array.IndexOf(args, "-arena-screenshot");
            if (at < 0 || at + 1 >= args.Length) yield break;
            Application.runInBackground = true;
            for (var i = 0; i < 6; i++) SpawnEnemy(new Vector3(-4f + i * 1.6f, 7f + i * 0.4f, 0f));
            _player.Hp += 50; _player.Hp -= 15; _player.Gold += 25; _player.Volume += 0.5f;
            yield return new WaitForSeconds(1.0f);
            ScreenCapture.CaptureScreenshot(args[at + 1]);
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }
    }
}
