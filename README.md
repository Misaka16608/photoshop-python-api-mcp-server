# Photoshop MCP Server (C# Edition)

MCP server that lets AI assistants (Claude Code, Claude Desktop, etc.) control Adobe Photoshop via the stdio protocol.

> **This is a full C# rewrite** of [loonghao/photoshop-python-api-mcp-server](https://github.com/loonghao/photoshop-python-api-mcp-server).
> The original Python version suffered from COM blocking issues (GIL + synchronous COM calls hang the entire session).
> This rewrite uses .NET native COM interop with a dedicated STA thread and per-call timeout protection.

## Why the rewrite?

| Problem in Python | Solved in C# |
|---|---|
| GIL + blocking COM → entire process freezes | Dedicated STA thread, MCP protocol layer unaffected |
| Only `execute_javascript` had timeout | **Every** COM call has configurable timeout (`PS_MCP_TIMEOUT`, default 15s) |
| `print()` leaks to stdout → corrupts JSON-RPC | All logging to stderr via `Microsoft.Extensions.Logging` |
| Manual `register_tool(mcp, func, name)` | `[McpServerToolType]` attribute → auto-discovered by `WithToolsFromAssembly()` |

## Requirements

- Windows only (COM-based Photoshop automation)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (for building)
- Adobe Photoshop (CC2017–2024 tested)

## Quick Start

### 1. Build

```bash
cd PhotoshopMcpServer
dotnet publish src/PhotoshopMcpServer -c Release -r win-x64 --self-contained -o publish
```

### 2. Configure MCP client

Add to your project's `.mcp.json` (or Claude Code / Claude Desktop settings):

```json
{
  "mcpServers": {
    "photoshop": {
      "command": "<absolute-path>\\PhotoshopMcpServer\\publish\\photoshop-mcp-server.exe",
      "env": {
        "PS_MCP_TIMEOUT": "30"
      }
    }
  }
}
```

### 3. Restart

Restart your MCP client session to load the new server. The server connects to Photoshop on first tool invocation (lazy initialization).

## Available Tools

### Document

| Tool | Description |
|---|---|
| `photoshop_create_document` | Create new document (width, height, name, mode) |
| `photoshop_open_document` | Open existing file |
| `photoshop_save_document` | Save as PSD / JPEG / PNG |

### Layer

| Tool | Description |
|---|---|
| `photoshop_create_text_layer` | Create text layer with position, size, color |
| `photoshop_create_solid_color_layer` | Create solid color fill layer |
| `photoshop_get_layer_info` | Get layer details by name or index (bounds, opacity, text props, blend mode) |
| `photoshop_delete_layer` | Delete layer by name or index |
| `photoshop_modify_layer` | Rename, reposition, change text, toggle visibility, opacity, blend mode |
| `photoshop_get_layer_thumbnail` | Export layer as base64 PNG thumbnail |
| `photoshop_export_layer` | Export layer as PNG file to disk (with scale & trim options) |

### Session

| Tool | Description |
|---|---|
| `photoshop_get_session_info` | Photoshop version, documents list, preferences |
| `photoshop_get_active_document_info` | Active document metadata via Action Manager |
| `photoshop_get_selection_info` | Current selection bounds and area |

### Resources

| URI | Description |
|---|---|
| `photoshop://info` | App version + active document status |
| `photoshop://document/info` | Document name, dimensions, resolution, layer count |
| `photoshop://document/layers` | Full hierarchical layer tree with properties |

## Architecture

```
PhotoshopMcpServer/
├── Program.cs                         # Entry point, DI + MCP setup
├── Services/
│   └── PhotoshopBridge.cs             # STA-thread COM isolation + timeout
├── Tools/
│   ├── DocumentTools.cs               # Document CRUD
│   ├── LayerTools.cs                  # Layer CRUD + export
│   └── SessionTools.cs                # Session / selection info
├── Resources/
│   └── DocumentResources.cs           # 3 read-only resource endpoints
└── Infrastructure/
    └── JsHelpers.cs                   # ExtendScript JSON polyfill, string escaping
```

## Extending

Adding a new tool requires no registration code — just create a class:

```csharp
[McpServerToolType]
public sealed class MyTool(PhotoshopBridge ps, ILogger<MyTool> logger)
{
    [McpServerTool(Name = "photoshop_my_tool")]
    [Description("Description shown to AI clients.")]
    public async Task<object> Execute(
        [Description("Parameter description")] string param1,
        int param2 = 42)
    {
        var script = $@"... Photoshop ExtendScript ...";
        var result = await ps.ExecuteJavaScriptAsync(script);
        return new { success = true, data = result };
    }
}
```

`WithToolsFromAssembly()` in `Program.cs` auto-discovers all `[McpServerToolType]` classes.

## License

MIT — same as the [original project](https://github.com/loonghao/photoshop-python-api-mcp-server).

## Credits

- **Original Python version:** [loonghao/photoshop-python-api-mcp-server](https://github.com/loonghao/photoshop-python-api-mcp-server) by [Hal Long](https://github.com/loonghao)
- **C# rewrite:** [Misaka16608](https://github.com/Misaka16608)
- **MCP C# SDK:** [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk) (Microsoft)
