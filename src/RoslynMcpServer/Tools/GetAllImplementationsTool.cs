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

public sealed class GetAllImplementationsTool
{
    private readonly WorkspaceHost _workspaceHost;

    public GetAllImplementationsTool(WorkspaceHost workspaceHost)
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
            string? memberName = TryGetString(args, "member");

            bool solutionOnly = TryGetBool(args, "solutionOnly") ?? true;
            bool includeDerivedInterfaces = TryGetBool(args, "includeDerivedInterfaces") ?? true;
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

            // Resolve symbol (type or member)
            var resolved = fqn is not null
                ? await ResolveByFqnAsync(solution, fqn, ct)
                : await ResolveByLocationAsync(solution, file, line, column, ct);

            if (resolved is null)
                return Error($"Could not resolve a symbol (input: {(fqn ?? $"{file}:{line}:{column}")}).");

            INamedTypeSymbol? iface = null;
            ISymbol? member = null;

            switch (resolved)
            {
                case INamedTypeSymbol nt when nt.TypeKind == TypeKind.Interface:
                    iface = nt;
                    if (!string.IsNullOrWhiteSpace(memberName))
                        member = nt.GetMembers().FirstOrDefault(m => string.Equals(m.Name, memberName, StringComparison.Ordinal));
                    break;
                case IMethodSymbol ms when ms.ContainingType?.TypeKind == TypeKind.Interface:
                    iface = ms.ContainingType; member = ms; break;
                case IPropertySymbol ps when ps.ContainingType?.TypeKind == TypeKind.Interface:
                    iface = ps.ContainingType; member = ps; break;
                case IEventSymbol es when es.ContainingType?.TypeKind == TypeKind.Interface:
                    iface = es.ContainingType; member = es; break;
                default:
                    return Error("Target must be an interface type or an interface member.");
            }

            var ifaceDesc = SymbolFormatting.Describe(iface);

            var derivedInterfaces = new List<object>();
            if (includeDerivedInterfaces)
            {
                var derives = await SymbolFinder.FindDerivedInterfacesAsync(iface, solution, transitive: true, projects: null, ct).ConfigureAwait(false);
                foreach (var d in derives.OfType<INamedTypeSymbol>())
                {
                    if (solutionOnly && !HasSource(d)) continue;
                    derivedInterfaces.Add(Describe(d));
                }
            }

            var implementations = new List<object>();
            int total;
            string? nextCursor;

            if (member is null)
            {
                var types = await SymbolFinder.FindImplementationsAsync(iface, solution, transitive: true, projects: null, ct).ConfigureAwait(false);
                var list = types.OfType<INamedTypeSymbol>().Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct);
                if (solutionOnly) list = list.Where(HasSource);
                var all = list.OrderBy(t => t.ToDisplayString()).Select(Describe).ToList();
                total = all.Count;
                implementations.AddRange(all.Skip((page - 1) * pageSize).Take(pageSize));
                nextCursor = (page * pageSize) < total ? $"page={page + 1}" : null;
            }
            else
            {
                var implMembers = await SymbolFinder.FindImplementationsAsync(member, solution, projects: null, ct).ConfigureAwait(false);
                var all = implMembers.Where(s => !solutionOnly || HasSource(s)).OrderBy(s => s.ToDisplayString()).Select(Describe).ToList();
                total = all.Count;
                implementations.AddRange(all.Skip((page - 1) * pageSize).Take(pageSize));
                nextCursor = (page * pageSize) < total ? $"page={page + 1}" : null;
            }

            var result = new
            {
                success = true,
                @interface = new { display = ifaceDesc.Display, file = ifaceDesc.File, line = ifaceDesc.Line, column = ifaceDesc.Column },
                member = member?.ToDisplayString(),
                includeDerivedInterfaces,
                page,
                pageSize,
                total,
                nextCursor,
                derivedInterfaces,
                implementations
            };

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return Error("Operation canceled (timeout or external cancellation).");
        }
        catch (Exception ex)
        {
            return ErrorObj("Unhandled exception in get_all_implementations", ex);
        }
    }

    private static bool HasSource(ISymbol s) => s.Locations.Any(l => l.IsInSource);
    private static object Describe(ISymbol s) { var d = SymbolFormatting.Describe(s); return new { display = d.Display, file = d.File, line = d.Line, column = d.Column, kind = s.Kind.ToString() }; }

    private static string? TryGetString(JsonElement obj, string name) => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
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

    private static ToolCallResult Ok(object structured) => new()
    {
        Content = new List<ToolContent> { new ToolContent { Type = "text", Text = JsonSerializer.Serialize(structured, new JsonSerializerOptions { WriteIndented = true }) } },
        StructuredContent = JsonSerializer.SerializeToElement(structured)
    };
    private static ToolCallResult Error(string message) => new()
    {
        IsError = true,
        Content = new List<ToolContent> { new ToolContent { Type = "text", Text = JsonSerializer.Serialize(new { success = false, error = message }) } }
    };
    private static ToolCallResult ErrorObj(string error, Exception ex) => new()
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

    private static async Task<ISymbol?> ResolveByFqnAsync(Solution solution, string fqn, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;
            var type = compilation.GetTypeByMetadataName(fqn);
            if (type is not null) return type;

            var lastDot = fqn.LastIndexOf('.');
            if (lastDot > 0)
            {
                var typeName = fqn.Substring(0, lastDot);
                var t2 = compilation.GetTypeByMetadataName(typeName);
                if (t2 is not null)
                {
                    var memName = fqn.Substring(lastDot + 1);
                    var paren = memName.IndexOf('(');
                    if (paren >= 0) memName = memName[..paren];
                    return t2.GetMembers().FirstOrDefault(m => string.Equals(m.Name, memName, StringComparison.Ordinal)) ?? (ISymbol)t2;
                }
            }
        }
        foreach (var project in solution.Projects)
        {
            var decls = await SymbolFinder.FindDeclarationsAsync(project, fqn, ignoreCase: false, ct).ConfigureAwait(false);
            var best = decls.FirstOrDefault();
            if (best != null) return best;
        }
        return null;
    }

    private static async Task<ISymbol?> ResolveByLocationAsync(Solution solution, string? file, int line, int column, CancellationToken ct)
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
        return model.GetEnclosingSymbol(pos, ct) ?? model.GetDeclaredSymbol((await model.SyntaxTree.GetRootAsync(ct).ConfigureAwait(false)).FindToken(pos).Parent, ct);
    }
}
