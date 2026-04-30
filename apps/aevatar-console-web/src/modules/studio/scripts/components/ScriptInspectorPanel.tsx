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
          <div className="console-scripts-eyebrow">脚本信息</div>
          <div className="console-scripts-panel-header-title">当前脚本</div>
        </div>
        <div className="console-scripts-panel-body">
          <ScriptsStudioEmptyState
            title="还没有选中脚本。"
            copy="选择一个脚本草稿后，这里会显示脚本信息、行为合约和发布状态。"
          />
        </div>
      </section>
    );
  }

  const isEmbeddedMode = appContext.mode === 'embedded';
  const availableActions = [
    '校验',
    ...(scopeBacked ? ['保存', '发布'] : []),
    ...(isEmbeddedMode ? ['测试运行', 'AI 辅助'] : []),
  ];
  const unavailableActions = [
    ...(!scopeBacked
      ? ['保存（需要当前团队）', '发布（需要当前团队）']
      : []),
    ...(!isEmbeddedMode
      ? ['测试运行（需要嵌入式 Host）', 'AI 辅助（需要嵌入式 Host）']
      : []),
  ];
  const scopeScript = selectedDraft.scopeDetail?.script || null;

  return (
    <section className="console-scripts-panel">
      <div className="console-scripts-panel-header">
        <div className="console-scripts-eyebrow">脚本信息</div>
        <div className="console-scripts-panel-header-title">
          {selectedDraft.package.entryBehaviorTypeName || selectedDraft.scriptId}
        </div>
      </div>
      <div className="console-scripts-panel-body">
        <ScriptsStudioSection eyebrow="概要" title="当前脚本">
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">脚本 ID</div>
              <div className="console-scripts-field-value">
                {selectedDraft.scriptId}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">已保存版本</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.revision)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">入口类</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.package.entryBehaviorTypeName)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">当前文件</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.selectedFilePath)}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection
          eyebrow="合约"
          title="行为合约"
        >
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">存储位置</div>
              <div className="console-scripts-field-value">
                {scopeBacked
                  ? `当前团队 · ${appContext.scopeId}`
                  : '仅本地草稿'}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">输入类型</div>
              <div className="console-scripts-copy-value">
                {appContext.scriptContract.inputType}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">ReadModel 字段</div>
              <div className="console-scripts-field-value">
                {appContext.scriptContract.readModelFields.join(', ') || '-'}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">Host 模式</div>
              <div className="console-scripts-field-value">
                {formatStudioHostModeLabel(appContext.mode)}
              </div>
            </div>
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection eyebrow="操作" title="当前可用操作">
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">当前可用</div>
              <div className="console-scripts-field-value">
                {availableActions.join(', ')}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">当前不可用</div>
              <div className="console-scripts-field-value">
                {unavailableActions.join(', ') || '无'}
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
              AI 辅助
            </button>
            <button
              type="button"
              className="console-scripts-solid-action"
              onClick={onOpenBindScope}
              disabled={!canBindScope}
            >
              绑定到团队
            </button>
          </div>
          <div className="console-scripts-detail-copy">
            {getStudioHostModeTooltip(appContext.mode)}
          </div>
        </ScriptsStudioSection>

        <ScriptsStudioSection eyebrow="更多信息" title="运行与发布">
          <div className="console-scripts-detail-grid">
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">定义 Actor</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.definitionActorId)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">运行 Actor</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.runtimeActorId)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">源码哈希</div>
              <div className="console-scripts-copy-value">
                {renderValue(selectedDraft.lastSourceHash)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">已发布版本</div>
              <div className="console-scripts-field-value">
                {renderValue(scopeScript?.activeRevision)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">目录 Actor</div>
              <div className="console-scripts-copy-value">
                {renderValue(scopeScript?.catalogActorId)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">入口源文件</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.package.entrySourcePath)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">基线版本</div>
              <div className="console-scripts-field-value">
                {renderValue(selectedDraft.baseRevision)}
              </div>
            </div>
            <div className="console-scripts-field">
              <div className="console-scripts-field-label">更新时间</div>
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
