import { ProCard } from '@ant-design/pro-components';
import { Button, Input, Space, Typography, theme } from 'antd';
import React from 'react';
import { moduleCardProps } from '@/shared/ui/proComponents';
import type { ScopeQueryDraft } from './scopeQuery';

type ScopeQueryCardProps = {
  activeScopeId?: string | null;
  draft: ScopeQueryDraft;
  onChange: (draft: ScopeQueryDraft) => void;
  onLoad: () => void;
  onReset?: () => void;
  resetDisabled?: boolean;
  loadLabel?: string;
  resolvedScopeId?: string | null;
  resolvedScopeSource?: string | null;
  onUseResolvedScope?: () => void;
};

const ScopeQueryCard: React.FC<ScopeQueryCardProps> = ({
  activeScopeId,
  draft,
  onChange,
  onLoad,
  onReset,
  resetDisabled,
  loadLabel = 'Load scope',
  resolvedScopeId,
  resolvedScopeSource,
  onUseResolvedScope,
}) => {
  const normalizedDraftScopeId = draft.scopeId.trim();
  const normalizedActiveScopeId = activeScopeId?.trim() ?? '';
  const normalizedResolvedScopeId = resolvedScopeId?.trim() ?? '';
  const normalizedResolvedScopeSource = resolvedScopeSource?.trim() ?? '';
  const canUseResolvedScope =
    normalizedResolvedScopeId.length > 0 &&
    normalizedDraftScopeId !== normalizedResolvedScopeId &&
    onUseResolvedScope;
  const loadIsNoOp =
    normalizedDraftScopeId.length > 0 &&
    normalizedDraftScopeId === normalizedActiveScopeId;
  const computedResetDisabled =
    normalizedDraftScopeId === normalizedResolvedScopeId &&
    normalizedActiveScopeId === normalizedResolvedScopeId;
  const resetIsNoOp = (resetDisabled ?? computedResetDisabled) === true;
  const { token } = theme.useToken();
  const helperLabelStyle = {
    color: token.colorTextSecondary,
    fontWeight: 500,
  };
  const helperCopyStyle = {
    color: token.colorTextTertiary,
  };
  const resolvedScopeValueStyle = {
    background: token.colorFillAlter,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: token.borderRadius,
    color: token.colorText,
    display: 'block',
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
    fontSize: 12,
    margin: 0,
    maxWidth: '100%',
    overflowWrap: 'anywhere' as const,
    padding: '6px 8px',
    whiteSpace: 'normal' as const,
    wordBreak: 'break-word' as const,
  };

  return (
    <ProCard {...moduleCardProps}>
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          flexWrap: 'wrap',
          gap: 8,
          width: '100%',
        }}
      >
        <Input
          allowClear
          placeholder="输入团队 scopeId"
          style={{ flex: '1 1 240px', minWidth: 0, width: '100%' }}
          value={draft.scopeId}
          onChange={(event) =>
            onChange({
              scopeId: event.target.value,
            })
          }
          onPressEnter={onLoad}
        />
        <Button disabled={!normalizedDraftScopeId || loadIsNoOp} type="primary" onClick={onLoad}>
          {loadLabel}
        </Button>
        {onReset ? (
          <Button disabled={resetDisabled ?? computedResetDisabled} onClick={onReset}>
            重置
          </Button>
        ) : null}
      </div>
      <div
        style={{
          display: 'grid',
          gap: 8,
          marginTop: 12,
          minWidth: 0,
        }}
      >
        {normalizedResolvedScopeId ? (
          <>
            <Typography.Text style={helperLabelStyle}>
              已解析团队
            </Typography.Text>
            <Typography.Paragraph
              copyable={{ text: normalizedResolvedScopeId }}
              style={resolvedScopeValueStyle}
            >
              {normalizedResolvedScopeId}
            </Typography.Paragraph>
            {normalizedResolvedScopeSource ? (
              <Typography.Text
                style={{
                  ...helperCopyStyle,
                  display: 'block',
                  maxWidth: '100%',
                  overflowWrap: 'anywhere',
                  whiteSpace: 'normal',
                  wordBreak: 'break-word',
                }}
              >
                当前会话已通过 {normalizedResolvedScopeSource} 解析出这个团队
              </Typography.Text>
            ) : null}
            {loadIsNoOp ? (
              <Typography.Text style={helperCopyStyle}>
                当前已加载这个团队，所以“{loadLabel}”不会再触发变化。
              </Typography.Text>
            ) : null}
            {resetIsNoOp ? (
              <Typography.Text style={helperCopyStyle}>
                当前已经回到会话解析出的团队，所以“重置”不会再触发变化。
              </Typography.Text>
            ) : null}
            {canUseResolvedScope ? (
              <div>
                <Button size="small" onClick={onUseResolvedScope}>
                  使用会话团队
                </Button>
              </div>
            ) : null}
          </>
        ) : (
          <Typography.Text
            style={{
              ...helperCopyStyle,
              display: 'block',
              maxWidth: '100%',
              overflowWrap: 'anywhere',
              whiteSpace: 'normal',
              wordBreak: 'break-word',
            }}
          >
            当前会话里没有自动解析出团队。请手动输入一个 scopeId。
          </Typography.Text>
        )}
      </div>
    </ProCard>
  );
};

export default ScopeQueryCard;
