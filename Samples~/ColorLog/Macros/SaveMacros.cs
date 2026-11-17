using System;
using System.Linq;
using HowMacro;

namespace ColorLog.Macros
{
    /// <summary>
    /// <c>[@Save] public int value1;</c> or <c>[@Save("my.key")]</c>: saved by <c>@SAVE_ALL();</c> and loaded by
    /// <c>@LOAD_ALL();</c> in the same class, through PlayerPrefs. The key defaults to "Type.member".
    /// Supported types: int, float, string, bool (anything else is a compile error), and a key used twice anywhere in the
    /// assembly is a compile error too.
    /// </summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Save : Attribute
    {
        public Save(string key = null)
        {
            if (Array.IndexOf(SaveMacros.Supported, Macro.Type) < 0)
            {
                Macro.Error(Macro.Name + ": " + Macro.Type + " can't be saved; use int, float, string or bool");
                return;
            }
            var type = Macro.ContainingType.Split('.').Last();
            Macro.Fragment(SaveMacros.Channel, Macro.ArgCount > 0 ? (string)Macro.ArgValue(0) : type + "." + Macro.Name);
        }
    }

    public static class SaveMacros
    {
        internal const string Channel = "save";
        internal static readonly string[] Supported = { "int", "float", "string", "bool" };

        /// <summary><c>@SAVE_ALL();</c> writes every [@Save] member of this class to PlayerPrefs, then saves.</summary>
        [Macro(MacroSite.Statement)]
        public static void SAVE_ALL()
        {
            foreach (var f in Mine())
            {
                var value = Access(f);
                var call = f.MemberType == "int" ? "SetInt(" + Macro.Quote(f.Code) + ", " + value + ")"
                    : f.MemberType == "float" ? "SetFloat(" + Macro.Quote(f.Code) + ", " + value + ")"
                    : f.MemberType == "string" ? "SetString(" + Macro.Quote(f.Code) + ", " + value + ")"
                    : "SetInt(" + Macro.Quote(f.Code) + ", " + value + " ? 1 : 0)";
                Macro.Emit("global::UnityEngine.PlayerPrefs." + call + ";");
            }
            Macro.Emit("global::UnityEngine.PlayerPrefs.Save();");
        }

        /// <summary><c>@LOAD_ALL();</c> reads every [@Save] member of this class back; members with nothing saved keep their value.</summary>
        [Macro(MacroSite.Statement)]
        public static void LOAD_ALL()
        {
            foreach (var f in Mine())
            {
                var key = Macro.Quote(f.Code);
                var read = f.MemberType == "int" ? "GetInt(" + key + ")"
                    : f.MemberType == "float" ? "GetFloat(" + key + ")"
                    : f.MemberType == "string" ? "GetString(" + key + ")"
                    : "GetInt(" + key + ") != 0";
                Macro.Emit("if (global::UnityEngine.PlayerPrefs.HasKey(" + key + ")) " + Access(f) + " = global::UnityEngine.PlayerPrefs." + read + ";");
            }
        }

        static MacroFragment[] Mine()
        {
            var all = Macro.Fragments(Channel);
            foreach (var dup in all.GroupBy(f => f.Code).Where(g => g.Count() > 1))
                Macro.Error("save key \"" + dup.Key + "\" is used by " + string.Join(" and ", dup.Select(f => f.ContainingType + "." + f.MemberName)) + "; keys must be unique");
            var mine = all.Where(f => f.ContainingType == Macro.ContainingType).ToArray();
            if (mine.Length == 0) Macro.Warning("@" + Macro.Self + ": " + Macro.ContainingType + " has no [@Save] members");
            return mine;
        }

        static string Access(MacroFragment f) => (f.Declaration?.IsStatic == true ? "global::" + f.ContainingType : "this") + "." + f.MemberName;
    }
}
