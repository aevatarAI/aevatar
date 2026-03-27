# Local Observability Stack

This repository uses an OpenTelemetry Collector in front of Prometheus, Grafana, and Jaeger for local observability.

## 1. Start Workflow Host

Run the API host with OTLP pointed at the local collector:

```bash
export OTEL_SERVICE_NAME=Aevatar.Workflow.Host.Api
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc

ASPNETCORE_URLS=http://localhost:5100 \
dotnet run --project src/workflow/Aevatar.Workflow.Host.Api
```

## 2. Start Observability Stack

```bash
docker compose -f docker-compose.observability.yml up -d
```

Services:

- OpenTelemetry Collector OTLP HTTP ingest endpoint: `http://localhost:4318` (ingest only, no UI)
- Prometheus: `http://localhost:9090`
- Grafana: `http://localhost:3000` (`admin` / `admin`)
- Jaeger: `http://localhost:16686`

## 3. Validate Scraping

Open Prometheus targets page:

`http://localhost:9090/targets`

Expect `aevatar-otel-collector` to be `UP`.

## 4. Explore Metrics in Grafana

Open Grafana Explore and query:

- `aevatar_runtime_events_handled_total`
- `aevatar_runtime_event_handle_duration_ms`
- `aevatar_api_requests_total`
- `aevatar_api_request_duration_ms`
- `aevatar_api_first_response_duration_ms`

A provisioned dashboard is available after startup:

- Folder: `Aevatar`
- Dashboard: `Aevatar Runtime Overview`
- Includes two sections:
  - SLO section (error ratio, first-response p95, full-request p95, runtime/api latency)
  - Runtime diagnostics section (self-direction runtime events, event amplification signals)

## 5. Stop Stack

```bash
docker compose -f docker-compose.observability.yml down
```

## 6. Alert Rules

Prometheus alert examples are provisioned from:

- `tools/observability/prometheus/alerts.yml`

Default examples:

- API error ratio > 1% for 10 minutes
- Runtime event error ratio > 1% for 10 minutes
- API first-response p95 > 5000 ms for 10 minutes

Inspect loaded rules in Prometheus:

- `http://localhost:9090/rules`

## 7. Histogram Buckets

The workflow host configures explicit histogram buckets for AI-oriented latency profiles:

- API request / first-response latency:
  - `25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 20000, 30000, 45000, 60000, 90000, 120000` ms
- Runtime event latency:
  - `1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000` ms

Override them with configuration values:

- `Observability:Metrics:ApiLatencyBucketsMs`
- `Observability:Metrics:RuntimeLatencyBucketsMs`

Use comma-separated millisecond values in ascending order.

## 8. Data Flow

The local metric and trace path is:

- Workflow host exports traces and metrics to OTLP (`localhost:4317`)
- OpenTelemetry Collector forwards traces to Jaeger
- OpenTelemetry Collector exposes Prometheus metrics on `:9464`
- Prometheus scrapes the collector and Grafana queries Prometheus

## 9. Docker Host Notes

The application runs on the host by default and exports OTLP to `localhost:4317`, which is published by the collector container.

If you change the collector port mapping, update `OTEL_EXPORTER_OTLP_ENDPOINT` before starting the workflow host.
