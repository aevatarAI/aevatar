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
});
