import { validateActionRequest } from "./protocol.js?v=20260823-m62-studio-redesign";

const ACTOR_EVENT_TYPES = new Set([
  "task_snapshot",
  "task_step_changed",
  "control_changed",
  "continuation_changed",
  "step_control_changed",
  "input_requested",
  "input_changed",
  "approval_requested",
  "approval_changed",
  "action_request",
]);

const ACTION_IDENTITY_KEYS = [
  "actorId",
  "originTurnId",
  "taskId",
  "stepId",
  "actionRequestId",
];

export function createActorProjection(actorId = null) {
  return {
    actorId,
    scopeId: null,
    stateVersion: 0,
    progressSequence: 0,
    sequenceFingerprints: new Set(),
    activeTurn: null,
    latestTurn: null,
    recentTerminalTurns: [],
    task: null,
    steps: new Map(),
    pendingInput: null,
    pendingApproval: null,
    pendingWorkflowSignal: null,
    latestInputResolution: null,
    latestApprovalResolution: null,
    taskStatus: null,
    attentionKind: "none",
    attentionSince: null,
    activeStepSummary: null,
    actions: new Map(),
    controlFence: null,
    latestControlResult: null,
    continuation: null,
    latestStepControlResult: null,
    conflicts: [],
  };
}

export function reduceActorEvent(projection, event) {
  if (!projection || !event || !ACTOR_EVENT_TYPES.has(event.type)) return projection;
  const sequence = Number(event.sequence);
  if (!Number.isSafeInteger(sequence) || sequence < 0) {
    return withConflict(projection, "NYXID_SEQUENCE_INVALID");
  }
  if (sequence < projection.progressSequence) return projection;

  const fingerprint = eventFingerprint(event);
  if (sequence === projection.progressSequence && projection.sequenceFingerprints.has(fingerprint)) {
    return projection;
  }

  const next = cloneProjection(projection);
  next.progressSequence = sequence;
  next.sequenceFingerprints = sequence > projection.progressSequence
    ? new Set([fingerprint])
    : new Set([...projection.sequenceFingerprints, fingerprint]);

  switch (event.type) {
    case "task_snapshot":
      applyTaskSnapshot(next, event.payload);
      break;
    case "task_step_changed":
      applyStepChanged(next, event.payload);
      break;
    case "control_changed":
      next.latestControlResult = cloneValue(event.payload);
      break;
    case "continuation_changed":
      next.continuation = cloneValue(event.payload);
      break;
    case "step_control_changed":
      next.latestStepControlResult = cloneValue(event.payload);
      break;
    case "input_requested":
      next.pendingInput = cloneValue(event.payload);
      next.attentionKind = "input";
      next.attentionSince = event.payload.askedAt || null;
      next.activeStepSummary = event.payload.prompt || null;
      break;
    case "input_changed":
      next.latestInputResolution = cloneValue(event.payload);
      if (next.pendingInput?.requestId === event.payload.requestId) next.pendingInput = null;
      updateAttention(next);
      break;
    case "approval_requested":
      next.pendingApproval = pendingApprovalFromEvent(event.payload);
      next.attentionKind = "approval";
      next.attentionSince = event.payload.askedAt || null;
      next.activeStepSummary = event.payload.presentation?.action || event.payload.toolName || null;
      break;
    case "approval_changed":
      next.latestApprovalResolution = cloneValue(event.payload);
      if (next.pendingApproval?.approvalRequestId === event.payload.requestId) next.pendingApproval = null;
      updateAttention(next);
      break;
    case "action_request":
      return applyActionRequest(next, projection, event.actionRequest, sequence);
  }
  return next;
}

