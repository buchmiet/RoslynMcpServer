using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Infrastructure;
using Serilog;

namespace RoslynMcpServer;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Wyciszenie banerów/telemetrii .NET
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");

        // Configure Serilog - only for tool usage logging
        var logDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var logPath = Path.Combine(logDirectory, "tooluse.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}",
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .CreateLogger();
        
        Console.Error.WriteLine($"MCP: Logging to {logDirectory}tooluse{{YYYYMMDD}}.log");

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(Log.Logger);
        });

        try
        {
            // Krótki log na STDERR (stdout MUSI być czysty dla MCP)
            Console.Error.WriteLine("MCP: starting (NDJSON over stdio)...");
            
            var logger = loggerFactory.CreateLogger<McpServer>();
            var mcpServer  = new McpServer(logger);
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
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
