import type { ScriptDraft, ScriptsStudioProps } from '../models';
import { formatDateTime } from '../utils';
import { CollapsibleSection } from './StudioChrome';

export function InspectorPanel(props: {
  selectedDraft: ScriptDraft | null;
  scopeBacked: boolean;
  appContext: ScriptsStudioProps['appContext'];
}) {
  const { selectedDraft } = props;
  if (!selectedDraft) {
    return null;
  }

  return (
    <section className="flex h-full min-h-0 flex-col overflow-hidden rounded-[28px] border border-[#E6E3DE] bg-white shadow-[0_10px_24px_rgba(31,28,24,0.04)]">
      <div className="border-b border-[#EEEAE4] bg-[#FAF8F4] px-4 py-4">
        <div className="panel-eyebrow">Inspector</div>
        <div className="mt-1 text-[15px] font-semibold text-gray-800">Draft metadata</div>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-4">
        <CollapsibleSection eyebrow="Identity" title="Draft identity" defaultOpen bodyClassName="border-t border-[#EEEAE4] px-4 pb-4">
          <div className="grid gap-3 pt-4">
            <div>
              <label className="field-label">Script ID</label>
              <div className="mt-1 break-all text-[13px] leading-6 text-gray-700">{selectedDraft.scriptId || '-'}</div>
            </div>
            <div>
              <label className="field-label">Draft Revision</label>
              <div className="mt-1 break-all text-[13px] leading-6 text-gray-700">{selectedDraft.revision || '-'}</div>
            </div>
            <div>
              <label className="field-label">Base Revision</label>
              <div className="mt-1 break-all text-[13px] leading-6 text-gray-700">{selectedDraft.baseRevision || '-'}</div>
            </div>
          </div>
        </CollapsibleSection>

        <CollapsibleSection eyebrow="Actors" title="Binding and runtime ids" defaultOpen={false} bodyClassName="border-t border-[#EEEAE4] px-4 pb-4">
          <div className="space-y-2 break-all pt-4 text-[12px] leading-6 text-gray-600">
            <div>definitionActorId: {selectedDraft.definitionActorId || '-'}</div>
            <div>runtimeActorId: {selectedDraft.runtimeActorId || '-'}</div>
            <div>lastSourceHash: {selectedDraft.lastSourceHash || '-'}</div>
            <div>updatedAt: {formatDateTime(selectedDraft.updatedAtUtc)}</div>
          </div>
        </CollapsibleSection>

        <CollapsibleSection eyebrow="Contract" title="Current app contract" defaultOpen={false} bodyClassName="border-t border-[#EEEAE4] px-4 pb-4">
          <div className="space-y-3 pt-4 text-[12px] leading-6 text-gray-600">
            <div>
              <div className="section-heading">Storage</div>
              <div className="mt-1 break-all text-[13px] text-gray-700">
                {props.scopeBacked ? `Scope-backed · ${props.appContext.scopeId}` : 'Local-only draft'}
              </div>
            </div>
            <div>
              <div className="section-heading">Input Type</div>
              <div className="mt-1 break-all text-[13px] text-gray-700">{props.appContext.scriptContract.inputType}</div>
            </div>
            <div>
              <div className="section-heading">Read Model Fields</div>
              <div className="mt-1 break-all text-[13px] text-gray-700">{props.appContext.scriptContract.readModelFields.join(', ')}</div>
            </div>
          </div>
        </CollapsibleSection>

        <CollapsibleSection eyebrow="Scope Snapshot" title="Saved scope state" defaultOpen={false} bodyClassName="border-t border-[#EEEAE4] px-4 pb-4">
          {selectedDraft.scopeDetail?.script ? (
            <div className="space-y-2 break-all pt-4 text-[12px] leading-6 text-gray-600">
              <div>scriptId: {selectedDraft.scopeDetail.script.scriptId}</div>
              <div>revision: {selectedDraft.scopeDetail.script.activeRevision}</div>
              <div>catalogActorId: {selectedDraft.scopeDetail.script.catalogActorId || '-'}</div>
              <div>updatedAt: {formatDateTime(selectedDraft.scopeDetail.script.updatedAt)}</div>
            </div>
          ) : (
            <div className="pt-4 text-[12px] leading-6 text-gray-500">
              This draft has not been saved into the current scope yet.
            </div>
          )}
        </CollapsibleSection>
      </div>
    </section>
  );
}
