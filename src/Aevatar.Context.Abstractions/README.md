# Aevatar.Context.Abstractions

Context Database 的契约层，定义 `aevatar://` 虚拟文件系统和检索链路的公共接口。该项目不包含具体实现，也不依赖外部基础设施。

## 职责

- 定义 `AevatarUri` 值对象（解析、构建、导航、比较）
- 定义 `IContextStore`（虚拟文件系统 CRUD + L0/L1 快捷访问）
- 定义 `IContextRetriever`（简单检索与复杂检索）
- 定义公共模型：`ContextType`、`ContextDirectoryEntry`、`FindResult`、`MatchedContext`、`SessionInfo`

## URI 结构

```text
aevatar://{scope}/{path}
```

约定 scope：

- `skills`
- `resources`
- `user`
- `agent`
- `session`

说明：物理路径映射由 `Aevatar.Context.Core` 中的 `AevatarUriPhysicalMapper` 实现。

## `AevatarUri` 行为

`AevatarUri` 是 `readonly record struct`，具备值语义和不可变特性。

```csharp
var uri = AevatarUri.Parse("aevatar://skills/web-search/SKILL.md");
uri.Scope       // "skills"
uri.Path        // "web-search/SKILL.md"
uri.Name        // "SKILL.md"
uri.IsDirectory // false
uri.Parent      // aevatar://skills/web-search/
```

关键规则：

- Scheme 大小写不敏感，`Scope` 归一化为小写
- `aevatar://scope` 与 `aevatar://scope/` 都视为目录
- `Path` 尾部 `/` 会裁剪，目录语义由 `IsDirectory` 保留
- 根目录的 `Parent` 返回自身
- `Join("")` 返回自身

快捷构建：

```csharp
AevatarUri.SkillsRoot();          // aevatar://skills/
AevatarUri.ResourcesRoot();       // aevatar://resources/
AevatarUri.UserRoot("u123");      // aevatar://user/u123/
AevatarUri.AgentRoot("a456");     // aevatar://agent/a456/
AevatarUri.SessionRoot("run789"); // aevatar://session/run789/
```

## 核心接口

### `IContextStore`

| 方法 | 说明 |
|---|---|
| `ReadAsync` | 读取文件内容 |
| `WriteAsync` | 写入文件内容 |
| `DeleteAsync` | 删除文件或目录 |
| `ListAsync` | 列举目录直接子项 |
| `GlobAsync` | 按模式搜索文件 |
| `ExistsAsync` | 检查文件或目录是否存在 |
| `CreateDirectoryAsync` | 创建目录 |
| `GetAbstractAsync` | 读取 L0（`.abstract.md`） |
| `GetOverviewAsync` | 读取 L1（`.overview.md`） |

### `IContextRetriever`

| 方法 | 说明 |
|---|---|
| `FindAsync` | 简单语义搜索（无 session 意图分析） |
| `SearchAsync` | 复杂语义搜索（有 session，上游可做意图分析） |

## 依赖

无。纯抽象层。
