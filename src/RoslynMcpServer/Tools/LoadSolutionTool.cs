using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Roslyn;

namespace RoslynMcpServer.Tools;

public class LoadSolutionTool
{
    private readonly WorkspaceHost _workspaceHost;

    public LoadSolutionTool(WorkspaceHost workspaceHost)
    {
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
    }

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            Console.Error.WriteLine($"LoadSolutionTool.ExecuteAsync called with arguments: {arguments?.ToString() ?? "null"}");
            
            if (!arguments.HasValue || arguments.Value.ValueKind == JsonValueKind.Null || arguments.Value.ValueKind == JsonValueKind.Undefined)
            {
                return CreateErrorResult("Missing arguments");
            }

            if (!arguments.Value.TryGetProperty("path", out var pathElement))
            {
                return CreateErrorResult("Missing 'path' argument");
            }

            var solutionPath = pathElement.GetString();
            if (string.IsNullOrEmpty(solutionPath))
            {
                return CreateErrorResult("Solution path cannot be empty");
            }

            if (!File.Exists(solutionPath))
            {
                return CreateErrorResult($"Solution file not found: {solutionPath}");
            }

            Console.Error.WriteLine($"Loading: {solutionPath}");
            
            // Call the actual loading with cancellation token
            var success = await _workspaceHost.OpenSolutionAsync(solutionPath, cancellationToken);
            
            if (!success)
            {
                return CreateErrorResult($"Failed to load solution/project. Check stderr for detailed logs (WorkspaceFailed events, progress)");
            }

            var solution = _workspaceHost.GetSolution();
            if (solution == null)
            {
                return CreateErrorResult("Solution is null after loading");
            }

            var projects = new List<object>();
            foreach (var project in solution.Projects)
            {
                var tfm = GetTargetFramework(project);
                projects.Add(new
                {
                    name = project.Name,
                    tfm = tfm ?? "unknown"
                });
                
                Console.Error.WriteLine($"  Project: {project.Name} (TFM: {tfm ?? "unknown"})");
            }

            var loadMode = _workspaceHost.GetLoadMode();
            Console.Error.WriteLine($"Solution load mode: {loadMode ?? "unknown"}");
            var result = new
            {
                success = true,
                solutionPath = solution.FilePath,
                projectCount = projects.Count,
                mode = loadMode ?? "unknown",
                projects = projects
            };

            // Return text plus structured content according to MCP
            var jsonText = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });

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
                StructuredContent = result
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LoadSolutionTool error: {ex}");
            return CreateErrorResult($"Exception: {ex.Message}");
        }
    }

    private string? GetTargetFramework(Project project)
    {
        try
        {
            if (project.CompilationOptions == null)
                return null;

            var analyzerConfigOptions = project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
            var globalOptions = analyzerConfigOptions?.GlobalOptions;
            
            if (globalOptions != null && globalOptions.TryGetValue("build_property.TargetFramework", out var tfm))
            {
                return tfm;
            }

            var msbuildProperties = project.ParseOptions?.PreprocessorSymbolNames
                .FirstOrDefault(s => s.StartsWith("NET") || s.StartsWith("NETCOREAPP") || s.StartsWith("NETSTANDARD"));
            
            if (!string.IsNullOrEmpty(msbuildProperties))
            {
                return msbuildProperties.ToLowerInvariant().Replace("_", ".");
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
