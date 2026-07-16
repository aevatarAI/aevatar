import {
  CheckCircleOutlined,
  ClockCircleOutlined,
  DeleteOutlined,
  EditOutlined,
  ExclamationCircleOutlined,
  HistoryOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  ThunderboltOutlined,
} from "@ant-design/icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Button,
  Checkbox,
  Input,
  Modal,
  Segmented,
  Select,
  Skeleton,
  Space,
  Switch,
  Tooltip,
  Typography,
  message,
  theme,
} from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import {
  scheduledDispatchApi,
  scheduledWorkflowPromptMaxLength,
  type ScheduledDispatchPreview,
} from "@/shared/api/scheduledDispatchApi";
import {
  createTeamAutomationOperationIdentity,
  teamAutomationApi,
  TeamAutomationApiError,
  type TeamAutomationCreateDraft,
  type TeamAutomationDisclosure,
  type TeamAutomationMutationReceipt,
  type TeamAutomationOperationIdentity,
  type TeamAutomationPermissionReview,
  type TeamAutomationRoute,
  type TeamAutomationUpdateInput,
  type TeamAutomationView,
} from "@/shared/api/teamAutomationApi";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import { buildTeamMemberPublishedRunsHref } from "@/shared/navigation/teamRoutes";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  FactLine,
} from "../components/TeamDetailPrimitives";

type IntlShape = ReturnType<typeof useIntl>;

export type TeamAutomationMemberRow = {
  readonly automationsHref: string;
  readonly canAutomateMember: boolean;
  readonly disabledReason: string;
  readonly implementationKind: string;
  readonly isSelectedMember?: boolean;
  readonly key: string;
  readonly lifecycleLabel: string;
  readonly lifecycleStyle: React.CSSProperties;
  readonly memberId: string;
  readonly name: string;
  readonly serviceId: string;
  readonly workflowSupported: boolean;
};

type TeamAutomationsTabProps = {
  readonly members?: readonly TeamAutomationMemberRow[];
  readonly scopeId: string;
  readonly teamId: string;
};

type AutomationFormState = {
  readonly cronExpression: string;
  readonly displayName: string;
  readonly enabled: boolean;
  readonly memberId: string;
  readonly preset: string;
  readonly prompt: string;
  readonly timezone: string;
};

type TeamAutomationCreateStage =
  | "draft"
  | "preflight"
  | "permissionReview"
  | "consent"
  | "planChanged"
  | "error";

type LifecycleOperationKind = "create" | "delete" | "reauthorize";

type AcceptedLifecycleOperation = {
  readonly identity: TeamAutomationOperationIdentity;
  readonly intentKey: string;
  readonly kind: LifecycleOperationKind;
  readonly scheduleId: string;
};

const scheduleListTake = 200;
const scheduleListRetryLimit = 4;
const scheduleListRetryBaseMs = 600;
const scheduleListRetryMaxMs = 2_500;
const customPreset = "custom";
const defaultPreset = "weekdays-0900";
const defaultCronExpression = "0 9 * * 1-5";

function buildDefaultAutomationFormState(memberId = ""): AutomationFormState {
  return {
    cronExpression: defaultCronExpression,
    displayName: "",
    enabled: true,
    memberId,
    preset: defaultPreset,
    prompt: "",
    timezone: resolveDefaultTimezone(),
  };
}

function hasAutomationDraft(formState: AutomationFormState): boolean {
  return Boolean(
    formState.displayName.trim() ||
      formState.prompt.trim() ||
      formState.memberId.trim() ||
      formState.cronExpression.trim() !== defaultCronExpression ||
      formState.preset !== defaultPreset ||
      formState.timezone.trim() !== resolveDefaultTimezone() ||
      !formState.enabled,
  );
}

function buildTeamAutomationEditInput({
  cronExpression,
  displayName,
  enabled,
  prompt,
  timezone,
}: {
  readonly cronExpression: string;
  readonly displayName: string;
  readonly enabled: boolean;
  readonly prompt: string;
  readonly timezone?: string;
}): TeamAutomationUpdateInput {
  return {
    displayName,
    cronExpression,
    timezone,
    enabled,
    prompt,
  };
}

function buildTeamAutomationCreateDraft({
  cronExpression,
  displayName,
  enabled,
  member,
  prompt,
  scopeId,
  teamId,
  timezone,
}: {
  readonly cronExpression: string;
  readonly displayName: string;
  readonly enabled: boolean;
  readonly member: TeamAutomationMemberRow;
  readonly prompt: string;
  readonly scopeId: string;
  readonly teamId: string;
  readonly timezone?: string;
}): TeamAutomationCreateDraft {
  return {
    scopeId,
    teamId,
    memberId: member.memberId,
    displayName,
    prompt,
    cronExpression,
    timezone,
    enabled,
  };
}

const pageGridStyle: React.CSSProperties = {
  alignItems: "start",
  display: "grid",
  gap: 16,
  gridTemplateColumns: "minmax(0, 1fr) minmax(280px, 340px)",
  minWidth: 0,
  width: "100%",
};

const responsiveStyle = `
.team-automations-layout,
.team-automations-layout * {
  box-sizing: border-box;
}

.team-automation-row {
  transition: background-color 160ms ease, border-color 160ms ease, box-shadow 160ms ease;
}

.team-automation-row:hover {
  background: rgba(22, 119, 255, 0.035);
}

.team-automation-action-button {
  transition: background-color 160ms ease, color 160ms ease, transform 160ms ease, box-shadow 160ms ease;
}

.team-automation-action-button:hover,
.team-automation-action-button:focus-visible {
  box-shadow: 0 8px 18px rgba(15, 23, 42, 0.08) !important;
  transform: translateY(-1px);
}

@media (max-width: 1320px) {
  .team-automations-layout {
    grid-template-columns: minmax(0, 1fr) !important;
  }
}

.team-automation-row > * {
  min-width: 0;
}

@media (max-width: 900px) {
  .team-automation-list-header {
    display: none !important;
  }

  .team-automation-row {
    align-items: start !important;
    grid-template-columns: minmax(0, 1fr) max-content !important;
    gap: 12px !important;
  }

  .team-automation-row__automation,
  .team-automation-row__member,
  .team-automation-row__schedule {
    grid-column: 1;
  }

  .team-automation-actions {
    align-self: start;
    grid-column: 2;
    grid-row: 1 / span 3;
  }
}

@media (max-width: 760px) {
  .team-automations-panel-header {
    align-items: stretch !important;
  }

  .team-automations-create-button {
    width: 100%;
  }

  .team-automation-summary {
    grid-template-columns: minmax(0, 1fr) !important;
  }

  .team-automation-form-schedule-grid {
    grid-template-columns: minmax(0, 1fr) !important;
  }
}

@media (max-width: 640px) {
  .team-automation-row {
    grid-template-columns: minmax(0, 1fr) !important;
    gap: 12px !important;
    padding: 14px !important;
  }

  .team-automation-actions {
    grid-column: 1;
    grid-row: auto;
    justify-content: flex-start !important;
    width: 100%;
  }
}

@media (max-width: 520px) {
  .team-automation-actions {
    display: grid !important;
    grid-template-columns: repeat(4, minmax(0, 1fr));
  }

  .team-automation-actions .ant-btn {
    min-width: 0 !important;
    width: 100% !important;
  }
}
`;

const panelHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  flexWrap: "wrap",
  gap: 12,
  justifyContent: "space-between",
  minWidth: 0,
};

const primaryHeaderButtonStyle: React.CSSProperties = {
  borderRadius: 10,
  boxShadow: "none",
  fontWeight: 700,
  height: 36,
  paddingInline: 14,
};

const inspectorActionButtonStyle: React.CSSProperties = {
  ...primaryHeaderButtonStyle,
  height: 38,
};

const titleGroupStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 4,
  minWidth: 0,
};

const titleStyle: React.CSSProperties = {
  fontSize: 16,
  fontWeight: 800,
  lineHeight: "24px",
  margin: 0,
};

const commitmentGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 10,
};

const commitmentRowStyle: React.CSSProperties = {
  alignItems: "center",
  display: "grid",
  gap: 14,
  gridTemplateColumns:
    "minmax(0, 1.16fr) minmax(0, 0.72fr) minmax(0, 0.48fr) max-content",
  minWidth: 0,
  padding: 14,
  width: "100%",
};

const automationSummaryGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 10,
  gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
};

const automationSummaryTileStyle: React.CSSProperties = {
  alignItems: "center",
  borderRadius: 12,
  display: "flex",
  gap: 10,
  minWidth: 0,
  padding: "12px 14px",
};

const automationListHeaderStyle: React.CSSProperties = {
  alignItems: "center",
  display: "grid",
  gap: 14,
  gridTemplateColumns: commitmentRowStyle.gridTemplateColumns,
  minWidth: 0,
  padding: "0 14px",
};

const automationNameLineStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  gap: 8,
  minWidth: 0,
};

const automationStatusBadgeStyle: React.CSSProperties = {
  alignItems: "center",
  borderRadius: 999,
  display: "inline-flex",
  flexShrink: 0,
  fontSize: 12,
  fontWeight: 700,
  gap: 6,
  lineHeight: 1,
  padding: "7px 10px",
  whiteSpace: "nowrap",
};

const automationStatusDotStyle: React.CSSProperties = {
  borderRadius: 999,
  flexShrink: 0,
  height: 7,
  width: 7,
};

const automationActionGroupBaseStyle: React.CSSProperties = {
  alignItems: "center",
  borderRadius: 12,
  display: "flex",
  flexWrap: "nowrap",
  gap: 4,
  inlineSize: "max-content",
  justifyContent: "flex-end",
  justifySelf: "end",
  maxWidth: "100%",
  minWidth: "max-content",
  padding: 4,
};

const automationActionButtonBaseStyle: React.CSSProperties = {
  border: "none",
  borderRadius: 8,
  boxShadow: "none",
  height: 32,
  lineHeight: 1,
  minWidth: 32,
  paddingInline: 0,
  width: 32,
};

const upcomingRowStyle: React.CSSProperties = {
  alignItems: "start",
  display: "grid",
  gap: 10,
  gridTemplateColumns: "28px minmax(0, 1fr)",
  minWidth: 0,
};

const modalFieldStyle: React.CSSProperties = {
  display: "grid",
  gap: 8,
};

const enabledFieldStyle: React.CSSProperties = {
  ...modalFieldStyle,
  alignContent: "start",
  justifyItems: "start",
};

const enabledSwitchStyle: React.CSSProperties = {
  justifySelf: "start",
  minWidth: 44,
  width: 44,
};

const scheduleInsightStyle: React.CSSProperties = {
  borderRadius: 10,
  display: "grid",
  gap: 4,
  minWidth: 0,
  padding: "10px 12px",
};

