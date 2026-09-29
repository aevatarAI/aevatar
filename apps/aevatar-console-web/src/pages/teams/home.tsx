import {
  AppstoreOutlined,
  BarsOutlined,
  PlusOutlined,
  TeamOutlined,
} from "@ant-design/icons";
import { useQueries, useQuery } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Skeleton,
  Space,
  Typography,
  theme,
} from "antd";
import React from "react";
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { loadRestorableAuthSession } from "@/shared/auth/session";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  buildTeamMemberWorkflowStudioHref,
} from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import type { ScopeServiceRunSummary } from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  formatStudioMemberLifecycleStage,
  normalizeStudioTeamLifecycleStage,
  type StudioMemberSummary,
  type StudioTeamSummary,
} from "@/shared/studio/models";
import { AevatarPageShell } from "@/shared/ui/aevatarPageShells";
import type { AevatarBreadcrumbItem } from "@/shared/ui/aevatarPageShells";
import { describeError } from "@/shared/ui/errorText";
import { resolveStudioScopeContext } from "../scopes/components/resolvedScope";
import {
  buildTeamCreateRoute,
  buildTeamWorkspaceRoute,
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
  readonly membersHref: string;
  readonly memberQuickAction: TeamMemberQuickAction | null;
  readonly memberPreviewLabel: string;
  readonly memberPreviewTooltip?: string;
  readonly serviceLabel: string;
  readonly serviceTooltip?: string;
  readonly team: StudioTeamSummary;
  readonly teamId: string;
  readonly title: string;
  readonly updatedAt: string | null;
};

type TeamMemberQuickActionKind =
  | "create-member"
  | "manage-members";

type TeamMemberQuickAction = {
  readonly href: string;
  readonly kind: TeamMemberQuickActionKind;
  readonly label: string;
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
      return t("pages.teams.home.copy", "Needs attention");
    case "failed":
    case "error":
      return t("pages.teams.home.copy.2", "Abnormal");
    case "completed":
      return t("pages.teams.home.copy.3", "Completed");
    default:
      return trimOptional(status) || t("pages.teams.home.copy.4", "Unknown");
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
      return t("pages.teams.home.copy.5", "Running");
    case "waiting":
      return t("pages.teams.home.copy.6", "Needs attention");
    case "failed":
      return t("pages.teams.home.copy.7", "Abnormal");
    case "draft":
      return t("pages.teams.home.copy.8", "Drafting");
    case "no-bound-service":
      return t("pages.teams.home.copy.9", "Waiting for bind");
    case "no-recent-runs":
      return t("pages.teams.home.copy.10", "Waiting to run");
    default:
      return t("pages.teams.home.copy.11", "Unknown");
  }
}

function formatAttentionLabel(attention: TeamOperationalAttention): string {
  switch (attention) {
    case "failed":
      return t("pages.teams.home.copy.12", "Pending");
    case "waiting":
      return t("pages.teams.home.copy.13", "Needs attention");
    case "healthy":
      return t("pages.teams.home.copy.14", "Running");
    case "draft":
      return t("pages.teams.home.copy.15", "Drafting");
    case "no-bound-service":
      return t("pages.teams.home.copy.16", "Waiting for bind");
    case "no-recent-runs":
      return t("pages.teams.home.copy.17", "Waiting to run");
    default:
      return t("pages.teams.home.copy.18", "Waiting for confirmation");
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

const SkeletonLine: React.FC<{
  readonly height?: number;
  readonly width: number | string;
}> = ({ height = 16, width }) => (
  <Skeleton.Input
    active
    size="small"
    style={{
      borderRadius: 999,
      height,
      maxWidth: "100%",
      width,
    }}
  />
);

const SummaryStatSkeletonCard: React.FC = () => {
  const { token } = theme.useToken();

  return (
    <div
      data-testid="teams-home-summary-skeleton"
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 22,
        boxShadow: token.boxShadowTertiary,
        display: "flex",
        flexDirection: "column",
        gap: 12,
        minHeight: 104,
        padding: 18,
      }}
    >
      <SkeletonLine height={30} width={68} />
      <SkeletonLine width="72%" />
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
        <AevatarTooltip title={tooltip || value}>{renderedValue}</AevatarTooltip>
      ) : (
        renderedValue
      )}
      <Typography.Text style={{ fontSize: 13 }} type="secondary">
        {label}
      </Typography.Text>
    </div>
  );
};

