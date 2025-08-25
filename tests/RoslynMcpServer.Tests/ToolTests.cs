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

public class ToolTests : IAsyncLifetime
{
    private readonly WorkspaceHost _workspaceHost = new();
    private readonly LoadSolutionTool _load;
    private readonly GetTypeInfoTool _getTypeInfo;
    private readonly FindReferencesTool _findRefs;
    private readonly DescribeSymbolTool _describe;
    private readonly GotoDefinitionTool _gotoDef;
    private readonly GetMethodDependenciesTool _deps;
    private readonly TestSymbolFormattingTool _fmt;

    private string _sampleProjectPath = string.Empty;

    public ToolTests()
    {
        _load = new LoadSolutionTool(_workspaceHost);
        _getTypeInfo = new GetTypeInfoTool(_workspaceHost);
        _findRefs = new FindReferencesTool(_workspaceHost);
        _describe = new DescribeSymbolTool(_workspaceHost);
        _gotoDef = new GotoDefinitionTool(_workspaceHost);
        _deps = new GetMethodDependenciesTool(_workspaceHost);
        _fmt = new TestSymbolFormattingTool(_workspaceHost);
    }

    public async Task InitializeAsync()
    {
        // Compute absolute path to sample csproj
        var repoRoot = GetRepoRoot();
        _sampleProjectPath = Path.GetFullPath(Path.Combine(repoRoot, "samples", "SampleApp", "SampleApp.csproj"));
        File.Exists(_sampleProjectPath).Should().BeTrue("sample project must exist for tests");

        var args = JsonSerializer.SerializeToElement(new { path = _sampleProjectPath });
        var res = await _load.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        res.StructuredContent.Should().NotBeNull();
    }

    public Task DisposeAsync()
    {
        _workspaceHost.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetTypeInfo_ReturnsMembers_ForMathUtils()
    {
        var args = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Core.MathUtils", pageSize = 500 });
        var res = await _getTypeInfo.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        Console.WriteLine("GetTypeInfo result:\n" + msg);
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        var json = JsonSerializer.Serialize(sc);
        json.Should().Contain("\"success\":true");
        json.Should().Contain("Add");
        json.Should().Contain("Multiply");
        json.Should().Contain("Pi");
    }

    [Fact]
    public async Task FindReferences_Finds_Usages_Of_Add_Method()
    {
        var args = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Core.MathUtils.Add(int,int)", pageSize = 200 });
        var res = await _findRefs.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        var json = JsonSerializer.Serialize(sc);
        json.Should().Contain("\"success\":true");
        json.Should().Contain("OrderService.cs");
    }

    [Fact]
    public async Task DescribeSymbol_ByFqn_Returns_Type_Info()
    {
        var args = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Services.OrderService" });
        var res = await _describe.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        var json = JsonSerializer.Serialize(sc);
        json.Should().Contain("\"success\":true");
        json.Should().Contain("OrderService");
    }

    [Fact]
    public async Task GotoDefinition_Returns_Source_Location_For_Method()
    {
        var args = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Core.MathUtils.Multiply" });
        var res = await _gotoDef.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        var json = JsonSerializer.Serialize(sc);
        json.Should().Contain("\"success\":true");
        json.Should().Contain("MathUtils.cs");
    }

    [Fact]
    public async Task GetMethodDependencies_Reports_Calls_Reads_Writes()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            fullyQualifiedName = "SampleApp.Core.MathUtils.Add",
            depth = 2,
            includeCallers = true,
            pageSize = 200
        });
        var res = await _deps.ExecuteAsync(args);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        var json = JsonSerializer.Serialize(sc);
        json.Should().Contain("\"success\":true");
        // Should include a call to Helper.Increment and a write to Counter
        json.Should().Contain("Helper.Increment");
        json.Should().Contain("Counter");
    }

    [Fact]
    public async Task TestSymbolFormatting_Produces_Some_Symbols()
    {
        var res = await _fmt.ExecuteAsync(null);
        var msg = res.Content.FirstOrDefault()?.Text ?? "";
        res.IsError.Should().NotBeTrue(msg);
        var sc = (JsonElement)res.StructuredContent!;
        sc.GetProperty("success").GetBoolean().Should().BeTrue();
        var json = JsonSerializer.Serialize(sc);
        json.Should().Contain("\"success\":true");
        json.Should().Contain("symbolCount");
    }

    private static string GetRepoRoot()
    {
        // Traverse up from current test assembly location to find repo root that contains 'src' folder
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
