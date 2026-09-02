import {
  PauseCircleOutlined,
  PercentageOutlined,
  ReloadOutlined,
  RollbackOutlined,
  SendOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Drawer,
  Empty,
  Input,
  InputNumber,
  Select,
  Space,
  Table,
  Tabs,
  Tag,
  Typography,
  theme,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import {
  readServiceQueryDraft,
  trimServiceQuery,
  type ServiceQueryDraft,
} from '@/pages/services/components/serviceQuery';
import { servicesApi } from '@/shared/api/servicesApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { history } from '@/shared/navigation/history';
import { buildPlatformDeploymentsHref } from '@/shared/navigation/platformRoutes';
import {
  invalidateServiceResourceQueries,
  serviceResourceQueryKeys,
} from '@/shared/query/serviceResourceQueryKeys';
import { resolveStudioScopeContext } from '@/shared/scope/context';
import { studioApi } from '@/shared/studio/api';
import type {
  ServiceCatalogSnapshot,
  ServiceDeploymentSnapshot,
  ServiceIdentityQuery,
  ServiceRevisionSnapshot,
  ServiceRolloutStageSnapshot,
  ServiceServingTargetInput,
  ServiceServingTargetSnapshot,
  ServiceTrafficEndpointSnapshot,
} from '@/shared/models/services';
import {
  AevatarContextDrawer,
  type AevatarBreadcrumbItem,
  AevatarInspectorEmpty,
} from '@/shared/ui/aevatarPageShells';
import {
  AevatarCompactTag,
  AevatarCompactText,
  aevatarMonoFontFamily,
  truncateMiddle,
} from '@/shared/ui/compactText';
import { getUserFacingIdentifierLabel } from '@/shared/ui/userFacingIdentifiers';
import InventoryReadinessState from '@/shared/ui/InventoryReadinessState';
import {
  aevatarDrawerBodyStyle,
  aevatarDrawerScrollStyle,
  buildAevatarMetricCardStyle,
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  formatAevatarStatusLabel,
  resolveAevatarMetricVisual,
  type AevatarStatusDomain,
  type AevatarThemeSurfaceToken,
} from '@/shared/ui/aevatarWorkbench';
import ConsoleMenuPageShell from '@/shared/ui/ConsoleMenuPageShell';
import ConsoleOperationNotice from '@/shared/ui/ConsoleOperationNotice';
import {
  cardStackStyle,
  summaryFieldLabelStyle,
  summaryMetricValueStyle,
} from '@/shared/ui/proComponents';
import {
  buildDeploymentReleaseHandoff,
  type DeploymentReleaseHandoff,
  type DeploymentReleaseHandoffAction,
} from './releaseHandoff';
import {
  buildDeploymentReleaseEvidenceSnapshot,
  type DeploymentReleaseEvidenceSnapshot,
  type DeploymentReleaseEvidenceStatus,
} from './releaseEvidence';
import { buildDeploymentDeactivateAvailability } from './deploymentActionAvailability';
import {
  buildRolloutActionAvailability,
  type RolloutControlAction,
} from './releaseActionAvailability';
import { buildServingTargetPlanStatus } from './servingTargetPlan';
import {
  formatConsoleMessage,
  t,
  type ConsoleMessageDescriptor,
} from '@/shared/i18n/messages';

type DeploymentWorkbenchView = 'catalog' | 'serving' | 'rollout' | 'traffic';

type DeploymentDrawerTab = 'candidate' | 'weights' | 'control';

type RolloutControlDefinition = {
  action: RolloutControlAction;
  danger?: boolean;
  icon: React.ReactNode;
  label: ConsoleMessageDescriptor;
  primary?: boolean;
};

const servingStateOptions = [
  {
    label: 'Active',
    value: 'active',
  },
  {
    label: 'Paused',
    value: 'paused',
  },
  {
    label: 'Draining',
    value: 'draining',
  },
  {
    label: 'Disabled',
    value: 'disabled',
  },
];

type DeploymentDrawerState = {
  open: boolean;
  tab: DeploymentDrawerTab;
};

type DeploymentInspectorState =
  | {
      open: false;
    }
  | {
      kind: 'serving';
      key: string;
      open: true;
    }
  | {
      kind: 'traffic';
      key: string;
      open: true;
    }
  | {
      kind: 'deployment';
      key: string;
      open: true;
    };

type DeploymentNotice = {
  message: string;
  tone: 'error' | 'info' | 'success' | 'warning';
};

type DeploymentTrafficRow = {
  endpointId: string;
  key: string;
  splitSummary: string;
  targetCount: number;
  targets: ReadonlyArray<ServiceTrafficEndpointSnapshot['targets'][number]>;
};

const defaultScopeServiceAppId = 'default';
const defaultScopeServiceNamespace = 'default';

function formatVersionVisibilityLabel(value: string | null | undefined): string {
  return value?.trim()
    ? t("pages.deployments.index.version.ready", "Version ready")
    : t("pages.deployments.index.no.version.information.yet", "No version information yet");
}

function formatDeploymentVisibilityLabel(value: string | null | undefined): string {
  return value?.trim()
    ? t("pages.deployments.index.deployment.attached", "Deployment attached")
    : t("pages.deployments.index.not.bound", "Not bound");
}

function formatActorVisibilityLabel(value: string | null | undefined): string {
  return value?.trim()
    ? t("pages.deployments.index.actor.available", "Actor available")
    : t("pages.deployments.index.none.yet", "None yet");
}

function formatTrafficTargetLabel(index: number): string {
  return t("pages.deployments.index.traffic.target.number", "Target {value1}", {
    value1: index + 1,
  });
}

function formatTrafficTargetSummary(
  target:
    | ServiceServingTargetSnapshot
    | ServiceTrafficEndpointSnapshot['targets'][number],
): string {
  return t("pages.deployments.index.copy.2", "{value1}% · {value2} · {value3}", {
    value1: target.allocationWeight,
    value2: formatAevatarStatusLabel(target.servingState || 'unknown'),
    value3: formatActorVisibilityLabel(target.primaryActorId),
  });
}
const tableHeaderCellStyle: React.CSSProperties = {
  background: 'var(--ant-color-fill-alter)',
  borderBottom: '1px solid var(--ant-color-border-secondary)',
  color: 'var(--ant-color-text-secondary)',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: 0.24,
  padding: '12px 14px',
  textAlign: 'left',
  textTransform: 'uppercase',
  whiteSpace: 'nowrap',
};
const tableCellStyle: React.CSSProperties = {
  borderBottom: '1px solid var(--ant-color-border-secondary)',
  padding: '12px 14px',
  verticalAlign: 'top',
};
const compactHintTagStyle: React.CSSProperties = {
  borderRadius: 999,
  fontWeight: 600,
  marginInlineEnd: 0,
};
const platformBreadcrumbItems: AevatarBreadcrumbItem[] = [
  {
    title: 'Platform',
  },
  {
    current: true,
    title: 'Deployments',
  },
];
const compactMonoValueStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-secondary)',
  fontFamily: aevatarMonoFontFamily,
  fontSize: 10.5,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};
const rolloutControlDefinitions: RolloutControlDefinition[] = [
  {
    action: 'advance',
    icon: <SendOutlined />,
    label: {
      defaultMessage: 'Advance rollout',
      id: 'pages.deployments.index.rollout.controls.advance',
    },
    primary: true,
  },
  {
    action: 'pause',
    icon: <PauseCircleOutlined />,
    label: {
      defaultMessage: 'Pause',
      id: 'pages.deployments.index.rollout.controls.pause',
    },
  },
  {
    action: 'resume',
    icon: <ReloadOutlined />,
    label: {
      defaultMessage: 'Resume',
      id: 'pages.deployments.index.rollout.controls.resume',
    },
  },
  {
    action: 'rollback',
    danger: true,
    icon: <RollbackOutlined />,
    label: {
      defaultMessage: 'Rollback rollout',
      id: 'pages.deployments.index.rollout.controls.rollback',
    },
  },
];

function buildScopePreview(
  tenantId: string,
  appId: string,
  namespace: string,
): string {
  return `${truncateMiddle(tenantId)}/${appId}/${namespace}`;
}

function formatDeploymentScopeLabel(query: ServiceIdentityQuery): string {
  const segments = [
    query.tenantId?.trim() || t("pages.deployments.index.no.team.set.up.2", "No team set up"),
    query.appId?.trim() || t("pages.deployments.index.app.not.set.up.2", "App not set up"),
    query.namespace?.trim() || t("pages.deployments.index.namespace.not.set.2", "namespace not set"),
  ];
  const resultWindow = query.take && query.take > 0 ? query.take : 200;

  return t("pages.deployments.index.items.2", "{value1} · {value2} items", { value1: segments.join(' / '), value2: resultWindow });
}

function isSameDeploymentScope(
  left: ServiceIdentityQuery,
  right: ServiceIdentityQuery,
): boolean {
  return (
    (left.tenantId?.trim() ?? '') === (right.tenantId?.trim() ?? '') &&
    (left.appId?.trim() ?? '') === (right.appId?.trim() ?? '') &&
    (left.namespace?.trim() ?? '') === (right.namespace?.trim() ?? '') &&
    (left.take ?? 200) === (right.take ?? 200)
  );
}

const CompactIdentifierText: React.FC<{
  color?: string;
  maxWidth?: React.CSSProperties['maxWidth'];
  singleLine?: boolean;
  strong?: boolean;
  value: string;
}> = ({
  color,
  maxWidth = '100%',
  singleLine = false,
  strong = false,
  value,
}) => {
  return (
    <AevatarCompactText
      color={color}
      head={4}
      maxWidth={maxWidth}
      monospace
      singleLine={singleLine}
      strong={strong}
      style={{ fontSize: 11 }}
      tail={4}
      value={value}
    />
  );
};

const CompactIdentifierTag: React.FC<{
  color?: string;
  style?: React.CSSProperties;
  value: string;
}> = ({ color, style, value }) => {
  return <AevatarCompactTag color={color} style={style} value={value} />;
};

const CompactLabelText: React.FC<{
  color?: string;
  maxChars?: number;
  maxWidth?: React.CSSProperties['maxWidth'];
  strong?: boolean;
  value: string;
}> = ({ color, maxChars = 20, maxWidth = 112, strong = false, value }) => {
  return (
    <AevatarCompactText
      color={color}
      maxChars={maxChars}
      maxWidth={maxWidth}
      mode="tail"
      strong={strong}
      style={{ fontSize: strong ? 13 : 12 }}
      value={value}
    />
  );
};

function readSelectedServiceId(): string {
  if (typeof window === 'undefined') {
    return '';
  }

  return (
    new URLSearchParams(window.location.search).get('serviceId')?.trim() ?? ''
  );
}

function readSelectedDeploymentId(): string {
  if (typeof window === 'undefined') {
    return '';
  }

  return (
    new URLSearchParams(window.location.search).get('deploymentId')?.trim() ??
    ''
  );
}

function buildRevisionSummary(
  revision: ServiceRevisionSnapshot | null | undefined,
): Array<{ label: string; value: string }> {
  if (!revision) {
    return [
      {
        label: t("pages.deployments.index.version.3", "Version"),
        value: t("pages.deployments.index.none.yet.9", "None yet"),
      },
    ];
  }

  return [
    {
      label: t("pages.deployments.index.version.4", "Version"),
      value: formatVersionVisibilityLabel(revision.revisionId),
    },
    {
      label: t("pages.deployments.index.state.5", "state"),
      value: formatAevatarStatusLabel(revision.status || 'unknown'),
    },
    {
      label: t("pages.deployments.index.number.of.entrances.2", "Number of entrances"),
      value: String(revision.endpoints.length),
    },
    {
      label: t("pages.deployments.index.products.2", "Products"),
      value: revision.artifactHash
        ? t("pages.deployments.index.artifact.ready", "Artifact ready")
        : 'n/a',
    },
    {
      label: t("pages.deployments.index.ready.to.complete.2", "Ready to complete"),
      value: formatDateTime(revision.preparedAt),
    },
    {
      label: t("pages.deployments.index.published.2", "Published"),
      value: formatDateTime(revision.publishedAt),
    },
  ];
}

