const SECRET_KEY = /(authorization|api[-_]?key|token|secret|password|credential|cookie)/i;
const PRIVATE_KEY = /^(reasoningContent|reasoning_content)$/i;
const SECRET_VALUE = /(Bearer\s+)[A-Za-z0-9._~+\/-]+|nyx(?:id)?_[A-Za-z0-9_-]{8,}/gi;
const FORBIDDEN_ACTION_KEY = /(?:^|[_-])(authorization|api[-_]?key|token|secret|password|credential|cookie|user[-_]?code|device[-_]?code)(?:$|[_-])/i;
const IDENTITY_KEYS = Object.freeze([
  "actorId",
  "originTurnId",
  "taskId",
  "stepId",
  "actionRequestId",
]);
const ACTION_REQUEST_KEYS = Object.freeze([
  "schemaVersion",
  ...IDENTITY_KEYS,
  "action",
  "params",
]);
const CATALOG_SERVICE_KEYS = Object.freeze([
  "serviceSlug",
  "requestedScopes",
  "viaNodeId",
  "targetOrgId",
]);
const CUSTOM_SERVICE_KEYS = Object.freeze([
  "name",
  "endpointUrl",
  "authMethod",
  "authKeyName",
  "viaNodeId",
  "targetOrgId",
]);
const SERVICE_ACCESS_REVIEW_KEYS = Object.freeze([
  "userServiceId",
  "serviceSlug",
  "resourceUri",
]);
const CUSTOM_AUTH_METHODS = Object.freeze([
  "bearer",
  "header",
  "query",
  "path",
  "basic",
  "body",
  "none",
]);
const ACTION_DISPOSITIONS = Object.freeze([
  "completed",
  "declined",
  "failed",
  "cancelled",
  "expired",
]);
const ACTION_RESOURCE_IDENTITIES = Object.freeze({
  userService: "userServiceId",
  key: "keyId",
  node: "nodeId",
  serviceAccount: "serviceAccountId",
  developerApp: "clientId",
  device: "deviceId",
});

const ACTOR_CUSTOM_TYPES = Object.freeze({
  "nyxid.task.snapshot": "task_snapshot",
  "nyxid.task.step.changed": "task_step_changed",
  "nyxid.control.changed": "control_changed",
  "nyxid.continuation.changed": "continuation_changed",
  "nyxid.step.control.changed": "step_control_changed",
  "nyxid.input.request": "input_requested",
  "nyxid.input.changed": "input_changed",
  "nyxid.approval.request": "approval_requested",
  "nyxid.approval.changed": "approval_changed",
});

const ENUMS = Object.freeze({
  taskStatus: enumDefinition("NYX_ID_CHAT_TASK_STATUS_", [
    "active", "succeeded", "failed", "stopped", "blocked",
  ]),
  stepStatus: enumDefinition("NYX_ID_CHAT_STEP_STATUS_", [
    "planned", "waiting", "running", "done", "failed", "skipped", "cancelled", "uncertain",
  ]),
  stepKind: enumDefinition("NYX_ID_CHAT_STEP_KIND_", [
    "llm", "tool", "browser_action", "postcondition", "input", "approval", "web", "condition",
  ]),
  stepAddedBy: enumDefinition("NYX_ID_CHAT_STEP_ADDED_BY_", ["initial", "replan", "steering"]),
  planRevisionCause: enumDefinition("NYX_ID_CHAT_PLAN_REVISION_CAUSE_", [
    "initial", "scope_resolution", "failure_recovery", "steering", "user_revision",
  ]),
  estimateKind: enumDefinition("NYX_ID_CHAT_STEP_ESTIMATE_KIND_", ["duration"]),
  substepStatus: enumDefinition("NYX_ID_CHAT_SUBSTEP_STATUS_", ["running", "done", "failed"]),
  stepChangeKind: enumDefinition("NYX_ID_CHAT_STEP_CHANGE_KIND_", [
    "status", "substep", "added", "cancelled",
  ]),
  effect: enumDefinition("NYX_ID_CHAT_EFFECT_EVIDENCE_", [
    "not_started", "not_applied", "confirmed", "may_have_changed",
  ]),
  operationPhase: enumDefinition("NYX_ID_CHAT_OPERATION_PHASE_", [
    "requested", "dispatched", "running", "succeeded", "failed", "cancelled", "uncertain",
  ]),
  controlKind: enumDefinition("NYX_ID_CHAT_CONTROL_KIND_", ["stop", "steering"]),
  controlOutcome: enumDefinition("NYX_ID_CHAT_CONTROL_OUTCOME_", [
    "accepted", "rejected", "already_terminal", "uncancellable",
  ]),
  continuationKind: enumDefinition("NYX_ID_CHAT_CONTINUATION_KIND_", ["steering", "action"]),
  continuationStatus: enumDefinition("NYX_ID_CHAT_CONTINUATION_ADMISSION_STATUS_", [
    "requested", "accepted", "accepted_for_later", "rejected", "started",
  ]),
  stepControlKind: enumDefinition("NYX_ID_CHAT_STEP_CONTROL_KIND_", ["retry", "skip"]),
  transitionOutcome: enumDefinition("NYX_ID_CHAT_TRANSITION_OUTCOME_", [
    "accepted", "rejected", "idempotent",
  ]),
  needsYouOutcome: enumDefinition("NYX_ID_CHAT_NEEDS_YOU_RESOLUTION_OUTCOME_", [
    "accepted", "expired",
  ]),
  approvalReversibility: enumDefinition("NYX_ID_CHAT_APPROVAL_REVERSIBILITY_", [
    "reversible", "irreversible", "unknown",
  ]),
});

