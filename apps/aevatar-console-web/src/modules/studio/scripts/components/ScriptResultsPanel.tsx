import {
  SyncOutlined,
} from '@ant-design/icons';
import React from 'react';
import { formatScriptDateTime } from '@/shared/studio/scriptUtils';
import type {
  ScriptDefinitionBindingSnapshot,
  ScriptCatalogSnapshot,
  ScriptPromotionDecision,
  ScriptRuntimeActivitySnapshot,
  ScriptValidationDiagnostic,
  ScriptValidationResult,
  ScopedScriptDetail,
} from '@/shared/studio/scriptsModels';
import {
  ScriptsStudioEmptyState,
  ScriptsStudioResultCard,
} from '../ScriptsStudioChrome';
import { t } from "@/shared/i18n/messages";

type SnapshotView = {
  input: string;
  output: string;
  status: string;
  lastCommandId: string;
  notes: string[];
};

type ScriptResultsPanelProps = {
  activeResultTab: string;
  activeDiagnosticKey: string;
  validationPending: boolean;
  validationError: string;
  validationResult: ScriptValidationResult | null;
  selectedSnapshot: ScriptRuntimeActivitySnapshot | null;
  selectedSnapshotView: SnapshotView;
  selectedCatalog: ScriptCatalogSnapshot | null;
  scopeDetail: ScopedScriptDetail | null;
  selectedDecision: ScriptPromotionDecision | null;
  onChangeActiveResultTab: (key: string) => void;
  onSelectDiagnostic: (diagnostic: ScriptValidationDiagnostic) => void;
};

function formatProblemLocation(diagnostic: ScriptValidationDiagnostic): string {
  const filePath = diagnostic.filePath || 'source';
  if (!diagnostic.startLine || !diagnostic.startColumn) {
    return filePath;
  }

  return `${filePath}:${diagnostic.startLine}:${diagnostic.startColumn}`;
}

function summarizeDefinitionSnapshot(
  definitionSnapshot: ScriptDefinitionBindingSnapshot | null | undefined,
): {
  fileCount: number;
  csharpCount: number;
  protoCount: number;
  entrySourcePath: string;
  contractLabel: string;
} {
  const scriptPackage = definitionSnapshot?.scriptPackage;
  const csharpCount = scriptPackage?.csharpSources?.length ?? 0;
  const protoCount = scriptPackage?.protoFiles?.length ?? 0;
  const entrySourcePath = scriptPackage?.entrySourcePath || '-';
  const contractLabel =
    definitionSnapshot?.readModelDescriptorFullName ||
    definitionSnapshot?.readModelTypeUrl ||
    definitionSnapshot?.stateDescriptorFullName ||
    definitionSnapshot?.stateTypeUrl ||
    '-';

  return {
    fileCount: csharpCount + protoCount,
    csharpCount,
    protoCount,
    entrySourcePath,
    contractLabel,
  };
}

