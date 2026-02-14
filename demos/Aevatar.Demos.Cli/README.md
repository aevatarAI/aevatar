# Aevatar Demo CLI

Scenario-driven CLI to showcase Aevatar runtime behavior with a shared event log model.

## Commands

- `dotnet run --project demos/Aevatar.Demos.Cli -- list`
- `dotnet run --project demos/Aevatar.Demos.Cli -- run hierarchy`
- `dotnet run --project demos/Aevatar.Demos.Cli -- run fanout`
- `dotnet run --project demos/Aevatar.Demos.Cli -- run pipeline`
- `dotnet run --project demos/Aevatar.Demos.Cli -- run hooks`
- `dotnet run --project demos/Aevatar.Demos.Cli -- run lifecycle`

## JSON + Web report

- `dotnet run --project demos/Aevatar.Demos.Cli -- run hierarchy --web artifacts/demo/hierarchy.html`
- `dotnet run --project demos/Aevatar.Demos.Cli -- web artifacts/demo/hierarchy.json --out artifacts/demo/hierarchy.html`

All scenarios emit a single JSON timeline model that is rendered both in CLI output and HTML report.