export class ProtocolValidationError extends Error {
  constructor(message, code = "NYXID_PROTOCOL_INVALID") {
    super(message);
    this.name = "ProtocolValidationError";
    this.code = code;
  }
}

function enumDefinition(prefix, values) {
  return Object.freeze({ prefix, values: Object.freeze(values) });
}

export async function consumeSse(response, onFrame) {
  if (!response.body) return;
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
    const parsed = extractSseEvents(buffer, done);
    buffer = parsed.rest;
    for (const event of parsed.events) {
      if (!event.data || event.data === "[DONE]") continue;
      let shouldContinue;
      try {
        shouldContinue = await onFrame(JSON.parse(event.data), event);
      } catch (error) {
        shouldContinue = await onFrame({
          type: "DEMO_PROTOCOL_ERROR",
          protocolError: { message: error.message, raw: event.data.slice(0, 500) },
        }, event);
      }
      if (shouldContinue === false) {
        try {
          await reader.cancel();
        } catch {
          // The transport may already be closing after the authoritative state converged.
        }
        return;
      }
    }
    if (done) break;
  }
}

export function extractSseEvents(input, flush = false) {
  const normalized = input.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  const blocks = normalized.split("\n\n");
  const rest = flush ? "" : blocks.pop() || "";
  const events = [];
  for (const block of blocks) {
    if (!block.trim()) continue;
    let event = "message";
    let id = "";
    const data = [];
    for (const line of block.split("\n")) {
      if (line.startsWith(":")) continue;
      const separator = line.indexOf(":");
      const field = separator < 0 ? line : line.slice(0, separator);
      const value = separator < 0
        ? ""
        : line.slice(separator + 1).replace(/^ /, "");
      if (field === "event") event = value;
      if (field === "id") id = value;
      if (field === "data") data.push(value);
    }
    if (data.length) events.push({ event, id, data: data.join("\n") });
  }
  if (flush && rest.trim()) {
    const tail = extractSseEvents(`${rest}\n\n`, false);
    events.push(...tail.events);
  }
  return { events, rest };
}

export function normalizeFrame(raw) {
  if (!raw || typeof raw !== "object") {
    return { type: "unknown", raw };
  }

  if (raw.type) return normalizeTypedFrame(raw);
  if (raw.runStarted) return normalizeRunStarted(raw);
  if (raw.runFinished) return { type: "run_finished", ...raw.runFinished, raw };
  if (raw.runError) return { type: "run_error", ...raw.runError, raw };
  if (raw.runStopped) return { type: "run_stopped", ...raw.runStopped, raw };
  if (raw.stepStarted) return { type: "step_started", ...raw.stepStarted, raw };
  if (raw.stepFinished) return { type: "step_finished", ...raw.stepFinished, raw };
  if (raw.textMessageStart) return { type: "text_start", ...raw.textMessageStart, raw };
  if (raw.textMessageContent) return { type: "text_delta", ...raw.textMessageContent, raw };
  if (raw.textMessageEnd) return { type: "text_end", ...raw.textMessageEnd, raw };
  if (raw.modelCallStart) return normalizeOperationFrame("model_start", raw.modelCallStart, raw);
  if (raw.modelCallEnd) return normalizeOperationFrame("model_end", raw.modelCallEnd, raw);
  if (raw.toolCallStart) return normalizeOperationFrame("tool_start", raw.toolCallStart, raw);
  if (raw.toolCallEnd) return normalizeOperationFrame("tool_end", raw.toolCallEnd, raw);
  if (raw.usage) return { type: "usage", ...raw.usage, raw };
  if (raw.stateSnapshot) return { type: "state_snapshot", ...raw.stateSnapshot, raw };
  if (raw.custom) return normalizeCustom(raw.custom, raw);
  return { type: "unknown", raw };
}

