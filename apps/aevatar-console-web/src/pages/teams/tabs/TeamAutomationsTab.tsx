import {
  DeleteOutlined,
  EditOutlined,
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
  Space,
  Spin,
  Switch,
  Tag,
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
import TeamAutomationAuthorizationReview from "../components/TeamAutomationAuthorizationReview";
import {
  consumeTeamAutomationAuthorizationDraft,
  saveTeamAutomationAuthorizationDraft,
  type TeamAutomationAuthorizationDraftInput,
} from "../teamAutomationAuthorizationDraftSession";

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

type Props = {
  readonly members?: readonly TeamAutomationMemberRow[];
  readonly routeMemberId?: string;
  readonly scopeId: string;
  readonly serviceIdentitiesLoading?: boolean;
  readonly teamId: string;
};

type Draft = {
  readonly displayName: string;
  readonly prompt: string;
  readonly cronExpression: string;
  readonly timezone: string;
  readonly enabled: boolean;
};

type AuthorizationMode = "create" | "reauthorize";

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
    }
  | {
      readonly state: "error";
      readonly code: string;
      readonly draft: TeamAutomationCreateDraft;
      readonly message: string;
      readonly mode: AuthorizationMode;
      readonly scheduleId?: string;
    };

const listTake = 200;
const promptMaxLength = 4_000;
const pendingPollIntervalMs = 2_000;
const pendingPollDurationMs = 60_000;
const retryablePreflightCodes = new Set([
  "TEAM_AUTOMATION_AUTHORIZATION_PROJECTION_PENDING",
  "TEAM_AUTOMATION_AUTHORIZATION_REFRESH_SUPERSEDED",
  "TEAM_AUTOMATION_AUTHORIZATION_REFRESH_UNAVAILABLE",
]);
const initialDraft: Draft = {
  displayName: "",
  prompt: "",
  cronExpression: "0 9 * * 1-5",
  timezone: "UTC",
  enabled: true,
};

const responsiveStyles = `
.team-automations-surface, .team-automations-surface * { box-sizing: border-box; }
.team-automations-list { display: grid; gap: 10px; }
.team-automation-row {
  align-items: start;
  border: 1px solid var(--ant-color-border-secondary);
  border-radius: 8px;
  display: grid;
  gap: 16px;
  grid-template-columns: minmax(220px, 1.25fr) minmax(170px, .8fr) minmax(210px, 1fr) max-content;
  padding: 16px;
}
.team-automation-actions { display: flex; flex-wrap: wrap; gap: 6px; justify-content: flex-end; }
.team-automation-action:focus-visible { outline: 2px solid var(--ant-color-primary); outline-offset: 2px; }
@media (max-width: 960px) {
  .team-automation-row { grid-template-columns: minmax(0, 1fr) minmax(180px, .65fr); }
  .team-automation-actions { grid-column: 1 / -1; justify-content: flex-start; }
}
@media (max-width: 620px) {
  .team-automation-row { grid-template-columns: minmax(0, 1fr); padding: 14px; }
  .team-automation-actions { grid-column: 1; }
  .team-automation-actions .ant-btn { flex: 1 1 44px; }
}
`;

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

