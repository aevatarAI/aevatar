# Role 与工作流、Connector 配置指南

本文说明 Aevatar 中 **Role（角色）** 如何与 **Workflow YAML** 配合、如何配置 **Connector**，以及如何把**外部服务（MCP、CLI、HTTP API）** 当作某个角色的能力在工作流里使用。

---

## 1. Role 是什么、和 Workflow 什么关系

- **Role**：工作流里的「参与者」—— 有唯一 `id`、显示名、系统提示词、可选的 LLM 配置（provider/model）、以及**允许使用的 Connector 列表**。
- **Workflow YAML** 里用 `roles:` 定义若干角色，用 `steps:` 定义步骤；步骤里通过 `role` / `target_role` 指定「这一步由谁干」。
- 运行时：**每个 role 会对应一个 RoleGAgent**（子 Actor），由工作流根 Agent 在首次执行前按 YAML 创建并挂成子树。  
  - `llm_call` 步骤会把用户/上步内容发给**指定角色的 RoleGAgent**，由该角色背后的 LLM 生成回复。  
  - `connector_call` 步骤会按名称调用已配置的 Connector；若步骤带了 `role`，且该角色配置了 `connectors` 列表，则**只允许调用列表里的 Connector**（按角色做能力授权）。

因此：**Role = 工作流里的「人」+ 其 LLM 身份 + 其可用的外部能力（Connector）**；Workflow YAML 是「谁做什么」的唯一定义来源。

---

## 2. Workflow YAML 里如何写 Role 与步骤

### 2.1 定义角色：`roles`

在 workflow 的顶层写 `roles:`，每个角色至少要有 `id` 和 `name`，常用字段如下：

```yaml
name: my_workflow
description: 可选描述

roles:
  - id: assistant          # 唯一 ID，步骤里 role 填这个
    name: Assistant        # 显示名
    system_prompt: |       # 该角色 LLM 的系统提示
      You are a helpful assistant.
    provider: deepseek     # 可选，LLM 提供方，默认 deepseek
    model: deepseek-chat   # 可选，模型名
    connectors:            # 可选，该角色允许调用的 Connector 名称列表
      - my_api
      - my_mcp_tools
```

- **id**：必填，步骤里 `role` / `target_role` 引用此值；也会用作该角色对应 RoleGAgent 的 Actor ID。
- **connectors**：字符串数组，名字须与 `~/.aevatar/connectors.json` 里配置的 `name` 一致；未写或空则表示该角色不授权任何 connector（若步骤仍用 `connector_call` 且指定该角色，会按实现做校验）。

### 2.2 步骤里指定角色：`role` / `target_role`

步骤支持 `role` 或 `target_role`（二者等价），表示「这一步由哪个角色执行」：

- **llm_call**：把输入发给该角色对应的 RoleGAgent，用该角色的 system_prompt + provider/model 调 LLM。
- **connector_call**：用 `parameters.connector` 指定要调的 Connector；若本步写了 `role`，且该角色配置了 `connectors`，则**只允许调用列表中的 connector**，否则报错。

示例：一问一答（单角色）

```yaml
roles:
  - id: assistant
    name: Assistant
    system_prompt: |
      You are a helpful assistant. Answer clearly and concisely.

steps:
  - id: answer
    type: llm_call
    role: assistant
    parameters: {}
```

示例：多角色 + 先 LLM 再调外部 API

```yaml
roles:
  - id: coordinator
    name: Coordinator
    system_prompt: |
      You coordinate tasks and decide when to call external services.
    connectors:
      - my_api
      - my_mcp_tools

steps:
  - id: think
    type: llm_call
    role: coordinator
    parameters: {}
  - id: call_api
    type: connector_call
    role: coordinator
    parameters:
      connector: my_api
      timeout_ms: "10000"
```

步骤里不写 `role` 时，`llm_call` 会退化为「发给工作流根自身」（通常无 LLM）；`connector_call` 则不按角色做 connector 允许列表校验（仅按名称解析 connector）。

---

## 3. Connector 配置（把外部服务接进来）

Connector 是**按名称调用的外部能力**：在 `~/.aevatar/connectors.json` 里定义，工作流里用 `connector_call` + 名称即可调用，无需在 YAML 里写 URL/命令/MCP 细节。

### 3.1 配置文件位置与结构

- **路径**：`~/.aevatar/connectors.json`
- **结构**（三种任选其一）：
  - `{ "connectors": [ { "name": "...", "type": "...", ... } ] }`
  - `{ "connectors": { "my_connector": { "type": "...", ... } } }`（key 即 name）
  - `{ "connectors": { "definitions": [ ... ] } }`

每条需有：

- **name**：名称，workflow 里 `parameters.connector` 用此名。
- **type**：`"http"` | `"cli"` | `"mcp"`。
- **enabled**：可选，默认 `true`。
- **timeoutMs**：可选，超时毫秒。
- **retry**：可选，重试次数。
- 按类型再写 **http** / **cli** / **mcp** 子对象（见下）。

### 3.2 三种类型：API、CLI、MCP

**HTTP（外部 API）**

```json
{
  "connectors": [
    {
      "name": "my_api",
      "type": "http",
      "timeoutMs": 10000,
      "http": {
        "baseUrl": "https://api.example.com",
        "allowedMethods": ["POST", "GET"],
        "allowedPaths": ["/v1/process", "/v1/status"],
        "allowedInputKeys": ["text", "options"],
        "defaultHeaders": { "X-Api-Version": "1" }
      }
    }
  ]
}
```

