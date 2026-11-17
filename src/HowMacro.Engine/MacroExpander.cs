using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HowMacro;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace HowMacro.Engine
{
    /// <summary>
    /// Expansion pipeline (a single pass, no rounds):
    /// 0. Build a Roslyn compilation from all sources + references (semantic analysis only). Macros are recognized semantically, not by the @ text:
    ///    method macro = the called method has [Macro]; attribute macro = the attribute class has [AttributeMacro].
    ///    Macros can't contain macros: a macro inside a method macro's arguments is an error.
    /// 1. Attribute macro phase: run every attribute macro in file and line order, collecting the fragments and startup code they produce.
    /// 2. Method macro phase: expand every method macro in place; all fragments from phase 1 are available now.
    ///    Output is folded onto one line and padded with newlines so line numbers don't change; attribute macros are removed from the source.
    /// 3. Check that no macros appear in the expanded result (macro output can't contain macros either).
    /// 4. Collect the startup code into a generated startup script.
    /// 5. (CLI) Compile-check the macro-generated code and report which macro generated any error.
    /// </summary>
    public static partial class MacroExpander
    {
        private const string MacroAttributeName = "HowMacro.MacroAttribute";
        private const string AttributeMacroAttributeName = "HowMacro.AttributeMacroAttribute";

        /// <summary>File name of the generated startup script.</summary>
        public const string StartupFileName = "HowMacro.Startup.g.cs";

        public static ExpandResult Expand(IReadOnlyList<SourceFile> sources, ExpandOptions options, IMacroInvoker invoker)
        {
            var parseOptions = new CSharpParseOptions(
                ParseLanguageVersion(options.LanguageVersion),
                DocumentationMode.Parse, // needed by runtime cleanup: without parsed doc comments GetUsedAssemblyReferences counts every reference as used
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
                "HowMacro.Expansion",
                trees,
                references,
                new CSharpCompilationOptions(ParseOutputKind(options.OutputType), allowUnsafe: true,
                    nullableContextOptions: ParseNullable(options.Nullable)));

            var diagnostics = new List<MacroDiagnostic>();
            var result = ExpandTrees(compilation, trees, options, invoker, diagnostics);

            if (options.Verify && !diagnostics.Exists(d => d.Level == DiagnosticLevel.Error))
                Verify(compilation, trees, result, parseOptions, diagnostics);

            // Runtime cleanup: remove unused usings that import macro libraries, find macro assemblies no longer needed at runtime
            IReadOnlyList<SyntaxTree> finalTrees = result.Expanded;
            var runtimeUnused = new List<string>();
            var reliable = false;
            if (options.RuntimeCleanup && !diagnostics.Exists(d => d.Level == DiagnosticLevel.Error))
                (finalTrees, runtimeUnused, reliable) = RuntimeCleanup(compilation, trees, result.Expanded, result.StartupCode, parseOptions);

            var files = new List<ExpandedFile>();
            for (var i = 0; i < sources.Count; i++)
            {
                if (ReferenceEquals(finalTrees[i], trees[i])) continue;
                var header = "#line 1 \"" + EscapePath(sources[i].Path) + "\"\n";
                files.Add(new ExpandedFile(sources[i].Id, sources[i].Path, header + finalTrees[i].GetText()));
            }

            var generated = new List<GeneratedFile>();
            if (result.StartupCode != null) generated.Add(new GeneratedFile(StartupFileName, result.StartupCode));

            return new ExpandResult(files, generated, diagnostics, result.Expansions) { RuntimeUnusedReferences = runtimeUnused, RuntimeAnalysisReliable = reliable };
        }

        /// <summary>
        /// Expands <paramref name="targets"/> within an existing compilation (they must belong to it).
        /// A host can build its own compilation (the CLI) or reuse one that already exists elsewhere (e.g. a future Unity host).
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
            var regions = new List<RegionUse>[n];
            var failed = new bool[n];
            var nestedOuters = new HashSet<InvocationExpressionSyntax>(); // calls whose arguments contain macros: not run, to avoid misleading follow-on errors
            var defines = targets.Count > 0 ? ((CSharpParseOptions)targets[0].Options).PreprocessorSymbolNames.ToArray() : new string[0];
            var semantics = new MacroSemantics(compilation); // semantic information is based on the compilation before expansion: the IDE's view

            // ---- 0. discovery + macros can't contain macros ----
            for (var i = 0; i < n; i++)
            {
                models[i] = compilation.GetSemanticModel(targets[i]);
                calls[i] = FindMacroCalls(targets[i], models[i]);
                attrs[i] = FindAttributeMacros(targets[i], models[i]);
                regions[i] = FindRegionMacros(targets[i], diagnostics, out var regionFailed);
                failed[i] |= regionFailed;
                // Core principle safety net: code at macro call sites must be valid before expansion
                if (!CheckUnexpandedSource(targets[i], models[i], calls[i], attrs[i], diagnostics)) failed[i] = true;

                foreach (var c in calls[i].ToList())
                {
                    var outer = calls[i].FirstOrDefault(o => o != c && o.Node.ArgumentList.Span.Contains(c.Node.Span));
                    if (outer == null) continue;
                    nestedOuters.Add(outer.Node);
                    diagnostics.Add(Diag(DiagnosticLevel.Error, "HMC012",
                        $"macros can't contain macros: @{c.Method.Name} is inside the arguments of @{outer.Method.Name}. " +
                        "Expand it separately first, e.g. into a local variable",
                        targets[i], c.Node));
                    failed[i] = true;
                }
            }

            // ---- macro filter (for debugging): macros not selected stay as they are ----
            var selected = MacroSelector(options.Macros);
            for (var i = 0; i < n; i++)
            {
                calls[i] = calls[i].Where(c => selected("@" + c.Method.Name)).ToList();
                attrs[i] = attrs[i].Where(a => selected("[@" + a.Ctor.ContainingType.Name + "]")).ToList();
                regions[i] = regions[i].Where(r => selected("@" + r.Name)).ToList();
            }
            var regionIndex = regions.Any(r => r.Count > 0) ? RegionMacroIndex(compilation) : null;
            var records = new List<MacroExpansion>();

            // ---- 1. attribute macro phase ----
            var fragments = new Dictionary<string, List<MacroFragment>>(StringComparer.Ordinal);
            var startup = new List<MacroFragment>();
            // Don't stop at the first error: run every macro so all errors are reported at once; files with errors just aren't replaced.
            foreach (var i in Enumerable.Range(0, n).OrderBy(i => targets[i].FilePath, StringComparer.Ordinal))
            {
                foreach (var use in attrs[i])
                {
                    if (!RunAttributeMacro(use, targets[i], models[i], compilation, invoker, defines, semantics, diagnostics, fragments, startup, records))
                        failed[i] = true;
                }
            }
            var available = fragments.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<MacroFragment>)kv.Value, StringComparer.Ordinal);

            // ---- 2. method macro phase ----
            var expanded = targets.ToArray();
            var expansionLines = new Dictionary<SyntaxTree, Dictionary<int, string>>();
            var transformed = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default); // members a macro turned from a field into a property → macro name
            for (var i = 0; i < n; i++)
            {
                if (calls[i].Count == 0 && attrs[i].Count == 0 && regions[i].Count == 0) continue;

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
                        diagnostics.Add(Diag(DiagnosticLevel.Error, "HMC014",
                            $"@{call.Method.Name} replaces code that overlaps with another macro on the same declaration", tree, call.Node));
                        failed[i] = true;
                        continue;
                    }

                    changes.Add(change.Value);
                    records.Add(Record(tree, change.Value, call));
                    if (call.UsedSite == MacroSite.Member && call.Node.Parent?.Parent is VariableDeclaratorSyntax declarator &&
                        SyntaxFactory.ParseMemberDeclaration(change.Value.NewText!.Trim()) is PropertyDeclarationSyntax &&
                        models[i].GetDeclaredSymbol(declarator) is { } field)
                        transformed[field] = "@" + call.Method.Name;
                    var line = tree.GetLineSpan(change.Value.Span).StartLinePosition.Line + 1;
                    lines[line] = lines.TryGetValue(line, out var names) ? names + ", @" + call.Method.Name : "@" + call.Method.Name;
                }

                // Region macros (same phase as method macros, after all attribute macros)
                foreach (var region in regions[i])
                {
                    if (!ExpandRegion(region, tree, models[i], compilation, invoker, defines, semantics, macroAttributes, available,
                            calls[i], attrs[i], regionIndex!, changes, lines, records, transformed, diagnostics))
                        failed[i] = true;
                }
                if (failed[i]) continue;

                // Remove attribute macros (except where the declaration was replaced as a whole by a Member macro: the Attributes it received already exclude attribute macros)
                foreach (var list in attrs[i].Select(a => (AttributeListSyntax)a.Node.Parent!).Distinct())
                {
                    if (changes.Any(c => c.Span.Contains(list.Span))) continue;
                    var newlines = tree.GetText().ToString(list.Span).Count(ch => ch == '\n');
                    changes.Add(new TextChange(list.Span, WithoutMacroAttributes(list, macroAttributes) + new string('\n', newlines)));
                }

                changes.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
                if (changes.Zip(changes.Skip(1), (a, b) => a.Span.End > b.Span.Start).Any(x => x))
                {
                    diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "HMC014",
                        "two macros replace overlapping code in this file", tree.FilePath, 1, 1));
                    failed[i] = true;
                    continue;
                }
                var newTree = tree.WithChangedText(tree.GetText().WithChanges(changes));
                expanded[i] = newTree;
                expansionLines[newTree] = lines;
            }

            // ---- 2b. once a field becomes a property, report field-only uses (ref/out, address-of, struct copy mutation) up front ----
            CheckPropertyUses(transformed, targets, models, failed, diagnostics);

            // ---- 3. macro output can't contain macros either ----
            var after = compilation;
            for (var i = 0; i < n; i++)
                if (!ReferenceEquals(expanded[i], targets[i])) after = after.ReplaceSyntaxTree(targets[i], expanded[i]);

            // ---- 3a. private fields a macro added for its own state can't be used by the source (they don't exist before expansion) ----
            CheckPrivateStateUses(after, targets, expanded, models, failed, diagnostics);

            for (var i = 0; i < n; i++)
            {
                if (failed[i] || ReferenceEquals(expanded[i], targets[i])) continue;
                var model = after.GetSemanticModel(expanded[i]);
                var lines = expansionLines[expanded[i]];

                IEnumerable<(SyntaxNode Node, string Name)> leftovers =
                    FindMacroCalls(expanded[i], model).Select(c => ((SyntaxNode)c.Node, "@" + c.Method.Name))
                    .Concat(FindAttributeMacros(expanded[i], model).Select(a => ((SyntaxNode)a.Node, "[@" + a.Ctor.ContainingType.Name + "]")));

                leftovers = leftovers.Concat(RegionMacroDirectives(expanded[i]).Select(r => ((SyntaxNode)r, "#region " + RegionMessage(r))));

                foreach (var (node, macro) in leftovers)
                {
                    if (!selected(macro)) continue; // macros that weren't selected stay as they are anyway
                    var line = expanded[i].GetLineSpan(node.Span).StartLinePosition.Line + 1;
                    var by = lines.TryGetValue(line, out var names) ? names : "a macro";
                    diagnostics.Add(Diag(DiagnosticLevel.Error, "HMC012",
                        $"macros can't contain macros: the output of {by} contains {macro}", expanded[i], node));
                    failed[i] = true;
                }
            }

            // ---- 4. startup code → generated startup script ----
            var parseOptions = n > 0 ? (CSharpParseOptions)targets[0].Options : CSharpParseOptions.Default;
            var startupCode = BuildStartupScript(startup, options.StartupAttribute, parseOptions, diagnostics);

            var ordered = records.OrderBy(r => r.FilePath, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
            return new ExpansionResult(expanded, expansionLines, startupCode, startup, ordered);
        }

        // ================================================================== Attribute macros

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
                diagnostics.Add(Diag(DiagnosticLevel.Warning, "HMC010",
                    $"attribute macro '{name}' should be written as [@{name}] so readers can tell it is expanded at compile time",
                    tree, attr));

            var list = (AttributeListSyntax)attr.Parent!;
            if (list.Target != null)
                return Fail("HMC003", $"[@{name}] can't have an attribute target specifier ('{list.Target.Identifier.Text}:')");

            MacroTarget target;
            string? memberName, memberType = null, modifiers;
            INamedTypeSymbol? containing;
            ISymbol? declared = null;
            switch (list.Parent)
            {
                case FieldDeclarationSyntax f:
                    if (f.Declaration.Variables.Count != 1)
                        return Fail("HMC003", $"[@{name}] must be on a field declaration with exactly one variable");
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
                    return Fail("HMC003", $"[@{name}] can only be used on fields, properties, methods and types");
            }

            if ((use.Targets & target) == 0)
                return Fail("HMC003", $"[@{name}] can't be used on a {target.ToString().ToLowerInvariant()}; it is declared for: {use.Targets}");

            var reference = ResolveMacroAssembly(use.Ctor.ContainingType, compilation);
            if (reference == null)
                return Fail("HMC004",
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
                var kind = context.ProducedStartup.Contains(f) ? "Startup" : "Fragment:" + f.Channel;
                // Startup code is C#, shown as expanded; fragment content is free text (not necessarily C#), shown verbatim
                var code = kind == "Startup" ? ToSingleLine(f.Code, (CSharpParseOptions)tree.Options, out _) ?? f.Code : f.Code;
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
                .Append("// HowMacro: startup code produced by attribute macros (Macro.Startup).\n")
                .Append("// Each statement is mapped back to the attribute that produced it.\n")
                .Append("// </auto-generated>\n")
                .Append("namespace HowMacro.Generated\n{\n")
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
                    diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "HMC011", $"[@{f.MacroName}]: {error}", f.FilePath, f.Line, 1));
                    continue;
                }

                sb.Append("#line ").Append(f.Line).Append(" \"").Append(EscapePath(f.FilePath)).Append("\"\n")
                  .Append("            { ").Append(oneLine).Append(" }\n");
            }

            return sb.Append("#line default\n        }\n    }\n}\n").ToString();
        }

        // ================================================================== Method macros

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

            /// <summary>The site actually matched during expansion.</summary>
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
                diagnostics.Add(Diag(DiagnosticLevel.Warning, "HMC010",
                    $"macro '{name}' should be called as @{name}(...) so readers can tell it is expanded at compile time",
                    tree, inv));

            // ---- determine the site ----
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
                            return Fail("HMC003", $"@{name} replaces the whole field declaration, so it must declare exactly one variable");
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
                    return Fail("HMC003", $"@{name} can't be used here; it is declared for: {call.Sites}");
                }
            }

            // ---- locate the assembly containing the macro method ----
            var def = call.Method.OriginalDefinition;
            var reference = ResolveMacroAssembly(def.ContainingType, compilation);
            if (reference == null)
                return Fail("HMC004",
                    $"@{name} is declared in the same project that uses it. Macros must live in a referenced assembly, " +
                    "because they are compiled and executed before this project is compiled");

            var method = new MacroMethod(reference, MetadataName(def.ContainingType), def.Name, def.Arity, def.Parameters.Length);

            // ---- build the context ----
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

            // ---- run the macro ----
            if (!Run(invoker, method, context, "@" + name, tree, inv, diagnostics)) return null;

            var emitted = context.Output.ToString();
            if (site == MacroSite.Expression && string.IsNullOrWhiteSpace(emitted))
                return Fail("HMC006", $"@{name} is used as an expression but emitted no code");

            // ---- fold onto one line + pad newlines ----
            var oneLine = ToSingleLine(emitted, parseOptions, out var compressError);
            if (oneLine == null) return Fail("HMC007", $"@{name}: {compressError}");

            // ---- core principle: the unexpanded project must stay statically correct ----
            // Names and types are declared only by the source; macros only change implementation. Expansion must not introduce names the IDE can't see.
            switch (site)
            {
                case MacroSite.Expression:
                    oneLine = "(" + oneLine + ")";
                    var expr = SyntaxFactory.ParseExpression(oneLine, options: parseOptions, consumeFullText: true);
                    if (FirstError(expr) is string exprError)
                        return Fail("HMC011", $"@{name} emitted code that is not a valid expression: {exprError} -- emitted: {oneLine}");
                    break;

                case MacroSite.Statement:
                    // Wrap in a block: locals declared inside the macro can't escape it, so later code can't see them and they can't clash with outer names.
                    oneLine = "{ " + oneLine + " }";
                    var block = SyntaxFactory.ParseStatement(oneLine, options: parseOptions, consumeFullText: true);
                    if (FirstError(block) is string stmtError)
                        return Fail("HMC011", $"@{name} emitted code that is not valid statements: {stmtError} -- emitted: {oneLine}");
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

        // ================================================================== Shared

        /// <summary>Runs a macro once, turning exceptions and the macro's own messages into diagnostics. Returns whether it succeeded.</summary>
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
                diagnostics.Add(Diag(DiagnosticLevel.Error, "HMC005", $"{display} threw {ex.GetType().Name}: {ex.Message}", tree, node));
                return false;
            }

            var ok = true;
            foreach (var msg in context.Messages)
            {
                var isError = msg.Severity == MacroMessageSeverity.Error;
                ok &= !isError;
                diagnostics.Add(Diag(isError ? DiagnosticLevel.Error : DiagnosticLevel.Warning,
                    isError ? "HMC100" : "HMC101", $"{display}: {msg.Text}", tree, node));
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

        /// <summary>Reformats code folded onto one line into multiple lines; only used for the report.</summary>
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

        // ================================================================== Compile check

        /// <summary>
        /// Compiles the expanded code, looking only at macro-generated lines (and the startup script). Errors are reported with the macro that generated them,
        /// so developers know the problem is in a macro rather than their own code; other errors are left to the compiler.
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
                    diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "HMC013",
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
                diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "HMC013",
                    $"startup code generated by [@{macro}] doesn't compile: {d.Id}: {d.GetMessage()}",
                    mapped.Path, line, 1));
            }
        }

        /// <summary>
        /// A Member macro's output must be exactly one field or property whose name and type match the source declaration.
        /// That way what the IDE sees in <c>public int Hp = @M(...)</c> (an int field Hp) and the member the compiler actually gets
        /// are equivalent to any code that uses Hp.
        /// Besides it, the output may declare private fields for the macro's own state (e.g. the storage behind a property:
        /// <c>private int @_temp_Hp;</c>). Nothing outside the type can see them and the source can't name them (they don't
        /// exist before expansion), so the IDE's view stays correct. By convention they are named <c>@_temp_&lt;member&gt;</c>.
        /// </summary>
        private static (string Code, string Message)? CheckMember(string code, string name, string type, CSharpParseOptions options)
        {
            var unit = SyntaxFactory.ParseCompilationUnit("class __HowMacroCheck { " + code + " }", options: options);
            if (FirstError(unit) is string parseError)
                return ("HMC011", $"emitted code is not a valid member declaration: {parseError}");

            var members = unit.Members.OfType<ClassDeclarationSyntax>().Single().Members
                .Where(m => !IsPrivateState(m, name)).ToList();
            var declared = members.Select(DescribeMember).ToList();

            if (members.Count != 1 || declared[0] == null || declared[0]!.Value.Name != name)
            {
                var got = members.Count == 0
                    ? "nothing"
                    : string.Join(", ", declared.Select(d => d == null ? "a non-field/property member" : $"'{d.Value.Name}'"));
                return ("HMC008",
                    $"must emit exactly one field or property named '{name}' (the name declared in source), but emitted {got}. " +
                    "Macros may not introduce or remove names: the unexpanded source must stay statically correct, so the IDE never sees a name that doesn't exist. " +
                    $"For the macro's own state, add private fields (by convention named @_temp_{name})");
            }

            if (Normalize(declared[0]!.Value.Type) != Normalize(type))
                return ("HMC009",
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

            // A private field (no access modifier, or `private`) that isn't the declared member itself
            static bool IsPrivateState(MemberDeclarationSyntax m, string declared) =>
                m is FieldDeclarationSyntax f
                && !f.Modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword) || t.IsKind(SyntaxKind.InternalKeyword) || t.IsKind(SyntaxKind.ProtectedKeyword))
                && f.Declaration.Variables.All(v => v.Identifier.ValueText != declared);
        }

        private static string? FirstError(SyntaxNode node) =>
            node.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.GetMessage();

        /// <summary>
        /// Re-tokenizes the macro's output and joins it with spaces: newlines and comments are dropped and everything lands on the call's line.
        /// Preprocessor directives and multi-line literals can't be folded onto one line and are reported as errors.
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

        // Spaces are omitted only next to parentheses, dots and separators; everywhere else they are kept, so "- -x" never becomes "--x".
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

        private static OutputKind ParseOutputKind(string? value) => value?.ToLowerInvariant() switch
        {
            "exe" => OutputKind.ConsoleApplication,
            "winexe" => OutputKind.WindowsApplication,
            "module" => OutputKind.NetModule,
            _ => OutputKind.DynamicallyLinkedLibrary,
        };

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
