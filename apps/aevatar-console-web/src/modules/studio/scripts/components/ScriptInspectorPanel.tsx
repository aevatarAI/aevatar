import React from 'react';
import type { StudioAppContext } from '@/shared/studio/models';
import {
  formatStudioHostModeLabel,
  getStudioHostModeTooltip,
} from '@/shared/studio/scriptHostCapabilities';
import { formatScriptDateTime } from '@/shared/studio/scriptUtils';
import type { ScriptDraft } from '@/shared/studio/scriptsModels';
import {
  ScriptsStudioEmptyState,
  ScriptsStudioSection,
} from '../ScriptsStudioChrome';

type ScriptInspectorPanelProps = {
  appContext: StudioAppContext;
  scopeBacked: boolean;
  selectedDraft: ScriptDraft | null;
};

function renderValue(value: string | number | null | undefined): string {
  const normalized = String(value || '').trim();
  return normalized || '-';
}

const ScriptInspectorPanel: React.FC<ScriptInspectorPanelProps> = ({
  appContext,
  scopeBacked,
  selectedDraft,
}) => {
  if (!selectedDraft) {
    return (
      <section className="console-scripts-panel">
        <div className="console-scripts-panel-header">
          <div className="console-scripts-eyebrow">Inspector</div>
          <div className="console-scripts-panel-header-title">
            Draft metadata
          </div>
        </div>
        <div className="console-scripts-panel-body">
          <ScriptsStudioEmptyState
            title="No draft selected."
            copy="Select a draft to inspect its identity, contract, actors, and scope state."
          />
        </div>
      </section>
    );
  }

  const isEmbeddedMode = appContext.mode === 'embedded';
  const availableActions = [
    'Validate',
    ...(scopeBacked ? ['Save', 'Promote'] : []),
    ...(isEmbeddedMode ? ['Draft Run', 'Ask AI'] : []),
  ];
  const unavailableActions = [
    ...(!scopeBacked
      ? ['Save (requires current scope)', 'Promote (requires current scope)']
      : []),
    ...(!isEmbeddedMode
      ? ['Draft Run (requires embedded host)', 'Ask AI (requires embedded host)']
      : []),
  ];

  return (
    <section className="console-scripts-panel">
      <div className="console-scripts-panel-header">
        <div className="console-scripts-eyebrow">Inspector</div>
        <div className="console-scripts-panel-header-title">Draft metadata</div>
      </div>
      <div className="console-scripts-panel-body">
        <ScriptsStudioSection eyebrow="Identity" title="Draft identity">
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Script ID</div>
              <div className="console-scripts-field-value">
                {selectedDraft.scriptId}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Draft Revision</div>
              <div className="console-scripts-field-value">
                {selectedDraft.revision}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Base Revision</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.baseRevision)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Updated</div>
              <div className="console-scripts-field-value">
                {formatScriptDateTime(selectedDraft.updatedAtUtc)}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow="Contract"
          title="Current app contract"
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Storage</div>
              <div className="console-scripts-field-value">
                {scopeBacked
                  ? `Resolved scope · ${appContext.scopeId}`
                  : 'Local-only draft'}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Input Type</div>
              <div className="console-scripts-copy-value">
                {appContext.scriptContract.inputType}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Read Model Fields</div>
              <div className="console-scripts-field-value">
                {appContext.scriptContract.readModelFields.join(', ') || '-'}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow="Host"
          title="Host capabilities"
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Host Mode</div>
              <div className="console-scripts-field-value">
                {formatStudioHostModeLabel(appContext.mode)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Available Here</div>
              <div className="console-scripts-field-value">
                {availableActions.join(', ')}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Unavailable Here</div>
              <div className="console-scripts-field-value">
                {unavailableActions.join(', ') || 'None'}
              </div>
            </div>
          </div>
          <div className="console-scripts-detail-copy">
            {getStudioHostModeTooltip(appContext.mode)}
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow="Actors"
          title="Binding and runtime ids"
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Definition Actor</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.definitionActorId)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Runtime Actor</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.runtimeActorId)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Source Hash</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.lastSourceHash)}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow="Package"
          title="Draft package"
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Selected File</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.selectedFilePath)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Entry Source</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.package.entrySourcePath)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Entry Type</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.package.entryBehaviorTypeName)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">C# / Proto</div>
              <div className="console-scripts-field-value">
                {selectedDraft.package.csharpSources.length} /{' '}
                {selectedDraft.package.protoFiles.length}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow="Scope Snapshot"
          title="Saved scope state"
        >
          {selectedDraft.scopeDetail?.script ? (
            <div className="console-scripts-detail-grid">
              <div className="console-scripts-field">
                <div className="console-scripts-field-label">Scope Script</div>
                <div className="console-scripts-field-value">
                  {selectedDraft.scopeDetail.script.scriptId}
                </div>
              </div>
              <div className="console-scripts-field">
                <div className="console-scripts-field-label">Scope Revision</div>
                <div className="console-scripts-field-value">
                  {selectedDraft.scopeDetail.script.activeRevision}
                </div>
              </div>
              <div className="console-scripts-field">
                <div className="console-scripts-field-label">Catalog Actor</div>
                <div className="console-scripts-copy-value">
                  {renderValue(selectedDraft.scopeDetail.script.catalogActorId)}
                </div>
              </div>
              <div className="console-scripts-field">
                <div className="console-scripts-field-label">Updated</div>
                <div className="console-scripts-field-value">
                  {formatScriptDateTime(selectedDraft.scopeDetail.script.updatedAt)}
                </div>
              </div>
            </div>
          ) : (
            <div className="console-scripts-detail-copy">
              This draft has not been saved into the current scope catalog yet.
            </div>
          )}
        </ScriptsStudioSection>
      </div>
    </section>
  );
};

export default ScriptInspectorPanel;
