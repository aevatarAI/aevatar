# PR Review 架构打分报告（Workflow Observability Sampling Ratio）- 2026-03-05

## 1. 审计范围与方法

1. 审计对象：`src/workflow/Aevatar.Workflow.Host.Api/ObservabilityExtensions.cs`。
2. 审计输入：本次 PR review 结论（有效问题 1 条：P2）。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制，6 维度）。
4. 证据标准：仅采纳可定位到 `文件路径:行号` 的实现证据。

## 2. 审计结论（摘要）

1. 当前变更主架构分层未破坏，但存在配置稳健性缺陷：`NaN/Infinity` 采样率可导致宿主启动失败。
2. 结论：建议先修复该 P2 再合并，避免将可配置输入变为启动崩溃触发器。

## 3. 总体评分（100 分制）

**总分：86 / 100（A-）**

| 维度 | 权重 | 得分 | 扣分说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | 仍保持 Host 扩展层职责，但配置容错不足影响宿主稳定性。 |
| CQRS 与统一投影链路 | 20 | 18 | 本问题不直接破坏 CQRS 主链，但会阻断运行时进入可观测主链路。 |
| Projection 编排与状态约束 | 20 | 18 | 不涉及中间层事实态字典，但异常采样值可使运行态初始化提前失败。 |
| 读写分离与会话语义 | 15 | 13 | 配置输入未做非有限数值兜底，削弱系统对异常输入的语义收敛。 |
| 命名语义与冗余清理 | 10 | 9 | 命名整体清晰，问题集中在数值合法性判定。 |
| 可验证性（门禁/构建/测试） | 15 | 9 | 现有验证缺少 `NaN/Infinity` 配置场景，未能在测试阶段阻断启动崩溃。 |

## 4. 问题分级与证据

### F1（P2）采样率解析未拦截非有限数值，可能触发启动崩溃

1. 证据：
   - `double.TryParse` 成功后直接进入 `Math.Clamp`，未验证 `double.IsNaN/IsInfinity`：  
     `src/workflow/Aevatar.Workflow.Host.Api/ObservabilityExtensions.cs:55`-`:58`
   - `samplingRatio` 直接进入 `TraceIdRatioBasedSampler` 构造：  
     `src/workflow/Aevatar.Workflow.Host.Api/ObservabilityExtensions.cs:33`
2. 影响：
   - 当 `Observability:Tracing:SampleRatio=NaN` 或 `OTEL_TRACES_SAMPLER_ARG=NaN` 时，`Math.Clamp` 返回 `NaN`。
   - `TraceIdRatioBasedSampler(NaN)` 抛出 `ArgumentOutOfRangeException`，可导致 Host 启动失败。
3. 修复准入：
   - 对解析值增加 `double.IsNaN(ratio) || double.IsInfinity(ratio)` 判定，命中时回退 `defaultValue`。
   - 保留现有 `0..1` clamp 逻辑，仅对有限值生效。

## 5. 合并门禁建议（本次 PR）

1. 修复 F1 后再合并，避免把可配置项变成进程启动级故障点。
2. 至少补充以下测试并通过：
   - `Observability:Tracing:SampleRatio=NaN` 回退默认值；
   - `OTEL_TRACES_SAMPLER_ARG=Infinity/-Infinity` 回退默认值；
   - 合法值（如 `0.25`、`1`、`0`）仍按预期生效。

## 6. 非扣分观察项（基线口径）

1. 本次问题不涉及 InMemory/Local Actor/ProjectReference 基线项，不触发基线扣分。
2. 其影响集中在 Host 可观测配置健壮性，不改变 Domain/Application/Infrastructure/Host 分层结构。