const modalSectionStyle: React.CSSProperties = {
  borderRadius: 12,
  display: "grid",
  gap: 14,
  padding: 14,
};

function trimText(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function resolveDefaultTimezone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

function formatScheduleTime(value: string | null | undefined, fallback: string): string {
  return formatCompactDateTime(value, fallback);
}

function parseCronInteger(value: string, min: number, max: number): number | null {
  if (!/^\d+$/.test(value)) {
    return null;
  }

  const parsed = Number(value);
  return parsed >= min && parsed <= max ? parsed : null;
}

function formatTwoDigit(value: number): string {
  return String(value).padStart(2, "0");
}

function formatCronWeekday(value: number, intl: IntlShape): string {
  const weekday = value % 7;
  const labels = [
    intl.formatMessage({
      id: "teams.automations.weekdays.sunday",
      defaultMessage: "Sunday",
    }),
    intl.formatMessage({
      id: "teams.automations.weekdays.monday",
      defaultMessage: "Monday",
    }),
    intl.formatMessage({
      id: "teams.automations.weekdays.tuesday",
      defaultMessage: "Tuesday",
    }),
    intl.formatMessage({
      id: "teams.automations.weekdays.wednesday",
      defaultMessage: "Wednesday",
    }),
    intl.formatMessage({
      id: "teams.automations.weekdays.thursday",
      defaultMessage: "Thursday",
    }),
    intl.formatMessage({
      id: "teams.automations.weekdays.friday",
      defaultMessage: "Friday",
    }),
    intl.formatMessage({
      id: "teams.automations.weekdays.saturday",
      defaultMessage: "Saturday",
    }),
  ];
  return labels[weekday];
}

function describeCronExpression(
  cronExpression: string,
  intl: IntlShape,
  timezone: string,
): {
  readonly detail: string;
  readonly summary: string;
} {
  const normalized = cronExpression.trim().replace(/\s+/g, " ");
  const parts = normalized.split(" ");
  const trimmedTimezone = trimText(timezone) || "UTC";
  if (parts.length !== 5) {
    return {
      detail: normalized || "--",
      summary: intl.formatMessage({
        id: "teams.automations.cron.custom",
        defaultMessage: "Custom schedule",
      }),
    };
  }

  const [minutePart, hourPart, dayOfMonthPart, monthPart, dayOfWeekPart] = parts;
  const minute = parseCronInteger(minutePart, 0, 59);
  const hour = parseCronInteger(hourPart, 0, 23);
  const formattedTime =
    hour !== null && minute !== null
      ? `${formatTwoDigit(hour)}:${formatTwoDigit(minute)}`
      : null;

  if (
    formattedTime &&
    dayOfMonthPart === "*" &&
    monthPart === "*" &&
    (dayOfWeekPart === "*" || dayOfWeekPart === "?")
  ) {
    return {
      detail: intl.formatMessage(
        {
          id: "teams.automations.cron.dailyDetail",
          defaultMessage: "Every day at {time} · {timezone}",
        },
        { time: formattedTime, timezone: trimmedTimezone },
      ),
      summary: intl.formatMessage(
        {
          id: "teams.automations.cron.daily",
          defaultMessage: "Daily · {time}",
        },
        { time: formattedTime },
      ),
    };
  }

  if (
    formattedTime &&
    dayOfMonthPart === "*" &&
    monthPart === "*" &&
    (dayOfWeekPart === "1-5" || dayOfWeekPart.toUpperCase() === "MON-FRI")
  ) {
    return {
      detail: intl.formatMessage(
        {
          id: "teams.automations.cron.weekdaysDetail",
          defaultMessage: "Weekdays at {time} · {timezone}",
        },
        { time: formattedTime, timezone: trimmedTimezone },
      ),
      summary: intl.formatMessage(
        {
          id: "teams.automations.cron.weekdays",
          defaultMessage: "Weekdays · {time}",
        },
        { time: formattedTime },
      ),
    };
  }

  const weeklyDay = parseCronInteger(dayOfWeekPart, 0, 7);
  if (
    formattedTime &&
    weeklyDay !== null &&
    dayOfMonthPart === "*" &&
    monthPart === "*"
  ) {
    const weekday = formatCronWeekday(weeklyDay, intl);
    return {
      detail: intl.formatMessage(
        {
          id: "teams.automations.cron.weeklyDetail",
          defaultMessage: "{weekday} at {time} · {timezone}",
        },
        { time: formattedTime, timezone: trimmedTimezone, weekday },
      ),
      summary: intl.formatMessage(
        {
          id: "teams.automations.cron.weekly",
          defaultMessage: "{weekday} · {time}",
        },
        { time: formattedTime, weekday },
      ),
    };
  }

  if (
    minute !== null &&
    hourPart === "*" &&
    dayOfMonthPart === "*" &&
    monthPart === "*" &&
    (dayOfWeekPart === "*" || dayOfWeekPart === "?")
  ) {
    return {
      detail: intl.formatMessage(
        {
          id: "teams.automations.cron.hourlyDetail",
          defaultMessage: "Every hour at minute {minute} · {timezone}",
        },
        { minute: formatTwoDigit(minute), timezone: trimmedTimezone },
      ),
      summary: intl.formatMessage(
        {
          id: "teams.automations.cron.hourly",
          defaultMessage: "Hourly · :{minute}",
        },
        { minute: formatTwoDigit(minute) },
      ),
    };
  }

  return {
    detail: `${normalized} · ${trimmedTimezone}`,
    summary: intl.formatMessage({
      id: "teams.automations.cron.custom",
      defaultMessage: "Custom schedule",
    }),
  };
}

function resolveCronValidationMessage(
  cronExpression: string,
  intl: IntlShape,
): string {
  const normalized = cronExpression.trim().replace(/\s+/g, " ");
  if (!normalized) {
    return intl.formatMessage({
      id: "teams.automations.messages.cronRequired",
      defaultMessage: "Enter a cron expression first.",
    });
  }

  if (normalized.split(" ").length !== 5) {
    return intl.formatMessage({
      id: "teams.automations.form.cronFiveFieldHint",
      defaultMessage:
        "Use a 5-field cron expression: minute hour day month weekday.",
    });
  }

  return "";
}

function sortByNextFire(
  left: TeamAutomationView,
  right: TeamAutomationView,
): number {
  const leftTime = left.nextFireAt ? Date.parse(left.nextFireAt) : Number.MAX_SAFE_INTEGER;
  const rightTime = right.nextFireAt ? Date.parse(right.nextFireAt) : Number.MAX_SAFE_INTEGER;
  return leftTime - rightTime;
}

function scheduleListRetryDelay(attemptIndex: number): number {
  return Math.min(
    scheduleListRetryBaseMs * 2 ** attemptIndex,
    scheduleListRetryMaxMs,
  );
}

function isAuthorizationPlanChanged(error: unknown): boolean {
  return (
    error instanceof TeamAutomationApiError &&
    error.status === 409 &&
    [
      "TEAM_AUTOMATION_AUTHORIZATION_PLAN_CHANGED",
      "TEAM_AUTOMATION_REAUTHORIZATION_REQUIRED",
    ].includes(error.code ?? "")
  );
}

function buildOperationIntentKey(
  kind: string,
  route: TeamAutomationRoute,
  scheduleId = "",
  permissionDigest = "",
): string {
  return [
    kind,
    route.scopeId,
    route.teamId,
    route.memberId,
    scheduleId,
    permissionDigest,
  ].join("\n");
}

function formatDisclosure(
  disclosure: TeamAutomationDisclosure,
  intl: IntlShape,
): string {
  switch (disclosure) {
    case "dedicated_credential":
      return intl.formatMessage({
        id: "teams.automations.form.agentKeyDedicated",
        defaultMessage: "Dedicated to this schedule",
      });
    case "aevatar_secret_custody":
      return intl.formatMessage({
        id: "teams.automations.form.agentKeyManaged",
        defaultMessage: "Aevatar managed",
      });
    case "browser_never_receives_secret":
      return intl.formatMessage({
        id: "teams.automations.form.agentKeyNoRawKey",
        defaultMessage: "Browser never receives the raw Agent Key",
      });
    case "delete_revokes_credential":
      return intl.formatMessage({
        id: "teams.automations.form.agentKeyDeleteRevokes",
        defaultMessage: "Delete revokes the Agent Key",
      });
    case "pause_resume_preserves_credential":
      return intl.formatMessage({
        id: "teams.automations.form.agentKeyPausePreserves",
        defaultMessage: "Pause and resume preserve the Agent Key",
      });
    case "node_ids_are_permission_set":
      return intl.formatMessage({
        id: "teams.automations.form.agentKeyNodePermissionSet",
        defaultMessage: "Node IDs are an exact permission set",
      });
  }
}

function resolveScheduleStatus(
  schedule: TeamAutomationView,
):
  | "active"
  | "paused"
  | "pending"
  | "needsAuthorization"
  | "revocationPending"
  | "error" {
  if (schedule.revocationPending) {
    return "revocationPending";
  }
  switch (schedule.authorizationStatus) {
    case "provisioning_pending":
    case "replacement_pending":
      return "pending";
    case "needs_authorization":
      return "needsAuthorization";
    case "deleting":
    case "revocation_pending":
      return "revocationPending";
    case "failed":
      return "error";
    default:
      return schedule.enabled ? "active" : "paused";
  }
}

function useTeamAutomationPermissionReview() {
  const intl = useIntl();
  const [stage, setStage] = React.useState<TeamAutomationCreateStage>("draft");
  const [review, setReview] =
    React.useState<TeamAutomationPermissionReview | null>(null);
  const [reviewedDraft, setReviewedDraft] =
    React.useState<TeamAutomationCreateDraft | null>(null);
  const [consentChecked, setConsentChecked] = React.useState(false);
  const [error, setError] = React.useState("");
  const mutation = useMutation({
    mutationFn: async (draft: TeamAutomationCreateDraft) => {
      await teamAutomationApi.refreshAuthorizationCatalog();
      return teamAutomationApi.preflightCreate(draft);
    },
    onError: (cause) => {
      const detail = cause instanceof Error ? cause.message : String(cause);
      setReview(null);
      setReviewedDraft(null);
      setConsentChecked(false);
      setStage("error");
      setError(detail);
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.reviewFailed",
            defaultMessage: "Permission review could not be prepared: {message}",
          },
          { message: detail },
        ),
      );
    },
    onMutate: () => {
      setReview(null);
      setReviewedDraft(null);
      setConsentChecked(false);
      setError("");
      setStage("preflight");
    },
    onSuccess: (nextReview, draft) => {
      setReview(nextReview);
      setReviewedDraft(draft);
      setStage(
        nextReview.status === "plan-changed"
          ? "planChanged"
          : "permissionReview",
      );
    },
  });
  const reset = React.useCallback(() => {
    setReview(null);
    setReviewedDraft(null);
    setConsentChecked(false);
    setError("");
    setStage("draft");
  }, []);
  const setConsent = React.useCallback((checked: boolean) => {
    setConsentChecked(checked);
    setStage(checked ? "consent" : "permissionReview");
  }, []);

  return {
    consentChecked,
    error,
    mutation,
    reset,
    review,
    reviewedDraft,
    setConsent,
    stage,
  };
}