export function applyCurrentStateResult(projection, envelope) {
  const status = envelope?.status;
  if (status === "not_modified") {
    if (!validVersion(envelope.stateVersion) || envelope.stateVersion !== projection.stateVersion) {
      return {
        projection: withConflict(projection, "NYXID_STATE_VERSION_CONFLICT"),
        reloadWithoutCursor: false,
      };
    }
    return { projection, reloadWithoutCursor: false };
  }
  if (status === "reload_required") {
    return { projection, reloadWithoutCursor: true };
  }
  if (status === "not_found") {
    return {
      projection: createActorProjection(projection.actorId),
      reloadWithoutCursor: false,
    };
  }
  if (status !== "current") {
    return {
      projection: withConflict(projection, "NYXID_STATE_STATUS_INVALID"),
      reloadWithoutCursor: false,
    };
  }

  const snapshot = envelope.snapshot;
  if (!snapshot || typeof snapshot !== "object" || Array.isArray(snapshot)) {
    return {
      projection: withConflict(projection, "NYXID_STATE_SNAPSHOT_INVALID"),
      reloadWithoutCursor: false,
    };
  }
  if (!validVersion(envelope.stateVersion) || !validVersion(snapshot.stateVersion)) {
    return {
      projection: withConflict(projection, "NYXID_STATE_VERSION_INVALID"),
      reloadWithoutCursor: false,
    };
  }
  if (envelope.stateVersion !== snapshot.stateVersion) {
    return {
      projection: withConflict(projection, "NYXID_STATE_VERSION_CONFLICT"),
      reloadWithoutCursor: false,
    };
  }
  if (!validVersion(snapshot.progressSequence)) {
    return {
      projection: withConflict(projection, "NYXID_SEQUENCE_INVALID"),
      reloadWithoutCursor: false,
    };
  }
  if (projection.stateVersion > snapshot.stateVersion ||
      projection.progressSequence > snapshot.progressSequence) {
    return { projection, reloadWithoutCursor: false };
  }

  const actorId = validIdentity(snapshot.actorId) ? snapshot.actorId : null;
  if (!actorId || (projection.actorId && projection.actorId !== actorId)) {
    return {
      projection: withConflict(projection, "NYXID_ACTOR_ID_CONFLICT"),
      reloadWithoutCursor: false,
    };
  }
  const scopeId = validIdentity(snapshot.scopeId) ? snapshot.scopeId : null;
  if (!scopeId || (projection.scopeId && projection.scopeId !== scopeId)) {
    return {
      projection: withConflict(projection, "NYXID_SCOPE_ID_CONFLICT"),
      reloadWithoutCursor: false,
    };
  }

  const next = createActorProjection(actorId);
  next.scopeId = scopeId;
  next.stateVersion = snapshot.stateVersion;
  next.progressSequence = snapshot.progressSequence;
  next.activeTurn = cloneNullable(snapshot.activeTurn);
  next.latestTurn = cloneNullable(snapshot.latestTurn);
  next.recentTerminalTurns = Array.isArray(snapshot.recentTerminalTurns)
    ? cloneValue(snapshot.recentTerminalTurns)
    : [];
  next.pendingInput = cloneNullable(snapshot.pendingInput);
  next.pendingApproval = cloneNullable(snapshot.pendingApproval);
  next.pendingWorkflowSignal = pendingWorkflowSignalFromSnapshot(
    snapshot.pendingWorkflowSignal || snapshot.pending_workflow_signal,
  );
  next.latestInputResolution = cloneNullable(snapshot.latestInputResolution);
  next.latestApprovalResolution = cloneNullable(snapshot.latestApprovalResolution);
  next.taskStatus = snapshot.taskStatus || snapshot.activeTask?.status || null;
  next.attentionKind = snapshot.attentionKind || "none";
  next.attentionSince = snapshot.attentionSince || null;
  next.activeStepSummary = snapshot.activeStepSummary || null;
  next.controlFence = cloneNullable(snapshot.controlFence);
  next.latestControlResult = cloneNullable(snapshot.latestControlResult);
  next.continuation = cloneNullable(snapshot.continuationAdmission);
  next.conflicts = projection.conflicts.map(cloneValue);
  applyTaskSnapshot(next, snapshot.activeTask);
  applyActionSummaries(next, snapshot.pendingActions, projection.actions);
  return { projection: next, reloadWithoutCursor: false };
}

export function restoreCachedAction(summary, cachedAction) {
  if (!summary || !cachedAction || summary.conflicted) return null;
  let request;
  try {
    request = validateActionRequest(cachedAction);
  } catch {
    return null;
  }
  if (summary.schemaVersion !== request.schemaVersion || summary.action !== request.action) return null;
  for (const key of ACTION_IDENTITY_KEYS) {
    if (summary[key] !== request[key]) return null;
  }
  return request;
}

