using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using RoslynMcpServer.Roslyn;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

public class NewToolsTests : IAsyncLifetime
{
    private readonly WorkspaceHost _workspaceHost = new();
    private readonly LoadSolutionTool _load;
    private readonly GetInheritanceTreeTool _inherit;
    private readonly GetAllImplementationsTool _impls;

    private string _sampleProjectPath = string.Empty;

    public NewToolsTests()
    {
        _load = new LoadSolutionTool(_workspaceHost);
        _inherit = new GetInheritanceTreeTool(_workspaceHost);
        _impls = new GetAllImplementationsTool(_workspaceHost);
    }

    public async Task InitializeAsync()
    {
        var repo = GetRepoRoot();
        _sampleProjectPath = Path.GetFullPath(Path.Combine(repo, "samples", "SampleApp", "SampleApp.csproj"));
        File.Exists(_sampleProjectPath).Should().BeTrue();

        var loadArgs = JsonSerializer.SerializeToElement(new { path = _sampleProjectPath });
        var res = await _load.ExecuteAsync(loadArgs);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
    }

    public Task DisposeAsync()
    {
        _workspaceHost.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InheritanceTree_Should_List_Descendants_And_Interfaces()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            fullyQualifiedName = "SampleApp.Core.CalculatorBase",
            direction = "descendants",
            includeInterfaces = true,
            solutionOnly = true,
            pageSize = 200
        });

        var res = await _inherit.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        msg.Should().Contain("BasicCalculator");
        msg.Should().Contain("AdvancedCalculator");
    }

    [Fact]
    public async Task InheritanceTree_Ancestors_Should_Include_BaseType()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            fullyQualifiedName = "SampleApp.Core.BasicCalculator",
            direction = "ancestors",
            includeInterfaces = true
        });

        var res = await _inherit.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        msg.Should().Contain("CalculatorBase");
    }

    [Fact]
    public async Task AllImplementations_For_Interface_Should_List_Types_And_DerivedInterfaces()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            fullyQualifiedName = "SampleApp.Core.ICalculator",
            includeDerivedInterfaces = true,
            solutionOnly = true,
            pageSize = 200
        });

        var res = await _impls.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        msg.Should().Contain("BasicCalculator");
        msg.Should().Contain("AdvancedCalculator");
        msg.Should().Contain("IAdvancedCalculator");
    }

    [Fact]
    public async Task AllImplementations_For_InterfaceMember_Should_List_Member_Implementations()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            fullyQualifiedName = "SampleApp.Core.ICalculator",
            member = "Compute",
            solutionOnly = true,
            pageSize = 200
        });

        var res = await _impls.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        msg.Should().Contain("BasicCalculator");
        msg.Should().Contain("AdvancedCalculator");
        msg.Should().Contain("Compute");
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        var di = new DirectoryInfo(dir);
        while (di != null)
        {
            if (Directory.Exists(Path.Combine(di.FullName, "src")) && Directory.Exists(Path.Combine(di.FullName, "samples")))
                return di.FullName;
            di = di.Parent;
        }
        throw new InvalidOperationException("Cannot locate repository root from test base directory.");
    }
}
