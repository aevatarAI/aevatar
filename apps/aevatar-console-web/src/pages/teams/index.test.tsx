import { screen } from "@testing-library/react";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamsHomePage from "./index";

jest.mock("./home", () => ({
  __esModule: true,
  default: () => {
    const mockReact = require("react");
    return mockReact.createElement("div", null, "Teams home route proxy");
  },
}));

describe("pages/teams/index", () => {
  it("proxies the route entry to the teams home implementation", () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(screen.getByText("Teams home route proxy")).toBeTruthy();
  });
});
