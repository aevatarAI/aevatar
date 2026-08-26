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
  ReloadOutlined,
  SafetyCertificateOutlined,
  ThunderboltOutlined,
} from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Input,
  Modal,
  Segmented,
  Select,
  Skeleton,
  Space,
  Spin,
  Switch,
  Tooltip,
  Typography,
  message,
  theme,
} from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import {
  createTeamAutomationOperationIdentity,
  teamAutomationApi,
  TeamAutomationApiError,
  type TeamAutomationCreateDraft,
  type TeamAutomationListRoute,
  type TeamAutomationMutationReceipt,
  type TeamAutomationOperationIdentity,
  type TeamAutomationPermissionReview,
  type TeamAutomationRoute,
  type TeamAutomationView,
} from "@/shared/api/teamAutomationApi";
import { previewScheduledDispatch } from "@/shared/api/scheduledDispatchApi";
import { NyxIDAuthClient } from "@/shared/auth/client";
import { getNyxIDRuntimeConfig } from "@/shared/auth/config";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildTeamMemberAutomationsHref,
  buildTeamMemberPublishedRunsHref,
} from "@/shared/navigation/teamRoutes";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import TeamAutomationAuthorizationReview from "../components/TeamAutomationAuthorizationReview";
import {
  DetailPill,
  FactLine,
} from "../components/TeamDetailPrimitives";
import {
  consumeTeamAutomationAuthorizationDraft,
  saveTeamAutomationAuthorizationDraft,
  type TeamAutomationAuthorizationDraftInput,
} from "../teamAutomationAuthorizationDraftSession";

export type TeamAutomationMemberRow = {
  readonly canAutomateMember: boolean;
  readonly disabledReason: string;
  readonly implementationKind: string;
  readonly key: string;
  readonly lifecycleLabel: string;
  readonly lifecycleStyle: React.CSSProperties;
  readonly memberId: string;
  readonly name: string;
  readonly serviceId: string;
  readonly workflowSupported: boolean;
};

type Props = {
  readonly members?: readonly TeamAutomationMemberRow[];
  readonly routeMemberId?: string;
  readonly scopeId: string;
  readonly serviceIdentitiesLoading?: boolean;
  readonly teamId: string;
};

type Draft = {
  readonly memberId: string;
  readonly displayName: string;
  readonly prompt: string;
  readonly cronExpression: string;
  readonly timezone: string;
  readonly enabled: boolean;
};

type AuthorizationMode = "create" | "reauthorize";

export type MutationObservation = {
  readonly kind:
    | AuthorizationMode
    | "update"
    | "pause"
    | "resume"
    | "runNow"
    | "delete"
    | "retryRevocation";
  readonly scheduleId: string;
  readonly acceptedAt: number;
  readonly baselineStateVersion: number;
  readonly expectedDraft?: TeamAutomationCreateDraft;
  readonly baselineLastFireAt?: string | null;
};

type AuthorizationFlow =
  | { readonly state: "idle" }
  | {
      readonly state: "preflighting";
      readonly draft: TeamAutomationCreateDraft;
      readonly mode: AuthorizationMode;
      readonly scheduleId?: string;
    }
  | {
      readonly state: "reviewing";
      readonly draft: TeamAutomationCreateDraft;
      readonly mode: AuthorizationMode;
      readonly review: TeamAutomationPermissionReview;
      readonly scheduleId?: string;
    }
  | {
      readonly state: "submitting";
      readonly draft: TeamAutomationCreateDraft;
      readonly identity: TeamAutomationOperationIdentity;
      readonly mode: AuthorizationMode;
      readonly review: TeamAutomationPermissionReview;
      readonly scheduleId?: string;
    }
  | {
      readonly state: "plan_changed";
      readonly draft: TeamAutomationCreateDraft;
      readonly mode: AuthorizationMode;
      readonly scheduleId?: string;
    }
  | {
      readonly state: "pending";
      readonly baselineStateVersion: number;
      readonly scheduleId: string;
    };

const listTake = 200;
const promptMaxLength = 4_000;
const pendingPollIntervalMs = 2_000;
const pendingPollDurationMs = 6_000;
const retryablePreflightCodes = new Set([
  "TEAM_AUTOMATION_AUTHORIZATION_PROJECTION_PENDING",
  "TEAM_AUTOMATION_AUTHORIZATION_SNAPSHOT_NOT_FOUND",
  "TEAM_AUTOMATION_AUTHORIZATION_SNAPSHOT_STALE",
  "TEAM_AUTOMATION_AUTHORIZATION_DURABLE_AUTHORIZATION_UNAVAILABLE",
  "TEAM_AUTOMATION_AUTHORIZATION_REFRESH_SUPERSEDED",
  "TEAM_AUTOMATION_AUTHORIZATION_REFRESH_UNAVAILABLE",
]);
const initialDraft: Draft = {
  memberId: "",
  displayName: "",
  prompt: "",
  cronExpression: "0 9 * * 1-5",
  timezone: "UTC",
  enabled: true,
};

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
    grid-template-columns: repeat(5, minmax(0, 1fr));
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

