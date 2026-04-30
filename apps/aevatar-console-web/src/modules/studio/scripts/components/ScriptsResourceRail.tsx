import {
  FileAddOutlined,
  SyncOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import React from 'react';
import { formatScriptDateTime, isScopeDetailDirty } from '@/shared/studio/scriptUtils';
import type {
  ScriptDraft,
  ScriptPromotionDecision,
  ScriptReadModelSnapshot,
  ScopedScriptDetail,
} from '@/shared/studio/scriptsModels';
import {
  ScriptsStudioEmptyState,
  ScriptsStudioResultCard,
  ScriptsStudioSection,
} from '../ScriptsStudioChrome';

type ScriptsResourceRailProps = {
  drafts: ScriptDraft[];
  filteredDrafts: ScriptDraft[];
  selectedDraftKey: string;
  search: string;
  scopeBacked: boolean;
  scopeSelectionId: string;
  scopeScripts: ScopedScriptDetail[];
  scopeScriptsLoading: boolean;
  runtimeSnapshots: ScriptReadModelSnapshot[];
  runtimeSnapshotsLoading: boolean;
  selectedRuntimeActorId: string;
  proposalDecisions: ScriptPromotionDecision[];
  selectedProposalId: string;
  onCreateDraft: () => void;
  onSearchChange: (value: string) => void;
  onSelectDraft: (draft: ScriptDraft) => void;
  onRefreshScopeScripts: () => void;
  onOpenScopeScript: (detail: ScopedScriptDetail) => void;
  onRefreshRuntimeSnapshots: () => void;
  onSelectRuntime: (snapshot: ScriptReadModelSnapshot) => void;
  onSelectProposal: (decision: ScriptPromotionDecision) => void;
};

const ScriptsResourceRail: React.FC<ScriptsResourceRailProps> = ({
  drafts,
  filteredDrafts,
  selectedDraftKey,
  search,
  scopeBacked,
  scopeSelectionId,
  scopeScripts,
  scopeScriptsLoading,
  runtimeSnapshots,
  runtimeSnapshotsLoading,
  selectedRuntimeActorId,
  proposalDecisions,
  selectedProposalId,
  onCreateDraft,
  onSearchChange,
  onSelectDraft,
  onRefreshScopeScripts,
  onOpenScopeScript,
  onRefreshRuntimeSnapshots,
  onSelectRuntime,
  onSelectProposal,
}) => (
  <section className="console-scripts-panel">
    <div className="console-scripts-panel-header">
      <div className="console-scripts-eyebrow">Scripts Studio</div>
      <div className="console-scripts-panel-header-title">Resource rail</div>
      <div className="console-scripts-search-field">
        <SearchOutlined />
        <input
          type="search"
          className="console-scripts-search-input"
          placeholder="Search drafts or the current scope catalog"
          value={search}
          onChange={(event) => onSearchChange(event.target.value)}
        />
      </div>
    </div>

    <div className="console-scripts-panel-body">
      <ScriptsStudioSection
        eyebrow="Drafts"
        title={`${drafts.length} local draft${drafts.length === 1 ? '' : 's'}`}
        actions={
          <button
            type="button"
            onClick={onCreateDraft}
            className="console-scripts-icon-button"
            title="New draft"
            aria-label="New draft"
          >
            <FileAddOutlined />
          </button>
        }
      >
        <div className="console-scripts-run-list">
          {filteredDrafts.length === 0 ? (
            <ScriptsStudioEmptyState
              title="No drafts matched"
              copy="Try a different search, or create a new draft."
            />
          ) : (
            filteredDrafts.map((draft) => {
              const dirty = isScopeDetailDirty(draft);
              return (
                <ScriptsStudioResultCard
                  key={draft.key}
                  active={draft.key === selectedDraftKey}
                  title={draft.scriptId}
                  meta={draft.revision || 'local draft'}
                  summary={formatScriptDateTime(draft.updatedAtUtc)}
                  status={dirty ? 'dirty' : draft.scopeDetail?.script ? 'scope' : ''}
                  onClick={() => onSelectDraft(draft)}
                />
              );
            })
          )}
        </div>
      </ScriptsStudioSection>

      <ScriptsStudioSection
        eyebrow="Scope Catalog"
        title={`Current Scope Catalog (${scopeScripts.length})`}
        actions={
          scopeBacked ? (
            <button
              type="button"
              onClick={onRefreshScopeScripts}
              className="console-scripts-icon-button"
              title="Refresh scope catalog"
              aria-label="Refresh scope catalog"
              disabled={scopeScriptsLoading}
            >
              <SyncOutlined spin={scopeScriptsLoading} />
            </button>
          ) : undefined
        }
      >
        <div className="console-scripts-run-list">
          {!scopeBacked ? (
            <ScriptsStudioEmptyState
              title="Scope save unavailable"
              copy="Studio has not resolved a current scope yet, so only local drafts are available."
            />
          ) : scopeScripts.length === 0 ? (
            <ScriptsStudioEmptyState
              title={
                scopeScriptsLoading
                  ? 'Loading scope catalog'
                  : 'No scope catalog entries matched'
              }
              copy={
                scopeScriptsLoading
                  ? 'Pulling the current scope catalog now.'
                  : 'Try a different search or save the active draft to the current scope.'
              }
            />
          ) : (
            scopeScripts.map((detail) => {
              const script = detail.script;
              if (!script) {
                return null;
              }

              return (
                <ScriptsStudioResultCard
                  key={`${detail.scopeId}:${script.scriptId}`}
                  active={scopeSelectionId === script.scriptId}
                  title={script.scriptId}
                  meta={script.activeRevision}
                  summary={formatScriptDateTime(script.updatedAt)}
                  status="scope"
                  onClick={() => onOpenScopeScript(detail)}
                />
              );
            })
          )}
        </div>
      </ScriptsStudioSection>

      <ScriptsStudioSection
        eyebrow="Runtimes"
        title={`Runtimes (${runtimeSnapshots.length})`}
        defaultOpen={false}
        actions={
          <button
            type="button"
            onClick={onRefreshRuntimeSnapshots}
            className="console-scripts-icon-button"
            title="Refresh runtimes"
            aria-label="Refresh runtimes"
            disabled={runtimeSnapshotsLoading}
          >
            <SyncOutlined spin={runtimeSnapshotsLoading} />
          </button>
        }
      >
        <div className="console-scripts-run-list">
          {runtimeSnapshots.length === 0 ? (
            <ScriptsStudioEmptyState
              title={
                runtimeSnapshotsLoading
                  ? 'Loading runtimes'
                  : 'No runtime snapshots yet'
              }
              copy={
                runtimeSnapshotsLoading
                  ? 'Pulling recent script runtimes now.'
                  : 'Run a draft to materialize recent runtime state.'
              }
            />
          ) : (
            runtimeSnapshots.map((snapshot) => (
              <ScriptsStudioResultCard
                key={snapshot.actorId}
                active={selectedRuntimeActorId === snapshot.actorId}
                title={snapshot.scriptId}
                meta={snapshot.revision}
                summary={formatScriptDateTime(snapshot.updatedAt)}
                status={`v${snapshot.stateVersion}`}
                onClick={() => onSelectRuntime(snapshot)}
              />
            ))
          )}
        </div>
      </ScriptsStudioSection>

      <ScriptsStudioSection
        eyebrow="Proposals"
        title={`Proposals (${proposalDecisions.length})`}
        defaultOpen={false}
      >
        <div className="console-scripts-run-list">
          {proposalDecisions.length === 0 ? (
            <ScriptsStudioEmptyState
              title="No proposal decisions yet"
              copy="Promotion decisions will appear here after the scope catalog points at them."
            />
          ) : (
            proposalDecisions.map((decision) => (
              <ScriptsStudioResultCard
                key={decision.proposalId}
                active={selectedProposalId === decision.proposalId}
                title={decision.scriptId}
                meta={decision.candidateRevision || decision.baseRevision || '-'}
                summary={decision.proposalId}
                status={
                  decision.status ||
                  (decision.accepted ? 'accepted' : 'rejected')
                }
                onClick={() => onSelectProposal(decision)}
              />
            ))
          )}
        </div>
      </ScriptsStudioSection>
    </div>
  </section>
);

export default ScriptsResourceRail;
