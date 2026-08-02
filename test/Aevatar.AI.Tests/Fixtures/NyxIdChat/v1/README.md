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
