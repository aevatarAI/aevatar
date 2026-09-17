---
title: "Admin-Only Workflow Observatory"
status: approved
owner: eanzhao
---

# Admin-Only Workflow Observatory

## Decision

The UI implied that Workflow Observatory was both a standalone product page and an admin module, while operators
expect run inspection and approval to belong to the admin console. `/admin#/observatory` is therefore the only
user-facing owner. `/workflow/observatory` and its callback route are removed.

## Runtime Shape

The existing renderer remains a single embedded static asset so run state, polling, graph rendering, approval
actions, and authentication refresh are not duplicated in `admin.html`. The admin shell loads it from the internal
same-origin `/admin/workflow-observatory` frame route and owns the visible URL. Opening the frame route as a
top-level document returns to `/admin#/observatory` while preserving only the supported observatory query fields.

All Studio, Skills, schedule, voice, CQRS, provisioning receipt, and workflow prompt links use the admin hash
route. The read APIs remain under `/api/workflow/observatory/*`; this migration changes UI ownership, not the
read-model or command boundaries.

## Verification Contract

- `/workflow/observatory` and `/workflow/observatory/callback` return 404.
- `/admin#/observatory` embeds the one renderer from `/admin/workflow-observatory`.
- Direct top-level frame navigation returns to the admin hash route.
- Human and tool approvals remain owner-only and use typed resume identities.
- Static-asset tests execute the shipped JavaScript and reject stale standalone links.
