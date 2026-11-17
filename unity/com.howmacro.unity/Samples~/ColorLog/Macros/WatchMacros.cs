using System;
using System.Linq;
using HowMacro;

namespace ColorLog.Macros
{
    /// <summary><c>[@Watch] int hp;</c> or <c>[@Watch("HP")]</c>: shown live by <c>@WATCH_GUI();</c> in the same class's OnGUI.</summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Watch : Attribute
    {
        public Watch(string label = null) =>
            Macro.Fragment(WatchMacros.Channel, Macro.ArgCount > 0 ? (string)Macro.ArgValue(0) : Macro.Name);
    }

    public static class WatchMacros
    {
        internal const string Channel = "watch";

        /// <summary>
        /// <c>void OnGUI() { @WATCH_GUI(); }</c> draws a box in the top-left corner with one line per [@Watch] member of this
        /// class. Compiled out of release builds (only the editor and development builds draw it).
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void WATCH_GUI()
        {
            if (!Macro.IsDefined("UNITY_EDITOR") && !Macro.IsDefined("DEVELOPMENT_BUILD")) return; // nothing in release builds
            var mine = Macro.Fragments(Channel).Where(f => f.ContainingType == Macro.ContainingType).ToArray();
            var title = Macro.Quote(Macro.ContainingType.Split('.').Last());
            Macro.Emit("global::UnityEngine.GUILayout.BeginArea(new global::UnityEngine.Rect(10, 10, 240, " + (34 + 22 * mine.Length) + "), " + title + ", global::UnityEngine.GUI.skin.window);");
            foreach (var f in mine)
            {
                var owner = f.Declaration?.IsStatic == true ? "global::" + f.ContainingType : "this";
                var value = f.MemberType == "float" ? owner + "." + f.MemberName + ".ToString(\"0.00\")" : "" + owner + "." + f.MemberName;
                Macro.Emit("global::UnityEngine.GUILayout.Label(" + Macro.Quote(f.Code + ": ") + " + " + value + ");");
            }
            Macro.Emit("global::UnityEngine.GUILayout.EndArea();");
        }
    }
}
