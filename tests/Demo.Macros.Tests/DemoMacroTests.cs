using HowMacro.Testing;
using Demo.Macros;
using Xunit;

// CaptureConsole temporarily replaces Console.Out, so tests don't run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Demo.Macros.Tests;

/// <summary>
/// Shows how to test macros. Three kinds of tests:
/// 1. Expansion output: assert what a line expanded to (expansion keeps line numbers, so source line N is r.Line(N)).
/// 2. Diagnostics: assert which error a macro reports for a misuse, and on which line.
/// 3. Runtime behavior: r.Compile() compiles and loads the expanded code, then calls the generated code.
/// </summary>
public class DemoMacroTests
{
    private static MacroTestResult Expand(string source, params string[] onlyMacros) =>
        MacroTest.Expand(source, new MacroTestOptions
        {
            References = { typeof(M).Assembly },
            Macros = onlyMacros.ToList(),
        });

    // Test sources have no implicit usings, so list what is needed. 5 lines; when the next """ block is appended, its first line is line 6.
    private const string Header = """
        using System; using System.Collections.Generic;
        using Demo.Macros;
        using static Demo.Macros.M;
        namespace Demo.App;
        public static class Data { public static int Hp = 100; public static readonly int Max = 999; }
        """;

    // ------------------------------------------------------------------ GETSET

    [Fact]
    public void GETSET_expands_to_a_forwarding_property()
    {
        var r = Expand(Header + """

            public class Player { public int Hp = @GETSET(Data.Hp); }
            """).AssertSuccess();

        Assert.Equal("public class Player { public int Hp { get => Data.Hp; set => Data.Hp = value; } }", r.Line(6));
    }

    [Fact]
    public void GETSET_forwards_reads_and_writes_at_runtime()
    {
        var compiled = Expand(Header + """

            public class Player { public int Hp = @GETSET(Data.Hp); }
            """).Compile();

        var player = compiled.Create("Demo.App.Player");
        Assert.True(compiled.IsProperty("Demo.App.Player", "Hp"));

        compiled.Set(player, "Hp", 42);
        Assert.Equal(42, compiled.Get("Demo.App.Data", "Hp"));
    }

    [Fact]
    public void GETSET_rejects_a_read_only_target()
    {
        var r = Expand(Header + """

            public class Player { public int Max = @GETSET(Data.Max); }
            """);

        r.AssertError("HMC100", contains: "read-only", line: 6);
    }

    // ------------------------------------------------------------------ LOGGED

    [Fact]
    public void LOGGED_logs_only_real_changes()
    {
        var compiled = Expand(Header + """

            public class Player { public int Gold = @LOGGED(100); }
            """).Compile();

        var player = compiled.Create("Demo.App.Player");
        var log = MacroTest.CaptureConsole(() =>
        {
            compiled.Set(player, "Gold", 150);
            compiled.Set(player, "Gold", 150); // unchanged, no log
        });

        Assert.Equal("[Player.Gold] 100 -> 150" + Environment.NewLine, log);
    }

    // ------------------------------------------------------------------ Attribute macros

    [Fact]
    public void Save_registers_fields_in_the_startup_script()
    {
        var r = Expand(Header + """

            public static class SaveContainer
            {
                public static List<string> Keys = new();
                public static void Register<T>(string key, Func<T, object?> get, Action<T, object?> set) => Keys.Add(key);
            }
            public class Player
            {
                [@Save] public int Gold = 1;
                [@Save("lv")] public int Level = 1;
                public int NotSaved = 0;
            }
            """).AssertSuccess();

        Assert.NotNull(r.Startup);
        Assert.Equal("public int Gold = 1;", r.Line(13).Trim()); // [@Save] is removed from the source; the field itself is unchanged

        var compiled = r.Compile(); // startup code runs on load
        Assert.Equal(new[] { "Gold", "lv" }, (List<string>)compiled.Get("Demo.App.SaveContainer", "Keys")!);
    }

    [Fact]
    public void DUMP_TRACKED_collects_fragments_from_Track()
    {
        var r = Expand(Header + """

            public class Player
            {
                [@Track] public int Hp = 1;
                public int Mp = 2;
                [@Track] public int Gold = 3;
                public string Dump() => @DUMP_TRACKED(this);
            }
            """);

        var compiled = r.Compile();
        Assert.Equal("Hp=1, Gold=3", compiled.Call(compiled.Create("Demo.App.Player"), "Dump"));
    }

    // ------------------------------------------------------------------ Semantic information

    [Fact]
    public void DUMP_walks_members_using_semantic_info()
    {
        var compiled = Expand(Header + """

            public class Player
            {
                public int Hp = 7;
                public string Name = "Ann";
                [Obsolete] public int Old = 0;
                private int _secret = 1;
                public string Dump() => @DUMP(this);
            }
            """).Compile();

        Assert.Equal("Player { Hp=7, Name=Ann }", compiled.Call(compiled.Create("Demo.App.Player"), "Dump"));
    }

    // ------------------------------------------------------------------ Rules

    [Fact]
    public void Macros_cannot_contain_macros()
    {
        var r = Expand(Header + """

            public class C { void F() { @LOG(@SQUARE(2)); } }
            """);

        r.AssertError("HMC012", contains: "@SQUARE is inside the arguments of @LOG", line: 6);
    }

    [Fact]
    public void LOG_wraps_its_output_in_a_block_and_keeps_the_line()
    {
        var r = Expand(Header + """

            public class C { void F(int x) { @LOG(x); } }
            """).AssertSuccess();

        Assert.Contains("{ System.Console.WriteLine(\"[Test0.cs:6] x = \" +(x)); }", r.Line(6));
    }

    // ------------------------------------------------------------------ Debugging tools: filtering, records, dump

    [Fact]
    public void Macro_filter_expands_only_the_selected_macros()
    {
        var r = Expand(Header + """

            public class Player
            {
                public int Hp = @GETSET(Data.Hp);
                public int Gold = @LOGGED(1);
            }
            """, "LOGGED").AssertSuccess();

        Assert.Contains("@GETSET(Data.Hp)", r.Line(8));   // not selected: left as is
        Assert.Contains("set {", r.Line(9));              // selected: expanded
        Assert.Equal("@LOGGED", Assert.Single(r.Expansions).Macro);
    }

    [Fact]
    public void DumpTo_writes_line_aligned_files_and_a_report()
    {
        var r = Expand(Header + """

            public class Player { public int Gold = @LOGGED(1); }
            """);

        var dir = Path.Combine(Path.GetTempPath(), "howmacro-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = r.DumpTo(dir);

            Assert.Contains("Test0.cs", written);
            var lines = File.ReadAllLines(Path.Combine(dir, "Test0.cs"));
            Assert.Equal(r.Line(6), lines[5]);

            var report = File.ReadAllText(Path.Combine(dir, "_expansions.txt"));
            Assert.Contains("Test0.cs:6  @LOGGED  (Member)", report);
            Assert.Contains("field = value;", report); // output formatted over several lines
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