function normalizeTypedFrame(raw) {
  switch (String(raw.type).toUpperCase()) {
    case "RUN_STARTED":
      return normalizeRunStarted(raw);
    case "RUN_FINISHED":
      return { type: "run_finished", ...(raw.runFinished || {}), raw };
    case "RUN_ERROR":
      return { type: "run_error", ...(raw.runError || {}), raw };
    case "RUN_STOPPED":
      return { type: "run_stopped", ...(raw.runStopped || {}), raw };
    case "TEXT_MESSAGE_START":
      return { type: "text_start", ...(raw.textMessageStart || {}), raw };
    case "TEXT_MESSAGE_CONTENT":
      return { type: "text_delta", ...(raw.textMessageContent || {}), raw };
    case "TEXT_MESSAGE_END":
      return { type: "text_end", ...(raw.textMessageEnd || {}), raw };
    case "MODEL_CALL_START":
      return normalizeOperationFrame("model_start", raw.modelCallStart || {}, raw);
    case "MODEL_CALL_END":
      return normalizeOperationFrame("model_end", raw.modelCallEnd || {}, raw);
    case "TOOL_CALL_START":
      return normalizeOperationFrame("tool_start", raw.toolCallStart || {}, raw);
    case "TOOL_CALL_END":
      return normalizeOperationFrame("tool_end", raw.toolCallEnd || {}, raw);
    case "TOOL_APPROVAL_REQUEST":
      return {
        type: "approval",
        approvalKind: "nyxid-chat",
        ...(raw.toolApprovalRequest || {}),
        raw,
      };
    case "AUTHORIZATION_REQUIRED":
      return {
        type: "authorization_required",
        ...(raw.authorizationRequired || {}),
        raw,
      };
    case "USAGE":
      return { type: "usage", ...(raw.usage || {}), raw };
    case "MEDIA_CONTENT":
      return { type: "media", ...(raw.mediaContent || {}), raw };
    case "CUSTOM":
      return normalizeCustom(raw.custom || {}, raw);
    case "DEMO_PROTOCOL_ERROR":
      return { type: "protocol_error", ...(raw.protocolError || {}), raw };
    default:
      return { type: String(raw.type).toLowerCase(), raw };
  }
}

function normalizeOperationFrame(type, payload, raw) {
  const sequence = Number(raw.sequence ?? payload.sequence);
  return {
    type,
    ...payload,
    ...(Number.isSafeInteger(sequence) && sequence > 0 ? { sequence } : {}),
    raw,
  };
}

function normalizeRunStarted(raw) {
  const started = raw.runStarted && typeof raw.runStarted === "object"
    ? raw.runStarted
    : {};
  const conversationId = raw.conversationId || raw.actorId ||
    started.conversationId || started.actorId || started.threadId;
  const turnId = raw.turnId || started.turnId || started.runId;
  return {
    type: "run_started",
    ...started,
    ...(conversationId ? {
      actorId: conversationId,
      conversationId,
      threadId: started.threadId || conversationId,
    } : {}),
    ...(turnId ? { turnId, runId: started.runId || turnId } : {}),
    raw,
  };
}

function normalizeCustom(custom, raw) {
  const name = String(custom.name || "");
  const payload = unpackAny(custom.payload);
  if (name === "nyxid.action.request") {
    try {
      return {
        type: "action_request",
        sequence: actorSequence(raw),
        actionRequest: validateActionRequest(payload),
        name,
        raw,
      };
    } catch (error) {
      if (!(error instanceof ProtocolValidationError)) throw error;
      return {
        type: "protocol_error",
        code: error.code,
        message: error.message,
        sequence: safeActorSequence(raw),
        name,
        raw,
      };
    }
  }
  if (ACTOR_CUSTOM_TYPES[name]) {
    try {
      return {
        type: ACTOR_CUSTOM_TYPES[name],
        sequence: actorSequence(raw),
        payload: normalizeActorPayload(ACTOR_CUSTOM_TYPES[name], payload),
        name,
        raw,
      };
    } catch (error) {
      if (!(error instanceof ProtocolValidationError)) throw error;
      return {
        type: "protocol_error",
        code: error.code,
        message: error.message,
        sequence: safeActorSequence(raw),
        name,
        raw,
      };
    }
  }
  if (name === "aevatar.run.context") {
    return { type: "run_context", ...payload, name, raw };
  }
  if (name === "aevatar.step.request") {
    return { type: "step_request", ...payload, name, raw };
  }
  if (name === "aevatar.step.completed") {
    return { type: "step_completed", ...payload, name, raw };
  }
  if (name === "aevatar.llm.reasoning") {
    return { type: "reasoning", name, raw };
  }
  if (name === "aevatar.human_input.request") {
    return {
      type: "approval",
      approvalKind: "workflow",
      ...payload,
      name,
      raw,
    };
  }
  if (name === "aevatar.tool_approval.pending") {
    return {
      type: "approval",
      approvalKind: "workflow",
      ...payload,
      toolApproval: {
        executionId: payload.executionId,
        toolCallId: payload.toolCallId,
        approvalRequestId: payload.approvalRequestId,
      },
      name,
      raw,
    };
  }
  if (name === "aevatar.authorization.required" || name === "nyxid.authorization.required") {
    return { type: "authorization_required", ...payload, name, raw };
  }
  if (name === "aevatar.workflow.waiting_signal") {
    return { type: "waiting_signal", ...payload, name, raw };
  }
  if (name === "aevatar.nyxid_chat.keepalive") {
    return { type: "keepalive", ...payload, name, raw };
  }
  if (name === "aevatar.raw.observed") {
    const nestedPayload = payload.payload && typeof payload.payload === "object"
      ? unpackAny(payload.payload)
      : payload;
    const payloadTypeUrl = payload.payloadTypeUrl ||
      payload.payload?.["@type"] ||
      custom.payload?.["@type"] ||
      "";
    const observedType = String(payloadTypeUrl).split(/[/.]/).at(-1) || "unknown";
    const observedEnvelope = {
      eventId: payload.eventId,
      publisherActorId: payload.publisherActorId,
      correlationId: payload.correlationId,
      stateVersion: payload.stateVersion,
    };
    if (observedType === "RoleChatSessionCompletedEvent") {
      return {
        type: "role_chat_completed",
        ...nestedPayload,
        observedType,
        observedEnvelope,
        name,
        raw,
      };
    }
    return {
      type: "raw_observed",
      observedType,
      observed: nestedPayload,
      observedEnvelope,
      name,
      raw,
    };
  }
  return { type: "custom", name, payload, raw };
}