function pickPreferredCandidateRevision(
  revisions: readonly ServiceRevisionSnapshot[],
  activeRevisionId: string,
): string {
  if (!revisions.length) {
    return '';
  }

  return (
    revisions.find((revision) => revision.revisionId !== activeRevisionId)
      ?.revisionId ??
    revisions[0]?.revisionId ??
    ''
  );
}

function buildTrafficRows(
  endpoints: readonly ServiceTrafficEndpointSnapshot[],
): DeploymentTrafficRow[] {
  return endpoints.map((endpoint) => ({
    endpointId: endpoint.endpointId,
    key: endpoint.endpointId,
    splitSummary:
      endpoint.targets
        .map((target) =>
          t("pages.deployments.index.traffic.target.summary", "{value1}% {value2}", {
            value1: target.allocationWeight,
            value2: formatAevatarStatusLabel(target.servingState || 'unknown'),
          }),
        )
        .join(' · ') || t("pages.deployments.index.no.traffic.target.yet.2", "No traffic target yet"),
    targetCount: endpoint.targets.length,
    targets: endpoint.targets,
  }));
}

function buildServingTargetKey(target: ServiceServingTargetSnapshot): string {
  return `${target.deploymentId}-${target.revisionId}-${target.servingState}`;
}

function describeTargets(
  targets:
    | ReadonlyArray<ServiceServingTargetSnapshot>
    | ReadonlyArray<ServiceTrafficEndpointSnapshot['targets'][number]>,
): string {
  if (!targets.length) {
    return t("pages.deployments.index.none.yet.10", "None yet");
  }

  return targets
    .map(
      (target) =>
        t("pages.deployments.index.traffic.target.summary", "{value1}% {value2}", {
          value1: target.allocationWeight,
          value2: formatAevatarStatusLabel(target.servingState || 'unknown'),
        }),
    )
    .join(' / ');
}

const DeploymentStatusTag: React.FC<{
  domain?: AevatarStatusDomain;
  status: string;
}> = ({ domain = 'governance', status }) => {
  const { token } = theme.useToken();

  return (
    <span
      style={buildAevatarTagStyle(
        token as AevatarThemeSurfaceToken,
        domain,
        status,
      )}
    >
      {formatAevatarStatusLabel(status)}
    </span>
  );
};

const MetricCard: React.FC<{
  label: string;
  tone?: 'default' | 'info' | 'success' | 'warning';
  value: string;
}> = ({ label, tone = 'default', value }) => {
  const { token } = theme.useToken();
  const visual = resolveAevatarMetricVisual(
    token as AevatarThemeSurfaceToken,
    tone,
  );

  return (
    <div
      style={buildAevatarMetricCardStyle(
        token as AevatarThemeSurfaceToken,
        tone,
      )}
    >
      <Typography.Text style={{ color: visual.labelColor }}>
        {label}
      </Typography.Text>
      <Typography.Text
        strong
        style={{
          ...summaryMetricValueStyle,
          color: visual.valueColor,
          fontSize: 20,
        }}
      >
        {value}
      </Typography.Text>
    </div>
  );
};

const WorkbenchSection: React.FC<{
  children: React.ReactNode;
  extra?: React.ReactNode;
  title: string;
}> = ({ children, extra, title }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  return (
    <div
      style={{
        ...buildAevatarPanelStyle(surfaceToken),
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
          gap: 12,
          justifyContent: 'space-between',
        }}
      >
        <Typography.Text
          strong
          style={{
            color: surfaceToken.colorTextHeading,
            fontSize: 16,
          }}
        >
          {title}
        </Typography.Text>
        {extra ? <div style={{ flexShrink: 0 }}>{extra}</div> : null}
      </div>
      {children}
    </div>
  );
};

const DetailFieldCard: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const primitiveValue =
    typeof value === 'string' || typeof value === 'number'
      ? String(value)
      : null;

  return (
    <div
      style={{
        background: 'rgba(248, 250, 252, 0.92)',
        border: `1px solid ${surfaceToken.colorBorderSecondary}`,
        borderRadius: 14,
        display: 'flex',
        flexDirection: 'column',
        gap: 8,
        minWidth: 0,
        padding: '14px 16px',
      }}
    >
      <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
      <div
        style={{
          color: surfaceToken.colorText,
          fontSize: 14,
          fontWeight: 600,
          lineHeight: 1.5,
          minWidth: 0,
          overflowWrap: 'anywhere',
        }}
      >
        {primitiveValue ? (
          <Typography.Text
            strong
            style={{
              color: 'inherit',
              fontSize: 'inherit',
              lineHeight: 'inherit',
            }}
          >
            {primitiveValue}
          </Typography.Text>
        ) : (
          value
        )}
      </div>
    </div>
  );
};

const DeploymentsScopeCard: React.FC<{
  draft: ServiceQueryDraft;
  draftScopeLabel: string;
  isDirty: boolean;
  isLoading?: boolean;
  loadedScopeLabel: string;
  onChange: (draft: ServiceQueryDraft) => void;
  onLoad: () => void;
  onReset: () => void;
  scopeLabel: string;
}> = ({
  draft,
  draftScopeLabel,
  isDirty,
  isLoading = false,
  loadedScopeLabel,
  onChange,
  onLoad,
  onReset,
  scopeLabel,
}) => (
  <div
    style={{
      background:
        'linear-gradient(180deg, rgba(255,255,255,0.98) 0%, rgba(248,250,252,0.92) 100%)',
      border: '1px solid var(--ant-color-border-secondary)',
      borderRadius: 14,
      boxShadow: '0 12px 28px rgba(15, 23, 42, 0.04)',
      display: 'flex',
      flexDirection: 'column',
      gap: 12,
      padding: 16,
    }}
  >
    <div
      style={{
        alignItems: 'center',
        display: 'flex',
        flexWrap: 'wrap',
        gap: 12,
        justifyContent: 'space-between',
      }}
    >
      <Space
        orientation="vertical"
        size={2}
        style={{ flex: '1 1 160px', minWidth: 160 }}
      >
        <span
          style={{
            color: 'var(--ant-color-primary)',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
          }}
        >
          {t("pages.deployments.index.deployment.scope.2", "deployment scope")}</span>
        <span
          style={{
            color: 'var(--ant-color-text)',
            fontSize: 16,
            fontWeight: 700,
            lineHeight: 1.2,
          }}
        >
          {t("pages.deployments.index.team.application.namespace.2", "team/Application/Namespace")}</span>
      </Space>
      <AevatarTooltip title={scopeLabel}>
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
            overflow: 'hidden',
            textOverflow: 'ellipsis',
          }}
        >
          {scopeLabel}
        </div>
      </AevatarTooltip>
    </div>

    <div
      style={{
        display: 'grid',
        gap: 12,
        gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
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
          {t("pages.deployments.index.team.2", "team")}</span>
        <Input
          placeholder={t("pages.deployments.index.team.id.2", "team ID")}
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
          {t("pages.deployments.index.application.2", "application")}</span>
        <Input
          placeholder={t("pages.deployments.index.application.id.2", "Application ID")}
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
          {t("pages.deployments.index.namespace.3", "namespace")}</span>
        <Input
          placeholder={t("pages.deployments.index.namespace.4", "namespace")}
          value={draft.namespace}
          onChange={(event) =>
            onChange({
              ...draft,
              namespace: event.target.value,
            })
          }
        />
      </div>
    </div>

    {isDirty ? (
      <Alert
        description={t("pages.deployments.index.the.current.service.metrics.2", "The current service metrics and lists are still from the loaded scope: {value1}. The draft range is: {value2}.", { value1: loadedScopeLabel, value2: draftScopeLabel })}
        message={t("pages.deployments.index.range.edited.but.not.2", "Range edited but not loaded yet")}
        showIcon
        type="warning"
      />
    ) : (
      <Alert
        description={t("pages.deployments.index.the.service.metrics.and.2", "The service metrics and list below are based on this loaded range: {value1}.", { value1: loadedScopeLabel })}
        message={t("pages.deployments.index.loaded.range.is.locked.2", "Loaded range is locked")}
        showIcon
        type="info"
      />
    )}

    <div
      style={{
        alignItems: 'center',
        display: 'flex',
        flexWrap: 'wrap',
        gap: 10,
        justifyContent: 'space-between',
      }}
    >
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          gap: 8,
        }}
      >
        <span
          style={{
            color: 'var(--ant-color-text-secondary)',
            fontSize: 11,
            fontWeight: 600,
            textTransform: 'uppercase',
            letterSpacing: '0.04em',
          }}
        >
          {t("pages.deployments.index.results.window.2", "results window")}</span>
        <InputNumber
          controls={false}
          min={1}
          max={500}
          size="small"
          style={{ width: 88 }}
          value={draft.take}
          onChange={(value) =>
            onChange({
              ...draft,
              take: Number(value) || 200,
            })
          }
        />
      </div>

      <Space size={8}>
        <Button aria-label={t("pages.deployments.index.reset.2", "reset")} size="small" onClick={onReset}>
          {t("pages.deployments.index.reset.3", "reset")}</Button>
        <Button
          loading={isLoading}
          size="small"
          type="primary"
          onClick={onLoad}
        >
          {isDirty ? t("pages.deployments.index.load.range.changes.2", "Load range changes") : t("pages.deployments.index.load.release.list.2", "Load release list")}
        </Button>
      </Space>
    </div>
  </div>
);

const RevisionSummaryCard: React.FC<{
  label: string;
  revision: ServiceRevisionSnapshot | null | undefined;
}> = ({ label, revision }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  return (
    <div
      style={{
        background: 'rgba(248, 250, 252, 0.92)',
        border: `1px solid ${surfaceToken.colorBorderSecondary}`,
        borderRadius: 14,
        display: 'flex',
        flexDirection: 'column',
        gap: 12,
        padding: 14,
      }}
    >
      <Typography.Text strong style={{ color: surfaceToken.colorTextHeading }}>
        {label}
      </Typography.Text>
      {revision ? (
        <>
          <Space wrap size={[8, 8]}>
            <DeploymentStatusTag status={revision.status} />
            <Tag>{formatVersionVisibilityLabel(revision.revisionId)}</Tag>
          </Space>
          <div
            style={{
              display: 'grid',
              gap: 10,
              gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
            }}
          >
            {buildRevisionSummary(revision).map((item) => (
              <DetailFieldCard
                key={`${label}-${item.label}`}
                label={item.label}
                value={item.value}
              />
            ))}
          </div>
        </>
      ) : (
        <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
          {t("pages.deployments.index.no.version.information.yet.2", "No version information yet")}</Typography.Text>
      )}
    </div>
  );
};

const TargetGroupCard: React.FC<{
  label: string;
  targets: readonly ServiceServingTargetSnapshot[];
}> = ({ label, targets }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  return (
    <div
      style={{
        background: 'rgba(248, 250, 252, 0.92)',
        border: `1px solid ${surfaceToken.colorBorderSecondary}`,
        borderRadius: 14,
        display: 'flex',
        flexDirection: 'column',
        gap: 12,
        padding: 14,
      }}
    >
      <Typography.Text strong style={{ color: surfaceToken.colorTextHeading }}>
        {label}
      </Typography.Text>
      {targets.length > 0 ? (
        targets.map((target) => (
          <div
            key={`${label}-${target.deploymentId}-${target.revisionId}`}
            style={{
              borderTop: `1px solid ${surfaceToken.colorBorderSecondary}`,
              display: 'flex',
              flexDirection: 'column',
              gap: 8,
              paddingTop: 12,
            }}
          >
            <Space wrap size={[8, 8]}>
              <Tag>{formatVersionVisibilityLabel(target.revisionId)}</Tag>
              <Tag>{formatDeploymentVisibilityLabel(target.deploymentId)}</Tag>
              <DeploymentStatusTag status={target.servingState || 'unknown'} />
              <Tag>{target.allocationWeight}%</Tag>
            </Space>
            <div style={{ color: surfaceToken.colorTextSecondary }}>
              {formatActorVisibilityLabel(target.primaryActorId)}{' '}
              · {target.enabledEndpointIds.join(', ') || t("pages.deployments.index.all.entrances.5", "All entrances")}
            </div>
          </div>
        ))
      ) : (
        <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
          {t("pages.deployments.index.no.target.yet.2", "No target yet")}</Typography.Text>
      )}
    </div>
  );
};

