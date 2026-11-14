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
    }
}
