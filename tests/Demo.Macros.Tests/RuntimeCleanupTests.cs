using CSMacro.Testing;
using Xunit;

namespace Demo.Macros.Tests;

/// <summary>运行时清理：展开后用不到的宏程序集要被报告出来，导入宏库的无用 using 要被清掉（行号不变）。</summary>
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
        Assert.Contains("CSMacro.Abstractions", Names(r));
        Assert.Equal("", r.Line(1));                          // using static 被清空，行号不变
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
                public string Label() => CSMacro.Macro.Quote("not a macro, just a helper used at runtime");
            }
            """).AssertSuccess();

        Assert.Contains("Demo.Macros", Names(r));                 // 只有宏，运行时用不到
        Assert.DoesNotContain("CSMacro.Abstractions", Names(r));  // 运行时还在用 Macro.Quote，必须保留
    }
}
