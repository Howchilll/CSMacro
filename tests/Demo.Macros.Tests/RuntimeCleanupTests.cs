using HowMacro.Testing;
using Xunit;

namespace Demo.Macros.Tests;

/// <summary>Runtime cleanup: macro assemblies unused after expansion are reported, and unused usings that import macro libraries are blanked (line numbers kept).</summary>
public class RuntimeCleanupTests
{
    private static MacroTestResult Expand(string source) =>
        MacroTest.Expand(source, new MacroTestOptions { References = { typeof(M).Assembly }, RuntimeCleanup = true });

    private static IEnumerable<string> Names(MacroTestResult r) => r.RuntimeUnused.Select(Path.GetFileNameWithoutExtension)!;

    [Fact]
    public void Macro_library_is_not_needed_at_runtime_and_its_using_is_removed()
    {
        var r = Expand("""
            using static Demo.Macros.M;
            namespace Demo.App;
            public class Player { public int Gold = @LOGGED(100); }
            """).AssertSuccess();

        Assert.Contains("Demo.Macros", Names(r));
        Assert.Contains("HowMacro.Abstractions", Names(r));
        Assert.Equal("", r.Line(1));                          // using static is blanked, line numbers kept
        Assert.StartsWith("public class Player { public int Gold { get;", r.Line(3));
    }

    [Fact]
    public void Assemblies_still_used_by_the_expanded_code_are_kept()
    {
        var r = Expand("""
            using static Demo.Macros.M;
            namespace Demo.App;
            public class Player
            {
                public int Gold = @LOGGED(100);
                public string Label() => HowMacro.Macro.Quote("not a macro, just a helper used at runtime");
            }
            """).AssertSuccess();

        Assert.Contains("Demo.Macros", Names(r));                 // only macros, not needed at runtime
        Assert.DoesNotContain("HowMacro.Abstractions", Names(r));  // Macro.Quote is still used at runtime, so it must stay
    }
}