function renderResultDetail(props: ScriptResultsPanelProps): React.JSX.Element {
  const {
    activeResultTab,
    validationPending,
    validationError,
    validationResult,
    selectedSnapshot,
    selectedSnapshotView,
    selectedCatalog,
    scopeDetail,
    selectedDecision,
  } = props;

  if (activeResultTab === 'runtime') {
    if (!selectedSnapshot) {
      return (
        <ScriptsStudioEmptyState
          title={t("modules.studio.scripts.scriptresultspanel.no.test.results.yet", "No test results yet")}
          copy={t("modules.studio.scripts.scriptresultspanel.after.executing.test.run", "After executing a test run, the runtime materialization results are displayed here.")}
        />
      );
    }

    return (
      <div className="console-scripts-detail-grid">
        <div className="console-scripts-detail-grid two-column">
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.runtime.output", "Runtime output")}</div>
            <div className="console-scripts-detail-copy">
              <div>Actor: {selectedSnapshot.actorId}</div>
              <div>Revision: {selectedSnapshot.revision}</div>
              <div>{t("modules.studio.scripts.scriptresultspanel.state.version", "State version:")}{selectedSnapshot.stateVersion}</div>
              <div>Status: {selectedSnapshotView.status || '-'}</div>
            </div>
          </div>
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.command.view", "Command view")}</div>
            <div className="console-scripts-detail-copy">
              <div>Input: {selectedSnapshotView.input || '-'}</div>
              <div>Output: {selectedSnapshotView.output || '-'}</div>
              <div>{t("modules.studio.scripts.scriptresultspanel.last.command", "Last command:")}{selectedSnapshotView.lastCommandId || '-'}</div>
              <div>
                Notes: {selectedSnapshotView.notes.join(', ') || '-'}
              </div>
            </div>
          </div>
        </div>
        <div className="console-scripts-detail-card muted">
          <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.runtime.activity", "Runtime activity")}</div>
          <div className="console-scripts-detail-copy">
            <div>Input: {selectedSnapshot.input || '-'}</div>
            <div>Output: {selectedSnapshot.output || '-'}</div>
            <div>{t("modules.studio.scripts.scriptresultspanel.last.command.2", "Last command:")}{selectedSnapshot.lastCommandId || '-'}</div>
            <div>Notes: {selectedSnapshot.notes.join(', ') || '-'}</div>
          </div>
        </div>
      </div>
    );
  }

  if (activeResultTab === 'save') {
    if (!scopeDetail?.script) {
      return (
        <ScriptsStudioEmptyState
          title={t("modules.studio.scripts.scriptresultspanel.not.saved.to.current", "Not saved to current workspace yet")}
          copy={t("modules.studio.scripts.scriptresultspanel.save.the.current.draft", "Save the current draft to the workspace first, then the directory status of the saved version will be displayed here.")}
        />
      );
    }

    return (
      <div className="console-scripts-detail-grid">
        <div className="console-scripts-detail-grid two-column">
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.catalog", "Catalog")}</div>
            <div className="console-scripts-detail-copy">
              <div>{t("modules.studio.scripts.scriptresultspanel.workspace.id", "Workspace ID:")}{scopeDetail.scopeId}</div>
              <div>Revision: {scopeDetail.script.activeRevision}</div>
              <div>Updated: {formatScriptDateTime(scopeDetail.script.updatedAt)}</div>
            </div>
          </div>
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.actors", "Actors")}</div>
            <div className="console-scripts-detail-copy">
              <div>Definition: {scopeDetail.script.definitionActorId}</div>
              <div>Catalog: {scopeDetail.script.catalogActorId}</div>
              <div>{t("modules.studio.scripts.scriptresultspanel.script.id", "Script ID:")}{scopeDetail.script.scriptId}</div>
            </div>
          </div>
        </div>
        <div className="console-scripts-detail-card muted">
          <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.revision.line", "Revision line")}</div>
          <div className="console-scripts-detail-copy">
            <div>Previous: {selectedCatalog?.previousRevision || '-'}</div>
            <div>
              History:{' '}
              {selectedCatalog?.revisionHistory?.length
                ? selectedCatalog.revisionHistory.join(' -> ')
                : scopeDetail.script.activeRevision}
            </div>
            <div>{t("modules.studio.scripts.scriptresultspanel.last.proposal", "Last proposal:")}{selectedCatalog?.lastProposalId || '-'}</div>
            <div>
              {t("modules.studio.scripts.scriptresultspanel.source.hash", "Source hash:")}{' '}
              {selectedCatalog?.activeSourceHash ||
                scopeDetail.script.activeSourceHash ||
                '-'}
            </div>
          </div>
        </div>
      </div>
    );
  }

  if (activeResultTab === 'promotion') {
    if (!selectedDecision) {
      return (
        <ScriptsStudioEmptyState
          title={t("modules.studio.scripts.scriptresultspanel.no.release.record.yet", "No release record yet")}
          copy={t("modules.studio.scripts.scriptresultspanel.when.the.draft.is", "When the draft is stable, use \"Publish\" to submit the evolution proposal, and the publication results will be displayed here.")}
        />
      );
    }

    const diagnostics =
      selectedDecision.validationReport?.diagnostics?.map((item) =>
        typeof item === 'string'
          ? item
          : item && typeof item === 'object' && 'message' in item
            ? String((item as { message?: string }).message || '')
            : '',
      )?.filter(Boolean) ?? [];
    const definitionSnapshotSummary = summarizeDefinitionSnapshot(
      selectedDecision.definitionSnapshot,
    );

    return (
      <div className="console-scripts-detail-grid">
        <div className="console-scripts-detail-grid two-column">
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.revision", "Revision")}</div>
            <div className="console-scripts-detail-copy">
              <div>Proposal: {selectedDecision.proposalId}</div>
              <div>Base: {selectedDecision.baseRevision || '-'}</div>
              <div>Candidate: {selectedDecision.candidateRevision || '-'}</div>
              <div>{t("modules.studio.scripts.scriptresultspanel.script.id.2", "Script ID:")}{selectedDecision.scriptId || '-'}</div>
            </div>
          </div>
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.decision", "Decision")}</div>
            <div className="console-scripts-detail-copy">
              <div>
                Status:{' '}
                {selectedDecision.status ||
                  (selectedDecision.accepted ? 'accepted' : 'rejected')}
              </div>
              <div>Catalog: {selectedDecision.catalogActorId || '-'}</div>
              <div>Definition: {selectedDecision.definitionActorId || '-'}</div>
              <div>Failure: {selectedDecision.failureReason || '-'}</div>
            </div>
          </div>
        </div>
        {selectedDecision.definitionSnapshot ? (
          <div className="console-scripts-detail-card muted">
            <div className="console-scripts-section-label">
              {t("modules.studio.scripts.scriptresultspanel.definition.snapshot", "Definition snapshot")}</div>
            <div className="console-scripts-detail-copy">
              <div>
                Revision: {selectedDecision.definitionSnapshot.revision || '-'}
              </div>
              <div>
                {t("modules.studio.scripts.scriptresultspanel.source.hash.2", "Source hash:")}{' '}
                {selectedDecision.definitionSnapshot.sourceHash || '-'}
              </div>
              <div>
                Schema:{' '}
                {selectedDecision.definitionSnapshot.readModelSchemaVersion || '-'}
                {selectedDecision.definitionSnapshot.readModelSchemaHash
                  ? ` · ${selectedDecision.definitionSnapshot.readModelSchemaHash}`
                  : ''}
              </div>
              <div>
                Entry:{' '}
                {definitionSnapshotSummary.entrySourcePath}
              </div>
              <div>
                Files:{' '}
                {definitionSnapshotSummary.fileCount}{' '}
                {t("modules.studio.scripts.scriptresultspanel.total", "total ·")}{' '}
                {definitionSnapshotSummary.csharpCount}{' '}
                {t("modules.studio.scripts.scriptresultspanel.copy", "C# ·")}{' '}
                {definitionSnapshotSummary.protoCount}{' '}
                {t("modules.studio.scripts.scriptresultspanel.proto", "proto")}</div>
              <div>
                Contract:{' '}
                {definitionSnapshotSummary.contractLabel}
              </div>
            </div>
          </div>
        ) : null}
        <div className="console-scripts-detail-card muted">
          <div className="console-scripts-section-label">
            {t("modules.studio.scripts.scriptresultspanel.validation.diagnostics", "Validation diagnostics")}</div>
          {diagnostics.length > 0 ? (
            <div className="console-scripts-detail-grid" style={{ marginTop: 12 }}>
              {diagnostics.map((item, index) => (
                <div key={`${item}-${index}`} className="console-scripts-detail-card">
                  <div className="console-scripts-detail-copy">{item}</div>
                </div>
              ))}
            </div>
          ) : (
            <div className="console-scripts-detail-copy">
              {t("modules.studio.scripts.scriptresultspanel.no.validation.diagnostics.were.returned", "No validation diagnostics were returned.")}</div>
          )}
        </div>
      </div>
    );
  }

  if (validationError) {
      return (
        <div className="console-scripts-detail-card">
        <div className="console-scripts-section-label">{t("modules.studio.scripts.scriptresultspanel.verification.failed", "Verification failed")}</div>
        <div className="console-scripts-detail-copy">{validationError}</div>
      </div>
    );
  }

  if (!validationResult?.diagnostics.length) {
    return (
      <ScriptsStudioEmptyState
        title={validationPending ? t("modules.studio.scripts.scriptresultspanel.verifying", "Verifying") : t("modules.studio.scripts.scriptresultspanel.no.diagnostic.information", "No diagnostic information")}
        copy={
          validationPending
            ? t("modules.studio.scripts.scriptresultspanel.compiling.and.verifying.the", "Compiling and verifying the current draft.")
            : t("modules.studio.scripts.scriptresultspanel.compiler.and.contract.issues", "Compiler and contract issues will appear here.")
        }
      />
    );
  }

  return (
      <div className="console-scripts-detail-grid">
        {validationResult.diagnostics.map((diagnostic, index) => (
          <button
            key={`${diagnostic.message}-${index}`}
            type="button"
            onClick={() => props.onSelectDiagnostic(diagnostic)}
            className={`console-scripts-detail-card console-scripts-detail-action ${
              props.activeDiagnosticKey ===
              `${diagnostic.filePath || ''}:${diagnostic.startLine || 0}:${diagnostic.startColumn || 0}:${diagnostic.message}`
                ? 'active'
                : ''
            }`}
          >
            <div className="console-scripts-section-label">
              {diagnostic.severity}
            </div>
            <div className="console-scripts-detail-copy">
              <div>{diagnostic.message}</div>
              <div>
                {formatProblemLocation(diagnostic)} · {diagnostic.origin || 'compiler'}
              </div>
            </div>
          </button>
        ))}
      </div>
  );
}

