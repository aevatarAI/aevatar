# Aevatar Inspector Demo

Local developer visualizer for the aevatar actor runtime.

```bash
dotnet run --project demos/Aevatar.Demos.Inspector -- --no-browser
```

Default URL: `http://localhost:5100`.

The backend keeps the plan's two-tier split:

- Tier 1 REST endpoints read projection readmodels/query ports:
  `/api/inspector/actors`, `/workflow-runs`, `/readmodels`, and `/readmodels/{name}`.
- Tier 2 `/api/inspector/events` is live-only SSE fed by `Aevatar.Agents`
  OpenTelemetry activities. It is animation data, not a query source.

To populate the local view without an LLM provider:

```bash
curl -X POST http://localhost:5100/api/inspector/demo/hierarchy
```

The demo creates a parent/child local actor pair, registers them through the
registry actor readmodel path, links them, and sends one message so the SSE
stream can animate the pulse.
