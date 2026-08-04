---
title: "LLM Selection and Workflow Invocation Admission"
status: approved-for-written-review
owner: eanzhao
date: 2026-08-01
---

# LLM Selection and Workflow Invocation Admission

## Context

A read-only production review of recent failed and timed-out workflow runs found two
platform-level failure classes in addition to workflow-specific authoring errors:

1. The persisted LLM route and model can describe different choices. Generic user
   configuration and channel commands can update DefaultModel without atomically
   committing the route whose catalog made that model valid. An empty model list is
   also ambiguous: it may mean that the provider publishes no catalog, that the
   provider is unavailable, or that the route accepts no models.
2. Persisted workflows authored against retired direct NyxID tool names can reach run
   provisioning. WorkflowRunActorPort may resolve or create a definition actor and
   create a run actor before the system proves that the stored YAML and capability
   admission plan are locally compatible. This creates runs that cannot succeed and
   makes scheduled invocation failures harder to classify.

These are system bugs. A malformed workflow remains a workflow defect, but the system
must reject it before allocating runtime state and must return a typed repair action.
Provider unavailability is an external operational condition, but the platform must
represent it honestly instead of inventing a fallback.

## Goals

- Make LLM route identity and model selection one actor-owned business fact.
- Distinguish an enumerated model catalog from an unverifiable or unavailable catalog.
- Keep interactive use practical while making unattended durable execution fail closed.
- Preserve exact NyxID UserService.id identity; never infer identity from a route, slug,
  display name, model prefix, or route position.
- Preserve Gateway diagnostics through catalog composition and expose actionable
  readiness.
- Reject incompatible persisted workflow artifacts before creating any definition or
  run actor.
- Reuse the existing workflow parser, authorization dependency evaluator, and
  WorkflowCapabilityAdmissionPlanIntegrity rules instead of creating a second
  validation system.
- Keep invocation admission local, deterministic, runtime-neutral, and free of
  query-time replay, repair, or external catalog reads.
- Give settings users and schedule operators stable, safe errors with a direct repair
  action.

## Non-Goals

- Do not correlate a schedule fire with the terminal result of the workflow run in this
  change.
- Do not redesign the lifecycle of permanently unknown runs.
- Do not automatically migrate production UserConfig or workflow artifacts.
- Do not rerun, pause, delete, or mutate production workflows or schedules as part of
  implementation.
- Do not add a general model registry or accept arbitrary model identifiers. A future
  provider may explicitly declare an OpenIdentifier capability in a separate change.
- Do not call RevalidatePersistedAsync for every invocation.
- Do not redesign unrelated workflow authoring, provider provisioning, or schedule
  orchestration.

## Decisions Considered

### Chosen: atomic selection plus local invocation preflight

Persist one typed LLM selection containing both route identity and model choice. Model
catalog certainty is a separate typed property of a catalog option. Before any workflow
actor lifecycle action, parse the root and inline YAML and validate the persisted
capability admission plan using only local committed inputs.

This fixes both root causes at their shared boundaries. Every settings surface follows
one write contract, and every run provisioning path crosses one compatibility gate.

### Rejected: repair at runtime

Looking up a current route or model when a run starts would make the same persisted
artifact execute differently over time. It would also introduce external I/O into an
actor lifecycle path and turn temporary catalog availability into authoritative state.

### Rejected: silently fall back to Gateway

Gateway may cross a different provider, data-processing, cost, residency, and policy
boundary. Replacing a saved service with Gateway is not a harmless availability
fallback. It must be a user or system-default decision expressed by a typed selection.

### Rejected: full persisted-admission revalidation on every invocation

RevalidatePersistedAsync checks source freshness as well as structural integrity. A
long-running schedule would eventually fail merely because its admission evidence aged,
even when its committed artifact remained structurally valid. Freshness belongs to
publish, bind, authorize, reauthorize, and explicit repair flows; invocation admission
checks compatibility only.

## Authoritative LLM Selection

### Semantic owner

UserConfigGAgent remains the single authority for one owner-scope or channel-binding
LLM selection. Existing resource-key isolation remains unchanged:

- owner schedule evidence reads UserConfigResourceKey.ForOwnerScope(scopeId);
- channel model commands write UserConfigResourceKey.ForChannelBinding(bindingId);
- neither key is an alias for the other.

The committed selection is a single typed fact. It contains route identity and model
selection; there is no independent authoritative DefaultModel write.

### Typed contract