const ScriptResultsPanel: React.FC<ScriptResultsPanelProps> = (props) => {
  const validationSummary = props.validationPending
    ? t("modules.studio.scripts.scriptresultspanel.checking", "Checking")
    : props.validationResult?.errorCount
      ? t("modules.studio.scripts.scriptresultspanel.errors", "{value1} errors", { value1: props.validationResult.errorCount })
      : props.validationResult?.warningCount
        ? t("modules.studio.scripts.scriptresultspanel.warnings", "{value1} warnings", { value1: props.validationResult.warningCount })
        : t("modules.studio.scripts.scriptresultspanel.pass", "pass");

  const runtimeSummary = props.selectedSnapshot
    ? props.selectedSnapshotView.output || props.selectedSnapshotView.status || t("modules.studio.scripts.scriptresultspanel.the.running.result.is", "The running result is ready")
    : t("modules.studio.scripts.scriptresultspanel.after.starting.test.run", "After starting a test run, a runtime snapshot is displayed here.");
  const saveSummary = props.scopeDetail?.script
    ? t("modules.studio.scripts.scriptresultspanel.the.current.workspace.is", "The current workspace {value1} is pointing to {value2}.", { value1: props.scopeDetail.scopeId, value2: props.scopeDetail.script.activeRevision })
    : t("modules.studio.scripts.scriptresultspanel.the.current.draft.has", "The current draft has not been saved to the workspace directory.");
  const promotionSummary = props.selectedDecision
    ? props.selectedDecision.failureReason ||
      `Candidate ${props.selectedDecision.candidateRevision || '-'}`
    : t("modules.studio.scripts.scriptresultspanel.once.the.draft.is", "Once the draft is stable, a publication proposal can be submitted.");

  return (
    <div className="console-scripts-panel" style={{ borderRadius: 24 }}>
      <div className="console-scripts-panel-body">
        <div className="console-scripts-detail-grid">
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'diagnostics'}
            title={t("modules.studio.scripts.scriptresultspanel.diagnosis", "diagnosis")}
            meta={
              props.validationPending ? t("modules.studio.scripts.scriptresultspanel.verifying.2", "Verifying") : validationSummary
            }
            summary={
              props.validationError ||
              props.validationResult?.diagnostics[0]?.message ||
              t("modules.studio.scripts.scriptresultspanel.compiler.and.contract.issues.2", "Compiler and contract issues will appear here.")
            }
            status={props.validationPending ? 'pending' : ''}
            onClick={() => props.onChangeActiveResultTab('diagnostics')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'runtime'}
            title={t("modules.studio.scripts.scriptresultspanel.test.run", "test run")}
            meta={
              props.selectedSnapshot
                ? formatScriptDateTime(props.selectedSnapshot.updatedAt)
                : t("modules.studio.scripts.scriptresultspanel.not.running.yet", "Not running yet")
            }
            summary={runtimeSummary}
            status={props.selectedSnapshotView.status || ''}
            onClick={() => props.onChangeActiveResultTab('runtime')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'save'}
            title={t("modules.studio.scripts.scriptresultspanel.saved", "saved")}
            meta={
              props.scopeDetail?.script
                ? formatScriptDateTime(props.scopeDetail.script.updatedAt)
                : t("modules.studio.scripts.scriptresultspanel.not.saved.yet", "Not saved yet")
            }
            summary={saveSummary}
            status={props.scopeDetail?.script ? 'saved' : 'pending'}
            onClick={() => props.onChangeActiveResultTab('save')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'promotion'}
            title={t("modules.studio.scripts.scriptresultspanel.release", "release")}
            meta={
              props.selectedDecision?.candidateRevision ||
              props.selectedDecision?.proposalId ||
              t("modules.studio.scripts.scriptresultspanel.no.candidate.version.yet", "No candidate version yet")
            }
            summary={promotionSummary}
            status={
              props.selectedDecision?.status ||
              (props.selectedDecision
                ? props.selectedDecision.accepted
                  ? 'accepted'
                  : 'rejected'
                : '')
            }
            onClick={() => props.onChangeActiveResultTab('promotion')}
          />

          <div className="console-scripts-detail-card muted">
            <div className="console-scripts-inline-actions" style={{ justifyContent: 'space-between' }}>
              <div>
                <div className="console-scripts-eyebrow">{t("modules.studio.scripts.scriptresultspanel.activity", "Activity")}</div>
                <div className="console-scripts-section-title">
                  {props.activeResultTab === 'runtime'
                    ? t("modules.studio.scripts.scriptresultspanel.test.run.2", "test run")
                    : props.activeResultTab === 'save'
                      ? t("modules.studio.scripts.scriptresultspanel.saved.status", "Saved status")
                      : props.activeResultTab === 'promotion'
                        ? t("modules.studio.scripts.scriptresultspanel.publish.results", "publish results")
                        : t("modules.studio.scripts.scriptresultspanel.diagnosis.2", "diagnosis")}
                </div>
              </div>
              {props.validationPending ? <SyncOutlined spin /> : null}
            </div>
            <div style={{ marginTop: 16 }}>{renderResultDetail(props)}</div>
          </div>
        </div>
      </div>
    </div>
  );
};

export default ScriptResultsPanel;
