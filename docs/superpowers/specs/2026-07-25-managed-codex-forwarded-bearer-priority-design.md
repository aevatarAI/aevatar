# Managed Codex Forwarded Bearer Priority Design

## Status

Approved for the internal P0 rollout on July 25, 2026.

## Product Mismatch

The workflow boundary currently treats the NyxID delegation token as the only
caller credential when both inbound credentials are present, while transparent
managed Codex readiness requires the forwarded current-user bearer for NyxID
identity confirmation and API-key management.

This is a runtime and credential-contract mismatch:

- `X-NyxID-Delegation-Token` is a short-lived downstream proxy credential;
- `Authorization: Bearer ...` is the internal P0 current-user authorization
  credential;
- the authenticated NyxID subject remains derived from the validated principal,
  not from either raw credential.

## Production Evidence

The canary workflow reached `codex_exec` on Aevatar image `0c873a03`, with the
managed feature enabled, the caller admitted by the allowlist, and an active
credential projection. The workflow boundary reported both an inbound caller
bearer and NyxID tool credentials.

`WorkflowCallerCredentialExtractor` selected the injected delegation token.
Managed Codex readiness then sent that token to
`GET https://nyx-api.chrono-ai.fun/api/v1/users/me`, which returned HTTP 403.
The execution failed with `nyxid_identity_invalid` before chrono-sandbox was
called.

## Decision

For the internal P0, workflow HTTP ingress selects its caller credential in this
order:

1. If an `Authorization` header is present, it must be a valid single
   `Bearer <token>` value. Use that bearer.
2. Otherwise, if `X-NyxID-Delegation-Token` is present, it must contain exactly
   one valid raw token. Use that delegation token.
3. If neither credential is present, continue without a workflow caller
   credential.
4. A malformed selected credential fails closed. Do not fall back from a
   malformed Authorization header to delegation.

This keeps the existing typed `WorkflowCallerCredential` contract for P0 and
does not introduce a second credential field into workflow Actor state.

## Why This Is the P0 Choice

The current internal Aevatar NyxID UserService temporarily forwards the user's
access token and also injects a delegation token. Prioritizing the forwarded
bearer lets the same `codex_exec` call create or repair the user's managed Agent
Key without a manual provisioning step or a NyxID change.

The alternatives are deliberately deferred:

- Carrying both credentials as separate typed fields is the long-term precise
  contract, but requires protobuf, Actor-state, command, mapping, and migration
  changes.
- Disabling delegation injection on the Aevatar UserService would avoid a code
  change but couples product readiness to mutable NyxID configuration.

## Boundary and Security Properties

- Caller identity still comes from the authenticated principal and typed native
  NyxID authority.
- `scopeId` is not used as a credential identity.
- The forwarded user bearer is used only because the internal P0 explicitly
  accepts this weaker boundary.
- The persistent managed Codex Agent Key remains stored only in `ISecretVault`.
- Aevatar continues to call the user's exact `chrono-sandbox` UserService with
  the Vault Agent Key.
- The chrono-sandbox UserService continues to terminate that persistent key at
  NyxID and inject a five-minute delegation token into the one-shot runner.
- No OpenSandbox, runner, Codex provider, or chrono-sandbox credential contract
  changes.

The public rollout remains blocked. Before disabling access-token forwarding,
the product must either carry both credential purposes as separate typed
contracts or NyxID must provide a delegated capability that can safely perform
the required self-service readiness operations.

## Implementation Surface

Change only the workflow HTTP credential extractor and its focused tests:

- prefer a valid forwarded Authorization bearer when both headers exist;
- retain delegation-only behavior;
- retain missing-credential behavior;
- fail closed for malformed Authorization and malformed selected delegation;
- update canonical NyxID workflow credential documentation and the managed
  Codex rollout documentation to describe the temporary internal P0 rule.

No API field, protobuf field, Actor identity, Projection Pipeline, or chrono
transport contract changes.

## Verification

Automated verification must prove:

1. Authorization-only requests expose the bearer.
2. Delegation-only requests expose the delegation token.
3. Requests containing both expose the forwarded bearer.
4. Malformed Authorization fails even when a valid delegation header exists.
5. A valid Authorization bearer is not rejected because an unselected
   delegation header is malformed.
6. Existing workflow capability tests, managed Codex tests, architecture
   guards, and the full build remain green.

Production verification must use the signed-in local NyxID CLI to invoke the
canonical inline `codex_exec` workflow. Success requires:

- workflow terminal status succeeded;
- managed target `managed_sandbox`;
- output exactly `CODEX_EXEC_READY`;
- a sanitized diagnostic ID;
- no manual provisioning call before the workflow;
- chrono-sandbox creation, execution, and cleanup evidence.
