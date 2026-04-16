import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import DeploymentsPage from "./index";

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    advanceRollout: jest.fn(),
    deactivateDeployment: jest.fn(),
    deployRevision: jest.fn(),
    getDeployments: jest.fn(),
    getRevisions: jest.fn(),
    getRollout: jest.fn(),
    getService: jest.fn(),
    getServingSet: jest.fn(),
    getTraffic: jest.fn(),
    listServices: jest.fn(),
    pauseRollout: jest.fn(),
    replaceServingTargets: jest.fn(),
    resumeRollout: jest.fn(),
    rollbackRollout: jest.fn(),
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      scope: {
        id: "scope-1",
      },
    })),
  },
}));

const { servicesApi: mockServicesApi } = jest.requireMock(
  "@/shared/api/servicesApi",
) as {
  servicesApi: {
    advanceRollout: jest.Mock;
    deactivateDeployment: jest.Mock;
    deployRevision: jest.Mock;
    getDeployments: jest.Mock;
    getRevisions: jest.Mock;
    getRollout: jest.Mock;
    getService: jest.Mock;
    getServingSet: jest.Mock;
    getTraffic: jest.Mock;
    listServices: jest.Mock;
    pauseRollout: jest.Mock;
    replaceServingTargets: jest.Mock;
    resumeRollout: jest.Mock;
    rollbackRollout: jest.Mock;
  };
};

function renderDeploymentsPage(
  path = "/deployments?tenantId=scope-1",
) {
  window.history.replaceState({}, "", path);
  return renderWithQueryClient(React.createElement(DeploymentsPage));
}

beforeEach(() => {
  jest.clearAllMocks();

  mockServicesApi.listServices.mockResolvedValue([
    {
      serviceKey: "scope-1:trade-agent",
      tenantId: "scope-1",
      appId: "trade-app",
      namespace: "cn.market",
      serviceId: "trade-agent",
      displayName: "Trade Agent",
      defaultServingRevisionId: "rev-11",
      activeServingRevisionId: "rev-11",
      deploymentId: "dep-1",
      primaryActorId: "actor-1",
      deploymentStatus: "active",
      endpoints: [],
      policyIds: ["policy-1"],
      updatedAt: "2026-03-30T10:00:00Z",
    },
  ]);

  mockServicesApi.getService.mockResolvedValue({
    serviceKey: "scope-1:trade-agent",
    tenantId: "scope-1",
    appId: "trade-app",
    namespace: "cn.market",
    serviceId: "trade-agent",
    displayName: "Trade Agent",
    defaultServingRevisionId: "rev-11",
    activeServingRevisionId: "rev-11",
    deploymentId: "dep-1",
    primaryActorId: "actor-1",
    deploymentStatus: "active",
    endpoints: [],
    policyIds: ["policy-1"],
    updatedAt: "2026-03-30T10:00:00Z",
  });

  mockServicesApi.getRevisions.mockResolvedValue({
    serviceKey: "scope-1:trade-agent",
    revisions: [
      {
        revisionId: "rev-12",
        implementationKind: "workflow",
        status: "validated",
        artifactHash: "hash-12",
        failureReason: "",
        endpoints: [],
        createdAt: "2026-03-30T10:00:00Z",
        preparedAt: "2026-03-30T10:02:00Z",
        publishedAt: "2026-03-30T10:05:00Z",
        retiredAt: null,
      },
      {
        revisionId: "rev-11",
        implementationKind: "workflow",
        status: "active",
        artifactHash: "hash-11",
        failureReason: "",
        endpoints: [],
        createdAt: "2026-03-29T10:00:00Z",
        preparedAt: "2026-03-29T10:02:00Z",
        publishedAt: "2026-03-29T10:05:00Z",
        retiredAt: null,
      },
    ],
    updatedAt: "2026-03-30T10:00:00Z",
  });

  mockServicesApi.getDeployments.mockResolvedValue({
    serviceKey: "scope-1:trade-agent",
    deployments: [
      {
        deploymentId: "dep-1",
        revisionId: "rev-11",
        primaryActorId: "actor-1",
        status: "active",
        activatedAt: "2026-03-29T10:05:00Z",
        updatedAt: "2026-03-30T10:00:00Z",
      },
    ],
    updatedAt: "2026-03-30T10:00:00Z",
  });

  mockServicesApi.getServingSet.mockResolvedValue({
    serviceKey: "scope-1:trade-agent",
    generation: 3,
    activeRolloutId: "rollout-1",
    targets: [
      {
        deploymentId: "dep-1",
        revisionId: "rev-11",
        primaryActorId: "actor-1",
        allocationWeight: 90,
        servingState: "active",
        enabledEndpointIds: ["chat"],
      },
      {
        deploymentId: "dep-2",
        revisionId: "rev-12",
        primaryActorId: "actor-2",
        allocationWeight: 10,
        servingState: "canary",
        enabledEndpointIds: ["chat"],
      },
    ],
    updatedAt: "2026-03-30T10:00:00Z",
  });

  mockServicesApi.getRollout.mockResolvedValue({
    serviceKey: "scope-1:trade-agent",
    rolloutId: "rollout-1",
    displayName: "March Canary",
    status: "canary",
    currentStageIndex: 1,
    stages: [
      {
        stageId: "stage-0",
        stageIndex: 0,
        targets: [],
      },
      {
        stageId: "stage-1",
        stageIndex: 1,
        targets: [
          {
            deploymentId: "dep-1",
            revisionId: "rev-11",
            primaryActorId: "actor-1",
            allocationWeight: 90,
            servingState: "active",
            enabledEndpointIds: ["chat"],
          },
          {
            deploymentId: "dep-2",
            revisionId: "rev-12",
            primaryActorId: "actor-2",
            allocationWeight: 10,
            servingState: "canary",
            enabledEndpointIds: ["chat"],
          },
        ],
      },
    ],
    baselineTargets: [
      {
        deploymentId: "dep-1",
        revisionId: "rev-11",
        primaryActorId: "actor-1",
        allocationWeight: 100,
        servingState: "active",
        enabledEndpointIds: ["chat"],
      },
    ],
    failureReason: "",
    startedAt: "2026-03-30T10:01:00Z",
    updatedAt: "2026-03-30T10:05:00Z",
  });

  mockServicesApi.getTraffic.mockResolvedValue({
    serviceKey: "scope-1:trade-agent",
    generation: 3,
    activeRolloutId: "rollout-1",
    endpoints: [
      {
        endpointId: "chat",
        targets: [
          {
            deploymentId: "dep-1",
            revisionId: "rev-11",
            primaryActorId: "actor-1",
            allocationWeight: 90,
            servingState: "active",
          },
          {
            deploymentId: "dep-2",
            revisionId: "rev-12",
            primaryActorId: "actor-2",
            allocationWeight: 10,
            servingState: "canary",
          },
        ],
      },
    ],
    updatedAt: "2026-03-30T10:05:00Z",
  });

  mockServicesApi.deployRevision.mockResolvedValue({
    targetActorId: "actor-1",
    commandId: "cmd-1",
    correlationId: "corr-1",
  });

  mockServicesApi.replaceServingTargets.mockResolvedValue({
    targetActorId: "actor-1",
    commandId: "cmd-2",
    correlationId: "corr-2",
  });

  mockServicesApi.advanceRollout.mockResolvedValue({
    targetActorId: "actor-1",
    commandId: "cmd-3",
    correlationId: "corr-3",
  });
  mockServicesApi.pauseRollout.mockResolvedValue({
    targetActorId: "actor-1",
    commandId: "cmd-4",
    correlationId: "corr-4",
  });
  mockServicesApi.resumeRollout.mockResolvedValue({
    targetActorId: "actor-1",
    commandId: "cmd-5",
    correlationId: "corr-5",
  });
  mockServicesApi.rollbackRollout.mockResolvedValue({
    targetActorId: "actor-1",
    commandId: "cmd-6",
    correlationId: "corr-6",
  });
  mockServicesApi.deactivateDeployment.mockResolvedValue({
    targetActorId: "actor-1",
    commandId: "cmd-7",
    correlationId: "corr-7",
  });
});

