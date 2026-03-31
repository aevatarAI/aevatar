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

    expect(container.textContent).toContain("Primitive Library");
    expect(container.textContent).toContain(
      "Primitive definitions are now managed as a runtime library workbench. The main stage stays dedicated to discovery while parameter contracts and example workflows live in the inspector.",
    );
    expect(container.textContent).toContain("Runtime Primitives");
    expect(container.textContent).toContain("Filter Library");
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

    expect(await screen.findByText("Library Digest")).toBeTruthy();
    expect(container.querySelector(".ant-select")).toHaveStyle({ width: "100%" });
  });

  it("renders primitive cards as full-width summaries with in-card actions", async () => {
    renderWithQueryClient(React.createElement(PrimitivesPage));

    expect(await screen.findByText("Ready")).toBeTruthy();
    expect(screen.getByText("Category")).toBeTruthy();
    expect(screen.getByText("Parameters")).toBeTruthy();
    expect(screen.getByText("Examples")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Inspect" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Example workflow" })).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Inspect primitive human_input" }),
    ).toHaveStyle({
      width: "100%",
    });
  });

  it("opens the primitive inspector when the catalog card is clicked", async () => {
    renderWithQueryClient(React.createElement(PrimitivesPage));

    fireEvent.click(
      await screen.findByRole("button", { name: "Inspect primitive human_input" }),
    );

    expect(await screen.findByText("Primitive contract")).toBeTruthy();
  });
});
