import { screen, waitFor } from "@testing-library/react";
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
    getServiceRevisions: jest.fn(),
    listServices: jest.fn(),
  },
}));

jest.mock("../studio/components/StudioMemberInvokePanel", () => ({
  __esModule: true,
  default: (props: {
    initialServiceId?: string;
    memberId?: string;
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
      React.createElement("span", { key: "team" }, `team:${props.teamId}`),
      React.createElement("span", { key: "service" }, `service:${props.initialServiceId}`),
      React.createElement("span", { key: "label" }, `label:${props.selectedMemberLabel}`),
      React.createElement("span", { key: "variant" }, `variant:${props.targetSummaryVariant}`),
      React.createElement(
        "span",
        { key: "services" },
        `services:${props.services?.map((service) => service.serviceId).join(",")}`,
      ),
    ]);
  },
}));

function createWorkflowMember(overrides?: Record<string, unknown>) {
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
      ...(overrides ?? {}),
    },
    implementationRef: null,
    lastBinding: null,
    currentBindingRun: null,
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

function createService(overrides?: Record<string, unknown>) {
  return {
    activeServingRevisionId: "rev-alpha",
    appId: "default",
    defaultServingRevisionId: "rev-alpha",
    deploymentId: "dep-alpha",
    deploymentStatus: "Active",
    displayName: "Alpha Workflow",
    endpoints: [
      {
        description: "Chat with the workflow.",
        displayName: "Chat",
        endpointId: "chat",
        kind: "chat",
        requestTypeUrl: "",
        responseTypeUrl: "",
      },
    ],
    namespace: "default",
    policyIds: [],
    primaryActorId: "actor-alpha",
    serviceId: "svc-alpha",
    serviceKey: "scope-1:svc-alpha",
    tenantId: "default",
    updatedAt: "2026-06-01T00:10:00Z",
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
      "/teams/scope-1/team-1/members/member-alpha/invoke",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue(createWorkflowMember());
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValue(createBinding());
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValue([createService()]);
    (scopeRuntimeApi.getServiceRevisions as jest.Mock).mockResolvedValue(
      createRevisionCatalog(),
    );
  });

  it("renders the invoke workbench with Studio team-context routing", async () => {
    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByText("Run member")).toBeTruthy();
    expect(await screen.findByTestId("member-invoke-panel")).toHaveTextContent(
      "scope:scope-1",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "member:member-alpha",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "target:default",
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
      "variant:member-run",
    );
    expect(screen.queryByRole("button", { name: "Team members" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Workflow Studio" })).toBeNull();
    expect(scopeRuntimeApi.listServices).toHaveBeenCalledWith("scope-1", {
      appId: "default",
    });
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
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValue([
      createService(),
      createService({
        activeServingRevisionId: "rev-beta",
        defaultServingRevisionId: "rev-beta",
        deploymentId: "dep-beta",
        displayName: "Beta Workflow",
        primaryActorId: "actor-beta",
        serviceId: "svc-beta",
        serviceKey: "scope-1:svc-beta",
      }),
    ]);

    renderWithQueryClient(React.createElement(TeamMemberInvokePage));

    expect(await screen.findByTestId("member-invoke-panel")).toHaveTextContent(
      "member:member-alpha",
    );
    expect(screen.getByTestId("member-invoke-panel")).toHaveTextContent(
      "service:svc-alpha",
    );

    history.push("/teams/scope-1/team-1/members/member-beta/invoke");

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
    expect(screen.getAllByRole("button", { name: "Workflow Studio" })).toHaveLength(1);
  });
});
