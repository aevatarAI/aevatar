import {
  AppstoreOutlined,
  BarsOutlined,
  PlusOutlined,
} from "@ant-design/icons";
import { useQueries, useQuery } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Space,
  Tooltip,
  Typography,
  theme,
} from "antd";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { loadRestorableAuthSession } from "@/shared/auth/session";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import { buildTeamDetailHref } from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import type { ScopeServiceRunSummary } from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  formatStudioMemberLifecycleStage,
  type StudioMemberSummary,
  type StudioTeamSummary,
} from "@/shared/studio/models";
import {
  AevatarInspectorEmpty,
  AevatarPageShell,
} from "@/shared/ui/aevatarPageShells";
import { describeError } from "@/shared/ui/errorText";
import { resolveStudioScopeContext } from "../scopes/components/resolvedScope";
import {
  buildScopeHref,
  readScopeQueryDraft,
} from "../scopes/components/scopeQuery";
import {
  WORKFLOW_RUNTIME_GUARDRAIL,
  type WorkflowOperationalAttention,
} from "./workflowOperationalUnits";
import {
  clearSyncedPendingTeamRosterSummaries,
  mergePendingTeamRosterSummaries,
} from "./pendingTeamRoster";

const scopeServiceAppId = "default";
const compactTeamRosterThreshold = 6;

type MemberRosterPreview = {
  readonly attention: WorkflowOperationalAttention;
  readonly attentionDetail: string;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly memberId: string;
  readonly serviceId: string;
  readonly serviceLabel: string;
  readonly title: string;
  readonly updatedAt: string | null;
};

type TeamRosterPreview = {
  readonly attention: WorkflowOperationalAttention;
  readonly attentionDetail: string;
  readonly detailHref: string;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly memberPreviewLabel: string;
  readonly serviceLabel: string;
  readonly team: StudioTeamSummary;
  readonly teamId: string;
  readonly title: string;
  readonly updatedAt: string | null;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function isPlaceholderTeamLabel(value: string | null | undefined): boolean {
  const normalized = trimOptional(value).toLowerCase();
  if (!normalized) {
    return true;
  }

  return ["not configured", "unconfigured", "unknown", "n/a"].includes(normalized);
}

function pickMeaningfulLabel(
  ...candidates: Array<string | null | undefined>
): string {
  for (const candidate of candidates) {
    const normalized = trimOptional(candidate);
    if (normalized && !isPlaceholderTeamLabel(normalized)) {
      return normalized;
    }
  }

  return "";
}

function formatRunStatusLabel(status: string | null | undefined): string {
  switch (trimOptional(status).toLowerCase()) {
    case "waiting":
    case "waiting_approval":
    case "waiting_signal":
      return "待关注";
    case "failed":
    case "error":
      return "异常";
    case "completed":
      return "稳定";
    default:
      return trimOptional(status) || "未知";
  }
}

function formatOperationalStatusLabel(
  status: string | null | undefined,
  attention: WorkflowOperationalAttention,
): string {
  const normalizedStatus = trimOptional(status);
  if (normalizedStatus) {
    return formatRunStatusLabel(normalizedStatus);
  }

  switch (attention) {
    case "healthy":
      return "运行中";
    case "waiting":
      return "待关注";
    case "failed":
      return "异常";
    case "draft":
      return "草稿中";
    case "no-bound-service":
      return "待绑定";
    case "no-recent-runs":
      return "待运行";
    case "runtime-unresolved":
      return "同步中";
    default:
      return "未知";
  }
}

function formatAttentionLabel(attention: WorkflowOperationalAttention): string {
  switch (attention) {
    case "failed":
      return "待处理";
    case "waiting":
      return "待关注";
    case "healthy":
      return "运行中";
    case "draft":
      return "草稿中";
    case "no-bound-service":
      return "待绑定";
    case "no-recent-runs":
      return "待运行";
    case "runtime-unresolved":
      return "状态同步中";
    default:
      return "待确认";
  }
}

function resolveAttentionPillStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  attention: WorkflowOperationalAttention,
): React.CSSProperties {
  switch (attention) {
    case "healthy":
      return {
        background: "rgba(24, 144, 255, 0.08)",
        color: token.colorInfo,
      };
    case "waiting":
    case "no-bound-service":
    case "no-recent-runs":
      return {
        background: "rgba(250, 173, 20, 0.12)",
        color: token.colorWarning,
      };
    case "failed":
      return {
        background: "rgba(255, 77, 79, 0.12)",
        color: token.colorError,
      };
    case "draft":
      return {
        background: token.colorFillQuaternary,
        color: token.colorTextSecondary,
      };
    default:
      return {
        background: token.colorFillQuaternary,
        color: token.colorTextSecondary,
      };
  }
}

