# Workflow Observability Guide (Jaeger + API + Logs)

## 1. Purpose

This guide provides one complete operational reference for workflow tracing:

- Jaeger setup and verification
- API tracing contract (HTTP and WebSocket)
- Runtime log correlation contract
- End-to-end validation checklist

Use this document when validating `stream-first tracing` behavior in development, CI smoke checks, or production troubleshooting.

## 2. Scope

This guide covers:

- `Aevatar.Workflow.Host.Api` API-layer tracing output
- runtime event handling tracing in Local and Orleans execution paths
- cross-surface correlation between API response, logs, and Jaeger trace view

Out of scope:

- custom `traceparent` propagation protocol design
- non-stream orchestration paths

## 3. Canonical Correlation Model

The system uses three orthogonal IDs:

| Key | Surface | Meaning |
|---|---|---|
| `trace_id` | OTel / Jaeger / logs | distributed trace identity |
| `correlation_id` | API + logs + ws payload | business operation identity (run-level) |
| `causation_id` | logs + envelope metadata | direct upstream event identity (one hop) |

Key principles:

- `trace_id` can change across retries/timers/new roots.
- `correlation_id` should remain stable for the same business run.
- `causation_id` forms the event DAG edge-by-edge.

## 4. API Contract

## 4.1 HTTP `/api/chat`

Expected response headers:

- `X-Trace-Id`: current request trace id (empty when no active Activity)
- `X-Correlation-Id`: populated after command start

For `202 Accepted` command-style responses, body includes:

- `commandId`
- `correlationId`
- `traceId`
- `actorId`

## 4.2 WebSocket `/api/ws/chat`

Supported envelope message types:

- `command.ack`
- `agui.event`
- `command.error`

All envelopes should carry tracing context fields:

- `traceId`
- `correlationId` (when known; may fallback to request-level correlation for early errors)

Minimal examples:

```json
{
  "type": "command.ack",
  "requestId": "req-1",
  "correlationId": "cmd-1",
  "traceId": "7f4d1d2b...",
  "payload": {
    "commandId": "cmd-1",
    "actorId": "actor-1",
    "workflow": "direct"
  }
}
```

```json
{
  "type": "command.error",
  "requestId": "req-1",
  "correlationId": "req-1",
  "traceId": "7f4d1d2b...",
  "code": "INVALID_COMMAND",
  "message": "Expected { type: 'chat.command', payload: { prompt, workflow?, workflowYaml?, agentId? } }."
}
```

## 5. Runtime Logging Contract

All key runtime handling logs should be scoped with:

- `trace_id`
- `correlation_id`
- `causation_id`

Expected behavior:

- missing values are represented as empty strings (never crash logging)
- scope is applied for both success and failure paths
- Local and Orleans use the same field names

Do we need to add these fields on every log line manually?

- No manual per-line field appending is required.
- Yes, a scope must exist at the entry point (`BeginScope` via tracing helpers).
- Logs outside a tracing scope are not guaranteed to carry `correlation_id` and `causation_id`.
- `trace_id` may appear globally in some providers, but this is provider/config dependent; do not rely on that as the only correlation path.

## 5.1 Log Samples

API command log (inside API scope):

```json
{
  "Timestamp": "2026-03-04T10:52:30.104Z",
  "Level": "Information",
  "Category": "Aevatar.Workflow.Host.Api.Command",
  "Message": "Workflow command accepted.",
  "trace_id": "7f4d1d2b4df0f9d10e9d4f6b2f7b0132",
  "correlation_id": "cmd-1",
  "causation_id": ""
}
```

Runtime event handling log (inside envelope scope):

```json
{
  "Timestamp": "2026-03-04T10:52:30.311Z",
  "Level": "Debug",
  "Category": "RuntimeActorGrain",
  "Message": "Handled envelope.",
  "trace_id": "7f4d1d2b4df0f9d10e9d4f6b2f7b0132",
  "correlation_id": "cmd-1",
  "causation_id": "evt-upstream-23"
}
```

Framework-level trace fields (like `TraceId`/`SpanId`) may also appear depending on logger provider configuration, but the contract fields above are the stable cross-surface keys used by this project.

## 6. Jaeger Setup (Local)

Start Jaeger all-in-one:

```bash
docker run --rm --name jaeger \
  -e COLLECTOR_OTLP_ENABLED=true \
  -p 16686:16686 \
  -p 4317:4317 \
  -p 4318:4318 \
  jaegertracing/all-in-one:latest
```

Export OTEL variables before host startup:

```bash
export OTEL_SERVICE_NAME=Aevatar.Workflow.Host.Api
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

Optional sampling overrides:

```bash
export Observability__Tracing__SampleRatio=0.1
export OTEL_TRACES_SAMPLER_ARG=0.1
```

Start API host:

```bash
dotnet run --project src/workflow/Aevatar.Workflow.Host.Api
```

## 7. End-to-End Validation Flow

Trigger a request:

```bash
curl -i -X POST "http://localhost:5000/api/chat" \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "hello",
    "workflow": "direct"
  }'
```

Then validate:

1. API response headers include `X-Trace-Id` and `X-Correlation-Id`.
2. Runtime logs include `trace_id`, `correlation_id`, `causation_id`.
3. Jaeger (`http://localhost:16686`) shows trace under `Aevatar.Workflow.Host.Api`.
4. `X-Trace-Id` from API equals Jaeger trace id and log `trace_id`.

## 8. Test Coverage Checklist

Recommended minimum automated checks:

- HTTP accepted payload contains `traceId` and `correlationId`.
- HTTP SSE path exposes `X-Trace-Id` and `X-Correlation-Id`.
- WebSocket parse error includes tracing fields.
- WebSocket execution failure includes tracing fields for both text and binary response types.
- Runtime tracing helper tests verify log scope construction and metadata fallback behavior.

## 9. Troubleshooting

No traces in Jaeger:

- confirm Jaeger container and OTLP ports are up
- confirm process env vars are applied to host process
- confirm sampling is not effectively `0`

Missing `X-Trace-Id`:

- verify request is inside ASP.NET Core Activity instrumentation scope
- verify host tracing registration is enabled

Logs missing `correlation_id` or `causation_id`:

- verify envelope propagation path is active
- verify runtime handling entry points use tracing scope helper

WebSocket has trace but weak correlation:

- check if failure happened before command start (fallback correlation may be request-level)

## 10. Related Documents

- `docs/architecture/stream-first-tracing-design.md`
- `docs/architecture/jaeger-stream-tracing-validation.md`

