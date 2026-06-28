# Photoshop MCP Server

A Model Context Protocol (MCP) server that gives AI assistants — Claude Code, Claude Desktop, and other MCP clients — the ability to control Adobe Photoshop programmatically through natural language. | [简体中文](README_zh.md)

> **This is a C# port** of [loonghao/photoshop-python-api-mcp-server](https://github.com/loonghao/photoshop-python-api-mcp-server). The Python version can suffer from COM blocking under the GIL; this port uses .NET native COM interop with STA-thread isolation and per-call timeout to avoid that.

## What problems does this solve?

Without this server, every design round-trip requires a human to manually operate Photoshop:

```
AI 描述设计  →  人打开 PS  →  人绘制  →  人导出  →  AI 检查结果
```

With it, AI executes directly:

```
AI 描述设计  →  AI 调用工具  →  Photoshop 渲染  →  AI 检查结果  →  完成
```

The human becomes the reviewer, not the bottleneck.

### What can you actually do?

| Capability | Example |
|---|---|
| **Document management** | Create new PSD documents |
| **Layer creation** | Text layers, solid color fills |
| **Layer inspection** | Read full layer tree with bounds, text properties, blend modes, locked states |
| **Layer modification** | Rename, reposition, change text, toggle visibility, adjust opacity, switch blend mode |
| **Layer deletion** | Remove layers by name or index |
| **Layer export** | Export any layer as PNG to disk, with scale and trim-to-content |
| **Context queries** | Get session info, active document metadata, selection bounds |
| **Token-efficient queries** | Ask for exactly the fields you need (e.g. `fields="name,id,index,kind,visible,opacity,bounds,text,parentId"`) |

### Verified workflow

- **PS → Unity UI reconstruction** — read PSD layer hierarchy, extract text/font/color/bounds per layer, feed into engine-side layout generation

## How it works

```
AI Assistant (Claude Code, etc.)
        │
        ▼
  MCP stdio transport  ← JSON-RPC over stdin/stdout
        │
        ▼
  Program.cs  →  DI container  →  auto-discovers Tools & Resources
        │
        ▼
  PhotoshopBridge.cs  ←  Dedicated STA thread, all COM calls
        │                    have configurable timeout (PS_MCP_TIMEOUT)
        ▼
  Photoshop COM  →  ExecuteJavaScript  →  Photoshop DOM / Action Manager
```

**Key architectural decisions:**

- **STA thread isolation** — All COM calls run on a single dedicated background thread with `STA` apartment state. The MCP protocol loop on the main thread is never blocked, so timeouts and cancellations work reliably.
- **Per-call timeout** — Every COM operation has a timeout (default 15s, configurable via `PS_MCP_TIMEOUT`). After a timeout, the bridge is marked unhealthy and all further calls fail fast — no more "stuck spinner" scenarios.
- **stdout hygiene** — All diagnostic output goes to stderr. stdout carries only MCP JSON-RPC messages. No more corrupted protocol frames from stray `print()` calls.
- **Zero-registration extensibility** — `[McpServerToolType]` attribute on a class + `[McpServerTool]` on methods is all it takes. `WithToolsFromAssembly()` discovers everything. No wiring code.

## Requirements

- **Windows only** — Photoshop automation uses COM interop (no macOS/Linux support)
- **.NET 9 SDK** or later (build-time only)
- **Adobe Photoshop** CC 2017–2024 (tested)

## Quick start

### 1. Build

```bash
cd PhotoshopMcpServer
dotnet publish src/PhotoshopMcpServer -c Release -r win-x64 --self-contained -o publish
```

### 2. Configure your MCP client

Project-level `.mcp.json` (recommended):

```json
{
  "mcpServers": {
    "photoshop": {
      "command": "./PhotoshopMcpServer/publish/photoshop-mcp-server.exe",
      "env": {
        "PS_MCP_TIMEOUT": "30"
      }
    }
  }
}
```

Or in Claude Code / Claude Desktop settings (`settings.json`):

```json
{
  "mcpServers": {
    "photoshop": {
      "command": "./PhotoshopMcpServer/publish/photoshop-mcp-server.exe",
      "env": {
        "PS_MCP_TIMEOUT": "30"
      }
    }
  }
}
```

### 3. Restart

Restart your MCP client session. The server connects to Photoshop lazily — on first tool call, not at startup. Photoshop must be running first.

## Tool reference

### Document tools

| Tool | Description | Key parameters |
|---|---|---|
| `photoshop_create_document` | Create a new document | `width`, `height`, `name`, `mode` (rgb/cmyk/grayscale/bitmap/lab) |

