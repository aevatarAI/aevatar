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
import { useIntl } from "@umijs/max";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { loadRestorableAuthSession } from "@/shared/auth/session";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import { buildTeamDetailHref } from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import type { ScopeServiceRunSummary } from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import type {
  StudioMemberLifecycleStage,
  StudioMemberSummary,
  StudioTeamSummary,
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

type TeamsHomeFormatMessage = ReturnType<typeof useIntl>["formatMessage"];

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

function formatTeamHomeMessage(
  formatMessage: TeamsHomeFormatMessage,
  id: string,
  defaultMessage: string,
  values?: Record<string, string | number>,
): string {
  return formatMessage({ defaultMessage, id }, values);
}

function formatMemberLifecycleStageLabel(
  formatMessage: TeamsHomeFormatMessage,
  value: StudioMemberLifecycleStage | string | null | undefined,
): string {
  switch (trimOptional(value).toLowerCase()) {
    case "created":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.lifecycle.created",
        t("pages.teams.home.created", "Created"),
      );
    case "build_ready":
    case "buildready":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.lifecycle.buildReady",
        t("pages.teams.home.buildable", "Buildable"),
      );
    case "bind_ready":
    case "bindready":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.lifecycle.bindReady",
        t("pages.teams.home.callable", "callable"),
      );
    default:
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.lifecycle.unknown",
        t("pages.teams.home.status.unknown", "Status unknown"),
      );
  }
}

function formatRunStatusLabel(
  formatMessage: TeamsHomeFormatMessage,
  status: string | null | undefined,
): string {
  switch (trimOptional(status).toLowerCase()) {
    case "waiting":
    case "waiting_approval":
    case "waiting_signal":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.needsAttention",
        t("pages.teams.home.to.be.noticed", "To be noticed"),
      );
    case "failed":
    case "error":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.failed",
        t("pages.teams.home.abnormal", "abnormal"),
      );
    case "completed":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.stable",
        t("pages.teams.home.stablize", "Stablize"),
      );
    default:
      return (
        trimOptional(status) ||
        formatTeamHomeMessage(
          formatMessage,
          "teams.home.status.unknown",
          t("pages.teams.home.unknown", "unknown"),
        )
      );
  }
}

function formatOperationalStatusLabel(
  formatMessage: TeamsHomeFormatMessage,
  status: string | null | undefined,
  attention: TeamOperationalAttention,
): string {
  const normalizedStatus = trimOptional(status);
  if (normalizedStatus) {
    return formatRunStatusLabel(formatMessage, normalizedStatus);
  }

  switch (attention) {
    case "healthy":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.running",
        t("pages.teams.home.running", "Running"),
      );
    case "waiting":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.needsAttention",
        t("pages.teams.home.to.be.noticed.2", "To be noticed"),
      );
    case "failed":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.failed",
        t("pages.teams.home.abnormal.2", "abnormal"),
      );
    case "draft":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.draft",
        t("pages.teams.home.in.draft", "In draft"),
      );
    case "no-bound-service":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.bindingPending",
        t("pages.teams.home.to.be.bound", "To be bound"),
      );
    case "no-recent-runs":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.runPending",
        t("pages.teams.home.to.be.run", "To be run"),
      );
    default:
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.status.unknown",
        t("pages.teams.home.unknown.2", "unknown"),
      );
  }
}

