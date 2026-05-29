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
import { t } from "@/shared/i18n/messages";

type ScriptInspectorPanelProps = {
  appContext: StudioAppContext;
  scopeBacked: boolean;
  selectedDraft: ScriptDraft | null;
  canAskAi?: boolean;
  canBindScope?: boolean;
  onOpenAskAi?: () => void;
  onOpenBindScope?: () => void;
};

function renderValue(value: string | number | null | undefined): string {
  const normalized = String(value || '').trim();
  return normalized || '-';
}

const ScriptInspectorPanel: React.FC<ScriptInspectorPanelProps> = ({
  appContext,
  scopeBacked,
  selectedDraft,
  canAskAi = false,
  canBindScope = false,
  onOpenAskAi,
  onOpenBindScope,
}) => {
  if (!selectedDraft) {
    return (
      <section className="console-scripts-panel">
        <div className="console-scripts-panel-header">
          <div className="console-scripts-eyebrow">{t("modules.studio.scripts.scriptinspectorpanel.script.information", "Script information")}</div>
          <div className="console-scripts-panel-header-title">{t("modules.studio.scripts.scriptinspectorpanel.current.script", "current script")}</div>
        </div>
        <div className="console-scripts-panel-body">
          <ScriptsStudioEmptyState
            title={t("modules.studio.scripts.scriptinspectorpanel.no.script.has.been", "No script has been selected yet.")}
            copy={t("modules.studio.scripts.scriptinspectorpanel.after.selecting.script.draft", "After selecting a script draft, the script information, behavior contract, and release status are displayed here.")}
          />
        </div>
      </section>
    );
  }

  const isEmbeddedMode = appContext.mode === 'embedded';
  const availableActions = [
    t("modules.studio.scripts.scriptinspectorpanel.check", "check"),
    ...(scopeBacked ? [t("modules.studio.scripts.scriptinspectorpanel.save", "save"), t("modules.studio.scripts.scriptinspectorpanel.release", "release")] : []),
    ...(isEmbeddedMode ? [t("modules.studio.scripts.scriptinspectorpanel.test.run", "test run"), t("modules.studio.scripts.scriptinspectorpanel.ai.assisted", "AI-assisted")] : []),
  ];
  const unavailableActions = [
    ...(!scopeBacked
      ? [t("modules.studio.scripts.scriptinspectorpanel.save.requires.current.workspace", "Save (requires current workspace)"), t("modules.studio.scripts.scriptinspectorpanel.publish.requires.current.workspace", "Publish (requires current workspace)")]
      : []),
    ...(!isEmbeddedMode
      ? [t("modules.studio.scripts.scriptinspectorpanel.test.run.embedded.host", "Test run (embedded Host required)"), t("modules.studio.scripts.scriptinspectorpanel.ai.assistance.embedded.host", "AI assistance (embedded Host required)")]
      : []),
  ];
  const scopeScript = selectedDraft.scopeDetail?.script || null;

  return (
    <section className="console-scripts-panel">
      <div className="console-scripts-panel-header">
        <div className="console-scripts-eyebrow">{t("modules.studio.scripts.scriptinspectorpanel.script.information.2", "Script information")}</div>
        <div className="console-scripts-panel-header-title">
          {selectedDraft.package.entryBehaviorTypeName || selectedDraft.scriptId}
        </div>
      </div>
      <div className="console-scripts-panel-body">
        <ScriptsStudioSection
          eyebrow={t("modules.studio.scripts.scriptinspectorpanel.summary", "Summary")}
          title={t("modules.studio.scripts.scriptinspectorpanel.current.script.2", "current script")}
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.script.id", "Script ID")}</div>
              <div className="console-scripts-field-value">
                {selectedDraft.scriptId}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.saved.version", "saved version")}</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.revision)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.entry.class", "Entry class")}</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.package.entryBehaviorTypeName)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.current.file", "current file")}</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.selectedFilePath)}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow={t("modules.studio.scripts.scriptinspectorpanel.contract", "Contract")}
          title={t("modules.studio.scripts.scriptinspectorpanel.behavioral.contract", "behavioral contract")}
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.storage.location", "storage location")}</div>
              <div className="console-scripts-field-value">
                {scopeBacked
                  ? t("modules.studio.scripts.scriptinspectorpanel.workspace.id", "Workspace ID · {value1}", { value1: appContext.scopeId })
                  : t("modules.studio.scripts.scriptinspectorpanel.local.draft.only", "Local draft only")}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.input.type", "input type")}</div>
              <div className="console-scripts-copy-value">
                {appContext.scriptContract.inputType}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.readmodel.field", "ReadModel field")}</div>
              <div className="console-scripts-field-value">
                {appContext.scriptContract.readModelFields.join(', ') || '-'}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.host.mode", "Host mode")}</div>
              <div className="console-scripts-field-value">
                {formatStudioHostModeLabel(appContext.mode)}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow={t("modules.studio.scripts.scriptinspectorpanel.actions", "Actions")}
          title={t("modules.studio.scripts.scriptinspectorpanel.currently.available.operations", "Currently available operations")}
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.currently.available", "Currently available")}</div>
              <div className="console-scripts-field-value">
                {availableActions.join(', ')}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.currently.unavailable", "Currently unavailable")}</div>
              <div className="console-scripts-field-value">
                {unavailableActions.join(', ') || t("modules.studio.scripts.scriptinspectorpanel.none", "none")}
              </div>
            </div>
          </div>
          <div
            className="console-scripts-inline-actions"
            style={{ marginTop: 16, justifyContent: 'space-between' }}
          >
            <button
              type="button"
              className="console-scripts-ghost-action"
              onClick={onOpenAskAi}
              disabled={!canAskAi}
            >
              {t("modules.studio.scripts.scriptinspectorpanel.ai.assisted.2", "AI-assisted")}</button>
            <button
              type="button"
              className="console-scripts-solid-action"
              onClick={onOpenBindScope}
              disabled={!canBindScope}
            >
              {t("modules.studio.scripts.scriptinspectorpanel.bind.to.team", "Bind to team")}</button>
          </div>
          <div className="console-scripts-detail-copy">
            {getStudioHostModeTooltip(appContext.mode)}
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow={t("modules.studio.scripts.scriptinspectorpanel.more.information", "More information")}
          title={t("modules.studio.scripts.scriptinspectorpanel.run.and.publish", "Run and publish")}
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.define.actor", "Define actor")}</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.definitionActorId)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.run.actor", "Run actor")}</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.runtimeActorId)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.source.code.hash", "Source code hash")}</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.lastSourceHash)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.published.version", "Published version")}</div>
              <div className="console-scripts-field-value">
                {renderValue(scopeScript?.activeRevision)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.directory.actors", "Directory Actors")}</div>
              <div className="console-scripts-copy-value">
                {renderValue(scopeScript?.catalogActorId)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.entry.source.file", "Entry source file")}</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.package.entrySourcePath)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.baseline.version", "baseline version")}</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.baseRevision)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">{t("modules.studio.scripts.scriptinspectorpanel.update.time", "Update time")}</div>
              <div className="console-scripts-field-value">
                {formatScriptDateTime(selectedDraft.updatedAtUtc)}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>
      </div>
    </section>
  );
};

export default ScriptInspectorPanel;
