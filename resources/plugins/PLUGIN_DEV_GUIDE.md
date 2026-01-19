# LiteMonitor 插件开发完全指南 (v1.0)

欢迎使用 LiteMonitor 插件系统！
本指南专为初学者设计，旨在帮助你从零开始编写自己的监控插件。无需精通编程，只需了解基本的 JSON 格式即可上手。

---

## 📚 目录

1.  [什么是插件？](#1-什么是插件)
2.  [快速开始：你的第一个插件](#2-快速开始你的第一个插件)
3.  [核心概念：数据是如何流动的？](#3-核心概念数据是如何流动的)
4.  [详细配置手册](#4-详细配置手册)
    *   [4.1 Meta (基本信息)](#41-meta-基本信息)
    *   [4.2 Inputs (让用户配置)](#42-inputs-让用户配置)
    *   [4.3 Execution (获取数据)](#43-execution-获取数据)
    *   [4.4 Extract (提取数据)](#44-extract-提取数据)
    *   [4.5 Process (加工数据)](#45-process-加工数据)
    *   [4.6 Outputs (显示结果)](#46-outputs-显示结果)
5.  [实战案例库](#5-实战案例库)
6.  [常见问题 (FAQ)](#6-常见问题-faq)

---

## 1. 什么是插件？

LiteMonitor 的插件是一个 **`.json` 文本文件**，存放在 `resources/plugins/` 目录下。
它告诉程序：
1.  **去哪里拿数据？** (例如：访问天气 API)
2.  **怎么处理数据？** (例如：把温度从华氏度转为摄氏度)
3.  **怎么显示数据？** (例如：在任务栏显示 "上海 25°C")

---

## 2. 快速开始：你的第一个插件

让我们编写一个最简单的插件：**显示一段固定的文字**。

**步骤：**
1.  在 `resources/plugins/` 下新建文件 `Hello.json`。
2.  复制以下内容并保存：

```json
{
  "id": "hello_world",
  "meta": {
    "name": "你好世界",
    "version": "1.0",
    "author": "Me",
    "description": "我的第一个插件"
  },
  "execution": {
    "type": "chain",
    "interval": 60,  // 每 60 秒刷新一次
    "steps": []      // 这里为空，表示不需要联网
  },
  "outputs": [
    {
      "key": "msg",
      "label": "演示",
      "format_val": "Hello World!",
      "unit": ""
    }
  ]
}
```
3.  重启 LiteMonitor 或在设置页面点击“重载插件”。
4.  在插件设置页，你应该能看到“你好世界”插件，添加它，你的任务栏就会显示 "Hello World!"。

---

## 3. 核心概念：数据是如何流动的？

理解 **Context (上下文/变量池)** 是开发插件的关键。你可以把它想象成一个“购物篮”。

1.  **Inputs (进货)**: 用户在设置界面填写的配置（如城市名 `city`），首先被放入篮子。
    *   篮子：`{ "city": "Beijing" }`
2.  **Execution & Extract (采摘)**: 插件上网请求 API，提取出温度 `temp`，放入篮子。
    *   篮子：`{ "city": "Beijing", "temp": "25" }`
3.  **Process (加工)**: 插件发现温度是纯数字，想加个符号，于是生成新变量 `temp_display`。
    *   篮子：`{ "city": "Beijing", "temp": "25", "temp_display": "25°C" }`
4.  **Outputs (上架)**: 最后，插件从篮子里拿出 `temp_display` 显示在屏幕上。

---

## 4. 详细配置手册

### 4.1 Meta (基本信息)
描述插件是干什么的。

```json
"meta": {
  "name": "天气监控",      // 插件名 (显示在添加列表)
  "version": "1.0",       // 版本号
  "author": "你的名字",    // 作者
  "description": "从 WeatherAPI 获取实时天气" // 详细描述
}
```

### 4.2 Inputs (让用户配置)
定义设置界面，让用户输入参数。

```json
"inputs": [
  {
    "key": "city",            // 变量名 (在后面用 {{city}} 引用)
    "label": "城市名称",       // 设置界面显示的标题
    "type": "text",           // 输入框类型: text (文本), select (下拉框)
    "default": "Shanghai",    // 默认值
    "placeholder": "请输入拼音", // 提示语
    "scope": "target"         // 作用域 (见下文)
  }
]
```

**关于 Scope (作用域):**
*   `"target"` (**推荐**): 每个监控项独立配置。例如：你可以添加两个监控项，一个看“北京”天气，一个看“上海”天气。
*   `"global"`: 全局共享。例如：API Key，通常所有监控项都用同一个 Key，填一次就行了。

### 4.3 Execution (获取数据)
定义如何联网获取数据。目前使用标准的 `chain` (链式) 模式。

```json
"execution": {
  "type": "chain",
  "interval": 300, // 刷新间隔 (秒)。建议不要太频繁，以免被 API 封禁。
  "steps": [
    {
      "id": "step_weather",
      // URL 模板：使用 {{变量名}} 插入用户输入的内容
      "url": "https://api.example.com/weather?q={{city}}&apikey=123456",
      "method": "GET",          // GET 或 POST
      
      // 响应格式：
      // "json": 标准 JSON (默认)
      // "jsonp": JSONP 格式 (自动去除 callback( ... ))
      // "text": 纯文本 (配合 regex_replace 使用)
      "response_format": "json",
      
      "cache_minutes": 10, // 缓存时间 (分钟)。在10分钟内再次请求相同 URL，直接用缓存，不联网。
      "skip_if_set": "stop_flag", // 条件跳过：如果变量 stop_flag 有值，则不执行此步骤。
      
      "extract": { ... }        // 见下一节
    }
  ]
}
```

### 4.4 Extract (提取数据)
从 API 返回的 JSON 结果中抓取你想要的数据。

假设 API 返回：
```json
{
  "location": { "name": "Beijing" },
  "data": {
    "current": { "temp_c": 25.5 },
    "forecast": [ { "day": "Mon" }, { "day": "Tue" } ]
  },
  "rates": { "CNY": 7.2 }
}
```

**提取规则写法：**

| 目标变量 | JSON 路径写法 | 说明 |
| :--- | :--- | :--- |
| `"city_name"` | `"location.name"` | 提取对象属性 |
| `"temperature"`| `"data.current.temp_c"`| 多层嵌套 |
| `"tomorrow"` | `"data.forecast[1].day"`| 数组索引 (从0开始) |
| `"my_rate"` | `"rates.{{currency}}"` | **动态提取** (v1.1新功能): 如果变量 `currency` 是 "CNY"，则提取 `rates.CNY` |
| `"raw_html"` | `"$"` | **全文提取**: 仅当 response_format 为 text 时，提取整个响应内容 |

```json
"extract": {
  "temp": "data.current.temp_c",
  "city_name": "location.name"
}
```

### 4.5 Process (加工数据)
数据提取出来后，可能不满足显示需求（例如需要截取字符串、判断颜色），这时就需要 Process。

它是一个**有序列表**，按顺序执行。

#### 常用功能函数：

**1. `regex_replace` (正则替换)**
*   用途：去掉多余字符，或提取特定部分。
*   示例：把 "25.555" 变成 "25.5"
```json
{
  "var": "temp_fixed",       // 结果存到这个新变量
  "source": "temp",          // 源变量
  "function": "regex_replace",
  "pattern": "(\\d+\\.\\d).*", // 正则表达式
  "to": "$1"                 // 替换内容
}
```

**2. `threshold_switch` (阈值颜色开关)**
*   用途：根据数值大小，决定显示颜色（绿/黄/红）。
*   **颜色代码**：`"0"`=绿色(安全), `"1"`=黄色(警告), `"2"`=红色(危险)。
```json
{
  "var": "color_state",
  "source": "cpu_usage",
  "function": "threshold_switch",
  "value_map": {
    "0": "0",   // 大于等于 0 -> 绿色
    "80": "1",  // 大于等于 80 -> 黄色
    "90": "2"   // 大于等于 90 -> 红色
  }
}
```

**3. `map` (字典映射)**
*   用途：把代码转为人类可读的文字。
*   示例：把 "rainy" 转为 "🌧️"
```json
{
  "var": "weather_icon",
  "source": "condition",
  "function": "map",
  "map": {
    "sunny": "☀️",
    "cloudy": "☁️",
    "rainy": "🌧️"
  }
}
```

**4. `resolve_template` (组合字符串)**
*   用途：把多个变量拼在一起。
*   示例：把 "25" 和 "C" 拼成 "25°C"
```json
{
  "var": "full_text",
  "source": "temp", // source 只要不为空即可，实际内容看 template
  "function": "resolve_template",
  // 实际上你可以在 outputs 里直接拼，这里仅用于复杂逻辑
}
```

### 4.6 Outputs (显示结果)
最后一步，告诉 LiteMonitor 在任务栏显示什么。

```json
"outputs": [
  {
    "key": "main",
    "label": "{{city_name}}", // 标签：显示在数值左边 (如 "北京:")
    "short_label": "W",       // 短标签：空间不足时显示
    "format_val": "{{temp}}", // 数值部分
    "unit": "°C",             // 单位后缀
    "color": "{{color_state}}"// 绑定颜色变量
  }
]
```

**💡 小技巧：Fallback (兜底) 语法**
如果 API 还没返回数据，`{{city_name}}` 可能是空的。你可以这样写：
`{{city_name ?? city ?? "Loading..."}}`
意思是：优先显示 API 返回的城市名；如果没有，显示用户输入的城市名；如果还没输入，显示 "Loading..."。

---

## 5. 实战案例库

### 案例 1: 简单的 IP 查询 (标准 JSON)
这是一个不需要用户输入的简单插件。

```json
{
  "id": "public_ip",
  "meta": { "name": "公网IP", "version": "1.0", "author": "Demo" },
  "execution": {
    "type": "chain", "interval": 300,
    "steps": [
      {
        "id": "get_ip",
        "url": "https://api.ipify.org?format=json",
        "extract": { "ip": "ip" }
      }
    ]
  },
  "outputs": [
    { "key": "ip", "label": "IP", "format_val": "{{ip}}" }
  ]
}
```

### 案例 2: 自动定位天气 (链式请求 + 缓存 + 条件跳过)
这个案例展示了真正的**链式请求**能力。
逻辑：先尝试自动获取 IP 所在的城市；如果用户手动填了城市，则跳过自动定位步骤，直接查询天气。

```json
{
  "id": "auto_weather",
  "meta": { "name": "自动天气", "version": "1.0", "author": "Demo" },
  "inputs": [
    // 允许用户手动覆盖城市
    { "key": "manual_city", "label": "手动城市(可选)", "type": "text", "placeholder": "留空则自动定位" }
  ],
  "execution": {
    "type": "chain", "interval": 600,
    "steps": [
      {
        "id": "locate",
        "url": "http://ip-api.com/json",
        // 关键点 1: 条件跳过
        // 如果用户填了 manual_city，就不跑这一步了，省一次请求
        "skip_if_set": "manual_city",
        // 关键点 2: 缓存
        // IP 定位结果很久才变，缓存 60 分钟，避免频繁请求
        "cache_minutes": 60,
        "extract": { "city": "city" }
      },
      {
        "id": "weather",
        // 关键点 3: 变量回退
        // 优先用 manual_city，没有则用上一步获取的 city
        "url": "https://wttr.in/{{manual_city ?? city}}?format=j1",
        "extract": { 
          "temp": "current_condition[0].temp_C",
          "desc": "current_condition[0].lang_zh[0].value"
        }
      }
    ]
  },
  "outputs": [
    { 
      "key": "main", 
      "label": "{{manual_city ?? city}}", 
      "format_val": "{{temp}}°C {{desc}}" 
    }
  ]
}
```

### 案例 3: 汇率监控 (进阶 - 动态 Key)
展示如何根据用户输入提取不同的 JSON 字段。

```json
{
  "id": "exchange_rate",
  "meta": { "name": "汇率", "version": "1.0", "author": "Demo" },
  "inputs": [
    { "key": "target", "label": "目标货币", "default": "CNY", "scope": "target" }
  ],
  "execution": {
    "type": "chain", "interval": 60,
    "steps": [
      {
        "id": "fetch",
        "url": "https://api.frankfurter.app/latest?to={{target}}",
        "extract": {
          // 关键点：这里用了 {{target}} 动态提取
          // 如果用户填 CNY，就提取 rates.CNY
          "rate": "rates.{{target}}" 
        }
      }
    ]
  },
  "outputs": [
    { "key": "rate", "label": "EUR/{{target}}", "format_val": "{{rate}}" }
  ]
}
```

---

## 6. 常见问题 (FAQ)

**Q: 为什么显示 "Err"?**
A: 通常是网络请求失败。请检查 URL 是否正确，或者 API 是否需要翻墙。

**Q: 为什么显示 "?"**
A: JSON 解析失败。说明你 `extract` 里的路径写错了，或者 API 返回的结构变了。

**Q: 为什么显示 "[Empty]"**
A: 变量提取成功了，但是那个字段的值是空的。

**Q: 修改了 json 没生效？**
A: 需要在 LiteMonitor 的插件设置页面操作关闭->保存>启动，或者直接重启软件。

**Q: 怎么调试？**
A: 可以在 `outputs` 里临时加一项，把 `format_val` 设为整个 JSON (`"{{$raw}}"`) 或者中间变量，看看数据到底是什么。
