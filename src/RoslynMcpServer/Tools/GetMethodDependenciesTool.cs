using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Roslyn;

namespace RoslynMcpServer.Tools;

public sealed class GetMethodDependenciesTool
{
    private readonly WorkspaceHost _workspaceHost;

    public GetMethodDependenciesTool(WorkspaceHost workspaceHost)
    {
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
    }

    /// <summary>
    /// Execute the tool.
    /// Arguments JSON supports either:
    ///  - { "fullyQualifiedName": "My.Namespace.Type.Member(params)" }
    /// or
    ///  - { "file": "/abs/path.cs", "line": 42, "column": 17 }
    ///
    /// Optional flags:
    ///  - depth (int, default: 1) -> depth==1 = bezpośrednie zależności; >1 = tranzytywne (DFS/BFS)
    ///  - includeCallers (bool, default: false)
    ///  - treatPropertiesAsMethods (bool, default: true) => raportuj get_/set_ jako calls
    ///  - page/pageSize (int) -> proste stronicowanie wyniku "calls" (największa część); reads/writes zwracane w całości
    ///  - timeoutMs (int, default: 60000)
    /// </summary>
    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.HasValue || arguments.Value.ValueKind == JsonValueKind.Null)
                return CreateErrorResult("Missing arguments");

            var solution = _workspaceHost.GetSolution();
            if (solution is null)
                return CreateErrorResult("No solution loaded. Please load a solution first.");

            // Parse args
            string? fqn = TryGetString(arguments.Value, "fullyQualifiedName");
            string? file = TryGetString(arguments.Value, "file");
            int line = TryGetInt(arguments.Value, "line") ?? -1;
            int column = TryGetInt(arguments.Value, "column") ?? -1;

            int depth = Math.Max(1, TryGetInt(arguments.Value, "depth") ?? 1);
            bool includeCallers = TryGetBool(arguments.Value, "includeCallers") ?? false;
            bool treatPropertiesAsMethods = TryGetBool(arguments.Value, "treatPropertiesAsMethods") ?? true;

            int page = Math.Max(1, TryGetInt(arguments.Value, "page") ?? 1);
            int pageSize = Math.Clamp(TryGetInt(arguments.Value, "pageSize") ?? 200, 1, 500);
            int timeoutMs = Math.Clamp(TryGetInt(arguments.Value, "timeoutMs") ?? 60000, 1000, 300000);

            // Validate input parameters - require either fullyQualifiedName OR (file + line + column)
            if (string.IsNullOrEmpty(fqn))
            {
                if (string.IsNullOrEmpty(file) || line <= 0 || column <= 0)
                {
                    return CreateErrorResult("Provide either fullyQualifiedName OR file+line+column.");
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            var ct = cts.Token;

            // Resolve target method symbol (enhanced logic per spec)
            var methodResolution = fqn is not null
                ? await ResolveMethodSymbolByFqnAsync(solution, fqn, ct)
                : await ResolveMethodSymbolByLocationAsync(solution, file, line, column, treatPropertiesAsMethods, ct);

            if (methodResolution.Method is null)
            {
                // If we resolved to a type but ambiguous, return candidates with hint
                if (methodResolution.Type is not null && methodResolution.Candidates is { Count: > 0 })
                {
                    var payload = new
                    {
                        success = false,
                        error = "Ambiguous: fullyQualifiedName resolves to a type with multiple candidates. Specify either ..ctor or a concrete method.",
                        candidates = methodResolution.Candidates.Select(DescribeCandidate),
                        hint = "Try fullyQualifiedName: \"My.Namespace.Type..ctor\" or \"My.Namespace.Type.MethodName\" (optionally with signature)."
                    };
                    return CreateErrorStructured(payload);
                }

                var msg = methodResolution.Error ?? $"Could not resolve a method symbol (input: {(fqn ?? $"{file}:{line}:{column}")}).";
                return CreateErrorResult(msg);
            }

            var rootMethod = methodResolution.Method;

            // Traverse dependencies (direct or transitive)
            var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var callSet = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var readSet = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var writeSet = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            // BFS over call graph up to "depth"
            var queue = new Queue<(IMethodSymbol method, int level)>();
            queue.Enqueue((rootMethod, 1));
            visited.Add(rootMethod);

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var (currentMethod, level) = queue.Dequeue();

                // Analyze a single method body using IOperation
                await AnalyzeMethodBodyAsync(currentMethod, callSet, readSet, writeSet, treatPropertiesAsMethods, ct);

                if (level < depth)
                {
                    // Enqueue next level: direct calls from currentMethod
                    foreach (var callee in callSet.Where(m => !visited.Contains(m)))
                    {
                        visited.Add(callee);
                        queue.Enqueue((callee, level + 1));
                    }
                }
            }

            // Prepare "callers" (optional, only for the root symbol)
            var callersList = new List<object>();
            if (includeCallers)
            {
                var callers = await SymbolFinder.FindCallersAsync(rootMethod, solution, ct);
                foreach (var c in callers)
                {
                    // Try to pick a source location if available
                    var loc = c.CallingSymbol != null ? c.CallingSymbol.Locations.FirstOrDefault(l => l.IsInSource) : null;
                    var desc = c.CallingSymbol is null ? null : SymbolFormatting.Describe(c.CallingSymbol);
                    callersList.Add(new
                    {
                        display = desc?.Display ?? c.CallingSymbol?.ToDisplayString() ?? "<unknown>",
                        file = desc?.File,
                        line = desc?.Line,
                        column = desc?.Column,
                        isDirect = c.IsDirect,
                        callSites = c.Locations.Select(l => new
                        {
                            file = TryGetFilePath(l),
                            line = TryGetLine(l),
                            column = TryGetColumn(l)
                        })
                    });
                }
            }

            // Turn symbols → presentation objects
            var callsArray = callSet
                .Select(s => SymbolFormatting.Describe(s))
                .ToArray();

            var readsArray = readSet
                .Select(s => SymbolFormatting.Describe(s))
                .ToArray();

            var writesArray = writeSet
                .Select(s => SymbolFormatting.Describe(s))
                .ToArray();

            var targetDesc = SymbolFormatting.Describe(rootMethod);

            // Simple paging for "calls" (often the largest)
            var totalCalls = callsArray.Length;
            var callsPaged = callsArray
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();

            string? nextCursor = null;
            if ((page * pageSize) < totalCalls)
                nextCursor = $"page={page + 1}";

            var result = new
            {
                success = true,
                symbol = new
                {
                    display = targetDesc.Display,
                    file = targetDesc.File,
                    line = targetDesc.Line,
                    column = targetDesc.Column,
                    kind = rootMethod.Kind.ToString(),
                    containingType = rootMethod.ContainingType?.ToDisplayString(),
                    containingNamespace = rootMethod.ContainingNamespace?.ToDisplayString()
                },
                depth,
                page,
                pageSize,
                totalCalls,
                nextCursor,
                calls = callsPaged.Select(d => new
                {
                    display = d.Display,
                    file = d.File,
                    line = d.Line,
                    column = d.Column
                }),
                reads = readsArray.Select(d => new
                {
                    display = d.Display,
                    file = d.File,
                    line = d.Line,
                    column = d.Column
                }),
                writes = writesArray.Select(d => new
                {
                    display = d.Display,
                    file = d.File,
                    line = d.Line,
                    column = d.Column
                }),
                callers = callersList
            };

            var elem = JsonSerializer.SerializeToElement(result);
            return new ToolCallResult
            {
                Content = new List<ToolContent>
                {
                    new ToolContent
                    {
                        Type = "text",
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                },
                StructuredContent = elem
            };
        }
        catch (OperationCanceledException)
        {
            return CreateErrorResult("Operation canceled (timeout or external cancellation).");
        }
        catch (Exception ex)
        {
            var err = new
            {
                success = false,
                error = "Unhandled exception in get_method_dependencies",
                exception = ex.GetType().FullName,
                message = ex.Message,
                stack = ex.StackTrace
            };

            return new ToolCallResult
            {
                IsError = true,
                Content = new List<ToolContent>
                {
                    new ToolContent
                    {
                        Type = "text",
                        Text = JsonSerializer.Serialize(err, new JsonSerializerOptions { WriteIndented = true })
                    }
                },
                StructuredContent = err
            };
        }
    }

    #region Core analysis

    private async Task AnalyzeMethodBodyAsync(
        IMethodSymbol method,
        HashSet<IMethodSymbol> calls,
        HashSet<ISymbol> reads,
        HashSet<ISymbol> writes,
        bool treatPropertiesAsMethods,
        CancellationToken ct)
    {
        // Prefer first source declaration
        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef is null)
            return; // metadata-only or external

        var syntax = await syntaxRef.GetSyntaxAsync(ct);
        var doc = method.DeclaringSyntaxReferences
            .Select(r => r.SyntaxTree)
            .Select(tree => FindDocumentByTree(tree))
            .FirstOrDefault(d => d != null);

        // Fallback: get Document by SyntaxTree from any project in solution
        Document? document = doc ?? FindDocumentByTree(syntax.SyntaxTree);
        if (document is null)
            return;

        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null)
            return;

        // Body or expression-body
        var bodyNode =
            syntax.ChildNodes().FirstOrDefault(n => n.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.Block) ??
            syntax.ChildNodes().FirstOrDefault(n => n.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.ArrowExpressionClause) ??
            syntax;

        var op = model.GetOperation(bodyNode, ct);
        if (op is null)
            return;

        // Walk IOperation tree
        var walker = new OperationWalker(treatPropertiesAsMethods);
        walker.Walk(op);

        foreach (var m in walker.Calls)
            calls.Add(m);

        foreach (var r in walker.Reads)
            reads.Add(r);

        foreach (var w in walker.Writes)
            writes.Add(w);
    }

    private sealed class OperationWalker
    {
        private readonly bool _propsAsMethods;

        public HashSet<IMethodSymbol> Calls { get; } = new(SymbolEqualityComparer.Default);
        public HashSet<ISymbol> Reads { get; } = new(SymbolEqualityComparer.Default);
        public HashSet<ISymbol> Writes { get; } = new(SymbolEqualityComparer.Default);

        public OperationWalker(bool propsAsMethods)
        {
            _propsAsMethods = propsAsMethods;
        }

        public void Walk(IOperation root)
        {
            Visit(root, writeContext: false);
        }

        private void Visit(IOperation? node, bool writeContext)
        {
            if (node is null) return;

            switch (node)
            {
                case IInvocationOperation inv:
                    var target = inv.TargetMethod?.ReducedFrom ?? inv.TargetMethod;
                    if (target != null)
                        Calls.Add(target);
                    break;

                case IObjectCreationOperation ctor:
                    if (ctor.Constructor != null)
                        Calls.Add(ctor.Constructor);
                    break;

                case IPropertyReferenceOperation propRef:
                    if (writeContext)
                    {
                        Writes.Add(propRef.Property);
                        if (_propsAsMethods && propRef.Property?.SetMethod != null)
                            Calls.Add(propRef.Property.SetMethod);
                    }
                    else
                    {
                        Reads.Add(propRef.Property);
                        if (_propsAsMethods && propRef.Property?.GetMethod != null)
                            Calls.Add(propRef.Property.GetMethod);
                    }
                    break;

                case IFieldReferenceOperation fieldRef:
                    if (writeContext) Writes.Add(fieldRef.Field);
                    else Reads.Add(fieldRef.Field);
                    break;

                case IEventReferenceOperation evtRef:
                    // Treat event add/remove as writes to event symbol (and optionally as synthetic calls)
                    if (writeContext) Writes.Add(evtRef.Event);
                    else Reads.Add(evtRef.Event);
                    break;

                case ICompoundAssignmentOperation compound:
                    // Both read and write of the target (like x += y)
                    MarkReadWrite(compound.Target);
                    Visit(compound.Value, writeContext: false);
                    return;

                case IAssignmentOperation assign:
                    // Left in write context, right in read context
                    Visit(assign.Target, writeContext: true);
                    Visit(assign.Value, writeContext: false);
                    return; // children visited with explicit contexts

                case IIncrementOrDecrementOperation incdec:
                    // ++x / x++ : both read & write
                    MarkReadWrite(incdec.Target);
                    return;

                case IArgumentOperation arg:
                    // ref/out implies write into referenced location; in and default = read
                    var isWrite = arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out;
                    Visit(arg.Value, writeContext: isWrite);
                    return;

                default:
                    break;
            }

            foreach (var child in node.ChildOperations)
                Visit(child, writeContext);
        }

        private void MarkReadWrite(IOperation target)
        {
            switch (target)
            {
                case IPropertyReferenceOperation pr:
                    Reads.Add(pr.Property);
                    Writes.Add(pr.Property);
                    if (_propsAsMethods)
                    {
                        if (pr.Property?.GetMethod is { } gm) Calls.Add(gm);
                        if (pr.Property?.SetMethod is { } sm) Calls.Add(sm);
                    }
                    break;

                case IFieldReferenceOperation fr:
                    Reads.Add(fr.Field);
                    Writes.Add(fr.Field);
                    break;

                default:
                    Visit(target, writeContext: false);
                    Visit(target, writeContext: true);
                    break;
            }
        }
    }

    #endregion

    #region Symbol resolution helpers

    private sealed record MethodResolution(IMethodSymbol? Method, Document? Document, INamedTypeSymbol? Type, List<IMethodSymbol>? Candidates, string? Error);

    private async Task<MethodResolution> ResolveMethodSymbolByFqnAsync(Solution solution, string fqn, CancellationToken ct)
    {
        // Split optional parameter list
        string baseFqn = fqn;
        string? paramList = null;
        var parenIdx = fqn.IndexOf('(');
        if (parenIdx >= 0)
        {
            baseFqn = fqn.Substring(0, parenIdx);
            var endParen = fqn.LastIndexOf(')');
            if (endParen > parenIdx)
                paramList = fqn.Substring(parenIdx + 1, endParen - parenIdx - 1).Trim();
        }

        // Try to resolve as exact type name first
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;

            var type = compilation.GetTypeByMetadataName(baseFqn);
            if (type is INamedTypeSymbol namedType)
            {
                var ctors = namedType.Constructors.Where(c => !c.IsImplicitlyDeclared).ToList();
                var methods = namedType.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind != MethodKind.Constructor && !m.IsImplicitlyDeclared)
                    .ToList();

                if (paramList == null)
                {
                    // No member specified. If exactly one .ctor → use it; otherwise ambiguous
                    if (ctors.Count == 1)
                    {
                        var doc = await FindDocumentForSymbolAsync(solution, ctors[0], ct);
                        return new MethodResolution(ctors[0], doc, namedType, null, null);
                    }
                    else
                    {
                        var cands = new List<IMethodSymbol>();
                        cands.AddRange(ctors);
                        cands.AddRange(methods);
                        return new MethodResolution(null, null, namedType, cands, null);
                    }
                }
            }
        }

        // Try to split into Type + Member
        var lastDot = baseFqn.LastIndexOf('.');
        if (lastDot > 0)
        {
            var typeName = baseFqn.Substring(0, lastDot);
            var memberName = baseFqn.Substring(lastDot + 1);

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is null) continue;
                var t = compilation.GetTypeByMetadataName(typeName);
                if (t is null) continue;

                // Constructors explicitly
                if (string.Equals(memberName, ".ctor", StringComparison.Ordinal))
                {
                    var ctors = t.Constructors.Where(c => !c.IsImplicitlyDeclared).ToList();
                    var chosen = ChooseBySignature(ctors, paramList);
                    if (chosen is not null)
                        return new MethodResolution(chosen, await FindDocumentForSymbolAsync(solution, chosen, ct), t, null, null);
                    return new MethodResolution(null, null, t, ctors, null);
                }

                // Property accessors when requested
                var prop = t.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(p => string.Equals(p.Name, memberName, StringComparison.Ordinal));
                if (prop != null)
                {
                    var accessor = prop.GetMethod ?? prop.SetMethod;
                    if (accessor != null)
                        return new MethodResolution(accessor, await FindDocumentForSymbolAsync(solution, accessor, ct), t, null, null);
                }

                // Methods by name
                var methods = t.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => !m.IsImplicitlyDeclared && string.Equals(m.Name, memberName, StringComparison.Ordinal))
                    .ToList();
                if (methods.Count == 1 && paramList == null)
                    return new MethodResolution(methods[0], await FindDocumentForSymbolAsync(solution, methods[0], ct), t, null, null);

                var chosenBySig = ChooseBySignature(methods, paramList);
                if (chosenBySig is not null)
                    return new MethodResolution(chosenBySig, await FindDocumentForSymbolAsync(solution, chosenBySig, ct), t, null, null);

                if (methods.Count > 0)
                    return new MethodResolution(null, null, t, methods, null);
            }
        }

        // Fallback: search declarations by name
        foreach (var project in solution.Projects)
        {
            var decls = await SymbolFinder.FindDeclarationsAsync(project, baseFqn, ignoreCase: false, ct).ConfigureAwait(false);
            var method = decls.OfType<IMethodSymbol>().FirstOrDefault();
            if (method != null)
                return new MethodResolution(method, await FindDocumentForSymbolAsync(solution, method, ct), method.ContainingType, null, null);
        }

        return new MethodResolution(null, null, null, null, null);
    }

    private async Task<MethodResolution> ResolveMethodSymbolByLocationAsync(
        Solution solution,
        string? file,
        int line,
        int column,
        bool treatPropertiesAsMethods,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(file) || line < 1 || column < 1)
            return new MethodResolution(null, null, null, null, "Invalid file/line/column");

        var docId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        if (docId == null)
            return new MethodResolution(null, null, null, null, "Document not found for file");

        var document = solution.GetDocument(docId);
        if (document == null)
            return new MethodResolution(null, null, null, null, "Document not found");

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var pos = text.Lines.GetPosition(new LinePosition(line - 1, column - 1));
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model == null)
            return new MethodResolution(null, document, null, null, "No semantic model");

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, pos, solution.Workspace, ct).ConfigureAwait(false);
        if (symbol is IMethodSymbol ms)
            return new MethodResolution(ms, document, null, null, null);
        if (symbol is IPropertySymbol ps && treatPropertiesAsMethods)
        {
            var acc = ps.GetMethod ?? ps.SetMethod;
            if (acc != null) return new MethodResolution(acc, document, null, null, null);
        }
        if (symbol is INamedTypeSymbol nt)
        {
            // Apply the type rules: single ctor -> analyze; otherwise report candidates
            var ctors = nt.Constructors.Where(c => !c.IsImplicitlyDeclared).ToList();
            var methods = nt.GetMembers().OfType<IMethodSymbol>()
                .Where(m => !m.IsImplicitlyDeclared && m.MethodKind != MethodKind.Constructor)
                .ToList();
            if (ctors.Count == 1)
                return new MethodResolution(ctors[0], await FindDocumentForSymbolAsync(solution, ctors[0], ct), nt, null, null);
            var cands = new List<IMethodSymbol>();
            cands.AddRange(ctors);
            cands.AddRange(methods);
            return new MethodResolution(null, document, nt, cands, null);
        }

        // Try enclosing or declared symbol
        var enclosing = model.GetEnclosingSymbol(pos, ct) as IMethodSymbol;
        if (enclosing != null)
            return new MethodResolution(enclosing, document, null, null, null);

        var root = await model.SyntaxTree.GetRootAsync(ct).ConfigureAwait(false);
        var node = root.FindToken(pos).Parent;
        var declared = model.GetDeclaredSymbol(node, ct) as IMethodSymbol;
        if (declared != null)
            return new MethodResolution(declared, document, null, null, null);

        return new MethodResolution(null, document, null, null, "No symbol at position");
    }

    private static IMethodSymbol? ChooseBySignature(List<IMethodSymbol> methods, string? paramList)
    {
        if (methods.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(paramList))
            return methods.Count == 1 ? methods[0] : null;

        var parts = paramList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(p => p.Trim())
                             .ToArray();
        foreach (var m in methods)
        {
            if (m.Parameters.Length != parts.Length) continue;
            bool allMatch = true;
            for (int i = 0; i < parts.Length; i++)
            {
                var expected = NormalizeTypeName(parts[i]);
                var actual = NormalizeTypeName(m.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                if (!StringComparer.Ordinal.Equals(expected, actual)) { allMatch = false; break; }
            }
            if (allMatch) return m;
        }
        return null;
    }

    private static string NormalizeTypeName(string name)
    {
        var s = name.Replace("global::", string.Empty);
        // unify common aliases
        return s switch
        {
            "int" => "System.Int32",
            "string" => "System.String",
            "bool" => "System.Boolean",
            "double" => "System.Double",
            "float" => "System.Single",
            "long" => "System.Int64",
            "short" => "System.Int16",
            "byte" => "System.Byte",
            "char" => "System.Char",
            _ => s
        };
    }

    private static object DescribeCandidate(IMethodSymbol m)
    {
        var d = SymbolFormatting.Describe(m);
        return new { display = d.Display, file = d.File, line = d.Line, column = d.Column, kind = m.Kind.ToString() };
    }

    private static async Task<Document?> FindDocumentForSymbolAsync(Solution solution, ISymbol symbol, CancellationToken ct)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc?.SourceTree == null) return null;
        var docId = solution.GetDocumentId(loc.SourceTree);
        if (docId == null) return null;
        return solution.GetDocument(docId);
    }

    private Document? FindDocumentByTree(SyntaxTree tree)
    {
        var solution = _workspaceHost.GetSolution();
        if (solution == null) return null;
        var id = solution.GetDocumentId(tree);
        return id != null ? solution.GetDocument(id) : null;
    }

    #endregion

    #region JSON helpers & formatting

    private static string? TryGetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? TryGetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out var i) ? i : (int?)null,
            JsonValueKind.String => int.TryParse(e.GetString(), out var j) ? j : (int?)null,
            _ => null
        };
    }

    private static bool? TryGetBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(e.GetString(), out var b) ? b : (bool?)null,
            _ => null
        };
    }

    private static string? TryGetFilePath(Location? loc)
        => loc is { IsInSource: true } ? loc.SourceTree?.FilePath : null;

    private static int? TryGetLine(Location? loc)
    {
        if (loc is null || !loc.IsInSource) return null;
        var span = loc.GetLineSpan();
        return span.StartLinePosition.Line + 1;
    }

    private static int? TryGetColumn(Location? loc)
    {
        if (loc is null || !loc.IsInSource) return null;
        var span = loc.GetLineSpan();
        return span.StartLinePosition.Character + 1;
    }

    private ToolCallResult CreateErrorResult(string message)
    {
        var result = new { success = false, error = message };

        return new ToolCallResult
        {
            IsError = true,
            Content = new List<ToolContent>
            {
                new ToolContent
                {
                    Type = "text",
                    Text = JsonSerializer.Serialize(result)
                }
            }
        };
    }

    private ToolCallResult CreateErrorStructured(object payload)
    {
        var text = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new ToolCallResult
        {
            IsError = true,
            Content = new List<ToolContent>
            {
                new ToolContent { Type = "text", Text = text }
            },
            StructuredContent = JsonSerializer.SerializeToElement(payload)
        };
    }

    #endregion
}
