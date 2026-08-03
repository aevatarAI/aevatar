import {
  actorCan,
  applyCurrentStateResult,
  createChatActorProjection,
  decodeActorFrame,
  readCachedActionRequest,
  reduceActorFrame,
  validateActionRequest,
  writeCachedActionRequest,
} from "./chatActorState";

const actionRequest = {
  schemaVersion: 4,
  actorId: "conversation-alpha",
  originTurnId: "turn-alpha",
  taskId: "task-alpha",
  stepId: "step-connect",
  actionRequestId: "action-alpha",
  action: "service.connect",
  params: {
    catalogService: {
      serviceSlug: "api-github",
      requestedScopes: ["repo"],
    },
  },
} as const;

const currentEnvelope = {
  status: "current",
  stateVersion: 7,
  turnId: "turn-alpha",
  snapshot: {
    actorId: "conversation-alpha",
    scopeId: "scope-alpha",
    stateVersion: 7,
    progressSequence: 11,
    activeTurn: {
      turnId: "turn-alpha",
      taskId: "task-alpha",
      status: "active",
    },
    latestTurn: null,
    recentTerminalTurns: [],
    activeTask: {
      taskId: "task-alpha",
      turnId: "turn-alpha",
      status: "active",
      steps: [
        {
          stepId: "step-alpha",
          order: 1,
          kind: "tool",
          status: "failed",
          availableActions: { retry: true, skip: false, stop: true },
          operation: { operationGeneration: 2 },
        },
      ],
    },
    pendingInput: {
      requestId: "input-alpha",
      turnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-input",
      prompt: "Select a region",
      options: [{ optionId: "option-sg", label: "Singapore" }],
      askedAt: "2026-08-04T08:00:00Z",
      allowFreeText: false,
      multiSelect: false,
    },
    pendingApproval: {
      approvalRequestId: "approval-alpha",
      turnId: "turn-alpha",
      taskId: "task-alpha",
      stepId: "step-approval",
      toolName: "deploy",
      action: "Deploy",
      target: "production",
    },
    pendingActions: [],
  },
};

