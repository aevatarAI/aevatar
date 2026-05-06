import { persistAuthSession } from "@/shared/auth/session";
import { scopeRuntimeApi } from "./scopeRuntimeApi";

describe("scopeRuntimeApi", () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, "now").mockReturnValue(1_700_000_000_000);
    persistAuthSession({
      tokens: {
        accessToken: "access-token",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: "user-1",
      },
    });
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it("loads scope-scoped services from the scope catalog endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [
        {
          serviceKey: "scope-a:custom-app:default:orders",
          tenantId: "scope-a",
          appId: "custom-app",
          namespace: "default",
          serviceId: "orders",
          displayName: "Orders",
          defaultServingRevisionId: "rev-1",
          activeServingRevisionId: "rev-1",
          deploymentId: "dep-1",
          primaryActorId: "orders-actor",
          deploymentStatus: "Active",
          endpoints: [
            {
              endpointId: "run",
              displayName: "Run",
              kind: "command",
              requestTypeUrl: "type.googleapis.com/aevatar.RunRequest",
              responseTypeUrl: "type.googleapis.com/aevatar.RunReply",
              description: "Run command",
            },
          ],
          policyIds: [],
          updatedAt: "2026-04-30T08:20:00Z",
        },
      ],
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.listServices("scope-a", {
        appId: " custom-app ",
        take: 25,
      }),
    ).resolves.toEqual([
      expect.objectContaining({
        serviceId: "orders",
        appId: "custom-app",
        endpoints: [
          expect.objectContaining({
            endpointId: "run",
            kind: "command",
          }),
        ],
      }),
    ]);

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe("/api/scopes/scope-a/services?appId=custom-app&take=25");
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token",
    );
  });

  it("loads scope-scoped service bindings", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        serviceKey: "scope-a:default:default:default",
        updatedAt: "2026-03-31T08:20:00Z",
        bindings: [
          {
            bindingId: "binding-knowledge",
            displayName: "Knowledge base",
            bindingKind: "secret",
            policyIds: ["policy-alpha"],
            retired: false,
            serviceRef: null,
            connectorRef: null,
            secretRef: {
              secretName: "knowledge-api-key",
            },
          },
        ],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.getServiceBindings("scope-a", "default"),
    ).resolves.toEqual({
      serviceKey: "scope-a:default:default:default",
      updatedAt: "2026-03-31T08:20:00Z",
      bindings: [
        expect.objectContaining({
          bindingId: "binding-knowledge",
          bindingKind: "secret",
          secretRef: {
            secretName: "knowledge-api-key",
          },
        }),
      ],
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe("/api/scopes/scope-a/services/default/bindings");
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token",
    );
  });

  it("creates a scope-scoped service binding", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => ({
        targetActorId: "actor://bindings",
        commandId: "cmd-1",
        correlationId: "corr-1",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.createServiceBinding("scope-a", "default", {
        bindingId: "binding-cache",
        displayName: "Shared cache",
        bindingKind: "connector",
        policyIds: ["policy-alpha"],
        connector: {
          connectorType: "redis",
          connectorId: "cache-main",
        },
      }),
    ).resolves.toEqual({
      targetActorId: "actor://bindings",
      commandId: "cmd-1",
      correlationId: "corr-1",
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe("/api/scopes/scope-a/services/default/bindings");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(String(init?.body))).toEqual({
      bindingId: "binding-cache",
      displayName: "Shared cache",
      bindingKind: "connector",
      policyIds: ["policy-alpha"],
      service: null,
      connector: {
        connectorType: "redis",
        connectorId: "cache-main",
      },
      secret: null,
    });
  });

  it("loads scope-scoped revisions and normalizes implementation details", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: "scope-a",
        serviceId: "default",
        serviceKey: "scope-a:default:default:default",
        displayName: "Workspace Demo",
        defaultServingRevisionId: "rev-2",
        activeServingRevisionId: "rev-2",
        deploymentId: "deploy-2",
        deploymentStatus: "Active",
        primaryActorId: "actor://scope-a/default",
        catalogStateVersion: 2,
        catalogLastEventId: "evt-2",
        updatedAt: "2026-03-31T08:00:00Z",
        revisions: [
          {
            revisionId: "rev-2",
            implementationKind: 3,
            status: "Ready",
            artifactHash: "artifact-2",
            failureReason: "",
            isDefaultServing: true,
            isActiveServing: true,
            isServingTarget: true,
            allocationWeight: 100,
            servingState: "active",
            deploymentId: "deploy-2",
            primaryActorId: "actor://scope-a/default",
            createdAt: "2026-03-31T07:00:00Z",
            preparedAt: "2026-03-31T07:10:00Z",
            publishedAt: "2026-03-31T07:20:00Z",
            retiredAt: null,
            staticActorTypeName: "SupportGAgent",
            staticPreferredActorId: "actor://scope-a/default",
          },
        ],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.getServiceRevisions("scope-a", "default"),
    ).resolves.toEqual(
      expect.objectContaining({
        scopeId: "scope-a",
        serviceId: "default",
        revisions: [
          expect.objectContaining({
            revisionId: "rev-2",
            implementationKind: "gagent",
            staticActorTypeName: "SupportGAgent",
          }),
        ],
      }),
    );

    const [input] = fetchMock.mock.calls[0] as [string, RequestInit | undefined];
    expect(input).toBe("/api/scopes/scope-a/services/default/revisions");
  });

  it("loads endpoint invoke contract details for Bind surfaces", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: "scope-a",
        serviceId: "default",
        endpointId: "chat",
        invokePath: "/api/scopes/scope-a/services/default/invoke/chat:stream",
        method: "POST",
        requestContentType: "application/json",
        responseContentType: "text/event-stream",
        requestTypeUrl: "type.googleapis.com/Aevatar.AI.Abstractions.ChatRequestEvent",
        responseTypeUrl: "type.googleapis.com/Aevatar.AI.Abstractions.ChatResponseEvent",
        supportsSse: true,
        supportsWebSocket: false,
        supportsAguiFrames: false,
        streamFrameFormat: "workflow-run-event",
        smokeTestSupported: true,
        defaultSmokeInputMode: "prompt",
        defaultSmokePrompt: "Hello from Studio Bind.",
        sampleRequestJson: null,
        deploymentStatus: "Active",
        revisionId: "rev-chat",
        curlExample: "curl -N ...",
        fetchExample: "const response = await fetch(...)",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.getServiceEndpointContract("scope-a", "default", "chat"),
    ).resolves.toEqual(
      expect.objectContaining({
        endpointId: "chat",
        invokePath: "/api/scopes/scope-a/services/default/invoke/chat:stream",
        supportsSse: true,
        supportsAguiFrames: false,
        defaultSmokeInputMode: "prompt",
        defaultSmokePrompt: "Hello from Studio Bind.",
        revisionId: "rev-chat",
      }),
    );

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      "/api/scopes/scope-a/services/default/endpoints/chat/contract",
    );
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token",
    );
  });

  it("loads member endpoint invoke contract details", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: "scope-a",
        memberId: "script-1",
        publishedServiceId: "script-1",
        endpointId: "command",
        invokePath: "/api/scopes/scope-a/members/script-1/invoke/command",
        method: "POST",
        requestContentType: "application/json",
        responseContentType: "application/json",
        requestTypeUrl:
          "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
        responseTypeUrl: "",
        supportsSse: false,
        supportsWebSocket: false,
        supportsAguiFrames: false,
        streamFrameFormat: null,
        smokeTestSupported: true,
        defaultSmokeInputMode: "typed-payload",
        defaultSmokePrompt: null,
        sampleRequestJson: "{}",
        deploymentStatus: "Active",
        revisionId: "rev-script",
        curlExample: "curl ...",
        fetchExample: "await fetch(...)",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.getMemberEndpointContract("scope-a", "script-1", "command"),
    ).resolves.toEqual(
      expect.objectContaining({
        endpointId: "command",
        memberId: "script-1",
        publishedServiceId: "script-1",
        serviceId: "",
        requestTypeUrl:
          "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
      }),
    );

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      "/api/scopes/scope-a/members/script-1/endpoints/command/contract",
    );
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token",
    );
  });

  it("keeps endpoint contract identities in separate fields", async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: "scope-a",
        endpointId: "command",
        invokePath: "/api/scopes/scope-a/members/script-1/invoke/command",
        method: "POST",
        requestContentType: "application/json",
        responseContentType: "application/json",
        requestTypeUrl:
          "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
        responseTypeUrl: "",
        supportsSse: false,
        supportsWebSocket: false,
        supportsAguiFrames: false,
        streamFrameFormat: null,
        smokeTestSupported: true,
        defaultSmokeInputMode: "typed-payload",
        defaultSmokePrompt: null,
        sampleRequestJson: "{}",
        deploymentStatus: "Active",
        revisionId: "rev-script",
        curlExample: null,
        fetchExample: null,
      }),
    } as Response) as typeof global.fetch;

    await expect(
      scopeRuntimeApi.getMemberEndpointContract("scope-a", "script-1", "command"),
    ).resolves.toEqual(
      expect.objectContaining({
        memberId: undefined,
        publishedServiceId: undefined,
        serviceId: "",
      }),
    );
  });

  it("loads member-scoped runs through the member runtime route", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: "scope-a",
        serviceId: "joker",
        serviceKey: "scope-a:default:joker",
        displayName: "joker",
        runs: [
          {
            scopeId: "scope-a",
            serviceId: "joker",
            runId: "run-42",
            actorId: "actor://scope-a/joker",
            definitionActorId: "definition://joker",
            revisionId: "rev-2",
            deploymentId: "deploy-2",
            workflowName: "joker",
            completionStatus: "Completed",
            stateVersion: 3,
            lastEventId: "evt-3",
            lastUpdatedAt: "2026-03-31T08:00:00Z",
            boundAt: "2026-03-31T07:50:00Z",
            bindingUpdatedAt: "2026-03-31T07:55:00Z",
            lastSuccess: true,
            totalSteps: 4,
            completedSteps: 4,
            roleReplyCount: 1,
            lastOutput: "Done",
            lastError: "",
          },
        ],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.listMemberRuns("scope-a", "joker", { take: 5 }),
    ).resolves.toEqual(
      expect.objectContaining({
        serviceId: "joker",
        runs: [expect.objectContaining({ runId: "run-42" })],
      }),
    );

    const [input] = fetchMock.mock.calls[0] as [string, RequestInit | undefined];
    expect(input).toBe("/api/scopes/scope-a/members/joker/runs?take=5");
  });

  it("loads run audit for a scope-scoped service run", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        summary: {
          scopeId: "scope-a",
          serviceId: "default",
          runId: "run-42",
          actorId: "actor://scope-a/default",
          definitionActorId: "definition://support",
          revisionId: "rev-2",
          deploymentId: "deploy-2",
          workflowName: "support_flow",
          completionStatus: 1,
          stateVersion: 9,
          lastEventId: "evt-9",
          lastUpdatedAt: "2026-03-31T08:30:00Z",
          boundAt: "2026-03-31T08:25:00Z",
          bindingUpdatedAt: "2026-03-31T08:26:00Z",
          lastSuccess: true,
          totalSteps: 4,
          completedSteps: 4,
          roleReplyCount: 2,
          lastOutput: "Resolved successfully",
          lastError: "",
        },
        audit: {
          reportVersion: "1.0",
          projectionScope: 0,
          topologySource: 0,
          completionStatus: 1,
          workflowName: "support_flow",
          rootActorId: "actor://scope-a/default",
          commandId: "cmd-42",
          stateVersion: 9,
          lastEventId: "evt-9",
          createdAt: "2026-03-31T08:25:00Z",
          updatedAt: "2026-03-31T08:30:00Z",
          startedAt: "2026-03-31T08:25:05Z",
          endedAt: "2026-03-31T08:30:00Z",
          durationMs: 295000,
          success: true,
          input: "hello service",
          finalOutput: "Resolved successfully",
          finalError: "",
          topology: [],
          steps: [],
          roleReplies: [],
          timeline: [
            {
              timestamp: "2026-03-31T08:25:05Z",
              stage: "step_requested",
              message: "Asked support agent",
              agentId: "agent-1",
              stepId: "answer",
              stepType: "llm_call",
              eventType: "requested",
              data: {},
            },
          ],
          summary: {
            totalSteps: 4,
            requestedSteps: 4,
            completedSteps: 4,
            roleReplyCount: 2,
            stepTypeCounts: {
              llm_call: 4,
            },
          },
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.getServiceRunAudit("scope-a", "default", "run-42", {
        actorId: "actor://scope-a/default",
      }),
    ).resolves.toEqual(
      expect.objectContaining({
        summary: expect.objectContaining({
          runId: "run-42",
          completionStatus: "completed",
        }),
        audit: expect.objectContaining({
          completionStatus: "completed",
          finalOutput: "Resolved successfully",
          summary: expect.objectContaining({
            totalSteps: 4,
          }),
        }),
      }),
    );

    const [input] = fetchMock.mock.calls[0] as [string, RequestInit | undefined];
    expect(input).toBe(
      "/api/scopes/scope-a/services/default/runs/run-42/audit?actorId=actor%3A%2F%2Fscope-a%2Fdefault",
    );
  });

  it("loads run audit for a member-scoped run", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        summary: {
          scopeId: "scope-a",
          serviceId: "joker",
          runId: "run-42",
          actorId: "actor://scope-a/joker",
          definitionActorId: "definition://joker",
          revisionId: "rev-2",
          deploymentId: "deploy-2",
          workflowName: "joker",
          completionStatus: "Completed",
          stateVersion: 3,
          lastEventId: "evt-3",
          lastUpdatedAt: "2026-03-31T08:30:00Z",
          boundAt: "2026-03-31T08:25:00Z",
          bindingUpdatedAt: "2026-03-31T08:26:00Z",
          lastSuccess: true,
          totalSteps: 0,
          completedSteps: 0,
          roleReplyCount: 0,
          lastOutput: "Done",
          lastError: "",
        },
        audit: {
          reportVersion: "1.0",
          projectionScope: 0,
          topologySource: 0,
          completionStatus: "Completed",
          workflowName: "joker",
          rootActorId: "actor://scope-a/joker",
          commandId: "cmd-42",
          stateVersion: 3,
          lastEventId: "evt-3",
          createdAt: "2026-03-31T08:25:00Z",
          updatedAt: "2026-03-31T08:30:00Z",
          startedAt: "2026-03-31T08:25:05Z",
          endedAt: "2026-03-31T08:30:00Z",
          durationMs: 295000,
          success: true,
          input: "hello joker",
          finalOutput: "Done",
          finalError: "",
          topology: [],
          timeline: [],
          steps: [],
          roleReplies: [],
          summary: {
            totalSteps: 0,
            requestedSteps: 0,
            completedSteps: 0,
            roleReplyCount: 0,
            stepTypeCounts: {},
          },
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.getMemberRunAudit("scope-a", "joker", "run-42", {
        actorId: "actor://scope-a/joker",
      }),
    ).resolves.toEqual(
      expect.objectContaining({
        summary: expect.objectContaining({
          runId: "run-42",
          serviceId: "joker",
        }),
      }),
    );

    const [input] = fetchMock.mock.calls[0] as [string, RequestInit | undefined];
    expect(input).toBe(
      "/api/scopes/scope-a/members/joker/runs/run-42/audit?actorId=actor%3A%2F%2Fscope-a%2Fjoker",
    );
  });

  it("retires a scope-scoped service revision", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: "scope-a",
        serviceId: "default",
        revisionId: "rev-2",
        status: "retired",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopeRuntimeApi.retireServiceRevision("scope-a", "default", "rev-2"),
    ).resolves.toEqual({
      scopeId: "scope-a",
      serviceId: "default",
      revisionId: "rev-2",
      status: "retired",
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      "/api/scopes/scope-a/services/default/revisions/rev-2:retire",
    );
    expect(init?.method).toBe("POST");
  });
});
