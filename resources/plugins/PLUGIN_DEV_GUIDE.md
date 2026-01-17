# LiteMonitor 插件开发指南

本指南详细介绍了如何为 LiteMonitor 开发插件。系统采用基于 JSON 的配置模型，支持 API 调用、链式执行、数据解析以及动态 UI 更新。

## 1. 文件位置
请将插件文件放置在 `resources/plugins/` 目录下，并使用 `.json` 扩展名（例如 `resources/plugins/my_plugin.json`）。

## 2. 基本结构 (JSON)
```json
{
  "id": "unique_plugin_id",
  "meta": {
    "name": "插件名称",
    "version": "1.0.0",
    "author": "作者",
    "description": "插件功能描述"
  },
  "inputs": [],
  "execution": {},
  "parsing": {}
}
```

## 3. 配置详解

### 3.1 Inputs (用户输入)
定义用户需要配置的字段（例如：城市名、API Key）。
```json
"inputs": [
  {
    "key": "city",
    "label": "城市名称",
    "type": "text", 
    "default": "上海",
    "placeholder": "请输入城市名称",
    "scope": "target" 
  }
]
```
*   `scope`: `"global"` (所有目标共享，如 API Key) 或 `"target"` (每个目标单独配置，如 股票代码)。

### 3.2 Execution (执行逻辑)
支持 `api_json` (传统的单次调用) 或 `chain` (多步工作流)。

#### Chain 模式 (推荐)
顺序执行多个步骤。上下文变量（inputs + 步骤输出）在所有步骤间共享。
```json
"execution": {
  "type": "chain",
  "interval": 300000, // 刷新间隔 (毫秒)
  "steps": [
    {
      "id": "step1",
      "url": "https://api.example.com/get-id?name={{city}}",
      "extract": { "user_id": "data.id" } // 将 'data.id' 提取为 {{user_id}}
    },
    {
      "id": "step2",
      "url": "https://api.example.com/get-info?id={{user_id}}",
      "extract": { "temp": "current.temp" }
    }
  ]
}
```

#### 步骤配置 (Step Configuration)
| 字段 | 描述 |
| :--- | :--- |
| `url` | 目标 URL。支持 `{{var}}` 变量替换。 |
| `method` | `GET` (默认) 或 `POST`。 |
| `headers` | 自定义请求头 (如 `Referer`, `Authorization`)。 |
| `response_format` | `json` (默认), `jsonp` (自动去除回调包裹), `text`。 |
| `response_encoding` | `utf-8` (默认) 或 `gbk` (中文旧接口常用)。 |
| `cache_minutes` | 缓存时间（分钟）。`0`=不缓存, `-1`=永久缓存（直到重启）。适合 IP/ID 查询等低频接口。 |
| `extract` | 变量提取映射：`变量名` -> `JSON路径`。 |
| `process` | 数据清洗规则 (见下文)。 |

### 3.3 Extract (数据提取)
使用点号分隔符提取数据：
*   `data.value`: 提取对象属性。
*   `list[0].name`: 提取数组元素中的属性。
*   `[0].ref`: 提取根数组的第一个元素。

### 3.4 Process (数据处理)
使用正则或映射表修改变量。
```json
"process": [
  {
    "var": "clean_city",
    "source": "raw_city",
    "function": "regex_replace",
    "pattern": "(市|区|县)$",
    "to": ""
  },
  {
    "var": "status_icon",
    "source": "status_code",
    "function": "map",
    "map": { "200": "✅", "404": "❌" }
  }
]
```

### 3.5 Outputs (UI 显示)
定义数据如何在监控面板中显示。
```json
"parsing": {
  "outputs": [
    {
      "key": "temp",
      "label": "{{city}} 气温",
      "short_label": "气温",
      "format": "{{temp}}°C",
      "unit": ""
    }
  ]
}
```

## 4. 动态 UI 与性能最佳实践

### 动态标签 (Dynamic Labels)
你可以在 `label` 和 `short_label` 中使用变量（如 `{{city}}`）。当这些值发生变化时，系统会自动更新 UI。

**⚠️ 性能警告：**
*   **推荐做法：** 仅使用**静态元数据**（如 `{{city}}`, `{{ip}}`, `{{device_name}}`）。这些数据很少变化（通常只在启动或网络切换时变）。
*   **禁止做法：** 在 **Label** 中使用**高频变化的数据**（如 `{{cpu_usage}}` 或 `{{temperature}}`）。
    *   **原因：** Label 的变化会触发 `Settings.Save()` (磁盘写入) 和 `UI.Rebuild()` (界面重绘)。如果每秒都触发，会导致严重的界面卡顿和磁盘损耗。
    *   **正确做法：** 将动态数值放在 `format` 字段中，不要放在 `label` 中。`format` 字段的更新走的是高效渲染通道，不会触发重绘。

## 5. 故障排查
*   **"Err"**: 发生异常 (请查看 Debug 输出)。
*   **"?"**: JSON 解析失败 (检查 JSON Path 是否正确)。
*   **"[Empty]"**: 变量提取成功但内容为空。
*   **API Err**: API 返回了包含错误的响应对象。
