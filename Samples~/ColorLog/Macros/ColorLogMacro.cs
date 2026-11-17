using HowMacro;
using UnityEngine;

namespace ColorLog.Macros
{
    public static class ColorLogMacro
    {
        /// <summary>
        /// <c>public int value1 = @COLORLOG(0, Color.cyan);</c>
        /// Turns value1 into a property; every change is logged in the given color by ColorLog.Tools.ColorfulLogger,
        /// e.g. <c>[ColorLogDemo.value1] 3 -> 4</c>. The first argument is the initial value (it also tells C# the type).
        /// The value lives in a private field the macro adds for itself, <c>@_temp_value1</c>; it is serialized like the
        /// original field, so the Inspector and saved scenes keep working. The calling assembly must reference ColorLog.Tools.
        /// </summary>
        [Macro(MacroSite.Member)]
        public static T COLORLOG<T>(T initial, Color color)
        {
            var store = "@_temp_" + Macro.Name;
            var isStatic = Macro.Modifiers.Contains("static");
            var serialized = !isStatic && (Macro.Modifiers.Contains("public") || Macro.Attributes.Contains("SerializeField"));
            var label = Macro.Quote("[" + Macro.ContainingTypeInfo?.Name + "." + Macro.Name + "] ");

            // the storage: private, serialized under the original name so existing scene data still loads
            if (serialized)
                Macro.Emit("[global::UnityEngine.SerializeField, global::UnityEngine.Serialization.FormerlySerializedAs(" + Macro.Quote(Macro.Name) + ")] ");
            Macro.Emit("private " + (isStatic ? "static " : "") + Macro.Type + " " + store + " = " + Macro.Arg(0) + "; ");

            // the property, under the declared name and type
            Macro.Emit(Macro.Modifiers + " " + Macro.Type + " " + Macro.Name + " { get => " + store + "; set { " +
                       "if (global::System.Collections.Generic.EqualityComparer<" + Macro.Type + ">.Default.Equals(" + store + ", value)) return; " +
                       "var __old = " + store + "; " + store + " = value; " +
                       "global::ColorLog.Tools.ColorfulLogger.Log(" + label + " + __old + \" -> \" + value, " + Macro.Arg(1) + "); } }");
            return default;
        }
    }
}