function formatAttentionLabel(
  formatMessage: TeamsHomeFormatMessage,
  attention: TeamOperationalAttention,
): string {
  switch (attention) {
    case "failed":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.attention.failed",
        t("pages.teams.home.pending", "Pending"),
      );
    case "waiting":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.attention.waiting",
        t("pages.teams.home.to.be.noticed.3", "To be noticed"),
      );
    case "healthy":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.attention.healthy",
        t("pages.teams.home.running.2", "Running"),
      );
    case "draft":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.attention.draft",
        t("pages.teams.home.in.draft.2", "In draft"),
      );
    case "no-bound-service":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.attention.noBoundService",
        t("pages.teams.home.to.be.bound.2", "To be bound"),
      );
    case "no-recent-runs":
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.attention.noRecentRuns",
        t("pages.teams.home.to.be.run.2", "To be run"),
      );
    default:
      return formatTeamHomeMessage(
        formatMessage,
        "teams.home.attention.unknown",
        t("pages.teams.home.to.be.confirmed", "To be confirmed"),
      );
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
  readonly formatMessage: TeamsHomeFormatMessage;
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
    (trimOptional(input.member.lastBoundRevisionId)
      ? formatTeamHomeMessage(
          input.formatMessage,
          "teams.home.service.boundPending",
          t("pages.teams.home.already.bound.to.be", "Already bound to be confirmed"),
        )
      : formatTeamHomeMessage(
          input.formatMessage,
          "teams.home.service.unbound",
          t("pages.teams.home.not.bound", "Not bound"),
        ));
  const title =
    pickMeaningfulLabel(input.member.displayName, input.member.memberId) ||
    formatTeamHomeMessage(
      input.formatMessage,
      "teams.home.member.unnamed",
      t("pages.teams.home.unnamed.member", "unnamed member"),
    );

  let attention: TeamOperationalAttention = "draft";
  let attentionDetail = formatTeamHomeMessage(
    input.formatMessage,
    "teams.home.attentionDetail.memberStage",
    t("pages.teams.home.the.current.member.is", "The current member is still in {stage}."),
    {
      stage: formatMemberLifecycleStageLabel(
        input.formatMessage,
        input.member.lifecycleStage,
      ),
    },
  );

  if (latestRun && isFailedRun(latestRun)) {
    attention = "failed";
    attentionDetail = trimOptional(latestRun.lastError) || formatTeamHomeMessage(
      input.formatMessage,
      "teams.home.attentionDetail.memberFailed",
      t("pages.teams.home.the.latest.member.operation", "The latest member operation was in an abnormal state."),
    );
  } else if (latestRun && isWaitingRun(latestRun)) {
    attention = "waiting";
    attentionDetail = trimOptional(latestRun.lastError) || formatTeamHomeMessage(
      input.formatMessage,
      "teams.home.attentionDetail.memberWaiting",
      t("pages.teams.home.the.last.member.run", "The last member run was waiting for a manual or external signal."),
    );
  } else if (latestRun && isSuccessfulRun(latestRun)) {
    attention = "healthy";
    attentionDetail = formatTeamHomeMessage(
      input.formatMessage,
      "teams.home.attentionDetail.memberHealthy",
      t("pages.teams.home.the.latest.member.operation.2", "The latest member operation is normal, you can continue to enter the details to view."),
    );
  } else if (serviceId || matchedService) {
    attention = "no-recent-runs";
    attentionDetail = formatTeamHomeMessage(
      input.formatMessage,
      "teams.home.attentionDetail.memberNoRecentRuns",
      t("pages.teams.home.the.member.has.been", "The member has been bound to the service and has no recent running records."),
    );
  } else if (
    trimOptional(input.member.lastBoundRevisionId) ||
    input.member.lifecycleStage === "bind_ready"
  ) {
    attention = "no-bound-service";
    attentionDetail = formatTeamHomeMessage(
      input.formatMessage,
      "teams.home.attentionDetail.memberNoBoundService",
      t("pages.teams.home.the.current.member.is.2", "The current member is ready to be bound, but there is no stable member calling entrance yet."),
    );
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
  readonly formatMessage: TeamsHomeFormatMessage;
  readonly members: readonly StudioMemberSummary[];
  readonly runsByMemberId: Readonly<Record<string, readonly ScopeServiceRunSummary[]>>;
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
  readonly team: StudioTeamSummary;
}): TeamRosterPreview {
  const memberPreviews = input.members.map((member) =>
    buildMemberRosterPreview({
      formatMessage: input.formatMessage,
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
          ? formatTeamHomeMessage(
              input.formatMessage,
              "teams.home.member.previewWithMore",
              t("pages.teams.home.and.members", "{name} and {count} members"),
              {
                count: memberCount,
                name: firstMemberLabel,
              },
            )
          : firstMemberLabel
        : formatTeamHomeMessage(
            input.formatMessage,
            "teams.home.member.count",
            t("pages.teams.home.members", "{count} members"),
            {
              count: memberCount,
            },
          )
      : formatTeamHomeMessage(
          input.formatMessage,
          "teams.home.member.none",
          t("pages.teams.home.no.members.yet", "No members yet"),
        );
  const serviceLabels = memberPreviews
    .map((preview) => preview.serviceLabel)
    .filter(
      (label) =>
        label &&
        label !==
          formatTeamHomeMessage(
            input.formatMessage,
            "teams.home.service.unbound",
            t("pages.teams.home.not.bound.2", "Not bound"),
          ),
    );
  const uniqueServiceLabels = Array.from(new Set(serviceLabels));
  const memberPreviewTooltip =
    sortedMembers.length > 0
      ? sortedMembers
          .map((member) =>
            pickMeaningfulLabel(member.displayName, member.memberId) ||
            formatTeamHomeMessage(
              input.formatMessage,
              "teams.home.member.unnamed",
              t("pages.teams.home.unnamed.member.2", "unnamed member"),
            ),
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
  let attentionDetail = formatTeamHomeMessage(
    input.formatMessage,
    "teams.home.attentionDetail.teamNoMembers",
    t("pages.teams.home.backend.fact.already.exists", "A backend fact already exists for this team, but no members have been assigned yet."),
  );
  if (input.team.lifecycleStage === "archived") {
    attention = "draft";
    attentionDetail = formatTeamHomeMessage(
      input.formatMessage,
      "teams.home.attentionDetail.teamArchived",
      t("pages.teams.home.this.team.is.archived", "This team is archived and only its backend roster facts remain in the list."),
    );
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
        : formatTeamHomeMessage(
            input.formatMessage,
            "teams.home.service.none",
            t("pages.teams.home.no.binding.service.yet", "No binding service yet"),
          ),
    serviceTooltip,
    team: input.team,
    teamId: input.team.teamId,
    title:
      pickMeaningfulLabel(input.team.displayName, input.team.teamId) ||
      formatTeamHomeMessage(
        input.formatMessage,
        "teams.home.team.unnamed",
        t("pages.teams.home.unnamed.team", "Unnamed team"),
      ),
    updatedAt:
      latestRun?.lastUpdatedAt ||
      mostImportantMemberPreview?.updatedAt ||
      input.team.updatedAt ||
      null,
  };
}

const TeamRosterCard: React.FC<{
  readonly formatMessage: TeamsHomeFormatMessage;
  readonly preview: TeamRosterPreview;
}> = ({ formatMessage, preview }) => {
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
          {formatAttentionLabel(formatMessage, preview.attention)}
        </span>
      </div>

      <Typography.Text
        title={preview.teamId}
        ellipsis={{ tooltip: preview.teamId }}
        style={{
          color: token.colorTextSecondary,
          display: "block",
          fontSize: 13,
        }}
      >
        {formatTeamHomeMessage(
          formatMessage,
          "teams.home.team.identity",
          t("pages.teams.home.team.id", "team ID: {teamId}"),
          {
            teamId: preview.teamId,
          },
        )}
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
          label={formatTeamHomeMessage(
            formatMessage,
            "teams.home.facts.currentStatus",
            t("pages.teams.home.current.status", "Current status"),
          )}
          value={formatOperationalStatusLabel(
            formatMessage,
            preview.latestRun?.completionStatus,
            preview.attention,
          )}
        />
        <TeamFact
          label={formatTeamHomeMessage(
            formatMessage,
            "teams.home.facts.latestUpdate",
            t("pages.teams.home.latest.updates", "Latest updates"),
          )}
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
          label={formatTeamHomeMessage(
            formatMessage,
            "teams.home.facts.teamMembers",
            t("pages.teams.home.team.member", "team member"),
          )}
          tooltip={preview.memberPreviewTooltip}
          value={preview.memberPreviewLabel}
        />
        <TeamFact
          label={formatTeamHomeMessage(
            formatMessage,
            "teams.home.facts.relatedServices",
            t("pages.teams.home.related.services", "Related services"),
          )}
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
          {formatTeamHomeMessage(
            formatMessage,
            "teams.home.actions.viewTeam",
            t("pages.teams.home.view.the.team", "View the team"),
          )}
        </Button>
      </Space>
    </article>
  );
};

const TeamRosterRow: React.FC<{
  readonly formatMessage: TeamsHomeFormatMessage;
  readonly preview: TeamRosterPreview;
}> = ({ formatMessage, preview }) => {
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
              {formatAttentionLabel(formatMessage, preview.attention)}
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
              fontSize: 13,
              marginTop: 4,
            }}
          >
            {formatTeamHomeMessage(
              formatMessage,
              "teams.home.team.identity",
              t("pages.teams.home.team.id.2", "team ID: {teamId}"),
              {
                teamId: preview.teamId,
              },
            )}
          </Typography.Text>
        </div>

        <Space className="teams-home-roster-row-actions" wrap>
          <Button onClick={() => history.push(preview.detailHref)} type="primary">
            {formatTeamHomeMessage(
              formatMessage,
              "teams.home.actions.viewTeam",
              t("pages.teams.home.view.the.team.2", "View the team"),
            )}
          </Button>
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
          label={formatTeamHomeMessage(
            formatMessage,
            "teams.home.facts.status",
            t("pages.teams.home.state", "state"),
          )}
          value={formatOperationalStatusLabel(
            formatMessage,
            preview.latestRun?.completionStatus,
            preview.attention,
          )}
        />
        <TeamFact
          label={formatTeamHomeMessage(
            formatMessage,
            "teams.home.facts.update",
            t("pages.teams.home.renew", "renew"),
          )}
          value={formatShortTime(preview.updatedAt)}
        />
        <TeamFact
          label={formatTeamHomeMessage(
            formatMessage,
            "teams.home.facts.members",
            t("pages.teams.home.member", "member"),
          )}
          tooltip={preview.memberPreviewTooltip}
          value={preview.memberPreviewLabel}
        />
        <TeamFact
          label={formatTeamHomeMessage(
            formatMessage,
            "teams.home.facts.services",
            t("pages.teams.home.serve", "Serve"),
          )}
          tooltip={preview.serviceTooltip}
          value={preview.serviceLabel}
        />
      </div>
    </article>
  );
};

