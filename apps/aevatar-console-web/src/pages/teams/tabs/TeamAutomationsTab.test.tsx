import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { message } from "antd";
import * as React from "react";
import TeamAutomationsTab, {
  mutationObservationComplete,
} from "./TeamAutomationsTab";
import {
  teamAutomationApi,
  TeamAutomationApiError,
} from "@/shared/api/teamAutomationApi";
import { scheduledDispatchApi } from "@/shared/api/scheduledDispatchApi";
import { NyxIDAuthClient } from "@/shared/auth/client";
import { history } from "@/shared/navigation/history";

jest.mock("@umijs/max", () => ({
  useIntl: () => ({
    formatMessage: (
      { defaultMessage, id }: { defaultMessage?: string; id: string },
      values?: Record<string, string | number>,
    ) =>
      Object.entries(values ?? {}).reduce(
        (message, [key, value]) => message.replaceAll(`{${key}}`, String(value)),
        defaultMessage ?? id,
      ),
  }),
}));

jest.mock("@/shared/api/teamAutomationApi", () => ({
  createTeamAutomationOperationIdentity: jest.fn(() => ({
    operationId: "op-alpha",
    idempotencyKey: "idem-alpha",
  })),
  teamAutomationApi: {
    create: jest.fn(),
    delete: jest.fn(),
    listAll: jest.fn(),
    pause: jest.fn(),
    preflightCreate: jest.fn(),
    reauthorize: jest.fn(),
    resume: jest.fn(),
    retryRevocation: jest.fn(),
    runNow: jest.fn(),
    update: jest.fn(),
  },
  TeamAutomationApiError: class TeamAutomationApiError extends Error {
    code?: string;
    status: number;

    constructor(message: string, status: number, code?: string) {
      super(message);
      this.code = code;
      this.status = status;
    }
  },
}));

jest.mock("@/shared/api/scheduledDispatchApi", () => ({
  previewScheduledDispatch: jest.fn(),
  scheduledDispatchApi: {
    listAll: jest.fn(async () => ({ items: [], nextCursor: null, totalCount: 0 })),
  },
  scheduledWorkflowPromptMaxLength: 4_000,
}));

jest.mock("@/shared/navigation/history", () => ({
  history: { push: jest.fn(), replace: jest.fn() },
}));

jest.mock("@/shared/auth/client", () => ({
  NyxIDAuthClient: jest.fn(() => ({ loginWithRedirect: jest.fn() })),
}));

jest.mock("@/shared/auth/config", () => ({
  getNyxIDRuntimeConfig: jest.fn(() => ({})),
}));

const member = {
  canAutomateMember: true,
  disabledReason: "",
  implementationKind: "Workflow",
  key: "m-alpha",
  lifecycleLabel: "Published",
  lifecycleStyle: {},
  memberId: "m-alpha",
  name: "Planner",
  serviceId: "svc-alpha",
  workflowSupported: true,
};

function renderTab(
  routeMemberId = "",
  members = [member],
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <TeamAutomationsTab
        members={members}
        routeMemberId={routeMemberId}
        scopeId="scope-alpha"
        teamId="team-alpha"
      />
    </QueryClientProvider>,
  );
}

function authorizationReview() {
  return {
    status: "ready",
    schemaVersion: "scheduled-invocation-authorization/v1",
    permissionDigest: "digest-alpha",
    policyVersion: "scheduled-invocation-auth/v1",
    serviceGrants: [
      {
        displayName: "Connector Alpha",
        grantId: "service:us-alpha",
        kind: "service",
        nodeGrantRequirement: "required",
        nodeIds: ["node-alpha"],
        serviceSlug: "connector-alpha",
        targetId: "us-alpha",
      },
    ],
    nodeGrants: [],
    credentialPlan: {
      allowAllNodes: false,
      allowAllServices: false,
      browserReceivesRawKey: false,
      expiresAt: "2026-10-14T00:00:00Z",
      scopes: ["read", "proxy"],
    },
    ownerLLMSelection: {
      model: "gpt-5",
      routeKind: "nyx_id_user_service",
      serviceSlugSnapshot: "connector-alpha",
    },
    disclosures: [
      "dedicated_credential",
      "aevatar_secret_custody",
      "browser_never_receives_secret",
      "delete_revokes_credential",
      "pause_resume_preserves_credential",
      "node_ids_are_permission_set",
    ],
  };
}

