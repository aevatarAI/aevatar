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
      defaultModel: "",
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
          providerName: "OpenAI Gateway",
          proxyUrl: "https://nyx.example/gateway/openai",
          source: "gateway_provider",
          status: "ready",
        },
        {
          providerSlug: "anthropic",
          providerName: "Anthropic Gateway",
          proxyUrl: "https://nyx.example/gateway/anthropic",
          source: "gateway_provider",
          status: "ready",
        },
        {
          providerSlug: "openai-team",
          providerName: "OpenAI Team Service",
          proxyUrl: "https://nyx.example/openai",
          source: "user_service",
          status: "ready",
        },
        {
          providerSlug: "anthropic-team",
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
        "openai-team": ["gpt-4.1-mini"],
        "anthropic-team": ["claude-3-haiku"],
      },
      supportedModels: [
        "gpt-4o",
        "gpt-4o-mini",
        "claude-3-5-sonnet",
        "claude-3-opus",
        "gpt-4.1-mini",
        "claude-3-haiku",
      ],
    });
    mockStudioApi.saveUserConfig.mockImplementation(async (input) => ({
      defaultModel: input.defaultModel,
      preferredLlmRoute: input.preferredLlmRoute ?? "",
      runtimeMode: "local",
      localRuntimeBaseUrl: "",
      remoteRuntimeBaseUrl: "",
      maxToolRounds: 40,
    }));
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
    fireEvent.click(screen.getByRole("button", { name: /Advanced runtime/i }));
    expect(screen.getByDisplayValue("http://127.0.0.1:5080")).toBeTruthy();
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
    expect(screen.queryByRole("button", { name: "Save config" })).toBeNull();
    expect(await screen.findByText("Profile")).toBeTruthy();
    expect(screen.getByText("Ada Lovelace")).toBeTruthy();
    expect(screen.getByText("Authentication")).toBeTruthy();
  });

  it("shows gateway models from every ready gateway provider", async () => {
    renderWithQueryClient(React.createElement(SettingsPage));

    expect(await screen.findByText("4 live")).toBeTruthy();
  });

  it("uses route-scoped model choices when the preferred route is a service", async () => {
    mockStudioApi.getUserConfig.mockResolvedValueOnce({
      defaultModel: "",
      preferredLlmRoute: "/api/v1/proxy/s/anthropic-team",
      runtimeMode: "local",
      localRuntimeBaseUrl: "",
      remoteRuntimeBaseUrl: "",
      maxToolRounds: 40,
    });

    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() => {
      expect(screen.getByText("1 live")).toBeTruthy();
    });
  });

  it("does not relabel the global supported model union as a service-specific catalog", async () => {
    mockStudioApi.getUserConfig.mockResolvedValueOnce({
      defaultModel: "",
      preferredLlmRoute: "/api/v1/proxy/s/anthropic-team",
      runtimeMode: "local",
      localRuntimeBaseUrl: "",
      remoteRuntimeBaseUrl: "",
      maxToolRounds: 40,
    });
    mockStudioApi.getUserConfigModels.mockResolvedValueOnce({
      providers: [
        {
          providerSlug: "openai",
          providerName: "OpenAI Gateway",
          proxyUrl: "https://nyx.example/gateway/openai",
          source: "gateway_provider",
          status: "ready",
        },
        {
          providerSlug: "anthropic-team",
          providerName: "Anthropic Lab Service",
          proxyUrl: "https://nyx.example/anthropic",
          source: "user_service",
          status: "ready",
        },
      ],
      gatewayUrl: "https://nyx.example/gateway",
      modelsByProvider: {
        openai: ["gpt-4o", "gpt-4o-mini"],
      },
      supportedModels: ["gpt-4o", "gpt-4o-mini", "claude-3-haiku"],
    });

    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() => {
      expect(screen.getByPlaceholderText("Type a model ID for Anthropic Lab Service")).toBeTruthy();
    });

    expect(screen.queryByRole("combobox", { name: "Default model" })).toBeNull();
  });

  it("saves only the editable LLM fields", async () => {
    mockStudioApi.getUserConfigModels.mockResolvedValueOnce({
      providers: [],
      gatewayUrl: "",
      modelsByProvider: {},
      supportedModels: [],
    });

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.change(await screen.findByLabelText("Default model"), {
      target: { value: "gpt-4o" },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Save config" }));

    await waitFor(() => {
      expect(mockStudioApi.saveUserConfig).toHaveBeenCalledWith({
        defaultModel: "gpt-4o",
        preferredLlmRoute: "",
      });
    });
  });
});
