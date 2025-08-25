using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Roslyn;

namespace RoslynMcpServer.Tools;

public class GetTypeInfoTool
{
    private readonly WorkspaceHost _workspaceHost;

    public GetTypeInfoTool(WorkspaceHost workspaceHost)
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

            if (arguments.Value.TryGetProperty("page", out var pageElement) && pageElement.ValueKind == JsonValueKind.Number)
            {
                page = Math.Max(1, pageElement.GetInt32());
            }

            if (arguments.Value.TryGetProperty("pageSize", out var pageSizeElement) && pageSizeElement.ValueKind == JsonValueKind.Number)
            {
                pageSize = Math.Min(500, Math.Max(1, pageSizeElement.GetInt32()));
            }

            Console.Error.WriteLine($"GetTypeInfo: FQN={fullyQualifiedName}, page={page}, pageSize={pageSize}");

            // Get current solution
            var solution = _workspaceHost.GetSolution();
            if (solution == null)
            {
                return CreateErrorResult("No solution loaded. Please load a solution first.");
            }

            // Search for the type across all projects
            INamedTypeSymbol? typeSymbol = null;
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) continue;

                typeSymbol = compilation.GetTypeByMetadataName(fullyQualifiedName);
                if (typeSymbol != null)
                {
                    Console.Error.WriteLine($"Found type in project: {project.Name}");
                    break;
                }
            }

            if (typeSymbol == null)
            {
                return new ToolCallResult
                {
                    Content = new List<ToolContent>
                    {
                        new ToolContent
                        {
                            Type = "text",
                            Text = JsonSerializer.Serialize(new 
                            { 
                                success = false,
                                error = $"Type not found: {fullyQualifiedName}"
                            })
                        }
                    }
                };
            }

            // Get symbol description
            var symbolDescription = SymbolFormatting.Describe(typeSymbol);
            var symbolInfo = new
            {
                display = symbolDescription.Display,
                file = symbolDescription.File,
                line = symbolDescription.Line,
                column = symbolDescription.Column
            };

            // Get all members
            var allMembers = typeSymbol.GetMembers()
                .Where(m => m.Kind != SymbolKind.NamedType) // Exclude nested types for simplicity
                .Select(m => new
                {
                    Name = m.Name,
                    Kind = m.Kind.ToString(),
                    Accessibility = m.DeclaredAccessibility.ToString(),
                    IsStatic = m.IsStatic,
                    Type = GetMemberType(m),
                    Parameters = GetParameters(m)
                })
                .ToList();

            // Apply pagination
            var skip = (page - 1) * pageSize;
            var pagedMembers = allMembers.Skip(skip).Take(pageSize).ToList();
            var hasMore = skip + pageSize < allMembers.Count;

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["symbol"] = symbolInfo,
                ["totalMembers"] = allMembers.Count,
                ["page"] = page,
                ["pageSize"] = pageSize,
                ["members"] = pagedMembers
            };

            if (hasMore)
            {
                result["nextCursor"] = $"page={page + 1}";
            }

            var jsonText = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            var elem = JsonSerializer.SerializeToElement(result);

            return new ToolCallResult
            {
                Content = new List<ToolContent>
                {
                    new ToolContent
                    {
                        Type = "text",
                        Text = jsonText
                    }
                },
                StructuredContent = elem
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GetTypeInfoTool error: {ex}");
            return CreateErrorResult($"Exception: {ex.Message}");
        }
    }

    private string? GetMemberType(ISymbol member)
    {
        try
        {
            return member switch
            {
                IFieldSymbol field => field.Type?.ToDisplayString(),
                IPropertySymbol property => property.Type?.ToDisplayString(),
                IMethodSymbol method => method.ReturnType?.ToDisplayString(),
                IEventSymbol evt => evt.Type?.ToDisplayString(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private string? GetParameters(ISymbol member)
    {
        try
        {
            if (member is IMethodSymbol method)
            {
                var parameters = method.Parameters.Select(p => 
                    $"{p.Type.ToDisplayString()} {p.Name}");
                return $"({string.Join(", ", parameters)})";
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private ToolCallResult CreateErrorResult(string message)
    {
        return new ToolCallResult
        {
            IsError = true,
            Content = new List<ToolContent>
            {
                new ToolContent
                {
                    Type = "text",
                    Text = JsonSerializer.Serialize(new 
                    { 
                        success = false, 
                        error = message 
                    })
                }
            }
        };
    }
}
