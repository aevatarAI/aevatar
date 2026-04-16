import { screen } from "@testing-library/react";
import React from "react";
import { persistAuthSession } from "@/shared/auth/session";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import AccountSettingsPage from "./account";

describe("AccountSettingsPage", () => {
  beforeEach(() => {
    window.localStorage.clear();
    jest.clearAllMocks();
  });

  it("renders signed-in account details", async () => {
    persistAuthSession({
      tokens: {
        accessToken: "token",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 60_000,
      },
      user: {
        sub: "user-123",
        email: "ada@example.com",
        email_verified: true,
        name: "Ada Lovelace",
        roles: ["admin", "operator"],
        groups: ["platform"],
      },
    });

    renderWithQueryClient(React.createElement(AccountSettingsPage));

    expect(await screen.findByText("Aevatar / Settings")).toBeTruthy();
    expect(await screen.findByText("Account Settings")).toBeTruthy();
    expect(screen.getByText("Profile")).toBeTruthy();
    expect(screen.getByText("Authentication")).toBeTruthy();
    expect(screen.queryByText("Access")).toBeNull();
    expect(screen.getByText("Ada Lovelace")).toBeTruthy();
    expect(screen.getAllByText("ada@example.com").length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Sign out" })).toBeTruthy();
  });
});
