import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { persistAuthSession } from "@/shared/auth/session";
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import SettingsPage from "./index";

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getUserConfig: jest.fn(),
    getUserConfigModels: jest.fn(),
    saveUserConfig: jest.fn(),
  },
}));

const { studioApi: mockStudioApi } = jest.requireMock(
  "@/shared/studio/api",
) as {
  studioApi: {
    getUserConfig: jest.Mock;
    getUserConfigModels: jest.Mock;
    saveUserConfig: jest.Mock;
  };
};

describe("SettingsPage", () => {
  beforeEach(() => {
    window.localStorage.clear();
    window.history.replaceState({}, "", "/settings");
    jest.clearAllMocks();

    mockStudioApi.getUserConfig.mockResolvedValue({
      defaultModel: "gpt-4o",
      preferredLlmRoute: "",
      runtimeMode: "local",
      localRuntimeBaseUrl: "",
      remoteRuntimeBaseUrl: "",
      maxToolRounds: 40,
    });
    mockStudioApi.getUserConfigModels.mockResolvedValue({
      providers: [
        {
          providerSlug: "openai",
          providerName: "OpenAI Team Service",
          proxyUrl: "https://nyx.example/openai",
          source: "user_service",
          status: "ready",
        },
        {
          providerSlug: "anthropic",
          providerName: "Anthropic Lab Service",
          proxyUrl: "https://nyx.example/anthropic",
          source: "user_service",
          status: "ready",
        },
      ],
      gatewayUrl: "https://nyx.example/gateway",
      modelsByProvider: {
        openai: ["gpt-4o", "gpt-4o-mini"],
        anthropic: ["claude-3-5-sonnet", "claude-3-opus"],
      },
      supportedModels: [
        "gpt-4o",
        "gpt-4o-mini",
        "claude-3-5-sonnet",
        "claude-3-opus",
      ],
    });
  });

  afterEach(() => {
    cleanupTestQueryClients();
  });

  it("renders the full-body LLM tab by default", async () => {
    renderWithQueryClient(React.createElement(SettingsPage));

    expect(await screen.findByText("Settings")).toBeTruthy();
    expect(await screen.findByText("Edit defaults")).toBeTruthy();
    expect(screen.getByText("How defaults work")).toBeTruthy();
    expect(screen.getByText("Technical preview")).toBeTruthy();
    expect(screen.getAllByText("Effective route").length).toBeGreaterThan(0);
    expect(screen.getByText("Connected providers")).toBeTruthy();
    expect(screen.getAllByText("OpenAI Team Service").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Default model").length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Save config" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Reset" })).toBeDisabled();
  });

  it("switches to the account tab in-place", async () => {
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
        roles: ["admin"],
        groups: ["platform"],
      },
    });

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.click(await screen.findByRole("tab", { name: "Account" }));

    await waitFor(() => {
      expect(window.location.search).toBe("?section=account");
    });
    expect(await screen.findByText("Profile")).toBeTruthy();
    expect(screen.getByText("Ada Lovelace")).toBeTruthy();
    expect(screen.getByText("Authentication")).toBeTruthy();
  });

  it("switches default model choices when preferred route changes", async () => {
    renderWithQueryClient(React.createElement(SettingsPage));

    expect(
      await screen.findByPlaceholderText("Type a model ID for NyxID Gateway"),
    ).toBeTruthy();

    fireEvent.mouseDown(screen.getByLabelText("Preferred route"));
    const anthropicMatches = await screen.findAllByText("Anthropic Lab Service");
    fireEvent.click(anthropicMatches[anthropicMatches.length - 1]!);

    await waitFor(() => {
      expect(
        screen.queryByPlaceholderText("Type a model ID for NyxID Gateway"),
      ).toBeNull();
    });

    fireEvent.mouseDown(screen.getByLabelText("Default model"));

    expect(
      await screen.findByRole("option", { name: "claude-3-5-sonnet" }),
    ).toBeTruthy();
    expect(screen.getByRole("option", { name: "claude-3-opus" })).toBeTruthy();
    expect(
      screen.queryByRole("option", { name: "gpt-4o-mini" }),
    ).toBeNull();
  });
});
