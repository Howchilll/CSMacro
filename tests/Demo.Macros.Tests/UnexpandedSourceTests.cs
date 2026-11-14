using HowMacro.Testing;
using Xunit;

namespace Demo.Macros.Tests;

/// <summary>
/// Core principle backstop (HMC020): code at a macro call site must be valid C# before expansion.
/// Code that only compiles after expansion (which the IDE keeps flagging) must fail the build instead of passing silently.
/// </summary>
public class UnexpandedSourceTests
{
    private static MacroTestResult Expand(string source) =>
        MacroTest.Expand(source, new MacroTestOptions { References = { typeof(M).Assembly } });

    // 4 lines; when the next """ block is appended, its first line is line 5.
    private const string Header = """
        using Demo.Macros;
        using static Demo.Macros.M;
        namespace Demo.App;
        public class SaveContainer { public static void Register<T>(string k, System.Func<T, object?> g, System.Action<T, object?> s) { } }
        """;

    [Fact]
    public void Instance_member_in_a_field_initializer_is_reported()
    {
        // Before expansion: a field initializer referencing the instance member Backing → CS0236; after expansion Backing moves into the getter/setter and compiles.
        var r = Expand(Header + """

            public class Player
            {
                public int Backing;
                public int Hp = @GETSET(Backing);
            }
            """);

        var d = r.AssertError("HMC020", contains: "CS0236", line: 8);
        Assert.Contains("only valid after @GETSET is expanded", d.Message);
    }

    [Fact]
    public void Non_constant_attribute_macro_argument_is_reported()
    {
        var r = Expand(Header + """

            public class Player
            {
                static string Key() => "gold";
                [@Save(Key())] public int Gold = 1;
            }
            """);

        r.AssertError("HMC020", contains: "only valid after [@Save] is expanded", line: 8);
    }

    [Fact]
    public void Valid_unexpanded_code_passes()
    {
        Expand(Header + """

            public static class Data { public static int Hp = 1; }
            public class Player
            {
                public int Hp = @GETSET(Data.Hp);   // static member: valid before expansion too
                public int Gold = @LOGGED(10);
            }
            """).AssertSuccess();
    }
}
