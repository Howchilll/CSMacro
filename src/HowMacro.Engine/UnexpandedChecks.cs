using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace HowMacro.Engine
{
    /// <summary>
    /// Safety net for the core principle: the unexpanded project must stay statically correct.
    /// If code at a macro call site is only valid after expansion (so the IDE keeps showing an error), the build reports HMC020 instead of letting it pass silently.
    /// Typical example: <c>public int Hp = @CLAMP(0, MaxHp, 20);</c>
    /// Before expansion this is a field initializer, which can't reference the instance member MaxHp (CS0236); after expansion MaxHp moves into the setter, so it compiles.
    /// </summary>
    public static partial class MacroExpander
    {
        /// <summary>
        /// Errors not reported here: missing names/types/members. The compiler reports them after expansion anyway,
        /// and reporting them here would cause false positives in projects that use types produced by source generators (the expansion compilation doesn't run source generators).
        /// </summary>
        private static readonly HashSet<string> SkippedIds = new HashSet<string> { "CS0103", "CS0246", "CS0234", "CS1061", "CS0117" };

        private static bool CheckUnexpandedSource(
            SyntaxTree tree,
            SemanticModel model,
            List<MacroCall> calls,
            List<AttributeUse> attributes,
            List<MacroDiagnostic> diagnostics)
        {
            var sites = calls.Select(c => (Span: c.Node.Span, Name: "@" + c.Method.Name))
                .Concat(attributes.Select(a => (Span: a.Node.Span, Name: "[@" + a.Ctor.ContainingType.Name + "]")))
                .ToList();
            if (sites.Count == 0) return true;

            var ok = true;
            var reported = new HashSet<(int, string)>();
            foreach (var (span, name) in sites)
            {
                foreach (var d in model.GetDiagnostics(span))
                {
                    if (d.Severity != DiagnosticSeverity.Error || SkippedIds.Contains(d.Id)) continue;
                    if (!reported.Add((d.Location.SourceSpan.Start, d.Id))) continue;

                    var pos = d.Location.GetLineSpan().StartLinePosition;
                    diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "HMC020",
                        $"{d.Id}: {d.GetMessage()} -- this code is only valid after {name} is expanded, so the IDE shows it as an error. " +
                        "The unexpanded source must stay statically correct; rewrite the call so it is valid C# on its own",
                        tree.FilePath, pos.Line + 1, pos.Character + 1));
                    ok = false;
                }
            }
            return ok;
        }

        /// <summary>
        /// Member and region macros may add private fields for their own state (e.g. <c>private int @_temp_Hp;</c>). They don't
        /// exist in the unexpanded source, so source code that names one would compile after expansion while the IDE shows an
        /// error: reported as HMC021. Found by comparing each type's member names before and after expansion.
        /// </summary>
        private static void CheckPrivateStateUses(Compilation after, IReadOnlyList<SyntaxTree> targets, IReadOnlyList<SyntaxTree> expanded, IReadOnlyList<SemanticModel> models,
            bool[] failed, List<MacroDiagnostic> diagnostics)
        {
            var added = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (var i = 0; i < targets.Count; i++)
            {
                if (ReferenceEquals(expanded[i], targets[i])) continue;
                var model = after.GetSemanticModel(expanded[i]);
                foreach (var decl in expanded[i].GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
                {
                    if (!(model.GetDeclaredSymbol(decl) is INamedTypeSymbol type)) continue;
                    var key = type.ToDisplayString();
                    if (added.ContainsKey(key)) continue;
                    var original = models[i].Compilation.GetTypeByMetadataName(MetadataNameOf(type));
                    if (original == null) continue;
                    var before = new HashSet<string>(original.GetMembers().Select(m => m.Name));
                    var extra = new HashSet<string>(type.GetMembers().OfType<IFieldSymbol>()
                        .Where(f => f.DeclaredAccessibility == Accessibility.Private && !f.IsImplicitlyDeclared && !before.Contains(f.Name))
                        .Select(f => f.Name));
                    if (extra.Count > 0) added[key] = extra;
                }
            }
            if (added.Count == 0) return;

            for (var i = 0; i < targets.Count; i++)
            {
                foreach (var id in targets[i].GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>())
                {
                    var enclosing = models[i].GetEnclosingSymbol(id.SpanStart);
                    var type = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
                    if (type == null || !added.TryGetValue(type.ToDisplayString(), out var names) || !names.Contains(id.Identifier.ValueText)) continue;
                    var info = models[i].GetSymbolInfo(id);
                    if (info.Symbol != null || info.CandidateSymbols.Length > 0) continue;

                    var pos = id.GetLocation().GetLineSpan().StartLinePosition;
                    diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "HMC021",
                        $"'{id.Identifier.ValueText}' is a private field a macro generates for its own state; it doesn't exist in the source, " +
                        "so the IDE can't see it. Use the member the macro expands instead",
                        targets[i].FilePath, pos.Line + 1, pos.Character + 1));
                    failed[i] = true;
                }
            }
        }

        private static string MetadataNameOf(INamedTypeSymbol type)
        {
            var name = type.MetadataName;
            for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType) name = outer.MetadataName + "+" + name;
            var ns = type.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
        }
    }
}
