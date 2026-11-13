using System;
using System.Linq;
using CSMacro;

namespace Dungeon.Macros
{
    public static class Properties
    {
        /// <summary>
        /// 变更通知属性：<c>public int Gold = @OBSERVABLE(0);</c>
        /// 值真的变了才赋值并调用 <c>OnPropertyChanged(nameof(Gold))</c>（所在类型必须声明它，否则编译期报错）。
        /// </summary>
        [Macro(MacroSite.Member)]
        public static T OBSERVABLE<T>(T initial)
        {
            var notify = Shared.NotifyCall();
            if (notify == null)
            {
                Macro.Error($"{Macro.ContainingTypeInfo?.Name} must declare `void OnPropertyChanged(string propertyName)` for @OBSERVABLE to call");
                return default!;
            }
            Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get; set {{ " +
                       $"if (global::System.Collections.Generic.EqualityComparer<{Macro.Type}>.Default.Equals(field, value)) return; " +
                       $"field = value; {notify} }} }} = {Macro.Arg(0)};");
            return default!;
        }

        /// <summary>
        /// 范围约束属性：<c>public int Hp = @CLAMP(0, MaxHp, 20);</c>
        /// 范围可以是任意表达式（比如另一个属性 MaxHp）；全是常量时编译期检查 min ≤ initial ≤ max。
        /// 所在类型声明了 OnPropertyChanged 时，变化时顺带通知。
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
            Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get; set {{ " +
                       $"var __clamped = value < ({lower}) ? ({lower}) : value > ({upper}) ? ({upper}) : value; " +
                       $"if (field == __clamped) return; field = __clamped; {Shared.NotifyCall()} }} }} = {Macro.Arg(2)};");
            return default!;
        }
    }
}
