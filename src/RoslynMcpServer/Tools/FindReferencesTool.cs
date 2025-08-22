using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Roslyn;

namespace RoslynMcpServer.Tools;

public class FindReferencesTool
{
    private readonly WorkspaceHost _workspaceHost;

    public FindReferencesTool(WorkspaceHost workspaceHost)
    {
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
    }

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse arguments
            if (!arguments.HasValue || arguments.Value.ValueKind == JsonValueKind.Null)
            {
                return CreateErrorResult("Missing arguments");
            }

            if (!arguments.Value.TryGetProperty("fullyQualifiedName", out var fqnElement))
            {
                return CreateErrorResult("Missing 'fullyQualifiedName' argument");
            }

            var fullyQualifiedName = fqnElement.GetString();
            if (string.IsNullOrEmpty(fullyQualifiedName))
            {
                return CreateErrorResult("fullyQualifiedName cannot be empty");
            }

            // Parse pagination parameters
            int page = 1;
            int pageSize = 200;
            int timeoutMs = 60000;

            if (arguments.Value.TryGetProperty("page", out var pageElement))
            {
                // Handle both page number and cursor token
                if (pageElement.ValueKind == JsonValueKind.String)
                {
                    var cursor = pageElement.GetString();
                    if (!string.IsNullOrEmpty(cursor))
                    {
                        try
                        {
                            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                            var cursorData = JsonSerializer.Deserialize<Dictionary<string, int>>(decoded);
                            if (cursorData != null && cursorData.TryGetValue("offset", out var offset))
                            {
                                page = (offset / pageSize) + 1;
                            }
                        }
                        catch
                        {
                            // Invalid cursor, use default
                        }
                    }
                }
                else if (pageElement.ValueKind == JsonValueKind.Number)
                {
                    page = Math.Max(1, pageElement.GetInt32());
                }
            }

            if (arguments.Value.TryGetProperty("pageSize", out var pageSizeElement) && pageSizeElement.ValueKind == JsonValueKind.Number)
            {
                pageSize = Math.Min(500, Math.Max(1, pageSizeElement.GetInt32()));
            }

            if (arguments.Value.TryGetProperty("timeoutMs", out var timeoutElement) && timeoutElement.ValueKind == JsonValueKind.Number)
            {
                timeoutMs = Math.Min(300000, Math.Max(1000, timeoutElement.GetInt32()));
            }

            Console.Error.WriteLine($"FindReferences: FQN={fullyQualifiedName}, page={page}, pageSize={pageSize}, timeout={timeoutMs}ms");

            // Create timeout cancellation token
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            // Get current solution
            var solution = _workspaceHost.GetSolution();
            if (solution == null)
            {
                return CreateErrorResult("No solution loaded. Please load a solution first.");
            }

            // Find the symbol
            ISymbol? symbol = null;
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(cts.Token);
                if (compilation == null) continue;

                symbol = compilation.GetTypeByMetadataName(fullyQualifiedName);
                if (symbol != null)
                {
                    Console.Error.WriteLine($"Found symbol in project: {project.Name}");
                    break;
                }

                // Try to find method or property by parsing the name
                if (fullyQualifiedName.Contains(".") && fullyQualifiedName.Contains("("))
                {
                    // Method signature - simplified parsing
                    var lastDot = fullyQualifiedName.LastIndexOf('.');
                    if (lastDot > 0)
                    {
                        var typeName = fullyQualifiedName.Substring(0, lastDot);
                        var memberName = fullyQualifiedName.Substring(lastDot + 1);
                        var parenIndex = memberName.IndexOf('(');
                        if (parenIndex > 0)
                        {
                            memberName = memberName.Substring(0, parenIndex);
                        }

                        var type = compilation.GetTypeByMetadataName(typeName);
                        if (type != null)
                        {
                            symbol = type.GetMembers(memberName).FirstOrDefault();
                            if (symbol != null)
                            {
                                Console.Error.WriteLine($"Found member symbol in project: {project.Name}");
                                break;
                            }
                        }
                    }
                }
            }

            if (symbol == null)
            {
                return CreateErrorResult($"Symbol not found: {fullyQualifiedName}");
            }

            // Find references
            Console.Error.WriteLine($"Finding references for: {symbol.ToDisplayString()}");
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cts.Token);

            // Flatten locations
            var allLocations = new List<ReferenceLocation>();
            foreach (var referencedSymbol in references)
            {
                foreach (var location in referencedSymbol.Locations)
                {
                    var lineSpan = location.Location.GetLineSpan();
                    allLocations.Add(new ReferenceLocation
                    {
                        File = lineSpan.Path,
                        Line = lineSpan.StartLinePosition.Line + 1, // Convert to 1-based
                        Column = lineSpan.StartLinePosition.Character + 1,
                        Text = await GetLocationTextAsync(location.Document, lineSpan, cts.Token)
                    });
                }
            }

            // Sort deterministically
            allLocations = allLocations
                .OrderBy(l => l.File)
                .ThenBy(l => l.Line)
                .ThenBy(l => l.Column)
                .ToList();

            Console.Error.WriteLine($"Found {allLocations.Count} references");

            // Apply pagination
            var skip = (page - 1) * pageSize;
            var pagedLocations = allLocations.Skip(skip).Take(pageSize).ToList();
            var hasMore = skip + pageSize < allLocations.Count;

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = allLocations.Count,
                ["page"] = page,
                ["pageSize"] = pageSize,
                ["references"] = pagedLocations.Select(l => new
                {
                    file = l.File,
                    line = l.Line,
                    column = l.Column,
                    text = l.Text
                }).ToList()
            };

            if (hasMore)
            {
                // Create opaque cursor token
                var nextOffset = skip + pageSize;
                var cursorData = new { offset = nextOffset };
                var cursorJson = JsonSerializer.Serialize(cursorData);
                var cursorBytes = Encoding.UTF8.GetBytes(cursorJson);
                result["nextCursor"] = Convert.ToBase64String(cursorBytes);
            }

            // Return text plus structured content according to MCP
            return new ToolCallResult
            {
                Content = new List<ToolContent>
                {
                    new ToolContent
                    {
                        Type = "text",
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions 
                        { 
                            WriteIndented = true 
                        })
                    }
                },
                StructuredContent = result
            };
        }
        catch (OperationCanceledException)
        {
            return CreateErrorResult($"Operation timed out");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FindReferencesTool error: {ex}");
            return CreateErrorResult($"Exception: {ex.Message}");
        }
    }

    private async Task<string> GetLocationTextAsync(Document? document, FileLinePositionSpan lineSpan, CancellationToken cancellationToken)
    {
        try
        {
            if (document == null) return "";
            
            var text = await document.GetTextAsync(cancellationToken);
            var line = text.Lines[lineSpan.StartLinePosition.Line];
            return line.ToString().Trim();
        }
        catch
        {
            return "";
        }
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

    private class ReferenceLocation
    {
        public string File { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public string Text { get; set; } = "";
    }
}
