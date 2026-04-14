import { render, waitFor } from "@testing-library/react";
import React from "react";
import { ProtectedRouteRedirectGate } from "./shared/auth/ProtectedRouteRedirectGate";

const mockedHistoryReplace = jest.fn();

jest.mock("./shared/navigation/history", () => ({
  history: {
    push: jest.fn(),
    replace: (...args: unknown[]) => mockedHistoryReplace(...args),
  },
}));

describe("ProtectedRouteRedirectGate", () => {
  beforeEach(() => {
    mockedHistoryReplace.mockReset();
    window.history.replaceState({}, "", "/teams");
  });

  it("redirects protected routes into the login flow after mount", async () => {
    render(
      React.createElement(ProtectedRouteRedirectGate, {
        pathname: "/teams",
      }),
    );

    await waitFor(() => {
      expect(mockedHistoryReplace).toHaveBeenCalledWith("/login?redirect=%2Fteams");
    });
  });
});
