# Local Metrics Display (Grafana)

This repository uses Prometheus + Grafana for local metrics display.

## 1. Start Workflow Host

Run the API host (default expected metrics endpoint: `http://localhost:5000/metrics`):

```bash
ASPNETCORE_URLS=http://localhost:5000 \
dotnet run --project src/workflow/Aevatar.Workflow.Host.Api
```

## 2. Start Observability Stack

```bash
docker compose -f docker-compose.observability.yml up -d
```

Services:

- Prometheus: `http://localhost:9090`
- Grafana: `http://localhost:3000` (`admin` / `admin`)

## 3. Validate Scraping

Open Prometheus targets page:

`http://localhost:9090/targets`

Expect `aevatar-workflow-host` to be `UP`.

## 4. Explore Metrics in Grafana

Open Grafana Explore and query:

- `aevatar_runtime_events_handled_total`
- `aevatar_runtime_event_handle_duration_ms`
- `aevatar_api_requests_total`
- `aevatar_api_request_duration_ms`

A provisioned dashboard is available after startup:

- Folder: `Aevatar`
- Dashboard: `Aevatar Runtime Overview`
- Includes `AI vs Core Runtime Latency`, `AI vs Core Throughput`, and `SSE/WS First Response vs Full`

## 5. Stop Stack

```bash
docker compose -f docker-compose.observability.yml down
```
