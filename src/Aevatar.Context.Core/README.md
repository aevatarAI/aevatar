# Aevatar.Context.Core

Context Database 的存储实现层。负责 URI 到物理路径映射，以及 `IContextStore` 的本地/内存实现。

## 职责

- `AevatarUriPhysicalMapper`：`aevatar://` 与物理路径的双向转换
- `LocalFileContextStore`：基于本地文件系统的生产默认实现
- `InMemoryContextStore`：基于 `ConcurrentDictionary` 的测试实现
- DI 注册扩展：`AddContextStore` / `AddInMemoryContextStore`

## 物理路径映射

| URI | 物理路径 | 映射常量 |
|---|---|---|
| `aevatar://skills/...` | `~/.aevatar/skills/...` | `AevatarPaths.Skills` |
| `aevatar://resources/...` | `~/.aevatar/resources/...` | `AevatarPaths.Resources` |
| `aevatar://user/...` | `~/.aevatar/users/...` | `AevatarPaths.Users` |
| `aevatar://agent/...` | `~/.aevatar/agents/...` | `AevatarPaths.AgentData` |
| `aevatar://session/...` | `~/.aevatar/sessions/...` | `AevatarPaths.Sessions` |

说明：

- `FromPhysicalPath` 仅识别上述路径前缀，不匹配时返回 `null`。
- `AevatarPaths.AgentData` 当前与 `AevatarPaths.Agents` 指向同一物理目录。

## `LocalFileContextStore` 行为

| 方法 | 当前行为 |
|---|---|
| `ReadAsync` | 文件不存在抛 `FileNotFoundException` |
| `WriteAsync` | 自动创建父目录，覆盖写入 |
| `DeleteAsync` | 目标不存在时静默返回；目录删除依赖 `recursive` |
| `ListAsync` | 仅返回直接子项，跳过 `.` 开头隐藏项 |
| `GlobAsync` | 递归搜索，大小写不敏感，模式会做简化 |
| `ExistsAsync` | 文件与目录分别走 `File.Exists` / `Directory.Exists` |
| `GetAbstractAsync` | 读取目录下 `.abstract.md`（文件 URI 自动取父目录） |
| `GetOverviewAsync` | 读取目录下 `.overview.md`（文件 URI 自动取父目录） |

## `InMemoryContextStore` 行为

- 写入时自动补齐父目录。
- 目录 `ExistsAsync` 在有后代时也会返回 `true`。
- `GlobAsync` 实现覆盖常见模式，但不是完整 glob 语义。

## L0/L1 约定

L0/L1 作为目录级隐藏文件存储：

```text
~/.aevatar/skills/web-search/
├── .abstract.md
├── .overview.md
└── SKILL.md
```

## 已知限制

- 路径映射层当前没有显式路径穿越防护。
- `GlobAsync` 为轻量实现，复杂组合模式不保证完整兼容。

## DI 注册

```csharp
// 生产环境
services.AddContextStore();

// 测试环境
services.AddInMemoryContextStore();
```

`AddContextStore()` 会调用 `AevatarPaths.EnsureContextDirectories()`，确保 Context 相关目录存在。

## 依赖

- `Aevatar.Context.Abstractions`
- `Aevatar.Configuration`
