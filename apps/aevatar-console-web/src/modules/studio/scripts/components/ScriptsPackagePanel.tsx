import {
  DeleteOutlined,
} from '@ant-design/icons';
import React from 'react';
import { serializePersistedSource } from '@/shared/studio/scriptPackage';
import type { ScriptDraft } from '@/shared/studio/scriptsModels';

type ScriptsPackagePanelProps = {
  selectedDraft: ScriptDraft | null;
  onRevisionChange: (value: string) => void;
  onBaseRevisionChange: (value: string) => void;
  onEntryBehaviorTypeChange: (value: string) => void;
  onDeleteDraft: () => void;
  canDeleteDraft: boolean;
};

const ScriptsPackagePanel: React.FC<ScriptsPackagePanelProps> = ({
  selectedDraft,
  onRevisionChange,
  onBaseRevisionChange,
  onEntryBehaviorTypeChange,
  onDeleteDraft,
  canDeleteDraft,
}) => {
  if (!selectedDraft) {
    return (
      <div className="console-scripts-package-panel">
        <div className="console-scripts-empty">
          <div>
            <div className="console-scripts-empty-title">No draft selected</div>
            <div className="console-scripts-empty-copy">
              Select or create a draft before inspecting the script package.
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="console-scripts-package-panel">
      <div className="console-scripts-package-summary">
        <div className="console-scripts-section-label">Entry contract</div>
        <div className="console-scripts-detail-grid" style={{ marginTop: 12 }}>
          <label className="console-scripts-field">
            <span className="console-scripts-field-label">Revision</span>
            <input
              className="console-scripts-input"
              placeholder="rev-..."
              value={selectedDraft.revision}
              onChange={(event) => onRevisionChange(event.target.value)}
            />
          </label>
          <label className="console-scripts-field">
            <span className="console-scripts-field-label">Base Revision</span>
            <input
              className="console-scripts-input"
              placeholder="scope active revision"
              value={selectedDraft.baseRevision}
              onChange={(event) => onBaseRevisionChange(event.target.value)}
            />
          </label>
          <label className="console-scripts-field">
            <span className="console-scripts-field-label">
              Entry Behavior Type
            </span>
            <input
              className="console-scripts-input"
              placeholder="DraftBehavior"
              value={selectedDraft.package.entryBehaviorTypeName}
              onChange={(event) => onEntryBehaviorTypeChange(event.target.value)}
            />
          </label>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">Entry Source Path</div>
            <div className="console-scripts-copy-value">
              {selectedDraft.package.entrySourcePath || '-'}
            </div>
          </div>
        </div>
      </div>

      <div className="console-scripts-package-summary">
        <div className="console-scripts-section-label">Package summary</div>
        <div className="console-scripts-detail-grid" style={{ marginTop: 12 }}>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">Format</div>
            <div className="console-scripts-copy-value">
              {selectedDraft.package.format}
            </div>
          </div>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">Selected File</div>
            <div className="console-scripts-field-value">
              {selectedDraft.selectedFilePath || '-'}
            </div>
          </div>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">C# Files</div>
            <div className="console-scripts-field-value">
              {selectedDraft.package.csharpSources.length}
            </div>
          </div>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">Proto Files</div>
            <div className="console-scripts-field-value">
              {selectedDraft.package.protoFiles.length}
            </div>
          </div>
        </div>

        <div className="console-scripts-ask-ai-toolbar">
          <div className="console-scripts-ask-ai-copy">
            Draft deletion only affects the local browser draft list.
          </div>
          <button
            type="button"
            onClick={onDeleteDraft}
            disabled={!canDeleteDraft}
            className="console-scripts-ghost-action"
          >
            <DeleteOutlined />
            Delete Draft
          </button>
        </div>
      </div>

      <div className="console-scripts-package-preview">
        <details open>
          <summary>Persisted source preview</summary>
          <pre className="console-scripts-pre" style={{ marginTop: 12 }}>
            {serializePersistedSource(selectedDraft.package) || '-'}
          </pre>
        </details>
      </div>
    </div>
  );
};

export default ScriptsPackagePanel;
