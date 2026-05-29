import {
  DeleteOutlined,
} from '@ant-design/icons';
import React from 'react';
import { serializePersistedSource } from '@/shared/studio/scriptPackage';
import type { ScriptDraft } from '@/shared/studio/scriptsModels';
import { t } from "@/shared/i18n/messages";

type ScriptsPackagePanelProps = {
  selectedDraft: ScriptDraft | null;
  onBaseRevisionChange: (value: string) => void;
  onEntryBehaviorTypeChange: (value: string) => void;
  onDeleteDraft: () => void;
  canDeleteDraft: boolean;
};

const ScriptsPackagePanel: React.FC<ScriptsPackagePanelProps> = ({
  selectedDraft,
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
            <div className="console-scripts-empty-title">{t("modules.studio.scripts.scriptspackagepanel.no.draft.selected", "No draft selected")}</div>
            <div className="console-scripts-empty-copy">
              {t("modules.studio.scripts.scriptspackagepanel.select.or.create.draft.before", "Select or create a draft before inspecting the script package.")}</div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="console-scripts-package-panel">
      <div className="console-scripts-package-summary">
        <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptspackagepanel.entry.contract", "Entry contract")}</div>
        <div className="console-scripts-detail-grid" style={{ marginTop: 12 }}>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptspackagepanel.saved.revision", "Saved Revision")}</div>
            <div className="console-scripts-copy-value">
              {selectedDraft.revision || 'Generated on save'}
            </div>
          </div>
          <label className="console-scripts-field">
            <span className="console-scripts-field-label">{t("modules.studio.scripts.scriptspackagepanel.base.revision", "Base Revision")}</span>
            <input
              className="console-scripts-input"
              placeholder={t("modules.studio.scripts.scriptspackagepanel.scope.active.revision", "scope active revision")}
              value={selectedDraft.baseRevision}
              onChange={(event) => onBaseRevisionChange(event.target.value)}
            />
          </label>
          <label className="console-scripts-field">
            <span className="console-scripts-field-label">
              {t("modules.studio.scripts.scriptspackagepanel.entry.behavior.type", "Entry Behavior Type")}</span>
            <input
              className="console-scripts-input"
              placeholder="DraftBehavior"
              value={selectedDraft.package.entryBehaviorTypeName}
              onChange={(event) => onEntryBehaviorTypeChange(event.target.value)}
            />
          </label>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptspackagepanel.entry.source.path", "Entry Source Path")}</div>
            <div className="console-scripts-copy-value">
              {selectedDraft.package.entrySourcePath || '-'}
            </div>
          </div>
        </div>
      </div>

      <div className="console-scripts-package-summary">
        <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptspackagepanel.package.summary", "Package summary")}</div>
        <div className="console-scripts-detail-grid" style={{ marginTop: 12 }}>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptspackagepanel.format", "Format")}</div>
            <div className="console-scripts-copy-value">
              {selectedDraft.package.format}
            </div>
          </div>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptspackagepanel.selected.file", "Selected File")}</div>
            <div className="console-scripts-field-value">
              {selectedDraft.selectedFilePath || '-'}
            </div>
          </div>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptspackagepanel.files", "C# Files")}</div>
            <div className="console-scripts-field-value">
              {selectedDraft.package.csharpSources.length}
            </div>
          </div>
          <div className="console-scripts-field">
            <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptspackagepanel.proto.files", "Proto Files")}</div>
            <div className="console-scripts-field-value">
              {selectedDraft.package.protoFiles.length}
            </div>
          </div>
        </div>

        <div className="console-scripts-ask-ai-toolbar">
          <div className="console-scripts-ask-ai-copy">
            {t("modules.studio.scripts.scriptspackagepanel.draft.deletion.only.affects.the", "Draft deletion only affects the local browser draft list.")}</div>
          <button
            type="button"
            onClick={onDeleteDraft}
            disabled={!canDeleteDraft}
            className="console-scripts-ghost-action"
          >
            <DeleteOutlined />
            {t("modules.studio.scripts.scriptspackagepanel.delete.draft", "Delete Draft")}</button>
        </div>
      </div>

      <div className="console-scripts-package-preview">
        <details open>
          <summary>{t("modules.studio.scripts.scriptspackagepanel.persisted.source.preview", "Persisted source preview")}</summary>
          <pre className="console-scripts-pre" style={{ marginTop: 12 }}>
            {serializePersistedSource(selectedDraft.package) || '-'}
          </pre>
        </details>
      </div>
    </div>
  );
};

export default ScriptsPackagePanel;
