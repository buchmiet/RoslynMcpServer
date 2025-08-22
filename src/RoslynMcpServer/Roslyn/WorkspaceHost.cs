using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcpServer.Roslyn;

public class WorkspaceHost
{
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _loadMode;
    private readonly object _lock = new();
    private static bool _msbuildRegistered = false;

    static WorkspaceHost()
    {
        if (!_msbuildRegistered)
        {
            try
            {
                // Console.Error.WriteLine("Registering MSBuild...");
                
                // Try to find Visual Studio instances first
                var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
                
                if (instances.Any())
                {
                    // Choose the latest version
                    var latestInstance = instances.OrderByDescending(x => x.Version).First();
                    // Console.Error.WriteLine($"Found {instances.Count} VS instance(s), using: {latestInstance.Name} {latestInstance.Version} at {latestInstance.MSBuildPath}");
                    MSBuildLocator.RegisterInstance(latestInstance);
                }
                else
                {
                    // Fallback for Linux/WSL without VS
                    // Console.Error.WriteLine("No VS instances found, using MSBuild defaults (Linux/WSL mode)");
                    MSBuildLocator.RegisterDefaults();
                }
                
                _msbuildRegistered = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to register MSBuild: {ex.Message}");
            }
        }
    }

    public async Task<bool> OpenSolutionAsync(string path, CancellationToken cancellationToken = default, int timeoutMs = 90000)
    {
        try
        {
            Console.Error.WriteLine($"Opening: {path}");
            
            lock (_lock)
            {
                _workspace?.Dispose();
                _workspace = MSBuildWorkspace.Create();
                _loadMode = null;
                
                _workspace.WorkspaceFailed += (sender, args) =>
                {
                    Console.Error.WriteLine($"Workspace failed: [{args.Diagnostic.Kind}] {args.Diagnostic.Message}");
                };
            }

            // Create progress reporter
            var progressReporter = new Progress<ProjectLoadProgress>(progress =>
            {
                var tfm = progress.TargetFramework ?? "unknown";
                Console.Error.WriteLine($"Loading {progress.FilePath} ({tfm})");
            });

            // Create timeout cancellation token
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            try
            {
                // Determine if it's a solution or project file
                var extension = Path.GetExtension(path).ToLowerInvariant();
                
                if (extension == ".sln")
                {
                    Console.Error.WriteLine("Loading as solution file...");
                    _solution = await _workspace.OpenSolutionAsync(path, progressReporter, cts.Token).ConfigureAwait(false);
                    _loadMode = "native";
                }
                else if (extension == ".slnx")
                {
                    Console.Error.WriteLine("Loading as .slnx file...");
                    // Try native loading first
                    try
                    {
                        _solution = await _workspace.OpenSolutionAsync(path, progressReporter, cts.Token).ConfigureAwait(false);
                        if (_solution?.Projects.Any() == true)
                        {
                            _loadMode = "native";
                            Console.Error.WriteLine($"Successfully loaded .slnx using native mode");
                        }
                        else
                        {
                            throw new InvalidOperationException("Native loading returned empty solution");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Native .slnx loading failed: {ex.Message}, trying fallback...");
                        _solution = await LoadSlnxWithFallbackAsync(path, progressReporter, cts.Token).ConfigureAwait(false);
                        _loadMode = "slnx-fallback";
                    }
                }
                else if (extension == ".csproj" || extension == ".vbproj" || extension == ".fsproj")
                {
                    Console.Error.WriteLine("Loading as project file...");
                    var project = await _workspace.OpenProjectAsync(path, progressReporter, cts.Token).ConfigureAwait(false);
                    _solution = project?.Solution;
                    _loadMode = "project";
                }
                else
                {
                    Console.Error.WriteLine($"Unsupported file extension: {extension}");
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"Loading timed out after {timeoutMs}ms");
                return false;
            }

            // Set solution to current workspace solution
            if (_solution == null)
            {
                _solution = _workspace.CurrentSolution;
            }

            var projectCount = _solution?.Projects.Count() ?? 0;
            Console.Error.WriteLine($"Solution loaded: {_solution?.FilePath ?? path}");
            Console.Error.WriteLine($"Projects count: {projectCount}");
            
            // Return true if we have at least one project
            return projectCount > 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open: {ex.Message}");
            Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    private async Task<Solution?> LoadSlnxWithFallbackAsync(string slnxPath, IProgress<ProjectLoadProgress> progressReporter, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"Loading .slnx with XML fallback: {slnxPath}");
        
        try
        {
            // Parse the .slnx XML file
            var doc = XDocument.Load(slnxPath);
            var projectPaths = new List<string>();
            
            // Extract project paths from XML
            // Looking for <Project Path="..."> elements
            var projectElements = doc.Descendants("Project")
                .Where(e => e.Attribute("Path") != null)
                .Select(e => e.Attribute("Path")!.Value);
            
            foreach (var relativePath in projectElements)
            {
                var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(slnxPath)!, relativePath));
                if (File.Exists(fullPath))
                {
                    projectPaths.Add(fullPath);
                    Console.Error.WriteLine($"Found project in .slnx: {fullPath}");
                }
                else
                {
                    Console.Error.WriteLine($"Warning: Project file not found: {fullPath}");
                }
            }
            
            if (!projectPaths.Any())
            {
                Console.Error.WriteLine("No valid project paths found in .slnx file");
                return null;
            }
            
            // Start with empty solution
            var solution = _workspace!.CurrentSolution;
            
            // Load each project and add to solution
            foreach (var projectPath in projectPaths)
            {
                try
                {
                    Console.Error.WriteLine($"Loading project: {projectPath}");
                    var project = await _workspace.OpenProjectAsync(projectPath, progressReporter, cancellationToken).ConfigureAwait(false);
                    
                    if (project != null)
                    {
                        // Get the updated solution that contains this project
                        solution = project.Solution;
                        Console.Error.WriteLine($"Added project to solution: {project.Name}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to load project {projectPath}: {ex.Message}");
                    // Continue with other projects
                }
            }
            
            Console.Error.WriteLine($"Fallback loading completed with {solution.Projects.Count()} projects");
            return solution;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse .slnx file: {ex.Message}");
            return null;
        }
    }
    
    public Solution? GetSolution()
    {
        lock (_lock)
        {
            return _solution;
        }
    }
    
    public string? GetLoadMode()
    {
        lock (_lock)
        {
            return _loadMode;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _workspace?.Dispose();
            _workspace = null;
            _solution = null;
        }
    }
}