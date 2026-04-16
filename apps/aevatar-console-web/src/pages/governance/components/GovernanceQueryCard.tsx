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
  const selectedScopeSegments = useMemo(
    () =>
      [
        draft.tenantId.trim(),
        draft.appId.trim(),
        draft.namespace.trim(),
        draft.serviceId.trim(),
      ].filter(Boolean),
    [draft],
  );
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
        background:
          'linear-gradient(180deg, rgba(255,255,255,0.98) 0%, rgba(248,250,252,0.92) 100%)',
        border: '1px solid var(--ant-color-border-secondary)',
        borderRadius: 16,
        boxShadow: '0 12px 28px rgba(15, 23, 42, 0.04)',
        display: 'flex',
        flexDirection: 'column',
        gap: 16,
        padding: 18,
      }}
    >
      <div
        style={{
          alignItems: 'flex-start',
          display: 'grid',
          gap: 12,
          gridTemplateColumns: 'minmax(0, 1fr) auto',
        }}
      >
        <Space orientation="vertical" size={4} style={{ width: '100%' }}>
          <span
            style={{
              color: 'var(--ant-color-primary)',
              fontSize: 12,
              fontWeight: 700,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
            }}
            >
            治理范围
          </span>
          <span
            style={{
              color: 'var(--ant-color-text)',
              fontSize: 20,
              fontWeight: 700,
              lineHeight: 1.2,
            }}
          >
            选择服务范围
          </span>
        </Space>
        <div
          style={{
            alignItems: 'center',
            background: 'rgba(24, 144, 255, 0.06)',
            border: '1px solid rgba(24, 144, 255, 0.12)',
            borderRadius: 999,
            color: 'var(--ant-color-primary)',
            display: 'inline-flex',
            fontSize: 12,
            fontWeight: 600,
            minHeight: 30,
            padding: '0 12px',
            whiteSpace: 'nowrap',
          }}
        >
          {selectedScopeSegments.length > 0
            ? `当前范围 ${selectedScopeSegments.join(' / ')}`
            : '尚未锁定服务范围'}
        </div>
      </div>

      <div
        style={{
          display: 'grid',
          gap: 12,
          gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
        }}
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span
            style={{
              color: 'var(--ant-color-text-secondary)',
              fontSize: 12,
              fontWeight: 600,
            }}
          >
            团队
          </span>
          <Input
            placeholder="团队 ID"
            value={draft.tenantId}
            onChange={(event) =>
              onChange({
                ...draft,
                tenantId: event.target.value,
              })
            }
          />
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span
            style={{
              color: 'var(--ant-color-text-secondary)',
              fontSize: 12,
              fontWeight: 600,
            }}
          >
            应用
          </span>
          <Input
            placeholder="应用 ID"
            value={draft.appId}
            onChange={(event) =>
              onChange({
                ...draft,
                appId: event.target.value,
              })
            }
          />
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span
            style={{
              color: 'var(--ant-color-text-secondary)',
              fontSize: 12,
              fontWeight: 600,
            }}
          >
            命名空间
          </span>
          <Input
            placeholder="命名空间"
            value={draft.namespace}
            onChange={(event) =>
              onChange({
                ...draft,
                namespace: event.target.value,
              })
            }
          />
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span
            style={{
              color: 'var(--ant-color-text-secondary)',
              fontSize: 12,
              fontWeight: 600,
            }}
          >
            服务
          </span>
          <Select
            allowClear
            placeholder={
              serviceSearchEnabled
                ? '选择服务'
                : '先填写团队、应用和命名空间'
            }
            showSearch
            style={{ width: '100%' }}
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
                option?.appId,
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
        </div>

        {includeRevision ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <span
              style={{
                color: 'var(--ant-color-text-secondary)',
                fontSize: 12,
                fontWeight: 600,
              }}
            >
              版本
            </span>
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
              style={{ width: '100%' }}
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
          </div>
        ) : null}
      </div>

      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          flexWrap: 'wrap',
          gap: 10,
          justifyContent: 'flex-end',
        }}
      >
        <Space size={10}>
          {onReset ? <Button onClick={onReset}>重置</Button> : null}
          <Button type="primary" onClick={onLoad}>
            {loadLabel}
          </Button>
        </Space>
      </div>
    </div>
  );
};

export default GovernanceQueryCard;
