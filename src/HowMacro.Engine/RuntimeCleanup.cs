using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace HowMacro.Engine
{
    /// <summary>
    /// Runtime cleanup: macro libraries are only useful at compile time; after expansion the program shouldn't depend on them.
    /// <list type="number">
    /// <item>Remove now-unused usings that import macro libraries (e.g. <c>using static Demo.Macros.M;</c>). Only that line is blanked, so line numbers stay the same.
    /// Otherwise the compiler keeps references to macro library types in the assembly for the PDB's import information.</item>
    /// <item>Use Roslyn's GetUsedAssemblyReferences to decide whether macro assemblies (including HowMacro.Abstractions) are still used,
    /// and make sure no other runtime dependency references them. Only then are they reported as "not needed at runtime" for MSBuild to remove from references and output.</item>
    /// </list>
    /// If the expanded code still uses ordinary (non-macro) types from a macro library, that assembly is kept.
    /// </summary>
    public static partial class MacroExpander
    {
        private const string AbstractionsAssemblyName = "HowMacro.Abstractions";

        private static (SyntaxTree[] Trees, List<string> Unused, bool Reliable) RuntimeCleanup(
            CSharpCompilation compilation, SyntaxTree[] originals, IReadOnlyList<SyntaxTree> expanded, string? startupCode, CSharpParseOptions parseOptions)
        {
            var current = expanded.ToArray();
            var macroAssemblies = new HashSet<IAssemblySymbol>(
                compilation.References
                    .Select(r => compilation.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol)
                    .Where(a => a != null && (a.Name == AbstractionsAssemblyName || ContainsMacros(a.GlobalNamespace)))!,
                SymbolEqualityComparer.Default);
            if (macroAssemblies.Count == 0) return (current, new List<string>(), true);

            // The compilation after expansion (including the startup script)
            var after = compilation;
            for (var i = 0; i < originals.Length; i++)
                if (!ReferenceEquals(current[i], originals[i])) after = after.ReplaceSyntaxTree(originals[i], current[i]);
            if (startupCode != null) after = after.AddSyntaxTrees(CSharpSyntaxTree.ParseText(startupCode, parseOptions, StartupFileName));

            // ---- 1. remove usings that import macro libraries and are no longer used ----
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

                // Safety net: removing them must not add errors (otherwise the using is still needed, e.g. it imports a plain helper type from a macro library)
                int Errors(Compilation c, SyntaxTree t) => c.GetSemanticModel(t).GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
                if (Errors(candidate, stripped) > Errors(after, tree)) continue;

                after = candidate;
                current[i] = stripped;
            }

            // ---- 2. which macro assemblies are no longer needed at runtime ----
            // Note: GetUsedAssemblyReferences counts every reference as used in two cases → nothing is removed (conservative, safe):
            // 1. The compilation has any error. So the analysis compilation must match the real one (output type, Nullable etc. are passed in by the host).
            // 2. Documentation comments are not parsed (DocumentationMode.None): it can't tell what <see cref> refers to. So expansion parses with DocumentationMode.Parse.
            var used = new HashSet<MetadataReference>(after.GetUsedAssemblyReferences());
            var reliable = !after.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            var unused = macroAssemblies.Where(a => after.GetMetadataReference(a) is { } r && !used.Contains(r)).ToList();

            // Assemblies still referenced by other runtime dependencies must be kept
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
            return (current, paths, reliable);
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

        /// <summary>Whether the type or namespace this using imports comes entirely from macro assemblies.</summary>
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
