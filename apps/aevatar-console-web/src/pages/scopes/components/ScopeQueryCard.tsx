import { ProCard } from '@ant-design/pro-components';
import { Button, Input, Space, Typography } from 'antd';
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
  loadLabel = 'Load scope',
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

  return (
    <ProCard {...moduleCardProps}>
      <Space wrap>
        <Input
          allowClear
          placeholder="Enter scopeId"
          style={{ minWidth: 280 }}
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
      </Space>
      <div className="mt-3 flex flex-wrap items-center gap-2">
        {normalizedResolvedScopeId ? (
          <>
            <Typography.Text type="secondary">Resolved scope</Typography.Text>
            <Typography.Text code copyable>
              {normalizedResolvedScopeId}
            </Typography.Text>
            {normalizedResolvedScopeSource ? (
              <Typography.Text type="secondary">
                Resolved from the current session via {normalizedResolvedScopeSource}
              </Typography.Text>
            ) : null}
            {canUseResolvedScope ? (
              <Button size="small" onClick={onUseResolvedScope}>
                Use resolved scope
              </Button>
            ) : null}
          </>
        ) : (
          <Typography.Text type="secondary">
            No scope was resolved from the current session. Enter a scopeId
            manually. tenantId and appId stay platform-managed and hidden in this
            flow.
          </Typography.Text>
        )}
      </div>
    </ProCard>
  );
};

export default ScopeQueryCard;
