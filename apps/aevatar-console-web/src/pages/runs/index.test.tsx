import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import {
  loadDraftRunPayload,
  saveDraftRunPayload,
  saveEndpointInvocationDraftPayload,
  saveObservedRunSessionPayload,
} from "@/shared/runs/draftRunSession";
import { saveRecentRun } from "@/shared/runs/recentRuns";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import RunsPage from "./index";

const mockDispatch = jest.fn();
const mockReset = jest.fn();
const mockSession = {
  context: undefined,
  status: "idle",
  messages: [],
  events: [],
  activeSteps: new Set<string>(),
  pendingHumanInput: undefined,
  runId: "",
  error: undefined,
};

jest.mock("@aevatar-react-sdk/agui", () => ({
  connectChatWebSocket: jest.fn(),
  parseSSEStream: jest.fn(() => (async function* () {})()),
  useHumanInteraction: jest.fn(() => ({
    resume: jest.fn(),
    signal: jest.fn(),
    resuming: false,
    signaling: false,
  })),
  useRunSession: jest.fn(() => ({
    session: mockSession,
    dispatch: mockDispatch,
    reset: mockReset,
  })),
}));

jest.mock("@aevatar-react-sdk/types", () => ({
  AGUIEventType: {
    CUSTOM: "CUSTOM",
    RUN_FINISHED: "RUN_FINISHED",
    RUN_STARTED: "RUN_STARTED",
    RUN_ERROR: "RUN_ERROR",
  },
  CustomEventName: {
    RunContext: "aevatar.run.context",
    WaitingSignal: "WaitingSignal",
    StepRequest: "StepRequest",
  },
}));

jest.mock("@/shared/api/runtimeCatalogApi", () => ({
  runtimeCatalogApi: {
    listWorkflowCatalog: jest.fn(async () => []),
  },
}));

jest.mock("@/shared/agui/sseFrameNormalizer", () => ({
  parseBackendSSEStream: jest.fn(() => (async function* () {})()),
}));

jest.mock("@/shared/api/runtimeActorsApi", () => ({
  runtimeActorsApi: {
    getActorSnapshot: jest.fn(),
  },
}));

jest.mock("@/shared/api/runtimeRunsApi", () => ({
  runtimeRunsApi: {
    invokeEndpoint: jest.fn(),
    streamChat: jest.fn(),
    streamDraftRun: jest.fn(),
    resume: jest.fn(),
    signal: jest.fn(),
    stop: jest.fn(),
  },
}));

