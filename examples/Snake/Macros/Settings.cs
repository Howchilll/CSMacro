using System;
using System.Linq;
using HowMacro;

namespace Snake.Macros;

/// <summary>
/// <c>[@Setting("width", "Board width")]</c>: binds a field to a key in snake.ini.
/// <c>@APPLY_SETTINGS(values, report)</c> generates the parsing and assignment code; <c>@SETTINGS_TEMPLATE(this)</c> generates the default settings file.
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
    /// <c>@APPLY_SETTINGS(values, report);</c> where values is a sequence of key/value pairs and report is an Action&lt;string&gt;.
    /// Each value is parsed by the member's type and assigned through the setter (so @CLAMP ranges still apply); bad formats, out-of-range values and unknown keys are reported.
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

    /// <summary><c>@SETTINGS_TEMPLATE(obj)</c>: generates a commented settings file from obj's current values.</summary>
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
