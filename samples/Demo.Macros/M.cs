using System.Linq;
using System.Text.RegularExpressions;
using CSMacro;

namespace Demo.Macros
{
    /// <summary>
    /// 示例宏。每个方法的签名给 IDE 看（补全、类型检查、F12 跳转），
    /// 方法体在编译期执行，用 Macro.Emit 输出代码。
    /// </summary>
    public static class M
    {
        /// <summary>
        /// 把字段变成转发属性：
        /// <c>public int Hp = @GETSET(DataCenter.PlayerData.Hp);</c>
        /// 展开为 <c>public int Hp { get => DataCenter.PlayerData.Hp; set => DataCenter.PlayerData.Hp = value; }</c>
        /// </summary>
        [Macro(MacroSite.Member)]
        public static T GETSET<T>(T target)
        {
            var path = Macro.Arg(0);

            // 语义检查：目标必须是可写的字段或属性，否则在宏的调用处报一个清楚的错误。
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
        /// 统一规则：值改变时自动打 log。
        /// <c>public int Gold = @LOGGED(100);</c>
        /// 展开为带 setter 逻辑的属性，初值 100（初始化不触发 log）。
        /// 名字和类型都由源码声明；宏只决定 setter 怎么实现，用 C# 14 的 field 关键字，不引入任何新名字。
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
        /// 打印表达式及其值，带文件名和行号：<c>@LOG(player.Hp);</c>
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void LOG(object value)
        {
            var text = Regex.Replace(Macro.Arg(0), @"\s+", " ");
            var label = Macro.Quote($"[{Macro.FileName}:{Macro.Line}] {text} = ");
            Macro.Emit($"System.Console.WriteLine({label} + ({Macro.Arg(0)}));");
        }

        /// <summary>
        /// 断言，失败时消息里带原始表达式：<c>@ASSERT(hp > 0);</c>
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void ASSERT(bool condition)
        {
            var message = Macro.Quote($"Assertion failed at {Macro.FileName}:{Macro.Line}: {Macro.Arg(0)}");
            Macro.Emit($"if (!({Macro.Arg(0)})) throw new System.InvalidOperationException({message});");
        }

        /// <summary>表达式宏：<c>@SQUARE(x)</c> → <c>((x) * (x))</c></summary>
        [Macro]
        public static T SQUARE<T>(T x)
        {
            Macro.Emit($"({Macro.Arg(0)}) * ({Macro.Arg(0)})");
            return default!;
        }

        /// <summary>
        /// 方法宏消费特性宏的片段：把 obj 类型上所有标了 [@Track] 的成员拼成一行文本。
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
        /// 用语义信息遍历类型成员：把 obj 所有公开、可读的实例字段和属性拼成一行文本。
        /// <c>@DUMP(p)</c> → <c>("Player { Hp=" + p.Hp + ", Mp=" + p.Mp + ... + " }")</c>
        /// 加了 [Obsolete] 的成员会被跳过（演示读取成员上的特性）。
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

        /// <summary>调用所在的源码行号，编译期常量。</summary>
        [Macro]
        public static int LINE()
        {
            Macro.Emit(Macro.Line.ToString());
            return 0;
        }

        /// <summary>
        /// 编译期拼出一个 switch：<c>string s = @NAME_OF_INDEX(i, "Red", "Green");</c>
        /// 演示宏可以根据实参在编译期生成任意逻辑。
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
