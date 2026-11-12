using System;
using System.Linq;
using CSMacro;

namespace Showcase.Macros
{
    /// <summary>属性类宏：变更通知、范围约束。</summary>
    public static class Properties
    {
        /// <summary>
        /// 变更通知属性：<c>public string Name = @OBSERVABLE("Nameless");</c>
        /// 值真的变了才赋值并调用 <c>OnPropertyChanged(nameof(Name))</c>。
        /// 所在类必须自己声明 <c>void OnPropertyChanged(string)</c>（名字由源码声明，宏不造名字），否则编译期报错。
        /// </summary>
        [Macro(MacroSite.Member)]
        public static T OBSERVABLE<T>(T initial)
        {
            var notify = NotifyCall();
            if (notify == null)
            {
                Macro.Error($"{Macro.ContainingTypeInfo?.Name} must declare `void OnPropertyChanged(string propertyName)` for @OBSERVABLE to call");
                return default!;
            }

            Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get; set {{");
            Macro.Emit($"  if (global::System.Collections.Generic.EqualityComparer<{Macro.Type}>.Default.Equals(field, value)) return;");
            Macro.Emit($"  field = value; {notify}");
            Macro.Emit($"}} }} = {Macro.Arg(0)};");
            return default!;
        }

        /// <summary>
        /// 范围约束属性：<c>public int Hp = @CLAMP(0, 100, 100);</c>
        /// 赋值时自动夹到 [min, max]。编译期检查：类型必须是数值；常量参数要满足 min ≤ initial ≤ max。
        /// 如果所在类声明了 <c>OnPropertyChanged(string)</c>，变化时顺带发通知（按语义信息自动组合，不需要嵌套宏）。
        /// </summary>
        [Macro(MacroSite.Member)]
        public static T CLAMP<T>(T min, T max, T initial)
        {
            var numeric = new[] { "int", "long", "short", "byte", "float", "double", "decimal" };
            if (!numeric.Contains(Macro.TypeInfo?.FullName))
            {
                Macro.Error($"@CLAMP needs a numeric member, but {Macro.Name} is {Macro.Type}");
                return default!;
            }

            if (Macro.Args.All(a => a.HasConstant))
            {
                double lo = Convert.ToDouble(Macro.ArgValue(0)), hi = Convert.ToDouble(Macro.ArgValue(1)), init = Convert.ToDouble(Macro.ArgValue(2));
                if (lo > hi) Macro.Error($"min {Macro.Arg(0)} is greater than max {Macro.Arg(1)}");
                else if (init < lo || init > hi) Macro.Error($"initial value {Macro.Arg(2)} of {Macro.Name} is outside [{Macro.Arg(0)}, {Macro.Arg(1)}]");
            }

            string lower = Macro.Arg(0), upper = Macro.Arg(1);
            Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get; set {{");
            Macro.Emit($"  var __clamped = value < ({lower}) ? ({lower}) : value > ({upper}) ? ({upper}) : value;");
            Macro.Emit($"  if (field == __clamped) return;");
            Macro.Emit($"  field = __clamped; {NotifyCall()}");
            Macro.Emit($"}} }} = {Macro.Arg(2)};");
            return default!;
        }

        /// <summary>所在类有 <c>OnPropertyChanged(string)</c> 时返回调用它的语句，否则 null。</summary>
        private static string? NotifyCall()
        {
            var hasNotify = Macro.ContainingTypeInfo?.MembersNamed("OnPropertyChanged")
                .Any(m => m.Kind == MacroMemberKind.Method && m.Parameters.Count == 1 && m.Parameters[0].Type == "string") == true;
            return hasNotify ? $"OnPropertyChanged(nameof({Macro.Name}));" : null;
        }
    }
}
