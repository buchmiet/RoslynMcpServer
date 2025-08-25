# Roslyn MCP Server

A Model Context Protocol (MCP) server that provides Roslyn-based code analysis tools for .NET solutions and projects.

## Features

- **Load .NET solutions and projects** - Support for `.sln` and `.csproj` files
- **Type information retrieval** - Get detailed information about types including members, accessibility, and documentation
- **Find references** - Search for all references to a symbol across the entire solution
- **Symbol formatting** - Format symbols with source location information
- **Describe symbols** - Get detailed information about any symbol by name or position
- **Go to definition** - Find the source definition of symbols

## Prerequisites

- .NET 9.0 SDK or later
- MSBuild (included with .NET SDK)

## Building

```bash
dotnet build src/RoslynMcpServer/RoslynMcpServer.csproj
```

## Running

The server communicates via JSON-RPC 2.0 over stdin/stdout:

```bash
dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

### Run as DLL

For production use, build the Release configuration and run the compiled DLL directly:

```bash
# Build Release configuration
dotnet build -c Release src/RoslynMcpServer/RoslynMcpServer.csproj

# Run the DLL directly
dotnet /home/lukasz/RoslynMcpServer/src/RoslynMcpServer/bin/Release/net9.0/RoslynMcpServer.dll
```

The server uses newline-delimited JSON (NDJSON) for JSON-RPC messages as per the [MCP STDIO transport specification](https://modelcontextprotocol.io/docs/1.0.0/specification/transports#stdio). Messages are delimited by newlines (LF), and must not contain embedded newlines. All logging and diagnostic output is sent to STDERR, keeping STDOUT clean for JSON-RPC communication.

### Self-Test STDIO

To verify the server is working correctly with clean STDIO:

```bash
# Test the JSON-RPC communication (NDJSON format)
printf '{"jsonrpc":"2.0","method":"tools/list","params":{},"id":1}\n' | \
dotnet /home/lukasz/RoslynMcpServer/src/RoslynMcpServer/bin/Release/net9.0/RoslynMcpServer.dll

# Expected: JSON-RPC response as a single line terminated with newline, no extra text on STDOUT
```

## Available Tools

### 1. load_solution

Load a .NET solution or project file for analysis.

**Parameters:**
- `path` (string, required): Path to the .sln or .csproj file

**Example:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "load_solution",
    "arguments": {
      "path": "/path/to/solution.sln"
    }
  }],
  "id": 1
}
```

**Response:**
```json
{
  "success": true,
  "solutionPath": "/path/to/solution.sln",
  "projectCount": 3,
  "projects": [
    {"name": "Project1", "tfm": "net9.0"},
    {"name": "Project2", "tfm": "net9.0"}
  ]
}
```

### 2. get_type_info

Get detailed information about a type including members, inheritance, and documentation.

**Parameters:**
- `fullyQualifiedName` (string, required): Fully qualified name of the type (e.g., System.String, MyNamespace.MyClass)
- `page` (integer, optional): Page number (1-based), default: 1
- `pageSize` (integer, optional): Number of results per page (1-500), default: 200

**Example:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "get_type_info",
    "arguments": {
      "fullyQualifiedName": "System.String",
      "pageSize": 10
    }
  }],
  "id": 2
}
```

**Response:**
```json
{
  "success": true,
  "symbol": {
    "display": "string",
    "file": null,
    "line": -1,
    "column": -1
  },
  "totalMembers": 206,
  "page": 1,
  "pageSize": 10,
  "members": [
    {
      "Name": "Empty",
      "Kind": "Field",
      "Accessibility": "Public",
      "IsStatic": true,
      "Type": "string",
      "Parameters": null
    }
  ],
  "nextCursor": "page=2"
}
```

### 3. find_references

Find all references to a symbol in the loaded solution.

**Parameters:**
- `fullyQualifiedName` (string, required): Fully qualified name of the symbol
- `page` (integer, optional): Page number (1-based), default: 1
- `pageSize` (integer, optional): Number of results per page (1-500), default: 200
- `timeoutMs` (integer, optional): Timeout in milliseconds (1000-300000), default: 60000

**Example:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "find_references",
    "arguments": {
      "fullyQualifiedName": "MyNamespace.MyClass",
      "pageSize": 50
    }
  }],
  "id": 3
}
```

**Response:**
```json
{
  "success": true,
  "total": 125,
  "page": 1,
  "pageSize": 50,
  "references": [
    {
      "file": "/path/to/file.cs",
      "line": 42,
      "column": 15,
      "text": "var instance = new MyClass();"
    },
    {
      "file": "/path/to/another.cs",
      "line": 10,
      "column": 20,
      "text": "MyClass.StaticMethod();"
    }
  ],
  "nextCursor": "eyJvZmZzZXQiOjUwfQ=="
}
```

The `nextCursor` is an opaque token that can be passed as the `page` parameter to get the next page of results.

### 4. describe_symbol

Get detailed information about a symbol by its fully qualified name or file position.

