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
    getAuthSession: jest.fn(),
    getUserConfigRuntime: jest.fn(),
    getUserLlmSettings: jest.fn(),
    saveUserLlmSettings: jest.fn(),
  },
}));

const { studioApi: mockStudioApi } = jest.requireMock(
  "@/shared/studio/api",
) as {
  studioApi: {
    getAuthSession: jest.Mock;
    getUserConfigRuntime: jest.Mock;
    getUserLlmSettings: jest.Mock;
    saveUserLlmSettings: jest.Mock;
  };
};

function createLlmSettings(overrides: Record<string, unknown> = {}) {
  return {
    savedRoute: "",
    savedRouteLabel: "NyxID Gateway",
    effectiveRoute: "",
    effectiveRouteLabel: "NyxID Gateway",
    routeFallbackActive: false,
    fallbackReason: null,
    catalogStatus: "ready",
    defaultModel: "",
    capabilities: {
      canEditRoute: true,
      canEditModel: true,
      canSave: true,
      canRetryCatalog: false,
    },
    routeOptions: [
      {
        routeValue: "",
        label: "NyxID Gateway",
        source: "gateway_provider",
        status: "ready",
        allowed: true,
        ready: true,
        serviceId: null,
        serviceSlug: null,
        description: null,
      },
      {
        routeValue: "/api/v1/proxy/s/openai-team",
        label: "OpenAI Team Service",
        source: "user_service",
        status: "ready",
        allowed: true,
        ready: true,
        serviceId: "svc-openai",
        serviceSlug: "openai-team",
        description: null,
      },
      {
        routeValue: "/api/v1/proxy/s/anthropic-team",
        label: "Anthropic Lab Service",
        source: "user_service",
        status: "ready",
        allowed: true,
        ready: true,
        serviceId: "svc-anthropic",
        serviceSlug: "anthropic-team",
        description: null,
      },
    ],
    modelGroupsByRoute: [
      {
        routeValue: "",
        groupId: "openai",
        label: "OpenAI Gateway",
        models: ["gpt-4o", "gpt-4o-mini"],
      },
      {
        routeValue: "",
        groupId: "anthropic",
        label: "Anthropic Gateway",
        models: ["claude-3-5-sonnet", "claude-3-opus"],
      },
      {
        routeValue: "/api/v1/proxy/s/openai-team",
        groupId: "openai-team",
        label: "OpenAI Team Service",
        models: ["gpt-4.1-mini"],
      },
      {
        routeValue: "/api/v1/proxy/s/anthropic-team",
        groupId: "anthropic-team",
        label: "Anthropic Lab Service",
        models: ["claude-3-haiku"],
      },
    ],
    ...overrides,
  };
}

describe("SettingsPage", () => {
  beforeEach(() => {
    window.localStorage.clear();
    window.history.replaceState({}, "", "/settings");
    jest.clearAllMocks();

    mockStudioApi.getAuthSession.mockResolvedValue({
      enabled: true,
      authenticated: true,
      providerDisplayName: "NyxID",
      profile: null,
      session: {
        authenticated: true,
        providerDisplayName: "NyxID",
      },
    });
    mockStudioApi.getUserLlmSettings.mockResolvedValue(createLlmSettings());
    mockStudioApi.getUserConfigRuntime.mockResolvedValue({
      runtimeMode: "local",
      activeRuntimeBaseUrl: "http://127.0.0.1:5080",
      localRuntimeBaseUrl: "http://127.0.0.1:5080",
      remoteRuntimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
      runtimeDefaults: {
        localRuntimeBaseUrl: "http://127.0.0.1:5080",
        remoteRuntimeBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
        localMode: "local",
        remoteMode: "remote",
      },
    });
    mockStudioApi.saveUserLlmSettings.mockImplementation(async (input) =>
      createLlmSettings({
        savedRoute: input.routeValue,
        effectiveRoute: input.routeValue,
        defaultModel: input.model ?? "",
      })
    );
  });

  afterEach(() => {
    cleanupTestQueryClients();
  });

  it("renders the full-body LLM tab by default", async () => {
    renderWithQueryClient(React.createElement(SettingsPage));

    expect(await screen.findByText("设置")).toBeTruthy();
    expect(await screen.findByText("编辑默认配置")).toBeTruthy();
    expect(screen.getByText("默认配置如何生效")).toBeTruthy();
    expect(screen.getByText("技术预览")).toBeTruthy();
    expect(screen.getAllByText("当前生效路由").length).toBeGreaterThan(0);
    expect(screen.getByText("已连接 Provider")).toBeTruthy();
    expect(screen.getAllByText("OpenAI Team Service").length).toBeGreaterThan(0);
    expect(screen.getAllByText("默认模型").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: /高级运行时/i }));
    expect(screen.getByDisplayValue("http://127.0.0.1:5080")).toBeTruthy();
    expect(screen.getByRole("button", { name: "保存配置" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "重置" })).toBeDisabled();
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
    expect(screen.queryByRole("button", { name: "保存配置" })).toBeNull();
    expect(await screen.findByText("Profile")).toBeTruthy();
    expect(screen.getByText("Ada Lovelace")).toBeTruthy();
    expect(screen.getByText("Authentication")).toBeTruthy();
  });

  it("shows gateway models from backend model groups", async () => {
    renderWithQueryClient(React.createElement(SettingsPage));

    expect(await screen.findByText("4 个可用")).toBeTruthy();
  });

  it("uses backend route-scoped model choices when the saved route is a service", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedRoute: "/api/v1/proxy/s/anthropic-team",
        savedRouteLabel: "Anthropic Lab Service",
        effectiveRoute: "/api/v1/proxy/s/anthropic-team",
        effectiveRouteLabel: "Anthropic Lab Service",
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() => {
      expect(screen.getByText("1 个可用")).toBeTruthy();
    });
  });

  it("does not invent service-specific model choices when backend sends no route group", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        savedRoute: "/api/v1/proxy/s/anthropic-team",
        savedRouteLabel: "Anthropic Lab Service",
        effectiveRoute: "/api/v1/proxy/s/anthropic-team",
        effectiveRouteLabel: "Anthropic Lab Service",
        modelGroupsByRoute: [
          {
            routeValue: "",
            groupId: "openai",
            label: "OpenAI Gateway",
            models: ["gpt-4o", "gpt-4o-mini"],
          },
        ],
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    await waitFor(() => {
      expect(screen.getByPlaceholderText("输入 Anthropic Lab Service 的模型 ID")).toBeTruthy();
    });

    expect(screen.queryByRole("combobox", { name: "默认模型" })).toBeNull();
  });

  it("saves canonical LLM settings", async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValueOnce(
      createLlmSettings({
        modelGroupsByRoute: [],
      })
    );

    renderWithQueryClient(React.createElement(SettingsPage));

    fireEvent.change(await screen.findByLabelText("默认模型"), {
      target: { value: "gpt-4o" },
    });

    fireEvent.click(await screen.findByRole("button", { name: "保存配置" }));

    await waitFor(() => {
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledWith({
        model: "gpt-4o",
        routeValue: "",
      });
    });
  });
});