### Layer tools

| Tool | Description | Key parameters |
|---|---|---|
| `photoshop_create_text_layer` | Create a text layer | `text`, `x`, `y`, `size`, `color_r`, `color_g`, `color_b` |
| `photoshop_create_solid_color_layer` | Create a solid color fill layer | `color_r`, `color_g`, `color_b`, `name` |
| `photoshop_get_layer_info` | Get one layer's details | `layer_name` or `layer_index` → returns bounds, opacity, blend mode, text properties |
| `photoshop_get_layers` | Get all layers (field-filterable) | `fields` — comma-separated, e.g. `"name,id,index,kind,visible,opacity,bounds,text"` |
| `photoshop_delete_layer` | Delete a layer | `layer_name` or `layer_index` |
| `photoshop_modify_layer` | Modify layer properties | `new_name`, `text`, `x`, `y`, `visible`, `opacity`, `blend_mode` |
| `photoshop_export_layer` | Export layer as PNG | `output_path`, `layer_name`/`layer_index`, `scale`, `trim` |

### Session tools

| Tool | Description |
|---|---|
| `photoshop_get_session_info` | Photoshop version, document list, ruler/type unit preferences |
| `photoshop_get_active_document_info` | Document metadata via Action Manager (name, size, resolution, mode, bit depth, layer count, file path) |
| `photoshop_get_selection_info` | Current selection bounds (left/top/right/bottom), width, height, area |

### Resources (read-only endpoints)

| URI | Returns |
|---|---|
| `photoshop://info` | App version, build, active document name |
| `photoshop://document/info` | Document name, dimensions, resolution, color mode, bit depth, layer count, file path |
| `photoshop://document/layers` | Full hierarchical layer tree with `id`/`parentId` relationships, text/font/color properties |

> **Field filtering tip:** Use `photoshop_get_layers` with a `fields` parameter for token-efficient queries. For PS → Unity workflows, nine fields cover layer matching:
> ```
> photoshop_get_layers fields="name,id,index,kind,visible,opacity,bounds,text,parentId"
> ```

## Adding new tools

No registration boilerplate. Create one class:

```csharp
[McpServerToolType]
public sealed class MyTool(PhotoshopBridge ps, ILogger<MyTool> logger)
{
    [McpServerTool(Name = "photoshop_my_tool")]
    [Description("What it does — this description is shown to the AI client.")]
    public async Task<object> Execute(
        [Description("Parameter docs for the AI")] string param1,
        int param2 = 42)
    {
        var script = $@"... Photoshop ExtendScript ...";
        var result = await ps.ExecuteJavaScriptAsync(script);
        return new { success = true, data = result };
    }
}
```

`WithToolsFromAssembly()` in `Program.cs` discovers it automatically. Same pattern for resources with `[McpServerResourceType]` + `[McpServerResource]`.

## Architecture

```
PhotoshopMcpServer/
├── Program.cs                         # Entry point, DI + MCP host setup
├── Services/
│   └── PhotoshopBridge.cs             # STA-thread COM isolation, timeout, retry logic
├── Tools/
│   ├── DocumentTools.cs               # create/open/save document
│   ├── LayerTools.cs                  # Layer CRUD + export to PNG
│   └── SessionTools.cs                # Session info, document metadata, selection bounds
├── Resources/
│   └── DocumentResources.cs           # 3 read-only resource endpoints
└── Infrastructure/
    └── JsHelpers.cs                   # ExtendScript JSON polyfill, string escaping, field filtering
```

## Troubleshooting

When the server doesn't appear after configuration:

1. **Check the `.exe` exists** — `ls -la` the publish path
2. **Check MCP handshake** — the `initialize` message must succeed; logs go to stderr
3. **Check `.claude/settings.local.json`** — the `enabledMcpjsonServers` field acts as a **global allowlist** for MCP servers (despite the "Mcpjson" name). If present, `"photoshop"` must be in the array.
4. **Restart your session** — MCP servers are loaded at session start, not on config save
5. **Increase timeout** — complex operations (like layer export) may need `PS_MCP_TIMEOUT=60`

## License

MIT — same as the [original project](https://github.com/loonghao/photoshop-python-api-mcp-server).

## Credits

- **Python original:** [loonghao/photoshop-python-api-mcp-server](https://github.com/loonghao/photoshop-python-api-mcp-server) by [Hal Long](https://github.com/loonghao)
- **C# port:** [Misaka16608](https://github.com/Misaka16608)
- **MCP C# SDK:** [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk) by Microsoft
