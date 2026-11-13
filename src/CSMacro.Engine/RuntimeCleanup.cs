using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSMacro.Engine
{
    /// <summary>
    /// 运行时清理：宏库只在编译期有用，展开后程序不应该再依赖它们。
    /// <list type="number">
    /// <item>去掉导入宏库、且已经用不到的 using（比如 <c>using static Demo.Macros.M;</c>）。只清空那一行，行号不变。
    /// 不去掉的话，编译器会为 PDB 的导入信息在程序集里保留对宏库类型的引用。</item>
    /// <item>用 Roslyn 的 GetUsedAssemblyReferences 判断宏程序集（含 CSMacro.Abstractions）是否还被用到，
    /// 并确认没有其他运行时依赖引用它们。都满足才报告为"运行时不需要"，由 MSBuild 从引用和输出里移除。</item>
    /// </list>
    /// 展开后的代码如果仍然用到了宏库里的普通类型（不是宏），对应的程序集会照常保留。
    /// </summary>
    public static partial class MacroExpander
    {
        private const string AbstractionsAssemblyName = "CSMacro.Abstractions";

        private static (SyntaxTree[] Trees, List<string> Unused) RuntimeCleanup(
            CSharpCompilation compilation, SyntaxTree[] originals, IReadOnlyList<SyntaxTree> expanded, string? startupCode, CSharpParseOptions parseOptions)
        {
            var current = expanded.ToArray();
            var macroAssemblies = new HashSet<IAssemblySymbol>(
                compilation.References
                    .Select(r => compilation.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol)
                    .Where(a => a != null && (a.Name == AbstractionsAssemblyName || ContainsMacros(a.GlobalNamespace)))!,
                SymbolEqualityComparer.Default);
            if (macroAssemblies.Count == 0) return (current, new List<string>());

            // 展开后的编译（含启动脚本）
            var after = compilation;
            for (var i = 0; i < originals.Length; i++)
                if (!ReferenceEquals(current[i], originals[i])) after = after.ReplaceSyntaxTree(originals[i], current[i]);
            if (startupCode != null) after = after.AddSyntaxTrees(CSharpSyntaxTree.ParseText(startupCode, parseOptions, StartupFileName));

            // ---- 1. 去掉导入宏库、已经用不到的 using ----
            for (var i = 0; i < current.Length; i++)
            {
                var tree = current[i];
                var model = after.GetSemanticModel(tree);
                var usings = tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Where(u => ImportsOnlyMacroAssemblies(u, model, macroAssemblies))
                    .ToList();
                if (usings.Count == 0) continue;

                var stripped = tree.WithChangedText(tree.GetText().WithChanges(usings.Select(u => new TextChange(u.Span, ""))));
                var candidate = after.ReplaceSyntaxTree(tree, stripped);

                // 保险：去掉后不能多出错误（说明那个 using 其实还在用，比如导入了宏库里的普通辅助类型）
                int Errors(Compilation c, SyntaxTree t) => c.GetSemanticModel(t).GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
                if (Errors(candidate, stripped) > Errors(after, tree)) continue;

                after = candidate;
                current[i] = stripped;
            }

            // ---- 2. 哪些宏程序集运行时已经用不到 ----
            // 注意 GetUsedAssemblyReferences 在两种情况下会把所有引用都算作用到了 → 什么都不去掉（保守、安全）：
            // 1. 编译有任何错误。所以分析用的编译要和真实编译一致（输出类型、Nullable 等由宿主传入）。
            // 2. 没有解析文档注释（DocumentationMode.None）：无法判断 <see cref> 引用了什么。所以展开时用 DocumentationMode.Parse。
            var used = new HashSet<MetadataReference>(after.GetUsedAssemblyReferences());
            var unused = macroAssemblies.Where(a => after.GetMetadataReference(a) is { } r && !used.Contains(r)).ToList();

            // 仍被其他运行时依赖引用的不能去掉
            var stillNeeded = new HashSet<string>(used
                .Select(r => after.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol)
                .Where(a => a != null && !unused.Contains(a, SymbolEqualityComparer.Default))
                .SelectMany(a => a!.Modules.SelectMany(m => m.ReferencedAssemblySymbols))
                .Select(a => a.Name));

            var paths = unused
                .Where(a => !stillNeeded.Contains(a.Name))
                .Select(a => (after.GetMetadataReference(a) as PortableExecutableReference)?.FilePath)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();
            return (current, paths);
        }

        private static bool ContainsMacros(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                switch (member)
                {
                    case INamedTypeSymbol type:
                        if (type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == AttributeMacroAttributeName)) return true;
                        if (type.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a =>
                                a.AttributeClass?.ToDisplayString() is MacroAttributeName or RegionMacroAttributeName))) return true;
                        if (ContainsMacros(type)) return true;
                        break;
                    case INamespaceSymbol ns:
                        if (ContainsMacros(ns)) return true;
                        break;
                }
            }
            return false;
        }

        /// <summary>这个 using 导入的类型或命名空间是否完全来自宏程序集。</summary>
        private static bool ImportsOnlyMacroAssemblies(UsingDirectiveSyntax u, SemanticModel model, HashSet<IAssemblySymbol> macroAssemblies)
        {
            if (u.NamespaceOrType == null) return false;
            return model.GetSymbolInfo(u.NamespaceOrType).Symbol switch
            {
                INamedTypeSymbol type => macroAssemblies.Contains(type.ContainingAssembly),
                INamespaceSymbol ns => ns.ConstituentNamespaces.Length > 0 &&
                                       ns.ConstituentNamespaces.All(c => c.ContainingAssembly != null && macroAssemblies.Contains(c.ContainingAssembly)),
                _ => false,
            };
        }
    }
}
