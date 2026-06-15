import { fireEvent, screen, waitFor } from "@testing-library/react";
import { setLocale } from "@umijs/max";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import PlatformOverviewPage from "./index";
import { loadRecentRuns } from "@/shared/runs/recentRuns";

const serviceAlphaFixture = {
  activeServingRevisionId: "rev-2",
  appId: "app-a",
  defaultServingRevisionId: "rev-1",
  deploymentId: "deploy-1",
  deploymentStatus: "ready",
  displayName: "Service Alpha",
  endpoints: [
    {
      description: "Chat endpoint",
      displayName: "Chat",
      endpointId: "chat",
      kind: "chat",
      requestTypeUrl: "type.googleapis.com/demo.ChatRequest",
      responseTypeUrl: "type.googleapis.com/demo.ChatResponse",
    },
  ],
  namespace: "default",
  policyIds: ["policy-a"],
  primaryActorId: "actor-1",
  serviceId: "service-alpha",
  serviceKey: "tenant-a/app-a/default/service-alpha",
  tenantId: "tenant-a",
  updatedAt: "2026-03-25T10:00:00Z",
};

const serviceBetaFixture = {
  activeServingRevisionId: "",
  appId: "app-a",
  defaultServingRevisionId: "",
  deploymentId: "",
  deploymentStatus: "",
  displayName: "Service Beta",
  endpoints: [],
  namespace: "default",
  policyIds: [],
  primaryActorId: "",
  serviceId: "service-beta",
  serviceKey: "tenant-a/app-a/default/service-beta",
  tenantId: "tenant-a",
  updatedAt: "2026-03-24T10:00:00Z",
};

const serviceCatalogFixture = [serviceAlphaFixture, serviceBetaFixture];

const recentRunFixture = [
  {
    actorId: "actor-1",
    commandId: "command-1",
    endpointId: "chat",
    endpointKind: "chat",
    id: "recent-1",
    lastMessagePreview: "Done",
    observedEvents: [],
    payloadBase64: "",
    payloadTypeUrl: "",
    prompt: "Hello",
    recordedAt: "2026-03-25T11:00:00Z",
    routeName: "Chat",
    runId: "run-1",
    scopeId: "tenant-a",
    serviceOverrideId: "service-alpha",
    status: "completed",
  },
];

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    getDeployments: jest.fn(async () => ({
      deployments: [
        {
          activatedAt: "2026-03-25T09:00:00Z",
          deploymentId: "deploy-1",
          primaryActorId: "actor-1",
          revisionId: "rev-2",
          status: "ready",
          updatedAt: "2026-03-25T10:00:00Z",
        },
      ],
      serviceKey: "tenant-a/app-a/default/service-alpha",
      updatedAt: "2026-03-25T10:00:00Z",
    })),
    getTraffic: jest.fn(async () => ({
      activeRolloutId: "",
      endpoints: [
        {
          endpointId: "chat",
          targets: [
            {
              allocationWeight: 100,
              deploymentId: "deploy-1",
              primaryActorId: "actor-1",
              revisionId: "rev-2",
              servingState: "active",
            },
          ],
        },
      ],
      generation: 1,
      serviceKey: "tenant-a/app-a/default/service-alpha",
      updatedAt: "2026-03-25T10:00:00Z",
    })),
    listServices: jest.fn(),
  },
}));

jest.mock("@/shared/api/governanceApi", () => ({
  governanceApi: {
    getBindings: jest.fn(async () => ({
      bindings: [
        {
          bindingId: "binding-a",
          bindingKind: "service",
          connectorRef: null,
          displayName: "Binding A",
          policyIds: ["policy-a"],
          retired: false,
          secretRef: null,
          serviceRef: null,
        },
      ],
      serviceKey: "tenant-a/app-a/default/service-alpha",
      updatedAt: "2026-03-25T10:00:00Z",
    })),
  },
}));

jest.mock("@/shared/runs/recentRuns", () => ({
  loadRecentRuns: jest.fn(),
}));

const { servicesApi: mockServicesApi } = jest.requireMock(
  "@/shared/api/servicesApi",
) as {
  servicesApi: {
    getDeployments: jest.Mock;
    getTraffic: jest.Mock;
    listServices: jest.Mock;
  };
};

const { governanceApi: mockGovernanceApi } = jest.requireMock(
  "@/shared/api/governanceApi",
) as {
  governanceApi: {
    getBindings: jest.Mock;
  };
};

async function renderOverviewPage() {
  renderWithQueryClient(React.createElement(PlatformOverviewPage));
  expect(await screen.findByRole("heading", { name: "Platform overview" })).toBeInTheDocument();
}

async function clickModuleCta(ctaName: string) {
  await renderOverviewPage();
  await screen.findByText("2 capabilities, 1 currently attached to serving.");
  fireEvent.click(screen.getByRole("button", { name: ctaName }));

  await waitFor(() => {
    expect(window.location.pathname).not.toBe("/platform");
  });
}

function expectFirstServiceIdentityQuery() {
  expect(window.location.search).toContain("tenantId=tenant-a");
  expect(window.location.search).toContain("appId=app-a");
  expect(window.location.search).toContain("namespace=default");
  expect(window.location.search).toContain("serviceId=service-alpha");
}