export function unpackAny(payload) {
  if (!payload || typeof payload !== "object") return {};
  if (payload.value && typeof payload.value === "object") return payload.value;
  const clone = { ...payload };
  delete clone["@type"];
  return clone;
}

export function validateActionRequest(payload) {
  const value = requireObject(unpackAny(payload), "NyxID action request must be an object.");
  assertAllowedKeys(value, ACTION_REQUEST_KEYS);
  if (value.schemaVersion !== 4 ||
      !["service.connect", "service.access_review"].includes(value.action)) {
    throw new ProtocolValidationError(
      "Unsupported NyxID action request.",
      "NYXID_ACTION_UNSUPPORTED",
    );
  }

  const identity = Object.fromEntries(
    IDENTITY_KEYS.map((key) => [key, validateIdentity(value[key])]),
  );
  const params = value.action === "service.connect"
    ? validateServiceConnectParams(value.params)
    : validateServiceAccessReviewParams(value.params);
  rejectSecretBearingInput({ ...identity, params });
  return deepFreeze({
    schemaVersion: 4,
    ...identity,
    action: value.action,
    params,
  });
}

export function validateActionContinuation(input, { expectedAction = null } = {}) {
  const value = requireContinuationObject(input);
  assertAllowedKeys(value, ["type", "clientRequestId", "originTurnId", "actions"]);
  if (value.type !== "action.continue") {
    throw invalidActionContinuation();
  }
  const clientRequestId = validateIdentity(value.clientRequestId);
  const originTurnId = validateIdentity(value.originTurnId);
  if (!Array.isArray(value.actions) || value.actions.length < 1) {
    throw invalidActionContinuation();
  }

  const actionRequestIds = new Set();
  const actions = value.actions.map((inputReport) => {
    const report = requireContinuationObject(inputReport);
    assertAllowedKeys(report, ["actionRequestId", "originTurnId", "disposition", "resource"]);
    const actionRequestId = validateIdentity(report.actionRequestId);
    if (actionRequestIds.has(actionRequestId)) {
      throw new ProtocolValidationError(
        "NyxID action report identity is duplicated.",
        "NYXID_ACTION_REPORT_DUPLICATE",
      );
    }
    actionRequestIds.add(actionRequestId);

    const reportOriginTurnId = validateIdentity(report.originTurnId);
    if (reportOriginTurnId !== originTurnId) {
      throw new ProtocolValidationError(
        "NyxID action report origin does not match.",
        "NYXID_ACTION_ORIGIN_MISMATCH",
      );
    }
    if (!ACTION_DISPOSITIONS.includes(report.disposition)) {
      throw invalidActionContinuation();
    }

    const resource = validateActionResource(report.resource);
    if (["service.connect", "service.access_review"].includes(expectedAction) &&
        report.disposition === "completed" &&
        (!resource || !Object.prototype.hasOwnProperty.call(resource, "userService"))) {
      throw invalidActionResource();
    }
    return {
      actionRequestId,
      originTurnId: reportOriginTurnId,
      disposition: report.disposition,
      ...(resource ? { resource } : {}),
    };
  });

  const result = {
    type: "action.continue",
    clientRequestId,
    originTurnId,
    actions,
  };
  rejectSecretBearingInput(result);
  return deepFreeze(result);
}

function validateActionResource(input) {
  if (input === undefined || input === null) return null;
  const value = requireContinuationObject(input);
  assertAllowedKeys(value, Object.keys(ACTION_RESOURCE_IDENTITIES));
  const variants = Object.keys(value);
  if (variants.length !== 1) throw invalidActionResource();
  const variant = variants[0];
  const identityKey = ACTION_RESOURCE_IDENTITIES[variant];
  if (!identityKey) throw invalidActionResource();
  const reference = requireContinuationObject(value[variant]);
  assertAllowedKeys(reference, [identityKey]);
  return { [variant]: { [identityKey]: validateIdentity(reference[identityKey]) } };
}

function requireContinuationObject(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw invalidActionContinuation();
  }
  return value;
}

function invalidActionContinuation() {
  return new ProtocolValidationError(
    "NyxID action continuation is invalid.",
    "NYXID_ACTION_CONTINUATION_INVALID",
  );
}

function invalidActionResource() {
  return new ProtocolValidationError(
    "NyxID action resource is invalid.",
    "NYXID_ACTION_RESOURCE_INVALID",
  );
}

function validateServiceConnectParams(input) {
  const value = requireObject(input, "NyxID action params must be an object.");
  assertAllowedKeys(value, ["catalogService", "customService"]);
  const hasCatalog = Object.prototype.hasOwnProperty.call(value, "catalogService");
  const hasCustom = Object.prototype.hasOwnProperty.call(value, "customService");
  if (hasCatalog === hasCustom) {
    throw new ProtocolValidationError(
      "NyxID service connect requires exactly one typed variant.",
      "NYXID_ACTION_VARIANT_INVALID",
    );
  }
  return hasCatalog
    ? { catalogService: validateCatalogService(value.catalogService) }
    : { customService: validateCustomService(value.customService) };
}

