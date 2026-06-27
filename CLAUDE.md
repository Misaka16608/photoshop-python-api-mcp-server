# CLAUDE.md

## Project Overview

Fork of `loonghao/photoshop-python-api-mcp-server` — MCP server that lets AI assistants control Adobe Photoshop via stdio protocol.

- **Upstream:** https://github.com/loonghao/photoshop-python-api-mcp-server
- **Fork:** https://github.com/Misaka16608/photoshop-python-api-mcp-server
- **Current version:** v0.1.11
- **Transport:** stdio (launched by MCP client, no manual terminal run)
- **Platform:** Windows only (COM-based)
- **Photoshop:** CC2017–2024 tested, env var `PS_VERSION` sets target version
- **Python:** 3.10+, managed via poetry

## Usage Context

This fork is maintained to add missing layer manipulation tools needed by the **PS → Unity UI 还原工作流** (see `[[ps-to-unity-workflow]]` in the WeatherPet project memory). The upstream server only creates layers — it cannot read layer details, modify, or delete them.

## Architecture

```
photoshop_mcp_server/
├── server.py              ← FastMCP entry point, calls register_all_tools/resources
├── registry.py            ← Discovers modules, invokes their register(mcp)
├── decorators.py          ← debug_tool + log_tool_call wrappers
├── resources/
│   └── document_resources.py   ← 3 resources (info, document/info, document/layers)
├── tools/
│   ├── document_tools.py  ← create/open/save document
│   ├── layer_tools.py     ← create_text_layer, create_solid_color_layer
│   └── session_tools.py   ← get_session_info, get_active_document_info, get_selection_info
└── ps_adapter/
    ├── application.py     ← PhotoshopApp singleton (Session + photoshop.api)
    └── action_manager.py  ← Low-level Action Manager API (used by session_tools)
```

## Registration Pattern

```python
# Add a new module under tools/ or resources/ with a register(mcp) function:
def register(mcp):
    def my_tool(param: str) -> dict:
        ps_app = PhotoshopApp()
        doc = ps_app.get_active_document()
        ...
        return {"success": True, "data": ...}

    register_tool(mcp, my_tool, "my_tool")  # auto-prefixes with photoshop_
```

## Key APIs

- `PhotoshopApp()` — singleton, `.get_active_document()`, `.execute_javascript(script)`
- `doc.artLayers` — iterable of layers, each has `.name`, `.visible`, `.kind`, `.bounds`
- `ActionManager` — low-level COM Action Manager for properties not exposed via `photoshop.api`
- `register_tool(mcp, func, name)` / `mcp.resource(uri)(func)` — from `registry.py`

## Build & Run

| Task | How |
|------|-----|
| Install deps | `poetry install` |
| Run server | `poetry run photoshop-mcp-server` (or the `.exe` entry point) |
| Run tests | `poetry run pytest` |
| Lint | `poetry run flake8` |

## MCP Server Troubleshooting

When the photoshop MCP server doesn't appear after configuration:

1. **exe exists** — `ls -la <path>` 确认文件存在
2. **MCP 握手** — 检查 `initialize` 握手是否成功
3. **用户级 `settings.json`** — `mcpServers.photoshop` 配置正确
4. **项目级 `.mcp.json`** — 检查是否覆盖了配置
5. **项目级 `.claude/settings.local.json`** — **最常见根因**：`enabledMcpjsonServers` 是项目级 MCP server 白名单，会过滤所有来源的 server。虽然字段名带 "Mcpjson"，实际是全局过滤。解决：把 `"photoshop"` 加入数组。

- 可编辑安装（`pip install -e .`）的 `.exe` 不会自动反映源码改动，需 `pip install -e . --force-reinstall --no-deps` 重建
- 修改配置/代码后需**重启会话**才能加载新的 MCP server

## Code Conventions

- Python 3.10+ with type hints
- Functions return `dict` with at least `"success": True/False`
- Error returns include `"error"` and `"detailed_error"` fields
- Tab-based indentation (existing codebase style)
- Decorators `debug_tool` and `log_tool_call` are auto-applied by `register_tool()`

---

## C# Edition (recommended)

The Python version suffers from COM blocking issues (GIL + synchronous COM = sessions hang). A full C# rewrite lives in `PhotoshopMcpServer/`:

```
PhotoshopMcpServer/
├── PhotoshopMcpServer.sln
├── src/PhotoshopMcpServer/
│   ├── Program.cs                      # Entry, DI + MCP setup
│   ├── Services/PhotoshopBridge.cs     # STA-thread COM isolation
│   ├── Tools/
│   │   ├── DocumentTools.cs            # create/open/save_document
│   │   ├── LayerTools.cs               # Layer CRUD + thumbnail + export
│   │   └── SessionTools.cs             # Session/doc/selection info
│   ├── Resources/DocumentResources.cs  # 3 resource endpoints
│   └── Infrastructure/JsHelpers.cs     # JSON polyfill, JS escaping
└── publish/                            # Published self-contained exe
```

### Key improvements over Python

| Issue | Python | C# |
|-------|--------|----|
| COM blocking | GIL + sync COM → session hang | Dedicated STA thread, per-call timeout |
| stdout pollution | `print()` goes to stdout | All logs → stderr |
| Timeout coverage | Only `execute_javascript` | **Every** COM call has timeout via `QueueWorkAsync<T>` |
| Extensibility | `register_tool(mcp, func, name)` | `[McpServerToolType]` class + `[McpServerTool]` methods → auto-discovered |

### Build & Deploy

```bash
cd PhotoshopMcpServer
dotnet build                          # dev build
dotnet publish -c Release -r win-x64 --self-contained -o publish  # deploy
```

### .mcp.json config

```json
{
  "mcpServers": {
    "photoshop": {
      "command": "D:\\_Code\\photoshop-python-api-mcp-server\\PhotoshopMcpServer\\publish\\photoshop-mcp-server.exe",
      "env": { "PS_MCP_TIMEOUT": "30" }
    }
  }
}
```

### Adding new tools (extensibility)

```csharp
[McpServerToolType]
public sealed class MyNewTool(PhotoshopBridge ps, ILogger<MyNewTool> logger)
{
    [McpServerTool(Name = "photoshop_my_tool")]
    [Description("Does something.")]
    public async Task<object> MyTool(string param1, int param2 = 0)
    {
        var script = $@"...";
        var raw = await ps.ExecuteJavaScriptAsync(script);
        return new { success = true, data = raw };
    }
}
// Done! WithToolsFromAssembly() auto-discovers — zero registration code.
```
