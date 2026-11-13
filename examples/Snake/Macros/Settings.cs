using System;
using System.Linq;
using CSMacro;

namespace Snake.Macros;

/// <summary>
/// <c>[@Setting("width", "Board width")]</c>：把字段绑定到 snake.ini 里的一个键。
/// 由 <c>@APPLY_SETTINGS(values, report)</c> 生成解析和赋值代码，由 <c>@SETTINGS_TEMPLATE(this)</c> 生成默认配置文件。
/// </summary>
[AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
public sealed class Setting : Attribute
{
    internal const char Separator = '\u001f';

    public Setting(string key, string description = "")
    {
        var member = Macro.Declaration!;
        if (Shared.Parse(member.TypeInfo, "x") == null)
            Macro.Error($"{member.Name} has type {member.Type}, which [@Setting] can't parse; supported: {Shared.SupportedTypes}");
        else
            Macro.Fragment("settings", string.Join(Separator.ToString(), (string)Macro.ArgValue(0)!, Macro.ArgCount > 1 ? (string)Macro.ArgValue(1)! : ""));
    }
}

public static class SettingsMacros
{
    /// <summary>
    /// <c>@APPLY_SETTINGS(values, report);</c>：values 是键值对序列，report 是 Action&lt;string&gt;。
    /// 按成员类型解析后赋值（经过 setter，所以 @CLAMP 的范围照样生效）；格式错误、越界、未知键都会报告。
    /// </summary>
    [Macro(MacroSite.Statement)]
    public static void APPLY_SETTINGS(object values, object report)
    {
        var settings = Settings(Macro.ContainingType);
        foreach (var dup in settings.GroupBy(s => s.Key).Where(g => g.Count() > 1))
            Macro.Error($"setting key '{dup.Key}' is bound twice: {string.Join(" and ", dup.Select(d => d.Member.Name))}");

        var r = Macro.Arg(1);
        Macro.Emit($"foreach (var __kv in {Macro.Arg(0)}) {{ switch (__kv.Key.Trim().ToLowerInvariant()) {{");
        foreach (var s in settings)
        {
            var key = Macro.Quote(s.Key);
            Macro.Emit($"case {key}: try {{ var __v = {Shared.Parse(s.Member.TypeInfo, "__kv.Value.Trim()")}; this.{s.Member.Name} = __v; " +
                       $"if (!global::System.Collections.Generic.EqualityComparer<{s.Member.Type}>.Default.Equals(this.{s.Member.Name}, __v)) " +
                       $"{r}({key} + \" = \" + __v + \" is out of range, using \" + this.{s.Member.Name}); }} " +
                       $"catch (global::System.Exception) {{ {r}({key} + \": can't read '\" + __kv.Value.Trim() + \"' as {s.Member.Type}\"); }} break;");
        }
        Macro.Emit($"default: {r}(\"unknown setting '\" + __kv.Key.Trim() + \"'; known: \" + {Macro.Quote(string.Join(", ", settings.Select(s => s.Key)))}); break; }} }}");
    }

    /// <summary><c>@SETTINGS_TEMPLATE(obj)</c>：用 obj 当前的值生成一份带说明的配置文件文本。</summary>
    [Macro]
    public static string SETTINGS_TEMPLATE<T>(T obj)
    {
        var parts = Settings(Macro.ArgTypeInfo(0)?.FullName ?? "")
            .Select(s => $"{Macro.Quote($"# {s.Description} ({s.Member.Type})\n{s.Key} = ")} + {Macro.Arg(0)}.{s.Member.Name} + \"\\n\\n\"");
        Macro.Emit(string.Join(" + ", parts.DefaultIfEmpty("\"\"")));
        return "";
    }

    private static (string Key, string Description, MacroMember Member)[] Settings(string owner) =>
        Macro.Fragments("settings")
            .Where(f => f.Declaration != null && f.ContainingType == owner)
            .Select(f => { var p = f.Code.Split(Setting.Separator); return (p[0], p[1], f.Declaration!); })
            .ToArray();
}
