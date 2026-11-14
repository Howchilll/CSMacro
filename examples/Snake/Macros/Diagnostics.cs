using HowMacro;

namespace Snake.Macros;

public static class Diagnostics
{
    /// <summary>
    /// <c>@ASSERT(condition, "message");</c>
    /// Generates the check only when DEBUG is defined; in Release it expands to an empty statement and the condition is not even evaluated.
    /// The failure message includes the original expression and source location.
    /// </summary>
    [Macro(MacroSite.Statement)]
    public static void ASSERT(bool condition, string message = "")
    {
        if (!Macro.IsDefined("DEBUG")) return;
        var detail = Macro.ArgCount > 1 ? " + \" — \" + " + Macro.Arg(1) : "";
        Macro.Emit($"if (!({Macro.Arg(0)})) throw new global::System.InvalidOperationException(" +
                   $"{Macro.Quote($"assertion failed at {Macro.FileName}:{Macro.Line}: {Macro.Arg(0)}")}{detail});");
    }

    /// <summary>
    /// Region macro <c>#region @TRACE_STATE</c>: active only when TRACE_STATE is defined (<c>dotnet build -p:TraceState=true</c>);
    /// it turns the fields in the region into properties that log every change. Otherwise it changes nothing: zero cost.
    /// </summary>
    [RegionMacro]
    public static void TRACE_STATE()
    {
        if (!Macro.IsDefined("TRACE_STATE")) return;
        foreach (var m in Macro.RegionMembers)
        {
            if (!m.IsField || !m.CanReplace || m.Declaration?.CanWrite != true) continue;
            var init = m.Initializer != null ? $" = {m.Initializer};" : "";
            m.Replace($"{m.Attributes} {m.Modifiers} {m.Type} {m.Name} {{ get; set {{ " +
                      $"if (!global::System.Collections.Generic.EqualityComparer<{m.Type}>.Default.Equals(field, value)) " +
                      $"global::System.Console.WriteLine({Macro.Quote($"[trace] {m.Name}: ")} + field + \" -> \" + value); field = value; }} }}{init}");
        }
    }
}
