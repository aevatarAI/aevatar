# Channel sender service discovery and registration read implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development for bounded implementation and superpowers:requesting-code-review for independent review. The user approved the preceding incident analysis and requested implementation plus a commit.

**Goal:** A bound Channel sender can discover their actual NyxID services and read their own Aevatar Channel registrations without using the platform registration's account authority.

**Architecture:** Keep the existing Channel profile and the shared admitted tool executor. Use NyxID `/keys` as the executable inventory authority and distinguish malformed contracts from genuine empty results. A sender-specific read capability must use the verified binding, exact service identity, the existing connected-service operation contract, and the shared execution/audit boundary. It must not grant platform Agent Key operations the sender's authority.

**Tech Stack:** C#/.NET 10, Protobuf internal contracts, NyxID HTTP adapters, xUnit/FluentAssertions.

## Task 1: Correct executable inventory discovery

Files: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/NyxIdServiceInstanceClient.cs`, the inventory receipt factory and reader boundary, `agents/Aevatar.GAgents.NyxidChat/ChannelNyxIdConnectedServiceInventoryToolSource.cs`, and corresponding AI/ChannelRuntime tests.

- [x] Add a contract-faithful HTTP test that returns different bodies for `/keys` and `/user-services`; require successful sender discovery through `/keys`.
- [x] Add regression cases for malformed JSON, missing inventory envelope, missing readiness fields, and a genuine empty `keys` array. Malformed data must produce an explicit contract error; a genuine empty array remains successful.
- [x] Run the new tests and confirm failures before changing production code.
- [x] Change both user and organization discovery calls from `ListUserServicesAsync` to `ListServicesAsync`. Preserve strict readiness validation and authority checks.
- [x] Propagate a typed inventory contract failure to a distinct tool receipt error. Never include response bodies or credentials in that failure.
- [x] Fix fixtures that currently provide `/keys` data at `/user-services` and run the affected inventory and connected-service tests.

## Task 2: Admit the sender's own registration read

Files: Channel sender capability sources in `agents/Aevatar.GAgents.NyxidChat/`, the identity capability port/broker as needed, Channel tool-set composition, and ChannelRuntime integration tests.

- [x] Trace the bound-sender capability through discovery, admission, execution, and the final Channel tool catalog; verify the published NyxID contracts in the pinned external source.
- [x] Add an integration test using a registration Agent Key and a different bound sender. The actual Channel catalog must expose the sender-authorized registration read, and execution must reach `GET /api/channels/registrations` under sender authority.
- [x] Cover unbound senders, revoked bindings, caller-supplied account/scope/header overrides, missing or drifted exact service/operation identities, and platform credential isolation.
- [x] Confirm the regressions fail, then add only the narrowly required read capability using the shared execution and audit path. Keep user tokens request-local and avoid persistent token storage or process-wide state.
- [x] Verify the Skill can discover this callable from its actual schema and description. Do not change merchant-target eligibility rules or create registrations during discovery.

## Task 3: Review, document, and commit

- [x] Update the canonical connected-service/Channel documentation with the authority boundary, error semantics, and a compact diagram.
- [x] Run the relevant AI and ChannelRuntime tests, `bash tools/ci/test_stability_guards.sh`, `bash tools/ci/query_projection_priming_guard.sh`, documentation lint, and applicable architecture guards.
- [x] Request independent contract and security review; fix material findings and rerun affected checks.
- [x] Stage only this repair's files and commit with an imperative message. Leave production verification explicitly pending deployment; do not mutate production pods or push without a user request.


## Implementation and validation record

The implementation uses the existing system Channel tool set and shared admitted executor. It adds a sender-only, empty-argument registration read whose discovery and execution each exchange the verified binding for a request-local capability. Catalog filtering occurs before any remote OpenAPI fetch, and the inner call preserves exact service identity, credential separation, contract revalidation, and independent audit completion.

Contract verification also reproduced two published-document blockers. The generated Mainnet OpenAPI was 667,546 bytes before the response declaration fix, exceeding the previous 128 KiB transport bound. The bound is now 1 MiB, with an over-limit rejection regression. The registration-list endpoint now declares its existing JSON-array success response; the real generated Mainnet document is used in a discovery integration test.

Inventory regression tests first reproduced the incorrect `/user-services` route and silent empty results. The final adapter reads `/keys` for user and organization credentials and distinguishes invalid envelopes/readiness from valid empty inventory with `NYXID_SERVICE_INVENTORY_CONTRACT_INVALID`. Existing Agent Key transport behavior was retained; a stale test expecting `X-API-Key` was corrected to the supported Bearer Agent Key contract, verified against NyxID commit `47f2e0086c6c2117f644f8559d871a01d2a61982`.

Final validation from the isolated repair worktree:

| Command | Result |
|---|---|
| `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo -m:1` | 3,590 passed; 0 failed |
| `dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --nologo -m:1` | 2,545 passed; 0 failed |
| `dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --no-restore --nologo -m:1 --filter 'FullyQualifiedName~MainnetHostCompositionTests\|FullyQualifiedName~MainnetSettingsEndpointSecurityTests'` | 72 passed; 0 failed |
| `bash tools/ci/test_stability_guards.sh` | Passed |
| `bash tools/ci/query_projection_priming_guard.sh` | Passed |
| `bash tools/ci/architecture_guards.sh` | Passed, including documentation lint |
| `git diff --check` | Passed |

The independent review findings were fixed: unrelated service contracts are no longer fetched by the sender read, the documentation states the correct runtime guarantee, and an incomplete inner audit cannot become outer success. Follow-up review reported no remaining actionable findings in that scope.

The tests above build the changed projects and their dependencies; they are not a whole-solution test claim. Existing generated-code and dependency warnings remain. Production OpenAPI verification through the official NyxID CLI failed with TLS handshake EOF after one retry and a doctor check. Local TestServer contract verification passed. Production Telegram behavior still requires verification after the repair is deployed through the normal source-delivery workflow; this task does not push or deploy the commit.
