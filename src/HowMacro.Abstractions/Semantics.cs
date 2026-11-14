using System;
using System.Collections.Generic;
using System.Linq;

namespace HowMacro
{
    // =====================================================================================
    // Semantic information model: type and member information macros can query during expansion.
    //
    // - No Roslyn dependency: macro libraries only reference HowMacro.Abstractions (usable from regular .NET and Unity).
    // - It shows the IDE's view, i.e. the source before expansion: `public int Hp = @GETSET(...)` is an int field here,
    //   and ExpandedBy says it will be expanded by @GETSET. This matches the core principle: macros don't change names or types.
    // - Everything is computed lazily, so parts you don't use cost nothing.
    // =====================================================================================

    public enum MacroTypeKind { Class, Struct, Interface, Enum, Delegate, Array, TypeParameter, Pointer, Other }

    public enum MacroMemberKind { Field, Property, Method, Event, Constructor, Local, Parameter }

    /// <summary>A type (class, struct, enum, array, type parameter, ...).</summary>
    public abstract class MacroType
    {
        /// <summary>Simple name: <c>Player</c>, <c>List</c>, <c>Int32</c>.</summary>
        public abstract string Name { get; }

        /// <summary>Display name: <c>Demo.App.Player</c>, <c>int</c>, <c>System.Collections.Generic.List&lt;int&gt;</c>.
        /// Same format as <see cref="Macro.ContainingType"/> and <see cref="MacroArg.Type"/>.</summary>
        public abstract string FullName { get; }

        /// <summary>Fully qualified form for generated code: <c>global::Demo.App.Player</c>, which can't clash with names at the call site.</summary>
        public abstract string CodeName { get; }

        public abstract string? Namespace { get; }
        public abstract MacroTypeKind Kind { get; }
        public abstract bool IsValueType { get; }
        public abstract bool IsStatic { get; }
        public abstract bool IsAbstract { get; }
        public abstract bool IsSealed { get; }
        public abstract bool IsRecord { get; }

        /// <summary><c>int?</c> (Nullable&lt;T&gt;) or a reference type annotated with <c>?</c>.</summary>
        public abstract bool IsNullable { get; }

        /// <summary>Generic type arguments: <c>Dictionary&lt;string, int&gt;</c> → string, int.</summary>
        public abstract IReadOnlyList<MacroType> TypeArguments { get; }

        /// <summary>The element type of an array, the T of <c>Nullable&lt;T&gt;</c>, or a pointer's pointed-at type; null otherwise.</summary>
        public abstract MacroType? ElementType { get; }

        public abstract MacroType? BaseType { get; }

        /// <summary>All implemented interfaces (including inherited ones).</summary>
        public abstract IReadOnlyList<MacroType> Interfaces { get; }

        /// <summary>Members explicitly declared in source (fields, properties, methods, events, constructors); excludes compiler-generated and inherited members.</summary>
        public abstract IReadOnlyList<MacroMember> Members { get; }

        public abstract IReadOnlyList<MacroAttributeInfo> Attributes { get; }

        /// <summary>All member names of an enum; empty if not an enum.</summary>
        public abstract IReadOnlyList<string> EnumValues { get; }

        /// <summary>Source location of the type declaration; null for types from referenced assemblies.</summary>
        public abstract string? FilePath { get; }
        public abstract int? Line { get; }

        // ---- Convenience queries ----

        public IEnumerable<MacroMember> Fields => Members.Where(m => m.Kind == MacroMemberKind.Field);
        public IEnumerable<MacroMember> Properties => Members.Where(m => m.Kind == MacroMemberKind.Property);
        public IEnumerable<MacroMember> Methods => Members.Where(m => m.Kind == MacroMemberKind.Method);

        /// <summary>Finds a member by name (the first one for overloads).</summary>
        public MacroMember? Member(string name) => Members.FirstOrDefault(m => m.Name == name);

        public IEnumerable<MacroMember> MembersNamed(string name) => Members.Where(m => m.Name == name);

        /// <summary>Whether this type is, inherits from, or implements <paramref name="fullName"/> (compared by <see cref="FullName"/>).</summary>
        public bool Is(string fullName)
        {
            for (var t = this; t != null; t = t.BaseType)
                if (t.FullName == fullName) return true;
            return Interfaces.Any(i => i.FullName == fullName);
        }

        public bool HasAttribute(string name) => Attributes.Any(a => a.Is(name));

        public override string ToString() => FullName;
    }

    /// <summary>A member or local symbol (field, property, method, event, constructor, local variable, parameter).</summary>
    public abstract class MacroMember
    {
        public abstract string Name { get; }
        public abstract MacroMemberKind Kind { get; }

        /// <summary>Type display name; the return type for methods.</summary>
        public abstract string Type { get; }

