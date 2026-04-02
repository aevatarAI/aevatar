import {
  SyncOutlined,
} from '@ant-design/icons';
import React from 'react';
import { formatScriptDateTime } from '@/shared/studio/scriptUtils';
import type {
  ScriptDefinitionBindingSnapshot,
  ScriptCatalogSnapshot,
  ScriptPromotionDecision,
  ScriptReadModelSnapshot,
  ScriptValidationDiagnostic,
  ScriptValidationResult,
  ScopedScriptDetail,
} from '@/shared/studio/scriptsModels';
import {
  ScriptsStudioEmptyState,
  ScriptsStudioResultCard,
} from '../ScriptsStudioChrome';

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
  selectedSnapshot: ScriptReadModelSnapshot | null;
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

function prettyPrintJson(rawJson: string | null | undefined): string {
  if (!rawJson) {
    return '-';
  }

  try {
    return JSON.stringify(JSON.parse(rawJson), null, 2);
  } catch {
    return rawJson;
  }
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
          title="No runtime output yet"
          copy="Run the current draft. The materialized read model will appear here."
        />
      );
    }

    return (
      <div className="console-scripts-detail-grid">
        <div className="console-scripts-detail-grid two-column">
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">Runtime output</div>
            <div className="console-scripts-detail-copy">
              <div>Actor: {selectedSnapshot.actorId}</div>
              <div>Revision: {selectedSnapshot.revision}</div>
              <div>State version: {selectedSnapshot.stateVersion}</div>
              <div>Status: {selectedSnapshotView.status || '-'}</div>
            </div>
          </div>
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">Command view</div>
            <div className="console-scripts-detail-copy">
              <div>Input: {selectedSnapshotView.input || '-'}</div>
              <div>Output: {selectedSnapshotView.output || '-'}</div>
              <div>Last command: {selectedSnapshotView.lastCommandId || '-'}</div>
              <div>
                Notes: {selectedSnapshotView.notes.join(', ') || '-'}
              </div>
            </div>
          </div>
        </div>
        <div className="console-scripts-detail-card muted">
          <div className="console-scripts-section-label">Read model payload</div>
          <pre className="console-scripts-pre" style={{ marginTop: 12 }}>
            {prettyPrintJson(selectedSnapshot.readModelPayloadJson)}
          </pre>
        </div>
      </div>
    );
  }

  if (activeResultTab === 'save') {
    if (!scopeDetail?.script) {
      return (
        <ScriptsStudioEmptyState
          title="Not saved into current scope catalog yet"
          copy="Save the active draft into the current scope catalog to inspect the stored catalog state."
        />
      );
    }

    return (
      <div className="console-scripts-detail-grid">
        <div className="console-scripts-detail-grid two-column">
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">Catalog</div>
            <div className="console-scripts-detail-copy">
              <div>Scope: {scopeDetail.scopeId}</div>
              <div>Revision: {scopeDetail.script.activeRevision}</div>
              <div>Updated: {formatScriptDateTime(scopeDetail.script.updatedAt)}</div>
            </div>
          </div>
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">Actors</div>
            <div className="console-scripts-detail-copy">
              <div>Definition: {scopeDetail.script.definitionActorId}</div>
              <div>Catalog: {scopeDetail.script.catalogActorId}</div>
              <div>Script ID: {scopeDetail.script.scriptId}</div>
            </div>
          </div>
        </div>
        <div className="console-scripts-detail-card muted">
          <div className="console-scripts-section-label">Revision line</div>
          <div className="console-scripts-detail-copy">
            <div>Previous: {selectedCatalog?.previousRevision || '-'}</div>
            <div>
              History:{' '}
              {selectedCatalog?.revisionHistory?.length
                ? selectedCatalog.revisionHistory.join(' -> ')
                : scopeDetail.script.activeRevision}
            </div>
            <div>Last proposal: {selectedCatalog?.lastProposalId || '-'}</div>
            <div>
              Source hash:{' '}
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
          title="No promotion submitted"
          copy="When the draft is stable, use Promote to send an evolution proposal and inspect the decision here."
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
            <div className="console-scripts-section-label">Revision</div>
            <div className="console-scripts-detail-copy">
              <div>Proposal: {selectedDecision.proposalId}</div>
              <div>Base: {selectedDecision.baseRevision || '-'}</div>
              <div>Candidate: {selectedDecision.candidateRevision || '-'}</div>
              <div>Script ID: {selectedDecision.scriptId || '-'}</div>
            </div>
          </div>
          <div className="console-scripts-detail-card">
            <div className="console-scripts-section-label">Decision</div>
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
              Definition snapshot
            </div>
            <div className="console-scripts-detail-copy">
              <div>
                Revision: {selectedDecision.definitionSnapshot.revision || '-'}
              </div>
              <div>
                Source hash:{' '}
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
                total ·{' '}
                {definitionSnapshotSummary.csharpCount}{' '}
                C# ·{' '}
                {definitionSnapshotSummary.protoCount}{' '}
                proto
              </div>
              <div>
                Contract:{' '}
                {definitionSnapshotSummary.contractLabel}
              </div>
            </div>
          </div>
        ) : null}
        <div className="console-scripts-detail-card muted">
          <div className="console-scripts-section-label">
            Validation diagnostics
          </div>
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
              No validation diagnostics were returned.
            </div>
          )}
        </div>
      </div>
    );
  }

  if (validationError) {
    return (
      <div className="console-scripts-detail-card">
        <div className="console-scripts-section-label">Validation error</div>
        <div className="console-scripts-detail-copy">{validationError}</div>
      </div>
    );
  }

  if (!validationResult?.diagnostics.length) {
    return (
      <ScriptsStudioEmptyState
        title={validationPending ? 'Validation in progress' : 'No diagnostics'}
        copy={
          validationPending
            ? 'Compiling the active draft package now.'
            : 'Compiler and contract problems will appear here.'
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
    ? 'Checking'
    : props.validationResult?.errorCount
      ? `${props.validationResult.errorCount} error${props.validationResult.errorCount === 1 ? '' : 's'}`
      : props.validationResult?.warningCount
        ? `${props.validationResult.warningCount} warning${props.validationResult.warningCount === 1 ? '' : 's'}`
        : 'Clean';

  const runtimeSummary = props.selectedSnapshot
    ? props.selectedSnapshotView.output || props.selectedSnapshotView.status || 'Runtime snapshot available'
    : 'Run the current draft to materialize a runtime read model.';
  const saveSummary = props.scopeDetail?.script
    ? `Scope ${props.scopeDetail.scopeId} is pointing at ${props.scopeDetail.script.activeRevision}.`
    : 'The active draft has not been saved into the current scope catalog yet.';
  const promotionSummary = props.selectedDecision
    ? props.selectedDecision.failureReason ||
      `Candidate ${props.selectedDecision.candidateRevision || '-'}`
    : 'Submit a promotion proposal when this draft is ready.';

  return (
    <div className="console-scripts-panel" style={{ borderRadius: 24 }}>
      <div className="console-scripts-panel-body">
        <div className="console-scripts-detail-grid">
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'diagnostics'}
            title="Diagnostics"
            meta={
              props.validationPending ? 'Running validation' : validationSummary
            }
            summary={
              props.validationError ||
              props.validationResult?.diagnostics[0]?.message ||
              'Compiler and contract problems will appear here.'
            }
            status={props.validationPending ? 'pending' : ''}
            onClick={() => props.onChangeActiveResultTab('diagnostics')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'runtime'}
            title="Draft Run"
            meta={
              props.selectedSnapshot
                ? formatScriptDateTime(props.selectedSnapshot.updatedAt)
                : 'Not run yet'
            }
            summary={runtimeSummary}
            status={props.selectedSnapshotView.status || ''}
            onClick={() => props.onChangeActiveResultTab('runtime')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'save'}
            title="Catalog"
            meta={
              props.scopeDetail?.script
                ? formatScriptDateTime(props.scopeDetail.script.updatedAt)
                : 'Not saved yet'
            }
            summary={saveSummary}
            status={props.scopeDetail?.script ? 'saved' : 'pending'}
            onClick={() => props.onChangeActiveResultTab('save')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'promotion'}
            title="Promotion"
            meta={
              props.selectedDecision?.candidateRevision ||
              props.selectedDecision?.proposalId ||
              'No candidate'
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
                <div className="console-scripts-eyebrow">Activity</div>
                <div className="console-scripts-section-title">
                  {props.activeResultTab === 'runtime'
                    ? 'Runtime output'
                    : props.activeResultTab === 'save'
                      ? 'Catalog state'
                      : props.activeResultTab === 'promotion'
                        ? 'Promotion proposal'
                        : 'Diagnostics'}
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