function trim(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function defaultTimezone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

function describeDraftCadence(
  cronExpression: string,
  timezone: string,
  copy: (
    id: string,
    defaultMessage: string,
    values?: Record<string, string | number>,
  ) => string,
): { readonly detail: string; readonly summary: string } {
  const cron = trim(cronExpression).replace(/\s+/g, " ");
  const zone = trim(timezone) || "UTC";
  const descriptions: Record<string, { readonly detail: string; readonly summary: string }> = {
    "0 9 * * 1-5": {
      detail: copy(
        "teams.automations.cron.weekdaysDetail",
        "Weekdays at {time} · {timezone}",
        { time: "09:00", timezone: zone },
      ),
      summary: copy(
        "teams.automations.cron.weekdays",
        "Weekdays · {time}",
        { time: "09:00" },
      ),
    },
    "0 9 * * *": {
      detail: copy(
        "teams.automations.cron.dailyDetail",
        "Every day at {time} · {timezone}",
        { time: "09:00", timezone: zone },
      ),
      summary: copy(
        "teams.automations.cron.daily",
        "Daily · {time}",
        { time: "09:00" },
      ),
    },
    "0 9 * * 1": {
      detail: copy(
        "teams.automations.cron.weeklyDetail",
        "{weekday} at {time} · {timezone}",
        {
          weekday: copy("teams.automations.weekdays.monday", "Monday"),
          time: "09:00",
          timezone: zone,
        },
      ),
      summary: copy(
        "teams.automations.cron.weekly",
        "{weekday} · {time}",
        {
          weekday: copy("teams.automations.weekdays.monday", "Monday"),
          time: "09:00",
        },
      ),
    },
    "0 * * * *": {
      detail: copy(
        "teams.automations.cron.hourlyDetail",
        "Every hour at minute {minute} · {timezone}",
        { minute: "00", timezone: zone },
      ),
      summary: copy(
        "teams.automations.cron.hourly",
        "Hourly · :{minute}",
        { minute: "00" },
      ),
    },
  };
  return descriptions[cron] ?? {
    detail: `${cron || "--"} · ${zone}`,
    summary: copy("teams.automations.cron.custom", "Custom schedule"),
  };
}

function fromView(view: TeamAutomationView): Draft {
  return {
    memberId: view.memberId,
    displayName: view.displayName,
    prompt: view.prompt,
    cronExpression: view.cronExpression,
    timezone: view.timezone || "UTC",
    enabled: view.enabled,
  };
}

function isPending(view: TeamAutomationView): boolean {
  return [
    "provisioning_pending",
    "replacement_pending",
    "deleting",
    "revocation_pending",
  ].includes(view.authorizationStatus);
}

function matchesDraft(
  view: TeamAutomationView,
  expectedDraft: TeamAutomationCreateDraft,
): boolean {
  return (
    view.displayName === expectedDraft.displayName &&
    view.prompt === expectedDraft.prompt &&
    view.cronExpression === expectedDraft.cronExpression &&
    view.timezone === (expectedDraft.timezone || "UTC") &&
    view.enabled === expectedDraft.enabled
  );
}

export function mutationObservationComplete(
  observation: MutationObservation,
  items: readonly TeamAutomationView[],
): boolean {
  const view = items.find((item) => item.scheduleId === observation.scheduleId);
  if (!view && ["delete", "retryRevocation"].includes(observation.kind)) {
    return true;
  }
  if (observation.kind === "delete") {
    return false;
  }
  if (!view || view.stateVersion <= observation.baselineStateVersion) {
    return false;
  }
  switch (observation.kind) {
    case "create":
    case "reauthorize":
      return ["active", "needs_authorization", "failed"].includes(
        view.authorizationStatus,
      );
    case "update":
      return (
        (observation.expectedDraft
          ? matchesDraft(view, observation.expectedDraft)
          : false) ||
        ["needs_authorization", "failed"].includes(view.authorizationStatus)
      );
    case "pause":
      return !view.enabled;
    case "resume":
      return view.enabled;
    case "runNow":
      return (
        view.lastFireAt !== (observation.baselineLastFireAt ?? null) ||
        view.authorizationStatus === "failed"
      );
    case "retryRevocation":
      return (
        view.revocationPending &&
        view.authorizationStatus === "failed" &&
        (view.nyxIdRevocationStatus === "Failed" ||
          view.vaultRevocationStatus === "Failed")
      );
  }
}

function credentialLabel(view: TeamAutomationView): string {
  switch (view.authorizationStatus) {
    case "provisioning_pending":
      return "Preparing authorization";
    case "active":
      return "Credential active";
    case "needs_authorization":
      return "Authorization required";
    case "replacement_pending":
      return "Replacing authorization";
    case "deleting":
      return "Deleting";
    case "revocation_pending":
      return "Revocation pending";
    case "failed":
      return view.revocationPending ? "Revocation needs attention" : "Authorization failed";
  }
}

function authorizationDraft(
  route: TeamAutomationListRoute,
  draft: Draft,
): TeamAutomationCreateDraft {
  return {
    ...route,
    memberId: trim(draft.memberId) || route.memberId || "",
    displayName: trim(draft.displayName),
    prompt: trim(draft.prompt),
    cronExpression: trim(draft.cronExpression),
    timezone: trim(draft.timezone) || "UTC",
    enabled: draft.enabled,
  };
}

function exactAutomationRoute(route: TeamAutomationRoute): TeamAutomationRoute {
  return {
    scopeId: route.scopeId,
    teamId: route.teamId,
    memberId: route.memberId,
  };
}

function recoveryDraft(
  draft: TeamAutomationCreateDraft,
  mode: AuthorizationMode,
  scheduleId?: string,
): TeamAutomationAuthorizationDraftInput {
  return {
    scopeId: draft.scopeId,
    teamId: draft.teamId,
    memberId: draft.memberId,
    mode,
    scheduleId,
    displayName: draft.displayName,
    prompt: draft.prompt,
    scheduleCron: draft.cronExpression,
    scheduleTimezone: draft.timezone ?? "UTC",
    enabled: draft.enabled,
  };
}

function requiresBindingRecovery(error: unknown): error is TeamAutomationApiError {
  return (
    error instanceof TeamAutomationApiError &&
    error.code === "TEAM_AUTOMATION_AUTHORIZATION_BINDING_REQUIRED"
  );
}

async function retryTypedPreflight<T>(operation: () => Promise<T>): Promise<T> {
  const delays = [500, 1_000, 2_000];
  for (let attempt = 0; ; attempt += 1) {
    try {
      return await operation();
    } catch (error) {
      const code = error instanceof TeamAutomationApiError ? error.code : undefined;
      if (!code || !retryablePreflightCodes.has(code) || attempt >= delays.length) {
        throw error;
      }
      await new Promise<void>((resolve) => setTimeout(resolve, delays[attempt]));
    }
  }
}

const TeamAutomationsTab: React.FC<Props> = ({
  members = [],
  routeMemberId: routeMemberIdInput = "",
  scopeId,
  teamId,
}) => {
  const intl = useIntl();
  const copy = React.useCallback(
    (id: string, defaultMessage: string, values?: Record<string, string | number>) =>
      intl.formatMessage({ id, defaultMessage }, values),
    [intl],
  );
  const queryClient = useQueryClient();
  const { token } = theme.useToken();
  const routeMemberId = trim(routeMemberIdInput);
  const route = React.useMemo<TeamAutomationListRoute>(
    () => ({
      scopeId: trim(scopeId),
      teamId: trim(teamId),
      ...(routeMemberId ? { memberId: routeMemberId } : {}),
    }),
    [routeMemberId, scopeId, teamId],
  );
  const membersById = React.useMemo(
    () => new Map(members.map((member) => [trim(member.memberId), member])),
    [members],
  );
  const eligibleMembers = React.useMemo(
    () => members.filter((member) => member.canAutomateMember),
    [members],
  );
  const routeMember = membersById.get(routeMemberId);
  const routeMemberAuthority = React.useMemo<TeamAutomationRoute | null>(
    () => route.memberId
      ? { scopeId: route.scopeId, teamId: route.teamId, memberId: route.memberId }
      : null,
    [route],
  );
  const canQuery = Boolean(
    route.scopeId &&
    route.teamId &&
    (!route.memberId || routeMember?.canAutomateMember),
  );
  const queryKey = React.useMemo(
    () => ["team-automations", route.scopeId, route.teamId, route.memberId] as const,
    [route],
  );
  const [draft, setDraft] = React.useState<Draft>({
    ...initialDraft,
    timezone: defaultTimezone(),
  });
  const [formMode, setFormMode] = React.useState<"create" | "edit">("create");
  const [formOpen, setFormOpen] = React.useState(false);
  const [editing, setEditing] = React.useState<TeamAutomationView | null>(null);
  const [authorizationFlow, setAuthorizationFlow] = React.useState<AuthorizationFlow>({ state: "idle" });
  const [mutationObservation, setMutationObservation] =
    React.useState<MutationObservation | null>(null);
  const [expiredMutationObservation, setExpiredMutationObservation] =
    React.useState<MutationObservation | null>(null);
  const [busyScheduleId, setBusyScheduleId] = React.useState("");
  const [previewTimes, setPreviewTimes] = React.useState<readonly string[]>([]);
  const recoveredRouteRef = React.useRef("");
  const modalPanelRef = React.useRef<HTMLDivElement | null>(null);
  const flowBusy = authorizationFlow.state === "preflighting" || authorizationFlow.state === "submitting";
  const review = authorizationFlow.state === "reviewing" || authorizationFlow.state === "submitting"
    ? authorizationFlow.review
    : null;
  const modalDescriptionId = review
    ? "team-automation-authorization-description"
    : "team-automation-form-description";

  React.useEffect(() => {
    modalPanelRef.current?.setAttribute("aria-describedby", modalDescriptionId);
  }, [formOpen, modalDescriptionId]);

  const automationsQuery = useQuery({
    enabled: canQuery,
    queryKey,
    queryFn: () => teamAutomationApi.listAll(route, { take: listTake }),
    retry: (failureCount, error) =>
      failureCount < 3 &&
      error instanceof TeamAutomationApiError &&
      Boolean(error.code && retryablePreflightCodes.has(error.code)),
    retryDelay: (attempt) => [500, 1_000, 2_000][Math.min(attempt, 2)],
    refetchInterval: () => {
      if (!mutationObservation) return false;
      return Date.now() - mutationObservation.acceptedAt < pendingPollDurationMs
        ? pendingPollIntervalMs
        : false;
    },
    refetchOnWindowFocus: true,
  });

  React.useEffect(() => {
    if (!mutationObservation) {
      setExpiredMutationObservation(null);
      return;
    }
    const remainingMs = Math.max(
      0,
      mutationObservation.acceptedAt + pendingPollDurationMs - Date.now(),
    );
    if (remainingMs === 0) {
      setExpiredMutationObservation(mutationObservation);
      return;
    }
    const timeoutId = window.setTimeout(
      () => setExpiredMutationObservation(mutationObservation),
      remainingMs,
    );
    return () => window.clearTimeout(timeoutId);
  }, [mutationObservation]);

  React.useEffect(() => {
    const data = automationsQuery.data;
    if (
      !mutationObservation ||
      !data ||
      !mutationObservationComplete(mutationObservation, data.items)
    ) {
      return;
    }
    setMutationObservation((current) =>
      current === mutationObservation ? null : current,
    );
    setAuthorizationFlow((current) =>
      current.state === "pending" &&
      current.scheduleId === mutationObservation.scheduleId
        ? { state: "idle" }
        : current,
    );
  }, [automationsQuery.data, mutationObservation]);

  const invalidate = React.useCallback(
    () => queryClient.invalidateQueries({ queryKey }),
    [queryClient, queryKey],
  );

  const redirectToBindingRecovery = React.useCallback(
    async (
      target:
        | {
            readonly draft: TeamAutomationCreateDraft;
            readonly mode: AuthorizationMode;
            readonly scheduleId?: string;
          }
        | { readonly route: TeamAutomationRoute },
    ) => {
      if (typeof window === "undefined") {
        throw new Error("NyxID authorization recovery requires a browser environment.");
      }
      if ("draft" in target) {
        saveTeamAutomationAuthorizationDraft(
          window.sessionStorage,
          recoveryDraft(target.draft, target.mode, target.scheduleId),
        );
      }
      await new NyxIDAuthClient(getNyxIDRuntimeConfig()).loginWithRedirect({
        returnTo: buildTeamMemberAutomationsHref(
          "draft" in target ? target.draft : target.route,
        ),
        prompt: "consent",
      });
    },
    [],
  );

  const beginPreflight = React.useCallback(
    async (nextDraft: TeamAutomationCreateDraft, mode: AuthorizationMode, scheduleId?: string) => {
      setAuthorizationFlow({ state: "preflighting", draft: nextDraft, mode, scheduleId });
      try {
        const review = await retryTypedPreflight(() => teamAutomationApi.preflightCreate(nextDraft));
        if (review.status === "plan-changed") {
          setAuthorizationFlow({ state: "plan_changed", draft: nextDraft, mode, scheduleId });
          return;
        }
        setAuthorizationFlow({ state: "reviewing", draft: nextDraft, mode, review, scheduleId });
      } catch (error) {
        if (requiresBindingRecovery(error)) {
          try {
            await redirectToBindingRecovery({
              draft: nextDraft,
              mode,
              scheduleId,
            });
            return;
          } catch {
            void message.error(copy(
              "teams.automations.authorization.error",
              "Authorization could not continue",
            ));
            setAuthorizationFlow({ state: "idle" });
            return;
          }
        }
        if (
          error instanceof TeamAutomationApiError &&
          error.code === "TEAM_AUTOMATION_AUTHORIZATION_PLAN_CHANGED"
        ) {
          setAuthorizationFlow({ state: "plan_changed", draft: nextDraft, mode, scheduleId });
          return;
        }
        void message.error(
          error instanceof TeamAutomationApiError
            ? error.message
            : copy(
                "teams.automations.authorization.error",
                "Authorization could not continue",
              ),
        );
        setAuthorizationFlow({ state: "idle" });
      }
    },
    [copy, redirectToBindingRecovery],
  );

  React.useEffect(() => {
    if (!canQuery || !routeMemberAuthority || typeof window === "undefined") return;
    const routeKey = JSON.stringify(route);
    if (recoveredRouteRef.current === routeKey) return;
    recoveredRouteRef.current = routeKey;
    const recovered = consumeTeamAutomationAuthorizationDraft(
      window.sessionStorage,
      routeMemberAuthority,
    );
    if (!recovered) return;
    const recoveredDraft: Draft = {
      memberId: recovered.memberId,
      displayName: recovered.displayName,
      prompt: recovered.prompt,
      cronExpression: recovered.scheduleCron,
      timezone: recovered.scheduleTimezone,
      enabled: recovered.enabled,
    };
    setDraft(recoveredDraft);
    setFormMode("create");
    setEditing(null);
    setFormOpen(true);
    void beginPreflight(
      authorizationDraft(route, recoveredDraft),
      recovered.mode,
      recovered.scheduleId,
    );
  }, [beginPreflight, canQuery, route, routeMemberAuthority]);

  const openCreate = () => {
    setDraft({
      ...initialDraft,
      memberId: routeMember?.memberId ?? "",
      timezone: defaultTimezone(),
    });
    setEditing(null);
    setFormMode("create");
    setAuthorizationFlow({ state: "idle" });
    setPreviewTimes([]);
    setFormOpen(true);
  };

  const openEdit = (view: TeamAutomationView) => {
    setDraft(fromView(view));
    setEditing(view);
    setFormMode("edit");
    setAuthorizationFlow({ state: "idle" });
    setPreviewTimes([]);
    setFormOpen(true);
  };

  const validateDraft = (): TeamAutomationCreateDraft | null => {
    const next = authorizationDraft(route, draft);
    if (!next.cronExpression) {
      void message.error("Enter a cron expression.");
      return null;
    }
    if (next.prompt.length > promptMaxLength) {
      void message.error(`Recurring prompt must be ${promptMaxLength} characters or fewer.`);
      return null;
    }
    return next;
  };

  const reviewAuthorization = () => {
    const next = validateDraft();
    if (next) void beginPreflight(next, "create");
  };

  const reviewReauthorization = (view: TeamAutomationView) => {
    const nextDraft = fromView(view);
    setDraft(nextDraft);
    setEditing(view);
    setFormMode("edit");
    setFormOpen(true);
    void beginPreflight(authorizationDraft(view, nextDraft), "reauthorize", view.scheduleId);
  };

  const submitAuthorization = async () => {
    if (authorizationFlow.state !== "reviewing") return;
    const { draft: confirmedDraft, mode, review, scheduleId } = authorizationFlow;
    const identity = createTeamAutomationOperationIdentity();
    setAuthorizationFlow({
      state: "submitting",
      draft: confirmedDraft,
      identity,
      mode,
      review,
      scheduleId,
    });
    try {
      const receipt = mode === "create"
        ? await teamAutomationApi.create(
            confirmedDraft,
            review.permissionDigest,
            review.policyVersion,
            identity,
          )
        : await teamAutomationApi.reauthorize(
            exactAutomationRoute(confirmedDraft),
            scheduleId ?? "",
            confirmedDraft,
            review.permissionDigest,
            review.policyVersion,
            identity,
          );
      setAuthorizationFlow({
        state: "pending",
        scheduleId: receipt.scheduleId,
        baselineStateVersion: editing?.stateVersion ?? 0,
      });
      setMutationObservation({
        kind: mode,
        scheduleId: receipt.scheduleId,
        acceptedAt: Date.now(),
        baselineStateVersion: mode === "reauthorize" ? editing?.stateVersion ?? 0 : 0,
        expectedDraft: confirmedDraft,
      });
      void message.info(copy(
        "teams.automations.messages.authorizationAccepted",
        "Authorization request accepted",
      ));
      setFormOpen(false);
      await invalidate();
    } catch (error) {
      if (requiresBindingRecovery(error)) {
        try {
          await redirectToBindingRecovery({
            draft: confirmedDraft,
            mode,
            scheduleId,
          });
          return;
        } catch {
          void message.error(copy(
            "teams.automations.authorization.error",
            "Authorization could not continue",
          ));
          setAuthorizationFlow({ state: "idle" });
          return;
        }
      }
      if (
        error instanceof TeamAutomationApiError &&
        error.code === "TEAM_AUTOMATION_AUTHORIZATION_PLAN_CHANGED"
      ) {
        setAuthorizationFlow({
          state: "plan_changed",
          draft: confirmedDraft,
          mode,
          scheduleId,
        });
        return;
      }
      void message.error(copy(
        "teams.automations.authorization.error",
        "Authorization could not continue",
      ));
      setAuthorizationFlow({ state: "idle" });
    }
  };

  const updateAutomation = async () => {
    if (!editing) return;
    const next = validateDraft();
    if (!next) return;
    const identity = createTeamAutomationOperationIdentity();
    setBusyScheduleId(editing.scheduleId);
    try {
      const receipt = await teamAutomationApi.update(exactAutomationRoute(editing), editing.scheduleId, {
        displayName: next.displayName,
        prompt: next.prompt,
        cronExpression: next.cronExpression,
        timezone: next.timezone,
        enabled: next.enabled,
      }, identity);
      setMutationObservation({
        kind: "update",
        scheduleId: receipt.scheduleId,
        acceptedAt: Date.now(),
        baselineStateVersion: editing.stateVersion,
        expectedDraft: next,
      });
      void message.info(copy("teams.automations.messages.updateAccepted", "Update request accepted"));
      setFormOpen(false);
      await invalidate();
    } catch (error) {
      if (requiresBindingRecovery(error)) {
        try {
          await redirectToBindingRecovery({
            draft: next,
            mode: "reauthorize",
            scheduleId: editing.scheduleId,
          });
          return;
        } catch (redirectError) {
          void message.error(
            redirectError instanceof Error
              ? redirectError.message
              : String(redirectError),
          );
          return;
        }
      }
      if (
        error instanceof TeamAutomationApiError &&
        error.code === "TEAM_AUTOMATION_REAUTHORIZATION_REQUIRED"
      ) {
        await beginPreflight(next, "reauthorize", editing.scheduleId);
        return;
      }
      void message.error(error instanceof Error ? error.message : String(error));
    } finally {
      setBusyScheduleId("");
    }
  };

  const runAction = async (
    view: TeamAutomationView,
    action: "pause" | "resume" | "runNow" | "delete" | "retryRevocation",
  ) => {
    setBusyScheduleId(view.scheduleId);
    try {
      let receipt: TeamAutomationMutationReceipt;
      if (action === "retryRevocation") {
        receipt = await teamAutomationApi.retryRevocation(
          exactAutomationRoute(view),
          view.scheduleId,
        );
        void message.info(copy(
          "teams.automations.messages.revocationRetryAccepted",
          "Revocation retry accepted",
        ));
      } else {
        const identity = createTeamAutomationOperationIdentity();
        receipt = await teamAutomationApi[action](
          exactAutomationRoute(view),
          view.scheduleId,
          identity,
        );
        void message.info(
          action === "runNow"
            ? copy("teams.automations.messages.runAccepted", "Run request accepted")
            : action === "delete"
              ? copy("teams.automations.messages.deleteAccepted", "Delete request accepted")
              : action === "pause"
                ? copy("teams.automations.messages.pauseAccepted", "Pause request accepted")
                : copy("teams.automations.messages.resumeAccepted", "Resume request accepted"),
        );
      }
      setMutationObservation({
        kind: action,
        scheduleId: receipt.scheduleId,
        acceptedAt: Date.now(),
        baselineStateVersion: view.stateVersion,
        baselineLastFireAt: action === "runNow" ? view.lastFireAt : undefined,
      });
      await invalidate();
    } catch (error) {
      if (requiresBindingRecovery(error)) {
        try {
          await redirectToBindingRecovery({ route: exactAutomationRoute(view) });
          return;
        } catch (redirectError) {
          void message.error(
            redirectError instanceof Error
              ? redirectError.message
              : String(redirectError),
          );
          return;
        }
      }
      void message.error(error instanceof Error ? error.message : String(error));
    } finally {
      setBusyScheduleId("");
    }
  };

  const preview = async () => {
    const cronExpression = trim(draft.cronExpression);
    if (!cronExpression) return;
    try {
      const result = await previewScheduledDispatch({
        cronExpression,
        timezone: trim(draft.timezone) || undefined,
        count: 3,
      });
      setPreviewTimes(result.nextFireTimes);
    } catch (error) {
      void message.error(error instanceof Error ? error.message : String(error));
    }
  };

  const automationStatus = (
    view: TeamAutomationView,
  ): "active" | "error" | "paused" | "pending" => {
    if (
      view.revocationPending ||
      ["needs_authorization", "failed"].includes(view.authorizationStatus)
    ) {
      return "error";
    }
    if (isPending(view)) return "pending";
    return view.enabled ? "active" : "paused";
  };

  const renderStatusPill = (view: TeamAutomationView) => {
    const status = automationStatus(view);
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
          : status === "pending"
            ? {
                background: token.colorInfoBg,
                border: `1px solid ${token.colorInfoBorder}`,
                color: token.colorInfo,
              }
            : {
                background: token.colorSuccessBg,
                border: `1px solid ${token.colorSuccessBorder}`,
                color: token.colorSuccess,
              };
    const statusLabel =
      status === "error"
        ? credentialLabel(view)
        : status === "paused"
          ? copy("teams.automations.status.paused", "Paused")
          : status === "pending"
            ? credentialLabel(view)
            : copy("teams.automations.status.active", "Active");

    return (
      <span aria-label={statusLabel} role="status" style={{ ...automationStatusBadgeStyle, ...statusStyle }}>
        <span aria-hidden="true" style={{ ...automationStatusDotStyle, background: "currentColor" }} />
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
        <span style={{ alignItems: "center", display: "inline-flex", fontSize: 16 }}>{icon}</span>
        <div style={{ display: "grid", gap: 2, minWidth: 0 }}>
          <Typography.Text style={{ color: "inherit", fontSize: 20, lineHeight: 1 }} strong>{value}</Typography.Text>
          <Typography.Text ellipsis style={{ color: "inherit", fontSize: 12, opacity: 0.82 }}>{label}</Typography.Text>
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
    danger = false,
    icon,
    label,
    onClick,
    primary = false,
    disabled = false,
  }: {
    readonly danger?: boolean;
    readonly disabled?: boolean;
    readonly icon: React.ReactNode;
    readonly label: string;
    readonly onClick: () => void;
    readonly primary?: boolean;
  }) => (
    <Tooltip title={label}>
      <Button
        aria-label={label}
        className="team-automation-action-button"
        danger={danger}
        disabled={disabled}
        icon={icon}
        onClick={onClick}
        size="small"
        style={buildAutomationActionButtonStyle(
          danger ? "danger" : primary ? "primary" : "default",
        )}
      />
    </Tooltip>
  );

  const renderActions = (view: TeamAutomationView) => {
    const busy = busyScheduleId === view.scheduleId;
    const active = view.authorizationStatus === "active";
    const canReauthorize =
      view.authorizationStatus === "needs_authorization" ||
      (view.authorizationStatus === "failed" && !view.revocationPending);
    const canDelete =
      !view.revocationPending &&
      ["provisioning_pending", "active", "needs_authorization", "failed"].includes(
        view.authorizationStatus,
      );
    return (
      <div
        className="team-automation-actions"
        style={{
          ...automationActionGroupBaseStyle,
          background: token.colorFillQuaternary,
          border: `1px solid ${token.colorBorderSecondary}`,
        }}
      >
        {renderAutomationActionButton({
          icon: <HistoryOutlined />,
          label: copy("teams.automations.actions.viewRuns", "View runs"),
          onClick: () =>
            history.push(
              buildTeamMemberPublishedRunsHref({
                scopeId: view.scopeId,
                teamId: view.teamId,
                memberId: view.memberId,
                scheduleId: view.scheduleId,
              }),
            ),
        })}
        {active
          ? renderAutomationActionButton({
              icon: <EditOutlined />,
              label: copy("teams.automations.actions.edit", "Edit"),
              disabled: busy,
              onClick: () => openEdit(view),
            })
          : null}
        {active ? (
          renderAutomationActionButton({
            icon: <ThunderboltOutlined />,
            label: copy("teams.automations.actions.runNow", "Run now"),
            disabled: busy,
            onClick: () => void runAction(view, "runNow"),
            primary: true,
          })
        ) : null}
        {active ? (
          renderAutomationActionButton({
            icon: view.enabled ? <PauseCircleOutlined /> : <PlayCircleOutlined />,
            label: view.enabled
              ? copy("teams.automations.actions.pause", "Pause")
              : copy("teams.automations.actions.resume", "Resume"),
            disabled: busy,
            onClick: () => void runAction(view, view.enabled ? "pause" : "resume"),
          })
        ) : null}
        {canReauthorize ? (
          renderAutomationActionButton({
            icon: <SafetyCertificateOutlined />,
            label: copy("teams.automations.actions.reauthorize", "Review and reauthorize"),
            disabled: busy,
            onClick: () => reviewReauthorization(view),
          })
        ) : null}
        {view.revocationPending ? (
          renderAutomationActionButton({
            icon: <ReloadOutlined />,
            label: copy("teams.automations.actions.retryRevocation", "Retry revocation"),
            disabled: busy,
            onClick: () => void runAction(view, "retryRevocation"),
          })
        ) : null}
        {canDelete ? (
          renderAutomationActionButton({
            danger: true,
            icon: <DeleteOutlined />,
            label: copy("teams.automations.actions.delete", "Delete"),
            disabled: busy,
            onClick: () =>
                Modal.confirm({
                  title: copy("teams.automations.delete.title", "Delete automation?"),
                  content: copy(
                    "teams.automations.delete.description",
                    "The row remains visible until NyxID and Vault revocation are complete.",
                  ),
                  okText: copy("teams.automations.actions.delete", "Delete"),
                  okButtonProps: { danger: true },
                  onOk: () => runAction(view, "delete"),
                }),
          })
        ) : null}
      </div>
    );
  };

  const renderRow = (view: TeamAutomationView) => {
    const cadence = describeDraftCadence(view.cronExpression, view.timezone, copy);
    const ownerMember = membersById.get(trim(view.memberId));
    const status = automationStatus(view);
    const rowBorderColor =
      status === "error"
        ? token.colorErrorBorder
        : status === "paused"
          ? token.colorWarningBorder
          : status === "pending"
            ? token.colorInfoBorder
            : token.colorBorderSecondary;

    return (
      <article
        aria-label={view.displayName}
        className="team-automation-row"
        key={view.scheduleId}
        style={{
          ...commitmentRowStyle,
          background: token.colorBgContainer,
          border: `1px solid ${rowBorderColor}`,
          borderRadius: 12,
          boxShadow: token.boxShadowTertiary,
        }}
      >
        <div className="team-automation-row__automation" style={{ display: "grid", gap: 7, minWidth: 0 }}>
          <div style={automationNameLineStyle}>
            {renderStatusPill(view)}
            <Typography.Text ellipsis strong>
              {view.displayName || copy("teams.automations.untitled", "Untitled automation")}
            </Typography.Text>
          </div>
          <FactLine
            secondary
            text={copy("teams.automations.row.target", "Workflow chat · {endpoint}", { endpoint: "chat" })}
          />
          {view.lastAuthorizationErrorCode ? (
            <Tooltip placement="topLeft" title={view.lastAuthorizationErrorCode}>
              <Typography.Text ellipsis style={{ color: token.colorError, display: "block", fontSize: 12 }}>
                {view.lastAuthorizationErrorCode}
              </Typography.Text>
            </Tooltip>
          ) : null}
          {view.revocationPending ? (
            <Space size={8} wrap>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {copy("teams.automations.revocation.nyxId", "NyxID: {status}", { status: view.nyxIdRevocationStatus })}
              </Typography.Text>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {copy("teams.automations.revocation.vault", "Vault: {status}", { status: view.vaultRevocationStatus })}
              </Typography.Text>
            </Space>
          ) : null}
        </div>
        <div className="team-automation-row__member" style={{ display: "grid", gap: 5, minWidth: 0 }}>
          <Typography.Text ellipsis strong>{ownerMember?.name ?? "--"}</Typography.Text>
          <FactLine
            rows={2}
            secondary
            text={copy("teams.automations.preview.runsThroughService", "Runs through published service")}
          />
        </div>
        <div className="team-automation-row__schedule" style={{ display: "grid", gap: 5, minWidth: 0 }}>
          <FactLine monospace={false} text={cadence.summary} tooltipText={`${cadence.detail} · ${view.cronExpression}`} />
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {view.timezone}
          </Typography.Text>
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {view.nextFireAt
              ? copy("teams.automations.row.nextRun", "Next {time}", {
                  time: formatCompactDateTime(view.nextFireAt, "--"),
                })
              : copy("teams.automations.row.noNextRun", "No next run")}
          </Typography.Text>
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {view.lastFireAt
              ? copy("teams.automations.row.lastRun", "Last {time}", {
                  time: formatCompactDateTime(view.lastFireAt, "--"),
                })
              : copy("teams.automations.row.noLastRun", "No previous run")}
          </Typography.Text>
        </div>
        {renderActions(view)}
      </article>
    );
  };

  if (routeMemberId && (!routeMember || !routeMember.canAutomateMember)) {
    return (
      <section className="team-automations-layout" aria-live="polite">
        <style>{responsiveStyle}</style>
        <Typography.Title level={3}>
          {copy("teams.automations.memberUnavailable.title", "Member unavailable for automation")}
        </Typography.Title>
        <Typography.Paragraph type="secondary">
          {routeMember?.disabledReason || copy(
            "teams.automations.memberUnavailable.description",
            "This member is not part of the current Team. Choose another member.",
          )}
        </Typography.Paragraph>
      </section>
    );
  }

  const upcomingAutomations = (automationsQuery.data?.items ?? [])
    .filter((automation) => automation.enabled && automation.nextFireAt)
    .slice(0, 3);
  const activeFormMember = membersById.get(trim(draft.memberId));
  const cronPresets = [
    { label: copy("teams.automations.form.preset.weekdaysMorning", "Weekdays · 09:00"), value: "weekdays-0900", cronExpression: "0 9 * * 1-5" },
    { label: copy("teams.automations.form.preset.dailyMorning", "Daily · 09:00"), value: "daily-0900", cronExpression: "0 9 * * *" },
    { label: copy("teams.automations.form.preset.weeklyMonday", "Monday · 09:00"), value: "weekly-monday-0900", cronExpression: "0 9 * * 1" },
    { label: copy("teams.automations.form.preset.hourly", "Hourly"), value: "hourly", cronExpression: "0 * * * *" },
    { label: copy("teams.automations.form.preset.custom", "Custom cron"), value: "custom", cronExpression: "" },
  ] as const;
  const activePreset =
    cronPresets.find((preset) => preset.cronExpression === draft.cronExpression && preset.cronExpression)?.value ??
    "custom";
  const normalizedDraftCron = trim(draft.cronExpression).replace(/\s+/g, " ");
  const formCronValidationMessage = !normalizedDraftCron
    ? copy("teams.automations.messages.cronRequired", "Enter a cron expression first.")
    : normalizedDraftCron.split(" ").length !== 5
      ? copy(
          "teams.automations.form.cronFiveFieldHint",
          "Use a 5-field cron expression: minute hour day month weekday.",
        )
      : "";
  const formCadence = describeDraftCadence(draft.cronExpression, draft.timezone, copy);
  const promptTooLong = draft.prompt.trim().length > promptMaxLength;
  const automationItems = automationsQuery.data?.items ?? [];
  const acceptedCreateObservation =
    mutationObservation?.kind === "create" &&
    mutationObservation.expectedDraft &&
    !automationItems.some(
      (automation) => automation.scheduleId === mutationObservation.scheduleId,
    )
      ? mutationObservation
      : null;
  const activeAutomationCount = automationItems.filter(
    (automation) => automationStatus(automation) === "active",
  ).length;
  const pausedAutomationCount = automationItems.filter(
    (automation) => automationStatus(automation) === "paused",
  ).length;
  const attentionAutomationCount = automationItems.filter(
    (automation) => automationStatus(automation) === "error",
  ).length;
  const unavailableMembers = members.filter((member) => !member.canAutomateMember);
  const mutationObservationExpired =
    mutationObservation !== null &&
    expiredMutationObservation === mutationObservation;
  const renderAcceptedCreateRow = (observation: MutationObservation) => {
    const expectedDraft = observation.expectedDraft;
    if (!expectedDraft) return null;
    const cadence = describeDraftCadence(
      expectedDraft.cronExpression,
      expectedDraft.timezone ?? "UTC",
      copy,
    );
    const ownerMember = membersById.get(trim(expectedDraft.memberId));
    const statusLabel = copy(
      "teams.automations.row.awaitingReadModel",
      "Waiting for schedule sync",
    );
    const displayName = expectedDraft.displayName || copy(
      "teams.automations.form.title",
      "New member automation",
    );

    return (
      <article
        aria-label={displayName}
        className="team-automation-row"
        key={`accepted:${observation.scheduleId}`}
        style={{
          ...commitmentRowStyle,
          borderColor: token.colorInfoBorder,
          boxShadow: "none",
        }}
      >
        <div style={{ display: "grid", gap: 5, minWidth: 0 }}>
          <span
            aria-label={statusLabel}
            role="status"
            style={{
              ...automationStatusBadgeStyle,
              background: token.colorInfoBg,
              border: `1px solid ${token.colorInfoBorder}`,
              color: token.colorInfo,
            }}
          >
            <span
              aria-hidden="true"
              style={{ ...automationStatusDotStyle, background: "currentColor" }}
            />
            {statusLabel}
          </span>
          <Typography.Text ellipsis strong>{displayName}</Typography.Text>
          <FactLine
            rows={2}
            secondary
            text={expectedDraft.prompt || copy(
              "teams.automations.row.noPrompt",
              "No recurring prompt",
            )}
          />
        </div>
        <div style={{ display: "grid", gap: 5, minWidth: 0 }}>
          <Typography.Text ellipsis strong>{ownerMember?.name ?? "--"}</Typography.Text>
          <FactLine
            rows={2}
            secondary
            text={copy(
              "teams.automations.preview.runsThroughService",
              "Runs through published service",
            )}
          />
        </div>
        <div style={{ display: "grid", gap: 5, minWidth: 0 }}>
          <FactLine
            monospace={false}
            text={cadence.summary}
            tooltipText={`${cadence.detail} · ${expectedDraft.cronExpression}`}
          />
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {statusLabel}
          </Typography.Text>
        </div>
        <div style={{ justifySelf: "end" }}>
          <Spin size="small" />
        </div>
      </article>
    );
  };

  return (
    <>
      <style>{responsiveStyle}</style>
      <section
        aria-labelledby="team-automations-title"
        className="team-automations-layout"
        style={pageGridStyle}
      >
        <AevatarPanel>
          <div className="team-automations-panel-header" style={panelHeaderStyle}>
            <div style={titleGroupStyle}>
              <Typography.Title id="team-automations-title" level={3} style={titleStyle}>
                {copy("teams.automations.title", "Automations")}
              </Typography.Title>
              <Typography.Text style={{ maxWidth: 680 }} type="secondary">
                {copy(
                  "teams.automations.description",
                  "Recurring work belongs to a member. The team view shows every commitment so operators can see what will run next and what needs attention.",
                )}
              </Typography.Text>
            </div>
            <Space>
              <Tooltip title={copy("teams.automations.actions.refresh", "Refresh")}>
                <Button
                  aria-label={copy("teams.automations.actions.refresh", "Refresh")}
                  icon={<ReloadOutlined />}
                  loading={automationsQuery.isFetching}
                  onClick={() => void automationsQuery.refetch()}
                />
              </Tooltip>
              <Button
                className="team-automations-create-button"
                disabled={eligibleMembers.length === 0}
                icon={<PlusOutlined />}
                onClick={openCreate}
                style={primaryHeaderButtonStyle}
                type="primary"
              >
                {copy("teams.automations.actions.create", "New automation")}
              </Button>
            </Space>
          </div>

          <div aria-live="polite" style={{ display: "grid", gap: 12, marginTop: 16 }}>
            {automationsQuery.isLoading ? (
              <div
                aria-label={copy("teams.automations.loading", "Loading automations")}
                role="status"
                style={{ display: "grid", gap: 12 }}
              >
                {[0, 1, 2].map((placeholder) => (
                  <Skeleton.Input
                    active
                    block
                    key={placeholder}
                    style={{ borderRadius: 8, height: 78 }}
                  />
                ))}
              </div>
            ) : null}
            {automationsQuery.isError ? (
              <AevatarInspectorEmpty
                compact
                title={copy("teams.automations.error.title", "Automations could not load")}
                description={copy(
                  "teams.automations.error.description",
                  "Refresh the page or try again after the schedule service is available.",
                )}
              />
            ) : null}
            {mutationObservationExpired ? (
              <Alert
                description={copy(
                  "teams.automations.pending.description",
                  "Automatic refresh stopped. Use Refresh to check authoritative state.",
                )}
                message={copy("teams.automations.pending.title", "Still pending")}
                showIcon
                type="warning"
              />
            ) : null}
            {!automationsQuery.isLoading &&
            !automationsQuery.isError &&
            !automationItems.length &&
            !acceptedCreateObservation ? (
              <Empty
                description={routeMember
                  ? copy("teams.automations.empty.member", "No automations for this member")
                  : copy("teams.automations.empty.title", "No recurring work yet")}
              />
            ) : null}
            {automationItems.length || acceptedCreateObservation ? (
              <div style={commitmentGridStyle}>
                <div className="team-automation-summary" style={automationSummaryGridStyle}>
                  {renderSummaryTile({
                    icon: <CheckCircleOutlined />,
                    label: copy("teams.automations.summary.active", "Active"),
                    tone: "success",
                    value: activeAutomationCount,
                  })}
                  {renderSummaryTile({
                    icon: <PauseCircleOutlined />,
                    label: copy("teams.automations.summary.paused", "Paused"),
                    tone: "warning",
                    value: pausedAutomationCount,
                  })}
                  {renderSummaryTile({
                    icon: <ExclamationCircleOutlined />,
                    label: copy("teams.automations.summary.needsAttention", "Need attention"),
                    tone: "error",
                    value: attentionAutomationCount,
                  })}
                </div>
                <div className="team-automation-list-header" style={automationListHeaderStyle}>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {copy("teams.automations.columns.automation", "Automation")}
                  </Typography.Text>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {copy("teams.automations.columns.member", "Member")}
                  </Typography.Text>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {copy("teams.automations.columns.schedule", "Schedule")}
                  </Typography.Text>
                  <Typography.Text style={{ fontSize: 12, justifySelf: "end" }} type="secondary">
                    {copy("teams.automations.columns.actions", "Actions")}
                  </Typography.Text>
                </div>
                {acceptedCreateObservation
                  ? renderAcceptedCreateRow(acceptedCreateObservation)
                  : null}
                {automationItems.map(renderRow)}
              </div>
            ) : null}
          </div>
        </AevatarPanel>

        <div style={{ display: "grid", gap: 16 }}>
          <AevatarPanel>
            <div style={{ display: "grid", gap: 14 }}>
              <div style={{ display: "grid", gap: 4 }}>
                <Typography.Title level={3} style={titleStyle}>
                  {copy("teams.automations.createPanel.title", "Give a member recurring work")}
                </Typography.Title>
                <Typography.Text type="secondary">
                  {copy(
                    "teams.automations.createPanel.description",
                    "Pick a published member, describe the job, choose a cadence, and preview the next runs before creating it.",
                  )}
                </Typography.Text>
              </div>
              {routeMember ? (
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
                  <Typography.Text ellipsis strong>{routeMember.name}</Typography.Text>
                  <FactLine secondary text={copy("teams.automations.member.publishedServiceReady", "Published service ready")} />
                  <DetailPill compact style={routeMember.lifecycleStyle} text={routeMember.lifecycleLabel} />
                </div>
              ) : eligibleMembers.length === 0 ? (
                <AevatarInspectorEmpty
                  compact
                  title={copy("teams.automations.noPublishedMember.title", "Publish a member first")}
                  description={copy(
                    "teams.automations.noPublishedMember.description",
                    "Automations need a member with a published service identity before they can run.",
                  )}
                />
              ) : null}
              <Button
                block
                disabled={eligibleMembers.length === 0}
                icon={<ClockCircleOutlined />}
                onClick={openCreate}
                style={inspectorActionButtonStyle}
                type="primary"
              >
                {copy("teams.automations.actions.addRecurringWork", "Add recurring work")}
              </Button>
            </div>
          </AevatarPanel>

          <AevatarPanel>
            <div style={{ display: "grid", gap: 12 }}>
              <Typography.Title level={3} style={titleStyle}>
                {copy("teams.automations.upcoming.title", "Upcoming")}
              </Typography.Title>
              {upcomingAutomations.length ? (
                upcomingAutomations.map((automation) => (
                  <div key={automation.scheduleId} style={upcomingRowStyle}>
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
                        {formatCompactDateTime(automation.nextFireAt, "--")}
                      </Typography.Text>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {copy(
                          "teams.automations.upcoming.memberCaption",
                          "{memberName} recurring work",
                          {
                            memberName:
                              membersById.get(trim(automation.memberId))?.name ??
                              copy("teams.automations.columns.member", "Member"),
                          },
                        )}
                      </Typography.Text>
                    </div>
                  </div>
                ))
              ) : (
                <Typography.Text type="secondary">
                  {copy("teams.automations.upcoming.empty", "No upcoming runs are visible yet.")}
                </Typography.Text>
              )}
            </div>
          </AevatarPanel>

          {unavailableMembers.length ? (
            <AevatarPanel>
              <div style={{ display: "grid", gap: 10 }}>
                <Typography.Title level={3} style={titleStyle}>
                  {copy("teams.automations.unavailable.title", "Not ready for automation")}
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
      </section>

      {formOpen ? <Modal
        aria-describedby={modalDescriptionId}
        confirmLoading={flowBusy || Boolean(busyScheduleId)}
        destroyOnHidden
        footer={review ? (
          <Space>
            <Button disabled={flowBusy} onClick={() => setAuthorizationFlow({ state: "idle" })}>
              {copy("teams.automations.authorization.back", "Back")}
            </Button>
            <Button loading={flowBusy} onClick={() => void submitAuthorization()} type="primary">
              {copy("teams.automations.authorization.confirm", "Authorize and continue")}
            </Button>
          </Space>
        ) : undefined}
        onCancel={() => {
          setFormOpen(false);
          setAuthorizationFlow({ state: "idle" });
        }}
        onOk={formMode === "edit" ? () => void updateAutomation() : reviewAuthorization}
        okButtonProps={{
          disabled: !activeFormMember || promptTooLong || Boolean(formCronValidationMessage),
        }}
        okText={formMode === "edit"
          ? copy("teams.automations.form.save", "Save changes")
          : copy("teams.automations.form.create", "Create automation")}
        open={formOpen}
        panelRef={modalPanelRef}
        styles={review ? {
          body: { maxHeight: "min(70vh, 640px)", overflowY: "auto" },
        } : undefined}
        title={formMode === "edit"
          ? copy("teams.automations.form.editTitle", "Edit automation")
          : copy("teams.automations.form.title", "New member automation")}
        width={720}
      >
        {review ? (
          <TeamAutomationAuthorizationReview review={review} />
        ) : (
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
                  {copy("teams.automations.form.section.target", "1. Target member")}
                </Typography.Text>
                <Typography.Text id="team-automation-form-description" style={{ fontSize: 12 }} type="secondary">
                  {copy(
                    "teams.automations.form.section.targetHint",
                    "Recurring work runs through the selected member's published service.",
                  )}
                </Typography.Text>
              </div>
              <div style={modalFieldStyle}>
                <Typography.Text strong>{copy("teams.automations.form.member", "Member")}</Typography.Text>
                <Select
                  aria-label={copy("teams.automations.form.memberAria", "Automation member")}
                  disabled={flowBusy || formMode === "edit"}
                  onChange={(memberId) => setDraft((current) => ({ ...current, memberId }))}
                  options={members
                    .filter((member) => member.canAutomateMember)
                    .map((member) => ({ label: member.name, value: member.memberId }))}
                  value={activeFormMember?.memberId}
                />
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {copy("teams.automations.form.identityReady", "Targets the member's published service.")}
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
                <Typography.Text strong>{copy("teams.automations.form.section.work", "2. Work to run")}</Typography.Text>
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {copy(
                    "teams.automations.form.section.workHint",
                    "Name the automation and optionally add a prompt for each run.",
                  )}
                </Typography.Text>
              </div>
              <div style={modalFieldStyle}>
                <Typography.Text strong>{copy("teams.automations.form.displayName", "Name")}</Typography.Text>
                <Input
                  aria-label={copy("teams.automations.form.displayNameAria", "Automation name")}
                  disabled={flowBusy}
                  onChange={(event) => setDraft((current) => ({ ...current, displayName: event.target.value }))}
                  placeholder={copy("teams.automations.form.displayNamePlaceholder", "Daily escalation digest")}
                  value={draft.displayName}
                />
              </div>
              <div style={modalFieldStyle}>
                <Typography.Text strong>{copy("teams.automations.form.prompt", "Recurring prompt (optional)")}</Typography.Text>
                <Input.TextArea
                  aria-label={copy("teams.automations.form.promptAria", "Recurring prompt")}
                  autoSize={{ minRows: 4, maxRows: 7 }}
                  disabled={flowBusy}
                  maxLength={promptMaxLength}
                  onChange={(event) => setDraft((current) => ({ ...current, prompt: event.target.value }))}
                  placeholder={copy(
                    "teams.automations.form.promptPlaceholder",
                    "Summarize escalations, blocked accounts, and follow-up owners.",
                  )}
                  showCount
                  status={promptTooLong ? "error" : undefined}
                  value={draft.prompt}
                />
                <Typography.Text style={{ fontSize: 12 }} type={promptTooLong ? "danger" : "secondary"}>
                  {copy("teams.automations.form.promptLimit", "Up to {maxLength} characters.", { maxLength: promptMaxLength })}
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
                <Typography.Text strong>{copy("teams.automations.form.section.schedule", "3. Schedule")}</Typography.Text>
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {copy(
                    "teams.automations.form.section.scheduleHint",
                    "Choose a common cadence or switch to custom cron for advanced schedules.",
                  )}
                </Typography.Text>
              </div>
              <div style={modalFieldStyle}>
                <Typography.Text strong>{copy("teams.automations.form.cadence", "Cadence")}</Typography.Text>
                <Segmented
                  aria-label={copy("teams.automations.form.cadenceAria", "Automation cadence")}
                  block
                  disabled={flowBusy}
                  onChange={(presetValue) => {
                    const preset = cronPresets.find((candidate) => candidate.value === String(presetValue));
                    if (preset?.cronExpression) {
                      setDraft((current) => ({ ...current, cronExpression: preset.cronExpression }));
                    }
                  }}
                  options={cronPresets.map(({ label, value }) => ({ label, value }))}
                  value={activePreset}
                />
              </div>
              <div
                className="team-automation-form-schedule-grid"
                style={{ display: "grid", gap: 12, gridTemplateColumns: "minmax(0, 1fr) minmax(180px, 0.54fr)" }}
              >
                <div style={modalFieldStyle}>
                  <Typography.Text strong>{copy("teams.automations.form.cron", "Cron expression")}</Typography.Text>
                  <Input
                    aria-label={copy("teams.automations.form.cronAria", "Cron expression")}
                    disabled={flowBusy}
                    onChange={(event) => setDraft((current) => ({ ...current, cronExpression: event.target.value }))}
                    status={formCronValidationMessage ? "error" : undefined}
                    value={draft.cronExpression}
                  />
                  {formCronValidationMessage ? (
                    <Typography.Text style={{ fontSize: 12 }} type="danger">{formCronValidationMessage}</Typography.Text>
                  ) : null}
                </div>
                <div style={modalFieldStyle}>
                  <Typography.Text strong>{copy("teams.automations.form.timezone", "Timezone")}</Typography.Text>
                  <Input
                    aria-label={copy("teams.automations.form.timezoneAria", "Timezone")}
                    disabled={flowBusy}
                    onChange={(event) => setDraft((current) => ({ ...current, timezone: event.target.value }))}
                    value={draft.timezone}
                  />
                </div>
              </div>
              <div
                className="team-automation-form-schedule-grid"
                style={{ display: "grid", gap: 12, gridTemplateColumns: "minmax(0, 1fr) minmax(160px, 0.42fr)" }}
              >
                <div
                  style={{
                    ...scheduleInsightStyle,
                    background: token.colorBgContainer,
                    border: `1px solid ${token.colorBorderSecondary}`,
                  }}
                >
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {copy("teams.automations.form.scheduleReadsAs", "Schedule reads as")}
                  </Typography.Text>
                  <Typography.Text strong>{formCadence.detail}</Typography.Text>
                </div>
                <div style={enabledFieldStyle}>
                  <Typography.Text strong>{copy("teams.automations.form.enabled", "Enabled")}</Typography.Text>
                  <Switch
                    aria-label={copy("teams.automations.form.enabled", "Enabled")}
                    checked={draft.enabled}
                    disabled={flowBusy}
                    onChange={(enabled) => setDraft((current) => ({ ...current, enabled }))}
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
                    onClick={() => void preview()}
                  >
                    {copy("teams.automations.form.preview", "Preview next runs")}
                  </Button>
                  <Typography.Text type="secondary">
                    {copy("teams.automations.form.previewHint", "Preview uses the schedule service before saving.")}
                  </Typography.Text>
                </Space>
                {previewTimes.length ? (
                  <div style={{ display: "grid", gap: 4 }}>
                    {previewTimes.map((time) => (
                      <Typography.Text key={time} style={{ fontSize: 12 }}>
                        {formatCompactDateTime(time, time)}
                      </Typography.Text>
                    ))}
                  </div>
                ) : (
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {copy("teams.automations.form.previewEmpty", "Preview the cadence to confirm the next scheduled runs.")}
                  </Typography.Text>
                )}
              </div>
            </div>

            {authorizationFlow.state === "preflighting" ? (
              <Spin
                aria-label={copy(
                  "teams.automations.authorization.preparing",
                  "Preparing authorization review",
                )}
              />
            ) : null}
            {authorizationFlow.state === "plan_changed" ? (
              <Alert
                action={(
                  <Button onClick={() => void beginPreflight(authorizationFlow.draft, authorizationFlow.mode, authorizationFlow.scheduleId)}>
                    {copy("teams.automations.authorization.reviewAgain", "Review again")}
                  </Button>
                )}
                description={copy(
                  "teams.automations.planChanged.description",
                  "The previous digest and operation identity were discarded. Run preflight and consent again.",
                )}
                message={copy("teams.automations.planChanged.title", "Authorization plan changed")}
                showIcon
                type="warning"
              />
            ) : null}
          </div>
        )}
      </Modal> : null}
    </>
  );
};

export default TeamAutomationsTab;
