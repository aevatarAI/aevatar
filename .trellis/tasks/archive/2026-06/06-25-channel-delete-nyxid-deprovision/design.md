# Design — NyxID deprovision on registration delete

## Current code (verified)
- Endpoint: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelCallbackEndpoints.cs`
  - `MapDelete("/registrations/{registrationId}", HandleDeleteRegistrationAsync)` (line ~34).
  - `HandleDeleteRegistrationAsync(string registrationId, ChannelRegistrationCommandFacade commandFacade,
    IChannelBotRegistrationQueryPort queryPort, CancellationToken ct)` (line ~321): existence check via
    `queryPort.GetAsync` → `commandFacade.UnregisterAsync(registrationId, ct)` → `Ok({status="deleted"})`.
  - Bearer extraction helper already present: `ResolveBearerAccessToken(HttpContext http)` (line ~311),
    used by register/other handlers that take `HttpContext http`.
- Local unregister path (KEEP unchanged): `ChannelRegistrationCommandFacade.UnregisterAsync` →
  `ChannelBotUnregisterCommand` → `ChannelBotRegistrationGAgent.HandleUnregister` (tombstone only).
- NyxID client (`src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`) already exposes:
  `DeleteConversationRouteAsync` (611), `DeleteChannelBotAsync` (591), `DeleteApiKeyAsync` (442).
  These return the raw response string; HTTP-level helpers (`DeleteAsync`) — confirm how non-2xx /
  404 surface (exception vs status in body) and key the 404=success handling off that.
- Registration entry/document carries the ids to delete:
  `nyx_conversation_route_id`, `nyx_channel_bot_id`, `nyx_agent_api_key_id`
  (`protos/channel_bot_registration.proto`; exposed on the query document the manage UI already reads).
- Register-side provisioning reference for symmetry + rollback idiom:
  `NyxLarkProvisioningService.cs` (`ProvisionAsync`, rollback `TryRollbackAsync(() => DeleteChannelBotAsync(...))`
  at line ~208) and `NyxTelegramProvisioningService.cs`.

## Approach: platform-neutral deprovision service, endpoint orchestrates (mirror of register)
Register orchestration = provision on NyxID (provisioning service) THEN write local mirror.
Deprovision is the reverse: delete on NyxID THEN tombstone local mirror. The endpoint owns the
orchestration (it already calls `UnregisterAsync`); we add a NyxID-only teardown call before it.
**No NyxID call inside the actor** (keeps the actor a pure local fact owner; CLAUDE.md layering).

### New: `INyxChannelBotDeprovisioningService` + `NyxChannelBotDeprovisioningService`
File: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxChannelBotDeprovisioningService.cs`.
Platform-neutral (delete by id — no per-platform branch, so one impl serves lark + telegram).

```
public sealed record NyxChannelBotDeprovisioningResult(
    bool ChannelBotRemoved,          // true if deleted OR already-gone (404)
    bool Succeeded,                  // ChannelBotRemoved == true (gate for local tombstone)
    IReadOnlyList<string> Warnings); // residual route/api-key cleanup failures (best-effort)

public interface INyxChannelBotDeprovisioningService
{
    Task<NyxChannelBotDeprovisioningResult> DeprovisionAsync(
        string accessToken, string? conversationRouteId, string? channelBotId, string? apiKeyId,
        CancellationToken ct);
}
```

Impl (inject the same NyxID client the provisioning services use — confirm the interface type,
e.g. `INyxIdApiClient`/`NyxIdApiClient`):
- Order = reverse of creation: **route → channel-bot → api-key**.
- Each delete wrapped so **404 / not-found counts as success** (reuse `NyxApiResponseHelper`
  idioms if available; otherwise catch the not-found signal). Skip blank ids.
- **channel-bot** result is authoritative → sets `ChannelBotRemoved`/`Succeeded`.
- route + api-key are best-effort: on failure append a `Warnings` entry, do not fail the result.
- Never throw for an expected not-found; only a hard channel-bot failure makes `Succeeded=false`.

### Endpoint change: `HandleDeleteRegistrationAsync`
- Add params `HttpContext http` and `[FromServices] INyxChannelBotDeprovisioningService deprovision`.
- After the existence check (keep), extract `accessToken = ResolveBearerAccessToken(http)`
  (401 if missing). Read `nyx_conversation_route_id` / `nyx_channel_bot_id` / `nyx_agent_api_key_id`
  from the looked-up registration.
- `var result = await deprovision.DeprovisionAsync(token, routeId, channelBotId, apiKeyId, ct);`
- If `!result.Succeeded` (hard channel-bot failure) → return non-2xx (e.g. `502` / problem) and
  **do not** call `UnregisterAsync`.
- Else → `await commandFacade.UnregisterAsync(registrationId, ct)`; return
  `Ok({ status = "deleted", warnings = result.Warnings })`.

### DI
Register the service in `DependencyInjection/NyxIdRelayChannelServiceCollectionExtensions.cs`
(`TryAddSingleton<INyxChannelBotDeprovisioningService, NyxChannelBotDeprovisioningService>()`),
next to the provisioning-service registrations. Singleton (stateless, deps singleton — like the
provisioning services / scope resolver).

## Failure / consistency contract
- NyxID-first, tombstone-second → no window where local is gone but NyxID lingers *silently*
  (a hard failure surfaces as an error and the local entry stays, so the row remains visible and
  retryable).
- 404 idempotent → re-clicking delete, or deleting a manually-cleaned bot, still succeeds.
- Residual route/api-key leak (best-effort) is surfaced as a warning, not hidden — honest ACK.

## Risks / notes
- Confirm `NyxIdApiClient` non-2xx behavior (throw vs return) so 404=success is keyed correctly;
  the provisioning rollback (`TryRollbackAsync`) shows the established idiom — match it.
- Admin deleting a *foreign* registration: NyxID delete will fail owner-scope → by the contract
  that blocks the tombstone. Acceptable (admin shouldn't silently orphan someone else's bot); if a
  pure-local admin purge is later wanted, that's a separate explicit path. Note in code comment.
- Do NOT add a new actor / envelope / projection. Local tombstone stays on the existing command skeleton.
```
