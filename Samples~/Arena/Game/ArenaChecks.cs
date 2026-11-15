using System;
using System.Collections.Generic;
using UnityEngine;
using static Arena.Macros.ArenaMacros;

namespace Arena.Game
{
    /// <summary>
    /// Headless checks for the Arena sample: each line shows the value and what the expanded code should give.
    /// Unexpanded code (no HowMacro) gives visibly different values. Runs in the editor via -executeMethod and in a
    /// player started with -arena-checks.
    /// </summary>
    public static class ArenaChecks
    {
        public static string Run()
        {
            var lines = new List<string>();
            var failures = 0;
            void Check(string what, object actual, object expected)
            {
                var ok = Equals(actual, expected);
                if (!ok) failures++;
                lines.Add((ok ? "  ok   " : "  FAIL ") + what + " = " + actual + (ok ? "" : "   (expected " + expected + ")"));
            }

            var go = new GameObject("ArenaChecks");
            var player = go.AddComponent<ArenaPlayer>();

            // @CLAMPED: clamping, backing field, change callback
            Check("Hp initially (from serialized hp)", player.Hp, 100);
            player.Hp = 150;
            Check("Hp after = 150", player.Hp, 100);
            player.Hp = -5;
            Check("Hp after = -5", player.Hp, 0);
            player.Volume = 3f;
            Check("Volume after = 3", player.Volume, 1f);
            player.Gold += 42;
            Check("Gold after += 42", player.Gold, 42);
            Check("changes reported", string.Join(",", player.Changes), "Hp,Volume,Gold");
            player.Changes.Clear();
            player.Hp = 0; // same value: no change callback
            Check("no callback when unchanged", player.Changes.Count, 0);

            // [@Save] + @SAVE_ALL / @LOAD_ALL round trip through PlayerPrefs
            const string slot = "arena.checks.";
            player.Hp = 77; player.Gold = 1234; player.Volume = 0.25f; player.PlayerName = "Ada"; player.Music = false;
            player.SaveTo(slot);
            player.Hp = 1; player.Gold = 2; player.Volume = 0.5f; player.PlayerName = "x"; player.Music = true;
            player.LoadFrom(slot);
            Check("loaded Hp/Gold/Volume/Name/Music", player.Hp + "/" + player.Gold + "/" + player.Volume + "/" + player.PlayerName + "/" + player.Music,
                  "77/1234/0.25/Ada/False");
            Check("save keys", string.Join(",", ArenaPlayer.SaveKeys), "hp,gold,volume,name,music");
            foreach (var key in ArenaPlayer.SaveKeys) PlayerPrefs.DeleteKey(slot + key);

            // @LAYER / @LAYER_MASK / @TAG, checked against TagManager.asset at compile time
            Check("@LAYER(\"Ground\")", @LAYER("Ground"), LayerMask.NameToLayer("Ground"));
            Check("@LAYER(\"Enemy\")", @LAYER("Enemy"), 7);
            Check("@LAYER_MASK(\"Ground\", \"Enemy\")", @LAYER_MASK("Ground", "Enemy"), LayerMask.GetMask("Ground", "Enemy"));
            Check("@TAG(\"Enemy\")", @TAG("Enemy"), "Enemy");

            // The layer mask in a real raycast: the ground is found, an enemy in between is ignored
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.layer = @LAYER("Ground");
            var enemy = ArenaGame.SpawnEnemy(new Vector3(0, 3, 0));
            UnityEngine.Object.DestroyImmediate(enemy.GetComponent<Rigidbody>());
            Physics.SyncTransforms();
            Check("ground height under an enemy", ArenaGame.GroundHeight(new Vector3(0, 10, 0)), (float?)0f);
            Check("enemies by tag", ArenaGame.EnemyCount, 1);
            UnityEngine.Object.DestroyImmediate(enemy);
            UnityEngine.Object.DestroyImmediate(ground);
            UnityEngine.Object.DestroyImmediate(go);

            lines.Insert(0, failures == 0 ? "ALL PASSED" : failures + " FAILED");
            return string.Join("\n", lines);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InPlayer()
        {
            if (Application.isEditor || Array.IndexOf(Environment.GetCommandLineArgs(), "-arena-checks") < 0) return;
            Debug.Log("[arena] player\n" + Run());
            Application.Quit();
        }
    }
}
