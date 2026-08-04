---
title: "Chat Activity Audit Retention"
status: active
owner: platform
---

# Chat Activity Audit Retention

## Scope and safety boundary

Chat Activity shares the existing `aevatar-audit-trail-current` Elasticsearch
alias. Its default retention is 30 days, measured from
`artifact.recorded_at`. The retention operation deletes a document only when
both conditions are true:

1. `artifact.recorded_at` is earlier than `now-30d/d`;
2. `artifact.record.provenance.chat.surface` exists.

The typed-provenance predicate keeps unrelated Audit Trail governance
artifacts out of this TTL. The operation does not inspect prompts, transcripts,
tool arguments/results, action parameters, subjects, credentials, or matched
documents, and it never prints document bodies.

This is an operator-owned maintenance action. It must not run in application
startup, an HTTP request, a query handler, or the Projection Pipeline. Cleanup
of legacy or previous fingerprinted physical indices is a separate approved
operation; this script targets only the stable active alias.

## Prerequisites and evidence

The operator environment needs Bash, `curl`, and `jq`. Configure:

- required `AEVATAR_ELASTICSEARCH_URL`;
- optional `AEVATAR_AUDIT_INDEX_ALIAS`, default
  `aevatar-audit-trail-current`;
- optional `AEVATAR_ELASTICSEARCH_API_KEY`, supplied by the deployment secret
  store; when absent, curl uses its configured netrc.

Never place credentials in a command line, repository file, shell history, or
change record. The dry-run identity needs only count/read access. The execution
identity additionally needs the narrow delete-by-query permission for the
audit alias; it does not need index creation, alias mutation, or cluster-admin
permissions.

Before enabling retention in staging, record:

- daily Chat Activity tool count and action count;
- current primary-store bytes and replica factor;
- measured indexed bytes per day and projected 30-day primary/replica bytes;
- at least one additional full physical-index copy of temporary headroom for
  fingerprinted schema reconciliation;
- the active alias target, expected schema fingerprint, source/target document
  counts, and backup or snapshot evidence.

Keep every HMAC audit identity key that wrote still-unexpired Chat Activity
configured for at least 30 days after its last write. Removing a retained key
early does not expose a subject, but it makes those personal records
intentionally undiscoverable by the user's multi-key query.

## Dry run

The script defaults to dry-run and calls `POST /<alias>/_count` with the exact
retention predicate:

```bash
tools/audit/retain_chat_activity.sh
```

The equivalent explicit invocation is:

```bash
tools/audit/retain_chat_activity.sh --dry-run
```

Record only the emitted mode, alias, cutoff, matched count, duration, and
status. Review the count against the staging daily-volume and capacity evidence.
A surprising count blocks execution; investigate mapping, alias, clock, and
ingestion state without changing or recreating the index.

## Governed execution

After operations approval, run once with the least-privilege execution
identity:

```bash
tools/audit/retain_chat_activity.sh --execute
```

Execution calls only:

```text
POST /<alias>/_delete_by_query?conflicts=proceed&wait_for_completion=true&refresh=false
```

It fails unless Elasticsearch reports `timed_out=false`, no per-document
failures, and numeric deleted/duration fields. Record the deleted count,
duration, cutoff, alias, UTC execution time, release commit, and approval
reference. Run the dry-run again; the remaining old typed-chat count must be
explained before a scheduled execution is enabled.

Schedule this operation through the deployment operations system only after
the one-shot staging and production reviews. The repository does not create a
timer, background service, or startup deletion path.

## Rollout and rollback

Roll out in this order:

1. deploy the typed chat mapping/query contract;
2. wait for fingerprinted copy-forward reconciliation;
3. verify the alias target, fingerprint, backup, and old/new counts;
4. generate and inspect sanitized NyxID/Workflow tool and NyxID action records;
5. run and review the retention dry-run;
6. obtain approval, execute once, verify the post-count, then enable the
   operator-owned schedule.

To stop retention, disable the external schedule; do not mutate application
startup or the query path. Deletion is not rolled back by redeploying the
application. If approved recovery is required, restore the verified backup to
a new physical index, validate counts and mapping, and atomically repoint the
alias using the audit index cutover procedure.

Previous physical-index cleanup remains separately gated by rollback expiry,
independent backup evidence, alias validation, and document-count comparison.
Never use this Chat Activity TTL as approval to delete a legacy or inactive
physical index.

## Verification

The local contract test uses a fake curl boundary and does not contact a
cluster:

```bash
bash tools/audit/tests/test_retain_chat_activity.sh
```

It verifies default dry-run, explicit execution, the exact scoped predicate,
safe alias validation, and absence of credentials or fake document content in
output. Cluster dry-run/execution evidence remains an operations responsibility
and is not produced by repository CI.

Related procedure: [Audit Trail Elasticsearch Index Cutover](2026-07-20-audit-trail-index-cutover.md).
