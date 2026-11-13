using System;
using System.Linq;
using CSMacro;

namespace Dungeon.Macros
{
    /// <summary>
    /// <c>[@Command("heal", "Restore HP")]</c>：把一个静态方法注册成作弊控制台命令。
    /// 启动时自动注册；按参数类型生成解析代码，可选参数用默认值，返回值会打印。没有反射。
    /// </summary>
    [AttributeMacro(MacroTarget.Method)]
    public sealed class Command : Attribute
    {
        internal const char Separator = '\u001f';

        public Command(string name, string help = "")
        {
            var method = Macro.Declaration!;
            var command = (string)Macro.ArgValue(0)!;
            var description = Macro.ArgCount > 1 ? (string)Macro.ArgValue(1)! : "";

            if (!method.IsStatic)
            {
                Macro.Error($"[@Command] method {method.Name} must be static: commands are registered at startup");
                return;
            }

            var parameters = method.Parameters;
            var args = new string[parameters.Count];
            for (var i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                var parse = Shared.Parse(p.TypeInfo, $"a[{i}]");
                if (parse == null)
                {
                    Macro.Error($"parameter '{p.Name}' of {method.Name} has type {p.Type}, which [@Command] can't parse; supported: {Shared.SupportedTypes}");
                    return;
                }
                args[i] = p.HasDefault ? $"a.Length > {i} ? {parse} : {Shared.Literal(p.DefaultValue, p.TypeInfo!)}" : parse;
            }

            var required = parameters.Count(p => !p.HasDefault);
            var usage = (command + " " + string.Join(" ", parameters.Select(p =>
                p.HasDefault ? $"[{p.Name}:{Shared.Label(p.TypeInfo!)}={p.DefaultValue}]" : $"<{p.Name}:{Shared.Label(p.TypeInfo!)}>"))).Trim();
            var call = $"{method.ContainingType!.CodeName}.{method.Name}({string.Join(", ", args)})";
            var body = method.Type == "void" ? $"{call}; return null;" : $"return {call};";

            Macro.Startup(
                $"global::Dungeon.DebugConsole.Register({Macro.Quote(command)}, {Macro.Quote(usage)}, a => {{ " +
                $"if (a.Length < {required} || a.Length > {parameters.Count}) throw new global::System.ArgumentException(); {body} }});");
            Macro.Fragment("commands", string.Join(Separator.ToString(), command, usage, description));
        }
    }

    public static class Commands
    {
        /// <summary>编译期生成的帮助表；命令名重复时编译期报错，指出两处定义。</summary>
        [Macro]
        public static string COMMAND_HELP()
        {
            var commands = Macro.Fragments("commands").Select(f => (Fragment: f, Parts: f.Code.Split(Command.Separator))).ToList();
            foreach (var group in commands.GroupBy(c => c.Parts[0]).Where(g => g.Count() > 1))
                Macro.Error($"command '{group.Key}' is registered more than once: " +
                            string.Join(" and ", group.Select(c => $"{c.Fragment.ContainingType}.{c.Fragment.MemberName} ({System.IO.Path.GetFileName(c.Fragment.FilePath)}:{c.Fragment.Line})")));

            var width = commands.Count == 0 ? 0 : commands.Max(c => c.Parts[1].Length);
            var lines = commands.OrderBy(c => c.Parts[0], StringComparer.Ordinal).Select(c => "  " + c.Parts[1].PadRight(width) + "  " + c.Parts[2]);
            Macro.Emit(Macro.Quote(string.Join("\n", lines)));
            return "";
        }
    }
}
