using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace HowMacro
{
    /// <summary>Where a method macro call may appear.</summary>
    [Flags]
    public enum MacroSite
    {
        /// <summary>Replaces only the call expression itself: <c>var x = @SQUARE(3);</c></summary>
        Expression = 1,

        /// <summary>Replaces the whole statement (the result is wrapped in <c>{ }</c>): <c>@LOG(x);</c></summary>
        Statement = 2,

        /// <summary>Replaces the whole field declaration; the result must be a field or property with the same name and type: <c>public int Hp = @GETSET(...);</c></summary>
        Member = 4,
    }

    /// <summary>Declarations an attribute macro can be applied to.</summary>
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
    /// Marks a method as a method macro: it expands in place at the call site.
    /// The signature is for the IDE (completion, type checking, go-to-definition); the body is the expansion logic, which runs only at compile time and is never called at runtime.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class MacroAttribute : Attribute
    {
        public MacroAttribute(MacroSite sites = MacroSite.Expression) => Sites = sites;
        public MacroSite Sites { get; }
    }

    /// <summary>
    /// Marks an attribute class as an attribute macro: <c>[@Save] public int Gold;</c>
    /// An attribute macro doesn't change the declaration it marks; it only produces fragments of code (<see cref="Macro.Fragment"/> / <see cref="Macro.Startup"/>).
    /// The constructor body is the expansion logic and runs only at compile time; after expansion the attribute is removed from the source and doesn't exist at runtime.
    /// All attribute macros run before any method macro.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AttributeMacroAttribute : Attribute
    {
        public AttributeMacroAttribute(MacroTarget targets = MacroTarget.All) => Targets = targets;
        public MacroTarget Targets { get; }
    }

    /// <summary>
    /// Marks a static method as a region macro: it processes the members between <c>#region @NAME</c> and <c>#endregion</c>.
    /// The method body is the expansion logic: call <see cref="MacroRegionMember.Replace"/> per member, or replace the whole text with <see cref="Macro.RegionText"/> + <see cref="Macro.Emit"/>.
    /// Note: the text after <c>#region</c> is just a comment to the IDE, so region macros get no completion or go-to-definition; a misspelled name is reported at expansion time.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class RegionMacroAttribute : Attribute
    {
    }

    /// <summary>A member wrapped by a region macro. Members that don't call <see cref="Replace"/> stay unchanged.</summary>
    public sealed class MacroRegionMember
    {
        private readonly string? _cannotReplace;

        public MacroRegionMember(string name, MacroMemberKind kind, string type, string modifiers, string attributes,
            string? initializer, string source, int line, MacroMember? declaration, string? expandedBy, string? cannotReplace)
        {
            Name = name;
            Kind = kind;
            Type = type;
            Modifiers = modifiers;
            Attributes = attributes;
            Initializer = initializer;
            Source = source;
            Line = line;
            Declaration = declaration;
            ExpandedBy = expandedBy;
            _cannotReplace = cannotReplace;
        }

        public string Name { get; }
        public MacroMemberKind Kind { get; }

        /// <summary>The declared type (the return type for methods).</summary>
        public string Type { get; }

        public string Modifiers { get; }

        /// <summary>The source text of the member's regular attributes (attribute macros removed).</summary>
        public string Attributes { get; }

        /// <summary>The initializer's source text: <c>public int Gold = 100;</c> → <c>100</c>; null if there is none.</summary>
        public string? Initializer { get; }

        /// <summary>The member's source text.</summary>
        public string Source { get; }

        public int Line { get; }

        /// <summary>Semantic information about the member.</summary>
        public MacroMember? Declaration { get; }

        /// <summary>The Member method macro that already expands this member (e.g. "GETSET"); region macros should usually skip it.</summary>
        public string? ExpandedBy { get; }

        public bool IsField => Kind == MacroMemberKind.Field;
        public bool IsProperty => Kind == MacroMemberKind.Property;

        /// <summary>Whether it can be replaced: only single-variable fields and properties can, and the member must not already contain a macro (macros can't contain macros).</summary>
        public bool CanReplace => _cannotReplace == null;

        /// <summary>The replacement code; null means unchanged.</summary>
        public string? Replacement { get; private set; }

        /// <summary>
        /// Replaces this member with <paramref name="code"/>. The result must still be a field or property with the same name and type (core principle),
        /// and stays on the member's original line.
        /// </summary>
        public void Replace(string code)
        {
            if (_cannotReplace != null) throw new InvalidOperationException($"can't replace {Name}: {_cannotReplace}");
            Replacement = code;
        }

        /// <summary>Undoes the replacement and keeps the member unchanged.</summary>
        public void Keep() => Replacement = null;

        public override string ToString() => Source;
    }

    /// <summary>One argument at a call site.</summary>
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

        /// <summary>The symbol the argument refers to: <c>DataCenter.PlayerData.Hp</c> → field Hp; null when the expression doesn't refer to a symbol.</summary>
        public MacroMember? Symbol => _symbol.Value;

        /// <summary>Type information for the argument expression.</summary>
        public MacroType? TypeInfo => _typeInfo.Value;

        /// <summary>The argument's source text, e.g. <c>DataCenter.PlayerData.Hp</c>.</summary>
        public string Text { get; }

        /// <summary>The type the compiler inferred, e.g. <c>int</c>; null if it can't be inferred.</summary>
        public string? Type { get; }

        public bool HasConstant { get; }

        /// <summary>The value when the argument is a compile-time constant, e.g. the string Atk for <c>"Atk"</c>.</summary>
        public object? Constant { get; }

        public override string ToString() => Text;
    }

    /// <summary>A code fragment produced by an attribute macro, together with information about the declaration it marks.</summary>
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

        /// <summary>Name of the attribute macro that produced this fragment.</summary>
        public string MacroName { get; }

        /// <summary>Full name of the type containing the marked declaration; the type itself when a type is marked.</summary>
        public string? ContainingType { get; }

        /// <summary>Name of the marked member (the type name when a type is marked).</summary>
        public string? MemberName { get; }

        /// <summary>Type of the marked member (the return type for methods; null for types).</summary>
        public string? MemberType { get; }

        public string FilePath { get; }
        public int Line { get; }

        /// <summary>Semantic information about the marked member; null when a type is marked.</summary>
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

    /// <summary>The context the expander fills in for each macro run. Macro authors access it through <see cref="Macro"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public sealed class MacroContext
    {
        public string MacroName { get; set; } = "";
        public MacroSite Site { get; set; }

        /// <summary>Kind of declaration an attribute macro marks; null for method macros.</summary>
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

        /// <summary>Method macro phase: all fragments produced by attribute macros, grouped by channel.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MacroFragment>>? AvailableFragments { get; set; }

        /// <summary>Region macros: the wrapped members; null for other macros.</summary>
        public IReadOnlyList<MacroRegionMember>? RegionMembers { get; set; }

        /// <summary>Region macros: the source text between #region and #endregion.</summary>
        public string? RegionText { get; set; }

        public StringBuilder Output { get; } = new StringBuilder();
        public List<MacroFragment> ProducedFragments { get; } = new List<MacroFragment>();
        public List<MacroFragment> ProducedStartup { get; } = new List<MacroFragment>();
        public List<MacroMessage> Messages { get; } = new List<MacroMessage>();
    }

    /// <summary>The API used inside macro bodies: read information about the call site, emit code or fragments.</summary>
    public static class Macro
    {
        [ThreadStatic] internal static MacroContext? Current;

        private static MacroContext Ctx => Current ?? throw new InvalidOperationException(
            "HowMacro: a macro was executed at runtime. Macro bodies only run during macro expansion — " +
            "make sure the HowMacro build step is enabled for the project that uses it.");

        // ------------------------------------------------------------------ Call site information

        /// <summary>Whether a macro expansion is currently running.</summary>
        public static bool IsExpanding => Current != null;

        /// <summary>Name of the macro being run.</summary>
        public static string Self => Ctx.MacroName;

        /// <summary>Whether the running macro is an attribute macro (true) or a method macro (false).</summary>
        public static bool IsAttributeMacro => Ctx.Target != null;

        /// <summary>Whether the running macro is a region macro (<c>#region @NAME</c>).</summary>
        public static bool IsRegionMacro => Ctx.RegionMembers != null;

        /// <summary>Region macros: the wrapped members, in order. Call <see cref="MacroRegionMember.Replace"/> per member; members you don't replace stay unchanged.</summary>
        public static IReadOnlyList<MacroRegionMember> RegionMembers => Ctx.RegionMembers
            ?? throw new InvalidOperationException($"Macro.RegionMembers is only available in region macros; @{Self} is not one");

        /// <summary>
        /// Region macros: the source text between #region and #endregion, for whole-text replacement: emit the replaced text with <see cref="Emit"/>.
        /// The output can't have more lines than the original (or every following line number would shift); missing lines are padded.
        /// Use either this or per-member replacement, not both.
        /// </summary>
        public static string RegionText => Ctx.RegionText
            ?? throw new InvalidOperationException($"Macro.RegionText is only available in region macros; @{Self} is not one");

        /// <summary>Method macros: the site this call actually matched.</summary>
        public static MacroSite Site => Ctx.Site;

        /// <summary>Attribute macros: the kind of the marked declaration.</summary>
        public static MacroTarget Target => Ctx.Target
            ?? throw new InvalidOperationException($"Macro.Target is only available in attribute macros; @{Self} is a method macro");

        public static string File => Ctx.FilePath;
        public static string FileName => System.IO.Path.GetFileName(Ctx.FilePath);

        /// <summary>Source line of the call (or attribute), starting at 1.</summary>
        public static int Line => Ctx.Line;

        public static IReadOnlyList<MacroArg> Args => Ctx.Args;
        public static int ArgCount => Ctx.Args.Count;

        /// <summary>Source text of argument i.</summary>
        public static string Arg(int i) => GetArg(i).Text;

        /// <summary>Type of argument i (display name).</summary>
        public static string? ArgType(int i) => GetArg(i).Type;

        /// <summary>Compile-time constant value of argument i; reports an error if it isn't a constant.</summary>
        public static object? ArgValue(int i)
        {
            var a = GetArg(i);
            if (!a.HasConstant)
                throw new InvalidOperationException($"argument {i} of @{Self} must be a compile-time constant, got `{a.Text}`");
            return a.Constant;
        }

        /// <summary>The declared name: Hp in <c>public int Hp = @M()</c> for method macros; the marked member or type name for attribute macros.</summary>
        public static string Name => Ctx.MemberName ?? throw NotInDeclaration(nameof(Name));

        /// <summary>The declared type: int in <c>public int Hp = @M()</c>; the return type when an attribute macro marks a method.</summary>
        public static string Type => Ctx.MemberType ?? throw NotInDeclaration(nameof(Type));

        /// <summary>Modifiers on the declaration, such as <c>public static</c>; may be an empty string.</summary>
        public static string Modifiers => Ctx.Modifiers ?? "";

        /// <summary>Source text of the field's regular attributes (attribute macros removed), e.g. <c>[SerializeField]</c>; empty string if none.</summary>
        public static string Attributes => Ctx.Attributes ?? "";

        /// <summary>Full name of the containing type; the type itself when an attribute macro marks a type.</summary>
        public static string ContainingType => Ctx.ContainingType ?? "";

        // ------------------------------------------------------------------ Semantic information (the IDE's view, computed lazily)

        /// <summary>The symbol argument i refers to: <c>@GETSET(DataCenter.PlayerData.Hp)</c> → field Hp (check CanWrite and so on).</summary>
        public static MacroMember? ArgSymbol(int i) => GetArg(i).Symbol;

        /// <summary>Type information for argument i (members, attributes, base type and so on).</summary>
        public static MacroType? ArgTypeInfo(int i) => GetArg(i).TypeInfo;

        /// <summary>
        /// The declared or marked member: Hp in <c>public int Hp = @M()</c> for method macros (a field or local variable);
        /// the marked field, property or method for attribute macros. Null outside a declaration or when a type is marked.
        /// </summary>
        public static MacroMember? Declaration => Ctx.Declaration;

        /// <summary>Type information for <see cref="Type"/>.</summary>
        public static MacroType? TypeInfo => Ctx.DeclaredTypeInfo;

        /// <summary>Type information for <see cref="ContainingType"/>; the type itself when an attribute macro marks a type.</summary>
        public static MacroType? ContainingTypeInfo => Ctx.ContainingTypeInfo;

        /// <summary>Looks up any type by metadata name, e.g. <c>"Demo.App.Player"</c> or <c>"System.Collections.Generic.List`1"</c>.</summary>
        public static MacroType? FindType(string metadataName) => Ctx.TypeLookup?.Invoke(metadataName);

        /// <summary>True when the current compilation defines a preprocessor symbol (such as DEBUG). Macros can generate different implementations based on it.</summary>
        public static bool IsDefined(string symbol) => System.Linq.Enumerable.Contains(Ctx.Defines, symbol);

        // ------------------------------------------------------------------ Method macros: expand in place

        /// <summary>Method macros: appends code to expand into. Multiple calls are concatenated; line breaks are folded onto one line to keep line numbers.</summary>
        public static void Emit(string code)
        {
            if (IsAttributeMacro)
                throw new InvalidOperationException(
                    $"[@{Self}] is an attribute macro: it doesn't expand in place. Use Macro.Fragment(...) or Macro.Startup(...)");
            Ctx.Output.Append(code).Append(' ');
        }

        /// <summary>
        /// Method macros: reads all fragments attribute macros produced on a channel (sorted by file and line).
        /// All attribute macros run before method macros, so the set is complete.
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

        // ------------------------------------------------------------------ Attribute macros: produce fragments

        /// <summary>
        /// Attribute macros: produces a fragment on <paramref name="channel"/>, for method macros to read with <see cref="Fragments"/>.
        /// The fragment automatically carries information about the marked declaration (containing type, name, type, location); <paramref name="code"/> may be empty.
        /// </summary>
        public static void Fragment(string channel, string code = "") =>
            RequireAttributeMacro(nameof(Fragment)).ProducedFragments.Add(MakeFragment(channel, code));

        /// <summary>
        /// Attribute macros: produces startup code (statements). The engine collects all startup code into a separate generated startup script
        /// that runs automatically when the program starts (via <c>[ModuleInitializer]</c> by default), with no hand-written code.
        /// Startup code may only reference names that already exist in the source.
        /// </summary>
        public static void Startup(string code) =>
            RequireAttributeMacro(nameof(Startup)).ProducedStartup.Add(MakeFragment("startup", code));

        // ------------------------------------------------------------------ General

        public static void Error(string message) => Ctx.Messages.Add(new MacroMessage(MacroMessageSeverity.Error, message));
        public static void Warning(string message) => Ctx.Messages.Add(new MacroMessage(MacroMessageSeverity.Warning, message));

        /// <summary>Turns any text into a C# string literal (quoted and escaped).</summary>
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

namespace HowMacro.Hosting
{
    /// <summary>For expansion hosts: runs a macro once in the given context and returns the emitted code.</summary>
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
