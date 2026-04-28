import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { AGUIEventType, CustomEventName } from "@aevatar-react-sdk/types";
import { loadDraftRunPayload } from "@/shared/runs/draftRunSession";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import ChatPage from "./index";

async function openToolsMenu(): Promise<void> {
  fireEvent.click(await screen.findByRole("button", { name: "Tools" }));
}

async function openAdvancedConsole(): Promise<void> {
  await openToolsMenu();
  fireEvent.click(await screen.findByRole("menuitem", { name: "Advanced Console" }));
}

async function openEventStream(): Promise<void> {
  await openToolsMenu();
  fireEvent.click(await screen.findByRole("menuitem", { name: "Event Stream" }));
}

jest.mock("@/shared/ui/aevatarPageShells", () => {
  const mockReact = require("react");

  return {
    AevatarContextDrawer: ({ children, open, title }: any) =>
      open
        ? mockReact.createElement(
            "section",
            null,
            title ? mockReact.createElement("h2", null, title) : null,
            children
          )
        : null,
    AevatarPageShell: ({ children, title }: any) =>
      mockReact.createElement(
        "section",
        null,
        title ? mockReact.createElement("h1", null, title) : null,
        children
      ),
  };
});

jest.mock("@/shared/studio/api", () => ({
  studioApi: (() => {
    const providerTypes = [
      {
        category: "llm",
        defaultEndpoint: "https://api.openai.com/v1",
        defaultModel: "gpt-5.4-mini",
        description: "OpenAI platform",
        displayName: "OpenAI",
        id: "openai",
        recommended: true,
      },
      {
        category: "llm",
        defaultEndpoint: "https://api.anthropic.com",
        defaultModel: "claude-sonnet-4-5",
        description: "Anthropic Claude",
        displayName: "Anthropic",
        id: "anthropic",
        recommended: false,
      },
    ];

    return {
      bindScopeGAgent: jest.fn(async () => ({
        displayName: "NyxID Chat",
        revisionId: "rev-nyx",
        scopeId: "scope-a",
        serviceId: "nyxid-chat",
        targetKind: "gagent",
        targetName: "NyxID Chat",
      })),
      getAuthSession: jest.fn(async () => ({
        enabled: true,
        scopeId: "scope-a",
        scopeSource: "nyxid",
      })),
      getScopeBinding: jest.fn(async () => ({
        available: true,
        scopeId: "scope-a",
        serviceId: "support-service",
      })),
      getDefaultRouteTarget: jest.fn(async () => ({
        available: true,
        scopeId: "scope-a",
        serviceId: "support-service",
      })),
      getUserConfig: jest.fn(async () => ({
        defaultModel: "",
        preferredLlmRoute: "",
        runtimeBaseUrl: "https://runtime.example.test",
      })),
      getUserConfigModels: jest.fn(async () => ({
        gatewayUrl: "https://nyx-gateway.example.test",
        modelsByProvider: {
          openai: ["gpt-5.4-mini", "gpt-4.1-mini"],
        },
        providers: [
          {
            providerName: "OpenAI",
            providerSlug: "openai",
            proxyUrl: "https://nyx-api.example/openai",
            source: "user_service",
            status: "ready",
          },
        ],
        supportedModels: ["gpt-5.4-mini", "gpt-4.1-mini"],
      })),
      getSettings: jest.fn(async () => ({
        defaultProviderName: "openai-1",
        providerTypes,
        providers: [
          {
            apiKey: "",
            apiKeyConfigured: true,
            category: "llm",
            description: "OpenAI platform",
            displayName: "OpenAI",
            endpoint: "https://api.openai.com/v1",
            model: "gpt-5.4-mini",
            providerName: "openai-1",
            providerType: "openai",
          },
        ],
        runtimeBaseUrl: "https://runtime.example.test",
      })),
      saveSettings: jest.fn(async (input) => ({
        defaultProviderName: input.defaultProviderName || input.providers?.[0]?.providerName || "",
        providerTypes,
        providers: (input.providers || []).map((provider: any) => {
          const providerType =
            providerTypes.find((item) => item.id === provider.providerType) ||
            providerTypes[0];
          return {
            apiKey: "",
            apiKeyConfigured: Boolean(provider.apiKey),
            category: providerType.category,
            description: providerType.description,
            displayName: providerType.displayName,
            endpoint: provider.endpoint || providerType.defaultEndpoint,
            model: provider.model || providerType.defaultModel,
            providerName: provider.providerName,
            providerType: provider.providerType,
          };
        }),
        runtimeBaseUrl: input.runtimeBaseUrl || "https://runtime.example.test",
      })),
    };
  })(),
}));

jest.mock("@/shared/api/servicesApi", () => ({
  servicesApi: {
    listServices: jest.fn(async () => [
      {
        activeServingRevisionId: "rev-1",
        appId: "default",
        defaultServingRevisionId: "rev-1",
        deploymentId: "deploy-1",
        deploymentStatus: "Active",
        displayName: "Support service",
        endpoints: [
          {
            description: "Streaming support chat",
            displayName: "Chat",
            endpointId: "chat",
            kind: "chat",
            requestTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
            responseTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
          },
          {
            description: "Ask for structured help",
            displayName: "Assist",
            endpointId: "assist",
            kind: "command",
            requestTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
            responseTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
          },
        ],
        namespace: "default",
        policyIds: [],
        primaryActorId: "actor://support",
        serviceId: "support-service",
        serviceKey: "scope-a:default:default:support-service",
        tenantId: "scope-a",
        updatedAt: "2026-04-01T09:00:00Z",
      },
    ]),
  },
}));

