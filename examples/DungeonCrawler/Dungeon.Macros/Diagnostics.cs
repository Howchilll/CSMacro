using System.Linq;
using CSMacro;

namespace Dungeon.Macros
{
    public static class Diagnostics
    {
        /// <summary>
        /// <c>@ASSERT(rooms.Count > 0, "map needs a room");</c>
        /// 只在定义了 DEBUG 的构建里生成检查代码；Release 构建里展开成空语句，连条件表达式都不会求值。
        /// 失败消息自带原始表达式和源码位置。
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void ASSERT(bool condition, string message = "")
        {
            if (!Macro.IsDefined("DEBUG")) return; // Release：什么都不生成
            var detail = Macro.ArgCount > 1 ? " + \" — \" + " + Macro.Arg(1) : "";
            Macro.Emit($"if (!({Macro.Arg(0)})) throw new global::System.InvalidOperationException(" +
                       $"{Macro.Quote($"assertion failed at {Macro.FileName}:{Macro.Line}: {Macro.Arg(0)}")}{detail});");
        }

        /// <summary>
        /// <c>@DUMP(obj)</c>：用语义信息遍历 obj 所有公开可读的实例字段和属性，编译期生成拼接代码。没有反射。
        /// </summary>
        [Macro]
        public static string DUMP<T>(T obj)
        {
            var type = Macro.ArgTypeInfo(0);
            if (type == null) { Macro.Error($"can't determine the type of `{Macro.Arg(0)}`"); return ""; }
            var members = type.Members.Where(m => m.IsFieldOrProperty && m.IsPublic && !m.IsStatic && m.CanRead).ToList();
            var parts = members.Select((m, i) => $"{Macro.Quote((i == 0 ? "" : ", ") + m.Name + "=")} + {Macro.Arg(0)}.{m.Name}");
            Macro.Emit(members.Count == 0 ? "\"\"" : string.Join(" + ", parts));
            return "";
        }

        /// <summary>
        /// 片段宏 <c>#region @TRACE_AI</c>：构建时定义了 TRACE_AI（<c>dotnet build -p:TraceAI=true</c>）才生效，
        /// 把片段里的可写字段变成"改变时写一条跟踪日志"的属性；否则什么都不改，零开销。
        /// 排查 AI 问题时不用到处加日志，也不用担心忘了删。
        /// </summary>
        [RegionMacro]
        public static void TRACE_AI()
        {
            if (!Macro.IsDefined("TRACE_AI")) return;
            foreach (var m in Macro.RegionMembers)
            {
                if (!m.IsField || !m.CanReplace || m.Declaration?.CanWrite != true) continue;
                var init = m.Initializer != null ? $" = {m.Initializer};" : "";
                m.Replace($"{m.Attributes} {m.Modifiers} {m.Type} {m.Name} {{ get; set {{ " +
                          $"if (!global::System.Collections.Generic.EqualityComparer<{m.Type}>.Default.Equals(field, value)) " +
                          $"global::Dungeon.TraceLog.Write(this, {Macro.Quote(m.Name)}, field, value); field = value; }} }}{init}");
            }
        }
    }
}
