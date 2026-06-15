import { fireEvent, screen, waitFor } from "@testing-library/react";
import { setLocale } from "@umijs/max";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import PlatformOverviewPage from "./index";
import { loadRecentRuns } from "@/shared/runs/recentRuns";

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
    listServices: jest.fn(async () => [
      {
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
      },
      {
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
      },
    ]),
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
  loadRecentRuns: jest.fn(() => recentRunFixture),
}));

const { servicesApi: mockServicesApi } = jest.requireMock(
  "@/shared/api/servicesApi",
) as {
  servicesApi: {
    listServices: jest.Mock;
  };
};

describe("PlatformOverviewPage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (loadRecentRuns as jest.Mock).mockReturnValue(recentRunFixture);
    setLocale("en-US", false);
    window.history.replaceState({}, "", "/platform");
  });

  it("renders five task modules with summaries and deep-link CTAs", async () => {
    renderWithQueryClient(React.createElement(PlatformOverviewPage));

    expect(await screen.findByRole("heading", { name: "Platform overview" })).toBeInTheDocument();
    expect(screen.getByText("Publish and run workflow")).toBeInTheDocument();

    expect(screen.getByRole("heading", { name: "Capabilities" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Access & Rules" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Releases" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Runs" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Runtime Map" })).toBeInTheDocument();

    expect(await screen.findByText("2 capabilities, 1 currently attached to serving.")).toBeInTheDocument();
    expect(await screen.findByText("1 policies and 1 active bindings on the first visible capability.")).toBeInTheDocument();
    expect(await screen.findByText("1 deployments are visible for the first capability.")).toBeInTheDocument();
    expect(screen.getByText("1 recent local runs, latest status completed.")).toBeInTheDocument();
    expect(screen.getByText("Runtime map can start from the current capability owner.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Open capabilities" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/services");
    });
    expect(window.location.search).toContain("serviceId=service-alpha");
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