jest.mock("@/shared/api/runtimeRunsApi", () => ({
  runtimeRunsApi: {
    invokeEndpoint: jest.fn(async () => ({
      commandId: "cmd-execute",
      requestId: "run-execute-command",
      targetActorId: "actor://support-command",
    })),
    resume: jest.fn(async () => ({
      accepted: true,
      actorId: "actor://support",
      commandId: "cmd-resume",
      runId: "run-1",
      stepId: "triage_input",
    })),
    signal: jest.fn(async () => ({
      accepted: true,
      actorId: "actor://support",
      commandId: "cmd-signal",
      runId: "run-1",
      signalName: "deployment_ready",
      stepId: "wait_for_signal",
    })),
    streamChat: jest.fn(async () => ({
      body: {},
      ok: true,
      streamKind: "chat",
    })),
    streamEndpoint: jest.fn(async () => ({
      body: {},
      ok: true,
      streamKind: "execute",
    })),
  },
}));

jest.mock("@/shared/api/runtimeActorsApi", () => ({
  runtimeActorsApi: {
    getActorGraphEnriched: jest.fn(async (actorId: string) => ({
      snapshot: {
        actorId: actorId || "actor://support",
        completedSteps: 3,
        completionStatusValue: 1,
        lastCommandId: "cmd-1",
        lastError: "",
        lastEventId: "evt-1",
        lastOutput: "done",
        lastSuccess: true,
        lastUpdatedAt: "2026-04-01T09:00:00Z",
        requestedSteps: 3,
        roleReplyCount: 1,
        stateVersion: 7,
        totalSteps: 3,
        workflowName: "support",
      },
      subgraph: {
        edges: [
          {
            edgeId: "edge-1",
            edgeType: "invokes",
            fromNodeId: actorId || "actor://support",
            properties: {},
            toNodeId: "actor://support-worker",
            updatedAt: "2026-04-01T09:00:00Z",
          },
        ],
        nodes: [
          {
            nodeId: actorId || "actor://support",
            nodeType: "WorkflowAgent",
            properties: {},
            updatedAt: "2026-04-01T09:00:00Z",
          },
          {
            nodeId: "actor://support-worker",
            nodeType: "WorkerAgent",
            properties: {},
            updatedAt: "2026-04-01T09:00:00Z",
          },
        ],
        rootNodeId: actorId || "actor://support",
      },
    })),
    getActorSnapshot: jest.fn(async (actorId: string) => ({
      actorId: actorId || "actor://support",
      completedSteps: 3,
      completionStatusValue: 1,
      lastCommandId: "cmd-1",
      lastError: "",
      lastEventId: "evt-1",
      lastOutput: "done",
      lastSuccess: true,
      lastUpdatedAt: "2026-04-01T09:00:00Z",
      requestedSteps: 3,
      roleReplyCount: 1,
      stateVersion: 7,
      totalSteps: 3,
      workflowName: "support",
    })),
    getActorTimeline: jest.fn(async () => [
      {
        agentId: "actor://support-command",
        data: {
          prompt: "Provide incident severity before continuing.",
          suspension_type: "human_input",
          timeout_seconds: "120",
        },
        eventType: "workflow_suspended",
        message: "Waiting for operator input",
        stage: "workflow.suspended",
        stepId: "assist_step",
        stepType: "llm_call",
        timestamp: "2026-04-01T09:00:06Z",
      },
      {
        agentId: "actor://support-command",
        data: {
          duration_ms: "3500",
        },
        eventType: "step_completed",
        message: "Step completed successfully",
        stage: "step.completed",
        stepId: "assist_step",
        stepType: "llm_call",
        timestamp: "2026-04-01T09:00:07Z",
      },
    ]),
  },
}));

jest.mock("@/shared/api/scopesApi", () => ({
  scopesApi: {
    listWorkflows: jest.fn(async () => [
      {
        activeRevisionId: "rev-workflow",
        actorId: "actor://workflow",
        deploymentId: "deploy-workflow",
        deploymentStatus: "Active",
        displayName: "Support workflow",
        scopeId: "scope-a",
        serviceKey: "scope-a:default:default:support-service",
        updatedAt: "2026-04-01T09:00:00Z",
        workflowId: "workflow-1",
        workflowName: "SupportWorkflow",
      },
    ]),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    getServiceRunAudit: jest.fn(async () => ({
      summary: {
        actorId: "actor://support-command",
        bindingUpdatedAt: "2026-04-01T09:00:00Z",
        boundAt: "2026-04-01T09:00:00Z",
        completedSteps: 3,
        completionStatus: "completed",
        definitionActorId: "definition://support",
        deploymentId: "deploy-1",
        lastError: "",
        lastEventId: "evt-1",
        lastOutput: "Structured help is ready.",
        lastSuccess: true,
        lastUpdatedAt: "2026-04-01T09:00:10Z",
        revisionId: "rev-1",
        roleReplyCount: 2,
        runId: "run-execute-command",
        scopeId: "scope-a",
        serviceId: "support-service",
        stateVersion: 7,
        totalSteps: 3,
        workflowName: "support_flow",
      },
      audit: {
        commandId: "cmd-execute",
        completionStatus: "completed",
        createdAt: "2026-04-01T09:00:00Z",
        durationMs: 8200,
        endedAt: "2026-04-01T09:00:10Z",
        finalError: "",
        finalOutput: "Structured help is ready.",
        input: "Need structured help.",
        lastEventId: "evt-1",
        projectionScope: "actor_shared",
        reportVersion: "1.0",
        roleReplies: [
          {
            content: "Reply one",
            contentLength: 9,
            roleId: "support",
            sessionId: "session-1",
            timestamp: "2026-04-01T09:00:05Z",
          },
        ],
        rootActorId: "actor://support-command",
        startedAt: "2026-04-01T09:00:02Z",
        stateVersion: 7,
        steps: [
          {
            assignedValue: "",
            assignedVariable: "",
            branchKey: "",
            completedAt: "2026-04-01T09:00:07Z",
            completionAnnotations: {},
            durationMs: 3500,
            error: "",
            nextStepId: "",
            outputPreview: "done",
            requestParameters: {},
            requestedAt: "2026-04-01T09:00:04Z",
            requestedVariableName: "",
            stepId: "assist_step",
            stepType: "llm_call",
            success: true,
            suspensionPrompt: "",
            suspensionTimeoutSeconds: null,
            suspensionType: "",
            targetRole: "support",
            workerId: "worker-1",
          },
        ],
        success: true,
        summary: {
          completedSteps: 3,
          requestedSteps: 3,
          roleReplyCount: 2,
          stepTypeCounts: {
            llm_call: 3,
          },
          totalSteps: 3,
        },
        timeline: [
          {
            agentId: "support",
            data: {},
            eventType: "requested",
            message: "Asked support agent",
            stage: "step_requested",
            stepId: "assist_step",
            stepType: "llm_call",
            timestamp: "2026-04-01T09:00:04Z",
          },
        ],
        topology: [],
        topologySource: "runtime_snapshot",
        updatedAt: "2026-04-01T09:00:10Z",
        workflowName: "support_flow",
      },
    })),
  },
}));

