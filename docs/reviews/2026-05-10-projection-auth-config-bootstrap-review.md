# 2026-05-10 Projection / Authentication / Configuration / Bootstrap Review

## 范围

- U06: `Aevatar.CQRS.Projection.Providers.Elasticsearch`, `Aevatar.CQRS.Projection.Providers.Neo4j`, `Aevatar.CQRS.Projection.Providers.InMemory`
- U07: `Aevatar.Authentication.Abstractions`, `Aevatar.Authentication.Hosting`, `Aevatar.Authentication.Providers.NyxId`
- U08: `Aevatar.Configuration`
- U09: `Aevatar.Bootstrap`, `Aevatar.Bootstrap.Extensions.AI`, `Aevatar.Hosting`

本轮重点复核旧审计中认证覆盖不足的问题，并额外检查 issue #533 指向的 Elasticsearch projection index mapping 标准化。

## 总体结论

| 单元 | 结论 | 风险级别 |
| --- | --- | --- |
| U06 Projection Providers | Provider 边界基本成型，但读路径仍会触发生命周期操作，Elasticsearch 稳定字段 mapping 未标准化完成。 | P1 |
| U07 Authentication | 旧审计里的“完全缺少保护”已经不准确，当前有 DI/单元测试覆盖；但 NyxID JWT 真实验证链路、claim 映射边界仍不够硬。 | P1 |
| U08 Configuration | 路径与配置加载能力完整，但关键配置存在 fail-open 和本地明文 secret 回退。 | P2 |
| U09 Bootstrap / Hosting | 组合面做了不少防线，但不同 capability 的生产策略不一致，AI fallback 和 connector bootstrap 仍可能绕过预期治理。 | P1 |

## Findings

### P1 - Query 和 health 路径会创建/修改外部存储结构

证据：

- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.cs:169` 的 `GetAsync` 在读路径调用 `EnsureIndexAsync`。
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.cs:197` 的 `QueryAsync` 同样会先 `EnsureIndexAsync`。
- `src/Aevatar.CQRS.Projection.Providers.Neo4j/Stores/Neo4jProjectionGraphStore.cs:213`、`:250`、`:287`、`:325` 的 read/list/neighbors/subgraph 路径都会调用 `EnsureSchemaAsync`。
- `src/Aevatar.CQRS.Projection.Providers.Neo4j/Stores/Neo4jProjectionGraphStore.Infrastructure.cs:35` 会在 `EnsureSchemaAsync` 里创建约束。
- `src/Aevatar.Hosting/Extensions/AevatarPlatformHostBuilderExtensions.cs:63` 和 `:78` 的 readiness health contributor 分别调用 document query 与 graph list。

影响：

read/query/health probe 被动创建 Elasticsearch index 或 Neo4j constraint，违反仓库规则里的 query/read path 不触发 projection priming/lifecycle。生产上也会让只读 health endpoint 带来权限、审计和启动顺序上的副作用。

建议：

- 把 index/schema 初始化移动到显式 writer/bootstrap/materializer 生命周期。
- read store 提供纯读语义：索引不存在时返回 empty/not found 或明确异常，不隐式创建。
- health check 改用不变更外部系统的 ping/readiness port，或只检查已经存在的资源。

### P1 - #533 未真正完成 Elasticsearch 稳定 read model 字段 mapping

证据：

