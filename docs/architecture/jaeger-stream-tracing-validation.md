# Jaeger Stream Tracing Validation Guide

## Goal

Validate that stream-only tracing data is visible end-to-end:

- API response includes `traceId` and `correlationId`
- Runtime logs include `trace_id`, `correlation_id`, `causation_id`
- Jaeger UI can search and display traces produced by workflow API requests

## 1. Start Jaeger (local all-in-one)

```bash
docker run --rm --name jaeger \
  -e COLLECTOR_OTLP_ENABLED=true \
  -p 16686:16686 \
  -p 4317:4317 \
  -p 4318:4318 \
  jaegertracing/all-in-one:latest
```

Open UI: `http://localhost:16686`

## 2. Configure workflow host OTLP export

Set environment variables before starting `Aevatar.Workflow.Host.Api`:

```bash
export OTEL_SERVICE_NAME=Aevatar.Workflow.Host.Api
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

Optional sampling overrides:

```bash
# Preferred app-level setting (0.0 ~ 1.0)
export Observability__Tracing__SampleRatio=0.1

# OTel-compatible fallback (used when app-level setting is not provided)
export OTEL_TRACES_SAMPLER_ARG=0.1
```

Sampling behavior in workflow host:

- Development default: `1.0` (record all root spans)
- Non-development default: `0.1`
- Sampler type: `ParentBased(TraceIdRatioBased)`
  - child spans follow parent decision
  - root spans use ratio-based sampling

## 3. Start workflow host

```bash
dotnet run --project src/workflow/Aevatar.Workflow.Host.Api
```

## 4. Trigger a trace

```bash
curl -i -X POST "http://localhost:5000/api/chat" \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "hello",
    "workflow": "direct"
  }'
```

Expected:

- Response headers contain `X-Trace-Id` and `X-Correlation-Id`
- Body stream contains run events

## 5. Validate runtime logs

Search for log entries that include:

- `trace_id`
- `correlation_id`
- `causation_id`

When request enters API boundary:

- `trace_id` should be present when `Activity.Current` exists
- `correlation_id` may be empty before command start, then populated after command start
- `causation_id` is expected to be empty at API entry

During stream event handling:

- `correlation_id` should stay consistent for the same business operation/run (it may default to command id at entry, but is not semantically limited to command id)
- `causation_id` should match direct upstream event id

## 6. Validate Jaeger UI

In Jaeger UI:

1. Select service `Aevatar.Workflow.Host.Api`
2. Click **Find Traces**
3. Open a trace and verify:
   - API span exists for `/api/chat`
   - Span trace id equals the `X-Trace-Id` returned by API
4. Correlate runtime logs with the same `trace_id`

## 7. Quick troubleshooting

- No traces in Jaeger:
  - verify Jaeger container is running
  - verify `OTEL_EXPORTER_OTLP_ENDPOINT` and protocol
  - verify host process has OTEL env vars
  - verify sampling ratio is not set to `0`
- Missing `X-Trace-Id`:
  - verify request path is covered by ASP.NET Core activity instrumentation
- Missing `correlation_id`/`causation_id` in runtime logs:
  - verify envelope propagation policy is active
  - verify runtime entry points use tracing scope helpers
