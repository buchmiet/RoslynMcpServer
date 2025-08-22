using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Roslyn;

namespace RoslynMcpServer.Tools;

public class GotoDefinitionTool
{
    private readonly WorkspaceHost _workspaceHost;

    public GotoDefinitionTool(WorkspaceHost workspaceHost)
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

            if (!arguments.Value.TryGetProperty("fullyQualifiedName", out var fqnElement))
            {
                return CreateErrorResult("Missing required field: fullyQualifiedName");
            }

            var fullyQualifiedName = fqnElement.GetString();
            if (string.IsNullOrEmpty(fullyQualifiedName))
            {
                return CreateErrorResult("fullyQualifiedName cannot be empty");
            }

            var solution = _workspaceHost.GetSolution();
            if (solution == null)
            {
                return CreateErrorResult("No solution loaded. Please load a solution first.");
            }

            Console.Error.WriteLine($"GotoDefinition: Finding symbol {fullyQualifiedName}");

            // Find symbol by FQN
            ISymbol? symbol = null;
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

            // Try to find source definition
            Console.Error.WriteLine($"Finding source definition for {symbol.ToDisplayString()}");
            var sourceDefinition = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, cancellationToken);
            
            // If no source definition found, use the original symbol (might be from metadata)
            var definitionSymbol = sourceDefinition ?? symbol;
            Console.Error.WriteLine($"Using {(sourceDefinition != null ? "source" : "metadata")} definition");

            // Get the definition location
            var description = SymbolFormatting.Describe(definitionSymbol);
            
            var result = new
            {
                success = true,
                display = description.Display,
                file = description.File,
                line = description.Line,
                column = description.Column,
                isSourceDefinition = sourceDefinition != null,
                isFromMetadata = sourceDefinition == null && !definitionSymbol.Locations.Any(loc => loc.IsInSource),
                kind = definitionSymbol.Kind.ToString(),
                containingType = definitionSymbol.ContainingType?.ToDisplayString(),
                containingNamespace = definitionSymbol.ContainingNamespace?.ToDisplayString()
            };

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
                StructuredContent = result
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GotoDefinitionTool error: {ex}");
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
