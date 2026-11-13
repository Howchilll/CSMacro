using System.Text.RegularExpressions;
using CSMacro;
using CSMacro.Testing;
using Xunit;

namespace Demo.Macros.Tests;

/// <summary>
/// 只给测试用的片段宏：故意违反规则，验证引擎会报错。
/// 宏可以直接定义在测试项目里：测试程序集本身就是被引用的程序集。
/// </summary>
public static class TestRegionMacros
{
    [RegionMacro]
    public static void RENAME_FIRST() => Macro.RegionMembers[0].Replace("public int Renamed;");

    [RegionMacro]
    public static void BOTH_MODES()
    {
        Macro.RegionMembers[0].Replace(Macro.RegionMembers[0].Source);
        Macro.Emit(Macro.RegionText);
    }

    [RegionMacro]
    public static void ADD_LINE() => Macro.Emit(Macro.RegionText + "\npublic int Extra;");

    [RegionMacro]
    public static void ADD_MEMBER() => Macro.Emit(Regex.Replace(Macro.RegionText, @"int A;", "int A; public int Extra;"));

    [RegionMacro]
    public static void REPLACE_EVERYTHING()
    {
        foreach (var m in Macro.RegionMembers) m.Replace(m.Source);
    }
}

public class RegionMacroTests
{
    private static MacroTestResult Expand(string source) =>
        MacroTest.Expand(source, new MacroTestOptions { References = { typeof(M).Assembly, typeof(TestRegionMacros).Assembly } });

    // 占 5 行；拼上 """ 开头的下一段后，那一段的第一行就是第 6 行。
    private const string Header = """
        using System; using System.Collections.Generic;
        using Demo.Macros;
        using static Demo.Macros.M;
        namespace Demo.App;
        public static class Data { public static int Hp = 100; }
        """;

    // ------------------------------------------------------------------ 逐成员替换

    [Fact]
    public void LOG_ALL_logs_every_wrapped_field_and_keeps_lines()
    {
        var r = Expand(Header + """

            public class Enemy
            {
                #region @LOG_ALL
                public int Hp = 50;
                public string State = "Idle";
                public readonly int Id = 1;
                #endregion
            }
            """).AssertSuccess();

        Assert.Contains("#region [expanded] @LOG_ALL", r.Line(8));
        Assert.StartsWith("    public int Hp { get; set {", r.Line(9));
        Assert.Equal("    public readonly int Id = 1;", r.Line(11)); // readonly 被跳过
        Assert.Equal(2, r.Expansions.Count(e => e.Kind == "Region:Member"));

        var compiled = r.Compile();
        var enemy = compiled.Create("Demo.App.Enemy");
        var log = MacroTest.CaptureConsole(() =>
        {
            compiled.Set(enemy, "Hp", 30);
            compiled.Set(enemy, "State", "Chasing");
            compiled.Set(enemy, "Hp", 30); // 值没变
        });
        Assert.Equal("[Enemy.Hp] 50 -> 30\n[Enemy.State] Idle -> Chasing\n", log.Replace("\r\n", "\n"));
    }

    [Fact]
    public void LOG_ALL_skips_members_already_expanded_by_another_macro()
    {
        var r = Expand(Header + """

            public class Enemy
            {
                #region @LOG_ALL
                public int Hp = @GETSET(Data.Hp);
                public int Mp = 5;
                #endregion
            }
            """).AssertSuccess();

        Assert.Contains("get => Data.Hp; set => Data.Hp = value;", r.Line(9)); // 由 @GETSET 展开
        Assert.Contains("Console.WriteLine", r.Line(10));                      // 由 @LOG_ALL 替换
    }

    [Fact]
    public void Replacing_a_member_that_contains_a_macro_is_an_error()
    {
        var r = Expand(Header + """

            public class Enemy
            {
                #region @REPLACE_EVERYTHING
                public int Hp = @GETSET(Data.Hp);
                #endregion
            }
            """);

        r.AssertError("CSM005", contains: "already expanded by @GETSET", line: 8);
    }

    [Fact]
    public void Member_replacement_must_keep_the_name()
    {
        var r = Expand(Header + """

            public class C
            {
                #region @RENAME_FIRST
                public int A;
                #endregion
            }
            """);

        r.AssertError("CSM008", contains: "named 'A'", line: 9);
    }

    // ------------------------------------------------------------------ 整段文本替换

    [Fact]
    public void PUBLIC_ALL_replaces_text_line_by_line()
    {
        var r = Expand(Header + """

            public class C
            {
                #region @PUBLIC_ALL
                private int _secret = 42;
                private string _name = "x";
                #endregion
            }
            """).AssertSuccess();

        Assert.Equal("    public int _secret = 42;", r.Line(9));
        Assert.Equal("    public string _name = \"x\";", r.Line(10));
        Assert.Equal(42, r.Compile().Get(r.Compile().Create("Demo.App.C"), "_secret"));
    }

    [Fact]
    public void Text_output_may_not_add_lines()
    {
        var r = Expand(Header + """

            public class C
            {
                #region @ADD_LINE
                public int A;
                #endregion
            }
            """);

        r.AssertError("CSM007", contains: "can't add lines", line: 8);
    }

    [Fact]
    public void Text_output_may_not_add_names()
    {
        var r = Expand(Header + """

            public class C
            {
                #region @ADD_MEMBER
                public int A;
                #endregion
            }
            """);

        r.AssertError("CSM008", contains: "added: Extra", line: 8);
    }

    [Fact]
    public void A_region_macro_uses_one_mode_only()
    {
        var r = Expand(Header + """

            public class C
            {
                #region @BOTH_MODES
                public int A;
                #endregion
            }
            """);

        r.AssertError("CSM016", line: 8);
    }

    // ------------------------------------------------------------------ 发现和位置

    [Fact]
    public void Unknown_region_macro_stops_the_build()
    {
        var r = Expand(Header + """

            public class C
            {
                #region @NO_SUCH_MACRO
                public int A;
                #endregion
                #region ordinary region, not a macro
                public int B;
                #endregion
            }
            """);

        r.AssertError("CSM015", contains: "no region macro named 'NO_SUCH_MACRO'", line: 8);
        Assert.Single(r.Errors); // 普通 region 不受影响
    }

    [Fact]
    public void Region_macros_are_only_supported_in_type_bodies()
    {
        var r = Expand(Header + """

            public class C
            {
                void F()
                {
                    #region @LOG_ALL
                    var x = 1;
                    #endregion
                }
            }
            """);

        r.AssertError("CSM003", contains: "directly inside the same class/struct body", line: 10);
    }

    [Fact]
    public void Region_macros_cannot_be_nested()
    {
        var r = Expand(Header + """

            public class C
            {
                #region @LOG_ALL
                public int A;
                #region @PUBLIC_ALL
                private int B;
                #endregion
                #endregion
            }
            """);

        r.AssertError("CSM012", contains: "#region @PUBLIC_ALL is inside #region @LOG_ALL", line: 10);
    }
}
