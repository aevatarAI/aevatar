import {
  PauseCircleOutlined,
  PercentageOutlined,
  ReloadOutlined,
  RollbackOutlined,
  SendOutlined,
  StopOutlined,
} from "@ant-design/icons";
import {
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
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
  Tooltip,
  Typography,
  theme,
} from "antd";
import type { ColumnsType } from "antd/es/table";
import React, { useCallback, useEffect, useMemo, useState } from "react";
import {
  readServiceQueryDraft,
  trimServiceQuery,
  type ServiceQueryDraft,
} from "@/pages/services/components/serviceQuery";
import { servicesApi } from "@/shared/api/servicesApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import { buildPlatformDeploymentsHref } from "@/shared/navigation/platformRoutes";
import {
  invalidateServiceResourceQueries,
  serviceResourceQueryKeys,
} from "@/shared/query/serviceResourceQueryKeys";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
import type {
  ServiceCatalogSnapshot,
  ServiceDeploymentSnapshot,
  ServiceIdentityQuery,
  ServiceRevisionSnapshot,
  ServiceRolloutStageSnapshot,
  ServiceServingTargetInput,
  ServiceServingTargetSnapshot,
  ServiceTrafficEndpointSnapshot,
} from "@/shared/models/services";
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
} from "@/shared/ui/aevatarPageShells";
import {
  AevatarCompactTag,
  AevatarCompactText,
  aevatarMonoFontFamily,
  truncateMiddle,
} from "@/shared/ui/compactText";
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
} from "@/shared/ui/aevatarWorkbench";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import {
  cardStackStyle,
  summaryFieldLabelStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";
import { t } from "@/shared/i18n/messages";

type DeploymentWorkbenchView =
  | "catalog"
  | "serving"
  | "rollout"
  | "traffic";

type DeploymentDrawerTab = "candidate" | "weights" | "control";

type DeploymentDrawerState = {
  open: boolean;
  tab: DeploymentDrawerTab;
};

type DeploymentInspectorState =
  | {
      open: false;
    }
  | {
      kind: "serving";
      key: string;
      open: true;
    }
  | {
      kind: "traffic";
      key: string;
      open: true;
    }
  | {
      kind: "deployment";
      key: string;
      open: true;
    };

type DeploymentNotice = {
  message: string;
  tone: "error" | "info" | "success" | "warning";
};

type DeploymentTrafficRow = {
  endpointId: string;
  key: string;
  splitSummary: string;
  targetCount: number;
  targets: ReadonlyArray<ServiceTrafficEndpointSnapshot["targets"][number]>;
};

const defaultScopeServiceAppId = "default";
const defaultScopeServiceNamespace = "default";
const tableHeaderCellStyle: React.CSSProperties = {
  background: "var(--ant-color-fill-alter)",
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  color: "var(--ant-color-text-secondary)",
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: 0.24,
  padding: "12px 14px",
  textAlign: "left",
  textTransform: "uppercase",
  whiteSpace: "nowrap",
};
const tableCellStyle: React.CSSProperties = {
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  padding: "12px 14px",
  verticalAlign: "top",
};
const compactHintTagStyle: React.CSSProperties = {
  borderRadius: 999,
  fontWeight: 600,
  marginInlineEnd: 0,
};
const compactMonoValueStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontFamily: aevatarMonoFontFamily,
  fontSize: 10.5,
  overflow: "hidden",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
};

function buildScopePreview(
  tenantId: string,
  appId: string,
  namespace: string,
): string {
  return `${truncateMiddle(tenantId)}/${appId}/${namespace}`;
}

function formatDeploymentScopeLabel(query: ServiceIdentityQuery): string {
  const segments = [
    query.tenantId?.trim() || t("pages.deployments.index.no.team.set.up", "No team set up"),
    query.appId?.trim() || t("pages.deployments.index.app.not.set.up", "App not set up"),
    query.namespace?.trim() || t("pages.deployments.index.namespace.not.set", "namespace not set"),
  ];
  const resultWindow = query.take && query.take > 0 ? query.take : 200;

  return t("pages.deployments.index.items", "{value1} · {value2} items", { value1: segments.join(" / "), value2: resultWindow });
}

function isSameDeploymentScope(
  left: ServiceIdentityQuery,
  right: ServiceIdentityQuery,
): boolean {
  return (
    (left.tenantId?.trim() ?? "") === (right.tenantId?.trim() ?? "") &&
    (left.appId?.trim() ?? "") === (right.appId?.trim() ?? "") &&
    (left.namespace?.trim() ?? "") === (right.namespace?.trim() ?? "") &&
    (left.take ?? 200) === (right.take ?? 200)
  );
}

