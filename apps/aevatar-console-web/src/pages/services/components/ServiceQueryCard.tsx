import { ProCard } from '@ant-design/pro-components';
import { Button, Input, InputNumber, Space, Typography } from 'antd';
import React from 'react';
import { moduleCardProps } from '@/shared/ui/proComponents';
import type { ServiceQueryDraft } from './serviceQuery';

type ServiceQueryCardProps = {
  draft: ServiceQueryDraft;
  onChange: (draft: ServiceQueryDraft) => void;
  onLoad: () => void;
  onReset?: () => void;
  loadLabel?: string;
};

const ServiceQueryCard: React.FC<ServiceQueryCardProps> = ({
  draft,
  onChange,
  onLoad,
  onReset,
  loadLabel = 'Load services',
}) => {
  return (
    <ProCard {...moduleCardProps}>
      <Space wrap>
        <Input
          placeholder="tenantId (scopeId)"
          style={{ width: 200 }}
          value={draft.tenantId}
          onChange={(event) =>
            onChange({
              ...draft,
              tenantId: event.target.value,
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
        <InputNumber
          min={1}
          max={500}
          value={draft.take}
          onChange={(value) =>
            onChange({
              ...draft,
              take: Number(value) || 200,
            })
          }
        />
        <Button type="primary" onClick={onLoad}>
          {loadLabel}
        </Button>
        {onReset ? <Button onClick={onReset}>Reset</Button> : null}
      </Space>
      <Typography.Text
        type="secondary"
        style={{ display: 'block', marginTop: 12 }}
      >
        Raw platform catalog only. End-user workflow assets should be opened
        from Scopes.
      </Typography.Text>
    </ProCard>
  );
};

export default ServiceQueryCard;