Add the shared route/model semantics to a new LLM selection protobuf in
Aevatar.AI.Abstractions:

    enum LLMRouteKind {
      LLM_ROUTE_KIND_UNSPECIFIED = 0;
      LLM_ROUTE_KIND_GATEWAY = 1;
      LLM_ROUTE_KIND_NYX_ID_USER_SERVICE = 2;
    }

    enum LLMModelSelectionKind {
      LLM_MODEL_SELECTION_KIND_UNSPECIFIED = 0;
      LLM_MODEL_SELECTION_KIND_PROVIDER_DEFAULT = 1;
      LLM_MODEL_SELECTION_KIND_EXPLICIT_MODEL = 2;
    }

    message LLMModelSelection {
      LLMModelSelectionKind kind = 1;
      string model_id = 2;
    }

    enum LLMModelCatalogCertainty {
      LLM_MODEL_CATALOG_CERTAINTY_UNSPECIFIED = 0;
      LLM_MODEL_CATALOG_CERTAINTY_ENUMERATED = 1;
      LLM_MODEL_CATALOG_CERTAINTY_NOT_VERIFIABLE = 2;
      LLM_MODEL_CATALOG_CERTAINTY_UNAVAILABLE = 3;
    }

    enum LLMModelCatalogDiagnosticKind {
      LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_UNSPECIFIED = 0;
      LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_NOT_PUBLISHED = 1;
      LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_ROUTE_NOT_READY = 2;
      LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_ACCESS_DENIED = 3;
      LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_OBSERVATION_UNAVAILABLE = 4;
      LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_RESPONSE_INVALID = 5;
      LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_RESPONSE_TOO_LARGE = 6;
      LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_PATTERN_ONLY = 7;
    }

    message LLMModelCatalog {
      LLMModelCatalogCertainty certainty = 1;
      repeated string model_ids = 2;
      string default_model_id = 3;
      LLMModelCatalogDiagnosticKind diagnostic_kind = 4;
    }

    message LLMSelection {
      LLMRouteKind route_kind = 1;
      string route_value = 2;
      string nyx_id_user_service_id = 3;
      string service_slug_snapshot = 4;
      LLMModelSelection model_selection = 5;
    }

Replace the existing UserLlmSelection generated type with the shared LLMSelection at
the same UserConfig state/event field numbers. Its first four fields retain the current
wire numbers and numeric route-kind values, so historical typed route selections still
deserialize. They have no model_selection and are therefore legacy/incomplete until an
explicit reselection; the old default_model string is never merged into them.

The UserConfig command reserves the removed default_model field number/name and accepts
only the complete shared selection for an LLM change:

    message UpdateUserConfigCommand {
      reserved 1;
      reserved "default_model";
      aevatar.ai.LLMSelection llm_selection = 2;
      // Existing non-LLM delta fields keep their current numbers.
    }

The corresponding Application value is equally typed. These invariants are mandatory:

| Model selection | Required value | Meaning |
| --- | --- | --- |
| Unspecified | empty model_id | No user model decision exists. |
| ProviderDefault | empty model_id | Use the selected route provider default. |
| ExplicitModel | non-empty canonical model_id | Use exactly this verified model. |

LLMRouteKind.Unspecified has an empty route, no service identity, and an
Unspecified model selection. Gateway and exact NyxID user-service route invariants from
the existing exact-service-identity design remain in force.

Route and model presence are coupled:

- Unspecified route requires Unspecified model selection;
- Gateway or NyxID user-service route requires ProviderDefault or ExplicitModel;
- ExplicitModel requires one non-empty canonical model ID;
- ProviderDefault and Unspecified require an empty model ID.

The persisted shared selection belongs to UserConfig because it records the user's
decision, not because UserConfig owns the provider catalog. Route, model selection, and
catalog-certainty enums live in the lowest existing common LLM contract layer and are
imported by UserConfig and scheduled-authorization protobufs. The schedule plan keeps
its narrower authorization snapshot with one explicit model string, but uses the common
route kind and an explicit mapper. Studio, scheduling, and runtime do not define
independent enums with subtly different meanings.

No bag or string flag carries model semantics. Protobuf fields are the internal source
of truth, and committed state continues through the normal projection pipeline.

### Atomic write boundary

All LLM mutations use /api/user-config/llm or the equivalent channel preference port.
The write command always resolves and submits one complete UserLlmSelection:

- service selection: exact inventory UserService.id plus ProviderDefault or one verified
  ExplicitModel;
- Gateway selection: canonical Gateway route plus ProviderDefault or one verified
  ExplicitModel;
- model change: the caller submits the exact route identity and new model as one
  selection, and the writer resolves that pair against the authenticated catalog;
- Reset: submit route kind Unspecified and model selection Unspecified.

The generic PUT /api/user-config contract removes defaultModel from mutation input. It
remains responsible only for non-LLM settings. UserLlmPreferenceWriter remains the
shared application write boundary used by Console settings and channel selection.

PUT /api/user-config/llm accepts exactly one typed intent:

