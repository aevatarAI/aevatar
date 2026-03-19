import { Plus, RefreshCw, Search } from 'lucide-react';
import type {
  ScriptCatalogSnapshot,
  ScriptDraft,
  ScriptPromotionDecision,
  ScriptReadModelSnapshot,
  ScopedScriptDetail,
} from '../models';
import { formatDateTime, isScopeDetailDirty } from '../utils';
import { CollapsibleSection, EmptyState } from './StudioChrome';

export function ResourceRail(props: {
  drafts: ScriptDraft[];
  filteredDrafts: ScriptDraft[];
  filteredScopeScripts: ScopedScriptDetail[];
  runtimeSnapshots: ScriptReadModelSnapshot[];
  proposalDecisions: ScriptPromotionDecision[];
  scopeCatalogsByScriptId: Record<string, ScriptCatalogSnapshot>;
  selectedDraft: ScriptDraft | null;
  scopeSelectionId: string;
  selectedRuntimeActorId: string;
  selectedProposalId: string;
  search: string;
  scopeBacked: boolean;
  scopeId: string | null;
  scopeScriptsPending: boolean;
  runtimeSnapshotsPending: boolean;
  proposalDecisionsPending: boolean;
  onSearchChange: (value: string) => void;
  onCreateDraft: () => void;
  onSelectDraft: (draftKey: string) => void;
  onOpenScopeScript: (detail: ScopedScriptDetail) => void;
  onRefreshScopeScripts: () => void;
  onSelectRuntime: (actorId: string) => void;
  onRefreshRuntimeSnapshots: () => void;
  onSelectProposal: (proposalId: string) => void;
}) {
  return (
    <section className="flex h-full min-h-0 flex-col overflow-hidden rounded-[28px] border border-[#E6E3DE] bg-white shadow-[0_10px_24px_rgba(31,28,24,0.04)]">
      <div className="border-b border-[#EEEAE4] bg-[#FAF8F4] px-4 py-4">
        <div className="panel-eyebrow">Scripts</div>
        <div className="mt-1 text-[15px] font-semibold text-gray-800">Resource rail</div>
        <div className="mt-3 search-field !min-h-[40px] !rounded-[18px] !border-[#E8E1D8] !bg-white">
          <Search size={14} className="text-gray-400" />
          <input
            className="search-input"
            placeholder="Search drafts or saved scripts"
            value={props.search}
            onChange={event => props.onSearchChange(event.target.value)}
          />
        </div>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-4">
        <CollapsibleSection
          eyebrow="Drafts"
          title={`${props.drafts.length} local draft${props.drafts.length === 1 ? '' : 's'}`}
          defaultOpen
          bodyClassName="border-t border-[#EEEAE4] px-4 pb-4"
          actions={(
            <button type="button" onClick={props.onCreateDraft} className="panel-icon-button" title="New draft">
              <Plus size={14} />
            </button>
          )}
        >
          <div className="max-h-[320px] space-y-2 overflow-y-auto pt-4 pr-1">
            {props.filteredDrafts.length === 0 ? (
              <EmptyState title="No drafts matched" copy="Try a different search, or create a new draft." />
            ) : props.filteredDrafts.map(draft => {
              const dirty = isScopeDetailDirty(draft);
              return (
                <button
                  key={draft.key}
                  type="button"
                  onClick={() => props.onSelectDraft(draft.key)}
                  className={`execution-run-card ${draft.key === props.selectedDraft?.key ? 'active' : ''}`}
                >
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="truncate text-[13px] font-semibold text-gray-800">{draft.scriptId}</div>
                      <div className="mt-1 truncate text-[11px] text-gray-400">{draft.revision}</div>
                    </div>
                    <div className="flex shrink-0 flex-col items-end gap-1">
                      {draft.scopeDetail?.script ? (
                        <span className="rounded-full border border-[#DCE8C8] bg-[#F5FBEE] px-2 py-0.5 text-[10px] uppercase tracking-[0.14em] text-[#5C7A2D]">
                          scope
                        </span>
                      ) : null}
                      {dirty ? (
                        <span className="rounded-full border border-[#E9D6AE] bg-[#FFF7E6] px-2 py-0.5 text-[10px] uppercase tracking-[0.14em] text-[#9B6A1C]">
                          dirty
                        </span>
                      ) : null}
                    </div>
                  </div>
                  <div className="mt-2 text-[11px] text-gray-400">{formatDateTime(draft.updatedAtUtc)}</div>
                </button>
              );
            })}
          </div>
        </CollapsibleSection>

        <CollapsibleSection
          eyebrow="Saved in Scope"
          title={props.scopeBacked ? (props.scopeId || '-') : 'Unavailable'}
          defaultOpen
          bodyClassName="border-t border-[#EEEAE4] px-4 pb-4"
          actions={props.scopeBacked ? (
            <button
              type="button"
              onClick={props.onRefreshScopeScripts}
              className="panel-icon-button"
              title="Refresh saved scripts"
              disabled={props.scopeScriptsPending}
            >
              <RefreshCw size={14} className={props.scopeScriptsPending ? 'animate-spin' : ''} />
            </button>
          ) : undefined}
        >
          <div className="max-h-[320px] space-y-2 overflow-y-auto pt-4 pr-1">
            {!props.scopeBacked ? (
              <EmptyState
                title="Scope save unavailable"
                copy="This session is not bound to a resolved scope, so only local drafts are available."
              />
            ) : props.filteredScopeScripts.length === 0 ? (
              <EmptyState
                title={props.scopeScriptsPending ? 'Loading scope scripts' : 'No saved scripts matched'}
                copy={props.scopeScriptsPending ? 'Pulling the scope catalog now.' : 'Try a different search or save the active draft.'}
              />
            ) : props.filteredScopeScripts.map(detail => {
              const script = detail.script;
              if (!script) {
                return null;
              }

              return (
                <button
                  key={`${detail.scopeId}:${script.scriptId}`}
                  type="button"
                  onClick={() => props.onOpenScopeScript(detail)}
                  className={`execution-run-card ${props.scopeSelectionId === script.scriptId ? 'active' : ''}`}
                >
                  <div className="truncate text-[13px] font-semibold text-gray-800">{script.scriptId}</div>
                  <div className="mt-1 truncate text-[11px] text-gray-400">{script.activeRevision}</div>
                  <div className="mt-2 text-[11px] text-gray-400">{formatDateTime(script.updatedAt)}</div>
                </button>
              );
            })}
          </div>
        </CollapsibleSection>

        <CollapsibleSection
          eyebrow="Runtimes"
          title={`${props.runtimeSnapshots.length} recent snapshot${props.runtimeSnapshots.length === 1 ? '' : 's'}`}
          defaultOpen={false}
          bodyClassName="border-t border-[#EEEAE4] px-4 pb-4"
          actions={(
            <button
              type="button"
              onClick={props.onRefreshRuntimeSnapshots}
              className="panel-icon-button"
              title="Refresh runtime snapshots"
              disabled={props.runtimeSnapshotsPending}
            >
              <RefreshCw size={14} className={props.runtimeSnapshotsPending ? 'animate-spin' : ''} />
            </button>
          )}
        >
          <div className="max-h-[320px] space-y-2 overflow-y-auto pt-4 pr-1">
            {props.runtimeSnapshots.length === 0 ? (
              <EmptyState
                title={props.runtimeSnapshotsPending ? 'Loading runtimes' : 'No runtime snapshots yet'}
                copy={props.runtimeSnapshotsPending ? 'Pulling recent script runtimes now.' : 'Run a draft to materialize recent runtime state.'}
              />
            ) : props.runtimeSnapshots.map(snapshot => (
              <button
                key={snapshot.actorId}
                type="button"
                onClick={() => props.onSelectRuntime(snapshot.actorId)}
                className={`execution-run-card ${props.selectedRuntimeActorId === snapshot.actorId ? 'active' : ''}`}
              >
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="truncate text-[13px] font-semibold text-gray-800">{snapshot.scriptId}</div>
                    <div className="mt-1 truncate text-[11px] text-gray-400">{snapshot.revision}</div>
                  </div>
                  <span className="rounded-full border border-[#E5DED3] bg-[#F7F2E8] px-2 py-0.5 text-[10px] uppercase tracking-[0.14em] text-[#8E6A3D]">
                    v{snapshot.stateVersion}
                  </span>
                </div>
                <div className="mt-2 truncate text-[11px] text-gray-400">{formatDateTime(snapshot.updatedAt)}</div>
              </button>
            ))}
          </div>
        </CollapsibleSection>

        <CollapsibleSection
          eyebrow="Proposals"
          title={`${props.proposalDecisions.length} terminal decision${props.proposalDecisions.length === 1 ? '' : 's'}`}
          defaultOpen={false}
          bodyClassName="border-t border-[#EEEAE4] px-4 pb-4"
        >
          <div className="max-h-[320px] space-y-2 overflow-y-auto pt-4 pr-1">
            {props.proposalDecisions.length === 0 ? (
              <EmptyState
                title={props.proposalDecisionsPending ? 'Loading proposals' : 'No proposal decisions yet'}
                copy={props.proposalDecisionsPending ? 'Resolving terminal proposal decisions now.' : 'Promotion decisions will appear here after the scope catalog points at them.'}
              />
            ) : props.proposalDecisions.map(decision => {
              const catalog = props.scopeCatalogsByScriptId[decision.scriptId];
              return (
                <button
                  key={decision.proposalId}
                  type="button"
                  onClick={() => props.onSelectProposal(decision.proposalId)}
                  className={`execution-run-card ${props.selectedProposalId === decision.proposalId ? 'active' : ''}`}
                >
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="truncate text-[13px] font-semibold text-gray-800">{decision.scriptId}</div>
                      <div className="mt-1 truncate text-[11px] text-gray-400">{decision.candidateRevision || decision.baseRevision || '-'}</div>
                    </div>
                    <span className={`rounded-full border px-2 py-0.5 text-[10px] uppercase tracking-[0.14em] ${
                      decision.accepted
                        ? 'border-[#DCE8C8] bg-[#F5FBEE] text-[#5C7A2D]'
                        : 'border-[#F2CCC4] bg-[#FFF4F1] text-[#B15647]'
                    }`}>
                      {decision.status || (decision.accepted ? 'accepted' : 'rejected')}
                    </span>
                  </div>
                  <div className="mt-2 truncate text-[11px] text-gray-400">
                    {catalog?.updatedAt ? formatDateTime(catalog.updatedAt) : decision.proposalId}
                  </div>
                </button>
              );
            })}
          </div>
        </CollapsibleSection>
      </div>
    </section>
  );
}
