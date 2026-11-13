using System;
using System.Linq;
using CSMacro;

namespace Dungeon.Macros
{
    /// <summary>
    /// <c>[@Setting("view_radius", "How far you can see")]</c>：把字段绑定到配置文件里的一个键。
    /// 由 <c>@APPLY_SETTINGS(values, report)</c> 生成解析和赋值代码（类型解析、越界提示、未知键提示），
    /// 由 <c>@SETTINGS_TEMPLATE(this)</c> 生成带说明的默认配置文件。没有反射。
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
            else if (!member.CanWrite)
                Macro.Error($"{member.Name} is read-only, so [@Setting] couldn't assign it");
            else
                Macro.Fragment("settings", string.Join(Separator.ToString(), (string)Macro.ArgValue(0)!, Macro.ArgCount > 1 ? (string)Macro.ArgValue(1)! : ""));
        }
    }

    public static class SettingsMacros
    {
        /// <summary>
        /// <c>@APPLY_SETTINGS(values, report);</c>：values 是 IEnumerable&lt;KeyValuePair&lt;string, string&gt;&gt;，report 是 Action&lt;string&gt;。
        /// 每个键按成员类型解析后赋值（经过 setter，所以 @CLAMP 的范围约束照样生效）；
        /// 解析失败、被夹到范围内、未知键都会通过 report 报告。键名重复在编译期报错。
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void APPLY_SETTINGS(object values, object report)
        {
            var settings = Settings(Macro.ContainingType);
            foreach (var dup in settings.GroupBy(s => s.Key).Where(g => g.Count() > 1))
                Macro.Error($"setting key '{dup.Key}' is bound twice: {string.Join(" and ", dup.Select(d => d.Member.Name))}");

            var known = Macro.Quote(string.Join(", ", settings.Select(s => s.Key)));
            Macro.Emit($"foreach (var __kv in {Macro.Arg(0)}) {{ switch (__kv.Key.Trim().ToLowerInvariant()) {{");
            foreach (var s in settings)
            {
                var parse = Shared.Parse(s.Member.TypeInfo, "__kv.Value.Trim()");
                var key = Macro.Quote(s.Key);
                Macro.Emit($"case {key}: try {{ var __v = {parse}; this.{s.Member.Name} = __v; " +
                           $"if (!global::System.Collections.Generic.EqualityComparer<{s.Member.Type}>.Default.Equals(this.{s.Member.Name}, __v)) " +
                           $"{Macro.Arg(1)}({key} + \" = \" + __v + \" is out of range, using \" + this.{s.Member.Name}); }} " +
                           $"catch (global::System.Exception) {{ {Macro.Arg(1)}({key} + \": can't read '\" + __kv.Value.Trim() + \"' as {Shared.Label(s.Member.TypeInfo!)}\"); }} break;");
            }
            Macro.Emit($"default: {Macro.Arg(1)}(\"unknown setting '\" + __kv.Key.Trim() + \"'; known: \" + {known}); break; }} }}");
        }

        /// <summary><c>@SETTINGS_TEMPLATE(obj)</c>：用 obj 当前的值生成一份带说明的配置文件文本。</summary>
        [Macro]
        public static string SETTINGS_TEMPLATE<T>(T obj)
        {
            var o = Macro.Arg(0);
            var parts = Settings(Macro.ArgTypeInfo(0)?.FullName ?? "").Select(s =>
                $"\"# {Escape(s.Description)} ({Escape(Shared.Label(s.Member.TypeInfo!))})\\n{s.Key} = \" + {o}.{s.Member.Name} + \"\\n\\n\"");
            Macro.Emit(string.Join(" + ", parts.DefaultIfEmpty("\"\"")));
            return "";
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static (string Key, string Description, MacroMember Member)[] Settings(string owner) =>
            Macro.Fragments("settings")
                .Where(f => f.Declaration != null && f.ContainingType == owner)
                .Select(f => { var p = f.Code.Split(Setting.Separator); return (p[0], p[1], f.Declaration!); })
                .ToArray();
    }
}