- Reset;
- select Gateway plus one model selection;
- select an exact userServiceId plus one model selection;
- activate one preset.

It does not accept routeValue, display name, slug, or a bare model string. The writer
derives the canonical route from Gateway or the exact authenticated inventory identity.
The JSON boundary uses a required action discriminator with exactly one matching
payload: reset, select_gateway, select_user_service, or activate_preset. Unknown actions,
missing payloads, fields belonging to another action, duplicate identities, and unknown
enum values are rejected before catalog access. The Application command is a closed
typed union, not one record with combinable nullable fields.

Omitting a model from channel service selection means ProviderDefault; it never promotes
the first or advertised default model into an ExplicitModel. A preset model becomes an
ExplicitModel only when the exact route catalog enumerates it; otherwise activation is
rejected or uses ProviderDefault when the preset declares no model.

Authoritative writes call a new GetFreshServicesAsync operation on the existing catalog
port and bypass the current stale-while-revalidate cache. The settings read view may use
the bounded cache as a presentation hint, but a stale cached option cannot authorize a
new committed selection. The direct SaveSelectedOptionAsync shortcut is removed; every
Console, slash, and card write passes its typed intent and authenticated bearer through
UserLlmPreferenceWriter.

The actor validates the complete typed selection and commits one full-state event. A
caller cannot independently set DefaultModel, preferred_llm_route, or only the model
sub-message. Application code does not read an eventually consistent UserConfig
document and merge it into a write.

Actor validation covers structural invariants only. Catalog ownership stays outside the
actor: UserLlmPreferenceWriter validates the authenticated current catalog before
dispatch, while later durable authorization validates a committed catalog snapshot
again. An accepted settings receipt does not prove actor commit or provider availability;
only the matching committed current-state projection proves that the selection became
active.

The current UserConfigSaveReceipt promises only accepted-for-dispatch plus stable
command, actor, and correlation IDs. Settings and channel copy says “selection update
submitted,” not “saved” or “active.” The client retains the submitted form value while
the current-state read model catches up and marks it active only after a later GET
returns the same typed selection. A timeout remains pending/retryable; it is not
reported as success. This change does not add query-time projection priming or a
synchronous committed ACK.

Console model changes submit the exact selected route identity already present in the
form. Channel /model use <service> [model] resolves a concrete option and commits that
option and model together. The ambiguous /model use <model-only> form is removed; it
returns a usage/list hint and never reads UserConfig to assemble a write. This keeps
write-side merge inside the actor while ensuring every caller expresses its whole
route/model decision.

Channel card actions follow the same rule. Select-service and preset actions resolve a
complete selection. Any legacy model-only action is rejected with a refresh/list hint;
it cannot call SetModelOverrideAsync. The model-only method is removed from the channel
selection interface so a future card or slash handler cannot reintroduce the split
write.

### Compatibility fields

Existing protobuf fields default_model and preferred_llm_route remain readable for
historical state and wire compatibility during this change. Their role is narrow:

- new committed events derive both fields from the typed selection;
- read APIs may expose them as compatibility views derived from the typed selection;
- a legacy document with no typed selection may expose the old values in a separately
  labelled legacy repair view;
- runtime routing, schedule authorization, catalog matching, and new writes must not
  treat either field as authoritative;
- no code may combine a legacy route with a newly written model or infer an exact
  service identity from either field.

For ProviderDefault and Unspecified, the compatibility default_model view is empty. For
ExplicitModel, it is the canonical model_id. The fields can be removed only after
legacy state and external consumers have been audited in a later change.

This Reset decision supersedes the earlier exact-service-identity design statement that
Reset writes Gateway. Reset now means restore system default, which is Unspecified. An
explicit Gateway choice remains a distinct user selection.

Non-LLM updates preserve legacy fields byte-for-byte. Only a command containing a new
typed LLM selection derives and replaces the compatibility fields. This prevents an
unrelated runtime or GitHub setting update from silently migrating or erasing legacy LLM
state.

## Model Catalog Certainty

### Strongly typed certainty

An empty list cannot carry catalog semantics. Add a typed certainty value to each
routable option and propagate it through NyxID parsing, inventory composition,
Application contracts, settings views, and channel views:

| Certainty | Required data | Meaning |
| --- | --- | --- |
| Enumerated | non-empty models, optional default in models | Provider published a verifiable catalog. |
| NotVerifiable | no selectable model IDs | Route is ready, but no verifiable catalog exists. |
| Unavailable | typed readiness reason | Route or provider is not ready or allowed. |

Use the shared LLMModelCatalog value plus existing typed route readiness on the option
contracts. API wire strings are presentation values only. Required invariants are:

