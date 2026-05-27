import {
  AppstoreOutlined,
  BarsOutlined,
  InfoCircleOutlined,
  PlusOutlined,
  WarningOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
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
import { translate, useTranslation } from "@/shared/i18n/localization";
import { history } from "@/shared/navigation/history";
import { buildTeamDetailHref } from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
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

const scopeServiceAppId = "default";
const compactTeamRosterThreshold = 6;

type TeamOperationalAttention =
  | Exclude<WorkflowOperationalAttention, "runtime-unresolved">
  | "archived";

type MemberRosterPreview = {
  readonly attention: TeamOperationalAttention;
  readonly attentionDetail: string;
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
  readonly memberPreviewLabel: string;
  readonly memberPreviewTooltip?: string;
  readonly serviceLabel: string;
  readonly serviceTooltip?: string;
  readonly team: StudioTeamSummary;
  readonly teamId: string;
  readonly title: string;
  readonly updatedAt: string | null;
};

type TeamsHomeNoticeTone = "info" | "warning";

type TeamsHomeNotice = {
  readonly description?: string;
  readonly key: string;
  readonly title: string;
  readonly tone: TeamsHomeNoticeTone;
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
      return translate("team.home.run.waiting");
    case "failed":
    case "error":
      return translate("team.home.run.failed");
    case "completed":
      return translate("team.home.run.stable");
    default:
      return trimOptional(status) || translate("team.home.run.unknown");
  }
}

function formatMemberLifecycleStage(value: string | null | undefined): string {
  switch (trimOptional(value).toLowerCase()) {
    case "created":
      return translate("team.test.lifecycle.created");
    case "build_ready":
    case "buildready":
      return translate("team.test.lifecycle.buildReady");
    case "bind_ready":
    case "bindready":
      return translate("team.test.lifecycle.bindReady");
    default:
      return translate("team.test.lifecycle.unknown");
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
      return translate("team.home.attention.running");
    case "waiting":
      return translate("team.home.attention.waiting");
    case "failed":
      return translate("team.home.run.failed");
    case "draft":
      return translate("team.home.attention.draft");
    case "archived":
      return translate("team.home.attention.archived");
    case "no-bound-service":
      return translate("team.home.attention.needsBinding");
    case "no-recent-runs":
      return translate("team.home.attention.needsRun");
    default:
      return translate("team.home.run.unknown");
  }
}

