# Channel default skill routing repair

> **For agentic workers:** Use superpowers:executing-plans to implement and verify these steps in the current task.

**Goal:** A normal message to a registration with a configured default skill loads that skill using the registration's Channel Agent Key, including when the sender has no NyxID binding.

**Architecture:** Keep the existing Channel runner, protobuf tool context, skill recovery planner, `use_skill`, and streaming reply pipeline. Distinguish a registration default from an explicit sender invocation with a typed field. Registration authority is limited to the exact configured primary skill; an unavailable selected credential fails without falling back to another authority. Unbound default turns expose only `use_skill` within the existing tool visibility ceiling.

**Tech Stack:** C#, Protobuf, xUnit, FluentAssertions, NyxID proxy, ISecretVault.

## Contracts and implementation

- [ ] Complete the existing regression fixtures in `ChannelConversationTurnRunnerTests.cs` and `ChannelRemoteSkillAccessTokenResolverTests.cs`, then confirm the current implementation fails them.
- [ ] Add `bool from_channel_default_skill_binding = 11` to `AgentSkillRecoveryContextPayload` and field `9` to `AgentSkillRecoveryCheckpointPayload` in `src/Aevatar.AI.Abstractions/ai_messages.proto`. Append `bool FromChannelDefaultSkillBinding = false` to the C# record and preserve it through both protobuf mapping paths. Test transport and checkpoint round trips.
- [ ] In `ChannelConversationTurnRunner`, allow registration default routing independently of sender binding, while keeping explicit triggers gated. Set the typed flag from the resolved trigger source.
- [ ] In `ChannelRemoteSkillAccessTokenResolver`, select registration authority only for a marked default invocation whose requested skill exactly matches `PrimarySkillName`. Validate execution owner, registration identity, vault purpose, scope, subject and descriptor. Resolve only through `ISecretVault`; never copy the key into durable context or logs. Keep sender resolution for other invocations.
- [ ] In `ConversationReplyGenerator`, permit an unbound default invocation to enter skill recovery, intersect its tool visibility with `use_skill`, and preserve explicit force-disable and profile catalog restrictions. Verify with the real skill loader and a deterministic streaming provider.
- [ ] Update `docs/canon/aevatar-channel-architecture.md` to explain the changed default-skill authority, failure behavior and single execution path.

## Verification

- [ ] Run focused Channel runtime tests for routing, remote credential selection, reply generation, and the agent-run execution path.
- [ ] Run AI context/checkpoint tests for persistence of the new authority-source field.
- [ ] Run `dotnet build aevatar.slnx --nologo` and relevant test projects.
- [ ] Run `bash tools/ci/test_stability_guards.sh`, `bash tools/ci/architecture_guards.sh`, and document lint.
- [ ] Review the final diff and report local results separately from production deployment and Telegram acceptance.

## Evidence and scope

The source task recorded registration `c8389d951ca1498ca0145e535c161949` with `default_skill_name=test-default-skill`, Ornn and LLM services allowed, and a Telegram activity whose log reported `senderBindingFound=False` and no skill recovery. Local inspection found three gates: route construction, remote credential resolution, and reply tool availability. The workspace already contained unfinished routing and resolver regression tests; this task completes that work without removing unrelated changes.

NyxID's API-key middleware derives the proxy user and allowed services from the key; its proxy rejects services outside the exact grant. Ornn applies its own skill-read ACL to the forwarded identity. No external repository changes or production pod operations are part of this repair.
