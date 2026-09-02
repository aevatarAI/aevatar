# Workflow Studio Canvas Visual Evidence

This evidence closes the review gap:

```text
narrative-over-verification: desktop/tablet/mobile evidence is generated from static `workflow-studio-canvas-evidence.html`, not the actual Workflow Studio editor (`diff.patch:17-22`, `diff.patch:35-46`).
```

Graph-editor readability is a rendered viewport fact: node hierarchy, ports,
directed edges, minimap weight, canvas controls, and first-frame comprehension
must be checked in a browser viewport, not only through `GraphCanvas.test.tsx`
prop assertions.

The captures below were rendered from the actual Umi route:

`/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha`

That route mounts the production `TeamMemberWorkflowStudioPage` component tree,
including `WorkflowStudioHeader`, `WorkflowStudioCanvas`, `GraphCanvas`, and
`StudioWorkflowNode`. The local Studio API responses used distinct identities:
`memberId = "m-alpha"`, `workflowId = "wf-alpha"`, and
`publishedServiceId = "svc-alpha"`.

## Captures

- `desktop`: [workflow-studio-canvas-desktop-1280x720.png](workflow-studio-canvas-desktop-1280x720.png)
- `tablet`: [workflow-studio-canvas-tablet-768x1024.png](workflow-studio-canvas-tablet-768x1024.png)
- `mobile`: [workflow-studio-canvas-mobile-375x812.png](workflow-studio-canvas-mobile-375x812.png)

## Capture Command

Captured locally on 2026-07-22 with Google Chrome headless from the repository
root. A throwaway local Studio API served only the route data above on
`http://127.0.0.1:15180`, and the real console dev server was started with both
API targets pointed at that mock API:

```sh
AEVATAR_API_TARGET=http://127.0.0.1:15180 \
AEVATAR_STUDIO_API_TARGET=http://127.0.0.1:15180 \
PORT=5174 \
UMI_ENV=dev \
pnpm --dir apps/aevatar-console-web start
```

Replace `1280,720` and the output filename for the other viewports.

```sh
CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
URL="http://127.0.0.1:5174/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha"
OUT="apps/aevatar-console-web/docs/visual-evidence/2026-07-22-workflow-studio-canvas/workflow-studio-canvas-desktop-1280x720.png"
"$CHROME" \
  --headless=new \
  --disable-gpu \
  --hide-scrollbars \
  --no-first-run \
  --window-size=1280,720 \
  --timeout=12000 \
  --screenshot="$OUT" \
  "$URL"
```
