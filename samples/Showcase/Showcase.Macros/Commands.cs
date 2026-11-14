using System;
using System.Globalization;
using System.Linq;
using HowMacro;

namespace Showcase.Macros
{
    /// <summary>
    /// Attribute macro <c>[@Command("heal", "Restore HP")]</c>: registers a static method as a debug console command.
    /// <list type="bullet">
    /// <item>Registered automatically at startup (generated startup script); no command table to maintain.</item>
    /// <item>Generates parsing code per parameter type (int / float / bool / string / enum…); optional parameters use their defaults; return values are printed.</item>
    /// <item>Compile-time checks: the method must be static and its parameter types parseable; duplicate command names are reported by @COMMAND_HELP.</item>
    /// </list>
    /// No reflection: IL2CPP / AOT trimming can't drop the commands.
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
                Macro.Error($"[@Command] method {method.Name} must be static: commands are registered at startup, before any instance exists");
                return;
            }

            var parameters = method.Parameters;
            var args = new string[parameters.Count];
            for (var i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                var parse = Parse(p.TypeInfo, $"a[{i}]");
                if (parse == null)
                {
                    Macro.Error($"parameter '{p.Name}' of {method.Name} has type {p.Type}, which [@Command] can't parse; supported: int, long, float, double, bool, string, enums");
                    return;
                }
                args[i] = p.HasDefault ? $"a.Length > {i} ? {parse} : {Literal(p.DefaultValue, p.TypeInfo!)}" : parse;
            }

            var required = parameters.Count(p => !p.HasDefault);
            var usage = (command + " " + string.Join(" ", parameters.Select(p =>
                p.HasDefault ? $"[{p.Name}:{Label(p.TypeInfo!)}={p.DefaultValue}]" : $"<{p.Name}:{Label(p.TypeInfo!)}>"))).Trim();

            var call = $"{method.ContainingType!.CodeName}.{method.Name}({string.Join(", ", args)})";
            var body = method.Type == "void" ? $"{call}; return null;" : $"return {call};";

            Macro.Startup(
                $"global::Showcase.App.DebugConsole.Register({Macro.Quote(command)}, {Macro.Quote(usage)}, {Macro.Quote(description)}, a => {{ " +
                $"if (a.Length < {required} || a.Length > {parameters.Count}) throw new global::System.ArgumentException(); {body} }});");

            Macro.Fragment("commands", string.Join(Separator.ToString(), command, usage, description));
        }

        /// <summary>The type shown in usage: enums list their values (from semantic information), others use the C# keyword.</summary>
        private static string Label(MacroType type) =>
            type.Kind == MacroTypeKind.Enum ? string.Join("|", type.EnumValues).ToLowerInvariant() : type.FullName;

        private static string? Parse(MacroType? type, string a)
        {
            if (type == null) return null;
            if (type.Kind == MacroTypeKind.Enum) return $"({type.CodeName})global::System.Enum.Parse(typeof({type.CodeName}), {a}, true)";
            const string inv = "global::System.Globalization.CultureInfo.InvariantCulture";
            return type.FullName switch
            {
                "int" => $"int.Parse({a}, {inv})",
                "long" => $"long.Parse({a}, {inv})",
                "float" => $"float.Parse({a}, {inv})",
                "double" => $"double.Parse({a}, {inv})",
                "bool" => $"bool.Parse({a})",
                "string" => a,
                _ => null,
            };
        }

        private static string Literal(object? value, MacroType type) => value switch
        {
            null => "default",
            string s => Macro.Quote(s),
            bool b => b ? "true" : "false",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            _ when type.Kind == MacroTypeKind.Enum => $"({type.CodeName}){Convert.ToInt64(value, CultureInfo.InvariantCulture)}",
            IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!,
        };
    }

    public static class Commands
    {
        /// <summary>
        /// Help table generated at compile time: <c>public static string Help => @COMMAND_HELP();</c>
        /// Collects all [@Command]s sorted by name; duplicate command names are compile-time errors that point at both definitions.
        /// </summary>
        [Macro]
        public static string COMMAND_HELP()
        {
            var commands = Macro.Fragments("commands")
                .Select(f => (Fragment: f, Parts: f.Code.Split(Command.Separator)))
                .ToList();

            foreach (var group in commands.GroupBy(c => c.Parts[0]).Where(g => g.Count() > 1))
            {
                var places = group.Select(c => $"{c.Fragment.ContainingType}.{c.Fragment.MemberName} ({System.IO.Path.GetFileName(c.Fragment.FilePath)}:{c.Fragment.Line})");
                Macro.Error($"command '{group.Key}' is registered more than once: {string.Join(" and ", places)}");
            }

            var width = commands.Count == 0 ? 0 : commands.Max(c => c.Parts[1].Length);
            var lines = commands
                .OrderBy(c => c.Parts[0], StringComparer.Ordinal)
                .Select(c => "  " + c.Parts[1].PadRight(width) + "  " + c.Parts[2]);
            Macro.Emit(Macro.Quote(string.Join("\n", lines)));
            return "";
        }
    }
}