function formatShortTime(value: string | null | undefined): string {
  return formatCompactDateTime(value, "--");
}

function parseTimestamp(value: string | null | undefined): number {
  const parsed = Date.parse(value || "");
  return Number.isFinite(parsed) ? parsed : 0;
}

function normalizeStatus(value: string | null | undefined): string {
  return trimOptional(value).toLowerCase();
}

function compareRuns(
  left: ScopeServiceRunSummary,
  right: ScopeServiceRunSummary,
): number {
  const rightTime = parseTimestamp(right.lastUpdatedAt);
  const leftTime = parseTimestamp(left.lastUpdatedAt);
  if (rightTime !== leftTime) {
    return rightTime - leftTime;
  }

  if (right.stateVersion !== left.stateVersion) {
    return right.stateVersion - left.stateVersion;
  }

  return right.runId.localeCompare(left.runId);
}

function isSuccessfulRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  if (run.lastSuccess === true) {
    return true;
  }

  return ["completed", "finished", "success", "succeeded"].includes(
    normalizeStatus(run.completionStatus),
  );
}

function isWaitingRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  return [
    "waiting",
    "waiting_approval",
    "waiting_signal",
    "blocked",
    "human_approval",
    "human_input",
    "suspended",
  ].includes(normalizeStatus(run.completionStatus));
}

function isFailedRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  if (isWaitingRun(run)) {
    return false;
  }

  if (run.lastSuccess === false) {
    return true;
  }

  return ["failed", "error", "stopped", "timed_out", "timedout"].includes(
    normalizeStatus(run.completionStatus),
  );
}

const SummaryStatCard: React.FC<{
  readonly accent?: boolean;
  readonly label: string;
  readonly value: React.ReactNode;
}> = ({ accent = false, label, value }) => {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 22,
        boxShadow: token.boxShadowTertiary,
        display: "flex",
        flexDirection: "column",
        gap: 8,
        minHeight: 104,
        padding: 18,
      }}
    >
      <Typography.Title
        level={2}
        style={{
          color: accent ? token.colorPrimary : token.colorText,
          fontSize: 24,
          margin: 0,
        }}
      >
        {value}
      </Typography.Title>
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 14,
        }}
      >
        {label}
      </Typography.Text>
    </div>
  );
};

const TeamFact: React.FC<{
  readonly label: string;
  readonly value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      display: "flex",
      flexDirection: "column",
      gap: 4,
      minWidth: 0,
    }}
  >
    <Typography.Text
      strong
      style={{
        fontSize: 16,
        margin: 0,
        overflowWrap: "anywhere",
      }}
    >
      {value}
    </Typography.Text>
    <Typography.Text style={{ fontSize: 13 }} type="secondary">
      {label}
    </Typography.Text>
  </div>
);

function compareMembers(
  left: StudioMemberSummary,
  right: StudioMemberSummary,
): number {
  const rightTime = parseTimestamp(right.updatedAt);
  const leftTime = parseTimestamp(left.updatedAt);
  if (rightTime !== leftTime) {
    return rightTime - leftTime;
  }

  return right.memberId.localeCompare(left.memberId);
}

function compareTeams(
  left: StudioTeamSummary,
  right: StudioTeamSummary,
): number {
  const rightTime = parseTimestamp(right.updatedAt);
  const leftTime = parseTimestamp(left.updatedAt);
  if (rightTime !== leftTime) {
    return rightTime - leftTime;
  }

  return right.teamId.localeCompare(left.teamId);
}

