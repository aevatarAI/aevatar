# Workflow Jaeger Observability Runbook

## 1. Purpose

This document is the operational runbook for validating workflow tracing end to end.

Use this guide for:

- local verification
- CI smoke checks
- incident troubleshooting

For design rationale and propagation internals, use:

- `docs/architecture/stream-first-tracing-design.md`

## 2. Runtime Contract Snapshot

These are the required correlation keys across surfaces:

| Key | Surface | Meaning |
|---|---|---|
| `trace_id` | Jaeger + logs + API trace field | distributed trace identity |
| `correlation_id` | API + logs + ws payload | business run identity |
| `causation_id` | logs + envelope metadata | direct upstream event identity |

Expected API behavior:

- HTTP `/api/chat` sets `X-Trace-Id` and `X-Correlation-Id` when available.
- HTTP `202 Accepted` payload includes `traceId` and `correlationId`.
- WebSocket `/api/ws/chat` envelopes (`command.ack`, `agui.event`, `command.error`) include `traceId` and `correlationId`.

Expected runtime logging behavior:

- key handling logs include `trace_id`, `correlation_id`, and `causation_id`
- missing values degrade to empty string instead of throwing

## 3. Local Setup

Start Jaeger all-in-one:

```bash
docker run --rm --name jaeger \
  -e COLLECTOR_OTLP_ENABLED=true \
  -p 16686:16686 \
  -p 4317:4317 \
  -p 4318:4318 \
  jaegertracing/all-in-one:latest
```

Set OTEL environment variables before host startup:

```bash
export OTEL_SERVICE_NAME=Aevatar.Workflow.Host.Api
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

Optional sampling override:

```bash
export Observability__Tracing__SampleRatio=0.1
export OTEL_TRACES_SAMPLER_ARG=0.1
```

Start API host:

```bash
dotnet run --project src/workflow/Aevatar.Workflow.Host.Api
```

## 4. End-to-End Validation

Trigger request:

```bash
curl -i -X POST "http://localhost:5000/api/chat" \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "hello",
    "workflow": "direct"
  }'
```

Validate all checks:

1. Response headers include `X-Trace-Id` and `X-Correlation-Id`.
2. Response body includes `traceId` and `correlationId` for async accepted flow.
3. Runtime logs include `trace_id`, `correlation_id`, and `causation_id`.
4. Jaeger UI (`http://localhost:16686`) shows trace under `Aevatar.Workflow.Host.Api`.
5. `X-Trace-Id` equals Jaeger trace id and log `trace_id`.

## 5. Automated Test Checklist

Recommended minimum checks:

- HTTP accepted payload includes `traceId` and `correlationId`.
- HTTP/SSE path exposes `X-Trace-Id` and `X-Correlation-Id`.
- WebSocket parse errors include tracing fields.
- WebSocket execution failures include tracing fields for text and binary responses.
- runtime tracing helper tests cover scope construction and metadata fallback.

## 6. Troubleshooting

No traces in Jaeger:

- verify Jaeger container is healthy and OTLP ports are reachable
- verify OTEL environment variables are visible to host process
- verify sampling is not effectively zero

Missing `X-Trace-Id`:

- verify request runs inside ASP.NET Core Activity instrumentation
- verify host tracing registration is enabled

Logs missing `correlation_id` or `causation_id`:

- verify envelope propagation path is active
- verify runtime handling entry points are wrapped with tracing scope helper

WebSocket has trace but weak correlation:

- check whether failure happened before command start (fallback may use request-level correlation)

## 7. Related Documents

- Design source of truth: `docs/architecture/stream-first-tracing-design.md`
