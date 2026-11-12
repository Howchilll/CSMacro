using System;
using System.Collections.Generic;
using System.Linq;

namespace CSMacro
{
    // =====================================================================================
    // 语义信息模型：宏在展开时可以查询的类型、成员信息。
    //
    // - 不依赖 Roslyn，宏库只需引用 CSMacro.Abstractions（普通 .NET 和 Unity 都能用）。
    // - 看到的是 IDE 视角，也就是展开前的源码：`public int Hp = @GETSET(...)` 在这里是一个 int 字段，
    //   并通过 ExpandedBy 标注它会被 @GETSET 展开。这与核心原则一致：宏不改名字和类型。
    // - 所有信息都是懒加载的，没用到的部分不会产生开销。
    // =====================================================================================

    public enum MacroTypeKind { Class, Struct, Interface, Enum, Delegate, Array, TypeParameter, Pointer, Other }

    public enum MacroMemberKind { Field, Property, Method, Event, Constructor, Local, Parameter }

    /// <summary>一个类型（类、结构、枚举、数组、类型参数……）。</summary>
    public abstract class MacroType
    {
        /// <summary>简单名：<c>Player</c>、<c>List</c>、<c>Int32</c>。</summary>
        public abstract string Name { get; }

        /// <summary>显示名：<c>Demo.App.Player</c>、<c>int</c>、<c>System.Collections.Generic.List&lt;int&gt;</c>。
        /// 与 <see cref="Macro.ContainingType"/>、<see cref="MacroArg.Type"/> 的写法一致。</summary>
        public abstract string FullName { get; }

        /// <summary>生成代码时使用的全限定写法：<c>global::Demo.App.Player</c>，不会和调用处的名字冲突。</summary>
        public abstract string CodeName { get; }

        public abstract string? Namespace { get; }
        public abstract MacroTypeKind Kind { get; }
        public abstract bool IsValueType { get; }
        public abstract bool IsStatic { get; }
        public abstract bool IsAbstract { get; }
        public abstract bool IsSealed { get; }
        public abstract bool IsRecord { get; }

        /// <summary><c>int?</c>（Nullable&lt;T&gt;）或带 <c>?</c> 标注的引用类型。</summary>
        public abstract bool IsNullable { get; }

        /// <summary>泛型实参：<c>Dictionary&lt;string, int&gt;</c> → string、int。</summary>
        public abstract IReadOnlyList<MacroType> TypeArguments { get; }

        /// <summary>数组的元素类型、<c>Nullable&lt;T&gt;</c> 的 T、指针指向的类型；其他为 null。</summary>
        public abstract MacroType? ElementType { get; }

        public abstract MacroType? BaseType { get; }

        /// <summary>实现的全部接口（包括继承来的）。</summary>
        public abstract IReadOnlyList<MacroType> Interfaces { get; }

        /// <summary>源码中显式声明的成员（字段、属性、方法、事件、构造函数），不含编译器生成的成员和继承来的成员。</summary>
        public abstract IReadOnlyList<MacroMember> Members { get; }

        public abstract IReadOnlyList<MacroAttributeInfo> Attributes { get; }

        /// <summary>枚举的全部成员名；不是枚举时为空。</summary>
        public abstract IReadOnlyList<string> EnumValues { get; }

        /// <summary>类型声明所在的源码位置；来自引用程序集时为 null。</summary>
        public abstract string? FilePath { get; }
        public abstract int? Line { get; }

        // ---- 便捷查询 ----

        public IEnumerable<MacroMember> Fields => Members.Where(m => m.Kind == MacroMemberKind.Field);
        public IEnumerable<MacroMember> Properties => Members.Where(m => m.Kind == MacroMemberKind.Property);
        public IEnumerable<MacroMember> Methods => Members.Where(m => m.Kind == MacroMemberKind.Method);

        /// <summary>按名字找成员（同名重载时返回第一个）。</summary>
        public MacroMember? Member(string name) => Members.FirstOrDefault(m => m.Name == name);

        public IEnumerable<MacroMember> MembersNamed(string name) => Members.Where(m => m.Name == name);