jest.mock("@/shared/api/nyxIdChatApi", () => ({
  nyxIdChatApi: {
    approveToolCall: jest.fn(async () => ({
      body: {},
      ok: true,
      streamKind: "approval",
    })),
  },
}));

jest.mock("@/shared/agui/sseFrameNormalizer", () => ({
  parseBackendSSEStream: jest.fn(),
}));

jest.mock("./chatHistoryApi", () => ({
  chatHistoryApi: {
    deleteConversation: jest.fn(async () => undefined),
    listConversationMetas: jest.fn(async () => []),
    loadConversation: jest.fn(async () => []),
    saveConversation: jest.fn(async () => undefined),
  },
}));

import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { nyxIdChatApi } from "@/shared/api/nyxIdChatApi";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { studioApi } from "@/shared/studio/api";
import { chatHistoryApi } from "./chatHistoryApi";

function createChatEventStream() {
  return (async function* () {
    yield {
      runId: "run-1",
      threadId: "thread-1",
      timestamp: 1,
      type: AGUIEventType.RUN_STARTED,
    };
    yield {
      name: CustomEventName.RunContext,
      timestamp: 2,
      type: AGUIEventType.CUSTOM,
      value: {
        actorId: "actor://support",
        commandId: "cmd-1",
      },
    };
    yield {
      name: "aevatar.llm.reasoning",
      timestamp: 3,
      type: AGUIEventType.CUSTOM,
      value: {
        delta: "Inspecting the request",
      },
    };
    yield {
      stepName: "lookup_context",
      timestamp: 4,
      type: AGUIEventType.STEP_STARTED,
    };
    yield {
      timestamp: 5,
      toolCallId: "tool-1",
      toolName: "knowledge.search",
      type: AGUIEventType.TOOL_CALL_START,
    };
    yield {
      result: "3 matches",
      timestamp: 6,
      toolCallId: "tool-1",
      toolName: "knowledge.search",
      type: AGUIEventType.TOOL_CALL_END,
    };
    yield {
      delta: "Hello from the migrated chat.",
      messageId: "msg-1",
      timestamp: 7,
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
    };
    yield {
      stepName: "lookup_context",
      timestamp: 8,
      type: AGUIEventType.STEP_FINISHED,
    };
  })();
}

function createExecuteEventStream() {
  return (async function* () {
    yield {
      runId: "run-execute",
      threadId: "thread-execute",
      timestamp: 1,
      type: AGUIEventType.RUN_STARTED,
    };
    yield {
      delta: "Executed through endpoint stream.",
      messageId: "msg-execute",
      timestamp: 2,
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
    };
  })();
}

function createApprovalRequestEventStream() {
  return (async function* () {
    yield {
      actorId: "actor://nyxid",
      timestamp: 1,
      type: AGUIEventType.RUN_STARTED,
    };
    yield {
      argumentsJson: "{\"scopeId\":\"scope-a\"}",
      isDestructive: false,
      requestId: "approval-1",
      timestamp: 2,
      timeoutSeconds: 30,
      toolCallId: "tool-approval-1",
      toolName: "scope.bind",
      type: "TOOL_APPROVAL_REQUEST",
    };
  })();
}

function createApprovalContinuationEventStream() {
  return (async function* () {
    yield {
      delta: "Approval applied successfully.",
      messageId: "msg-approval",
      timestamp: 3,
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
    };
  })();
}

function createHumanInputRequestEventStream() {
  return (async function* () {
    yield {
      runId: "run-1",
      threadId: "thread-1",
      timestamp: 1,
      type: AGUIEventType.RUN_STARTED,
    };
    yield {
      name: CustomEventName.RunContext,
      timestamp: 2,
      type: AGUIEventType.CUSTOM,
      value: {
        actorId: "actor://support",
        commandId: "cmd-1",
      },
    };
    yield {
      delta: "I need the incident severity before I can continue.",
      messageId: "msg-human-input",
      timestamp: 3,
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
    };
    yield {
      metadata: {
        variableName: "severity",
      },
      prompt: "Provide the incident severity.",
      runId: "run-1",
      stepId: "triage_input",
      suspensionType: "human_input",
      timeoutSeconds: 120,
      timestamp: 4,
      type: AGUIEventType.HUMAN_INPUT_REQUEST,
    };
  })();
}

function createHumanApprovalRequestEventStream() {
  return (async function* () {
    yield {
      runId: "run-1",
      threadId: "thread-1",
      timestamp: 1,
      type: AGUIEventType.RUN_STARTED,
    };
    yield {
      name: CustomEventName.RunContext,
      timestamp: 2,
      type: AGUIEventType.CUSTOM,
      value: {
        actorId: "actor://support",
        commandId: "cmd-1",
      },
    };
    yield {
      delta: "Release summary is ready for review.",
      messageId: "msg-human-approval",
      timestamp: 3,
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
    };
    yield {
      prompt: "Approve this release gate?",
      runId: "run-1",
      stepId: "release_gate",
      suspensionType: "human_approval",
      timeoutSeconds: 300,
      timestamp: 4,
      type: AGUIEventType.HUMAN_INPUT_REQUEST,
    };
  })();
}

