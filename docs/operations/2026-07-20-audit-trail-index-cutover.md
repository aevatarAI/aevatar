---
title: "Audit Trail Elasticsearch Index Cutover"
status: active
owner: platform
---

# Audit Trail Elasticsearch Index Cutover

## Scope

This runbook deploys the audit query/index fix without enabling Elasticsearch dynamic
auto-create and without deleting, rebuilding in place, or overwriting the existing
`<prefix>-audit-trail` index.

The application startup reconciler performs these governed operations:

1. Resolve `<prefix>-audit-trail-current` as an alias.
2. Create `<prefix>-audit-trail-current-v<schema-fingerprint>` with explicit audit mappings.
3. If the legacy `<prefix>-audit-trail` index exists, reindex it into the new physical with
   `op_type=create` and require zero per-document failures and no timeout.
4. Attach or atomically repoint `<prefix>-audit-trail-current` only after the copy completes.
5. Retain the legacy index and every previous physical index. No startup action deletes data.

Normal rollout requires no manual Elasticsearch write. A failed reconcile is visible in the
application log, `/health/ready`, and the `audit-query-index` target in `/api/status`; stop the
rollout and investigate instead of deleting or recreating an index.

Chat Activity records share this alias and add typed chat-provenance mappings. Before enabling
their 30-day TTL, wait for fingerprinted copy-forward reconciliation and verify the active alias,
schema fingerprint, source/target document counts, backup evidence, and enough headroom for both
physical copies. Then follow the separate
[Chat Activity Audit Retention](chat-activity-audit-retention.md) procedure. Retention never runs
inside startup reconciliation.

## Pre-Deployment Record

Record the intended image commit and current deployment image:

```bash
git rev-parse HEAD
KUBECONFIG="$HOME/Code/aelf-shared-k8s-prod.yaml" \
  kubectl -n aismart-app-mainnet get deployment aevatar-console-backend \
  -o jsonpath='{.spec.template.spec.containers[?(@.name=="aevatar-console-backend")].image}{"\n"}'
```

Do not print kubeconfig contents, bearer values, or Elasticsearch credentials in the change
record.

## Deployment

Build and deploy the image containing the reviewed fix commit through the normal Mainnet release
pipeline. Do not run `kubectl apply`, restart a workload, or modify Elasticsearch manually as part
of this repository verification. The rollout owner must retain the release-system audit record.

## Read-Only Verification

Set the valid caller credential only in the local shell. The admin-only checks require an Aevatar
admin bearer; the default-scope checks require any authenticated bearer with one unambiguous
`scope_id`.

```bash
export AEVATAR_BASE_URL="https://aevatar-console-backend-api.aevatar.ai"
export AEVATAR_BEARER="<redacted>"
```

Verify the four formerly failing query shapes. Each command requires HTTP 200 and a JSON array at
`records`; the future window additionally requires an empty array.

```bash
curl --fail-with-body --silent --show-error \
  -H "Authorization: Bearer ${AEVATAR_BEARER}" \
  "${AEVATAR_BASE_URL}/api/audit/trail?take=1" \
  | jq -e '.records | type == "array"'

curl --fail-with-body --silent --show-error \
  -H "Authorization: Bearer ${AEVATAR_BEARER}" \
  "${AEVATAR_BASE_URL}/api/audit/trail?take=1&scope=__all__" \
  | jq -e '.records | type == "array"'

curl --fail-with-body --silent --show-error \
  -H "Authorization: Bearer ${AEVATAR_BEARER}" \
  "${AEVATAR_BASE_URL}/api/audit/trail?take=1&auditActorId=definitely-not-an-audit-actor" \
  | jq -e '.records | type == "array"'

curl --fail-with-body --silent --show-error \
  -H "Authorization: Bearer ${AEVATAR_BEARER}" \
  "${AEVATAR_BASE_URL}/api/audit/trail?take=1&from=2100-01-01T00%3A00%3A00Z&to=2100-01-02T00%3A00%3A00Z" \
  | jq -e '.records == []'
```

Verify health visibility and the inventory boundary:

```bash
curl --fail-with-body --silent --show-error \
  "${AEVATAR_BASE_URL}/api/status" \
  | jq -e '.targets[] | select(.slug == "audit-query-index") | .status == "ok"'

curl --fail-with-body --silent --show-error \
  -H "Authorization: Bearer ${AEVATAR_BEARER}" \
  "${AEVATAR_BASE_URL}/api/cqrs/readmodels" \
  | jq -e '[.groups[].items[].name | select(test("audit"; "i"))] | length == 0'
```

Finally, inspect only the new pod logs for the sanitized reconcile outcome. Absence of
`errorType` and presence of a completed reconcile are required; raw backend response bodies must
not appear.

```bash
KUBECONFIG="$HOME/Code/aelf-shared-k8s-prod.yaml" \
  kubectl -n aismart-app-mainnet logs -l app=aevatar-console-backend \
  --tail=-1 --since=30m --timestamps --all-containers=true \
  | rg 'audit-trail-current|artifact index reconcile|Projection index startup reconcile'
```

## Failure Handling

If the query returns `503 AUDIT_QUERY_UNAVAILABLE` or the status target is not `ok`, do not delete
or recreate either index. Record the deployment image, alias name, sanitized `errorType`, and UTC
time; stop or roll back through the normal release pipeline. Any later cleanup of retained legacy
or physical indices is a separate retention change with explicit approval, backup evidence, and
document-count verification.

Do not treat a successful Chat Activity delete-by-query as approval to remove a legacy or previous
physical index. Physical-index cleanup additionally requires rollback-window expiry and a separate
change record.
