using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HowMacro;

namespace Arena.Macros
{
    /// <summary>
    /// Macros for the Arena sample. Each body returns a dummy value, so unexpanded code gives a visibly wrong answer.
    /// Generated code must be C# 9 (Unity's language version): no `field` keyword, so a stateful property is backed by a
    /// field the user declares, e.g. <c>[SerializeField] int hp = 100; public int Hp = @CLAMPED(nameof(hp), 0, 100);</c>
    /// </summary>
    public static class ArenaMacros
    {
        // ---- @CLAMPED: a validated property over a serialized field ----

        /// <summary>
        /// <c>public int Hp = @CLAMPED(nameof(hp), 0, 100);</c> turns Hp into a property that reads and writes the field
        /// <c>hp</c> and clamps every assignment to [min, max]. If the containing type declares
        /// <c>void OnStatChanged(string name)</c>, it is called after each real change.
        /// The backing field is named with <c>nameof</c>, so renaming it is safe and a typo is a compile error.
        /// </summary>
        [Macro(MacroSite.Member)]
        public static T CLAMPED<T>(string backingField, T min, T max)
        {
            var type = Macro.ContainingTypeInfo;
            var fieldName = Macro.ArgValue(0) as string;
            var field = fieldName == null ? null : type?.Member(fieldName);
            if (field == null || field.Kind != MacroMemberKind.Field)
            {
                Macro.Error("@CLAMPED on " + Macro.Name + ": " + Macro.Arg(0) + " must name a field of " + type?.Name + ", e.g. @CLAMPED(nameof(hp), 0, 100)");
                return default(T);
            }
            if (field.Type != Macro.Type)
            {
                Macro.Error("@CLAMPED on " + Macro.Name + ": the backing field " + field.Name + " is " + field.Type + " but " + Macro.Name + " is " + Macro.Type);
                return default(T);
            }
            if (!new[] { "int", "long", "short", "byte", "float", "double" }.Contains(Macro.Type))
            {
                Macro.Error("@CLAMPED needs a numeric member, but " + Macro.Name + " is " + Macro.Type);
                return default(T);
            }
            if (Macro.Args[1].HasConstant && Macro.Args[2].HasConstant &&
                Convert.ToDouble(Macro.ArgValue(1)) > Convert.ToDouble(Macro.ArgValue(2)))
                Macro.Error("@CLAMPED on " + Macro.Name + ": min " + Macro.Arg(1) + " is greater than max " + Macro.Arg(2));

            var notify = type.Methods.Any(m => m.Name == "OnStatChanged" && m.Parameters.Count == 1)
                ? "OnStatChanged(" + Macro.Quote(Macro.Name) + ");" : "";
            string f = field.IsStatic ? field.Name : "this." + field.Name, lo = "(" + Macro.Arg(1) + ")", hi = "(" + Macro.Arg(2) + ")";
            Macro.Emit(Macro.Attributes + " " + Macro.Modifiers + " " + Macro.Type + " " + Macro.Name + " { get => " + f + "; set { " +
                       "var __v = value < " + lo + " ? " + lo + " : value > " + hi + " ? " + hi + " : value; " +
                       "if (" + f + " == __v) return; " + f + " = __v; " + notify + " } }");
            return default(T);
        }

        // ---- [@Save] + @SAVE_ALL / @LOAD_ALL: PlayerPrefs code generated per field ----

        /// <summary><c>@SAVE_ALL("prefix.");</c> writes every [@Save] member of the containing type to PlayerPrefs, then saves.</summary>
        [Macro(MacroSite.Statement)]
        public static void SAVE_ALL(string prefix)
        {
            foreach (var s in Saved())
            {
                var key = Macro.Arg(0) + " + " + Macro.Quote(s.Key);
                switch (s.Member.Type)
                {
                    case "int": Macro.Emit("global::UnityEngine.PlayerPrefs.SetInt(" + key + ", " + s.Access + ");"); break;
                    case "float": Macro.Emit("global::UnityEngine.PlayerPrefs.SetFloat(" + key + ", " + s.Access + ");"); break;
                    case "bool": Macro.Emit("global::UnityEngine.PlayerPrefs.SetInt(" + key + ", " + s.Access + " ? 1 : 0);"); break;
                    default: Macro.Emit("global::UnityEngine.PlayerPrefs.SetString(" + key + ", " + s.Access + " ?? \"\");"); break;
                }
            }
            Macro.Emit("global::UnityEngine.PlayerPrefs.Save();");
        }

        /// <summary><c>@LOAD_ALL("prefix.");</c> reads every [@Save] member of the containing type back; missing keys keep their current value.</summary>
        [Macro(MacroSite.Statement)]
        public static void LOAD_ALL(string prefix)
        {
            foreach (var s in Saved())
            {
                var key = Macro.Arg(0) + " + " + Macro.Quote(s.Key);
                string read;
                switch (s.Member.Type)
                {
                    case "int": read = "global::UnityEngine.PlayerPrefs.GetInt(" + key + ")"; break;
                    case "float": read = "global::UnityEngine.PlayerPrefs.GetFloat(" + key + ")"; break;
                    case "bool": read = "global::UnityEngine.PlayerPrefs.GetInt(" + key + ") != 0"; break;
                    default: read = "global::UnityEngine.PlayerPrefs.GetString(" + key + ")"; break;
                }
                Macro.Emit("if (global::UnityEngine.PlayerPrefs.HasKey(" + key + ")) " + s.Access + " = " + read + ";");
            }
        }

