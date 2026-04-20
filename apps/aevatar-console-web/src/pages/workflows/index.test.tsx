import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import WorkflowsPage from "./index";

jest.mock("@/shared/api/runtimeCatalogApi", () => ({
  runtimeCatalogApi: {
    listWorkflowCatalog: jest.fn(async () => [
      {
        name: "demo_flow",
        description: "Demo workflow",
        category: "demo",
        group: "demo",
        groupLabel: "Demo",
        sortOrder: 1,
        source: "BuiltIn",
        sourceLabel: "Built-in",
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: false,
        primitives: ["human_input"],
      },
    ]),
    getWorkflowDetail: jest.fn(async () => ({
      catalog: {
        name: "demo_flow",
        description: "Demo workflow",
        category: "demo",
        group: "demo",
        groupLabel: "Demo",
        sortOrder: 1,
        source: "BuiltIn",
        sourceLabel: "Built-in",
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: false,
        primitives: ["human_input"],
      },
      yaml: "name: demo_flow\nsteps: []\n",
      definition: {
        name: "demo_flow",
        description: "Demo workflow",
        closedWorldMode: true,
        roles: [
          {
            id: "planner",
            name: "Planner",
            systemPrompt: "Plan the work.",
            provider: "",
            model: "",
            temperature: 0,
            maxTokens: 0,
            maxToolRounds: 0,
            maxHistoryMessages: 0,
            streamBufferCapacity: 0,
            eventModules: [],
            eventRoutes: "",
            connectors: ["memory"],
          },
        ],
        steps: [
          {
            id: "step_prepare",
            type: "prompt",
            targetRole: "planner",
            parameters: { input: "{{prompt}}" },
            next: "",
            branches: {},
            children: [],
          },
        ],
      },
      edges: [],
    })),
  },
}));

describe("WorkflowsPage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/runtime/workflows");
  });

  it("opens the workflow inspector from the workflow query", async () => {
    window.history.replaceState({}, "", "/runtime/workflows?workflow=demo_flow");

    renderWithQueryClient(React.createElement(WorkflowsPage));

    await waitFor(() => {
      expect(runtimeCatalogApi.getWorkflowDetail).toHaveBeenCalledWith(
        "demo_flow",
      );
    });

    expect(await screen.findByText("Workflow Summary")).toBeTruthy();
    expect(window.location.search).toContain("workflow=demo_flow");
  });

  it("closes the inspector and clears the workflow query", async () => {
    window.history.replaceState({}, "", "/runtime/workflows?workflow=demo_flow");

    renderWithQueryClient(React.createElement(WorkflowsPage));

    await waitFor(() => {
      expect(runtimeCatalogApi.getWorkflowDetail).toHaveBeenCalledWith(
        "demo_flow",
      );
    });

    fireEvent.click(document.querySelector(".ant-drawer-close") as HTMLElement);

    await waitFor(() => {
      expect(window.location.search).toBe("");
      expect(screen.queryByText("Workflow Summary")).toBeNull();
    });
  });

  it("stretches the filter group selector to the panel width", async () => {
    const { container } = renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("Library Digest")).toBeTruthy();
    expect(container.querySelector(".ant-select")).toHaveStyle({ width: "100%" });
  });

  it("renders catalog cards as full-width summaries with in-card actions", async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("Closed-world ready")).toBeTruthy();
    expect(screen.getByText("Group")).toBeTruthy();
    expect(screen.getByText("Source")).toBeTruthy();
    expect(screen.getByText("Primitives")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Inspect" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Run" })).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Inspect workflow demo_flow" }),
    ).toHaveStyle({
      width: "100%",
    });
  });

  it("opens the workflow inspector when the catalog card is clicked", async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    fireEvent.click(
      await screen.findByRole("button", { name: "Inspect workflow demo_flow" }),
    );

    expect(await screen.findByText("Workflow Summary")).toBeTruthy();
  });

  it("opens the Studio workflow editor from the workflow inspector", async () => {
    window.history.replaceState({}, "", "/runtime/workflows?workflow=demo_flow");

    renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("Workflow Summary")).toBeTruthy();

    fireEvent.click(
      screen.getByRole("button", { name: "Open workflow editor" }),
    );

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
      expect(window.location.search).toBe("?workflow=demo_flow&tab=studio");
    });
  });
});
