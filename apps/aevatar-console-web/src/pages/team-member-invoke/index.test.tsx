import { QueryClient } from "@tanstack/react-query";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { history } from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import TeamMemberInvokePage from "./index";

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getMember: jest.fn(),
    getMemberBinding: jest.fn(),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    getMemberEndpointContract: jest.fn(),
    getServiceRevisions: jest.fn(),
  },
}));

jest.mock("../studio/components/StudioMemberInvokePanel", () => ({
  __esModule: true,
  default: (props: {
    authoritativeEndpointContract?: {
      endpointId: string;
      memberId?: string;
      publishedServiceId?: string;
      scopeId: string;
    } | null;
    enableFileAttachments?: boolean;
    initialServiceId?: string;
    memberId?: string;
    presentation?: string;
    runtimeTarget?: string;
    scopeId?: string;
    selectedMemberLabel?: string;
    services?: Array<{ serviceId: string }>;
    targetSummaryVariant?: string;
    teamId?: string;
  }) => {
    const React = require("react");
    return React.createElement("div", { "data-testid": "member-invoke-panel" }, [
      React.createElement("span", { key: "scope" }, `scope:${props.scopeId}`),
      React.createElement("span", { key: "member" }, `member:${props.memberId}`),
      React.createElement("span", { key: "target" }, `target:${props.runtimeTarget ?? "default"}`),
      React.createElement("span", { key: "attachments" }, `attachments:${props.enableFileAttachments ? "on" : "off"}`),
      React.createElement("span", { key: "team" }, `team:${props.teamId}`),
      React.createElement("span", { key: "service" }, `service:${props.initialServiceId}`),
      React.createElement("span", { key: "label" }, `label:${props.selectedMemberLabel}`),
      React.createElement("span", { key: "presentation" }, `presentation:${props.presentation}`),
      React.createElement("span", { key: "variant" }, `variant:${props.targetSummaryVariant}`),
      React.createElement(
        "span",
        { key: "contract" },
        `contract:${props.authoritativeEndpointContract?.scopeId}/${props.authoritativeEndpointContract?.memberId}/${props.authoritativeEndpointContract?.publishedServiceId}/${props.authoritativeEndpointContract?.endpointId}`,
      ),
      React.createElement(
        "span",
        { key: "services" },
        `services:${props.services?.map((service) => service.serviceId).join(",")}`,
      ),
    ]);
  },
}));

function createWorkflowMember(
  summaryOverrides?: Record<string, unknown>,
  detailOverrides?: Record<string, unknown>,
) {
  return {
    summary: {
      memberId: "member-alpha",
      scopeId: "scope-1",
      teamId: "team-1",
      displayName: "Alpha Workflow",
      description: "Runs alpha flow",
      implementationKind: "workflow",
      lifecycleStage: "bind_ready",
      publishedServiceId: "svc-alpha",
      lastBoundRevisionId: "rev-alpha",
      createdAt: "2026-06-01T00:00:00Z",
      updatedAt: "2026-06-01T00:00:00Z",
      ...(summaryOverrides ?? {}),
    },
    implementationRef: null,
    lastBinding: null,
    currentBindingRun: null,
    ...(detailOverrides ?? {}),
  };
}

function createBinding(overrides?: Record<string, unknown>) {
  return {
    currentBindingRun: null,
    lastBinding: {
      boundAt: "2026-06-01T00:10:00Z",
      implementationKind: "workflow",
      publishedServiceId: "svc-alpha",
      revisionId: "rev-alpha",
      ...(overrides ?? {}),
    },
  };
}

function createEndpointContract(overrides?: Record<string, unknown>) {
  return {
    deploymentStatus: "Active",
    endpointId: "chat",
    fetchExample: null,
    invokePath: "/api/scopes/scope-1/members/member-alpha/invoke/chat:stream",
    memberId: "member-alpha",
    method: "POST",
    publishedServiceId: "svc-alpha",
    requestContentType: "application/json",
    requestTypeUrl: "type.googleapis.com/aevatar.ai.ChatRequestEvent",
    responseContentType: "text/event-stream",
    responseTypeUrl: "type.googleapis.com/aevatar.ai.ChatResponseEvent",
    revisionId: "rev-alpha",
    invocationReadiness: {
      canInvoke: true,
      deploymentId: "dep-alpha",
      message: "Member endpoint is ready for invocation.",
      observedAtUtc: "2026-08-17T04:57:08Z",
      reasonCode: "ready",
      revisionId: "rev-alpha",
      status: "ready",
    },
    publishedServiceStateVersion: 2,
    boundRevisionStateVersion: 3,
    sampleRequestJson: null,
    scopeId: "scope-1",
    serviceId: "",
    smokeTestSupported: true,
    streamFrameFormat: "workflow-run-event",
    supportsAguiFrames: false,
    supportsSse: true,
    supportsWebSocket: false,
    curlExample: null,
    defaultSmokeInputMode: "prompt",
    defaultSmokePrompt: "Hello",
    ...(overrides ?? {}),
  };
}