function groupMembersByTeamId(
  members: readonly StudioMemberSummary[],
): Map<string, StudioMemberSummary[]> {
  const result = new Map<string, StudioMemberSummary[]>();
  members.forEach((member) => {
    const teamId = trimOptional(member.teamId);
    if (!teamId) {
      return;
    }

    const existing = result.get(teamId);
    if (existing) {
      existing.push(member);
    } else {
      result.set(teamId, [member]);
    }
  });

  result.forEach((teamMembers) => {
    teamMembers.sort(compareMembers);
  });
  return result;
}

function resolveMemberPreviewService(input: {
  readonly member: StudioMemberSummary;
  readonly services: readonly ServiceCatalogSnapshot[];
}): ServiceCatalogSnapshot | null {
  const boundServiceId = trimOptional(input.member.publishedServiceId);
  if (!boundServiceId) {
    return null;
  }

  return (
    input.services.find(
      (service) => trimOptional(service.serviceId) === boundServiceId,
    ) ?? null
  );
}

function resolveRuntimeUnavailable(input: {
  readonly memberId: string;
  readonly runtimeAvailableByMemberId?: ReadonlySet<string>;
  readonly runtimeGuardrailedMemberIds?: ReadonlySet<string>;
}): boolean {
  const memberId = trimOptional(input.memberId);
  if (!memberId) {
    return false;
  }

  if (input.runtimeGuardrailedMemberIds?.has(memberId)) {
    return true;
  }

  if (!input.runtimeAvailableByMemberId) {
    return false;
  }

  return !input.runtimeAvailableByMemberId.has(memberId);
}

function buildMemberRosterPreview(input: {
  readonly guardrailedMemberIds?: ReadonlySet<string>;
  readonly member: StudioMemberSummary;
  readonly runsByMemberId: Readonly<Record<string, readonly ScopeServiceRunSummary[]>>;
  readonly runtimeAvailableByMemberId?: ReadonlySet<string>;
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
  readonly teamId?: string | null;
}): MemberRosterPreview {
  const matchedService = resolveMemberPreviewService({
    member: input.member,
    services: input.services,
  });
  const memberId = trimOptional(input.member.memberId);
  const serviceId =
    trimOptional(input.member.publishedServiceId) ||
    trimOptional(matchedService?.serviceId);
  const runtimeRelevant = Boolean(
    serviceId || trimOptional(input.member.lastBoundRevisionId),
  );
  const runtimeUnavailable =
    runtimeRelevant &&
    resolveRuntimeUnavailable({
      memberId,
      runtimeAvailableByMemberId: input.runtimeAvailableByMemberId,
      runtimeGuardrailedMemberIds: input.guardrailedMemberIds,
    });
  const runs =
    memberId && !runtimeUnavailable ? input.runsByMemberId[memberId] ?? [] : [];
  const latestRun = runs.slice().sort(compareRuns)[0] ?? null;
  const serviceLabel =
    pickMeaningfulLabel(trimOptional(matchedService?.displayName), serviceId) ||
    (trimOptional(input.member.lastBoundRevisionId) ? "已绑定待确认" : "未绑定");
  const title = pickMeaningfulLabel(input.member.displayName, input.member.memberId) || "未命名成员";

  let attention: WorkflowOperationalAttention = "draft";
  let attentionDetail = `当前成员还处于 ${formatStudioMemberLifecycleStage(input.member.lifecycleStage)} 阶段。`;

  if (runtimeUnavailable) {
    attention = "runtime-unresolved";
    attentionDetail = "成员已绑定，首页暂未同步到最近运行状态；打开团队可查看完整上下文。";
  } else if (latestRun && isFailedRun(latestRun)) {
    attention = "failed";
    attentionDetail =
      trimOptional(latestRun.lastError) || "最近一次成员运行处于异常状态。";
  } else if (latestRun && isWaitingRun(latestRun)) {
    attention = "waiting";
    attentionDetail =
      trimOptional(latestRun.lastError) || "最近一次成员运行正在等待人工或外部信号。";
  } else if (latestRun && isSuccessfulRun(latestRun)) {
    attention = "healthy";
    attentionDetail = "最近一次成员运行正常，可继续进入详情查看。";
  } else if (serviceId || matchedService) {
    attention = "no-recent-runs";
    attentionDetail = "当前成员已经形成绑定，但还没有可见的运行信号。";
  } else if (
    trimOptional(input.member.lastBoundRevisionId) ||
    input.member.lifecycleStage === "bind_ready"
  ) {
    attention = "no-bound-service";
    attentionDetail = "当前成员已经准备好绑定，但还没有稳定的成员调用入口。";
  }

  return {
    attention,
    attentionDetail,
    latestRun,
    memberId,
    serviceId,
    serviceLabel,
    title,
    updatedAt:
      latestRun?.lastUpdatedAt ||
      matchedService?.updatedAt ||
      input.member.updatedAt ||
      null,
  };
}