describe("PlatformOverviewPage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (loadRecentRuns as jest.Mock).mockReturnValue(recentRunFixture);
    mockServicesApi.listServices.mockResolvedValue(serviceCatalogFixture);
    setLocale("en-US", false);
    window.history.replaceState({}, "", "/platform");
  });

  it("renders five task modules with summaries and deep-link CTAs", async () => {
    await renderOverviewPage();

    expect(screen.getByText("Publish and run workflow")).toBeInTheDocument();

    expect(screen.getByRole("heading", { name: "Capabilities" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Access & Rules" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Releases" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Runs" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Runtime Map" })).toBeInTheDocument();

    expect(await screen.findByText("2 capabilities, 1 currently attached to serving.")).toBeInTheDocument();
    expect(await screen.findByText("Policies: 1; active bindings: 1 on the first visible capability.")).toBeInTheDocument();
    expect(await screen.findByText("Visible deployments for the first capability: 1.")).toBeInTheDocument();
    expect(screen.getByText("Recent local runs: 1; latest status completed.")).toBeInTheDocument();
    expect(screen.getByText("Runtime map can start from the current capability owner.")).toBeInTheDocument();
  });

  it("routes Open capabilities to the scoped first service catalog entry", async () => {
    await clickModuleCta("Open capabilities");

    expect(window.location.pathname).toBe("/services");
    expectFirstServiceIdentityQuery();
  });

  it("routes Review access and rules to the scoped first service governance entry", async () => {
    await clickModuleCta("Review access and rules");

    expect(window.location.pathname).toBe("/governance");
    expectFirstServiceIdentityQuery();
  });

  it("routes Manage releases to the scoped first service deployments entry", async () => {
    await clickModuleCta("Manage releases");

    expect(window.location.pathname).toBe("/deployments");
    expectFirstServiceIdentityQuery();
  });

  it("routes Inspect runs to runtime runs with the first service actor context", async () => {
    await clickModuleCta("Inspect runs");

    expect(window.location.pathname).toBe("/runtime/runs");
    expect(window.location.search).toContain("serviceOverrideId=service-alpha");
    expect(window.location.search).toContain("actorId=actor-1");
  });

  it("routes Open runtime map to the first service actor detail context", async () => {
    await clickModuleCta("Open runtime map");

    expect(window.location.pathname).toBe("/runtime/explorer/detail");
    expect(window.location.search).toContain("serviceId=service-alpha");
    expect(window.location.search).toContain("actorId=actor-1");
  });

  it("shows guidance summaries when the service catalog is successfully empty", async () => {
    mockServicesApi.listServices.mockResolvedValueOnce([]);
    (loadRecentRuns as jest.Mock).mockReturnValue([]);

    await renderOverviewPage();

    expect(await screen.findByText("No capabilities are visible in the current workspace yet.")).toBeInTheDocument();
    expect(screen.getByText("Choose a capability to inspect who can call it and which rules apply.")).toBeInTheDocument();
    expect(screen.getByText("Release controls appear after a capability has a serving target.")).toBeInTheDocument();
    expect(screen.getByText("Open the runtime map to inspect actors and relationships after a run exists.")).toBeInTheDocument();
    expect(screen.getByText("Summaries use existing frontend reads and local run handoffs, so weak signals stay labeled as guidance.")).toBeInTheDocument();
    expect(mockGovernanceApi.getBindings).not.toHaveBeenCalled();
    expect(mockServicesApi.getDeployments).not.toHaveBeenCalled();
    expect(mockServicesApi.getTraffic).not.toHaveBeenCalled();
  });

  it("keeps capability data visible while partial governance and release queries fail", async () => {
    mockGovernanceApi.getBindings.mockRejectedValueOnce(
      new Error("bindings unavailable"),
    );
    mockServicesApi.getDeployments.mockRejectedValueOnce(
      new Error("deployments unavailable"),
    );

    await renderOverviewPage();

    expect(await screen.findByText("2 capabilities, 1 currently attached to serving.")).toBeInTheDocument();
    expect(await screen.findByText("Access and rule catalogs are temporarily unavailable.")).toBeInTheDocument();
    expect(await screen.findByText("Release and traffic evidence are temporarily unavailable.")).toBeInTheDocument();
    expect(screen.getByText("Summaries use existing frontend reads and local run handoffs, so weak signals stay labeled as guidance.")).toBeInTheDocument();
  });

  it("shows honest fallback summaries when catalog reads are unavailable", async () => {
    mockServicesApi.listServices.mockRejectedValueOnce(
      new Error("service catalog unavailable"),
    );
    (loadRecentRuns as jest.Mock).mockReturnValue([]);

    renderWithQueryClient(React.createElement(PlatformOverviewPage));

    expect(await screen.findByText("Capability catalog is temporarily unavailable.")).toBeInTheDocument();
    expect(screen.getByText("Current workspace summary is using guidance because the capability catalog could not be read.")).toBeInTheDocument();
    expect(screen.getByText("No recent local run handoff has been recorded in this browser.")).toBeInTheDocument();
    expect(screen.getAllByText("Guidance").length).toBeGreaterThan(0);
  });
});
