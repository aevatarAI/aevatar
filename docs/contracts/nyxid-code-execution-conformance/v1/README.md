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
