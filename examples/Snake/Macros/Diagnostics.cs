using CSMacro;

namespace Snake.Macros;

public static class Diagnostics
{
    /// <summary>
    /// <c>@ASSERT(condition, "message");</c>
    /// 只在定义了 DEBUG 的构建里生成检查代码；Release 里展开成空语句，连条件都不求值。
    /// 失败消息自带原始表达式和源码位置。
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
    /// 片段宏 <c>#region @TRACE_STATE</c>：构建时定义了 TRACE_STATE（<c>dotnet build -p:TraceState=true</c>）才生效，
    /// 把片段里的字段变成"改变时写一条跟踪日志"的属性；否则什么都不改，零开销。
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
