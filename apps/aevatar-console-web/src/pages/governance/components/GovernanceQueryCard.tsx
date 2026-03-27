import { ProCard } from '@ant-design/pro-components';
import { Button, Input, Select, Space, Typography } from 'antd';
import React, { useEffect, useMemo } from 'react';
import { moduleCardProps } from '@/shared/ui/proComponents';
import {
  applyGovernanceServiceSelection,
  findGovernanceServiceOption,
  type GovernanceDraft,
  type GovernanceServiceOption,
} from './governanceQuery';

type GovernanceQueryCardProps = {
  draft: GovernanceDraft;
  serviceOptions: GovernanceServiceOption[];
  serviceSearchEnabled?: boolean;
  includeRevision?: boolean;
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
  loadLabel = 'Load governance',
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
        <Select
          allowClear
          placeholder={
            serviceSearchEnabled
              ? 'Search platform service'
              : 'Enter tenantId and namespace first'
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

            onChange(
              selectedOption
                ? applyGovernanceServiceSelection(draft, selectedOption)
                : { ...draft, serviceId: '' },
            );
          }}
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
      <Typography.Text
        type="secondary"
        style={{ display: 'block', marginTop: 12 }}
      >
        {serviceSearchEnabled
          ? 'Select a service to hydrate the identity fields for this raw governance view.'
          : 'This raw governance view needs tenantId and namespace first. Most user flows should stay on Scopes or open this page from Platform Services.'}
      </Typography.Text>
    </ProCard>
  );
};

export default GovernanceQueryCard;