**Parameters (Option 1 - by name):**
- `fullyQualifiedName` (string, required): Fully qualified name of the symbol

**Parameters (Option 2 - by position):**
- `file` (string, required): File path
- `line` (integer, required): Line number (1-based)
- `column` (integer, required): Column number (1-based)

**Example 1 - By Name:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "describe_symbol",
    "arguments": {
      "fullyQualifiedName": "System.Console.WriteLine"
    }
  }],
  "id": 4
}
```

**Example 2 - By Position:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "describe_symbol",
    "arguments": {
      "file": "/path/to/file.cs",
      "line": 42,
      "column": 15
    }
  }],
  "id": 5
}
```

**Response:**
```json
{
  "success": true,
  "display": "void Console.WriteLine(string? value)",
  "file": "/source/path/Console.cs",
  "line": 123,
  "column": 25,
  "kind": "Method",
  "containingType": "System.Console",
  "containingNamespace": "System"
}
```

### 5. goto_definition

Find the source definition of a symbol. If the source is not available, returns the metadata definition.

**Parameters:**
- `fullyQualifiedName` (string, required): Fully qualified name of the symbol

**Example:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "goto_definition",
    "arguments": {
      "fullyQualifiedName": "MyNamespace.MyClass.MyMethod"
    }
  }],
  "id": 6
}
```

### 6. get_method_dependencies

Analyze method body and list called methods and member reads/writes; optionally include callers of the root method.

Parameters:
- `fullyQualifiedName` (string) or `file`+`line`+`column`: Target method/property accessor
- `depth` (integer, optional): Traversal depth for transitive calls (default: 1)
- `includeCallers` (boolean, optional): Include list of callers of the root (default: false)
- `treatPropertiesAsMethods` (boolean, optional): Report get_/set_ as calls (default: true)
- `page`, `pageSize` (integers, optional): Pagination for the `calls` list (default: 1 / 200)
- `timeoutMs` (integer, optional): 1000–300000 (default: 60000)

Example:
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "get_method_dependencies",
    "arguments": {
      "fullyQualifiedName": "MyApp.Core.Utils.DoWork",
      "depth": 2,
      "includeCallers": true,
      "pageSize": 200
    }
  }],
  "id": 7
}
```

Response (abridged):
```json
{
  "success": true,
  "symbol": {"display": "int MyApp.Core.Utils.DoWork()", "file": "/.../Utils.cs", "line": 10, "column": 17},
  "totalCalls": 4,
  "calls": [{"display": "void MyApp.Core.Helper.Log()", "file": "/.../Helper.cs", "line": 5, "column": 17}],
  "reads": [{"display": "int MyApp.Core.Utils.Counter", "file": "/.../Utils.cs", "line": 4, "column": 19}],
  "writes": [{"display": "int MyApp.Core.Utils.Counter", "file": "/.../Utils.cs", "line": 4, "column": 19}],
  "callers": [{"display": "int MyApp.Services.Svc.Run()", "file": "/.../Svc.cs", "line": 12, "column": 15}]
}
```

### STDIO Examples (CLI)

All tools communicate via MCP over STDIO using NDJSON. Below are quick CLI snippets using `printf` piped into the running server.

Note: Load a solution or project first.

```bash
# Load solution or project
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"load_solution","arguments":{"path":"/absolute/path/to/your.sln"}}],"id":1}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

get_method_dependencies:
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"get_method_dependencies","arguments":{"fullyQualifiedName":"MyApp.Core.Utils.DoWork","depth":2,"includeCallers":true}}],"id":2}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

get_inheritance_tree:
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"get_inheritance_tree","arguments":{"fullyQualifiedName":"MyApp.Core.BaseType","direction":"descendants","includeInterfaces":true}}],"id":3}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

get_all_implementations:
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"get_all_implementations","arguments":{"fullyQualifiedName":"MyApp.Core.IMyInterface","includeDerivedInterfaces":true}}],"id":4}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

get_type_info:
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"get_type_info","arguments":{"fullyQualifiedName":"System.String","pageSize":10}}],"id":5}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

find_references:
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"find_references","arguments":{"fullyQualifiedName":"MyApp.Core.SomeType","pageSize":50}}],"id":6}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

describe_symbol (by name):
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"describe_symbol","arguments":{"fullyQualifiedName":"System.Console.WriteLine"}}],"id":7}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

describe_symbol (by position):
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"describe_symbol","arguments":{"file":"/absolute/path/to/File.cs","line":42,"column":15}}],"id":8}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

goto_definition:
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"goto_definition","arguments":{"fullyQualifiedName":"MyApp.Core.SomeType.SomeMethod"}}],"id":9}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

test_symbol_formatting:
```bash
printf '{"jsonrpc":"2.0","method":"tools/call","params":[{"name":"test_symbol_formatting","arguments":{}}],"id":10}\n' \
| dotnet run --project src/RoslynMcpServer/RoslynMcpServer.csproj
```

### 7. get_inheritance_tree

Return full inheritance tree: ancestors chain, implemented interfaces, descendants (transitively), and optionally overrides per member.

Parameters:
- `fullyQualifiedName` (string) or `file`+`line`+`column`: Target type
- `direction` (string, optional): `both|ancestors|descendants` (default: `both`)
- `includeInterfaces` (boolean, optional): Include interfaces (default: true)
- `includeOverrides` (boolean, optional): Include overrides per member (default: false)
- `maxDepth` (integer, optional): Depth limit for descendants tree (default: 10)
- `solutionOnly` (boolean, optional): Only symbols with source in the loaded solution (default: true)
- `page`, `pageSize` (integers, optional): Pagination for flat descendants list (default: 1 / 200)
- `timeoutMs` (integer, optional): 1000–300000 (default: 60000)

Example:
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "get_inheritance_tree",
    "arguments": {
      "fullyQualifiedName": "MyApp.Core.BaseType",
      "direction": "descendants",
      "includeInterfaces": true
    }
  }],
  "id": 8
}
```