function buildTeamRosterPreview(input: {
  readonly guardrailedMemberIds?: ReadonlySet<string>;
  readonly members: readonly StudioMemberSummary[];
  readonly runsByMemberId: Readonly<Record<string, readonly ScopeServiceRunSummary[]>>;
  readonly runtimeAvailableByMemberId?: ReadonlySet<string>;
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
  readonly team: StudioTeamSummary;
}): TeamRosterPreview {
  const memberPreviews = input.members.map((member) =>
    buildMemberRosterPreview({
      guardrailedMemberIds: input.guardrailedMemberIds,
      member,
      runsByMemberId: input.runsByMemberId,
      runtimeAvailableByMemberId: input.runtimeAvailableByMemberId,
      scopeId: input.scopeId,
      services: input.services,
      teamId: input.team.teamId,
    }),
  );
  const sortedMembers = [...input.members].sort(compareMembers);
  const latestRun =
    memberPreviews
      .map((preview) => preview.latestRun)
      .filter((run): run is ScopeServiceRunSummary => Boolean(run))
      .sort(compareRuns)[0] ?? null;
  const statusRank: Record<WorkflowOperationalAttention, number> = {
    failed: 0,
    waiting: 1,
    "runtime-unresolved": 2,
    "no-bound-service": 3,
    "no-recent-runs": 4,
    draft: 5,
    healthy: 6,
  };
  const mostImportantMemberPreview = memberPreviews
    .slice()
    .sort(
      (left, right) =>
        statusRank[left.attention] - statusRank[right.attention] ||
        parseTimestamp(right.updatedAt) - parseTimestamp(left.updatedAt) ||
        right.memberId.localeCompare(left.memberId),
    )[0];
  const memberCount =
    input.team.memberCount > 0 ? input.team.memberCount : input.members.length;
  const firstMemberLabel = pickMeaningfulLabel(
    sortedMembers[0]?.displayName,
    sortedMembers[0]?.memberId,
  );
  const memberPreviewLabel =
    memberCount > 0
      ? firstMemberLabel
        ? memberCount > 1
          ? `${firstMemberLabel} 等 ${memberCount} 个成员`
          : firstMemberLabel
        : `${memberCount} 个成员`
      : "暂无成员";
  const serviceLabels = memberPreviews
    .map((preview) => preview.serviceLabel)
    .filter((label) => label && label !== "未绑定");
  const uniqueServiceLabels = Array.from(new Set(serviceLabels));
  const primaryMemberPreview =
    memberPreviews.find((preview) => preview.serviceId) ?? memberPreviews[0] ?? null;
  const detailHref = buildTeamDetailHref({
    memberId: primaryMemberPreview?.memberId || undefined,
    runId: latestRun?.runId || undefined,
    scopeId: input.scopeId,
    serviceId: primaryMemberPreview?.serviceId || undefined,
    teamId: input.team.teamId,
  });

  let attention: WorkflowOperationalAttention =
    mostImportantMemberPreview?.attention ?? "draft";
  let attentionDetail = "这个 Team 已经存在后端事实，但还没有分配成员。";
  if (input.team.lifecycleStage === "archived") {
    attention = "draft";
    attentionDetail = "这个 Team 已归档，列表中仅保留它的后端 roster 事实。";
  } else if (mostImportantMemberPreview) {
    attentionDetail = mostImportantMemberPreview.attentionDetail;
  }

  return {
    attention,
    attentionDetail,
    detailHref,
    latestRun,
    memberPreviewLabel,
    serviceLabel:
      uniqueServiceLabels.length > 0
        ? uniqueServiceLabels.slice(0, 2).join(" / ")
        : "暂无绑定服务",
    team: input.team,
    teamId: input.team.teamId,
    title: pickMeaningfulLabel(input.team.displayName, input.team.teamId) || "未命名 Team",
    updatedAt:
      latestRun?.lastUpdatedAt ||
      mostImportantMemberPreview?.updatedAt ||
      input.team.updatedAt ||
      null,
  };
}

