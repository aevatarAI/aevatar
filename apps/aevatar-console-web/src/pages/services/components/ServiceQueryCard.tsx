import { Button, Input, InputNumber, Space } from 'antd';
import React from 'react';
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
  loadLabel = '筛选服务',
}) => {
  return (
    <div
      style={{
        background: '#ffffff',
        border: '1px solid #e8e8e8',
        borderRadius: 8,
        padding: '10px 14px',
      }}
    >
      <Space wrap size={[8, 8]}>
        <Input
          placeholder="团队 ID"
          style={{ width: 220 }}
          value={draft.tenantId}
          onChange={(event) =>
            onChange({
              ...draft,
              tenantId: event.target.value,
            })
          }
        />
        <Input
          placeholder="应用 ID"
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
          placeholder="命名空间"
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
          style={{ width: 96 }}
          value={draft.take}
          onChange={(value) =>
            onChange({
              ...draft,
              take: Number(value) || 200,
            })
          }
        />
        <Button onClick={onLoad}>
          {loadLabel}
        </Button>
        {onReset ? <Button onClick={onReset}>重置</Button> : null}
      </Space>
    </div>
  );
};

export default ServiceQueryCard;
