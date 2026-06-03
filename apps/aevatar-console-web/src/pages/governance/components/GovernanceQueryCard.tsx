import { Button, Input, Select, Space } from 'antd';
import React, { useEffect, useMemo } from 'react';
import {
  applyGovernanceServiceSelection,
  findGovernanceServiceOption,
  type GovernanceDraft,
  type GovernanceServiceOption,
} from './governanceQuery';
import { t } from "@/shared/i18n/messages";

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
  loadLabel = t(
    'pages.governance.governancequerycard.load.governance.information',
    'Load governance information',
  ),
  onChange,
  onLoad,
  onReset,
}) => {
  const normalizedTenantId = draft.tenantId.trim();
  const normalizedAppId = draft.appId.trim();
  const normalizedNamespace = draft.namespace.trim();
  const normalizedServiceId = draft.serviceId.trim();
  const normalizedRevisionId = draft.revisionId.trim();
  const selectedScopeSegments = useMemo(
    () =>
      [
        normalizedTenantId,
        normalizedAppId,
        normalizedNamespace,
        normalizedServiceId,
      ].filter(Boolean),
    [
      normalizedAppId,
      normalizedNamespace,
      normalizedServiceId,
      normalizedTenantId,
    ],
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

  const loadDisabledReason = useMemo(() => {
    if (!normalizedTenantId || !normalizedNamespace) {
      return t("pages.governance.governancequerycard.fill.in.the.governance", "Fill in the governance scope first");
    }

    if (!serviceSearchEnabled) {
      return t("pages.governance.governancequerycard.the.service.cannot.yet", "The service cannot yet be loaded in the current scope");
    }

    if (!normalizedServiceId) {
      return serviceOptions.length === 0 ? t("pages.governance.governancequerycard.there.are.no.services", "There are no services available in the current scope") : t("pages.governance.governancequerycard.choose.service.first", "Choose a service first");
    }

    if (includeRevision && !normalizedRevisionId) {
      return revisionOptionsLoading ? t("pages.governance.governancequerycard.loading.version", "Loading version") : t("pages.governance.governancequerycard.select.version.first", "Select version first");
    }

    return '';
  }, [
    includeRevision,
    normalizedNamespace,
    normalizedRevisionId,
    normalizedServiceId,
    normalizedTenantId,
    revisionOptionsLoading,
    serviceOptions.length,
    serviceSearchEnabled,
  ]);

  const loadDisabled = loadDisabledReason.length > 0;

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
          display: 'flex',
          flexWrap: 'wrap',
          gap: 12,
          justifyContent: 'space-between',
        }}
      >
        <Space
          orientation="vertical"
          size={4}
          style={{ flex: '1 1 160px', minWidth: 160 }}
        >
          <span
            style={{
              color: 'var(--ant-color-primary)',
              fontSize: 12,
              fontWeight: 700,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
            }}
            >
            {t("pages.governance.governancequerycard.governance.scope", "Governance scope")}</span>
          <span
            style={{
              color: 'var(--ant-color-text)',
              fontSize: 20,
              fontWeight: 700,
              lineHeight: 1.2,
            }}
          >
            {t("pages.governance.governancequerycard.select.service.scope", "Select service scope")}</span>
        </Space>
        <div
          style={{
            alignItems: 'center',
            background: 'rgba(24, 144, 255, 0.06)',
            border: '1px solid rgba(24, 144, 255, 0.12)',
            borderRadius: 999,
            color: 'var(--ant-color-primary)',
            display: 'inline-flex',
            flex: '0 1 auto',
            fontSize: 12,
            fontWeight: 600,
            maxWidth: '100%',
            minHeight: 30,
            overflowWrap: 'anywhere',
            padding: '0 12px',
          }}
        >
          {selectedScopeSegments.length > 0
            ? t("pages.governance.governancequerycard.current.scope", "Current scope {value1}", { value1: selectedScopeSegments.join(' / ') })
            : t("pages.governance.governancequerycard.the.service.scope.has", "The service scope has not been locked yet")}
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
            {t("pages.governance.governancequerycard.team", "team")}</span>
          <Input
            placeholder={t("pages.governance.governancequerycard.team.id", "team ID")}
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
            {t("pages.governance.governancequerycard.application", "application")}</span>
          <Input
            placeholder={t("pages.governance.governancequerycard.application.id", "Application ID")}
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
            {t("pages.governance.governancequerycard.namespace", "namespace")}</span>
          <Input
            placeholder={t("pages.governance.governancequerycard.namespace.2", "namespace")}
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
            {t("pages.governance.governancequerycard.serve", "Serve")}</span>
          <Select
            allowClear
            placeholder={
              serviceSearchEnabled
                ? t("pages.governance.governancequerycard.select.service", "Select service")
                : t("pages.governance.governancequerycard.first.fill.in.the", "First fill in the team, application and namespace")
            }
            showSearch
            style={{ width: '100%' }}
            options={serviceOptions}
            disabled={!serviceSearchEnabled}
            notFoundContent={
              serviceSearchEnabled ? t("pages.governance.governancequerycard.there.are.no.services.2", "There are no services in the current scope") : t("pages.governance.governancequerycard.first.fill.in.the.2", "First fill in the team, application and namespace")
            }
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
              {t("pages.governance.governancequerycard.version", "Version")}</span>
            <Select
              allowClear
              placeholder={
                !draft.serviceId.trim()
                  ? t("pages.governance.governancequerycard.choose.service.first.2", "Choose a service first")
                  : revisionOptionsLoading
                    ? t("pages.governance.governancequerycard.loading.version.2", "Loading version")
                    : revisionOptions.length > 0
                      ? t("pages.governance.governancequerycard.select.version", "Select version")
                      : t("pages.governance.governancequerycard.no.version.yet", "No version yet")
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
          justifyContent: 'space-between',
        }}
      >
        <span
          style={{
            color: 'var(--ant-color-text-secondary)',
            fontSize: 12,
            minHeight: 18,
          }}
        >
          {loadDisabledReason}
        </span>
        <Space size={10}>
          {onReset ? (
            <Button
              aria-label={t("pages.governance.governancequerycard.reset", "Reset")}
              onClick={onReset}
            >
              {t("pages.governance.governancequerycard.reset", "Reset")}
            </Button>
          ) : null}
          <Button
            aria-disabled={loadDisabled}
            disabled={loadDisabled}
            style={
              loadDisabled
                ? {
                    background: 'var(--ant-color-fill-tertiary)',
                    borderColor: 'var(--ant-color-border-secondary)',
                    boxShadow: 'none',
                    color: 'var(--ant-color-text-tertiary)',
                    cursor: 'not-allowed',
                  }
                : undefined
            }
            type={loadDisabled ? 'default' : 'primary'}
            onClick={onLoad}
          >
            {loadLabel}
          </Button>
        </Space>
      </div>
    </div>
  );
};

export default GovernanceQueryCard;
