# Photoshop MCP Server

一个 MCP（模型上下文协议）服务器，让 AI 助手（Claude Code、Claude Desktop 等 MCP 客户端）能够通过自然语言直接操控 Adobe Photoshop。

> **这是 [loonghao/photoshop-python-api-mcp-server](https://github.com/loonghao/photoshop-python-api-mcp-server) 的 C# 移植版**。Python 版在 GIL 下可能出现 COM 阻塞；此移植版使用 .NET 原生 COM 互操作，配合 STA 线程隔离和每次调用的超时保护来避免该问题。

## 这解决了什么问题？

没有这个服务器时，每次设计迭代都需要人在 Photoshop 之间反复操作：

```
AI 描述设计  →  人打开 PS  →  人绘制  →  人导出  →  AI 检查结果
```

有了它，AI 直接执行：

```
AI 描述设计  →  AI 调用工具  →  Photoshop 渲染  →  AI 检查结果  →  完成
```

人变成审核者，不再是瓶颈。

### 实际能做什么？

| 能力 | 举例 |
|---|---|
| **文档管理** | 新建、打开、保存 PSD / PNG / JPEG |
| **图层创建** | 创建文字图层（字体/字号/颜色）、纯色填充图层 |
| **图层查看** | 读取完整图层树，含边界框、文字属性、混合模式、锁定状态 |
| **图层修改** | 重命名、移动、改文字、显隐、透明度、混合模式 |
| **图层删除** | 按名称或索引删除图层 |
| **图层导出** | 将任意图层导出为磁盘上的 PNG 文件，支持缩放和裁切透明边 |
| **上下文查询** | 获取 Photoshop 版本、打开的文档列表、当前选区边界 |
| **Token 高效查询** | 指定需要的字段即可，如 `fields="name,id,index,kind,visible,opacity,bounds,text,parentId"` |

### 已验证的工作流

- **PS → Unity UI 还原** — 读取 PSD 图层层级，提取每层的文字/字体/颜色/边界，输入引擎侧自动生成布局

## 工作原理

```
AI 助手（Claude Code 等）
        │
        ▼
  MCP stdio 传输层  ← stdin/stdout 上跑 JSON-RPC
        │
        ▼
  Program.cs  →  DI 容器  →  自动发现 Tools 和 Resources
        │
        ▼
  PhotoshopBridge.cs  ←  专用 STA 线程，所有 COM 调用
        │                    都有可配置的超时（PS_MCP_TIMEOUT）
        ▼
  Photoshop COM  →  ExecuteJavaScript  →  Photoshop DOM / Action Manager
```

**关键架构决策：**

- **STA 线程隔离** — 所有 COM 调用都跑在一条专用的后台 STA 线程上。主线程上的 MCP 协议循环永远不会被阻塞，超时和取消都能正常工作。
- **每次调用都有超时保护** — 每个 COM 操作都有超时（默认 15 秒，可通过 `PS_MCP_TIMEOUT` 配置）。超时后，bridge 被标记为 unhealthy，后续所有调用都会快速失败——再也不会出现"转圈卡死"的场景。
- **stdout 干净** — 所有诊断输出都走 stderr，stdout 只承载 MCP JSON-RPC 消息。不会有 stray `print()` 破坏协议帧。
- **零注册扩展** — 类上标 `[McpServerToolType]`，方法上标 `[McpServerTool]`，完事。`WithToolsFromAssembly()` 自动发现一切，无需任何注册代码。

## 环境要求

- **仅 Windows** — Photoshop 自动化依赖 COM 互操作（不支持 macOS/Linux）
- **.NET 9 SDK** 或更高（仅构建时需要）
- **Adobe Photoshop** CC 2017–2024（已测试）

## 快速开始

### 1. 构建

```bash
cd PhotoshopMcpServer
dotnet publish src/PhotoshopMcpServer -c Release -r win-x64 --self-contained -o publish
```

### 2. 配置 MCP 客户端

项目级 `.mcp.json`（推荐）：

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

或 Claude Code / Claude Desktop 设置文件（`settings.json`）：

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

### 3. 重启

重启 MCP 客户端会话。服务器采用懒初始化——首次调用工具时才会连接 Photoshop，不会在启动时连接。注意 Photoshop 需要先于服务器启动。

## 工具参考

### 文档工具

| 工具 | 说明 | 主要参数 |
|---|---|---|
| `photoshop_create_document` | 新建文档 | `width`, `height`, `name`, `mode`（rgb/cmyk/grayscale/bitmap/lab） |
| `photoshop_open_document` | 打开已有文件 | `file_path` |
| `photoshop_save_document` | 保存当前文档 | `file_path`, `format`（psd/jpg/png） |

### 图层工具

| 工具 | 说明 | 主要参数 |
|---|---|---|
| `photoshop_create_text_layer` | 创建文字图层 | `text`, `x`, `y`, `size`, `color_r`, `color_g`, `color_b` |
| `photoshop_create_solid_color_layer` | 创建纯色填充图层 | `color_r`, `color_g`, `color_b`, `name` |
| `photoshop_get_layer_info` | 获取单个图层详情 | `layer_name` 或 `layer_index` → 返回边界、透明度、混合模式、文字属性 |
| `photoshop_get_layers` | 获取所有图层（支持字段过滤） | `fields` — 逗号分隔，如 `"name,id,index,kind,visible,opacity,bounds,text"` |
| `photoshop_delete_layer` | 删除图层 | `layer_name` 或 `layer_index` |
| `photoshop_modify_layer` | 修改图层属性 | `new_name`, `text`, `x`, `y`, `visible`, `opacity`, `blend_mode` |
| `photoshop_export_layer` | 导出图层为 PNG | `output_path`, `layer_name`/`layer_index`, `scale`, `trim` |

### 会话工具

| 工具 | 说明 |
|---|---|
| `photoshop_get_session_info` | Photoshop 版本、文档列表、标尺/字体单位偏好设置 |
| `photoshop_get_active_document_info` | 通过 Action Manager 获取文档元数据（名称、尺寸、分辨率、色彩模式、位深、图层数、文件路径） |
| `photoshop_get_selection_info` | 当前选区边界（left/top/right/bottom）、宽度、高度、面积 |

### 资源（只读端点）

| URI | 返回内容 |
|---|---|
| `photoshop://info` | 应用版本、Build 号、当前活动文档名称 |
| `photoshop://document/info` | 文档名、尺寸、分辨率、色彩模式、位深、图层数、文件路径 |
| `photoshop://document/layers` | 完整层级化图层树，含 `id`/`parentId` 父子关系，text/font/color 文字属性 |

> **字段过滤技巧：** 建议用 `photoshop_get_layers` 的 `fields` 参数做按需查询，避免浪费 token。在 PS → Unity 还原工作流中，9 个字段即可覆盖图层匹配：
> ```
> photoshop_get_layers fields="name,id,index,kind,visible,opacity,bounds,text,parentId"
> ```

## 添加新工具

无需任何注册样板代码。只需新建一个类：

```csharp
[McpServerToolType]
public sealed class MyTool(PhotoshopBridge ps, ILogger<MyTool> logger)
{
    [McpServerTool(Name = "photoshop_my_tool")]
    [Description("这个描述会直接展示给 AI 客户端，告诉它这个工具是干什么的。")]
    public async Task<object> Execute(
        [Description("参数说明，AI 会读到")] string param1,
        int param2 = 42)
    {
        var script = $@"... Photoshop ExtendScript ...";
        var result = await ps.ExecuteJavaScriptAsync(script);
        return new { success = true, data = result };
    }
}
```

`Program.cs` 中的 `WithToolsFromAssembly()` 会自动发现它。资源同理，使用 `[McpServerResourceType]` + `[McpServerResource]`。

## 架构

```
PhotoshopMcpServer/
├── Program.cs                         # 入口，依赖注入 + MCP 主机配置
├── Services/
│   └── PhotoshopBridge.cs             # STA 线程 COM 隔离、超时控制、重试逻辑
├── Tools/
│   ├── DocumentTools.cs               # 文档增删改查
│   ├── LayerTools.cs                  # 图层增删改查 + PNG 导出
│   └── SessionTools.cs                # 会话信息、文档元数据、选区边界
├── Resources/
│   └── DocumentResources.cs           # 3 个只读资源端点
└── Infrastructure/
    └── JsHelpers.cs                   # ExtendScript JSON polyfill、字符串转义、字段过滤
```

## 常见问题排查

服务器配置后不出现时的排查步骤：

1. **确认 .exe 存在** — `ls -la` 检查发布目录
2. **检查 MCP 握手** — `initialize` 消息是否成功；日志走 stderr，可在客户端日志中查看
3. **检查 `.claude/settings.local.json`** — `enabledMcpjsonServers` 字段虽然名字带 "Mcpjson"，实际是所有 MCP server 的**全局白名单**。如果存在该字段，`"photoshop"` 必须在数组中
4. **重启会话** — MCP server 在会话启动时加载，修改配置后需要重启，不是保存即生效
5. **调高超时** — 复杂操作（如图层导出）可能需要 `PS_MCP_TIMEOUT=60`

## 许可证

MIT — 与[原项目](https://github.com/loonghao/photoshop-python-api-mcp-server)相同。

## 致谢

- **Python 原版：** [loonghao/photoshop-python-api-mcp-server](https://github.com/loonghao/photoshop-python-api-mcp-server)，作者 [Hal Long](https://github.com/loonghao)
- **C# 移植：** [Misaka16608](https://github.com/Misaka16608)
- **MCP C# SDK：** [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)（Microsoft）
