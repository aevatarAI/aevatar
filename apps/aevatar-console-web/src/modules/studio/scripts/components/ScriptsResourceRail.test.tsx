import { fireEvent, screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../../../tests/reactQueryTestUtils';
import type {
  ScriptDraft,
  ScriptPromotionDecision,
  ScriptReadModelSnapshot,
  ScopedScriptDetail,
} from '@/shared/studio/scriptsModels';
import ScriptsResourceRail from './ScriptsResourceRail';

function createDraft(): ScriptDraft {
  return {
    key: 'draft-1',
    scriptId: 'script-1',
    revision: 'rev-1',
    baseRevision: 'rev-0',
    reason: '',
    input: '',
    package: {
      format: 'aevatar.scripting.package.v1',
      csharpSources: [{ path: 'Behavior.cs', content: 'public sealed class DraftBehavior {}' }],
      protoFiles: [],
      entryBehaviorTypeName: 'DraftBehavior',
      entrySourcePath: 'Behavior.cs',
    },
    selectedFilePath: 'Behavior.cs',
    definitionActorId: 'definition-1',
    runtimeActorId: 'runtime-1',
    updatedAtUtc: '2026-03-23T00:00:00Z',
    lastSourceHash: 'hash-1',
    lastRun: null,
    lastSnapshot: null,
    lastPromotion: null,
    scopeDetail: null,
  };
}

function createScopeScript(): ScopedScriptDetail {
  return {
    available: true,
    scopeId: 'scope-1',
    script: {
      scopeId: 'scope-1',
      scriptId: 'scope-script',
      catalogActorId: 'catalog-1',
      definitionActorId: 'definition-1',
      activeRevision: 'rev-1',
      activeSourceHash: 'hash-1',
      updatedAt: '2026-03-23T00:00:00Z',
    },
    source: null,
  };
}

function createRuntimeSnapshot(): ScriptReadModelSnapshot {
  return {
    actorId: 'runtime-1',
    scriptId: 'runtime-script',
    definitionActorId: 'definition-1',
    revision: 'rev-1',
    readModelTypeUrl: 'type.googleapis.com/example.ReadModel',
    readModelPayloadJson: '{"status":"ok"}',
    stateVersion: 1,
    lastEventId: 'event-1',
    updatedAt: '2026-03-23T00:00:00Z',
  };
}

function createProposalDecision(): ScriptPromotionDecision {
  return {
    accepted: true,
    proposalId: 'proposal-1',
    scriptId: 'proposal-script',
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
  };
}

describe('ScriptsResourceRail', () => {
  it('renders resource groups and dispatches actions', () => {
    const draft = createDraft();
    const scopeScript = createScopeScript();
    const runtimeSnapshot = createRuntimeSnapshot();
    const proposalDecision = createProposalDecision();
    const handleCreateDraft = jest.fn();
    const handleSearchChange = jest.fn();
    const handleSelectDraft = jest.fn();
    const handleRefreshScopeScripts = jest.fn();
    const handleOpenScopeScript = jest.fn();
    const handleRefreshRuntimes = jest.fn();
    const handleSelectRuntime = jest.fn();
    const handleSelectProposal = jest.fn();

    renderWithQueryClient(
      <ScriptsResourceRail
        drafts={[draft]}
        filteredDrafts={[draft]}
        selectedDraftKey={draft.key}
        search=""
        scopeBacked
        scopeSelectionId="scope-script"
        scopeScripts={[scopeScript]}
        scopeScriptsLoading={false}
        runtimeSnapshots={[runtimeSnapshot]}
        runtimeSnapshotsLoading={false}
        selectedRuntimeActorId="runtime-1"
        proposalDecisions={[proposalDecision]}
        selectedProposalId="proposal-1"
        onCreateDraft={handleCreateDraft}
        onSearchChange={handleSearchChange}
        onSelectDraft={handleSelectDraft}
        onRefreshScopeScripts={handleRefreshScopeScripts}
        onOpenScopeScript={handleOpenScopeScript}
        onRefreshRuntimeSnapshots={handleRefreshRuntimes}
        onSelectRuntime={handleSelectRuntime}
        onSelectProposal={handleSelectProposal}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /New draft/ }));
    fireEvent.change(screen.getByRole('searchbox'), {
      target: { value: 'runtime' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'script-1' }));
    fireEvent.click(screen.getByRole('button', { name: /Refresh scope catalog/ }));
    fireEvent.click(screen.getByRole('button', { name: 'scope-script' }));
    fireEvent.click(screen.getByRole('button', { name: /Runtimes \(1\)/ }));
    fireEvent.click(screen.getByRole('button', { name: /Refresh runtimes/ }));
    fireEvent.click(screen.getByRole('button', { name: 'runtime-script' }));
    fireEvent.click(screen.getByRole('button', { name: /Proposals \(1\)/ }));
    fireEvent.click(screen.getByRole('button', { name: 'proposal-script' }));

    expect(handleCreateDraft).toHaveBeenCalledTimes(1);
    expect(handleSearchChange).toHaveBeenCalledWith('runtime');
    expect(handleSelectDraft).toHaveBeenCalledWith(draft);
    expect(handleRefreshScopeScripts).toHaveBeenCalledTimes(1);
    expect(handleOpenScopeScript).toHaveBeenCalledWith(scopeScript);
    expect(handleRefreshRuntimes).toHaveBeenCalledTimes(1);
    expect(handleSelectRuntime).toHaveBeenCalledWith(runtimeSnapshot);
    expect(handleSelectProposal).toHaveBeenCalledWith(proposalDecision);
  });
});