- Enumerated has a normalized, de-duplicated model list. Its default model, when
  present, is a member of that list.
- NotVerifiable means the route is ready but the provider did not publish a catalog that
  Aevatar can verify. It is not an empty enumeration.
- Unavailable cannot be selected for a new write.
- an empty upstream list without an explicit capability maps to NotVerifiable, never to
  accept any model.
- concrete model IDs are compared with ordinal equality, are trimmed, contain no control
  characters, and are at most 256 UTF-8 bytes;
- one route observation contains at most 2,048 distinct model IDs. A larger, malformed,
  wildcard-only, or pattern-only response is NotVerifiable and cannot authorize durable
  execution.

The Host adapter maps external responses into the typed diagnostic enum. A missing or
valid empty models surface is NotVerifiable with NotPublished; pattern-only is
NotVerifiable with PatternOnly; invalid or oversized successful responses are
NotVerifiable with ResponseInvalid or ResponseTooLarge. Authentication denial makes the
target Unavailable. Transport failure or timeout does not commit a negative catalog
fact; the read or refresh operation returns VerificationUnavailable and preserves the
previous committed snapshot until its existing freshness expires.

If a provider later supports arbitrary model IDs, it must declare a distinct typed
OpenIdentifier capability. No current option receives that capability by inference.

### Interaction matrix

| Catalog certainty | Interactive selection | Durable Workflow or Schedule |
| --- | --- | --- |
| Enumerated | ProviderDefault or a listed ExplicitModel | Requires a listed ExplicitModel. |
| NotVerifiable | ProviderDefault only | Rejected; model target is not verifiable. |
| Unavailable | Cannot save a new selection; show repair | Rejected. |

An explicit model is compared by exact canonical ID within the exact route option. A
model present on another route is not accepted. Display name, slug, prefix, and fuzzy
matching remain presentation conveniences only and cannot enter the authoritative
write.

### Gateway composition and readiness

NyxIdLlmServiceCatalogParser.ComposeUserServiceInventory currently replaces the
diagnostic service list with inventory-backed user services. The composed result must
retain Gateway provider diagnostics in addition to exact inventory-backed options.
Gateway readiness, allowed state, catalog certainty, models, default model, and setup
diagnostics survive composition.

Absence of Gateway diagnostics no longer means ready. The Gateway option is:

- ready only when an explicit Gateway provider diagnostic is ready and allowed;
- unavailable when the provider is missing, disconnected, denied, or reports an error;
- disabled in settings when unavailable, with the existing setup/retry affordance or a
  provider connection action.

Reset does not select Gateway. It restores the system default by writing Unspecified.
Interactive runtime may apply its configured default for an unspecified selection, but
the settings view labels this as System default; it does not claim the user selected
Gateway.

When a previously saved route becomes unavailable, the read view preserves its exact
route, service identity, and model selection and sets a repair-required status. It does
not compute an EffectiveRoute or fall back to Gateway or another ready service. Remove
EffectiveRoute, EffectiveRouteLabel, RouteFallbackActive, and FallbackReason from the
settings contract. Replace them with one typed selection status: SystemDefault, Ready,
VerificationUnavailable, NeedsRepair, or LegacyRepairRequired, plus a typed diagnostic
and remediation. VerificationUnavailable means the catalog request itself failed and
offers Retry without claiming the saved route is broken. NeedsRepair means a successful
catalog observation proved the saved route/model unavailable. The UI keeps the original
selection visible and enables Save only after a valid replacement or Reset is selected.

### Committed catalog authority

UserConfig proves what the user selected; it does not prove that the provider still
publishes that model. Durable authorization therefore extends the existing owner-scoped
NyxIdAuthorizationCatalogGAgent and its current-state read model rather than creating a
parallel LLM catalog.

The existing NyxIdAuthorizationServiceEvidence gains an optional typed LLM target
evidence sub-message. It is present only when that exact UserService is an LLM route and
contains route value, catalog certainty, normalized models, optional default model,
observed/fresh timestamps, and the authority contract/policy versions. The catalog
state also gains optional Gateway LLM target evidence because Gateway is not a
UserService grant. Non-LLM services carry no LLM target evidence.

The existing explicit authorization-catalog refresh flow remains the only writer. When
an LLM-dependent authorization requires a target, the command-side coordinator supplies
that target requirement to refresh. The Infrastructure adapter uses the authenticated
bearer to fetch the exact UserService inventory/scope plan plus only the required
Gateway or exact service model surface, normalizes it, and submits one typed observation
to the catalog actor. It does not fan out across unrelated providers. The actor commits
the observation and the normal projection pipeline materializes it.

