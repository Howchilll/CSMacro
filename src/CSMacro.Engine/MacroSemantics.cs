using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CSMacro;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSMacro.Engine
{
    /// <summary>
    /// 用 Roslyn 符号实现 CSMacro 的语义信息模型。基于展开前的编译（IDE 视角），
    /// 同一个符号只包装一次，所有属性都是懒计算的。
    /// </summary>
    internal sealed class MacroSemantics
    {
        private static readonly SymbolDisplayFormat CodeFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private readonly Compilation _compilation;
        private readonly Dictionary<ISymbol, MacroType> _types = new Dictionary<ISymbol, MacroType>(SymbolEqualityComparer.IncludeNullability);
        private readonly Dictionary<ISymbol, MacroMember> _members = new Dictionary<ISymbol, MacroMember>(SymbolEqualityComparer.Default);
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = new Dictionary<SyntaxTree, SemanticModel>();

        public MacroSemantics(Compilation compilation) => _compilation = compilation;

        public MacroType? Type(ITypeSymbol? symbol)
        {
            if (symbol == null || symbol.TypeKind == TypeKind.Error) return null;
            if (!_types.TryGetValue(symbol, out var t)) _types[symbol] = t = new RoslynType(symbol, this);
            return t;
        }

        public MacroMember? Member(ISymbol? symbol)
        {
            switch (symbol)
            {
                case IFieldSymbol _:
                case IPropertySymbol _:
                case IEventSymbol _:
                case ILocalSymbol _:
                case IParameterSymbol _:
                case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary || m.MethodKind == MethodKind.Constructor:
                    if (!_members.TryGetValue(symbol, out var member)) _members[symbol] = member = new RoslynMember(symbol, this);
                    return member;
                default:
                    return null;
            }
        }

        public MacroType? Find(string metadataName) => Type(_compilation.GetTypeByMetadataName(metadataName));

        public static string Display(ITypeSymbol t) => t.ToDisplayString();
        public static string Code(ITypeSymbol t) => t.ToDisplayString(CodeFormat);

        public IReadOnlyList<MacroAttributeInfo> Attributes(ImmutableArray<AttributeData> attributes) =>
            attributes
                .Where(a => a.AttributeClass != null)
                .Select(a => new MacroAttributeInfo(
                    a.AttributeClass!.Name,
                    a.AttributeClass.ToDisplayString(),
                    a.AttributeClass.GetAttributes().Any(x => x.AttributeClass?.ToDisplayString() == "CSMacro.AttributeMacroAttribute"),
                    a.ConstructorArguments.Select(Value).ToArray(),
                    a.NamedArguments.ToDictionary(kv => kv.Key, kv => Value(kv.Value))))
                .ToArray();

        private static object? Value(TypedConstant c) =>
            c.Kind == TypedConstantKind.Array
                ? c.Values.Select(Value).ToArray()
                : c.Kind == TypedConstantKind.Type ? (c.Value as ITypeSymbol)?.ToDisplayString() : c.Value;

        public static (string? Path, int? Line) Location(ISymbol symbol)
        {
            var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc == null) return (null, null);
            var span = loc.GetLineSpan();
            return (span.Path, span.StartLinePosition.Line + 1);
        }

        /// <summary>字段声明的初始值是不是一个 Member 方法宏：<c>public int Gold = @LOGGED(100)</c> → "LOGGED"。</summary>
        public string? ExpandedBy(IFieldSymbol field)
        {
            foreach (var reference in field.DeclaringSyntaxReferences)
            {
                if (!(reference.GetSyntax() is VariableDeclaratorSyntax declarator)) continue;
                if (!(declarator.Initializer?.Value is InvocationExpressionSyntax invocation)) continue;

                var model = Model(declarator.SyntaxTree);
                if (!(model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method)) continue;

                var attr = method.OriginalDefinition.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "CSMacro.MacroAttribute");
                if (attr == null) continue;

                var sites = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int v ? (MacroSite)v : MacroSite.Expression;
                if ((sites & MacroSite.Member) != 0) return method.Name;
            }
            return null;
        }

        private SemanticModel Model(SyntaxTree tree)
        {
            if (!_models.TryGetValue(tree, out var model)) _models[tree] = model = _compilation.GetSemanticModel(tree);
            return model;
        }

        public static string AccessibilityText(Accessibility a) => a switch
        {
            Accessibility.Public => "public",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "",
        };
    }

    internal sealed class RoslynType : MacroType
    {
        private readonly ITypeSymbol _t;
        private readonly MacroSemantics _s;
        private readonly Lazy<IReadOnlyList<MacroMember>> _members;
        private readonly Lazy<IReadOnlyList<MacroAttributeInfo>> _attributes;
        private readonly Lazy<IReadOnlyList<MacroType>> _interfaces;

        public RoslynType(ITypeSymbol t, MacroSemantics s)
        {
            _t = t;
            _s = s;
            _members = new Lazy<IReadOnlyList<MacroMember>>(() => _t.GetMembers()
                .Where(m => !m.IsImplicitlyDeclared)
                .Select(m => _s.Member(m))
                .Where(m => m != null && m.Kind != MacroMemberKind.Local && m.Kind != MacroMemberKind.Parameter)
                .ToArray()!);
            _attributes = new Lazy<IReadOnlyList<MacroAttributeInfo>>(() => _s.Attributes(_t.GetAttributes()));
            _interfaces = new Lazy<IReadOnlyList<MacroType>>(() => _t.AllInterfaces.Select(i => _s.Type(i)!).Where(i => i != null).ToArray());
        }

        public override string Name => _t.Name.Length > 0 ? _t.Name : _t.ToDisplayString();
        public override string FullName => MacroSemantics.Display(_t);
        public override string CodeName => MacroSemantics.Code(_t);
        public override string? Namespace => _t.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

        public override MacroTypeKind Kind => _t.TypeKind switch
        {
            TypeKind.Class => MacroTypeKind.Class,
            TypeKind.Struct => MacroTypeKind.Struct,
            TypeKind.Interface => MacroTypeKind.Interface,
            TypeKind.Enum => MacroTypeKind.Enum,
            TypeKind.Delegate => MacroTypeKind.Delegate,
            TypeKind.Array => MacroTypeKind.Array,
            TypeKind.TypeParameter => MacroTypeKind.TypeParameter,
            TypeKind.Pointer => MacroTypeKind.Pointer,
            _ => MacroTypeKind.Other,
        };

        public override bool IsValueType => _t.IsValueType;
        public override bool IsStatic => _t.IsStatic;
        public override bool IsAbstract => _t.IsAbstract && _t.TypeKind != TypeKind.Interface;
        public override bool IsSealed => _t.IsSealed;
        public override bool IsRecord => _t.IsRecord;

        public override bool IsNullable =>
            _t.NullableAnnotation == NullableAnnotation.Annotated ||
            _t.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        public override IReadOnlyList<MacroType> TypeArguments =>
            _t is INamedTypeSymbol n ? n.TypeArguments.Select(a => _s.Type(a)!).Where(a => a != null).ToArray() : Array.Empty<MacroType>();

        public override MacroType? ElementType => _t switch
        {
            IArrayTypeSymbol a => _s.Type(a.ElementType),
            IPointerTypeSymbol p => _s.Type(p.PointedAtType),
            INamedTypeSymbol n when n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T => _s.Type(n.TypeArguments[0]),
            _ => null,
        };

        public override MacroType? BaseType => _s.Type(_t.BaseType);
        public override IReadOnlyList<MacroType> Interfaces => _interfaces.Value;
        public override IReadOnlyList<MacroMember> Members => _members.Value;
        public override IReadOnlyList<MacroAttributeInfo> Attributes => _attributes.Value;

        public override IReadOnlyList<string> EnumValues =>
            _t.TypeKind == TypeKind.Enum ? _t.GetMembers().OfType<IFieldSymbol>().Select(f => f.Name).ToArray() : Array.Empty<string>();

        public override string? FilePath => MacroSemantics.Location(_t).Path;
        public override int? Line => MacroSemantics.Location(_t).Line;
    }

    internal sealed class RoslynMember : MacroMember
    {
        private readonly ISymbol _m;
        private readonly MacroSemantics _s;
        private readonly Lazy<IReadOnlyList<MacroAttributeInfo>> _attributes;
        private readonly Lazy<string?> _expandedBy;

        public RoslynMember(ISymbol m, MacroSemantics s)
        {
            _m = m;
            _s = s;
            _attributes = new Lazy<IReadOnlyList<MacroAttributeInfo>>(() => _s.Attributes(_m.GetAttributes()));
            _expandedBy = new Lazy<string?>(() => _m is IFieldSymbol f ? _s.ExpandedBy(f) : null);
        }

        private ITypeSymbol? TypeSymbol => _m switch
        {
            IFieldSymbol f => f.Type,
            IPropertySymbol p => p.Type,
            IEventSymbol e => e.Type,
            ILocalSymbol l => l.Type,
            IParameterSymbol p => p.Type,
            IMethodSymbol m => m.ReturnType,
            _ => null,
        };

        public override string Name => _m is IMethodSymbol { MethodKind: MethodKind.Constructor } ? _m.ContainingType.Name : _m.Name;

        public override MacroMemberKind Kind => _m switch
        {
            IFieldSymbol _ => MacroMemberKind.Field,
            IPropertySymbol _ => MacroMemberKind.Property,
            IEventSymbol _ => MacroMemberKind.Event,
            ILocalSymbol _ => MacroMemberKind.Local,
            IParameterSymbol _ => MacroMemberKind.Parameter,
            IMethodSymbol { MethodKind: MethodKind.Constructor } => MacroMemberKind.Constructor,
            _ => MacroMemberKind.Method,
        };

        public override string Type => TypeSymbol is { } t ? MacroSemantics.Display(t) : "";
        public override MacroType? TypeInfo => _s.Type(TypeSymbol);
        public override MacroType? ContainingType => _s.Type(_m.ContainingType);

        public override string Accessibility =>
            _m is ILocalSymbol || _m is IParameterSymbol ? "" : MacroSemantics.AccessibilityText(_m.DeclaredAccessibility);

        public override bool IsStatic => _m.IsStatic;
        public override bool IsConst => _m is IFieldSymbol { IsConst: true } || _m is ILocalSymbol { IsConst: true };

        public override bool IsReadOnly => _m switch
        {
            IFieldSymbol f => f.IsReadOnly || f.IsConst,
            IPropertySymbol p => p.SetMethod == null,
            ILocalSymbol l => l.IsConst || l.IsForEach || l.IsUsing,
            IParameterSymbol p => p.RefKind == RefKind.In,
            _ => false,
        };

        public override bool IsInitOnly => _m is IPropertySymbol { SetMethod: { IsInitOnly: true } };

        public override bool CanRead => _m switch
        {
            IPropertySymbol p => p.GetMethod != null,
            IParameterSymbol p => p.RefKind != RefKind.Out,
            IFieldSymbol _ => true,
            ILocalSymbol _ => true,
            _ => false,
        };

        public override bool CanWrite => _m switch
        {
            IFieldSymbol f => !f.IsReadOnly && !f.IsConst,
            IPropertySymbol p => p.SetMethod != null && !p.SetMethod.IsInitOnly,
            ILocalSymbol l => !l.IsConst && !l.IsForEach && !l.IsUsing,
            IParameterSymbol p => p.RefKind != RefKind.In,
            _ => false,
        };

        public override bool IsVirtual => _m.IsVirtual;
        public override bool IsAbstract => _m.IsAbstract;
        public override bool IsOverride => _m.IsOverride;

        public override IReadOnlyList<MacroParameter> Parameters => (_m switch
        {
            IMethodSymbol m => m.Parameters,
            IPropertySymbol p => p.Parameters,
            _ => ImmutableArray<IParameterSymbol>.Empty,
        }).Select(p => new MacroParameter(
            p.Name,
            MacroSemantics.Display(p.Type),
            () => _s.Type(p.Type),
            p.RefKind switch { RefKind.Ref => "ref", RefKind.Out => "out", RefKind.In => "in", _ => p.RefKind.ToString() == "RefReadOnlyParameter" ? "ref readonly" : "" },
            p.IsParams,
            p.HasExplicitDefaultValue,
            p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null)).ToArray();

        public override bool HasConstantValue => _m is IFieldSymbol { HasConstantValue: true } || _m is ILocalSymbol { HasConstantValue: true };

        public override object? ConstantValue => _m switch
        {
            IFieldSymbol { HasConstantValue: true } f => f.ConstantValue,
            ILocalSymbol { HasConstantValue: true } l => l.ConstantValue,
            _ => null,
        };

        public override IReadOnlyList<MacroAttributeInfo> Attributes => _attributes.Value;
        public override string? ExpandedBy => _expandedBy.Value;
        public override string? FilePath => MacroSemantics.Location(_m).Path;
        public override int? Line => MacroSemantics.Location(_m).Line;
    }
}
