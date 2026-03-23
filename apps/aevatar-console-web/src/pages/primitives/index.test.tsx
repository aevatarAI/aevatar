import { screen } from "@testing-library/react";
import React from "react";
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
      React.createElement(PrimitivesPage)
    );

    expect(container.textContent).toContain("Runtime Primitives");
    expect(container.textContent).toContain(
      "Browse the backend-authored runtime primitive view, including normalized parameters and example workflow references."
    );
    expect(
      await screen.findByRole("button", { name: "Inspect runtime" })
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: "Run" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Scope assets" })).toBeTruthy();
    expect(container.textContent).not.toContain("Legacy draft");
    expect(container.textContent).not.toContain("Studio");
  });
});
