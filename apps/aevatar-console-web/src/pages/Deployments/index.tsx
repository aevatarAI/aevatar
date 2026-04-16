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
  codeBlockStyle,
  summaryFieldLabelStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";

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
  fontFamily: '"IBM Plex Mono", "SF Mono", monospace',
  fontSize: 10.5,
  overflow: "hidden",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
};

function truncateMiddle(value: string, head = 4, tail = 4): string {
  if (!value || value.length <= head + tail + 3) {
    return value;
  }

  return `${value.slice(0, head)}...${value.slice(-tail)}`;
}

function buildScopePreview(
  tenantId: string,
  appId: string,
  namespace: string,
): string {
  return `${truncateMiddle(tenantId)}/${appId}/${namespace}`;
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
  const text = truncateMiddle(value);
  const content = (
    <Typography.Text
      strong={strong}
      style={{
        color: color ?? "inherit",
        display: "inline-block",
        fontFamily: '"IBM Plex Mono", "SF Mono", monospace',
        fontSize: 11,
        maxWidth,
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: singleLine ? "nowrap" : "normal",
        ...(singleLine
          ? {}
          : {
              overflowWrap: "anywhere",
              wordBreak: "break-word",
            }),
      }}
    >
      {text}
    </Typography.Text>
  );

  return value.length > 12 ? <Tooltip title={value}>{content}</Tooltip> : content;
};

const CompactIdentifierTag: React.FC<{
  color?: string;
  style?: React.CSSProperties;
  value: string;
}> = ({ color, style, value }) => {
  const tag = (
    <Tag color={color} style={style}>
      {truncateMiddle(value)}
    </Tag>
  );

  return value.length > 12 ? <Tooltip title={value}>{tag}</Tooltip> : tag;
};