const TeamRosterCard: React.FC<{
  readonly preview: TeamRosterPreview;
}> = ({ preview }) => {
  const { token } = theme.useToken();

  return (
    <article
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 24,
        boxShadow: token.boxShadowTertiary,
        display: "flex",
        flexDirection: "column",
        gap: 14,
        minWidth: 0,
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
        <div style={{ minWidth: 0 }}>
          <Typography.Title
            level={3}
            style={{
              fontSize: 22,
              margin: 0,
              overflowWrap: "anywhere",
            }}
          >
            {preview.title}
          </Typography.Title>
          <Typography.Paragraph
            ellipsis={{ rows: 1, tooltip: preview.attentionDetail }}
            style={{
              color: token.colorTextSecondary,
              fontSize: 14,
              marginBottom: 0,
              marginTop: 6,
            }}
          >
            {preview.attentionDetail}
          </Typography.Paragraph>
        </div>
        <span
          style={{
            ...resolveAttentionPillStyle(token, preview.attention),
            borderRadius: 999,
            display: "inline-flex",
            fontSize: 12,
            fontWeight: 600,
            lineHeight: 1,
            padding: "8px 12px",
            whiteSpace: "nowrap",
          }}
        >
          {formatAttentionLabel(preview.attention)}
        </span>
      </div>

      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 13,
        }}
      >
        Team 标识：{preview.teamId}
      </Typography.Text>

      <div
        style={{
          borderTop: `1px solid ${token.colorBorderSecondary}`,
          display: "grid",
          gap: 14,
          gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
          paddingTop: 14,
        }}
      >
        <TeamFact
          label="当前状态"
          value={formatOperationalStatusLabel(
            preview.latestRun?.completionStatus,
            preview.attention,
          )}
        />
        <TeamFact
          label="最近更新"
          value={formatShortTime(preview.updatedAt)}
        />
      </div>

      <div
        style={{
          borderTop: `1px solid ${token.colorBorderSecondary}`,
          display: "grid",
          gap: 14,
          gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
          paddingTop: 14,
        }}
      >
        <TeamFact label="Team 成员" value={preview.memberPreviewLabel} />
        <TeamFact label="关联服务" value={preview.serviceLabel} />
      </div>

      <Space wrap>
        <Button
          onClick={() => history.push(preview.detailHref)}
          size="large"
          type="primary"
        >
          查看团队
        </Button>
      </Space>
    </article>
  );
};

const TeamRosterRow: React.FC<{
  readonly preview: TeamRosterPreview;
}> = ({ preview }) => {
  const { token } = theme.useToken();

  return (
    <article
      className="teams-home-roster-row"
      style={{
        alignItems: "center",
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 20,
        boxShadow: token.boxShadowTertiary,
        display: "grid",
        gap: 16,
        gridTemplateColumns: "minmax(0, 1.8fr) repeat(4, minmax(88px, 120px)) auto",
        minWidth: 0,
        padding: 16,
      }}
    >
      <div style={{ minWidth: 0 }}>
        <Space size={[8, 8]} wrap style={{ marginBottom: 6 }}>
          <Typography.Title
            level={4}
            style={{
              margin: 0,
              overflowWrap: "anywhere",
            }}
          >
            {preview.title}
          </Typography.Title>
          <span
            style={{
              ...resolveAttentionPillStyle(token, preview.attention),
              borderRadius: 999,
              display: "inline-flex",
              fontSize: 12,
              fontWeight: 600,
              lineHeight: 1,
              padding: "7px 10px",
              whiteSpace: "nowrap",
            }}
          >
            {formatAttentionLabel(preview.attention)}
          </span>
        </Space>
        <Typography.Paragraph
          ellipsis={{ rows: 1, tooltip: preview.attentionDetail }}
          style={{
            color: token.colorTextSecondary,
            fontSize: 13,
            marginBottom: 0,
            marginTop: 0,
          }}
        >
          {preview.attentionDetail}
        </Typography.Paragraph>
        <Typography.Text
          style={{
            color: token.colorTextSecondary,
            fontSize: 13,
          }}
        >
          Team 标识：{preview.teamId}
        </Typography.Text>
      </div>

      <TeamFact
        label="状态"
        value={formatOperationalStatusLabel(
          preview.latestRun?.completionStatus,
          preview.attention,
        )}
      />
      <TeamFact label="更新" value={formatShortTime(preview.updatedAt)} />
      <TeamFact label="成员" value={preview.memberPreviewLabel} />
      <TeamFact label="服务" value={preview.serviceLabel} />

      <Space className="teams-home-roster-row-actions" wrap>
        <Button onClick={() => history.push(preview.detailHref)} type="primary">
          查看团队
        </Button>
      </Space>
    </article>
  );
};

