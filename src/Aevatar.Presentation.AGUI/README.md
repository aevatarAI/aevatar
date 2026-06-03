# Aevatar.Presentation.AGUI

`Aevatar.Presentation.AGUI` provides the HTTP/SSE adapter for AG-UI events.

## Responsibilities

- Provides the SSE writer `AGUISseWriter`.
- Consumes `AGUIEvent` frames published by upstream CQRS/projection flows.

## Core Types

- `AGUIEvent`: generated from `src/Aevatar.AGUI.Contracts/agui_events.proto`.
- `AGUISseWriter`: serializes `AGUIEvent` as `data: {json}\n\n` SSE output.

## Usage

- API layers push `AGUIEvent` frames to clients over SSE after receiving them from CQRS/projection interaction streams.

## Dependencies

- `Aevatar.AGUI.Contracts`
- `Microsoft.AspNetCore.App` framework reference
