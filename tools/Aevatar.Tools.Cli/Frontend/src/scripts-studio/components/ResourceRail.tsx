import { Plus, RefreshCw, Search } from 'lucide-react';
import type { ScriptDraft, ScopedScriptDetail } from '../models';
import { EmptyState } from './StudioChrome';
import { formatDateTime, isScopeDetailDirty } from '../utils';

export function ResourceRail(props: {
  drafts: ScriptDraft[];
  filteredDrafts: ScriptDraft[];
  filteredScopeScripts: ScopedScriptDetail[];
  selectedDraft: ScriptDraft | null;
  scopeSelectionId: string;
  search: string;
  scopeBacked: boolean;
  scopeId: string | null;
  scopeScriptsPending: boolean;
  onSearchChange: (value: string) => void;
  onCreateDraft: () => void;
  onSelectDraft: (draftKey: string) => void;
  onOpenScopeScript: (detail: ScopedScriptDetail) => void;
  onRefreshScopeScripts: () => void;
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
        <section className="rounded-[24px] border border-[#E6E3DE] bg-[#FAF8F4] p-4">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="panel-eyebrow">Drafts</div>
              <div className="mt-1 text-[14px] font-semibold text-gray-800">{props.drafts.length} local draft{props.drafts.length === 1 ? '' : 's'}</div>
            </div>
            <button type="button" onClick={props.onCreateDraft} className="panel-icon-button" title="New draft">
              <Plus size={14} />
            </button>
          </div>

          <div className="mt-4 max-h-[320px] space-y-2 overflow-y-auto pr-1">
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
        </section>

        <section className="rounded-[24px] border border-[#E6E3DE] bg-[#FAF8F4] p-4">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="panel-eyebrow">Saved in Scope</div>
              <div className="mt-1 text-[14px] font-semibold text-gray-800">{props.scopeBacked ? (props.scopeId || '-') : 'Unavailable'}</div>
            </div>
            {props.scopeBacked ? (
              <button
                type="button"
                onClick={props.onRefreshScopeScripts}
                className="panel-icon-button"
                title="Refresh saved scripts"
                disabled={props.scopeScriptsPending}
              >
                <RefreshCw size={14} className={props.scopeScriptsPending ? 'animate-spin' : ''} />
              </button>
            ) : null}
          </div>

          <div className="mt-4 max-h-[320px] space-y-2 overflow-y-auto pr-1">
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
        </section>
      </div>
    </section>
  );
}
