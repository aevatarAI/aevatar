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

## Semantic Authority

The ordered normalized SSE events are authoritative for live execution. In
particular, `aevatar.step.request` starts one node attempt and
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

## Interaction Contract

The existing Nodes behavior remains authoritative after each render:

- only nodes with execution facts appear;
- the newest running node is selected and highlighted;
- the active row scrolls into view without receiving DOM focus;
- manual inspection remains stable until a different node attempt starts;
- lifecycle Events remain explicit secondary diagnostics.

The scheduling helper changes when React may consume the next node boundary;
it does not change execution IDs, Activity reconciliation, output assembly,
status calculation, or backend contracts.

## Verification

A page integration test will provide multiple node request/completion events in
one synchronous async-generator burst. It will control animation-frame
callbacks and assert that only the first node is visible and Running before the
paint boundary is released, then that the second node appears and becomes
Running only after the first boundary. Focused helper tests cover non-node
events and cancellation. Existing component tests continue to cover selection,
scroll-follow, and the running indicator.
