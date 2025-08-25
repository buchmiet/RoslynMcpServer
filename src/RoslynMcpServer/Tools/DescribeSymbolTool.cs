using System;
using System.IO;
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

public class DescribeSymbolTool
{
    private readonly WorkspaceHost _workspaceHost;

    public DescribeSymbolTool(WorkspaceHost workspaceHost)
    {
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
    }

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.HasValue || arguments.Value.ValueKind == JsonValueKind.Null)
            {
                return CreateErrorResult("Missing arguments");
            }

            var solution = _workspaceHost.GetSolution();
            if (solution == null)
            {
                return CreateErrorResult("No solution loaded. Please load a solution first.");
            }

            ISymbol? symbol = null;

            // Check for fullyQualifiedName mode
            if (arguments.Value.TryGetProperty("fullyQualifiedName", out var fqnElement))
            {
                var fullyQualifiedName = fqnElement.GetString();
                if (string.IsNullOrEmpty(fullyQualifiedName))
                {
                    return CreateErrorResult("fullyQualifiedName cannot be empty");
                }

                Console.Error.WriteLine($"DescribeSymbol: Finding by FQN={fullyQualifiedName}");

                // Find symbol by FQN
                foreach (var project in solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation == null) continue;

                    symbol = compilation.GetTypeByMetadataName(fullyQualifiedName);
                    if (symbol != null)
                    {
                        Console.Error.WriteLine($"Found type symbol in project: {project.Name}");
                        break;
                    }

                    // Try to find method or property by parsing the name
                    if (fullyQualifiedName.Contains("."))
                    {
                        var lastDot = fullyQualifiedName.LastIndexOf('.');
                        if (lastDot > 0)
                        {
                            var typeName = fullyQualifiedName.Substring(0, lastDot);
                            var memberName = fullyQualifiedName.Substring(lastDot + 1);
                            
                            // Remove method parameters if present
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
            }
            // Check for file:line:column mode
            else if (arguments.Value.TryGetProperty("file", out var fileElement) &&
                     arguments.Value.TryGetProperty("line", out var lineElement) &&
                     arguments.Value.TryGetProperty("column", out var columnElement))
            {
                var file = fileElement.GetString();
                if (string.IsNullOrEmpty(file))
                {
                    return CreateErrorResult("file cannot be empty");
                }

                if (lineElement.ValueKind != JsonValueKind.Number || columnElement.ValueKind != JsonValueKind.Number)
                {
                    return CreateErrorResult("line and column must be numbers");
                }

                var line = lineElement.GetInt32();
                var column = columnElement.GetInt32();

                Console.Error.WriteLine($"DescribeSymbol: Finding at {file}:{line}:{column}");

                // Find document by file path
                Document? document = null;
                foreach (var project in solution.Projects)
                {
                    foreach (var doc in project.Documents)
                    {
                        var docPath = doc.FilePath;
                        if (docPath != null && Path.GetFullPath(docPath) == Path.GetFullPath(file))
                        {
                            document = doc;
                            break;
                        }
                    }
                    if (document != null) break;
                }

                if (document == null)
                {
                    return CreateErrorResult($"Document not found: {file}");
                }

                // Convert line:column (1-based) to position (0-based)
                var text = await document.GetTextAsync(cancellationToken);
                var position = text.Lines.GetPosition(new LinePosition(line - 1, column - 1));

                // Find symbol at position
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (semanticModel != null)
                {
                    symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, solution.Workspace, cancellationToken);
                }

                if (symbol == null)
                {
                    return CreateErrorResult($"No symbol found at {file}:{line}:{column}");
                }
            }
            else
            {
                return CreateErrorResult("Either 'fullyQualifiedName' or 'file', 'line', 'column' must be provided");
            }

            // Describe the symbol
            var description = SymbolFormatting.Describe(symbol);
            var result = new
            {
                success = true,
                display = description.Display,
                file = description.File,
                line = description.Line,
                column = description.Column,
                kind = symbol.Kind.ToString(),
                containingType = symbol.ContainingType?.ToDisplayString(),
                containingNamespace = symbol.ContainingNamespace?.ToDisplayString()
            };

            var elem = JsonSerializer.SerializeToElement(result);
            return new ToolCallResult
            {
                Content = new[]
                {
                    new ToolContent
                    {
                        Type = "text",
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                }.ToList(),
                StructuredContent = elem
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DescribeSymbolTool error: {ex}");
            return CreateErrorResult($"Exception: {ex.Message}");
        }
    }

    private ToolCallResult CreateErrorResult(string message)
    {
        var result = new { success = false, error = message };
        
        return new ToolCallResult
        {
            IsError = true,
            Content = new[]
            {
                new ToolContent
                {
                    Type = "text",
                    Text = JsonSerializer.Serialize(result)
                }
            }.ToList()
        };
    }
}