function validateServiceAccessReviewParams(input) {
  const value = requireObject(input, "NyxID action params must be an object.");
  assertAllowedKeys(value, ["serviceAccessReview"]);
  if (!Object.prototype.hasOwnProperty.call(value, "serviceAccessReview")) {
    throw invalidActionParams();
  }
  const review = requireObject(
    value.serviceAccessReview,
    "NyxID service access review params must be an object.",
  );
  assertAllowedKeys(review, SERVICE_ACCESS_REVIEW_KEYS);
  const serviceSlug = validateBoundedString(review.serviceSlug, 128);
  if (!/^[A-Za-z0-9._-]+$/.test(serviceSlug)) throw invalidActionParams();
  const resourceUri = validateSafeHttpsUrl(review.resourceUri);
  const parsedResource = new URL(resourceUri);
  if (!parsedResource.pathname.endsWith(`/s/${serviceSlug}`)) throw invalidActionParams();
  return {
    serviceAccessReview: {
      userServiceId: validateIdentity(review.userServiceId),
      serviceSlug,
      resourceUri,
    },
  };
}

function validateCatalogService(input) {
  const value = requireObject(input, "Catalog service params must be an object.");
  assertAllowedKeys(value, CATALOG_SERVICE_KEYS);
  const result = {
    serviceSlug: validateBoundedString(value.serviceSlug, 128),
  };
  if (!/^[A-Za-z0-9._-]+$/.test(result.serviceSlug)) {
    throw invalidActionVariant();
  }
  if (Object.prototype.hasOwnProperty.call(value, "requestedScopes")) {
    if (!Array.isArray(value.requestedScopes) || value.requestedScopes.length > 64) {
      throw invalidActionVariant();
    }
    result.requestedScopes = value.requestedScopes.map((scope) => validateBoundedString(scope, 256));
  }
  copyOptionalIdentity(value, result, "viaNodeId");
  copyOptionalIdentity(value, result, "targetOrgId");
  return result;
}

function validateCustomService(input) {
  const value = requireObject(input, "Custom service params must be an object.");
  assertAllowedKeys(value, CUSTOM_SERVICE_KEYS);
  const authMethod = validateBoundedString(value.authMethod, 32);
  if (!CUSTOM_AUTH_METHODS.includes(authMethod)) throw invalidActionVariant();
  const result = {
    name: validateBoundedString(value.name, 256),
    endpointUrl: validateSafeHttpsUrl(value.endpointUrl),
    authMethod,
  };
  if (Object.prototype.hasOwnProperty.call(value, "authKeyName")) {
    const authKeyName = validateBoundedString(value.authKeyName, 256);
    if (!/^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/.test(authKeyName)) {
      throw invalidActionVariant();
    }
    result.authKeyName = authKeyName;
  }
  copyOptionalIdentity(value, result, "viaNodeId");
  copyOptionalIdentity(value, result, "targetOrgId");
  return result;
}

function copyOptionalIdentity(source, target, key) {
  if (Object.prototype.hasOwnProperty.call(source, key)) {
    target[key] = validateIdentity(source[key]);
  }
}

function validateIdentity(value) {
  if (typeof value !== "string" ||
      value.length < 1 ||
      value.length > 256 ||
      /[\s\u0000-\u001f\u007f/\\?#]/u.test(value)) {
    throw new ProtocolValidationError(
      "NyxID action identity is invalid.",
      "NYXID_IDENTITY_INVALID",
    );
  }
  return value;
}

function validateBoundedString(value, maximum) {
  if (typeof value !== "string" || value.length < 1 || value.length > maximum || value.trim() !== value) {
    throw invalidActionVariant();
  }
  return value;
}

function validateSafeHttpsUrl(value) {
  const input = validateBoundedString(value, 2048);
  let parsed;
  try {
    parsed = new URL(input);
  } catch {
    throw unsafeActionUrl();
  }
  if (parsed.protocol !== "https:" ||
      !parsed.hostname ||
      parsed.username ||
      parsed.password ||
      parsed.search ||
      parsed.hash) {
    throw unsafeActionUrl();
  }
  return input;
}

function assertAllowedKeys(value, allowed) {
  const allowedKeys = new Set(allowed);
  if (Object.keys(value).some((key) => !allowedKeys.has(key))) {
    throw new ProtocolValidationError(
      "NyxID action contains an undeclared field.",
      "NYXID_FIELD_UNDECLARED",
    );
  }
}

function requireObject(value, message) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new ProtocolValidationError(message, "NYXID_ACTION_VARIANT_INVALID");
  }
  return value;
}

function rejectSecretBearingInput(value) {
  if (Array.isArray(value)) {
    for (const item of value) rejectSecretBearingInput(item);
    return;
  }
  if (value && typeof value === "object") {
    for (const [key, item] of Object.entries(value)) {
      if (FORBIDDEN_ACTION_KEY.test(key)) throw forbiddenActionSecret();
      rejectSecretBearingInput(item);
    }
    return;
  }
  if (typeof value === "string") {
    SECRET_VALUE.lastIndex = 0;
    if (SECRET_VALUE.test(value)) throw forbiddenActionSecret();
  }
}

