using System;
using System.Linq;
using HowMacro;

namespace Demo.Macros
{
    // Macros for the Unity spike. Each macro body returns a dummy value, so unexpanded code gives a visibly wrong answer.
    public static class DemoMacros
    {
        /// <summary><c>SQUARE(x)</c> becomes <c>((x) * (x))</c>.</summary>
        [Macro]
        public static int SQUARE(int x)
        {
            Macro.Emit($"(({Macro.Arg(0)}) * ({Macro.Arg(0)}))");
            return 0;
        }

        /// <summary><c>LOG_VALUE(expr);</c> logs the expression's source text and its value.</summary>
        [Macro(MacroSite.Statement)]
        public static void LOG_VALUE(object value)
        {
            Macro.Emit($"global::UnityEngine.Debug.Log({Macro.Quote(Macro.Arg(0) + " = ")} + ({Macro.Arg(0)}));");
        }

        /// <summary><c>WHERE()</c>: "File.cs:line" of the call site.</summary>
        [Macro]
        public static string WHERE()
        {
            Macro.Emit(Macro.Quote(Macro.FileName + ":" + Macro.Line));
            return "";
        }

        /// <summary><c>COMMANDS()</c>: names of every [@Command] method in the assembly.</summary>
        [Macro]
        public static string[] COMMANDS()
        {
            var names = Macro.Fragments("commands").Select(f => Macro.Quote(f.Code)).OrderBy(n => n);
            Macro.Emit("new string[] { " + string.Join(", ", names) + " }");
            return new string[0];
        }

        /// <summary>
        /// <c>@ASSERT(condition);</c> checks the condition in the editor and in development builds, and disappears completely
        /// from release builds. The message carries the condition's source text and where it is.
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void ASSERT(bool condition)
        {
            if (!Macro.IsDefined("UNITY_EDITOR") && !Macro.IsDefined("DEVELOPMENT_BUILD")) return; // emits nothing
            Macro.Emit("if (!(" + Macro.Arg(0) + ")) global::UnityEngine.Debug.LogError(" +
                       Macro.Quote("assertion failed at " + Macro.FileName + ":" + Macro.Line + ": " + Macro.Arg(0)) + ");");
        }

        /// <summary>
        /// <c>@WATCH_PANEL()</c> inside a type: (name, current value) for every <c>[@Watch]</c> member of that type,
        /// e.g. for a debug overlay. Adding a watched value is one attribute, nothing else to keep in sync.
        /// </summary>
        [Macro]
        public static (string Name, Func<object> Value)[] WATCH_PANEL()
        {
            var entries = Macro.Fragments("watch")
                .Where(f => f.ContainingType == Macro.ContainingType)
                .Select(f => "(" + Macro.Quote(f.Code) + ", (global::System.Func<object>)(() => this." + f.MemberName + "))");
            Macro.Emit("new (string Name, global::System.Func<object> Value)[] { " + string.Join(", ", entries) + " }");
            return new (string, Func<object>)[0];
        }
    }

    /// <summary><c>[@Watch]</c> / <c>[@Watch("label")]</c> on a field or property: shown by <c>@WATCH_PANEL()</c>.</summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Watch : Attribute
    {
        public Watch(string label = null)
        {
            var member = Macro.Declaration;
            if (member.IsStatic) { Macro.Error("[@Watch] on " + member.Name + ": static members aren't supported, @WATCH_PANEL() reads this." + member.Name); return; }
            Macro.Fragment("watch", Macro.ArgCount > 0 ? (string)Macro.ArgValue(0) : member.Name);
        }
    }

    /// <summary><c>[@Command("name")]</c> on a static parameterless method: registered with the console at startup.</summary>
    [AttributeMacro(MacroTarget.Method)]
    public sealed class Command : Attribute
    {
        public Command(string name)
        {
            name = (string)Macro.ArgValue(0); // macro constructors read their arguments from the call site
            var method = Macro.Declaration;
            if (method.Parameters.Count != 0) { Macro.Error("[@Command] method " + method.Name + " must have no parameters"); return; }
            if (!method.IsStatic) { Macro.Error("[@Command] method " + method.Name + " must be static: commands are registered before any object exists"); return; }
            if (string.Equals(method.Accessibility, "private", StringComparison.OrdinalIgnoreCase))
            { Macro.Error("[@Command] method " + method.Name + " is private; make it internal or public so the generated registration can call it"); return; }
            Macro.Fragment("commands", name);
            Macro.Startup("global::Demo.Game.CommandConsole.Register(" + Macro.Quote(name) + ", global::" + Macro.ContainingType + "." + method.Name + ");");
        }
    }
}
