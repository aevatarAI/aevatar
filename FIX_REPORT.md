# Fix report for PR 791 round 1

## Applied
- (A) `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs:11`: removed the private `ActorToolServiceDiscoveryCache` field and helper type; `ResolveTokenForServiceAsync` now checks NyxID `/proxy/services` live on each route decision instead of holding token-hash slug facts in a process-local dictionary (addresses reviewer:architect evidence #1).
- (A) `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServiceSpecCache.cs:50`: removed `_snapshots`, `SpecSnapshot`, and cache TTL state; connected-service spec hints fetch the current OpenAPI document per request through the named HTTP client instead of returning singleton process snapshots (addresses reviewer:architect evidence #2).
- (A) `agents/Aevatar.GAgents.NyxidChat/NyxIdRelayPromptConfiguration.cs:24`: added the required `Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache)` Old/New comment for the prompt-surface change (addresses reviewer:architect evidence #4).
- (A) `src/Aevatar.AI.ToolProviders.Ornn/OrnnSearchSkillsTool.cs:17`: added the required `Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache)` Old/New comment for the tool-description change (addresses reviewer:architect evidence #4).
- (A) `test/Aevatar.AI.Tests/ToolProviderHttpClientRegistrationTests.cs:34`: added a behavior test that builds DI, resolves `IAgentToolSource`, calls `DiscoverToolsAsync`, asserts `nyxid_proxy` is present, and asserts `nyxid_search_capabilities` / `nyxid_proxy_execute` plus deleted catalog/cache registrations stay absent (addresses reviewer:tests evidence #1 and #2).
- (A) `test/Aevatar.GAgents.ChannelRuntime.Tests/NyxIdProxyToolDualTokenTests.cs:97`: updated the proxy routing test to assert route decisions perform live NyxID discovery on each call, proving the deleted tool-instance cache cannot mask token/service ownership changes (addresses reviewer:architect evidence #1).
- (B) `test/Aevatar.AI.Tests/ConnectedServiceSpecCacheTests.cs:21`: SCOPE_EXTEND reason: existing touched test expectations encoded the removed process-local spec snapshot behavior; updated assertions to expect a live fetch on the second call.
- (B) `test/Aevatar.AI.Tests/ToolProviderHttpClientOwnershipTests.cs:106`: SCOPE_EXTEND reason: existing touched factory-client test encoded the removed spec snapshot behavior; updated assertions to expect a named factory client and HTTP request per spec fetch.

## Rejected as false positive
- None.

## Blocked (cannot fix this round)
- None.

## Build status
- build: pass (`dotnet build aevatar.slnx --nologo`; existing warnings only)
- tests: pass (`dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-build`: 592 passed; `dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --no-build`: 816 passed)
- guards: pass (`bash tools/ci/test_stability_guards.sh`; `bash tools/ci/architecture_guards.sh`; playground asset drift guard skipped by script because `pnpm` is not installed)

## Recommendation for next round
- expect unanimous

⟦AI:AUTO-LOOP⟧
