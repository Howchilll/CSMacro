using System;
using System.Linq;
using HowMacro;

namespace Showcase.Macros
{
    /// <summary>Property macros: change notification and range clamping.</summary>
    public static class Properties
    {
        /// <summary>
        /// Change-notifying property: <c>public string Name = @OBSERVABLE("Nameless");</c>
        /// Assigns and calls <c>OnPropertyChanged(nameof(Name))</c> only when the value actually changes.
        /// The containing class must declare <c>void OnPropertyChanged(string)</c> itself (names come from source; macros don't invent them), otherwise it is a compile-time error.
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
        /// Range-clamped property: <c>public int Hp = @CLAMP(0, 100, 100);</c>
        /// Assignments are clamped to [min, max]. Compile-time checks: the type must be numeric; constant arguments must satisfy min ≤ initial ≤ max.
        /// If the containing class declares <c>OnPropertyChanged(string)</c>, changes also notify (composed via semantic information, no nested macros needed).
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

        /// <summary>The statement that calls <c>OnPropertyChanged(string)</c> if the containing class has it, otherwise null.</summary>
        private static string? NotifyCall()
        {
            var hasNotify = Macro.ContainingTypeInfo?.MembersNamed("OnPropertyChanged")
                .Any(m => m.Kind == MacroMemberKind.Method && m.Parameters.Count == 1 && m.Parameters[0].Type == "string") == true;
            return hasNotify ? $"OnPropertyChanged(nameof({Macro.Name}));" : null;
        }
    }
}