export function actorCan(projection, action, stepId = null) {
  if (!projection?.steps || !(projection.steps instanceof Map)) return false;
  if (action === "stop") {
    if (stepId) return projection.steps.get(stepId)?.availableActions?.stop === true;
    return [...projection.steps.values()].some((step) => step?.availableActions?.stop === true);
  }
  if (action === "retry" || action === "skip") {
    if (!stepId) return false;
    return projection.steps.get(stepId)?.availableActions?.[action] === true;
  }
  return false;
}

function applyTaskSnapshot(projection, input) {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    projection.task = null;
    projection.steps = new Map();
    return;
  }
  const task = cloneValue(input);
  const ordered = orderedSteps(task.steps);
  task.steps = ordered;
  projection.task = task;
  projection.steps = new Map(ordered
    .filter((step) => validIdentity(step?.stepId))
    .map((step) => [step.stepId, step]));
}

function applyStepChanged(projection, input) {
  if (!input || typeof input !== "object" || Array.isArray(input) ||
      !validIdentity(input.taskId) || !validIdentity(input.step?.stepId)) {
    addConflict(projection, "NYXID_STEP_ID_INVALID");
    return;
  }
  if (projection.task?.taskId && projection.task.taskId !== input.taskId) {
    addConflict(projection, "NYXID_TASK_ID_CONFLICT");
    return;
  }
  const planRevision = Number(input.planRevision);
  if (!Number.isSafeInteger(planRevision) || planRevision < 1 ||
      (projection.task?.planRevision && planRevision < projection.task.planRevision)) {
    addConflict(projection, "NYXID_PLAN_REVISION_CONFLICT");
    return;
  }
  const previous = projection.steps.get(input.step.stepId) || {};
  const step = { ...cloneValue(previous), ...cloneValue(input.step) };
  projection.steps.set(step.stepId, step);
  projection.steps = new Map(orderedSteps([...projection.steps.values()])
    .map((item) => [item.stepId, item]));
  if (projection.task) {
    projection.task = {
      ...projection.task,
      planRevision,
      steps: [...projection.steps.values()].map(cloneValue),
    };
  }
}

function pendingWorkflowSignalFromSnapshot(input) {
  if (!input || typeof input !== "object" || Array.isArray(input)) return null;
  const runId = String(input.runId || input.run_id || "").trim();
  const signalName = String(input.signalName || input.signal_name || "").trim();
  if (!runId || !signalName) return null;
  return {
    actorId: String(input.actorId || input.actor_id || runId).trim(),
    runId,
    signalName,
    stepId: String(input.stepId || input.step_id || "").trim(),
    prompt: String(input.prompt || "Workflow 正在等待你的选择。"),
    timeoutMs: Number.isSafeInteger(input.timeoutMs) ? input.timeoutMs : 0,
    observedAt: input.observedAt || null,
    status: "waiting",
  };
}

function pendingApprovalFromEvent(input) {
  const presentation = input?.presentation && typeof input.presentation === "object"
    ? input.presentation
    : {};
  return {
    approvalRequestId: input.approvalRequestId,
    turnId: input.turnId,
    taskId: input.taskId,
    stepId: input.stepId,
    toolName: input.toolName || "",
    toolCallId: input.toolCallId || "",
    askedAt: input.askedAt || null,
    expiresAt: input.expiresAt || null,
    action: presentation.action || "",
    target: presentation.target || "",
    actorLabel: presentation.actorLabel || "",
    reversibility: presentation.reversibility || "unknown",
    grantBoundary: presentation.grantBoundary || "",
    nyxidRequestId: presentation.nyxidRequestId || "",
  };
}

