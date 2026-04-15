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
          title="还没有测试结果"
          copy="执行一次测试运行后，这里会显示运行时的物化结果。"
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
          title="还没有保存到当前团队"
          copy="先把当前草稿保存到团队里，这里才会显示已保存版本的目录状态。"
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
          title="还没有发布记录"
          copy="当草稿稳定后，使用“发布”提交演进提案，这里会显示发布结果。"
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
        <div className="console-scripts-section-label">校验失败</div>
        <div className="console-scripts-detail-copy">{validationError}</div>
      </div>
    );
  }

  if (!validationResult?.diagnostics.length) {
    return (
      <ScriptsStudioEmptyState
        title={validationPending ? '正在校验' : '没有诊断信息'}
        copy={
          validationPending
            ? '正在编译并校验当前草稿。'
            : '编译器和合约问题会显示在这里。'
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
    ? '校验中'
    : props.validationResult?.errorCount
      ? `${props.validationResult.errorCount} 个错误`
      : props.validationResult?.warningCount
        ? `${props.validationResult.warningCount} 个警告`
        : '通过';

  const runtimeSummary = props.selectedSnapshot
    ? props.selectedSnapshotView.output || props.selectedSnapshotView.status || '运行结果已就绪'
    : '开始一次测试运行后，这里会显示运行时快照。';
  const saveSummary = props.scopeDetail?.script
    ? `当前团队 ${props.scopeDetail.scopeId} 正在指向 ${props.scopeDetail.script.activeRevision}。`
    : '当前草稿还没有保存到团队目录。';
  const promotionSummary = props.selectedDecision
    ? props.selectedDecision.failureReason ||
      `Candidate ${props.selectedDecision.candidateRevision || '-'}`
    : '草稿稳定后，可以提交发布提案。';

  return (
    <div className="console-scripts-panel" style={{ borderRadius: 24 }}>
      <div className="console-scripts-panel-body">
        <div className="console-scripts-detail-grid">
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'diagnostics'}
            title="诊断"
            meta={
              props.validationPending ? '正在校验' : validationSummary
            }
            summary={
              props.validationError ||
              props.validationResult?.diagnostics[0]?.message ||
              '编译器和合约问题会显示在这里。'
            }
            status={props.validationPending ? 'pending' : ''}
            onClick={() => props.onChangeActiveResultTab('diagnostics')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'runtime'}
            title="测试运行"
            meta={
              props.selectedSnapshot
                ? formatScriptDateTime(props.selectedSnapshot.updatedAt)
                : '还未运行'
            }
            summary={runtimeSummary}
            status={props.selectedSnapshotView.status || ''}
            onClick={() => props.onChangeActiveResultTab('runtime')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'save'}
            title="已保存"
            meta={
              props.scopeDetail?.script
                ? formatScriptDateTime(props.scopeDetail.script.updatedAt)
                : '还未保存'
            }
            summary={saveSummary}
            status={props.scopeDetail?.script ? 'saved' : 'pending'}
            onClick={() => props.onChangeActiveResultTab('save')}
          />
          <ScriptsStudioResultCard
            active={props.activeResultTab === 'promotion'}
            title="发布"
            meta={
              props.selectedDecision?.candidateRevision ||
              props.selectedDecision?.proposalId ||
              '暂无候选版本'
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
                    ? '测试运行'
                    : props.activeResultTab === 'save'
                      ? '已保存状态'
                      : props.activeResultTab === 'promotion'
                        ? '发布结果'
                        : '诊断'}
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