const DrawerSection: React.FC<{
  children: React.ReactNode;
  title: string;
}> = ({ children, title }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  return (
    <div
      style={{
        ...buildAevatarPanelStyle(surfaceToken),
        display: 'flex',
        flexDirection: 'column',
        gap: 14,
        padding: 18,
      }}
    >
      <Typography.Text
        strong
        style={{ color: surfaceToken.colorTextHeading, fontSize: 15 }}
      >
        {title}
      </Typography.Text>
      {children}
    </div>
  );
};

const ReleaseHandoffPanel: React.FC<{
  evidence: DeploymentReleaseEvidenceSnapshot;
  handoff: DeploymentReleaseHandoff;
  onClose: () => void;
  onOpenEvidence: () => void;
}> = ({ evidence, handoff, onClose, onOpenEvidence }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const statusCopy: Record<
    DeploymentReleaseEvidenceStatus,
    {
      color: string;
      label: string;
    }
  > = {
    observed: {
      color: 'green',
      label: t("pages.deployments.index.observed", "Observed"),
    },
    pending: {
      color: 'gold',
      label: t("pages.deployments.index.to.be.seen", "To be seen"),
    },
    review: {
      color: 'blue',
      label: t("pages.deployments.index.need.to.check", "Need to check"),
    },
  };

  return (
    <section
      aria-label={t("pages.deployments.index.release.action.handoff", "release action handoff")}
      style={{
        background: 'rgba(255, 251, 230, 0.72)',
        border: `1px solid ${surfaceToken.colorWarningBorder}`,
        borderRadius: surfaceToken.borderRadiusLG,
        display: 'flex',
        flexDirection: 'column',
        gap: 14,
        padding: 16,
      }}
    >
      <div
        style={{
          alignItems: 'flex-start',
          display: 'flex',
          gap: 12,
          justifyContent: 'space-between',
        }}
      >
        <Space orientation="vertical" size={4}>
          <Space wrap size={[8, 8]}>
            <Tag color="gold" style={compactHintTagStyle}>
              {handoff.pendingLabel}
            </Tag>
            <Tag color="blue" style={compactHintTagStyle}>
              {handoff.evidenceViewLabel}
            </Tag>
          </Space>
          <Typography.Text
            strong
            style={{
              color: surfaceToken.colorTextHeading,
              fontSize: 15,
            }}
          >
            {handoff.title}
          </Typography.Text>
          <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
            {handoff.evidenceDescription}
          </Typography.Text>
          <Typography.Text strong style={{ color: surfaceToken.colorText }}>
            {evidence.summary}
          </Typography.Text>
        </Space>
        <Space wrap size={[8, 8]} style={{ justifyContent: 'flex-end' }}>
          <Button size="small" onClick={onOpenEvidence}>
            {t("pages.deployments.index.check", "Check")}{handoff.evidenceViewLabel}{t("pages.deployments.index.evidence", "evidence")}</Button>
          <Button size="small" type="text" onClick={onClose}>
            {t("pages.deployments.index.closure", "closure")}</Button>
        </Space>
      </div>

      <div
        style={{
          display: 'grid',
          gap: 8,
          gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
        }}
      >
        {handoff.summaryItems.map((item) => (
          <div
            key={`${handoff.id}-${item.label}`}
            style={{
              background: surfaceToken.colorBgContainer,
              border: `1px solid ${surfaceToken.colorBorderSecondary}`,
              borderRadius: surfaceToken.borderRadius,
              minWidth: 0,
              padding: '10px 12px',
            }}
          >
            <Typography.Text style={summaryFieldLabelStyle}>
              {item.label}
            </Typography.Text>
            <div style={{ marginTop: 4, minWidth: 0 }}>
              <CompactIdentifierText
                color={surfaceToken.colorText}
                maxWidth="100%"
                singleLine
                value={item.value}
              />
            </div>
          </div>
        ))}
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {evidence.checks.map((check) => (
          <div
            key={`${handoff.id}-${check.key}`}
            style={{
              alignItems: 'flex-start',
              display: 'grid',
              gap: 8,
              gridTemplateColumns: 'auto minmax(0, 1fr)',
            }}
          >
            <Tag
              color={statusCopy[check.status].color}
              style={compactHintTagStyle}
            >
              {statusCopy[check.status].label}
            </Tag>
            <Space orientation="vertical" size={2} style={{ minWidth: 0 }}>
              <Typography.Text strong style={{ color: surfaceToken.colorText }}>
                {check.label}
              </Typography.Text>
              <Typography.Text
                style={{ color: surfaceToken.colorTextSecondary }}
              >
                {check.detail}
              </Typography.Text>
            </Space>
          </div>
        ))}
      </div>
    </section>
  );
};

