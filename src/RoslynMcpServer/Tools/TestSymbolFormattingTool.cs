using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Roslyn;

namespace RoslynMcpServer.Tools;

/// <summary>
/// Test tool to demonstrate SymbolFormatting.Describe functionality
/// </summary>
public class TestSymbolFormattingTool
{
    private readonly WorkspaceHost _workspaceHost;

    public TestSymbolFormattingTool(WorkspaceHost workspaceHost)
    {
        _workspaceHost = workspaceHost;
    }

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = _workspaceHost.GetSolution();
            if (solution == null)
            {
                return CreateErrorResult("No solution loaded. Please load a solution first.");
            }

            var results = new List<object>();

            // Test with first project
            var project = solution.Projects.FirstOrDefault();
            if (project == null)
            {
                return CreateErrorResult("No projects in solution");
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                return CreateErrorResult("Failed to get compilation");
            }

            // Find some symbols to test
            foreach (var document in project.Documents.Take(3))
            {
                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (syntaxTree == null) continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                // Find class declarations
                var classDeclarations = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Take(2);

                foreach (var classDecl in classDeclarations)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(classDecl);
                    if (symbol != null)
                    {
                        var description = SymbolFormatting.Describe(symbol);
                        results.Add(new
                        {
                            kind = "class",
                            display = description.Display,
                            file = description.File,
                            line = description.Line,
                            column = description.Column,
                            formatted = description.ToString()
                        });
                    }
                }

                // Find method declarations
                var methodDeclarations = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .Take(2);

                foreach (var methodDecl in methodDeclarations)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(methodDecl);
                    if (symbol != null)
                    {
                        var description = SymbolFormatting.Describe(symbol);
                        results.Add(new
                        {
                            kind = "method",
                            display = description.Display,
                            file = description.File,
                            line = description.Line,
                            column = description.Column,
                            formatted = description.ToString()
                        });
                    }
                }
            }

            // Test with a metadata symbol (e.g., System.String)
            var stringSymbol = compilation.GetTypeByMetadataName("System.String");
            if (stringSymbol != null)
            {
                var description = SymbolFormatting.Describe(stringSymbol);
                results.Add(new
                {
                    kind = "metadata",
                    display = description.Display,
                    file = description.File,
                    line = description.Line,
                    column = description.Column,
                    formatted = description.ToString()
                });
            }

            var result = new
            {
                success = true,
                symbolCount = results.Count,
                symbols = results
            };

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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TestSymbolFormattingTool error: {ex}");
            return CreateErrorResult($"Exception: {ex.Message}");
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