function invalidActionVariant() {
  return new ProtocolValidationError(
    "NyxID action params are invalid.",
    "NYXID_ACTION_VARIANT_INVALID",
  );
}

function invalidActionParams() {
  return new ProtocolValidationError(
    "NyxID action params are invalid.",
    "NYXID_ACTION_PARAMS_INVALID",
  );
}

function unsafeActionUrl() {
  return new ProtocolValidationError(
    "NyxID action URL is unsafe.",
    "NYXID_URL_UNSAFE",
  );
}

function forbiddenActionSecret() {
  return new ProtocolValidationError(
    "NyxID action input must not contain secrets.",
    "NYXID_SECRET_FORBIDDEN",
  );
}

function deepFreeze(value) {
  if (value && typeof value === "object" && !Object.isFrozen(value)) {
    Object.freeze(value);
    for (const item of Object.values(value)) deepFreeze(item);
  }
  return value;
}

function actorSequence(raw) {
  const value = Number(raw?.sequence ?? 0);
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new ProtocolValidationError(
      "Actor progress sequence is invalid.",
      "NYXID_SEQUENCE_INVALID",
    );
  }
  return value;
}

function safeActorSequence(raw) {
  const value = Number(raw?.sequence ?? 0);
  return Number.isSafeInteger(value) && value >= 0 ? value : 0;
}

function normalizeActorPayload(type, payload) {
  const value = cloneJsonObject(payload);
  if (type === "task_snapshot") {
    normalizeIntegerProperty(value, "schemaVersion");
    normalizeIntegerProperty(value, "planRevision");
    normalizeIntegerProperty(value, "planRevisionHistoryStart");
    if (!Number.isSafeInteger(value.planRevision) || value.planRevision < 1) {
      throw new ProtocolValidationError("Actor plan revision is invalid.", "NYXID_NUMBER_INVALID");
    }
    normalizeEnumProperty(value, "status", ENUMS.taskStatus);
    if (Array.isArray(value.steps)) value.steps = value.steps.map(normalizeStep);
    if (Object.prototype.hasOwnProperty.call(value, "planRevisions")) {
      if (!Array.isArray(value.planRevisions)) {
        throw new ProtocolValidationError("Actor plan revision history is invalid.");
      }
      value.planRevisions = value.planRevisions.map(normalizePlanRevision);
      const historyStart = value.planRevisionHistoryStart ||
        value.planRevisions[0]?.planRevision || 0;
      for (let index = 0; index < value.planRevisions.length; index += 1) {
        const expected = historyStart + index;
        if (value.planRevisions[index].planRevision !== expected) {
          throw new ProtocolValidationError(
            "Actor plan revision history is not contiguous.",
            "NYXID_PLAN_REVISION_CONFLICT",
          );
        }
      }
      if (value.planRevisions.length > 0 &&
          value.planRevisions.at(-1).planRevision !== value.planRevision) {
        throw new ProtocolValidationError(
          "Actor plan revision history does not match the current plan.",
          "NYXID_PLAN_REVISION_CONFLICT",
        );
      }
      if (value.planRevisions.length > 0 && historyStart < 1) {
        throw new ProtocolValidationError(
          "Actor plan revision history start is invalid.",
          "NYXID_PLAN_REVISION_CONFLICT",
        );
      }
    }
    return value;
  }
  if (type === "task_step_changed") {
    value.taskId = validateIdentity(value.taskId);
    normalizeIntegerProperty(value, "planRevision");
    normalizeEnumProperty(value, "changeKind", ENUMS.stepChangeKind);
    value.step = normalizeStep(value.step);
    return value;
  }
  if (type === "control_changed") {
    normalizeEnumProperty(value, "kind", ENUMS.controlKind);
    normalizeEnumProperty(value, "outcome", ENUMS.controlOutcome);
    normalizeIntegerProperty(value, "operationGeneration");
    return value;
  }
  if (type === "continuation_changed") {
    normalizeEnumProperty(value, "kind", ENUMS.continuationKind);
    normalizeEnumProperty(value, "status", ENUMS.continuationStatus);
    return value;
  }
  if (type === "step_control_changed") {
    normalizeEnumProperty(value, "kind", ENUMS.stepControlKind);
    normalizeEnumProperty(value, "outcome", ENUMS.transitionOutcome);
    normalizeIntegerProperty(value, "expectedOperationGeneration");
    normalizeIntegerProperty(value, "operationGeneration");
    normalizeIntegerProperty(value, "expectedStateVersion");
    return value;
  }
  if (type === "input_requested") return normalizeInputRequest(value);
  if (type === "input_changed") return normalizeNeedsYouResolution(value);
  if (type === "approval_requested") return normalizeApprovalRequest(value);
  if (type === "approval_changed") return normalizeNeedsYouResolution(value);
  return value;
}

