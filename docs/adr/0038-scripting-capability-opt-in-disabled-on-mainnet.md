---
title: "Scripting Capability Is Opt-In and Disabled on Mainnet"
status: accepted
owner: eanzhao
---

# Scripting Capability Is Opt-In and Disabled on Mainnet

## Context

The scripting capability (`Aevatar.Scripting.*`) compiles and executes
tenant-supplied C# in-process via Roslyn. Its runtime surface spans the
`/api/scripts/*` evolution endpoints, the `/api/scopes/{scopeId}/scripts`
CRUD endpoints, script-implemented services (binding, activation, invocation,
chat stream), Studio script authoring previews, and the `script_*` LLM agent
tools. A security review found this execution model unacceptable for the
mainnet host: it runs arbitrary tenant code inside the production process.

Before this decision, scripting was composed by default
(`AevatarPlatformCompositionOptions.EnableScriptingCapability = true`) and —
independently of that option — `AddGAgentServiceCapability` pulled
`AddScriptCapability` back in whenever the marker was absent, so no host could
actually opt out.

## Decision

1. Scripting is an explicit host opt-in.
   `AevatarPlatformCompositionOptions.EnableScriptingCapability` defaults to
   `false`; only a host that sets it to `true` composes the scripting bundle
   and the scripting LLM tools.
2. The mainnet host (`Aevatar.Mainnet.Host.Api`) pins
   `EnableScriptingCapability = false` as a stated invariant. A future change
   to the platform default cannot silently re-enable scripting there.
3. `AddGAgentServiceCapability` never composes scripting itself. It bridges to
   the scripting capability (implementation adapter, revision republish hook,
   scope script ports, script service run interaction, scope script endpoints,
   health probe route) only when `ScriptCapabilityRegistrationsMarker` is
   already registered by the host.
4. Components that serve non-scripting traffic but referenced scripting ports
   (`ScopeBindingCommandApplicationService`, `DefaultServiceRuntimeActivator`,
   `DefaultServiceInvocationDispatcher`, scope service chat-stream endpoints,
   Studio authoring preview) treat those ports as optional dependencies and
   fail closed with an explicit "Scripting capability is not enabled on this
   host" error when a scripting-kind request arrives.
5. The scripting source projects stay in the repository. Removal is a
   composition decision, not a code deletion; a host that accepts the risk can
   still opt in (tests and local development do so explicitly).

## Consequences

- Mainnet no longer registers scripting actors, endpoints, tools, projections,
  or hooks; script-kind service bindings, activations, and invocations are
  rejected with an explicit error instead of executing tenant C#.
- Ornn skill publishing rejects script assets on hosts without scripting
  (`missing_script_compiler` diagnostic), and `use_skill` handoffs to
  `script_compile`/`script_execute` reference tools that no longer exist.
- Hosts and tests that need scripting must compose `AddScriptCapability`
  (or set `EnableScriptingCapability = true`) before
  `AddGAgentServiceCapabilityBundle`.
- Existing scripting data (event streams, read models) is untouched; without
  registered actor kinds and projections it is unreachable at runtime.
