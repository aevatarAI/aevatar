import { ProCard } from '@ant-design/pro-components';
import { Button, Input, Space, Typography, theme } from 'antd';
import React from 'react';
import { moduleCardProps } from '@/shared/ui/proComponents';
import type { ScopeQueryDraft } from './scopeQuery';

type ScopeQueryCardProps = {
  draft: ScopeQueryDraft;
  onChange: (draft: ScopeQueryDraft) => void;
  onLoad: () => void;
  onReset?: () => void;
  loadLabel?: string;
  resolvedScopeId?: string | null;
  resolvedScopeSource?: string | null;
  onUseResolvedScope?: () => void;
};

const ScopeQueryCard: React.FC<ScopeQueryCardProps> = ({
  draft,
  onChange,
  onLoad,
  onReset,
  loadLabel = 'Load team',
  resolvedScopeId,
  resolvedScopeSource,
  onUseResolvedScope,
}) => {
  const normalizedResolvedScopeId = resolvedScopeId?.trim() ?? '';
  const normalizedResolvedScopeSource = resolvedScopeSource?.trim() ?? '';
  const canUseResolvedScope =
    normalizedResolvedScopeId.length > 0 &&
    draft.scopeId.trim() !== normalizedResolvedScopeId &&
    onUseResolvedScope;
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
          placeholder="Enter team ID"
          style={{ flex: '1 1 240px', minWidth: 0, width: '100%' }}
          value={draft.scopeId}
          onChange={(event) =>
            onChange({
              scopeId: event.target.value,
            })
          }
          onPressEnter={onLoad}
        />
        <Button type="primary" onClick={onLoad}>
          {loadLabel}
        </Button>
        {onReset ? <Button onClick={onReset}>Reset</Button> : null}
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
              Resolved team
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
                Resolved from the current session via {normalizedResolvedScopeSource}
              </Typography.Text>
            ) : null}
            {canUseResolvedScope ? (
              <div>
                <Button size="small" onClick={onUseResolvedScope}>
                  Use resolved team
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
            No team context was resolved from the current session. Enter a
            team ID manually. tenantId and appId stay platform-managed and
            hidden in this flow.
          </Typography.Text>
        )}
      </div>
    </ProCard>
  );
};

export default ScopeQueryCard;