        public abstract MacroType? TypeInfo { get; }

        /// <summary>The containing type; for local variables and parameters, the type of the containing method.</summary>
        public abstract MacroType? ContainingType { get; }

        /// <summary><c>public</c>, <c>private</c>, <c>protected</c>, <c>internal</c>, <c>protected internal</c>,
        /// <c>private protected</c>; an empty string for local variables and parameters.</summary>
        public abstract string Accessibility { get; }

        public abstract bool IsStatic { get; }
        public abstract bool IsConst { get; }

        /// <summary>readonly fields, properties without a setter, const, foreach/using variables.</summary>
        public abstract bool IsReadOnly { get; }

        /// <summary>A property with only an init accessor.</summary>
        public abstract bool IsInitOnly { get; }

        public abstract bool CanRead { get; }

        /// <summary>Whether ordinary code can assign it (false for init-only properties).</summary>
        public abstract bool CanWrite { get; }

        public abstract bool IsVirtual { get; }
        public abstract bool IsAbstract { get; }
        public abstract bool IsOverride { get; }

        /// <summary>Parameters of methods, constructors and indexers.</summary>
        public abstract IReadOnlyList<MacroParameter> Parameters { get; }

        public abstract bool HasConstantValue { get; }
        public abstract object? ConstantValue { get; }

        /// <summary>Attributes on the member, including attribute macros (<see cref="MacroAttributeInfo.IsMacro"/>).</summary>
        public abstract IReadOnlyList<MacroAttributeInfo> Attributes { get; }

        /// <summary>
        /// The Member method macro that will expand this field's declaration, e.g. "LOGGED" for <c>public int Gold = @LOGGED(100)</c>; null otherwise.
        /// Macros see the IDE's view (it is still a field here); use this when you need to know what it will actually become.
        /// </summary>
        public abstract string? ExpandedBy { get; }

        /// <summary>Source location of the declaration; null for members from referenced assemblies.</summary>
        public abstract string? FilePath { get; }
        public abstract int? Line { get; }

        // ---- Convenience queries ----

        public bool IsPublic => Accessibility == "public";
        public bool IsFieldOrProperty => Kind == MacroMemberKind.Field || Kind == MacroMemberKind.Property;
        public bool HasAttribute(string name) => Attributes.Any(a => a.Is(name));
        public MacroAttributeInfo? Attribute(string name) => Attributes.FirstOrDefault(a => a.Is(name));

        public override string ToString() => $"{Kind} {Type} {Name}";
    }

    public sealed class MacroParameter
    {
        private readonly Lazy<MacroType?> _typeInfo;

        public MacroParameter(string name, string type, Func<MacroType?> typeInfo, string refKind, bool isParams, bool hasDefault, object? defaultValue)
        {
            Name = name;
            Type = type;
            _typeInfo = new Lazy<MacroType?>(typeInfo);
            RefKind = refKind;
            IsParams = isParams;
            HasDefault = hasDefault;
            DefaultValue = defaultValue;
        }

        public string Name { get; }
        public string Type { get; }
        public MacroType? TypeInfo => _typeInfo.Value;

        /// <summary>"", "ref", "out", "in", "ref readonly".</summary>
        public string RefKind { get; }

        public bool IsParams { get; }
        public bool HasDefault { get; }
        public object? DefaultValue { get; }

        public override string ToString() => $"{RefKind} {Type} {Name}".Trim();
    }

    /// <summary>One use of an attribute: <c>[Save("lv")]</c> → Name="Save", Args=["lv"].</summary>
    public sealed class MacroAttributeInfo
    {
        public MacroAttributeInfo(string name, string fullName, bool isMacro, IReadOnlyList<object?> args, IReadOnlyDictionary<string, object?> namedArgs)
        {
            Name = name;
            FullName = fullName;
            IsMacro = isMacro;
            Args = args;
            NamedArgs = namedArgs;
        }

        /// <summary>Simple name of the attribute class: <c>SerializeField</c>, <c>Save</c>.</summary>
        public string Name { get; }

        public string FullName { get; }

        /// <summary>Whether it is an attribute macro.</summary>
        public bool IsMacro { get; }

        /// <summary>Constructor arguments (constant values; array arguments are object?[]).</summary>
        public IReadOnlyList<object?> Args { get; }

        /// <summary>Named arguments: <c>[X(Key = "a")]</c> → Key="a".</summary>
        public IReadOnlyDictionary<string, object?> NamedArgs { get; }

        /// <summary>Matches by simple or full name; the Attribute suffix may be omitted: <c>Is("Obsolete")</c> matches ObsoleteAttribute.</summary>
        public bool Is(string name) =>
            Name == name || FullName == name || Name == name + "Attribute" || FullName == name + "Attribute";

        public override string ToString() => FullName;
    }
}
