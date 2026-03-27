import { screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../../../tests/reactQueryTestUtils';
import type {
  ScriptCatalogSnapshot,
  ScriptPromotionDecision,
  ScriptReadModelSnapshot,
  ScriptValidationResult,
  ScopedScriptDetail,
} from '@/shared/studio/scriptsModels';
import ScriptResultsPanel from './ScriptResultsPanel';

const validationResult: ScriptValidationResult = {
  success: false,
  scriptId: 'script-1',
  scriptRevision: 'rev-1',
  primarySourcePath: 'Behavior.cs',
  errorCount: 1,
  warningCount: 0,
  diagnostics: [
    {
      severity: 'error',
      code: 'CS1002',
      message: 'Semicolon expected',
      filePath: 'Behavior.cs',
      startLine: 12,
      startColumn: 4,
      endLine: 12,
      endColumn: 5,
      origin: 'compiler',
    },
  ],
};

const runtimeSnapshot: ScriptReadModelSnapshot = {
  actorId: 'runtime-1',
  scriptId: 'script-1',
  definitionActorId: 'definition-1',
  revision: 'rev-1',
  readModelTypeUrl: 'type.googleapis.com/example.ReadModel',
  readModelPayloadJson: '{"input":"hello","output":"HELLO","status":"ok","last_command_id":"cmd-1","notes":["trimmed"]}',
  stateVersion: 3,
  lastEventId: 'event-1',
  updatedAt: '2026-03-23T00:00:00Z',
};

const scopeDetail: ScopedScriptDetail = {
  available: true,
  scopeId: 'scope-1',
  script: {
    scopeId: 'scope-1',
    scriptId: 'script-1',
    catalogActorId: 'catalog-1',
    definitionActorId: 'definition-1',
    activeRevision: 'rev-1',
    activeSourceHash: 'hash-1',
    updatedAt: '2026-03-23T00:00:00Z',
  },
  source: null,
};

const promotionDecision: ScriptPromotionDecision = {
  accepted: true,
  proposalId: 'proposal-1',
  scriptId: 'script-1',
  baseRevision: 'rev-1',
  candidateRevision: 'rev-2',
  status: 'accepted',
  failureReason: '',
  definitionActorId: 'definition-1',
  catalogActorId: 'catalog-1',
  validationReport: {
    isSuccess: true,
    diagnostics: [],
  },
  definitionSnapshot: {
    scriptId: 'script-1',
    revision: 'rev-2',
    sourceText: 'using System;',
    sourceHash: 'hash-2',
    scriptPackage: {
      format: 'aevatar-script-package@1',
      csharpSources: [
        {
          path: 'Behavior.cs',
          content: 'using System;',
        },
      ],
      protoFiles: [
        {
          path: 'schema.proto',
          content: 'syntax = "proto3";',
        },
      ],
      entryBehaviorTypeName: 'DraftBehavior',
      entrySourcePath: 'Behavior.cs',
    },
    stateTypeUrl: 'type.googleapis.com/example.State',
    readModelTypeUrl: 'type.googleapis.com/example.ReadModel',
    readModelSchemaVersion: '2',
    readModelSchemaHash: 'schema-hash-2',
    stateDescriptorFullName: 'example.State',
    readModelDescriptorFullName: 'example.ReadModel',
  },
};

const selectedCatalog: ScriptCatalogSnapshot = {
  scriptId: 'script-1',
  activeRevision: 'rev-1',
  activeDefinitionActorId: 'definition-1',
  activeSourceHash: 'hash-1',
  previousRevision: 'rev-0',
  revisionHistory: ['rev-0', 'rev-1'],
  lastProposalId: 'proposal-1',
  catalogActorId: 'catalog-1',
  scopeId: 'scope-1',
  updatedAt: '2026-03-23T00:00:00Z',
};

describe('ScriptResultsPanel', () => {
  it('renders diagnostics for the active validation tab', () => {
    const onSelectDiagnostic = jest.fn();
    renderWithQueryClient(
      <ScriptResultsPanel
        activeResultTab="diagnostics"
        activeDiagnosticKey=""
        validationPending={false}
        validationError=""
        validationResult={validationResult}
        selectedSnapshot={runtimeSnapshot}
        selectedSnapshotView={{
          input: 'hello',
          output: 'HELLO',
          status: 'ok',
          lastCommandId: 'cmd-1',
          notes: ['trimmed'],
        }}
        selectedCatalog={selectedCatalog}
        scopeDetail={scopeDetail}
        selectedDecision={promotionDecision}
        onChangeActiveResultTab={jest.fn()}
        onSelectDiagnostic={onSelectDiagnostic}
      />,
    );

    expect(screen.getAllByText('Semicolon expected').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Behavior.cs:12:4 · compiler')).toBeTruthy();
    screen.getByRole('button', { name: /Semicolon expected/ });
  });

  it('renders runtime snapshot details when runtime tab is active', () => {
    renderWithQueryClient(
      <ScriptResultsPanel
        activeResultTab="runtime"
        activeDiagnosticKey=""
        validationPending={false}
        validationError=""
        validationResult={validationResult}
        selectedSnapshot={runtimeSnapshot}
        selectedSnapshotView={{
          input: 'hello',
          output: 'HELLO',
          status: 'ok',
          lastCommandId: 'cmd-1',
          notes: ['trimmed'],
        }}
        selectedCatalog={selectedCatalog}
        scopeDetail={scopeDetail}
        selectedDecision={promotionDecision}
        onChangeActiveResultTab={jest.fn()}
        onSelectDiagnostic={jest.fn()}
      />,
    );

    expect(screen.getByText('Actor: runtime-1')).toBeTruthy();
    expect(screen.getByText('Output: HELLO')).toBeTruthy();
    expect(screen.getByText(/"status": "ok"/)).toBeTruthy();
  });

  it('renders scope and promotion details in their respective tabs', () => {
    const { rerender } = renderWithQueryClient(
      <ScriptResultsPanel
        activeResultTab="save"
        activeDiagnosticKey=""
        validationPending={false}
        validationError=""
        validationResult={validationResult}
        selectedSnapshot={runtimeSnapshot}
        selectedSnapshotView={{
          input: 'hello',
          output: 'HELLO',
          status: 'ok',
          lastCommandId: 'cmd-1',
          notes: ['trimmed'],
        }}
        selectedCatalog={selectedCatalog}
        scopeDetail={scopeDetail}
        selectedDecision={promotionDecision}
        onChangeActiveResultTab={jest.fn()}
        onSelectDiagnostic={jest.fn()}
      />,
    );

    expect(screen.getByText('Scope: scope-1')).toBeTruthy();
    expect(screen.getByText('Catalog: catalog-1')).toBeTruthy();
    expect(screen.getByText('Previous: rev-0')).toBeTruthy();
    expect(screen.getByText('History: rev-0 -> rev-1')).toBeTruthy();

    rerender(
      <ScriptResultsPanel
        activeResultTab="promotion"
        activeDiagnosticKey=""
        validationPending={false}
        validationError=""
        validationResult={validationResult}
        selectedSnapshot={runtimeSnapshot}
        selectedSnapshotView={{
          input: 'hello',
          output: 'HELLO',
          status: 'ok',
          lastCommandId: 'cmd-1',
          notes: ['trimmed'],
        }}
        selectedCatalog={selectedCatalog}
        scopeDetail={scopeDetail}
        selectedDecision={promotionDecision}
        onChangeActiveResultTab={jest.fn()}
        onSelectDiagnostic={jest.fn()}
      />,
    );

    expect(screen.getByText('Proposal: proposal-1')).toBeTruthy();
    expect(screen.getAllByText('rev-2').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Entry: Behavior.cs')).toBeTruthy();
    expect(screen.getByText('Schema: 2 · schema-hash-2')).toBeTruthy();
    expect(screen.getByText('Files: 2 total · 1 C# · 1 proto')).toBeTruthy();
  });

  it('invokes the diagnostic callback when a diagnostic is clicked', () => {
    const onSelectDiagnostic = jest.fn();
    renderWithQueryClient(
      <ScriptResultsPanel
        activeResultTab="diagnostics"
        activeDiagnosticKey=""
        validationPending={false}
        validationError=""
        validationResult={validationResult}
        selectedSnapshot={runtimeSnapshot}
        selectedSnapshotView={{
          input: 'hello',
          output: 'HELLO',
          status: 'ok',
          lastCommandId: 'cmd-1',
          notes: ['trimmed'],
        }}
        selectedCatalog={selectedCatalog}
        scopeDetail={scopeDetail}
        selectedDecision={promotionDecision}
        onChangeActiveResultTab={jest.fn()}
        onSelectDiagnostic={onSelectDiagnostic}
      />,
    );

    screen.getByRole('button', { name: /Semicolon expected/ }).click();
    expect(onSelectDiagnostic).toHaveBeenCalledWith(
      expect.objectContaining({
        message: 'Semicolon expected',
        filePath: 'Behavior.cs',
      }),
    );
  });
});