The model endpoint is built only from the configured NyxID authority and a canonical
route derived from Gateway or the exact verified inventory slug. A request-supplied
absolute URL, saved route string, display name, or model prefix is never used to build a
network destination. The adapter reads the required route models endpoint using the
existing bounded HTTP client and strict response parsing. This prevents SSRF and keeps
malformed or oversized upstream data outside actor state.

The catalog content digest expands to cover Gateway evidence and per-service LLM target
evidence. Existing snapshots without those fields remain valid service-grant evidence,
but they cannot authorize an LLM-dependent durable target. The planner reads only the
committed UserConfig and authorization-catalog read models, matches exact route/service
identity and model against typed evidence, and includes the catalog state version in the
existing authority stamp and permission digest. It performs no network call or refresh.

The planner returns a typed catalog-refresh requirement when matching LLM evidence is
missing or stale. The command-side schedule coordinator passes that requirement to the
existing refresh port and retries only after the committed projection reaches the
reported actor state version. It does not parse a detail string or query UserConfig a
second time to reconstruct the target.

The refresh requirement contains the shared route kind, exact UserService ID and slug
snapshot when applicable, explicit model ID, and the UserConfig source state version.
It never contains a caller-provided URL. One authorization command performs at most one
targeted refresh and one re-plan. If the UserConfig version changes, the refreshed target
does not match, or projection has not reached the committed catalog state version, the
command returns the typed changed/pending outcome and the user retries; it does not loop
or refresh a second inferred target.

Gateway also requires the catalog snapshot even though it requires no UserService
grant. The planner's no-service-catalog shortcut is allowed only when no LLM target and
no service grant are required. This closes the current path where Gateway could bypass
model verification merely because its service grant set is empty.

An authorization-catalog refresh is allowed to enforce freshness because it is an
explicit authorization action. A schedule fire uses the frozen authorization fact and
does not re-query the current model catalog; catalog change takes effect on explicit
reauthorization, not midway through an existing authorization epoch.

Adding LLM evidence bumps the authorization-catalog contract/policy versions. Adding
typed planner failures bumps the scheduled-authorization schema version. Historical
catalog snapshots with absent LLM fields remain valid for non-LLM service grants, and
historical authorization facts keep their frozen runtime semantics. Any new or
reauthorized LLM-dependent schedule must pass the new catalog contract.

## Validation and Security Boundaries

Validation is intentionally split by responsibility:

1. Settings write authenticates the resource owner, resolves an exact route identity,
   validates readiness and catalog certainty, and atomically commits route and model.
2. A publish or bind command that declares durable execution, and every schedule
   authorization command, performs an explicit owner-scoped catalog refresh and then
   consumes committed UserConfig and authorization-catalog read models. It requires an
   exact route plus an enumerated explicit model. Interactive-only authoring may persist
   ProviderDefault, but it is not durable authorization evidence. Grants are issued only
   for the exact service identity.
3. Invocation runtime verifies that the persisted authorization fact and payload route
   and model still match exactly before credential access or dispatch.

The existing permission digest continues to bind route, exact service identity, slug,
and model. ProviderDefault, NotVerifiable, Unavailable, and Unspecified cannot be used
as durable model evidence for an LLM-dependent workflow. The downstream
ScheduledInvocationOwnerLLMSelection contract can continue carrying one explicit model
string because only ExplicitModel is admitted into durable authorization.

Normal read, planning, and invocation layers may not:

- obtain a live catalog while reading UserConfig, planning from committed evidence, or
  authorizing an invocation; only the explicit command-side catalog refresh adapter may
  call NyxID before the planner is retried against the materialized state version;
- infer a model from a provider display name, slug, route, or model prefix;
- silently fall back across providers;
- log bearer tokens, refresh tokens, Agent Keys, Vault ciphertext, or upstream response
  bodies that may contain credentials;
- expose internal exception details as the user-facing message.

Bearer tokens and model endpoint bodies remain Host or Infrastructure adapter data and
are never placed in UserConfig, workflow state, schedule state, catalog diagnostics, or
logs. Logs contain stable codes and non-sensitive owner, service, workflow, revision,
and schedule IDs only.

Interactive runtime ports such as IOwnerLlmConfigSource and
INyxIdUserLlmPreferencesStore return the shared typed LLMSelection plus MaxToolRounds;
they no longer expose independent DefaultModel and PreferredRoute strings. Runtime
appliers branch on the typed selection: a genuine empty Unspecified state uses the
configured system default, ProviderDefault sets only the selected route, and
ExplicitModel sets the selected route and exact model.

A typed route without model selection, or absent typed selection accompanied by any
legacy route/model value, is LegacyRepairRequired rather than Unspecified. Interactive
execution stops before the LLM call with a safe reselect-model action. It does not
silently switch to the system default or combine compatibility strings. A structurally
valid saved selection remains the runtime target even when a later catalog read marks it
unavailable; the call may fail with its typed provider error, but it never crosses to a
different provider automatically.

