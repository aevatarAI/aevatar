# Implementation Report

## Scope

- Phase 9 issue #1535: typed NyxID channel-relay update failure classification.
- Changed only Channel/NyxID relay contracts, runtime propagation, adapter parsing, and matching tests.
- Scope extension: updated this report because PR 1608 reviewers identified the previous report as stale and mismatched to the actual diff.

## Changes

- Moved `FailureKind` into `channel_contracts.proto` so channel abstractions own the retry/terminal failure contract.
- Extended `EmitResult`, `ConversationStreamChunkResult`, `ConversationTurnResult`, and Nyx relay continuation payloads with typed failure kind, retry-after, HTTP status, and sanitized upstream error key/code fields.
- Normalized NyxID relay update failures at the external HTTP adapter boundary in `NyxIdApiClient`, including edit-unsupported, rate-limit, transient, permanent, and platform-unavailable classifications.
- Propagated typed diagnostics through `NyxIdRelayOutboundPort`, `ChannelConversationTurnRunner`, and `ConversationGAgent` so actor continuation policy no longer infers retry behavior from raw error summaries.
- Added refactor self-documentation comments on the changed contract/helper surfaces.

## Boundary Notes

- No external sibling repository changes are required; the implementation uses NyxID's existing error envelope surface.
- JSON parsing remains inside the NyxID HTTP adapter boundary only; internal propagation uses generated Protobuf contracts and typed C# records.
- No new actor, read model, projection pipeline, or query-time replay path was added.
- ACK semantics are unchanged; the added fields only classify already observed adapter failures.

## Verification

```bash
dotnet build aevatar.slnx --nologo 2>&1 | tail -20
```

Result: passed with existing warnings only.

```text
    138 个警告
    0 个错误

已用时间 00:00:38.01
```

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-build 2>&1 | tail -10
```

Result: passed.

```text
已通过! - 失败:     0，通过:   708，已跳过:     0，总计:   708，持续时间: 38 s - Aevatar.AI.Tests.dll (net10.0)
```

```bash
bash tools/ci/test_stability_guards.sh
```

Result: passed.

```text
Test stability guard passed (polling waits constrained by allowlist).
pytest: 6 passed in 66.21s (0:01:06)
```
⟦AI:AUTO-LOOP⟧
