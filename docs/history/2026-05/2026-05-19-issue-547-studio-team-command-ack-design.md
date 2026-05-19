---
title: "Issue 547：Studio team command ACK 语义诚实化设计"
status: design
owner: liyingpei
---

# Issue 547：Studio team command ACK 语义诚实化设计

> 本文面向 [Issue 547](https://github.com/aevatarAI/aevatar/issues/547)：`StudioTeamService.UpdateAsync` / `ArchiveAsync` 在 dispatch command 后立即读取 eventually consistent team readmodel，并把读取结果作为 HTTP 返回。这会让 endpoint 隐含“command 已提交且 readmodel 已观察”的强语义，但当前实现只能保证 command dispatch/accepted。

## 1. 背景

当前 Studio team HTTP surface：

- `PATCH /api/scopes/{scopeId}/teams/{teamId}`
- `POST /api/scopes/{scopeId}/teams/{teamId}/archive`

在 application service 中走：

```csharp
await _commandPort.UpdateAsync(scopeId, teamId, request, ct);
return await GetAsync(scopeId, teamId, ct);
```

```csharp
await _commandPort.ArchiveAsync(scopeId, teamId, ct);
return await GetAsync(scopeId, teamId, ct);
```

其中：

- `IStudioTeamCommandPort` 只负责向 `StudioTeamGAgent` dispatch command/event。
- `GetAsync` 读取 projection readmodel。
- projection 是 eventually consistent，dispatch 成功不等价于 readmodel 已 materialized。

这导致 endpoint 返回 `200 OK + StudioTeamSummaryResponse` 时，调用方可能以为返回的是 post-state snapshot，但实际可能是旧 readmodel，甚至可能因为 readmodel 尚未追上而误报 not found。

## 2. 问题判断

当前代码混合了三个阶段：

| 阶段 | 当前承载位置 | 问题 |
|---|---|---|
| command accepted / dispatched | `IStudioTeamCommandPort.UpdateAsync/ArchiveAsync` | 合理 |
| actor authoritative transition | `StudioTeamGAgent` | endpoint 当前没有同步观察这个阶段 |
| readmodel materialized | dispatch 后立即 `GetAsync` | 不应由 weak ACK 后的即时查询暗示强一致 |

对应 `CLAUDE.md` 约束：

- ACK 诚实：同步返回只承诺已达到阶段；`committed` / `read-model observed` 等强保证须通过独立契约或异步观察获取。
- 查询诚实：readmodel 可最终一致，但不能在弱读结果上暗示强一致。
- Command / Query 分离：write side 不应通过 projection freshness 塑造 command ACK 语义。

## 3. 目标

本设计目标：

1. `UpdateAsync` / `ArchiveAsync` dispatch 后不再立即读取 team readmodel。
2. PATCH / archive endpoint 返回诚实的 accepted receipt，只表达 command intent 已 accepted；state-changing path 才额外表达 envelope 已 dispatched。
3. stale / missing readmodel 不会让成功 dispatch 被误报为 404 或返回旧 post-state。
4. 保持 `GET /teams/{teamId}` 作为读取 materialized team readmodel 的入口。
5. 不引入 bounded polling、observed-version readiness 或 generic async operation framework。

非目标：

- 不重做 StudioTeam actor 协议。
- 不新增 public readiness endpoint。
- 不引入 team command run / async operation resource。
- 不解决 team roster fanout reliable outbox（#544）。
- 不解决 member patch intent 下沉（#545）。
- 不改变 create path，除非 implementation review 发现必须跟随接口收敛；#547 最小范围只覆盖 update/archive。

## 4. 设计原则

### 4.1 Command ACK 必须诚实

PATCH / archive 的同步响应只能承诺 command intent 已通过当前 command port accepted；只有实际 dispatch 了 envelope 的 path 才能承诺 dispatched。它不能承诺 actor state transition 已完成或 readmodel 已可见。

### 4.2 Post-state 读取走显式 query

调用方需要最新 team state 时，应继续使用：

```text
GET /api/scopes/{scopeId}/teams/{teamId}
```

该 query 返回的是 readmodel 当前可见状态，而不是 command response 的一部分。

### 4.3 不用 readmodel 判定 write success

dispatch 后立即读取 projection 并把 missing 映射为 404，是把 readmodel freshness 当成 write result。#547 移除这个模式。

### 4.4 不扩大为通用 async operation framework

#547 只把 Studio team update/archive 的 ACK 改诚实；不引入 operation status endpoint、run actor 或全局 observed-version contract。

## 5. 新增 response contract

建议在：

```text
src/Aevatar.Studio.Application/Studio/Contracts/TeamContracts.cs
```

新增：

```csharp
public static class StudioTeamCommandAckStageNames
{
    public const string Accepted = "accepted";
}

public sealed record StudioTeamCommandAcceptedResponse(
    string ScopeId,
    string TeamId,
    string? CommandId,
    string AckStage,
    DateTimeOffset AcceptedAtUtc);
```

说明：

- `CommandId` 对 dispatch path 使用 `EventEnvelope.Id`，便于日志/追踪；对 no-op patch 为 `null`，避免伪造不存在的 envelope id。
- `AcceptedAtUtc` 使用 acceptance / envelope creation timestamp；它只表示当前 application/adapter accepted intent 的时间，不表示 actor 已 commit，也不表示 readmodel 已 materialized。
- `AckStage` 是同步 ACK 阶段字面量，当前只允许 `accepted`；它不是 operation lifecycle/status，不承诺未来会出现 `committed` / `failed` / `observed` / terminal 状态。
- response 不包含 `DisplayName` / `Description` / `LifecycleStage` / `MemberCount`，因为这些是 readmodel post-state。

## 6. 修改 application / command port contract

当前：

```csharp
Task UpdateAsync(string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default);
Task ArchiveAsync(string scopeId, string teamId, CancellationToken ct = default);
```

改为：

```csharp
Task<StudioTeamCommandAcceptedResponse> UpdateAsync(
    string scopeId,
    string teamId,
    UpdateStudioTeamRequest request,
    CancellationToken ct = default);

Task<StudioTeamCommandAcceptedResponse> ArchiveAsync(
    string scopeId,
    string teamId,
    CancellationToken ct = default);
```

`IStudioTeamService` 同步改为返回 `StudioTeamCommandAcceptedResponse`。

`StudioTeamService.UpdateAsync`：

```csharp
ValidatePatch(request);
return await _commandPort.UpdateAsync(scopeId, teamId, request, ct);
```

`StudioTeamService.ArchiveAsync`：

```csharp
return await _commandPort.ArchiveAsync(scopeId, teamId, ct);
```

删除 dispatch 后 `GetAsync`。

## 7. 修改 ActorDispatchStudioTeamCommandService

`UpdateAsync` / `ArchiveAsync` dispatch 后返回 accepted receipt。

建议把 `DispatchAsync` helper 改为返回 receipt：

```csharp
private async Task<StudioTeamCommandAcceptedResponse> DispatchAsync(
    string scopeId,
    string teamId,
    IMessage payload,
    CancellationToken ct)
{
    var actorId = StudioTeamConventions.BuildActorId(scopeId, teamId);
    var actor = await _bootstrap.EnsureAsync<StudioTeamGAgent>(actorId, ct);
    var commandId = Guid.NewGuid().ToString("N");
    var acceptedAtUtc = DateTimeOffset.UtcNow;

    var envelope = new EventEnvelope
    {
        Id = commandId,
        Timestamp = Timestamp.FromDateTimeOffset(acceptedAtUtc),
        Payload = Any.Pack(payload),
        Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
    };

    await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);

    return new StudioTeamCommandAcceptedResponse(
        scopeId,
        teamId,
        commandId,
        StudioTeamCommandAckStageNames.Accepted,
        acceptedAtUtc);
}
```

### 7.1 No-op patch

当前 no-op patch 不 dispatch：

```csharp
if (!request.DisplayName.HasValue && !request.Description.HasValue)
    return;
```

改成仍返回 accepted receipt，但不 dispatch event，且 `CommandId = null`：

```csharp
if (!request.DisplayName.HasValue && !request.Description.HasValue)
    return BuildAcceptedResponse(normalizedScopeId, normalizedTeamId, commandId: null, acceptedAtUtc: DateTimeOffset.UtcNow);
```

这比伪造 command id 更诚实：同步 response 表示 PATCH intent 已被 accepted，且判定为无需产生 state-changing command；由于没有 envelope dispatch，就没有 dispatched `EventEnvelope.Id` 可返回。

不新增 `noop` ACK stage，避免扩大 wire state machine。含义是：patch intent 已被 accepted，且无需产生 state-changing event。

如果后续产品需要区分 no-op，可另开契约扩展；#547 最小范围不做。

## 8. 修改 endpoint

当前 PATCH：

```csharp
var detail = await teamService.UpdateAsync(...);
return Results.Ok(detail);
```

改为：

```csharp
var accepted = await teamService.UpdateAsync(...);
var location = BuildTeamLocation(accepted.ScopeId, accepted.TeamId);
return Results.Accepted(location, accepted);
```

当前 archive：

```csharp
return Results.Ok(await teamService.ArchiveAsync(scopeId, teamId, ct));
```

改为：

```csharp
var accepted = await teamService.ArchiveAsync(scopeId, teamId, ct);
var location = BuildTeamLocation(accepted.ScopeId, accepted.TeamId);
return Results.Accepted(location, accepted);
```

`BuildTeamLocation` 必须使用 normalized receipt values，并对 path segment 做 URI escaping：

```csharp
private static string BuildTeamLocation(string scopeId, string teamId) =>
    $"/api/scopes/{Uri.EscapeDataString(scopeId)}/teams/{Uri.EscapeDataString(teamId)}";
```

`Location` 指向已有 `GET /api/scopes/{scopeId}/teams/{teamId}` readmodel query URI，不是新的 command status/readiness resource。

保留 exception mapping：

- `InvalidOperationException` -> 400
- `StudioTeamNotFoundException` -> 404

但 implementation 不再通过 post-dispatch readmodel lookup 产生 not-found。若未来 actor-owned command contract 能同步返回 authoritative not-found，endpoint mapping 可继续复用。

## 9. API / client migration note

#547 会改变 PATCH / archive 成功响应 wire contract：

| Endpoint | 当前成功响应 | 新成功响应 |
|---|---|---|
| `PATCH /api/scopes/{scopeId}/teams/{teamId}` | `200 OK + StudioTeamSummaryResponse` | `202 Accepted + StudioTeamCommandAcceptedResponse` |
| `POST /api/scopes/{scopeId}/teams/{teamId}/archive` | `200 OK + StudioTeamSummaryResponse` | `202 Accepted + StudioTeamCommandAcceptedResponse` |

调用方不能再从 write response 读取 post-state 字段：

- `DisplayName`
- `Description`
- `LifecycleStage`
- `MemberCount`
- `CreatedAt`
- `UpdatedAt`

需要 post-state 时，调用方必须显式读取 `Location` 指向的 team GET URI。该 GET 仍是 eventually consistent readmodel query，不能把 write response 的 `202 Accepted` 理解为 readmodel 已更新。

## 10. Not-found 语义

#547 最小实现后：

- PATCH / archive 不再用 stale projection 判断 not found。
- 对不存在 team 的 command 是否最终 rejected，由 `StudioTeamGAgent` 的 authoritative state/command handling 决定。
- endpoint 的 202 response 只表示 command intent accepted；state-changing path 也表示 command envelope 已 dispatched；不表示 team 一定存在或 update/archive 已 materialized。

如果产品要求 PATCH / archive 对不存在 team 同步返回 404，需要引入 actor-owned reply/continuation 或 observed contract。那是更强 command result 语义，不属于 #547 最小范围。

## 11. 测试计划

### 11.1 StudioTeamServiceTests

修改：

```text
test/Aevatar.Studio.Tests/StudioTeamServiceTests.cs
```

覆盖：

1. `UpdateAsync` validates patch and delegates command port。
2. `UpdateAsync` returns accepted receipt without calling query port。
3. `ArchiveAsync` returns accepted receipt without calling query port。
4. stale/missing query fake should not affect update/archive result。

现有：

```text
UpdateAsync_ShouldDelegateAndReRead
ArchiveAsync_ShouldDelegateAndReRead
```

改名为：

```text
UpdateAsync_ShouldReturnAcceptedReceiptWithoutReReading
ArchiveAsync_ShouldReturnAcceptedReceiptWithoutReReading
```

### 11.2 StudioTeamEndpointTests

修改：

```text
test/Aevatar.Studio.Tests/StudioTeamEndpointTests.cs
```

覆盖：

1. PATCH success -> `202 Accepted`。
2. archive success -> `202 Accepted`。
3. response body contains `scopeId` / `teamId` / `ackStage == accepted` / `acceptedAtUtc`。
4. dispatch path response body contains non-empty `commandId`；no-op path response body has `commandId == null`。
5. response body does not contain post-state fields (`displayName`, `description`, `lifecycleStage`, `memberCount`, `createdAt`, `updatedAt`)。
6. `Location` is built from normalized receipt values and URI-escaped path segments。
7. validation failures still -> 400。
8. existing fake service throwing `StudioTeamNotFoundException` can still map -> 404, but this no longer represents post-dispatch readmodel missing in production implementation。

### 11.3 ActorDispatchStudioTeamCommandServiceTests

修改：

```text
test/Aevatar.Studio.Tests/ActorDispatchStudioTeamCommandServiceTests.cs
```

覆盖：

1. update dispatch returns accepted receipt matching envelope id。
2. archive dispatch returns accepted receipt matching envelope id。
3. dispatched receipt `AcceptedAtUtc` equals envelope timestamp。
4. no-op patch returns accepted receipt with `CommandId == null` and dispatches no event。
5. no-op receipt `AckStage == accepted` does not imply actor commit or readmodel observation。

### 11.4 Route binding tests

Run existing route binding tests to ensure endpoint signatures still bind correctly:

```text
test/Aevatar.Studio.Tests/StudioTeamEndpointsRouteBindingTests.cs
```

## 12. 验证命令

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --nologo --filter "StudioTeamServiceTests|StudioTeamEndpointTests|ActorDispatchStudioTeamCommandServiceTests|StudioTeamEndpointsRouteBindingTests"
```

```bash
bash tools/ci/query_projection_priming_guard.sh
```

```bash
bash tools/ci/test_stability_guards.sh
```

```bash
git diff --check
```

如果测试改动涉及 wait/delay，不应使用 `Task.Delay` 做节奏；本设计不需要 delay。

## 13. 迁移步骤

1. 在 `TeamContracts.cs` 新增 accepted receipt model。
2. 修改 `IStudioTeamCommandPort` update/archive 返回 receipt。
3. 修改 `ActorDispatchStudioTeamCommandService` dispatch helper 返回 receipt。
4. 修改 `IStudioTeamService` update/archive 返回 receipt。
5. 修改 `StudioTeamService` 移除 dispatch 后 `GetAsync`。
6. 修改 `StudioTeamEndpoints` PATCH/archive 返回 202 accepted。
7. 更新相关 tests。
8. 跑 Studio team tests、projection priming guard、test stability guard 与 `git diff --check`。

## 14. Open questions

### 14.1 Create 是否也应返回 accepted receipt？

暂不改。

理由：#547 明确点名 update/archive dispatch 后读 readmodel 的问题；create 当前 command service 自己生成 team id 并返回基于 command input 构造的 summary，不依赖 readmodel post-read。它仍可能暗示 actor commit，但不是 #547 的主要 stale readmodel 问题。为控制范围，create 保持不变。

若后续要统一 team command ACK，可另开 issue 或扩展 #547 scope。

### 14.2 PATCH / archive 对不存在 team 是否还应同步 404？

最小实现不保证。

同步 404 必须来自 authoritative actor result 或明确 observed contract，不能来自 dispatch 后 readmodel lookup。#547 先移除不诚实 404；如果产品需要强 result，可设计 actor-owned command result continuation。

### 14.3 是否需要 command status endpoint？

不需要。

#547 只要求 ACK 诚实，不要求用户可观察每个 team command 的 terminal state。引入 status endpoint 会扩大到 async operation framework。

## 15. 完成标准

- `StudioTeamService.UpdateAsync` / `ArchiveAsync` 不再 dispatch 后调用 `GetAsync`。
- PATCH / archive endpoint 返回 `202 Accepted + StudioTeamCommandAcceptedResponse`。
- dispatch path response `CommandId` 匹配 envelope id；no-op patch response `CommandId == null`。
- response 不包含 team readmodel post-state 字段。
- stale/missing readmodel 不影响 successful dispatch ACK。
- tests 覆盖 no post-dispatch readmodel read。
- 不引入 polling、readiness endpoint 或 generic async operation framework。