function TeamAutomationGrantList({
  grants,
  title,
}: {
  readonly grants: TeamAutomationPermissionReview["serviceGrants"];
  readonly title: string;
}) {
  return (
    <div style={{ display: "grid", gap: 6 }}>
      <Typography.Text strong>{title}</Typography.Text>
      {grants.map((grant, index) => (
        <Typography.Text key={`${grant.grantId}:${index}`} style={{ fontSize: 12 }}>
          {grant.displayName} · {grant.permission}
        </Typography.Text>
      ))}
    </div>
  );
}

function TeamAutomationPermissionReviewPanel({
  consentChecked,
  error,
  onConsentChange,
  review,
  stage,
}: {
  readonly consentChecked: boolean;
  readonly error: string;
  readonly onConsentChange: (checked: boolean) => void;
  readonly review: TeamAutomationPermissionReview | null;
  readonly stage: TeamAutomationCreateStage;
}) {
  const intl = useIntl();
  const { token } = theme.useToken();

  return (
    <div
      style={{
        ...modalSectionStyle,
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
      }}
    >
      <div style={{ display: "grid", gap: 2 }}>
        <Typography.Text strong>
          {intl.formatMessage({
            id: "teams.automations.form.section.permissionReview",
            defaultMessage: "4. Review Agent Key consent",
          })}
        </Typography.Text>
        <Typography.Text style={{ fontSize: 12 }} type="secondary">
          {intl.formatMessage({
            id: "teams.automations.form.section.permissionReviewHint",
            defaultMessage:
              "Review the backend authorization facts before confirming credential provisioning.",
          })}
        </Typography.Text>
      </div>

      {stage === "preflight" ? (
        <div
          style={{
            background: token.colorFillQuaternary,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 10,
            padding: 12,
          }}
        >
          <Skeleton active paragraph={{ rows: 2 }} title={false} />
        </div>
      ) : null}

      {stage === "error" ? (
        <div
          role="alert"
          style={{
            background: token.colorErrorBg,
            border: `1px solid ${token.colorErrorBorder}`,
            borderRadius: 10,
            color: token.colorErrorText,
            display: "grid",
            gap: 4,
            padding: 12,
          }}
        >
          <Typography.Text strong>
            {intl.formatMessage({
              id: "teams.automations.form.reviewErrorTitle",
              defaultMessage: "Permission review needs attention",
            })}
          </Typography.Text>
          <Typography.Text style={{ color: token.colorErrorText }}>
            {error ||
              intl.formatMessage({
                id: "teams.automations.form.reviewErrorBody",
                defaultMessage:
                  "The authorization service could not prepare the review. Keep the draft and try again.",
              })}
          </Typography.Text>
        </div>
      ) : null}

      {review ? (
        <div style={{ display: "grid", gap: 12 }}>
          {stage === "planChanged" ? (
            <div
              role="status"
              style={{
                background: token.colorWarningBg,
                border: `1px solid ${token.colorWarningBorder}`,
                borderRadius: 10,
                color: token.colorWarningText,
                padding: 12,
              }}
            >
              <Typography.Text style={{ color: token.colorWarningText }}>
                {review.warning ||
                  intl.formatMessage({
                    id: "teams.automations.form.planChanged",
                    defaultMessage:
                      "The authorization plan changed. Refresh the review before creating.",
                  })}
              </Typography.Text>
            </div>
          ) : null}

          {review.status === "ready" ? <div
            style={{
              background: token.colorFillQuaternary,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 10,
              display: "grid",
              gap: 10,
              padding: 12,
            }}
          >
            <div style={{ display: "grid", gap: 4 }}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.agentKeyPlan",
                  defaultMessage: "Automation dedicated Agent Key",
                })}
              </Typography.Text>
              <FactLine
                text={intl.formatMessage(
                  {
                    id: "teams.automations.form.agentKeyMode",
                    defaultMessage: "Credential mode · {mode}",
                  },
                  { mode: review.credentialPlan.mode },
                )}
              />
              <FactLine text={`NyxID scopes · ${review.credentialPlan.scopes.join(" ")}`} />
              <FactLine text="Exact service allowlist · allow all disabled" />
              <FactLine text="Exact node allowlist · allow all disabled" />
              {review.disclosures.map((disclosure, index) => (
                <FactLine
                  key={`${disclosure}:${index}`}
                  text={formatDisclosure(disclosure, intl)}
                />
              ))}
              <FactLine
                text={intl.formatMessage(
                  {
                    id: "teams.automations.form.agentKeyExpiry",
                    defaultMessage: "Expires {time}",
                  },
                  { time: formatScheduleTime(review.credentialPlan.expiresAt, "--") },
                )}
              />
              <FactLine
                text={intl.formatMessage(
                  {
                    id: "teams.automations.form.permissionDigest",
                    defaultMessage: "Permission digest · {permissionDigest}",
                  },
                  { permissionDigest: review.permissionDigest },
                )}
              />
              <FactLine
                text={intl.formatMessage(
                  {
                    id: "teams.automations.form.policyVersion",
                    defaultMessage: "Policy version · {policyVersion}",
                  },
                  { policyVersion: review.policyVersion },
                )}
              />
            </div>
            <div
              className="team-automation-form-schedule-grid"
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "minmax(0, 1fr) minmax(0, 1fr)",
              }}
            >
              <TeamAutomationGrantList
                grants={review.serviceGrants}
                title={intl.formatMessage({
                  id: "teams.automations.form.serviceGrants",
                  defaultMessage: "Service grants",
                })}
              />
              <TeamAutomationGrantList
                grants={review.nodeGrants}
                title={intl.formatMessage({
                  id: "teams.automations.form.nodeGrants",
                  defaultMessage: "Node grants",
                })}
              />
            </div>
          </div> : null}

          {review.status === "ready" &&
          (stage === "permissionReview" || stage === "consent") ? (
            <div style={{ display: "grid", gap: 6 }}>
              <Checkbox
                checked={consentChecked}
                onChange={(event) => onConsentChange(event.target.checked)}
              >
                {intl.formatMessage({
                  id: "teams.automations.form.agentKeyConsent",
                  defaultMessage:
                    "I consent to Aevatar creating an automation-dedicated Agent Key for this schedule.",
                })}
              </Checkbox>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.form.previewOnlyNotice",
                  defaultMessage:
                    "No automation or Agent Key is created until you confirm this review.",
                })}
              </Typography.Text>
            </div>
          ) : null}
        </div>
      ) : stage !== "preflight" && stage !== "error" ? (
        <Typography.Text style={{ fontSize: 12 }} type="secondary">
          {intl.formatMessage({
            id: "teams.automations.form.reviewPlaceholder",
            defaultMessage:
              "Review is prepared after the draft cadence and target are ready.",
          })}
        </Typography.Text>
      ) : null}
    </div>
  );
}

