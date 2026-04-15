import { Button, Input, Select, Space } from 'antd';
import React, { useEffect, useMemo } from 'react';
import {
  applyGovernanceServiceSelection,
  findGovernanceServiceOption,
  type GovernanceDraft,
  type GovernanceServiceOption,
} from './governanceQuery';

export type GovernanceRevisionOption = {
  label: string;
  value: string;
};

type GovernanceQueryCardProps = {
  draft: GovernanceDraft;
  serviceOptions: GovernanceServiceOption[];
  serviceSearchEnabled?: boolean;
  includeRevision?: boolean;
  revisionOptions?: GovernanceRevisionOption[];
  revisionOptionsLoading?: boolean;
  loadLabel?: string;
  onChange: (draft: GovernanceDraft) => void;
  onLoad: () => void;
  onReset?: () => void;
};

const GovernanceQueryCard: React.FC<GovernanceQueryCardProps> = ({
  draft,
  serviceOptions,
  serviceSearchEnabled = true,
  includeRevision = false,
  revisionOptions = [],
  revisionOptionsLoading = false,
  loadLabel = '加载治理信息',
  onChange,
  onLoad,
  onReset,
}) => {
  const selectedServiceOption = useMemo(
    () => findGovernanceServiceOption(serviceOptions, draft),
    [draft, serviceOptions],
  );

  useEffect(() => {
    if (!selectedServiceOption) {
      return;
    }

    const hasIncompleteIdentity =
      !draft.tenantId.trim() || !draft.namespace.trim();
    if (!hasIncompleteIdentity) {
      return;
    }

    const nextDraft = applyGovernanceServiceSelection(draft, selectedServiceOption);
    if (
      nextDraft.tenantId === draft.tenantId &&
      nextDraft.namespace === draft.namespace &&
      nextDraft.serviceId === draft.serviceId
    ) {
      return;
    }

    onChange(nextDraft);
  }, [draft, onChange, selectedServiceOption]);

  return (
    <div
      style={{
        background: '#ffffff',
        border: '1px solid #e8e8e8',
        borderRadius: 12,
        padding: 16,
      }}
    >
      <Space wrap>
        <Input
          placeholder="团队 ID"
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
        <Select
          allowClear
          placeholder={
            serviceSearchEnabled
              ? '选择服务'
              : '先填写团队 ID 和命名空间'
          }
          showSearch
          style={{ minWidth: 260 }}
          options={serviceOptions}
          disabled={!serviceSearchEnabled}
          value={selectedServiceOption?.value}
          filterOption={(input, option) => {
            const normalizedInput = input.trim().toLowerCase();
            if (!normalizedInput) {
              return true;
            }

            const candidate = [
              option?.label,
              option?.serviceId,
              option?.tenantId,
              option?.namespace,
            ]
              .map((value) => String(value ?? '').toLowerCase())
              .join(' ');

            return candidate.includes(normalizedInput);
          }}
          onChange={(_, option) => {
            const selectedOption = Array.isArray(option) ? option[0] : option;
            const nextDraft = selectedOption
              ? applyGovernanceServiceSelection(draft, selectedOption)
              : { ...draft, appId: '', serviceId: '', revisionId: '' };
            const selectionChanged =
              nextDraft.tenantId !== draft.tenantId ||
              nextDraft.appId !== draft.appId ||
              nextDraft.namespace !== draft.namespace ||
              nextDraft.serviceId !== draft.serviceId;

            onChange(
              includeRevision && selectionChanged
                ? { ...nextDraft, revisionId: '' }
                : nextDraft,
            );
          }}
        />
        {includeRevision ? (
          <Select
            allowClear
            placeholder={
              !draft.serviceId.trim()
                ? '先选择服务'
                : revisionOptionsLoading
                  ? '正在加载版本'
                  : revisionOptions.length > 0
                    ? '选择版本'
                    : '暂无版本'
            }
            showSearch
            style={{ minWidth: 240 }}
            options={revisionOptions}
            loading={revisionOptionsLoading}
            disabled={
              !draft.serviceId.trim() ||
              revisionOptionsLoading ||
              revisionOptions.length === 0
            }
            value={draft.revisionId}
            optionFilterProp="label"
            onChange={(value) =>
              onChange({
                ...draft,
                revisionId: String(value ?? ''),
              })
            }
          />
        ) : null}
        <Button type="primary" onClick={onLoad}>
          {loadLabel}
        </Button>
        {onReset ? <Button onClick={onReset}>重置</Button> : null}
      </Space>
    </div>
  );
};

export default GovernanceQueryCard;
