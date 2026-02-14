# Aevatar.Configuration

`Aevatar.Configuration` 提供 Aevatar 运行时配置加载能力，统一处理 `~/.aevatar` 下的配置文件和密钥文件。

## 职责

- 加载 `config.json`、`secrets.json`、`mcp.json`、`connectors.json`
- 管理 `~/.aevatar/agents` 与 `~/.aevatar/workflows` 下的 YAML 文件发现与读取
- 提供 `secrets.json` 的读写封装（API Key 等）
- 提供命名 connector 配置模型与策略字段（MCP/HTTP/CLI）
- 提供 DI 注册扩展 `AddAevatarConfig()`

## 核心类型

- `AevatarConfigLoader`：把本地配置合并到 `IConfigurationBuilder`
- `AevatarSecretsStore`：按 key 读取/写入敏感配置
- `AevatarMCPConfig`：读取 MCP 服务器配置
- `AevatarConnectorConfig`：读取命名 connector 配置（含 allowlist/timeout/retry）
- `AevatarAgentYamlLoader`：扫描并读取 Agent/Workflow YAML
- `AevatarPaths`：统一路径定义与目录初始化；另提供 `RepoRoot` / `RepoRootWorkflows`，宿主（如 Api）会从仓库根目录的 `workflows/` 加载 YAML（若存在），用户无需拷贝到 `~/.aevatar`。

## Connector 作用与配置

### Connector 是什么、用来做什么

Connector 是框架提供的**命名外部调用抽象**：在认知工作流（Cognitive Workflow）中，用统一契约调用外部能力，而不必在 YAML 里写死 URL、命令或 MCP 细节。

- **使用场景**：由带 role 的 workflow（如 MAKER 分析）在某个步骤里按「名称」调用外部服务或本地命令。
- **谁消费**：工作流步骤类型 `connector_call`。步骤里通过 `parameters.connector` 指定已配置的 connector 名称，运行时从 `IConnectorRegistry` 解析并执行。
- **支持类型**：`http`（HTTP 接口）、`cli`（本地可执行命令）、`mcp`（MCP 服务器工具调用）。每种类型有独立的策略字段（如 baseUrl、command、allowedTools 等），用于安全与行为控制。

配置好的 connector 在应用启动时由宿主从 `connectors.json` 加载，并注册到 `IConnectorRegistry`；workflow 只需在 YAML 里写 `connector_call` + 对应名称即可。

### Role 与 Connector 分配（方案 A：中心化配置 + 按角色授权）

Connector **定义**保持中心化在 `~/.aevatar/connectors.json`；**谁能用**则在 **Role（角色）** 上配置，便于在 Agent/Role YAML 中一眼看出该「AI 员工」具备哪些外部能力。

- 在 **workflow 的 roles** 或 **独立 role/agent YAML** 中，为每个角色增加 `connectors` 列表，列出该角色允许调用的 connector 名称（须与 `connectors.json` 中的 `name` 一致）。
- 工作流中 `connector_call` 步骤需指定 `role`（或 `target_role`），表示「以该角色身份调用」；运行时校验该角色的 `connectors` 是否包含当前 connector，不包含则报错。
- 若不指定 `role`，则不进行角色允许列表校验（向后兼容）。

示例（workflow 内联 role）：

```yaml
roles:
  - id: coordinator
    name: Coordinator
    system_prompt: "..."
    connectors:
      - maker_post_processor
      - my_api
steps:
  - id: connector_post
    type: connector_call
    role: coordinator
    parameters:
      connector: maker_post_processor
      timeout_ms: "8000"
```

示例（独立 role YAML，如 `roles/coordinator.yaml`）：

```yaml
name: Coordinator
system_prompt: "..."
provider: deepseek
model: deepseek-chat
connectors:
  - maker_post_processor
```

### 配置文件位置与格式

- **默认路径**：`~/.aevatar/connectors.json`
- **支持三种 JSON 形状**（任选其一）：
  1. `{ "connectors": [ { "name": "...", "type": "...", ... } ] }` — 数组
  2. `{ "connectors": { "my_connector": { "type": "...", ... } } }` — 对象，key 为 name
  3. `{ "connectors": { "definitions": [ ... ] } }` — 对象内 definitions 数组

