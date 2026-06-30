# Implement — NyxID deprovision on registration delete

## Preconditions
- Branch `feature/integrate` (auto-deploys). Build/test locally only; do NOT commit (the parent
  session handles the isolated-worktree push). Local Mainnet host can't boot → verify by build + unit tests.

## Ordered steps
1. **Confirm NyxID client contract.** Read `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
   around `DeleteAsync` / `DeleteChannelBotAsync` (591) / `DeleteApiKeyAsync` (442) /
   `DeleteConversationRouteAsync` (611) and `NyxApiResponseHelper`. Determine how a 404 / non-2xx
   surfaces (exception vs status in returned body) so "404 = already gone = success" is keyed correctly.
   Confirm the client interface type injected into `NyxLarkProvisioningService` (field `_nyxClient`).

2. **Add deprovision service.** New file
   `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxChannelBotDeprovisioningService.cs`:
   `INyxChannelBotDeprovisioningService` + `NyxChannelBotDeprovisioningService` +
   `NyxChannelBotDeprovisioningResult` per design.md. Inject the NyxID client. Delete order
   route → channel-bot → api-key; skip blank ids; 404 = success; channel-bot authoritative;
   route/api-key best-effort → `Warnings`. Match the `TryRollbackAsync` / `NyxApiResponseHelper`
   idiom from `NyxLarkProvisioningService`. No `catch (Exception)` blanket — catch the specific
   not-found / API-error type the client raises.

3. **Register DI** in
   `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/DependencyInjection/NyxIdRelayChannelServiceCollectionExtensions.cs`
   — `TryAddSingleton<INyxChannelBotDeprovisioningService, NyxChannelBotDeprovisioningService>()`.

4. **Wire the endpoint.** In `ChannelCallbackEndpoints.cs` `HandleDeleteRegistrationAsync`:
   add `HttpContext http` + `[FromServices] INyxChannelBotDeprovisioningService deprovision`;
   extract bearer via `ResolveBearerAccessToken(http)` (401 if null); read the 3 nyx ids from the
   looked-up registration; call `DeprovisionAsync`; if `!Succeeded` return `502`/problem WITHOUT
   tombstoning; else `UnregisterAsync` then `Ok({status="deleted", warnings})`. Update the comment
   block to reflect NyxID teardown + local tombstone (drop the now-stale "query + unregister only" note).

5. **Tests** in `test/Aevatar.GAgents.ChannelRuntime.Tests/` (extend `ChannelCallbackEndpointsTests`
   or add a focused file). Use a fake `INyxChannelBotDeprovisioningService` for endpoint tests and/or
   a fake NyxID client for the service unit tests. Cover the acceptance criteria:
   - happy path: deprovision invoked with route/channel-bot/api-key ids, then unregister; 200 + status="deleted".
   - 404 on a resource → treated as success.
   - hard channel-bot failure → non-2xx AND unregister NOT called (assert the command facade/unregister was not invoked).
   - route/api-key residual failure → unregister STILL called, warnings surfaced.
   - both `lark` and `telegram` registrations route through the same path.
   Follow repo test stack (xUnit + FluentAssertions); no `Task.Delay`/`[Skip]`.

## Validation (run before reporting done)
- `dotnet build agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/Aevatar.GAgents.Channel.NyxIdRelay.csproj --nologo` → 0 errors
  (also build `agents/Aevatar.GAgents.Channel.Runtime` if touched).
- `dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo` → green.
- `bash tools/ci/architecture_guards.sh` and `bash tools/ci/test_stability_guards.sh` → green
  (watch the catch-observability guard if any new catch is added — keep specific catch + log).

## Review gates
- No NyxID repo change; only existing `NyxIdApiClient` methods.
- No new actor/envelope/projection; local tombstone unchanged on the command skeleton.
- Honest failure contract: hard channel-bot failure does NOT tombstone; residuals surfaced as warnings.
- Naming: `INyxChannelBotDeprovisioningService` (business semantics, not a generic store/helper).

## Rollback
Single cohesive change across ~3 files + tests. If wrong, revert the endpoint wiring (restore the
2-line `HandleDeleteRegistrationAsync`) and drop the new service file — local tombstone behavior
returns to today's.
