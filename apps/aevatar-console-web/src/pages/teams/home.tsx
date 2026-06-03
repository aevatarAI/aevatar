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
import type { WorkflowOperationalAttention } from "./workflowOperationalUnits";
import {
  clearSyncedPendingTeamRosterSummaries,
  mergePendingTeamRosterSummaries,
} from "./pendingTeamRoster";
import { t } from "@/shared/i18n/messages";

const scopeServiceAppId = "default";
const compactTeamRosterThreshold = 6;

type TeamOperationalAttention = Exclude<
  WorkflowOperationalAttention,
  "runtime-unresolved"
>;

type MemberRosterPreview = {
  readonly attention: TeamOperationalAttention;
  readonly attentionDetail: string;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly memberId: string;
  readonly serviceId: string;
  readonly serviceLabel: string;
  readonly title: string;
  readonly updatedAt: string | null;
};

type TeamRosterPreview = {
  readonly attention: TeamOperationalAttention;
  readonly attentionDetail: string;
  readonly detailHref: string;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly memberPreviewLabel: string;
  readonly memberPreviewTooltip?: string;
  readonly serviceLabel: string;
  readonly serviceTooltip?: string;
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

// Refactor (v1/issue1444-first):
//   Old: workflow run status labels leaked raw runtime terms directly into the teams UI.
//   New: run status mapping keeps UI display labels stable while preserving the underlying status semantics.
function formatRunStatusLabel(status: string | null | undefined): string {
  switch (trimOptional(status).toLowerCase()) {
    case "waiting":
    case "waiting_approval":
    case "waiting_signal":
      return t("pages.teams.home.copy", "待关注");
    case "failed":
    case "error":
      return t("pages.teams.home.copy.2", "异常");
    case "completed":
      return t("pages.teams.home.copy.3", "已完成");
    default:
      return trimOptional(status) || t("pages.teams.home.copy.4", "未知");
  }
}

function formatOperationalStatusLabel(
  status: string | null | undefined,
  attention: TeamOperationalAttention,
): string {
  const normalizedStatus = trimOptional(status);
  if (normalizedStatus) {
    return formatRunStatusLabel(normalizedStatus);
  }

  switch (attention) {
    case "healthy":
      return t("pages.teams.home.copy.5", "运行中");
    case "waiting":
      return t("pages.teams.home.copy.6", "待关注");
    case "failed":
      return t("pages.teams.home.copy.7", "异常");
    case "draft":
      return t("pages.teams.home.copy.8", "草稿中");
    case "no-bound-service":
      return t("pages.teams.home.copy.9", "待绑定");
    case "no-recent-runs":
      return t("pages.teams.home.copy.10", "待运行");
    default:
      return t("pages.teams.home.copy.11", "未知");
  }
}

function formatAttentionLabel(attention: TeamOperationalAttention): string {
  switch (attention) {
    case "failed":
      return t("pages.teams.home.copy.12", "待处理");
    case "waiting":
      return t("pages.teams.home.copy.13", "待关注");
    case "healthy":
      return t("pages.teams.home.copy.14", "运行中");
    case "draft":
      return t("pages.teams.home.copy.15", "草稿中");
    case "no-bound-service":
      return t("pages.teams.home.copy.16", "待绑定");
    case "no-recent-runs":
      return t("pages.teams.home.copy.17", "待运行");
    default:
      return t("pages.teams.home.copy.18", "待确认");
  }
}

function resolveAttentionPillStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  attention: TeamOperationalAttention,
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

const TeamTitle: React.FC<{
  readonly level: 3 | 4;
  readonly title: string;
}> = ({ level, title }) => (
  <div
    style={{
      minWidth: 0,
    }}
    title={title}
  >
    <Typography.Title
      level={level}
      style={{
        margin: 0,
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
      }}
    >
      {title}
    </Typography.Title>
  </div>
);

const TeamFact: React.FC<{
  readonly label: string;
  readonly tooltip?: string;
  readonly value: React.ReactNode;
}> = ({ label, tooltip, value }) => {
  const { token } = theme.useToken();
  const renderedValue = (
    <span
      style={{
        color: token.colorText,
        display: "block",
        fontSize: 16,
        fontWeight: 600,
        margin: 0,
        minWidth: 0,
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
      }}
      title={typeof value === "string" ? tooltip || value : undefined}
    >
      {value}
    </span>
  );

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        gap: 4,
        minWidth: 0,
      }}
    >
      {typeof value === "string" && (tooltip || value) ? (
        <Tooltip title={tooltip || value}>{renderedValue}</Tooltip>
      ) : (
        renderedValue
      )}
      <Typography.Text style={{ fontSize: 13 }} type="secondary">
        {label}
      </Typography.Text>
    </div>
  );
};

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

