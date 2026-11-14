using System.Linq;
using System.Text.RegularExpressions;
using HowMacro;

namespace Demo.Macros
{
    /// <summary>
    /// Sample macros. Each method's signature is for the IDE (completion, type checking, F12),
    /// and its body runs at compile time, emitting code with Macro.Emit.
    /// </summary>
    public static class M
    {
        /// <summary>
        /// Turns the field into a forwarding property:
        /// <c>public int Hp = @GETSET(DataCenter.PlayerData.Hp);</c>
        /// Expands to <c>public int Hp { get => DataCenter.PlayerData.Hp; set => DataCenter.PlayerData.Hp = value; }</c>
        /// </summary>
        [Macro(MacroSite.Member)]
        public static T GETSET<T>(T target)
        {
            var path = Macro.Arg(0);

            // Semantic check: the target must be a writable field or property; otherwise report a clear error at the macro call.
            var symbol = Macro.ArgSymbol(0);
            if (symbol == null || !symbol.IsFieldOrProperty)
            {
                Macro.Error($"`{path}` must be a field or property to forward {Macro.Name} to");
                return default!;
            }
            if (!symbol.CanWrite)
            {
                var why = symbol.IsConst ? "const" : symbol.IsInitOnly ? "init-only" : "read-only";
                Macro.Error($"`{path}` is {why}, so {Macro.Name}'s setter can't forward to it");
                return default!;
            }

            Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get => {path}; set => {path} = value; }}");
            return default!;
        }

        /// <summary>
        /// One shared rule: log every change of the value.
        /// <c>public int Gold = @LOGGED(100);</c>
        /// Expands to a property with setter logic and initial value 100 (initialization doesn't log).
        /// The name and type are declared in source; the macro only decides how the setter works, using the C# 14 field keyword, without introducing any new names.
        /// </summary>
        [Macro(MacroSite.Member)]
        public static T LOGGED<T>(T initial)
        {
            var owner = Macro.ContainingType.Split('.').Last();
            var label = Macro.Quote($"[{owner}.{Macro.Name}] ");
            Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name}");
            Macro.Emit("{ get; set {");
            Macro.Emit($"  if (global::System.Collections.Generic.EqualityComparer<{Macro.Type}>.Default.Equals(field, value)) return;");
            Macro.Emit($"  global::System.Console.WriteLine({label} + field + \" -> \" + value);");
            Macro.Emit("  field = value;");
            Macro.Emit($"}} }} = {Macro.Arg(0)};");
            return default!;
        }

        /// <summary>
        /// Prints an expression and its value, with file name and line: <c>@LOG(player.Hp);</c>
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void LOG(object value)
        {
            var text = Regex.Replace(Macro.Arg(0), @"\s+", " ");
            var label = Macro.Quote($"[{Macro.FileName}:{Macro.Line}] {text} = ");
            Macro.Emit($"System.Console.WriteLine({label} + ({Macro.Arg(0)}));");
        }

        /// <summary>
        /// Assertion whose failure message includes the original expression: <c>@ASSERT(hp > 0);</c>
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void ASSERT(bool condition)
        {
            var message = Macro.Quote($"Assertion failed at {Macro.FileName}:{Macro.Line}: {Macro.Arg(0)}");
            Macro.Emit($"if (!({Macro.Arg(0)})) throw new System.InvalidOperationException({message});");
        }

        /// <summary>Expression macro: <c>@SQUARE(x)</c> → <c>((x) * (x))</c></summary>
        [Macro]
        public static T SQUARE<T>(T x)
        {
            Macro.Emit($"({Macro.Arg(0)}) * ({Macro.Arg(0)})");
            return default!;
        }

        /// <summary>
        /// A method macro consuming attribute-macro fragments: joins all [@Track] members of obj's type into one line of text.
        /// <c>@DUMP_TRACKED(p)</c> → <c>("Hp=" + p.Hp + ", " + "Gold=" + p.Gold)</c>
        /// </summary>
        [Macro]
        public static string DUMP_TRACKED<T>(T obj)
        {
            var type = Macro.ArgType(0);
            var parts = Macro.Fragments("track")
                .Where(f => f.ContainingType == type)
                .Select(f => $"{Macro.Quote(f.MemberName + "=")} + {Macro.Arg(0)}.{f.MemberName}")
                .ToList();

            if (parts.Count == 0)
            {
                Macro.Warning($"no [@Track] members on {type}");
                Macro.Emit("\"\"");
            }
            else
            {
                Macro.Emit(string.Join(" + \", \" + ", parts));
            }
            return "";
        }

        /// <summary>
        /// Walks the type's members via semantic information: joins all public, readable instance fields and properties of obj into one line of text.
        /// <c>@DUMP(p)</c> → <c>("Player { Hp=" + p.Hp + ", Mp=" + p.Mp + ... + " }")</c>
        /// Members marked [Obsolete] are skipped (shows reading attributes on members).
        /// </summary>
        [Macro]
        public static string DUMP<T>(T obj)
        {
            var type = Macro.ArgTypeInfo(0);
            if (type == null)
            {
                Macro.Error($"can't determine the type of `{Macro.Arg(0)}`");
                return "";
            }

            var members = type.Members
                .Where(m => m.IsFieldOrProperty && m.IsPublic && !m.IsStatic && m.CanRead && !m.HasAttribute("Obsolete"))
                .ToList();

            var parts = members.Select((m, i) =>
                $"{Macro.Quote((i == 0 ? type.Name + " { " : ", ") + m.Name + "=")} + {Macro.Arg(0)}.{m.Name}");
            Macro.Emit(members.Count == 0
                ? Macro.Quote(type.Name + " { }")
                : string.Join(" + ", parts) + " + " + Macro.Quote(" }"));
            return "";
        }

        /// <summary>The source line of the call, as a compile-time constant.</summary>
        [Macro]
        public static int LINE()
        {
            Macro.Emit(Macro.Line.ToString());
            return 0;
        }

        /// <summary>
        /// Builds a switch at compile time: <c>string s = @NAME_OF_INDEX(i, "Red", "Green");</c>
        /// Shows that a macro can generate arbitrary logic from its arguments at compile time.
        /// </summary>
        [Macro]
        public static string NAME_OF_INDEX(int index, params string[] names)
        {
            var arms = Enumerable.Range(1, Macro.ArgCount - 1)
                .Select(i => $"{i - 1} => {Macro.Quote((string)Macro.ArgValue(i)!)},");
            Macro.Emit($"{Macro.Arg(0)} switch {{ {string.Join(" ", arms)} _ => \"?\" }}");
            return "";
        }
    }
}