const TeamsHomePage: React.FC = () => {
  const { token } = theme.useToken();
  const { formatMessage } = useIntl();
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
      formatTeamHomeMessage(
        formatMessage,
        "teams.home.alerts.authUnavailableDescription",
        t("pages.teams.home.the.login.status.is", "The login status is temporarily unavailable, please refresh and try again."),
      ),
    );
  }, [authSessionQuery.error, authSessionQuery.isError, formatMessage]);

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
          formatMessage,
          members: membersByTeamId.get(team.teamId) ?? [],
          runsByMemberId,
          scopeId,
          services: servicesQuery.data ?? [],
          team,
        }),
      ),
    [
      membersByTeamId,
      formatMessage,
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
      ? formatTeamHomeMessage(
          formatMessage,
          "teams.home.empty.description",
          t("pages.teams.home.no.team.has.been", "No team has been created for the current account. Once created, your AI team list will be displayed here."),
        )
      : formatTeamHomeMessage(
          formatMessage,
          "teams.home.alerts.noScope",
          t("pages.teams.home.the.current.login.status", "The current login status has not resolved the available team scope, please refresh and try again."),
        );
  const partialIssues = [
    membersQuery.isError
      ? formatTeamHomeMessage(
          formatMessage,
          "teams.home.alerts.membersUnavailable",
          t("pages.teams.home.the.member.list.of", "The member list of the current workspace is temporarily invisible."),
        )
      : null,
    teamsQuery.isError
      ? formatTeamHomeMessage(
          formatMessage,
          "teams.home.alerts.teamsUnavailable",
          t("pages.teams.home.the.team.roster.for", "The team roster for the current workspace is temporarily invisible."),
        )
      : null,
  ].filter((issue): issue is string => Boolean(issue));

  const titleNode = (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 14,
        }}
      >
        {formatTeamHomeMessage(
          formatMessage,
          "teams.home.breadcrumb",
          "Aevatar / Teams",
        )}
      </Typography.Text>
      <Typography.Title
        level={1}
        style={{
          margin: 0,
        }}
      >
        {formatTeamHomeMessage(
          formatMessage,
          "teams.home.title",
          t("pages.teams.home.my.ai.team", "My AI team"),
        )}
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
            {formatTeamHomeMessage(
              formatMessage,
              "teams.home.actions.createTeam",
              t("pages.teams.home.form.new.team", "Form a new team"),
            )}
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
            title={formatTeamHomeMessage(
              formatMessage,
              "teams.home.alerts.noScope",
              t("pages.teams.home.the.current.login.status.2", "The current login status has not resolved the available team scope, please refresh and try again."),
            )}
            type="info"
          />
        ) : null}

        {canLoadRoster && partialIssues.length > 0 ? (
          <Alert
            description={partialIssues.join(" ")}
            showIcon
            title={formatTeamHomeMessage(
              formatMessage,
              "teams.home.alerts.partialSignals",
              t("pages.teams.home.some.team.signals.are", "Some team signals are temporarily unavailable"),
            )}
            type="warning"
          />
        ) : null}

        {authSessionIssue ? (
          <Alert
            description={
              resolvedScope?.scopeId
                ? formatTeamHomeMessage(
                    formatMessage,
                    "teams.home.alerts.localAuthFallbackDescription",
                    t("pages.teams.home.loading.of.teams.has", "{issue} Loading of teams has continued using local login information."),
                    {
                      issue: authSessionIssue,
                    },
                  )
                : authSessionIssue
            }
            showIcon
            title={
              resolvedScope?.scopeId
                ? formatTeamHomeMessage(
                    formatMessage,
                    "teams.home.alerts.localAuthFallbackTitle",
                    t("pages.teams.home.the.current.login.status.3", "The current login status verification failed, local login information has been used"),
                  )
                : formatTeamHomeMessage(
                    formatMessage,
                    "teams.home.alerts.authFailedTitle",
                    t("pages.teams.home.current.login.status.verification", "Current login status verification failed"),
                  )
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
              <SummaryStatCard
                accent
                label={formatTeamHomeMessage(
                  formatMessage,
                  "teams.home.summary.total",
                  t("pages.teams.home.total.number.of.ai", "Total number of AI Teams"),
                )}
                value={visibleTeamCount}
              />
              <SummaryStatCard
                label={formatTeamHomeMessage(
                  formatMessage,
                  "teams.home.summary.actionable",
                  t("pages.teams.home.pending.team", "Pending team"),
                )}
                value={actionableTeamCount}
              />
              <SummaryStatCard
                label={formatTeamHomeMessage(
                  formatMessage,
                  "teams.home.summary.healthy",
                  t("pages.teams.home.stable.operation", "Stable operation"),
                )}
                value={healthyTeamCount}
              />
            </div>

            {teamsQuery.isLoading ? (
              <AevatarInspectorEmpty
                description={formatTeamHomeMessage(
                  formatMessage,
                  "teams.home.loading.roster",
                  t("pages.teams.home.reading.team.list", "Reading team list."),
                )}
              />
            ) : teamsQuery.isError ? (
              <Alert
                showIcon
                title={formatTeamHomeMessage(
                  formatMessage,
                  "teams.home.errors.rosterUnavailable",
                  t("pages.teams.home.the.team.list.cannot", "The team list cannot be loaded at the moment."),
                )}
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
                      {formatTeamHomeMessage(
                        formatMessage,
                        "teams.home.roster.title",
                        t("pages.teams.home.team.list", "team list"),
                      )}
                    </Typography.Title>
                    <Typography.Text type="secondary">
                      {formatTeamHomeMessage(
                        formatMessage,
                        "teams.home.roster.description",
                        t("pages.teams.home.aggregate.members.and.recent", "Aggregate members and recent running signals by team to prioritize exceptions or items of concern."),
                      )}
                    </Typography.Text>
                  </div>
                  {visibleTeamCount > 1 ? (
                    <Space.Compact>
                      <Tooltip
                        title={formatTeamHomeMessage(
                          formatMessage,
                          "teams.home.view.cards",
                          t("pages.teams.home.card.view", "card view"),
                        )}
                      >
                        <Button
                          aria-label={formatTeamHomeMessage(
                            formatMessage,
                            "teams.home.view.switchToCards",
                            t("pages.teams.home.switch.to.card.view", "Switch to card view"),
                          )}
                          icon={<AppstoreOutlined />}
                          onClick={() => setManualRosterView("cards")}
                          style={{ height: 44, width: 44 }}
                          type={resolvedRosterView === "cards" ? "primary" : "default"}
                        />
                      </Tooltip>
                      <Tooltip
                        title={formatTeamHomeMessage(
                          formatMessage,
                          "teams.home.view.list",
                          t("pages.teams.home.list.view", "list view"),
                        )}
                      >
                        <Button
                          aria-label={formatTeamHomeMessage(
                            formatMessage,
                            "teams.home.view.switchToList",
                            t("pages.teams.home.switch.to.list.view", "Switch to list view"),
                          )}
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
                    aria-label={formatTeamHomeMessage(
                      formatMessage,
                      "teams.home.view.compactAria",
                      t("pages.teams.home.team.compact.view", "team compact view"),
                    )}
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
                        <TeamRosterRow
                          formatMessage={formatMessage}
                          preview={preview}
                        />
                      </li>
                    ))}
                  </ul>
                ) : (
                  <ul
                    aria-label={formatTeamHomeMessage(
                      formatMessage,
                      "teams.home.view.cardsAria",
                      t("pages.teams.home.team.card.view", "team card view"),
                    )}
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
                        <TeamRosterCard
                          formatMessage={formatMessage}
                          preview={preview}
                        />
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
                  {formatTeamHomeMessage(
                    formatMessage,
                    "teams.home.actions.createTeam",
                    t("pages.teams.home.form.new.team.2", "Form a new team"),
                  )}
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
