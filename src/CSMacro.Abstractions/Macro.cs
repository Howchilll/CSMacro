using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace CSMacro
{
    /// <summary>方法宏调用可以出现的位置。</summary>
    [Flags]
    public enum MacroSite
    {
        /// <summary>只替换调用表达式本身：<c>var x = @SQUARE(3);</c></summary>
        Expression = 1,

        /// <summary>替换整条语句（结果包进 <c>{ }</c>）：<c>@LOG(x);</c></summary>
        Statement = 2,

        /// <summary>替换整条字段声明，结果必须是同名同类型的字段或属性：<c>public int Hp = @GETSET(...);</c></summary>
        Member = 4,
    }

    /// <summary>特性宏可以标记的声明。</summary>
    [Flags]
    public enum MacroTarget
    {
        Field = 1,
        Property = 2,
        Method = 4,
        Type = 8,
        All = Field | Property | Method | Type,
    }

    /// <summary>
    /// 把一个方法标记为方法宏：在调用处原地展开。
    /// 方法签名供 IDE 使用（补全、类型检查、跳转），方法体是展开逻辑，只在编译期执行，运行时永远不会被调用。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class MacroAttribute : Attribute
    {
        public MacroAttribute(MacroSite sites = MacroSite.Expression) => Sites = sites;
        public MacroSite Sites { get; }
    }

    /// <summary>
    /// 把一个特性类标记为特性宏：<c>[@Save] public int Gold;</c>
    /// 特性宏不改它标记的声明，只产出辅助代码片段（<see cref="Macro.Fragment"/> / <see cref="Macro.Startup"/>）。
    /// 构造函数体是展开逻辑，只在编译期执行；展开后特性本身从源码中删除，运行时不存在。
    /// 所有特性宏先于所有方法宏执行。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AttributeMacroAttribute : Attribute
    {
        public AttributeMacroAttribute(MacroTarget targets = MacroTarget.All) => Targets = targets;
        public MacroTarget Targets { get; }
    }

    /// <summary>调用处的一个实参。</summary>
    public sealed class MacroArg
    {
        private readonly Lazy<MacroMember?> _symbol;
        private readonly Lazy<MacroType?> _typeInfo;

        public MacroArg(string text, string? type, bool hasConstant, object? constant,
            Func<MacroMember?>? symbol = null, Func<MacroType?>? typeInfo = null)
        {
            Text = text;
            Type = type;
            HasConstant = hasConstant;
            Constant = constant;
            _symbol = new Lazy<MacroMember?>(symbol ?? (() => null));
            _typeInfo = new Lazy<MacroType?>(typeInfo ?? (() => null));
        }

        /// <summary>实参指向的符号：<c>DataCenter.PlayerData.Hp</c> → 字段 Hp；表达式不指向符号时为 null。</summary>
        public MacroMember? Symbol => _symbol.Value;

        /// <summary>实参表达式的类型信息。</summary>
        public MacroType? TypeInfo => _typeInfo.Value;

        /// <summary>实参的源码原文，例如 <c>DataCenter.PlayerData.Hp</c>。</summary>
        public string Text { get; }

        /// <summary>编译器推断出的类型，例如 <c>int</c>；无法推断时为 null。</summary>
        public string? Type { get; }

        public bool HasConstant { get; }

        /// <summary>实参是编译期常量时的值，例如 <c>"Atk"</c> 对应字符串 Atk。</summary>
        public object? Constant { get; }

        public override string ToString() => Text;
    }

    /// <summary>特性宏产出的一个代码片段，连同它所标记的声明的信息。</summary>
    public sealed class MacroFragment
    {
        public MacroFragment(string channel, string code, string macroName, string? containingType,
            string? memberName, string? memberType, string filePath, int line, MacroMember? declaration = null)
        {
            Declaration = declaration;
            Channel = channel;
            Code = code;
            MacroName = macroName;
            ContainingType = containingType;
            MemberName = memberName;
            MemberType = memberType;
            FilePath = filePath;
            Line = line;
        }

        public string Channel { get; }
        public string Code { get; }

        /// <summary>产出这个片段的特性宏名字。</summary>
        public string MacroName { get; }

        /// <summary>被标记声明所在的类型全名；标记在类型上时就是该类型本身。</summary>
        public string? ContainingType { get; }

        /// <summary>被标记的成员名（标记在类型上时为类型名）。</summary>
        public string? MemberName { get; }

        /// <summary>被标记成员的类型（方法为返回类型，类型上为 null）。</summary>
        public string? MemberType { get; }

        public string FilePath { get; }
        public int Line { get; }

        /// <summary>被标记成员的语义信息；标记在类型上时为 null。</summary>
        public MacroMember? Declaration { get; }

        public override string ToString() => $"{ContainingType}.{MemberName} [{Channel}] {Code}";
    }

    public enum MacroMessageSeverity { Warning, Error }

    public sealed class MacroMessage
    {
        public MacroMessage(MacroMessageSeverity severity, string text)
        {
            Severity = severity;
            Text = text;
        }

        public MacroMessageSeverity Severity { get; }
        public string Text { get; }
    }

    /// <summary>由展开器为每一次宏执行填充的上下文。宏作者通过 <see cref="Macro"/> 访问。</summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public sealed class MacroContext
    {
        public string MacroName { get; set; } = "";
        public MacroSite Site { get; set; }

        /// <summary>特性宏所标记的声明种类；方法宏为 null。</summary>
        public MacroTarget? Target { get; set; }

        public string FilePath { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public IReadOnlyList<MacroArg> Args { get; set; } = Array.Empty<MacroArg>();
        public string? MemberName { get; set; }
        public string? MemberType { get; set; }
        public string? Modifiers { get; set; }
        public string? Attributes { get; set; }
        public string? ContainingType { get; set; }
        public IReadOnlyCollection<string> Defines { get; set; } = Array.Empty<string>();

        public MacroMember? Declaration { get; set; }
        public MacroType? DeclaredTypeInfo { get; set; }
        public MacroType? ContainingTypeInfo { get; set; }
        public Func<string, MacroType?>? TypeLookup { get; set; }

        /// <summary>方法宏阶段：所有特性宏产出的片段，按频道分组。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MacroFragment>>? AvailableFragments { get; set; }

        public StringBuilder Output { get; } = new StringBuilder();
        public List<MacroFragment> ProducedFragments { get; } = new List<MacroFragment>();
        public List<MacroFragment> ProducedStartup { get; } = new List<MacroFragment>();
        public List<MacroMessage> Messages { get; } = new List<MacroMessage>();
    }

    /// <summary>宏体里使用的 API：读取调用处信息，输出代码或片段。</summary>
    public static class Macro
    {
        [ThreadStatic] internal static MacroContext? Current;

        private static MacroContext Ctx => Current ?? throw new InvalidOperationException(
            "CSMacro: a macro was executed at runtime. Macro bodies only run during macro expansion — " +
            "make sure the CSMacro build step is enabled for the project that uses it.");

        // ------------------------------------------------------------------ 调用处信息

        /// <summary>当前是否处在宏展开过程中。</summary>
        public static bool IsExpanding => Current != null;

        /// <summary>正在执行的宏的名字。</summary>
        public static string Self => Ctx.MacroName;

        /// <summary>当前执行的是特性宏（true）还是方法宏（false）。</summary>
        public static bool IsAttributeMacro => Ctx.Target != null;

        /// <summary>方法宏：这次调用实际匹配到的位置类型。</summary>
        public static MacroSite Site => Ctx.Site;

        /// <summary>特性宏：被标记的声明种类。</summary>
        public static MacroTarget Target => Ctx.Target
            ?? throw new InvalidOperationException($"Macro.Target is only available in attribute macros; @{Self} is a method macro");

        public static string File => Ctx.FilePath;
        public static string FileName => System.IO.Path.GetFileName(Ctx.FilePath);

        /// <summary>调用（或特性）所在的源码行号（从 1 开始）。</summary>
        public static int Line => Ctx.Line;

        public static IReadOnlyList<MacroArg> Args => Ctx.Args;
        public static int ArgCount => Ctx.Args.Count;

        /// <summary>第 i 个实参的源码原文。</summary>
        public static string Arg(int i) => GetArg(i).Text;

        /// <summary>第 i 个实参的类型（显示名）。</summary>
        public static string? ArgType(int i) => GetArg(i).Type;

        /// <summary>第 i 个实参的编译期常量值；不是常量时报错。</summary>
        public static object? ArgValue(int i)
        {
            var a = GetArg(i);
            if (!a.HasConstant)
                throw new InvalidOperationException($"argument {i} of @{Self} must be a compile-time constant, got `{a.Text}`");
            return a.Constant;
        }

        /// <summary>被声明的名字：方法宏中 <c>public int Hp = @M()</c> 的 Hp；特性宏中被标记的成员/类型名。</summary>
        public static string Name => Ctx.MemberName ?? throw NotInDeclaration(nameof(Name));

        /// <summary>被声明的类型：<c>public int Hp = @M()</c> 中的 int；特性宏标记方法时为返回类型。</summary>
        public static string Type => Ctx.MemberType ?? throw NotInDeclaration(nameof(Type));

        /// <summary>声明上的修饰符：<c>public static</c> 等，可能为空字符串。</summary>
        public static string Modifiers => Ctx.Modifiers ?? "";

        /// <summary>字段声明上的普通特性原文（已去掉特性宏），例如 <c>[SerializeField]</c>；没有时为空字符串。</summary>
        public static string Attributes => Ctx.Attributes ?? "";

        /// <summary>所在类型的全名；特性宏标记在类型上时就是该类型本身。</summary>
        public static string ContainingType => Ctx.ContainingType ?? "";

        // ------------------------------------------------------------------ 语义信息（IDE 视角，懒加载）

        /// <summary>第 i 个实参指向的符号：<c>@GETSET(DataCenter.PlayerData.Hp)</c> → 字段 Hp（可查 CanWrite 等）。</summary>
        public static MacroMember? ArgSymbol(int i) => GetArg(i).Symbol;

        /// <summary>第 i 个实参表达式的类型信息（可查成员、特性、基类等）。</summary>
        public static MacroType? ArgTypeInfo(int i) => GetArg(i).TypeInfo;

        /// <summary>
        /// 被声明/被标记的成员：方法宏中 <c>public int Hp = @M()</c> 的 Hp（字段或局部变量）；
        /// 特性宏中被标记的字段、属性、方法。不在声明中或标记在类型上时为 null。
        /// </summary>
        public static MacroMember? Declaration => Ctx.Declaration;

        /// <summary><see cref="Type"/> 的类型信息。</summary>
        public static MacroType? TypeInfo => Ctx.DeclaredTypeInfo;

        /// <summary><see cref="ContainingType"/> 的类型信息；特性宏标记在类型上时就是该类型本身。</summary>
        public static MacroType? ContainingTypeInfo => Ctx.ContainingTypeInfo;

        /// <summary>按元数据名查找任意类型，例如 <c>"Demo.App.Player"</c>、<c>"System.Collections.Generic.List`1"</c>。</summary>
        public static MacroType? FindType(string metadataName) => Ctx.TypeLookup?.Invoke(metadataName);

        /// <summary>当前编译定义了某个预处理符号（如 DEBUG）时返回 true。宏可以据此生成不同的实现。</summary>
        public static bool IsDefined(string symbol) => System.Linq.Enumerable.Contains(Ctx.Defines, symbol);

        // ------------------------------------------------------------------ 方法宏：原地展开

        /// <summary>方法宏：追加要展开成的代码。多次调用会依次拼接；换行会被压成一行以保持行号。</summary>
        public static void Emit(string code)
        {
            if (IsAttributeMacro)
                throw new InvalidOperationException(
                    $"[@{Self}] is an attribute macro: it doesn't expand in place. Use Macro.Fragment(...) or Macro.Startup(...)");
            Ctx.Output.Append(code).Append(' ');
        }

        /// <summary>
        /// 方法宏：读取特性宏产出到某个频道的全部片段（按文件、行排序）。
        /// 所有特性宏都在方法宏之前执行，所以这里拿到的是完整的集合。
        /// </summary>
        public static IReadOnlyList<MacroFragment> Fragments(string channel)
        {
            if (IsAttributeMacro)
                throw new InvalidOperationException(
                    $"[@{Self}]: fragments are read by method macros, which run after all attribute macros; attribute macros can only produce them");
            return Ctx.AvailableFragments != null && Ctx.AvailableFragments.TryGetValue(channel, out var list)
                ? list
                : Array.Empty<MacroFragment>();
        }

        // ------------------------------------------------------------------ 特性宏：产出片段

        /// <summary>
        /// 特性宏：向频道 <paramref name="channel"/> 产出一个片段，供方法宏用 <see cref="Fragments"/> 读取。
        /// 片段自动带上被标记声明的信息（所在类型、名字、类型、位置），<paramref name="code"/> 可以留空。
        /// </summary>
        public static void Fragment(string channel, string code = "") =>
            RequireAttributeMacro(nameof(Fragment)).ProducedFragments.Add(MakeFragment(channel, code));

        /// <summary>
        /// 特性宏：产出一段启动代码（语句）。引擎把所有启动代码汇总成一个独立的辅助脚本，
        /// 程序启动时自动执行（默认用 <c>[ModuleInitializer]</c>），不需要任何手写代码。
        /// 启动代码只能引用源码里已有的名字。
        /// </summary>
        public static void Startup(string code) =>
            RequireAttributeMacro(nameof(Startup)).ProducedStartup.Add(MakeFragment("startup", code));

        // ------------------------------------------------------------------ 通用

        public static void Error(string message) => Ctx.Messages.Add(new MacroMessage(MacroMessageSeverity.Error, message));
        public static void Warning(string message) => Ctx.Messages.Add(new MacroMessage(MacroMessageSeverity.Warning, message));

        /// <summary>把任意文本转成 C# 字符串字面量（带引号、已转义）。</summary>
        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static MacroFragment MakeFragment(string channel, string code) =>
            new MacroFragment(channel, code, Ctx.MacroName, Ctx.ContainingType, Ctx.MemberName, Ctx.MemberType, Ctx.FilePath, Ctx.Line, Ctx.Declaration);

        private static MacroContext RequireAttributeMacro(string api)
        {
            var ctx = Ctx;
            if (ctx.Target == null)
                throw new InvalidOperationException(
                    $"Macro.{api} is only available in attribute macros; @{ctx.MacroName} is a method macro and expands in place with Macro.Emit");
            return ctx;
        }

        private static MacroArg GetArg(int i)
        {
            var args = Ctx.Args;
            if (i < 0 || i >= args.Count)
                throw new ArgumentOutOfRangeException(nameof(i), $"@{Self} was called with {args.Count} argument(s), asked for #{i}");
            return args[i];
        }

        private static Exception NotInDeclaration(string what) => new InvalidOperationException(
            $"Macro.{what} is only available when @{Self} is the initializer of a declaration (e.g. `int x = @{Self}(...)`) or in an attribute macro");
    }
}

namespace CSMacro.Hosting
{
    /// <summary>供展开器宿主使用：在给定上下文中执行一次宏，返回输出的代码。</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class MacroHost
    {
        public static string Run(MacroContext context, Action body)
        {
            var prev = Macro.Current;
            Macro.Current = context;
            try { body(); }
            finally { Macro.Current = prev; }
            return context.Output.ToString();
        }
    }
}