Response (abridged):
```json
{
  "success": true,
  "root": {"display": "class MyApp.Core.BaseType", "file": "/.../BaseType.cs", "line": 5},
  "descendantsTree": {"display": "class MyApp.Core.Derived", "children": [...]},
  "descendantsFlat": [{"display": "class MyApp.Core.Derived"}],
  "page": 1, "pageSize": 200, "total": 3
}
```

### 8. get_all_implementations

List all implementations of an interface type, or implementations of a specific interface member.

Parameters:
- `fullyQualifiedName` (string) or `file`+`line`+`column`: Target interface or interface member
- `member` (string, optional): Member name when FQN points to an interface type (e.g., `Compute`)
- `solutionOnly` (boolean, optional): Only symbols with source in the loaded solution (default: true)
- `includeDerivedInterfaces` (boolean, optional): Include derived interfaces (default: true)
- `page`, `pageSize` (integers, optional): Pagination (default: 1 / 200)
- `timeoutMs` (integer, optional): 1000–300000 (default: 60000)

Example:
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": [{
    "name": "get_all_implementations",
    "arguments": {
      "fullyQualifiedName": "MyApp.Core.IMyInterface",
      "includeDerivedInterfaces": true,
      "pageSize": 200
    }
  }],
  "id": 9
}
```

Response (abridged):
```json
{
  "success": true,
  "interface": {"display": "interface MyApp.Core.IMyInterface"},
  "derivedInterfaces": [{"display": "interface MyApp.Core.IMySubInterface"}],
  "implementations": [{"display": "class MyApp.Core.MyImpl", "file": "/.../MyImpl.cs", "line": 7}],
  "total": 5
}
```

**Response:**
```json
{
  "success": true,
  "display": "void MyMethod(string param)",
  "file": "/path/to/MyClass.cs",
  "line": 42,
  "column": 17,
  "isSourceDefinition": true,
  "isFromMetadata": false,
  "kind": "Method",
  "containingType": "MyNamespace.MyClass",
  "containingNamespace": "MyNamespace"
}
```

## Protocol

The server implements the Model Context Protocol (MCP) using JSON-RPC 2.0. Messages are transmitted using the NDJSON (newline-delimited JSON) format.

### Message Format

Request:
```
{"jsonrpc":"2.0","method":"<method>","params":<params>,"id":<id>}\n
```

Response:
```
{"jsonrpc":"2.0","id":<id>,"result":<result>}\n
```

### Transport (STDIO)

The server uses the MCP STDIO transport with NDJSON format:
- **Format**: One JSON object per line, terminated by `\n` (LF)
- **Encoding**: UTF-8
- **No embedded newlines**: JSON messages must not contain literal newlines
- **Output streams**: 
  - STDOUT: Only valid MCP messages
  - STDERR: All logs and diagnostic output

For more details, see the [MCP Transport specification](https://modelcontextprotocol.io/docs/1.0.0/specification/transports#stdio).

## Error Handling

- Missing or invalid arguments return an error with `isError: true`
- Timeouts are handled gracefully with appropriate error messages
- WorkspaceFailed events are logged to stderr for debugging

## Known Issues

- `OpenSolutionAsync` may hang in some environments. The server includes a 90-second timeout as a workaround.
- Target framework detection may not work for all project types.

## Development

The project is structured as follows:

```
src/RoslynMcpServer/
   Infrastructure/     # MCP protocol implementation
      McpServer.cs   # Main MCP server with tool registration
      JsonRpcLoop.cs # JSON-RPC message handling
   Roslyn/            # Roslyn integration
      WorkspaceHost.cs     # MSBuild workspace management
      SymbolFormatting.cs  # Symbol display formatting
   Tools/             # MCP tool implementations
       LoadSolutionTool.cs    # Solution/project loading
       GetTypeInfoTool.cs     # Type information retrieval
       FindReferencesTool.cs  # Reference finding
       DescribeSymbolTool.cs  # Symbol description by name or position
       GotoDefinitionTool.cs  # Find symbol source definition
```

## License

MIT