- `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/ReadModels/IProjectionReadModel.cs:5` 定义了稳定字段：`Id`, `ActorId`, `StateVersion`, `LastEventId`, `UpdatedAt`。
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStoreMetadataSupport.cs:139` 在无 mapping 时只补 `ProjectionDocumentId`。
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStoreMetadataSupport.cs:157` 在有 mapping 时也只校验/补 `ProjectionDocumentId`。
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStorePayloadSupport.cs:9` 默认主排序字段是 `CreatedAt`，但 `IProjectionReadModel` 的稳定时间字段是 `UpdatedAt`。

影响：

issue #533 要求稳定 read model 字段的 index mapping 标准化。当前 provider 层只保证了内部 tie-breaker 字段，核心字段仍依赖各 read model 自己声明或 ES dynamic mapping。默认排序还指向不属于根契约的 `CreatedAt`，容易造成 unmapped sort、动态类型漂移或跨 provider 查询顺序不一致。

建议：

- Provider 层统一注入根契约字段 mapping：`Id/ActorId/LastEventId` 为 `keyword`，`StateVersion` 为长整型，`UpdatedAt` 为 date。
- 默认排序改为 `UpdatedAt + ProjectionDocumentId`，或明确要求 metadata 显式声明排序字段。
- 增加测试覆盖空 mapping、有 mapping、冲突 mapping、默认排序四类场景。

### P1 - GAgentService / Governance 缺少生产环境禁用 InMemory projection 的最后防线

证据：

- `src/Aevatar.Hosting/DependencyInjection/WorkflowProjectionProviderServiceCollectionExtensions.cs:268` 和 `:285` 在 Production 或 deny policy 下拒绝 InMemory provider。
- `src/Aevatar.Hosting/DependencyInjection/ScriptingProjectionProviderServiceCollectionExtensions.cs:263` 和 `:280` 有同样策略。
- `src/platform/Aevatar.GAgentService.Hosting/DependencyInjection/ServiceCollectionExtensions.cs:89` 只做 Elasticsearch/InMemory 二选一，没有调用生产策略。
- `src/platform/Aevatar.GAgentService.Governance.Hosting/DependencyInjection/ServiceCollectionExtensions.cs:49` 也只做二选一，没有生产策略。
- `src/Aevatar.Hosting/Extensions/MainnetHostBuilderExtensions.cs:70` 会把 GAgentService capability bundle 放进 mainnet host。

影响：

Workflow/Scripting 已经有生产策略，但 GAgentService/Governance 没有同等保护。只要 mainnet/distributed config 被遗漏或覆盖，这两条 read model 链路仍可能在生产环境使用 InMemory。

建议：

- 抽出共享的 projection provider policy helper，所有 capability 的 provider 注册都调用同一套规则。
- 增加 mainnet composition 测试：Production + InMemory GAgentService/Governance 应该启动失败。

### P1 - NyxID claim transformer 会把任意 `*_id` 映射为 `scope_id`

证据：

- `src/Aevatar.Authentication.Providers.NyxId/NyxIdClaimsTransformer.cs:7` 注释声明 fallback 顺序包含 `any *_id claim`。
- `src/Aevatar.Authentication.Providers.NyxId/NyxIdClaimsTransformer.cs:47` 会取第一个未忽略的 `*_id` claim。
- `test/Aevatar.Bootstrap.Tests/AuthenticationHostCoverageTests.cs:175` 固定了 `order_id -> scope_id` 的行为。

影响：

`scope_id` 是授权与访问控制语义，不能从任意 `tenant_id/order_id/app_id` 等泛化 claim 推断。当前 fallback 容易把业务对象 ID 当成 scope 身份，属于认证边界的语义降级。

建议：

- 删除泛化 `*_id` fallback，只接受 NyxID 明确契约字段，例如 `scope_id/uid/sub/NameIdentifier`。
- 如确需兼容历史 claim，应通过显式 allowlist 配置，而不是字符串后缀推断。

### P1 - Auth enabled 后没有对 Authority / Audience 做启动期硬校验

证据：

- `src/Aevatar.Authentication.Hosting/Extensions/AevatarAuthenticationHostExtensions.cs:28` 默认注册 JWT bearer。
- `src/Aevatar.Authentication.Hosting/Extensions/AevatarAuthenticationHostExtensions.cs:49` 直接设置 `jwt.Authority = options.Authority`。
- `src/Aevatar.Authentication.Hosting/Extensions/AevatarAuthenticationHostExtensions.cs:55` 在 Audience 为空时关闭 audience validation。
- `src/Aevatar.Authentication.Abstractions/AevatarAuthenticationOptions.cs:20` 允许 Audience 为空并跳过校验。
- 当前测试主要是 DI 与策略测试，没有覆盖 fake OIDC/JWKS 的真实 JWT 验证 roundtrip。

影响：

认证默认开启是正确方向，但生产配置缺少 Authority 或 Audience 时，host 可以完成构建，错误会延后到请求时暴露；Audience 为空还会降低 token 约束强度。

建议：

- 在非 Development 且 auth enabled 时启动期校验 Authority 为合法绝对 URL。
- 明确生产是否允许空 Audience；若不允许，启动期 fail fast。
- 增加 TestServer + fake OIDC/JWKS 的集成测试，覆盖有效 token、错误 issuer、错误 audience、缺 scope 四类路径。

### P1 - AI provider fallback 直接读取通用环境变量，绕过 DI secret policy

证据：

- `src/Aevatar.Bootstrap.Extensions.AI/ServiceCollectionExtensions.cs:597` 的 `CreateSecretsStoreAccessor` 优先使用 DI secret store，这是正确的主路径。
- `src/Aevatar.Bootstrap.Extensions.AI/ServiceCollectionExtensions.cs:714` 的 `ResolveApiKeySelection` 又直接读取 `DEEPSEEK_API_KEY`、`OPENAI_API_KEY`、`AEVATAR_LLM_API_KEY`。
- `test/Aevatar.Bootstrap.Tests/AIFeatureBootstrapCoverageTests.cs:240` 和 `:266` 明确覆盖了通用环境变量触发 fallback provider。
- `src/Aevatar.Hosting/Extensions/AevatarPlatformHostBuilderExtensions.cs:41` 默认开启 AI feature bootstrap。

影响：

mainnet host 已经把 local file secret store 关掉并使用环境 secret store，但 AI fallback 又绕回 raw process env。机器上残留的 `OPENAI_API_KEY` 或 `DEEPSEEK_API_KEY` 可能创建非预期 provider，绕过平台希望使用的 provider/secret 策略。

建议：

- raw env fallback 只允许 Development/local 模式，生产/mainnet 只读 `IAevatarSecretsStore` 或显式配置。
- 增加 mainnet composition 测试：存在通用 raw env key、没有配置 provider 时，不应隐式注册 direct LLM provider。

### P1 - Neo4j graph edge 唯一性与 InMemory provider 语义不一致

证据：

- `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/IProjectionGraphStore.cs:10` 的 `DeleteEdgeAsync(scope, edgeId)` 表明 `edgeId` 在 scope 内是稳定定位键。
- `src/Aevatar.CQRS.Projection.Providers.InMemory/Stores/InMemoryProjectionGraphStore.cs:94` 使用 `scope + edgeId` 作为 upsert key。
- `src/Aevatar.CQRS.Projection.Providers.Neo4j/Stores/Neo4jProjectionGraphStoreCypherSupport.cs:15` 使用 `(from)-[r:{edgeType} {scope, edgeId}]->(to)` 做 `MERGE`，相同 `scope + edgeId` 但端点不同会生成多条关系。
- `src/Aevatar.CQRS.Projection.Providers.Neo4j/Stores/Neo4jProjectionGraphStoreCypherSupport.cs:35` 的 delete 会匹配并删除所有相同 `scope + edgeId` 的关系。

影响：

同一业务图在 InMemory 与 Neo4j 下 upsert/delete 结果可能不同，尤其是 edge 被重新指向新节点时。读模型 provider 不应改变契约语义。

建议：

- Neo4j 写入前按 `scope + edgeId` 删除旧关系，再创建新关系，或把 edge 建模为唯一节点再连接端点。
- 增加跨 provider contract tests，覆盖同 edgeId 改端点、重复 upsert、delete 后列表为空。

### P2 - connector 配置解析失败会静默变成“没有 connector”

证据：

- `src/Aevatar.Configuration/AevatarConnectorConfig.cs:95` 读取 JSON。
- `src/Aevatar.Configuration/AevatarConnectorConfig.cs:112` catch all 后返回空数组。
- `src/Aevatar.Bootstrap/Connectors/ConnectorRegistration.cs:16` entries 为空时直接 no-op。
- `src/Aevatar.Bootstrap/Connectors/ConnectorBootstrapHostedService.cs:35` bootstrap 只记录注册数量，不区分“没有配置”和“配置坏了”。

影响：

坏 JSON、字段拼错、entry 缺必需字段都会让 host 正常启动，只是 connector surface 消失。对插件/工具调用能力来说，这是典型 fail-open。

建议：

- Host/bootstrap 使用 strict loader：配置文件存在但不可解析或 entry 无效时 fail fast。
- CLI/discovery 如需 lenient 行为，单独保留显式模式。
- readiness 可选检查 required connector names。

### P2 - HttpConnector 默认 `AllowedPaths = ["/"]` 实际允许所有路径

证据：

- `src/Aevatar.Configuration/AevatarConnectorConfig.cs:27` 默认路径 allowlist 是 `/`。
- `src/Aevatar.Bootstrap/Connectors/HttpConnector.cs:300` 的 path matcher 把 `/` 当作所有 path 的前缀。
- `test/Aevatar.Bootstrap.Tests/ConnectorAndHostingCoverageTests.cs:399` 的有效配置只需要 baseUrl，不需要显式 allowed paths。

影响：

HTTP connector 注释表达的是 domain/method/path allowlist，但默认配置会允许 base URL 下所有路径。若 connector 能被配置到敏感内网服务，这个默认过宽。

建议：

- `/` 只匹配根路径，通配应使用显式 `/*`。
- 对生产配置要求显式 `allowedPaths`，并增加负向测试。

### P2 - 本地 secret store 会静默回退到明文文件

证据：

- `src/Aevatar.Configuration/AevatarSecretsStore.cs:106` 加密读取失败会 fallback plaintext。
- `src/Aevatar.Configuration/AevatarSecretsStore.cs:153` 保存失败或无 master key 时会写 plaintext。
- `src/Aevatar.Hosting/Extensions/MainnetHostBuilderExtensions.cs:54` mainnet 已强制关闭 local file secret store，这是正确防线。

影响：

mainnet 路径已覆盖，但非 mainnet host 或开发部署仍可能在用户不知情的情况下写入明文 secret。secret store 的安全强度不应由“有没有 keychain/master key”静默决定。

建议：

- 明文 fallback 改成显式选项，例如 `AllowPlaintextLocalSecrets`，默认关闭。
- 至少在写明文时输出强 warning，并在 CI/Production 环境直接拒绝。

### P2 - YAML loader 接收未校验的 ID 拼接文件路径

证据：

- `src/Aevatar.Configuration/AevatarAgentYamlLoader.cs:19` 通过 `AevatarPaths.AgentYaml(agentId)` 读取文件。
- `src/Aevatar.Configuration/AevatarAgentYamlLoader.cs:30` 通过 `AevatarPaths.WorkflowYaml(workflowName)` 读取文件。
- `src/Aevatar.Configuration/AevatarPaths.cs:164` 和 `:169` 直接 `Path.Combine(..., $"{id}.yaml")`，没有校验 `..` 或绝对路径。

影响：

如果上层把用户输入的 agentId/workflowName 传入 loader，`../` 可逃逸出目标目录并读取同后缀文件。

建议：

- 对 ID 使用严格 token 校验：小写字母、数字、`-`、`_`。
- 或在 `AevatarPaths` 里统一 `GetFullPath` 后检查必须落在 root 目录下。

### P2 - auth enabled 解析逻辑在 Hosting 与 scope guard 中重复

证据：

- `src/Aevatar.Authentication.Hosting/Extensions/AevatarAuthenticationHostExtensions.cs:72` 有一份 `ResolveAuthenticationEnabled`。
- `src/Aevatar.Hosting/Middleware/AevatarScopeAccessGuard.cs:110` 有另一份同名逻辑。

影响：

当前两份逻辑看起来一致，但安全开关不应该靠复制保持一致。后续任何一处修改都可能让认证中间件和 scope guard 对“是否启用认证”产生不同理解。

建议：

- 抽成 `AevatarAuthenticationOptions` 的共享解析 helper。
- scope guard 使用 `AevatarStandardClaimTypes.ScopeId`，避免 claim type 字符串在多个层面散落。

## 旧审计校正

旧审计提到 U07 “缺少专门保护”。这句话需要更新：现在 `test/Aevatar.Bootstrap.Tests/AuthenticationHostCoverageTests.cs` 已覆盖 JWT scheme 注册、fallback policy、disabled scheme、NyxID claims transformer 等 DI 行为；`test/Aevatar.Hosting.Tests/MainnetAuthenticationDisabledTests.cs` 也覆盖了 mainnet 禁用认证仍拒绝 protected endpoint 的场景。

但这还不是完整的 NyxID M0 auth integration。缺口仍在：没有真实 JWT/JWKS 验证链路，没有 issuer/audience/scope 的端到端负向测试，也没有 production 配置缺失时的 fail-fast 测试。

## 建议补充门禁

- Projection providers:
  - 空 mapping 自动补齐稳定字段。
  - 读路径不调用 index/schema lifecycle。
  - InMemory 与 Neo4j graph edge contract tests。
- Authentication:
  - fake OIDC/JWKS TestServer 集成测试。
  - 非 Development auth enabled 缺 Authority/Audience 启动失败。
  - 删除 `*_id` fallback 后的 claim 映射回归测试。
- Configuration / Bootstrap:
  - strict connector config loader。
  - mainnet + raw AI env key 不隐式创建 direct provider。
  - Production + InMemory GAgentService/Governance 启动失败。

## 本轮未执行

本轮是静态 review 与文档产出，未运行 `dotnet test` 或 CI guard。