describe("chatActorState", () => {
  beforeEach(() => globalThis.sessionStorage.clear());
  afterEach(() => jest.restoreAllMocks());

  it("applies only monotonic typed current-state results and actor actions", () => {
    const initial = createChatActorProjection("conversation-alpha");
    const current = applyCurrentStateResult(initial, currentEnvelope);

    expect(current.reloadWithoutCursor).toBe(false);
    expect(current.projection).toMatchObject({
      actorId: "conversation-alpha",
      scopeId: "scope-alpha",
      stateVersion: 7,
      progressSequence: 11,
      pendingInput: { requestId: "input-alpha" },
      pendingApproval: { approvalRequestId: "approval-alpha" },
    });
    expect(actorCan(current.projection, "retry", "step-alpha")).toBe(true);
    expect(actorCan(current.projection, "skip", "step-alpha")).toBe(false);
    expect(actorCan(current.projection, "stop")).toBe(true);

    const older = applyCurrentStateResult(current.projection, {
      ...currentEnvelope,
      stateVersion: 6,
      snapshot: {
        ...currentEnvelope.snapshot,
        stateVersion: 6,
        progressSequence: 10,
      },
    });
    expect(older.projection).toBe(current.projection);

    expect(
      applyCurrentStateResult(current.projection, {
        status: "not_modified",
        stateVersion: 7,
        turnId: "turn-alpha",
      }).projection
    ).toBe(current.projection);
    expect(
      applyCurrentStateResult(current.projection, {
        status: "reload_required",
        stateVersion: 7,
        reasonCode: "turn_mismatch",
      }).reloadWithoutCursor
    ).toBe(true);
    expect(
      applyCurrentStateResult(current.projection, { status: "not_found" })
        .projection.stateVersion
    ).toBe(0);
  });

  it("reduces canonical actor frames by monotonic progress sequence", () => {
    const snapshotFrame = decodeActorFrame({
      type: "CUSTOM",
      sequence: 5,
      custom: {
        name: "nyxid.task.snapshot",
        payload: {
          taskId: "task-alpha",
          turnId: "turn-alpha",
          status: "active",
          steps: [
            {
              stepId: "step-alpha",
              order: 1,
              availableActions: { retry: false, skip: true, stop: false },
            },
          ],
        },
      },
    });
    const first = reduceActorFrame(
      createChatActorProjection("conversation-alpha"),
      snapshotFrame
    );
    const stale = reduceActorFrame(
      first,
      decodeActorFrame({
        type: "CUSTOM",
        sequence: 4,
        custom: {
          name: "nyxid.task.step.changed",
          payload: {
            stepId: "step-alpha",
            availableActions: { retry: true, skip: false, stop: true },
          },
        },
      })
    );

    expect(first.progressSequence).toBe(5);
    expect(actorCan(first, "skip", "step-alpha")).toBe(true);
    expect(stale).toBe(first);
  });

  it("accepts only secret-free schema-v4 service.connect requests", () => {
    expect(validateActionRequest(actionRequest)).toEqual(actionRequest);
    expect(
      validateActionRequest({
        ...actionRequest,
        params: {
          customService: {
            name: "Private API",
            endpointUrl: "https://example.com/api",
            authMethod: "header",
            authKeyName: "X-Service-Key",
          },
        },
      }).params
    ).toEqual({
      customService: {
        name: "Private API",
        endpointUrl: "https://example.com/api",
        authMethod: "header",
        authKeyName: "X-Service-Key",
      },
    });
    expect(() =>
      validateActionRequest({ ...actionRequest, schemaVersion: 3 })
    ).toThrow(expect.objectContaining({ code: "NYXID_ACTION_UNSUPPORTED" }));
    expect(() =>
      validateActionRequest({ ...actionRequest, apiKey: "secret" })
    ).toThrow(expect.objectContaining({ code: "NYXID_FIELD_UNDECLARED" }));
    expect(() =>
      validateActionRequest({
        ...actionRequest,
        params: {
          customService: {
            name: "Private API",
            endpointUrl: "https://user:pass@example.com/api",
            authMethod: "bearer",
          },
        },
      })
    ).toThrow(expect.objectContaining({ code: "NYXID_URL_UNSAFE" }));
    expect(() =>
      validateActionRequest({
        ...actionRequest,
        params: {
          customService: {
            name: "Private API",
            endpointUrl: "https://example.com/api",
            authMethod: "header",
            authKeyName: "X Service Key",
          },
        },
      })
    ).toThrow(expect.objectContaining({ code: "NYXID_ACTION_VARIANT_INVALID" }));
    expect(() =>
      validateActionRequest({
        ...actionRequest,
        params: { catalogService: { serviceSlug: "nyxid_live_secret123" } },
      })
    ).toThrow(expect.objectContaining({ code: "NYXID_SECRET_FORBIDDEN" }));
    expect(() =>
      validateActionRequest({
        ...actionRequest,
        params: {
          customService: {
            name: "Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature",
            endpointUrl: "https://example.com/api",
            authMethod: "bearer",
          },
        },
      })
    ).toThrow(expect.objectContaining({ code: "NYXID_SECRET_FORBIDDEN" }));
  });

  it("rejects an action request owned by a different conversation actor", () => {
    const projection = createChatActorProjection("conversation-alpha");
    const next = reduceActorFrame(
      projection,
      decodeActorFrame({
        type: "CUSTOM",
        sequence: 1,
        custom: {
          name: "nyxid.action.request",
          payload: { ...actionRequest, actorId: "conversation-beta" },
        },
      })
    );

    expect(next.actions.size).toBe(0);
    expect(next.conflicts).toContainEqual({
      code: "NYXID_STATE_IDENTITY_CONFLICT",
    });
  });

  it("conflicts instead of replacing action params under the same identity", () => {
    const first = reduceActorFrame(
      createChatActorProjection("conversation-alpha"),
      decodeActorFrame({
        sequence: 1,
        custom: { name: "nyxid.action.request", payload: actionRequest },
      })
    );
    const next = reduceActorFrame(
      first,
      decodeActorFrame({
        sequence: 2,
        custom: {
          name: "nyxid.action.request",
          payload: {
            ...actionRequest,
            params: { catalogService: { serviceSlug: "api-gitlab" } },
          },
        },
      })
    );

    expect(next.actions.get("action-alpha")).toMatchObject({
      conflicted: true,
      request: actionRequest,
    });
  });

  it("disables duplicate current-state action identities", () => {
    writeCachedActionRequest(actionRequest);
    const result = applyCurrentStateResult(
      createChatActorProjection("conversation-alpha"),
      {
        ...currentEnvelope,
        snapshot: {
          ...currentEnvelope.snapshot,
          pendingActions: [
            {
              ...actionRequest,
              reports: [],
              postconditionResult: null,
            },
            {
              ...actionRequest,
              originTurnId: "turn-beta",
              reports: [],
              postconditionResult: null,
            },
          ],
        },
      }
    );

    expect(result.projection.actions.get("action-alpha")).toMatchObject({
      conflicted: true,
      request: null,
    });
  });

  it("keeps live actions usable when session storage is unavailable", () => {
    const setItem = jest
      .spyOn(Storage.prototype, "setItem")
      .mockImplementation(() => {
        throw new DOMException("Unavailable", "SecurityError");
      });

    expect(() =>
      reduceActorFrame(
        createChatActorProjection("conversation-alpha"),
        decodeActorFrame({
          sequence: 1,
          custom: { name: "nyxid.action.request", payload: actionRequest },
        })
      )
    ).not.toThrow();
    setItem.mockRestore();
  });

  it("restores cached action params only for the exact pending identity", () => {
    writeCachedActionRequest(actionRequest);
    expect(
      readCachedActionRequest({
        schemaVersion: 4,
        actorId: "conversation-alpha",
        originTurnId: "turn-alpha",
        taskId: "task-alpha",
        stepId: "step-connect",
        actionRequestId: "action-alpha",
        action: "service.connect",
      })
    ).toEqual(actionRequest);

    const colonActor = {
      ...actionRequest,
      actorId: "conversation:alpha",
      actionRequestId: "action",
    } as const;
    const colonAction = {
      ...actionRequest,
      actorId: "conversation",
      actionRequestId: "alpha:action",
    } as const;
    writeCachedActionRequest(colonActor);
    writeCachedActionRequest(colonAction);
    expect(
      readCachedActionRequest({
        schemaVersion: 4,
        actorId: colonActor.actorId,
        originTurnId: colonActor.originTurnId,
        taskId: colonActor.taskId,
        stepId: colonActor.stepId,
        actionRequestId: colonActor.actionRequestId,
        action: colonActor.action,
      })
    ).toEqual(colonActor);
    expect(
      readCachedActionRequest({
        schemaVersion: 4,
        actorId: colonAction.actorId,
        originTurnId: colonAction.originTurnId,
        taskId: colonAction.taskId,
        stepId: colonAction.stepId,
        actionRequestId: colonAction.actionRequestId,
        action: colonAction.action,
      })
    ).toEqual(colonAction);
    expect(
      readCachedActionRequest({
        schemaVersion: 4,
        actorId: "conversation-alpha",
        originTurnId: "turn-alpha",
        taskId: "task-alpha",
        stepId: "step-other",
        actionRequestId: "action-alpha",
        action: "service.connect",
      })
    ).toBeNull();

    writeCachedActionRequest({
      ...actionRequest,
      actorId: "conversation-beta",
      params: { catalogService: { serviceSlug: "api-gitlab" } },
    });
    expect(
      readCachedActionRequest({
        schemaVersion: 4,
        actorId: "conversation-alpha",
        originTurnId: "turn-alpha",
        taskId: "task-alpha",
        stepId: "step-connect",
        actionRequestId: "action-alpha",
        action: "service.connect",
      })
    ).toEqual(actionRequest);
  });
});
