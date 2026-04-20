import { fireEvent, screen, waitFor } from "@testing-library/react";
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
  it("keeps primitive examples inside runtime and scope surfaces", async () => {
    const { container } = renderWithQueryClient(
      React.createElement(PrimitivesPage),
    );

    expect(container.textContent).toContain("连接器目录");
    expect(
      screen.queryByText(
        "Primitive definitions are now managed as a runtime library workbench. The main stage stays dedicated to discovery while parameter contracts and example workflows live in the inspector.",
      ),
    ).toBeNull();
    expect(screen.getAllByRole("button", { name: "Show help" }).length).toBeGreaterThan(0);
    expect(container.textContent).toContain("可用连接器");
    expect(container.textContent).toContain("筛选连接器");
    expect(container.textContent).not.toContain("Legacy draft");
    expect(container.textContent).not.toContain("Studio");

    await waitFor(() => {
      expect(runtimeQueryApi.listPrimitives).toHaveBeenCalled();
    });
  });

  it("stretches the category filter selector to the panel width", async () => {
    const { container } = renderWithQueryClient(
      React.createElement(PrimitivesPage),
    );

    expect(await screen.findByText("目录摘要")).toBeTruthy();
    expect(container.querySelector(".ant-select")).toHaveStyle({ width: "100%" });
  });

  it("renders primitive cards as full-width summaries with in-card actions", async () => {
    renderWithQueryClient(React.createElement(PrimitivesPage));

    expect(await screen.findByText("Ready")).toBeTruthy();
    expect(screen.getByText("分类")).toBeTruthy();
    expect(screen.getByText("参数")).toBeTruthy();
    expect(screen.getByText("示例")).toBeTruthy();
    expect(screen.getByRole("button", { name: "查看" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "示例 workflow" })).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "查看连接器 human_input" }),
    ).toHaveStyle({
      width: "100%",
    });
  });

  it("opens the primitive inspector when the catalog card is clicked", async () => {
    renderWithQueryClient(React.createElement(PrimitivesPage));

    fireEvent.click(
      await screen.findByRole("button", { name: "查看连接器 human_input" }),
    );

    expect(await screen.findByText("连接器契约")).toBeTruthy();
  });
});
