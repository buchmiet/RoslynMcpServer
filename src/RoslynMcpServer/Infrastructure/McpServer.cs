using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using RoslynMcpServer.Roslyn;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Infrastructure;

public class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private const string ServerName = "roslyn-mcp-server";
    private const string ServerVersion = "1.0.0";
    
    private readonly ILogger<McpServer> _logger;
    private readonly WorkspaceHost _workspaceHost;
    private readonly LoadProjectTool _loadProjectTool;
    private readonly TestSymbolFormattingTool _testSymbolFormattingTool;
    private readonly GetTypeInfoTool _getTypeInfoTool;
    private readonly FindReferencesTool _findReferencesTool;
    private readonly DescribeSymbolTool _describeSymbolTool;
    private readonly GotoDefinitionTool _gotoDefinitionTool;
    private readonly GetMethodDependenciesTool _getMethodDependenciesTool;
    private readonly GetInheritanceTreeTool _getInheritanceTreeTool;
    private readonly GetAllImplementationsTool _getAllImplementationsTool;

    public McpServer(ILogger<McpServer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workspaceHost = new WorkspaceHost();
        _loadProjectTool = new LoadProjectTool(_workspaceHost);
        _testSymbolFormattingTool = new TestSymbolFormattingTool(_workspaceHost);
        _getTypeInfoTool = new GetTypeInfoTool(_workspaceHost);
        _findReferencesTool = new FindReferencesTool(_workspaceHost);
        _describeSymbolTool = new DescribeSymbolTool(_workspaceHost);
        _gotoDefinitionTool = new GotoDefinitionTool(_workspaceHost);
        _getMethodDependenciesTool = new GetMethodDependenciesTool(_workspaceHost);
        _getInheritanceTreeTool = new GetInheritanceTreeTool(_workspaceHost);
        _getAllImplementationsTool = new GetAllImplementationsTool(_workspaceHost);
    }

    // MCP handshake: catch-all initialize (object params)
    [JsonRpcMethod("initialize")]
    public object Initialize(JsonElement anyParams)
    {
        // Domyślna wersja, jeśli klient nie przekaże
        string pv = "2025-06-18";

        if (anyParams.ValueKind == JsonValueKind.Object)
        {
            if (anyParams.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String)
                pv = v.GetString()!;
            else if (anyParams.TryGetProperty("ProtocolVersion", out var v2) && v2.ValueKind == JsonValueKind.String)
                pv = v2.GetString()!;
        }

        // Minimalny InitializeResult zgodny ze spec MCP
        return new
        {
            protocolVersion = pv,
            serverInfo = new { name = "roslyn-index", version = "0.1.0" },
            capabilities = new
            {
                tools = new { },
                logging = new { },
                experimental = new { }
            }
        };
    }

    // Wariant pozycyjny (niektóre klienty mogą tak wołać):
    [JsonRpcMethod("initialize")]
    public object Initialize(JsonElement a, JsonElement b, JsonElement c)
    {
        if (a.ValueKind == JsonValueKind.Object) return Initialize(a);
        if (c.ValueKind == JsonValueKind.Object) return Initialize(c);
        return Initialize(new JsonElement());
    }

    // Object-based initialize (spec-compliant)
    [JsonRpcMethod("initialize")]
    public Task<InitializeResult> Initialize(InitializeParams parameters, CancellationToken cancellationToken = default)
    {
        return InitializeCore(
            parameters.ProtocolVersion,
            parameters.Capabilities,
            parameters.ClientInfo,
            cancellationToken,
            parameters.Capabilities);
    }

    // Positional 3-parameter initialize (Claude uses this)
    [JsonRpcMethod("initialize")]
    public Task<InitializeResult> Initialize(
        string protocolVersion,
        ClientCapabilities capabilities,
        Implementation clientInfo,
        CancellationToken cancellationToken = default)
    {
        return InitializeCore(protocolVersion, capabilities, clientInfo, cancellationToken, capabilities);
    }

    // Universal 3-parameter initialize (handles any order)
    [JsonRpcMethod("initialize")]
    public Task<InitializeResult> Initialize(object a, object b, object c, CancellationToken cancellationToken = default)
    {
        // Rozpoznaj kolejność dynamicznie:
        string? protocolVersion = null;
        Implementation? clientInfo = null;
        object? capabilities = null;

        foreach (var arg in new[] { a, b, c })
        {
            switch (arg)
            {
                case string s:
                    protocolVersion ??= s;
                    break;
                case JsonElement je:
                    TryClassifyJson(je, ref clientInfo, ref capabilities);
                    break;
                default:
                    if (arg is not null)
                    {
                        var je2 = JsonSerializer.SerializeToElement(arg);
                        TryClassifyJson(je2, ref clientInfo, ref capabilities);
                    }
                    break;
            }
        }

        protocolVersion ??= "2025-06-18";
        clientInfo ??= new Implementation { Name = "unknown-client", Version = "0" };
        capabilities ??= new { };

        return InitializeCore(protocolVersion, new ClientCapabilities { Tools = null, Logging = null, Experimental = null }, clientInfo, cancellationToken, capabilities);
    }

    private static void TryClassifyJson(JsonElement je, ref Implementation? clientInfo, ref object? capabilities)
    {
        // clientInfo: { "name": "...", "version": "..." } i zazwyczaj tylko te dwa pola
        // capabilities może mieć tools, logging, experimental itp.
        if (je.ValueKind == JsonValueKind.Object)
        {
            bool hasName = je.TryGetProperty("name", out _);
            bool hasVersion = je.TryGetProperty("version", out _);
            bool hasTools = je.TryGetProperty("tools", out _);
            bool hasLogging = je.TryGetProperty("logging", out _);
            bool hasExperimental = je.TryGetProperty("experimental", out _);
            
            // Jeśli ma name i version, ale nie ma typowych pól capabilities, to pewnie clientInfo
            if (hasName && hasVersion && !hasTools && !hasLogging && !hasExperimental)
            {
                try
                {
                    var ci = je.Deserialize<Implementation>();
                    if (ci is not null) 
                    {
                        clientInfo ??= ci;
                        return;
                    }
                }
                catch { /* ignore */ }
            }
        }
        // w pozostałych przypadkach traktuj jako capabilities (dowolny kształt)
        capabilities ??= je;
    }

    private Task<InitializeResult> InitializeCore(
        string protocolVersion,
        ClientCapabilities? capabilities,
        object? clientInfo,
        CancellationToken cancellationToken,
        object? rawCapabilities = null)
    {
        // For MVP, we'll accept the client's protocol version if it's compatible
        // In production, you'd negotiate the version properly
        var negotiatedVersion = protocolVersion;
        
        var result = new InitializeResult
        {
            ProtocolVersion = negotiatedVersion,
            ServerInfo = new Implementation
            {
                Name = "RoslynMcpServer",
                Version = "0.1.0"
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
                Experimental = new { }
            },
            Instructions = "Use tools/list then tools/call; load_project first."
        };
        
        return Task.FromResult(result);
    }

    // Handle initialized notification (no response)
    [JsonRpcMethod("initialized")]
    public void Initialized(object? parameters = null)
    {
        // Client has acknowledged initialization
        // Could log or set a flag here if needed
    }

    [JsonRpcMethod("tools/list", UseSingleObjectParameterDeserialization = true)]
    public Task<ToolsListResult> ListTools(JsonElement? _ = default, CancellationToken cancellationToken = default)
    {
        // Console.Error.WriteLine("MCP ListTools called");
        
        var tools = new List<Tool>
        {
            new Tool
            {
                Name = "load_project",
                Description = "Load a .NET project file (.csproj) or solution (.sln) for analysis. Prefer .csproj files. Use ABSOLUTE paths only!",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new
                        {
                            type = "string",
                            description = "ABSOLUTE path to the .csproj (preferred) or .sln file. Must be a full absolute path!"
                        }
                    },
                    required = new[] { "path" }
                }
            },
            new Tool
            {
                Name = "get_type_info",
                Description = "Get detailed information about a type including members, inheritance, and documentation",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fullyQualifiedName = new
                        {
                            type = "string",
                            description = "Fully qualified name of the type (e.g., System.String, MyNamespace.MyClass)"
                        },
                        page = new
                        {
                            type = "integer",
                            description = "Page number (1-based)",
                            minimum = 1,
                            @default = 1
                        },
                        pageSize = new
                        {
                            type = "integer",
                            description = "Number of results per page",
                            minimum = 1,
                            maximum = 500,
                            @default = 200
                        }
                    },
                    required = new[] { "fullyQualifiedName" }
                }
            },
            new Tool
            {
                Name = "get_inheritance_tree",
                Description = "Return full inheritance tree (ancestors, interfaces, descendants; optional overrides)",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fullyQualifiedName = new { type = "string" },
                        file = new { type = "string" },
                        line = new { type = "integer", minimum = 1 },
                        column = new { type = "integer", minimum = 1 },
                        direction = new { type = "string", enumValues = new[] { "both", "ancestors", "descendants" }, _default = "both" },
                        includeInterfaces = new { type = "boolean", _default = true },
                        includeOverrides = new { type = "boolean", _default = false },
                        maxDepth = new { type = "integer", minimum = 1, maximum = 100, _default = 10 },
                        solutionOnly = new { type = "boolean", _default = true },
                        page = new { type = "integer", minimum = 1, _default = 1 },
                        pageSize = new { type = "integer", minimum = 1, maximum = 500, _default = 200 },
                        timeoutMs = new { type = "integer", minimum = 1000, maximum = 300000, _default = 60000 }
                    }
                }
            },
            new Tool
            {
                Name = "get_all_implementations",
                Description = "List all implementations of an interface, or implementations of a specific interface member",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fullyQualifiedName = new { type = "string" },
                        file = new { type = "string" },
                        line = new { type = "integer", minimum = 1 },
                        column = new { type = "integer", minimum = 1 },
                        member = new { type = "string", description = "Optional member name when FQN targets interface type" },
                        solutionOnly = new { type = "boolean", _default = true },
                        includeDerivedInterfaces = new { type = "boolean", _default = true },
                        page = new { type = "integer", minimum = 1, _default = 1 },
                        pageSize = new { type = "integer", minimum = 1, maximum = 500, _default = 200 },
                        timeoutMs = new { type = "integer", minimum = 1000, maximum = 300000, _default = 60000 }
                    }
                }
            },
            new Tool
            {
                Name = "find_references",
                Description = "Find all references to a symbol in the loaded solution",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fullyQualifiedName = new
                        {
                            type = "string",
                            description = "Fully qualified name of the symbol"
                        },
                        page = new
                        {
                            type = "integer",
                            description = "Page number (1-based)",
                            minimum = 1,
                            @default = 1
                        },
                        pageSize = new
                        {
                            type = "integer",
                            description = "Number of results per page",
                            minimum = 1,
                            maximum = 500,
                            @default = 200
                        },
                        timeoutMs = new
                        {
                            type = "integer",
                            description = "Timeout in milliseconds",
                            minimum = 1000,
                            maximum = 300000,
                            @default = 60000
                        }
                    },
                    required = new[] { "fullyQualifiedName" }
                }
            },
            new Tool
            {
                Name = "test_symbol_formatting",
                Description = "Test tool to demonstrate symbol formatting with location info",
                InputSchema = new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                }
            },
            new Tool
            {
                Name = "describe_symbol",
                Description = "Get detailed information about a symbol by fully qualified name or file position",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fullyQualifiedName = new
                        {
                            type = "string",
                            description = "Fully qualified name of the symbol (provide this OR file/line/column)"
                        },
                        file = new
                        {
                            type = "string",
                            description = "File path (use with line and column)"
                        },
                        line = new
                        {
                            type = "integer",
                            description = "Line number 1-based (use with file and column)"
                        },
                        column = new
                        {
                            type = "integer",
                            description = "Column number 1-based (use with file and line)"
                        }
                    }
                }
            },
            new Tool
            {
                Name = "goto_definition",
                Description = "Find the source definition of a symbol",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fullyQualifiedName = new
                        {
                            type = "string",
                            description = "Fully qualified name of the symbol"
                        }
                    },
                    required = new[] { "fullyQualifiedName" }
                }
            },
            new Tool
            {
                Name = "get_method_dependencies",
                Description = "Analyze method dependencies: calls, reads/writes of fields/properties; optional callers",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fullyQualifiedName = new { type = "string", description = "Fully qualified name (or use file/line/column)" },
                        file = new { type = "string" },
                        line = new { type = "integer", minimum = 1 },
                        column = new { type = "integer", minimum = 1 },
                        depth = new { type = "integer", minimum = 1, @default = 1 },
                        includeCallers = new { type = "boolean", @default = false },
                        treatPropertiesAsMethods = new { type = "boolean", @default = true },
                        page = new { type = "integer", minimum = 1, @default = 1 },
                        pageSize = new { type = "integer", minimum = 1, maximum = 500, @default = 200 },
                        timeoutMs = new { type = "integer", minimum = 1000, maximum = 300000, @default = 60000 }
                    }
                }
            }

        };

        return Task.FromResult(new ToolsListResult { Tools = tools });
    }


    // tools/call – wariant 2-parametrowy (dopasowanie do named args: { name, arguments })
    [JsonRpcMethod("tools/call")]
    public Task<ToolCallResult> CallTool(string name, JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        var p = new ToolCallParams { Name = name, Arguments = arguments };
        return CallTool(p, cancellationToken);
    }

    [JsonRpcMethod("tools/call", UseSingleObjectParameterDeserialization = true)]
    public async Task<ToolCallResult> CallTool(ToolCallParams parameters, CancellationToken cancellationToken = default)
    {
        // Console.Error.WriteLine($"MCP CallTool called: name={parameters?.Name}, arguments={parameters?.Arguments?.ToString() ?? "null"}");
        
        var name = parameters?.Name;
        var arguments = parameters?.Arguments;
        
        // Log tool usage
        _logger.LogInformation("Tool: {ToolName} | Parameters: {Parameters}", 
            name ?? "unknown", 
            arguments?.ToString() ?? "null");
        
        switch (name)
        {
            case "load_project":
                return await _loadProjectTool.ExecuteAsync(arguments);
                
            case "load_solution": // Keep for backward compatibility
                return await _loadProjectTool.ExecuteAsync(arguments);
                
            case "test_symbol_formatting":
                return await _testSymbolFormattingTool.ExecuteAsync(arguments);
                
            case "get_type_info":
                return await _getTypeInfoTool.ExecuteAsync(arguments);
                
            case "find_references":
                return await _findReferencesTool.ExecuteAsync(arguments, cancellationToken);
            
            case "describe_symbol":
                return await _describeSymbolTool.ExecuteAsync(arguments, cancellationToken);
            
            case "goto_definition":
                return await _gotoDefinitionTool.ExecuteAsync(arguments, cancellationToken);
            
            case "get_method_dependencies":
                return await _getMethodDependenciesTool.ExecuteAsync(arguments, cancellationToken);

            case "get_inheritance_tree":
                return await _getInheritanceTreeTool.ExecuteAsync(arguments, cancellationToken);

            case "get_all_implementations":
                return await _getAllImplementationsTool.ExecuteAsync(arguments, cancellationToken);

            default:
                return new ToolCallResult
                {
                    IsError = true,
                    Content =
                    [
                        new ToolContent
                        {
                            Type = "text",
                            Text = $"Unknown tool: {name}"
                        }
                    ]
                };
        }
    }

    // Positional 3-parameter overload: [name, arguments, options]
    [JsonRpcMethod("tools/call")]
    public Task<ToolCallResult> CallTool(string name, JsonElement? arguments, JsonElement? _ignoredOptions, CancellationToken cancellationToken = default)
    {
        var p = new ToolCallParams { Name = name, Arguments = arguments };
        return CallTool(p, cancellationToken);
    }
}

