using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSMacro.Engine
{
    /// <summary>
    /// 补上核心原则的一个缺口：Member 宏 / 片段宏会把字段变成同名同类型的属性。
    /// 对绝大多数代码这两者等价，但有几种用法只对字段成立：IDE 看到的是字段，不会报错，
    /// 展开后才编译失败，而且编译器报的"属性不能按引用传递"和源码对不上。这里提前在用法处报清楚：
    /// <list type="bullet">
    /// <item><c>ref</c> / <c>out</c> 实参、<c>ref</c> 表达式、取地址 <c>&amp;</c>：属性没有存储位置。</item>
    /// <item>修改结构体类型成员的内部：<c>p.Pos.X = 1</c>，属性返回的是副本，修改会丢失。</item>
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
                    diagnostics.Add(Diag(DiagnosticLevel.Error, "CSM017",
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

                        // p.Pos.X = 1 / p.Pos.X++ ：修改的是属性返回的副本
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