function formatAttentionLabel(attention: TeamOperationalAttention): string {
  switch (attention) {
    case "failed":
      return translate("team.home.attention.needsAction");
    case "waiting":
      return translate("team.home.attention.waiting");
    case "healthy":
      return translate("team.home.attention.running");
    case "draft":
      return translate("team.home.attention.draft");
    case "archived":
      return translate("team.home.attention.archived");
    case "no-bound-service":
      return translate("team.home.attention.needsBinding");
    case "no-recent-runs":
      return translate("team.home.attention.needsRun");
    default:
      return translate("team.home.attention.unknown");
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
    case "archived":
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

function resolveSupplementalTooltip(
  value: string,
  tooltip: string | null | undefined,
): string | undefined {
  const normalizedValue = trimOptional(value);
  const normalizedTooltip = trimOptional(tooltip);
  if (!normalizedTooltip || normalizedTooltip === normalizedValue) {
    return undefined;
  }

  return normalizedTooltip;
}

function resolveLongTextTitle(value: string): string | undefined {
  const normalizedValue = trimOptional(value);
  return normalizedValue.length > 24 ? normalizedValue : undefined;
}

function parseTimestamp(value: string | null | undefined): number {
  const parsed = Date.parse(value || "");
  return Number.isFinite(parsed) ? parsed : 0;
}

const TeamTitle: React.FC<{
  readonly level: 3 | 4;
  readonly title: string;
}> = ({ level, title }) => {
  const titleTooltip = resolveLongTextTitle(title);

  return (
    <div
      style={{
        minWidth: 0,
      }}
      title={titleTooltip}
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
};

const TeamsHomeNoticeRail: React.FC<{
  readonly notices: readonly TeamsHomeNotice[];
}> = ({ notices }) => {
  const { token } = theme.useToken();

  if (notices.length === 0) {
    return null;
  }

  const toneStyle: Record<TeamsHomeNoticeTone, React.CSSProperties> = {
    info: {
      background: "rgba(24, 144, 255, 0.06)",
      borderColor: "rgba(24, 144, 255, 0.18)",
      color: token.colorInfo,
    },
    warning: {
      background: "rgba(250, 173, 20, 0.08)",
      borderColor: "rgba(250, 173, 20, 0.24)",
      color: token.colorWarning,
    },
  };

  return (
    <section
      aria-label={translate("team.home.status.aria")}
      role="status"
      style={{
        display: "flex",
        flexDirection: "column",
        gap: 8,
      }}
    >
      {notices.map((notice) => {
        const isWarning = notice.tone === "warning";
        return (
          <div
            key={notice.key}
            style={{
              ...toneStyle[notice.tone],
              alignItems: "flex-start",
              border: "1px solid",
              borderRadius: 8,
              display: "grid",
              gap: 10,
              gridTemplateColumns: "16px minmax(0, 1fr)",
              padding: "10px 12px",
            }}
          >
            {isWarning ? <WarningOutlined /> : <InfoCircleOutlined />}
            <div
              style={{
                display: "flex",
                flexDirection: "column",
                gap: 2,
                minWidth: 0,
              }}
            >
              <Typography.Text
                style={{
                  color: token.colorText,
                  fontSize: 13,
                  fontWeight: 600,
                  lineHeight: 1.4,
                }}
              >
                {notice.title}
              </Typography.Text>
              {notice.description ? (
                <Typography.Text
                  style={{
                    color: token.colorTextSecondary,
                    fontSize: 13,
                    lineHeight: 1.45,
                  }}
                >
                  {notice.description}
                </Typography.Text>
              ) : null}
            </div>
          </div>
        );
      })}
    </section>
  );
};

const TeamFact: React.FC<{
  readonly label: string;
  readonly tooltip?: string;
  readonly value: React.ReactNode;
}> = ({ label, tooltip, value }) => {
  const { token } = theme.useToken();
  const tooltipTitle =
    typeof value === "string"
      ? resolveSupplementalTooltip(value, tooltip)
      : undefined;
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
      {tooltipTitle ? (
        <Tooltip title={tooltipTitle}>{renderedValue}</Tooltip>
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
  const serviceLabel =
    pickMeaningfulLabel(trimOptional(matchedService?.displayName), serviceId) ||
    (trimOptional(input.member.lastBoundRevisionId)
      ? translate("team.home.member.boundPending")
      : translate("team.home.member.unbound"));
  const title =
    pickMeaningfulLabel(input.member.displayName, input.member.memberId) ||
    translate("team.home.unnamedMember");

  let attention: TeamOperationalAttention = "draft";
  let attentionDetail = translate("team.home.member.lifecycle", {
    stage: formatMemberLifecycleStage(input.member.lifecycleStage),
  });

  if (serviceId || matchedService) {
    attention = "no-recent-runs";
    attentionDetail = translate("team.home.member.noRecentRuns");
  } else if (
    trimOptional(input.member.lastBoundRevisionId) ||
    input.member.lifecycleStage === "bind_ready"
  ) {
    attention = "no-bound-service";
    attentionDetail = translate("team.home.member.noBoundService");
  }

  return {
    attention,
    attentionDetail,
    memberId,
    serviceId,
    serviceLabel,
    title,
    updatedAt:
      matchedService?.updatedAt ||
      input.member.updatedAt ||
      null,
  };
}

function buildTeamRosterPreview(input: {
  readonly members: readonly StudioMemberSummary[];
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
  readonly team: StudioTeamSummary;
}): TeamRosterPreview {
  const memberPreviews = input.members.map((member) =>
    buildMemberRosterPreview({
      member,
      scopeId: input.scopeId,
      services: input.services,
      teamId: input.team.teamId,
    }),
  );
  const sortedMembers = [...input.members].sort(compareMembers);
  const statusRank: Record<TeamOperationalAttention, number> = {
    failed: 0,
    waiting: 1,
    "no-bound-service": 2,
    "no-recent-runs": 3,
    draft: 4,
    archived: 5,
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
          ? translate("team.home.memberPreview", {
              count: memberCount,
              name: firstMemberLabel,
            })
          : firstMemberLabel
        : translate("team.home.memberCount", { count: memberCount })
      : translate("team.home.noMembers");
  const serviceLabels = memberPreviews
    .map((preview) => preview.serviceLabel)
    .filter((label) => label && label !== translate("team.home.member.unbound"));
  const uniqueServiceLabels = Array.from(new Set(serviceLabels));
  const memberPreviewTooltip =
    sortedMembers.length > 0
      ? sortedMembers
          .map((member) =>
            pickMeaningfulLabel(member.displayName, member.memberId) ||
            translate("team.home.member.unnamed"),
          )
          .join(" / ")
      : undefined;
  const serviceTooltip =
    uniqueServiceLabels.length > 0 ? uniqueServiceLabels.join(" / ") : undefined;
  const primaryMemberPreview =
    memberPreviews.find((preview) => preview.serviceId) ?? memberPreviews[0] ?? null;
  const detailHref = buildTeamDetailHref({
    memberId: primaryMemberPreview?.memberId || undefined,
    scopeId: input.scopeId,
    serviceId: primaryMemberPreview?.serviceId || undefined,
    teamId: input.team.teamId,
  });

  let attention: TeamOperationalAttention =
    mostImportantMemberPreview?.attention ?? "draft";
  let attentionDetail = translate("team.home.noMembersFact");
  if (input.team.lifecycleStage === "archived") {
    attention = "archived";
    attentionDetail = translate("team.home.archivedFact");
  } else if (mostImportantMemberPreview) {
    attentionDetail = mostImportantMemberPreview.attentionDetail;
  }

  return {
    attention,
    attentionDetail,
    detailHref,
    memberPreviewLabel,
    memberPreviewTooltip,
    serviceLabel:
      uniqueServiceLabels.length > 0
        ? uniqueServiceLabels.slice(0, 2).join(" / ")
        : translate("team.home.noBoundService"),
    serviceTooltip,
    team: input.team,
    teamId: input.team.teamId,
    title:
      pickMeaningfulLabel(input.team.displayName, input.team.teamId) ||
      translate("team.home.unnamedTeam"),
    updatedAt:
      mostImportantMemberPreview?.updatedAt ||
      input.team.updatedAt ||
      null,
  };
}

const TeamRosterCard: React.FC<{
  readonly preview: TeamRosterPreview;
}> = ({ preview }) => {
  const { token } = theme.useToken();
  const { t } = useTranslation();

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
            style={{
              color: token.colorTextSecondary,
              fontSize: 14,
              marginBottom: 0,
              marginTop: 6,
              overflow: "hidden",
              textOverflow: "ellipsis",
              whiteSpace: "nowrap",
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
          display: "block",
          fontSize: 13,
          overflow: "hidden",
          textOverflow: "ellipsis",
          whiteSpace: "nowrap",
        }}
      >
        {t("team.home.teamIdLabel")}：{preview.teamId}
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
          label={t("team.home.currentStatus")}
          value={formatOperationalStatusLabel(
            undefined,
            preview.attention,
          )}
        />
        <TeamFact
          label={t("team.home.latestUpdate")}
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
          label={t("team.home.teamMembers")}
          tooltip={preview.memberPreviewTooltip}
          value={preview.memberPreviewLabel}
        />
        <TeamFact
          label={t("team.home.relatedServices")}
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
          {t("team.home.viewTeam")}
        </Button>
      </Space>
    </article>
  );
};

const TeamRosterRow: React.FC<{
  readonly preview: TeamRosterPreview;
}> = ({ preview }) => {
  const { token } = theme.useToken();
  const { t } = useTranslation();

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
            style={{
              color: token.colorTextSecondary,
              fontSize: 13,
              marginBottom: 0,
              marginTop: 0,
              overflow: "hidden",
              textOverflow: "ellipsis",
              whiteSpace: "nowrap",
            }}
          >
            {preview.attentionDetail}
          </Typography.Paragraph>
          <Typography.Text
            style={{
              color: token.colorTextSecondary,
              display: "block",
              fontSize: 13,
              marginTop: 4,
              overflow: "hidden",
              textOverflow: "ellipsis",
              whiteSpace: "nowrap",
            }}
          >
            {t("team.home.teamIdLabel")}：{preview.teamId}
          </Typography.Text>
        </div>

        <Space className="teams-home-roster-row-actions" wrap>
          <Button onClick={() => history.push(preview.detailHref)} type="primary">
            {t("team.home.viewTeam")}
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
          label={t("team.home.status")}
          value={formatOperationalStatusLabel(
            undefined,
            preview.attention,
          )}
        />
        <TeamFact label={t("team.home.update")} value={formatShortTime(preview.updatedAt)} />
        <TeamFact
          label={t("team.home.members")}
          tooltip={preview.memberPreviewTooltip}
          value={preview.memberPreviewLabel}
        />
        <TeamFact
          label={t("team.home.services")}
          tooltip={preview.serviceTooltip}
          value={preview.serviceLabel}
        />
      </div>
    </article>
  );
};

const TeamsHomePage: React.FC = () => {
  const { token } = theme.useToken();
  const { t } = useTranslation();
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
      "登录状态暂时不可用，请刷新后重试。",
    );
  }, [authSessionQuery.error, authSessionQuery.isError]);
  const authUnavailable = Boolean(
    authSessionIssue ||
      (authSessionQuery.isSuccess && !authSessionQuery.data?.authenticated),
  );

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
  const teamPreviews = React.useMemo(
    () =>
      studioTeams.map((team) =>
        buildTeamRosterPreview({
          members: membersByTeamId.get(team.teamId) ?? [],
          scopeId,
          services: servicesQuery.data ?? [],
          team,
        }),
      ),
    [
      membersByTeamId,
      queryScopeId,
      scopeId,
      servicesQuery.data,
      studioTeams,
    ],
  );
  const visibleTeamCount = teamPreviews.length;
  const resolvedRosterView =
    manualRosterView ??
    (visibleTeamCount >= compactTeamRosterThreshold ? "list" : "cards");
  const useCompactRoster = resolvedRosterView === "list";
  const canCreateTeam = canLoadRoster && !authUnavailable;
  const createTeamDisabledTitle = canCreateTeam
    ? undefined
    : t("team.home.authRequired");
  const emptyRosterHint =
    canLoadRoster
      ? t("team.home.empty")
      : t("team.home.missingScope");
  const partialIssues = [
    membersQuery.isError ? t("team.home.membersUnavailable") : null,
    teamsQuery.isError ? t("team.home.rosterUnavailable") : null,
  ].filter((issue): issue is string => Boolean(issue));
  const homeNotices = React.useMemo(() => {
    const notices: TeamsHomeNotice[] = [];

    if (!scopeId) {
      notices.push({
        key: "missing-scope",
        title: t("team.home.missingScope"),
        tone: "info",
      });
    } else if (!canLoadRoster && !authUnavailable && !authSessionQuery.isLoading) {
      notices.push({
        description: t("team.home.rosterAuthUnavailable"),
        key: "scope-not-confirmed",
        title: t("team.home.authRequired"),
        tone: "warning",
      });
    }

    if (canLoadRoster && partialIssues.length > 0) {
      notices.push({
        description: partialIssues.join(" "),
        key: "partial-roster",
        title: t("team.home.partialSignalsTitle"),
        tone: "warning",
      });
    }

    if (authSessionIssue) {
      notices.push({
        description: authSessionIssue,
        key: "auth-session-error",
        title: t("team.home.authFailed"),
        tone: "warning",
      });
    } else if (authUnavailable) {
      notices.push({
        description: t("team.home.rosterAuthUnavailable"),
        key: "auth-session-unavailable",
        title: t("team.home.authUnavailable"),
        tone: "warning",
      });
    }

    return notices;
  }, [
    authSessionIssue,
    authUnavailable,
    authSessionQuery.isLoading,
    canLoadRoster,
    partialIssues,
    scopeId,
    t,
  ]);

  const titleNode = (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 14,
        }}
      >
        {t("common.appName")} / {t("common.teams")}
      </Typography.Text>
      <Typography.Title
        level={1}
        style={{
          margin: 0,
        }}
      >
        {t("team.home.title")}
      </Typography.Title>
    </div>
  );

  return (
    <AevatarPageShell
      extra={
        <Space wrap>
          <Button
            disabled={!canCreateTeam}
            icon={<PlusOutlined />}
            onClick={() =>
              history.push(buildScopeHref("/teams/new", { scopeId }))
            }
            style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
            title={createTeamDisabledTitle}
            type="primary"
          >
            {t("team.home.create")}
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
        <TeamsHomeNoticeRail notices={homeNotices} />

        {canLoadRoster ? (
          <>
            {teamsQuery.isLoading ? (
              <AevatarInspectorEmpty description={t("team.home.loading")} />
            ) : teamsQuery.isError ? (
              <Alert
                showIcon
                title={t("team.home.loadFailed")}
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
                      {t("team.home.listTitle")}
                    </Typography.Title>
                  </div>
                  {visibleTeamCount > 1 ? (
                    <Space.Compact>
                      <Tooltip title={t("team.home.cardView")}>
                        <Button
                          aria-label={t("team.home.switchCardView")}
                          icon={<AppstoreOutlined />}
                          onClick={() => setManualRosterView("cards")}
                          style={{ height: 44, width: 44 }}
                          type={resolvedRosterView === "cards" ? "primary" : "default"}
                        />
                      </Tooltip>
                      <Tooltip title={t("team.home.listView")}>
                        <Button
                          aria-label={t("team.home.switchListView")}
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
                    aria-label={t("team.home.compactView")}
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
                    aria-label={t("team.home.cardListView")}
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
                  disabled={!canCreateTeam}
                  onClick={() =>
                    history.push(buildScopeHref("/teams/new", { scopeId }))
                  }
                  title={createTeamDisabledTitle}
                  type="primary"
                >
                  {t("team.home.create")}
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