const CompactIdentifierText: React.FC<{
  color?: string;
  maxWidth?: React.CSSProperties["maxWidth"];
  singleLine?: boolean;
  strong?: boolean;
  value: string;
}> = ({
  color,
  maxWidth = "100%",
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
  maxWidth?: React.CSSProperties["maxWidth"];
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
  if (typeof window === "undefined") {
    return "";
  }

  return (
    new URLSearchParams(window.location.search).get("serviceId")?.trim() ?? ""
  );
}

function readSelectedDeploymentId(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return (
    new URLSearchParams(window.location.search).get("deploymentId")?.trim() ?? ""
  );
}

function buildRevisionSummary(
  revision: ServiceRevisionSnapshot | null | undefined,
): Array<{ label: string; value: string }> {
  if (!revision) {
    return [
      {
        label: t("pages.deployments.index.version", "Version"),
        value: t("pages.deployments.index.none.yet", "None yet"),
      },
    ];
  }

  return [
    {
      label: t("pages.deployments.index.version.2", "Version"),
      value: revision.revisionId,
    },
    {
      label: t("pages.deployments.index.state", "state"),
      value: formatAevatarStatusLabel(revision.status || "unknown"),
    },
    {
      label: t("pages.deployments.index.number.of.entrances", "Number of entrances"),
      value: String(revision.endpoints.length),
    },
    {
      label: t("pages.deployments.index.products", "Products"),
      value: revision.artifactHash || "n/a",
    },
    {
      label: t("pages.deployments.index.ready.to.complete", "Ready to complete"),
      value: formatDateTime(revision.preparedAt),
    },
    {
      label: t("pages.deployments.index.published", "Published"),
      value: formatDateTime(revision.publishedAt),
    },
  ];
}

function pickPreferredCandidateRevision(
  revisions: readonly ServiceRevisionSnapshot[],
  activeRevisionId: string,
): string {
  if (!revisions.length) {
    return "";
  }

  return (
    revisions.find((revision) => revision.revisionId !== activeRevisionId)
      ?.revisionId ??
    revisions[0]?.revisionId ??
    ""
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
        .map((target) => `${target.revisionId} ${target.allocationWeight}%`)
        .join(" · ") || t("pages.deployments.index.no.traffic.target.yet", "No traffic target yet"),
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
    | ReadonlyArray<ServiceTrafficEndpointSnapshot["targets"][number]>,
): string {
  if (!targets.length) {
    return t("pages.deployments.index.none.yet.2", "None yet");
  }

  return targets
    .map(
      (target) =>
        `${target.revisionId} · ${target.allocationWeight}% · ${formatAevatarStatusLabel(
          target.servingState || "unknown",
        )}`,
    )
    .join(" / ");
}

const DeploymentStatusTag: React.FC<{
  domain?: AevatarStatusDomain;
  status: string;
}> = ({ domain = "governance", status }) => {
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
  tone?: "default" | "info" | "success" | "warning";
  value: string;
}> = ({ label, tone = "default", value }) => {
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
        display: "flex",
        flexDirection: "column",
        gap: 16,
        padding: 18,
      }}
    >
      <div
        style={{
          alignItems: "flex-start",
          display: "flex",
          gap: 12,
          justifyContent: "space-between",
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
    typeof value === "string" || typeof value === "number" ? String(value) : null;

  return (
    <div
      style={{
        background: "rgba(248, 250, 252, 0.92)",
        border: `1px solid ${surfaceToken.colorBorderSecondary}`,
        borderRadius: 14,
        display: "flex",
        flexDirection: "column",
        gap: 8,
        minWidth: 0,
        padding: "14px 16px",
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
          overflowWrap: "anywhere",
        }}
      >
        {primitiveValue ? (
          <Typography.Text
            strong
            style={{
              color: "inherit",
              fontSize: "inherit",
              lineHeight: "inherit",
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
        "linear-gradient(180deg, rgba(255,255,255,0.98) 0%, rgba(248,250,252,0.92) 100%)",
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 14,
      boxShadow: "0 12px 28px rgba(15, 23, 42, 0.04)",
      display: "flex",
      flexDirection: "column",
      gap: 12,
      padding: 16,
    }}
  >
    <div
      style={{
        alignItems: "center",
        display: "flex",
        flexWrap: "wrap",
        gap: 12,
        justifyContent: "space-between",
      }}
    >
      <Space
        data-testid="deployments-scope-card-heading"
        orientation="vertical"
        size={2}
        style={{ flex: "1 1 220px", minWidth: 0 }}
      >
        <span
          style={{
            color: "var(--ant-color-primary)",
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: "0.08em",
            textTransform: "uppercase",
          }}
        >
          {t("pages.deployments.index.deployment.scope", "deployment scope")}</span>
        <span
          style={{
            color: "var(--ant-color-text)",
            fontSize: 16,
            fontWeight: 700,
            lineHeight: 1.2,
            overflowWrap: "anywhere",
          }}
        >
          {t("pages.deployments.index.team.application.namespace", "team/Application/Namespace")}</span>
      </Space>
      <Tooltip title={scopeLabel}>
        <div
          style={{
            alignItems: "center",
            background: "rgba(24, 144, 255, 0.06)",
            border: "1px solid rgba(24, 144, 255, 0.12)",
            borderRadius: 999,
            color: "var(--ant-color-primary)",
            display: "inline-flex",
            flex: "1 1 240px",
            fontSize: 12,
            fontWeight: 600,
            minHeight: 30,
            minWidth: 0,
            maxWidth: "100%",
            overflowWrap: "anywhere",
            padding: "0 12px",
            overflow: "hidden",
            textOverflow: "ellipsis",
          }}
        >
          {scopeLabel}
        </div>
      </Tooltip>
    </div>

    <div
      style={{
        display: "grid",
        gap: 12,
        gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
      }}
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <span style={{ color: "var(--ant-color-text-secondary)", fontSize: 12, fontWeight: 600 }}>
          {t("pages.deployments.index.team", "team")}</span>
        <Input
          placeholder={t("pages.deployments.index.team.id", "team ID")}
          value={draft.tenantId}
          onChange={(event) =>
            onChange({
              ...draft,
              tenantId: event.target.value,
            })
          }
        />
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <span style={{ color: "var(--ant-color-text-secondary)", fontSize: 12, fontWeight: 600 }}>
          {t("pages.deployments.index.application", "application")}</span>
        <Input
          placeholder={t("pages.deployments.index.application.id", "Application ID")}
          value={draft.appId}
          onChange={(event) =>
            onChange({
              ...draft,
              appId: event.target.value,
            })
          }
        />
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <span style={{ color: "var(--ant-color-text-secondary)", fontSize: 12, fontWeight: 600 }}>
          {t("pages.deployments.index.namespace", "namespace")}</span>
        <Input
          placeholder={t("pages.deployments.index.namespace.2", "namespace")}
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
        description={t("pages.deployments.index.the.current.service.metrics", "The current service metrics and lists are still from the loaded scope: {value1}. The draft range is: {value2}.", { value1: loadedScopeLabel, value2: draftScopeLabel })}
        message={t("pages.deployments.index.range.edited.but.not", "Range edited but not loaded yet")}
        showIcon
        type="warning"
      />
    ) : (
      <Alert
        description={t("pages.deployments.index.the.service.metrics.and", "The service metrics and list below are based on this loaded range: {value1}.", { value1: loadedScopeLabel })}
        message={t("pages.deployments.index.loaded.range.is.locked", "Loaded range is locked")}
        showIcon
        type="info"
      />
    )}

    <div
      style={{
        alignItems: "center",
        display: "flex",
        flexWrap: "wrap",
        gap: 10,
        justifyContent: "space-between",
      }}
    >
      <div
        style={{
          alignItems: "center",
          display: "flex",
          gap: 8,
        }}
      >
        <span
          style={{
            color: "var(--ant-color-text-secondary)",
            fontSize: 11,
            fontWeight: 600,
            textTransform: "uppercase",
            letterSpacing: "0.04em",
          }}
        >
          {t("pages.deployments.index.return.count", "Result count")}
        </span>
        <InputNumber
          aria-label={t("pages.deployments.index.return.count", "Result count")}
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
        <Button
          aria-label={t("pages.deployments.index.reset", "Reset")}
          size="small"
          onClick={onReset}
        >
          {t("pages.deployments.index.reset", "Reset")}
        </Button>
        <Button
          loading={isLoading}
          size="small"
          type="primary"
          onClick={onLoad}
        >
          {isDirty ? t("pages.deployments.index.load.range.changes", "Load range changes") : t("pages.deployments.index.load.release.list", "Load release list")}
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
        background: "rgba(248, 250, 252, 0.92)",
        border: `1px solid ${surfaceToken.colorBorderSecondary}`,
        borderRadius: 14,
        display: "flex",
        flexDirection: "column",
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
            <CompactIdentifierTag value={revision.revisionId} />
          </Space>
          <div
            style={{
              display: "grid",
              gap: 10,
              gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
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
          {t("pages.deployments.index.no.version.information.yet", "No version information yet")}</Typography.Text>
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
        background: "rgba(248, 250, 252, 0.92)",
        border: `1px solid ${surfaceToken.colorBorderSecondary}`,
        borderRadius: 14,
        display: "flex",
        flexDirection: "column",
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
              display: "flex",
              flexDirection: "column",
              gap: 8,
              paddingTop: 12,
            }}
          >
            <Space wrap size={[8, 8]}>
              <CompactIdentifierTag value={target.revisionId} />
              <CompactIdentifierTag value={target.deploymentId} />
              <DeploymentStatusTag status={target.servingState || "unknown"} />
              <Tag>{target.allocationWeight}%</Tag>
            </Space>
            <div style={{ color: surfaceToken.colorTextSecondary }}>
              {target.primaryActorId ? (
                <CompactIdentifierText
                  color="var(--ant-color-text-secondary)"
                  value={target.primaryActorId}
                />
              ) : (
                t("pages.deployments.index.no.actor.yet", "No actor yet")
              )}{" "}
              · {target.enabledEndpointIds.join(", ") || t("pages.deployments.index.all.entrances", "All entrances")}
            </div>
          </div>
        ))
      ) : (
        <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
          {t("pages.deployments.index.no.target.yet", "No target yet")}</Typography.Text>
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
        display: "flex",
        flexDirection: "column",
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
  const [view, setView] = useState<DeploymentWorkbenchView>("catalog");
  const [drawerState, setDrawerState] = useState<DeploymentDrawerState>({
    open: false,
    tab: "candidate",
  });
  const [inspectorState, setInspectorState] =
    useState<DeploymentInspectorState>({
      open: false,
    });
  const [drawerReason, setDrawerReason] = useState("");
  const [editableTargets, setEditableTargets] = useState<
    ServiceServingTargetInput[]
  >([]);
  const [candidateRevisionId, setCandidateRevisionId] = useState("");
  const [notice, setNotice] = useState<DeploymentNotice | null>(null);

  const authSessionQuery = useQuery({
    queryKey: ["deployments", "auth-session"],
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
      servicesQuery.data?.find((service) => service.serviceId === selectedServiceId) ??
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
        setSelectedServiceId("");
      }
      if (selectedDeploymentId) {
        setSelectedDeploymentId("");
      }
      return;
    }

    if (!selectedServiceId.trim()) {
      return;
    }

    if (services.some((service) => service.serviceId === selectedServiceId)) {
      return;
    }

    setSelectedServiceId("");
    if (selectedDeploymentId) {
      setSelectedDeploymentId("");
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
        setSelectedDeploymentId("");
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

    setSelectedDeploymentId("");
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
    "";

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
    const currentDeploymentId = serviceDetailQuery.data?.deploymentId?.trim() ?? "";

    return (
      deployments.find(
        (deployment) => deployment.deploymentId === currentDeploymentId,
      ) ??
      deployments.find((deployment) =>
        deployment.status.toLowerCase().includes("active"),
      ) ??
      null
    );
  }, [deploymentsQuery.data?.deployments, serviceDetailQuery.data?.deploymentId]);

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
    if (!inspectorState.open || inspectorState.kind !== "serving") {
      return null;
    }

    return (
      servingQuery.data?.targets.find(
        (target) => buildServingTargetKey(target) === inspectorState.key,
      ) ?? null
    );
  }, [inspectorState, servingQuery.data?.targets]);

  const selectedTrafficRow = useMemo(() => {
    if (!inspectorState.open || inspectorState.kind !== "traffic") {
      return null;
    }

    return trafficRows.find((row) => row.key === inspectorState.key) ?? null;
  }, [inspectorState, trafficRows]);

  const inspectedDeployment = useMemo(() => {
    if (!inspectorState.open || inspectorState.kind !== "deployment") {
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
      ? t("pages.deployments.index.current.scope", "Current scope {value1}", { value1: segments.join(" / ") })
      : t("pages.deployments.index.the.service.scope.has", "The service scope has not been locked yet");
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
          : t("pages.deployments.index.no.activity.rollout", "No activity rollout"),
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
      if (state.kind === "deployment") {
        setSelectedDeploymentId(state.key);
      }
      setInspectorState(state);
    },
    [],
  );

  const deployMutation = useMutation({
    mutationFn: () => {
      if (!candidateRevisionId.trim()) {
        throw new Error(t("pages.deployments.index.please.select.release.candidate", "Please select a release candidate first."));
      }

      return servicesApi.deployRevision(selectedServiceId, {
        ...query,
        revisionId: candidateRevisionId,
      });
    },
    onError: (error: Error) => {
      setNotice({
        message: error.message || t("pages.deployments.index.release.candidate.failed", "Release candidate failed."),
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: t("pages.deployments.index.the.release.candidate.has", "The release candidate has been submitted to the release control plane."),
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const weightsMutation = useMutation({
    mutationFn: () =>
      servicesApi.replaceServingTargets(selectedServiceId, {
        ...query,
        reason: drawerReason,
        rolloutId: rolloutQuery.data?.rolloutId,
        targets: editableTargets,
      }),
    onError: (error: Error) => {
      setNotice({
        message: error.message || t("pages.deployments.index.failed.to.apply.serving", "Failed to apply serving targets."),
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: t("pages.deployments.index.new.serving.targets.submitted", "New serving targets submitted."),
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const rolloutMutation = useMutation({
    mutationFn: async (kind: "advance" | "pause" | "resume" | "rollback") => {
      const rolloutId = rolloutQuery.data?.rolloutId;
      if (!rolloutId) {
        throw new Error(t("pages.deployments.index.there.is.no.active", "There is no active rollout for the current service."));
      }

      if (kind === "advance") {
        return servicesApi.advanceRollout(selectedServiceId, rolloutId, query);
      }

      if (kind === "pause") {
        return servicesApi.pauseRollout(selectedServiceId, rolloutId, {
          ...query,
          reason: drawerReason,
        });
      }

      if (kind === "resume") {
        return servicesApi.resumeRollout(selectedServiceId, rolloutId, query);
      }

      return servicesApi.rollbackRollout(selectedServiceId, rolloutId, {
        ...query,
        reason: drawerReason,
      });
    },
    onError: (error: Error) => {
      setNotice({
        message: error.message || t("pages.deployments.index.release.control.action.submission", "Release control action submission failed."),
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: t("pages.deployments.index.release.control.action.submitted", "Release control action submitted."),
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const deactivateMutation = useMutation({
    mutationFn: (deploymentId: string) => {
      if (!deploymentId.trim()) {
        throw new Error(t("pages.deployments.index.please.select.deployment", "Please select a deployment."));
      }

      return servicesApi.deactivateDeployment(
        selectedServiceId,
        deploymentId,
        query,
      );
    },
    onError: (error: Error) => {
      setNotice({
        message: error.message || t("pages.deployments.index.deactivating.the.deployment.failed", "Deactivating the deployment failed."),
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: t("pages.deployments.index.the.request.to.deactivate", "The request to deactivate the deployment has been submitted."),
        tone: "warning",
      });
      await invalidateDetailQueries();
    },
  });

  const servingColumns = useMemo<
    ColumnsType<ServiceServingTargetSnapshot>
  >(
    () => [
      {
        dataIndex: "revisionId",
        key: "revisionId",
        title: "Revision",
        render: (value: string, record) => (
          <Space orientation="vertical" size={4}>
            <CompactIdentifierText maxWidth={220} singleLine strong value={value} />
            {record.deploymentId ? (
              <CompactIdentifierText
                color="var(--ant-color-text-secondary)"
                maxWidth={220}
                singleLine
                value={record.deploymentId}
              />
            ) : (
              <Typography.Text type="secondary">{t("pages.deployments.index.unbound.deployment", "Unbound deployment")}</Typography.Text>
            )}
          </Space>
        ),
      },
      {
        dataIndex: "primaryActorId",
        key: "primaryActorId",
        title: t("pages.deployments.index.main.actor", "Main actor"),
        render: (value: string) =>
          value ? <CompactIdentifierText maxWidth={160} singleLine value={value} /> : t("pages.deployments.index.none.yet.3", "None yet"),
      },
      {
        dataIndex: "allocationWeight",
        key: "allocationWeight",
        title: t("pages.deployments.index.weight", "weight"),
        render: (value: number) => `${value}%`,
      },
      {
        dataIndex: "servingState",
        key: "servingState",
        title: t("pages.deployments.index.serving.status", "serving status"),
        render: (value: string) => <DeploymentStatusTag status={value || "unknown"} />,
      },
      {
        dataIndex: "enabledEndpointIds",
        key: "enabledEndpointIds",
        title: t("pages.deployments.index.entrance", "Entrance"),
        render: (value: readonly string[]) =>
          value.length > 0 ? value.join(", ") : t("pages.deployments.index.all.entrances.2", "All entrances"),
      },
      {
        key: "actions",
        title: t("pages.deployments.index.operate", "operate"),
        render: (_, record) => (
          <Button
            size="small"
            onClick={() =>
              openInspector({
                kind: "serving",
                key: buildServingTargetKey(record),
                open: true,
              })
            }
          >
            {t("pages.deployments.index.check.the.details", "check the details")}</Button>
        ),
      },
    ],
    [openInspector],
  );

  const rolloutColumns = useMemo<
    ColumnsType<ServiceRolloutStageSnapshot>
  >(
    () => [
      {
        dataIndex: "stageIndex",
        key: "stageIndex",
        title: "Stage",
        render: (value: number) => `Stage ${value + 1}`,
      },
      {
        dataIndex: "stageId",
        key: "stageId",
        title: t("pages.deployments.index.logo", "logo"),
      },
      {
        dataIndex: "targets",
        key: "targets",
        title: t("pages.deployments.index.target.allocation", "target allocation"),
        render: (targets: readonly ServiceServingTargetSnapshot[]) =>
          describeTargets(targets),
      },
    ],
    [describeTargets],
  );

  const trafficColumns = useMemo<ColumnsType<DeploymentTrafficRow>>(
    () => [
      {
        dataIndex: "endpointId",
        key: "endpointId",
        title: "Endpoint",
        render: (value: string) => (
          <CompactIdentifierText maxWidth={180} singleLine value={value} />
        ),
      },
      {
        dataIndex: "targetCount",
        key: "targetCount",
        title: t("pages.deployments.index.number.of.targets", "number of targets"),
      },
      {
        dataIndex: "splitSummary",
        key: "splitSummary",
        title: t("pages.deployments.index.traffic.distribution", "traffic distribution"),
      },
      {
        dataIndex: "targets",
        key: "states",
        title: t("pages.deployments.index.serving.status.2", "serving status"),
        render: (targets: DeploymentTrafficRow["targets"]) => (
          <Space wrap size={[8, 8]}>
            {targets.map((target) => (
              <Tag key={`${target.deploymentId}-${target.revisionId}`}>
                {formatAevatarStatusLabel(target.servingState || "unknown")}
              </Tag>
            ))}
          </Space>
        ),
      },
      {
        key: "actions",
        title: t("pages.deployments.index.operate.2", "operate"),
        render: (_, record) => (
          <Button
            size="small"
            onClick={() =>
              openInspector({
                kind: "traffic",
                key: record.key,
                open: true,
              })
            }
          >
            {t("pages.deployments.index.check.the.details.2", "check the details")}</Button>
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
        dataIndex: "deploymentId",
        key: "deploymentId",
        title: "Deployment",
        width: 220,
        render: (value: string, record) => (
          <Space orientation="vertical" size={2}>
            <CompactIdentifierText maxWidth={180} singleLine strong value={value} />
            <CompactIdentifierText
              color="var(--ant-color-text-secondary)"
              maxWidth={180}
              singleLine
              value={record.revisionId}
            />
          </Space>
        ),
      },
      {
        dataIndex: "primaryActorId",
        key: "primaryActorId",
        title: t("pages.deployments.index.main.actor.2", "Main actor"),
        width: 150,
        render: (value: string) =>
          value ? (
            <CompactIdentifierText maxWidth={116} singleLine value={value} />
          ) : (
            t("pages.deployments.index.none.yet.4", "None yet")
          ),
      },
      {
        dataIndex: "status",
        key: "status",
        title: t("pages.deployments.index.state.2", "state"),
        width: 104,
        render: (value: string) => <DeploymentStatusTag status={value || "unknown"} />,
      },
      {
        dataIndex: "activatedAt",
        key: "activatedAt",
        title: t("pages.deployments.index.activation.time", "activation time"),
        width: 148,
        render: (value: string | null) => (
          <Typography.Text
            style={{ color: surfaceToken.colorTextSecondary, whiteSpace: "nowrap" }}
          >
            {formatDateTime(value)}
          </Typography.Text>
        ),
      },
      {
        dataIndex: "updatedAt",
        key: "updatedAt",
        title: t("pages.deployments.index.latest.updates", "Latest updates"),
        width: 148,
        render: (value: string) => (
          <Typography.Text
            style={{ color: surfaceToken.colorTextSecondary, whiteSpace: "nowrap" }}
          >
            {formatDateTime(value)}
          </Typography.Text>
        ),
      },
      {
        key: "actions",
        title: t("pages.deployments.index.operate.3", "operate"),
        width: 104,
        render: (_, record) => (
          <Button
            size="small"
            onClick={() =>
              openInspector({
                kind: "deployment",
                key: record.deploymentId,
                open: true,
              })
            }
          >
            {t("pages.deployments.index.check.the.details.3", "check the details")}</Button>
        ),
      },
    ],
    [openInspector, surfaceToken.colorTextSecondary],
  );

  const handleDraftChange = useCallback((nextDraft: ServiceQueryDraft) => {
    setDraft(nextDraft);
    setSelectedServiceId("");
    setSelectedDeploymentId("");
  }, []);

  const openServiceWorkbench = useCallback(
    (service: Pick<ServiceCatalogSnapshot, "deploymentId" | "serviceId">) => {
      setSelectedServiceId(service.serviceId);
      setSelectedDeploymentId(service.deploymentId || "");
      setInspectorState({ open: false });
      setView("catalog");
    },
    [],
  );

  const closeServiceWorkbench = useCallback(() => {
    setSelectedServiceId("");
    setSelectedDeploymentId("");
    setInspectorState({ open: false });
    setDrawerState((current) => ({
      ...current,
      open: false,
    }));
  }, []);

  const handleReset = useCallback(() => {
    const nextDraft = isScopeDirty
      ? {
          appId: query.appId?.trim() ?? "",
          namespace: query.namespace?.trim() ?? "",
          take: query.take && query.take > 0 ? query.take : 200,
          tenantId: query.tenantId?.trim() ?? "",
        }
      : resolvedScope?.scopeId?.trim()
        ? {
            ...readServiceQueryDraft(""),
            appId: defaultScopeServiceAppId,
            namespace: defaultScopeServiceNamespace,
            tenantId: resolvedScope.scopeId.trim(),
          }
        : readServiceQueryDraft("");
    setDraft(nextDraft);
    if (!isScopeDirty) {
      setQuery(trimServiceQuery(nextDraft));
    }
    setSelectedServiceId("");
    setSelectedDeploymentId("");
    setCandidateRevisionId("");
    setDrawerReason("");
    setView("catalog");
  }, [isScopeDirty, query, resolvedScope?.scopeId]);

  const drawerSubtitle = selectedService
    ? `${selectedService.tenantId}/${selectedService.appId}/${selectedService.namespace}`
    : t("pages.deployments.index.publish.workspace", "Publish workspace");

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Platform"
      description={t("pages.deployments.index.deployments.is.platform.release", "Deployments is Platform's release workbench, focusing on current serving, rollout progress and traffic distribution.")}
      title="Deployments"
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        {notice ? (
          <Alert
            closable
            message={notice.message}
            showIcon
            type={notice.tone}
            onClose={() => setNotice(null)}
          />
        ) : null}

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
            display: "grid",
            gap: 12,
            gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
          }}
        >
          <MetricCard
            label={t("pages.deployments.index.visible.services", "Visible services")}
            tone="info"
            value={String(visibleServiceDigest.services)}
          />
          <MetricCard
            label={t("pages.deployments.index.serving.has.been.suspended", "serving has been suspended")}
            tone="success"
            value={String(visibleServiceDigest.servingServices)}
          />
          <MetricCard
            label={t("pages.deployments.index.waiting.serving", "Waiting serving")}
            tone="warning"
            value={String(visibleServiceDigest.waitingServices)}
          />
          <MetricCard
            label={t("pages.deployments.index.there.is.entrance.service", "There is entrance service")}
            value={String(visibleServiceDigest.endpointServices)}
          />
        </div>

        <div
          style={{
            ...buildAevatarPanelStyle(surfaceToken),
            display: "flex",
            flexDirection: "column",
            gap: 16,
            padding: 18,
          }}
        >
          <div
            style={{
              alignItems: "flex-start",
              display: "flex",
              gap: 16,
              justifyContent: "space-between",
            }}
          >
            <Space orientation="vertical" size={4}>
              <span
                style={{
                  color: "var(--ant-color-primary)",
                  fontSize: 12,
                  fontWeight: 700,
                  letterSpacing: "0.08em",
                  textTransform: "uppercase",
                }}
              >
                {t("pages.deployments.index.publish.service.list", "Publish service list")}</span>
              <Typography.Text
                strong
                style={{ color: surfaceToken.colorTextHeading, fontSize: 22 }}
              >
                {t("pages.deployments.index.first.lock.the.publishing", "First lock the publishing object from the service list")}</Typography.Text>
              <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
                {t("pages.deployments.index.scan.the.serving.deployment", "Scan the serving, deployment and entry scale, and then enter the release details of a service.")}</Typography.Text>
              <Space wrap size={[8, 8]}>
                <Tag color={isScopeDirty ? "gold" : "blue"}>
                  {isScopeDirty ? t("pages.deployments.index.show.last.loaded.range", "Show last loaded range") : t("pages.deployments.index.show.loaded.range", "Show loaded range")}
                </Tag>
                <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
                  {loadedScopeLabel}
                </Typography.Text>
              </Space>
            </Space>
          </div>

          {servicesQuery.error ? (
            <Alert
              message={
                servicesQuery.error instanceof Error
                  ? servicesQuery.error.message
                  : t("pages.deployments.index.failed.to.load.service", "Failed to load service publishing list.")
              }
              showIcon
              type="error"
            />
          ) : null}

          {servicesQuery.data?.length ? (
            <div style={{ overflowX: "auto" }}>
              <table
                style={{
                  background: surfaceToken.colorBgContainer,
                  borderCollapse: "separate",
                  borderSpacing: 0,
                  width: "100%",
                }}
              >
                <thead>
                  <tr>
                    {[t("pages.deployments.index.state.3", "state"), t("pages.deployments.index.serve", "Serve"), t("pages.deployments.index.scope", "scope"), t("pages.deployments.index.current.serving", "Current serving"), t("pages.deployments.index.current.deployment", "Current deployment"), t("pages.deployments.index.entrance.2", "Entrance"), t("pages.deployments.index.latest.updates.2", "Latest updates"), t("pages.deployments.index.operate.4", "operate")].map(
                      (label) => (
                        <th key={label} style={tableHeaderCellStyle}>
                          {label}
                        </th>
                      ),
                    )}
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
                          cursor: "pointer",
                        }}
                      >
                        <td style={tableCellStyle}>
                          <DeploymentStatusTag
                            status={service.deploymentStatus || "pending"}
                          />
                        </td>
                        <td
                          style={{
                            ...tableCellStyle,
                            minWidth: 136,
                            width: 136,
                          }}
                        >
                          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                            <CompactLabelText
                              maxWidth={120}
                              strong
                              value={service.displayName || service.serviceId}
                            />
                            {service.displayName &&
                            service.displayName !== service.serviceId ? (
                              <AevatarCompactText
                                maxWidth={120}
                                monospace
                                style={compactMonoValueStyle}
                                value={service.serviceId}
                              />
                            ) : null}
                          </div>
                        </td>
                        <td style={tableCellStyle}>
                          <Tooltip
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
                          </Tooltip>
                        </td>
                        <td style={tableCellStyle}>
                          {service.activeServingRevisionId ||
                          service.defaultServingRevisionId ? (
                            <CompactIdentifierText
                              maxWidth={168}
                              singleLine
                              strong
                              value={
                                service.activeServingRevisionId ||
                                service.defaultServingRevisionId
                              }
                            />
                          ) : (
                            <Typography.Text
                              style={{ color: surfaceToken.colorText, fontWeight: 600 }}
                            >
                              {t("pages.deployments.index.unpublished", "Unpublished")}</Typography.Text>
                          )}
                        </td>
                        <td style={tableCellStyle}>
                          {service.deploymentId ? (
                            <CompactIdentifierTag
                              color="blue"
                              style={compactHintTagStyle}
                              value={service.deploymentId}
                            />
                          ) : (
                            <Tag color="default" style={compactHintTagStyle}>
                              {t("pages.deployments.index.not.hung.serving", "Not hung serving")}</Tag>
                          )}
                        </td>
                        <td style={tableCellStyle}>
                          <Tag
                            color={service.endpoints.length > 0 ? "cyan" : "default"}
                            style={compactHintTagStyle}
                          >
                            {service.endpoints.length}
                          </Tag>
                        </td>
                        <td style={{ ...tableCellStyle, whiteSpace: "nowrap" }}>
                          <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
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
                            {t("pages.deployments.index.view.release.details", "View release details")}</Button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <Empty
              description={t("pages.deployments.index.there.are.no.services", "There are no services in the current scope")}
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              style={{ padding: 24 }}
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
                onClick={() => openDrawer("candidate")}
                type="primary"
              >
                {t("pages.deployments.index.deploy.release.candidate", "Deploy a release candidate")}</Button>
              <Button
                icon={<PercentageOutlined />}
                onClick={() => openDrawer("weights")}
              >
                {t("pages.deployments.index.adjust.flow", "Adjust flow")}</Button>
              <Button
                icon={<RollbackOutlined />}
                onClick={() => openDrawer("control")}
              >
                {t("pages.deployments.index.release.control", "Release control")}</Button>
            </Space>
          ) : null
        }
        onClose={closeServiceWorkbench}
        open={Boolean(selectedServiceId)}
        subtitle={drawerSubtitle}
        title={selectedService?.displayName || selectedServiceId || "Deployment Service"}
        width={1080}
      >
        {serviceDetailQuery.isLoading && !selectedService ? (
          <AevatarInspectorEmpty description={t("pages.deployments.index.loading.release.details", "Loading release details")} title={t("pages.deployments.index.loading.deployment", "Loading deployment")} />
        ) : !selectedService ? (
          <AevatarInspectorEmpty description={t("pages.deployments.index.choose.service", "Choose a service")} />
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <WorkbenchSection title={t("pages.deployments.index.release.summary", "Release summary")}>
              <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
                <Space wrap size={[8, 8]}>
                  <DeploymentStatusTag
                    status={selectedService.deploymentStatus || "pending"}
                  />
                  {focusDeployment?.deploymentId ? (
                    <CompactIdentifierTag value={focusDeployment.deploymentId} />
                  ) : null}
                  {rolloutQuery.data?.rolloutId ? (
                    <CompactIdentifierTag
                      color="blue"
                      value={rolloutQuery.data.rolloutId}
                    />
                  ) : null}
                  <Tag
                    color={selectedService.endpoints.length > 0 ? "cyan" : "default"}
                    style={compactHintTagStyle}
                  >
                    {selectedService.endpoints.length} {t("pages.deployments.index.entrance.3", "entrance")}</Tag>
                </Space>

                <div
                  style={{
                    display: "grid",
                    gap: 10,
                    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
                  }}
                >
                  <DetailFieldCard
                    label={t("pages.deployments.index.currently.serving", "currently serving")}
                    value={
                      activeRevisionId ? (
                        <CompactIdentifierText maxWidth="100%" singleLine value={activeRevisionId} />
                      ) : (
                        t("pages.deployments.index.no.serving.version.yet", "No serving version yet")
                      )
                    }
                  />
                  <DetailFieldCard
                    label={t("pages.deployments.index.current.deployment.2", "current deployment")}
                    value={
                      focusDeployment?.deploymentId ? (
                        <CompactIdentifierText
                          maxWidth="100%"
                          singleLine
                          value={focusDeployment.deploymentId}
                        />
                      ) : (
                        t("pages.deployments.index.not.hung.serving.2", "Not hung serving")
                      )
                    }
                  />
                  <DetailFieldCard
                    label={t("pages.deployments.index.main.actor.3", "Main actor")}
                    value={
                      selectedService.primaryActorId ? (
                        <CompactIdentifierText
                          maxWidth="100%"
                          singleLine
                          value={selectedService.primaryActorId}
                        />
                      ) : (
                        t("pages.deployments.index.not.declared", "Not declared")
                      )
                    }
                  />
                  <DetailFieldCard
                    label={t("pages.deployments.index.recently.synced", "Recently synced")}
                    value={
                      formatDateTime(
                        rolloutQuery.data?.updatedAt ||
                          trafficQuery.data?.updatedAt ||
                          deploymentsQuery.data?.updatedAt ||
                          selectedService.updatedAt,
                      ) || t("pages.deployments.index.to.be.synchronized", "To be synchronized")
                    }
                  />
                </div>

                <div
                  style={{
                    display: "grid",
                    gap: 10,
                    gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))",
                  }}
                >
                  <MetricCard
                    label={t("pages.deployments.index.serving.goals", "serving goals")}
                    tone="info"
                    value={String(deploymentDigest.targets)}
                  />
                  <MetricCard
                    label={t("pages.deployments.index.inlet.traffic", "Inlet traffic")}
                    tone="success"
                    value={String(deploymentDigest.endpoints)}
                  />
                  <MetricCard
                    label={t("pages.deployments.index.deployment.number", "deployment number")}
                    value={String(deploymentDigest.deployments)}
                  />
                  <MetricCard
                    label={t("pages.deployments.index.current.stage", "Current Stage")}
                    tone="warning"
                    value={deploymentDigest.stage}
                  />
                </div>
              </div>
            </WorkbenchSection>

            <WorkbenchSection title={t("pages.deployments.index.publish.workspace.2", "Publish workspace")}>
              <Tabs
                activeKey={view}
                items={[
                  {
                    key: "catalog",
                    label: t("pages.deployments.index.deployment.directory", "deployment directory"),
                    children: (
                      <WorkbenchSection title={t("pages.deployments.index.deployment.catalog", "Deployment Catalog")}>
                        <Table<ServiceDeploymentSnapshot>
                          columns={drawerDeploymentColumns}
                          dataSource={deploymentsQuery.data?.deployments ?? []}
                          locale={{ emptyText: t("pages.deployments.index.there.is.currently.no", "There is currently no deployment catalog") }}
                          onRow={(record) => ({
                            onClick: () =>
                              openInspector({
                                kind: "deployment",
                                key: record.deploymentId,
                                open: true,
                              }),
                            style: { cursor: "pointer" },
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
                    key: "serving",
                    label: "Serving",
                    children: (
                      <WorkbenchSection
                        title={t("pages.deployments.index.serving.targets", "Serving Targets")}
                        extra={
                          <Space wrap size={[8, 8]}>
                            <Tag>{t("pages.deployments.index.generation", "Generation")}{servingQuery.data?.generation ?? 0}</Tag>
                            {servingQuery.data?.activeRolloutId ? (
                              <Tag color="blue">
                                {servingQuery.data.activeRolloutId}
                              </Tag>
                            ) : null}
                            <Button
                              icon={<PercentageOutlined />}
                              onClick={() => openDrawer("weights")}
                            >
                              {t("pages.deployments.index.adjust.flow.2", "Adjust flow")}</Button>
                          </Space>
                        }
                      >
                        <Table<ServiceServingTargetSnapshot>
                          columns={servingColumns}
                          dataSource={servingQuery.data?.targets ?? []}
                          locale={{ emptyText: t("pages.deployments.index.there.are.currently.no", "There are currently no serving targets") }}
                          onRow={(record) => ({
                            onClick: () =>
                              openInspector({
                                kind: "serving",
                                key: buildServingTargetKey(record),
                                open: true,
                              }),
                            style: { cursor: "pointer" },
                          })}
                          pagination={false}
                          rowKey={buildServingTargetKey}
                          size="middle"
                        />
                      </WorkbenchSection>
                    ),
                  },
                  {
                    key: "traffic",
                    label: "Traffic",
                    children: (
                      <WorkbenchSection
                        title={t("pages.deployments.index.inlet.traffic.2", "Inlet traffic")}
                        extra={
                          <Space wrap size={[8, 8]}>
                            {trafficQuery.data?.activeRolloutId ? (
                              <CompactIdentifierTag
                                color="blue"
                                value={trafficQuery.data.activeRolloutId}
                              />
                            ) : null}
                            <Tag>{t("pages.deployments.index.generation.2", "Generation")}{trafficQuery.data?.generation ?? 0}</Tag>
                            <Button
                              icon={<PercentageOutlined />}
                              onClick={() => openDrawer("weights")}
                            >
                              {t("pages.deployments.index.adjust.flow.3", "Adjust flow")}</Button>
                          </Space>
                        }
                      >
                        <Table<DeploymentTrafficRow>
                          columns={trafficColumns}
                          dataSource={trafficRows}
                          locale={{ emptyText: t("pages.deployments.index.there.is.currently.no.2", "There is currently no traffic view") }}
                          onRow={(record) => ({
                            onClick: () =>
                              openInspector({
                                kind: "traffic",
                                key: record.key,
                                open: true,
                              }),
                            style: { cursor: "pointer" },
                          })}
                          pagination={false}
                          rowKey="key"
                          size="middle"
                        />
                      </WorkbenchSection>
                    ),
                  },
                  {
                    key: "rollout",
                    label: "Rollout",
                    children: rolloutQuery.data ? (
                      <div style={cardStackStyle}>
                        <WorkbenchSection
                          title={t("pages.deployments.index.rollout.overview", "rollout Overview")}
                          extra={
                            <Space wrap size={[8, 8]}>
                              <DeploymentStatusTag status={rolloutQuery.data.status} />
                              <CompactIdentifierTag value={rolloutQuery.data.rolloutId} />
                              <Button
                                icon={<RollbackOutlined />}
                                onClick={() => openDrawer("control")}
                              >
                                {t("pages.deployments.index.release.control.2", "Release control")}</Button>
                            </Space>
                          }
                        >
                          <div
                            style={{
                              display: "grid",
                              gap: 12,
                              gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
                            }}
                          >
                            <DetailFieldCard
                              label="Rollout"
                              value={rolloutQuery.data.displayName || rolloutQuery.data.rolloutId}
                            />
                            <DetailFieldCard
                              label={t("pages.deployments.index.current.stage.2", "Current Stage")}
                              value={
                                currentStage
                                  ? `${currentStage.stageIndex + 1} / ${rolloutQuery.data.stages.length}`
                                  : t("pages.deployments.index.none.yet.5", "None yet")
                              }
                            />
                            <DetailFieldCard
                              label={t("pages.deployments.index.start.time", "start time")}
                              value={formatDateTime(rolloutQuery.data.startedAt)}
                            />
                            <DetailFieldCard
                              label={t("pages.deployments.index.latest.updates.3", "Latest updates")}
                              value={formatDateTime(rolloutQuery.data.updatedAt)}
                            />
                          </div>
                        </WorkbenchSection>

                        <div
                          style={{
                            display: "grid",
                            gap: 16,
                            gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                          }}
                        >
                          <WorkbenchSection title={t("pages.deployments.index.stage.plan", "stage plan")}>
                            <Table<ServiceRolloutStageSnapshot>
                              columns={rolloutColumns}
                              dataSource={rolloutQuery.data.stages}
                              pagination={false}
                              rowKey={(record) => record.stageId}
                              size="middle"
                            />
                          </WorkbenchSection>
                          <WorkbenchSection title={t("pages.deployments.index.baseline.and.current.stage", "Baseline and current stage")}>
                            <div
                              style={{
                                display: "grid",
                                gap: 12,
                                gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                              }}
                            >
                              <TargetGroupCard
                                label="Baseline"
                                targets={rolloutQuery.data.baselineTargets}
                              />
                              <TargetGroupCard
                                label={t("pages.deployments.index.current.stage.3", "Current Stage")}
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
                          description={t("pages.deployments.index.there.is.currently.no.3", "There is currently no active rollout")}
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
        title={t("pages.deployments.index.release.control.3", "Release control")}
        styles={{
          body: aevatarDrawerBodyStyle,
          wrapper: {
            maxWidth: "94vw",
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
                status={serviceDetailQuery.data?.deploymentStatus || "pending"}
              />
              {rolloutQuery.data?.rolloutId ? (
                <CompactIdentifierTag
                  color="blue"
                  value={rolloutQuery.data.rolloutId}
                />
              ) : null}
              {focusDeployment?.deploymentId ? (
                <CompactIdentifierTag value={focusDeployment.deploymentId} />
              ) : null}
              {focusDeployment?.revisionId ? (
                <CompactIdentifierTag value={focusDeployment.revisionId} />
              ) : null}
            </Space>
          </div>

          <Tabs
            activeKey={drawerState.tab}
            items={[
              {
                children: (
                  <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns:
                          "minmax(260px, 320px) repeat(auto-fit, minmax(220px, 1fr))",
                      }}
                    >
                      <WorkbenchSection title={t("pages.deployments.index.release.candidate", "release candidate")}>
                        <Space orientation="vertical" size={12} style={{ width: "100%" }}>
                          <Select
                            options={(revisionsQuery.data?.revisions ?? []).map(
                              (revision) => ({
                                label: `${revision.revisionId} · ${formatAevatarStatusLabel(
                                  revision.status,
                                )}`,
                                value: revision.revisionId,
                              }),
                            )}
                            placeholder={t("pages.deployments.index.select.release.candidate", "Select a release candidate")}
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
                            {t("pages.deployments.index.release.candidate.2", "Release candidate")}</Button>
                        </Space>
                      </WorkbenchSection>
                      <RevisionSummaryCard
                        label={t("pages.deployments.index.current.serving.version", "Current serving version")}
                        revision={activeRevision}
                      />
                      <RevisionSummaryCard
                        label={t("pages.deployments.index.release.candidate.3", "release candidate")}
                        revision={candidateRevision}
                      />
                    </div>
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
                      }}
                    >
                      <TargetGroupCard
                        label="Baseline"
                        targets={rolloutQuery.data?.baselineTargets ?? []}
                      />
                      <TargetGroupCard
                        label={t("pages.deployments.index.current.stage.4", "Current Stage")}
                        targets={
                          currentStage?.targets ?? servingQuery.data?.targets ?? []
                        }
                      />
                    </div>
                  </div>
                ),
                key: "candidate",
                label: t("pages.deployments.index.release.candidate.4", "release candidate"),
              },
              {
                children: (
                  <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                    {editableTargets.length ? (
                      editableTargets.map((target, index) => (
                        <div
                          key={`${target.revisionId}-${target.servingState || "unset"}`}
                          style={{
                            background: surfaceToken.colorFillAlter,
                            border: `1px solid ${surfaceToken.colorBorderSecondary}`,
                            borderRadius: surfaceToken.borderRadiusLG,
                            display: "grid",
                            gap: 12,
                            gridTemplateColumns: "minmax(0, 1fr) 140px 160px",
                            padding: 14,
                          }}
                        >
                          <div>
                            <CompactIdentifierText
                              maxWidth={240}
                              singleLine
                              strong
                              value={target.revisionId}
                            />
                            <Typography.Paragraph
                              style={{
                                color: surfaceToken.colorTextSecondary,
                                marginBottom: 0,
                                marginTop: 4,
                              }}
                            >
                              {target.enabledEndpointIds?.join(", ") || t("pages.deployments.index.all.entrances.3", "All entrances")}
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
                          <Input
                            value={target.servingState}
                            onChange={(event) =>
                              setEditableTargets((current) =>
                                current.map((item, itemIndex) =>
                                  itemIndex === index
                                    ? {
                                        ...item,
                                        servingState: event.target.value,
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
                        description={t("pages.deployments.index.there.are.currently.no.2", "There are currently no serving targets")}
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                    <Input.TextArea
                      placeholder={t("pages.deployments.index.explain.the.reason.for", "Explain the reason for this canary or weight adjustment")}
                      rows={3}
                      value={drawerReason}
                      onChange={(event) => setDrawerReason(event.target.value)}
                    />
                    <Button
                      icon={<PercentageOutlined />}
                      loading={weightsMutation.isPending}
                      onClick={() => weightsMutation.mutate()}
                      type="primary"
                    >
                      {t("pages.deployments.index.apply.weights", "Apply weights")}</Button>
                  </div>
                ),
                key: "weights",
                label: t("pages.deployments.index.traffic.weight", "Traffic weight"),
              },
              {
                children: (
                  <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                    <MetricCard
                      label={t("pages.deployments.index.current.rollout", "current rollout")}
                      tone="warning"
                      value={rolloutQuery.data?.rolloutId || t("pages.deployments.index.no.activity.yet.rollout", "No activity yet rollout")}
                    />
                    <Input.TextArea
                      placeholder={t("pages.deployments.index.explain.the.reason.for.2", "Explain the reason for this pause, resume or rollback")}
                      rows={3}
                      value={drawerReason}
                      onChange={(event) => setDrawerReason(event.target.value)}
                    />
                    <Space wrap size={[8, 8]}>
                      <Button
                        icon={<SendOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("advance")}
                        type="primary"
                      >
                        {t("pages.deployments.index.advance.rollout", "advance rollout")}</Button>
                      <Button
                        icon={<PauseCircleOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("pause")}
                      >
                        {t("pages.deployments.index.pause", "pause")}</Button>
                      <Button
                        icon={<ReloadOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("resume")}
                      >
                        {t("pages.deployments.index.recover", "recover")}</Button>
                      <Button
                        danger
                        icon={<RollbackOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("rollback")}
                      >
                        {t("pages.deployments.index.rollback.rollout", "rollback rollout")}</Button>
                    </Space>
                  </div>
                ),
                key: "control",
                label: t("pages.deployments.index.release.control.4", "Release control"),
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
            ? inspectorState.kind === "serving"
              ? t("pages.deployments.index.serving.target.details", "serving Target details")
              : inspectorState.kind === "traffic"
                ? t("pages.deployments.index.traffic.endpoint.details", "Traffic Endpoint Details")
                : t("pages.deployments.index.deployment.details", "deployment details")
            : t("pages.deployments.index.details", "Details")
        }
        styles={{
          body: aevatarDrawerBodyStyle,
          wrapper: {
            maxWidth: "92vw",
            width: 640,
          },
        }}
        onClose={() => setInspectorState({ open: false })}
      >
        <div style={aevatarDrawerScrollStyle}>
          {inspectorState.open && inspectorState.kind === "serving" ? (
            selectedServingTarget ? (
              <div style={cardStackStyle}>
                <DrawerSection title={t("pages.deployments.index.target.summary", "Target Summary")}>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <DetailFieldCard
                      label="Revision"
                      value={
                        <CompactIdentifierText
                          maxWidth="100%"
                          singleLine
                          value={selectedServingTarget.revisionId}
                        />
                      }
                    />
                    <DetailFieldCard
                      label="Deployment"
                      value={
                        selectedServingTarget.deploymentId ? (
                          <CompactIdentifierText
                            maxWidth="100%"
                            singleLine
                            value={selectedServingTarget.deploymentId}
                          />
                        ) : (
                          t("pages.deployments.index.not.bound", "Not bound")
                        )
                      }
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.main.actor.4", "Main actor")}
                      value={
                        selectedServingTarget.primaryActorId ? (
                          <CompactIdentifierText
                            maxWidth="100%"
                            singleLine
                            value={selectedServingTarget.primaryActorId}
                          />
                        ) : (
                          t("pages.deployments.index.none.yet.6", "None yet")
                        )
                      }
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.serving.status.3", "serving status")}
                      value={formatAevatarStatusLabel(
                        selectedServingTarget.servingState || "unknown",
                      )}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.weight.2", "weight")}
                      value={`${selectedServingTarget.allocationWeight}%`}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.entrance.4", "Entrance")}
                      value={
                        selectedServingTarget.enabledEndpointIds.join(", ") ||
                        t("pages.deployments.index.all.entrances.4", "All entrances")
                      }
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title={t("pages.deployments.index.next.steps", "Next steps")}>
                  <Space wrap size={[8, 8]}>
                    <Button
                      icon={<PercentageOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer("weights");
                      }}
                    >
                      {t("pages.deployments.index.adjust.flow.4", "Adjust flow")}</Button>
                    <Button
                      icon={<SendOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer("candidate");
                      }}
                    >
                      {t("pages.deployments.index.deploy.release.candidate.2", "Deploy a release candidate")}</Button>
                  </Space>
                </DrawerSection>
              </div>
            ) : (
              <Empty description={t("pages.deployments.index.serving.target.not.found", "serving target not found")} image={Empty.PRESENTED_IMAGE_SIMPLE} />
            )
          ) : null}

          {inspectorState.open && inspectorState.kind === "traffic" ? (
            selectedTrafficRow ? (
              <div style={cardStackStyle}>
                <DrawerSection title={t("pages.deployments.index.endpoint.summary", "Endpoint summary")}>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <DetailFieldCard
                      label="Endpoint"
                      value={
                        <CompactIdentifierText
                          maxWidth="100%"
                          singleLine
                          value={selectedTrafficRow.endpointId}
                        />
                      }
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.number.of.targets.2", "number of targets")}
                      value={String(selectedTrafficRow.targetCount)}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.assignment.summary", "Assignment summary")}
                      value={selectedTrafficRow.splitSummary}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.activity.rollout", "Activity rollout")}
                      value={trafficQuery.data?.activeRolloutId || t("pages.deployments.index.none.yet.7", "None yet")}
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title={t("pages.deployments.index.traffic.target", "traffic target")}>
                  <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                    {selectedTrafficRow.targets.map((target) => (
                      <DetailFieldCard
                        key={`${target.deploymentId}-${target.revisionId}`}
                        label={`${target.revisionId} · ${target.deploymentId}`}
                        value={t("pages.deployments.index.copy", "{value1}% · {value2} · {value3}", { value1: target.allocationWeight, value2: formatAevatarStatusLabel(target.servingState || "unknown"), value3: target.primaryActorId || t("pages.deployments.index.no.actor.yet", "No actor yet") })}
                      />
                    ))}
                  </div>
                </DrawerSection>
              </div>
            ) : (
              <Empty description={t("pages.deployments.index.traffic.endpoint.not.found", "traffic endpoint not found")} image={Empty.PRESENTED_IMAGE_SIMPLE} />
            )
          ) : null}

          {inspectorState.open && inspectorState.kind === "deployment" ? (
            inspectedDeployment ? (
              <div style={cardStackStyle}>
                <DrawerSection title={t("pages.deployments.index.deployment.summary", "deployment summary")}>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <DetailFieldCard
                      label="Deployment"
                      value={
                        <CompactIdentifierText
                          maxWidth="100%"
                          singleLine
                          value={inspectedDeployment.deploymentId}
                        />
                      }
                    />
                    <DetailFieldCard
                      label="Revision"
                      value={
                        <CompactIdentifierText
                          maxWidth="100%"
                          singleLine
                          value={inspectedDeployment.revisionId}
                        />
                      }
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.state.4", "state")}
                      value={formatAevatarStatusLabel(inspectedDeployment.status)}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.main.actor.5", "Main actor")}
                      value={
                        inspectedDeployment.primaryActorId ? (
                          <CompactIdentifierText
                            maxWidth="100%"
                            singleLine
                            value={inspectedDeployment.primaryActorId}
                          />
                        ) : (
                          t("pages.deployments.index.none.yet.8", "None yet")
                        )
                      }
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.activation.time.2", "activation time")}
                      value={formatDateTime(inspectedDeployment.activatedAt)}
                    />
                    <DetailFieldCard
                      label={t("pages.deployments.index.latest.updates.4", "Latest updates")}
                      value={formatDateTime(inspectedDeployment.updatedAt)}
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title={t("pages.deployments.index.next.steps.2", "Next steps")}>
                  <Space wrap size={[8, 8]}>
                    <Button
                      icon={<PercentageOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer("weights");
                      }}
                    >
                      {t("pages.deployments.index.adjust.flow.5", "Adjust flow")}</Button>
                    <Button
                      danger
                      icon={<StopOutlined />}
                      loading={
                        deactivateMutation.isPending &&
                        deactivateMutation.variables === inspectedDeployment.deploymentId
                      }
                      onClick={() =>
                        deactivateMutation.mutate(inspectedDeployment.deploymentId)
                      }
                    >
                      {t("pages.deployments.index.deactivate.deployment", "Deactivate deployment")}</Button>
                  </Space>
                </DrawerSection>
              </div>
            ) : (
              <Empty description={t("pages.deployments.index.deployment.not.found", "deployment not found")} image={Empty.PRESENTED_IMAGE_SIMPLE} />
            )
          ) : null}
        </div>
      </Drawer>
    </ConsoleMenuPageShell>
  );
};

export default DeploymentsPage;
