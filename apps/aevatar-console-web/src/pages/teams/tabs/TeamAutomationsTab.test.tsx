import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import * as React from "react";
import TeamAutomationsTab from "./TeamAutomationsTab";
import { teamAutomationApi } from "@/shared/api/teamAutomationApi";
import { scheduledDispatchApi } from "@/shared/api/scheduledDispatchApi";
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
    refreshAuthorizationCatalog: jest.fn(),
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
  history: { push: jest.fn() },
}));

jest.mock("@/shared/auth/client", () => ({
  NyxIDAuthClient: jest.fn(() => ({ loginWithRedirect: jest.fn() })),
}));

jest.mock("@/shared/auth/config", () => ({
  getNyxIDRuntimeConfig: jest.fn(() => ({})),
}));

const member = {
  automationsHref: "/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations",
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

function renderTab(routeMemberId = "") {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <TeamAutomationsTab
        members={[member]}
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
    (teamAutomationApi.listAll as jest.Mock).mockResolvedValue({
      items: [],
      nextCursor: null,
      totalCount: 0,
    });
  });

  it("makes zero automation requests without a path member", async () => {
    renderTab();

    expect(await screen.findByText("Choose a team member")).toBeInTheDocument();
    expect(teamAutomationApi.listAll).not.toHaveBeenCalled();
    expect(scheduledDispatchApi.listAll).not.toHaveBeenCalled();
  });

  it("navigates the Team shell to the canonical member resource", async () => {
    renderTab();

    fireEvent.click(await screen.findByRole("button", { name: "Planner" }));

    expect(history.push).toHaveBeenCalledWith(
      "/scopes/scope-alpha/teams/team-alpha/members/m-alpha/automations",
    );
    expect(teamAutomationApi.listAll).not.toHaveBeenCalled();
  });

  it("loads only the exact canonical member collection", async () => {
    renderTab("m-alpha");

    await waitFor(() =>
      expect(teamAutomationApi.listAll).toHaveBeenCalledWith(
        { scopeId: "scope-alpha", teamId: "team-alpha", memberId: "m-alpha" },
        { take: 200 },
      ),
    );
    expect(scheduledDispatchApi.listAll).not.toHaveBeenCalled();
  });

  it("does not query when the path member is outside the current Team", async () => {
    renderTab("m-other");

    expect(await screen.findByText("Member unavailable for automation")).toBeInTheDocument();
    expect(teamAutomationApi.listAll).not.toHaveBeenCalled();
    expect(scheduledDispatchApi.listAll).not.toHaveBeenCalled();
  });

  it("requires preflight and explicit review before create", async () => {
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
    fireEvent.click(screen.getByRole("button", { name: "Review authorization" }));

    await waitFor(() => expect(teamAutomationApi.preflightCreate).toHaveBeenCalledTimes(1));
    expect(teamAutomationApi.create).not.toHaveBeenCalled();
    expect(await screen.findByText("Dedicated Agent Key")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Authorize and continue" }));

    await waitFor(() => expect(teamAutomationApi.create).toHaveBeenCalledTimes(1));
    expect(await screen.findByText("Authorization request accepted")).toBeInTheDocument();
    expect(screen.queryByText("Automation created")).not.toBeInTheDocument();
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

  it("retries actor-owned revocation after refresh without a browser operation ledger", async () => {
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
  });
});
