import { screen } from "@testing-library/react";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamDetailPage from "./index";

jest.mock("./detail", () => ({
  __esModule: true,
  default: () => {
    const mockReact = require("react");
    return mockReact.createElement("div", null, "Team detail route proxy");
  },
}));

describe("pages/teams/index", () => {
  it("proxies the route entry to the detail page implementation", () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(screen.getByText("Team detail route proxy")).toBeTruthy();
  });
});
