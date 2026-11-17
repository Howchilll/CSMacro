using System.Reflection;
using HowMacro;
using HowMacro.Testing;
using Xunit;

namespace Demo.Macros.Tests;

/// <summary>Test-only member macros: one that keeps its state in a private field, one that wrongly adds a public one.</summary>
public static class StateMacros
{
    /// <summary><c>public int Gold = @TRACKED(5);</c> → a property over a private field @_temp_Gold, counting sets in TrackLog (works in C# 9: no `field`).</summary>
    [Macro(MacroSite.Member)]
    public static T TRACKED<T>(T initial)
    {
        var store = "@_temp_" + Macro.Name;
        Macro.Emit($"private {Macro.Type} {store} = {Macro.Arg(0)}; ");
        Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get => {store}; set {{ {store} = value; global::Demo.Macros.Tests.TrackLog.Sets++; }} }}");
        return default!;
    }

    [Macro(MacroSite.Member)]
    public static T PUBLIC_EXTRA<T>(T initial)
    {
        Macro.Emit($"public int Extra; {Macro.Modifiers} {Macro.Type} {Macro.Name} = {Macro.Arg(0)};");
        return default!;
    }
}

public static class TrackLog { public static int Sets; }

public class PrivateStateTests
{
    private static MacroTestResult Expand(string source) =>
        MacroTest.Expand(source, new MacroTestOptions { References = { typeof(M).Assembly, typeof(StateMacros).Assembly }, LanguageVersion = "9" });

    private const string Header = """
        using static Demo.Macros.Tests.StateMacros;
        namespace Demo.App {
        """;

    [Fact]
    public void A_member_macro_may_keep_state_in_private_fields()
    {
        var r = Expand(Header + """

            public class Wallet
            {
                public int Gold = @TRACKED(5);
                public int Gems = @TRACKED(1);
            }
            }
            """).AssertSuccess();

        Assert.Contains("private int @_temp_Gold = 5;", r.Line(5));
        var c = r.Compile();
        var w = c.Create("Demo.App.Wallet");
        Assert.Equal(5, c.Get(w, "Gold"));
        var before = TrackLog.Sets;
        c.Set(w, "Gold", 9);
        c.Set(w, "Gems", 2);
        Assert.Equal(9, c.Get(w, "Gold"));
        Assert.Equal(before + 2, TrackLog.Sets);
        var store = w.GetType().GetField("_temp_Gold", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(store.IsPrivate);
    }

    [Fact]
    public void Extra_members_must_be_private_fields()
    {
        var r = Expand(Header + """

            public class Wallet
            {
                public int Gold = @PUBLIC_EXTRA(5);
            }
            }
            """);

        r.AssertError("HMC008", contains: "private fields", line: 5);
    }

    [Fact]
    public void Source_cannot_use_the_private_state()
    {
        var r = Expand(Header + """

            public class Wallet
            {
                public int Gold = @TRACKED(5);
                public int Peek() => _temp_Gold;
            }
            }
            """);

        r.AssertError("HMC021", contains: "'_temp_Gold' is a private field a macro generates", line: 6);
    }
}
