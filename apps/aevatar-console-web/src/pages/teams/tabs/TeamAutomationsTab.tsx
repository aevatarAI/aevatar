import {
  CheckCircleOutlined,
  ClockCircleOutlined,
  DeleteOutlined,
  EditOutlined,
  ExclamationCircleOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  PlusOutlined,
} from "@ant-design/icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Button,
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
  type ScheduledDispatchConfigurationInput,
  type ScheduledDispatchListResult,
  type ScheduledDispatchMutationReceipt,
  type ScheduledDispatchPreview,
  type ScheduledDispatchSummary,
} from "@/shared/api/scheduledDispatchApi";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import type { ServiceIdentity } from "@/shared/models/services";
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
  readonly serviceIdentity?: ServiceIdentity;
  readonly serviceRevisionId?: string;
  readonly workflowSupported: boolean;
};

type TeamAutomationsTabProps = {
  readonly members?: readonly TeamAutomationMemberRow[];
  readonly scopeId: string;
  readonly serviceIdentitiesLoading?: boolean;
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

const scheduleListTake = 200;
const scheduleListRetryLimit = 4;
const scheduleListRetryBaseMs = 600;
const scheduleListRetryMaxMs = 2_500;
const scheduleMutationRefreshDelayMs = 1_000;
const createdScheduleHighlightMs = 4_000;
const customPreset = "custom";
const defaultPreset = "weekdays-0900";
const defaultCronExpression = "0 9 * * 1-5";

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

@media (max-width: 1180px) {
  .team-automations-layout {
    grid-template-columns: minmax(0, 1fr) !important;
  }
}

@media (max-width: 760px) {
  .team-automations-panel-header {
    align-items: stretch !important;
  }

  .team-automations-create-button {
    width: 100%;
  }

  .team-automation-row {
    grid-template-columns: minmax(0, 1fr) !important;
    gap: 12px !important;
    padding: 14px !important;
  }

  .team-automation-actions {
    justify-content: flex-start !important;
    width: 100%;
  }

  .team-automation-list-header {
    display: none !important;
  }

  .team-automation-summary {
    grid-template-columns: minmax(0, 1fr) !important;
  }

  .team-automation-form-schedule-grid {
    grid-template-columns: minmax(0, 1fr) !important;
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
    "minmax(180px, 1.16fr) minmax(132px, 0.72fr) minmax(112px, 0.48fr) minmax(142px, max-content)",
  minWidth: 0,
  padding: 14,
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
  gap: 4,
  justifyContent: "flex-end",
  justifySelf: "end",
  minWidth: 0,
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
  left: ScheduledDispatchSummary,
  right: ScheduledDispatchSummary,
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

function resolveScheduleStatus(
  schedule: ScheduledDispatchSummary,
): "active" | "error" | "paused" {
  if (trimText(schedule.lastError)) {
    return "error";
  }

  return schedule.enabled ? "active" : "paused";
}

const TeamAutomationsTab: React.FC<TeamAutomationsTabProps> = ({
  members = [],
  scopeId,
  serviceIdentitiesLoading = false,
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
    automatableMembers.find((member) => member.isSelectedMember) ??
    automatableMembers[0] ??
    null;
  const unavailableMembers = members.filter((member) => !member.canAutomateMember);
  const [createOpen, setCreateOpen] = React.useState(false);
  const [editingSchedule, setEditingSchedule] =
    React.useState<ScheduledDispatchSummary | null>(null);
  const [preview, setPreview] = React.useState<ScheduledDispatchPreview | null>(null);
  const [locallyDeletedScheduleIds, setLocallyDeletedScheduleIds] =
    React.useState<ReadonlySet<string>>(() => new Set());
  const [pendingCreatedSchedules, setPendingCreatedSchedules] = React.useState<
    readonly ScheduledDispatchSummary[]
  >([]);
  const [localScheduleEnabledOverrides, setLocalScheduleEnabledOverrides] =
    React.useState<ReadonlyMap<string, boolean>>(() => new Map());
  const [highlightedScheduleId, setHighlightedScheduleId] = React.useState("");
  const delayedScheduleRefreshRef = React.useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  const highlightScheduleRef = React.useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  const [formState, setFormState] = React.useState<AutomationFormState>(() => ({
    cronExpression: defaultCronExpression,
    displayName: "",
    enabled: true,
    memberId: "",
    preset: defaultPreset,
    prompt: "",
    timezone: resolveDefaultTimezone(),
  }));
  const scheduleQueryKey = React.useMemo(
    () => ["scheduled-dispatches", "team", scopeId, teamId] as const,
    [scopeId, teamId],
  );
  const serviceIdToMember = React.useMemo(() => {
    const next = new Map<string, TeamAutomationMemberRow>();
    for (const member of automatableMembers) {
      const serviceId = trimText(member.serviceId);
      if (serviceId && serviceId !== "--") {
        next.set(serviceId, member);
      }
    }

    return next;
  }, [automatableMembers]);
  const schedulesQuery = useQuery({
    enabled: scopeId.length > 0 && teamId.length > 0,
    queryFn: () =>
      scheduledDispatchApi.listAll({
        includeTotalCount: true,
        take: scheduleListTake,
      }),
    queryKey: scheduleQueryKey,
    retry: (failureCount) => failureCount < scheduleListRetryLimit,
    retryDelay: scheduleListRetryDelay,
  });
  const teamSchedules = React.useMemo(
    () => {
      const backendSchedules = schedulesQuery.data?.items ?? [];
      const backendScheduleIds = new Set(
        backendSchedules.map((schedule) => trimText(schedule.scheduleId)),
      );
      return [
        ...pendingCreatedSchedules.filter(
          (schedule) => !backendScheduleIds.has(trimText(schedule.scheduleId)),
        ),
        ...backendSchedules,
      ]
        .filter(
          (schedule) =>
            !schedule.deleted &&
            !locallyDeletedScheduleIds.has(trimText(schedule.scheduleId)) &&
            serviceIdToMember.has(trimText(schedule.serviceId)),
        )
        .map((schedule) => {
          const scheduleId = trimText(schedule.scheduleId);
          if (!localScheduleEnabledOverrides.has(scheduleId)) {
            return schedule;
          }

          return {
            ...schedule,
            enabled: localScheduleEnabledOverrides.get(scheduleId) ?? schedule.enabled,
          };
        })
        .sort(sortByNextFire);
    },
    [
      locallyDeletedScheduleIds,
      localScheduleEnabledOverrides,
      pendingCreatedSchedules,
      schedulesQuery.data?.items,
      serviceIdToMember,
    ],
  );
  const activeFormMember =
    automatableMembers.find((member) => member.memberId === formState.memberId) ??
    selectedMember;
  const editingScheduleId = trimText(editingSchedule?.scheduleId);
  const isEditingAutomation = editingScheduleId.length > 0;
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
      queryKey: ["scheduled-dispatches"],
    });
  }, [queryClient]);
  const scheduleDelayedRefresh = React.useCallback(() => {
    if (delayedScheduleRefreshRef.current) {
      clearTimeout(delayedScheduleRefreshRef.current);
    }

    delayedScheduleRefreshRef.current = setTimeout(() => {
      delayedScheduleRefreshRef.current = null;
      void invalidateSchedules();
    }, scheduleMutationRefreshDelayMs);
  }, [invalidateSchedules]);
  const removeScheduleFromCache = React.useCallback(
    (scheduleId: string) => {
      const normalizedScheduleId = trimText(scheduleId);
      if (!normalizedScheduleId) {
        return;
      }

      queryClient.setQueriesData<ScheduledDispatchListResult>(
        { queryKey: ["scheduled-dispatches"] },
        (current) => {
          if (!current) {
            return current;
          }

          const nextItems = current.items.filter(
            (schedule) => trimText(schedule.scheduleId) !== normalizedScheduleId,
          );
          if (nextItems.length === current.items.length) {
            return current;
          }

          return {
            ...current,
            items: nextItems,
            totalCount:
              typeof current.totalCount === "number"
                ? Math.max(0, current.totalCount - 1)
                : current.totalCount,
          };
        },
      );
    },
    [queryClient],
  );
  const hideDeletedSchedule = React.useCallback(
    (scheduleId: string) => {
      const normalizedScheduleId = trimText(scheduleId);
      if (!normalizedScheduleId) {
        return;
      }

      setLocallyDeletedScheduleIds((current) => {
        if (current.has(normalizedScheduleId)) {
          return current;
        }

        const next = new Set(current);
        next.add(normalizedScheduleId);
        return next;
      });
      removeScheduleFromCache(normalizedScheduleId);
    },
    [removeScheduleFromCache],
  );
  React.useEffect(() => {
    if (localScheduleEnabledOverrides.size === 0) {
      return;
    }

    const backendSchedules = schedulesQuery.data?.items ?? [];
    if (backendSchedules.length === 0) {
      return;
    }

    const backendEnabledByScheduleId = new Map(
      backendSchedules.map((schedule) => [
        trimText(schedule.scheduleId),
        schedule.enabled,
      ]),
    );
    setLocalScheduleEnabledOverrides((current) => {
      let changed = false;
      const next = new Map(current);
      for (const [scheduleId, enabled] of current) {
        if (backendEnabledByScheduleId.get(scheduleId) === enabled) {
          next.delete(scheduleId);
          changed = true;
        }
      }

      return changed ? next : current;
    });
  }, [
    localScheduleEnabledOverrides,
    schedulesQuery.data?.items,
  ]);

  const updateScheduleEnabledLocally = React.useCallback(
    (scheduleId: string, enabled: boolean) => {
      const normalizedScheduleId = trimText(scheduleId);
      if (!normalizedScheduleId) {
        return;
      }

      const updatedAt = new Date().toISOString();
      setLocalScheduleEnabledOverrides((current) => {
        const next = new Map(current);
        next.set(normalizedScheduleId, enabled);
        return next;
      });
      setPendingCreatedSchedules((current) =>
        current.map((schedule) =>
          trimText(schedule.scheduleId) === normalizedScheduleId
            ? {
                ...schedule,
                enabled,
                updatedAt,
              }
            : schedule,
        ),
      );
    },
    [],
  );
  const showCreatedScheduleFeedback = React.useCallback(
    ({
      input,
      member,
      receipt,
    }: {
      readonly input: ScheduledDispatchConfigurationInput;
      readonly member: TeamAutomationMemberRow;
      readonly receipt: ScheduledDispatchMutationReceipt;
    }) => {
      const scheduleId = trimText(receipt.scheduleId);
      const serviceId = trimText(member.serviceIdentity?.serviceId) || trimText(member.serviceId);
      if (!scheduleId || !serviceId) {
        return;
      }

      const now = new Date().toISOString();
      const pendingSchedule: ScheduledDispatchSummary = {
        scheduleId,
        displayName: trimText(input.displayName),
        targetKind: "service_invocation",
        targetActorId: "",
        payloadTypeUrl: "",
        serviceKey: [
          input.workflowChatTarget.identity.tenantId,
          input.workflowChatTarget.identity.appId,
          input.workflowChatTarget.identity.namespace,
          serviceId,
        ]
          .map(trimText)
          .filter(Boolean)
          .join(":"),
        serviceId,
        serviceEndpointId: "chat",
        cronExpression: input.cronExpression,
        timezone: trimText(input.timezone) || resolveDefaultTimezone(),
        enabled: input.enabled ?? true,
        createdAt: now,
        updatedAt: now,
        nextFireAt: null,
        lastFireAt: null,
        lastTargetActorId: "",
        lastCommandId: receipt.commandId,
        lastCorrelationId: receipt.correlationId,
        lastError: "",
        fireCount: 0,
        failureCount: 0,
        headers: { ...(input.headers ?? {}) },
        scheduleActorId: receipt.scheduleActorId,
        scheduleKind: "workflow",
        deleted: false,
      };

      setPendingCreatedSchedules((current) => [
        pendingSchedule,
        ...current.filter((schedule) => trimText(schedule.scheduleId) !== scheduleId),
      ]);
      setHighlightedScheduleId(scheduleId);
      if (highlightScheduleRef.current) {
        clearTimeout(highlightScheduleRef.current);
      }
      highlightScheduleRef.current = setTimeout(() => {
        highlightScheduleRef.current = null;
        setHighlightedScheduleId((current) =>
          current === scheduleId ? "" : current,
        );
      }, createdScheduleHighlightMs);
    },
    [],
  );
  React.useEffect(
    () => () => {
      if (delayedScheduleRefreshRef.current) {
        clearTimeout(delayedScheduleRefreshRef.current);
      }
      if (highlightScheduleRef.current) {
        clearTimeout(highlightScheduleRef.current);
      }
    },
    [],
  );

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
  const createMutation = useMutation({
    mutationFn: ({
      input,
      member,
    }: {
      readonly input: ScheduledDispatchConfigurationInput;
      readonly member: TeamAutomationMemberRow;
    }) =>
      scheduledDispatchApi.create(input).then((receipt) => ({
        input,
        member,
        receipt,
      })),
    onError: (error) => {
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
    onSuccess: ({ input, member, receipt }) => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.createSuccess",
          defaultMessage: "Automation created.",
        }),
      );
      showCreatedScheduleFeedback({ input, member, receipt });
      setCreateOpen(false);
      setPreview(null);
      scheduleDelayedRefresh();
    },
  });
  const updateMutation = useMutation({
    mutationFn: ({
      input,
      scheduleId,
    }: {
      readonly input: ScheduledDispatchConfigurationInput;
      readonly scheduleId: string;
    }) => scheduledDispatchApi.update(scheduleId, input),
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
    onSuccess: async () => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.updateSuccess",
          defaultMessage: "Automation updated.",
        }),
      );
      setCreateOpen(false);
      setEditingSchedule(null);
      setPreview(null);
      await invalidateSchedules();
    },
  });
  const runNowMutation = useMutation({
    mutationFn: scheduledDispatchApi.runNow,
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
    onSuccess: async () => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.runNowSuccess",
          defaultMessage: "Run requested.",
        }),
      );
      await invalidateSchedules();
    },
  });
  const enableMutation = useMutation({
    mutationFn: (scheduleId: string) =>
      scheduledDispatchApi.enable(
        scheduleId,
        "Enabled from Team Automations",
      ),
    onSuccess: (_receipt, scheduleId) => {
      updateScheduleEnabledLocally(scheduleId, true);
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.enableSuccess",
          defaultMessage: "Automation resumed.",
        }),
      );
      scheduleDelayedRefresh();
    },
  });
  const disableMutation = useMutation({
    mutationFn: (scheduleId: string) =>
      scheduledDispatchApi.disable(
        scheduleId,
        "Disabled from Team Automations",
      ),
    onSuccess: (_receipt, scheduleId) => {
      updateScheduleEnabledLocally(scheduleId, false);
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.disableSuccess",
          defaultMessage: "Automation paused.",
        }),
      );
      scheduleDelayedRefresh();
    },
  });
  const deleteMutation = useMutation({
    mutationFn: (scheduleId: string) =>
      scheduledDispatchApi.delete(
        scheduleId,
        "Deleted from Team Automations",
      ),
    onSuccess: (_receipt, scheduleId) => {
      hideDeletedSchedule(scheduleId);
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.deleteSuccess",
          defaultMessage: "Automation deleted.",
        }),
      );
      scheduleDelayedRefresh();
    },
  });

  const openCreate = React.useCallback(() => {
    const member = selectedMember;
    setEditingSchedule(null);
    setFormState({
      cronExpression: defaultCronExpression,
      displayName: "",
      enabled: true,
      memberId: member?.memberId ?? "",
      preset: defaultPreset,
      prompt: "",
      timezone: resolveDefaultTimezone(),
    });
    setPreview(null);
    setCreateOpen(true);
  }, [selectedMember]);

  const openEdit = React.useCallback(
    (schedule: ScheduledDispatchSummary) => {
      const member =
        serviceIdToMember.get(trimText(schedule.serviceId)) ?? selectedMember;
      const cronExpression = trimText(schedule.cronExpression);
      const preset =
        cronPresets.find((item) => item.cronExpression === cronExpression)?.value ??
        customPreset;
      setEditingSchedule(schedule);
      setFormState({
        cronExpression,
        displayName: trimText(schedule.displayName),
        enabled: schedule.enabled,
        memberId: member?.memberId ?? "",
        preset,
        prompt: "",
        timezone: trimText(schedule.timezone) || resolveDefaultTimezone(),
      });
      setPreview(null);
      setCreateOpen(true);
    },
    [cronPresets, selectedMember, serviceIdToMember],
  );

  const updateForm = React.useCallback(
    (patch: Partial<AutomationFormState>) => {
      setFormState((current) => ({
        ...current,
        ...patch,
      }));
      setPreview(null);
    },
    [],
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

  const saveAutomation = React.useCallback(async () => {
    const member = activeFormMember;
    const serviceIdentity = member?.serviceIdentity;
    const serviceRevisionId = trimText(member?.serviceRevisionId);
    const prompt = formState.prompt.trim();
    const cronExpression = formState.cronExpression.trim();
    if (!member || !serviceIdentity) {
      void message.error(
        serviceIdentitiesLoading
          ? intl.formatMessage({
              id: "teams.automations.messages.serviceIdentityLoading",
              defaultMessage: "Service identity is still loading.",
            })
          : intl.formatMessage({
              id: "teams.automations.messages.serviceIdentityMissing",
              defaultMessage:
                "The selected member does not have a service identity yet.",
            }),
      );
      return;
    }
    if (!prompt) {
      void message.error(
        intl.formatMessage({
          id: "teams.automations.messages.promptRequired",
          defaultMessage: "Describe the recurring work before saving it.",
        }),
      );
      return;
    }
    if (!cronExpression) {
      void message.error(
        intl.formatMessage({
          id: "teams.automations.messages.cronRequired",
          defaultMessage: "Enter a cron expression first.",
        }),
      );
      return;
    }

    const input: ScheduledDispatchConfigurationInput = {
      displayName:
        formState.displayName.trim() ||
        intl.formatMessage(
          {
            id: "teams.automations.form.defaultTitle",
            defaultMessage: "{memberName} recurring work",
          },
          { memberName: member.name },
        ),
      cronExpression,
      timezone: trimText(formState.timezone) || undefined,
      enabled: formState.enabled,
      headers: {
        source: "team-automations",
      },
      workflowChatTarget: {
        identity: serviceIdentity,
        prompt,
        ...(serviceRevisionId ? { revisionId: serviceRevisionId } : {}),
      },
    };

    if (isEditingAutomation) {
      await updateMutation.mutateAsync({
        input,
        scheduleId: editingScheduleId,
      });
      return;
    }

    await createMutation.mutateAsync({ input, member });
  }, [
    activeFormMember,
    createMutation,
    editingScheduleId,
    formState.cronExpression,
    formState.displayName,
    formState.enabled,
    formState.prompt,
    formState.timezone,
    intl,
    isEditingAutomation,
    serviceIdentitiesLoading,
    showCreatedScheduleFeedback,
    updateMutation,
  ]);

  const handleSaveAutomation = React.useCallback(() => {
    saveAutomation().catch(() => {
      // The mutation onError path owns the user-visible failure message.
    });
  }, [saveAutomation]);

  const renderStatusPill = (schedule: ScheduledDispatchSummary) => {
    const status = resolveScheduleStatus(schedule);
    const statusStyle =
      status === "error"
        ? {
            background: token.colorErrorBg,
            border: `1px solid ${token.colorErrorBorder}`,
            color: token.colorError,
          }
        : status === "paused"
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
    icon,
    label,
    loading,
    onClick,
    primary,
  }: {
    readonly danger?: boolean;
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
      (schedule) => resolveScheduleStatus(schedule) === "error",
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
          const member = serviceIdToMember.get(trimText(schedule.serviceId));
          const scheduleId = trimText(schedule.scheduleId);
          const scheduleCadence = describeCronExpression(
            schedule.cronExpression,
            intl,
            schedule.timezone,
          );
          const isPendingCreated = pendingCreatedSchedules.some(
            (pendingSchedule) =>
              trimText(pendingSchedule.scheduleId) === scheduleId,
          );
          const statusMutation =
            schedule.enabled ? disableMutation : enableMutation;
          const status = resolveScheduleStatus(schedule);
          const isHighlighted = highlightedScheduleId === scheduleId;
          const rowBorderColor =
            isHighlighted
              ? token.colorPrimaryBorder
              : status === "error"
                ? token.colorErrorBorder
                : status === "paused"
                  ? token.colorWarningBorder
                  : token.colorBorderSecondary;
          const rowBackground =
            isHighlighted
              ? token.colorPrimaryBg
              : token.colorBgContainer;
          const rowShadow = isHighlighted
            ? token.boxShadowSecondary
            : token.boxShadowTertiary;
          const rowOutlineColor =
            isHighlighted ? token.colorPrimaryBorder : undefined;

          const scheduleSecondaryText =
            schedule.nextFireAt
              ? intl.formatMessage(
                  {
                    id: "teams.automations.row.nextRun",
                    defaultMessage: "Next {time}",
                  },
                  {
                    time: formatScheduleTime(schedule.nextFireAt, "--"),
                  },
                )
              : isPendingCreated
                ? intl.formatMessage({
                    id: "teams.automations.row.awaitingReadModel",
                    defaultMessage: "Waiting for schedule sync",
                  })
                : intl.formatMessage({
                    id: "teams.automations.row.noNextRun",
                    defaultMessage: "No next run",
                  });

          const rowAriaLabel =
            status === "error"
              ? `${schedule.displayName} ${schedule.lastError}`
              : schedule.displayName;

          return (
            <div
              aria-label={rowAriaLabel}
              className="team-automation-row"
              key={scheduleId}
              style={{
                ...commitmentRowStyle,
                background: rowBackground,
                border: `1px solid ${rowBorderColor}`,
                borderRadius: 12,
                boxShadow: rowShadow,
                outline: rowOutlineColor
                  ? `1px solid ${rowOutlineColor}`
                  : undefined,
              }}
            >
              <div style={{ display: "grid", gap: 7, minWidth: 0 }}>
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
                      id: "teams.automations.row.target",
                      defaultMessage: "Workflow chat · {endpoint}",
                    },
                    { endpoint: schedule.serviceEndpointId || "chat" },
                  )}
                />
                {schedule.lastError ? (
                  <Typography.Text
                    ellipsis
                    style={{ color: token.colorError, fontSize: 12 }}
                  >
                    {schedule.lastError}
                  </Typography.Text>
                ) : null}
              </div>
              <div style={{ display: "grid", gap: 5, minWidth: 0 }}>
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
                  text={intl.formatMessage(
                    {
                      id: "teams.automations.preview.runsThroughService",
                      defaultMessage: "Runs through {serviceId}",
                    },
                    { serviceId: schedule.serviceId },
                  )}
                />
              </div>
              <div style={{ display: "grid", gap: 5, minWidth: 0 }}>
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
                  icon: <EditOutlined />,
                  label: intl.formatMessage({
                    id: "teams.automations.actions.edit",
                    defaultMessage: "Edit",
                  }),
                  onClick: () => openEdit(schedule),
                })}
                {renderAutomationActionButton({
                  icon: <PlayCircleOutlined />,
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
                {renderAutomationActionButton({
                  danger: true,
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
            </div>
          );
        })}
      </div>
    );
  };

  const upcomingSchedules = teamSchedules
    .filter((schedule) => schedule.enabled && schedule.nextFireAt)
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
  const canCreateAutomation = Boolean(activeFormMember?.serviceIdentity);
  const formSubmitting = createMutation.isPending || updateMutation.isPending;
  const formTitle = isEditingAutomation
    ? intl.formatMessage({
        id: "teams.automations.form.editTitle",
        defaultMessage: "Edit automation",
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
    : intl.formatMessage({
        id: "teams.automations.form.create",
        defaultMessage: "Create automation",
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
                <FactLine secondary text={selectedMember.serviceId} />
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
                const member = serviceIdToMember.get(schedule.serviceId);

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
        confirmLoading={formSubmitting}
        okButtonProps={{
          disabled:
            !activeFormMember ||
            !formState.prompt.trim() ||
            Boolean(formCronValidationMessage) ||
            !canCreateAutomation,
        }}
        okText={formOkText}
        onCancel={() => {
          if (!formSubmitting) {
            setCreateOpen(false);
            setEditingSchedule(null);
            setPreview(null);
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
                disabled={formSubmitting || isEditingAutomation}
                onChange={(memberId) => updateForm({ memberId })}
                options={automatableMembers.map((member) => ({
                  label: member.name,
                  value: member.memberId,
                }))}
                value={activeFormMember?.memberId}
              />
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {activeFormMember?.serviceIdentity
                  ? intl.formatMessage(
                      {
                        id: "teams.automations.form.identityReady",
                        defaultMessage: "Targets published service {serviceId}.",
                      },
                      { serviceId: activeFormMember.serviceIdentity.serviceId },
                    )
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
                    "Name the automation and write the prompt the member receives each time.",
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
                  defaultMessage: "Recurring prompt",
                })}
              </Typography.Text>
              <Input.TextArea
                aria-label={intl.formatMessage({
                  id: "teams.automations.form.promptAria",
                  defaultMessage: "Recurring prompt",
                })}
                autoSize={{ minRows: 4, maxRows: 7 }}
                disabled={formSubmitting}
                onChange={(event) => updateForm({ prompt: event.target.value })}
                placeholder={intl.formatMessage({
                  id: "teams.automations.form.promptPlaceholder",
                  defaultMessage:
                    "Summarize escalations, blocked accounts, and follow-up owners.",
                })}
                value={formState.prompt}
              />
              {isEditingAutomation ? (
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {intl.formatMessage({
                    id: "teams.automations.form.editPromptHint",
                    defaultMessage: "Re-enter the recurring prompt to save changes.",
                  })}
                </Typography.Text>
              ) : null}
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
        </div>
      </Modal>
    </div>
  );
};

export default TeamAutomationsTab;
