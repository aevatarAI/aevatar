# Live Workflow Node Presentation Design

## Problem

The Run console currently consumes every normalized event yielded from an SSE
response and calls React state setters for each event. A single network chunk
can contain multiple complete node lifecycles. React batches those setter calls
into one browser render, so several node rows can appear together and the user
never sees the earlier nodes own the Running state.

The UI therefore implies that the network chunk is one execution transition,
while the runtime contract says that each `aevatar.step.request` is a distinct
node-start fact.

The console also currently uses runtime events to discover which nodes exist,
even though both execution surfaces already hold the exact submitted workflow
definition. This hides all future nodes until they start and makes the workflow
look as though it is being constructed during execution.

## Semantic Authority

The submitted workflow definition is authoritative for the node inventory,
identity, label, type, and order. The ordered normalized SSE events are
authoritative for execution attempts and their status, timing, input, and
output. In particular, `aevatar.step.request` starts one node attempt and
`aevatar.step.completed` completes that attempt. HTTP response chunks are only
transport envelopes and have no product meaning.

The live Run console must present every observed node-start fact before it
continues to another node-start fact from the same transport burst. It must not
invent a historical timeline, delay lifecycle diagnostics, or replay nodes
after the stream has already advanced.

## Presentation Boundary

A shared workflow presentation helper will run after the current event has
been applied to the accumulator and committed to React state.

- Non-node-start events return immediately.
- A node-start event waits across a browser paint boundary before the consumer
  asks the async iterator for another event.
- The browser boundary uses two animation-frame callbacks: React can commit
  before the first callback, one paint occurs, and stream consumption resumes
  on the following frame.
- There is no fixed millisecond dwell time. Fast workflows remain fast while
  every started node receives at least one rendered Running frame.
- Abort cancels pending frame callbacks and releases the waiter so route
  changes, stop actions, and superseding runs cannot strand the stream loop.
- Environments without animation frames return without fabricating a timer.

Both published Workflow Activity runs and Team Member Workflow Studio draft
runs use this helper. Chat and unrelated SSE consumers remain unchanged.

## Definition And Runtime Merge

The Nodes overview is built from the submitted definition snapshot before any
node event arrives. Definition nodes appear in definition order as nonselectable
`Pending` entries and do not manufacture log indexes, payloads, inputs, outputs,
or timestamps. When a runtime attempt for a definition node arrives, its real
log-backed entry replaces that node's placeholder. Additional attempts remain
visible as separate runtime entries under the same definition position.

After a run becomes terminal, definition nodes that never received a runtime
attempt become `Not run`. Runtime nodes absent from the submitted definition are
still appended after definition nodes so version skew remains observable rather
than being silently discarded.

## Interaction Contract

The Nodes behavior after each render is:

- every node from the submitted definition appears immediately;
- future nodes are `Pending`, while never-entered nodes in a terminal run are
  `Not run`;
- the newest running node is selected and highlighted;
- the active row scrolls into view without receiving DOM focus;
- manual inspection remains stable until a different node attempt starts;
- lifecycle Events remain explicit secondary diagnostics.

The scheduling helper changes when React may consume the next node boundary;
it does not change execution IDs, Activity reconciliation, output assembly,
status calculation, or backend contracts.

## Verification

A component test will assert that all submitted definition nodes are visible as
Pending before the first node event, then that runtime events promote only the
current node to Running and select it. A terminal-run case will assert that
unentered definition nodes become Not run. A page integration test will provide
multiple node request/completion events in one synchronous async-generator
burst. It will control animation-frame callbacks and assert that the first node
is Running while later nodes remain Pending before the paint boundary is
released, then that the second node becomes Running only after the first
boundary. Focused helper tests cover non-node events and cancellation. Existing
component tests continue to cover selection, scroll-follow, and the running
indicator.