afterEach(() => {
  cleanupTestQueryClients();
});

describe("DeploymentsPage", () => {
  it("stays in empty detail state until an operator selects a deployment", async () => {
    renderDeploymentsPage();

    expect(await screen.findByText("Aevatar / Platform")).toBeInTheDocument();
    expect(await screen.findByText("Deployments")).toBeInTheDocument();
    expect(await screen.findByText("Deployments 负责解释谁在 serving、候选版本推进到了哪一个 stage，以及流量目前如何分配。它是从 Services drill-down 后进入的发布操作台。")).toBeInTheDocument();
    expect(await screen.findByText("选择一个部署")).toBeInTheDocument();
    expect(screen.queryByText("Deployment Inventory")).toBeNull();
  });

  it("opens the extra-wide rollout drawer from the detail header", async () => {
    renderDeploymentsPage(
      "/deployments?tenantId=scope-1&serviceId=trade-agent&deploymentId=dep-1",
    );

    expect(await screen.findByText("Trade Agent")).toBeInTheDocument();
    fireEvent.click(await screen.findByRole("button", { name: "查看部署" }));

    fireEvent.click(await screen.findByRole("button", { name: "调整权重" }));

    expect(await screen.findByText("Serving Controls")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "应用权重" })).toBeInTheDocument();
  });

  it("dispatches the candidate revision from the compare drawer", async () => {
    renderDeploymentsPage(
      "/deployments?tenantId=scope-1&serviceId=trade-agent&deploymentId=dep-1",
    );

    expect(await screen.findByText("Trade Agent")).toBeInTheDocument();
    fireEvent.click(await screen.findByRole("button", { name: "查看部署" }));

    fireEvent.click(await screen.findByRole("button", { name: "推进发布" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "发布候选版本" }),
    );

    await waitFor(() => {
      expect(mockServicesApi.deployRevision).toHaveBeenCalledWith(
        "trade-agent",
        expect.objectContaining({
          revisionId: "rev-12",
        }),
      );
    });
  });
});