#region MCP Protocol Types

// Implementation type used for both clientInfo and serverInfo
public class Implementation
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public required string Version { get; set; }
}

public class ClientCapabilities
{
    [JsonPropertyName("tools")]
    public object? Tools { get; set; }
    
    [JsonPropertyName("logging")]
    public object? Logging { get; set; }
    
    [JsonPropertyName("experimental")]
    public object? Experimental { get; set; }
}

public class InitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; set; }

    [JsonPropertyName("clientInfo")]
    public required Implementation ClientInfo { get; set; }

    [JsonPropertyName("capabilities")]
    public required ClientCapabilities Capabilities { get; set; }
}

public class InitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; set; }

    [JsonPropertyName("serverInfo")]
    public required Implementation ServerInfo { get; set; }

    [JsonPropertyName("capabilities")]
    public required ServerCapabilities Capabilities { get; set; }
    
    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }
    
    [JsonPropertyName("_meta")]
    public Dictionary<string, object>? Meta { get; set; }
}

public class ServerCapabilities
{
    [JsonPropertyName("tools")]
    public ToolsCapability? Tools { get; set; }
    
    [JsonPropertyName("resources")]
    public object? Resources { get; set; }
    
    [JsonPropertyName("prompts")]
    public object? Prompts { get; set; }
    
    [JsonPropertyName("logging")]
    public object? Logging { get; set; }
    
    [JsonPropertyName("experimental")]
    public object? Experimental { get; set; }
}

public class ToolsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}

public class ToolsListResult
{
    [JsonPropertyName("tools")]
    public required List<Tool> Tools { get; set; }
}

public class Tool
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("inputSchema")]
    public required object InputSchema { get; set; }
}

public class ToolCallParams
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}

public class ToolCallResult
{
    [JsonPropertyName("content")]
    public required List<ToolContent> Content { get; set; }

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; set; }
    
    [JsonPropertyName("structuredContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? StructuredContent { get; set; }
}

public class ToolContent
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
}

#endregion
