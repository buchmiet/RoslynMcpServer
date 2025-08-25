using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RoslynMcpServer.Roslyn;
using RoslynMcpServer.Tools;

class Program
{
    static async Task Main()
    {
        var root = FindRoot();
        var proj = Path.Combine(root, "samples", "SampleApp", "SampleApp.csproj");
        Console.WriteLine($"Project: {proj}");
        var host = new WorkspaceHost();
        var load = new LoadSolutionTool(host);
        var args = JsonSerializer.SerializeToElement(new { path = proj });
        var res = await load.ExecuteAsync(args);
        Console.WriteLine($"IsError={res.IsError}");
        foreach (var c in res.Content)
        {
            Console.WriteLine(c.Text);
        }

        var getType = new GetTypeInfoTool(host);
        var targs = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Core.MathUtils", pageSize = 50 });
        var tRes = await getType.ExecuteAsync(targs);
        Console.WriteLine("-- GetTypeInfo --");
        Console.WriteLine(tRes.Content[0].Text);

        var findRefs = new FindReferencesTool(host);
        var fargs = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Core.MathUtils.Add(int,int)" });
        var fRes = await findRefs.ExecuteAsync(fargs);
        Console.WriteLine("-- FindReferences --");
        Console.WriteLine(fRes.Content[0].Text);

        var describe = new DescribeSymbolTool(host);
        var dargs = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Services.OrderService" });
        var dRes = await describe.ExecuteAsync(dargs);
        Console.WriteLine("-- DescribeSymbol --");
        Console.WriteLine(dRes.Content[0].Text);

        var g2d = new GotoDefinitionTool(host);
        var gargs = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Core.MathUtils.Multiply" });
        var gRes = await g2d.ExecuteAsync(gargs);
        Console.WriteLine("-- GotoDefinition --");
        Console.WriteLine(gRes.Content[0].Text);

        var deps = new GetMethodDependenciesTool(host);
        var depsArgs = JsonSerializer.SerializeToElement(new { fullyQualifiedName = "SampleApp.Core.MathUtils.Add", depth = 2, includeCallers = true });
        var depsRes = await deps.ExecuteAsync(depsArgs);
        Console.WriteLine("-- GetMethodDependencies --");
        Console.WriteLine(depsRes.Content[0].Text);

        var fmt = new TestSymbolFormattingTool(host);
        var fmtRes = await fmt.ExecuteAsync(null);
        Console.WriteLine("-- TestSymbolFormatting --");
        Console.WriteLine(fmtRes.Content[0].Text);
    }

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        var di = new DirectoryInfo(dir);
        while (di != null)
        {
            if (Directory.Exists(Path.Combine(di.FullName, "src")) && Directory.Exists(Path.Combine(di.FullName, "samples")))
                return di.FullName;
            di = di.Parent!;
        }
        throw new Exception("no root");
    }
}
