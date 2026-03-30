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
      "Start from the current project for scope-first workflow, script, and binding flows. Runtime and platform diagnostics stay grouped under Operator Tools."
    );
    expect(container.textContent).toContain("Current project");
    expect(container.textContent).toContain("Operator Tools");
    expect(container.textContent).toContain("Enter Current Project");
    expect(container.textContent).toContain("Open Studio");
    expect(container.textContent).toContain("Runtime tools");
    expect(container.textContent).toContain("Platform operator views");
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