const TeamRosterCardSkeleton: React.FC = () => {
  const { token } = theme.useToken();

  return (
    <article
      data-testid="teams-home-card-skeleton"
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 24,
        boxShadow: token.boxShadowTertiary,
        display: "flex",
        flexDirection: "column",
        gap: 16,
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
        <div
          style={{
            display: "flex",
            flex: "1 1 auto",
            flexDirection: "column",
            gap: 10,
            minWidth: 0,
          }}
        >
          <SkeletonLine height={26} width="62%" />
          <SkeletonLine width="88%" />
        </div>
        <Skeleton.Button active shape="round" size="small" style={{ width: 92 }} />
      </div>
      <SkeletonLine width="38%" />
      <div
        style={{
          borderTop: `1px solid ${token.colorBorderSecondary}`,
          display: "grid",
          gap: 14,
          gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
          paddingTop: 14,
        }}
      >
        <SkeletonLine width="78%" />
        <SkeletonLine width="68%" />
        <SkeletonLine width="74%" />
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
        <SkeletonLine width="80%" />
        <SkeletonLine width="72%" />
      </div>
      <Space size={8} wrap>
        <Skeleton.Button active shape="round" style={{ width: 132 }} />
        <Skeleton.Button active shape="round" style={{ width: 108 }} />
        <Skeleton.Button active shape="round" style={{ width: 126 }} />
      </Space>
    </article>
  );
};

const summaryStatSkeletonKeys = ["total", "actionable", "healthy"] as const;
const rosterCardSkeletonKeys = ["primary", "secondary", "tertiary"] as const;

const TeamsHomeLoadingSkeleton: React.FC = () => (
  <section
    aria-busy="true"
    aria-label={t("pages.teams.home.copy.53", "Reading the team list.")}
    data-testid="teams-home-skeleton"
    role="status"
    style={{ display: "flex", flexDirection: "column", gap: 20 }}
  >
    <div
      style={{
        display: "grid",
        gap: 16,
        gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
      }}
    >
      {summaryStatSkeletonKeys.map((key) => (
        <SummaryStatSkeletonCard key={key} />
      ))}
    </div>

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
          gap: 8,
          minWidth: 240,
        }}
      >
        <SkeletonLine height={22} width={128} />
        <SkeletonLine width={360} />
      </div>
      <Space.Compact>
        <Skeleton.Button active style={{ height: 44, width: 44 }} />
        <Skeleton.Button active style={{ height: 44, width: 44 }} />
      </Space.Compact>
    </div>

    <ul
      aria-hidden="true"
      style={{
        display: "grid",
        gap: 16,
        gridTemplateColumns: "repeat(auto-fit, minmax(340px, 1fr))",
        listStyle: "none",
        margin: 0,
        padding: 0,
      }}
    >
      {rosterCardSkeletonKeys.map((key) => (
        <li key={key}>
          <TeamRosterCardSkeleton />
        </li>
      ))}
    </ul>
  </section>
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

