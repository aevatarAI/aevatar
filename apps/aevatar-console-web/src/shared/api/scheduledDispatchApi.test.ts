import { persistAuthSession } from "@/shared/auth/session";
import {
  configureScheduledDispatchRetryDelay,
  decodeScheduledDispatchSummary,
  scheduledDispatchApi,
  scheduledWorkflowPromptMaxLength,
} from "./scheduledDispatchApi";
import { encodeChatRequestEventBase64 } from "@/shared/runs/protobufPayload";

describe("scheduledDispatchApi", () => {
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

  function createSummary(overrides?: Record<string, unknown>) {
    return {
      scheduleId: "sch-alpha",
      displayName: "Daily escalation digest",
      targetKind: "ServiceInvocation",
      targetActorId: "actor-alpha",
      payloadTypeUrl: "type.googleapis.com/aevatar.ChatRequestEvent",
      serviceKey: "scope-1:default:default:svc-alpha",
      serviceId: "svc-alpha",
      serviceEndpointId: "chat",
      cronExpression: "0 9 * * 1-5",
      timezone: "Asia/Shanghai",
      enabled: true,
      createdAt: "2026-06-10T08:00:00Z",
      updatedAt: "2026-06-10T08:30:00Z",
      nextFireAt: "2026-06-11T01:00:00Z",
      lastFireAt: null,
      lastTargetActorId: "",
      lastCommandId: "",
      lastCorrelationId: "",
      lastError: "",
      fireCount: 0,
      failureCount: 0,
      headers: {
        source: "team-automations",
      },
      scheduleActorId: "schedule-actor-alpha",
      scheduleKind: "Workflow",
      deleted: false,
      ...overrides,
    };
  }

  function createReceipt(overrides?: Record<string, unknown>) {
    return {
      scheduleId: "sch-alpha",
      scheduleActorId: "schedule-actor-alpha",
      accepted: true,
      commandId: "cmd-alpha",
      correlationId: "corr-alpha",
      ackedAt: "2026-06-10T08:35:00Z",
      ackStage: "accepted",
      ...overrides,
    };
  }

  it("lists schedules through the backend schedules collection", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        items: [createSummary()],
        nextCursor: "cursor-2",
        totalCount: 12,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scheduledDispatchApi.list({
        cursor: "cursor-1",
        includeTotalCount: true,
        take: 25,
      }),
    ).resolves.toEqual({
      items: [
        expect.objectContaining({
          scheduleId: "sch-alpha",
          scheduleKind: "workflow",
          serviceId: "svc-alpha",
          targetKind: "service_invocation",
        }),
      ],
      nextCursor: "cursor-2",
      totalCount: 12,
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      "/api/schedules?cursor=cursor-1&includeTotalCount=true&take=25",
    );
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer access-token",
    );
  });

  it("lists every schedule page when requested", async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          items: [createSummary({ scheduleId: "sch-first" })],
          nextCursor: "cursor-2",
          totalCount: 2,
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          items: [createSummary({ scheduleId: "sch-second" })],
          nextCursor: null,
          totalCount: 2,
        }),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scheduledDispatchApi.listAll({
        includeTotalCount: true,
        take: 200,
      }),
    ).resolves.toEqual({
      items: [
        expect.objectContaining({ scheduleId: "sch-first" }),
        expect.objectContaining({ scheduleId: "sch-second" }),
      ],
      nextCursor: null,
      totalCount: 2,
    });

    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      "/api/schedules?includeTotalCount=true&take=200",
      "/api/schedules?cursor=cursor-2&includeTotalCount=true&take=200",
    ]);
  });

  it("posts workflow chat as base64 service invocation when creating a schedule with a revision", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => createReceipt(),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scheduledDispatchApi.create({
        displayName: " Daily escalation digest ",
        cronExpression: " 0 9 * * 1-5 ",
        timezone: " Asia/Shanghai ",
        enabled: true,
        headers: {
          source: "team-automations",
        },
        workflowChatTarget: {
          identity: {
            tenantId: " scope-1 ",
            appId: " default ",
            namespace: " default ",
            serviceId: " svc-alpha ",
          },
          prompt: " Summarize escalations. ",
          sessionId: " session-alpha ",
          revisionId: " rev-alpha ",
        },
      }),
    ).resolves.toEqual(
      expect.objectContaining({
        scheduleId: "sch-alpha",
        accepted: true,
      }),
    );

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("/api/schedules");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      displayName: "Daily escalation digest",
      cronExpression: "0 9 * * 1-5",
      timezone: "Asia/Shanghai",
      enabled: true,
      headers: {
        source: "team-automations",
      },
      scheduleKind: "workflow",
      serviceInvocation: {
        identity: {
          tenantId: "scope-1",
          appId: "default",
          namespace: "default",
          serviceId: "svc-alpha",
        },
        endpointId: "chat",
        payloadTypeUrl: "type.googleapis.com/aevatar.ai.ChatRequestEvent",
        payloadBase64: encodeChatRequestEventBase64({
          prompt: "Summarize escalations.",
          sessionId: "session-alpha",
          scopeId: "scope-1",
        }),
        revisionId: "rev-alpha",
      },
    });
    expect(JSON.parse(String(init.body)).serviceInvocation).not.toHaveProperty("payload");
    expect(JSON.parse(String(init.body)).serviceInvocation).not.toHaveProperty("payloadJson");
    expect(String(init.body)).not.toContain("memberId");
    expect(String(init.body)).not.toContain("workflowId");
    expect(String(init.body)).not.toContain("workflowChatTarget");
  });

  it("retries schedule creation when the owner binding read model briefly lags after NyxID finalization", async () => {
    const restoreRetryDelay = configureScheduledDispatchRetryDelay(async () => {});
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: false,
        status: 400,
        statusText: "Bad Request",
        text: async () =>
          JSON.stringify({
            error: "NyxID binding was not found for the scheduled subject.",
          }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => createReceipt(),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    try {
      await expect(
        scheduledDispatchApi.create({
          displayName: "Daily escalation digest",
          cronExpression: "0 9 * * 1-5",
          timezone: "Asia/Shanghai",
          enabled: true,
          workflowChatTarget: {
            identity: {
              tenantId: "scope-1",
              appId: "default",
              namespace: "default",
              serviceId: "svc-alpha",
            },
            prompt: "Summarize escalations.",
          },
        }),
      ).resolves.toEqual(
        expect.objectContaining({
          accepted: true,
          scheduleId: "sch-alpha",
        }),
      );
    } finally {
      restoreRetryDelay();
    }

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      "/api/schedules",
      "/api/schedules",
    ]);
  });

  it("stops retrying schedule creation after bounded owner binding read model attempts", async () => {
    const retryDelays: number[] = [];
    const restoreRetryDelay = configureScheduledDispatchRetryDelay(async (delayMs) => {
      retryDelays.push(delayMs);
    });
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 400,
      statusText: "Bad Request",
      text: async () =>
        JSON.stringify({
          error: "NyxID binding was not found for the scheduled subject.",
        }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    try {
      await expect(
        scheduledDispatchApi.create({
          displayName: "Daily escalation digest",
          cronExpression: "0 9 * * 1-5",
          timezone: "Asia/Shanghai",
          enabled: true,
          workflowChatTarget: {
            identity: {
              tenantId: "scope-1",
              appId: "default",
              namespace: "default",
              serviceId: "svc-alpha",
            },
          },
        }),
      ).rejects.toThrow("NyxID binding was not found for the scheduled subject.");
    } finally {
      restoreRetryDelay();
    }

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(retryDelays).toEqual([400, 900]);
  });

  it("posts workflow chat as base64 service invocation when creating without a revision", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => createReceipt(),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await scheduledDispatchApi.create({
      displayName: "Daily escalation digest",
      cronExpression: "0 9 * * 1-5",
      timezone: "Asia/Shanghai",
      enabled: true,
      headers: {
        source: "team-automations",
      },
      workflowChatTarget: {
        identity: {
          tenantId: "scope-1",
          appId: "default",
          namespace: "default",
          serviceId: "svc-alpha",
        },
        prompt: "Summarize escalations.",
        sessionId: "session-alpha",
      },
    });

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const body = JSON.parse(String(init.body));
    expect(body.scheduleKind).toBe("workflow");
    expect(body.serviceInvocation).toEqual({
      identity: {
        tenantId: "scope-1",
        appId: "default",
        namespace: "default",
        serviceId: "svc-alpha",
      },
      endpointId: "chat",
      payloadTypeUrl: "type.googleapis.com/aevatar.ai.ChatRequestEvent",
      payloadBase64: encodeChatRequestEventBase64({
        prompt: "Summarize escalations.",
        sessionId: "session-alpha",
        scopeId: "scope-1",
      }),
    });
    expect(body.serviceInvocation).not.toHaveProperty("payloadJson");
    expect(body.serviceInvocation).not.toHaveProperty("revisionId");
  });

  it("creates workflow schedules without requiring a recurring prompt", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => createReceipt(),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await scheduledDispatchApi.create({
      displayName: "Daily escalation digest",
      cronExpression: "0 9 * * 1-5",
      timezone: "Asia/Shanghai",
      enabled: true,
      workflowChatTarget: {
        identity: {
          tenantId: "scope-1",
          appId: "default",
          namespace: "default",
          serviceId: "svc-alpha",
        },
      },
    });

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(String(init.body)).serviceInvocation).toEqual({
      identity: {
        tenantId: "scope-1",
        appId: "default",
        namespace: "default",
        serviceId: "svc-alpha",
      },
      endpointId: "chat",
      payloadTypeUrl: "type.googleapis.com/aevatar.ai.ChatRequestEvent",
      payloadBase64: encodeChatRequestEventBase64({
        prompt: "",
        scopeId: "scope-1",
      }),
    });
  });

  it("puts workflow chat as base64 service invocation when updating without a revision", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => createReceipt(),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scheduledDispatchApi.update(" sch-alpha ", {
        displayName: " Edited escalation digest ",
        cronExpression: " 0 10 * * 1-5 ",
        timezone: " Asia/Shanghai ",
        enabled: false,
        headers: {
          source: "team-automations",
        },
        workflowChatTarget: {
          identity: {
            tenantId: " scope-1 ",
            appId: " default ",
            namespace: " default ",
            serviceId: " alpha-service ",
          },
          prompt: " Summarize changed escalations. ",
        },
      }),
    ).resolves.toEqual(
      expect.objectContaining({
        scheduleId: "sch-alpha",
        accepted: true,
      }),
    );

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("/api/schedules/sch-alpha");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(String(init.body))).toEqual({
      displayName: "Edited escalation digest",
      cronExpression: "0 10 * * 1-5",
      timezone: "Asia/Shanghai",
      enabled: false,
      headers: {
        source: "team-automations",
      },
      scheduleKind: "workflow",
      serviceInvocation: {
        identity: {
          tenantId: "scope-1",
          appId: "default",
          namespace: "default",
          serviceId: "alpha-service",
        },
        endpointId: "chat",
        payloadTypeUrl: "type.googleapis.com/aevatar.ai.ChatRequestEvent",
        payloadBase64: encodeChatRequestEventBase64({
          prompt: "Summarize changed escalations.",
          scopeId: "scope-1",
        }),
      },
    });
    expect(JSON.parse(String(init.body)).serviceInvocation).not.toHaveProperty("payload");
    expect(JSON.parse(String(init.body)).serviceInvocation).not.toHaveProperty("payloadJson");
    expect(JSON.parse(String(init.body)).serviceInvocation).not.toHaveProperty("revisionId");
    expect(String(init.body)).not.toContain("memberId");
    expect(String(init.body)).not.toContain("workflowId");
    expect(String(init.body)).not.toContain("workflowChatTarget");
  });

  it("retries schedule updates when the owner binding read model briefly lags", async () => {
    const restoreRetryDelay = configureScheduledDispatchRetryDelay(async () => {});
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: false,
        status: 400,
        statusText: "Bad Request",
        text: async () =>
          JSON.stringify({
            error: "NyxID binding was not found for the scheduled subject.",
          }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => createReceipt({ commandId: "cmd-update" }),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    try {
      await expect(
        scheduledDispatchApi.update(" sch-alpha ", {
          displayName: "Daily escalation digest",
          cronExpression: "0 9 * * 1-5",
          timezone: "Asia/Shanghai",
          enabled: true,
          workflowChatTarget: {
            identity: {
              tenantId: "scope-1",
              appId: "default",
              namespace: "default",
              serviceId: "svc-alpha",
            },
          },
        }),
      ).resolves.toEqual(
        expect.objectContaining({
          commandId: "cmd-update",
          scheduleId: "sch-alpha",
        }),
      );
    } finally {
      restoreRetryDelay();
    }

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      "/api/schedules/sch-alpha",
      "/api/schedules/sch-alpha",
    ]);
  });

  it("rejects oversized workflow prompts before storing the schedule payload", async () => {
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    expect(() =>
      scheduledDispatchApi.create({
        displayName: "Daily escalation digest",
        cronExpression: "0 9 * * 1-5",
        timezone: "Asia/Shanghai",
        workflowChatTarget: {
          identity: {
            tenantId: "scope-1",
            appId: "default",
            namespace: "default",
            serviceId: "svc-alpha",
          },
          prompt: "x".repeat(scheduledWorkflowPromptMaxLength + 1),
        },
      }),
    ).toThrow(
      `Recurring prompt must be ${scheduledWorkflowPromptMaxLength} characters or fewer.`,
    );
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("previews cron fire times through the preview endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        CronExpression: "0 9 * * 1-5",
        Timezone: "Asia/Shanghai",
        NextFireTimes: ["2026-06-11T01:00:00Z"],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scheduledDispatchApi.preview({
        cronExpression: "0 9 * * 1-5",
        timezone: "Asia/Shanghai",
        count: 3,
      }),
    ).resolves.toEqual({
      cronExpression: "0 9 * * 1-5",
      timezone: "Asia/Shanghai",
      nextFireTimes: ["2026-06-11T01:00:00Z"],
    });

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("/api/schedules/preview");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      cronExpression: "0 9 * * 1-5",
      timezone: "Asia/Shanghai",
      count: 3,
    });
  });

  it("uses backend route suffixes for immediate and state-change actions", async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () =>
          createReceipt({
            scheduledFireAt: "2026-06-10T08:40:00Z",
            idempotencyKey: "idem-alpha",
          }),
      } as Response)
      .mockResolvedValue({
        ok: true,
        status: 202,
        json: async () => createReceipt(),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await scheduledDispatchApi.runNow(" sch-alpha ");
    await scheduledDispatchApi.disable("sch-alpha", " pause for review ");
    await scheduledDispatchApi.enable("sch-alpha", " resume ");

    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      "/api/schedules/sch-alpha:run-now",
      "/api/schedules/sch-alpha:disable",
      "/api/schedules/sch-alpha:enable",
    ]);
    expect(JSON.parse(String(fetchMock.mock.calls[1][1]?.body))).toEqual({
      reason: " pause for review ",
    });
  });

  it("deletes a schedule through the backend schedule resource", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => createReceipt({ commandId: "cmd-delete" }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scheduledDispatchApi.delete(" sch-alpha ", " remove obsolete cadence "),
    ).resolves.toEqual(
      expect.objectContaining({
        accepted: true,
        commandId: "cmd-delete",
        scheduleId: "sch-alpha",
      }),
    );

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe(
      "/api/schedules/sch-alpha?reason=remove+obsolete+cadence",
    );
    expect(init.method).toBe("DELETE");
    expect(JSON.parse(String(init.body))).toEqual({
      reason: "remove obsolete cadence",
    });
  });

  it("decodes PascalCase summary payloads without collapsing schedule identity", () => {
    expect(
      decodeScheduledDispatchSummary({
        ScheduleId: "sch-pascal",
        DisplayName: "Pascal schedule",
        TargetKind: 1,
        TargetActorId: "actor-pascal",
        PayloadTypeUrl: "type.googleapis.com/aevatar.ChatRequestEvent",
        ServiceKey: "scope-1:default:default:svc-pascal",
        ServiceId: "svc-pascal",
        ServiceEndpointId: "chat",
        CronExpression: "0 10 * * 1-5",
        Timezone: "UTC",
        Enabled: true,
        CreatedAt: "2026-06-10T08:00:00Z",
        UpdatedAt: "2026-06-10T08:30:00Z",
        NextFireAt: null,
        LastFireAt: null,
        LastTargetActorId: "",
        LastCommandId: "",
        LastCorrelationId: "",
        LastError: "",
        FireCount: 0,
        FailureCount: 0,
        Headers: {},
        ScheduleActorId: "schedule-actor-pascal",
        ScheduleKind: 1,
        Deleted: false,
      }),
    ).toEqual(
      expect.objectContaining({
        scheduleId: "sch-pascal",
        serviceId: "svc-pascal",
        targetKind: "service_invocation",
        scheduleKind: "workflow",
        deleted: false,
      }),
    );
  });
});
