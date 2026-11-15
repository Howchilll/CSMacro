using System.Collections.Generic;
using Arena.Macros;
using UnityEngine;
using static Arena.Macros.ArenaMacros;

namespace Arena.Game
{
    /// <summary>
    /// Player stats for the Arena sample.
    ///   @CLAMPED(nameof(field), min, max)  -> a clamped property over a serialized field, with a change callback
    ///   [@Save("key")] + @SAVE_ALL / @LOAD_ALL -> PlayerPrefs code for every saved member, type-checked at compile time
    /// </summary>
    public sealed class ArenaPlayer : MonoBehaviour
    {
        // Serialized state: edited in the Inspector, saved with the scene.
        [SerializeField] int hp = 100;
        [SerializeField] int gold = 0;
        [SerializeField] float volume = 0.8f;

        // The public API: each line is a validated property (the IDE sees a field of the same name and type).
        [@Save("hp")] public int Hp = @CLAMPED(nameof(hp), 0, 100);
        [@Save("gold")] public int Gold = @CLAMPED(nameof(gold), 0, 9999);
        [@Save("volume")] public float Volume = @CLAMPED(nameof(volume), 0f, 1f);
        [@Save("name")] public string PlayerName = "Hero";
        [@Save("music")] public bool Music = true;

        public readonly List<string> Changes = new List<string>();

        // Called by the generated setters after a real change.
        void OnStatChanged(string stat) => Changes.Add(stat);

        public void SaveTo(string prefix) { @SAVE_ALL(prefix); }
        public void LoadFrom(string prefix) { @LOAD_ALL(prefix); }
        public static string[] SaveKeys => @SAVE_KEYS();
    }
}
