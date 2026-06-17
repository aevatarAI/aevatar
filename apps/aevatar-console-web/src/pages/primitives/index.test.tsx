import { fireEvent, screen, waitFor } from "@testing-library/react";
import { setLocale } from "@umijs/max";
import React from "react";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import PrimitivesPage from "./index";

jest.mock("@/shared/api/runtimeQueryApi", () => ({
  runtimeQueryApi: {
    listPrimitives: jest.fn(async () => [
      {
        name: "human_input",
        category: "interaction",
        description: "Pause the workflow and request human input.",
        aliases: ["humanApproval"],
        parameters: [
          {
            name: "prompt",
            type: "string",
            required: true,
            default: "",
            enumValues: [],
            description: "Prompt shown to the human operator.",
          },
        ],
        exampleWorkflows: ["incident_triage"],
      },
    ]),
  },
}));

describe("PrimitivesPage", () => {
  beforeEach(() => {
    setLocale("zh-CN", false);
  });

  afterEach(() => {
    setLocale("en-US", false);
  });

  it("keeps primitive examples inside runtime and scope surfaces", async () => {
    renderWithQueryClient(React.createElement(PrimitivesPage));

    expect(screen.getByText("连接器目录")).toBeInTheDocument();
    expect(
      screen.queryByText(
        "Primitive definitions are now managed as a runtime library workbench. The main stage stays dedicated to discovery while parameter contracts and example workflows live in the inspector.",
      ),
    ).toBeNull();
    expect(screen.getAllByRole("button", { name: "显示帮助" }).length).toBeGreaterThan(0);
    expect(screen.getByText("可用连接器")).toBeInTheDocument();
    expect(screen.getByText("筛选连接器")).toBeInTheDocument();
    expect(screen.queryByText("Legacy draft")).not.toBeInTheDocument();
    expect(screen.queryByText("Studio")).not.toBeInTheDocument();

    await waitFor(() => {
      expect(runtimeQueryApi.listPrimitives).toHaveBeenCalled();
    });
  });

  it("renders primitive cards with summary fields and in-card actions", async () => {
    renderWithQueryClient(React.createElement(PrimitivesPage));

    expect(await screen.findByText("Ready")).toBeInTheDocument();
    expect(screen.getByText("分类")).toBeInTheDocument();
    expect(screen.getByText("参数")).toBeInTheDocument();
    expect(screen.getByText("示例")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "查看" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "示例行为定义" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "查看连接器 human_input" })).toBeInTheDocument();
  });

  it("opens the primitive inspector when the catalog card is clicked", async () => {
    renderWithQueryClient(React.createElement(PrimitivesPage));

    fireEvent.click(
      await screen.findByRole("button", { name: "查看连接器 human_input" }),
    );

    expect(await screen.findByText("连接器契约")).toBeTruthy();
  });
});