function updateAttention(projection) {
  if (projection.pendingInput) {
    projection.attentionKind = "input";
    projection.attentionSince = projection.pendingInput.askedAt || null;
    projection.activeStepSummary = projection.pendingInput.prompt || null;
    return;
  }
  if (projection.pendingApproval) {
    projection.attentionKind = "approval";
    projection.attentionSince = projection.pendingApproval.askedAt || null;
    projection.activeStepSummary = projection.pendingApproval.action ||
      projection.pendingApproval.toolName || null;
    return;
  }
  if (projection.pendingWorkflowSignal) {
    projection.attentionKind = "workflow_signal";
    projection.attentionSince = projection.pendingWorkflowSignal.observedAt || null;
    projection.activeStepSummary = projection.pendingWorkflowSignal.prompt ||
      projection.pendingWorkflowSignal.signalName || null;
    return;
  }
  projection.attentionKind = "none";
  projection.attentionSince = null;
}

function applyActionRequest(next, previous, input, sequence) {
  let request;
  try {
    request = validateActionRequest(input);
  } catch {
    addConflict(next, "NYXID_ACTION_REQUEST_INVALID", { sequence });
    return next;
  }
  if (next.actorId && next.actorId !== request.actorId) {
    addConflict(next, "NYXID_ACTOR_ID_CONFLICT", {
      actionRequestId: request.actionRequestId,
      sequence,
    });
    return next;
  }
  next.actorId ||= request.actorId;

  const existing = previous.actions.get(request.actionRequestId);
  if (!existing) {
    next.actions.set(request.actionRequestId, actionRecordFromRequest(request));
    return next;
  }
  if (existing.request && stableJson(existing.request) === stableJson(request)) return next;
  if (!existing.request && summaryMatchesRequest(existing, request)) {
    next.actions.set(request.actionRequestId, {
      ...actionRecordFromRequest(request),
      reports: cloneValue(existing.reports || []),
      postconditionResult: cloneNullable(existing.postconditionResult),
    });
    return next;
  }

  next.actions.set(request.actionRequestId, {
    ...cloneValue(existing),
    conflicted: true,
    executable: false,
    errorCode: "NYXID_ACTION_ID_CONFLICT",
  });
  addConflict(next, "NYXID_ACTION_ID_CONFLICT", {
    actionRequestId: request.actionRequestId,
    sequence,
  });
  return next;
}

function summaryMatchesRequest(summary, request) {
  if (summary.schemaVersion !== request.schemaVersion || summary.action !== request.action) {
    return false;
  }
  return ACTION_IDENTITY_KEYS.every((key) => summary[key] === request[key]);
}

function actionRecordFromRequest(request) {
  return {
    schemaVersion: request.schemaVersion,
    actorId: request.actorId,
    originTurnId: request.originTurnId,
    taskId: request.taskId,
    stepId: request.stepId,
    actionRequestId: request.actionRequestId,
    action: request.action,
    params: request.params,
    request,
    reports: [],
    postconditionResult: null,
    executable: true,
    conflicted: false,
    errorCode: null,
  };
}

function applyActionSummaries(projection, input, observedActions = new Map()) {
  projection.actions = new Map();
  if (!Array.isArray(input)) return;
  for (const summary of input) {
    if (!summary || typeof summary !== "object" ||
        !validIdentity(summary.actionRequestId) ||
        !validIdentity(summary.originTurnId) ||
        !validIdentity(summary.taskId) ||
        !validIdentity(summary.stepId)) {
      addConflict(projection, "NYXID_ACTION_SUMMARY_INVALID");
      continue;
    }
    if (projection.actions.has(summary.actionRequestId)) {
      const existing = projection.actions.get(summary.actionRequestId);
      projection.actions.set(summary.actionRequestId, {
        ...existing,
        conflicted: true,
        executable: false,
        errorCode: "NYXID_ACTION_ID_CONFLICT",
      });
      addConflict(projection, "NYXID_ACTION_ID_CONFLICT", {
        actionRequestId: summary.actionRequestId,
      });
      continue;
    }
    const item = {
      schemaVersion: summary.schemaVersion,
      actorId: projection.actorId,
      originTurnId: summary.originTurnId,
      taskId: summary.taskId,
      stepId: summary.stepId,
      actionRequestId: summary.actionRequestId,
      action: summary.action,
      params: null,
      request: null,
      reports: Array.isArray(summary.reports) ? cloneValue(summary.reports) : [],
      postconditionResult: cloneNullable(summary.postconditionResult),
      executable: false,
      conflicted: false,
      errorCode: null,
    };

    let request = null;
    if (summary.request && typeof summary.request === "object" && !Array.isArray(summary.request)) {
      try {
        request = validateActionRequest(summary.request);
      } catch {
        request = null;
      }
      if (request && !summaryMatchesRequest(item, request)) {
        item.conflicted = true;
        item.errorCode = "NYXID_ACTION_ID_CONFLICT";
        addConflict(projection, "NYXID_ACTION_ID_CONFLICT", {
          actionRequestId: summary.actionRequestId,
        });
        request = null;
      }
    }

    const observed = observedActions?.get(summary.actionRequestId);
    if (!request && !item.conflicted && observed?.request && !observed.conflicted &&
        summaryMatchesRequest(item, observed.request)) {
      request = observed.request;
    }
    if (request) {
      projection.actions.set(summary.actionRequestId, {
        ...item,
        actorId: request.actorId,
        schemaVersion: request.schemaVersion,
        action: request.action,
        params: request.params,
        request,
        executable: true,
      });
      continue;
    }
    projection.actions.set(summary.actionRequestId, item);
  }
}