function automationView(overrides: Record<string, unknown> = {}) {
  return {
    scopeId: "scope-alpha",
    teamId: "team-alpha",
    memberId: "m-alpha",
    scheduleId: "sch-alpha",
    publishedServiceId: "svc-alpha",
    credentialSourceKind: "scheduled_invocation_agent_key",
    displayName: "Daily review",
    prompt: "Summarize open work.",
    cronExpression: "0 9 * * 1-5",
    timezone: "Asia/Singapore",
    enabled: true,
    authorizationStatus: "active",
    credentialExpiresAtUtc: "2026-10-14T00:00:00Z",
    lastAuthorizationErrorCode: "",
    operationId: "op-alpha",
    credentialGeneration: 1,
    revocationPending: false,
    nextFireAt: "2026-07-29T01:00:00Z",
    lastFireAt: null,
    nyxIdRevocationStatus: "NotRequired",
    vaultRevocationStatus: "NotRequired",
    ownerLLMRouteKind: "nyx_id_user_service",
    ownerLLMRoute: "us-alpha",
    ownerLLMUserServiceId: "us-alpha",
    ownerLLMServiceSlug: "connector-alpha",
    ownerLLMModel: "gpt-5",
    stateVersion: 4,
    updatedAt: "2026-07-28T00:00:00Z",
    ...overrides,
  };
}

