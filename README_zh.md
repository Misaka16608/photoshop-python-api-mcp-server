# Photoshop MCP Server（C# 版）

让 AI 助手（Claude Code、Claude Desktop 等）通过 stdio 协议操控 Adobe Photoshop 的 MCP 服务器。

> **这是 [loonghao/photoshop-python-api-mcp-server](https://github.com/loonghao/photoshop-python-api-mcp-server) 的完整 C# 重写版。**
> 原 Python 版存在 COM 阻塞问题（GIL + 同步 COM 调用导致整个会话卡死）。
> 此重写版使用 .NET 原生 COM 互操作，配合专用 STA 线程和每次调用的超时保护。

## 为什么要重写？

| Python 版的问题 | C# 版的解决 |
|---|---|
| GIL + 阻塞 COM → 整个进程卡死 | 专用 STA 线程，MCP 协议层不受影响 |
| 仅 `execute_javascript` 有超时 | **所有** COM 调用都有可配置超时（`PS_MCP_TIMEOUT`，默认 15 秒） |
| `print()` 泄漏到 stdout → 破坏 JSON-RPC | 所有日志通过 `Microsoft.Extensions.Logging` 输出到 stderr |
| 手动 `register_tool(mcp, func, name)` 注册 | `[McpServerToolType]` 特性 → `WithToolsFromAssembly()` 自动发现 |

## 环境要求

- 仅 Windows（通过 COM 操控 Photoshop）
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)（构建用）
- Adobe Photoshop（已测试 CC2017–2024）

## 快速开始

### 1. 构建

```bash
cd PhotoshopMcpServer
dotnet publish src/PhotoshopMcpServer -c Release -r win-x64 --self-contained -o publish
```

### 2. 配置 MCP 客户端

在项目的 `.mcp.json`（或 Claude Code / Claude Desktop 设置）中添加：

```json
{
  "mcpServers": {
    "photoshop": {
      "command": "<绝对路径>\\PhotoshopMcpServer\\publish\\photoshop-mcp-server.exe",
      "env": {
        "PS_MCP_TIMEOUT": "30"
      }
    }
  }
}
```

### 3. 重启

重启 MCP 客户端会话以加载新的服务器。服务器在首次调用工具时才会连接 Photoshop（懒初始化）。

## 可用工具

### 文档

| 工具 | 说明 |
|---|---|
| `photoshop_create_document` | 新建文档（宽度、高度、名称、颜色模式） |
| `photoshop_open_document` | 打开已有文件 |
| `photoshop_save_document` | 保存为 PSD / JPEG / PNG |

### 图层

| 工具 | 说明 |
|---|---|
| `photoshop_create_text_layer` | 创建文字图层（位置、字号、颜色） |
| `photoshop_create_solid_color_layer` | 创建纯色填充图层 |
| `photoshop_get_layer_info` | 按名称或索引获取图层详情（边界、透明度、文字属性、混合模式等） |
| `photoshop_delete_layer` | 按名称或索引删除图层 |
| `photoshop_modify_layer` | 修改图层：重命名、移动、改文字、显隐、透明度、混合模式 |
| `photoshop_get_layer_thumbnail` | 导出图层为 base64 编码的 PNG 缩略图 |
| `photoshop_export_layer` | 导出图层为磁盘上的 PNG 文件（支持缩放和裁切） |

### 会话

| 工具 | 说明 |
|---|---|
| `photoshop_get_session_info` | Photoshop 版本、文档列表、偏好设置 |
| `photoshop_get_active_document_info` | 通过 Action Manager 获取当前文档元数据 |
| `photoshop_get_selection_info` | 当前选区的边界与面积 |

### 资源

| URI | 说明 |
|---|---|
| `photoshop://info` | 应用版本 + 是否有活动文档 |
| `photoshop://document/info` | 文档名称、尺寸、分辨率、图层数量 |
| `photoshop://document/layers` | 完整图层树（含所有属性） |

## 架构

```
PhotoshopMcpServer/
├── Program.cs                         # 入口，依赖注入 + MCP 配置
├── Services/
│   └── PhotoshopBridge.cs             # STA 线程 COM 隔离 + 超时控制
├── Tools/
│   ├── DocumentTools.cs               # 文档增删改查
│   ├── LayerTools.cs                  # 图层增删改查 + 导出
│   └── SessionTools.cs                # 会话 / 选区信息
├── Resources/
│   └── DocumentResources.cs           # 3 个只读资源端点
└── Infrastructure/
    └── JsHelpers.cs                   # ExtendScript JSON polyfill、字符串转义
```

## 扩展

添加新工具无需任何注册代码——只需新建一个类：

```csharp
[McpServerToolType]
public sealed class MyTool(PhotoshopBridge ps, ILogger<MyTool> logger)
{
    [McpServerTool(Name = "photoshop_my_tool")]
    [Description("展示给 AI 客户端的描述。")]
    public async Task<object> Execute(
        [Description("参数说明")] string param1,
        int param2 = 42)
    {
        var script = $@"... Photoshop ExtendScript ...";
        var result = await ps.ExecuteJavaScriptAsync(script);
        return new { success = true, data = result };
    }
}
```

`Program.cs` 中的 `WithToolsFromAssembly()` 会自动发现所有标注了 `[McpServerToolType]` 的类。

## 许可证

MIT — 与[原项目](https://github.com/loonghao/photoshop-python-api-mcp-server)相同。

## 致谢

- **Python 原版：** [loonghao/photoshop-python-api-mcp-server](https://github.com/loonghao/photoshop-python-api-mcp-server)，作者 [Hal Long](https://github.com/loonghao)
- **C# 重写：** [Misaka16608](https://github.com/Misaka16608)
- **MCP C# SDK：** [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)（Microsoft）