function createRevisionCatalog() {
  return {
    activeServingRevisionId: "rev-alpha",
    catalogLastEventId: "evt-alpha",
    catalogStateVersion: 1,
    defaultServingRevisionId: "rev-alpha",
    deploymentId: "dep-alpha",
    deploymentStatus: "Active",
    displayName: "Alpha Workflow",
    primaryActorId: "actor-alpha",
    revisions: [
      {
        allocationWeight: 100,
        artifactHash: "hash-alpha",
        createdAt: "2026-06-01T00:00:00Z",
        deploymentId: "dep-alpha",
        failureReason: "",
        implementationKind: "workflow",
        inlineWorkflowCount: 1,
        isActiveServing: true,
        isDefaultServing: true,
        isServingTarget: true,
        preparedAt: "2026-06-01T00:05:00Z",
        primaryActorId: "actor-alpha",
        publishedAt: "2026-06-01T00:10:00Z",
        retiredAt: null,
        revisionId: "rev-alpha",
        scriptDefinitionActorId: "",
        scriptId: "",
        scriptRevision: "",
        scriptSourceHash: "",
        servingState: "Active",
        staticActorTypeName: "",
        status: "Published",
        workflowDefinitionActorId: "definition://alpha",
        workflowName: "Alpha Workflow",
      },
    ],
    scopeId: "scope-1",
    serviceId: "svc-alpha",
    serviceKey: "scope-1:svc-alpha",
    updatedAt: "2026-06-01T00:10:00Z",
  };
}