function fromView(view: TeamAutomationView): Draft {
  return {
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

function credentialColor(view: TeamAutomationView): string {
  if (view.authorizationStatus === "active") return "success";
  if (["needs_authorization", "failed", "revocation_pending"].includes(view.authorizationStatus)) {
    return "error";
  }
  return "processing";
}

function expiresSoon(value: string | null): boolean {
  if (!value) return false;
  const expiresAt = Date.parse(value);
  return Number.isFinite(expiresAt) && expiresAt - Date.now() <= 14 * 24 * 60 * 60 * 1_000;
}

function authorizationDraft(
  route: TeamAutomationRoute,
  draft: Draft,
): TeamAutomationCreateDraft {
  return {
    ...route,
    displayName: trim(draft.displayName),
    prompt: trim(draft.prompt),
    cronExpression: trim(draft.cronExpression),
    timezone: trim(draft.timezone) || "UTC",
    enabled: draft.enabled,
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
  const route = React.useMemo<TeamAutomationRoute>(
    () => ({ scopeId: trim(scopeId), teamId: trim(teamId), memberId: routeMemberId }),
    [routeMemberId, scopeId, teamId],
  );
  const routeMember = members.find((member) => trim(member.memberId) === routeMemberId);
  const canQuery = Boolean(route.scopeId && route.teamId && route.memberId && routeMember?.canAutomateMember);
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
  const [notice, setNotice] = React.useState("");
  const [busyScheduleId, setBusyScheduleId] = React.useState("");
  const [previewText, setPreviewText] = React.useState("");
  const pollingStartedAtRef = React.useRef<number | null>(null);
  const recoveredRouteRef = React.useRef("");

  const automationsQuery = useQuery({
    enabled: canQuery,
    queryKey,
    queryFn: () => teamAutomationApi.listAll(route, { take: listTake }),
    retry: (failureCount, error) =>
      failureCount < 3 &&
      error instanceof TeamAutomationApiError &&
      Boolean(error.code && retryablePreflightCodes.has(error.code)),
    retryDelay: (attempt) => [500, 1_000, 2_000][Math.min(attempt, 2)],
    refetchInterval: (query) => {
      const data = query.state.data;
      const pending =
        authorizationFlow.state === "pending" ||
        Boolean(data?.items.some(isPending));
      if (!pending) {
        pollingStartedAtRef.current = null;
        return false;
      }
      pollingStartedAtRef.current ??= Date.now();
      return Date.now() - pollingStartedAtRef.current < pendingPollDurationMs
        ? pendingPollIntervalMs
        : false;
    },
    refetchOnWindowFocus: true,
  });

  const invalidate = React.useCallback(
    () => queryClient.invalidateQueries({ queryKey }),
    [queryClient, queryKey],
  );

  const beginPreflight = React.useCallback(
    async (nextDraft: TeamAutomationCreateDraft, mode: AuthorizationMode, scheduleId?: string) => {
      setAuthorizationFlow({ state: "preflighting", draft: nextDraft, mode, scheduleId });
      try {
        const review = await retryTypedPreflight(async () => {
          await teamAutomationApi.refreshAuthorizationCatalog();
          return teamAutomationApi.preflightCreate(nextDraft);
        });
        if (review.status === "plan-changed") {
          setAuthorizationFlow({ state: "plan_changed", draft: nextDraft, mode, scheduleId });
          return;
        }
        setAuthorizationFlow({ state: "reviewing", draft: nextDraft, mode, review, scheduleId });
      } catch (error) {
        if (
          error instanceof TeamAutomationApiError &&
          error.code === "TEAM_AUTOMATION_AUTHORIZATION_BINDING_REQUIRED" &&
          typeof window !== "undefined"
        ) {
          saveTeamAutomationAuthorizationDraft(
            window.sessionStorage,
            recoveryDraft(nextDraft, mode, scheduleId),
          );
          try {
            const returnTo = buildTeamMemberAutomationsHref(route);
            await new NyxIDAuthClient(getNyxIDRuntimeConfig()).loginWithRedirect({
              returnTo,
              prompt: "consent",
            });
            return;
          } catch (redirectError) {
            setAuthorizationFlow({
              state: "error",
              code: "TEAM_AUTOMATION_AUTHORIZATION_REDIRECT_FAILED",
              draft: nextDraft,
              message: redirectError instanceof Error ? redirectError.message : String(redirectError),
              mode,
              scheduleId,
            });
            return;
          }
        }
        setAuthorizationFlow({
          state: "error",
          code: error instanceof TeamAutomationApiError ? error.code ?? "TEAM_AUTOMATION_PREFLIGHT_FAILED" : "TEAM_AUTOMATION_PREFLIGHT_FAILED",
          draft: nextDraft,
          message: error instanceof Error ? error.message : String(error),
          mode,
          scheduleId,
        });
      }
    },
    [route],
  );

  React.useEffect(() => {
    if (!canQuery || typeof window === "undefined") return;
    const routeKey = JSON.stringify(route);
    if (recoveredRouteRef.current === routeKey) return;
    recoveredRouteRef.current = routeKey;
    const recovered = consumeTeamAutomationAuthorizationDraft(window.sessionStorage, route);
    if (!recovered) return;
    const recoveredDraft: Draft = {
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
  }, [beginPreflight, canQuery, route]);

  const openCreate = () => {
    setDraft({ ...initialDraft, timezone: defaultTimezone() });
    setEditing(null);
    setFormMode("create");
    setAuthorizationFlow({ state: "idle" });
    setPreviewText("");
    setFormOpen(true);
  };

  const openEdit = (view: TeamAutomationView) => {
    setDraft(fromView(view));
    setEditing(view);
    setFormMode("edit");
    setAuthorizationFlow({ state: "idle" });
    setPreviewText("");
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
    void beginPreflight(authorizationDraft(route, nextDraft), "reauthorize", view.scheduleId);
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
            route,
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
      setNotice(copy(
        "teams.automations.messages.authorizationAccepted",
        "Authorization request accepted",
      ));
      setFormOpen(false);
      await invalidate();
    } catch (error) {
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
      setAuthorizationFlow({
        state: "error",
        code: error instanceof TeamAutomationApiError ? error.code ?? "TEAM_AUTOMATION_SUBMIT_FAILED" : "TEAM_AUTOMATION_SUBMIT_FAILED",
        draft: confirmedDraft,
        message: error instanceof Error ? error.message : String(error),
        mode,
        scheduleId,
      });
    }
  };

  const updateAutomation = async () => {
    if (!editing) return;
    const next = validateDraft();
    if (!next) return;
    const identity = createTeamAutomationOperationIdentity();
    setBusyScheduleId(editing.scheduleId);
    try {
      await teamAutomationApi.update(route, editing.scheduleId, {
        displayName: next.displayName,
        prompt: next.prompt,
        cronExpression: next.cronExpression,
        timezone: next.timezone,
        enabled: next.enabled,
      }, identity);
      setNotice(copy("teams.automations.messages.updateAccepted", "Update request accepted"));
      setFormOpen(false);
      await invalidate();
    } catch (error) {
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
      if (action === "retryRevocation") {
        await teamAutomationApi.retryRevocation(route, view.scheduleId);
        setNotice(copy(
          "teams.automations.messages.revocationRetryAccepted",
          "Revocation retry accepted",
        ));
      } else {
        const identity = createTeamAutomationOperationIdentity();
        await teamAutomationApi[action](route, view.scheduleId, identity);
        setNotice(
          action === "runNow"
            ? copy("teams.automations.messages.runAccepted", "Run request accepted")
            : action === "delete"
              ? copy("teams.automations.messages.deleteAccepted", "Delete request accepted")
              : action === "pause"
                ? copy("teams.automations.messages.pauseAccepted", "Pause request accepted")
                : copy("teams.automations.messages.resumeAccepted", "Resume request accepted"),
        );
      }
      await invalidate();
    } catch (error) {
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
      setPreviewText(result.nextFireTimes.map((value) => formatCompactDateTime(value, value)).join(" / "));
    } catch (error) {
      void message.error(error instanceof Error ? error.message : String(error));
    }
  };

  const renderActions = (view: TeamAutomationView) => {
    const busy = busyScheduleId === view.scheduleId;
    const active = view.authorizationStatus === "active";
    const canReauthorize =
      view.authorizationStatus === "needs_authorization" ||
      (view.authorizationStatus === "failed" && !view.revocationPending);
    const canDelete = !["deleting", "revocation_pending"].includes(view.authorizationStatus);
    return (
      <div className="team-automation-actions">
        {active ? (
          <Tooltip title={copy("teams.automations.actions.runNow", "Run now")}>
            <Button
              aria-label={copy("teams.automations.actions.runNow", "Run now")}
              className="team-automation-action"
              disabled={busy}
              icon={<ThunderboltOutlined />}
              onClick={() => void runAction(view, "runNow")}
            />
          </Tooltip>
        ) : null}
        {active ? (
          <Tooltip title={view.enabled
            ? copy("teams.automations.actions.pause", "Pause")
            : copy("teams.automations.actions.resume", "Resume")}>
            <Button
              aria-label={view.enabled
                ? copy("teams.automations.actions.pause", "Pause")
                : copy("teams.automations.actions.resume", "Resume")}
              className="team-automation-action"
              disabled={busy}
              icon={view.enabled ? <PauseCircleOutlined /> : <PlayCircleOutlined />}
              onClick={() => void runAction(view, view.enabled ? "pause" : "resume")}
            />
          </Tooltip>
        ) : null}
        {active ? (
          <Tooltip title={copy("teams.automations.actions.edit", "Edit")}>
            <Button
              aria-label={copy("teams.automations.actions.edit", "Edit")}
              className="team-automation-action"
              disabled={busy}
              icon={<EditOutlined />}
              onClick={() => openEdit(view)}
            />
          </Tooltip>
        ) : null}
        {canReauthorize ? (
          <Button
            disabled={busy}
            icon={<SafetyCertificateOutlined />}
            onClick={() => reviewReauthorization(view)}
          >
            {copy("teams.automations.actions.reauthorize", "Review and reauthorize")}
          </Button>
        ) : null}
        {view.revocationPending ? (
          <Button
            disabled={busy}
            icon={<ReloadOutlined />}
            onClick={() => void runAction(view, "retryRevocation")}
          >
            {copy("teams.automations.actions.retryRevocation", "Retry revocation")}
          </Button>
        ) : null}
        {canDelete ? (
          <Tooltip title={copy("teams.automations.actions.delete", "Delete")}>
            <Button
              aria-label={copy("teams.automations.actions.delete", "Delete")}
              className="team-automation-action"
              danger
              disabled={busy}
              icon={<DeleteOutlined />}
              onClick={() =>
                Modal.confirm({
                  title: copy("teams.automations.delete.title", "Delete automation?"),
                  content: copy(
                    "teams.automations.delete.description",
                    "The row remains visible until NyxID and Vault revocation are complete.",
                  ),
                  okText: copy("teams.automations.actions.delete", "Delete"),
                  okButtonProps: { danger: true },
                  onOk: () => runAction(view, "delete"),
                })
              }
            />
          </Tooltip>
        ) : null}
      </div>
    );
  };

  const renderRow = (view: TeamAutomationView) => (
    <article className="team-automation-row" key={view.scheduleId}>
      <div>
        <Typography.Text strong>
          {view.displayName || copy("teams.automations.untitled", "Untitled automation")}
        </Typography.Text>
        <Typography.Paragraph type="secondary" ellipsis={{ rows: 2 }} style={{ margin: "6px 0 0" }}>
          {view.prompt || copy("teams.automations.row.noPrompt", "No recurring prompt")}
        </Typography.Paragraph>
        {view.lastAuthorizationErrorCode ? (
          <details>
            <summary>{copy("teams.automations.authorization.diagnostics", "Authorization diagnostics")}</summary>
            <Typography.Text code>{view.lastAuthorizationErrorCode}</Typography.Text>
          </details>
        ) : null}
      </div>
      <div style={{ display: "grid", gap: 8, justifyItems: "start" }}>
        <Tag color={view.enabled ? "green" : "default"}>
          {view.enabled
            ? copy("teams.automations.firing.enabled", "Firing enabled")
            : copy("teams.automations.firing.paused", "Firing paused")}
        </Tag>
        <Tag color={credentialColor(view)}>
          {copy(
            `teams.automations.authorizationStatus.${view.authorizationStatus}`,
            credentialLabel(view),
          )}
        </Tag>
        {expiresSoon(view.credentialExpiresAtUtc) ? (
          <Typography.Text type="warning">
            {copy("teams.automations.expiry.soon", "Credential expires within 14 days")}
          </Typography.Text>
        ) : null}
      </div>
      <div>
        <Typography.Text>{view.cronExpression}</Typography.Text>
        <Typography.Paragraph type="secondary" style={{ margin: "4px 0 0" }}>
          {view.timezone} · {copy(
            "teams.automations.schedule.next",
            "Next {time}",
            {
              time: formatCompactDateTime(
                view.nextFireAt,
                copy("teams.automations.schedule.notScheduled", "not scheduled"),
              ),
            },
          )}
        </Typography.Paragraph>
        {view.revocationPending ? (
          <Space size={[6, 6]} wrap>
            <Tag>{copy("teams.automations.revocation.nyxId", "NyxID: {status}", { status: view.nyxIdRevocationStatus })}</Tag>
            <Tag>{copy("teams.automations.revocation.vault", "Vault: {status}", { status: view.vaultRevocationStatus })}</Tag>
          </Space>
        ) : null}
      </div>
      {renderActions(view)}
    </article>
  );

  if (!routeMemberId) {
    return (
      <section className="team-automations-surface" aria-labelledby="team-automations-title">
        <style>{responsiveStyles}</style>
        <Typography.Title id="team-automations-title" level={3}>
          {copy("teams.automations.memberSelector.title", "Choose a team member")}
        </Typography.Title>
        <Typography.Paragraph type="secondary">
          {copy(
            "teams.automations.memberSelector.description",
            "Automations are owned by one published member. Choose the member to open its canonical resource.",
          )}
        </Typography.Paragraph>
        <Space wrap>
          {members.map((member) => (
            <Button
              key={member.memberId}
              onClick={() => history.push(member.automationsHref || buildTeamMemberAutomationsHref({
                scopeId: route.scopeId,
                teamId: route.teamId,
                memberId: member.memberId,
              }))}
            >
              {member.name}
            </Button>
          ))}
        </Space>
      </section>
    );
  }

  if (!routeMember || !routeMember.canAutomateMember) {
    return (
      <section className="team-automations-surface" aria-live="polite">
        <style>{responsiveStyles}</style>
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

  const flowBusy = authorizationFlow.state === "preflighting" || authorizationFlow.state === "submitting";
  const review = authorizationFlow.state === "reviewing" || authorizationFlow.state === "submitting"
    ? authorizationFlow.review
    : null;

  return (
    <section className="team-automations-surface" aria-labelledby="team-automations-title">
      <style>{responsiveStyles}</style>
      <div style={{ alignItems: "flex-start", display: "flex", flexWrap: "wrap", gap: 12, justifyContent: "space-between" }}>
        <div>
          <Typography.Title id="team-automations-title" level={3} style={{ margin: 0 }}>
            {copy("teams.automations.memberTitle", "{memberName} automations", {
              memberName: routeMember.name,
            })}
          </Typography.Title>
          <Typography.Paragraph type="secondary" style={{ margin: "6px 0 0" }}>
            {copy(
              "teams.automations.memberDescription",
              "Dedicated Agent Keys are authorized per schedule and held by Aevatar.",
            )}
          </Typography.Paragraph>
        </div>
        <Space wrap>
          <Button icon={<ReloadOutlined />} onClick={() => void automationsQuery.refetch()}>
            {copy("teams.automations.actions.refresh", "Refresh")}
          </Button>
          <Button icon={<PlusOutlined />} onClick={openCreate} type="primary">
            {copy("teams.automations.actions.create", "New automation")}
          </Button>
        </Space>
      </div>

      <div aria-live="polite" style={{ marginTop: 14 }}>
        {notice ? <Alert closable message={notice} onClose={() => setNotice("")} showIcon type="info" /> : null}
        {authorizationFlow.state === "pending" && pollingStartedAtRef.current &&
        Date.now() - pollingStartedAtRef.current >= pendingPollDurationMs ? (
          <Alert
            message={copy("teams.automations.pending.title", "Still pending")}
            description={copy(
              "teams.automations.pending.description",
              "Automatic refresh stopped. Use Refresh to check authoritative state.",
            )}
            showIcon
            type="warning"
          />
        ) : null}
      </div>

      <div style={{ marginTop: 16 }}>
        {automationsQuery.isLoading ? (
          <Spin aria-label={copy("teams.automations.loading", "Loading automations")} />
        ) : null}
        {automationsQuery.isError ? (
          <Alert
            action={(
              <Button onClick={() => void automationsQuery.refetch()}>
                {copy("teams.automations.actions.tryAgain", "Try again")}
              </Button>
            )}
            description={automationsQuery.error instanceof Error ? automationsQuery.error.message : String(automationsQuery.error)}
            message={copy(
              "teams.automations.error.stateLoad",
              "Automation state could not be loaded",
            )}
            showIcon
            type="error"
          />
        ) : null}
        {!automationsQuery.isLoading && !automationsQuery.isError && !automationsQuery.data?.items.length ? (
          <Empty description={copy("teams.automations.empty.member", "No automations for this member")} />
        ) : null}
        <div className="team-automations-list">
          {automationsQuery.data?.items.map(renderRow)}
        </div>
      </div>

      <Modal
        aria-describedby="team-automation-form-description"
        destroyOnHidden
        footer={review ? null : undefined}
        onCancel={() => {
          setFormOpen(false);
          setAuthorizationFlow({ state: "idle" });
        }}
        onOk={formMode === "edit" ? () => void updateAutomation() : reviewAuthorization}
        okButtonProps={{ loading: flowBusy || Boolean(busyScheduleId) }}
        okText={formMode === "edit"
          ? copy("teams.automations.form.save", "Save changes")
          : copy("teams.automations.authorization.review", "Review authorization")}
        open={formOpen}
        title={formMode === "edit"
          ? copy("teams.automations.form.editTitle", "Edit automation")
          : copy("teams.automations.actions.create", "New automation")}
        width={680}
      >
        <Typography.Paragraph id="team-automation-form-description" type="secondary">
          {copy(
            "teams.automations.form.description",
            "Configure recurring work for {memberName}. Authorization is reviewed separately before creation or replacement.",
            { memberName: routeMember.name },
          )}
        </Typography.Paragraph>
        {review ? (
          <TeamAutomationAuthorizationReview
            busy={flowBusy}
            onCancel={() => setAuthorizationFlow({ state: "idle" })}
            onConfirm={() => void submitAuthorization()}
            review={review}
          />
        ) : (
          <div style={{ display: "grid", gap: 14 }}>
            <label>
              <Typography.Text>
                {copy("teams.automations.form.displayNameAria", "Automation name")}
              </Typography.Text>
              <Input
                aria-label={copy("teams.automations.form.displayNameAria", "Automation name")}
                onChange={(event) => setDraft((current) => ({ ...current, displayName: event.target.value }))}
                value={draft.displayName}
              />
            </label>
            <label>
              <Typography.Text>
                {copy("teams.automations.form.promptAria", "Recurring prompt")}
              </Typography.Text>
              <Input.TextArea
                aria-label={copy("teams.automations.form.promptAria", "Recurring prompt")}
                maxLength={promptMaxLength}
                onChange={(event) => setDraft((current) => ({ ...current, prompt: event.target.value }))}
                rows={4}
                value={draft.prompt}
              />
            </label>
            <div style={{ display: "grid", gap: 12, gridTemplateColumns: "minmax(0, 1fr) minmax(160px, .55fr)" }}>
              <label>
                <Typography.Text>
                  {copy("teams.automations.form.cron", "Cron expression")}
                </Typography.Text>
                <Input
                  aria-label={copy("teams.automations.form.cronAria", "Cron expression")}
                  onChange={(event) => setDraft((current) => ({ ...current, cronExpression: event.target.value }))}
                  value={draft.cronExpression}
                />
              </label>
              <label>
                <Typography.Text>
                  {copy("teams.automations.form.timezone", "Timezone")}
                </Typography.Text>
                <Input
                  aria-label={copy("teams.automations.form.timezoneAria", "Timezone")}
                  onChange={(event) => setDraft((current) => ({ ...current, timezone: event.target.value }))}
                  value={draft.timezone}
                />
              </label>
            </div>
            <Space>
              <Switch
                aria-label={copy("teams.automations.form.firingEnabled", "Firing enabled")}
                checked={draft.enabled}
                onChange={(enabled) => setDraft((current) => ({ ...current, enabled }))}
              />
              <Typography.Text>
                {copy(
                  "teams.automations.form.enableAfterAuthorization",
                  "Enable firing after authorization",
                )}
              </Typography.Text>
            </Space>
            <div>
              <Button onClick={() => void preview()}>
                {copy("teams.automations.form.preview", "Preview schedule")}
              </Button>
              {previewText ? <Typography.Paragraph type="secondary">{previewText}</Typography.Paragraph> : null}
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
            {authorizationFlow.state === "error" ? (
              <Alert
                description={authorizationFlow.message}
                message={copy(
                  "teams.automations.authorization.error",
                  "Authorization could not continue",
                )}
                showIcon
                type="error"
              />
            ) : null}
          </div>
        )}
      </Modal>

      <Button
        icon={<PlayCircleOutlined />}
        onClick={() => history.push(buildTeamMemberPublishedRunsHref({ ...route }))}
        style={{ marginTop: 16 }}
        type="link"
      >
        {copy("teams.automations.actions.runHistory", "View member run history")}
      </Button>
    </section>
  );
};

export default TeamAutomationsTab;
