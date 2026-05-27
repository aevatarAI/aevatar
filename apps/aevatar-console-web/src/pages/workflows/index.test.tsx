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

    expect(await screen.findByText("定义摘要")).toBeTruthy();
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
      expect(screen.queryByText("定义摘要")).toBeNull();
    });
  });

  it("renders a compact workflow filter bar with runtime-focused controls", async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("查找 Workflow")).toBeTruthy();
    expect(screen.getByPlaceholderText("搜索 Workflow、描述、分组或 primitive")).toBeTruthy();
    expect(screen.getByText("Workflow 目录")).toBeTruthy();
    expect(screen.getByRole("button", { name: "清除筛选" })).toBeTruthy();
  });

  it("renders the catalog as a table with inspect and run actions", async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("闭环就绪")).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "集合" })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "运行适配" })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "Primitives" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "查看" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "运行" })).toBeTruthy();
  });

  it("opens the definition inspector when the inspect action is clicked", async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    fireEvent.click(await screen.findByRole("button", { name: "查看" }));

    expect(await screen.findByText("定义摘要")).toBeTruthy();
  });

  it("opens the Studio workflow editor from the definition inspector", async () => {
    window.history.replaceState({}, "", "/runtime/workflows?workflow=demo_flow");

    renderWithQueryClient(React.createElement(WorkflowsPage));

    expect(await screen.findByText("定义摘要")).toBeTruthy();

    fireEvent.click(
      screen.getByRole("button", { name: "打开 Workflow 编辑器" }),
    );

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
      expect(window.location.search).toBe("?focus=workflow%3Ademo_flow&tab=studio");
    });
  });
});