describe("TeamAutomationsTab canonical member authority", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    Object.values(teamAutomationApi).forEach((apiMethod) => {
      (apiMethod as jest.Mock).mockReset();
    });
    window.sessionStorage.clear();
    (teamAutomationApi.listAll as jest.Mock).mockResolvedValue({
      items: [],
      nextCursor: null,
      totalCount: 0,
    });
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it("completes revocation retry observation when the scoped row disappears", () => {
    expect(
      mutationObservationComplete(
        {
          kind: "retryRevocation",
          scheduleId: "sch-alpha",
          baselineStateVersion: 4,
        },
        [],
      ),
    ).toBe(true);
  });

  it("keeps member selection inside the create form when multiple members are eligible", async () => {
    const otherMember = {
      ...member,
      key: "m-beta",
      memberId: "m-beta",
      name: "Reviewer",
      serviceId: "svc-beta",
    };

    renderTab("", [member, otherMember]);

    expect(await screen.findByRole("heading", { name: "Automations" })).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Automation member" })).not.toBeInTheDocument();
    expect(history.replace).not.toHaveBeenCalled();
    expect(teamAutomationApi.listAll).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "New automation" }));

    const form = await screen.findByRole("dialog");
    expect(within(form).getByRole("combobox", { name: "Automation member" })).toBeEnabled();
    expect(history.push).not.toHaveBeenCalled();
  });

  it("opens the complete dev form directly from the Team shell", async () => {
    renderTab();

    fireEvent.click(await screen.findByRole("button", { name: "New automation" }));

    expect(await screen.findByText("1. Target member")).toBeInTheDocument();
    expect(screen.getByText("2. Work to run")).toBeInTheDocument();
    expect(screen.getByText("3. Schedule")).toBeInTheDocument();
    expect(screen.getByLabelText("Automation member")).toBeEnabled();
    expect(screen.getByLabelText("Automation cadence")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview next runs" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create automation" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Review authorization" })).not.toBeInTheDocument();
    expect(history.push).not.toHaveBeenCalled();
    expect(teamAutomationApi.listAll).not.toHaveBeenCalled();
  });

  it("loads only the exact canonical member collection", async () => {
    renderTab("m-alpha");

    expect(await screen.findByRole("heading", { name: "Automations" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Give a member recurring work" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Upcoming" })).toBeInTheDocument();
    await waitFor(() =>
      expect(teamAutomationApi.listAll).toHaveBeenCalledWith(
        { scopeId: "scope-alpha", teamId: "team-alpha", memberId: "m-alpha" },
        { take: 200 },
      ),
    );
    expect(scheduledDispatchApi.listAll).not.toHaveBeenCalled();
  });

  it("derives Upcoming from the exact member read model", async () => {
    (teamAutomationApi.listAll as jest.Mock).mockResolvedValue({
      items: [automationView()],
      nextCursor: null,
      totalCount: 1,
    });

    renderTab("m-alpha");

    expect(await screen.findByText("Planner recurring work")).toBeInTheDocument();
    expect(teamAutomationApi.listAll).toHaveBeenCalledWith(
      { scopeId: "scope-alpha", teamId: "team-alpha", memberId: "m-alpha" },
      { take: 200 },
    );
  });

  it("does not query when the path member is outside the current Team", async () => {
    renderTab("m-other");

    expect(await screen.findByText("Member unavailable for automation")).toBeInTheDocument();
    expect(teamAutomationApi.listAll).not.toHaveBeenCalled();
    expect(scheduledDispatchApi.listAll).not.toHaveBeenCalled();
    expect(history.push).not.toHaveBeenCalled();
    expect(history.replace).not.toHaveBeenCalled();
  });

  it("starts scoped preflight directly and requires explicit review before create", async () => {
    (teamAutomationApi.preflightCreate as jest.Mock).mockResolvedValue(
      authorizationReview(),
    );
    (teamAutomationApi.create as jest.Mock).mockResolvedValue({
      accepted: true,
      status: "accepted",
      scheduleId: "sch-alpha",
      operationId: "op-alpha",
      commandId: "cmd-alpha",
    });
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Automation name"), {
      target: { value: "Daily review" },
    });
    fireEvent.change(screen.getByLabelText("Recurring prompt"), {
      target: { value: "Summarize open work." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create automation" }));

    await waitFor(() => expect(teamAutomationApi.preflightCreate).toHaveBeenCalledTimes(1));
    expect(teamAutomationApi.create).not.toHaveBeenCalled();
    expect(await screen.findByText("Dedicated Agent Key")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Authorize and continue" }));

    await waitFor(() => expect(teamAutomationApi.create).toHaveBeenCalledTimes(1));
    expect(await screen.findByText("Authorization request accepted")).toBeInTheDocument();
    expect(screen.queryByText("Automation created")).not.toBeInTheDocument();
  });

  it("reports authorization preflight failures through the shared toast without changing the modal layout", async () => {
    const rawCatalogFailure = JSON.stringify({
      ready: false,
      refreshStatus: "catalog_unstable",
      refreshFailureCode: "api_key_scope_plan_route_unresolved",
      visibilityStatus: "not_evaluated",
    });
    const messageError = jest
      .spyOn(message, "error")
      .mockImplementation(() => undefined as never);
    (teamAutomationApi.preflightCreate as jest.Mock).mockRejectedValue(
      new Error(rawCatalogFailure),
    );
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Recurring prompt"), {
      target: { value: "Summarize open work." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create automation" }));

    await waitFor(() =>
      expect(messageError).toHaveBeenCalledWith("Authorization could not continue"),
    );
    expect(screen.queryByText(rawCatalogFailure)).not.toBeInTheDocument();
    const dialog = screen.getByRole("dialog");
    expect(
      within(dialog).queryByText("Authorization could not continue"),
    ).not.toBeInTheDocument();
  });

  it("clears accepted create observation after the authoritative row becomes terminal", async () => {
    (teamAutomationApi.listAll as jest.Mock)
      .mockResolvedValueOnce({ items: [], nextCursor: null, totalCount: 0 })
      .mockResolvedValueOnce({
        items: [
          automationView({
            authorizationStatus: "provisioning_pending",
            stateVersion: 1,
          }),
        ],
        nextCursor: null,
        totalCount: 1,
      })
      .mockResolvedValue({
        items: [automationView({ stateVersion: 2 })],
        nextCursor: null,
        totalCount: 1,
      });
    (teamAutomationApi.preflightCreate as jest.Mock).mockResolvedValue(
      authorizationReview(),
    );
    (teamAutomationApi.create as jest.Mock).mockResolvedValue({
      accepted: true,
      status: "accepted",
      scheduleId: "sch-alpha",
      operationId: "op-alpha",
      commandId: "cmd-alpha",
    });
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Recurring prompt"), {
      target: { value: "Summarize open work." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create automation" }));
    fireEvent.click(await screen.findByRole("button", { name: "Authorize and continue" }));

    expect(await screen.findByText("Preparing authorization")).toBeInTheDocument();
    expect(
      await screen.findByRole("status", { name: "Active" }, { timeout: 3_500 }),
    ).toBeInTheDocument();
    expect(teamAutomationApi.listAll).toHaveBeenCalledTimes(3);
    expect(screen.queryByText("Still pending")).not.toBeInTheDocument();
  });

  it("recovers a confirmed create when the fresh binding is missing", async () => {
    (teamAutomationApi.preflightCreate as jest.Mock).mockResolvedValue(
      authorizationReview(),
    );
    (teamAutomationApi.create as jest.Mock).mockRejectedValue(
      new TeamAutomationApiError(
        "Reconnect NyxID to authorize this automation.",
        409,
        "TEAM_AUTOMATION_AUTHORIZATION_BINDING_REQUIRED",
      ),
    );
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Automation name"), {
      target: { value: "Daily review" },
    });
    fireEvent.change(screen.getByLabelText("Recurring prompt"), {
      target: { value: "Summarize open work." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create automation" }));
    fireEvent.click(await screen.findByRole("button", { name: "Authorize and continue" }));

    await waitFor(() => expect(NyxIDAuthClient).toHaveBeenCalledTimes(1));
    const loginWithRedirect = (NyxIDAuthClient as jest.Mock).mock.results[0].value
      .loginWithRedirect as jest.Mock;
    expect(loginWithRedirect).toHaveBeenCalledWith({
      returnTo:
        "/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations",
      prompt: "consent",
    });

    const stored = JSON.parse(
      String(window.sessionStorage.getItem(window.sessionStorage.key(0) ?? "")),
    ) as Record<string, unknown>;
    expect(stored).toEqual(
      expect.objectContaining({
        mode: "create",
        scopeId: "scope-alpha",
        teamId: "team-alpha",
        memberId: "m-alpha",
      }),
    );
    expect(stored).not.toHaveProperty("permissionDigest");
    expect(stored).not.toHaveProperty("operationId");
    expect(stored).not.toHaveProperty("idempotencyKey");
  });

  it("recovers update as a fresh reauthorization draft", async () => {
    (teamAutomationApi.listAll as jest.Mock).mockResolvedValue({
      items: [automationView()],
      nextCursor: null,
      totalCount: 1,
    });
    (teamAutomationApi.update as jest.Mock).mockRejectedValue(
      new TeamAutomationApiError(
        "Reconnect NyxID to authorize this automation.",
        409,
        "TEAM_AUTOMATION_AUTHORIZATION_BINDING_REQUIRED",
      ),
    );
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(NyxIDAuthClient).toHaveBeenCalledTimes(1));
    const loginWithRedirect = (NyxIDAuthClient as jest.Mock).mock.results[0].value
      .loginWithRedirect as jest.Mock;
    expect(loginWithRedirect).toHaveBeenCalled();
    const stored = JSON.parse(
      String(window.sessionStorage.getItem(window.sessionStorage.key(0) ?? "")),
    ) as Record<string, unknown>;
    expect(stored).toEqual(
      expect.objectContaining({
        mode: "reauthorize",
        scheduleId: "sch-alpha",
      }),
    );
  });

  it("allows run now while an active automation is paused", async () => {
    (teamAutomationApi.listAll as jest.Mock).mockResolvedValue({
      items: [automationView({ enabled: false })],
      nextCursor: null,
      totalCount: 1,
    });
    (teamAutomationApi.runNow as jest.Mock).mockResolvedValue({
      accepted: true,
      status: "accepted",
      scheduleId: "sch-alpha",
      operationId: "op-run",
      commandId: "cmd-run",
    });
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "Run now" }));

    await waitFor(() =>
      expect(teamAutomationApi.runNow).toHaveBeenCalledWith(
        { scopeId: "scope-alpha", teamId: "team-alpha", memberId: "m-alpha" },
        "sch-alpha",
        { operationId: "op-alpha", idempotencyKey: "idem-alpha" },
      ),
    );
    expect(await screen.findByText("Run request accepted")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Resume" })).toBeInTheDocument();
  });

  it("keeps observing a pause after the first post-202 read is still stale", async () => {
    const active = automationView({ enabled: true, stateVersion: 4 });
    const paused = automationView({ enabled: false, stateVersion: 5 });
    (teamAutomationApi.listAll as jest.Mock)
      .mockResolvedValueOnce({
        items: [active],
        nextCursor: null,
        totalCount: 1,
      })
      .mockResolvedValueOnce({
        items: [active],
        nextCursor: null,
        totalCount: 1,
      })
      .mockResolvedValue({
        items: [paused],
        nextCursor: null,
        totalCount: 1,
      });
    (teamAutomationApi.pause as jest.Mock).mockResolvedValue({
      accepted: true,
      status: "accepted",
      scheduleId: "sch-alpha",
      operationId: "op-pause",
      commandId: "cmd-pause",
    });
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "Pause" }));

    expect(await screen.findByText("Pause request accepted")).toBeInTheDocument();
    await waitFor(
      () => expect(screen.getByRole("button", { name: "Resume" })).toBeInTheDocument(),
      { timeout: 3_500 },
    );
    expect(teamAutomationApi.listAll).toHaveBeenCalledTimes(3);
  });

  it("keeps observing delete until the authoritative row disappears", async () => {
    const active = automationView({ stateVersion: 4 });
    (teamAutomationApi.listAll as jest.Mock)
      .mockResolvedValueOnce({
        items: [active],
        nextCursor: null,
        totalCount: 1,
      })
      .mockResolvedValueOnce({
        items: [active],
        nextCursor: null,
        totalCount: 1,
      })
      .mockResolvedValue({
        items: [],
        nextCursor: null,
        totalCount: 0,
      });
    (teamAutomationApi.delete as jest.Mock).mockResolvedValue({
      accepted: true,
      status: "accepted",
      scheduleId: "sch-alpha",
      operationId: "op-delete",
      commandId: "cmd-delete",
    });
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "Delete" }));
    const confirmation = await screen.findByRole("dialog");
    fireEvent.click(
      within(confirmation).getByRole("button", { name: "Delete" }),
    );

    expect(await screen.findByText("Delete request accepted")).toBeInTheDocument();
    await waitFor(
      () =>
        expect(screen.getByText("No automations for this member")).toBeInTheDocument(),
      { timeout: 3_500 },
    );
    expect(teamAutomationApi.listAll).toHaveBeenCalledTimes(3);
  });

  it("offers reauthorization for a projected authorization failure", async () => {
    (teamAutomationApi.listAll as jest.Mock).mockResolvedValue({
      items: [automationView({ authorizationStatus: "needs_authorization" })],
      nextCursor: null,
      totalCount: 1,
    });
    (teamAutomationApi.preflightCreate as jest.Mock).mockResolvedValue(
      authorizationReview(),
    );
    renderTab("m-alpha");

    fireEvent.click(
      await screen.findByRole("button", { name: "Review and reauthorize" }),
    );

    await waitFor(() => expect(teamAutomationApi.preflightCreate).toHaveBeenCalledTimes(1));
    expect(await screen.findByText("Dedicated Agent Key")).toBeInTheDocument();
  });

  it("retries actor-owned revocation without a browser operation ledger", async () => {
    const revoking = automationView({
      authorizationStatus: "revocation_pending",
      revocationPending: true,
      nyxIdRevocationStatus: "Completed",
      vaultRevocationStatus: "Pending",
    });
    (teamAutomationApi.listAll as jest.Mock)
      .mockResolvedValueOnce({
        items: [revoking],
        nextCursor: null,
        totalCount: 1,
      })
      .mockResolvedValueOnce({
        items: [revoking],
        nextCursor: null,
        totalCount: 1,
      })
      .mockResolvedValue({ items: [], nextCursor: null, totalCount: 0 });
    (teamAutomationApi.retryRevocation as jest.Mock).mockResolvedValue({
      accepted: true,
      status: "accepted",
      scheduleId: "sch-alpha",
      operationId: "op-alpha",
      commandId: "cmd-retry",
    });
    renderTab("m-alpha");

    expect(await screen.findByText("NyxID: Completed")).toBeInTheDocument();
    expect(screen.getByText("Vault: Pending")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry revocation" }));

    await waitFor(() =>
      expect(teamAutomationApi.retryRevocation).toHaveBeenCalledWith(
        { scopeId: "scope-alpha", teamId: "team-alpha", memberId: "m-alpha" },
        "sch-alpha",
      ),
    );
    expect(
      await screen.findByText("No automations for this member", {}, { timeout: 3_500 }),
    ).toBeInTheDocument();
    expect(teamAutomationApi.listAll).toHaveBeenCalledTimes(3);
    expect(screen.queryByText("Still pending")).not.toBeInTheDocument();
  });

  it("reconnects NyxID for revocation retry without persisting an action ledger", async () => {
    (teamAutomationApi.listAll as jest.Mock).mockResolvedValue({
      items: [
        automationView({
          authorizationStatus: "revocation_pending",
          revocationPending: true,
          nyxIdRevocationStatus: "Completed",
          vaultRevocationStatus: "Pending",
        }),
      ],
      nextCursor: null,
      totalCount: 1,
    });
    (teamAutomationApi.retryRevocation as jest.Mock).mockRejectedValue(
      new TeamAutomationApiError(
        "Reconnect NyxID to authorize this automation.",
        409,
        "TEAM_AUTOMATION_AUTHORIZATION_BINDING_REQUIRED",
      ),
    );
    renderTab("m-alpha");

    fireEvent.click(await screen.findByRole("button", { name: "Retry revocation" }));

    await waitFor(() => expect(NyxIDAuthClient).toHaveBeenCalledTimes(1));
    const loginWithRedirect = (NyxIDAuthClient as jest.Mock).mock.results[0].value
      .loginWithRedirect as jest.Mock;
    expect(loginWithRedirect).toHaveBeenCalledWith({
      returnTo:
        "/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations",
      prompt: "consent",
    });
    expect(window.sessionStorage.length).toBe(0);
  });

  it("keeps delete unavailable while replacement or revocation is pending", async () => {
    (teamAutomationApi.listAll as jest.Mock).mockResolvedValue({
      items: [
        automationView({
          scheduleId: "sch-replacement",
          authorizationStatus: "replacement_pending",
        }),
        automationView({
          scheduleId: "sch-revocation",
          authorizationStatus: "failed",
          revocationPending: true,
          nyxIdRevocationStatus: "Failed",
          vaultRevocationStatus: "Pending",
        }),
      ],
      nextCursor: null,
      totalCount: 2,
    });
    renderTab("m-alpha");

    expect(await screen.findByText("Replacing authorization")).toBeInTheDocument();
    expect(screen.getByText("Revocation needs attention")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Delete" })).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Retry revocation" }),
    ).toBeInTheDocument();
  });
});
