import { ProCard } from '@ant-design/pro-components';
import { Button, Input, Select, Space } from 'antd';
import React from 'react';
import { moduleCardProps } from '@/shared/ui/proComponents';
import type { GovernanceDraft } from './governanceQuery';

type GovernanceQueryCardProps = {
  draft: GovernanceDraft;
  serviceOptions: Array<{ label: string; value: string }>;
  includeRevision?: boolean;
  loadLabel?: string;
  onChange: (draft: GovernanceDraft) => void;
  onLoad: () => void;
  onReset?: () => void;
};

const GovernanceQueryCard: React.FC<GovernanceQueryCardProps> = ({
  draft,
  serviceOptions,
  includeRevision = false,
  loadLabel = 'Load governance',
  onChange,
  onLoad,
  onReset,
}) => {
  return (
    <ProCard {...moduleCardProps}>
      <Space wrap>
        <Input
          placeholder="tenantId"
          style={{ width: 180 }}
          value={draft.tenantId}
          onChange={(event) =>
            onChange({
              ...draft,
              tenantId: event.target.value,
            })
          }
        />
        <Input
          placeholder="appId"
          style={{ width: 180 }}
          value={draft.appId}
          onChange={(event) =>
            onChange({
              ...draft,
              appId: event.target.value,
            })
          }
        />
        <Input
          placeholder="namespace"
          style={{ width: 180 }}
          value={draft.namespace}
          onChange={(event) =>
            onChange({
              ...draft,
              namespace: event.target.value,
            })
          }
        />
        <Select
          allowClear
          placeholder="serviceId"
          showSearch
          style={{ minWidth: 260 }}
          options={serviceOptions}
          value={draft.serviceId || undefined}
          onChange={(value) =>
            onChange({
              ...draft,
              serviceId: value ?? '',
            })
          }
          onSearch={(value) =>
            onChange({
              ...draft,
              serviceId: value,
            })
          }
        />
        {includeRevision ? (
          <Input
            placeholder="revisionId"
            style={{ minWidth: 220 }}
            value={draft.revisionId}
            onChange={(event) =>
              onChange({
                ...draft,
                revisionId: event.target.value,
              })
            }
          />
        ) : null}
        <Button type="primary" onClick={onLoad}>
          {loadLabel}
        </Button>
        {onReset ? <Button onClick={onReset}>Reset</Button> : null}
      </Space>
    </ProCard>
  );
};

export default GovernanceQueryCard;