describe("TeamMemberInvokePage", () => {
  beforeEach(() => {
    cleanupTestQueryClients();
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/team-1/members/member-alpha/invoke",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue(createWorkflowMember());
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValue(createBinding());
    (scopeRuntimeApi.getMemberEndpointContract as jest.Mock).mockResolvedValue(
      createEndpointContract(),
    );
    (scopeRuntimeApi.getServiceRevisions as jest.Mock).mockResolvedValue(
      createRevisionCatalog(),
    );
  });

  it("renders the invoke workbench with Studio team-context routing", async () => {
    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByText("Run workflow member")).toBeTruthy();
    expect(await screen.findByTestId("member-invoke-panel")).toHaveTextContent(
      "scope:scope-1",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "member:member-alpha",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "target:member",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "attachments:on",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "team:team-1",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "service:svc-alpha",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "services:svc-alpha",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "contract:scope-1/member-alpha/svc-alpha/chat",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "presentation:member-run",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "variant:undefined",
    );
    expect(screen.queryByRole("button", { name: "Team members" })).toBeNull();
    const publishedRunsButton = screen.getByRole("button", {
      name: "Published runs",
    });
    expect(publishedRunsButton).toHaveAttribute(
      "title",
      "View runs from the published member service.",
    );
    fireEvent.click(publishedRunsButton);
    expect(window.location.pathname).toBe(
      "/scopes/scope-1/teams/team-1/members/member-alpha/runs",
    );
    expect(window.location.search).toBe("");
    expect(scopeRuntimeApi.getMemberEndpointContract).toHaveBeenCalledWith(
      "scope-1",
      "member-alpha",
      "chat",
    );
  });

  it("carries the member read-model draft workflow id into Workflow Studio", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/invoke",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValueOnce(
      createWorkflowMember(
        {
          memberId: "m-alpha",
          publishedServiceId: "svc-alpha",
          teamId: "t-alpha",
        },
        {
          implementationRef: {
            implementationKind: "workflow",
            workflowId: "wf-alpha",
          },
        },
      ),
    );
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce(createEndpointContract({ memberId: "m-alpha" }));

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByTestId("member-invoke-panel")).toHaveTextContent(
      "member:m-alpha",
    );
    fireEvent.click(screen.getByRole("button", { name: "Workflow Studio" }));

    expect(window.location.pathname).toBe(
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow",
    );
    const params = new URLSearchParams(window.location.search);
    expect(params.get("workflowId")).toBe("wf-alpha");
    expect(params.get("workflowId")).not.toBe("m-alpha");
    expect(params.get("workflowId")).not.toBe("svc-alpha");
    expect(params.get("serviceId")).toBeNull();
  });

  it("uses the member endpoint contract as an explicit published service identity source", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/invoke",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValueOnce(
      createWorkflowMember(
        {
          memberId: "m-alpha",
          publishedServiceId: "",
          teamId: "t-alpha",
        },
        {
          implementationRef: {
            implementationKind: "workflow",
            workflowId: "wf-alpha",
          },
        },
      ),
    );
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValueOnce(
      createBinding({ publishedServiceId: "" }),
    );
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce(
      createEndpointContract({ memberId: "m-alpha", publishedServiceId: "svc-alpha" }),
    );

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByTestId("member-invoke-panel")).toHaveTextContent(
      "member:m-alpha",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "service:svc-alpha",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "services:svc-alpha",
    );
    expect(screen.queryByText("Published service is not visible yet.")).toBeNull();
    expect(scopeRuntimeApi.getMemberEndpointContract).toHaveBeenCalledWith(
      "scope-1",
      "m-alpha",
      "chat",
    );
    expect(scopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
      "scope-1",
      "svc-alpha",
    );
  });

  it("refetches the member endpoint contract instead of reusing a fresh cached identity", async () => {
    const currentServiceId = "svc-current";
    let resolveEndpointContract: ((value: unknown) => void) | undefined;
    const endpointContractRequest = new Promise((resolve) => {
      resolveEndpointContract = resolve;
    });
    (studioApi.getMember as jest.Mock).mockResolvedValueOnce(
      createWorkflowMember({ publishedServiceId: currentServiceId }),
    );
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValueOnce(
      createBinding({ publishedServiceId: currentServiceId }),
    );
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockReturnValueOnce(endpointContractRequest);
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          gcTime: Infinity,
          retry: false,
          staleTime: 30_000,
        },
      },
    });
    queryClient.setQueryData(
      [
        "team-member-invoke",
        "endpoint-contract",
        "scope-1",
        "member-alpha",
        "chat",
      ],
      createEndpointContract({ publishedServiceId: "svc-stale" }),
    );

    renderWithQueryClient(
      React.createElement(TeamMemberInvokePage),
      queryClient,
    );

    await waitFor(() => {
      expect(scopeRuntimeApi.getMemberEndpointContract).toHaveBeenCalledWith(
        "scope-1",
        "member-alpha",
        "chat",
      );
    });
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
    expect(screen.getByText("Loading invoke context...")).toBeTruthy();

    resolveEndpointContract?.(
      createEndpointContract({ publishedServiceId: currentServiceId }),
    );

    expect(await screen.findByTestId("member-invoke-panel")).toHaveTextContent(
      `service:${currentServiceId}`,
    );
  });

  it("fails closed when the endpoint contract omits its published service identity", async () => {
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce(
      createEndpointContract({ publishedServiceId: "" }),
    );

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(
      await screen.findByText("No callable endpoint is available."),
    ).toBeTruthy();
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
    expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
  });

  it("shows the committed readiness reason instead of enabling an unavailable endpoint", async () => {
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce(
      createEndpointContract({
        invocationReadiness: {
          canInvoke: false,
          deploymentId: "dep-alpha",
          message: "Invocation catalog has not observed the serving revision.",
          observedAtUtc: "2026-08-17T04:57:08Z",
          reasonCode: "invocation_catalog_not_ready",
          revisionId: "rev-alpha",
          status: "invocation_catalog_not_ready",
        },
      }),
    );

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByText("Member endpoint is not ready.")).toBeTruthy();
    expect(
      screen.getAllByText(
        "Invocation catalog has not observed the serving revision.",
      ),
    ).toHaveLength(2);
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
    expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Refresh status" }));

    expect(await screen.findByTestId("member-invoke-panel")).toHaveTextContent(
      "service:svc-alpha",
    );
    expect(scopeRuntimeApi.getMemberEndpointContract).toHaveBeenCalledTimes(2);
  });

  it("does not treat a non-stream endpoint contract as the chat stream", async () => {
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce(createEndpointContract({ supportsSse: false }));

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(
      await screen.findByText("No callable endpoint is available."),
    ).toBeTruthy();
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
    expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
  });

  it("waits for committed service and revision source versions", async () => {
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce(
      createEndpointContract({ boundRevisionStateVersion: 0 }),
    );

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByText("Member endpoint is not ready.")).toBeTruthy();
    expect(
      screen.getAllByText(
        "Committed service and revision source versions are not visible yet.",
      ),
    ).toHaveLength(2);
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
    expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
  });

  it("does not read removed legacy Team member invoke links", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/team-1/members/member-alpha/invoke",
    );

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByText("Missing member route")).toBeTruthy();
    expect(studioApi.getMember).not.toHaveBeenCalled();
  });

  it("updates the invoke target when navigating between member invoke routes", async () => {
    (studioApi.getMember as jest.Mock).mockImplementation(
      (_scopeId: string, memberId: string) =>
        Promise.resolve(
          createWorkflowMember({
            displayName:
              memberId === "member-beta" ? "Beta Workflow" : "Alpha Workflow",
            lastBoundRevisionId:
              memberId === "member-beta" ? "rev-beta" : "rev-alpha",
            memberId,
            publishedServiceId:
              memberId === "member-beta" ? "svc-beta" : "svc-alpha",
          }),
        ),
    );
    (studioApi.getMemberBinding as jest.Mock).mockImplementation(
      (_scopeId: string, memberId: string) =>
        Promise.resolve(
          createBinding({
            publishedServiceId:
              memberId === "member-beta" ? "svc-beta" : "svc-alpha",
            revisionId: memberId === "member-beta" ? "rev-beta" : "rev-alpha",
          }),
        ),
    );
    (scopeRuntimeApi.getMemberEndpointContract as jest.Mock).mockImplementation(
      (_scopeId: string, memberId: string) =>
        Promise.resolve(
          createEndpointContract({
            invokePath: `/api/scopes/scope-1/members/${memberId}/invoke/chat:stream`,
            memberId,
            publishedServiceId:
              memberId === "member-beta" ? "svc-beta" : "svc-alpha",
          }),
        ),
    );

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByTestId("member-invoke-panel")).toHaveTextContent(
      "member:member-alpha",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "service:svc-alpha",
    );

    history.push("/scopes/scope-1/teams/team-1/members/member-beta/invoke");

    await waitFor(() => {
      expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
        "member:member-beta",
      );
    });
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "service:svc-beta",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "label:Beta Workflow",
    );
    expect(studioApi.getMember).toHaveBeenCalledWith("scope-1", "member-beta");
  });

  it("blocks non-workflow members", async () => {
    (studioApi.getMember as jest.Mock).mockResolvedValueOnce(
      createWorkflowMember({
        implementationKind: "script",
        publishedServiceId: "svc-alpha",
      }),
    );
    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(
      await screen.findByText("Invoke is available for workflow members only."),
    ).toBeTruthy();
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
  });

  it("blocks workflow members before binding", async () => {
    (studioApi.getMember as jest.Mock).mockResolvedValueOnce(
      createWorkflowMember({
        lifecycleStage: "build_ready",
        publishedServiceId: "",
      }),
    );
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValueOnce({
      currentBindingRun: null,
      lastBinding: null,
    });

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(
      await screen.findByText("This workflow member is not bound yet."),
    ).toBeTruthy();
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
    const publishedRunsButton = screen.getByRole("button", {
      name: "Published runs",
    });
    expect(publishedRunsButton).toBeDisabled();
    expect(publishedRunsButton).toHaveAttribute(
      "title",
      "Publish this member to start recording published runs.",
    );
    const workflowStudioButtons = screen.getAllByRole("button", {
      name: "Workflow Studio",
    });
    expect(workflowStudioButtons).toHaveLength(2);

    fireEvent.click(workflowStudioButtons[1]);

    expect(window.location.pathname).toBe(
      "/scopes/scope-1/teams/team-1/members/member-alpha/workflow",
    );
  });

  it("does not treat a member service identity as a completed binding", async () => {
    (studioApi.getMember as jest.Mock).mockResolvedValueOnce(
      createWorkflowMember({
        lastBoundRevisionId: null,
        lifecycleStage: "bind_ready",
        publishedServiceId: "svc-alpha",
      }),
    );
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValueOnce({
      currentBindingRun: null,
      lastBinding: null,
    });
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          gcTime: Infinity,
          retry: false,
          staleTime: 30_000,
        },
      },
    });
    queryClient.setQueryData(
      [
        "team-member-invoke",
        "endpoint-contract",
        "scope-1",
        "member-alpha",
        "chat",
      ],
      createEndpointContract(),
    );

    renderWithQueryClient(
      React.createElement(TeamMemberInvokePage),
      queryClient,
    );

    expect(
      await screen.findByText("This workflow member is not bound yet."),
    ).toBeTruthy();
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
    expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.getMemberEndpointContract).not.toHaveBeenCalled();
  });

  it("fails closed when the member endpoint contract resolves to another published service", async () => {
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce(
      createEndpointContract({ publishedServiceId: "svc-other" }),
    );

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(
      await screen.findByText("No callable endpoint is available."),
    ).toBeTruthy();
    expect(screen.queryByTestId("member-invoke-panel")).toBeNull();
    expect(scopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
  });
});