jest.mock("./components/RunsLaunchRail", () => {
  const React = require("react");

  type MockRunFormValues = {
    actorId?: string;
    endpointId?: string;
    endpointKind?: "chat" | "command";
    payloadBase64?: string;
    payloadTypeUrl?: string;
    prompt: string;
    routeName?: string;
    scopeId?: string;
    serviceOverrideId?: string;
    transport: "sse" | "ws";
  };

  const normalizeValues = (
    value: Record<string, unknown> = {}
  ): MockRunFormValues => ({
    actorId:
      typeof value.actorId === "string" ? value.actorId : undefined,
    endpointId:
      typeof value.endpointId === "string" ? value.endpointId : "chat",
    endpointKind:
      value.endpointKind === "command" ? "command" : "chat",
    payloadBase64:
      typeof value.payloadBase64 === "string" ? value.payloadBase64 : undefined,
    payloadTypeUrl:
      typeof value.payloadTypeUrl === "string" ? value.payloadTypeUrl : undefined,
    prompt: typeof value.prompt === "string" ? value.prompt : "",
    routeName:
      typeof value.routeName === "string" ? value.routeName : undefined,
    scopeId: typeof value.scopeId === "string" ? value.scopeId : undefined,
    serviceOverrideId:
      typeof value.serviceOverrideId === "string"
        ? value.serviceOverrideId
        : undefined,
    transport: value.transport === "ws" ? "ws" : "sse",
  });

  const normalizePatch = (
    value: Record<string, unknown> = {}
  ): Partial<MockRunFormValues> => ({
    ...(Object.prototype.hasOwnProperty.call(value, "actorId")
      ? { actorId: normalizeValues({ actorId: value.actorId }).actorId }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "endpointId")
      ? { endpointId: normalizeValues({ endpointId: value.endpointId }).endpointId }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "endpointKind")
      ? {
          endpointKind: normalizeValues({
            endpointKind: value.endpointKind,
          }).endpointKind,
        }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "payloadBase64")
      ? {
          payloadBase64: normalizeValues({
            payloadBase64: value.payloadBase64,
          }).payloadBase64,
        }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "payloadTypeUrl")
      ? {
          payloadTypeUrl: normalizeValues({
            payloadTypeUrl: value.payloadTypeUrl,
          }).payloadTypeUrl,
        }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "prompt")
      ? { prompt: normalizeValues({ prompt: value.prompt }).prompt }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "routeName")
      ? { routeName: normalizeValues({ routeName: value.routeName }).routeName }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "scopeId")
      ? { scopeId: normalizeValues({ scopeId: value.scopeId }).scopeId }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "serviceOverrideId")
      ? {
          serviceOverrideId: normalizeValues({
            serviceOverrideId: value.serviceOverrideId,
          }).serviceOverrideId,
        }
      : {}),
    ...(Object.prototype.hasOwnProperty.call(value, "transport")
      ? { transport: normalizeValues({ transport: value.transport }).transport }
      : {}),
  });

  const RunsLaunchRail = (props: any) => {
    const [values, setValues] = React.useState(() =>
      normalizeValues(props.initialFormValues)
    );

    React.useEffect(() => {
      setValues((current: Record<string, unknown>) => ({
        ...current,
        ...normalizeValues(props.initialFormValues),
      }));
    }, [props.initialFormValues]);

    React.useEffect(() => {
      if (!props.composerFormRef) {
        return;
      }

      props.composerFormRef.current = {
        getFieldValue: (name: string) => (values as Record<string, unknown>)[name],
        getFieldsValue: () => values,
        resetFields: () => setValues(normalizeValues(props.initialFormValues)),
        setFieldValue: (name: string, value: unknown) =>
          setValues((current: Record<string, unknown>) => ({
            ...current,
            [name]: value,
          })),
        setFieldsValue: (nextValues: Record<string, unknown>) =>
          setValues((current: Record<string, unknown>) => ({
            ...current,
            ...normalizePatch(nextValues),
          })),
        submit: () => props.onSubmitRun(values),
        validateFields: async () => values,
      };

      return () => {
        props.composerFormRef.current = undefined;
      };
    }, [props.composerFormRef, props.initialFormValues, props.onSubmitRun, values]);

    return React.createElement(
      "section",
      null,
      React.createElement("div", null, "Launch rail"),
      React.createElement("textarea", {
        "aria-label": "Prompt",
        onChange: (event: any) =>
          setValues((current: Record<string, unknown>) => ({
            ...current,
            prompt: event.target.value,
          })),
        placeholder: "Describe the task to run.",
        value: values.prompt,
      }),
      React.createElement("input", {
        "aria-label": "Scope ID",
        onChange: (event: any) =>
          setValues((current: Record<string, unknown>) => ({
            ...current,
            scopeId: event.target.value,
          })),
        value: values.scopeId ?? "",
      }),
      React.createElement(
        "button",
        {
          onClick: () => props.onSubmitRun(values),
          type: "button",
        },
        "Start run"
      ),
      props.recentRunRows.map((row: any) =>
        React.createElement(
          "button",
          {
            key: row.key,
            onClick: () => row.onRestore?.(),
            type: "button",
          },
          "Restore"
        )
      )
    );
  };

  return {
    __esModule: true,
    default: RunsLaunchRail,
  };
});