## Workflow Artifact Compatibility Preflight

### Placement and scope

Add one shared, pure local preflight in every definition or run provisioning path in
WorkflowRunActorPort:

- EnsureDefinitionAsync;
- CreateRunAsync;
- EnsureRunAsync and EnsureRunAndDispatchAsync through EnsureRunCoreAsync;
- direct definition binding before its actor dispatch.

For an inline or newly provisioned definition, preflight runs before every runtime
operation. When a caller references an existing definition actor, the port may perform
the existing read-only runtime lookup and binding read first so that the authoritative
bound YAML and plan can be compared with the request. In all cases, preflight completes
before runtime create, link, binding repair, or actor dispatch. A rejected artifact
therefore creates no definition actor, no run actor, no link, and no binding event.

The port reuses WorkflowDefinitionParser, WorkflowAuthorizationDependencyEvaluator,
and WorkflowCapabilityAdmissionPlanIntegrity. It does not introduce a second parser,
regex scanner, registry, or external-capability resolver.

### Deterministic algorithm

Given WorkflowDefinitionBinding and its persisted capability plan, preflight performs
only these steps:

1. Parse and validate the root YAML.
2. Parse and validate every distinct inline YAML document, including the root only once
   when the bundle map also contains it, using the existing parser and naming rules.
3. Evaluate the complete root and inline invocation set. The existing evaluator rejects
   retired direct nyxid_*__* tool names with typed migration readiness.
4. Determine whether the parsed bundle has an external invocation requiring a
   capability admission plan.
5. If it has external invocations, require a persisted plan and use the structural
   portion of WorkflowCapabilityAdmissionPlanIntegrity to verify schema, execution
   mode, definition digest, call-site set, selector mapping, request and grant digests,
   durable owner shape, required source presence, canonical ordering, and admission
   digest.
6. If it has no external invocations, accept an absent plan. If a plan is present, it
   must still be structurally valid and contain no unmatched invocation admission.
7. Return success or throw an existing typed workflow capability exception with a
   stable code, safe message, and remediation.

The check does not inspect source freshness and does not call a catalog, event store,
actor state, or network service. The existing-definition branch may consume only the
already-required binding current-state read model; it cannot prime, replay, repair, or
write that read model. The check does not mutate or replace the plan.

The preflight validates the execution mode recorded by the persisted plan against the
mode already established by the publish or bind flow. It does not infer execution mode
from ScheduleId, RunOrigin, actor ID, or route position. Add non-optional
ExpectedExecutionMode to WorkflowDefinitionBinding and to the persisted
WorkflowActorBinding read contract. Every producer supplies Interactive or Durable from
its typed publish, binding, deployment, schedule, fork-seed, or chat context. Unspecified
is rejected before actor lifecycle. Existing definition reuse requires the requested and
bound modes to match.

The shared local preflight implementation belongs beside the existing external
capability admission logic in the Application layer. It reuses the same bundle parser
and WorkflowCapabilityAdmissionPlanIntegrity validation but omits source-freshness and
external-readiness checks. WorkflowRunActorPort invokes that narrow port; Infrastructure
does not reimplement admission semantics.

WorkflowCapabilityAdmissionPlanIntegrity exposes one pure typed compatibility result
for schema, mode, definition digest, call sites, selectors, source presence, owner shape,
ordering, and admission digest. Existing throw-based callers may wrap that result, but
preflight switches on the failure enum and maps all artifact-integrity failures to
CAPABILITY_ADMISSION_REBIND_REQUIRED. Typed parser authoring failures such as the legacy
NyxID tool code pass through unchanged. No layer parses exception text to choose a code.

### Absent-plan rules

| Parsed workflow | Persisted plan | Result |
| --- | --- | --- |
| No external capability invocation | absent | Accept. |
| No external capability invocation | present, empty, structurally matching | Accept. |
| No external capability invocation | present with admissions or mismatched digest | Reject and rebind. |
| External capability invocation | absent | Reject and rebind before actor lifecycle. |
| External capability invocation | present and structurally matching | Accept. |
| External capability invocation | present but legacy, corrupt, or mismatched | Reject and rebind. |

OwnerLlmRouteRequired by itself does not require a workflow capability admission plan;
owner LLM selection is validated by the schedule authorization contract. Artifact
preflight validates YAML and the external call-site plan, not a live owner preference.

### Source freshness boundary

RevalidatePersistedAsync remains valid for explicit publish, save-and-bind, binding
upsert, authorization, and reauthorization workflows where current source evidence is
part of the user action. Invocation preflight calls only local structural integrity
logic. This preserves long-lived schedule execution without weakening structural or
identity checks.

