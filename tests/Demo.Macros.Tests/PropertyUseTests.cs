using CSMacro.Testing;
using Xunit;

namespace Demo.Macros.Tests;

/// <summary>
/// 字段被宏变成属性后，只对字段成立的用法（ref/out、ref 表达式、取地址、修改结构体副本）要在用法处报 CSM017。
/// </summary>
public class PropertyUseTests
{
    private static MacroTestResult Expand(string source) =>
        MacroTest.Expand(source, new MacroTestOptions { References = { typeof(M).Assembly } });

    // 占 5 行；拼上 """ 开头的下一段后，那一段的第一行就是第 6 行。
    private const string Header = """
        using System; using System.Threading;
        using Demo.Macros;
        using static Demo.Macros.M;
        namespace Demo.App;
        public struct Vec { public int X; }
        """;

    [Fact]
    public void Passing_a_macro_property_by_ref_is_reported_at_the_use()
    {
        var r = Expand(Header + """

            public class Player { public int Gold = @LOGGED(100); }
            public static class Bank
            {
                static void Add(ref int x) => x++;
                public static void Pay(Player p)
                {
                    Add(ref p.Gold);
                    Interlocked.Increment(ref p.Gold);
                }
            }
            """);

        r.AssertError("CSM017", contains: "Gold is turned into a property by @LOGGED, so it can't be passed as `ref`", line: 12);
        r.AssertError("CSM017", contains: "can't be passed as `ref`", line: 13);
    }

    [Fact]
    public void Out_arguments_are_reported_for_region_macros_too()
    {
        var r = Expand(Header + """

            public class Enemy
            {
                #region @LOG_ALL
                public int Hp = 50;
                #endregion
                public void Parse(string s) { int.TryParse(s, out Hp); }
            }
            """);

        r.AssertError("CSM017", contains: "Hp is turned into a property by #region @LOG_ALL, so it can't be passed as `out`", line: 11);
    }

    [Fact]
    public void Ref_expressions_and_struct_mutation_are_reported()
    {
        var r = Expand(Header + """

            public class Player
            {
                public int Hp = @LOGGED(1);
                public Vec Pos = @LOGGED(new Vec());
                public void F()
                {
                    ref int hp = ref Hp;
                    Pos.X = 3;
                    Pos.X++;
                }
            }
            """);

        r.AssertError("CSM017", contains: "you can't take a `ref` to it", line: 12);
        r.AssertError("CSM017", contains: "assigning to `Pos.X` would only change a copy", line: 13);
        r.AssertError("CSM017", contains: "assigning to `Pos.X` would only change a copy", line: 14);
    }

    [Fact]
    public void Ordinary_uses_and_untransformed_fields_are_fine()
    {
        Expand(Header + """

            public class Player
            {
                public int Gold = @LOGGED(100);
                public int Plain = @SQUARE(3);   // 表达式宏：仍然是字段
                public Vec Pos = new Vec();      // 普通字段
                static void Read(in int x) { }
                static void Add(ref int x) => x++;
                public void F()
                {
                    Gold += 5; Gold++; Read(in Gold); Console.WriteLine(Gold);
                    Add(ref Plain);
                    Pos.X = 1;
                    var p = Pos; p.X = 2;
                }
            }
            """).AssertSuccess();
    }
}