describe("RunsPage", () => {
  const mockedRuntimeCatalogApi = runtimeCatalogApi as unknown as {
    listWorkflowCatalog: jest.Mock;
  };
  const mockedRuntimeRunsApi = runtimeRunsApi as unknown as {
    invokeEndpoint: jest.Mock;
    streamChat: jest.Mock;
    streamDraftRun: jest.Mock;
    resume: jest.Mock;
    signal: jest.Mock;
    stop: jest.Mock;
  };
  const mockedParseBackendSSEStream = parseBackendSSEStream as jest.Mock;

  beforeEach(() => {
    window.history.replaceState({}, "", "/runtime/runs");
    window.sessionStorage.clear();
    window.localStorage.clear();
    jest.clearAllMocks();
    mockDispatch.mockReset();
    mockReset.mockReset();
    mockSession.context = undefined;
    mockSession.status = "idle";
    mockSession.messages = [];
    mockSession.events = [];
    mockSession.activeSteps = new Set<string>();
    mockSession.pendingHumanInput = undefined;
    mockSession.runId = "";
    mockSession.error = undefined;
    mockedRuntimeRunsApi.invokeEndpoint.mockResolvedValue({
      requestId: "cmd-1",
      targetActorId: "actor-1",
      endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
    });
    mockedRuntimeRunsApi.streamChat.mockResolvedValue({
      ok: true,
      body: {},
    });
    mockedRuntimeRunsApi.streamDraftRun.mockResolvedValue({});
    mockedRuntimeCatalogApi.listWorkflowCatalog.mockResolvedValue([]);
    mockedParseBackendSSEStream.mockImplementation(
      () => (async function* () {})()
    );
  });

  it("renders the runtime run console header and navigation actions", async () => {
    const { container } = renderWithQueryClient(React.createElement(RunsPage));

    expect(container.textContent).toContain("Runtime endpoint console");
    expect(
      screen.getByRole("button", { name: "Open runtime console guide" })
    );
    expect(
      screen.getByRole("button", { name: "Catalog" })
    ).toBeTruthy();
    expect(
      screen.queryByRole("button", { name: "返回团队高级编辑" })
    ).toBeNull();
    expect(
      screen.getByRole("button", { name: "Explorer" })
    ).toBeTruthy();
    expect(
      screen.queryByRole("button", { name: "Open observability hub" })
    ).toBeNull();
    expect(screen.getByRole("button", { name: "Inspector" })).toBeTruthy();
    expect(
      screen.getByPlaceholderText("Describe the task to run.")
    ).toBeTruthy();
    expect(container.textContent).toContain("Launch rail");
    expect(container.textContent).toContain("Run trace");
    expect(container.textContent).toContain("Inspector");
  });

  it("navigates back to the team advanced tab from the runs console", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/runs?scopeId=scope-1"
    );

    renderWithQueryClient(React.createElement(RunsPage));

    fireEvent.click(
      await screen.findByRole("button", { name: "返回团队高级编辑" })
    );

    expect(window.location.pathname).toBe("/teams/scope-1");
    expect(new URLSearchParams(window.location.search).get("tab")).toBe(
      "advanced"
    );
  });

  it("returns to the originating studio route when a return target is provided", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/runs?scopeId=scope-1&returnTo=%2Fstudio%3FscopeId%3Dscope-1%26tab%3Dstudio%26template%3Dhello-chat"
    );

    renderWithQueryClient(React.createElement(RunsPage));

    fireEvent.click(
      await screen.findByRole("button", { name: "返回团队高级编辑" })
    );

    expect(window.location.pathname).toBe("/studio");
    expect(new URLSearchParams(window.location.search).get("scopeId")).toBe(
      "scope-1"
    );
    expect(new URLSearchParams(window.location.search).get("tab")).toBe(
      "studio"
    );
    expect(new URLSearchParams(window.location.search).get("template")).toBe(
      "hello-chat"
    );
  });

  it("keeps the trace workspace viewport stretchable so the inner console can scroll", async () => {
    const { container } = renderWithQueryClient(React.createElement(RunsPage));

    const tabs = container.querySelectorAll(".ant-tabs");
    expect(tabs[0]).toHaveStyle({
      flex: "1",
      minHeight: "0",
    });

    const contentHolder = tabs[0]?.querySelector(".ant-tabs-content-holder");
    expect(contentHolder).not.toBeNull();
    expect(contentHolder).toHaveStyle({
      flex: "1",
      minHeight: "0",
      overflow: "hidden",
    });
  });

  it("uses the generic invoke path for prepared service invocation drafts", async () => {
    const draftKey = saveEndpointInvocationDraftPayload({
      endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
      prompt: "script payload",
      payloadTypeUrl: "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
      payloadBase64: "CgBSCnNjcmlwdCBwYXlsb2Fk",
    });
    window.history.replaceState(
      {},
      "",
      `/runtime/runs?scopeId=scope-1&endpointId=aevatar.tools.cli.hosting.AppScriptCommand&draftKey=${draftKey}`
    );

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("script payload");
    fireEvent.click(screen.getByRole("button", { name: "Start run" }));

    await waitFor(() => {
      expect(mockedRuntimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
          prompt: "script payload",
          payloadTypeUrl:
            "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
          payloadBase64: "CgBSCnNjcmlwdCBwYXlsb2Fk",
        }),
        {
          serviceId: undefined,
        }
      );
    });
    expect(mockedRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
    expect(mockDispatch).toHaveBeenCalledWith(
      expect.objectContaining({
        type: "RUN_STARTED",
        runId: "cmd-1",
      })
    );
    expect(mockDispatch).not.toHaveBeenCalledWith(
      expect.objectContaining({
        type: "RUN_FINISHED",
      })
    );
  });

  it("auto-starts workflow draft runs handed off from Studio", async () => {
    const draftKey = saveDraftRunPayload({
      workflowName: "workspace-demo",
      workflowYamls: ["name: workspace-demo\nsteps:\n  - id: review_step\n"],
    });
    window.history.replaceState(
      {},
      "",
      `/runtime/runs?scopeId=scope-1&route=workspace-demo&prompt=Run%20the%20draft&draftKey=${draftKey}`
    );

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("Run the draft");
    await waitFor(() => {
      expect(mockedRuntimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          prompt: "Run the draft",
          workflowYamls: [
            expect.stringContaining("name: workspace-demo"),
          ],
        }),
        expect.any(AbortSignal)
      );
    });

    expect(new URLSearchParams(window.location.search).get("draftKey")).toBeNull();
    expect(loadDraftRunPayload(draftKey)).toBeNull();
  });

  it("retries chat runs against the scope default binding when a stale service id is missing", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/runs?scopeId=scope-1&route=hello-chat&serviceOverrideId=scope-1:default:default:hello-chat&prompt=%E4%BD%A0%E5%A5%BD%EF%BC%8C%E8%AF%B7%E5%81%9A%E4%B8%AA%E8%87%AA%E6%88%91%E4%BB%8B%E7%BB%8D"
    );

    mockedRuntimeRunsApi.streamChat
      .mockRejectedValueOnce(
        new Error(
          "Service 'scope-1:default:default:hello-chat' was not found."
        )
      )
      .mockResolvedValueOnce({
        ok: true,
        body: {},
      });

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("scope-1");
    fireEvent.click(screen.getByRole("button", { name: "Start run" }));

    await waitFor(() => {
      expect(mockedRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(2);
    });

    expect(mockedRuntimeRunsApi.streamChat).toHaveBeenNthCalledWith(
      1,
      "scope-1",
      expect.objectContaining({
        prompt: "你好，请做个自我介绍",
      }),
      expect.any(AbortSignal),
      {
        serviceId: "scope-1:default:default:hello-chat",
      }
    );

    expect(mockedRuntimeRunsApi.streamChat).toHaveBeenNthCalledWith(
      2,
      "scope-1",
      expect.objectContaining({
        prompt: "你好，请做个自我介绍",
      }),
      expect.any(AbortSignal),
      {
        serviceId: undefined,
      }
    );
  });

  it("retries streamed chat runs against the scope default binding when the stream reports a missing service", async () => {
    window.history.replaceState(
      {},
      "",
      "/runtime/runs?scopeId=scope-1&route=hello-chat&serviceOverrideId=scope-1:default:default:hello-chat&prompt=%E4%BD%A0%E5%A5%BD%EF%BC%8C%E8%AF%B7%E5%81%9A%E4%B8%AA%E8%87%AA%E6%88%91%E4%BB%8B%E7%BB%8D"
    );

    mockedRuntimeRunsApi.streamChat
      .mockResolvedValueOnce({
        ok: true,
        body: {},
      })
      .mockResolvedValueOnce({
        ok: true,
        body: {},
      });
    mockedParseBackendSSEStream
      .mockImplementationOnce(
        () =>
          (async function* () {
            yield {
              type: "RUN_ERROR",
              message: "Service 'scope-1:default:default:hello-chat' was not found.",
            };
          })()
      )
      .mockImplementationOnce(() => (async function* () {})());

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("scope-1");
    fireEvent.click(screen.getByRole("button", { name: "Start run" }));

    await waitFor(() => {
      expect(mockedRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(2);
    });

    expect(mockedRuntimeRunsApi.streamChat).toHaveBeenNthCalledWith(
      1,
      "scope-1",
      expect.objectContaining({
        prompt: "你好，请做个自我介绍",
      }),
      expect.any(AbortSignal),
      {
        serviceId: "scope-1:default:default:hello-chat",
      }
    );

    expect(mockedRuntimeRunsApi.streamChat).toHaveBeenNthCalledWith(
      2,
      "scope-1",
      expect.objectContaining({
        prompt: "你好，请做个自我介绍",
      }),
      expect.any(AbortSignal),
      {
        serviceId: undefined,
      }
    );
  });

  it("hydrates observed run sessions without starting a new invoke", async () => {
    const draftKey = saveObservedRunSessionPayload({
      scopeId: "scope-1",
      serviceOverrideId: "svc-1",
      endpointId: "chat",
      prompt: "hello observed run",
      actorId: "actor-1",
      commandId: "cmd-1",
      runId: "run-1",
      events: [
        {
          type: "RUN_STARTED",
          runId: "run-1",
          threadId: "thread-1",
          timestamp: Date.now(),
        } as any,
        {
          type: "CUSTOM",
          name: "aevatar.run.context",
          value: {
            actorId: "actor-1",
            commandId: "cmd-1",
            workflowName: "chat",
          },
          timestamp: Date.now(),
        } as any,
      ],
    });
    window.history.replaceState(
      {},
      "",
      `/runtime/runs?scopeId=scope-1&endpointId=chat&draftKey=${draftKey}`
    );

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("hello observed run");
    await waitFor(() => {
      expect(mockReset).toHaveBeenCalled();
      expect(mockDispatch).toHaveBeenCalledTimes(2);
    });

    expect(mockDispatch).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({
        type: "RUN_STARTED",
        runId: "run-1",
      })
    );
    expect(mockDispatch).toHaveBeenNthCalledWith(
      2,
      expect.objectContaining({
        type: "CUSTOM",
        name: "aevatar.run.context",
      })
    );
    expect(mockedRuntimeRunsApi.invokeEndpoint).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
    expect(new URLSearchParams(window.location.search).get("draftKey")).toBeNull();
    expect(loadDraftRunPayload(draftKey)).toBeNull();
  });

  it("replays observed logs when restoring a recent run", async () => {
    saveRecentRun({
      id: "cmd-recent",
      scopeId: "scope-1",
      routeName: "direct",
      endpointId: "chat",
      prompt: "recent replay",
      actorId: "actor-1",
      commandId: "cmd-1",
      runId: "run-1",
      status: "finished",
      observedEvents: [
        {
          type: "RUN_STARTED",
          runId: "run-1",
          threadId: "thread-1",
          timestamp: Date.now(),
        } as any,
        {
          type: "CUSTOM",
          name: "aevatar.run.context",
          value: {
            actorId: "actor-1",
            commandId: "cmd-1",
            workflowName: "direct",
          },
          timestamp: Date.now(),
        } as any,
      ],
    });

    renderWithQueryClient(React.createElement(RunsPage));

    mockDispatch.mockClear();
    mockReset.mockClear();

    fireEvent.click(screen.getAllByRole("button", { name: "Restore" })[0]);

    await waitFor(() => {
      expect(mockReset).toHaveBeenCalled();
      expect(mockDispatch).toHaveBeenCalledTimes(2);
    });

    expect(mockDispatch).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({
        type: "RUN_STARTED",
        runId: "run-1",
      })
    );
    expect(mockDispatch).toHaveBeenNthCalledWith(
      2,
      expect.objectContaining({
        type: "CUSTOM",
        name: "aevatar.run.context",
      })
    );
    expect(mockedRuntimeRunsApi.invokeEndpoint).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
  });

  it("routes chat runs through the selected workflow service when endpoint kind is chat", async () => {
    mockedRuntimeCatalogApi.listWorkflowCatalog.mockResolvedValue([
      {
        name: "direct",
        description: "Direct chat workflow",
        category: "core",
        group: "default",
        groupLabel: "Default",
        sortOrder: 0,
        source: "built-in",
        sourceLabel: "Built-in",
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: true,
        primitives: [],
      },
    ]);

    window.history.replaceState(
      {},
      "",
      "/runtime/runs?scopeId=scope-1&route=direct&endpointId=support-chat&endpointKind=chat&prompt=Run%20it"
    );

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("Run it");
    fireEvent.click(screen.getByRole("button", { name: "Start run" }));

    await waitFor(() => {
      expect(mockedRuntimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          prompt: "Run it",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "direct",
        }
      );
    });
    expect(mockedRuntimeRunsApi.invokeEndpoint).not.toHaveBeenCalled();
  });
});
