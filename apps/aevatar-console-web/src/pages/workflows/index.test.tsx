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

  it("opens the definition inspector from the workflow query", async () => {
    window.history.replaceState({}, "", "/runtime/workflows?workflow=demo_flow");

    renderWithQueryClient(React.createElement(WorkflowsPage));

    await waitFor(() => {
      expect(runtimeCatalogApi.getWorkflowDetail).toHaveBeenCalledWith(
        "demo_flow",
      );
    });

    expect(await screen.findByText("Definition summary")).toBeTruthy();
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
      expect(screen.queryByText("Definition summary")).toBeNull();
    });
  });

  it("renders a compact workflow filter bar with runtime-focused controls", async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("Find workflows")).toBeTruthy();
    expect(screen.getByPlaceholderText("Search workflow, description, group, or primitive")).toBeTruthy();
    expect(screen.getByText("Workflow catalog")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reset" })).toBeTruthy();
  });

  it("renders the catalog as a table with inspect and run actions", async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("Closed-world ready")).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "Collection" })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "Runtime fit" })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "Primitives" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Inspect" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Run" })).toBeTruthy();
  });

  it("opens the definition inspector when the inspect action is clicked", async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    fireEvent.click(await screen.findByRole("button", { name: "Inspect" }));

    expect(await screen.findByText("Definition summary")).toBeTruthy();
  });

  it("opens the Studio workflow editor from the definition inspector", async () => {
    window.history.replaceState({}, "", "/runtime/workflows?workflow=demo_flow");

    renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("Definition summary")).toBeTruthy();

    fireEvent.click(
      screen.getByRole("button", { name: "Open workflow editor" }),
    );

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
      expect(window.location.search).toBe("?member=workflow%3Ademo_flow&tab=studio");
    });
  });
});
