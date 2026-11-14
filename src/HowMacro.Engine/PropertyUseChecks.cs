using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HowMacro.Engine
{
    /// <summary>
    /// Closes a gap in the core principle: Member macros and region macros turn a field into a property with the same name and type.
    /// For almost all code the two are equivalent, but a few uses only work with fields: the IDE sees a field and reports nothing,
    /// the build fails only after expansion, and the compiler's "a property can't be passed by reference" doesn't match the source. These are reported at the use site up front:
    /// <list type="bullet">
    /// <item><c>ref</c> / <c>out</c> arguments, <c>ref</c> expressions, address-of <c>&amp;</c>: a property has no storage location.</item>
    /// <item>Mutating a member of a struct-typed member: <c>p.Pos.X = 1</c> — the property returns a copy, so the change is lost.</item>
    /// </list>
    /// </summary>
    public static partial class MacroExpander
    {
        private static void CheckPropertyUses(
            Dictionary<ISymbol, string> transformed,
            IReadOnlyList<SyntaxTree> trees,
            SemanticModel[] models,
            bool[] failed,
            List<MacroDiagnostic> diagnostics)
        {
            if (transformed.Count == 0) return;
            var names = new HashSet<string>(transformed.Keys.Select(s => s.Name));

            for (var i = 0; i < trees.Count; i++)
            {
                var root = trees[i].GetRoot();
                if (!root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && names.Contains(t.ValueText))) continue;
                var model = models[i];

                string? MacroOf(ExpressionSyntax expression)
                {
                    var symbol = model.GetSymbolInfo(expression).Symbol?.OriginalDefinition;
                    return symbol != null && transformed.TryGetValue(symbol, out var macro) ? macro : null;
                }

                void Report(ExpressionSyntax at, string macro, string why)
                {
                    var name = model.GetSymbolInfo(at).Symbol?.Name ?? at.ToString();
                    diagnostics.Add(Diag(DiagnosticLevel.Error, "HMC017",
                        $"{name} is turned into a property by {macro}, so {why}. " +
                        "The IDE still sees a field here, but the compiled code has a property", trees[i], at));
                    failed[i] = true;
                }

                foreach (var node in root.DescendantNodes())
                {
                    switch (node)
                    {
                        case ArgumentSyntax arg when arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                            if (MacroOf(arg.Expression) is string m1) Report(arg.Expression, m1, $"it can't be passed as `{arg.RefKindKeyword.Text}`");
                            break;

                        case RefExpressionSyntax refExpr:
                            if (MacroOf(refExpr.Expression) is string m2) Report(refExpr.Expression, m2, "you can't take a `ref` to it");
                            break;

                        case PrefixUnaryExpressionSyntax addressOf when addressOf.IsKind(SyntaxKind.AddressOfExpression):
                            if (MacroOf(addressOf.Operand) is string m3) Report(addressOf.Operand, m3, "you can't take its address");
                            break;

                        // p.Pos.X = 1 / p.Pos.X++: this mutates the copy returned by the property
                        case MemberAccessExpressionSyntax access when IsMutated(access) && IsValueTypeMember(access.Expression, model):
                            if (MacroOf(access.Expression) is string m4)
                                Report(access.Expression, m4, $"assigning to `{access}` would only change a copy of the struct");
                            break;
                    }
                }
            }
        }

        private static bool IsMutated(ExpressionSyntax target) => target.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == target,
            PostfixUnaryExpressionSyntax postfix => postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression),
            PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression),
            _ => false,
        };

        private static bool IsValueTypeMember(ExpressionSyntax expression, SemanticModel model) =>
            model.GetTypeInfo(expression).Type is { IsValueType: true, SpecialType: SpecialType.None } type && type.TypeKind != TypeKind.Enum;
    }
}