function createWaitingSignalEventStream() {
  return (async function* () {
    yield {
      runId: "run-1",
      threadId: "thread-1",
      timestamp: 1,
      type: AGUIEventType.RUN_STARTED,
    };
    yield {
      name: CustomEventName.RunContext,
      timestamp: 2,
      type: AGUIEventType.CUSTOM,
      value: {
        actorId: "actor://support",
        commandId: "cmd-1",
      },
    };
    yield {
      delta: "Waiting for deployment_ready before finishing the run.",
      messageId: "msg-signal",
      timestamp: 3,
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
    };
    yield {
      name: CustomEventName.WaitingSignal,
      timestamp: 4,
      type: AGUIEventType.CUSTOM,
      value: {
        prompt: "Send deployment_ready once the environment is green.",
        runId: "run-1",
        signalName: "deployment_ready",
        stepId: "wait_for_signal",
        timeoutMs: 60000,
      },
    };
  })();
}

describe("ChatPage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.history.pushState({}, "", "/chat");
    window.scrollTo = jest.fn();
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([]);
    (parseBackendSSEStream as jest.Mock).mockImplementation(
      (response: { streamKind?: string }) => {
        switch (response?.streamKind) {
          case "approval":
            return createApprovalContinuationEventStream();
          case "approval-request":
            return createApprovalRequestEventStream();
          case "human-approval-request":
            return createHumanApprovalRequestEventStream();
          case "human-input-request":
            return createHumanInputRequestEventStream();
          case "wait-signal-request":
            return createWaitingSignalEventStream();
          case "execute":
            return createExecuteEventStream();
          default:
            return createChatEventStream();
        }
      }
    );
  });

  it("renders chat as a scoped page and streams the assistant output", async () => {
    const { container } = renderWithQueryClient(React.createElement(ChatPage));

    expect(container.textContent).toContain("Console");
    expect(container.textContent).toContain("History");
    expect(container.textContent).toContain("New Chat");
    expect(container.textContent).toContain("Chat");
    expect(container.textContent).toContain("Tools");

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });
    expect(screen.getByAltText("NyxID")).toBeTruthy();

    const promptInput = await screen.findByPlaceholderText(
      "Send a message..."
    );
    fireEvent.change(promptInput, {
      target: { value: "Help me draft the next response." },
    });
    const sendButton = await screen.findByLabelText("Send");
    fireEvent.click(sendButton);

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          prompt: "Help me draft the next response.",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "support-service",
        }
      );
    });
    expect(await screen.findByText("Hello from the migrated chat.")).toBeTruthy();
    await waitFor(() => {
      expect(chatHistoryApi.saveConversation).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          runId: "run-1",
          serviceId: "support-service",
          title: "Help me draft the next response.",
        }),
        expect.any(Array)
      );
    });
    fireEvent.click(screen.getByRole("button", { name: "Thinking" }));
    fireEvent.click(screen.getByRole("button", { name: /2 actions/i }));
    expect(screen.getByText("Inspecting the request")).toBeTruthy();
    expect(screen.getByText("lookup_context")).toBeTruthy();
    expect(screen.getByText("knowledge.search")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Runtime details" }));
    expect(screen.getByText("run-1")).toBeTruthy();
    expect(screen.getByText("actor://support")).toBeTruthy();
  });

  it("binds and uses the built-in NyxID chat service", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    fireEvent.click(serviceSelector);
    fireEvent.click(await screen.findByRole("option", { name: /NyxID Chat/i }));

    const promptInput = await screen.findByPlaceholderText(
      "Send a message..."
    );
    fireEvent.change(promptInput, {
      target: { value: "Help me inspect my service binding." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    await waitFor(() => {
      expect(studioApi.bindScopeGAgent).toHaveBeenCalledWith(
        expect.objectContaining({
          actorTypeName: "Aevatar.GAgents.NyxidChat.NyxIdChatGAgent",
          scopeId: "scope-a",
          serviceId: "nyxid-chat",
        })
      );
    });

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          prompt: "Help me inspect my service binding.",
          sessionId: expect.any(String),
        }),
        expect.any(AbortSignal),
        {
          serviceId: "nyxid-chat",
        }
      );
    });
  });

  it("matches the CLI composer footer and sends route/model overrides", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    expect(
      await screen.findByRole("button", { name: "Conversation model settings" })
    ).toHaveTextContent("Provider default");
    expect(screen.getAllByText("NyxID Gateway").length).toBeGreaterThan(0);

    fireEvent.click(
      await screen.findByRole("button", { name: "Conversation model settings" })
    );
    fireEvent.change(await screen.findByLabelText("Conversation route"), {
      target: { value: "/api/v1/proxy/s/openai" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "gpt-5.4-mini" }));

    fireEvent.change(await screen.findByPlaceholderText("Send a message..."), {
      target: { value: "Use the OpenAI route override." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          metadata: {
            "aevatar.model_override": "gpt-5.4-mini",
            "nyxid.route_preference": "/api/v1/proxy/s/openai",
          },
          prompt: "Use the OpenAI route override.",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "support-service",
        }
      );
    });

    await waitFor(() => {
      expect(chatHistoryApi.saveConversation).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          llmModel: "gpt-5.4-mini",
          llmRoute: "/api/v1/proxy/s/openai",
        }),
        expect.any(Array)
      );
    });
  });

  it("guides onboarding through provider setup and saves the provider via Studio Settings", async () => {
    (studioApi.getSettings as jest.Mock).mockResolvedValueOnce({
      defaultProviderName: "",
      providerTypes: [
        {
          category: "llm",
          defaultEndpoint: "https://api.openai.com/v1",
          defaultModel: "gpt-5.4-mini",
          description: "OpenAI platform",
          displayName: "OpenAI",
          id: "openai",
          recommended: true,
        },
      ],
      providers: [],
      runtimeBaseUrl: "https://runtime.example.test",
    });

    renderWithQueryClient(React.createElement(ChatPage));

    fireEvent.click(await screen.findByLabelText("Chat service"));
    fireEvent.click(await screen.findByRole("option", { name: /Onboarding/i }));

    expect(
      await screen.findByText(/Connect a provider for NyxID Chat/i)
    ).toBeTruthy();
    expect(await screen.findByRole("button", { name: "OpenAI" })).toBeTruthy();

    fireEvent.click(await screen.findByRole("button", { name: "OpenAI" }));

    expect(await screen.findByRole("button", { name: "Use default endpoint" })).toBeTruthy();

    fireEvent.click(await screen.findByRole("button", { name: "Use default endpoint" }));

    expect(await screen.findByLabelText("Onboarding API key")).toBeTruthy();

    fireEvent.change(await screen.findByLabelText("Onboarding API key"), {
      target: { value: "sk-test-secret-key" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "Save provider" }));

    await waitFor(() => {
      expect(studioApi.saveSettings).toHaveBeenCalledWith(
        expect.objectContaining({
          defaultProviderName: "openai-1",
          providers: [
            expect.objectContaining({
              apiKey: "sk-test-secret-key",
              endpoint: "https://api.openai.com/v1",
              model: "gpt-5.4-mini",
              providerName: "openai-1",
              providerType: "openai",
            }),
          ],
        })
      );
    });

    expect(await screen.findByText(/Connected! Saved OpenAI as/i)).toBeTruthy();
    expect(screen.getByText(/API key provided \(••••-key\)/i)).toBeTruthy();
    expect(screen.queryByText("sk-test-secret-key")).toBeNull();
  });

  it("keeps the onboarding composer available for typed replies", async () => {
    (studioApi.getSettings as jest.Mock).mockResolvedValueOnce({
      defaultProviderName: "",
      providerTypes: [
        {
          category: "llm",
          defaultEndpoint: "https://api.openai.com/v1",
          defaultModel: "gpt-5.4-mini",
          description: "OpenAI platform",
          displayName: "OpenAI",
          id: "openai",
          recommended: true,
        },
      ],
      providers: [],
      runtimeBaseUrl: "https://runtime.example.test",
    });

    renderWithQueryClient(React.createElement(ChatPage));

    fireEvent.click(await screen.findByLabelText("Chat service"));
    fireEvent.click(await screen.findByRole("option", { name: /Onboarding/i }));

    const onboardingInput = await screen.findByPlaceholderText(
      "Reply with a provider number, like 1."
    );
    fireEvent.change(onboardingInput, {
      target: { value: "1" },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    expect(
      await screen.findByText(/Choose how to connect the provider/i)
    ).toBeTruthy();
    expect(
      await screen.findByPlaceholderText("Reply with 1 for default or 2 for custom.")
    ).toBeTruthy();
  });

  it("shows a start onboarding action in the NyxID Chat empty state", async () => {
    window.history.pushState({}, "", "/chat?serviceId=nyxid-chat");
    (studioApi.getSettings as jest.Mock).mockResolvedValueOnce({
      defaultProviderName: "",
      providerTypes: [
        {
          category: "llm",
          defaultEndpoint: "https://api.openai.com/v1",
          defaultModel: "gpt-5.4-mini",
          description: "OpenAI platform",
          displayName: "OpenAI",
          id: "openai",
          recommended: true,
        },
      ],
      providers: [],
      runtimeBaseUrl: "https://runtime.example.test",
    });

    renderWithQueryClient(React.createElement(ChatPage));

    expect(
      await screen.findByText(
        /Connect a provider and save it to Studio Settings before starting your first NyxID conversation/i
      )
    ).toBeTruthy();

    fireEvent.click(
      await screen.findByRole("button", { name: /Start onboarding/i })
    );

    expect(
      await screen.findByText(/Connect a provider for NyxID Chat/i)
    ).toBeTruthy();
    expect(await screen.findByRole("button", { name: "OpenAI" })).toBeTruthy();
  });

  it("supports custom endpoints during onboarding", async () => {
    (studioApi.getSettings as jest.Mock).mockResolvedValueOnce({
      defaultProviderName: "",
      providerTypes: [
        {
          category: "llm",
          defaultEndpoint: "https://api.openai.com/v1",
          defaultModel: "gpt-5.4-mini",
          description: "OpenAI platform",
          displayName: "OpenAI",
          id: "openai",
          recommended: true,
        },
      ],
      providers: [],
      runtimeBaseUrl: "https://runtime.example.test",
    });

    renderWithQueryClient(React.createElement(ChatPage));

    fireEvent.click(await screen.findByLabelText("Chat service"));
    fireEvent.click(await screen.findByRole("option", { name: /Onboarding/i }));

    fireEvent.click(await screen.findByRole("button", { name: "OpenAI" }));
    fireEvent.click(await screen.findByRole("button", { name: "Set custom endpoint" }));

    fireEvent.change(await screen.findByLabelText("Onboarding custom endpoint"), {
      target: { value: "https://proxy.example.test/v1" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "Continue" }));

    fireEvent.change(await screen.findByLabelText("Onboarding API key"), {
      target: { value: "sk-custom-endpoint" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "Save provider" }));

    await waitFor(() => {
      expect(studioApi.saveSettings).toHaveBeenCalledWith(
        expect.objectContaining({
          providers: [
            expect.objectContaining({
              endpoint: "https://proxy.example.test/v1",
            }),
          ],
        })
      );
    });
  });

  it("prefers the routed scope and service when Chat opens from Studio", async () => {
    window.history.pushState(
      {},
      "",
      "/chat?scopeId=scope-route&serviceId=nyxid-chat"
    );
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("NyxID Chat");
    });

    const promptInput = await screen.findByPlaceholderText(
      "Send a message..."
    );
    fireEvent.change(promptInput, {
      target: { value: "Route me to the published chat." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    await waitFor(() => {
      expect(studioApi.bindScopeGAgent).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-route",
          serviceId: "nyxid-chat",
        })
      );
    });

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-route",
        expect.objectContaining({
          prompt: "Route me to the published chat.",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "nyxid-chat",
        }
      );
    });
  });

  it("opens the advanced console and queries the current scope binding", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    await openAdvancedConsole();
    expect(await screen.findByText("Advanced Console")).toBeTruthy();

    fireEvent.click(
      await screen.findByRole("button", { name: "Query Scope Binding" })
    );

    expect(
      await screen.findByText((content) =>
        content.includes('"serviceId": "support-service"')
      )
    ).toBeTruthy();
  });

  it("executes a non-chat endpoint from the advanced console", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    await openAdvancedConsole();
    fireEvent.click(await screen.findByRole("button", { name: "Execute" }));

    const serviceSelect = await screen.findByLabelText(
      "Advanced execute service"
    );
    fireEvent.change(serviceSelect, { target: { value: "support-service" } });

    const endpointSelect = await screen.findByLabelText(
      "Advanced execute endpoint"
    );
    fireEvent.change(endpointSelect, { target: { value: "assist" } });
    await screen.findByLabelText("Advanced execute payload type URL");

    const promptInput = await screen.findByLabelText("Advanced execute prompt");
    fireEvent.change(promptInput, {
      target: { value: "Need structured help." },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          endpointId: "assist",
          prompt: "Need structured help.",
        }),
        {
          serviceId: "support-service",
        }
      );
    });

    expect(
      await screen.findByText((content) =>
        content.includes('"targetActorId": "actor://support-command"')
      )
    ).toBeTruthy();
  });

  it("opens runtime runs from the advanced console execution metadata", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    await openAdvancedConsole();
    fireEvent.click(await screen.findByRole("button", { name: "Execute" }));

    fireEvent.change(await screen.findByLabelText("Advanced execute service"), {
      target: { value: "support-service" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute endpoint"), {
      target: { value: "assist" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute prompt"), {
      target: { value: "Need structured help." },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));
    await screen.findByText((content) =>
      content.includes('"targetActorId": "actor://support-command"')
    );

    fireEvent.click(await screen.findByRole("button", { name: "Open Runs" }));

    expect(window.location.pathname).toBe("/runtime/runs");
    const draftKey = new URLSearchParams(window.location.search).get("draftKey");
    expect(draftKey).toBeTruthy();
    expect(loadDraftRunPayload(draftKey)).toEqual(
      expect.objectContaining({
        actorId: "actor://support-command",
        commandId: "cmd-execute",
        endpointId: "assist",
        kind: "observed_run_session",
        prompt: "Need structured help.",
        runId: "run-execute-command",
        scopeId: "scope-a",
        serviceOverrideId: "support-service",
      })
    );
  });

  it("opens runtime explorer from the advanced console execution metadata", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    await openAdvancedConsole();
    fireEvent.click(await screen.findByRole("button", { name: "Execute" }));

    fireEvent.change(await screen.findByLabelText("Advanced execute service"), {
      target: { value: "support-service" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute endpoint"), {
      target: { value: "assist" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute prompt"), {
      target: { value: "Need structured help." },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));
    await screen.findByText((content) =>
      content.includes('"targetActorId": "actor://support-command"')
    );

    fireEvent.click(await screen.findByRole("button", { name: "Open Explorer" }));

    expect(window.location.pathname).toBe("/runtime/explorer/detail");
    const query = new URLSearchParams(window.location.search);
    expect(query.get("actorId")).toBe("actor://support-command");
    expect(query.get("runId")).toBe("run-execute-command");
    expect(query.get("scopeId")).toBe("scope-a");
    expect(query.get("serviceId")).toBe("support-service");
  });

  it("loads run audit details inside the advanced console", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    await openAdvancedConsole();
    fireEvent.click(await screen.findByRole("button", { name: "Execute" }));

    fireEvent.change(await screen.findByLabelText("Advanced execute service"), {
      target: { value: "support-service" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute endpoint"), {
      target: { value: "assist" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute prompt"), {
      target: { value: "Need structured help." },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));
    await screen.findByText((content) =>
      content.includes('"targetActorId": "actor://support-command"')
    );

    fireEvent.click(await screen.findByRole("button", { name: "Load Audit" }));

    await waitFor(() => {
      expect(scopeRuntimeApi.getServiceRunAudit).toHaveBeenCalledWith(
        "scope-a",
        "support-service",
        "run-execute-command",
        {
          actorId: "actor://support-command",
        }
      );
    });

    expect(await screen.findByText("Run Audit")).toBeTruthy();
    expect(screen.getByText("Structured help is ready.")).toBeTruthy();
    expect(screen.getByText("Timeline Highlights")).toBeTruthy();
    expect(screen.getByText("Step Highlights")).toBeTruthy();
    expect(screen.getByText("Reply Highlights")).toBeTruthy();
    expect(screen.getByText("Asked support agent")).toBeTruthy();
    expect(screen.getByText("Reply one")).toBeTruthy();
  });

  it("shows actor timeline context inside the advanced console", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    await openAdvancedConsole();
    fireEvent.click(await screen.findByRole("button", { name: "Execute" }));

    fireEvent.change(await screen.findByLabelText("Advanced execute service"), {
      target: { value: "support-service" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute endpoint"), {
      target: { value: "assist" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute prompt"), {
      target: { value: "Need structured help." },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));
    await screen.findByText((content) =>
      content.includes('"targetActorId": "actor://support-command"')
    );

    fireEvent.click(await screen.findByRole("button", { name: "Timeline" }));

    await waitFor(() => {
      expect(runtimeActorsApi.getActorSnapshot).toHaveBeenCalledWith(
        "actor://support-command"
      );
      expect(runtimeActorsApi.getActorTimeline).toHaveBeenCalledWith(
        "actor://support-command",
        {
          take: 40,
        }
      );
      expect(runtimeActorsApi.getActorGraphEnriched).toHaveBeenCalledWith(
        "actor://support-command",
        {
          depth: 2,
          take: 40,
        }
      );
    });

    expect(await screen.findByText("Blocking State")).toBeTruthy();
    expect(screen.getByText("Topology Digest")).toBeTruthy();
    expect(screen.getByText("actor://support-worker")).toBeTruthy();
    expect(screen.getByText("Waiting for input")).toBeTruthy();
    expect(
      (await screen.findAllByText("Waiting for operator input")).length
    ).toBeGreaterThan(0);

    fireEvent.click(
      await screen.findByRole("button", { name: "Load Audit for Timeline" })
    );

    await waitFor(() => {
      expect(scopeRuntimeApi.getServiceRunAudit).toHaveBeenCalledWith(
        "scope-a",
        "support-service",
        "run-execute-command",
        {
          actorId: "actor://support-command",
        }
      );
    });

    expect(await screen.findByText("Related Audit Step")).toBeTruthy();
    expect(screen.getByText("Output preview")).toBeTruthy();
  });

  it("submits blocking timeline actions from the advanced console", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    await openAdvancedConsole();
    fireEvent.click(await screen.findByRole("button", { name: "Execute" }));

    fireEvent.change(await screen.findByLabelText("Advanced execute service"), {
      target: { value: "support-service" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute endpoint"), {
      target: { value: "assist" },
    });
    fireEvent.change(await screen.findByLabelText("Advanced execute prompt"), {
      target: { value: "Need structured help." },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));
    await screen.findByText((content) =>
      content.includes('"targetActorId": "actor://support-command"')
    );

    fireEvent.click(await screen.findByRole("button", { name: "Timeline" }));
    await screen.findByText("Blocking State");

    fireEvent.change(
      await screen.findByLabelText("Advanced timeline action input"),
      {
        target: { value: "sev-1" },
      }
    );

    fireEvent.click(await screen.findByRole("button", { name: "Resume" }));

    await waitFor(() => {
      expect(runtimeRunsApi.resume).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          actorId: "actor://support-command",
          approved: true,
          runId: "run-execute-command",
          stepId: "assist_step",
          userInput: "sev-1",
        }),
        {
          serviceId: "support-service",
        }
      );
    });

    expect(
      (await screen.findAllByText("Input submitted for assist_step.")).length
    ).toBeGreaterThan(0);
    await waitFor(() => {
      expect(chatHistoryApi.saveConversation).toHaveBeenCalledWith(
        "scope-a",
        expect.any(Object),
        expect.arrayContaining([
          expect.objectContaining({
            content: "Input submitted for assist_step.",
            role: "assistant",
          }),
        ])
      );
    });
  });

  it("approves NyxID tool requests and streams the continuation", async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValueOnce({
      body: {},
      ok: true,
      streamKind: "approval-request",
    });

    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    fireEvent.click(serviceSelector);
    fireEvent.click(await screen.findByRole("option", { name: /NyxID Chat/i }));

    const promptInput = await screen.findByPlaceholderText(
      "Send a message..."
    );
    fireEvent.change(promptInput, {
      target: { value: "Bind the current scope for me." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    expect(await screen.findByText("TOOL APPROVAL")).toBeTruthy();
    expect(screen.getAllByText("scope.bind").length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => {
      expect(nyxIdChatApi.approveToolCall).toHaveBeenCalledWith(
        "scope-a",
        "actor://nyxid",
        expect.objectContaining({
          approved: true,
          requestId: "approval-1",
        }),
        expect.any(AbortSignal)
      );
    });

    expect(await screen.findByText("Approval applied successfully.")).toBeTruthy();
    expect(screen.queryByText("TOOL APPROVAL")).toBeNull();
  });

  it("submits human input interventions from the chat thread", async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValueOnce({
      body: {},
      ok: true,
      streamKind: "human-input-request",
    });

    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    fireEvent.click(serviceSelector);
    fireEvent.click(await screen.findByRole("option", { name: /Support service/i }));

    const promptInput = await screen.findByPlaceholderText("Send a message...");
    fireEvent.change(promptInput, {
      target: { value: "Run the triage workflow." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    expect(await screen.findByText("INPUT REQUIRED")).toBeTruthy();
    expect(screen.getAllByText("triage_input").length).toBeGreaterThan(0);

    fireEvent.change(
      await screen.findByLabelText(/Run intervention input human_input:run-1:triage_input/i),
      {
        target: { value: "sev-1" },
      }
    );
    fireEvent.click(screen.getByRole("button", { name: "Resume" }));

    await waitFor(() => {
      expect(runtimeRunsApi.resume).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          actorId: "actor://support",
          approved: true,
          runId: "run-1",
          stepId: "triage_input",
          userInput: "sev-1",
        }),
        {
          serviceId: "support-service",
        }
      );
    });

    expect(await screen.findByText("Input submitted for triage_input.")).toBeTruthy();
    expect(screen.queryByText("INPUT REQUIRED")).toBeNull();
  });

  it("submits approval decisions from the chat thread", async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValueOnce({
      body: {},
      ok: true,
      streamKind: "human-approval-request",
    });

    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    fireEvent.click(serviceSelector);
    fireEvent.click(await screen.findByRole("option", { name: /Support service/i }));

    const promptInput = await screen.findByPlaceholderText("Send a message...");
    fireEvent.change(promptInput, {
      target: { value: "Prepare the release summary." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    expect(await screen.findByText("HUMAN APPROVAL")).toBeTruthy();

    fireEvent.change(
      await screen.findByLabelText(/Run intervention input human_approval:run-1:release_gate/i),
      {
        target: { value: "Looks good." },
      }
    );
    fireEvent.click(screen.getByRole("button", { name: "Reject" }));

    await waitFor(() => {
      expect(runtimeRunsApi.resume).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          actorId: "actor://support",
          approved: false,
          runId: "run-1",
          stepId: "release_gate",
          userInput: "Looks good.",
        }),
        {
          serviceId: "support-service",
        }
      );
    });

    expect(await screen.findByText("Rejection submitted for release_gate.")).toBeTruthy();
  });

  it("sends waiting-signal actions from the chat thread", async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValueOnce({
      body: {},
      ok: true,
      streamKind: "wait-signal-request",
    });

    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    fireEvent.click(serviceSelector);
    fireEvent.click(await screen.findByRole("option", { name: /Support service/i }));

    const promptInput = await screen.findByPlaceholderText("Send a message...");
    fireEvent.change(promptInput, {
      target: { value: "Wait for deployment readiness." },
    });
    fireEvent.click(await screen.findByLabelText("Send"));

    expect(await screen.findByText("WAIT SIGNAL")).toBeTruthy();
    expect(screen.getByText("Signal: deployment_ready")).toBeTruthy();

    fireEvent.change(
      await screen.findByLabelText(
        /Run intervention input wait_signal:run-1:wait_for_signal:deployment_ready/i
      ),
      {
        target: { value: "{\"status\":\"green\"}" },
      }
    );
    fireEvent.click(screen.getByRole("button", { name: "Send Signal" }));

    await waitFor(() => {
      expect(runtimeRunsApi.signal).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          actorId: "actor://support",
          payload: "{\"status\":\"green\"}",
          runId: "run-1",
          signalName: "deployment_ready",
          stepId: "wait_for_signal",
        }),
        {
          serviceId: "support-service",
        }
      );
    });

    expect(
      await screen.findByText("Signal deployment_ready accepted for wait_for_signal.")
    ).toBeTruthy();
  });

  it("restores chat history through the remote history API", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      {
        actorId: "actor://support",
        createdAt: "2026-04-01T08:00:00.000Z",
        id: "conversation-1",
        messageCount: 2,
        runId: "run-history",
        serviceId: "support-service",
        serviceKind: "service",
        title: "Need historical context",
        updatedAt: "2026-04-01T08:10:00.000Z",
      },
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([
      {
        content: "Need historical context",
        id: "user-1",
        role: "user",
        status: "complete",
        timestamp: 1,
      },
      {
        content: "Restored answer",
        id: "assistant-1",
        role: "assistant",
        status: "complete",
        thinking: "stored reasoning",
        timestamp: 2,
      },
    ]);

    renderWithQueryClient(React.createElement(ChatPage));

    const historyItem = await screen.findByText("Need historical context");
    fireEvent.click(historyItem);

    await screen.findByText("Restored answer");
    fireEvent.click(screen.getByRole("button", { name: "Thinking" }));
    expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
      "scope-a",
      "conversation-1"
    );
    expect(screen.getByText("stored reasoning")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Runtime details" }));
    expect(screen.getByText("run-history")).toBeTruthy();
  });

  it("restores NyxID approval identity from stored runtime events", async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      {
        createdAt: "2026-04-01T08:00:00.000Z",
        id: "conversation-nyxid",
        messageCount: 2,
        serviceId: "nyxid-chat",
        serviceKind: "nyxid-chat",
        title: "Pending NyxID approval",
        updatedAt: "2026-04-01T08:10:00.000Z",
      },
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([
      {
        content: "Bind the current scope for me.",
        id: "user-1",
        role: "user",
        status: "complete",
        timestamp: 1,
      },
      {
        content: "Approval required before NyxID can continue.",
        events: [
          {
            runId: "run-restore-1",
            threadId: "thread-restore-1",
            timestamp: 1,
            type: AGUIEventType.RUN_STARTED,
          },
          {
            name: CustomEventName.RunContext,
            timestamp: 2,
            type: AGUIEventType.CUSTOM,
            value: {
              actorId: "actor://nyxid-restored",
              commandId: "cmd-restore-1",
            },
          },
        ],
        id: "assistant-1",
        pendingApproval: {
          argumentsJson: "{\"scopeId\":\"scope-a\"}",
          isDestructive: false,
          requestId: "approval-restored-1",
          timeoutSeconds: 60,
          toolCallId: "call-restored-1",
          toolName: "scope.bind",
        },
        role: "assistant",
        status: "complete",
        timestamp: 2,
      },
    ]);

    renderWithQueryClient(React.createElement(ChatPage));

    fireEvent.click(await screen.findByText("Pending NyxID approval"));
    await screen.findByText("Approval required before NyxID can continue.");
    await waitFor(() => {
      expect(screen.getByLabelText("Chat service").textContent).toContain(
        "NyxID Chat"
      );
    });

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => {
      expect(nyxIdChatApi.approveToolCall).toHaveBeenCalledWith(
        "scope-a",
        "actor://nyxid-restored",
        expect.objectContaining({
          approved: true,
          requestId: "approval-restored-1",
          sessionId: "conversation-nyxid",
        }),
        expect.any(AbortSignal)
      );
    });
  });

  it("shows the raw debug stream for the active conversation", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    const serviceSelector = await screen.findByLabelText("Chat service");
    await waitFor(() => {
      expect(serviceSelector.textContent).toContain("Support service");
    });

    const promptInput = await screen.findByPlaceholderText(
      "Send a message..."
    );
    fireEvent.change(promptInput, {
      target: { value: "Show me the runtime event stream." },
    });
    fireEvent.click(screen.getByLabelText("Send"));

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-a",
        expect.objectContaining({
          prompt: "Show me the runtime event stream.",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "support-service",
        }
      );
    });

    expect(screen.queryByText("Raw Events (8)")).toBeNull();
    await openEventStream();

    expect(await screen.findByText("Raw Events (8)")).toBeTruthy();
    expect(screen.getByText("RUN_STARTED")).toBeTruthy();
    expect(screen.getByText("TOOL_CALL_START")).toBeTruthy();
    expect(screen.getByText("TEXT_MESSAGE_CONTENT")).toBeTruthy();
  });

  it("opens Studio without creating an implicit draft when Create is clicked", async () => {
    renderWithQueryClient(React.createElement(ChatPage));

    fireEvent.click(await screen.findByLabelText("Chat service"));
    fireEvent.click(await screen.findByRole("button", { name: "Create" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/studio");
      const searchParams = new URLSearchParams(window.location.search);
      expect(searchParams.get("tab")).toBe("studio");
      expect(searchParams.get("draft")).toBeNull();
    });
  });
});