## Errors and User Experience

### Typed errors

Reuse ExternalCapabilityReadiness and existing remediation actions for workflow
artifact failures. Required stable outcomes are:

| Condition | Stable code | Safe message | Remediation |
| --- | --- | --- | --- |
| Invalid root or inline YAML | WORKFLOW_DEFINITION_INVALID | Workflow definition is invalid. | Update and rebind workflow. |
| Legacy direct NyxID tool | NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED | Workflow uses a retired NyxID tool contract. | Update and rebind workflow. |
| Missing or legacy plan | CAPABILITY_ADMISSION_REBIND_REQUIRED | Workflow capability admission must be rebuilt. | Update and rebind workflow. |
| YAML and plan mismatch | CAPABILITY_ADMISSION_REBIND_REQUIRED | Saved workflow and capability admission no longer match. | Update and rebind workflow. |
| LLM route unavailable | owner_llm_route_unavailable | Selected LLM service is unavailable. | Choose or reconnect a service. |
| Model catalog unverifiable | owner_llm_model_not_verifiable | Selected provider cannot verify a durable model target. | Choose a provider with a model catalog. |
| Explicit model absent | owner_llm_model_unavailable | Selected model is unavailable on this service. | Choose an available model. |
| Route or model fact drift | owner_llm_payload_mismatch | Authorized LLM target does not match invocation. | Reauthorize before retry. |

New LLM failures use typed application or authorization outcomes and stable wire codes;
they are not created by parsing exception message strings. Add distinct typed planner
failure enum values for OwnerLLMRouteUnavailable, OwnerLLMModelNotVerifiable, and
OwnerLLMModelUnavailable; each maps one-to-one to the stable codes in the table. The
typed catalog-refresh requirement carries the target and failure enum separately from
human detail. Host adapters map safe messages to API or channel responses and log the
stable code plus non-sensitive IDs. Runtime fact corruption continues to use the
existing typed runtime failure enum.

### Settings UX

- Show System default for Unspecified, not Gateway.
- Show Provider default as an explicit model choice only where interactive use permits
  it.
- Show catalog certainty and readiness next to each route.
- Preserve an unavailable saved selection in place with Needs repair.
- Disable unavailable options instead of hiding them when they explain saved state.
- Provide Retry, Connect, or Choose replacement actions from typed diagnostics.
- After accepted ACK, show Update submitted with the command ID and pending state; show
  Active only after the read model returns the exact submitted selection.

### Workflow and schedule UX

Invocation admission failure is attributed to the schedule invocation attempt, not to a
new workflow run. The caller receives workflow, revision, or schedule identity, a stable
code, a safe message, and remediation. Because admission fails before run creation, no
phantom run appears in the run list.

For scheduled dispatch, the invocation adapter returns the typed rejection to the
existing schedule actor. The actor then records failureCount and lastError through its
normal committed failure event. This is an implementation acceptance requirement, not
an assumption about current behavior. This design does not add terminal run
correlation; it only ensures an admission failure is visible at the schedule that
attempted it.

## Data Flow

### LLM selection write

    authenticated settings or model command
      -> exact catalog option and typed certainty
      -> validate route readiness and model choice
      -> complete UserLlmSelection command
      -> UserConfigGAgent commit
      -> committed state event
      -> UserConfig current-state read model

### Durable authorization and invocation

    explicit authorization catalog refresh
      -> catalog actor commit and current-state projection
    UserConfig read model plus authorization catalog read model
      -> schedule authorization planner
      -> exact route, model, and service authorization fact and digest
      -> scheduled payload with identical route and model
      -> runtime exact-match validation
      -> actor inbox dispatch

### Workflow artifact admission

    persisted WorkflowDefinitionBinding
      -> local root and inline parse
      -> external invocation evaluation
      -> local plan integrity validation
      -> accepted: resolve or create actors
      -> rejected: typed repair outcome and zero actor lifecycle mutation

## Migration and Rollout

### UserConfig

- New writes use only the complete typed selection.
- Legacy default_model and preferred_llm_route remain compatibility reads.
- Legacy route and model pairs are not automatically promoted into typed selection,
  because exact route identity and catalog certainty cannot be proven from strings.
- Interactive users explicitly reselect in settings. Durable authorization fails closed
  until the owner commits an exact route and enumerated explicit model.
- No background repair actor or startup migration is added in this batch.

### Workflow artifacts

- Existing compatible current-schema plans continue to run after local preflight.
- Artifacts containing direct nyxid_*__* tools or missing or mismatched plans fail
  before actor creation and require a normal authoring update and rebind.
- Invocation never rewrites YAML, recalculates a plan, or refreshes source stamps.
- Operators identify affected schedules through stable last-error codes and repair them
  deliberately.