function cloneProjection(projection) {
  return {
    ...projection,
    sequenceFingerprints: new Set(projection.sequenceFingerprints),
    recentTerminalTurns: projection.recentTerminalTurns.map(cloneValue),
    task: cloneNullable(projection.task),
    steps: new Map([...projection.steps].map(([key, value]) => [key, cloneValue(value)])),
    pendingInput: cloneNullable(projection.pendingInput),
    pendingApproval: cloneNullable(projection.pendingApproval),
    pendingWorkflowSignal: cloneNullable(projection.pendingWorkflowSignal),
    latestInputResolution: cloneNullable(projection.latestInputResolution),
    latestApprovalResolution: cloneNullable(projection.latestApprovalResolution),
    actions: new Map([...projection.actions].map(([key, value]) => [key, cloneValue(value)])),
    controlFence: cloneNullable(projection.controlFence),
    latestControlResult: cloneNullable(projection.latestControlResult),
    continuation: cloneNullable(projection.continuation),
    latestStepControlResult: cloneNullable(projection.latestStepControlResult),
    conflicts: projection.conflicts.map(cloneValue),
  };
}

function withConflict(projection, code, details = {}) {
  const next = cloneProjection(projection);
  addConflict(next, code, details);
  return next;
}

function addConflict(projection, code, details = {}) {
  const conflict = { code, ...details };
  const fingerprint = stableJson(conflict);
  if (!projection.conflicts.some((item) => stableJson(item) === fingerprint)) {
    projection.conflicts.push(conflict);
  }
}

function eventFingerprint(event) {
  return stableJson(event.type === "action_request"
    ? { type: event.type, sequence: event.sequence, actionRequest: event.actionRequest }
    : { type: event.type, sequence: event.sequence, payload: event.payload });
}

function orderedSteps(input) {
  if (!Array.isArray(input)) return [];
  return input.map(cloneValue).sort((left, right) => {
    const leftOrder = Number.isFinite(Number(left?.order)) ? Number(left.order) : Number.MAX_SAFE_INTEGER;
    const rightOrder = Number.isFinite(Number(right?.order)) ? Number(right.order) : Number.MAX_SAFE_INTEGER;
    return leftOrder - rightOrder || String(left?.stepId || "").localeCompare(String(right?.stepId || ""));
  });
}

function validVersion(value) {
  return Number.isSafeInteger(value) && value >= 0;
}

function validIdentity(value) {
  return typeof value === "string" &&
    value.length >= 1 &&
    value.length <= 256 &&
    !/[\s\u0000-\u001f\u007f/\\?#]/u.test(value);
}

function cloneNullable(value) {
  return value == null ? null : cloneValue(value);
}

function cloneValue(value) {
  return structuredClone(value);
}

function stableJson(value) {
  return JSON.stringify(sortJson(value));
}

function sortJson(value) {
  if (Array.isArray(value)) return value.map(sortJson);
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(
    Object.keys(value)
      .sort()
      .map((key) => [key, sortJson(value[key])]),
  );
}