        /// <summary><c>@SAVE_KEYS()</c>: the [@Save] keys of the containing type, in source order.</summary>
        [Macro]
        public static string[] SAVE_KEYS()
        {
            Macro.Emit("new string[] { " + string.Join(", ", Saved().Select(s => Macro.Quote(s.Key))) + " }");
            return new string[0];
        }

        static List<(string Key, MacroMember Member, string Access)> Saved()
        {
            var saved = Macro.Fragments("save")
                .Where(f => f.ContainingType == Macro.ContainingType && f.Declaration != null)
                .Select(f => (Key: f.Code, Member: f.Declaration, Access: (f.Declaration.IsStatic ? "global::" + f.ContainingType : "this") + "." + f.Declaration.Name))
                .ToList();
            foreach (var dup in saved.GroupBy(s => s.Key).Where(g => g.Count() > 1))
                Macro.Error("save key \"" + dup.Key + "\" is used by more than one member: " + string.Join(" and ", dup.Select(d => d.Member.Name)));
            return saved;
        }

        // ---- @LAYER / @LAYER_MASK / @TAG: checked against ProjectSettings/TagManager.asset at compile time ----

        /// <summary><c>@LAYER("Enemy")</c> becomes the layer's index; an unknown name is a compile error.</summary>
        [Macro]
        public static int LAYER(string name)
        {
            var index = LayerIndex((string)Macro.ArgValue(0));
            if (index >= 0) Macro.Emit(index.ToString());
            return -1;
        }

        /// <summary><c>@LAYER_MASK("Ground", "Enemy")</c> becomes the mask as an int constant; an unknown name is a compile error.</summary>
        [Macro]
        public static int LAYER_MASK(params string[] names)
        {
            var mask = 0;
            for (var i = 0; i < Macro.ArgCount; i++)
            {
                var index = LayerIndex((string)Macro.ArgValue(i));
                if (index < 0) return 0;
                mask |= 1 << index;
            }
            Macro.Emit(mask.ToString());
            return 0;
        }

        /// <summary><c>@TAG("Enemy")</c> becomes the string, after checking that the tag exists.</summary>
        [Macro]
        public static string TAG(string name)
        {
            var tag = (string)Macro.ArgValue(0);
            var builtIn = new[] { "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController" };
            var tags = builtIn.Concat(TagManager().Tags).ToList();
            if (tags.Contains(tag)) Macro.Emit(Macro.Quote(tag));
            else Macro.Error("tag \"" + tag + "\" is not defined in ProjectSettings/TagManager.asset; defined: " + string.Join(", ", tags) + Fix);
            return "";
        }

        const string Fix = ". Add it in Project Settings > Tags and Layers, then use HowMacro > Recompile All Scripts " +
                           "(Unity doesn't recompile scripts when only tags or layers change)";

        static int LayerIndex(string name)
        {
            var layers = TagManager().Layers;
            var index = Array.IndexOf(layers, name);
            if (index < 0 || name.Length == 0)
                Macro.Error("layer \"" + name + "\" is not defined in ProjectSettings/TagManager.asset; defined: " +
                            string.Join(", ", layers.Where(l => l.Length > 0)) + Fix);
            return string.IsNullOrEmpty(name) ? -1 : index;
        }

        /// <summary>Reads tags and layers from the project's TagManager.asset (a small YAML file).</summary>
        static (List<string> Tags, string[] Layers) TagManager()
        {
            var dir = Path.GetDirectoryName(Macro.File);
            while (dir != null && !File.Exists(Path.Combine(dir, "ProjectSettings", "TagManager.asset"))) dir = Path.GetDirectoryName(dir);
            var path = Path.Combine(dir ?? Directory.GetCurrentDirectory(), "ProjectSettings", "TagManager.asset");
            if (!File.Exists(path))
            {
                Macro.Error("couldn't find ProjectSettings/TagManager.asset above " + Macro.File);
                return (new List<string>(), new string[0]);
            }

            var tags = new List<string>();
            var layers = new List<string>();
            List<string> current = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimEnd();
                if (line == "  tags:") { current = tags; continue; }
                if (line == "  layers:") { current = layers; continue; }
                if (current != null && line.StartsWith("  -")) current.Add(line.Substring(3).Trim().Trim('\'', '"'));
                else current = null; // the next key ("  m_SortingLayers:", "  tags: []", ...) ends the list
            }
            return (tags, layers.ToArray());
        }
    }

    /// <summary>
    /// <c>[@Save("key")]</c> on a field or property: <c>@SAVE_ALL</c> / <c>@LOAD_ALL</c> in the same type read and write it.
    /// Supported types: int, float, bool, string.
    /// </summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Save : Attribute
    {
        public Save(string key)
        {
            key = (string)Macro.ArgValue(0);
            var member = Macro.Declaration;
            if (!new[] { "int", "float", "bool", "string" }.Contains(member.Type))
                Macro.Error("[@Save] " + member.Name + " is " + member.Type + "; PlayerPrefs can store int, float, bool or string");
            else if (!member.CanWrite)
                Macro.Error("[@Save] " + member.Name + " is read-only, so @LOAD_ALL couldn't load it back");
            else if (string.IsNullOrEmpty(key))
                Macro.Error("[@Save] " + member.Name + " needs a non-empty key");
            else
                Macro.Fragment("save", key);
        }
    }
}
