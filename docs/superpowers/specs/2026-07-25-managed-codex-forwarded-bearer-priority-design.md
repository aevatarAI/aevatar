# Managed Codex Workflow Caller Credential Separation

## Status

Amended on August 2, 2026. The former single-credential priority rule is
superseded.

## Product Mismatch

A NyxID-proxied workflow request can carry two credentials with different
purposes:

- `X-NyxID-Delegation-Token` authorizes downstream NyxID REST proxy execution;
- `Authorization: Bearer ...` is source-readable and supports current-user
  identity, inventory, and managed Codex readiness.

Treating these credentials as alternatives is incorrect. Selecting the
forwarded bearer makes managed Codex readiness work but sends the wrong token
to ordinary NyxID REST proxy tools. Selecting delegation makes proxy execution
work but prevents source-readable readiness.

## Decision

Workflow ingress preserves both purposes in one typed caller-credential
contract:

1. `BearerToken + Kind` is the execution credential. Delegation is selected
   when the delegation header is present; otherwise the source-readable bearer
   remains the execution credential for source-only callers.
2. `SourceReadableUserBearerToken` is an optional supplemental credential. It
   is valid only when the execution kind is `ProxyDelegation`.
3. Any present malformed credential fails closed. A valid credential never
   hides a malformed second credential.
4. Admission/readiness uses the source-readable credential when available.
   Runtime proxy execution uses the execution credential.
5. Service invocation carries the caller credential kind as a typed enum and
   the supplemental bearer in the dedicated
   `caller_source_readable_nyx_id_bearer_token` field. It never reuses LLM
   control or metadata fields for this purpose. Dispatch never infers
   credential purpose from token equality, route shape, or header precedence.

The execution and supplemental credentials are stored under distinct
run-scoped runtime-secret references. Raw values are removed from committed
events and committed state roots. An unresolved required secret reference
fails the entire caller-credential resolution.

## Tool Context Mapping

Workflow tool context maps the purpose-separated credentials as follows:

- `NyxIdAccessToken`: execution credential;
- `NyxIdOrgToken`: organization credential, not a source-readable transport;
- `SenderNyxIdAccessToken`: channel sender credential, not a source-readable transport;
- `SourceReadableNyxIdAccessToken`: supplemental source-readable credential;
- `NyxIdCredentialKind`: execution credential kind.

The credential middleware preserves the dedicated supplemental credential for
proxy delegation without copying it into organization, sender, LLM-control, or
metadata fields. Existing proxy isolation still clears organization and sender
credentials before tool execution.

Managed Codex resolves the source-readable credential. For a typed proxy
delegation context it must not fall back to the delegation token. Other NyxID
proxy tools continue to use `NyxIdAccessToken`.

## Security Properties

- Authenticated caller identity continues to come from the validated principal
  and typed NyxID authority, not from either raw token.
- The supplemental field cannot be used with an untyped, durable, or
  source-readable execution credential.
- Raw credentials are not logged, projected, returned by APIs, or included in
  workflow output.
- Managed Codex Agent Keys remain in `ISecretVault`; this change does not alter
  chrono-sandbox or NyxID credential contracts.
- Source-only and delegation-only callers remain supported without inventing a
  second workflow execution path.

## Verification

Automated verification must prove:

1. Authorization-only requests produce a typed source-readable credential.
2. Delegation-only requests preserve `ProxyDelegation` through service dispatch.
3. Requests containing both preserve distinct execution and source-readable
   credentials.
4. Malformed Authorization or delegation fails closed even when the other
   credential is valid.
5. Runtime-secret resolution restores both credentials, while a missing
   supplemental reference fails closed.
6. Tool context sends delegation to proxy tools and source-readable bearer to
   managed Codex.
7. Committed event and state-root publication remove both raw fields.
