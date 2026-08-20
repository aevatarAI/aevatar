# NyxID Chat attention fixtures v1

These are exact JSON shapes from the production `POST /api/chat`,
`GET /api/chat/conversations/{conversationId}/state`, and
`GET /api/chat/conversations` surfaces. They record one committed pending-input
fact at actor version 23 so browser and reducer implementations can verify that
live observation, current-state recovery, and conversation-list attention
converge after stream loss or reconnect.

Contract evidence:

- [Aevatar issue #3131](https://github.com/aevatarAI/aevatar/issues/3131)
- [nyxid-chat issue #6](https://github.com/eanz17/nyxid-chat/issues/6)

HTTP command acceptance remains transport-only. These fixtures represent
committed projection/read-model visibility, not the response to dispatch.

`needs-you-live-frames.json`, `needs-you-current-states.json`, and
`needs-you-conversation-summaries.json` freeze all four committed needs-you
transitions. The request scenarios retain their pending fact and matching
attention. The changed scenarios clear that fact, expose the matching latest
resolution, and remove attention while the exact resumed step is running.

Additional contract evidence:

- [Aevatar issue #3154](https://github.com/aevatarAI/aevatar/issues/3154)

`tool-recovery-live-frames.json` and `tool-recovery-current-states.json`
freeze matching failed and uncertain tool-step recovery facts. The exact
producer-authored `source.tool.readinessCapabilityId` converges with service
identity, status, external-effect evidence, and actor-computed available
actions. These fixtures intentionally contain no credentials, URLs, raw tool
arguments, or results.

Additional recovery contract evidence:

- [Aevatar issue #3152](https://github.com/aevatarAI/aevatar/issues/3152)
- [NyxID issue #1307](https://github.com/ChronoAIProject/NyxID/issues/1307)

`task-plan-live-frames.json` and `task-plan-current-state.json` freeze the full
TaskPlan v1 shape. The snapshot frame and current-state `activeTask` are
field-for-field identical, while the step-changed frame carries the complete
`taskId / planRevision / step / changeKind` envelope. The fixture covers every
v1 step source, including producer-authored tool readiness, approval, reserved
web, and the postcondition `check` contract.
`uc2-research-journey.json` freezes target journey UC2 from support-spec gist
revision `f45febb057a7182dab2495d4c739d2bb8d7026f5`. It keeps `task-uc2` stable
across steering while changing the continuation turn, preserves the completed
`web_search` evidence and substeps, fences the stopped task with a no-effect
partial receipt, and starts `turn-uc2b-1 / task-uc2b` as a distinct task. The
final artifact is ordinary assistant content, not a new wire object; the
fixture records its verified/cannot-check/no-reservation obligations for
deterministic conformance tests.
