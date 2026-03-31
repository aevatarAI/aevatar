import { waitFor } from "@testing-library/react";
import React from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import OverviewPage from "./index";

jest.mock("@/shared/api/runtimeCatalogApi", () => ({
  runtimeCatalogApi: {
    listWorkflowNames: jest.fn(async () => []),
    listWorkflowCatalog: jest.fn(async () => []),
  },
}));

jest.mock("@/shared/api/runtimeQueryApi", () => ({
  runtimeQueryApi: {
    listAgents: jest.fn(async () => []),
    getCapabilities: jest.fn(async () => ({
      schemaVersion: "capabilities.v1",
      generatedAtUtc: "2026-03-12T00:00:00Z",
      primitives: [],
      connectors: [],
      workflows: [],
    })),
  },
}));

describe("OverviewPage", () => {
  it("renders the overview title", async () => {
    const { container } = renderWithQueryClient(
      React.createElement(OverviewPage)
    );

    expect(container.textContent).toContain("Overview");
    expect(container.textContent).toContain(
      "A single command-center view from login to runtime: project-first actions on the left, ecosystem health in the center, and detail only when you ask for it."
    );
    expect(container.textContent).toContain("Command Path");
    expect(container.textContent).toContain("Operator Shortcuts");
    expect(container.textContent).toContain(
      "Anchor work to a project"
    );
    expect(container.textContent).toContain(
      "Promote a capability"
    );
    expect(container.textContent).toContain(
      "Operate the runtime"
    );
    expect(container.textContent).toContain("State Board");
    expect(container.textContent).toContain("Human Loop");
    expect(container.textContent).toContain("Live Actors");
    expect(container.textContent).toContain("Open Projects");
    expect(container.textContent).toContain("Open workflow workspace");
    expect(container.textContent).toContain("Open Runs");
    expect(container.textContent).toContain("Open Invoke Lab");
    expect(container.textContent).toContain("Open governance");
    expect(container.textContent).toContain("Workflow sources");
    expect(container.textContent).toContain("Runtime attention");
    expect(container.textContent).not.toContain("Start preferred workflow");
    expect(container.textContent).not.toContain("Preferred workflow");
    expect(container.textContent).not.toContain("Open Runtime Observability");
    expect(container.textContent).not.toContain("Capability surfaces");
    await waitFor(() => {
      expect(runtimeCatalogApi.listWorkflowNames).toHaveBeenCalled();
      expect(runtimeQueryApi.listAgents).toHaveBeenCalled();
      expect(runtimeQueryApi.getCapabilities).toHaveBeenCalled();
    });
  });
});