const TeamsHomePage: React.FC = () => {
  const { token } = theme.useToken();
  const [routeScopeId, setRouteScopeId] = React.useState(
    () => readScopeQueryDraft().scopeId.trim(),
  );
  const [manualRosterView, setManualRosterView] = React.useState<
    "cards" | "list" | null
  >(null);

  const authSessionQuery = useQuery({
    queryKey: ["scopes", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const localScopeId = trimOptional(loadRestorableAuthSession()?.user.sub);
  const locallyResolvedScope = React.useMemo(() => {
    if (!localScopeId) {
      return null;
    }

    return {
      scopeId: localScopeId,
      scopeSource: "local-session",
    };
  }, [localScopeId]);
  const resolvedScope = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data) ?? locallyResolvedScope,
    [authSessionQuery.data, locallyResolvedScope],
  );
  const authSessionIssue = React.useMemo(() => {
    if (!authSessionQuery.isError) {
      return "";
    }

    return describeError(
      authSessionQuery.error,
      "登录状态暂时不可用，请刷新后重试。",
    );
  }, [authSessionQuery.error, authSessionQuery.isError]);

  React.useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    setRouteScopeId((currentScopeId) =>
      currentScopeId.trim() ? currentScopeId : resolvedScope.scopeId,
    );
  }, [resolvedScope?.scopeId]);

  const scopeId = routeScopeId || resolvedScope?.scopeId?.trim() || "";

  React.useEffect(() => {
    if (!scopeId) {
      return;
    }

    const nextPath = buildScopeHref("/teams", { scopeId });
    const currentPath =
      typeof window === "undefined"
        ? ""
        : `${window.location.pathname}${window.location.search}`;
    if (nextPath !== currentPath) {
      history.replace(nextPath);
    }
  }, [scopeId]);

  const membersQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "members", scopeId],
    queryFn: () => studioApi.listMembers(scopeId),
    retry: false,
  });
  const teamsQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "roster", scopeId],
    queryFn: () => studioApi.listTeams(scopeId),
    retry: false,
  });
  const servicesQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "services", scopeId],
    queryFn: () =>
      scopeRuntimeApi.listServices(scopeId, {
        appId: scopeServiceAppId,
      }),
    retry: false,
  });

  const studioMembers = React.useMemo(
    () => [...(membersQuery.data?.members ?? [])].sort(compareMembers),
    [membersQuery.data?.members],
  );
  React.useEffect(() => {
    if (teamsQuery.isSuccess) {
      clearSyncedPendingTeamRosterSummaries(
        scopeId,
        teamsQuery.data?.teams ?? [],
      );
    }
  }, [scopeId, teamsQuery.data?.teams, teamsQuery.isSuccess]);
  const studioTeams = React.useMemo(
    () =>
      [
        ...mergePendingTeamRosterSummaries(
          scopeId,
          teamsQuery.data?.teams ?? [],
        ),
      ].sort(compareTeams),
    [scopeId, teamsQuery.data?.teams],
  );
  const membersByTeamId = React.useMemo(
    () => groupMembersByTeamId(studioMembers),
    [studioMembers],
  );
  const runtimeTrackableMembers = React.useMemo(
    () =>
      studioMembers.filter(
        (member) =>
          Boolean(trimOptional(member.publishedServiceId)) ||
          Boolean(trimOptional(member.lastBoundRevisionId)),
      ),
    [studioMembers],
  );
  const runtimeSampleMembers = React.useMemo(
    () => runtimeTrackableMembers.slice(0, WORKFLOW_RUNTIME_GUARDRAIL),
    [runtimeTrackableMembers],
  );
  const guardrailedMemberIds = React.useMemo(
    () =>
      new Set(
        runtimeTrackableMembers
          .slice(WORKFLOW_RUNTIME_GUARDRAIL)
          .map((member) => trimOptional(member.memberId))
          .filter(Boolean),
      ),
    [runtimeTrackableMembers],
  );
  const memberRunQueries = useQueries({
    queries: runtimeSampleMembers.map((member) => ({
      enabled: scopeId.length > 0 && membersQuery.isSuccess,
      queryKey: ["teams", "member-runs", scopeId, member.memberId],
      queryFn: () =>
        scopeRuntimeApi.listMemberRuns(scopeId, member.memberId, {
          take: 12,
        }),
      retry: false,
    })),
  });
  const runtimeAvailableByMemberId = React.useMemo(() => {
    const available = new Set<string>();
    memberRunQueries.forEach((query, index) => {
      if (query.isSuccess) {
        available.add(trimOptional(runtimeSampleMembers[index]?.memberId));
      }
    });
    return available;
  }, [memberRunQueries, runtimeSampleMembers]);
  const runsByMemberId = React.useMemo(
    () =>
      Object.fromEntries(
        runtimeSampleMembers.map((member, index) => [
          trimOptional(member.memberId),
          memberRunQueries[index]?.data?.runs ?? [],
        ]),
      ) as Record<string, readonly any[]>,
    [memberRunQueries, runtimeSampleMembers],
  );
  const teamPreviews = React.useMemo(
    () =>
      studioTeams.map((team) =>
        buildTeamRosterPreview({
          guardrailedMemberIds,
          members: membersByTeamId.get(team.teamId) ?? [],
          runsByMemberId,
          runtimeAvailableByMemberId,
          scopeId,
          services: servicesQuery.data ?? [],
          team,
        }),
      ),
    [
      guardrailedMemberIds,
      membersByTeamId,
      runsByMemberId,
      runtimeAvailableByMemberId,
      scopeId,
      servicesQuery.data,
      studioTeams,
    ],
  );
  const unresolvedRuntimeTeamCount = React.useMemo(
    () =>
      teamPreviews.filter(
        (preview) => preview.attention === "runtime-unresolved",
      ).length,
    [teamPreviews],
  );
  const visibleTeamCount = teamPreviews.length;
  const resolvedRosterView =
    manualRosterView ??
    (visibleTeamCount >= compactTeamRosterThreshold ? "list" : "cards");
  const useCompactRoster = resolvedRosterView === "list";
  const emptyRosterHint =
    scopeId.length > 0
      ? "当前账号还没有创建任何 Team。创建后，这里会展示你的 AI 团队列表。"
      : "当前登录状态还没有解析出可用的团队范围，请刷新后重试。";
  const partialIssues = [
    membersQuery.isError ? "当前工作空间的成员清单暂时不可见。" : null,
    teamsQuery.isError ? "当前工作空间的 Team roster 暂时不可见。" : null,
  ].filter((issue): issue is string => Boolean(issue));

  const titleNode = (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 14,
        }}
      >
        Aevatar / Teams
      </Typography.Text>
      <Typography.Title
        level={1}
        style={{
          margin: 0,
        }}
      >
        我的 AI 团队
      </Typography.Title>
    </div>
  );

  return (
    <AevatarPageShell
      extra={
        <Space wrap>
          <Button
            icon={<PlusOutlined />}
            onClick={() =>
              history.push(buildScopeHref("/teams/new", { scopeId }))
            }
            style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
            type="primary"
          >
            组建新团队
          </Button>
        </Space>
      }
      layoutMode="document"
      title={titleNode}
    >
      <div
        style={{
          display: "flex",
          flexDirection: "column",
          gap: 20,
        }}
      >
        {!scopeId ? (
          <Alert
            showIcon
            title="当前登录状态还没有解析出可用的团队范围，请刷新后重试。"
            type="info"
          />
        ) : null}

        {partialIssues.length > 0 ? (
          <Alert
            description={partialIssues.join(" ")}
            showIcon
            title="部分团队信号暂时不可见"
            type="warning"
          />
        ) : null}

        {authSessionIssue ? (
          <Alert
            description={
              resolvedScope?.scopeId
                ? `${authSessionIssue} 已使用本地登录信息继续加载团队。`
                : authSessionIssue
            }
            showIcon
            title={
              resolvedScope?.scopeId
                ? "当前登录态校验失败，已使用本地登录信息"
                : "当前登录态校验失败"
            }
            type="warning"
          />
        ) : null}

        {scopeId ? (
          <>
            <div
              style={{
                display: "grid",
                gap: 16,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <SummaryStatCard accent label="AI Team" value={visibleTeamCount} />
            </div>

            {teamsQuery.isLoading ? (
              <AevatarInspectorEmpty description="正在读取团队列表。" />
            ) : teamsQuery.isError ? (
              <Alert
                showIcon
                title="团队列表暂时无法加载。"
                type="error"
              />
            ) : teamPreviews.length > 0 ? (
              <>
                <div
                  style={{
                    alignItems: "center",
                    display: "flex",
                    flexWrap: "wrap",
                    gap: 12,
                    justifyContent: "space-between",
                  }}
                >
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 4,
                    }}
                  >
                    <Typography.Title
                      level={4}
                      style={{
                        margin: 0,
                      }}
                    >
                      团队列表
                    </Typography.Title>
                    <Typography.Text type="secondary">
                      当前账号下已经创建的 AI Team；成员和运行状态用于帮助你快速判断是否需要处理。
                    </Typography.Text>
                  </div>
                  {visibleTeamCount > 1 ? (
                    <Space.Compact>
                      <Tooltip title="卡片视图">
                        <Button
                          aria-label="切换到卡片视图"
                          icon={<AppstoreOutlined />}
                          onClick={() => setManualRosterView("cards")}
                          style={{ height: 44, width: 44 }}
                          type={resolvedRosterView === "cards" ? "primary" : "default"}
                        />
                      </Tooltip>
                      <Tooltip title="列表视图">
                        <Button
                          aria-label="切换到列表视图"
                          icon={<BarsOutlined />}
                          onClick={() => setManualRosterView("list")}
                          style={{ height: 44, width: 44 }}
                          type={resolvedRosterView === "list" ? "primary" : "default"}
                        />
                      </Tooltip>
                    </Space.Compact>
                  ) : null}
                </div>
                {unresolvedRuntimeTeamCount > 0 ? (
                  <Alert
                    description={`有 ${unresolvedRuntimeTeamCount} 个 Team 已完成成员绑定，但首页暂未同步到最近运行状态。它们不一定需要处理；打开团队详情可以查看成员、服务和运行上下文。`}
                    message="部分 Team 的运行状态仍在同步"
                    showIcon
                    type="info"
                  />
                ) : null}
                {useCompactRoster ? (
                  <ul
                    aria-label="团队紧凑视图"
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 14,
                      listStyle: "none",
                      margin: 0,
                      padding: 0,
                    }}
                  >
                    {teamPreviews.map((preview) => (
                      <li key={preview.teamId}>
                        <TeamRosterRow preview={preview} />
                      </li>
                    ))}
                  </ul>
                ) : (
                  <ul
                    aria-label="团队卡片视图"
                    style={{
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns: "repeat(auto-fit, minmax(340px, 1fr))",
                      listStyle: "none",
                      margin: 0,
                      padding: 0,
                    }}
                  >
                    {teamPreviews.map((preview) => (
                      <li key={preview.teamId}>
                        <TeamRosterCard preview={preview} />
                      </li>
                    ))}
                  </ul>
                )}
              </>
            ) : (
              <Empty
                description={emptyRosterHint}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              >
                <Button
                  onClick={() =>
                    history.push(buildScopeHref("/teams/new", { scopeId }))
                  }
                  type="primary"
                >
                  组建新团队
                </Button>
              </Empty>
            )}

          </>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

export default TeamsHomePage;