const DeploymentsPage: React.FC = () => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const queryClient = useQueryClient();

  const [draft, setDraft] = useState<ServiceQueryDraft>(() =>
    readServiceQueryDraft(),
  );
  const [query, setQuery] = useState<ServiceIdentityQuery>(() =>
    trimServiceQuery(readServiceQueryDraft()),
  );
  const [selectedServiceId, setSelectedServiceId] = useState(() =>
    readSelectedServiceId(),
  );
  const [selectedDeploymentId, setSelectedDeploymentId] = useState(() =>
    readSelectedDeploymentId(),
  );
  const [view, setView] = useState<DeploymentWorkbenchView>('catalog');
  const [drawerState, setDrawerState] = useState<DeploymentDrawerState>({
    open: false,
    tab: 'candidate',
  });
  const [inspectorState, setInspectorState] =
    useState<DeploymentInspectorState>({
      open: false,
    });
  const [drawerReason, setDrawerReason] = useState('');
  const [editableTargets, setEditableTargets] = useState<
    ServiceServingTargetInput[]
  >([]);
  const [candidateRevisionId, setCandidateRevisionId] = useState('');
  const [notice, setNotice] = useState<DeploymentNotice | null>(null);
  const [releaseHandoff, setReleaseHandoff] =
    useState<DeploymentReleaseHandoff | null>(null);

  const authSessionQuery = useQuery({
    queryKey: ['deployments', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  useEffect(() => {
    if (
      draft.tenantId.trim() ||
      draft.appId.trim() ||
      draft.namespace.trim() ||
      !resolvedScope?.scopeId?.trim()
    ) {
      return;
    }

    const nextDraft = {
      ...draft,
      appId: defaultScopeServiceAppId,
      namespace: defaultScopeServiceNamespace,
      tenantId: resolvedScope.scopeId.trim(),
    };
    setDraft(nextDraft);
    setQuery(trimServiceQuery(nextDraft));
  }, [draft, resolvedScope?.scopeId]);

  const servicesQuery = useQuery({
    queryFn: () => servicesApi.listServices(query),
    queryKey: serviceResourceQueryKeys.list(query),
  });

  const serviceDetailQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getService(selectedServiceId, query),
    queryKey: serviceResourceQueryKeys.detail(query, selectedServiceId),
  });
  const revisionsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getRevisions(selectedServiceId, query),
    queryKey: serviceResourceQueryKeys.revisions(query, selectedServiceId),
  });
  const deploymentsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getDeployments(selectedServiceId, query),
    queryKey: serviceResourceQueryKeys.deployments(query, selectedServiceId),
  });
  const servingQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getServingSet(selectedServiceId, query),
    queryKey: serviceResourceQueryKeys.serving(query, selectedServiceId),
  });
  const rolloutQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getRollout(selectedServiceId, query),
    queryKey: serviceResourceQueryKeys.rollout(query, selectedServiceId),
  });
  const trafficQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getTraffic(selectedServiceId, query),
    queryKey: serviceResourceQueryKeys.traffic(query, selectedServiceId),
  });

  const selectedService = useMemo(
    () =>
      serviceDetailQuery.data ??
      servicesQuery.data?.find(
        (service) => service.serviceId === selectedServiceId,
      ) ??
      null,
    [selectedServiceId, serviceDetailQuery.data, servicesQuery.data],
  );

  useEffect(() => {
    if (servicesQuery.data === undefined) {
      return;
    }

    const services = servicesQuery.data ?? [];
    if (!services.length) {
      if (selectedServiceId) {
        setSelectedServiceId('');
      }
      if (selectedDeploymentId) {
        setSelectedDeploymentId('');
      }
      return;
    }

    if (!selectedServiceId.trim()) {
      return;
    }

    if (services.some((service) => service.serviceId === selectedServiceId)) {
      return;
    }

    setSelectedServiceId('');
    if (selectedDeploymentId) {
      setSelectedDeploymentId('');
    }
  }, [selectedDeploymentId, selectedServiceId, servicesQuery.data]);

  useEffect(() => {
    history.replace(
      buildPlatformDeploymentsHref({
        appId: query.appId,
        deploymentId: selectedDeploymentId || undefined,
        namespace: query.namespace,
        serviceId: selectedServiceId || undefined,
        take: query.take,
        tenantId: query.tenantId,
      }),
    );
  }, [query, selectedDeploymentId, selectedServiceId]);

  useEffect(() => {
    if (selectedServiceId.trim() && deploymentsQuery.data === undefined) {
      return;
    }

    const deployments = deploymentsQuery.data?.deployments ?? [];
    if (!selectedServiceId.trim()) {
      if (selectedDeploymentId) {
        setSelectedDeploymentId('');
      }
      return;
    }

    if (!selectedDeploymentId) {
      return;
    }

    if (
      deployments.some(
        (deployment) => deployment.deploymentId === selectedDeploymentId,
      )
    ) {
      return;
    }

    setSelectedDeploymentId('');
  }, [
    deploymentsQuery.data?.deployments,
    selectedDeploymentId,
    selectedServiceId,
  ]);

  useEffect(() => {
    setEditableTargets(
      (servingQuery.data?.targets ?? []).map((target) => ({
        allocationWeight: target.allocationWeight,
        enabledEndpointIds: target.enabledEndpointIds,
        revisionId: target.revisionId,
        servingState: target.servingState,
      })),
    );
  }, [servingQuery.data?.updatedAt]);

  const activeRevisionId =
    serviceDetailQuery.data?.activeServingRevisionId ||
    serviceDetailQuery.data?.defaultServingRevisionId ||
    '';

  useEffect(() => {
    const revisions = revisionsQuery.data?.revisions ?? [];
    if (!revisions.length) {
      return;
    }

    if (
      candidateRevisionId.trim() &&
      revisions.some((revision) => revision.revisionId === candidateRevisionId)
    ) {
      return;
    }

    setCandidateRevisionId(
      pickPreferredCandidateRevision(revisions, activeRevisionId),
    );
  }, [activeRevisionId, candidateRevisionId, revisionsQuery.data?.revisions]);

  const selectedDeployment = useMemo(
    () =>
      deploymentsQuery.data?.deployments.find(
        (deployment) => deployment.deploymentId === selectedDeploymentId,
      ) ?? null,
    [deploymentsQuery.data?.deployments, selectedDeploymentId],
  );

  const activeDeployment = useMemo(() => {
    const deployments = deploymentsQuery.data?.deployments ?? [];
    const currentDeploymentId =
      serviceDetailQuery.data?.deploymentId?.trim() ?? '';

    return (
      deployments.find(
        (deployment) => deployment.deploymentId === currentDeploymentId,
      ) ??
      deployments.find((deployment) =>
        deployment.status.toLowerCase().includes('active'),
      ) ??
      null
    );
  }, [
    deploymentsQuery.data?.deployments,
    serviceDetailQuery.data?.deploymentId,
  ]);

  const focusDeployment = selectedDeployment ?? activeDeployment;

  const currentStage = useMemo(() => {
    const rollout = rolloutQuery.data;
    if (!rollout?.stages.length) {
      return null;
    }

    return (
      rollout.stages.find(
        (stage) => stage.stageIndex === rollout.currentStageIndex,
      ) ?? rollout.stages[rollout.stages.length - 1]
    );
  }, [rolloutQuery.data]);

  const activeRevision = useMemo(
    () =>
      revisionsQuery.data?.revisions.find(
        (revision) => revision.revisionId === activeRevisionId,
      ) ?? null,
    [activeRevisionId, revisionsQuery.data?.revisions],
  );

  const candidateRevision = useMemo(
    () =>
      revisionsQuery.data?.revisions.find(
        (revision) => revision.revisionId === candidateRevisionId,
      ) ?? null,
    [candidateRevisionId, revisionsQuery.data?.revisions],
  );

  const trafficRows = useMemo(
    () => buildTrafficRows(trafficQuery.data?.endpoints ?? []),
    [trafficQuery.data?.endpoints],
  );

  const selectedServingTarget = useMemo(() => {
    if (!inspectorState.open || inspectorState.kind !== 'serving') {
      return null;
    }

    return (
      servingQuery.data?.targets.find(
        (target) => buildServingTargetKey(target) === inspectorState.key,
      ) ?? null
    );
  }, [inspectorState, servingQuery.data?.targets]);

  const selectedTrafficRow = useMemo(() => {
    if (!inspectorState.open || inspectorState.kind !== 'traffic') {
      return null;
    }

    return trafficRows.find((row) => row.key === inspectorState.key) ?? null;
  }, [inspectorState, trafficRows]);

  const inspectedDeployment = useMemo(() => {
    if (!inspectorState.open || inspectorState.kind !== 'deployment') {
      return null;
    }

    return (
      deploymentsQuery.data?.deployments.find(
        (deployment) => deployment.deploymentId === inspectorState.key,
      ) ?? null
    );
  }, [deploymentsQuery.data?.deployments, inspectorState]);

  const draftScopeLabel = useMemo(
    () => formatDeploymentScopeLabel(trimServiceQuery(draft)),
    [draft],
  );

  const loadedScopeLabel = useMemo(
    () => formatDeploymentScopeLabel(query),
    [query],
  );

  const isScopeDirty = useMemo(
    () => !isSameDeploymentScope(trimServiceQuery(draft), query),
    [draft, query],
  );

  const currentScopeLabel = useMemo(() => {
    const segments = [
      query.tenantId?.trim() ?? draft.tenantId.trim(),
      query.appId?.trim() ?? draft.appId.trim(),
      query.namespace?.trim() ?? draft.namespace.trim(),
    ].filter(Boolean);

    return segments.length > 0
      ? t("pages.deployments.index.current.scope.2", "Current scope {value1}", { value1: segments.join(' / ') })
      : t("pages.deployments.index.the.service.scope.has.2", "The service scope has not been locked yet");
  }, [draft.appId, draft.namespace, draft.tenantId, query]);

  const deploymentDigest = useMemo(
    () => ({
      deployments: deploymentsQuery.data?.deployments.length ?? 0,
      endpoints:
        trafficQuery.data?.endpoints.length ??
        serviceDetailQuery.data?.endpoints.length ??
        0,
      stage:
        currentStage && rolloutQuery.data
          ? `${currentStage.stageIndex + 1}/${rolloutQuery.data.stages.length}`
          : t("pages.deployments.index.no.activity.rollout.2", "No activity rollout"),
      targets: servingQuery.data?.targets.length ?? 0,
    }),
    [
      currentStage,
      deploymentsQuery.data?.deployments.length,
      rolloutQuery.data,
      serviceDetailQuery.data?.endpoints.length,
      servingQuery.data?.targets.length,
      trafficQuery.data?.endpoints.length,
    ],
  );

  const visibleServiceDigest = useMemo(
    () => ({
      endpointServices: (servicesQuery.data ?? []).filter(
        (service) => service.endpoints.length > 0,
      ).length,
      services: servicesQuery.data?.length ?? 0,
      servingServices: (servicesQuery.data ?? []).filter((service) =>
        service.deploymentId.trim(),
      ).length,
      waitingServices: (servicesQuery.data ?? []).filter(
        (service) => !service.deploymentId.trim(),
      ).length,
    }),
    [servicesQuery.data],
  );
  const deploymentInventoryReady =
    servicesQuery.data !== undefined && !servicesQuery.error;

  const releaseEvidence = useMemo(
    () =>
      releaseHandoff
        ? buildDeploymentReleaseEvidenceSnapshot({
            deployments: deploymentsQuery.data?.deployments ?? [],
            handoff: releaseHandoff,
            rollout: rolloutQuery.data,
            serving: servingQuery.data,
            traffic: trafficQuery.data,
          })
        : null,
    [
      deploymentsQuery.data?.deployments,
      releaseHandoff,
      rolloutQuery.data,
      servingQuery.data,
      trafficQuery.data,
    ],
  );
  const servingTargetPlanStatus = useMemo(
    () => buildServingTargetPlanStatus(editableTargets),
    [editableTargets],
  );
  const rolloutActionAvailability = useMemo(
    () => buildRolloutActionAvailability(rolloutQuery.data),
    [rolloutQuery.data],
  );
  const servingEntryAvailability = useMemo(() => {
    const targetCount = servingQuery.data?.targets.length ?? 0;

    return {
      enabled: targetCount > 0,
      reason:
        targetCount > 0
          ? t("pages.deployments.index.after.traffic.weighting.is", "After traffic weighting is turned on, the weight total and serving status will be verified before submission.")
          : t("pages.deployments.index.there.are.currently.no.3", "There are currently no serving targets and traffic adjustment cannot be submitted."),
    };
  }, [servingQuery.data?.targets.length]);
  const rolloutControlEntryAvailability = useMemo(() => {
    const enabled = Object.values(rolloutActionAvailability).some(
      (availability) => availability.enabled,
    );

    return {
      enabled,
      reason: enabled
        ? t("pages.deployments.index.after.release.control.is", "After release control is turned on, only actions allowed by the current rollout life cycle will be retained.")
        : rolloutActionAvailability.advance.reason,
    };
  }, [rolloutActionAvailability]);
  const deploymentDeactivateAvailability = useMemo(
    () => buildDeploymentDeactivateAvailability(inspectedDeployment),
    [inspectedDeployment],
  );

  const invalidateDetailQueries = useCallback(async () => {
    await invalidateServiceResourceQueries(queryClient);
  }, [queryClient]);

  const openDrawer = useCallback((tab: DeploymentDrawerTab) => {
    setDrawerState({
      open: true,
      tab,
    });
  }, []);

  const openInspector = useCallback(
    (state: Exclude<DeploymentInspectorState, { open: false }>) => {
      if (state.kind === 'deployment') {
        setSelectedDeploymentId(state.key);
      }
      setInspectorState(state);
    },
    [],
  );

  const recordReleaseHandoff = useCallback(
    (
      action: DeploymentReleaseHandoffAction,
      receipt: Parameters<typeof buildDeploymentReleaseHandoff>[0]['receipt'],
      options: {
        deploymentId?: string;
      } = {},
    ) => {
      const handoff = buildDeploymentReleaseHandoff({
        action,
        activeRevisionId,
        candidateRevisionId:
          action === 'deploy-candidate' ? candidateRevisionId : undefined,
        createdAt: new Date().toISOString(),
        deploymentId:
          options.deploymentId ||
          focusDeployment?.deploymentId ||
          selectedDeploymentId ||
          undefined,
        endpointCount: trafficRows.length,
        receipt,
        rolloutId: rolloutQuery.data?.rolloutId,
        rolloutStageLabel:
          currentStage && rolloutQuery.data
            ? `${currentStage.stageIndex + 1}/${rolloutQuery.data.stages.length}`
            : undefined,
        serviceId: selectedServiceId,
        targetCount:
          servingQuery.data?.targets.length ?? editableTargets.length,
      });

      setReleaseHandoff(handoff);
      setNotice({
        message: handoff.noticeMessage,
        tone: handoff.noticeTone,
      });
    },
    [
      activeRevisionId,
      candidateRevisionId,
      currentStage,
      editableTargets.length,
      focusDeployment?.deploymentId,
      rolloutQuery.data,
      selectedDeploymentId,
      selectedServiceId,
      servingQuery.data?.targets.length,
      trafficRows.length,
    ],
  );

  const deployMutation = useMutation({
    mutationFn: () => {
      if (!candidateRevisionId.trim()) {
        throw new Error(t("pages.deployments.index.please.select.release.candidate.2", "Please select a release candidate first."));
      }

      return servicesApi.deployRevision(selectedServiceId, {
        ...query,
        revisionId: candidateRevisionId,
      });
    },
    onError: (error: Error) => {
      setReleaseHandoff(null);
      setNotice({
        message: error.message || t("pages.deployments.index.release.candidate.failed.2", "Release candidate failed."),
        tone: 'error',
      });
    },
    onSuccess: async (receipt) => {
      recordReleaseHandoff('deploy-candidate', receipt);
      await invalidateDetailQueries();
    },
  });

  const weightsMutation = useMutation({
    mutationFn: () => {
      if (!servingTargetPlanStatus.enabled) {
        throw new Error(servingTargetPlanStatus.reason);
      }

      return servicesApi.replaceServingTargets(selectedServiceId, {
        ...query,
        reason: drawerReason,
        rolloutId: rolloutQuery.data?.rolloutId,
        targets: editableTargets,
      });
    },
    onError: (error: Error) => {
      setReleaseHandoff(null);
      setNotice({
        message: error.message || t("pages.deployments.index.failed.to.apply.serving.2", "Failed to apply serving targets."),
        tone: 'error',
      });
    },
    onSuccess: async (receipt) => {
      recordReleaseHandoff('replace-serving-targets', receipt);
      await invalidateDetailQueries();
    },
  });

  const rolloutMutation = useMutation({
    mutationFn: async (kind: 'advance' | 'pause' | 'resume' | 'rollback') => {
      const availability = rolloutActionAvailability[kind];
      if (!availability.enabled) {
        throw new Error(availability.reason);
      }

      const rolloutId = rolloutQuery.data?.rolloutId;
      if (!rolloutId) {
        throw new Error(t("pages.deployments.index.there.is.no.active.2", "There is no active rollout for the current service."));
      }

      if (kind === 'advance') {
        return servicesApi.advanceRollout(selectedServiceId, rolloutId, query);
      }

      if (kind === 'pause') {
        return servicesApi.pauseRollout(selectedServiceId, rolloutId, {
          ...query,
          reason: drawerReason,
        });
      }

      if (kind === 'resume') {
        return servicesApi.resumeRollout(selectedServiceId, rolloutId, query);
      }

      return servicesApi.rollbackRollout(selectedServiceId, rolloutId, {
        ...query,
        reason: drawerReason,
      });
    },
    onError: (error: Error) => {
      setReleaseHandoff(null);
      setNotice({
        message: error.message || t("pages.deployments.index.release.control.action.submission.2", "Release control action submission failed."),
        tone: 'error',
      });
    },
    onSuccess: async (receipt, kind) => {
      const actionByKind: Record<
        RolloutControlAction,
        DeploymentReleaseHandoffAction
      > = {
        advance: 'advance-rollout',
        pause: 'pause-rollout',
        resume: 'resume-rollout',
        rollback: 'rollback-rollout',
      };
      recordReleaseHandoff(actionByKind[kind], receipt);
      await invalidateDetailQueries();
    },
  });

  const deactivateMutation = useMutation({
    mutationFn: (deploymentId: string) => {
      if (!deploymentId.trim()) {
        throw new Error(t("pages.deployments.index.please.select.deployment.2", "Please select a deployment."));
      }
      const deployment = deploymentsQuery.data?.deployments.find(
        (item) => item.deploymentId === deploymentId,
      );
      const availability = buildDeploymentDeactivateAvailability(deployment);
      if (!availability.enabled) {
        throw new Error(availability.reason);
      }

      return servicesApi.deactivateDeployment(
        selectedServiceId,
        deploymentId,
        query,
      );
    },
    onError: (error: Error) => {
      setReleaseHandoff(null);
      setNotice({
        message: error.message || t("pages.deployments.index.deactivating.the.deployment.failed.2", "Deactivating the deployment failed."),
        tone: 'error',
      });
    },
    onSuccess: async (receipt, deploymentId) => {
      recordReleaseHandoff('deactivate-deployment', receipt, {
        deploymentId,
      });
      await invalidateDetailQueries();
    },
  });

  const servingColumns = useMemo<ColumnsType<ServiceServingTargetSnapshot>>(
    () => [
      {
        dataIndex: 'revisionId',
        key: 'revisionId',
        title: 'Revision',
        render: (value: string, record) => (
          <Space orientation="vertical" size={4}>
            <Typography.Text strong>
              {formatVersionVisibilityLabel(value)}
            </Typography.Text>
            <Typography.Text type="secondary">
              {formatDeploymentVisibilityLabel(record.deploymentId)}
            </Typography.Text>
          </Space>
        ),
      },
      {
        dataIndex: 'primaryActorId',
        key: 'primaryActorId',
        title: t("pages.deployments.index.main.actor.6", "Main actor"),
        render: (value: string) =>
          formatActorVisibilityLabel(value),
      },
      {
        dataIndex: 'allocationWeight',
        key: 'allocationWeight',
        title: t("pages.deployments.index.weight.3", "weight"),
        render: (value: number) => `${value}%`,
      },
      {
        dataIndex: 'servingState',
        key: 'servingState',
        title: t("pages.deployments.index.serving.status.4", "serving status"),
        render: (value: string) => (
          <DeploymentStatusTag status={value || 'unknown'} />
        ),
      },
      {
        dataIndex: 'enabledEndpointIds',
        key: 'enabledEndpointIds',
        title: t("pages.deployments.index.entrance.5", "Entrance"),
        render: (value: readonly string[]) =>
          value.length > 0 ? value.join(', ') : t("pages.deployments.index.all.entrances.6", "All entrances"),
      },
      {
        key: 'actions',
        title: t("pages.deployments.index.operate.5", "operate"),
        render: (_, record) => (
          <Button
            size="small"
            onClick={() =>
              openInspector({
                kind: 'serving',
                key: buildServingTargetKey(record),
                open: true,
              })
            }
          >
            {t("pages.deployments.index.check.the.details.4", "check the details")}</Button>
        ),
      },
    ],
    [openInspector],
  );

  const rolloutColumns: ColumnsType<ServiceRolloutStageSnapshot> = [
      {
        dataIndex: 'stageIndex',
        key: 'stageIndex',
        title: 'Stage',
        render: (value: number) => `Stage ${value + 1}`,
      },
      {
        dataIndex: 'stageId',
        key: 'stageId',
        title: t("pages.deployments.index.logo.2", "logo"),
      },
      {
        dataIndex: 'targets',
        key: 'targets',
        title: t("pages.deployments.index.target.allocation.2", "target allocation"),
        render: (targets: readonly ServiceServingTargetSnapshot[]) =>
          describeTargets(targets),
      },
  ];

  const trafficColumns = useMemo<ColumnsType<DeploymentTrafficRow>>(
    () => [
      {
        dataIndex: 'endpointId',
        key: 'endpointId',
        title: 'Endpoint',
        render: (value: string) => (
          <CompactIdentifierText maxWidth={180} singleLine value={value} />
        ),
      },
      {
        dataIndex: 'targetCount',
        key: 'targetCount',
        title: t("pages.deployments.index.number.of.targets.3", "number of targets"),
      },
      {
        dataIndex: 'splitSummary',
        key: 'splitSummary',
        title: t("pages.deployments.index.traffic.distribution.2", "traffic distribution"),
      },
      {
        dataIndex: 'targets',
        key: 'states',
        title: t("pages.deployments.index.serving.status.5", "serving status"),
        render: (targets: DeploymentTrafficRow['targets']) => (
          <Space wrap size={[8, 8]}>
            {targets.map((target) => (
              <Tag key={`${target.deploymentId}-${target.revisionId}`}>
                {formatAevatarStatusLabel(target.servingState || 'unknown')}
              </Tag>
            ))}
          </Space>
        ),
      },
      {
        key: 'actions',
        title: t("pages.deployments.index.operate.6", "operate"),
        render: (_, record) => (
          <Button
            size="small"
            onClick={() =>
              openInspector({
                kind: 'traffic',
                key: record.key,
                open: true,
              })
            }
          >
            {t("pages.deployments.index.check.the.details.5", "check the details")}</Button>
        ),
      },
    ],
    [openInspector],
  );

  const drawerDeploymentColumns = useMemo<
    ColumnsType<ServiceDeploymentSnapshot>
  >(
    () => [
      {
        dataIndex: 'deploymentId',
        key: 'deploymentId',
        title: 'Deployment',
        width: 220,
        render: (value: string, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>
              {formatDeploymentVisibilityLabel(value)}
            </Typography.Text>
            <Typography.Text type="secondary">
              {formatVersionVisibilityLabel(record.revisionId)}
            </Typography.Text>
          </Space>
        ),
      },
      {
        dataIndex: 'primaryActorId',
        key: 'primaryActorId',
        title: t("pages.deployments.index.main.actor.7", "Main actor"),
        width: 150,
        render: (value: string) =>
          formatActorVisibilityLabel(value),
      },
      {
        dataIndex: 'status',
        key: 'status',
        title: t("pages.deployments.index.state.6", "state"),
        width: 104,
        render: (value: string) => (
          <DeploymentStatusTag status={value || 'unknown'} />
        ),
      },
      {
        dataIndex: 'activatedAt',
        key: 'activatedAt',
        title: t("pages.deployments.index.activation.time.3", "activation time"),
        width: 148,
        render: (value: string | null) => (
          <Typography.Text
            style={{
              color: surfaceToken.colorTextSecondary,
              whiteSpace: 'nowrap',
            }}
          >
            {formatDateTime(value)}
          </Typography.Text>
        ),
      },
      {
        dataIndex: 'updatedAt',
        key: 'updatedAt',
        title: t("pages.deployments.index.latest.updates.5", "Latest updates"),
        width: 148,
        render: (value: string) => (
          <Typography.Text
            style={{
              color: surfaceToken.colorTextSecondary,
              whiteSpace: 'nowrap',
            }}
          >
            {formatDateTime(value)}
          </Typography.Text>
        ),
      },
      {
        key: 'actions',
        title: t("pages.deployments.index.operate.7", "operate"),
        width: 104,
        render: (_, record) => (
          <Button
            size="small"
            onClick={() =>
              openInspector({
                kind: 'deployment',
                key: record.deploymentId,
                open: true,
              })
            }
          >
            {t("pages.deployments.index.check.the.details.6", "check the details")}</Button>
        ),
      },
    ],
    [openInspector, surfaceToken.colorTextSecondary],
  );

  const handleDraftChange = useCallback((nextDraft: ServiceQueryDraft) => {
    setDraft(nextDraft);
    setSelectedServiceId('');
    setSelectedDeploymentId('');
    setReleaseHandoff(null);
  }, []);

  const openServiceWorkbench = useCallback(
    (service: Pick<ServiceCatalogSnapshot, 'deploymentId' | 'serviceId'>) => {
      setSelectedServiceId(service.serviceId);
      setSelectedDeploymentId(service.deploymentId || '');
      setInspectorState({ open: false });
      setReleaseHandoff(null);
      setView('catalog');
    },
    [],
  );

  const closeServiceWorkbench = useCallback(() => {
    setSelectedServiceId('');
    setSelectedDeploymentId('');
    setInspectorState({ open: false });
    setReleaseHandoff(null);
    setDrawerState((current) => ({
      ...current,
      open: false,
    }));
  }, []);

  const handleReset = useCallback(() => {
    const nextDraft = isScopeDirty
      ? {
          appId: query.appId?.trim() ?? '',
          namespace: query.namespace?.trim() ?? '',
          take: query.take && query.take > 0 ? query.take : 200,
          tenantId: query.tenantId?.trim() ?? '',
        }
      : resolvedScope?.scopeId?.trim()
        ? {
            ...readServiceQueryDraft(''),
            appId: defaultScopeServiceAppId,
            namespace: defaultScopeServiceNamespace,
            tenantId: resolvedScope.scopeId.trim(),
          }
        : readServiceQueryDraft('');
    setDraft(nextDraft);
    if (!isScopeDirty) {
      setQuery(trimServiceQuery(nextDraft));
    }
    setSelectedServiceId('');
    setSelectedDeploymentId('');
    setCandidateRevisionId('');
    setDrawerReason('');
    setReleaseHandoff(null);
    setView('catalog');
  }, [isScopeDirty, query, resolvedScope?.scopeId]);

  const drawerSubtitle = selectedService
    ? `${selectedService.tenantId}/${selectedService.appId}/${selectedService.namespace}`
    : t("pages.deployments.index.publish.workspace.3", "Publish workspace");

  return (
    <ConsoleMenuPageShell
      breadcrumbItems={platformBreadcrumbItems}
      description={t("pages.deployments.index.deployments.is.platform.release.2", "Deployments is Platform's release workbench, focusing on current serving, rollout progress and traffic distribution.")}
      title="Deployments"
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <ConsoleOperationNotice
          errorMessage={t(
            'pages.deployments.index.actionFailed',
            'Deployment action could not be completed. Try again.',
          )}
          notice={
            notice ? { message: notice.message, type: notice.tone } : null
          }
          onClose={() => setNotice(null)}
        />

        <DeploymentsScopeCard
          draft={draft}
          draftScopeLabel={draftScopeLabel}
          isDirty={isScopeDirty}
          isLoading={servicesQuery.isFetching}
          loadedScopeLabel={loadedScopeLabel}
          onChange={handleDraftChange}
          onLoad={() => setQuery(trimServiceQuery(draft))}
          onReset={handleReset}
          scopeLabel={currentScopeLabel}
        />

        <div
          style={{
            display: 'grid',
            gap: 12,
            gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
          }}
        >
          <MetricCard
            label={t("pages.deployments.index.visible.services.2", "Visible services")}
            tone="info"
            value={
              deploymentInventoryReady
                ? String(visibleServiceDigest.services)
                : '—'
            }
          />
          <MetricCard
            label={t("pages.deployments.index.serving.has.been.suspended.2", "serving has been suspended")}
            tone="success"
            value={
              deploymentInventoryReady
                ? String(visibleServiceDigest.servingServices)
                : '—'
            }
          />
          <MetricCard
            label={t("pages.deployments.index.waiting.serving.2", "Waiting serving")}
            tone="warning"
            value={
              deploymentInventoryReady
                ? String(visibleServiceDigest.waitingServices)
                : '—'
            }
          />
          <MetricCard
            label={t("pages.deployments.index.there.is.entrance.service.2", "There is entrance service")}
            value={
              deploymentInventoryReady
                ? String(visibleServiceDigest.endpointServices)
                : '—'
            }
          />
        </div>

        <div
          style={{
            ...buildAevatarPanelStyle(surfaceToken),
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
              gap: 16,
              justifyContent: 'space-between',
            }}
          >
            <Space orientation="vertical" size={4}>
              <span
                style={{
                  color: 'var(--ant-color-primary)',
                  fontSize: 12,
                  fontWeight: 700,
                  letterSpacing: '0.08em',
                  textTransform: 'uppercase',
                }}
              >
                {t("pages.deployments.index.publish.service.list.2", "Publish service list")}</span>
              <Typography.Text
                strong
                style={{ color: surfaceToken.colorTextHeading, fontSize: 22 }}
              >
                {t("pages.deployments.index.first.lock.the.publishing.2", "First lock the publishing object from the service list")}</Typography.Text>
              <Typography.Text
                style={{ color: surfaceToken.colorTextSecondary }}
              >
                {t("pages.deployments.index.scan.the.serving.deployment.2", "Scan the serving, deployment and entry scale, and then enter the release details of a service.")}</Typography.Text>
              <Space wrap size={[8, 8]}>
                <Tag color={isScopeDirty ? 'gold' : 'blue'}>
                  {isScopeDirty ? t("pages.deployments.index.show.last.loaded.range.2", "Show last loaded range") : t("pages.deployments.index.show.loaded.range.2", "Show loaded range")}
                </Tag>
                <Typography.Text
                  style={{ color: surfaceToken.colorTextSecondary }}
                >
                  {loadedScopeLabel}
                </Typography.Text>
              </Space>
            </Space>
          </div>

          {servicesQuery.isLoading ? (
            <InventoryReadinessState
              description={t("pages.deployments.index.the.publishing.object.list", "The publishing object list is still loading, and the current scope will not be misjudged as empty before returning.")}
              kind="loading"
              title={t("pages.deployments.index.loading.publishing.service", "Loading publishing service")}
            />
          ) : servicesQuery.error ? (
            <InventoryReadinessState
              action={{
                label: t("pages.deployments.index.retry.publishing.list", "Retry publishing list"),
                onClick: () => {
                  void servicesQuery.refetch();
                },
              }}
              description={
                servicesQuery.error instanceof Error
                  ? servicesQuery.error.message
                  : t("pages.deployments.index.failed.to.load.service.2", "Failed to load service publishing list, please try again.")
              }
              kind="error"
              title={t("pages.deployments.index.publishing.service.list.is", "Publishing service list is currently unavailable")}
            />
          ) : servicesQuery.data?.length ? (
            <div style={{ overflowX: 'auto' }}>
              <table
                style={{
                  background: surfaceToken.colorBgContainer,
                  borderCollapse: 'separate',
                  borderSpacing: 0,
                  width: '100%',
                }}
              >
                <thead>
                  <tr>
                    {[
                      t("pages.deployments.index.state.7", "state"),
                      t("pages.deployments.index.serve.2", "Serve"),
                      t("pages.deployments.index.scope.2", "scope"),
                      t("pages.deployments.index.current.serving.2", "Current serving"),
                      t("pages.deployments.index.current.deployment.3", "Current deployment"),
                      t("pages.deployments.index.entrance.6", "Entrance"),
                      t("pages.deployments.index.latest.updates.6", "Latest updates"),
                      t("pages.deployments.index.operate.8", "operate"),
                    ].map((label) => (
                      <th key={label} style={tableHeaderCellStyle}>
                        {label}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {(servicesQuery.data ?? []).map((service) => {
                    const selected = service.serviceId === selectedServiceId;
                    return (
                      <tr
                        key={service.serviceKey}
                        onClick={() => openServiceWorkbench(service)}
                        style={{
                          background: selected
                            ? surfaceToken.colorPrimaryBg
                            : surfaceToken.colorBgContainer,
                          cursor: 'pointer',
                        }}
                      >
                        <td style={tableCellStyle}>
                          <DeploymentStatusTag
                            status={service.deploymentStatus || 'pending'}
                          />
                        </td>
                        <td
                          style={{
                            ...tableCellStyle,
                            minWidth: 136,
                            width: 136,
                          }}
                        >
                          <div
                            style={{
                              display: 'flex',
                              flexDirection: 'column',
                              gap: 4,
                            }}
                          >
                            <CompactLabelText
                              maxWidth={120}
                              strong
                              value={getUserFacingIdentifierLabel(
                                service.displayName || service.serviceId,
                                t("pages.deployments.index.service", "Service"),
                              )}
                            />
                          </div>
                        </td>
                        <td style={tableCellStyle}>
                          <AevatarTooltip
                            title={`${service.tenantId}/${service.appId}/${service.namespace}`}
                          >
                            <Typography.Text
                              style={{
                                ...compactMonoValueStyle,
                                maxWidth: 220,
                              }}
                            >
                              {buildScopePreview(
                                service.tenantId,
                                service.appId,
                                service.namespace,
                              )}
                            </Typography.Text>
                          </AevatarTooltip>
                        </td>
                        <td style={tableCellStyle}>
                          {service.activeServingRevisionId ||
                          service.defaultServingRevisionId ? (
                            <Typography.Text strong>
                              {formatVersionVisibilityLabel(
                                service.activeServingRevisionId ||
                                  service.defaultServingRevisionId,
                              )}
                            </Typography.Text>
                          ) : (
                            <Typography.Text
                              style={{
                                color: surfaceToken.colorText,
                                fontWeight: 600,
                              }}
                            >
                              {t("pages.deployments.index.unpublished.2", "Unpublished")}</Typography.Text>
                          )}
                        </td>
                        <td style={tableCellStyle}>
                          {service.deploymentId ? (
                            <Tag color="blue" style={compactHintTagStyle}>
                              {formatDeploymentVisibilityLabel(service.deploymentId)}
                            </Tag>
                          ) : (
                            <Tag color="default" style={compactHintTagStyle}>
                              {t("pages.deployments.index.not.hung.serving.3", "Not hung serving")}</Tag>
                          )}
                        </td>
                        <td style={tableCellStyle}>
                          <Tag
                            color={
                              service.endpoints.length > 0 ? 'cyan' : 'default'
                            }
                            style={compactHintTagStyle}
                          >
                            {service.endpoints.length}
                          </Tag>
                        </td>
                        <td style={{ ...tableCellStyle, whiteSpace: 'nowrap' }}>
                          <Typography.Text
                            style={{ color: surfaceToken.colorTextSecondary }}
                          >
                            {formatDateTime(service.updatedAt)}
                          </Typography.Text>
                        </td>
                        <td style={tableCellStyle}>
                          <Button
                            size="small"
                            onClick={(event) => {
                              event.stopPropagation();
                              openServiceWorkbench(service);
                            }}
                          >
                            {t("pages.deployments.index.view.release.details.2", "View release details")}</Button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <InventoryReadinessState
              action={{ label: t("pages.deployments.index.adjust.release.scope", "Adjust release scope"), onClick: handleReset }}
              description={t("pages.deployments.index.there.are.currently.no.4", "There are currently no publishable services under team, App and Namespace. You can reload after adjusting the range.")}
              kind="empty"
              title={t("pages.deployments.index.there.are.no.services.2", "There are no services in the current scope")}
            />
          )}
        </div>
      </div>

      <AevatarContextDrawer
        extra={
          selectedService ? (
            <Space wrap size={[8, 8]}>
              <Button
                icon={<SendOutlined />}
                onClick={() => openDrawer('candidate')}
                type="primary"
              >
                {t("pages.deployments.index.deploy.release.candidate.3", "Deploy a release candidate")}</Button>
              <AevatarTooltip title={servingEntryAvailability.reason}>
                <span>
                  <Button
                    disabled={!servingEntryAvailability.enabled}
                    icon={<PercentageOutlined />}
                    onClick={() => openDrawer('weights')}
                  >
                    {servingEntryAvailability.enabled
                      ? t("pages.deployments.index.adjust.flow.6", "Adjust flow")
                      : t("pages.deployments.index.view.traffic.status", "View traffic status")}
                  </Button>
                </span>
              </AevatarTooltip>
              <AevatarTooltip title={rolloutControlEntryAvailability.reason}>
                <span>
                  <Button
                    disabled={!rolloutControlEntryAvailability.enabled}
                    icon={<RollbackOutlined />}
                    onClick={() => openDrawer('control')}
                  >
                    {rolloutControlEntryAvailability.enabled
                      ? t("pages.deployments.index.release.control.5", "Release control")
                      : t("pages.deployments.index.no.activity.control", "No activity control")}
                  </Button>
                </span>
              </AevatarTooltip>
            </Space>
          ) : null
        }
        onClose={closeServiceWorkbench}
        open={Boolean(selectedServiceId)}
        subtitle={drawerSubtitle}
        title={
          selectedService?.displayName ||
          selectedServiceId ||
          'Deployment Service'
        }
        width={1080}
      >
        {serviceDetailQuery.isLoading && !selectedService ? (
          <AevatarInspectorEmpty
            description={t("pages.deployments.index.loading.release.details.2", "Loading release details")}
            title={t("pages.deployments.index.loading.deployment.2", "Loading deployment")}
          />
        ) : !selectedService ? (
          <AevatarInspectorEmpty description={t("pages.deployments.index.choose.service.2", "Choose a service")} />
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            {releaseHandoff && releaseEvidence ? (
              <ReleaseHandoffPanel
                evidence={releaseEvidence}
                handoff={releaseHandoff}
                onClose={() => setReleaseHandoff(null)}
                onOpenEvidence={() => setView(releaseHandoff.evidenceView)}
              />
            ) : null}

            <WorkbenchSection title={t("pages.deployments.index.release.summary.2", "Release summary")}>
              <div
                style={{ display: 'flex', flexDirection: 'column', gap: 14 }}
              >
                <Space wrap size={[8, 8]}>
                  <DeploymentStatusTag
                    status={selectedService.deploymentStatus || 'pending'}
                  />
                  {focusDeployment?.deploymentId ? (
                    <Tag>{formatDeploymentVisibilityLabel(focusDeployment.deploymentId)}</Tag>
                  ) : null}
                  {rolloutQuery.data?.rolloutId ? (
                    <Tag color="blue">{t("pages.deployments.index.rollout.active", "Rollout active")}</Tag>
                  ) : null}
                  <Tag
                    color={
                      selectedService.endpoints.length > 0 ? 'cyan' : 'default'
                    }
                    style={compactHintTagStyle}
                  >
                    {selectedService.endpoints.length} {t("pages.deployments.index.entrance.7", "entrance")}</Tag>
                </Space>

                <div
                  style={{
                    display: 'grid',
                    gap: 10,
                    gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
                  }}
                >
                  <DetailFieldCard
                    label={t("pages.deployments.index.currently.serving.2", "currently serving")}
                    value={
                      activeRevisionId
                        ? formatVersionVisibilityLabel(activeRevisionId)
                        : t("pages.deployments.index.no.serving.version.yet.2", "No serving version yet")
                    }
                  />
                  <DetailFieldCard
                    label={t("pages.deployments.index.current.deployment.4", "current deployment")}
                    value={
                      focusDeployment?.deploymentId
                        ? formatDeploymentVisibilityLabel(focusDeployment.deploymentId)
                        : t("pages.deployments.index.not.hung.serving.4", "Not hung serving")
                    }
                  />
                  <DetailFieldCard
                    label={t("pages.deployments.index.main.actor.8", "Main actor")}
                    value={
                      selectedService.primaryActorId
                        ? formatActorVisibilityLabel(selectedService.primaryActorId)
                        : t("pages.deployments.index.not.declared.2", "Not declared")
                    }
                  />
                  <DetailFieldCard
                    label={t("pages.deployments.index.recently.synced.2", "Recently synced")}
                    value={
                      formatDateTime(
                        rolloutQuery.data?.updatedAt ||
                          trafficQuery.data?.updatedAt ||
                          deploymentsQuery.data?.updatedAt ||
                          selectedService.updatedAt,
                      ) || t("pages.deployments.index.to.be.synchronized.2", "To be synchronized")
                    }
                  />
                </div>

                <div
                  style={{
                    display: 'grid',
                    gap: 10,
                    gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
                  }}
                >
                  <MetricCard
                    label={t("pages.deployments.index.serving.goals.2", "serving goals")}
                    tone="info"
                    value={String(deploymentDigest.targets)}
                  />
                  <MetricCard
                    label={t("pages.deployments.index.inlet.traffic.3", "Inlet traffic")}
                    tone="success"
                    value={String(deploymentDigest.endpoints)}
                  />
                  <MetricCard
                    label={t("pages.deployments.index.deployment.number.2", "deployment number")}
                    value={String(deploymentDigest.deployments)}
                  />
                  <MetricCard
                    label={t("pages.deployments.index.current.stage.5", "Current Stage")}
                    tone="warning"
                    value={deploymentDigest.stage}
                  />
                </div>
              </div>
            </WorkbenchSection>

            <WorkbenchSection title={t("pages.deployments.index.publish.workspace.4", "Publish workspace")}>
              <Tabs
                activeKey={view}
                items={[
                  {
                    key: 'catalog',
                    label: t("pages.deployments.index.deployment.directory.2", "deployment directory"),
                    children: (
                      <WorkbenchSection title={t("pages.deployments.index.deployment.catalog.2", "Deployment Catalog")}>
                        <Table<ServiceDeploymentSnapshot>
                          columns={drawerDeploymentColumns}
                          dataSource={deploymentsQuery.data?.deployments ?? []}
                          locale={{ emptyText: t("pages.deployments.index.there.is.currently.no.4", "There is currently no deployment catalog") }}
                          onRow={(record) => ({
                            onClick: () =>
                              openInspector({
                                kind: 'deployment',
                                key: record.deploymentId,
                                open: true,
                              }),
                            style: { cursor: 'pointer' },
                          })}
                          pagination={false}
                          rowKey={(record) => record.deploymentId}
                          scroll={{ x: 860 }}
                          size="small"
                          tableLayout="fixed"
                        />
                      </WorkbenchSection>
                    ),
                  },
                  {
                    key: 'serving',
                    label: 'Serving',
                    children: (
                      <WorkbenchSection
                        title={t("pages.deployments.index.serving.targets.2", "Serving Targets")}
                        extra={
                          <Space wrap size={[8, 8]}>
                            <Tag>
                              {t("pages.deployments.index.generation.3", "Generation")}{servingQuery.data?.generation ?? 0}
                            </Tag>
                            {servingQuery.data?.activeRolloutId ? (
                              <Tag color="blue">
                                {t("pages.deployments.index.rollout.active", "Rollout active")}
                              </Tag>
                            ) : null}
                            <AevatarTooltip title={servingEntryAvailability.reason}>
                              <span>
                                <Button
                                  disabled={!servingEntryAvailability.enabled}
                                  icon={<PercentageOutlined />}
                                  onClick={() => openDrawer('weights')}
                                >
                                  {servingEntryAvailability.enabled
                                    ? t("pages.deployments.index.adjust.flow.7", "Adjust flow")
                                    : t("pages.deployments.index.view.traffic.status.2", "View traffic status")}
                                </Button>
                              </span>
                            </AevatarTooltip>
                          </Space>
                        }
                      >
                        <Table<ServiceServingTargetSnapshot>
                          columns={servingColumns}
                          dataSource={servingQuery.data?.targets ?? []}
                          locale={{ emptyText: t("pages.deployments.index.there.are.currently.no.5", "There are currently no serving targets") }}
                          onRow={(record) => ({
                            onClick: () =>
                              openInspector({
                                kind: 'serving',
                                key: buildServingTargetKey(record),
                                open: true,
                              }),
                            style: { cursor: 'pointer' },
                          })}
                          pagination={false}
                          rowKey={buildServingTargetKey}
                          size="middle"
                        />
                      </WorkbenchSection>
                    ),
                  },
                  {
                    key: 'traffic',
                    label: 'Traffic',
                    children: (
                      <WorkbenchSection
                        title={t("pages.deployments.index.inlet.traffic.4", "Inlet traffic")}
                        extra={
                          <Space wrap size={[8, 8]}>
                            {trafficQuery.data?.activeRolloutId ? (
                              <CompactIdentifierTag
                                color="blue"
                                value={trafficQuery.data.activeRolloutId}
                              />
                            ) : null}
                            <Tag>
                              {t("pages.deployments.index.generation.4", "Generation")}{trafficQuery.data?.generation ?? 0}
                            </Tag>
                            <AevatarTooltip title={servingEntryAvailability.reason}>
                              <span>
                                <Button
                                  disabled={!servingEntryAvailability.enabled}
                                  icon={<PercentageOutlined />}
                                  onClick={() => openDrawer('weights')}
                                >
                                  {servingEntryAvailability.enabled
                                    ? t("pages.deployments.index.adjust.flow.8", "Adjust flow")
                                    : t("pages.deployments.index.view.traffic.status.3", "View traffic status")}
                                </Button>
                              </span>
                            </AevatarTooltip>
                          </Space>
                        }
                      >
                        <Table<DeploymentTrafficRow>
                          columns={trafficColumns}
                          dataSource={trafficRows}
                          locale={{ emptyText: t("pages.deployments.index.there.is.currently.no.5", "There is currently no traffic view") }}
                          onRow={(record) => ({
                            onClick: () =>
                              openInspector({
                                kind: 'traffic',
                                key: record.key,
                                open: true,
                              }),
                            style: { cursor: 'pointer' },
                          })}
                          pagination={false}
                          rowKey="key"
                          size="middle"
                        />
                      </WorkbenchSection>
                    ),
                  },
                  {
                    key: 'rollout',
                    label: 'Rollout',
                    children: rolloutQuery.data ? (
                      <div style={cardStackStyle}>
                        <WorkbenchSection
                          title={t("pages.deployments.index.rollout.overview.2", "rollout Overview")}
                          extra={
                            <Space wrap size={[8, 8]}>
                              <DeploymentStatusTag
                                status={rolloutQuery.data.status}
                              />
                              <Button
                                icon={<RollbackOutlined />}
                                onClick={() => openDrawer('control')}
                              >
                                {t("pages.deployments.index.release.control.6", "Release control")}</Button>
                            </Space>
                          }
                        >
                          <div
                            style={{
                              display: 'grid',
                              gap: 12,
                              gridTemplateColumns:
                                'repeat(auto-fit, minmax(220px, 1fr))',
                            }}
                          >
                            <DetailFieldCard
                              label="Rollout"
                              value={
                                getUserFacingIdentifierLabel(
                                  rolloutQuery.data.displayName ||
                                    rolloutQuery.data.rolloutId,
                                  t("pages.deployments.index.rollout.active", "Rollout active"),
                                )
                              }
                            />
                            <DetailFieldCard
                              label={t("pages.deployments.index.current.stage.6", "Current Stage")}
                              value={
                                currentStage
                                  ? `${currentStage.stageIndex + 1} / ${rolloutQuery.data.stages.length}`
                                  : t("pages.deployments.index.none.yet.13", "None yet")
                              }
                            />
                            <DetailFieldCard
                              label={t("pages.deployments.index.start.time.2", "start time")}
                              value={formatDateTime(
                                rolloutQuery.data.startedAt,
                              )}
                            />
                            <DetailFieldCard
                              label={t("pages.deployments.index.latest.updates.7", "Latest updates")}
                              value={formatDateTime(
                                rolloutQuery.data.updatedAt,
                              )}
                            />
                          </div>
                        </WorkbenchSection>

                        <div
                          style={{
                            display: 'grid',
                            gap: 16,
                            gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
                          }}
                        >
                          <WorkbenchSection title={t("pages.deployments.index.stage.plan.2", "stage plan")}>
                            <Table<ServiceRolloutStageSnapshot>
                              columns={rolloutColumns}
                              dataSource={rolloutQuery.data.stages}
                              pagination={false}
                              rowKey={(record) => record.stageId}
                              size="middle"
                            />
                          </WorkbenchSection>
                          <WorkbenchSection title={t("pages.deployments.index.baseline.and.current.stage.2", "Baseline and current stage")}>
                            <div
                              style={{
                                display: 'grid',
                                gap: 12,
                                gridTemplateColumns:
                                  'repeat(2, minmax(0, 1fr))',
                              }}
                            >
                              <TargetGroupCard
                                label="Baseline"
                                targets={rolloutQuery.data.baselineTargets}
                              />
                              <TargetGroupCard
                                label={t("pages.deployments.index.current.stage.7", "Current Stage")}
                                targets={
                                  currentStage?.targets ??
                                  servingQuery.data?.targets ??
                                  []
                                }
                              />
                            </div>
                          </WorkbenchSection>
                        </div>
                      </div>
                    ) : (
                      <WorkbenchSection title="Rollout">
                        <Empty
                          description={t("pages.deployments.index.there.is.currently.no.6", "There is currently no active rollout")}
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      </WorkbenchSection>
                    ),
                  },
                ]}
                onChange={(key) => setView(key as DeploymentWorkbenchView)}
              />
            </WorkbenchSection>
          </div>
        )}
      </AevatarContextDrawer>

      <Drawer
        open={drawerState.open}
        size="large"
        title={t("pages.deployments.index.release.control.7", "Release control")}
        styles={{
          body: aevatarDrawerBodyStyle,
          wrapper: {
            maxWidth: '94vw',
            width: 1040,
          },
        }}
        onClose={() =>
          setDrawerState((current) => ({
            ...current,
            open: false,
          }))
        }
      >
        <div style={aevatarDrawerScrollStyle}>
          <div
            style={{
              background: surfaceToken.colorFillAlter,
              border: `1px solid ${surfaceToken.colorBorderSecondary}`,
              borderRadius: surfaceToken.borderRadiusLG,
              padding: 14,
            }}
          >
            <Space wrap size={[8, 8]}>
              <DeploymentStatusTag
                status={serviceDetailQuery.data?.deploymentStatus || 'pending'}
              />
              {rolloutQuery.data?.rolloutId ? (
                <Tag color="blue">{t("pages.deployments.index.rollout.active", "Rollout active")}</Tag>
              ) : null}
              {focusDeployment?.deploymentId ? (
                <Tag>{formatDeploymentVisibilityLabel(focusDeployment.deploymentId)}</Tag>
              ) : null}
              {focusDeployment?.revisionId ? (
                <Tag>{formatVersionVisibilityLabel(focusDeployment.revisionId)}</Tag>
              ) : null}
            </Space>
          </div>

          <Tabs
            activeKey={drawerState.tab}
            items={[
              {
                children: (
                  <div
                    style={{
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 16,
                    }}
                  >
                    <div
                      style={{
                        display: 'grid',
                        gap: 12,
                        gridTemplateColumns:
                          'minmax(260px, 320px) repeat(auto-fit, minmax(220px, 1fr))',
                      }}
                    >
                      <WorkbenchSection title={t("pages.deployments.index.release.candidate.5", "release candidate")}>
                        <Space
                          orientation="vertical"
                          size={12}
                          style={{ width: '100%' }}
                        >
                          <Select
                            options={(revisionsQuery.data?.revisions ?? []).map(
                              (revision) => ({
                                label: t("pages.deployments.index.copy.3", "{value1} · {value2}", {
                                  value1: formatVersionVisibilityLabel(revision.revisionId),
                                  value2: formatAevatarStatusLabel(revision.status),
                                }),
                                value: revision.revisionId,
                              }),
                            )}
                            placeholder={t("pages.deployments.index.select.release.candidate.2", "Select a release candidate")}
                            value={candidateRevisionId || undefined}
                            onChange={setCandidateRevisionId}
                          />
                          <Button
                            disabled={
                              !candidateRevisionId.trim() ||
                              candidateRevisionId === activeRevisionId
                            }
                            icon={<SendOutlined />}
                            loading={deployMutation.isPending}
                            onClick={() => deployMutation.mutate()}
                            type="primary"
                          >
                            {t("pages.deployments.index.release.candidate.6", "Release candidate")}</Button>
                        </Space>
                      </WorkbenchSection>
                      <RevisionSummaryCard
                        label={t("pages.deployments.index.current.serving.version.2", "Current serving version")}
                        revision={activeRevision}
                      />
                      <RevisionSummaryCard
                        label={t("pages.deployments.index.release.candidate.7", "release candidate")}
                        revision={candidateRevision}
                      />
                    </div>
                    <div
                      style={{
                        display: 'grid',
                        gap: 12,
                        gridTemplateColumns:
                          'repeat(auto-fit, minmax(220px, 1fr))',
                      }}
                    >
                      <TargetGroupCard
                        label="Baseline"
                        targets={rolloutQuery.data?.baselineTargets ?? []}
                      />
                      <TargetGroupCard
                        label={t("pages.deployments.index.current.stage.8", "Current Stage")}
                        targets={
                          currentStage?.targets ??
                          servingQuery.data?.targets ??
                          []
                        }
                      />
                    </div>
                  </div>
                ),
                key: 'candidate',
                label: t("pages.deployments.index.release.candidate.8", "release candidate"),
              },
              {
                children: (
                  <div
                    style={{
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 12,
                    }}
                  >
                    {editableTargets.length ? (
                      editableTargets.map((target, index) => (
                        <div
                          key={`${target.revisionId}-${target.servingState || 'unset'}`}
                          style={{
                            background: surfaceToken.colorFillAlter,
                            border: `1px solid ${surfaceToken.colorBorderSecondary}`,
                            borderRadius: surfaceToken.borderRadiusLG,
                            display: 'grid',
                            gap: 12,
                            gridTemplateColumns: 'minmax(0, 1fr) 140px 160px',
                            padding: 14,
                          }}
                        >
                          <div>
                            <Typography.Text strong>
                              {formatVersionVisibilityLabel(target.revisionId)}
                            </Typography.Text>
                            <Typography.Paragraph
                              style={{
                                color: surfaceToken.colorTextSecondary,
                                marginBottom: 0,
                                marginTop: 4,
                              }}
                            >
                              {target.enabledEndpointIds?.join(', ') ||
                                t("pages.deployments.index.all.entrances.7", "All entrances")}
                            </Typography.Paragraph>
                          </div>
                          <InputNumber
                            max={100}
                            min={0}
                            value={target.allocationWeight}
                            onChange={(value) =>
                              setEditableTargets((current) =>
                                current.map((item, itemIndex) =>
                                  itemIndex === index
                                    ? {
                                        ...item,
                                        allocationWeight: Number(value) || 0,
                                      }
                                    : item,
                                ),
                              )
                            }
                          />
                          <Select
                            options={servingStateOptions}
                            value={target.servingState || 'active'}
                            onChange={(value) =>
                              setEditableTargets((current) =>
                                current.map((item, itemIndex) =>
                                  itemIndex === index
                                    ? {
                                        ...item,
                                        servingState: value,
                                      }
                                    : item,
                                ),
                              )
                            }
                          />
                        </div>
                      ))
                    ) : (
                      <Empty
                        description={t("pages.deployments.index.there.are.currently.no.6", "There are currently no serving targets")}
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                    <Input.TextArea
                      placeholder={t("pages.deployments.index.explain.the.reason.for.3", "Explain the reason for this canary or weight adjustment")}
                      rows={3}
                      value={drawerReason}
                      onChange={(event) => setDrawerReason(event.target.value)}
                    />
                    <Alert
                      message={servingTargetPlanStatus.summary}
                      description={servingTargetPlanStatus.reason}
                      showIcon
                      type={
                        servingTargetPlanStatus.enabled ? 'info' : 'warning'
                      }
                    />
                    <AevatarTooltip title={servingTargetPlanStatus.reason}>
                      <span
                        style={{ display: 'inline-flex', width: 'fit-content' }}
                      >
                        <Button
                          disabled={!servingTargetPlanStatus.enabled}
                          icon={<PercentageOutlined />}
                          loading={weightsMutation.isPending}
                          onClick={() => weightsMutation.mutate()}
                          type="primary"
                        >
                          {t("pages.deployments.index.apply.weights.2", "Apply weights")}</Button>
                      </span>
                    </AevatarTooltip>
                  </div>
                ),
                key: 'weights',
                label: t("pages.deployments.index.traffic.weight.2", "Traffic weight"),
              },
              {
                children: (
                  <div
                    style={{
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 12,
                    }}
                  >
                    <MetricCard
                      label={t("pages.deployments.index.current.rollout.2", "current rollout")}
                      tone="warning"
                      value={
                        rolloutQuery.data?.rolloutId
                          ? t("pages.deployments.index.rollout.active", "Rollout active")
                          : t("pages.deployments.index.no.activity.yet.rollout.2", "No activity yet rollout")
                      }
                    />
                    <Input.TextArea
                      placeholder={t("pages.deployments.index.explain.the.reason.for.4", "Explain the reason for this pause, resume or rollback")}
                      rows={3}
                      value={drawerReason}
                      onChange={(event) => setDrawerReason(event.target.value)}
                    />
                    <Alert
                      message={
                        rolloutQuery.data?.rolloutId
                          ? t("pages.deployments.index.current.rollout.status", "Current rollout status: {value1}", { value1: formatAevatarStatusLabel(rolloutQuery.data.status || 'unknown') })
                          : t("pages.deployments.index.there.is.currently.no.7", "There is currently no active rollout")
                      }
                      description={
                        rolloutQuery.data?.rolloutId
                          ? t("pages.deployments.index.only.control.actions.that", "Only control actions that match the current life cycle will remain executable; you still need to wait for the evidence to be refreshed after submission.")
                          : t("pages.deployments.index.releasing.control.action.requires", "Releasing a control action requires an active rollout.")
                      }
                      showIcon
                      type={rolloutQuery.data?.rolloutId ? 'info' : 'warning'}
                    />
                    <Space wrap size={[8, 8]}>
                      {rolloutControlDefinitions.map((definition) => {
                        const availability =
                          rolloutActionAvailability[definition.action];

                        return (
                          <AevatarTooltip
                            key={definition.action}
                            title={availability.reason}
                          >
                            <span>
                              <Button
                                danger={definition.danger}
                                disabled={!availability.enabled}
                                icon={definition.icon}
                                loading={rolloutMutation.isPending}
                                onClick={() =>
                                  rolloutMutation.mutate(definition.action)
                                }
                                type={
                                  definition.primary ? 'primary' : 'default'
                                }
                              >
                                {formatConsoleMessage(definition.label)}
                              </Button>
                            </span>
                          </AevatarTooltip>
                        );
                      })}
                    </Space>
                  </div>
                ),
                key: 'control',
                label: t("pages.deployments.index.release.control.8", "Release control"),
              },
            ]}
            onChange={(key) =>
              setDrawerState({
                open: true,
                tab: key as DeploymentDrawerTab,
              })
            }
          />
        </div>
      </Drawer>

      <Drawer
        open={inspectorState.open}
        size="default"
        title={
          inspectorState.open
            ? inspectorState.kind === 'serving'
              ? t("pages.deployments.index.serving.target.details.2", "serving Target details")
              : inspectorState.kind === 'traffic'
                ? t("pages.deployments.index.traffic.endpoint.details.2", "Traffic Endpoint Details")
                : t("pages.deployments.index.deployment.details.2", "deployment details")
            : t("pages.deployments.index.details.2", "Details")
        }
        styles={{
          body: aevatarDrawerBodyStyle,
          wrapper: {
            maxWidth: '92vw',
            width: 640,
          },
        }}
        onClose={() => setInspectorState({ open: false })}
      >
        <div style={aevatarDrawerScrollStyle}>
          {inspectorState.open && inspectorState.kind === 'serving' ? (
            selectedServingTarget ? (
              <div style={cardStackStyle}>
                <DrawerSection title={t("pages.deployments.index.target.summary.2", "Target Summary")}>
                  <div
                    style={{
                      display: 'grid',
                      gap: 12,
                      gridTemplateColumns:
                        'repeat(auto-fit, minmax(180px, 1fr))',
                    }}
                  >
                    <DetailFieldCard
                      label="Revision"
                      value={formatVersionVisibilityLabel(selectedServingTarget.revisionId)}
                    />
                    <DetailFieldCard
                      label="Deployment"
                      value={
                        selectedServingTarget.deploymentId
                          ? formatDeploymentVisibilityLabel(selectedServingTarget.deploymentId)
                          : t("pages.deployments.index.not.bound.2", "Not bound")
                      }
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.main.actor.9", "Main actor")}
                      value={formatActorVisibilityLabel(selectedServingTarget.primaryActorId)}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.serving.status.6", "serving status")}
                      value={formatAevatarStatusLabel(
                        selectedServingTarget.servingState || 'unknown',
                      )}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.weight.4", "weight")}
                      value={`${selectedServingTarget.allocationWeight}%`}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.entrance.8", "Entrance")}
                      value={
                        selectedServingTarget.enabledEndpointIds.join(', ') ||
                        t("pages.deployments.index.all.entrances.8", "All entrances")
                      }
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title={t("pages.deployments.index.next.steps.3", "Next steps")}>
                  <Space wrap size={[8, 8]}>
                    <Button
                      icon={<PercentageOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer('weights');
                      }}
                    >
                      {t("pages.deployments.index.adjust.flow.9", "Adjust flow")}</Button>
                    <Button
                      icon={<SendOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer('candidate');
                      }}
                    >
                      {t("pages.deployments.index.deploy.release.candidate.4", "Deploy a release candidate")}</Button>
                  </Space>
                </DrawerSection>
              </div>
            ) : (
              <Empty
                description={t("pages.deployments.index.serving.target.not.found.2", "serving target not found")}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )
          ) : null}

          {inspectorState.open && inspectorState.kind === 'traffic' ? (
            selectedTrafficRow ? (
              <div style={cardStackStyle}>
                <DrawerSection title={t("pages.deployments.index.endpoint.summary.2", "Endpoint Summary")}>
                  <div
                    style={{
                      display: 'grid',
                      gap: 12,
                      gridTemplateColumns:
                        'repeat(auto-fit, minmax(180px, 1fr))',
                    }}
                  >
                    <DetailFieldCard
                      label="Endpoint"
                      value={t("pages.deployments.index.endpoint.ready", "Endpoint ready")}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.number.of.targets.4", "number of targets")}
                      value={String(selectedTrafficRow.targetCount)}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.assignment.summary.2", "Assignment summary")}
                      value={selectedTrafficRow.splitSummary}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.activity.rollout.2", "Activity rollout")}
                      value={
                        trafficQuery.data?.activeRolloutId
                          ? t("pages.deployments.index.rollout.active", "Rollout active")
                          : t("pages.deployments.index.none.yet.15", "None yet")
                      }
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title={t("pages.deployments.index.traffic.target.2", "traffic target")}>
                  <div
                    style={{
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 12,
                    }}
                  >
                    {selectedTrafficRow.targets.map((target, index) => (
                      <DetailFieldCard
                        key={`${target.deploymentId}-${target.revisionId}`}
                        label={formatTrafficTargetLabel(index)}
                        value={formatTrafficTargetSummary(target)}
                      />
                    ))}
                  </div>
                </DrawerSection>
              </div>
            ) : (
              <Empty
                description={t("pages.deployments.index.traffic.endpoint.not.found.2", "traffic endpoint not found")}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )
          ) : null}

          {inspectorState.open && inspectorState.kind === 'deployment' ? (
            inspectedDeployment ? (
              <div style={cardStackStyle}>
                <DrawerSection title={t("pages.deployments.index.deployment.summary.2", "deployment Summary")}>
                  <div
                    style={{
                      display: 'grid',
                      gap: 12,
                      gridTemplateColumns:
                        'repeat(auto-fit, minmax(180px, 1fr))',
                    }}
                  >
                    <DetailFieldCard
                      label="Deployment"
                      value={formatDeploymentVisibilityLabel(inspectedDeployment.deploymentId)}
                    />
                    <DetailFieldCard
                      label="Revision"
                      value={formatVersionVisibilityLabel(inspectedDeployment.revisionId)}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.state.8", "state")}
                      value={formatAevatarStatusLabel(
                        inspectedDeployment.status,
                      )}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.main.actor.10", "Main actor")}
                      value={formatActorVisibilityLabel(inspectedDeployment.primaryActorId)}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.activation.time.4", "activation time")}
                      value={formatDateTime(inspectedDeployment.activatedAt)}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.latest.updates.8", "Latest updates")}
                      value={formatDateTime(inspectedDeployment.updatedAt)}
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title={t("pages.deployments.index.next.steps.4", "Next steps")}>
                  <Space wrap size={[8, 8]}>
                    <Button
                      icon={<PercentageOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer('weights');
                      }}
                    >
                      {t("pages.deployments.index.adjust.flow.10", "Adjust flow")}</Button>
                    <AevatarTooltip title={deploymentDeactivateAvailability.reason}>
                      <span>
                        <Button
                          danger
                          disabled={!deploymentDeactivateAvailability.enabled}
                          icon={<StopOutlined />}
                          loading={
                            deactivateMutation.isPending &&
                            deactivateMutation.variables ===
                              inspectedDeployment.deploymentId
                          }
                          onClick={() =>
                            deactivateMutation.mutate(
                              inspectedDeployment.deploymentId,
                            )
                          }
                        >
                          {deploymentDeactivateAvailability.enabled
                            ? t("pages.deployments.index.deactivate.deployment.2", "Deactivate deployment")
                            : t("pages.deployments.index.cannot.be.deactivated", "Cannot be deactivated")}
                        </Button>
                      </span>
                    </AevatarTooltip>
                  </Space>
                </DrawerSection>
              </div>
            ) : (
              <Empty
                description={t("pages.deployments.index.deployment.not.found.2", "deployment not found")}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )
          ) : null}
        </div>
      </Drawer>
    </ConsoleMenuPageShell>
  );
};

export default DeploymentsPage;