function buildMemberRosterPreview(input: {
  readonly member: StudioMemberSummary;
  readonly runsByMemberId: Readonly<Record<string, readonly ScopeServiceRunSummary[]>>;
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
  const runs = memberId ? input.runsByMemberId[memberId] ?? [] : [];
  const latestRun = runs.slice().sort(compareRuns)[0] ?? null;
  const serviceLabel =
    pickMeaningfulLabel(trimOptional(matchedService?.displayName), serviceId) ||
    (trimOptional(input.member.lastBoundRevisionId) ? t("pages.teams.home.copy.19", "已绑定待确认") : t("pages.teams.home.copy.20", "未绑定"));
  const title = pickMeaningfulLabel(input.member.displayName, input.member.memberId) || t("pages.teams.home.copy.21", "未命名成员");

  let attention: TeamOperationalAttention = "draft";
  let attentionDetail = t("pages.teams.home.copy.22", "当前成员还处于 {value1} 阶段。", { value1: formatStudioMemberLifecycleStage(input.member.lifecycleStage) });

  if (latestRun && isFailedRun(latestRun)) {
    attention = "failed";
    attentionDetail =
      trimOptional(latestRun.lastError) || t("pages.teams.home.copy.23", "最近一次成员运行处于异常状态。");
  } else if (latestRun && isWaitingRun(latestRun)) {
    attention = "waiting";
    attentionDetail =
      trimOptional(latestRun.lastError) || t("pages.teams.home.copy.24", "最近一次成员运行正在等待人工或外部信号。");
  } else if (latestRun && isSuccessfulRun(latestRun)) {
    attention = "healthy";
    attentionDetail = t("pages.teams.home.copy.25", "最近一次成员运行正常，可继续进入详情查看。");
  } else if (serviceId || matchedService) {
    attention = "no-recent-runs";
    attentionDetail = t("pages.teams.home.copy.26", "成员已绑定服务。下一步：进入团队详情后测试团队，生成第一条可见运行。");
  } else if (
    trimOptional(input.member.lastBoundRevisionId) ||
    input.member.lifecycleStage === "bind_ready"
  ) {
    attention = "no-bound-service";
    attentionDetail = t("pages.teams.home.copy.27", "当前成员已经准备好绑定，但还没有稳定的成员调用入口。");
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
  readonly members: readonly StudioMemberSummary[];
  readonly runsByMemberId: Readonly<Record<string, readonly ScopeServiceRunSummary[]>>;
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
  readonly team: StudioTeamSummary;
}): TeamRosterPreview {
  const memberPreviews = input.members.map((member) =>
    buildMemberRosterPreview({
      member,
      runsByMemberId: input.runsByMemberId,
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
  const statusRank: Record<TeamOperationalAttention, number> = {
    failed: 0,
    waiting: 1,
    "no-bound-service": 2,
    "no-recent-runs": 3,
    draft: 4,
    healthy: 5,
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
          ? t("pages.teams.home.copy.28", "{value1} 等 {value2} 个成员", { value1: firstMemberLabel, value2: memberCount })
          : firstMemberLabel
        : t("pages.teams.home.copy.29", "{value1} 个成员", { value1: memberCount })
      : t("pages.teams.home.copy.30", "暂无成员");
  const serviceLabels = memberPreviews
    .map((preview) => preview.serviceLabel)
    .filter((label) => label && label !== t("pages.teams.home.copy.31", "未绑定"));
  const uniqueServiceLabels = Array.from(new Set(serviceLabels));
  const memberPreviewTooltip =
    sortedMembers.length > 0
      ? sortedMembers
          .map((member) =>
            pickMeaningfulLabel(member.displayName, member.memberId) || t("pages.teams.home.copy.32", "未命名成员"),
          )
          .join(" / ")
      : undefined;
  const serviceTooltip =
    uniqueServiceLabels.length > 0 ? uniqueServiceLabels.join(" / ") : undefined;
  const primaryMemberPreview =
    memberPreviews.find((preview) => preview.serviceId) ?? memberPreviews[0] ?? null;
  const detailHref = buildTeamDetailHref({
    memberId: primaryMemberPreview?.memberId || undefined,
    runId: latestRun?.runId || undefined,
    scopeId: input.scopeId,
    serviceId: primaryMemberPreview?.serviceId || undefined,
    teamId: input.team.teamId,
  });

  let attention: TeamOperationalAttention =
    mostImportantMemberPreview?.attention ?? "draft";
  let attentionDetail = t("pages.teams.home.team", "这个 Team 还没有成员。下一步：添加入口成员后再测试团队。");
  if (input.team.lifecycleStage === "archived") {
    attention = "draft";
    attentionDetail = t("pages.teams.home.team.roster", "这个 Team 已归档，列表中仅保留它的后端 roster 事实。");
  } else if (mostImportantMemberPreview) {
    attentionDetail = mostImportantMemberPreview.attentionDetail;
  }

  return {
    attention,
    attentionDetail,
    detailHref,
    latestRun,
    memberPreviewLabel,
    memberPreviewTooltip,
    serviceLabel:
      uniqueServiceLabels.length > 0
        ? uniqueServiceLabels.slice(0, 2).join(" / ")
        : t("pages.teams.home.copy.33", "暂无绑定服务"),
    serviceTooltip,
    team: input.team,
    teamId: input.team.teamId,
    title: pickMeaningfulLabel(input.team.displayName, input.team.teamId) || t("pages.teams.home.team.2", "未命名 Team"),
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
          <div style={{ fontSize: 22 }}>
            <TeamTitle level={3} title={preview.title} />
          </div>
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
        title={preview.teamId}
        ellipsis={{ tooltip: preview.teamId }}
        style={{
          color: token.colorTextSecondary,
          display: "block",
          fontSize: 12,
        }}
      >
        {t("pages.teams.home.id", "ID：")}{preview.teamId}
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
          label={t("pages.teams.home.copy.34", "当前状态")}
          value={formatOperationalStatusLabel(
            preview.latestRun?.completionStatus,
            preview.attention,
          )}
        />
        <TeamFact
          label={t("pages.teams.home.copy.35", "最近更新")}
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
        <TeamFact
          label={t("pages.teams.home.team.3", "Team 成员")}
          tooltip={preview.memberPreviewTooltip}
          value={preview.memberPreviewLabel}
        />
        <TeamFact
          label={t("pages.teams.home.copy.36", "关联服务")}
          tooltip={preview.serviceTooltip}
          value={preview.serviceLabel}
        />
      </div>

      <Space wrap>
        <Button
          onClick={() => history.push(preview.detailHref)}
          size="large"
          type="primary"
        >
          {t("pages.teams.home.copy.37", "测试或配置团队")}</Button>
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
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 20,
        boxShadow: token.boxShadowTertiary,
        display: "flex",
        flexDirection: "column",
        gap: 14,
        minWidth: 0,
        padding: 16,
      }}
    >
      <div
        style={{
          alignItems: "flex-start",
          display: "flex",
          flexWrap: "wrap",
          gap: 12,
          justifyContent: "space-between",
        }}
      >
        <div style={{ flex: "1 1 280px", minWidth: 0 }}>
          <Space size={[8, 8]} wrap style={{ marginBottom: 6 }}>
            <div style={{ flex: "1 1 auto", minWidth: 0 }}>
              <TeamTitle level={4} title={preview.title} />
            </div>
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
            title={preview.teamId}
            ellipsis={{ tooltip: preview.teamId }}
            style={{
              color: token.colorTextSecondary,
              display: "block",
              fontSize: 12,
              marginTop: 4,
            }}
          >
            {t("pages.teams.home.id.2", "ID：")}{preview.teamId}
          </Typography.Text>
        </div>

        <Space className="teams-home-roster-row-actions" wrap>
          <Button onClick={() => history.push(preview.detailHref)} type="primary">
            {t("pages.teams.home.copy.38", "测试或配置团队")}</Button>
        </Space>
      </div>

      <div
        style={{
          borderTop: `1px solid ${token.colorBorderSecondary}`,
          display: "grid",
          gap: 14,
          gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
          paddingTop: 14,
        }}
      >
        <TeamFact
          label={t("pages.teams.home.copy.39", "状态")}
          value={formatOperationalStatusLabel(
            preview.latestRun?.completionStatus,
            preview.attention,
          )}
        />
        <TeamFact label={t("pages.teams.home.copy.40", "更新")} value={formatShortTime(preview.updatedAt)} />
        <TeamFact
          label={t("pages.teams.home.copy.41", "成员")}
          tooltip={preview.memberPreviewTooltip}
          value={preview.memberPreviewLabel}
        />
        <TeamFact
          label={t("pages.teams.home.copy.42", "服务")}
          tooltip={preview.serviceTooltip}
          value={preview.serviceLabel}
        />
      </div>
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
  const serverResolvedScope = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );
  const authSessionIssue = React.useMemo(() => {
    if (!authSessionQuery.isError) {
      return "";
    }

    return describeError(
      authSessionQuery.error,
      t("pages.teams.home.copy.43", "登录状态暂时不可用，请刷新后重试。"),
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
  const queryScopeId =
    serverResolvedScope?.scopeId?.trim() === scopeId ? scopeId : "";
  const canLoadRoster = queryScopeId.length > 0;

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
    enabled: canLoadRoster,
    queryKey: ["teams", "members", queryScopeId],
    queryFn: () => studioApi.listMembers(queryScopeId),
    retry: false,
  });
  const teamsQuery = useQuery({
    enabled: canLoadRoster,
    queryKey: ["teams", "roster", queryScopeId],
    queryFn: () => studioApi.listTeams(queryScopeId),
    retry: false,
  });
  const servicesQuery = useQuery({
    enabled: canLoadRoster,
    queryKey: ["teams", "services", queryScopeId],
    queryFn: () =>
      scopeRuntimeApi.listServices(queryScopeId, {
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
  const memberRunQueries = useQueries({
    queries: runtimeTrackableMembers.map((member) => ({
      enabled: canLoadRoster && membersQuery.isSuccess,
      queryKey: ["teams", "member-runs", queryScopeId, member.memberId],
      queryFn: () =>
        scopeRuntimeApi.listMemberRuns(queryScopeId, member.memberId, {
          take: 12,
        }),
      retry: false,
    })),
  });
  const runsByMemberId = React.useMemo(
    () =>
      Object.fromEntries(
        runtimeTrackableMembers.map((member, index) => [
          trimOptional(member.memberId),
          memberRunQueries[index]?.data?.runs ?? [],
        ]),
      ) as Record<string, readonly ScopeServiceRunSummary[]>,
    [memberRunQueries, runtimeTrackableMembers],
  );
  const teamPreviews = React.useMemo(
    () =>
      studioTeams.map((team) =>
        buildTeamRosterPreview({
          members: membersByTeamId.get(team.teamId) ?? [],
          runsByMemberId,
          scopeId,
          services: servicesQuery.data ?? [],
          team,
        }),
      ),
    [
      membersByTeamId,
      runsByMemberId,
      queryScopeId,
      scopeId,
      servicesQuery.data,
      studioTeams,
    ],
  );
  const visibleTeamCount = teamPreviews.length;
  const actionableTeamCount = teamPreviews.filter(
    (preview) => preview.attention !== "healthy",
  ).length;
  const healthyTeamCount = teamPreviews.filter(
    (preview) => preview.attention === "healthy",
  ).length;
  const resolvedRosterView =
    manualRosterView ??
    (visibleTeamCount >= compactTeamRosterThreshold ? "list" : "cards");
  const useCompactRoster = resolvedRosterView === "list";
  const emptyRosterHint =
    canLoadRoster
      ? t("pages.teams.home.team.ai", "当前账号还没有创建任何 Team。创建后，这里会展示你的 AI 团队列表。")
      : t("pages.teams.home.copy.44", "当前登录状态还没有解析出可用的团队范围，请刷新后重试。");
  const partialIssues = [
    membersQuery.isError ? t("pages.teams.home.copy.45", "当前工作空间的成员清单暂时不可见。") : null,
    teamsQuery.isError ? t("pages.teams.home.team.roster.2", "当前工作空间的 Team roster 暂时不可见。") : null,
  ].filter((issue): issue is string => Boolean(issue));

  const titleNode = (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 14,
        }}
      >
        {t("pages.teams.home.aevatar.teams", "Aevatar / Teams")}</Typography.Text>
      <Typography.Title
        level={1}
        style={{
          margin: 0,
        }}
      >
        {t("pages.teams.home.ai", "我的 AI 团队")}</Typography.Title>
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
            {t("pages.teams.home.copy.46", "组建新团队")}</Button>
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
            title={t("pages.teams.home.copy.47", "当前登录状态还没有解析出可用的团队范围，请刷新后重试。")}
            type="info"
          />
        ) : null}

        {canLoadRoster && partialIssues.length > 0 ? (
          <Alert
            description={partialIssues.join(" ")}
            showIcon
            title={t("pages.teams.home.copy.48", "部分团队信号暂时不可见")}
            type="warning"
          />
        ) : null}

        {authSessionIssue ? (
          <Alert
            description={
              resolvedScope?.scopeId
                ? t("pages.teams.home.copy.49", "{value1} 已使用本地登录信息继续加载团队。", { value1: authSessionIssue })
                : authSessionIssue
            }
            showIcon
            title={
              resolvedScope?.scopeId
                ? t("pages.teams.home.copy.50", "当前登录态校验失败，已使用本地登录信息")
                : t("pages.teams.home.copy.51", "当前登录态校验失败")
            }
            type="warning"
          />
        ) : null}

        {canLoadRoster ? (
          <>
            <div
              style={{
                display: "grid",
                gap: 16,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <SummaryStatCard accent label={t("pages.teams.home.ai.team", "AI Team 总数")} value={visibleTeamCount} />
              <SummaryStatCard label={t("pages.teams.home.team.4", "待启动 Team")} value={actionableTeamCount} />
              <SummaryStatCard label={t("pages.teams.home.copy.52", "已有稳定运行")} value={healthyTeamCount} />
            </div>

            {teamsQuery.isLoading ? (
              <AevatarInspectorEmpty description={t("pages.teams.home.copy.53", "正在读取团队列表。")} />
            ) : teamsQuery.isError ? (
              <Alert
                showIcon
                title={t("pages.teams.home.copy.54", "团队列表暂时无法加载。")}
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
                      {t("pages.teams.home.copy.55", "团队列表")}</Typography.Title>
                    <Typography.Text type="secondary">
                      {t("pages.teams.home.team.5", "按 Team 聚合成员与最近运行信号，优先处理异常或待关注项。")}</Typography.Text>
                  </div>
                  {visibleTeamCount > 1 ? (
                    <Space.Compact>
                      <Tooltip title={t("pages.teams.home.copy.56", "卡片视图")}>
                        <Button
                          aria-label={t("pages.teams.home.copy.57", "切换到卡片视图")}
                          icon={<AppstoreOutlined />}
                          onClick={() => setManualRosterView("cards")}
                          style={{ height: 44, width: 44 }}
                          type={resolvedRosterView === "cards" ? "primary" : "default"}
                        />
                      </Tooltip>
                      <Tooltip title={t("pages.teams.home.copy.58", "列表视图")}>
                        <Button
                          aria-label={t("pages.teams.home.copy.59", "切换到列表视图")}
                          icon={<BarsOutlined />}
                          onClick={() => setManualRosterView("list")}
                          style={{ height: 44, width: 44 }}
                          type={resolvedRosterView === "list" ? "primary" : "default"}
                        />
                      </Tooltip>
                    </Space.Compact>
                  ) : null}
                </div>
                {useCompactRoster ? (
                  <ul
                    aria-label={t("pages.teams.home.copy.60", "团队紧凑视图")}
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
                    aria-label={t("pages.teams.home.copy.61", "团队卡片视图")}
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
                  {t("pages.teams.home.copy.62", "组建新团队")}</Button>
              </Empty>
            )}

          </>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

export default TeamsHomePage;
