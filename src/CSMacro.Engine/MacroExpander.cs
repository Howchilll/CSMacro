using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CSMacro;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSMacro.Engine
{
    /// <summary>
    /// 展开流程（一次完成，不多轮）：
    /// 0. 用全部源码 + 引用建一个 Roslyn 编译（只做语义分析）。识别宏靠语义，不靠 @ 文本：
    ///    方法宏 = 被调用的方法带 [Macro]；特性宏 = 特性类带 [AttributeMacro]。
    ///    宏不能包含宏：方法宏的实参里出现宏直接报错。
    /// 1. 特性宏阶段：按文件、行的顺序执行所有特性宏，收集它们产出的片段和启动代码。
    /// 2. 方法宏阶段：原地展开所有方法宏，此时可以读取第 1 阶段的全部片段。
    ///    输出压成一行、补齐换行，保证行号不变；特性宏从源码中删除。
    /// 3. 检查展开结果里没有再出现宏（宏的输出也不能包含宏）。
    /// 4. 把启动代码汇总成一个辅助脚本。
    /// 5. （CLI）编译检查宏生成的代码，出错时报出是哪个宏生成的。
    /// </summary>
    public static class MacroExpander
    {
        private const string MacroAttributeName = "CSMacro.MacroAttribute";
        private const string AttributeMacroAttributeName = "CSMacro.AttributeMacroAttribute";

        /// <summary>汇总启动代码的辅助脚本文件名。</summary>
        public const string StartupFileName = "CSMacro.Startup.g.cs";

        public static ExpandResult Expand(IReadOnlyList<SourceFile> sources, ExpandOptions options, IMacroInvoker invoker)
        {
            var parseOptions = new CSharpParseOptions(
                ParseLanguageVersion(options.LanguageVersion),
                DocumentationMode.None,
                SourceCodeKind.Regular,
                options.Defines);

            var trees = sources
                .Select(s => CSharpSyntaxTree.ParseText(SourceText.From(s.Text, Encoding.UTF8), parseOptions, s.Path))
                .ToArray();

            var references = options.References
                .Where(File.Exists)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "CSMacro.Expansion",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                    nullableContextOptions: ParseNullable(options.Nullable)));

            var diagnostics = new List<MacroDiagnostic>();
            var result = ExpandTrees(compilation, trees, options, invoker, diagnostics);

            if (options.Verify && !diagnostics.Exists(d => d.Level == DiagnosticLevel.Error))
                Verify(compilation, trees, result, parseOptions, diagnostics);

            var files = new List<ExpandedFile>();
            for (var i = 0; i < sources.Count; i++)
            {
                if (ReferenceEquals(result.Expanded[i], trees[i])) continue;
                var header = "#line 1 \"" + EscapePath(sources[i].Path) + "\"\n";
                files.Add(new ExpandedFile(sources[i].Id, sources[i].Path, header + result.Expanded[i].GetText()));
            }

            var generated = new List<GeneratedFile>();
            if (result.StartupCode != null) generated.Add(new GeneratedFile(StartupFileName, result.StartupCode));

            return new ExpandResult(files, generated, diagnostics, result.Expansions);
        }

        /// <summary>
        /// 在一个已有的编译里展开 <paramref name="targets"/>（必须属于该编译）。
        /// 宿主可以自己建编译（CLI），也可以直接用别处现成的编译（例如以后的 Unity 宿主）。
        /// </summary>
        public static ExpansionResult ExpandTrees(
            CSharpCompilation compilation,
            IReadOnlyList<SyntaxTree> targets,
            ExpandOptions options,
            IMacroInvoker invoker,
            List<MacroDiagnostic> diagnostics)
        {
            var n = targets.Count;
            var models = new SemanticModel[n];
            var calls = new List<MacroCall>[n];
            var attrs = new List<AttributeUse>[n];
            var failed = new bool[n];
            var nestedOuters = new HashSet<InvocationExpressionSyntax>(); // 实参里含宏的调用：不执行，避免误导性的连带错误
            var defines = targets.Count > 0 ? ((CSharpParseOptions)targets[0].Options).PreprocessorSymbolNames.ToArray() : new string[0];
            var semantics = new MacroSemantics(compilation); // 语义信息基于展开前的编译：IDE 视角

            // ---- 0. 发现 + 宏不能包含宏 ----
            for (var i = 0; i < n; i++)
            {
                models[i] = compilation.GetSemanticModel(targets[i]);
                calls[i] = FindMacroCalls(targets[i], models[i]);
                attrs[i] = FindAttributeMacros(targets[i], models[i]);

                foreach (var c in calls[i].ToList())
                {
                    var outer = calls[i].FirstOrDefault(o => o != c && o.Node.ArgumentList.Span.Contains(c.Node.Span));
                    if (outer == null) continue;
                    nestedOuters.Add(outer.Node);
                    diagnostics.Add(Diag(DiagnosticLevel.Error, "CSM012",
                        $"macros can't contain macros: @{c.Method.Name} is inside the arguments of @{outer.Method.Name}. " +
                        "Expand it separately first, e.g. into a local variable",
                        targets[i], c.Node));
                    failed[i] = true;
                }
            }

            // ---- 宏筛选（排查问题用）：没选中的宏保持原样 ----
            var selected = MacroSelector(options.Macros);
            for (var i = 0; i < n; i++)
            {
                calls[i] = calls[i].Where(c => selected("@" + c.Method.Name)).ToList();
                attrs[i] = attrs[i].Where(a => selected("[@" + a.Ctor.ContainingType.Name + "]")).ToList();
            }
            var records = new List<MacroExpansion>();

            // ---- 1. 特性宏阶段 ----
            var fragments = new Dictionary<string, List<MacroFragment>>(StringComparer.Ordinal);
            var startup = new List<MacroFragment>();
            // 出错后不立即停下：把所有宏都跑一遍，一次报出全部错误；只是出错的文件不会被替换。
            foreach (var i in Enumerable.Range(0, n).OrderBy(i => targets[i].FilePath, StringComparer.Ordinal))
            {
                foreach (var use in attrs[i])
                {
                    if (!RunAttributeMacro(use, targets[i], models[i], compilation, invoker, defines, semantics, diagnostics, fragments, startup, records))
                        failed[i] = true;
                }
            }
            var available = fragments.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<MacroFragment>)kv.Value, StringComparer.Ordinal);

            // ---- 2. 方法宏阶段 ----
            var expanded = targets.ToArray();
            var expansionLines = new Dictionary<SyntaxTree, Dictionary<int, string>>();
            for (var i = 0; i < n; i++)
            {
                if (calls[i].Count == 0 && attrs[i].Count == 0) continue;

                var tree = targets[i];
                var macroAttributes = new HashSet<AttributeSyntax>(attrs[i].Select(a => a.Node));
                var changes = new List<TextChange>();
                var lines = new Dictionary<int, string>();

                foreach (var call in calls[i].Where(c => !nestedOuters.Contains(c.Node)).OrderBy(c => c.Node.SpanStart))
                {
                    var change = ExpandOne(call, tree, models[i], compilation, invoker, defines, semantics, macroAttributes, available, diagnostics);
                    if (change == null) { failed[i] = true; continue; }

                    if (changes.Count > 0 && changes[changes.Count - 1].Span.End > change.Value.Span.Start)
                    {
                        diagnostics.Add(Diag(DiagnosticLevel.Error, "CSM014",
                            $"@{call.Method.Name} replaces code that overlaps with another macro on the same declaration", tree, call.Node));
                        failed[i] = true;
                        continue;
                    }

                    changes.Add(change.Value);
                    records.Add(Record(tree, change.Value, call));
                    var line = tree.GetLineSpan(change.Value.Span).StartLinePosition.Line + 1;
                    lines[line] = lines.TryGetValue(line, out var names) ? names + ", @" + call.Method.Name : "@" + call.Method.Name;
                }
                if (failed[i]) continue;

                // 删除特性宏（所在声明已经被 Member 宏整体替换的除外：Member 宏拿到的 Attributes 本来就不含特性宏）
                foreach (var list in attrs[i].Select(a => (AttributeListSyntax)a.Node.Parent!).Distinct())
                {
                    if (changes.Any(c => c.Span.Contains(list.Span))) continue;
                    var newlines = tree.GetText().ToString(list.Span).Count(ch => ch == '\n');
                    changes.Add(new TextChange(list.Span, WithoutMacroAttributes(list, macroAttributes) + new string('\n', newlines)));
                }

                changes.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
                var newTree = tree.WithChangedText(tree.GetText().WithChanges(changes));
                expanded[i] = newTree;
                expansionLines[newTree] = lines;
            }

            // ---- 3. 宏的输出也不能包含宏 ----
            var after = compilation;
            for (var i = 0; i < n; i++)
                if (!ReferenceEquals(expanded[i], targets[i])) after = after.ReplaceSyntaxTree(targets[i], expanded[i]);

            for (var i = 0; i < n; i++)
            {
                if (failed[i] || ReferenceEquals(expanded[i], targets[i])) continue;
                var model = after.GetSemanticModel(expanded[i]);
                var lines = expansionLines[expanded[i]];

                IEnumerable<(SyntaxNode Node, string Name)> leftovers =
                    FindMacroCalls(expanded[i], model).Select(c => ((SyntaxNode)c.Node, "@" + c.Method.Name))
                    .Concat(FindAttributeMacros(expanded[i], model).Select(a => ((SyntaxNode)a.Node, "[@" + a.Ctor.ContainingType.Name + "]")));

                foreach (var (node, macro) in leftovers)
                {
                    if (!selected(macro)) continue; // 没选中的宏本来就保持原样
                    var line = expanded[i].GetLineSpan(node.Span).StartLinePosition.Line + 1;
                    var by = lines.TryGetValue(line, out var names) ? names : "a macro";
                    diagnostics.Add(Diag(DiagnosticLevel.Error, "CSM012",
                        $"macros can't contain macros: the output of {by} contains {macro}", expanded[i], node));
                    failed[i] = true;
                }
            }

            // ---- 4. 启动代码 → 辅助脚本 ----
            var parseOptions = n > 0 ? (CSharpParseOptions)targets[0].Options : CSharpParseOptions.Default;
            var startupCode = BuildStartupScript(startup, options.StartupAttribute, parseOptions, diagnostics);

            var ordered = records.OrderBy(r => r.FilePath, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
            return new ExpansionResult(expanded, expansionLines, startupCode, startup, ordered);
        }

        // ================================================================== 特性宏

        private sealed class AttributeUse
        {
            public AttributeUse(AttributeSyntax node, IMethodSymbol ctor, MacroTarget targets)
            {
                Node = node;
                Ctor = ctor;
                Targets = targets;
            }

            public AttributeSyntax Node { get; }
            public IMethodSymbol Ctor { get; }
            public MacroTarget Targets { get; }
        }

        private static List<AttributeUse> FindAttributeMacros(SyntaxTree tree, SemanticModel model)
        {
            var result = new List<AttributeUse>();
            foreach (var attr in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
            {
                var info = model.GetSymbolInfo(attr);
                var ctor = (info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
                if (ctor == null) continue;

                var marker = ctor.ContainingType.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttributeMacroAttributeName);
                if (marker == null) continue;

                var targets = marker.ConstructorArguments.Length > 0 && marker.ConstructorArguments[0].Value is int v
                    ? (MacroTarget)v
                    : MacroTarget.All;
                result.Add(new AttributeUse(attr, ctor, targets));
            }
            return result;
        }

        private static bool RunAttributeMacro(
            AttributeUse use,
            SyntaxTree tree,
            SemanticModel model,
            CSharpCompilation compilation,
            IMacroInvoker invoker,
            IReadOnlyCollection<string> defines,
            MacroSemantics semantics,
            List<MacroDiagnostic> diagnostics,
            Dictionary<string, List<MacroFragment>> fragments,
            List<MacroFragment> startup,
            List<MacroExpansion> records)
        {
            var attr = use.Node;
            var name = use.Ctor.ContainingType.Name;

            bool Fail(string code, string message)
            {
                diagnostics.Add(Diag(DiagnosticLevel.Error, code, message, tree, attr));
                return false;
            }

            if (!AttributeNameIsVerbatim(attr))
                diagnostics.Add(Diag(DiagnosticLevel.Warning, "CSM010",
                    $"attribute macro '{name}' should be written as [@{name}] so readers can tell it is expanded at compile time",
                    tree, attr));

            var list = (AttributeListSyntax)attr.Parent!;
            if (list.Target != null)
                return Fail("CSM003", $"[@{name}] can't have an attribute target specifier ('{list.Target.Identifier.Text}:')");

            MacroTarget target;
            string? memberName, memberType = null, modifiers;
            INamedTypeSymbol? containing;
            ISymbol? declared = null;
            switch (list.Parent)
            {
                case FieldDeclarationSyntax f:
                    if (f.Declaration.Variables.Count != 1)
                        return Fail("CSM003", $"[@{name}] must be on a field declaration with exactly one variable");
                    target = MacroTarget.Field;
                    memberName = f.Declaration.Variables[0].Identifier.ValueText;
                    memberType = f.Declaration.Type.ToString();
                    modifiers = f.Modifiers.ToString();
                    declared = model.GetDeclaredSymbol(f.Declaration.Variables[0]);
                    containing = declared?.ContainingType;
                    break;
                case PropertyDeclarationSyntax p:
                    target = MacroTarget.Property;
                    memberName = p.Identifier.ValueText;
                    memberType = p.Type.ToString();
                    modifiers = p.Modifiers.ToString();
                    declared = model.GetDeclaredSymbol(p);
                    containing = declared?.ContainingType;
                    break;
                case MethodDeclarationSyntax m:
                    target = MacroTarget.Method;
                    memberName = m.Identifier.ValueText;
                    memberType = m.ReturnType.ToString();
                    modifiers = m.Modifiers.ToString();
                    declared = model.GetDeclaredSymbol(m);
                    containing = declared?.ContainingType;
                    break;
                case TypeDeclarationSyntax t:
                    target = MacroTarget.Type;
                    memberName = t.Identifier.ValueText;
                    modifiers = t.Modifiers.ToString();
                    containing = model.GetDeclaredSymbol(t);
                    break;
                default:
                    return Fail("CSM003", $"[@{name}] can only be used on fields, properties, methods and types");
            }

            if ((use.Targets & target) == 0)
                return Fail("CSM003", $"[@{name}] can't be used on a {target.ToString().ToLowerInvariant()}; it is declared for: {use.Targets}");

            var reference = ResolveMacroAssembly(use.Ctor.ContainingType, compilation);
            if (reference == null)
                return Fail("CSM004",
                    $"[@{name}] is declared in the same project that uses it. Macros must live in a referenced assembly, " +
                    "because they are compiled and executed before this project is compiled");

            var pos = tree.GetLineSpan(attr.Span).StartLinePosition;
            var context = new MacroContext
            {
                MacroName = name,
                Target = target,
                FilePath = tree.FilePath,
                Line = pos.Line + 1,
                Column = pos.Character + 1,
                Args = attr.ArgumentList?.Arguments.Select(a => MakeArg(a.Expression, model, semantics)).ToArray() ?? Array.Empty<MacroArg>(),
                MemberName = memberName,
                MemberType = memberType,
                Modifiers = modifiers,
                ContainingType = containing?.ToDisplayString(),
                Defines = defines,
                Declaration = semantics.Member(declared),
                DeclaredTypeInfo = semantics.Type(declared switch
                {
                    IFieldSymbol fs => fs.Type,
                    IPropertySymbol ps => ps.Type,
                    IMethodSymbol ms => ms.ReturnType,
                    _ => null,
                }),
                ContainingTypeInfo = semantics.Type(containing),
                TypeLookup = semantics.Find,
            };

            var method = new MacroMethod(reference, MetadataName(use.Ctor.ContainingType), MacroMethod.Constructor, 0, use.Ctor.Parameters.Length);
            if (!Run(invoker, method, context, "[@" + name + "]", tree, attr, diagnostics)) return false;

            foreach (var f in context.ProducedFragments)
            {
                if (!fragments.TryGetValue(f.Channel, out var bucket)) fragments[f.Channel] = bucket = new List<MacroFragment>();
                bucket.Add(f);
            }
            startup.AddRange(context.ProducedStartup);

            var source = attr.Parent!.Parent!.ToString();
            source = source.Substring(0, Math.Min(source.Length, source.IndexOf('\n') is var nl && nl >= 0 ? nl : source.Length)).Trim();
            foreach (var f in context.ProducedFragments.Concat(context.ProducedStartup))
            {
                var kind = f.Channel == "startup" && context.ProducedStartup.Contains(f) ? "Startup" : "Fragment:" + f.Channel;
                var code = ToSingleLine(f.Code, (CSharpParseOptions)tree.Options, out _) ?? f.Code;
                records.Add(new MacroExpansion(tree.FilePath, context.Line, "[@" + name + "]", kind, source, code,
                    kind == "Startup" ? Format(code, MacroSite.Statement) : code));
            }
            return true;
        }

        private static string WithoutMacroAttributes(AttributeListSyntax list, HashSet<AttributeSyntax> macroAttributes)
        {
            var remaining = list.Attributes.Where(a => !macroAttributes.Contains(a)).ToList();
            if (remaining.Count == 0) return "";
            var target = list.Target != null ? list.Target.Identifier.Text + ": " : "";
            return "[" + target + string.Join(", ", remaining.Select(a => a.ToString())) + "]";
        }

        private static string? BuildStartupScript(
            List<MacroFragment> startup, string startupAttribute, CSharpParseOptions parseOptions, List<MacroDiagnostic> diagnostics)
        {
            if (startup.Count == 0) return null;

            var sb = new StringBuilder()
                .Append("// <auto-generated>\n")
                .Append("// CSMacro: startup code produced by attribute macros (Macro.Startup).\n")
                .Append("// Each statement is mapped back to the attribute that produced it.\n")
                .Append("// </auto-generated>\n")
                .Append("namespace CSMacro.Generated\n{\n")
                .Append("    internal static class Startup\n    {\n")
                .Append("        [").Append(startupAttribute).Append("]\n")
                .Append("        internal static void Run()\n        {\n");

            foreach (var f in startup)
            {
                var oneLine = ToSingleLine(f.Code, parseOptions, out var error);
                if (oneLine != null && FirstError(SyntaxFactory.ParseStatement("{ " + oneLine + " }", options: parseOptions, consumeFullText: true)) is string parseError)
                    error = $"startup code is not valid statements: {parseError} -- code: {oneLine}";
                if (error != null)
                {
                    diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "CSM011", $"[@{f.MacroName}]: {error}", f.FilePath, f.Line, 1));
                    continue;
                }

                sb.Append("#line ").Append(f.Line).Append(" \"").Append(EscapePath(f.FilePath)).Append("\"\n")
                  .Append("            { ").Append(oneLine).Append(" }\n");
            }

            return sb.Append("#line default\n        }\n    }\n}\n").ToString();
        }

        // ================================================================== 方法宏

        private sealed class MacroCall
        {
            public MacroCall(InvocationExpressionSyntax node, IMethodSymbol method, MacroSite sites)
            {
                Node = node;
                Method = method;
                Sites = sites;
            }

            public InvocationExpressionSyntax Node { get; }
            public IMethodSymbol Method { get; }
            public MacroSite Sites { get; }

            /// <summary>展开时实际匹配到的位置。</summary>
            public MacroSite UsedSite { get; set; }
        }

        private static List<MacroCall> FindMacroCalls(SyntaxTree tree, SemanticModel model)
        {
            var result = new List<MacroCall>();
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(inv);
                var method = (info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
                if (method == null) continue;

                var attr = method.OriginalDefinition.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MacroAttributeName);
                if (attr == null) continue;

                var sites = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int v
                    ? (MacroSite)v
                    : MacroSite.Expression;

                result.Add(new MacroCall(inv, method, sites));
            }
            return result;
        }

        private static TextChange? ExpandOne(
            MacroCall call,
            SyntaxTree tree,
            SemanticModel model,
            CSharpCompilation compilation,
            IMacroInvoker invoker,
            IReadOnlyCollection<string> defines,
            MacroSemantics semantics,
            HashSet<AttributeSyntax> macroAttributes,
            IReadOnlyDictionary<string, IReadOnlyList<MacroFragment>> fragments,
            List<MacroDiagnostic> diagnostics)
        {
            var inv = call.Node;
            var name = call.Method.Name;
            var parseOptions = (CSharpParseOptions)tree.Options;

            TextChange? Fail(string code, string message)
            {
                diagnostics.Add(Diag(DiagnosticLevel.Error, code, message, tree, inv));
                return null;
            }

            if (!CalleeNameIsVerbatim(inv))
                diagnostics.Add(Diag(DiagnosticLevel.Warning, "CSM010",
                    $"macro '{name}' should be called as @{name}(...) so readers can tell it is expanded at compile time",
                    tree, inv));

            // ---- 判定位置 ----
            string? memberName = null, memberType = null, modifiers = null, attributes = null;
            MacroSite? site = null;
            SyntaxNode target = inv;
            ISymbol? declared = null;

            if (inv.Parent is EqualsValueClauseSyntax eq
                && eq.Parent is VariableDeclaratorSyntax declarator
                && declarator.Parent is VariableDeclarationSyntax declaration)
            {
                memberName = declarator.Identifier.ValueText;
                memberType = declaration.Type.ToString();
                declared = model.GetDeclaredSymbol(declarator);

                if (declaration.Parent is FieldDeclarationSyntax field)
                {
                    modifiers = field.Modifiers.ToString();
                    attributes = string.Join(" ", field.AttributeLists
                        .Select(l => WithoutMacroAttributes(l, macroAttributes))
                        .Where(s => s.Length > 0));
                    if ((call.Sites & MacroSite.Member) != 0)
                    {
                        if (declaration.Variables.Count != 1)
                            return Fail("CSM003", $"@{name} replaces the whole field declaration, so it must declare exactly one variable");
                        site = MacroSite.Member;
                        target = field;
                    }
                }
                else if (declaration.Parent is LocalDeclarationStatementSyntax local)
                {
                    modifiers = local.Modifiers.ToString();
                }
            }

            if (site == null)
            {
                if (inv.Parent is ExpressionStatementSyntax statement && (call.Sites & MacroSite.Statement) != 0)
                {
                    site = MacroSite.Statement;
                    target = statement;
                }
                else if ((call.Sites & MacroSite.Expression) != 0)
                {
                    site = MacroSite.Expression;
                    target = inv;
                }
                else
                {
                    return Fail("CSM003", $"@{name} can't be used here; it is declared for: {call.Sites}");
                }
            }

            // ---- 定位宏方法所在的程序集 ----
            var def = call.Method.OriginalDefinition;
            var reference = ResolveMacroAssembly(def.ContainingType, compilation);
            if (reference == null)
                return Fail("CSM004",
                    $"@{name} is declared in the same project that uses it. Macros must live in a referenced assembly, " +
                    "because they are compiled and executed before this project is compiled");

            var method = new MacroMethod(reference, MetadataName(def.ContainingType), def.Name, def.Arity, def.Parameters.Length);

            // ---- 构造上下文 ----
            var pos = tree.GetLineSpan(inv.Span).StartLinePosition;
            var enclosing = model.GetEnclosingSymbol(inv.SpanStart);
            var containingType = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;

            var context = new MacroContext
            {
                MacroName = name,
                Site = site.Value,
                FilePath = tree.FilePath,
                Line = pos.Line + 1,
                Column = pos.Character + 1,
                Args = inv.ArgumentList.Arguments.Select(a => MakeArg(a.Expression, model, semantics)).ToArray(),
                MemberName = memberName,
                MemberType = memberType,
                Modifiers = modifiers,
                Attributes = attributes,
                ContainingType = containingType?.ToDisplayString(),
                Defines = defines,
                AvailableFragments = fragments,
                Declaration = semantics.Member(declared),
                DeclaredTypeInfo = semantics.Type(declared switch { IFieldSymbol fs => fs.Type, ILocalSymbol ls => ls.Type, _ => null }),
                ContainingTypeInfo = semantics.Type(containingType),
                TypeLookup = semantics.Find,
            };

            // ---- 执行宏 ----
            if (!Run(invoker, method, context, "@" + name, tree, inv, diagnostics)) return null;

            var emitted = context.Output.ToString();
            if (site == MacroSite.Expression && string.IsNullOrWhiteSpace(emitted))
                return Fail("CSM006", $"@{name} is used as an expression but emitted no code");

            // ---- 压成一行 + 补换行 ----
            var oneLine = ToSingleLine(emitted, parseOptions, out var compressError);
            if (oneLine == null) return Fail("CSM007", $"@{name}: {compressError}");

            // ---- 核心原则：不展开宏，项目静态上也必须正确 ----
            // 名字和类型只能由源码声明，宏只改实现；展开结果不得引入 IDE 看不见的名字。
            switch (site)
            {
                case MacroSite.Expression:
                    oneLine = "(" + oneLine + ")";
                    var expr = SyntaxFactory.ParseExpression(oneLine, options: parseOptions, consumeFullText: true);
                    if (FirstError(expr) is string exprError)
                        return Fail("CSM011", $"@{name} emitted code that is not a valid expression: {exprError} -- emitted: {oneLine}");
                    break;

                case MacroSite.Statement:
                    // 包进块里：宏内部声明的局部变量出不了这个块，后面的代码看不到它，也不会和外面重名。
                    oneLine = "{ " + oneLine + " }";
                    var block = SyntaxFactory.ParseStatement(oneLine, options: parseOptions, consumeFullText: true);
                    if (FirstError(block) is string stmtError)
                        return Fail("CSM011", $"@{name} emitted code that is not valid statements: {stmtError} -- emitted: {oneLine}");
                    break;

                case MacroSite.Member:
                    var memberError = CheckMember(oneLine, memberName!, memberType!, parseOptions);
                    if (memberError != null)
                        return Fail(memberError.Value.Code, $"@{name}: {memberError.Value.Message} -- emitted: {oneLine}");
                    break;
            }

            call.UsedSite = site.Value;
            var originalText = tree.GetText().ToString(target.Span);
            var newlines = originalText.Count(c => c == '\n');
            return new TextChange(target.Span, oneLine + new string('\n', newlines));
        }

        // ================================================================== 共用

        /// <summary>执行一次宏，把异常和宏自己报的消息转成诊断。返回是否成功。</summary>
        private static bool Run(IMacroInvoker invoker, MacroMethod method, MacroContext context, string display,
            SyntaxTree tree, SyntaxNode node, List<MacroDiagnostic> diagnostics)
        {
            try
            {
                invoker.Invoke(method, context);
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
                diagnostics.Add(Diag(DiagnosticLevel.Error, "CSM005", $"{display} threw {ex.GetType().Name}: {ex.Message}", tree, node));
                return false;
            }

            var ok = true;
            foreach (var msg in context.Messages)
            {
                var isError = msg.Severity == MacroMessageSeverity.Error;
                ok &= !isError;
                diagnostics.Add(Diag(isError ? DiagnosticLevel.Error : DiagnosticLevel.Warning,
                    isError ? "CSM100" : "CSM101", $"{display}: {msg.Text}", tree, node));
            }
            return ok;
        }

        private static MacroArg MakeArg(ExpressionSyntax expression, SemanticModel model, MacroSemantics semantics)
        {
            var constant = model.GetConstantValue(expression);
            var type = model.GetTypeInfo(expression).Type;
            return new MacroArg(
                expression.ToString(),
                type?.ToDisplayString(),
                constant.HasValue,
                constant.HasValue ? constant.Value : null,
                symbol: () => semantics.Member(model.GetSymbolInfo(expression).Symbol),
                typeInfo: () => semantics.Type(type));
        }

        private static MetadataReference? ResolveMacroAssembly(INamedTypeSymbol type, CSharpCompilation compilation) =>
            SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                ? null
                : compilation.GetMetadataReference(type.ContainingAssembly);

        private static bool AttributeNameIsVerbatim(AttributeSyntax attr)
        {
            SimpleNameSyntax? name = attr.Name switch
            {
                SimpleNameSyntax s => s,
                QualifiedNameSyntax q => q.Right,
                AliasQualifiedNameSyntax a => a.Name,
                _ => null,
            };
            return name != null && name.Identifier.Text.StartsWith("@", StringComparison.Ordinal);
        }

        private static Func<string, bool> MacroSelector(IReadOnlyCollection<string>? macros)
        {
            if (macros == null || macros.Count == 0) return _ => true;
            static string Key(string s) => s.Trim().Trim('[', ']').TrimStart('@');
            var set = new HashSet<string>(macros.Select(Key), StringComparer.Ordinal);
            return name => set.Contains(Key(name));
        }

        private static MacroExpansion Record(SyntaxTree tree, TextChange change, MacroCall call)
        {
            var source = string.Join(" ", tree.GetText().ToString(change.Span)
                .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            var output = change.NewText!.TrimEnd('\n');
            return new MacroExpansion(tree.FilePath, tree.GetLineSpan(change.Span).StartLinePosition.Line + 1,
                "@" + call.Method.Name, call.UsedSite.ToString(), source, output, Format(output, call.UsedSite));
        }

        /// <summary>把压成一行的代码重新格式化成多行，仅用于报告。</summary>
        private static string Format(string code, MacroSite site)
        {
            SyntaxNode node = site switch
            {
                MacroSite.Member => (SyntaxNode?)SyntaxFactory.ParseMemberDeclaration(code) ?? SyntaxFactory.ParseStatement(code),
                MacroSite.Statement => SyntaxFactory.ParseStatement(code),
                _ => SyntaxFactory.ParseExpression(code),
            };
            return node.NormalizeWhitespace("    ", "\n").ToFullString();
        }

        private static string EscapePath(string path) => path.Replace("\\", "\\\\");

        // ================================================================== 编译检查

        /// <summary>
        /// 编译展开后的代码，只关心宏生成的那些行（和启动脚本）。出错时报出是哪个宏生成的，
        /// 让开发者知道问题在宏而不是自己写的代码；其余错误留给编译器自己报。
        /// </summary>
        private static void Verify(CSharpCompilation compilation, SyntaxTree[] originals, ExpansionResult result,
            CSharpParseOptions parseOptions, List<MacroDiagnostic> diagnostics)
        {
            var after = compilation;
            for (var i = 0; i < originals.Length; i++)
                if (!ReferenceEquals(result.Expanded[i], originals[i])) after = after.ReplaceSyntaxTree(originals[i], result.Expanded[i]);

            SyntaxTree? startupTree = null;
            if (result.StartupCode != null)
            {
                startupTree = CSharpSyntaxTree.ParseText(result.StartupCode, parseOptions, StartupFileName);
                after = after.AddSyntaxTrees(startupTree);
            }

            foreach (var kv in result.ExpansionLines)
            {
                foreach (var d in after.GetSemanticModel(kv.Key).GetDiagnostics())
                {
                    if (d.Severity != DiagnosticSeverity.Error) continue;
                    var pos = d.Location.GetLineSpan().StartLinePosition;
                    if (!kv.Value.TryGetValue(pos.Line + 1, out var macros)) continue;
                    diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "CSM013",
                        $"code generated by {macros} doesn't compile: {d.Id}: {d.GetMessage()}",
                        kv.Key.FilePath, pos.Line + 1, pos.Character + 1));
                }
            }

            if (startupTree == null) return;
            foreach (var d in after.GetSemanticModel(startupTree).GetDiagnostics())
            {
                if (d.Severity != DiagnosticSeverity.Error) continue;
                var mapped = d.Location.GetMappedLineSpan();
                var line = mapped.StartLinePosition.Line + 1;
                var macro = result.StartupFragments.FirstOrDefault(f => f.FilePath == mapped.Path && f.Line == line)?.MacroName ?? "an attribute macro";
                diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "CSM013",
                    $"startup code generated by [@{macro}] doesn't compile: {d.Id}: {d.GetMessage()}",
                    mapped.Path, line, 1));
            }
        }

        /// <summary>
        /// Member 宏的输出必须正好是一个字段或属性，名字和类型都与源码声明一致。
        /// 这样 IDE 眼里的 <c>public int Hp = @M(...)</c>（int 字段 Hp）和编译器实际拿到的成员，
        /// 对任何使用 Hp 的代码来说都是等价的。
        /// </summary>
        private static (string Code, string Message)? CheckMember(string code, string name, string type, CSharpParseOptions options)
        {
            var unit = SyntaxFactory.ParseCompilationUnit("class __CSMacroCheck { " + code + " }", options: options);
            if (FirstError(unit) is string parseError)
                return ("CSM011", $"emitted code is not a valid member declaration: {parseError}");

            var members = unit.Members.OfType<ClassDeclarationSyntax>().Single().Members;
            var declared = members.Select(DescribeMember).ToList();

            if (members.Count != 1 || declared[0] == null || declared[0]!.Value.Name != name)
            {
                var got = members.Count == 0
                    ? "nothing"
                    : string.Join(", ", declared.Select(d => d == null ? "a non-field/property member" : $"'{d.Value.Name}'"));
                return ("CSM008",
                    $"must emit exactly one field or property named '{name}' (the name declared in source), but emitted {got}. " +
                    "Macros may not introduce or remove names: the unexpanded source must stay statically correct, so the IDE never sees a name that doesn't exist");
            }

            if (Normalize(declared[0]!.Value.Type) != Normalize(type))
                return ("CSM009",
                    $"must keep the declared type '{type}', but emitted '{declared[0]!.Value.Type}'. " +
                    "Changing the type would make the IDE's view of '" + name + "' wrong");

            return null;

            static (string Name, string Type)? DescribeMember(MemberDeclarationSyntax m) => m switch
            {
                FieldDeclarationSyntax f when f.Declaration.Variables.Count == 1
                    => (f.Declaration.Variables[0].Identifier.ValueText, f.Declaration.Type.ToString()),
                PropertyDeclarationSyntax p => (p.Identifier.ValueText, p.Type.ToString()),
                _ => null,
            };

            static string Normalize(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        private static string? FirstError(SyntaxNode node) =>
            node.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.GetMessage();

        /// <summary>
        /// 把宏输出的代码重新分词再用空格连接：去掉换行和注释，整段落在调用所在的那一行。
        /// 预处理指令和跨行字面量无法压成一行，直接报错。
        /// </summary>
        private static string? ToSingleLine(string code, CSharpParseOptions options, out string? error)
        {
            error = null;
            var sb = new StringBuilder();
            var previous = "";
            foreach (var token in SyntaxFactory.ParseTokens(code, options: options))
            {
                if (token.IsKind(SyntaxKind.EndOfFileToken)) break;

                if (token.LeadingTrivia.Any(t => t.IsDirective) || token.TrailingTrivia.Any(t => t.IsDirective))
                {
                    error = "emitted code contains a preprocessor directive (#...), which can't be folded onto one line";
                    return null;
                }

                var text = token.ToString();
                if (text.IndexOf('\n') >= 0)
                {
                    error = "emitted code contains a literal that spans several lines; keep emitted literals on one line (use \\n escapes)";
                    return null;
                }

                if (sb.Length > 0 && NeedsSpace(previous, text)) sb.Append(' ');
                sb.Append(text);
                previous = text;
            }
            return sb.ToString();
        }

        // 只在括号、点号、分隔符旁边省略空格；其余情况一律保留，避免 "- -x" 粘成 "--x" 之类的问题。
        private static bool NeedsSpace(string previous, string next) =>
            !(previous == "(" || previous == "[" || previous == "." || previous == "::" || next == "::" ||
              next == ")" || next == "]" || next == ";" || next == "," || next == "." || next == "(" || next == "[");

        private static bool CalleeNameIsVerbatim(InvocationExpressionSyntax inv)
        {
            SimpleNameSyntax? name = inv.Expression switch
            {
                SimpleNameSyntax s => s,
                MemberAccessExpressionSyntax m => m.Name,
                _ => null,
            };
            return name != null && name.Identifier.Text.StartsWith("@", StringComparison.Ordinal);
        }

        private static string MetadataName(INamedTypeSymbol type)
        {
            var parts = new Stack<string>();
            for (var t = type; t != null; t = t.ContainingType) parts.Push(t.MetadataName);
            var name = string.Join("+", parts);
            return type.ContainingNamespace.IsGlobalNamespace ? name : type.ContainingNamespace.ToDisplayString() + "." + name;
        }

        private static NullableContextOptions ParseNullable(string? value) =>
            Enum.TryParse<NullableContextOptions>(value, ignoreCase: true, out var n) ? n : NullableContextOptions.Disable;

        private static LanguageVersion ParseLanguageVersion(string? value) =>
            !string.IsNullOrWhiteSpace(value) && LanguageVersionFacts.TryParse(value, out var v) ? v : LanguageVersion.Preview;

        private static MacroDiagnostic Diag(DiagnosticLevel level, string code, string message, SyntaxTree tree, SyntaxNode node)
        {
            var pos = tree.GetLineSpan(node.Span).StartLinePosition;
            return new MacroDiagnostic(level, code, message, tree.FilePath, pos.Line + 1, pos.Character + 1);
        }
    }
}
