using System;
using System.Linq;
using HowMacro;

namespace Snake.Macros;

public static class Properties
{
    /// <summary>
    /// Change-notifying property: <c>public int Score = @OBSERVABLE(0);</c>
    /// Assigns and calls <c>OnPropertyChanged(nameof(Score))</c> only when the value actually changes (the containing type must declare it).
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
    /// Range-clamped property: <c>public int Width = @CLAMP(10, 60, 30);</c>
    /// Assignments are clamped to [min, max]; with constant arguments, min ≤ initial ≤ max is checked at compile time. Also notifies when the containing type declares OnPropertyChanged.
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
        string lo2 = $"({Macro.Arg(0)})", hi2 = $"({Macro.Arg(1)})";
        Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get; set {{ " +
                   $"var __clamped = value < {lo2} ? {lo2} : value > {hi2} ? {hi2} : value; " +
                   $"if (field == __clamped) return; field = __clamped; {Shared.NotifyCall()} }} }} = {Macro.Arg(2)};");
        return default!;
    }
}