const TeamAutomationsTab: React.FC<TeamAutomationsTabProps> = ({
  members = [],
  scopeId,
  teamId,
}) => {
  const intl = useIntl();
  const queryClient = useQueryClient();
  const { token } = theme.useToken();
  const automatableMembers = React.useMemo(
    () => members.filter((member) => member.canAutomateMember),
    [members],
  );
  const selectedMember =
    automatableMembers.find((member) => member.isSelectedMember) ?? null;
  const unavailableMembers = members.filter((member) => !member.canAutomateMember);
  const [createOpen, setCreateOpen] = React.useState(false);
  const [editingSchedule, setEditingSchedule] =
    React.useState<TeamAutomationView | null>(null);
  const [reauthorizingSchedule, setReauthorizingSchedule] =
    React.useState<TeamAutomationView | null>(null);
  const [preview, setPreview] = React.useState<ScheduledDispatchPreview | null>(null);
  const [formState, setFormState] = React.useState<AutomationFormState>(() =>
    buildDefaultAutomationFormState(),
  );
  const {
    consentChecked: agentKeyConsentChecked,
    error: createReviewError,
    mutation: permissionReviewMutation,
    reset: resetPermissionReview,
    review: permissionReview,
    reviewedDraft,
    setConsent: setAgentKeyConsentChecked,
    stage: createStage,
  } = useTeamAutomationPermissionReview();
  const [preservedCreateDraft, setPreservedCreateDraft] =
    React.useState<AutomationFormState | null>(null);
  const automationRoute = React.useMemo<TeamAutomationRoute | null>(
    () =>
      selectedMember
        ? { scopeId, teamId, memberId: selectedMember.memberId }
        : null,
    [scopeId, selectedMember, teamId],
  );
  const operationIdentitiesRef = React.useRef(
    new Map<string, TeamAutomationOperationIdentity>(),
  );
  const acceptedLifecycleOperationsRef = React.useRef(
    new Map<string, AcceptedLifecycleOperation>(),
  );
  const resolveOperationIdentity = React.useCallback((intentKey: string) => {
    const existing = operationIdentitiesRef.current.get(intentKey);
    if (existing) {
      return existing;
    }
    const created = createTeamAutomationOperationIdentity();
    operationIdentitiesRef.current.set(intentKey, created);
    return created;
  }, []);
  const releaseOperationIdentity = React.useCallback((intentKey: string) => {
    operationIdentitiesRef.current.delete(intentKey);
    acceptedLifecycleOperationsRef.current.delete(intentKey);
  }, []);
  const rememberAcceptedLifecycleOperation = React.useCallback(
    (
      kind: LifecycleOperationKind,
      intentKey: string,
      receipt: TeamAutomationMutationReceipt,
    ) => {
      const identity = operationIdentitiesRef.current.get(intentKey);
      if (!identity) {
        return;
      }
      acceptedLifecycleOperationsRef.current.set(intentKey, {
        identity,
        intentKey,
        kind,
        scheduleId: receipt.scheduleId,
      });
    },
    [],
  );
  const scheduleQueryKey = React.useMemo(
    () => ["team-automations", scopeId, teamId, selectedMember?.memberId ?? ""] as const,
    [scopeId, selectedMember?.memberId, teamId],
  );
  const findMemberForSchedule = React.useCallback(
    (schedule: TeamAutomationView): TeamAutomationMemberRow | undefined =>
      selectedMember?.memberId === schedule.memberId ? selectedMember : undefined,
    [selectedMember],
  );
  const schedulesQuery = useQuery({
    enabled: Boolean(automationRoute),
    queryFn: () => {
      if (!automationRoute) {
        throw new Error("A canonical Team member route is required.");
      }
      return teamAutomationApi.listAll(automationRoute, { take: scheduleListTake });
    },
    queryKey: scheduleQueryKey,
    refetchInterval: (query) => {
      const items = query.state.data?.items ?? [];
      return items.some((item) =>
        [
          "provisioning_pending",
          "replacement_pending",
          "deleting",
          "revocation_pending",
        ].includes(item.authorizationStatus),
      )
        ? 1_000
        : 10_000;
    },
    refetchIntervalInBackground: false,
    retry: (failureCount) => failureCount < scheduleListRetryLimit,
    retryDelay: scheduleListRetryDelay,
  });
  const teamSchedules = React.useMemo(
    () => [...(schedulesQuery.data?.items ?? [])].sort(sortByNextFire),
    [schedulesQuery.data?.items],
  );
  React.useEffect(() => {
    for (const operation of acceptedLifecycleOperationsRef.current.values()) {
      const schedule = teamSchedules.find(
        (item) => item.scheduleId === operation.scheduleId,
      );
      if (!schedule || schedule.operationId !== operation.identity.operationId) {
        continue;
      }
      const stillPending =
        schedule.revocationPending ||
        [
          "provisioning_pending",
          "replacement_pending",
          "deleting",
          "revocation_pending",
        ].includes(schedule.authorizationStatus);
      if (!stillPending) {
        releaseOperationIdentity(operation.intentKey);
      }
    }
  }, [releaseOperationIdentity, teamSchedules]);
  const activeFormMember =
    selectedMember?.memberId === formState.memberId ? selectedMember : null;
  const editingScheduleId = trimText(editingSchedule?.scheduleId);
  const isEditingAutomation = editingScheduleId.length > 0;
  const reauthorizingScheduleId = trimText(reauthorizingSchedule?.scheduleId);
  const isReauthorizingAutomation = reauthorizingScheduleId.length > 0;
  const trimmedPromptLength = formState.prompt.trim().length;
  const promptTooLong =
    trimmedPromptLength > scheduledWorkflowPromptMaxLength;
  const cronPresets = React.useMemo(
    () => [
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.weekdaysMorning",
          defaultMessage: "Weekdays · 09:00",
        }),
        value: defaultPreset,
        cronExpression: defaultCronExpression,
      },
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.dailyMorning",
          defaultMessage: "Daily · 09:00",
        }),
        value: "daily-0900",
        cronExpression: "0 9 * * *",
      },
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.weeklyMonday",
          defaultMessage: "Monday · 09:00",
        }),
        value: "weekly-monday-0900",
        cronExpression: "0 9 * * 1",
      },
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.hourly",
          defaultMessage: "Hourly",
        }),
        value: "hourly",
        cronExpression: "0 * * * *",
      },
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.custom",
          defaultMessage: "Custom cron",
        }),
        value: customPreset,
        cronExpression: "",
      },
    ],
    [intl],
  );

  const invalidateSchedules = React.useCallback(async () => {
    await queryClient.invalidateQueries({
      queryKey: scheduleQueryKey,
    });
  }, [queryClient, scheduleQueryKey]);

  const previewMutation = useMutation({
    mutationFn: scheduledDispatchApi.preview,
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.previewFailed",
            defaultMessage: "Preview failed: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: (result) => {
      setPreview(result);
    },
  });
  const requireAutomationRoute = React.useCallback((): TeamAutomationRoute => {
    if (!automationRoute) {
      throw new Error("Select a Team member before using automations.");
    }
    return automationRoute;
  }, [automationRoute]);
  const updateMutation = useMutation({
    mutationFn: ({
      input,
      scheduleId,
    }: {
      readonly input: TeamAutomationUpdateInput;
      readonly scheduleId: string;
    }) => {
      const route = requireAutomationRoute();
      const intentKey = buildOperationIntentKey("update", route, scheduleId);
      return teamAutomationApi.update(
        route,
        scheduleId,
        input,
        resolveOperationIdentity(intentKey),
      );
    },
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.updateFailed",
            defaultMessage: "Automation was not updated: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async (_receipt, variables) => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.updateSuccess",
          defaultMessage: "Update accepted. Waiting for committed state.",
        }),
      );
      setCreateOpen(false);
      setEditingSchedule(null);
      setPreview(null);
      await invalidateSchedules();
      releaseOperationIdentity(
        buildOperationIntentKey(
          "update",
          requireAutomationRoute(),
          variables.scheduleId,
        ),
      );
    },
  });
  const createMutation = useMutation({
    mutationFn: ({
      draft,
      permissionDigest,
      policyVersion,
    }: {
      readonly draft: TeamAutomationCreateDraft;
      readonly permissionDigest: string;
      readonly policyVersion: string;
    }) => {
      const intentKey = buildOperationIntentKey(
        "create",
        draft,
        "",
        permissionDigest,
      );
      return teamAutomationApi.create(
        draft,
        permissionDigest,
        policyVersion,
        resolveOperationIdentity(intentKey),
      );
    },
    onError: (error, variables) => {
      if (isAuthorizationPlanChanged(error)) {
        releaseOperationIdentity(
          buildOperationIntentKey(
            "create",
            variables.draft,
            "",
            variables.permissionDigest,
          ),
        );
        resetPermissionReview();
      }
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.createFailed",
            defaultMessage: "Automation was not created: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async (receipt, variables) => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.createAccepted",
          defaultMessage: "Automation creation was accepted. Waiting for committed state.",
        }),
      );
      setCreateOpen(false);
      setPreservedCreateDraft(null);
      setFormState(buildDefaultAutomationFormState(selectedMember?.memberId ?? ""));
      resetPermissionReview();
      rememberAcceptedLifecycleOperation(
        "create",
        buildOperationIntentKey(
          "create",
          variables.draft,
          "",
          variables.permissionDigest,
        ),
        receipt,
      );
      await invalidateSchedules();
    },
  });
  const reauthorizeMutation = useMutation({
    mutationFn: ({
      draft,
      permissionDigest,
      policyVersion,
      scheduleId,
    }: {
      readonly draft: TeamAutomationCreateDraft;
      readonly permissionDigest: string;
      readonly policyVersion: string;
      readonly scheduleId: string;
    }) => {
      const route = requireAutomationRoute();
      const intentKey = buildOperationIntentKey(
        "reauthorize",
        route,
        scheduleId,
        permissionDigest,
      );
      return teamAutomationApi.reauthorize(
        route,
        scheduleId,
        draft,
        permissionDigest,
        policyVersion,
        resolveOperationIdentity(intentKey),
      );
    },
    onError: (error, variables) => {
      if (isAuthorizationPlanChanged(error)) {
        releaseOperationIdentity(
          buildOperationIntentKey(
            "reauthorize",
            requireAutomationRoute(),
            variables.scheduleId,
            variables.permissionDigest,
          ),
        );
        resetPermissionReview();
      }
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.reauthorizeFailed",
            defaultMessage: "Authorization was not replaced: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async (receipt, variables) => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.reauthorizeAccepted",
          defaultMessage: "Re-authorization was accepted. Waiting for committed state.",
        }),
      );
      setCreateOpen(false);
      setReauthorizingSchedule(null);
      setFormState(buildDefaultAutomationFormState(selectedMember?.memberId ?? ""));
      resetPermissionReview();
      rememberAcceptedLifecycleOperation(
        "reauthorize",
        buildOperationIntentKey(
          "reauthorize",
          requireAutomationRoute(),
          variables.scheduleId,
          variables.permissionDigest,
        ),
        receipt,
      );
      await invalidateSchedules();
    },
  });
  const runNowMutation = useMutation({
    mutationFn: (scheduleId: string) => {
      const route = requireAutomationRoute();
      const intentKey = buildOperationIntentKey("run-now", route, scheduleId);
      return teamAutomationApi.runNow(
        route,
        scheduleId,
        resolveOperationIdentity(intentKey),
      );
    },
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.runNowFailed",
            defaultMessage: "Run request failed: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async (_receipt, scheduleId) => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.runNowSuccess",
          defaultMessage: "Run request accepted.",
        }),
      );
      await invalidateSchedules();
      releaseOperationIdentity(
        buildOperationIntentKey("run-now", requireAutomationRoute(), scheduleId),
      );
    },
  });
  const resumeMutation = useMutation({
    mutationFn: (scheduleId: string) => {
      const route = requireAutomationRoute();
      const intentKey = buildOperationIntentKey("resume", route, scheduleId);
      return teamAutomationApi.resume(
        route,
        scheduleId,
        resolveOperationIdentity(intentKey),
      );
    },
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.resumeFailed",
            defaultMessage: "Automation was not resumed: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async (_receipt, scheduleId) => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.enableSuccess",
          defaultMessage: "Resume accepted. Waiting for committed state.",
        }),
      );
      await invalidateSchedules();
      releaseOperationIdentity(
        buildOperationIntentKey("resume", requireAutomationRoute(), scheduleId),
      );
    },
  });
  const pauseMutation = useMutation({
    mutationFn: (scheduleId: string) => {
      const route = requireAutomationRoute();
      const intentKey = buildOperationIntentKey("pause", route, scheduleId);
      return teamAutomationApi.pause(
        route,
        scheduleId,
        resolveOperationIdentity(intentKey),
      );
    },
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.pauseFailed",
            defaultMessage: "Automation was not paused: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async (_receipt, scheduleId) => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.disableSuccess",
          defaultMessage: "Pause accepted. Waiting for committed state.",
        }),
      );
      await invalidateSchedules();
      releaseOperationIdentity(
        buildOperationIntentKey("pause", requireAutomationRoute(), scheduleId),
      );
    },
  });
  const deleteMutation = useMutation({
    mutationFn: (scheduleId: string) => {
      const route = requireAutomationRoute();
      const intentKey = buildOperationIntentKey("delete", route, scheduleId);
      return teamAutomationApi.delete(
        route,
        scheduleId,
        resolveOperationIdentity(intentKey),
      );
    },
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.deleteFailed",
            defaultMessage: "Deletion was not accepted: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async (receipt, scheduleId) => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.deleteSuccess",
          defaultMessage: "Deletion accepted. Waiting for credential revocation.",
        }),
      );
      rememberAcceptedLifecycleOperation(
        "delete",
        buildOperationIntentKey(
          "delete",
          requireAutomationRoute(),
          scheduleId,
        ),
        receipt,
      );
      await invalidateSchedules();
    },
  });

  const openCreate = React.useCallback(() => {
    const member = selectedMember;
    if (!member) {
      return;
    }
    setEditingSchedule(null);
    setReauthorizingSchedule(null);
    setFormState(
      preservedCreateDraft?.memberId === member.memberId
        ? preservedCreateDraft
        : buildDefaultAutomationFormState(member.memberId),
    );
    setPreview(null);
    resetPermissionReview();
    setCreateOpen(true);
  }, [preservedCreateDraft, resetPermissionReview, selectedMember]);

  const openEdit = React.useCallback(
    (schedule: TeamAutomationView) => {
      const member =
        findMemberForSchedule(schedule) ?? selectedMember;
      const cronExpression = trimText(schedule.cronExpression);
      const preset =
        cronPresets.find((item) => item.cronExpression === cronExpression)?.value ??
        customPreset;
      setEditingSchedule(schedule);
      setReauthorizingSchedule(null);
      setFormState({
        cronExpression,
        displayName: trimText(schedule.displayName),
        enabled: schedule.enabled,
        memberId: member?.memberId ?? "",
        preset,
        prompt: trimText(schedule.prompt),
        timezone: trimText(schedule.timezone) || resolveDefaultTimezone(),
      });
      setPreview(null);
      resetPermissionReview();
      setCreateOpen(true);
    },
    [cronPresets, findMemberForSchedule, resetPermissionReview, selectedMember],
  );

  const openReauthorize = React.useCallback(
    (schedule: TeamAutomationView) => {
      const member = findMemberForSchedule(schedule) ?? selectedMember;
      if (!member) {
        return;
      }
      const cronExpression = trimText(schedule.cronExpression);
      const preset =
        cronPresets.find((item) => item.cronExpression === cronExpression)?.value ??
        customPreset;
      setEditingSchedule(null);
      setReauthorizingSchedule(schedule);
      setFormState({
        cronExpression,
        displayName: trimText(schedule.displayName),
        enabled: schedule.enabled,
        memberId: member.memberId,
        preset,
        prompt: trimText(schedule.prompt),
        timezone: trimText(schedule.timezone) || resolveDefaultTimezone(),
      });
      setPreview(null);
      resetPermissionReview();
      setCreateOpen(true);
    },
    [cronPresets, findMemberForSchedule, resetPermissionReview, selectedMember],
  );

  const openScheduleRuns = React.useCallback(
    (member: TeamAutomationMemberRow | undefined, scheduleId: string) => {
      const normalizedScheduleId = trimText(scheduleId);
      const routeMemberId = trimText(member?.memberId);
      if (!member?.workflowSupported || !routeMemberId || !normalizedScheduleId) {
        return;
      }

      history.push(
        buildTeamMemberPublishedRunsHref({
          memberId: routeMemberId,
          scheduleId: normalizedScheduleId,
          scopeId,
          teamId,
        }),
      );
    },
    [scopeId, teamId],
  );

  const updateForm = React.useCallback(
    (patch: Partial<AutomationFormState>) => {
      setFormState((current) => ({
        ...current,
        ...patch,
      }));
      setPreview(null);
      resetPermissionReview();
    },
    [resetPermissionReview],
  );

  const previewNextRuns = React.useCallback(async () => {
    const cronExpression = formState.cronExpression.trim();
    if (!cronExpression) {
      void message.error(
        intl.formatMessage({
          id: "teams.automations.messages.cronRequired",
          defaultMessage: "Enter a cron expression first.",
        }),
      );
      return;
    }

    await previewMutation.mutateAsync({
      cronExpression,
      timezone: trimText(formState.timezone) || undefined,
      count: 5,
    });
  }, [
    formState.cronExpression,
    formState.timezone,
    intl,
    previewMutation,
  ]);

  const handlePreviewNextRuns = React.useCallback(() => {
    previewNextRuns().catch(() => {
      // The mutation onError path owns the user-visible failure message.
    });
  }, [previewNextRuns]);

  const validateAutomationDraft = React.useCallback(() => {
    const member = activeFormMember;
    const prompt = formState.prompt.trim();
    const cronExpression = formState.cronExpression.trim();
    if (!member || !trimText(member.serviceId) || member.serviceId === "--") {
      void message.error(
        intl.formatMessage({
          id: "teams.automations.messages.serviceIdentityMissing",
          defaultMessage:
            "The selected member does not have a published service identity yet.",
        }),
      );
      return null;
    }
    if (prompt.length > scheduledWorkflowPromptMaxLength) {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.promptTooLong",
            defaultMessage:
              "Recurring prompt must be {maxLength} characters or fewer.",
          },
          { maxLength: scheduledWorkflowPromptMaxLength },
        ),
      );
      return null;
    }
    if (!cronExpression) {
      void message.error(
        intl.formatMessage({
          id: "teams.automations.messages.cronRequired",
          defaultMessage: "Enter a cron expression first.",
        }),
      );
      return null;
    }

    return {
      cronExpression,
      displayName:
        formState.displayName.trim() ||
        intl.formatMessage(
          {
            id: "teams.automations.form.defaultTitle",
            defaultMessage: "{memberName} recurring work",
          },
          { memberName: member.name },
        ),
      member,
      prompt,
      timezone: trimText(formState.timezone) || undefined,
    };
  }, [activeFormMember, formState, intl]);

  const saveAutomation = React.useCallback(async () => {
    if (permissionReviewMutation.isPending) {
      return;
    }

    const validatedDraft = validateAutomationDraft();
    if (!validatedDraft) {
      return;
    }
    const {
      cronExpression,
      displayName,
      member,
      prompt,
      timezone,
    } = validatedDraft;
    if (isEditingAutomation) {
      await updateMutation.mutateAsync({
        input: buildTeamAutomationEditInput({
          displayName,
          cronExpression,
          timezone,
          enabled: formState.enabled,
          prompt,
        }),
        scheduleId: editingScheduleId,
      });
      return;
    }

    const draft = buildTeamAutomationCreateDraft({
      scopeId,
      teamId,
      member,
      displayName,
      prompt,
      cronExpression,
      timezone,
      enabled: formState.enabled,
    });

    if (
      !permissionReview ||
      createStage === "draft" ||
      createStage === "error" ||
      createStage === "planChanged"
    ) {
      await permissionReviewMutation.mutateAsync(draft);
      return;
    }

    if (!agentKeyConsentChecked || !reviewedDraft) {
      return;
    }

    if (isReauthorizingAutomation) {
      await reauthorizeMutation.mutateAsync({
        draft: reviewedDraft,
        permissionDigest: permissionReview.permissionDigest,
        policyVersion: permissionReview.policyVersion,
        scheduleId: reauthorizingScheduleId,
      });
      return;
    }

    await createMutation.mutateAsync({
      draft: reviewedDraft,
      permissionDigest: permissionReview.permissionDigest,
      policyVersion: permissionReview.policyVersion,
    });
  }, [
    agentKeyConsentChecked,
    createMutation,
    createStage,
    editingScheduleId,
    formState.enabled,
    isEditingAutomation,
    isReauthorizingAutomation,
    permissionReview,
    permissionReviewMutation,
    reauthorizeMutation,
    reauthorizingScheduleId,
    reviewedDraft,
    scopeId,
    teamId,
    updateMutation,
    validateAutomationDraft,
  ]);

  const handleSaveAutomation = React.useCallback(() => {
    saveAutomation().catch(() => {
      // The mutation onError path owns the user-visible failure message.
    });
  }, [saveAutomation]);

  const renderStatusPill = (
    schedule: TeamAutomationView,
  ) => {
    const status = resolveScheduleStatus(schedule);
    const statusStyle =
      status === "error"
        ? {
            background: token.colorErrorBg,
            border: `1px solid ${token.colorErrorBorder}`,
            color: token.colorError,
          }
        : status === "pending"
          ? {
              background: token.colorInfoBg,
              border: `1px solid ${token.colorInfoBorder}`,
              color: token.colorInfo,
            }
        : status === "paused" || status === "needsAuthorization" || status === "revocationPending"
          ? {
              background: token.colorWarningBg,
              border: `1px solid ${token.colorWarningBorder}`,
              color: token.colorWarning,
            }
          : {
              background: token.colorSuccessBg,
              border: `1px solid ${token.colorSuccessBorder}`,
              color: token.colorSuccess,
            };
    const statusLabel =
      status === "error"
        ? intl.formatMessage({
            id: "teams.automations.status.error",
            defaultMessage: "Error",
          })
        : status === "pending"
          ? intl.formatMessage({
              id: "teams.automations.status.pending",
              defaultMessage: "Authorizing",
            })
        : status === "needsAuthorization"
          ? intl.formatMessage({
              id: "teams.automations.status.needsAuthorization",
              defaultMessage: "Needs authorization",
            })
        : status === "revocationPending"
          ? intl.formatMessage({
              id: "teams.automations.status.revocationPending",
              defaultMessage: "Revocation pending",
            })
        : status === "paused"
          ? intl.formatMessage({
              id: "teams.automations.status.paused",
              defaultMessage: "Paused",
            })
          : intl.formatMessage({
              id: "teams.automations.status.active",
              defaultMessage: "Active",
            });

    return (
      <span
        aria-label={statusLabel}
        role="status"
        style={{
          ...automationStatusBadgeStyle,
          ...statusStyle,
        }}
      >
        <span
          aria-hidden="true"
          style={{
            ...automationStatusDotStyle,
            background: "currentColor",
          }}
        />
        {statusLabel}
      </span>
    );
  };

  const renderSummaryTile = ({
    icon,
    label,
    tone,
    value,
  }: {
    readonly icon: React.ReactNode;
    readonly label: string;
    readonly tone: "error" | "success" | "warning";
    readonly value: number;
  }) => {
    const toneStyle =
      tone === "error" && value === 0
        ? {
            background: token.colorFillQuaternary,
            border: `1px solid ${token.colorBorderSecondary}`,
            color: token.colorTextSecondary,
          }
        : tone === "error"
          ? {
              background: token.colorErrorBg,
              border: `1px solid ${token.colorErrorBorder}`,
              color: token.colorError,
            }
          : tone === "warning"
          ? {
              background: token.colorWarningBg,
              border: `1px solid ${token.colorWarningBorder}`,
              color: token.colorWarning,
            }
          : {
              background: token.colorSuccessBg,
              border: `1px solid ${token.colorSuccessBorder}`,
              color: token.colorSuccess,
            };

    return (
      <div style={{ ...automationSummaryTileStyle, ...toneStyle }}>
        <span
          style={{
            alignItems: "center",
            display: "inline-flex",
            fontSize: 16,
          }}
        >
          {icon}
        </span>
        <div style={{ display: "grid", gap: 2, minWidth: 0 }}>
          <Typography.Text
            style={{ color: "inherit", fontSize: 20, lineHeight: 1 }}
            strong
          >
            {value}
          </Typography.Text>
          <Typography.Text
            ellipsis
            style={{ color: "inherit", fontSize: 12, opacity: 0.82 }}
          >
            {label}
          </Typography.Text>
        </div>
      </div>
    );
  };

  const buildAutomationActionButtonStyle = (
    tone: "default" | "danger" | "primary" = "default",
  ): React.CSSProperties => {
    if (tone === "danger") {
      return {
        ...automationActionButtonBaseStyle,
        background: token.colorErrorBg,
        color: token.colorError,
      };
    }

    if (tone === "primary") {
      return {
        ...automationActionButtonBaseStyle,
        background: token.colorPrimaryBg,
        color: token.colorPrimary,
      };
    }

    return {
      ...automationActionButtonBaseStyle,
      background: token.colorBgContainer,
      color: token.colorTextSecondary,
    };
  };

  const renderAutomationActionButton = ({
    danger,
    disabled,
    icon,
    label,
    loading,
    onClick,
    primary,
  }: {
    readonly danger?: boolean;
    readonly disabled?: boolean;
    readonly icon: React.ReactNode;
    readonly label: string;
    readonly loading?: boolean;
    readonly onClick: () => void;
    readonly primary?: boolean;
  }) => (
    <Tooltip title={label}>
      <Button
        className="team-automation-action-button"
        aria-label={label}
        danger={danger}
        disabled={disabled}
        icon={icon}
        loading={loading}
        onClick={onClick}
        size="small"
        style={buildAutomationActionButtonStyle(
          danger ? "danger" : primary ? "primary" : "default",
        )}
      />
    </Tooltip>
  );

  const renderAutomationRows = () => {
    if (!selectedMember) {
      return (
        <div style={{ display: "grid", gap: 12 }}>
          <AevatarInspectorEmpty
            compact
            title={intl.formatMessage({
              id: "teams.automations.member.chooseTitle",
              defaultMessage: "Choose a member",
            })}
            description={intl.formatMessage({
              id: "teams.automations.member.chooseDescription",
              defaultMessage:
                "Open a member's automation surface to view or change its recurring work.",
            })}
          />
          {automatableMembers.map((member) => (
            <Button
              key={member.memberId}
              onClick={() => history.push(member.automationsHref)}
            >
              {member.name}
            </Button>
          ))}
        </div>
      );
    }

    if (schedulesQuery.isLoading) {
      return (
        <div style={{ display: "grid", gap: 12 }}>
          {[0, 1, 2].map((index) => (
            <Skeleton.Input
              active
              block
              key={index}
              style={{ borderRadius: 8, height: 78 }}
            />
          ))}
        </div>
      );
    }

    if (schedulesQuery.isError) {
      return (
        <AevatarInspectorEmpty
          compact
          title={intl.formatMessage({
            id: "teams.automations.error.title",
            defaultMessage: "Automations could not load",
          })}
          description={intl.formatMessage({
            id: "teams.automations.error.description",
            defaultMessage:
              "Refresh the page or try again after the schedule service is available.",
          })}
        />
      );
    }

    if (teamSchedules.length === 0) {
      return (
        <div
          style={{
            background: token.colorFillQuaternary,
            border: `1px dashed ${token.colorBorder}`,
            borderRadius: 14,
            display: "grid",
            gap: 14,
            justifyItems: "center",
            padding: "28px 18px",
            textAlign: "center",
          }}
        >
          <AevatarInspectorEmpty
            compact
            title={intl.formatMessage({
              id: "teams.automations.empty.title",
              defaultMessage: "No recurring work yet",
            })}
            description={intl.formatMessage({
              id: "teams.automations.empty.description",
              defaultMessage:
                "Create an automation from a published member so this team has visible recurring commitments.",
            })}
          />
          <Space direction="vertical" size={8}>
            <Button
              disabled={!selectedMember}
              icon={<PlusOutlined />}
              onClick={openCreate}
              style={primaryHeaderButtonStyle}
              type="primary"
            >
              {intl.formatMessage({
                id: "teams.automations.empty.createFirst",
                defaultMessage: "Create first automation",
              })}
            </Button>
            {!selectedMember ? (
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.empty.publishHint",
                  defaultMessage:
                    "Publish a workflow member before scheduling recurring work.",
                })}
              </Typography.Text>
            ) : null}
          </Space>
        </div>
      );
    }

    const activeCount = teamSchedules.filter(
      (schedule) => resolveScheduleStatus(schedule) === "active",
    ).length;
    const pausedCount = teamSchedules.filter(
      (schedule) => resolveScheduleStatus(schedule) === "paused",
    ).length;
    const errorCount = teamSchedules.filter(
      (schedule) =>
        ["error", "needsAuthorization", "revocationPending"].includes(
          resolveScheduleStatus(schedule),
        ),
    ).length;

    return (
      <div style={commitmentGridStyle}>
        <div className="team-automation-summary" style={automationSummaryGridStyle}>
          {renderSummaryTile({
            icon: <CheckCircleOutlined />,
            label: intl.formatMessage({
              id: "teams.automations.summary.active",
              defaultMessage: "Active",
            }),
            tone: "success",
            value: activeCount,
          })}
          {renderSummaryTile({
            icon: <PauseCircleOutlined />,
            label: intl.formatMessage({
              id: "teams.automations.summary.paused",
              defaultMessage: "Paused",
            }),
            tone: "warning",
            value: pausedCount,
          })}
          {renderSummaryTile({
            icon: <ExclamationCircleOutlined />,
            label: intl.formatMessage({
              id: "teams.automations.summary.needsAttention",
              defaultMessage: "Need attention",
            }),
            tone: "error",
            value: errorCount,
          })}
        </div>
        <div
          className="team-automation-list-header"
          style={automationListHeaderStyle}
        >
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {intl.formatMessage({
              id: "teams.automations.columns.automation",
              defaultMessage: "Automation",
            })}
          </Typography.Text>
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {intl.formatMessage({
              id: "teams.automations.columns.member",
              defaultMessage: "Member",
            })}
          </Typography.Text>
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {intl.formatMessage({
              id: "teams.automations.columns.schedule",
              defaultMessage: "Schedule",
            })}
          </Typography.Text>
          <Typography.Text
            style={{ fontSize: 12, justifySelf: "end" }}
            type="secondary"
          >
            {intl.formatMessage({
              id: "teams.automations.columns.actions",
              defaultMessage: "Actions",
            })}
          </Typography.Text>
        </div>
        {teamSchedules.map((schedule) => {
          const member = findMemberForSchedule(schedule);
          const scheduleId = trimText(schedule.scheduleId);
          const scheduleCadence = describeCronExpression(
            schedule.cronExpression,
            intl,
            schedule.timezone,
          );
          const statusMutation =
            schedule.enabled ? pauseMutation : resumeMutation;
          const status = resolveScheduleStatus(schedule);
          const canViewRuns = Boolean(member?.workflowSupported && scheduleId);
          const mutationLocked = status === "pending" || status === "revocationPending";
          const rowBorderColor =
            status === "error" || status === "needsAuthorization"
                ? token.colorErrorBorder
                : status === "pending"
                  ? token.colorInfoBorder
                : status === "paused" || status === "revocationPending"
                  ? token.colorWarningBorder
                  : token.colorBorderSecondary;
          const rowBackground = token.colorBgContainer;
          const rowShadow = token.boxShadowTertiary;

          const scheduleSecondaryText =
            status === "pending"
              ? intl.formatMessage({
                  id: "teams.automations.row.awaitingReadModel",
                  defaultMessage: "Waiting for committed authorization state",
                })
              : status === "revocationPending"
                ? intl.formatMessage({
                    id: "teams.automations.row.revocationPending",
                    defaultMessage: "Credential revocation is still converging",
                  })
              : schedule.nextFireAt
              ? intl.formatMessage(
                  {
                    id: "teams.automations.row.nextRun",
                    defaultMessage: "Next {time}",
                  },
                  {
                    time: formatScheduleTime(schedule.nextFireAt, "--"),
                  },
                )
              : intl.formatMessage({
                    id: "teams.automations.row.noNextRun",
                    defaultMessage: "No next run",
                  });

          const rowAriaLabel =
            schedule.lastAuthorizationErrorCode
              ? `${schedule.displayName} ${schedule.lastAuthorizationErrorCode}`
              : schedule.displayName;

          return (
            <article
              aria-label={rowAriaLabel}
              className="team-automation-row"
              key={scheduleId}
              style={{
                ...commitmentRowStyle,
                background: rowBackground,
                border: `1px solid ${rowBorderColor}`,
                borderRadius: 12,
                boxShadow: rowShadow,
              }}
            >
              <div
                className="team-automation-row__automation"
                style={{ display: "grid", gap: 7, minWidth: 0 }}
              >
                <div style={automationNameLineStyle}>
                  {renderStatusPill(schedule)}
                  <Typography.Text ellipsis strong>
                    {trimText(schedule.displayName) ||
                      intl.formatMessage({
                        id: "teams.automations.untitled",
                        defaultMessage: "Untitled automation",
                      })}
                  </Typography.Text>
                </div>
                <FactLine
                  secondary
                  text={intl.formatMessage(
                    {
                      id: "teams.automations.row.publishedService",
                      defaultMessage: "Published service · {serviceId}",
                    },
                    { serviceId: schedule.publishedServiceId },
                  )}
                />
                {schedule.lastAuthorizationErrorCode ? (
                  <Tooltip placement="topLeft" title={schedule.lastAuthorizationErrorCode}>
                    <Typography.Text
                      ellipsis
                      style={{
                        color: token.colorError,
                        display: "block",
                        fontSize: 12,
                        maxWidth: "100%",
                      }}
                    >
                      {schedule.lastAuthorizationErrorCode}
                    </Typography.Text>
                  </Tooltip>
                ) : null}
              </div>
              <div
                className="team-automation-row__member"
                style={{ display: "grid", gap: 5, minWidth: 0 }}
              >
                <Typography.Text ellipsis strong>
                  {member?.name ||
                    intl.formatMessage({
                      id: "teams.automations.member.unknown",
                      defaultMessage: "Unknown member",
                    })}
                </Typography.Text>
                <FactLine
                  rows={2}
                  secondary
                  text={intl.formatMessage({
                    id: "teams.automations.preview.runsThroughService",
                    defaultMessage: "Runs through published service",
                  })}
                />
              </div>
              <div
                className="team-automation-row__schedule"
                style={{ display: "grid", gap: 5, minWidth: 0 }}
              >
                <FactLine
                  monospace={false}
                  text={scheduleCadence.summary}
                  tooltipText={`${scheduleCadence.detail} · ${schedule.cronExpression}`}
                />
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {scheduleSecondaryText}
                </Typography.Text>
              </div>
              <div
                className="team-automation-actions"
                style={{
                  ...automationActionGroupBaseStyle,
                  background: token.colorFillQuaternary,
                  border: `1px solid ${token.colorBorderSecondary}`,
                }}
              >
                {renderAutomationActionButton({
                  disabled: mutationLocked,
                  icon: <EditOutlined />,
                  label: intl.formatMessage({
                    id: "teams.automations.actions.edit",
                    defaultMessage: "Edit",
                  }),
                  onClick: () => openEdit(schedule),
                })}
                {status === "needsAuthorization" || status === "error"
                  ? renderAutomationActionButton({
                      icon: <CheckCircleOutlined />,
                      label: intl.formatMessage({
                        id: "teams.automations.actions.reauthorize",
                        defaultMessage: "Re-authorize",
                      }),
                      onClick: () => openReauthorize(schedule),
                    })
                  : null}
                {renderAutomationActionButton({
                  disabled: mutationLocked || status === "needsAuthorization" || status === "error",
                  icon: <ThunderboltOutlined />,
                  label: intl.formatMessage({
                    id: "teams.automations.actions.runNow",
                    defaultMessage: "Run now",
                  }),
                  loading:
                    runNowMutation.isPending &&
                    runNowMutation.variables === scheduleId,
                  onClick: () => runNowMutation.mutate(scheduleId),
                  primary: true,
                })}
                {renderAutomationActionButton({
                  disabled: mutationLocked,
                  icon:
                    schedule.enabled ? (
                      <PauseCircleOutlined />
                    ) : (
                      <PlayCircleOutlined />
                    ),
                  label: schedule.enabled
                    ? intl.formatMessage({
                        id: "teams.automations.actions.pause",
                        defaultMessage: "Pause",
                      })
                    : intl.formatMessage({
                        id: "teams.automations.actions.resume",
                        defaultMessage: "Resume",
                      }),
                  loading:
                    statusMutation.isPending &&
                    statusMutation.variables === scheduleId,
                  onClick: () => statusMutation.mutate(scheduleId),
                })}
                {canViewRuns
                  ? renderAutomationActionButton({
                      icon: <HistoryOutlined />,
                      label: intl.formatMessage({
                        id: "teams.automations.actions.viewRuns",
                        defaultMessage: "View runs",
                      }),
                      onClick: () => openScheduleRuns(member, scheduleId),
                    })
                  : null}
                {renderAutomationActionButton({
                  danger: true,
                  disabled: mutationLocked,
                  icon: <DeleteOutlined />,
                  label: intl.formatMessage({
                    id: "teams.automations.actions.delete",
                    defaultMessage: "Delete",
                  }),
                  loading:
                    deleteMutation.isPending &&
                    deleteMutation.variables === scheduleId,
                  onClick: () => deleteMutation.mutate(scheduleId),
                })}
              </div>
            </article>
          );
        })}
      </div>
    );
  };

  const upcomingSchedules = teamSchedules
    .filter(
      (schedule) =>
        resolveScheduleStatus(schedule) === "active" && schedule.nextFireAt,
    )
    .slice(0, 3);
  const formCadence = describeCronExpression(
    formState.cronExpression,
    intl,
    formState.timezone,
  );
  const formCronValidationMessage = resolveCronValidationMessage(
    formState.cronExpression,
    intl,
  );
  const canCreateAutomation = Boolean(
    activeFormMember &&
      trimText(activeFormMember.serviceId) &&
      activeFormMember.serviceId !== "--",
  );
  const formSubmitting =
    updateMutation.isPending ||
    permissionReviewMutation.isPending ||
    createMutation.isPending ||
    reauthorizeMutation.isPending;
  const formTitle = isEditingAutomation
    ? intl.formatMessage({
        id: "teams.automations.form.editTitle",
        defaultMessage: "Edit automation",
      })
    : isReauthorizingAutomation
      ? intl.formatMessage({
          id: "teams.automations.form.reauthorizeTitle",
          defaultMessage: "Re-authorize automation",
        })
      : intl.formatMessage({
          id: "teams.automations.form.title",
          defaultMessage: "New member automation",
        });
  const formOkText = isEditingAutomation
    ? intl.formatMessage({
        id: "teams.automations.form.save",
        defaultMessage: "Save changes",
      })
    : createStage === "preflight"
      ? intl.formatMessage({
          id: "teams.automations.form.preparingReview",
          defaultMessage: "Preparing review",
        })
      : createStage === "permissionReview" ||
          createStage === "consent"
        ? intl.formatMessage({
            id: isReauthorizingAutomation
              ? "teams.automations.form.reauthorize"
              : "teams.automations.form.authorizeAndCreate",
            defaultMessage: isReauthorizingAutomation
              ? "Authorize replacement"
              : "Authorize & create automation",
          })
        : createStage === "planChanged"
          ? intl.formatMessage({
              id: "teams.automations.form.refreshReview",
              defaultMessage: "Refresh review",
            })
          : intl.formatMessage({
              id: "teams.automations.form.reviewPermissions",
              defaultMessage: "Review permissions",
            });

  return (
    <div className="team-automations-layout" style={pageGridStyle}>
      <style>{responsiveStyle}</style>
      <AevatarPanel>
        <div className="team-automations-panel-header" style={panelHeaderStyle}>
          <div style={titleGroupStyle}>
            <Typography.Title level={3} style={titleStyle}>
              {intl.formatMessage({
                id: "teams.automations.title",
                defaultMessage: "Automations",
              })}
            </Typography.Title>
            <Typography.Text style={{ maxWidth: 680 }} type="secondary">
              {intl.formatMessage({
                id: "teams.automations.description",
                defaultMessage:
                  "Recurring work belongs to a member. The team view shows every commitment so operators can see what will run next and what needs attention.",
              })}
            </Typography.Text>
          </div>
          <Button
            className="team-automations-create-button"
            disabled={!selectedMember}
            icon={<PlusOutlined />}
            onClick={openCreate}
            style={primaryHeaderButtonStyle}
            type="primary"
          >
            {intl.formatMessage({
              id: "teams.automations.actions.create",
              defaultMessage: "New automation",
            })}
          </Button>
        </div>
        {renderAutomationRows()}
      </AevatarPanel>

      <div style={{ display: "grid", gap: 16 }}>
        <AevatarPanel>
          <div style={{ display: "grid", gap: 14 }}>
            <div style={{ display: "grid", gap: 4 }}>
              <Typography.Title level={3} style={titleStyle}>
                {intl.formatMessage({
                  id: "teams.automations.createPanel.title",
                  defaultMessage: "Give a member recurring work",
                })}
              </Typography.Title>
              <Typography.Text type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.createPanel.description",
                  defaultMessage:
                    "Pick a published member, describe the job, choose a cadence, and preview the next runs before creating it.",
                })}
              </Typography.Text>
            </div>
            {selectedMember ? (
              <div
                style={{
                  background: token.colorFillQuaternary,
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 8,
                  display: "grid",
                  gap: 10,
                  padding: 14,
                }}
              >
                <Typography.Text ellipsis strong>
                  {selectedMember.name}
                </Typography.Text>
                <FactLine
                  secondary
                  text={intl.formatMessage({
                    id: "teams.automations.member.publishedServiceReady",
                    defaultMessage: "Published service ready",
                  })}
                />
                <DetailPill
                  compact
                  style={selectedMember.lifecycleStyle}
                  text={selectedMember.lifecycleLabel}
                />
              </div>
            ) : (
              <AevatarInspectorEmpty
                compact
                title={intl.formatMessage({
                  id: "teams.automations.noPublishedMember.title",
                  defaultMessage: "Publish a member first",
                })}
                description={intl.formatMessage({
                  id: "teams.automations.noPublishedMember.description",
                  defaultMessage:
                    "Automations need a member with a published service identity before they can run.",
                })}
              />
            )}
            <Button
              block
              disabled={!selectedMember}
              icon={<ClockCircleOutlined />}
              onClick={openCreate}
              style={inspectorActionButtonStyle}
              type="primary"
            >
              {intl.formatMessage({
                id: "teams.automations.actions.addRecurringWork",
                defaultMessage: "Add recurring work",
              })}
            </Button>
          </div>
        </AevatarPanel>

        <AevatarPanel>
          <div style={{ display: "grid", gap: 12 }}>
            <Typography.Title level={3} style={titleStyle}>
              {intl.formatMessage({
                id: "teams.automations.upcoming.title",
                defaultMessage: "Upcoming",
              })}
            </Typography.Title>
            {upcomingSchedules.length > 0 ? (
              upcomingSchedules.map((schedule) => {
                const member = findMemberForSchedule(schedule);

                return (
                  <div key={schedule.scheduleId} style={upcomingRowStyle}>
                    <div
                      style={{
                        alignItems: "center",
                        background: token.colorSuccessBg,
                        border: `1px solid ${token.colorSuccessBorder}`,
                        borderRadius: 999,
                        color: token.colorSuccess,
                        display: "inline-flex",
                        height: 28,
                        justifyContent: "center",
                        width: 28,
                      }}
                    >
                      <ClockCircleOutlined />
                    </div>
                    <div style={{ display: "grid", gap: 2, minWidth: 0 }}>
                      <Typography.Text strong>
                        {formatScheduleTime(schedule.nextFireAt, "--")}
                      </Typography.Text>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {member?.name
                          ? intl.formatMessage(
                              {
                                id: "teams.automations.upcoming.memberCaption",
                                defaultMessage: "{memberName} recurring work",
                              },
                              { memberName: member.name },
                            )
                          : intl.formatMessage({
                              id: "teams.automations.upcoming.scheduled.caption",
                              defaultMessage: "Scheduled teammate commitment",
                            })}
                      </Typography.Text>
                    </div>
                  </div>
                );
              })
            ) : (
              <Typography.Text type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.upcoming.empty",
                  defaultMessage: "No upcoming runs are visible yet.",
                })}
              </Typography.Text>
            )}
          </div>
        </AevatarPanel>

        {unavailableMembers.length > 0 ? (
          <AevatarPanel>
            <div style={{ display: "grid", gap: 10 }}>
              <Typography.Title level={3} style={titleStyle}>
                {intl.formatMessage({
                  id: "teams.automations.unavailable.title",
                  defaultMessage: "Not ready for automation",
                })}
              </Typography.Title>
              {unavailableMembers.slice(0, 3).map((member) => (
                <div key={member.key} style={{ display: "grid", gap: 2 }}>
                  <Typography.Text strong>{member.name}</Typography.Text>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {member.disabledReason}
                  </Typography.Text>
                </div>
              ))}
            </div>
          </AevatarPanel>
        ) : null}
      </div>

      <Modal
        cancelText={intl.formatMessage({
          id: "teams.automations.form.close",
          defaultMessage: "Close",
        })}
        confirmLoading={formSubmitting}
        okButtonProps={{
          disabled:
            formSubmitting ||
            !activeFormMember ||
            promptTooLong ||
            Boolean(formCronValidationMessage) ||
            !canCreateAutomation ||
            (!isEditingAutomation &&
              (createStage === "permissionReview" || createStage === "consent") &&
              !agentKeyConsentChecked),
        }}
        okText={formOkText}
        onCancel={() => {
          if (!formSubmitting) {
            if (isEditingAutomation || isReauthorizingAutomation) {
              setFormState(
                buildDefaultAutomationFormState(selectedMember?.memberId ?? ""),
              );
            } else {
              setPreservedCreateDraft(
                hasAutomationDraft(formState) ? formState : null,
              );
            }
            setCreateOpen(false);
            setEditingSchedule(null);
            setReauthorizingSchedule(null);
            setPreview(null);
            resetPermissionReview();
          }
        }}
        onOk={handleSaveAutomation}
        open={createOpen}
        title={formTitle}
        width={720}
      >
        <div style={{ display: "grid", gap: 16 }}>
          <div
            style={{
              ...modalSectionStyle,
              background: token.colorFillQuaternary,
              border: `1px solid ${token.colorBorderSecondary}`,
            }}
          >
            <div style={{ display: "grid", gap: 2 }}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.section.target",
                  defaultMessage: "1. Target member",
                })}
              </Typography.Text>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.form.section.targetHint",
                  defaultMessage:
                    "Recurring work runs through the selected member's published service.",
                })}
              </Typography.Text>
            </div>
            <div style={modalFieldStyle}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.member",
                  defaultMessage: "Member",
                })}
              </Typography.Text>
              <Select
                aria-label={intl.formatMessage({
                  id: "teams.automations.form.memberAria",
                  defaultMessage: "Automation member",
                })}
                disabled
                options={selectedMember ? [{
                  label: selectedMember.name,
                  value: selectedMember.memberId,
                }] : []}
                value={activeFormMember?.memberId}
              />
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {activeFormMember && trimText(activeFormMember.serviceId) !== "--"
                  ? intl.formatMessage({
                      id: "teams.automations.form.identityReady",
                      defaultMessage: "Targets the member's published service.",
                    })
                  : intl.formatMessage({
                      id: "teams.automations.form.identityMissing",
                      defaultMessage:
                        "Waiting for this member's published service identity.",
                    })}
              </Typography.Text>
            </div>
          </div>

          <div
            style={{
              ...modalSectionStyle,
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
            }}
          >
            <div style={{ display: "grid", gap: 2 }}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.section.work",
                  defaultMessage: "2. Work to run",
                })}
              </Typography.Text>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.form.section.workHint",
                  defaultMessage:
                    "Name the automation and optionally add a prompt for each run.",
                })}
              </Typography.Text>
            </div>
            <div style={modalFieldStyle}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.displayName",
                  defaultMessage: "Name",
                })}
              </Typography.Text>
              <Input
                aria-label={intl.formatMessage({
                  id: "teams.automations.form.displayNameAria",
                  defaultMessage: "Automation name",
                })}
                disabled={formSubmitting}
                onChange={(event) =>
                  updateForm({ displayName: event.target.value })
                }
                placeholder={intl.formatMessage({
                  id: "teams.automations.form.displayNamePlaceholder",
                  defaultMessage: "Daily escalation digest",
                })}
                value={formState.displayName}
              />
            </div>

            <div style={modalFieldStyle}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.prompt",
                  defaultMessage: "Recurring prompt (optional)",
                })}
              </Typography.Text>
              <Input.TextArea
                aria-label={intl.formatMessage({
                  id: "teams.automations.form.promptAria",
                  defaultMessage: "Recurring prompt (optional)",
                })}
                autoSize={{ minRows: 4, maxRows: 7 }}
                disabled={formSubmitting}
                maxLength={scheduledWorkflowPromptMaxLength}
                onChange={(event) => updateForm({ prompt: event.target.value })}
                placeholder={intl.formatMessage({
                  id: "teams.automations.form.promptPlaceholder",
                  defaultMessage:
                    "Summarize escalations, blocked accounts, and follow-up owners.",
                })}
                showCount
                status={promptTooLong ? "error" : undefined}
                value={formState.prompt}
              />
              <Typography.Text
                style={{ fontSize: 12 }}
                type={promptTooLong ? "danger" : "secondary"}
              >
                {intl.formatMessage(
                  {
                    id: "teams.automations.form.promptLimit",
                    defaultMessage: "Up to {maxLength} characters.",
                  },
                  { maxLength: scheduledWorkflowPromptMaxLength },
                )}
              </Typography.Text>
            </div>
          </div>

          <div
            style={{
              ...modalSectionStyle,
              background: token.colorFillQuaternary,
              border: `1px solid ${token.colorBorderSecondary}`,
            }}
          >
            <div style={{ display: "grid", gap: 2 }}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.section.schedule",
                  defaultMessage: "3. Schedule",
                })}
              </Typography.Text>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.form.section.scheduleHint",
                  defaultMessage:
                    "Choose a common cadence or switch to custom cron for advanced schedules.",
                })}
              </Typography.Text>
            </div>

            <div style={modalFieldStyle}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.cadence",
                  defaultMessage: "Cadence",
                })}
              </Typography.Text>
              <Segmented
                aria-label={intl.formatMessage({
                  id: "teams.automations.form.cadenceAria",
                  defaultMessage: "Automation cadence",
                })}
                block
                disabled={formSubmitting}
                onChange={(preset) => {
                  const nextPreset = String(preset);
                  const match = cronPresets.find(
                    (item) => item.value === nextPreset,
                  );
                  updateForm({
                    preset: nextPreset,
                    cronExpression:
                      nextPreset === customPreset
                        ? formState.cronExpression
                        : match?.cronExpression ?? formState.cronExpression,
                  });
                }}
                options={cronPresets.map(({ label, value }) => ({
                  label,
                  value,
                }))}
                value={formState.preset}
              />
            </div>

            <div
              className="team-automation-form-schedule-grid"
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "minmax(0, 1fr) minmax(180px, 0.54fr)",
              }}
            >
              <div style={modalFieldStyle}>
                <Typography.Text strong>
                  {intl.formatMessage({
                    id: "teams.automations.form.cron",
                    defaultMessage: "Cron expression",
                  })}
                </Typography.Text>
                <Input
                  aria-label={intl.formatMessage({
                    id: "teams.automations.form.cronAria",
                    defaultMessage: "Cron expression",
                  })}
                  disabled={formSubmitting}
                  onChange={(event) =>
                    updateForm({
                      cronExpression: event.target.value,
                      preset: customPreset,
                    })
                  }
                  status={formCronValidationMessage ? "error" : undefined}
                  value={formState.cronExpression}
                />
                {formCronValidationMessage ? (
                  <Typography.Text style={{ fontSize: 12 }} type="danger">
                    {formCronValidationMessage}
                  </Typography.Text>
                ) : null}
              </div>
              <div style={modalFieldStyle}>
                <Typography.Text strong>
                  {intl.formatMessage({
                    id: "teams.automations.form.timezone",
                    defaultMessage: "Timezone",
                  })}
                </Typography.Text>
                <Input
                  aria-label={intl.formatMessage({
                    id: "teams.automations.form.timezoneAria",
                    defaultMessage: "Timezone",
                  })}
                  disabled={formSubmitting}
                  onChange={(event) => updateForm({ timezone: event.target.value })}
                  value={formState.timezone}
                />
              </div>
            </div>

            <div
              className="team-automation-form-schedule-grid"
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "minmax(0, 1fr) minmax(160px, 0.42fr)",
              }}
            >
              <div
                style={{
                  ...scheduleInsightStyle,
                  background: token.colorBgContainer,
                  border: `1px solid ${token.colorBorderSecondary}`,
                }}
              >
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {intl.formatMessage({
                    id: "teams.automations.form.scheduleReadsAs",
                    defaultMessage: "Schedule reads as",
                  })}
                </Typography.Text>
                <Typography.Text strong>{formCadence.detail}</Typography.Text>
              </div>
              <div style={enabledFieldStyle}>
                <Typography.Text strong>
                  {intl.formatMessage({
                    id: "teams.automations.form.enabled",
                    defaultMessage: "Enabled",
                  })}
                </Typography.Text>
                <Switch
                  aria-label={intl.formatMessage({
                    id: "teams.automations.form.enabled",
                    defaultMessage: "Enabled",
                  })}
                  checked={formState.enabled}
                  disabled={formSubmitting}
                  onChange={(enabled) => updateForm({ enabled })}
                  style={enabledSwitchStyle}
                />
              </div>
            </div>

            <div
              style={{
                background: token.colorBgContainer,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 10,
                display: "grid",
                gap: 10,
                padding: 12,
              }}
            >
              <Space align="center" wrap>
                <Button
                  disabled={Boolean(formCronValidationMessage)}
                  icon={<ClockCircleOutlined />}
                  loading={previewMutation.isPending}
                  onClick={handlePreviewNextRuns}
                >
                  {intl.formatMessage({
                    id: "teams.automations.form.preview",
                    defaultMessage: "Preview next runs",
                  })}
                </Button>
                <Typography.Text type="secondary">
                  {intl.formatMessage({
                    id: "teams.automations.form.previewHint",
                    defaultMessage:
                      "Preview uses the schedule service before saving.",
                  })}
                </Typography.Text>
              </Space>
              {preview ? (
                <div style={{ display: "grid", gap: 4 }}>
                  {preview.nextFireTimes.map((time) => (
                    <Typography.Text key={time} style={{ fontSize: 12 }}>
                      {formatScheduleTime(time, time)}
                    </Typography.Text>
                  ))}
                </div>
              ) : (
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {intl.formatMessage({
                    id: "teams.automations.form.previewEmpty",
                    defaultMessage:
                      "Preview the cadence to confirm the next scheduled runs.",
                  })}
                </Typography.Text>
              )}
            </div>
          </div>

          {!isEditingAutomation ? (
            <TeamAutomationPermissionReviewPanel
              consentChecked={agentKeyConsentChecked}
              error={createReviewError}
              onConsentChange={setAgentKeyConsentChecked}
              review={permissionReview}
              stage={createStage}
            />
          ) : null}
        </div>
      </Modal>
    </div>
  );
};

export default TeamAutomationsTab;
