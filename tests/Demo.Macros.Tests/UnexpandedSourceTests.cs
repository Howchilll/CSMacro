using CSMacro.Testing;
using Xunit;

namespace Demo.Macros.Tests;

/// <summary>
/// 核心原则兜底（CSM020）：宏调用处的代码在展开前就必须是合法 C#。
/// 只有展开后才合法的写法（IDE 会一直标红）要在构建时报错，而不是悄悄通过。
/// </summary>
public class UnexpandedSourceTests
{
    private static MacroTestResult Expand(string source) =>
        MacroTest.Expand(source, new MacroTestOptions { References = { typeof(M).Assembly } });

    // 占 4 行；拼上 """ 开头的下一段后，那一段的第一行就是第 5 行。
    private const string Header = """
        using Demo.Macros;
        using static Demo.Macros.M;
        namespace Demo.App;
        public class SaveContainer { public static void Register<T>(string k, System.Func<T, object?> g, System.Action<T, object?> s) { } }
        """;

    [Fact]
    public void Instance_member_in_a_field_initializer_is_reported()
    {
        // 展开前：字段初始值引用实例成员 Backing → CS0236；展开后 Backing 进了 getter/setter，能编译。
        var r = Expand(Header + """

            public class Player
            {
                public int Backing;
                public int Hp = @GETSET(Backing);
            }
            """);

        var d = r.AssertError("CSM020", contains: "CS0236", line: 8);
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

        r.AssertError("CSM020", contains: "only valid after [@Save] is expanded", line: 8);
    }

    [Fact]
    public void Valid_unexpanded_code_passes()
    {
        Expand(Header + """

            public static class Data { public static int Hp = 1; }
            public class Player
            {
                public int Hp = @GETSET(Data.Hp);   // 静态成员：展开前也合法
                public int Gold = @LOGGED(10);
            }
            """).AssertSuccess();
    }
}
