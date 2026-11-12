using System;
using CSMacro;

namespace Demo.Macros
{
    /// <summary>
    /// 特性宏 <c>[@Save]</c> / <c>[@Save("key")]</c>：把字段登记到存档容器。
    /// 产出的是启动代码，引擎汇总成辅助脚本，程序启动时自动执行。存档容器里一行手写的登记代码都不需要。
    /// </summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Save : Attribute
    {
        public Save(string? key = null)
        {
            var k = Macro.ArgCount > 0 ? (string)Macro.ArgValue(0)! : Macro.Name;
            var owner = "global::" + Macro.ContainingType;
            Macro.Startup(
                $"global::Demo.App.SaveContainer.Register<{owner}>({Macro.Quote(k)}, " +
                $"o => o.{Macro.Name}, (o, v) => o.{Macro.Name} = ({Macro.Type})v!);");
        }
    }

    /// <summary>
    /// 特性宏 <c>[@Track]</c>：把成员登记到 "track" 频道，由方法宏 <c>@DUMP_TRACKED</c> 汇总。
    /// 片段自带成员名、所在类型等信息，所以这里不需要写代码。
    /// </summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Track : Attribute
    {
        public Track() => Macro.Fragment("track");
    }
}
