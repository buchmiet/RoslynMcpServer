using System;
using System.Threading.Tasks;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Wyciszenie banerów/telemetrii .NET
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");

        try
        {
            // Krótki log na STDERR (stdout MUSI być czysty dla MCP)
            Console.Error.WriteLine("MCP: starting (NDJSON over stdio)...");
            
            var mcpServer  = new McpServer();
            var jsonRpcLoop = new JsonRpcLoop();

            Console.Error.WriteLine("MCP: listening...");
            await jsonRpcLoop.RunAsync(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                mcpServer,
                default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex}");
            Environment.Exit(1);
        }
    }
}
