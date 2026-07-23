# NyxID Chat Profile Rollout Matrix

The executable source is `NyxIdChatProfileRolloutEvaluationTests`. It contains 64 typed cases: eight scenario families crossed with Chinese/English, SHADOW/ENFORCED, and ready/degraded variants. This document is the review index, not a second runner.

| Family | Expected profile behavior |
|---|---|
| discovery | inventory/catalog/status intent; read-only policy |
| connect | typed authorization handoff eligibility; no credentials |
| call | exact connected instance; reviewed operation or request fallback |
| maintenance | exact target and approval for update/route/delete |
| ambiguity | no-match or bounded classifier; recovery tools only on degradation |
| continuation | authenticated correction/recheck/cancel; no replay acceptance |
| isolation | pinned profile across next turn/restart/concurrency; no cross-conversation leakage |
| regression | old conversation, workflow, relay, built-in floor, prompt/tool size and latency unchanged |

Promotion consumes one typed `AgentProfileEvaluationReport`. Required results are 64/64 invariants, at least 95% expected-match accuracy, no-match at most 5%, classifier timeout/error at most 1%, and zero unsafe admission, approval bypass, replay acceptance, secret telemetry, and SHADOW execution side effects. SHADOW p95 is at most 600 ms; ENFORCED total pre-turn p95 is at most 2100 ms; first-output regression is at most 10%.