### Deployment order

Deploy protobuf contracts, actor validation, projectors and query mapping, catalog
composition, settings and channel writers, durable authorization validation, and
workflow preflight as one compatible release. Before enabling unattended fires, audit
active schedules for legacy UserConfig selections and incompatible workflow artifacts
using read-only evidence. Do not print credentials during the audit.

## Testing Strategy

Implementation follows test-driven development. Each behavior starts with a focused
failing test, then the minimum production change.

### UserConfig and catalog tests

- generic UserConfig cannot mutate any LLM field;
- service, Gateway, model-change, preset, and Reset writes commit a complete selection;
- model-only slash and card actions cannot write and the model-only selection interface
  method no longer exists;
- Enumerated, NotVerifiable, and Unavailable round-trip without using an empty list as a
  discriminator;
- explicit models must belong to the exact inventory-backed option;
- authoritative saves bypass or validate freshness of the presentation cache;
- ComposeUserServiceInventory retains Gateway diagnostics and exact inventory identity;
- missing Gateway diagnostics produce unavailable, not ready;
- an unavailable saved selection remains visible and does not fall back;
- settings expose one typed status and no effective-route fallback fields;
- catalog transport failure is VerificationUnavailable, while a successful negative
  observation is NeedsRepair;
- accepted ACK is rendered as submitted/pending until the exact projection is observed;
- compatibility fields are derived on new commits and ignored by runtime and
  authorization when typed selection is absent.

### Durable authorization tests

- an LLM-dependent durable workflow accepts only an exact route plus an enumerated
  explicit model;
- Unspecified, ProviderDefault, NotVerifiable, unavailable route, and unknown model fail
  with stable codes;
- route, model, or service identity changes alter the permission digest;
- catalog content digest changes when exact-service or Gateway LLM target evidence
  changes;
- planner accepts model evidence only from the matching committed catalog entry and
  performs no external call;
- Gateway cannot take the no-catalog planner shortcut;
- authorization refresh constructs model endpoints only from configured authority and
  canonical verified identity, bounds response size/model count, and never stores or
  logs credentials;
- runtime rejects payload and fact drift before credential access;
- interactive runtime consumers receive one typed route/model selection and do not
  reconstruct it from compatibility strings;
- no query port performs catalog lookup, projection priming, replay, or repair.

### Workflow preflight tests

- root and inline legacy nyxid_*__* tools fail before any runtime get, create, link, or
  dispatch call;
- external invocation with absent, legacy, mismatched, corrupt, or wrong-mode plan fails
  before actor lifecycle;
- unspecified or mismatched ExpectedExecutionMode fails before actor lifecycle;
- current matching plan succeeds;
- no-external-capability workflow with no plan succeeds;
- no-external-capability workflow with a valid empty matching plan succeeds;
- no-external-capability workflow with a non-empty or mismatched plan fails;
- preflight does not call external readiness or catalog ports and does not enforce
  source freshness;
- rejected scheduled invocation updates schedule failure evidence and creates no run.

### Required verification

Run the smallest related tests during implementation, then:

- bash tools/ci/test_stability_guards.sh;
- bash tools/ci/workflow_binding_boundary_guard.sh;
- bash tools/ci/query_projection_priming_guard.sh when query contracts change;
- bash tools/ci/architecture_guards.sh;
- bash tools/docs/lint.sh;
- affected Studio, Workflow, ChannelRuntime, and GAgentService test projects;
- dotnet build aevatar.slnx --nologo.

Tests use distinct memberId, workflowId, and publishedServiceId fixtures wherever those
identities appear. No test adds polling delays unless explicitly allowlisted by the
repository stability guard.

## Acceptance Criteria

- No public or internal normal write path can persist a model without its route
  identity.
- Catalog certainty is typed; AvailableModels being empty no longer drives authorization
  or selection semantics.
- Reset restores an unspecified system default and never silently selects Gateway.
- A saved unavailable route is retained and marked repair-required.
- Durable LLM-dependent execution is authorized only for an exact, verifiable route and
  explicit model.
- Legacy or structurally incompatible workflow artifacts are rejected before all actor
  lifecycle operations with typed remediation.
- Invocation preflight performs no external lookup, replay, repair, or source-freshness
  check.
- Compatible workflows without external capability calls still run without an admission
  plan.
- Required tests, guards, documentation lint, and build pass.

## Deferred Work

- Correlate every schedule fire with the workflow terminal result.
- Resolve lifecycle and repair semantics for permanently unknown runs.
- Add an explicit provider OpenIdentifier capability if a real provider contract
  requires it.
- Remove legacy compatibility fields after production state and external consumers are
  migrated.
