# NyxID code-execution wire conformance

This manifest is the reviewed boundary between Aevatar code execution and NyxID. It is deliberately
independent from the fixed NyxID Assistant semantic-evaluation pin: code-execution drift follows the
current NyxID `main`, while the Assistant corpus remains reproducible at its declared source revision.

The guard validates whole-file SHA-256 digests and semantic markers for:

- `/keys` ownership of `auto_connected` and the `/user-services` route fields consumed by admission;
- general versus `scheduled_invocation` Agent Key response fields and durable-grant behavior;
- actual bearer forwarding and delegation-token injection in the proxy;
- catalog identity propagation, including the no-op same-value transition and customized-row count;
- the `/keys` to `unified_key_service::create_key` credential-validation path.

## Reviewed upstream revision

The current baseline reviews NyxID
[`301fbe732a0f20a3c674184e7ea3408ab62968ab`](https://github.com/ChronoAIProject/NyxID/commit/301fbe732a0f20a3c674184e7ea3408ab62968ab),
including the following changes since `cdd0e3fdad4b45365dc7da3effdb1c1447de8286`:

| Upstream surface | Reviewed behavior and Aevatar impact |
| --- | --- |
| `handlers/api_keys.rs` | Optional conversation information and service-history storage were added. General/scheduled purpose, scheduled-write capability and durable-grant response semantics remain intact; Aevatar's security-class parser still rejects malformed or unexpected authority. |
| `handlers/keys.rs`, `handlers/user_services_handler.rs` | Additive icon, authorship and skill-revision fields do not change the fields consumed by Aevatar. API-key inventory reads now enforce the key's service scope and exclude Viewer organization rows; `/keys` uses read-only listing for API keys. Aevatar consumes caller-visible exact IDs and fails closed for missing/denied routes. It must not infer account-wide absence from scoped inventory. |
| `services/unified_key_service.rs` | `validate_token_exchange_catalog_credential` became `validate_catalog_credential` and also validates IFTTT credentials. The token-exchange branch still calls `provider_token_exchange_service::parse_credential` with the catalog's declared credential fields. The manifest checks the new call and that retained validation path. Other changes concern provisioning eligibility, history and deletion; read-only key listing remains distinct from provisioning. |
| `services/catalog_identity_service.rs` | Storage calls use the service-history collection. Same-value transitions remain no-ops and customized-row accounting still uses matched rows. Identity propagation field semantics are unchanged. |
| `handlers/proxy.rs`, `services/proxy_service.rs` | Proxy changes add destination routing, curation service-account restrictions and billing behavior. Exact instance routing, delegation-token injection and scheduled durable-operation headers remain enforced. Direct and node HTTP bearer forwarding now use `forwarded_caller_token`, which preserves an existing server-owned Authorization header. Its implementation is additionally digest-pinned so the forwarding contract is checked at its new owner. |

The corresponding Aevatar consumers are `NyxIdApiAccessResponseParser`,
`NyxIdApiClient`, `NyxIdCodeExecutionRouteAdmissionPreparer`,
`NyxIdCodeExecutionPort` and `NyxIdDurableCodeExecutionPort`. Their consumed
fields and authorization invariants remain compatible; no production adapter
change is needed for this baseline refresh. Existing focused tests cover
unknown response fields, exact selection, scoped/denied inventory, forwarding
and delegation requirements, and durable headers. Guard self-tests exercise
digest drift, missing markers and unchanged descendant revisions. This is
source-contract review and local regression evidence, not a live deployment test.

## Updating and checking the baseline

`nyxid.reviewed_revision` records the commit at which the hashes were reviewed. Validation does not
require the checkout `HEAD` to equal that commit: `HEAD` may be that revision or a descendant on
`main`. Any tracked source change still fails because its digest changes, so an upstream contract
change must be reviewed together with the corresponding Aevatar adapter and tests before refreshing
the baseline.

Run against a full-history checkout of current NyxID `main`:

```bash
bash tools/ci/nyxid_conformance_guard.sh \
  --nyxid-wire-root /path/to/NyxID
```

`.github/workflows/nyxid-conformance.yml` performs this check for relevant pull requests, manual
runs, and once per day. The workflow checks out `main` by name; it never substitutes the reviewed
revision for the current upstream branch.
