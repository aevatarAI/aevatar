# Scripts Studio Expansion Plan

## Context

`Scripts Studio` currently behaves like a single-file draft editor with three end-user actions:

- validate draft source
- run draft source
- save / promote the current source

The scripting module underneath already supports a broader surface:

- multi-file script packages (`csharp_sources`, `proto_files`, entry source/type)
- definition snapshots with contract metadata and runtime semantics
- catalog history (`active_revision`, `previous_revision`, `revision_history`, `last_proposal_id`)
- runtime snapshot listing
- evolution decision lookup and rollback semantics
- native document / graph projections

The page should evolve from a draft-only editor into a full scripting workbench without breaking the current local-draft and scope-backed flows.

## North Star

The target experience is a three-pane scripting workbench:

- left rail: drafts, saved scripts, runtimes, proposals
- center workbench: source/package/contract/runtime/governance tabs
- right inspector: selected object metadata and scope details

The implementation should stay incremental. Each milestone must be independently mergeable and must leave the existing flows usable.

## Milestone 1

### Goal

Rebuild the page skeleton and split the current monolithic component into reusable front-end pieces, while preserving the existing feature set and current `app/scripts` API shape.

### Scope

- keep existing back-end endpoints unchanged
- keep current flows unchanged: validate, run, save, promote, ask-ai, scope library
- introduce a persistent desktop workbench skeleton instead of relying only on modals
- extract reusable UI pieces from the current `ScriptsStudio.tsx`

### Deliverables

- `scripts-studio/` front-end module directory
- reusable components for:
  - resource rail
  - inspector panel
  - shared studio chrome (empty state, modal shell, result card)
- desktop three-pane layout:
  - left resource rail
  - center editor surface
  - right inspector
- modal fallback retained for smaller screens and existing flows

### Acceptance Criteria

- desktop view shows drafts and saved scripts in a persistent left rail
- desktop view shows identity / actor / contract / scope details in a persistent right inspector
- the active editor, Ask AI surface, validation problems, runtime activity, save state, and promotion state keep working
- existing local draft storage behavior is preserved
- existing scope-backed load/save/promote behavior is preserved
- current Ask AI floating panel behavior remains intact
- `ScriptsStudio.tsx` no longer owns all reusable UI sections inline

### Integration Tests

- `npm run build` in `tools/Aevatar.Tools.Cli/Frontend`
- `dotnet test test/Aevatar.App.Tests/Aevatar.App.Tests.csproj --nologo`
- smoke verify existing app API flows still pass:
  - `/api/app/scripts/validate`
  - `/api/app/scripts/draft-run`
  - `/api/app/scripts`
  - `/api/app/scripts/evolutions/proposals`

## Milestone 2

### Goal

Expose existing runtime, catalog, and evolution-read capabilities already present in the scripting stack.

### Scope

- no package-editor change yet
- add read-oriented `app` endpoints only
- extend the page from “last action result” to “browseable scripting state”

### Deliverables

- `GET /api/app/scripts/runtimes`
- `GET /api/app/scripts/{scriptId}/catalog`
- `GET /api/app/scripts/evolutions/{proposalId}`
- resource rail groups for:
  - runtimes
  - proposals
- runtime browsing view for arbitrary runtime snapshots
- governance browsing view for terminal proposal decisions
- saved script history view showing revision lineage

### Acceptance Criteria

- users can inspect runtime instances without rerunning the current draft
- users can inspect catalog history for a saved script
- users can look up proposal decisions after the original promote request finishes
- current save/run/promote flows continue to work without behavior regressions

### Integration Tests

- `npm run build` in `tools/Aevatar.Tools.Cli/Frontend`
- `dotnet test test/Aevatar.App.Tests/Aevatar.App.Tests.csproj --nologo`
- add app integration coverage for:
  - runtime list endpoint
  - catalog history endpoint
  - proposal decision lookup endpoint

## Milestone 3

### Goal

Upgrade the editor from a single `source` string into a multi-file script package workbench.

### Scope

- add V2 request models for validate, draft-run, save, and generator
- keep V1 compatibility while the front-end migrates
- move editor state to `ScriptPackageSpec`-shaped data

### Deliverables

- package file tree
- multi-file Monaco switching
- C# source files and proto files in the same draft
- package-aware Ask AI generation
- package-aware validation diagnostics

### Acceptance Criteria

- the page can create, edit, rename, and delete multiple C# and proto files
- validation diagnostics can point to multiple files, not only `Behavior.cs`
- draft run and save operate on the package, not on a flattened single-source string
- entry source path and entry behavior type are visible and editable

### Integration Tests

- `npm run build` in `tools/Aevatar.Tools.Cli/Frontend`
- `dotnet test test/Aevatar.App.Tests/Aevatar.App.Tests.csproj --nologo`
- add app integration coverage for:
  - package-based validate
  - package-based draft-run
  - package-based save
  - package-based generator roundtrip

## Milestone 4

### Goal

Expose the remaining contract, governance, and projection features so the page becomes a full scripting control plane.

### Scope

- definition snapshot lookup
- rollback action
- native document / graph projection inspection

### Deliverables

- `GET /api/app/scripts/{scriptId}/revisions/{revision}`
- `POST /api/app/scripts/{scriptId}:rollback`
- `GET /api/app/scripts/runtimes/{actorId}/native-document`
- `GET /api/app/scripts/runtimes/{actorId}/native-graph`
- contract tab with:
  - type urls
  - descriptor names
  - schema version / hash
  - runtime semantics
- governance tab with:
  - proposal timeline
  - base/candidate diff
  - rollback action
- projection views for native document and graph outputs

### Acceptance Criteria

- users can inspect a revision’s compiled contract and schema metadata
- users can rollback a promoted script revision from the UI
- users can inspect native projection outputs when a script emits them
- the page supports editing, validating, running, saving, promoting, rollback, and projection inspection in one place

### Integration Tests

- `npm run build` in `tools/Aevatar.Tools.Cli/Frontend`
- `dotnet test test/Aevatar.App.Tests/Aevatar.App.Tests.csproj --nologo`
- add app integration coverage for:
  - revision snapshot lookup
  - rollback endpoint
  - native document projection lookup
  - native graph projection lookup

## Implementation Order

1. milestone 1: front-end shell and component split
2. milestone 2: runtime/catalog/governance browseability
3. milestone 3: package editor and V2 request models
4. milestone 4: rollback, contract deep inspection, native projections

This order keeps the user-visible value high while minimizing the risk of mixing large UI refactors with protocol changes in the same pull request.