function isWorkflowMember(member: StudioMemberSummary | null | undefined): boolean {
  return trimOptional(member?.implementationKind).toLowerCase() === "workflow";
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
    (trimOptional(input.member.lastBoundRevisionId) ? t("pages.teams.home.copy.19", "Bound, awaiting confirmation") : t("pages.teams.home.copy.20", "Unbound"));
  const title = pickMeaningfulLabel(input.member.displayName, input.member.memberId) || t("pages.teams.home.copy.21", "Unnamed member");

  let attention: TeamOperationalAttention = "draft";
  let attentionDetail = t("pages.teams.home.copy.22", "The current member is still in the {value1} stage.", { value1: formatStudioMemberLifecycleStage(input.member.lifecycleStage) });

  if (latestRun && isFailedRun(latestRun)) {
    attention = "failed";
    attentionDetail =
      trimOptional(latestRun.lastError) || t("pages.teams.home.copy.23", "The latest member run is abnormal.");
  } else if (latestRun && isWaitingRun(latestRun)) {
    attention = "waiting";
    attentionDetail =
      trimOptional(latestRun.lastError) || t("pages.teams.home.copy.24", "The latest member run is waiting for a human or external signal.");
  } else if (latestRun && isSuccessfulRun(latestRun)) {
    attention = "healthy";
    attentionDetail = t("pages.teams.home.copy.25", "The latest member run is healthy; open details to continue.");
  } else if (serviceId || matchedService) {
    attention = "no-recent-runs";
    attentionDetail = t("pages.teams.home.copy.26", "The member has been bound to a service. Next: open team details and test the team to generate the first visible run.");
  } else if (
    trimOptional(input.member.lastBoundRevisionId) ||
    input.member.lifecycleStage === "bind_ready"
  ) {
    attention = "no-bound-service";
    attentionDetail = t("pages.teams.home.copy.27", "The current member is ready to bind, but it does not have a stable member invoke entry yet.");
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
  const memberCount =
    input.team.memberCount > 0 ? input.team.memberCount : input.members.length;
  const entryMemberId = trimOptional(input.team.entryMemberId);
  const entryMemberPreview = entryMemberId
    ? memberPreviews.find((preview) => preview.memberId === entryMemberId)
    : undefined;
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
  const runtimeSignalPreview = entryMemberPreview ?? mostImportantMemberPreview;
  const latestRun = runtimeSignalPreview?.latestRun ?? null;
  const hasWorkflowMember = sortedMembers.some(isWorkflowMember);
  const memberQuickAction: TeamMemberQuickAction | null = hasWorkflowMember
    ? null
    : memberCount === 0
      ? {
          href: buildTeamMemberWorkflowStudioHref({
            mode: "create-member",
            scopeId: input.scopeId,
            teamId: input.team.teamId,
          }),
          kind: "create-member",
          label: t("teams.home.actions.createWorkflowMember", "Create workflow member"),
        }
      : {
          href: buildTeamDetailHref({
            scopeId: input.scopeId,
            tab: "members",
            teamId: input.team.teamId,
          }),
          kind: "manage-members",
          label: t("teams.home.actions.manageMembers", "Manage members"),
        };
  const firstMemberLabel = pickMeaningfulLabel(
    sortedMembers[0]?.displayName,
    sortedMembers[0]?.memberId,
  );
  const memberPreviewLabel =
    memberCount > 0
      ? firstMemberLabel
        ? memberCount > 1
          ? t("pages.teams.home.copy.28", "{value1} and {value2} other members", { value1: firstMemberLabel, value2: memberCount })
          : firstMemberLabel
        : t("pages.teams.home.copy.29", "{value1} members", { value1: memberCount })
      : t("pages.teams.home.copy.30", "No members yet");
  const serviceLabels = memberPreviews
    .map((preview) => preview.serviceLabel)
    .filter((label) => label && label !== t("pages.teams.home.copy.31", "Unbound"));
  const uniqueServiceLabels = Array.from(new Set(serviceLabels));
  const memberPreviewTooltip =
    sortedMembers.length > 0
      ? sortedMembers
          .map((member) =>
            pickMeaningfulLabel(member.displayName, member.memberId) || t("pages.teams.home.copy.32", "Unnamed member"),
          )
          .join(" / ")
      : undefined;
  const serviceTooltip =
    uniqueServiceLabels.length > 0 ? uniqueServiceLabels.join(" / ") : undefined;
  const primaryMemberPreview =
    entryMemberPreview ??
    memberPreviews.find((preview) => preview.serviceId) ??
    memberPreviews[0] ??
    null;
  const detailHref = buildTeamDetailHref({
    memberId: primaryMemberPreview?.memberId || undefined,
    runId: latestRun?.runId || undefined,
    scopeId: input.scopeId,
    serviceId: primaryMemberPreview?.serviceId || undefined,
    teamId: input.team.teamId,
  });
  const membersHref = buildTeamDetailHref({
    scopeId: input.scopeId,
    tab: "members",
    teamId: input.team.teamId,
  });

  const attention: TeamOperationalAttention =
    runtimeSignalPreview?.attention ?? "draft";
  const attentionDetail =
    runtimeSignalPreview?.attentionDetail ??
    t("pages.teams.home.team", "This team has no members yet. Next: add an entry member, then test the team.");

  return {
    attention,
    attentionDetail,
    detailHref,
    latestRun,
    membersHref,
    memberQuickAction,
    memberPreviewLabel,
    memberPreviewTooltip,
    serviceLabel:
      uniqueServiceLabels.length > 0
        ? uniqueServiceLabels.slice(0, 2).join(" / ")
        : t("pages.teams.home.copy.33", "No bound service yet"),
    serviceTooltip,
    team: input.team,
    teamId: input.team.teamId,
    title: pickMeaningfulLabel(input.team.displayName, input.team.teamId) || t("pages.teams.home.team.2", "Unnamed team"),
    updatedAt:
      latestRun?.lastUpdatedAt ||
      runtimeSignalPreview?.updatedAt ||
      input.team.updatedAt ||
      null,
  };
}

function renderMemberQuickActionIcon(
  kind: TeamMemberQuickActionKind,
): React.ReactNode {
  switch (kind) {
    case "create-member":
      return <PlusOutlined />;
    case "manage-members":
      return <BarsOutlined />;
  }
}

const TeamRosterActionGroup: React.FC<{
  readonly large?: boolean;
  readonly preview: TeamRosterPreview;
}> = ({ large = false, preview }) => {
  const buttonSize = "middle";
  const memberQuickAction = preview.memberQuickAction;
  const { token } = theme.useToken();
  const showViewMembersAction =
    !memberQuickAction || memberQuickAction.href !== preview.membersHref;
  const buttonStyle: React.CSSProperties = {
    borderRadius: 999,
    fontSize: large ? 13 : 12,
    fontWeight: 600,
    height: large ? 34 : 30,
    lineHeight: "20px",
    paddingInline: large ? 10 : 8,
  };
  const renderSeparator = () => (
    <span
      aria-hidden="true"
      style={{
        alignSelf: "center",
        background: token.colorBorderSecondary,
        display: "inline-block",
        height: large ? 15 : 14,
        width: 1,
      }}
    />
  );

  return (
    <Space
      separator={renderSeparator()}
      size={large ? 4 : 2}
      style={{
        background: token.colorFillQuaternary,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 999,
        padding: large ? 3 : 2,
        width: "fit-content",
      }}
      wrap
    >
      {memberQuickAction ? (
        <Button
          icon={renderMemberQuickActionIcon(memberQuickAction.kind)}
          onClick={() => history.push(memberQuickAction.href)}
          size={buttonSize}
          style={buttonStyle}
          type="text"
        >
          {memberQuickAction.label}
        </Button>
      ) : null}
      <Button
        icon={<TeamOutlined />}
        onClick={() => history.push(preview.detailHref)}
        size={buttonSize}
        style={buttonStyle}
        type="text"
      >
        {t("teams.home.actions.viewTeam", "View team")}
      </Button>
      {showViewMembersAction ? (
        <Button
          icon={<BarsOutlined />}
          onClick={() => history.push(preview.membersHref)}
          size={buttonSize}
          style={buttonStyle}
          type="text"
        >
          {t("teams.home.actions.viewMembers", "View members")}
        </Button>
      ) : null}
    </Space>
  );
};

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
          <AevatarTooltip title={preview.attentionDetail}>
            <Typography.Paragraph
              ellipsis={{ rows: 1 }}
              style={{
                color: token.colorTextSecondary,
                fontSize: 14,
                marginBottom: 0,
                marginTop: 6,
              }}
            >
              {preview.attentionDetail}
            </Typography.Paragraph>
          </AevatarTooltip>
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
          label={t("pages.teams.home.copy.34", "Current status")}
          value={formatOperationalStatusLabel(
            preview.latestRun?.completionStatus,
            preview.attention,
          )}
        />
        <TeamFact
          label={t("pages.teams.home.copy.35", "Latest update")}
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
          label={t("pages.teams.home.team.3", "Team members")}
          tooltip={preview.memberPreviewTooltip}
          value={preview.memberPreviewLabel}
        />
        <TeamFact
          label={t("pages.teams.home.copy.36", "Related service")}
          tooltip={preview.serviceTooltip}
          value={preview.serviceLabel}
        />
      </div>

      <TeamRosterActionGroup large preview={preview} />
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
          <AevatarTooltip title={preview.attentionDetail}>
            <Typography.Paragraph
              ellipsis={{ rows: 1 }}
              style={{
                color: token.colorTextSecondary,
                fontSize: 13,
                marginBottom: 0,
                marginTop: 0,
              }}
            >
              {preview.attentionDetail}
            </Typography.Paragraph>
          </AevatarTooltip>
        </div>

        <div className="teams-home-roster-row-actions">
          <TeamRosterActionGroup preview={preview} />
        </div>
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
          label={t("pages.teams.home.copy.39", "Status")}
          value={formatOperationalStatusLabel(
            preview.latestRun?.completionStatus,
            preview.attention,
          )}
        />
        <TeamFact label={t("pages.teams.home.copy.40", "Update")} value={formatShortTime(preview.updatedAt)} />
        <TeamFact
          label={t("pages.teams.home.copy.41", "Members")}
          tooltip={preview.memberPreviewTooltip}
          value={preview.memberPreviewLabel}
        />
        <TeamFact
          label={t("pages.teams.home.copy.42", "Service")}
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
      t("pages.teams.home.copy.43", "Login status is temporarily unavailable. Refresh and try again."),
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
  const scopeAuthResolving =
    scopeId.length > 0 && !canLoadRoster && authSessionQuery.isLoading;

  React.useEffect(() => {
    if (!scopeId) {
      return;
    }

    const nextPath = buildTeamWorkspaceRoute(scopeId);
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
  const visibleStudioTeams = React.useMemo(
    () =>
      studioTeams.filter(
        (team) =>
          normalizeStudioTeamLifecycleStage(team.lifecycleStage) !== "archived",
      ),
    [studioTeams],
  );
  const membersByTeamId = React.useMemo(
    () => groupMembersByTeamId(studioMembers),
    [studioMembers],
  );
  const runtimeTrackableEntryMemberServices = React.useMemo(() => {
    const membersById = new Map(
      studioMembers
        .map((member) => [trimOptional(member.memberId), member] as const)
        .filter(([memberId]) => memberId.length > 0),
    );
    const result: Array<{
      readonly memberId: string;
      readonly serviceId: string;
    }> = [];

    visibleStudioTeams.forEach((team) => {
      const entryMemberId = trimOptional(team.entryMemberId);
      if (!entryMemberId) {
        return;
      }

      const member = membersById.get(entryMemberId);
      const serviceId = trimOptional(member?.publishedServiceId);
      if (!member || !serviceId) {
        return;
      }

      result.push({
        memberId: entryMemberId,
        serviceId,
      });
    });

    return result;
  }, [studioMembers, visibleStudioTeams]);
  const runtimeTrackableServiceIds = React.useMemo(
    () =>
      Array.from(
        new Set(
          runtimeTrackableEntryMemberServices.map((entry) => entry.serviceId),
        ),
      ),
    [runtimeTrackableEntryMemberServices],
  );
  const memberRunQueries = useQueries({
    queries: runtimeTrackableServiceIds.map((serviceId) => ({
      enabled: canLoadRoster && membersQuery.isSuccess,
      queryKey: ["teams", "service-runs", queryScopeId, serviceId],
      queryFn: () =>
        scopeRuntimeApi.listServiceRuns(queryScopeId, serviceId, {
          take: 1,
        }),
      retry: false,
    })),
  });
  const runsByMemberId = React.useMemo(
    () => {
      const runsByServiceId = Object.fromEntries(
        runtimeTrackableServiceIds.map((serviceId, index) => [
          serviceId,
          memberRunQueries[index]?.data?.runs ?? [],
        ]),
      ) as Record<string, readonly ScopeServiceRunSummary[]>;

      return Object.fromEntries(
        runtimeTrackableEntryMemberServices.map((entry) => [
          entry.memberId,
          runsByServiceId[entry.serviceId] ?? [],
        ]),
      ) as Record<string, readonly ScopeServiceRunSummary[]>;
    },
    [
      memberRunQueries,
      runtimeTrackableEntryMemberServices,
      runtimeTrackableServiceIds,
    ],
  );
  const teamPreviews = React.useMemo(
    () =>
      visibleStudioTeams.map((team) =>
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
      visibleStudioTeams,
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
  const rosterBootstrapLoading =
    scopeAuthResolving || (!teamsQuery.isError && teamsQuery.isLoading);
  const emptyRosterHint =
    canLoadRoster
      ? t("pages.teams.home.team.ai", "This account has not created any teams yet. Your AI team list will appear here after you create one.")
      : t("pages.teams.home.copy.44", "The current login status has not resolved an available team scope. Refresh and try again.");
  const partialIssues = [
    membersQuery.isError ? t("pages.teams.home.copy.45", "The member list for the current workspace is temporarily unavailable.") : null,
    teamsQuery.isError ? t("pages.teams.home.team.roster.2", "The team roster for the current workspace is temporarily unavailable.") : null,
  ].filter((issue): issue is string => Boolean(issue));

  const titleNode = (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <Typography.Title
        level={1}
        style={{
          margin: 0,
        }}
      >
        {t("pages.teams.home.ai", "My AI teams")}</Typography.Title>
    </div>
  );
  const breadcrumbItems: AevatarBreadcrumbItem[] = [
    {
      current: true,
      title: t("teams.detail.breadcrumb.teams", "Teams"),
    },
  ];

  return (
    <AevatarPageShell
      breadcrumbItems={breadcrumbItems}
      extra={
        <Space wrap>
          <Button
            icon={<PlusOutlined />}
            onClick={() =>
              history.push(buildTeamCreateRoute(scopeId))
            }
            style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
            type="primary"
          >
            {t("pages.teams.home.copy.46", "Create team")}</Button>
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
            title={t("pages.teams.home.copy.47", "The current login status has not resolved an available team scope. Refresh and try again.")}
            type="info"
          />
        ) : null}

        {canLoadRoster && partialIssues.length > 0 ? (
          <Alert
            description={partialIssues.join(" ")}
            showIcon
            title={t("pages.teams.home.copy.48", "Some team signals are temporarily unavailable")}
            type="warning"
          />
        ) : null}

        {authSessionIssue ? (
          <Alert
            description={
              resolvedScope?.scopeId
                ? t("pages.teams.home.copy.49", "{value1} continued loading teams with local login information.", { value1: authSessionIssue })
                : authSessionIssue
            }
            showIcon
            title={
              resolvedScope?.scopeId
                ? t("pages.teams.home.copy.50", "Current login verification failed; local login information was used")
                : t("pages.teams.home.copy.51", "Current login verification failed")
            }
            type="warning"
          />
        ) : null}

        {canLoadRoster || scopeAuthResolving ? (
          rosterBootstrapLoading ? (
            <TeamsHomeLoadingSkeleton />
          ) : teamsQuery.isError ? (
            <Alert
              showIcon
              title={t("pages.teams.home.copy.54", "The team list cannot be loaded right now.")}
              type="error"
            />
          ) : (
            <>
            <div
              style={{
                display: "grid",
                gap: 16,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <SummaryStatCard accent label={t("pages.teams.home.ai.team", "Total AI teams")} value={visibleTeamCount} />
              <SummaryStatCard label={t("pages.teams.home.team.4", "Teams needing action")} value={actionableTeamCount} />
              <SummaryStatCard label={t("pages.teams.home.copy.52", "Stable runs exist")} value={healthyTeamCount} />
            </div>

            {teamPreviews.length > 0 ? (
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
                      {t("pages.teams.home.copy.55", "Team list")}</Typography.Title>
                    <Typography.Text type="secondary">
                      {t("pages.teams.home.team.5", "Aggregate members and recent run signals by team, prioritizing abnormal or attention-needed items.")}</Typography.Text>
                  </div>
                  {visibleTeamCount > 1 ? (
                    <Space.Compact>
                      <AevatarTooltip title={t("pages.teams.home.copy.56", "Card view")}>
                        <Button
                          aria-label={t("pages.teams.home.copy.57", "Switch to card view")}
                          icon={<AppstoreOutlined />}
                          onClick={() => setManualRosterView("cards")}
                          style={{ height: 44, width: 44 }}
                          type={resolvedRosterView === "cards" ? "primary" : "default"}
                        />
                      </AevatarTooltip>
                      <AevatarTooltip title={t("pages.teams.home.copy.58", "List view")}>
                        <Button
                          aria-label={t("pages.teams.home.copy.59", "Switch to list view")}
                          icon={<BarsOutlined />}
                          onClick={() => setManualRosterView("list")}
                          style={{ height: 44, width: 44 }}
                          type={resolvedRosterView === "list" ? "primary" : "default"}
                        />
                      </AevatarTooltip>
                    </Space.Compact>
                  ) : null}
                </div>
                {useCompactRoster ? (
                  <ul
                    aria-label={t("pages.teams.home.copy.60", "Team compact view")}
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
                    aria-label={t("pages.teams.home.copy.61", "Team card view")}
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
                    history.push(buildTeamCreateRoute(scopeId))
                  }
                  type="primary"
                >
                  {t("pages.teams.home.copy.62", "Create team")}</Button>
              </Empty>
            )}
            </>
          )
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

export default TeamsHomePage;