function normalizeInputRequest(value) {
  for (const key of ["requestId", "turnId", "taskId", "stepId"]) {
    value[key] = validateIdentity(value[key]);
  }
  if (typeof value.prompt !== "string" || !value.prompt.trim() || value.prompt.length > 4000) {
    throw new ProtocolValidationError("Pending input prompt is invalid.");
  }
  value.prompt = value.prompt.trim();
  if (typeof value.allowFreeText !== "boolean" || typeof value.multiSelect !== "boolean") {
    throw new ProtocolValidationError("Pending input mode is invalid.");
  }
  if (!Array.isArray(value.options) || value.options.length > 20) {
    throw new ProtocolValidationError("Pending input options are invalid.");
  }
  const optionIds = new Set();
  value.options = value.options.map((input) => {
    const option = cloneJsonObject(input);
    option.optionId = validateIdentity(option.optionId);
    if (optionIds.has(option.optionId)) {
      throw new ProtocolValidationError("Pending input option identity is duplicated.");
    }
    optionIds.add(option.optionId);
    if (typeof option.label !== "string" || !option.label.trim() || option.label.length > 240) {
      throw new ProtocolValidationError("Pending input option label is invalid.");
    }
    option.label = option.label.trim();
    option.description = typeof option.description === "string"
      ? option.description.trim().slice(0, 600)
      : "";
    return option;
  });
  if (!value.allowFreeText && value.options.length === 0) {
    throw new ProtocolValidationError("Pending input has no answer mode.");
  }
  return value;
}

function normalizeApprovalRequest(value) {
  value.approvalRequestId = validateIdentity(value.approvalRequestId);
  for (const key of ["turnId", "taskId", "stepId"]) {
    value[key] = validateIdentity(value[key]);
  }
  if (value.presentation && typeof value.presentation === "object") {
    value.presentation = cloneJsonObject(value.presentation);
    normalizeEnumProperty(value.presentation, "reversibility", ENUMS.approvalReversibility);
  }
  return value;
}

function normalizeNeedsYouResolution(value) {
  value.requestId = validateIdentity(value.requestId);
  value.clientRequestId = validateIdentity(value.clientRequestId);
  normalizeEnumProperty(value, "outcome", ENUMS.needsYouOutcome);
  return value;
}

function normalizeStep(input) {
  const step = cloneJsonObject(input);
  normalizeEnumProperty(step, "kind", ENUMS.stepKind);
  normalizeEnumProperty(step, "status", ENUMS.stepStatus);
  normalizeEnumProperty(step, "externalEffect", ENUMS.effect);
  normalizeEnumProperty(step, "addedBy", ENUMS.stepAddedBy);
  normalizeIntegerProperty(step, "addedInPlanRevision");
  normalizeIntegerProperty(step, "cancelledInPlanRevision");
  if (Array.isArray(step.dependsOn)) step.dependsOn = step.dependsOn.map(validateIdentity);
  if (step.estimate && typeof step.estimate === "object") {
    step.estimate = cloneJsonObject(step.estimate);
    normalizeEnumProperty(step.estimate, "kind", ENUMS.estimateKind);
    normalizeIntegerProperty(step.estimate, "seconds");
  }
  if (Array.isArray(step.substeps)) {
    step.substeps = step.substeps.map((input) => {
      const substep = cloneJsonObject(input);
      substep.substepId = validateIdentity(substep.substepId);
      normalizeEnumProperty(substep, "status", ENUMS.substepStatus);
      if (typeof substep.title !== "string" || !substep.title.trim()) {
        throw new ProtocolValidationError("Actor substep title is invalid.");
      }
      substep.title = substep.title.trim().slice(0, 400);
      return substep;
    });
  }
  if (step.operation && typeof step.operation === "object") {
    step.operation = cloneJsonObject(step.operation);
    normalizeEnumProperty(step.operation, "kind", ENUMS.stepKind);
    normalizeEnumProperty(step.operation, "phase", ENUMS.operationPhase);
    normalizeIntegerProperty(step.operation, "latestProgressSequence");
    if (step.operation.key && typeof step.operation.key === "object") {
      step.operation.key = cloneJsonObject(step.operation.key);
      normalizeIntegerProperty(step.operation.key, "operationGeneration");
    }
  }
  return step;
}

function normalizePlanRevision(input) {
  const revision = cloneJsonObject(input);
  normalizeIntegerProperty(revision, "planRevision");
  if (!Number.isSafeInteger(revision.planRevision) || revision.planRevision < 1) {
    throw new ProtocolValidationError("Actor plan revision is invalid.", "NYXID_NUMBER_INVALID");
  }
  normalizeEnumProperty(revision, "revisionCause", ENUMS.planRevisionCause);
  if (!revision.revisionCause) {
    throw new ProtocolValidationError("Actor plan revision cause is invalid.", "NYXID_ENUM_INVALID");
  }
  for (const key of ["addedStepIds", "cancelledStepIds"]) {
    if (!Object.prototype.hasOwnProperty.call(revision, key)) {
      revision[key] = [];
    } else if (!Array.isArray(revision[key])) {
      throw new ProtocolValidationError("Actor plan revision step identities are invalid.");
    }
    revision[key] = revision[key].map(validateIdentity);
  }
  return revision;
}

