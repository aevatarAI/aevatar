import { screen } from "@testing-library/react";
import React from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ConsoleSettingsPage from "./console";

jest.mock("@/shared/api/runtimeCatalogApi", () => ({
  runtimeCatalogApi: {
    listWorkflowCatalog: jest.fn(async () => [
      {
        name: "incident_triage",
        description: "Incident triage",
        category: "ops",
        group: "starter",
        groupLabel: "Starter",
        sortOrder: 1,
        source: "home",
        sourceLabel: "Saved",
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: true,
        primitives: ["llm_call"],
      },
    ]),
  },
}));

describe("ConsoleSettingsPage", () => {
  beforeEach(() => {
    window.localStorage.clear();
    jest.clearAllMocks();
  });

  it("renders console preference sections on the dedicated settings page", async () => {
    renderWithQueryClient(React.createElement(ConsoleSettingsPage));

    expect(await screen.findByText("Console preferences")).toBeTruthy();
    expect(screen.getByText("Workflow defaults")).toBeTruthy();
    expect(screen.getByText("Observability URLs")).toBeTruthy();
    expect(screen.getByText("Runtime explorer defaults")).toBeTruthy();
    expect(runtimeCatalogApi.listWorkflowCatalog).toHaveBeenCalled();
  });
});
