# NyxID Proxy Audit Attribution Design

## Problem

Issue #2935 reports recurring `nyxid_proxy` failures, but the audit artifacts do
not identify the downstream NyxID UserService or retain the stable proxy failure
code. This makes several independent downstream services appear to be one
failing component.

`NyxIdProxyReceiptFactory` currently returns a provider receipt only for a
recognized proxy failure. It does not set `AgentToolReceipt.SubjectKind` or
`SubjectId`, so the audit target falls back to the per-invocation tool call id.
`ToolAuditRecordFactory` then reduces `NYXID_PROXY_HTTP_502` to `tool_error`
because its failure-code allowlist contains only generic middleware and approval
codes.

The runtime behavior itself is already truthful: the provider classifies the
downstream HTTP failure, workflow retry and fallback consume that typed failure,
and affected runs can recover. This change corrects the audit contract; it does
not change proxy execution or workflow control flow.

## Scope

This fix changes only the Aevatar repository. It does not modify FKST grouping,
detector revisions, incident state, alert thresholds, NyxID services, or
production configuration.

The Aevatar change must make every executed proxy call that has an admitted
exact UserService identity observable under the same stable audit target on
both success and failure. It must also preserve safe, repository-owned NyxID
proxy failure codes without accepting provider-controlled or free-form strings.

## Semantic Decision

The exact NyxID `user_service_id` is the resource identity of a proxy
invocation. The tool call id remains correlation for one invocation and must not
stand in for the downstream resource.

NyxID proxy receipts use:

- `SubjectKind = "nyxid.user-service"`;
- `SubjectId` equals the exact `user_service_id`;
- the existing `CallId` for invocation correlation;
- the existing provider-owned status and safe error fields for outcome.

The provider creates a typed receipt for successful proxy results as well as
recognized proxy failures. Success and failure therefore produce the same
`AuditTarget.Kind/Id`, allowing consumers to calculate a service-specific
success rate. If no valid exact UserService id is available, the factory must
not fabricate one from the slug, path, call id, or response.

The service slug and request path are routing snapshots, not resource identity.
They will not be copied into the audit target. Query strings, fragments,
headers, arguments, response bodies, and credential material remain excluded.

## Receipt Flow

The current `origin/feature/integrate` implementation has one live proxy
surface, `NyxIdProxyTool`. Ordinary calls parse `service_id` from structured
tool arguments and use the post-result factory. Proof-bound calls take the exact
UserService from committed admission and return a receipt at execution time,
where HTTP status and completed file ingress remain available. The
connected-service proxy layer was deleted upstream and this fix does not
restore it.

The factory performs the following mapping:

1. Normalize the exact UserService id using the existing bounded,
   control-character-free rule.
2. Parse the existing NyxID proxy error envelope.
3. For a recognized authorization-required response, retain the current typed
   authorization payload and attach the stable subject when the id is valid.
4. For another recognized proxy error, retain the current safe error receipt
   and attach the stable subject when the id is valid.
5. For a non-error result with a valid exact id, return a success receipt with
   the stable subject.
6. For a non-error result without a valid exact id, return `null` and retain the
   existing generic receipt behavior rather than inventing identity.

The existing receipt finalizer remains the single runtime normalization path.
No second audit pipeline, result inspector, or metadata bag is introduced.

## Failure-Code Policy

Audit error codes are safe classifications, not diagnostic text. The audit
factory will preserve only these NyxID proxy codes:

- exact `NYXID_PROXY_UNAUTHORIZED`;
- exact `NYXID_PROXY_FORBIDDEN`;
- exact `NYXID_PROXY_HTTP_[1-5][0-9][0-9]`.

The HTTP form is accepted only when the entire value matches the pattern. A
suffix, prefix, whitespace-internal variant, unexpected status shape, arbitrary
provider error key, or token-shaped value still maps to the generic
`tool_error`. The audit record continues to omit `ErrorMessage`, result JSON,
arguments, headers, and raw downstream content.

This narrow policy retains `NYXID_PROXY_HTTP_502` for #2935 while preserving the
current secret-exposure defense for all unowned strings.

## Compatibility

No protobuf field or public method signature changes. The existing strongly
typed `AgentToolReceipt.SubjectKind`, `SubjectId`, `Status`, and `ErrorCode`
fields already express the required contract.

Successful proxy calls will change from a synthetic generic audit receipt to a
provider-owned typed success receipt. Their execution result, workflow outcome,
approval behavior, and returned payload remain unchanged. Existing failure
receipts retain their user-facing safe messages and workflow failure semantics;
only their audit resource identity and allowlisted audit error code become more
specific.

## Tests And Verification

Regression tests will prove:

- a successful `nyxid_proxy` result creates a success receipt targeting the
  exact UserService;
- a 502 result creates an error receipt targeting the same exact UserService;
- authorization-required receipts also retain the exact UserService target;
- the audit factory preserves `NYXID_PROXY_HTTP_502`,
  `NYXID_PROXY_UNAUTHORIZED`, and `NYXID_PROXY_FORBIDDEN`;
- malformed lookalikes and arbitrary compact strings still become
  `tool_error` and do not leak into the serialized audit record;
- arguments, result bodies, paths with sensitive query values, and credentials
  remain absent from audit artifacts.

The canonical audit document will state that provider-owned stable resource
identity must be consistent across successful and failed tool receipts, and
that only allowlisted stable failure classifications may cross into audit
artifacts.

Verification includes focused AI Core and NyxID provider tests, test stability
guards, documentation lint, architecture guards, and the affected project
builds. Full-solution verification will be reported separately if its runtime
cost or unrelated existing failures prevent completion.
