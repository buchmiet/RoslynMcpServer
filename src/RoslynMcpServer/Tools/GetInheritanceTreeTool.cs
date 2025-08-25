using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Roslyn;

namespace RoslynMcpServer.Tools;

public sealed class GetInheritanceTreeTool
{
    private readonly WorkspaceHost _workspaceHost;

    public GetInheritanceTreeTool(WorkspaceHost workspaceHost)
    {
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
    }

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.HasValue || arguments.Value.ValueKind == JsonValueKind.Null)
                return Error("Missing arguments");

            var solution = _workspaceHost.GetSolution();
            if (solution is null)
                return Error("No solution loaded. Please load a solution first.");

            var args = arguments.Value;
            string? fqn = TryGetString(args, "fullyQualifiedName");
            string? file = TryGetString(args, "file");
            int line = TryGetInt(args, "line") ?? -1;
            int column = TryGetInt(args, "column") ?? -1;
            string direction = TryGetString(args, "direction")?.ToLowerInvariant() ?? "both"; // both|ancestors|descendants
            bool includeInterfaces = TryGetBool(args, "includeInterfaces") ?? true;
            bool includeOverrides = TryGetBool(args, "includeOverrides") ?? false;
            int maxDepth = Math.Clamp(TryGetInt(args, "maxDepth") ?? 10, 1, 100);
            bool solutionOnly = TryGetBool(args, "solutionOnly") ?? true;
            int page = Math.Max(1, TryGetInt(args, "page") ?? 1);
            int pageSize = Math.Clamp(TryGetInt(args, "pageSize") ?? 200, 1, 500);
            int timeoutMs = Math.Clamp(TryGetInt(args, "timeoutMs") ?? 60000, 1000, 300000);

            // Validate input parameters - require either fullyQualifiedName OR (file + line + column)
            if (string.IsNullOrEmpty(fqn))
            {
                if (string.IsNullOrEmpty(file) || line <= 0 || column <= 0)
                {
                    return Error("Provide either fullyQualifiedName OR file+line+column.");
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            var ct = cts.Token;

            // Resolve target type symbol
            var type = fqn is not null
                ? await ResolveTypeByFqnAsync(solution, fqn, ct)
                : await ResolveTypeByLocationAsync(solution, file, line, column, ct);

            if (type is null)
                return Error($"Could not resolve a type (input: {(fqn ?? $"{file}:{line}:{column}")}).");

            var rootDesc = SymbolFormatting.Describe(type);

            // Ancestors
            var ancestors = new List<object>();
            if (direction is "both" or "ancestors")
            {
                for (var t = type.BaseType; t != null; t = t.BaseType)
                {
                    if (solutionOnly && !HasSource(t)) continue;
                    ancestors.Add(Describe(t));
                }
            }

            // Interfaces
            var interfaces = new List<object>();
            if (includeInterfaces)
            {
                foreach (var itf in type.AllInterfaces)
                {
                    if (solutionOnly && !HasSource(itf)) continue;
                    interfaces.Add(Describe(itf));
                }
            }

            // Descendants
            var descendantsFlat = new List<object>();
            object? descendantsTree = null;
            if (direction is "both" or "descendants")
            {
                var (flat, tree) = await BuildDescendantsAsync(solution, type, maxDepth, solutionOnly, ct);
                descendantsFlat = flat;
                descendantsTree = tree;
            }

            // Overrides per member (optional)
            var overrides = new List<object>();
            if (includeOverrides && type.TypeKind is TypeKind.Class or TypeKind.Struct)
            {
                var members = type.GetMembers().Where(m => m.IsVirtual || m.IsAbstract || m.IsOverride);
                foreach (var mem in members)
                {
                    var impls = await SymbolFinder.FindOverridesAsync(mem, solution, projects: null, ct).ConfigureAwait(false);
                    var implDescs = new List<object>();
                    foreach (var s in impls)
                    {
                        if (solutionOnly && !HasSource(s)) continue;
                        implDescs.Add(Describe(s));
                    }
                    overrides.Add(new { member = mem.ToDisplayString(), count = implDescs.Count, implementations = implDescs });
                }
            }

            // Paging for descendantsFlat
            var total = descendantsFlat.Count;
            var paged = descendantsFlat.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
            string? nextCursor = (page * pageSize) < total ? $"page={page + 1}" : null;

            var result = new
            {
                success = true,
                root = new
                {
                    display = rootDesc.Display,
                    file = rootDesc.File,
                    line = rootDesc.Line,
                    column = rootDesc.Column,
                    kind = type.TypeKind.ToString(),
                    containingNamespace = type.ContainingNamespace?.ToDisplayString()
                },
                direction,
                includeInterfaces,
                includeOverrides,
                maxDepth,
                ancestors,
                interfaces,
                descendantsTree,
                page,
                pageSize,
                total,
                nextCursor,
                descendantsFlat = paged,
                overrides
            };

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return Error("Operation canceled (timeout or external cancellation).");
        }
        catch (Exception ex)
        {
            return ErrorObj("Unhandled exception in get_inheritance_tree", ex);
        }
    }

    private async Task<(List<object> flat, object tree)> BuildDescendantsAsync(
        Solution solution,
        INamedTypeSymbol root,
        int maxDepth,
        bool solutionOnly,
        CancellationToken ct)
    {
        var flatList = new List<INamedTypeSymbol>();

        if (root.TypeKind == TypeKind.Interface)
        {
            var derivedItf = await SymbolFinder.FindDerivedInterfacesAsync(root, solution, transitive: true, projects: null, ct).ConfigureAwait(false);
            foreach (var d in derivedItf.OfType<INamedTypeSymbol>()) flatList.Add(d);
        }
        else
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(root, solution, transitive: true, projects: null, ct).ConfigureAwait(false);
            foreach (var d in derived) flatList.Add(d);
        }

        if (solutionOnly)
            flatList = flatList.Where(HasSource).ToList();

        // Build parent->children map
        var children = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        if (root.TypeKind == TypeKind.Interface)
        {
            foreach (var t in flatList)
            {
                foreach (var p in t.Interfaces)
                {
                    if (SymbolEqualityComparer.Default.Equals(p, root) || flatList.Contains(p, SymbolEqualityComparer.Default))
                    {
                        if (!children.TryGetValue(p, out var list)) children[p] = list = new();
                        list.Add(t);
                    }
                }
            }
        }
        else
        {
            foreach (var t in flatList)
            {
                var p = t.BaseType;
                if (p is null) continue;
                if (SymbolEqualityComparer.Default.Equals(p, root) || flatList.Contains(p, SymbolEqualityComparer.Default))
                {
                    if (!children.TryGetValue(p, out var list)) children[p] = list = new();
                    list.Add(t);
                }
            }
        }

        object ToNode(INamedTypeSymbol node, int depth)
        {
            var d = SymbolFormatting.Describe(node);
            var kids = children.TryGetValue(node, out var ch) ? ch : new List<INamedTypeSymbol>();
            var nested = (depth < maxDepth)
                ? kids.OrderBy(t => t.Name).Select(t => ToNode(t, depth + 1)).ToArray()
                : Array.Empty<object>();

            return new { display = d.Display, file = d.File, line = d.Line, column = d.Column, kind = node.TypeKind.ToString(), children = nested };
        }

        var tree = ToNode(root, 1);
        var flatOut = flatList.OrderBy(t => t.ToDisplayString()).Select(Describe).ToList();
        return (flatOut, tree);
    }

    private static bool HasSource(ISymbol s) => s.Locations.Any(l => l.IsInSource);

    private static object Describe(ISymbol s)
    {
        var d = SymbolFormatting.Describe(s);
        return new { display = d.Display, file = d.File, line = d.Line, column = d.Column, kind = s.Kind.ToString() };
    }

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

    private static ToolCallResult Ok(object structured)
        => new()
        {
            Content = new List<ToolContent>
            {
                new ToolContent { Type = "text", Text = JsonSerializer.Serialize(structured, new JsonSerializerOptions { WriteIndented = true }) }
            },
            StructuredContent = JsonSerializer.SerializeToElement(structured)
        };

    private static ToolCallResult Error(string message)
        => new()
        {
            IsError = true,
            Content = new List<ToolContent> { new ToolContent { Type = "text", Text = JsonSerializer.Serialize(new { success = false, error = message }) } }
        };

    private static ToolCallResult ErrorObj(string error, Exception ex)
        => new()
        {
            IsError = true,
            Content = new List<ToolContent>
            {
                new ToolContent
                {
                    Type = "text",
                    Text = JsonSerializer.Serialize(new { success = false, error, exception = ex.GetType().FullName, message = ex.Message, stack = ex.StackTrace }, new JsonSerializerOptions { WriteIndented = true })
                }
            }
        };

    private static async Task<INamedTypeSymbol?> ResolveTypeByFqnAsync(Solution solution, string fqn, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;
            var type = compilation.GetTypeByMetadataName(fqn);
            if (type is INamedTypeSymbol nt) return nt;

            var lastDot = fqn.LastIndexOf('.');
            if (lastDot > 0)
            {
                var typeName = fqn.Substring(0, lastDot);
                var t2 = compilation.GetTypeByMetadataName(typeName);
                if (t2 is INamedTypeSymbol nt2) return nt2;
            }
        }
        foreach (var project in solution.Projects)
        {
            var decls = await SymbolFinder.FindDeclarationsAsync(project, fqn, ignoreCase: false, ct).ConfigureAwait(false);
            var firstType = decls.OfType<INamedTypeSymbol>().FirstOrDefault();
            if (firstType != null) return firstType;
        }
        return null;
    }

    private static async Task<INamedTypeSymbol?> ResolveTypeByLocationAsync(Solution solution, string? file, int line, int column, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(file) || line < 1 || column < 1) return null;
        var docId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        if (docId == null) return null;
        var document = solution.GetDocument(docId);
        if (document == null) return null;
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var pos = text.Lines.GetPosition(new LinePosition(line - 1, column - 1));
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model == null) return null;
        var sym = model.GetEnclosingSymbol(pos, ct) ?? model.GetDeclaredSymbol((await model.SyntaxTree.GetRootAsync(ct).ConfigureAwait(false)).FindToken(pos).Parent, ct);
        return (sym as INamedTypeSymbol) ?? sym?.ContainingType;
    }
}
