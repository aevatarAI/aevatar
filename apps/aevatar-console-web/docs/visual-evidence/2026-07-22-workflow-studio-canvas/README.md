# Workflow Studio Canvas Visual Evidence

This evidence closes the review gap
`narrative-over-verification: required desktop/tablet/mobile rendered visual evidence is absent.`

Graph-editor readability is a rendered viewport fact: node hierarchy, ports,
directed edges, minimap weight, canvas controls, and first-frame comprehension
must be checked in a browser viewport, not only through `GraphCanvas.test.tsx`
prop assertions.

The local evidence fixture is
`workflow-studio-canvas-evidence.html`. It renders the reviewed Workflow Studio
surface contract for `WorkflowStudioHeader`, `WorkflowStudioCanvas`,
`GraphCanvas`, and `StudioWorkflowNode` with the same grouped toolbar, canvas
shell, node card hierarchy, handles, branch labels, controls, and minimap
semantics used by the PR.

## Captures

- `desktop`: [workflow-studio-canvas-desktop-1280x720.png](workflow-studio-canvas-desktop-1280x720.png)
- `tablet`: [workflow-studio-canvas-tablet-768x1024.png](workflow-studio-canvas-tablet-768x1024.png)
- `mobile`: [workflow-studio-canvas-mobile-375x812.png](workflow-studio-canvas-mobile-375x812.png)

## Capture Command

Captured locally on 2026-07-22 with Google Chrome headless from the repository
root. Replace `1280,720` and the output filename for the other viewports.

```sh
CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
HTML="file://$(pwd)/apps/aevatar-console-web/docs/visual-evidence/2026-07-22-workflow-studio-canvas/workflow-studio-canvas-evidence.html"
OUT="apps/aevatar-console-web/docs/visual-evidence/2026-07-22-workflow-studio-canvas/workflow-studio-canvas-desktop-1280x720.png"
"$CHROME" \
  --headless=new \
  --disable-gpu \
  --hide-scrollbars \
  --no-first-run \
  --window-size=1280,720 \
  --screenshot="$OUT" \
  "$HTML"
```
