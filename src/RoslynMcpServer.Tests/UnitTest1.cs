using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;
using RoslynMcpServer.Roslyn;
using RoslynMcpServer.Tools;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Tests;

public class GetMethodDependenciesToolTests : IAsyncLifetime
{
    private readonly string _tempRoot;
    private readonly string _projDir;
    private readonly string _projPath;
    private readonly WorkspaceHost _workspaceHost;

    public GetMethodDependenciesToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", Guid.NewGuid().ToString("N"));
        _projDir = Path.Combine(_tempRoot, "TempProj");
        Directory.CreateDirectory(_projDir);

        _projPath = Path.Combine(_projDir, "TempProj.csproj");
        _workspaceHost = new WorkspaceHost();
    }

    public async Task InitializeAsync()
    {
        // 1) Zbuduj minimalny projekt SDK + pliki źródłowe
        WriteFile(_projPath, GetCsProj());

        WriteFile(Path.Combine(_projDir, "Foo.cs"), GetFooSource());
        WriteFile(Path.Combine(_projDir, "Bar.cs"), GetBarSource());
        WriteFile(Path.Combine(_projDir, "Caller.cs"), GetCallerSource());

        // 2) Otwórz projekt w WorkspaceHost
        var opened = await _workspaceHost.OpenSolutionAsync(_projPath, CancellationToken.None, timeoutMs: 120_000);
        opened.ShouldBeTrue("MSBuildWorkspace powinien wczytać projekt testowy (SDK style).");
        _workspaceHost.GetSolution().ShouldNotBeNull();
        _workspaceHost.GetSolution()!.Projects.Count().ShouldBe(1);
    }

    public Task DisposeAsync()
    {
        try
        {
            _workspaceHost.Dispose();
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, true);
        }
        catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DirectDependencies_Should_Include_Calls_And_Reads_Writes()
    {
        // Arrange
        var tool = new GetMethodDependenciesTool(_workspaceHost);
        var args = JsonElementFrom($$"""
        {
          "fullyQualifiedName": "Temp.Foo.DoWork",
          "depth": 1,
          "includeCallers": false,
          "treatPropertiesAsMethods": true,
          "page": 1,
          "pageSize": 500,
          "timeoutMs": 120000
        }
        """);

        // Act
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        // Assert (ogólne)
        result.ShouldNotBeNull();
        result.IsError.ShouldBeNull();
        result.StructuredContent.ShouldNotBeNull();

        dynamic sc = result.StructuredContent!;
        ((bool)sc.success).ShouldBeTrue();

        // Symbol details
        ((string)sc.symbol.display).ShouldContain("void Temp.Foo.DoWork()");
        ((string)sc.symbol.containingType).ShouldBe("Temp.Foo");

        // Calls (bezpośrednie): Callee(int), .ctor Bar(), Bar.Static(), get_Name, set_Name, get_Stat, set_Stat
        var calls = ((System.Text.Json.JsonElement)sc.calls).EnumerateArray().Select(e => e.GetProperty("display").GetString()!).ToArray();

        calls.ShouldContain(s => s.Contains("Temp.Foo.Callee(Int32)"), "Powinien być widoczny bezpośredni call do Callee(int).");
        calls.ShouldContain(s => s.Contains("Temp.Bar..ctor()"), "Nowy obiekt Bar => wywołanie konstruktora jako call.");
        calls.ShouldContain(s => s.Contains("Temp.Bar.Static()"), "Statyczna metoda Bar.Static powinna być wykryta.");
        calls.ShouldContain(s => s.Contains("System.String Temp.Foo.Name.get()"), "Odczyt właściwości => get_ jako call gdy treatPropertiesAsMethods=true.");
        calls.ShouldContain(s => s.Contains("Void Temp.Foo.Name.set(System.String)"), "Zapis właściwości => set_ jako call gdy treatPropertiesAsMethods=true.");
        calls.ShouldContain(s => s.Contains("Int32 Temp.Bar.Stat.get()"), "Odczyt właściwości statycznej get_.");
        calls.ShouldContain(s => s.Contains("Void Temp.Bar.Stat.set(Int32)"), "Zapis właściwości statycznej set_.");

        // Reads: _count, Name (get), Stat (get)
        var reads = ((System.Text.Json.JsonElement)sc.reads).EnumerateArray().Select(e => e.GetProperty("display").GetString()!).ToArray();
        reads.ShouldContain(s => s.EndsWith("Temp.Foo._count"));
        reads.ShouldContain(s => s.EndsWith("Temp.Foo.Name"));
        reads.ShouldContain(s => s.EndsWith("Temp.Bar.Stat"));

        // Writes: _count, Name (set), Stat (set)
        var writes = ((System.Text.Json.JsonElement)sc.writes).EnumerateArray().Select(e => e.GetProperty("display").GetString()!).ToArray();
        writes.ShouldContain(s => s.EndsWith("Temp.Foo._count"));
        writes.ShouldContain(s => s.EndsWith("Temp.Foo.Name"));
        writes.ShouldContain(s => s.EndsWith("Temp.Bar.Stat"));
    }

    [Fact]
    public async Task TreatPropertiesAsMethods_False_Should_Not_Report_GetSet_As_Calls()
    {
        // Arrange
        var tool = new GetMethodDependenciesTool(_workspaceHost);
        var args = JsonElementFrom($$"""
        {
          "fullyQualifiedName": "Temp.Foo.DoWork",
          "depth": 1,
          "includeCallers": false,
          "treatPropertiesAsMethods": false,
          "page": 1,
          "pageSize": 500
        }
        """);

        // Act
        var result = await tool.ExecuteAsync(args, CancellationToken.None);
        dynamic sc = result.StructuredContent!;

        // Assert
        var calls = ((System.Text.Json.JsonElement)sc.calls).EnumerateArray().Select(e => e.GetProperty("display").GetString()!).ToArray();

        calls.ShouldNotContain(s => s.Contains(".Name.get("));
        calls.ShouldNotContain(s => s.Contains(".Name.set("));
        calls.ShouldNotContain(s => s.Contains(".Stat.get("));
        calls.ShouldNotContain(s => s.Contains(".Stat.set("));

        // ale zwykłe wywołania nadal są
        calls.ShouldContain(s => s.Contains("Temp.Foo.Callee(Int32)"));
        calls.ShouldContain(s => s.Contains("Temp.Bar..ctor()"));
        calls.ShouldContain(s => s.Contains("Temp.Bar.Static()"));
    }

    [Fact]
    public async Task IncludeCallers_Should_Return_AtLeast_One_Caller()
    {
        // Caller.cs: new Foo().DoWork();  => powinniśmy znaleźć wywołującego
        var tool = new GetMethodDependenciesTool(_workspaceHost);
        var args = JsonElementFrom($$"""
        {
          "fullyQualifiedName": "Temp.Foo.DoWork",
          "includeCallers": true,
          "pageSize": 200
        }
        """);

        var result = await tool.ExecuteAsync(args, CancellationToken.None);
        dynamic sc = result.StructuredContent!;

        // callers to tablica obiektów z display/file/line/column (jeśli źródło)
        var callers = ((System.Text.Json.JsonElement)sc.callers).EnumerateArray().ToArray();
        callers.Length.ShouldBeGreaterThan(0, "Powinien istnieć przynajmniej jeden wywołujący (Temp.Caller.Run).");

        var anyDisplay = callers.Any(c =>
            c.TryGetProperty("display", out var d) &&
            d.GetString()!.Contains("Temp.Caller.Run", StringComparison.Ordinal));

        anyDisplay.ShouldBeTrue("Powinniśmy trafić na Temp.Caller.Run() jako wywołującego DoWork().");
    }

    // ---- helpers ----
    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string GetCsProj() => """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>
</Project>
""";

    private static string GetFooSource() => """
namespace Temp;

public class Foo
{
    private int _count;
    public string Name { get; set; } = "";

    public void DoWork()
    {
        // pole + operacje złożone
        _count += 2;
        _count++;

        // właściwość (get + set)
        Name = Name + "x";

        // wywołanie metody instancyjnej
        Callee(42);

        // konstruktor (jako call) + metoda statyczna
        var b = new Bar();
        Bar.Static();

        // właściwość statyczna (get + set)
        var s = Bar.Stat;
        Bar.Stat = s + 1;
    }

    public void Callee(int x) { }
}
""";

    private static string GetBarSource() => """
namespace Temp;

public class Bar
{
    public static int Stat { get; set; }

    public Bar() { }

    public static void Static() { }
}
""";

    private static string GetCallerSource() => """
namespace Temp;

public static class Caller
{
    public static void Run()
    {
        var foo = new Foo();
        foo.DoWork();
    }
}
""";

    private static System.Text.Json.JsonElement JsonElementFrom(string json)
        => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
}
