---
title: "Workflow YAML Resource Limits"
status: "Implemented and verified"
owner: eanzhao
issue: 3041
---

# Workflow YAML Resource Limits

## Context

Workflow YAML currently reaches YamlDotNet without a size, node-count, or nesting-depth
limit. `WorkflowParser` first builds a `YamlStream` and then recursively deserializes a
typed object graph. Studio has a second `YamlStream` parser for its editable document
model. A sufficiently deep `steps[].children` chain can therefore overflow the process
stack before normal endpoint exception handling can return a validation response.

The vulnerable data flow is broader than one HTTP endpoint:

- Chat inline workflow documents use `IWorkflowDefinitionParser`.
- Studio save and provisioning use `IWorkflowDefinitionParser`.
- Studio validate and editor parsing also use `IWorkflowYamlDocumentService`.
- service revision and binding paths use `IWorkflowDefinitionParser`.
- fork resolution uses `IWorkflowDefinitionParser`.
- dynamic workflow and workflow validation modules call `WorkflowParser` directly.

The limit must therefore live at the YAML parsing boundary rather than in individual
controllers or request-size middleware.

## Goal

Reject excessive workflow YAML before YamlDotNet builds a representation model or
recursively deserializes objects. Every workflow YAML ingress must enforce the same
fixed contract:

| Resource | Maximum |
| --- | ---: |
| UTF-8 encoded bytes | 1,048,576 |
| YAML nodes | 10,000 |
| collection nesting depth | 64 |

The thresholds are hard safety invariants, not deployment configuration. A host must
not be able to weaken them accidentally.

## Non-Goals

- Do not add HTTP-only request limits as the authoritative defense.
- Do not catch `StackOverflowException`.
- Do not change workflow execution-depth or `workflow_call` recursion policy.
- Do not redesign the workflow schema or remove valid YAML syntax.
- Do not add a third workflow parser or a parallel validation pipeline.
- Do not modify non-workflow YAML consumers in AI or configuration modules.

## Chosen Architecture

### Streaming preflight

Add one `WorkflowYamlResourceGuard` in `Aevatar.Workflow.Core.Primitives`. It performs
four checks in order:

1. Count UTF-8 bytes without allocating a second encoded copy and reject values over
   1 MiB.
2. Read YamlDotNet parsing events sequentially from `YamlDotNet.Core.Parser`.
3. Count scalar, alias, mapping, and sequence nodes while tracking open mapping and
   sequence events as collection depth.
4. Record a bounded node/anchor graph and iteratively evaluate alias-expanded node
   count and collection depth before any object graph is created.

Unresolved alias events are retained until the end of their YAML document, then
resolved against that document's final anchor table. This covers forward aliases while
preserving encounter-time targets for aliases whose anchors were already known. Anchor
tables never cross document boundaries, and genuinely missing anchors remain unresolved
so existing YamlDotNet syntax handling remains authoritative.

The guard stops at the first exceeded limit. Its compact graph can contain at most
10,000 syntactic nodes, and expanded traversal stops at node 10,001. It does not build
a `YamlStream`, create a typed object graph, or recursively walk YAML nodes. YAML syntax
errors remain YamlDotNet syntax errors and continue through existing validation
handling.

`WorkflowParser.Parse` invokes the guard before `ValidateRootSchema` and typed
deserialization. This protects all runtime, Chat, service-revision, fork, and dynamic
workflow paths, including direct `WorkflowParser` callers.

`YamlWorkflowDocumentService.Parse` invokes the same guard before its own
`YamlStream.Load`. Studio therefore cannot bypass the runtime boundary through its
editable document parser.

### Typed failure contract

The guard throws `WorkflowYamlResourceLimitException`, an
`InvalidOperationException` subtype with a strong `LimitKind`, `Actual`, and `Maximum`
contract. Supported kinds are `Utf8Bytes`, `Nodes`, and `NestingDepth`.

`WorkflowDefinitionParser` catches this exception before its generic catch and returns
a failed `WorkflowYamlParseResult` classified as `ResourceLimit`. The inline-bundle
result preserves the same classification. Existing Chat and fork result mapping still
returns their typed `InvalidWorkflowYaml` 4xx response; service revision and Studio
save continue through their existing validation-to-4xx boundary without exposing a
500.

`YamlWorkflowDocumentService` converts the same exception into a Studio
`ValidationFinding` with code `yaml_resource_limit`, path `/`, and no document. This
keeps Studio validate responses structured and prevents later recursive mapping.

Dynamic workflow modules already convert parser exceptions into deterministic YAML
validation failures. The strong exception message remains safe for that path while the
exception fields remain available to typed adapters.

### Node and depth semantics

A mapping or sequence start counts as one syntactic node and increments collection
depth. A scalar or alias counts as one syntactic node. Mapping and sequence end events
decrement depth. Stream and document framing events do not count as nodes or depth.

After this first pass, scalar aliases still count as one expanded node. Collection
aliases count as the referenced collection and all descendants for each traversal.
Alias cycles are treated as unbounded collection depth and rejected as
`NestingDepth`; acyclic alias graphs that expand beyond 10,000 traversed nodes are
rejected as `Nodes`. The expansion check uses an explicit stack and an active
collection set, so hostile aliases cannot move recursion into the guard itself.

Depth 64 is accepted; depth 65 is rejected. Node count 10,000 and byte count 1 MiB are
accepted; the first value above either maximum is rejected. These inclusive boundaries
make regression tests deterministic.

## Error Handling

- Empty and malformed YAML retain their current error behavior.
- Resource-limit failures are deterministic validation failures, never internal errors.
- Cancellation behavior is unchanged because parsing remains synchronous and bounded.
- No code attempts to recover from or catch a process-level stack overflow.
- Error messages identify the exceeded resource, actual count, and configured maximum
  without echoing submitted YAML.

## Testing

Tests follow red-green-refactor order.

Core parser tests:

- a normal nested workflow below the depth limit parses successfully;
- a `steps[].children` chain at depth 65 fails with
  `WorkflowYamlResourceLimitException` before deserialization;
- a document above 10,000 nodes fails with the node classification;
- a document above 1 MiB fails with the byte classification;
- exact boundary values remain accepted where a syntactically valid fixture can express
  them cheaply.
- a cyclic collection alias fails before runtime or Studio recursive mapping;
- a forward-alias cycle fails before runtime or Studio recursive mapping;
- an acyclic alias graph whose expansion exceeds 10,000 nodes fails as `Nodes`;
- a forward acyclic alias graph whose expansion exceeds 10,000 nodes fails as `Nodes`;
- scalar aliases remain valid, malformed YAML remains a syntax error, and node counts
  accumulate across YAML documents.

Adapter and ingress tests:

- `WorkflowDefinitionParser` returns `ResourceLimit` rather than a generic untyped
  parse failure;
- inline workflow bundles preserve the resource-limit classification;
- Studio document parsing returns `yaml_resource_limit` and no document;
- a normal Studio nested workflow remains valid;
- dynamic workflow validation reports a normal validation error instead of escaping an
  exception.

Focused tests run first. The final verification includes affected projects, full build,
full test, test-stability guards, architecture guards, and GitHub CI.

## Compatibility and Rollout

Valid workflows within all three limits, including scalar aliases and bounded acyclic
collection aliases, keep their current parsing and serialization behavior. Workflows
exceeding a limit or containing a collection-alias cycle become invalid at every
ingress, including previously persisted YAML when it is parsed again. This fail-closed
behavior is intentional because such content cannot be processed safely.

No migration, backfill, feature flag, or compatibility fallback is introduced.