        /// <summary>是否就是、继承自或实现了 <paramref name="fullName"/>（按 <see cref="FullName"/> 比较）。</summary>
        public bool Is(string fullName)
        {
            for (var t = this; t != null; t = t.BaseType)
                if (t.FullName == fullName) return true;
            return Interfaces.Any(i => i.FullName == fullName);
        }

        public bool HasAttribute(string name) => Attributes.Any(a => a.Is(name));

        public override string ToString() => FullName;
    }

    /// <summary>一个成员或局部符号（字段、属性、方法、事件、构造函数、局部变量、参数）。</summary>
    public abstract class MacroMember
    {
        public abstract string Name { get; }
        public abstract MacroMemberKind Kind { get; }

        /// <summary>类型显示名；方法为返回类型。</summary>
        public abstract string Type { get; }

        public abstract MacroType? TypeInfo { get; }

        /// <summary>所在类型；局部变量、参数为所在方法的类型。</summary>
        public abstract MacroType? ContainingType { get; }

        /// <summary><c>public</c>、<c>private</c>、<c>protected</c>、<c>internal</c>、<c>protected internal</c>、
        /// <c>private protected</c>；局部变量和参数为空字符串。</summary>
        public abstract string Accessibility { get; }

        public abstract bool IsStatic { get; }
        public abstract bool IsConst { get; }

        /// <summary>readonly 字段、没有 setter 的属性、const、foreach/using 变量。</summary>
        public abstract bool IsReadOnly { get; }

        /// <summary>只有 init 访问器的属性。</summary>
        public abstract bool IsInitOnly { get; }

        public abstract bool CanRead { get; }

        /// <summary>能否在普通代码里赋值（init-only 属性为 false）。</summary>
        public abstract bool CanWrite { get; }

        public abstract bool IsVirtual { get; }
        public abstract bool IsAbstract { get; }
        public abstract bool IsOverride { get; }

        /// <summary>方法、构造函数、索引器的参数。</summary>
        public abstract IReadOnlyList<MacroParameter> Parameters { get; }

        public abstract bool HasConstantValue { get; }
        public abstract object? ConstantValue { get; }

        /// <summary>成员上的特性，包括特性宏（<see cref="MacroAttributeInfo.IsMacro"/>）。</summary>
        public abstract IReadOnlyList<MacroAttributeInfo> Attributes { get; }

        /// <summary>
        /// 这个字段的声明会被哪个 Member 方法宏展开，例如 <c>public int Gold = @LOGGED(100)</c> 返回 "LOGGED"；否则为 null。
        /// 宏看到的是 IDE 视角（它在这里仍是字段），需要知道"实际会变成什么"时用这个判断。
        /// </summary>
        public abstract string? ExpandedBy { get; }

        /// <summary>声明所在的源码位置；来自引用程序集时为 null。</summary>
        public abstract string? FilePath { get; }
        public abstract int? Line { get; }

        // ---- 便捷查询 ----

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

        /// <summary>""、"ref"、"out"、"in"、"ref readonly"。</summary>
        public string RefKind { get; }

        public bool IsParams { get; }
        public bool HasDefault { get; }
        public object? DefaultValue { get; }

        public override string ToString() => $"{RefKind} {Type} {Name}".Trim();
    }

    /// <summary>一个特性的用法：<c>[Save("lv")]</c> → Name="Save"，Args=["lv"]。</summary>
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

        /// <summary>特性类的简单名：<c>SerializeField</c>、<c>Save</c>。</summary>
        public string Name { get; }

        public string FullName { get; }

        /// <summary>是不是特性宏。</summary>
        public bool IsMacro { get; }

        /// <summary>构造函数实参（常量值；数组实参为 object?[]）。</summary>
        public IReadOnlyList<object?> Args { get; }

        /// <summary>命名实参：<c>[X(Key = "a")]</c> → Key="a"。</summary>
        public IReadOnlyDictionary<string, object?> NamedArgs { get; }

        /// <summary>按简单名或全名匹配，可以省略 Attribute 后缀：<c>Is("Obsolete")</c> 匹配 ObsoleteAttribute。</summary>
        public bool Is(string name) =>
            Name == name || FullName == name || Name == name + "Attribute" || FullName == name + "Attribute";

        public override string ToString() => FullName;
    }
}