- 请求体来自上一步输出；`allowedPaths` / `allowedInputKeys` 用于安全白名单。
- 步骤里可用 `parameters.path` 或 `parameters.operation` 指定路径（须在 `allowedPaths` 内）。

**CLI（本地命令）**

```json
{
  "connectors": [
    {
      "name": "post_processor",
      "type": "cli",
      "timeoutMs": 8000,
      "cli": {
        "command": "python",
        "fixedArguments": ["-c", "import sys; print(sys.stdin.read().upper())"],
        "allowedOperations": [],
        "workingDirectory": "",
        "environment": {}
      }
    }
  ]
}
```

- `command` 为本机可执行命令；上一步输出经 stdin 传入，stdout 作为本步输出。

**MCP（Model Context Protocol 工具）**

```json
{
  "connectors": [
    {
      "name": "my_mcp_tools",
      "type": "mcp",
      "timeoutMs": 15000,
      "mcp": {
        "serverName": "my_mcp",
        "command": "npx",
        "arguments": ["-y", "my-mcp-server"],
        "environment": {},
        "defaultTool": "process",
        "allowedTools": ["process", "validate"],
        "allowedInputKeys": ["input"]
      }
    }
  ]
}
```

- 通过 `allowedTools` 限制可调用的 MCP 工具名，避免越权调用。

应用启动时会从该文件加载并注册到 `IConnectorRegistry`；工作流里只需写 `connector_call` + `connector: name`。

---

## 4. 把外部服务当作「某个 Role 的能力」

思路：**定义 Connector → 在 Role 上声明允许使用的 Connector 列表 → 在步骤里用该 Role 调用 Connector**。

1. **在 `~/.aevatar/connectors.json` 里**  
   按上面格式配置好一个或多个 connector（如 `my_api`、`post_processor`、`my_mcp_tools`）。

2. **在 Workflow YAML 的 `roles` 里**  
   给需要「用外部服务」的角色加上 `connectors` 列表，列出允许调用的 connector 名称：

   ```yaml
   roles:
     - id: coordinator
       name: Coordinator
       system_prompt: ...
       connectors:
         - my_api
         - my_mcp_tools
   ```

3. **在 `steps` 里**  
   使用 `connector_call`，并写上 `role` 和 `parameters.connector`：

   ```yaml
   steps:
     - id: call_external
       type: connector_call
       role: coordinator
       parameters:
         connector: my_mcp_tools
         timeout_ms: "15000"
         on_missing: "fail"
         on_error: "fail"
   ```

这样：

- **MCP**：在 connectors.json 里配 `type: "mcp"`，在 role 的 `connectors` 里写上该 connector 名，步骤里 `connector_call` + 该 role 即可把 MCP 工具当作该角色的能力。
- **CLI**：同上，`type: "cli"`，role 声明后即可在步骤里以该角色身份调用本地命令。
- **HTTP API**：同上，`type: "http"`，role 声明后即可在步骤里以该角色身份调该 API。

**校验规则**：当步骤带有 `role` 且该角色配置了 `connectors` 时，`parameters.connector` 的值必须在角色允许列表中，否则步骤会失败。未配置 `connectors` 的角色不做此校验（仅按 connector 名称解析）。

---

## 5. connector_call 步骤常用参数

| 参数 | 说明 |
|------|------|
| `role` / `target_role` | 可选。以该角色身份调用；若角色有 `connectors` 列表，则仅允许列表内的 connector。 |
| `connector` | 必填。connectors.json 中的 name。 |
| `timeout_ms` | 可选。本步超时（毫秒）。 |
| `retry` | 可选。失败后重试次数。 |
| `on_missing` | 可选。connector 未找到时：`fail`（默认）或 `skip`。 |
| `on_error` | 可选。执行失败时：`fail`（默认）或 `continue`（用上步输出继续）。 |
| `optional` | 可选。`true` 时等价于 `on_missing: skip`。 |

---

## 6. 和 tool_call 的区别（简要）

- **connector_call**：工作流步骤级、按**名称**调用在 `connectors.json` 里配置好的 HTTP/CLI/MCP，可与 **role + connectors** 配合做「按角色授权」。
- **tool_call**：调用已注册到 Agent 的**工具**（如 MCP、Skills 等），通常和具体 Agent/运行时注册方式绑定，而不是由 connectors.json 统一命名。

需要把「外部 API / 本地命令 / MCP 服务器」当作**工作流里某角色的能力**时，用 **Connector + role.connectors + connector_call** 即可。

---

## 7. 小结

| 要点 | 说明 |
|------|------|
| Role 定义 | 在 workflow YAML 的 `roles` 里写 id、name、system_prompt、provider/model、connectors。 |
| 步骤用角色 | 步骤里写 `role` 或 `target_role`，llm_call 发给对应 RoleGAgent，connector_call 做 connector 允许列表校验。 |
| Connector 配置 | `~/.aevatar/connectors.json`，类型 http / cli / mcp，每类有各自子对象与安全字段。 |
| 外部服务当能力 | 在 connectors.json 里配好 → 在 role 的 connectors 里写上名 → 步骤里 connector_call + 该 role。 |

更细的 Connector 字段与安全策略见 [Aevatar.Configuration README](src/Aevatar.Configuration/README.md#connector-作用与配置)。
