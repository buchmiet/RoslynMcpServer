# Roslyn MCP Server — Inspector Setup (v0.1.0)

Status: working with MCP Inspector. Version: 0.1.0

## Build

```bash
dotnet build -c Release src/RoslynMcpServer/RoslynMcpServer.csproj
```

## Run (DLL)

```bash
dotnet ./src/RoslynMcpServer/bin/Release/net9.0/RoslynMcpServer.dll
```

## Use with MCP Inspector

Quick launch with the built DLL:

```bash
npx @modelcontextprotocol/inspector dotnet ./src/RoslynMcpServer/bin/Release/net9.0/RoslynMcpServer.dll
```

Or configure a `.mcp` file (e.g., in your home directory or project root):

```json
{
  "mcpServers": {
    "roslyn-index": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["/absolute/path/to/RoslynMcpServer.dll"],
      "env": { "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1" }
    }
  }
}
```

Notes
- Replace `/absolute/path/to/RoslynMcpServer.dll` with your actual built DLL path, e.g. `<repo>/src/RoslynMcpServer/bin/Release/net9.0/RoslynMcpServer.dll`.
- STDOUT is reserved for JSON-RPC; logs go to STDERR (Inspector shows them separately).