const CompactLabelText: React.FC<{
  color?: string;
  maxWidth?: React.CSSProperties["maxWidth"];
  strong?: boolean;
  value: string;
}> = ({ color, maxWidth = 112, strong = false, value }) => {
  const content = (
    <Typography.Text
      strong={strong}
      style={{
        color: color ?? "inherit",
        display: "inline-block",
        fontSize: strong ? 13 : 12,
        maxWidth,
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
      }}
    >
      {value}
    </Typography.Text>
  );

  return value.length > 8 ? <Tooltip title={value}>{content}</Tooltip> : content;
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
        label: "版本",
        value: "暂无",
      },
    ];
  }

  return [
    {
      label: "版本",
      value: revision.revisionId,
    },
    {
      label: "状态",
      value: formatAevatarStatusLabel(revision.status || "unknown"),
    },
    {
      label: "入口数",
      value: String(revision.endpoints.length),
    },
    {
      label: "制品",
      value: revision.artifactHash || "n/a",
    },
    {
      label: "准备完成",
      value: formatDateTime(revision.preparedAt),
    },
    {
      label: "已发布",
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
        .join(" · ") || "暂无流量目标",
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
    return "暂无";
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
  onChange: (draft: ServiceQueryDraft) => void;
  onLoad: () => void;
  onReset: () => void;
  scopeLabel: string;
}> = ({
  draft,
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
        display: "grid",
        gap: 12,
        gridTemplateColumns: "minmax(0, 1fr) auto",
      }}
    >
      <Space orientation="vertical" size={2} style={{ width: "100%" }}>
        <span
          style={{
            color: "var(--ant-color-primary)",
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: "0.08em",
            textTransform: "uppercase",
          }}
        >
          部署范围
        </span>
        <span
          style={{
            color: "var(--ant-color-text)",
            fontSize: 16,
            fontWeight: 700,
            lineHeight: 1.2,
          }}
        >
          团队 / 应用 / 命名空间
        </span>
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
            fontSize: 12,
            fontWeight: 600,
            minHeight: 30,
            maxWidth: "100%",
            padding: "0 12px",
            whiteSpace: "nowrap",
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

      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <span style={{ color: "var(--ant-color-text-secondary)", fontSize: 12, fontWeight: 600 }}>
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

      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <span style={{ color: "var(--ant-color-text-secondary)", fontSize: 12, fontWeight: 600 }}>
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

    </div>

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
          结果窗口
        </span>
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
        <Button size="small" onClick={onReset}>
          重置
        </Button>
        <Button size="small" type="primary" onClick={onLoad}>
          加载发布列表
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
          暂无版本信息
        </Typography.Text>
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
                "暂无 Actor"
              )}{" "}
              · {target.enabledEndpointIds.join(", ") || "所有入口"}
            </div>
          </div>
        ))
      ) : (
        <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
          暂无目标
        </Typography.Text>
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
    queryKey: ["deployments", "services", query],
  });

  const serviceDetailQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getService(selectedServiceId, query),
    queryKey: ["deployments", "service", query, selectedServiceId],
  });
  const revisionsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getRevisions(selectedServiceId, query),
    queryKey: ["deployments", "revisions", query, selectedServiceId],
  });
  const deploymentsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getDeployments(selectedServiceId, query),
    queryKey: ["deployments", "catalog", query, selectedServiceId],
  });
  const servingQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getServingSet(selectedServiceId, query),
    queryKey: ["deployments", "serving", query, selectedServiceId],
  });
  const rolloutQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getRollout(selectedServiceId, query),
    queryKey: ["deployments", "rollout", query, selectedServiceId],
  });
  const trafficQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getTraffic(selectedServiceId, query),
    queryKey: ["deployments", "traffic", query, selectedServiceId],
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

  const currentScopeLabel = useMemo(() => {
    const segments = [
      query.tenantId?.trim() ?? draft.tenantId.trim(),
      query.appId?.trim() ?? draft.appId.trim(),
      query.namespace?.trim() ?? draft.namespace.trim(),
    ].filter(Boolean);

    return segments.length > 0
      ? `当前范围 ${segments.join(" / ")}`
      : "尚未锁定服务范围";
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
          : "无活动 rollout",
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
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["deployments", "service"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "revisions"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "catalog"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "serving"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "rollout"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "traffic"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "services"] }),
    ]);
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
        throw new Error("请先选择候选版本。");
      }

      return servicesApi.deployRevision(selectedServiceId, {
        ...query,
        revisionId: candidateRevisionId,
      });
    },
    onError: (error: Error) => {
      setNotice({
        message: error.message || "发布候选版本失败。",
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "候选版本已提交到发布控制面。",
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
        message: error.message || "应用 serving targets 失败。",
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "新的 serving targets 已提交。",
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const rolloutMutation = useMutation({
    mutationFn: async (kind: "advance" | "pause" | "resume" | "rollback") => {
      const rolloutId = rolloutQuery.data?.rolloutId;
      if (!rolloutId) {
        throw new Error("当前服务没有活动 rollout。");
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
        message: error.message || "发布控制动作提交失败。",
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "发布控制动作已提交。",
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const deactivateMutation = useMutation({
    mutationFn: (deploymentId: string) => {
      if (!deploymentId.trim()) {
        throw new Error("请选择 deployment。");
      }

      return servicesApi.deactivateDeployment(
        selectedServiceId,
        deploymentId,
        query,
      );
    },
    onError: (error: Error) => {
      setNotice({
        message: error.message || "停用 deployment 失败。",
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "停用 deployment 的请求已提交。",
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
              <Typography.Text type="secondary">未绑定 deployment</Typography.Text>
            )}
          </Space>
        ),
      },
      {
        dataIndex: "primaryActorId",
        key: "primaryActorId",
        title: "主 Actor",
        render: (value: string) =>
          value ? <CompactIdentifierText maxWidth={160} singleLine value={value} /> : "暂无",
      },
      {
        dataIndex: "allocationWeight",
        key: "allocationWeight",
        title: "权重",
        render: (value: number) => `${value}%`,
      },
      {
        dataIndex: "servingState",
        key: "servingState",
        title: "Serving 状态",
        render: (value: string) => <DeploymentStatusTag status={value || "unknown"} />,
      },
      {
        dataIndex: "enabledEndpointIds",
        key: "enabledEndpointIds",
        title: "入口",
        render: (value: readonly string[]) =>
          value.length > 0 ? value.join(", ") : "所有入口",
      },
      {
        key: "actions",
        title: "操作",
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
            查看详情
          </Button>
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
        title: "标识",
      },
      {
        dataIndex: "targets",
        key: "targets",
        title: "目标分配",
        render: (targets: readonly ServiceServingTargetSnapshot[]) =>
          describeTargets(targets),
      },
    ],
    [],
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
        title: "目标数",
      },
      {
        dataIndex: "splitSummary",
        key: "splitSummary",
        title: "流量分配",
      },
      {
        dataIndex: "targets",
        key: "states",
        title: "Serving 状态",
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
        title: "操作",
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
            查看详情
          </Button>
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
        title: "主 Actor",
        width: 150,
        render: (value: string) =>
          value ? (
            <CompactIdentifierText maxWidth={116} singleLine value={value} />
          ) : (
            "暂无"
          ),
      },
      {
        dataIndex: "status",
        key: "status",
        title: "状态",
        width: 104,
        render: (value: string) => <DeploymentStatusTag status={value || "unknown"} />,
      },
      {
        dataIndex: "activatedAt",
        key: "activatedAt",
        title: "激活时间",
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
        title: "最近更新",
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
        title: "操作",
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
            查看详情
          </Button>
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
    const nextDraft = resolvedScope?.scopeId?.trim()
      ? {
          ...readServiceQueryDraft(""),
          appId: defaultScopeServiceAppId,
          namespace: defaultScopeServiceNamespace,
          tenantId: resolvedScope.scopeId.trim(),
        }
      : readServiceQueryDraft("");
    setDraft(nextDraft);
    setQuery(trimServiceQuery(nextDraft));
    setSelectedServiceId("");
    setSelectedDeploymentId("");
    setCandidateRevisionId("");
    setDrawerReason("");
    setView("catalog");
  }, [resolvedScope?.scopeId]);

  const drawerSubtitle = selectedService
    ? `${selectedService.tenantId}/${selectedService.appId}/${selectedService.namespace}`
    : "发布工作区";

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Platform"
      description="Deployments 是 Platform 的发布工作台，聚焦当前 serving、rollout 进度和流量分配。"
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
            label="可见服务"
            tone="info"
            value={String(visibleServiceDigest.services)}
          />
          <MetricCard
            label="已挂 Serving"
            tone="success"
            value={String(visibleServiceDigest.servingServices)}
          />
          <MetricCard
            label="待挂 Serving"
            tone="warning"
            value={String(visibleServiceDigest.waitingServices)}
          />
          <MetricCard
            label="有入口服务"
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
                发布服务列表
              </span>
              <Typography.Text
                strong
                style={{ color: surfaceToken.colorTextHeading, fontSize: 22 }}
              >
                先从服务列表锁定发布对象
              </Typography.Text>
              <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
                扫描 serving、deployment 和入口规模，再进入某个服务的发布详情。
              </Typography.Text>
            </Space>
          </div>

          {servicesQuery.error ? (
            <Alert
              message={
                servicesQuery.error instanceof Error
                  ? servicesQuery.error.message
                  : "加载服务发布列表失败。"
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
                    {["状态", "服务", "范围", "当前 Serving", "当前 Deployment", "入口", "最近更新", "操作"].map(
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
                              <Tooltip title={service.serviceId}>
                                <Typography.Text
                                  style={{
                                    ...compactMonoValueStyle,
                                    maxWidth: 120,
                                  }}
                                >
                                  {truncateMiddle(service.serviceId)}
                                </Typography.Text>
                              </Tooltip>
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
                              未发布
                            </Typography.Text>
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
                              未挂 Serving
                            </Tag>
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
                            查看发布详情
                          </Button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <Empty
              description="当前范围没有服务"
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
                部署候选版本
              </Button>
              <Button
                icon={<PercentageOutlined />}
                onClick={() => openDrawer("weights")}
              >
                调整流量
              </Button>
              <Button
                icon={<RollbackOutlined />}
                onClick={() => openDrawer("control")}
              >
                发布控制
              </Button>
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
          <AevatarInspectorEmpty description="正在加载发布详情" title="Loading deployment" />
        ) : !selectedService ? (
          <AevatarInspectorEmpty description="选择一个服务" />
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <WorkbenchSection title="发布摘要">
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
                    {selectedService.endpoints.length} 个入口
                  </Tag>
                </Space>

                <div
                  style={{
                    display: "grid",
                    gap: 10,
                    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
                  }}
                >
                  <DetailFieldCard
                    label="当前 serving"
                    value={
                      activeRevisionId ? (
                        <CompactIdentifierText maxWidth="100%" singleLine value={activeRevisionId} />
                      ) : (
                        "暂无 serving 版本"
                      )
                    }
                  />
                  <DetailFieldCard
                    label="当前 deployment"
                    value={
                      focusDeployment?.deploymentId ? (
                        <CompactIdentifierText
                          maxWidth="100%"
                          singleLine
                          value={focusDeployment.deploymentId}
                        />
                      ) : (
                        "未挂 Serving"
                      )
                    }
                  />
                  <DetailFieldCard
                    label="主 Actor"
                    value={
                      selectedService.primaryActorId ? (
                        <CompactIdentifierText
                          maxWidth="100%"
                          singleLine
                          value={selectedService.primaryActorId}
                        />
                      ) : (
                        "未声明"
                      )
                    }
                  />
                  <DetailFieldCard
                    label="最近同步"
                    value={
                      formatDateTime(
                        rolloutQuery.data?.updatedAt ||
                          trafficQuery.data?.updatedAt ||
                          deploymentsQuery.data?.updatedAt ||
                          selectedService.updatedAt,
                      ) || "待同步"
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
                    label="Serving 目标"
                    tone="info"
                    value={String(deploymentDigest.targets)}
                  />
                  <MetricCard
                    label="入口流量"
                    tone="success"
                    value={String(deploymentDigest.endpoints)}
                  />
                  <MetricCard
                    label="Deployment 数"
                    value={String(deploymentDigest.deployments)}
                  />
                  <MetricCard
                    label="当前 Stage"
                    tone="warning"
                    value={deploymentDigest.stage}
                  />
                </div>
              </div>
            </WorkbenchSection>

            <WorkbenchSection title="发布工作区">
              <Tabs
                activeKey={view}
                items={[
                  {
                    key: "catalog",
                    label: "部署目录",
                    children: (
                      <WorkbenchSection title="Deployment Catalog">
                        <Table<ServiceDeploymentSnapshot>
                          columns={drawerDeploymentColumns}
                          dataSource={deploymentsQuery.data?.deployments ?? []}
                          locale={{ emptyText: "当前没有 deployment catalog" }}
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
                        title="Serving Targets"
                        extra={
                          <Space wrap size={[8, 8]}>
                            <Tag>Generation {servingQuery.data?.generation ?? 0}</Tag>
                            {servingQuery.data?.activeRolloutId ? (
                              <Tag color="blue">
                                {servingQuery.data.activeRolloutId}
                              </Tag>
                            ) : null}
                            <Button
                              icon={<PercentageOutlined />}
                              onClick={() => openDrawer("weights")}
                            >
                              调整流量
                            </Button>
                          </Space>
                        }
                      >
                        <Table<ServiceServingTargetSnapshot>
                          columns={servingColumns}
                          dataSource={servingQuery.data?.targets ?? []}
                          locale={{ emptyText: "当前没有 serving targets" }}
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
                        title="入口流量"
                        extra={
                          <Space wrap size={[8, 8]}>
                            {trafficQuery.data?.activeRolloutId ? (
                              <CompactIdentifierTag
                                color="blue"
                                value={trafficQuery.data.activeRolloutId}
                              />
                            ) : null}
                            <Tag>Generation {trafficQuery.data?.generation ?? 0}</Tag>
                            <Button
                              icon={<PercentageOutlined />}
                              onClick={() => openDrawer("weights")}
                            >
                              调整流量
                            </Button>
                          </Space>
                        }
                      >
                        <Table<DeploymentTrafficRow>
                          columns={trafficColumns}
                          dataSource={trafficRows}
                          locale={{ emptyText: "当前没有 traffic view" }}
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
                          title="Rollout 概况"
                          extra={
                            <Space wrap size={[8, 8]}>
                              <DeploymentStatusTag status={rolloutQuery.data.status} />
                              <CompactIdentifierTag value={rolloutQuery.data.rolloutId} />
                              <Button
                                icon={<RollbackOutlined />}
                                onClick={() => openDrawer("control")}
                              >
                                发布控制
                              </Button>
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
                              label="当前 Stage"
                              value={
                                currentStage
                                  ? `${currentStage.stageIndex + 1} / ${rolloutQuery.data.stages.length}`
                                  : "暂无"
                              }
                            />
                            <DetailFieldCard
                              label="开始时间"
                              value={formatDateTime(rolloutQuery.data.startedAt)}
                            />
                            <DetailFieldCard
                              label="最近更新"
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
                          <WorkbenchSection title="阶段计划">
                            <Table<ServiceRolloutStageSnapshot>
                              columns={rolloutColumns}
                              dataSource={rolloutQuery.data.stages}
                              pagination={false}
                              rowKey={(record) => record.stageId}
                              size="middle"
                            />
                          </WorkbenchSection>
                          <WorkbenchSection title="基线与当前 Stage">
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
                                label="Current Stage"
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
                          description="当前没有活动 rollout"
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
        title="发布控制"
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
                      <WorkbenchSection title="候选版本">
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
                            placeholder="选择候选版本"
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
                            发布候选版本
                          </Button>
                        </Space>
                      </WorkbenchSection>
                      <RevisionSummaryCard
                        label="当前 serving 版本"
                        revision={activeRevision}
                      />
                      <RevisionSummaryCard
                        label="候选版本"
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
                        label="Current Stage"
                        targets={
                          currentStage?.targets ?? servingQuery.data?.targets ?? []
                        }
                      />
                    </div>
                  </div>
                ),
                key: "candidate",
                label: "候选版本",
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
                              {target.enabledEndpointIds?.join(", ") || "所有入口"}
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
                        description="当前没有 serving targets"
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                    <Input.TextArea
                      placeholder="说明本次 canary 或权重调整原因"
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
                      应用权重
                    </Button>
                  </div>
                ),
                key: "weights",
                label: "流量权重",
              },
              {
                children: (
                  <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                    <MetricCard
                      label="当前 rollout"
                      tone="warning"
                      value={rolloutQuery.data?.rolloutId || "暂无活动 rollout"}
                    />
                    <Input.TextArea
                      placeholder="说明本次暂停、恢复或回滚原因"
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
                        推进 rollout
                      </Button>
                      <Button
                        icon={<PauseCircleOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("pause")}
                      >
                        暂停
                      </Button>
                      <Button
                        icon={<ReloadOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("resume")}
                      >
                        恢复
                      </Button>
                      <Button
                        danger
                        icon={<RollbackOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("rollback")}
                      >
                        回滚 rollout
                      </Button>
                    </Space>
                  </div>
                ),
                key: "control",
                label: "发布控制",
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
              ? "Serving Target 详情"
              : inspectorState.kind === "traffic"
                ? "Traffic Endpoint 详情"
                : "Deployment 详情"
            : "详情"
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
                <DrawerSection title="Target 摘要">
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
                          "未绑定"
                        )
                      }
                    />
                    <DetailFieldCard
                      label="主 Actor"
                      value={
                        selectedServingTarget.primaryActorId ? (
                          <CompactIdentifierText
                            maxWidth="100%"
                            singleLine
                            value={selectedServingTarget.primaryActorId}
                          />
                        ) : (
                          "暂无"
                        )
                      }
                    />
                    <DetailFieldCard
                      label="Serving 状态"
                      value={formatAevatarStatusLabel(
                        selectedServingTarget.servingState || "unknown",
                      )}
                    />
                    <DetailFieldCard
                      label="权重"
                      value={`${selectedServingTarget.allocationWeight}%`}
                    />
                    <DetailFieldCard
                      label="入口"
                      value={
                        selectedServingTarget.enabledEndpointIds.join(", ") ||
                        "所有入口"
                      }
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title="下一步操作">
                  <Space wrap size={[8, 8]}>
                    <Button
                      icon={<PercentageOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer("weights");
                      }}
                    >
                      调整流量
                    </Button>
                    <Button
                      icon={<SendOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer("candidate");
                      }}
                    >
                      部署候选版本
                    </Button>
                  </Space>
                </DrawerSection>
              </div>
            ) : (
              <Empty description="未找到 serving target" image={Empty.PRESENTED_IMAGE_SIMPLE} />
            )
          ) : null}

          {inspectorState.open && inspectorState.kind === "traffic" ? (
            selectedTrafficRow ? (
              <div style={cardStackStyle}>
                <DrawerSection title="Endpoint 摘要">
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
                      label="目标数"
                      value={String(selectedTrafficRow.targetCount)}
                    />
                    <DetailFieldCard
                      label="分配摘要"
                      value={selectedTrafficRow.splitSummary}
                    />
                    <DetailFieldCard
                      label="活动 Rollout"
                      value={trafficQuery.data?.activeRolloutId || "暂无"}
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title="流量目标">
                  <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                    {selectedTrafficRow.targets.map((target) => (
                      <DetailFieldCard
                        key={`${target.deploymentId}-${target.revisionId}`}
                        label={`${target.revisionId} · ${target.deploymentId}`}
                        value={`${target.allocationWeight}% · ${formatAevatarStatusLabel(target.servingState || "unknown")} · ${target.primaryActorId || "暂无 Actor"}`}
                      />
                    ))}
                  </div>
                </DrawerSection>
              </div>
            ) : (
              <Empty description="未找到 traffic endpoint" image={Empty.PRESENTED_IMAGE_SIMPLE} />
            )
          ) : null}

          {inspectorState.open && inspectorState.kind === "deployment" ? (
            inspectedDeployment ? (
              <div style={cardStackStyle}>
                <DrawerSection title="Deployment 摘要">
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
                      label="状态"
                      value={formatAevatarStatusLabel(inspectedDeployment.status)}
                    />
                    <DetailFieldCard
                      label="主 Actor"
                      value={
                        inspectedDeployment.primaryActorId ? (
                          <CompactIdentifierText
                            maxWidth="100%"
                            singleLine
                            value={inspectedDeployment.primaryActorId}
                          />
                        ) : (
                          "暂无"
                        )
                      }
                    />
                    <DetailFieldCard
                      label="激活时间"
                      value={formatDateTime(inspectedDeployment.activatedAt)}
                    />
                    <DetailFieldCard
                      label="最近更新"
                      value={formatDateTime(inspectedDeployment.updatedAt)}
                    />
                  </div>
                </DrawerSection>
                <DrawerSection title="下一步操作">
                  <Space wrap size={[8, 8]}>
                    <Button
                      icon={<PercentageOutlined />}
                      onClick={() => {
                        setInspectorState({ open: false });
                        openDrawer("weights");
                      }}
                    >
                      调整流量
                    </Button>
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
                      停用 deployment
                    </Button>
                  </Space>
                </DrawerSection>
              </div>
            ) : (
              <Empty description="未找到 deployment" image={Empty.PRESENTED_IMAGE_SIMPLE} />
            )
          ) : null}
        </div>
      </Drawer>
    </ConsoleMenuPageShell>
  );
};

export default DeploymentsPage;