每条 connector 需包含：

- `name`：名称，workflow 中 `parameters.connector` 用此名引用。
- `type`：`"http"` | `"cli"` | `"mcp"`。
- `enabled`：可选，默认 `true`；`false` 时不会加载。
- `timeoutMs`：可选，100–300000，默认 30000。
- `retry`：可选，0–5，默认 0。
- 按类型再写 `http` / `cli` / `mcp` 子对象（见下方示例）。

### 配置示例

#### HTTP Connector

```json
{
  "connectors": [
    {
      "name": "my_api",
      "type": "http",
      "enabled": true,
      "timeoutMs": 10000,
      "retry": 1,
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

- workflow 里可传 `parameters.path` 或 `parameters.operation` 指定路径（必须在 `allowedPaths` 内，或允许 `"/"` 表示任意）。
- 请求体来自上一步输出；若配置了 `allowedInputKeys`，则只允许 JSON 中出现这些 key。

#### CLI Connector

```json
{
  "connectors": [
    {
      "name": "maker_post_processor",
      "type": "cli",
      "timeoutMs": 8000,
      "cli": {
        "command": "cat",
        "fixedArguments": [],
        "allowedOperations": [],
        "allowedInputKeys": [],
        "workingDirectory": "",
        "environment": {}
      }
    }
  ]
}
```

- `command` 必须是本机已安装的可执行命令（不允许 `://` 形式）。
- 上一步输出通过 stdin 传入；stdout 作为本步输出。

#### MCP Connector

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

- 通过 `allowedTools` 限制可调用的 MCP 工具；未配置则按实现行为（如仅 defaultTool 或全部允许）。

### 在 Workflow YAML 里使用 Connector

在 workflow 的 `steps` 中增加一步 `type: connector_call`，用 `parameters.connector` 指定上面配置的名称；建议同时写 `role`（或 `target_role`），以便按角色允许列表校验（见上文「Role 与 Connector 分配」）：

```yaml
steps:
  # 前面的步骤（如 llm_call、compose）...
  - id: connector_post
    type: connector_call
    role: coordinator   # 以该角色身份调用，须在 role.connectors 允许列表中
    parameters:
      connector: maker_post_processor   # 对应 connectors.json 中的 name
      timeout_ms: "8000"
      retry: "1"
      on_missing: "skip"                # 未配置该 connector 时跳过，不失败
      on_error: "continue"              # 执行失败时用上一步输出继续
```

常用参数说明：

| 参数 | 说明 |
|------|------|
| `role` / `target_role` | 可选。指定以哪个角色身份调用；若该角色配置了 `connectors`，则仅允许列表内的 connector。 |
| `connector` | 必填。connectors.json 中的 connector 名称。 |
| `timeout_ms` | 可选。本步超时（毫秒），会受 connector 自身 timeoutMs 上限约束。 |
| `retry` | 可选。失败后重试次数，0–5。 |
| `optional` | 可选。`true` 时等价于 `on_missing: skip`。 |
| `on_missing` | 可选。connector 未找到时：`fail`（默认）或 `skip`。 |
| `on_error` | 可选。执行失败时：`fail`（默认）或 `continue`（用上步输出继续）。 |

执行结果会写入步骤元数据，例如：`connector.name`、`connector.type`、`connector.duration_ms`，HTTP 有 `connector.http.status_code`，CLI 有 `connector.cli.exit_code` 等，便于录制与排查。

### Connector 配置要点（安全与策略）

- 配置文件：`~/.aevatar/connectors.json`
- 支持类型：`mcp` / `http` / `cli`
- 安全字段（建议按需配置）：
  - allowlist：`allowedMethods` / `allowedPaths`（http）、`allowedOperations`（cli）、`allowedTools`（mcp）
  - timeout：`timeoutMs`
  - schema：`allowedInputKeys`（输入 JSON key 白名单）

## 配置优先级

默认优先级（从低到高）：

1. `config.json`
2. `secrets.json`
3. 环境变量（`AEVATAR_` 前缀）

## 依赖

- `Microsoft.Extensions.Configuration.*`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`
- `YamlDotNet`