function normalizeEnumProperty(target, key, definition) {
  if (!Object.prototype.hasOwnProperty.call(target, key) || target[key] == null || target[key] === "") {
    return;
  }
  target[key] = normalizeEnum(target[key], definition);
}

function normalizeEnum(value, definition) {
  if (typeof value === "number" && Number.isInteger(value)) {
    const resolved = definition.values[value - 1];
    if (resolved) return resolved;
  }
  if (typeof value === "string") {
    const normalized = value.trim();
    if (definition.values.includes(normalized)) return normalized;
    if (normalized.startsWith(definition.prefix)) {
      const candidate = normalized.slice(definition.prefix.length).toLowerCase();
      if (definition.values.includes(candidate)) return candidate;
    }
  }
  throw new ProtocolValidationError("Actor enum value is invalid.", "NYXID_ENUM_INVALID");
}

function normalizeIntegerProperty(target, key) {
  if (!Object.prototype.hasOwnProperty.call(target, key) || target[key] == null || target[key] === "") {
    return;
  }
  const value = Number(target[key]);
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new ProtocolValidationError("Actor numeric value is invalid.", "NYXID_NUMBER_INVALID");
  }
  target[key] = value;
}

function cloneJsonObject(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new ProtocolValidationError("Actor payload must be an object.");
  }
  return structuredClone(value);
}

export function redact(value) {
  if (Array.isArray(value)) return value.map(redact);
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value).map(([key, item]) => [
        key,
        PRIVATE_KEY.test(key)
          ? "[not displayed]"
          : SECRET_KEY.test(key)
            ? "[redacted]"
            : redact(item),
      ]),
    );
  }
  if (typeof value === "string") {
    const trimmed = value.trim();
    if ((trimmed.startsWith("{") && trimmed.endsWith("}")) ||
        (trimmed.startsWith("[") && trimmed.endsWith("]"))) {
      try {
        return JSON.stringify(redact(JSON.parse(trimmed)));
      } catch {
        // Fall through to pattern-based redaction for non-JSON tool output.
      }
    }
    return value
      .replace(
        /("?(?:authorization|api[-_]?key|token|secret|password|credential|cookie)"?\s*[:=]\s*)"?[^",\s}]+"?/gi,
        "$1\"[redacted]\"",
      )
      .replace(SECRET_VALUE, (match, prefix) => prefix ? `${prefix}[redacted]` : "nyx_[redacted]");
  }
  return value;
}

export function safeJson(value, spacing = 2) {
  try {
    return JSON.stringify(redact(value), null, spacing);
  } catch {
    return "[unserializable event]";
  }
}

export function parseArguments(value) {
  if (!value) return {};
  if (typeof value === "object") return redact(value);
  try {
    return redact(JSON.parse(value));
  } catch {
    return { value: redact(String(value)) };
  }
}

export function mergeUsage(current, incoming) {
  const supported = [
    "available",
    "promptTokens",
    "completionTokens",
    "totalTokens",
    "model",
  ];
  const next = { ...(current || {}) };
  let changed = false;
  for (const key of supported) {
    const value = incoming?.[key];
    if (value === undefined || value === null || value === "") continue;
    next[key] = value;
    changed = true;
  }
  return changed ? next : current || null;
}

export function normalizeConversationIndex(value) {
  const source = Array.isArray(value)
    ? value
    : Array.isArray(value?.conversations)
      ? value.conversations
      : [];
  return source
    .filter((item) => item && typeof item === "object")
    .map((item) => ({
      id: String(item.id || item.actorId || "").trim(),
      title: String(item.title || "未命名会话").trim() || "未命名会话",
      serviceId: String(item.serviceId || "").trim(),
      serviceKind: String(item.serviceKind || "").trim(),
      createdAt: item.createdAt || null,
      updatedAt: item.updatedAt || item.createdAt || null,
      messageCount: Number.isFinite(Number(item.messageCount)) ? Number(item.messageCount) : 0,
      llmRoute: item.llmRoute || null,
      llmModel: item.llmModel || null,
      taskStatus: item.taskStatus || null,
      attentionKind: String(item.attentionKind || "none").trim().toLowerCase() || "none",
      attentionSince: item.attentionSince || null,
      activeStepSummary: String(item.activeStepSummary || "").trim() || null,
      stateVersion: Number.isSafeInteger(Number(item.stateVersion))
        ? Number(item.stateVersion)
        : 0,
    }))
    .filter((item) => item.id)
    .sort((left, right) => {
      const leftTime = Date.parse(left.updatedAt || "") || 0;
      const rightTime = Date.parse(right.updatedAt || "") || 0;
      return rightTime - leftTime;
    });
}

export function normalizeStoredMessages(value) {
  if (!Array.isArray(value)) return [];
  return value
    .filter((item) => item && typeof item === "object")
    .map((item, index) => ({
      id: String(item.id || `history-message-${index}`),
      role: String(item.role || "assistant").toLowerCase(),
      content: String(item.content || ""),
      timestamp: Number.isFinite(Number(item.timestamp)) ? Number(item.timestamp) : 0,
      status: String(item.status || "completed"),
      error: item.error ? String(item.error) : null,
      turnId: item.turnId ? String(item.turnId) : null,
    }));
}
