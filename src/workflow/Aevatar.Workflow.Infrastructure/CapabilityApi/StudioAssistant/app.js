import "./transport.js?v=20260823-m62-studio-redesign";
import {
  consumeSse,
  mergeUsage,
  normalizeConversationIndex,
  normalizeFrame,
  normalizeStoredMessages,
  parseArguments,
  redact,
  safeJson,
  validateActionContinuation,
} from "./protocol.js?v=20260823-m62-studio-redesign";
import {
  buildConnectCardBlock,
  connectorInitial,
  splitMessageSegments,
} from "./blocks.js?v=20260823-m62-studio-redesign";
import {
  actorCan,
  applyCurrentStateResult,
  createActorProjection,
  reduceActorEvent,
  restoreCachedAction,
} from "./actor-state.js?v=20260823-m62-studio-redesign";
import { describeReadinessFailure } from "./readiness.js?v=20260823-m62-studio-redesign";

const PREFERENCES_KEY = "aevatar-studio:assistant-preferences:v4";
const SERVICE_ACCESS_REVIEW_KEY = "aevatar-studio:pending-service-access-review:v1";
const ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED_CODE =
  "NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED";
const MAX_ATTACHMENT_BYTES = 5 * 1024 * 1024;

let serviceAccessReviewResumePromise = null;

const surfaceLabels = {
  workflow: "Workflow API",
  "nyxid-chat": "Studio Assistant",
};

const surfacePaths = {
  workflow: "POST /api/chat",
  "nyxid-chat": "POST /api/chat",
};

const transportLabels = {
  "nyxid-session": "NyxID authenticated",
};

const $ = (selector) => document.querySelector(selector);
const dom = {
  actorFact: $("#actorFact"),
  attachButton: $("#attachButton"),
  attachmentChip: $("#attachmentChip"),
  attachmentName: $("#attachmentName"),
  cancelSettingsButton: $("#cancelSettingsButton"),
  clearEventsButton: $("#clearEventsButton"),
  closeComposerServicesButton: $("#closeComposerServicesButton"),
  closeInspectorButton: $("#closeInspectorButton"),
  closeSettingsButton: $("#closeSettingsButton"),
  commandFact: $("#commandFact"),
  commandFactRow: $("#commandFactRow"),
  composerForm: $("#composerForm"),
  composerWrap: $("#composerWrap"),
  composerInputOptions: $("#composerInputOptions"),
  composerInputPrompt: $("#composerInputPrompt"),
  composerInputRequest: $("#composerInputRequest"),
  composerServiceCount: $("#composerServiceCount"),
  composerServiceList: $("#composerServiceList"),
  composerServicePanel: $("#composerServicePanel"),
  composerServicesButton: $("#composerServicesButton"),
  composerStatus: $("#composerStatus"),
  connectionButton: $("#connectionButton"),
  connectionDot: $("#connectionDot"),
  connectionTest: $("#connectionTest"),
  connectionText: $("#connectionText"),
  conversationViewButton: $("#conversationViewButton"),
  conversationTitle: $("#conversationTitle"),
  accountAvatar: $("#accountAvatar"),
  accountEmail: $("#accountEmail"),
  accountName: $("#accountName"),
  authGate: $("#authGate"),
  emptyState: $("#emptyState"),
  emptyDescription: $("#emptyDescription"),
  emptyLoginButton: $("#emptyLoginButton"),
  emptyTitle: $("#emptyTitle"),
  eventCount: $("#eventCount"),
  eventList: $("#eventList"),
  eventsPanel: $("#eventsPanel"),
  eventsTabButton: $("#eventsTabButton"),
  fileInput: $("#fileInput"),
  inspector: $("#inspector"),
  mobileBackdrop: $("#mobileBackdrop"),
  mobileInspectorButton: $("#mobileInspectorButton"),
  mobileMenuButton: $("#mobileMenuButton"),
  newChatButton: $("#newChatButton"),
  observationDisconnectButton: $("#observationDisconnectButton"),
  promptInput: $("#promptInput"),
  quickActions: $("#quickActions"),
  readinessFreshness: $("#readinessFreshness"),
  readinessList: $("#readinessList"),
  readinessPanel: $("#readinessPanel"),
  readinessRecovery: $("#readinessRecovery"),
  readinessRecoveryButton: $("#readinessRecoveryButton"),
  readinessRecoveryButtonLabel: $("#readinessRecoveryButtonLabel"),
  readinessRecoveryDetail: $("#readinessRecoveryDetail"),
  readinessRecoveryTitle: $("#readinessRecoveryTitle"),
  readinessSummary: $("#readinessSummary"),
  refreshReadinessButton: $("#refreshReadinessButton"),
  refreshComposerServicesButton: $("#refreshComposerServicesButton"),
  recentGroup: $("#recentGroup"),
  recentSessionsList: $("#recentSessionsList"),
  requestTraceCount: $("#requestTraceCount"),
  requestTraceLive: $("#requestTraceLive"),
  requestTracePanel: $("#requestTracePanel"),
  needsYouCount: $("#needsYouCount"),
  needsYouFilterButton: $("#needsYouFilterButton"),
  removeAttachmentButton: $("#removeAttachmentButton"),
  routeClientState: $("#routeClientState"),
  routeLabel: $("#routeLabel"),
  routeOrnnState: $("#routeOrnnState"),
  routeSection: $("#routeSection"),
  routeSurfaceValue: $("#routeSurfaceValue"),
  routeTransportValue: $("#routeTransportValue"),
  routeUpstreamState: $("#routeUpstreamState"),
  runFact: $("#runFact"),
  runFactRow: $("#runFactRow"),
  runPanel: $("#runPanel"),
  runStatus: $("#runStatus"),
  runTabButton: $("#runTabButton"),
  sendButton: $("#sendButton"),
  serviceAccessDescription: $("#serviceAccessDescription"),
  serviceCount: $("#serviceCount"),
  serviceList: $("#serviceList"),
  servicesButton: $("#servicesButton"),
  servicesCount: $("#servicesCount"),
  runIdentityFact: $("#runIdentityFact"),
  runIdentityLabel: $("#runIdentityLabel"),
  inspectorEyebrow: $("#inspectorEyebrow"),
  inspectorTitle: $("#inspectorTitle"),
  settingsButton: $("#settingsButton"),
  settingsDialog: $("#settingsDialog"),
  settingsForm: $("#settingsForm"),
  loginButton: $("#loginButton"),
  logoutButton: $("#logoutButton"),
  sidebar: $("#sidebar"),
  sidebarRuntimeDot: $("#sidebarRuntimeDot"),
  currentSessionButton: $("#currentSessionButton"),
  sidebarSessionMeta: $("#sidebarSessionMeta"),
  sidebarSessionTitle: $("#sidebarSessionTitle"),
  sidebarSurface: $("#sidebarSurface"),
  sidebarTransport: $("#sidebarTransport"),
  stepCount: $("#stepCount"),
  stepList: $("#stepList"),
  steerButton: $("#steerButton"),
  stopButton: $("#stopButton"),
  testConnectionButton: $("#testConnectionButton"),
  thread: $("#thread"),
  toast: $("#toast"),
  toastText: $("#toastText"),
  traceClientRequestFact: $("#traceClientRequestFact"),
  traceDurationFact: $("#traceDurationFact"),
  traceEventFact: $("#traceEventFact"),
  traceInputFact: $("#traceInputFact"),
  traceOperationCount: $("#traceOperationCount"),
  traceOperationEmpty: $("#traceOperationEmpty"),
  traceOperationList: $("#traceOperationList"),
  traceOperationOverview: $("#traceOperationOverview"),
  trajectoryDetails: $("#trajectoryDetails"),
  trajectoryDetailsBody: $("#trajectoryDetailsBody"),
  trajectoryDetailsClose: $("#trajectoryDetailsClose"),
  trajectoryDetailsKind: $("#trajectoryDetailsKind"),
  trajectoryDetailsLocation: $("#trajectoryDetailsLocation"),
  trajectoryDetailsResize: $("#trajectoryDetailsResize"),
  trajectoryDetailsTabs: $("#trajectoryDetailsTabs"),
  trajectoryDurationButton: $("#trajectoryDurationButton"),
  trajectoryFoldCallsButton: $("#trajectoryFoldCallsButton"),
  trajectoryFoldCallsIcon: $("#trajectoryFoldCallsIcon"),
  trajectoryFoldRequestsButton: $("#trajectoryFoldRequestsButton"),
  trajectoryFoldRequestsIcon: $("#trajectoryFoldRequestsIcon"),
  trajectoryLedger: $("#trajectoryLedger"),
  trajectoryOverviewBoundaries: $("#trajectoryOverviewBoundaries"),
  trajectoryOverviewEmpty: $("#trajectoryOverviewEmpty"),
  trajectoryOverviewHairline: $("#trajectoryOverviewHairline"),
  trajectoryOverviewSelection: $("#trajectoryOverviewSelection"),
  trajectoryOverviewSpans: $("#trajectoryOverviewSpans"),
  trajectoryOverviewTrack: $("#trajectoryOverviewTrack"),
  trajectorySearchInput: $("#trajectorySearchInput"),
  traceOutputFact: $("#traceOutputFact"),
  traceOutputSection: $("#traceOutputSection"),
  traceReadonlyNotice: $("#traceReadonlyNotice"),
  traceStartedFact: $("#traceStartedFact"),
  traceStatusFact: $("#traceStatusFact"),
  traceToolFact: $("#traceToolFact"),
  traceViewButton: $("#traceViewButton"),
  usageElapsed: $("#usageElapsed"),
  usageModel: $("#usageModel"),
  usageTokens: $("#usageTokens"),
  workflowField: $("#workflowField"),
  workflowInput: $("#workflowInput"),
};

const state = {
  config: {
    transport: "nyxid-session",
    authMode: "site-session",
    surface: "nyxid-chat",
    directBaseUrl: "",
    proxyBaseUrl: "",
    ornnWebUrl: "",
    nyxidWebUrl: "",
    servicesUrl: "",
    scopeId: "",
    workflow: "direct",
    enableStudioWireInspector: false,
  },
  auth: { authenticated: false, user: null, resources: [] },
  services: [],
  connectors: { connected: [], available: [], loadedAt: 0 },
  readiness: { subject: "", snapshot: null, loading: false, error: null, inFlight: null },
  readinessOptionalOpen: false,
  pendingFirstTurn: null,
  historyFilter: "all",
  workflowSessionId: createId("workflow-session"),
  actorId: null,
  attachment: null,
  activeController: null,
  activeConversation: null,
  conversationStates: new Map(),
  conversations: [],
  conversationLoadSequence: 0,
  currentConversationMeta: null,
  health: null,
  historyError: null,
  historyLoading: false,
  historyRequestSequence: 0,
  historyRefreshTimer: null,
  run: createRunState(),
  traceRenderFrame: null,
  toastTimer: null,
};

function createRunState() {
  return {
    status: "idle",
    surface: null,
    config: null,
    startedAt: null,
    completedAt: null,
    context: {},
    steps: new Map(),
    tools: new Map(),
    events: [],
    usage: null,
    pendingApproval: null,
    assistantBody: null,
    activityCard: null,
    activityStatus: null,
    assistantText: "",
    textElement: null,
    progressRow: null,
    progressLabel: null,
    progressTimers: [],
    approvalCard: null,
    authorizationCards: [],
    authorizationPrompted: false,
    cardElements: new Map(),
    actionCardsElement: null,
    eventSequence: 0,
    clientRequestId: null,
    request: null,
  };
}

let conversationContext = null;

function createConversationState({ actorId = null, meta = null, title = "新会话" } = {}) {
  const thread = el("div", "conversation-view");
  thread.hidden = true;
  const entry = {
    key: createId("conversation"),
    actorId,
    workflowSessionId: createId("workflow-session"),
    actorProjection: createActorProjection(actorId),
    stateReloadInFlight: null,
    actionActorProjections: new Map(),
    actionStateReloads: new Map(),
    actionStateNotices: new Map(),
    actionStateRefreshTimers: new Map(),
    actionActorTaskElements: new Map(),
    actionActorControlReceipts: new Map(),
    actionFrameCache: new Map(),
    actorStateNotice: "",
    actorTaskElement: null,
    actorControlReceipt: null,
    actorStateRefreshTimer: null,
    actorStateRefreshGeneration: 0,
    actionStateRefreshGenerations: new Map(),
    historyRecoveredTurnId: null,
    needsYouDrafts: new Map(),
    needsYouSubmissions: new Map(),
    approvalConfirmRequestId: null,
    meta,
    title,
    draft: "",
    attachment: null,
    run: createRunState(),
    traces: new Map(),
    traceOrder: [],
    currentTraceKey: null,
    selectedTraceKey: null,
    mainView: "conversation",
    controller: null,
    controllers: new Set(),
    thread,
    scrollTop: 0,
    backgroundUi: {
      routeOrnnState: el("span"),
      routeUpstreamState: el("span"),
      sidebarSessionMeta: el("span"),
    },
  };
  state.conversationStates.set(entry.key, entry);
  dom.threadViewport.append(thread);
  return entry;
}

function createRequestTrace(entry, run) {
  const key = String(run?.clientRequestId || "").trim();
  if (!entry || !key) return null;
  const existing = entry.traces.get(key);
  if (existing) return existing;
  entry.currentTraceKey = key;
  const trace = {
    key,
    clientRequestId: key,
    serverRunId: null,
    serverTurnId: null,
    run,
    records: [],
    recordIndex: new Map(),
    selectedOperationKey: null,
    activeModelOperationKey: null,
    followLatestOperation: true,
    nextOperationSequence: 0,
    element: null,
    fields: null,
  };
  entry.traces.set(key, trace);
  entry.traceOrder.unshift(key);
  entry.selectedTraceKey = key;
  createInputTraceOperation(trace);
  return trace;
}

function currentRequestTrace(entry = state.activeConversation) {
  const key = String(entry?.currentTraceKey || entry?.run?.clientRequestId || "").trim();
  return key ? entry?.traces?.get(key) || null : null;
}

function selectedRequestTrace(entry = state.activeConversation) {
  if (!entry?.selectedTraceKey) return null;
  return entry.traces.get(entry.selectedTraceKey) || null;
}

function traceForRun(entry, run) {
  const key = String(run?.clientRequestId || "").trim();
  return key ? entry?.traces?.get(key) || null : null;
}

function currentRequestRun(entry = state.activeConversation) {
  return currentRequestTrace(entry)?.run || entry?.run || null;
}

function attachRequestTraceServerFacts(entry, run, event) {
  const trace = traceForRun(entry, run);
  if (!trace || !event) return trace;
  trace.serverRunId = event.runId || trace.serverRunId;
  trace.serverTurnId = event.turnId || trace.serverTurnId;
  return trace;
}

function isReviewingHistoricalTrace(entry = state.activeConversation) {
  const selected = selectedRequestTrace(entry);
  return Boolean(selected && selected !== currentRequestTrace(entry));
}

function ensureTraceOperationState(trace) {
  if (!trace) return null;
  if (!Array.isArray(trace.records)) trace.records = [];
  if (!(trace.recordIndex instanceof Map)) {
    trace.recordIndex = new Map(trace.records.map((record) => [record.key, record]));
  }
  if (!Number.isSafeInteger(trace.nextOperationSequence)) {
    trace.nextOperationSequence = trace.records.reduce(
      (maximum, record) => Math.max(maximum, Number(record.sequence) || 0),
      0,
    );
  }
  if (typeof trace.followLatestOperation !== "boolean") trace.followLatestOperation = true;
  if (typeof trace.typedModelLifecycleObserved !== "boolean") {
    trace.typedModelLifecycleObserved = false;
  }
  return trace;
}

function orderedTraceOperations(trace) {
  ensureTraceOperationState(trace);
  return [...(trace?.records || [])].sort((left, right) => {
    if (left.kind === "input" && right.kind !== "input") return -1;
    if (right.kind === "input" && left.kind !== "input") return 1;
    const leftHasServerSequence = Number.isSafeInteger(left.serverSequence);
    const rightHasServerSequence = Number.isSafeInteger(right.serverSequence);
    if (leftHasServerSequence !== rightHasServerSequence) return leftHasServerSequence ? -1 : 1;
    if (leftHasServerSequence && left.serverSequence !== right.serverSequence) {
      return left.serverSequence - right.serverSequence;
    }
    const localOrder = (Number(left.sequence) || 0) - (Number(right.sequence) || 0);
    return localOrder || String(left.key).localeCompare(String(right.key));
  });
}

function traceOperationKey(kind, id) {
  const normalizedKind = String(kind || "operation").trim().toLowerCase() || "operation";
  const normalizedId = String(id || "").trim();
  return `${normalizedKind}:${normalizedId || createId(normalizedKind)}`;
}

function upsertTraceOperation(trace, patch) {
  ensureTraceOperationState(trace);
  if (!trace || !patch?.kind) return null;
  const key = String(patch.key || traceOperationKey(patch.kind, patch.id)).trim();
  let record = trace.recordIndex.get(key);
  const created = !record;
  if (!record) {
    trace.nextOperationSequence += 1;
    record = {
      key,
      id: String(patch.id || key.slice(key.indexOf(":") + 1)),
      kind: String(patch.kind).toLowerCase(),
      title: "Operation",
      invocationName: "",
      description: "",
      presentation: null,
      status: "running",
      input: "",
      output: "",
      reasoning: "",
      model: "",
      provider: "",
      tools: [],
      toolCatalogCaptured: false,
      round: null,
      sessionId: "",
      finishReason: "",
      error: "",
      usage: null,
      startedAt: null,
      completedAt: null,
      timingClock: null,
      sequence: trace.nextOperationSequence,
      serverSequence: null,
      element: null,
      fields: null,
      barElement: null,
    };
    trace.records.push(record);
    trace.recordIndex.set(key, record);
  }

  for (const field of [
    "title", "invocationName", "description", "status", "input", "output", "reasoning",
    "model", "provider", "round", "sessionId", "finishReason", "error",
  ]) {
    if (patch[field] !== undefined && patch[field] !== null) record[field] = patch[field];
  }
  if (patch.presentation && typeof patch.presentation === "object") {
    record.presentation = patch.presentation;
  }
  if (Array.isArray(patch.tools)) record.tools = [...patch.tools];
  if (typeof patch.toolCatalogCaptured === "boolean") {
    record.toolCatalogCaptured = patch.toolCatalogCaptured;
  }
  const serverSequence = Number(patch.serverSequence);
  if (Number.isSafeInteger(serverSequence) && serverSequence > 0) {
    record.serverSequence = Number.isSafeInteger(record.serverSequence)
      ? Math.min(record.serverSequence, serverSequence)
      : serverSequence;
  }
  if (patch.usage) record.usage = mergeUsage(record.usage, patch.usage);
  if (patch.id) record.id = String(patch.id);
  if (created && trace.followLatestOperation) trace.selectedOperationKey = key;
  return record;
}

function createInputTraceOperation(trace) {
  if (!trace?.run) return null;
  const startedAt = Number(trace.run.startedAt);
  const record = upsertTraceOperation(trace, {
    key: traceOperationKey("input", trace.clientRequestId),
    id: trace.clientRequestId,
    kind: "input",
    title: "Input",
    status: "done",
    input: requestTraceInput(trace),
  });
  if (record && Number.isFinite(startedAt) && startedAt > 0 && record.startedAt == null) {
    record.startedAt = startedAt;
    record.timingClock = "client";
  }
  return record;
}

// ---------------------------------------------------------------------------
// Trajectory recovery.
//
// The live ledger is built from SSE frames, which are gone after a reload. Two
// committed sources restore it, and neither infers a record it did not read:
//   * stored turns  — `operations` from GET /api/chat/conversations/{id}
//   * the in-flight turn — the conversation actor's current-state projection
// Recovered containers are keyed by the server's `turnId`, because
// `clientRequestId` is a browser identity that does not survive the reload. A
// live container already owning that turn is never overwritten.
// ---------------------------------------------------------------------------

function restoredTraceKey(turnId) {
  return `turn:${String(turnId || "").trim()}`;
}

function ensureRestoredRequestTrace(entry, turnId, { prompt = "", status = "closed" } = {}) {
  const normalizedTurnId = String(turnId || "").trim();
  if (!entry || !normalizedTurnId) return null;
  for (const candidate of entry.traces.values()) {
    if (String(candidate.serverTurnId || "").trim() === normalizedTurnId) return candidate;
  }
  const key = restoredTraceKey(normalizedTurnId);
  const existing = entry.traces.get(key);
  if (existing) return existing;

  const run = createRunState();
  run.status = status;
  run.clientRequestId = key;
  run.request = { prompt: String(prompt || "") };
  run.context = { actorId: entry.actorId || "", turnId: normalizedTurnId };
  const trace = {
    key,
    clientRequestId: key,
    serverRunId: null,
    serverTurnId: normalizedTurnId,
    restored: true,
    run,
    records: [],
    recordIndex: new Map(),
    selectedOperationKey: null,
    activeModelOperationKey: null,
    followLatestOperation: false,
    nextOperationSequence: 0,
    typedModelLifecycleObserved: true,
    element: null,
    fields: null,
  };
  entry.traces.set(key, trace);
  entry.traceOrder.unshift(key);
  return trace;
}

function restoredOperationTimestamp(value) {
  const timestamp = traceServerTimestamp(value);
  return Number.isFinite(timestamp) && timestamp > 0 ? timestamp : null;
}

function restoredOperationUsage(operation) {
  const usage = {
    promptTokens: operation?.promptTokens ?? null,
    completionTokens: operation?.completionTokens ?? null,
    totalTokens: operation?.totalTokens ?? null,
  };
  return Object.values(usage).some((value) => value != null) ? usage : null;
}

function applyRestoredOperation(trace, operation, { kind, id, title }) {
  const key = traceOperationKey(kind, id);
  const tool = kind === "tool"
    ? describeToolOperation({
      toolName: operation?.toolName || title,
      presentation: operation?.presentation,
    })
    : null;
  const record = upsertTraceOperation(trace, {
    key,
    id,
    kind,
    title: tool?.title || title || traceOperationKindLabel(kind),
    invocationName: tool?.invocationName || "",
    description: tool?.description || "",
    presentation: tool?.presentation || null,
    status: String(operation?.status || "closed").toLowerCase(),
    input: operation?.inputPreview || operation?.argumentsPreview || "",
    // Tool result bodies are never archived, so a restored tool record reports
    // its status and timing but honestly has no captured output.
    output: operation?.outputPreview || "",
    model: operation?.model || "",
    provider: operation?.provider || "",
    tools: Array.isArray(operation?.availableToolNames) ? operation.availableToolNames : [],
    toolCatalogCaptured: operation?.toolCatalogCaptured === true,
    finishReason: operation?.finishReason || "",
    error: operation?.status === "error" ? operation?.safeMessage || "" : "",
    usage: restoredOperationUsage(operation),
    serverSequence: Number.isSafeInteger(operation?.order) && operation.order > 0
      ? operation.order
      : undefined,
  });
  if (!record) return null;
  record.restored = true;
  record.previewsTruncated = Boolean(operation?.previewsTruncated);
  record.startedAt = restoredOperationTimestamp(operation?.startedAt);
  record.completedAt = restoredOperationTimestamp(operation?.completedAt);
  record.timingClock = record.startedAt == null ? null : "server";
  return record;
}

function restoreTrajectoryFromStoredOperations(entry, operations, messages) {
  if (!entry || !Array.isArray(operations) || !operations.length) return;
  const promptByTurn = new Map();
  for (const message of messages || []) {
    if (message?.role !== "user" || !message.turnId) continue;
    if (!promptByTurn.has(message.turnId)) promptByTurn.set(message.turnId, message.content || "");
  }

  const grouped = new Map();
  for (const operation of operations) {
    const turnId = String(operation?.turnId || "").trim();
    if (!turnId) continue;
    if (!grouped.has(turnId)) grouped.set(turnId, []);
    grouped.get(turnId).push(operation);
  }

  for (const [turnId, turnOperations] of grouped) {
    const trace = ensureRestoredRequestTrace(entry, turnId, {
      prompt: promptByTurn.get(turnId) || "",
      status: turnOperations.some((operation) => operation?.status === "error")
        ? "error"
        : "complete",
    });
    if (!trace || !trace.restored || trace.records.length) continue;
    const ordered = [...turnOperations]
      .sort((left, right) => (Number(left?.order) || 0) - (Number(right?.order) || 0));
    const firstStart = ordered
      .map((operation) => restoredOperationTimestamp(operation?.startedAt))
      .find((value) => value != null) ?? null;
    const lastEnd = ordered
      .map((operation) => restoredOperationTimestamp(operation?.completedAt))
      .filter((value) => value != null)
      .at(-1) ?? null;
    trace.run.startedAt = firstStart;
    trace.run.completedAt = lastEnd;

    const input = createInputTraceOperation(trace);
    if (input) {
      input.restored = true;
      input.startedAt = firstStart;
      input.timingClock = firstStart == null ? null : "server";
    }
    for (const operation of ordered) {
      applyRestoredOperation(trace, operation, {
        kind: operation?.kind === "model" ? "model" : "tool",
        id: operation?.operationId || `${turnId}:${operation?.order ?? 0}`,
        title: operation?.title,
      });
    }
  }
}

function restoreTrajectoryFromActorProjection(entry, projection) {
  const task = projection?.task;
  const turnId = String(task?.turnId || "").trim();
  if (!entry || !turnId || !(projection?.steps instanceof Map)) return;
  const steps = [...projection.steps.values()]
    .filter((step) => step?.operation?.requestedAt &&
      (step.kind === "llm" || step.kind === "tool"));
  if (!steps.length) return;

  const trace = ensureRestoredRequestTrace(entry, turnId, {
    prompt: projection?.activeTurn?.turnId === turnId
      ? projection?.activeTurn?.prompt || ""
      : projection?.latestTurn?.prompt || "",
    status: task?.status === "failed" ? "error" : "running",
  });
  if (!trace || !trace.restored) return;

  const firstStart = steps
    .map((step) => restoredOperationTimestamp(step.operation.requestedAt))
    .find((value) => value != null) ?? null;
  if (trace.run.startedAt == null) trace.run.startedAt = firstStart;
  const input = createInputTraceOperation(trace);
  if (input && input.startedAt == null) {
    input.restored = true;
    input.startedAt = firstStart;
    input.timingClock = firstStart == null ? null : "server";
  }

  for (const step of steps) {
    applyRestoredOperation(trace, {
      status: step.status,
      title: step.kind === "tool" ? step.source?.tool?.toolName : step.source?.llm?.model,
      toolName: step.source?.tool?.toolName || "",
      presentation: step.source?.tool?.presentation || null,
      model: step.source?.llm?.model || "",
      startedAt: step.operation.requestedAt,
      completedAt: step.operation.completedAt,
      safeMessage: step.safeMessage,
      order: step.order,
    }, {
      kind: step.kind === "llm" ? "model" : "tool",
      id: step.operation.operationId || step.stepId,
      title: step.kind === "tool"
        ? step.source?.tool?.toolName
        : step.source?.llm?.model || step.description,
    });
  }
}

function selectedTraceOperation(trace) {
  ensureTraceOperationState(trace);
  if (!trace?.records.length) return null;
  const selected = trace.recordIndex.get(trace.selectedOperationKey);
  if (selected) return selected;
  const fallback = orderedTraceOperations(trace).at(-1);
  trace.selectedOperationKey = fallback.key;
  return fallback;
}

function traceServerTimestamp(value) {
  if (value == null || value === "") return null;
  if (typeof value === "number") {
    if (!Number.isFinite(value) || value <= 0) return null;
    return value < 1e12 ? value * 1000 : value;
  }
  if (typeof value === "string") {
    const numeric = Number(value);
    if (Number.isFinite(numeric) && numeric > 0) return numeric < 1e12 ? numeric * 1000 : numeric;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  if (typeof value === "object") {
    const seconds = Number(value.seconds ?? value.Seconds);
    const nanos = Number(value.nanos ?? value.Nanos ?? 0);
    return Number.isFinite(seconds) && seconds > 0
      ? seconds * 1000 + (Number.isFinite(nanos) ? nanos / 1e6 : 0)
      : null;
  }
  return null;
}

function traceEventTiming(event) {
  const serverAt = traceServerTimestamp(
    event?.timestamp ?? event?.terminalTime ?? event?.terminal_time ?? event?.raw?.timestamp,
  );
  return { serverAt };
}

function traceModelOperation(trace, event, { create = false } = {}) {
  ensureTraceOperationState(trace);
  if (!trace) return null;
  const rawId = String(event?.operationId || event?.messageId || event?.sessionId || "").trim();
  const id = rawId.startsWith("msg:") ? rawId.slice(4) : rawId;
  const explicitKey = id ? traceOperationKey("model", id) : null;
  let record = explicitKey ? trace.recordIndex.get(explicitKey) : null;
  if (!record && !explicitKey && trace.activeModelOperationKey) {
    record = trace.recordIndex.get(trace.activeModelOperationKey) || null;
  }
  if (record || !create) return record;
  const resolvedId = id || createId("model-response");
  record = upsertTraceOperation(trace, {
    key: traceOperationKey("model", resolvedId),
    id: resolvedId,
    kind: "model",
    title: "LLM response",
    status: "running",
  });
  trace.activeModelOperationKey = record?.key || null;
  return record;
}

function traceStreamingModelOperation(trace, event, { create = false } = {}) {
  if (trace?.activeModelOperationKey) {
    const active = trace.recordIndex.get(trace.activeModelOperationKey);
    if (active) return active;
  }
  const explicit = traceModelOperation(trace, event);
  if (explicit) return explicit;
  if (create && trace?.typedModelLifecycleObserved) return null;
  return create ? traceModelOperation(trace, event, { create: true }) : null;
}

function traceOperationRound(value, fallback) {
  if (value === null || value === undefined || value === "") return fallback;
  const round = Number(value);
  return Number.isSafeInteger(round) ? round : fallback;
}

function normalizedToolText(value, limit = 180) {
  return typeof value === "string"
    ? value.replace(/\s+/g, " ").trim().slice(0, limit)
    : "";
}

function containsOpaqueToolInvocation(value) {
  return /\bnyxop_[0-9a-f]{24,}\b/i.test(String(value || ""));
}

function readableToolInvocationName(value) {
  const name = normalizedToolText(value);
  if (!name || containsOpaqueToolInvocation(name)) return "连接服务操作";
  return name.replace(/[_./:-]+/g, " ").replace(/\s+/g, " ").trim() || "工具操作";
}

function nyxIdToolPresentationSource(presentation) {
  if (!presentation || typeof presentation !== "object") return null;
  const direct = presentation.nyxIdOperation;
  if (direct && typeof direct === "object") return direct;
  const sourceRef = presentation.sourceRef;
  return sourceRef?.nyxIdOperation && typeof sourceRef.nyxIdOperation === "object"
    ? sourceRef.nyxIdOperation
    : null;
}

function describeToolOperation(value = {}) {
  const presentation = value?.presentation && typeof value.presentation === "object"
    ? value.presentation
    : null;
  const source = nyxIdToolPresentationSource(presentation);
  const invocationName = normalizedToolText(
    presentation?.invocationName || value?.invocationName || value?.toolName,
  );
  const presentedName = normalizedToolText(presentation?.displayName);
  const displayName = presentedName && !containsOpaqueToolInvocation(presentedName)
    ? presentedName
    : readableToolInvocationName(invocationName);
  const serviceLabel = normalizedToolText(
    source?.connectionLabel || source?.connectorDisplayName ||
    source?.catalogServiceSlug || source?.serviceSlug,
  );
  const title = serviceLabel && !displayName.toLocaleLowerCase().includes(serviceLabel.toLocaleLowerCase())
    ? `${serviceLabel} · ${displayName}`
    : displayName;
  return {
    invocationName,
    displayName,
    serviceLabel,
    title,
    description: normalizedToolText(presentation?.description, 320),
    kind: normalizedToolText(presentation?.kind),
    presentation,
  };
}

function toolActivityRunningCopy(tool) {
  return tool.serviceLabel
    ? `正在通过 ${tool.serviceLabel} 执行 ${tool.displayName}…`
    : `正在执行 ${tool.displayName}…`;
}

function traceToolOperation(trace, event, { create = false } = {}) {
  ensureTraceOperationState(trace);
  if (!trace) return null;
  const id = String(event?.toolCallId || event?.callId || "").trim();
  const key = id ? traceOperationKey("tool", id) : null;
  const existing = key ? trace.recordIndex.get(key) : null;
  if (existing || !create) return existing;
  const resolvedId = id || createId("tool-call");
  const presentation = describeToolOperation(event);
  return upsertTraceOperation(trace, {
    key: traceOperationKey("tool", resolvedId),
    id: resolvedId,
    kind: "tool",
    title: presentation.title,
    invocationName: presentation.invocationName,
    description: presentation.description,
    presentation: presentation.presentation,
    status: "running",
  });
}

function traceTerminalOperationStatus(event) {
  if (event?.success == null && !event?.status && !event?.outcome && !event?.error) return "closed";
  const status = String(event?.status || event?.outcome || "").toUpperCase();
  return event?.success === false || /(ERROR|FAILED|DENIED)/.test(status) ? "error" : "done";
}

function closeUnfinishedTraceOperations(trace, status) {
  ensureTraceOperationState(trace);
  for (const record of trace?.records || []) {
    if (record.status !== "running") continue;
    record.status = status;
  }
  trace.activeModelOperationKey = null;
}

function applyRoleChatTraceSnapshot(trace, event) {
  const existingModels = orderedTraceOperations(trace).filter((record) => record.kind === "model");
  const model = existingModels.at(-1) || traceModelOperation(trace, event, { create: true });
  if (model) {
    if (!model.output && event.content) model.output = String(event.content);
    if (event.model) {
      model.model = String(event.model);
      model.title = String(event.model);
    }
    if (event.usage) model.usage = mergeUsage(model.usage, event.usage);
    if (model.status === "running") model.status = traceTerminalOperationStatus(event);
  }

  const calls = Array.isArray(event.toolCalls) ? event.toolCalls : [];
  const receipts = Array.isArray(event.toolReceipts) ? event.toolReceipts : [];
  const receiptsById = new Map(receipts.map((receipt) => [receipt.callId, receipt]));
  for (const call of calls) {
    const receipt = receiptsById.get(call.callId);
    const tool = traceToolOperation(trace, {
      toolCallId: call.callId,
      toolName: call.toolName,
      presentation: call.presentation,
    }, { create: true });
    if (!tool) continue;
    const presentation = describeToolOperation(call);
    tool.title = presentation.title || tool.title;
    tool.invocationName = presentation.invocationName || tool.invocationName;
    tool.description = presentation.description || tool.description;
    tool.presentation = presentation.presentation || tool.presentation;
    if (!tool.input && call.argumentsJson) tool.input = String(call.argumentsJson);
    if (!tool.output && receipt?.resultJson) tool.output = String(receipt.resultJson);
    if (receipt) tool.status = traceTerminalOperationStatus(receipt);
  }
  for (const receipt of receipts) {
    const tool = traceToolOperation(trace, {
      toolCallId: receipt.callId,
      toolName: receipt.toolName,
      presentation: receipt.presentation,
    }, { create: true });
    if (!tool) continue;
    const presentation = describeToolOperation(receipt);
    tool.title = presentation.title || tool.title;
    tool.invocationName = presentation.invocationName || tool.invocationName;
    tool.description = presentation.description || tool.description;
    tool.presentation = presentation.presentation || tool.presentation;
    if (!tool.output && receipt.resultJson) tool.output = String(receipt.resultJson);
    tool.status = traceTerminalOperationStatus(receipt);
  }
}

function applyRequestTraceEvent(entry, run, event) {
  const trace = traceForRun(entry, run);
  if (!trace || !event) return;
  const timing = traceEventTiming(event);
  switch (event.type) {
    case "model_start": {
      trace.typedModelLifecycleObserved = true;
      const model = traceModelOperation(trace, event, { create: true });
      if (!model) break;
      const round = traceOperationRound(event.round, model.round);
      upsertTraceOperation(trace, {
        key: model.key,
        kind: "model",
        title: event.model || (round == null ? "LLM response" : `Model response ${round}`),
        model: event.model || model.model,
        provider: event.provider || model.provider,
        input: event.inputSummary || model.input,
        tools: Array.isArray(event.availableToolNames) ? event.availableToolNames : model.tools,
        toolCatalogCaptured: Array.isArray(event.availableToolNames) || model.toolCatalogCaptured,
        round,
        sessionId: event.sessionId || model.sessionId,
        serverSequence: event.sequence,
      });
      const terminal = isTraceOperationTerminal(model.status);
      if (!terminal) model.status = "running";
      startTraceOperation(model, timing);
      if (!terminal) trace.activeModelOperationKey = model.key;
      break;
    }
    case "model_end": {
      trace.typedModelLifecycleObserved = true;
      const model = traceModelOperation(trace, event, { create: true });
      if (!model) break;
      upsertTraceOperation(trace, {
        key: model.key,
        kind: "model",
        title: event.model || model.title,
        model: event.model || model.model,
        output: event.content ?? model.output,
        reasoning: event.reasoningContent ?? model.reasoning,
        round: traceOperationRound(event.round, model.round),
        sessionId: event.sessionId || model.sessionId,
        finishReason: event.finishReason || model.finishReason,
        error: event.error || model.error,
        usage: event.usage,
        serverSequence: event.sequence,
      });
      model.status = traceTerminalOperationStatus(event);
      finishTraceOperation(model, timing);
      if (trace.activeModelOperationKey === model.key) trace.activeModelOperationKey = null;
      break;
    }
    case "text_start": {
      const model = traceStreamingModelOperation(trace, event);
      if (!model) break;
      if (!isTraceOperationTerminal(model.status)) model.status = "running";
      startTraceOperation(model, timing);
      break;
    }
    case "text_delta": {
      const model = traceStreamingModelOperation(trace, event, { create: true });
      if (!model) break;
      model.output += String(event.delta || "");
      break;
    }
    case "text_end": {
      const model = traceStreamingModelOperation(trace, event);
      if (!model) break;
      if (!isTraceOperationTerminal(model.status)) {
        model.status = "done";
        finishTraceOperation(model, timing);
      }
      if (trace.activeModelOperationKey === model.key) trace.activeModelOperationKey = null;
      break;
    }
    case "tool_start": {
      const tool = traceToolOperation(trace, event, { create: true });
      if (!tool) break;
      const presentation = describeToolOperation(event);
      upsertTraceOperation(trace, {
        key: tool.key,
        kind: "tool",
        title: presentation.title,
        invocationName: presentation.invocationName,
        description: presentation.description,
        presentation: presentation.presentation,
        sessionId: event.sessionId || tool.sessionId,
        serverSequence: event.sequence,
      });
      if (!isTraceOperationTerminal(tool.status)) tool.status = "running";
      startTraceOperation(tool, timing);
      break;
    }
    case "tool_end": {
      const tool = traceToolOperation(trace, event, { create: true });
      if (!tool) break;
      if (event.presentation || event.toolName) {
        const presentation = describeToolOperation(event);
        tool.title = presentation.title || tool.title;
        tool.invocationName = presentation.invocationName || tool.invocationName;
        tool.description = presentation.description || tool.description;
        tool.presentation = presentation.presentation || tool.presentation;
      }
      if (event.argumentsJson) tool.input = String(event.argumentsJson);
      if (event.result !== undefined && event.result !== null) tool.output = String(event.result);
      else if (event.error) tool.output = String(event.error);
      tool.error = String(event.error || "");
      upsertTraceOperation(trace, {
        key: tool.key,
        kind: "tool",
        sessionId: event.sessionId || tool.sessionId,
        serverSequence: event.sequence,
      });
      tool.status = traceTerminalOperationStatus(event);
      finishTraceOperation(tool, timing);
      break;
    }
    case "usage": {
      const model = traceModelOperation(trace, event) ||
        [...trace.records].reverse().find((record) => record.kind === "model");
      if (!model) break;
      model.usage = mergeUsage(model.usage, event);
      if (event.model) {
        model.model = String(event.model);
        model.title = String(event.model);
      }
      break;
    }
    case "role_chat_completed":
      applyRoleChatTraceSnapshot(trace, event);
      {
        const model = traceModelOperation(trace, event) ||
          [...trace.records].reverse().find((record) => record.kind === "model");
        if (model) {
          const terminal = traceServerTimestamp(event.terminalTime ?? event.terminal_time);
          if (terminal != null && model.startedAt != null && model.timingClock === "server" && terminal >= model.startedAt) {
            model.completedAt = terminal;
          }
        }
      }
      break;
    case "run_finished":
      closeUnfinishedTraceOperations(trace, "closed");
      break;
    case "run_stopped":
      closeUnfinishedTraceOperations(trace, "stopped");
      break;
    case "run_error":
    case "protocol_error":
      closeUnfinishedTraceOperations(trace, "error");
      break;
    default:
      break;
  }
}

function startTraceOperation(record, timing) {
  if (!record || record.startedAt != null) return;
  if (timing?.serverAt != null) {
    record.startedAt = timing.serverAt;
    record.timingClock = "server";
  }
}

function finishTraceOperation(record, timing) {
  if (!record || record.completedAt != null) return;
  const completedAt = timing?.serverAt;
  if (!Number.isFinite(completedAt)) return;
  record.completedAt = completedAt;
  record.timingClock ||= "server";
}

function isTraceOperationTerminal(status) {
  return ["done", "error", "stopped", "closed", "blocked"].includes(String(status || ""));
}

function traceOperationDurationMs(record) {
  if (!Number.isFinite(record?.startedAt) || !Number.isFinite(record?.completedAt)) return null;
  if (record.completedAt < record.startedAt) return null;
  return record.completedAt - record.startedAt;
}

function traceOperationDuration(record) {
  const duration = traceOperationDurationMs(record);
  if (duration != null) return formatDuration(duration);
  return record?.status === "running" ? "进行中 · Duration 不可用" : "Duration 不可用";
}

function traceOperationStartedAt(record) {
  if (!Number.isFinite(record?.startedAt)) return "—";
  return new Date(record.startedAt).toLocaleTimeString("zh-CN", {
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    fractionalSecondDigits: 3,
  });
}

function traceOperationKindLabel(kind) {
  return { input: "INPUT", model: "MODEL", tool: "TOOL" }[kind] || "OPERATION";
}

function traceOperationStatusLabel(status) {
  return {
    running: "进行中",
    done: "已完成",
    error: "失败",
    stopped: "已停止",
    closed: "已结束",
    blocked: "已阻塞",
  }[String(status || "").toLowerCase()] || "状态未知";
}

// ---------------------------------------------------------------------------
// Trajectory ledger.
//
// A request is only the stable container; its Input / Model / Tool operations
// are the selectable records. One operation identity is shared by its overview
// span, its ledger row and the details pane, so all three always describe the
// same recorded facts. Timing is rendered only when the operation reported it.
// ---------------------------------------------------------------------------

const TRAJECTORY_LANE = { input: 0, model: 1, tool: 2 };
const TRAJECTORY_DETAILS_MIN_WIDTH = 240;
const TRAJECTORY_DETAILS_DEFAULT_WIDTH = 340;
const TRAJECTORY_RANGE_MIN_PX = 3;
const TRAJECTORY_TAIL_THRESHOLD_PX = 24;

const trajectory = {
  durationMode: true,
  foldCalls: false,
  foldedRequests: new Set(),
  search: "",
  range: null,
  viewport: null,
  detailsWidth: null,
  detailsTab: null,
  detailsOpen: false,
  domain: null,
  spans: [],
  rows: [],
  drag: null,
};

function traceOperationIcon(kind) {
  return { input: "corner-down-right", model: "sparkles", tool: "wrench" }[kind] || "circle";
}

function trajectoryPreview(value, limit = 220) {
  return String(value || "").replace(/\s+/g, " ").trim().slice(0, limit);
}

function trajectoryRowTitle(record) {
  if (record.kind === "input") return trajectoryPreview(record.input) || "空请求";
  if (record.kind === "model") return record.model || record.title || "LLM response";
  return describeToolOperation({
    toolName: record.invocationName || record.title,
    presentation: record.presentation,
  }).title;
}

function trajectoryRowResult(record) {
  if (record.kind === "input") return null;
  const output = trajectoryPreview(record.error || record.output || record.reasoning);
  if (output) return { text: output, error: Boolean(record.error) || record.status === "error" };
  if (record.status === "running" && record.kind === "tool") {
    return {
      text: toolActivityRunningCopy(describeToolOperation({
        toolName: record.invocationName || record.title,
        presentation: record.presentation,
      })),
      pending: true,
    };
  }
  if (record.status === "running") return { text: "等待结果", pending: true };
  if (record.status === "error") return { text: "执行失败", error: true };
  return { text: "已完成", pending: false };
}

function trajectoryLoadedToolsSummary(record, limit = 3) {
  if (record.kind !== "model" || !record.toolCatalogCaptured) return null;
  if (!Array.isArray(record.tools) || !record.tools.length) return "已加载 0";
  const names = record.tools.slice(0, limit).join(", ");
  const remaining = Math.max(0, record.tools.length - limit);
  return `已加载 ${record.tools.length} · ${names}${remaining ? ` +${remaining}` : ""}`;
}

function trajectoryRequestSummary(records) {
  const models = records.filter((record) => record.kind === "model").length;
  const tools = records.filter((record) => record.kind === "tool").length;
  const parts = [`${records.length} 条记录`];
  if (models) parts.push(`${models} 次模型响应`);
  if (tools) parts.push(`${tools} 次工具调用`);
  return parts.join(" · ");
}

function trajectoryCollapsedCallsSummary(tools) {
  const failed = tools.filter((tool) => tool.status === "error").length;
  const names = [...new Set(tools.map(trajectoryRowTitle).filter(Boolean))].slice(0, 3).join(", ");
  const summary = `${tools.length} 次工具调用${names ? ` · ${names}` : ""}`;
  return failed ? `${summary} · ${failed} 失败` : summary;
}

function trajectorySearchText(row) {
  if (row.type === "request") {
    return `${row.number} ${row.records.map((record) => record.title).join(" ")}`.toLowerCase();
  }
  const record = row.record;
  return [
    traceOperationKindLabel(record.kind),
    trajectoryRowTitle(record),
    record.model,
    record.provider,
    record.input,
    record.output,
    record.reasoning,
    record.error,
    ...(record.tools || []),
  ].filter(Boolean).join(" ").toLowerCase();
}

function orderedRequestTraces(entry) {
  return [...(entry?.traceOrder || [])]
    .map((key) => entry?.traces?.get(key))
    .filter(Boolean)
    .reverse();
}

function buildTrajectoryRows(entry) {
  const rows = [];
  orderedRequestTraces(entry).forEach((trace, index) => {
    const records = orderedTraceOperations(trace);
    const number = index + 1;
    if (!records.length) return;
    if (trajectory.foldedRequests.has(trace.key) && records.length > 1) {
      rows.push({
        type: "request",
        key: `request:${trace.key}`,
        trace,
        number,
        records,
        requestStart: true,
        requestEnd: true,
      });
      return;
    }
    let cursor = 0;
    let first = true;
    while (cursor < records.length) {
      const record = records[cursor];
      const collapsedCalls = [];
      if (trajectory.foldCalls && record.kind === "model") {
        let lookahead = cursor + 1;
        while (lookahead < records.length && records[lookahead].kind === "tool") {
          collapsedCalls.push(records[lookahead]);
          lookahead += 1;
        }
      }
      rows.push({
        type: "operation",
        key: `${trace.key} ${record.key}`,
        trace,
        number,
        record,
        collapsedCalls,
        requestStart: first,
        requestEnd: false,
      });
      first = false;
      cursor += 1 + collapsedCalls.length;
    }
    const last = rows.at(-1);
    if (last) last.requestEnd = true;
  });
  return rows;
}

function trajectoryVisibleRecords(rows) {
  return rows.flatMap((row) => (row.type === "operation"
    ? [row.record, ...row.collapsedCalls]
    : row.records));
}

function trajectorySpanRange(record) {
  if (!Number.isFinite(record.startedAt)) return null;
  const end = Number.isFinite(record.completedAt) && record.completedAt >= record.startedAt
    ? record.completedAt
    : null;
  return { start: record.startedAt, end };
}

function buildTrajectorySpans(rows) {
  const records = trajectoryVisibleRecords(rows);
  if (!records.length) return { domain: null, spans: [] };
  if (!trajectory.durationMode) {
    return {
      domain: { start: 0, end: records.length, mode: "sequence" },
      spans: records.map((record, index) => ({
        record,
        start: index,
        end: index,
        equal: true,
      })),
    };
  }
  const timed = records
    .map((record) => ({ record, range: trajectorySpanRange(record) }))
    .filter((candidate) => candidate.range !== null);
  if (!timed.length) return { domain: null, spans: [] };
  const start = Math.min(...timed.map((candidate) => candidate.range.start));
  const end = Math.max(
    start + 1,
    ...timed.map((candidate) => candidate.range.end ?? candidate.range.start),
  );
  return {
    domain: { start, end, mode: "duration" },
    spans: timed.map((candidate) => ({
      record: candidate.record,
      start: candidate.range.start,
      end: candidate.range.end,
      equal: false,
    })),
  };
}

function trajectoryViewport() {
  const domain = trajectory.domain;
  if (!domain) return null;
  const viewport = trajectory.viewport;
  if (!viewport) return { start: domain.start, end: domain.end };
  const width = Math.max(1e-6, viewport.end - viewport.start);
  const clampedStart = Math.max(domain.start, Math.min(viewport.start, domain.end - width));
  return { start: clampedStart, end: clampedStart + width };
}

function trajectoryRequestBoundaries(rows) {
  const boundaries = [];
  for (const row of rows) {
    if (!row.requestStart) continue;
    const first = row.type === "operation" ? row.record : row.records[0];
    if (Number.isFinite(first?.startedAt)) boundaries.push({ at: first.startedAt, number: row.number });
  }
  return boundaries;
}

function trajectoryRecordInRange(record) {
  if (trajectory.range === null) return true;
  const { start, end } = trajectory.range;
  if (!trajectory.durationMode) {
    const index = trajectory.spans.findIndex((span) => span.record === record);
    return index >= 0 && index >= Math.floor(start) && index <= Math.ceil(end);
  }
  if (!Number.isFinite(record.startedAt)) return false;
  const recordEnd = Number.isFinite(record.completedAt) ? record.completedAt : record.startedAt;
  return record.startedAt <= end && recordEnd >= start;
}

function trajectoryRowInRange(row) {
  if (trajectory.range === null) return true;
  const records = row.type === "operation" ? [row.record, ...row.collapsedCalls] : row.records;
  return records.some((record) => trajectoryRecordInRange(record));
}

function selectedTrajectoryRecord(entry) {
  const trace = selectedRequestTrace(entry);
  return trace ? selectedTraceOperation(trace) : null;
}

function selectTraceOperation(entry, trace, key, { focusRow = false } = {}) {
  ensureTraceOperationState(trace);
  if (!entry || !trace?.recordIndex.has(key)) return;
  entry.selectedTraceKey = trace.key;
  trace.selectedOperationKey = key;
  trace.followLatestOperation = false;
  trajectory.detailsOpen = true;
  if (entry !== state.activeConversation) return;
  renderRequestTraces(entry);
  renderInspector();
  renderEventLog();
  if (focusRow) trace.recordIndex.get(key)?.element?.focus();
}

function moveTraceOperationSelection(event, entry) {
  if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
  event.preventDefault();
  const operations = trajectory.rows.filter((row) => row.type === "operation");
  if (!operations.length) return;
  const trace = selectedRequestTrace(entry);
  const currentKey = trace?.selectedOperationKey;
  const index = Math.max(0, operations.findIndex((row) =>
    row.trace === trace && row.record.key === currentKey));
  const nextIndex = event.key === "Home"
    ? 0
    : event.key === "End"
      ? operations.length - 1
      : event.key === "ArrowUp"
        ? Math.max(0, index - 1)
        : Math.min(operations.length - 1, index + 1);
  const next = operations[nextIndex];
  selectTraceOperation(entry, next.trace, next.record.key, { focusRow: true });
}

function createTrajectoryOperationRow(entry, row) {
  const record = row.record;
  const element = el("tr", "trajectory-row");
  element.tabIndex = 0;
  element.dataset.kind = record.kind;
  element.dataset.operationKey = record.key;
  element.dataset.traceKey = row.trace.key;

  const event = el("td", "trajectory-event");
  const requestRail = el("span", "trajectory-request-rail");
  const selectionRail = el("span", "trajectory-selection-rail");
  const requestLabel = el("button", "trajectory-request-label");
  requestLabel.type = "button";
  const eventInner = el("div", "trajectory-event-inner");
  const kindSlot = el("span", "trajectory-kind-slot");
  const kindTag = el("span", "trajectory-kind-tag");
  kindTag.dataset.kind = record.kind;
  kindTag.append(iconNode(traceOperationIcon(record.kind)), el("span", "", traceOperationKindLabel(record.kind)));
  kindSlot.append(kindTag);
  eventInner.append(kindSlot);
  event.append(requestRail, selectionRail, requestLabel, eventInner);

  const content = el("td", "trajectory-content");
  const inner = el("div", "trajectory-content-inner");
  const time = el("span", "trajectory-row-time");
  const text = el("span", "trajectory-content-text");
  const title = el("span", "trajectory-content-title");
  const tools = el("span", "trajectory-content-tools");
  const arrow = el("span", "trajectory-content-arrow", "→");
  const result = el("span", "trajectory-content-result");
  text.append(title, tools, arrow, result);
  inner.append(text, time);
  content.append(inner);

  element.append(event, content);
  record.element = element;
  record.fields = { requestRail, selectionRail, requestLabel, kindTag, title, tools, arrow, result, time };
  element.addEventListener("click", () => {
    selectTraceOperation(entry, row.trace, record.key);
  });
  element.addEventListener("keydown", (keyEvent) => {
    if (keyEvent.key === "Enter" || keyEvent.key === " ") {
      keyEvent.preventDefault();
      selectTraceOperation(entry, row.trace, record.key);
      return;
    }
    moveTraceOperationSelection(keyEvent, entry);
  });
  requestLabel.addEventListener("click", (clickEvent) => {
    clickEvent.stopPropagation();
    toggleTrajectoryRequest(entry, row.trace.key);
  });
  return element;
}

function updateTrajectoryOperationRow(entry, row, searchQuery) {
  const record = row.record;
  const element = record.element || createTrajectoryOperationRow(entry, row);
  const trace = row.trace;
  const selected = entry.selectedTraceKey === trace.key && trace.selectedOperationKey === record.key;
  const activeRequest = entry.selectedTraceKey === trace.key;
  element.dataset.selected = String(selected);
  element.dataset.status = record.status || "running";
  element.dataset.requestStart = String(Boolean(row.requestStart));
  element.dataset.requestEnd = String(Boolean(row.requestEnd));
  element.dataset.inRange = String(trajectoryRowInRange(row));
  element.dataset.searchMatch = String(!searchQuery || trajectorySearchText(row).includes(searchQuery));
  element.setAttribute("aria-selected", String(selected));

  const fields = record.fields;
  fields.requestRail.hidden = !activeRequest;
  fields.selectionRail.hidden = !selected;
  fields.requestLabel.hidden = !row.requestStart;
  if (row.requestStart) {
    fields.requestLabel.textContent = `Req ${row.number}`;
    fields.requestLabel.dataset.active = String(activeRequest);
    fields.requestLabel.title = trajectory.foldedRequests.has(trace.key)
      ? `展开请求 ${row.number}`
      : `折叠请求 ${row.number}`;
  }

  const collapsed = row.collapsedCalls.length > 0;
  fields.title.textContent = trajectoryRowTitle(record);
  fields.title.classList.toggle("mono", record.kind === "tool");
  const loadedTools = trajectoryLoadedToolsSummary(record);
  fields.tools.hidden = loadedTools === null;
  fields.tools.textContent = loadedTools || "";
  fields.tools.title = loadedTools === null ? "" : record.tools.join("\n");
  const result = collapsed
    ? { text: trajectoryCollapsedCallsSummary(row.collapsedCalls), pending: false }
    : trajectoryRowResult(record);
  fields.arrow.hidden = result === null;
  fields.result.hidden = result === null;
  if (result !== null) {
    fields.result.textContent = result.text;
    fields.result.dataset.error = String(Boolean(result.error));
    fields.result.dataset.pending = String(Boolean(result.pending));
  }
  const duration = traceOperationDurationMs(record);
  fields.time.textContent = duration == null
    ? (record.status === "running" ? "运行中" : "—")
    : formatDuration(duration);
  element.title = `${traceOperationKindLabel(record.kind)} · ${trajectoryRowTitle(record)} · ${traceOperationDuration(record)}`;
  return element;
}

function createTrajectoryRequestRow(entry, row) {
  const element = el("tr", "trajectory-row");
  element.tabIndex = 0;
  element.dataset.kind = "request";
  element.dataset.traceKey = row.trace.key;

  const event = el("td", "trajectory-event");
  const requestRail = el("span", "trajectory-request-rail");
  const requestLabel = el("button", "trajectory-request-label");
  requestLabel.type = "button";
  const eventInner = el("div", "trajectory-event-inner");
  const kindSlot = el("span", "trajectory-kind-slot");
  const kindTag = el("span", "trajectory-kind-tag");
  kindTag.dataset.kind = "request";
  kindTag.append(iconNode("waypoints"), el("span", "", "REQUEST"));
  kindSlot.append(kindTag);
  eventInner.append(kindSlot);
  event.append(requestRail, requestLabel, eventInner);

  const content = el("td", "trajectory-content");
  const collapsedText = el("span", "trajectory-collapsed");
  const ellipsis = el("span", "trajectory-collapsed-ellipsis", "…");
  const summary = el("span", "trajectory-collapsed-text");
  collapsedText.append(ellipsis, summary);
  content.append(collapsedText);

  element.append(event, content);
  row.trace.summaryElement = element;
  row.trace.summaryFields = { requestRail, requestLabel, summary };
  const expand = () => toggleTrajectoryRequest(entry, row.trace.key);
  element.addEventListener("click", expand);
  element.addEventListener("keydown", (keyEvent) => {
    if (keyEvent.key !== "Enter" && keyEvent.key !== " ") return;
    keyEvent.preventDefault();
    expand();
  });
  return element;
}

function updateTrajectoryRequestRow(entry, row, searchQuery) {
  const element = row.trace.summaryElement || createTrajectoryRequestRow(entry, row);
  const activeRequest = entry.selectedTraceKey === row.trace.key;
  element.dataset.selected = "false";
  element.dataset.status = row.trace.run?.status || "idle";
  element.dataset.requestStart = "true";
  element.dataset.requestEnd = "true";
  element.dataset.inRange = String(trajectoryRowInRange(row));
  element.dataset.searchMatch = String(!searchQuery || trajectorySearchText(row).includes(searchQuery));
  const fields = row.trace.summaryFields;
  fields.requestRail.hidden = !activeRequest;
  fields.requestLabel.textContent = `Req ${row.number}`;
  fields.requestLabel.dataset.active = String(activeRequest);
  fields.requestLabel.title = `展开请求 ${row.number}`;
  fields.summary.textContent = trajectoryRequestSummary(row.records);
  element.title = `请求 ${row.number} 已折叠 · ${trajectoryRequestSummary(row.records)}`;
  return element;
}

function toggleTrajectoryRequest(entry, key) {
  if (trajectory.foldedRequests.has(key)) trajectory.foldedRequests.delete(key);
  else trajectory.foldedRequests.add(key);
  renderRequestTraces(entry);
}

function renderTrajectoryLedger(entry, rows) {
  const body = dom.traceOperationList;
  if (!body) return;
  const searchQuery = trajectory.search.trim().toLowerCase();
  if (!rows.length) {
    body.replaceChildren();
    if (dom.traceOperationEmpty) {
      dom.traceOperationEmpty.classList.remove("hidden");
      dom.traceOperationEmpty.querySelector("strong").textContent = "尚无操作记录";
    }
    return;
  }
  dom.traceOperationEmpty?.classList.add("hidden");
  const ledger = dom.trajectoryLedger;
  const atTail = ledger
    ? ledger.scrollHeight - ledger.clientHeight - ledger.scrollTop <= TRAJECTORY_TAIL_THRESHOLD_PX
    : false;
  const needsIcons = rows.some((row) => (row.type === "operation"
    ? !row.record.element
    : !row.trace.summaryElement));
  const elements = rows.map((row) => (row.type === "operation"
    ? updateTrajectoryOperationRow(entry, row, searchQuery)
    : updateTrajectoryRequestRow(entry, row, searchQuery)));
  elements.forEach((element, index) => {
    const current = body.children[index];
    if (current !== element) body.insertBefore(element, current || null);
  });
  while (body.childElementCount > elements.length) body.lastElementChild.remove();
  if (needsIcons) refreshIcons(body);
  const visible = elements.some((element) => element.dataset.searchMatch !== "false");
  if (dom.traceOperationEmpty) {
    dom.traceOperationEmpty.classList.toggle("hidden", visible);
    dom.traceOperationEmpty.querySelector("strong").textContent = "没有匹配的记录";
  }
  if (atTail && ledger) ledger.scrollTop = ledger.scrollHeight;
}

function createTrajectorySpanElement(entry, span) {
  const element = el("button", "trajectory-span");
  element.type = "button";
  element.dataset.kind = span.record.kind;
  element.dataset.operationKey = span.record.key;
  element.addEventListener("click", (event) => {
    event.stopPropagation();
    const trace = trajectory.rows.find((row) =>
      row.type === "operation" && row.record === span.record)?.trace;
    if (trace) selectTraceOperation(entry, trace, span.record.key);
  });
  element.addEventListener("pointerdown", (event) => { event.stopPropagation(); });
  return element;
}

function applyTrajectoryProjection(rows) {
  const projection = buildTrajectorySpans(rows);
  trajectory.domain = projection.domain;
  trajectory.spans = projection.spans;
  if (trajectory.domain === null) {
    trajectory.viewport = null;
    trajectory.range = null;
  }
}

function renderTrajectoryOverview(entry, rows) {
  const track = dom.trajectoryOverviewTrack;
  if (!track) return;
  const projection = { domain: trajectory.domain, spans: trajectory.spans };
  const searchQuery = trajectory.search.trim().toLowerCase();
  const viewport = trajectoryViewport();
  const empty = !projection.domain || !projection.spans.length;
  dom.trajectoryOverviewEmpty?.classList.toggle("hidden", !empty);
  if (empty) {
    dom.trajectoryOverviewSpans?.replaceChildren();
    dom.trajectoryOverviewBoundaries?.replaceChildren();
    dom.trajectoryOverviewSelection?.classList.add("hidden");
    if (dom.trajectoryOverviewEmpty) {
      dom.trajectoryOverviewEmpty.textContent = trajectory.durationMode
        ? "尚无记录的时序"
        : "尚无操作记录";
    }
    return;
  }
  const width = Math.max(1e-6, viewport.end - viewport.start);
  const percent = (value) => ((value - viewport.start) / width) * 100;
  const selectedRecord = selectedTrajectoryRecord(entry);
  const spanElements = projection.spans.map((span) => {
    const element = span.record.barElement || createTrajectorySpanElement(entry, span);
    span.record.barElement = element;
    const left = percent(span.start);
    const spanWidth = span.equal
      ? 0
      : span.end === null ? 0 : percent(span.end) - left;
    element.style.setProperty("--trajectory-span-left", `${left}%`);
    element.style.setProperty("--trajectory-span-width", `${Math.max(0, spanWidth)}%`);
    element.style.setProperty("--trajectory-span-lane", String(TRAJECTORY_LANE[span.record.kind] ?? 0));
    element.dataset.equalDuration = String(span.equal);
    element.dataset.timing = span.equal ? "sequence" : span.end === null ? "start-only" : "recorded";
    element.dataset.status = span.record.status || "running";
    element.dataset.current = String(selectedRecord === span.record);
    element.dataset.inRange = String(trajectoryRecordInRange(span.record));
    element.dataset.searchMatch = String(
      !searchQuery || trajectorySearchText({ type: "operation", record: span.record }).includes(searchQuery),
    );
    const label = `${traceOperationKindLabel(span.record.kind)} · ${trajectoryRowTitle(span.record)} · ${traceOperationDuration(span.record)}`;
    element.title = label;
    element.setAttribute("aria-label", label);
    return element;
  });
  dom.trajectoryOverviewSpans?.replaceChildren(...spanElements);

  const boundaries = trajectory.durationMode
    ? trajectoryRequestBoundaries(rows)
    : rows.flatMap((row, index) => (row.requestStart && index > 0
      ? [{ at: index, number: row.number }]
      : []));
  dom.trajectoryOverviewBoundaries?.replaceChildren(...boundaries.map((boundary) => {
    const element = el("span", "trajectory-request-boundary");
    element.style.setProperty("--trajectory-boundary-left", `${percent(boundary.at)}%`);
    return element;
  }));

  const selection = dom.trajectoryOverviewSelection;
  if (selection) {
    const range = trajectory.range;
    selection.classList.toggle("hidden", range === null);
    if (range !== null) {
      const bounds = track.getBoundingClientRect();
      const left = (percent(range.start) / 100) * bounds.width;
      const right = (percent(range.end) / 100) * bounds.width;
      selection.style.setProperty("--trajectory-selection-left", `${Math.min(left, right)}px`);
      selection.style.setProperty("--trajectory-selection-width", `${Math.abs(right - left)}px`);
    }
  }
}

function trajectoryDomainAt(clientX) {
  const track = dom.trajectoryOverviewTrack;
  const viewport = trajectoryViewport();
  if (!track || !viewport) return null;
  const bounds = track.getBoundingClientRect();
  if (bounds.width <= 0) return null;
  const ratio = Math.max(0, Math.min(1, (clientX - bounds.left) / bounds.width));
  return viewport.start + ratio * (viewport.end - viewport.start);
}

function bindTrajectoryOverview() {
  const track = dom.trajectoryOverviewTrack;
  if (!track) return;
  track.addEventListener("contextmenu", (event) => { event.preventDefault(); });
  track.addEventListener("pointerdown", (event) => {
    const at = trajectoryDomainAt(event.clientX);
    if (at === null) return;
    if (event.button === 2) {
      trajectory.drag = { mode: "pan", pointerId: event.pointerId, clientX: event.clientX, moved: false };
      track.dataset.panning = "true";
    } else if (event.button === 0) {
      trajectory.drag = { mode: "range", pointerId: event.pointerId, clientX: event.clientX, anchor: at };
    } else return;
    track.setPointerCapture(event.pointerId);
    event.preventDefault();
  });
  track.addEventListener("pointermove", (event) => {
    const hairline = dom.trajectoryOverviewHairline;
    const bounds = track.getBoundingClientRect();
    if (hairline) {
      hairline.classList.remove("hidden");
      hairline.style.setProperty("--trajectory-hairline-left", `${event.clientX - bounds.left}px`);
    }
    const drag = trajectory.drag;
    if (drag === null || drag.pointerId !== event.pointerId) return;
    if (drag.mode === "pan") {
      const viewport = trajectoryViewport();
      const domain = trajectory.domain;
      if (!viewport || !domain || bounds.width <= 0) return;
      const delta = ((event.clientX - drag.clientX) / bounds.width) * (viewport.end - viewport.start);
      drag.clientX = event.clientX;
      drag.moved = true;
      const span = viewport.end - viewport.start;
      const start = Math.max(domain.start, Math.min(viewport.start - delta, domain.end - span));
      trajectory.viewport = { start, end: start + span };
      renderRequestTraces();
      return;
    }
    const at = trajectoryDomainAt(event.clientX);
    if (at === null) return;
    trajectory.range = { start: Math.min(drag.anchor, at), end: Math.max(drag.anchor, at) };
    drag.clientXEnd = event.clientX;
    renderRequestTraces();
  });
  const endDrag = (event) => {
    const drag = trajectory.drag;
    if (drag === null || drag.pointerId !== event.pointerId) return;
    trajectory.drag = null;
    delete track.dataset.panning;
    if (track.hasPointerCapture(event.pointerId)) track.releasePointerCapture(event.pointerId);
    if (drag.mode === "pan" && !drag.moved) {
      trajectory.range = null;
      renderRequestTraces();
      return;
    }
    if (drag.mode !== "range") return;
    if (Math.abs((drag.clientXEnd ?? drag.clientX) - drag.clientX) < TRAJECTORY_RANGE_MIN_PX) {
      trajectory.range = null;
    }
    renderRequestTraces();
  };
  track.addEventListener("pointerup", endDrag);
  track.addEventListener("pointercancel", endDrag);
  track.addEventListener("pointerleave", () => {
    dom.trajectoryOverviewHairline?.classList.add("hidden");
  });
  track.addEventListener("wheel", (event) => {
    const domain = trajectory.domain;
    const viewport = trajectoryViewport();
    if (!domain || !viewport) return;
    const focus = trajectoryDomainAt(event.clientX);
    if (focus === null) return;
    event.preventDefault();
    const domainWidth = domain.end - domain.start;
    const scale = event.deltaY > 0 ? 1.25 : 0.8;
    const width = Math.max(
      domainWidth / 200,
      Math.min(domainWidth, (viewport.end - viewport.start) * scale),
    );
    const ratio = (focus - viewport.start) / Math.max(1e-6, viewport.end - viewport.start);
    const start = Math.max(domain.start, Math.min(focus - ratio * width, domain.end - width));
    trajectory.viewport = width >= domainWidth ? null : { start, end: start + width };
    renderRequestTraces();
  }, { passive: false });
}

function trajectoryDetailTabs(record) {
  const tabs = [{ id: "overview", label: "概览" }];
  if (record.input || (record.kind === "model" && record.toolCatalogCaptured)) {
    tabs.push({ id: "input", label: "输入" });
  }
  if (record.output || record.reasoning || record.error) tabs.push({ id: "output", label: "输出" });
  if (Number.isFinite(record.startedAt)) tabs.push({ id: "timing", label: "计时" });
  return tabs;
}

function trajectoryFactList(pairs) {
  const list = el("dl", "trajectory-facts");
  for (const [term, value, mono] of pairs) {
    if (value === null || value === undefined || value === "") continue;
    const row = el("div");
    const dd = el("dd", mono ? "mono" : "", String(value));
    dd.title = String(value);
    row.append(el("dt", "", term), dd);
    list.append(row);
  }
  return list;
}

function trajectoryPayloadGroup(label, value, { truncated = false } = {}) {
  const group = el("div", "trajectory-payload-group");
  const heading = el("span", "", label);
  // A restored preview is a stored fragment, never the complete payload.
  if (truncated) heading.append(el("i", "trajectory-payload-note", "已截断的存档预览"));
  group.append(heading, el("pre", "trajectory-payload", value));
  return group;
}

function renderTrajectoryDetailBody(record, trace) {
  const tab = trajectory.detailsTab;
  const truncated = Boolean(record.previewsTruncated);
  if (tab === "input") {
    const body = el("div");
    if (record.input) body.append(trajectoryPayloadGroup("Input", record.input, { truncated }));
    if (record.kind === "model" && record.toolCatalogCaptured) {
      body.append(trajectoryPayloadGroup("本轮加载工具", record.tools?.length ? record.tools.join("\n") : "无"));
    }
    if (!body.childElementCount) body.append(el("p", "trajectory-payload-empty", "未捕获输入"));
    return body;
  }
  if (tab === "output") {
    const body = el("div");
    if (record.output) body.append(trajectoryPayloadGroup("Output", record.output, { truncated }));
    if (record.reasoning) body.append(trajectoryPayloadGroup("Reasoning", record.reasoning));
    if (record.error) body.append(trajectoryPayloadGroup("Error", record.error));
    if (!body.childElementCount) body.append(el("p", "trajectory-payload-empty", "未捕获输出"));
    return body;
  }
  if (tab === "timing") {
    return trajectoryFactList([
      ["开始", traceOperationStartedAt(record)],
      ["结束", Number.isFinite(record.completedAt)
        ? traceOperationStartedAt({ startedAt: record.completedAt })
        : "不可用"],
      ["Duration", traceOperationDuration(record)],
      ["时钟", record.timingClock === "server" ? "服务端上报" : record.timingClock === "client" ? "浏览器发起" : "不可用"],
    ]);
  }
  const tool = record.kind === "tool"
    ? describeToolOperation({
      toolName: record.invocationName || record.title,
      presentation: record.presentation,
    })
    : null;
  return trajectoryFactList([
    ["状态", traceOperationStatusLabel(record.status)],
    ["动作", tool?.displayName || null],
    ["连接", tool?.serviceLabel || null],
    ["说明", tool?.description || null],
    ["内部 Operation", record.id || record.key, true],
    ["内部 Tool", record.kind === "tool" ? record.invocationName || null : null, true],
    ["开始", traceOperationStartedAt(record)],
    ["Duration", traceOperationDuration(record)],
    ["模型", record.kind === "model" ? record.model || "未上报" : null],
    ["Provider", record.kind === "model" ? record.provider || "未上报" : null],
    ["加载工具", record.kind === "model"
      ? record.toolCatalogCaptured ? record.tools?.length || 0 : "未上报"
      : null],
    ["Round", record.round == null ? null : String(record.round)],
    ["Finish reason", record.finishReason || null],
    ["Input tokens", record.usage?.promptTokens ?? null],
    ["Output tokens", record.usage?.completionTokens ?? null],
    ["Total tokens", record.usage?.totalTokens ?? null],
    ["Session", record.sessionId || null, true],
    ["Turn", trace?.serverTurnId || null, true],
    ["Request", trace?.restored ? null : trace?.clientRequestId || null, true],
    ["Run", trace?.serverRunId || trace?.run?.context?.runId || null, true],
    ["来源", trace?.restored ? "已存档轨迹" : null],
  ]);
}

function setTrajectoryDetailsWidth(width) {
  const split = dom.trajectoryDetails?.parentElement;
  if (!split) return;
  split.style.setProperty(
    "--trajectory-details-width",
    `${width ?? TRAJECTORY_DETAILS_DEFAULT_WIDTH}px`,
  );
}

function renderTrajectoryDetails(entry) {
  const panel = dom.trajectoryDetails;
  if (!panel) return;
  const trace = selectedRequestTrace(entry);
  const record = trajectory.detailsOpen ? selectedTrajectoryRecord(entry) : null;
  panel.classList.toggle("hidden", !record);
  if (!record) return;
  setTrajectoryDetailsWidth(trajectory.detailsWidth);
  dom.trajectoryDetailsKind.textContent = traceOperationKindLabel(record.kind);
  dom.trajectoryDetailsKind.dataset.kind = record.kind;
  const requestNumber = trajectory.rows.find((row) => row.trace === trace)?.number;
  const location = `${requestNumber ? `Req ${requestNumber} · ` : ""}${trajectoryRowTitle(record)}`;
  dom.trajectoryDetailsLocation.textContent = location;
  dom.trajectoryDetailsLocation.title = location;

  const tabs = trajectoryDetailTabs(record);
  if (!tabs.some((tab) => tab.id === trajectory.detailsTab)) trajectory.detailsTab = tabs[0].id;
  dom.trajectoryDetailsTabs.replaceChildren(...tabs.map((tab) => {
    const button = el("button", "trajectory-details-tab", tab.label);
    button.type = "button";
    button.setAttribute("role", "tab");
    button.setAttribute("aria-selected", String(trajectory.detailsTab === tab.id));
    button.addEventListener("click", () => {
      trajectory.detailsTab = tab.id;
      renderTrajectoryDetails(entry);
    });
    return button;
  }));
  dom.trajectoryDetailsBody.replaceChildren(renderTrajectoryDetailBody(record, trace));
}

function updateTrajectoryToolbar(entry) {
  const traces = orderedRequestTraces(entry);
  const foldable = traces.filter((trace) => orderedTraceOperations(trace).length > 1);
  const allFolded = foldable.length > 0 && foldable.every((trace) => trajectory.foldedRequests.has(trace.key));
  if (dom.trajectoryDurationButton) {
    dom.trajectoryDurationButton.setAttribute("aria-pressed", String(trajectory.durationMode));
    dom.trajectoryDurationButton.title = trajectory.durationMode ? "切换为等宽排布" : "按记录耗时排布";
  }
  if (dom.trajectoryFoldRequestsButton) {
    dom.trajectoryFoldRequestsButton.setAttribute("aria-pressed", String(allFolded));
    dom.trajectoryFoldRequestsButton.title = allFolded ? "展开全部请求" : "折叠全部请求";
    dom.trajectoryFoldRequestsIcon.textContent = allFolded ? "⊞" : "⊟";
  }
  if (dom.trajectoryFoldCallsButton) {
    dom.trajectoryFoldCallsButton.setAttribute("aria-pressed", String(trajectory.foldCalls));
    dom.trajectoryFoldCallsButton.title = trajectory.foldCalls ? "展开工具调用" : "折叠模型记录下的工具调用";
    dom.trajectoryFoldCallsIcon.textContent = trajectory.foldCalls ? "⊞" : "⊟";
  }
}

function toggleAllTrajectoryRequests(entry) {
  const foldable = orderedRequestTraces(entry).filter((trace) => orderedTraceOperations(trace).length > 1);
  const allFolded = foldable.length > 0 && foldable.every((trace) => trajectory.foldedRequests.has(trace.key));
  for (const trace of foldable) {
    if (allFolded) trajectory.foldedRequests.delete(trace.key);
    else trajectory.foldedRequests.add(trace.key);
  }
  renderRequestTraces(entry);
}

function bindTrajectoryControls() {
  dom.trajectoryDurationButton?.addEventListener("click", () => {
    trajectory.durationMode = !trajectory.durationMode;
    trajectory.range = null;
    trajectory.viewport = null;
    renderRequestTraces();
  });
  dom.trajectoryFoldRequestsButton?.addEventListener("click", () => {
    toggleAllTrajectoryRequests(state.activeConversation);
  });
  dom.trajectoryFoldCallsButton?.addEventListener("click", () => {
    trajectory.foldCalls = !trajectory.foldCalls;
    renderRequestTraces();
  });
  dom.trajectorySearchInput?.addEventListener("input", (event) => {
    trajectory.search = event.currentTarget.value;
    renderRequestTraces();
  });
  dom.trajectoryDetailsClose?.addEventListener("click", () => {
    trajectory.detailsOpen = false;
    renderRequestTraces();
  });
  bindTrajectoryDetailsResize();
  bindTrajectoryOverview();
}

function bindTrajectoryDetailsResize() {
  const handle = dom.trajectoryDetailsResize;
  const panel = dom.trajectoryDetails;
  if (!handle || !panel) return;
  let drag = null;
  const clamp = (width) => {
    const split = panel.parentElement;
    const max = split ? split.getBoundingClientRect().width * 0.7 : width;
    return Math.max(TRAJECTORY_DETAILS_MIN_WIDTH, Math.min(width, max));
  };
  handle.addEventListener("pointerdown", (event) => {
    if (event.button !== 0) return;
    drag = { pointerId: event.pointerId, clientX: event.clientX, width: panel.getBoundingClientRect().width };
    handle.setPointerCapture(event.pointerId);
    event.preventDefault();
  });
  handle.addEventListener("pointermove", (event) => {
    if (drag === null || drag.pointerId !== event.pointerId) return;
    trajectory.detailsWidth = clamp(drag.width + drag.clientX - event.clientX);
    setTrajectoryDetailsWidth(trajectory.detailsWidth);
  });
  const stop = (event) => {
    if (drag === null || drag.pointerId !== event.pointerId) return;
    drag = null;
    if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId);
  };
  handle.addEventListener("pointerup", stop);
  handle.addEventListener("pointercancel", stop);
  handle.addEventListener("dblclick", () => {
    trajectory.detailsWidth = null;
    setTrajectoryDetailsWidth(null);
  });
  handle.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    event.preventDefault();
    const direction = event.key === "ArrowLeft" ? 1 : -1;
    trajectory.detailsWidth = clamp(panel.getBoundingClientRect().width + direction * 24);
    setTrajectoryDetailsWidth(trajectory.detailsWidth);
  });
}

function requestTraceInput(trace) {
  const prompt = String(trace?.run?.request?.prompt || "").trim();
  const attachmentName = String(trace?.run?.request?.attachment?.name || "").trim();
  if (prompt && attachmentName) return `${prompt} · 附件 ${attachmentName}`;
  return prompt || (attachmentName ? `附件 ${attachmentName}` : "空请求");
}

function requestTraceOutput(run) {
  return String(run?.assistantText || "").trim();
}

function requestTraceStatusLabel(status) {
  return {
    idle: "待命",
    running: "进行中",
    complete: "已完成",
    blocked: "已阻塞",
    error: "失败",
    stopped: "已停止",
    closed: "已结束",
  }[String(status || "idle").toLowerCase()] || "状态未知";
}

function requestTraceStatusEnglish(status) {
  return {
    idle: "Ready",
    running: "Running",
    complete: "Complete",
    blocked: "Blocked",
    error: "Error",
    stopped: "Stopped",
    closed: "Closed",
  }[String(status || "idle").toLowerCase()] || "Unknown";
}

function requestTraceDuration(trace) {
  const run = trace?.run;
  if (!run?.startedAt) return "—";
  if (!run.completedAt) return run.status === "running" ? "进行中" : "—";
  return formatDuration(Math.max(0, run.completedAt - run.startedAt));
}

function requestTraceStartTime(trace, { detailed = false } = {}) {
  const startedAt = trace?.run?.startedAt;
  if (!startedAt) return "—";
  const date = new Date(startedAt);
  if (Number.isNaN(date.getTime())) return "—";
  return detailed
    ? date.toLocaleString("zh-CN", { hour12: false })
    : date.toLocaleTimeString("zh-CN", { hour12: false, hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function renderRequestTraces(entry = state.activeConversation) {
  if (!entry || entry !== state.activeConversation || !dom.traceOperationList) return;
  const traces = orderedRequestTraces(entry);
  const activeTrace = currentRequestTrace(entry);
  const receiving = Boolean(entry.controllers?.size && activeTrace?.run?.status === "running");
  const liveCopy = receiving
    ? "SSE 实时更新"
    : activeTrace?.run?.startedAt
      ? "SSE 已结束"
      : "本页会话";
  dom.requestTraceCount.textContent = String(traces.length);
  dom.requestTraceLive.classList.toggle("active", receiving);
  dom.requestTraceLive.querySelector("span").textContent = liveCopy;
  if (!entry.traces.has(entry.selectedTraceKey)) {
    entry.selectedTraceKey = activeTrace?.key || traces.at(-1)?.key || null;
  }
  const rows = buildTrajectoryRows(entry);
  trajectory.rows = rows;
  if (dom.traceOperationCount) {
    dom.traceOperationCount.textContent = String(
      traces.reduce((total, trace) => total + orderedTraceOperations(trace).length, 0),
    );
  }
  applyTrajectoryProjection(rows);
  updateTrajectoryToolbar(entry);
  renderTrajectoryLedger(entry, rows);
  renderTrajectoryOverview(entry, rows);
  renderTrajectoryDetails(entry);
}

function queueRequestTraceRender(entry = conversationContext || state.activeConversation) {
  if (!entry || entry !== state.activeConversation || state.traceRenderFrame != null) return;
  state.traceRenderFrame = requestAnimationFrame(() => {
    state.traceRenderFrame = null;
    const activeEntry = state.activeConversation;
    renderRequestTraces(activeEntry);
    renderInspector();
    if (!dom.eventsPanel?.classList.contains("hidden")) renderEventLog();
  });
}

function switchWorkspaceView(view, { focusButton = false } = {}) {
  const entry = state.activeConversation;
  if (!entry) return;
  const traceView = view === "traces";
  entry.mainView = traceView ? "traces" : "conversation";
  if (traceView && !entry.traces.has(entry.selectedTraceKey)) {
    entry.selectedTraceKey = entry.traceOrder.find((key) => entry.traces.has(key)) || null;
  }
  if (!traceView) {
    entry.selectedTraceKey = currentRequestTrace(entry)?.key || null;
  }
  dom.threadViewport.classList.toggle("hidden", traceView);
  dom.requestTracePanel.classList.toggle("hidden", !traceView);
  dom.composerWrap.classList.toggle("hidden", traceView);
  dom.conversationViewButton.classList.toggle("active", !traceView);
  dom.traceViewButton.classList.toggle("active", traceView);
  dom.conversationViewButton.setAttribute("aria-selected", String(!traceView));
  dom.traceViewButton.setAttribute("aria-selected", String(traceView));
  dom.conversationViewButton.tabIndex = traceView ? -1 : 0;
  dom.traceViewButton.tabIndex = traceView ? 0 : -1;
  renderRequestTraces(entry);
  renderInspector();
  renderEventLog();
  renderActorControlUi();
  if (!traceView) {
    requestAnimationFrame(() => {
      dom.threadViewport.scrollTop = entry.scrollTop;
    });
  }
  if (focusButton) (traceView ? dom.traceViewButton : dom.conversationViewButton).focus();
}

function moveWorkspaceView(event) {
  if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
  event.preventDefault();
  const traces = event.key === "ArrowRight" || event.key === "End";
  switchWorkspaceView(traces ? "traces" : "conversation", { focusButton: true });
}

function persistConversationState(entry = state.activeConversation) {
  if (!entry) return;
  entry.actorId = state.actorId;
  entry.workflowSessionId = state.workflowSessionId;
  entry.meta = state.currentConversationMeta;
  entry.attachment = state.attachment;
  entry.run = state.run;
  entry.controller = state.activeController;
  if (entry === state.activeConversation && isActiveConversationContext()) {
    entry.draft = dom.promptInput.value;
  }
}

function restoreConversationState(entry) {
  state.actorId = entry.actorId;
  state.workflowSessionId = entry.workflowSessionId;
  state.currentConversationMeta = entry.meta;
  state.attachment = entry.attachment;
  state.run = currentRequestRun(entry) || entry.run;
  state.activeController = entry.controller;
}

function withConversationState(entry, callback) {
  if (!entry) return callback();
  if (conversationContext === entry) return callback();

  // Frame handlers are synchronous, so legacy render helpers can be routed to
  // the owning conversation without exposing background state to the active UI.
  const previousContext = conversationContext;
  const active = state.activeConversation;
  if (entry === active) {
    conversationContext = entry;
    try {
      return callback();
    } finally {
      persistConversationState(entry);
      conversationContext = previousContext;
    }
  }

  persistConversationState(active);
  const snapshot = {
    actorId: state.actorId,
    workflowSessionId: state.workflowSessionId,
    currentConversationMeta: state.currentConversationMeta,
    attachment: state.attachment,
    run: state.run,
    activeController: state.activeController,
    thread: dom.thread,
    routeOrnnState: dom.routeOrnnState,
    routeUpstreamState: dom.routeUpstreamState,
    sidebarSessionMeta: dom.sidebarSessionMeta,
  };
  conversationContext = entry;
  restoreConversationState(entry);
  dom.thread = entry.thread;
  dom.routeOrnnState = entry.backgroundUi.routeOrnnState;
  dom.routeUpstreamState = entry.backgroundUi.routeUpstreamState;
  dom.sidebarSessionMeta = entry.backgroundUi.sidebarSessionMeta;
  try {
    return callback();
  } finally {
    persistConversationState(entry);
    state.actorId = snapshot.actorId;
    state.workflowSessionId = snapshot.workflowSessionId;
    state.currentConversationMeta = snapshot.currentConversationMeta;
    state.attachment = snapshot.attachment;
    state.run = snapshot.run;
    state.activeController = snapshot.activeController;
    dom.thread = snapshot.thread;
    dom.routeOrnnState = snapshot.routeOrnnState;
    dom.routeUpstreamState = snapshot.routeUpstreamState;
    dom.sidebarSessionMeta = snapshot.sidebarSessionMeta;
    conversationContext = previousContext;
  }
}

function isActiveConversationContext() {
  return !conversationContext || conversationContext === state.activeConversation;
}

function findConversationState(actorId) {
  return Array.from(state.conversationStates.values()).find((entry) => entry.actorId === actorId) || null;
}

function activateConversationState(entry) {
  if (!entry) return;
  persistConversationState();
  if (state.activeConversation) {
    state.activeConversation.scrollTop = dom.threadViewport.scrollTop;
    state.activeConversation.thread.hidden = true;
  }
  state.activeConversation = entry;
  entry.thread.hidden = false;
  dom.thread = entry.thread;
  restoreConversationState(entry);
  dom.promptInput.value = entry.draft;
  autoResizeComposer();
  renderAttachment();
  switchWorkspaceView(entry.mainView);
  requestAnimationFrame(() => {
    dom.threadViewport.scrollTop = entry.scrollTop;
  });
}

function removeConversationState(entry) {
  if (!entry) return;
  abortConversationRun(entry);
  state.conversationStates.delete(entry.key);
  entry.thread.remove();
}

function initializeConversationStates() {
  dom.threadViewport = dom.thread;
  const emptyState = dom.emptyState;
  dom.threadViewport.replaceChildren();
  const initial = createConversationState();
  initial.thread.append(emptyState);
  initial.thread.hidden = false;
  state.activeConversation = initial;
  dom.thread = initial.thread;
  restoreConversationState(initial);
}

function createId(prefix) {
  const id = globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random()}`;
  return `${prefix}-${id}`;
}

function conversationStateVersion(entry) {
  const projectionVersion = Number.isSafeInteger(entry?.actorProjection?.stateVersion) && entry.actorProjection.stateVersion > 0
    ? entry.actorProjection.stateVersion
    : 0;
  const metaVersion = Number.isSafeInteger(entry?.meta?.stateVersion) && entry.meta.stateVersion > 0
    ? entry.meta.stateVersion
    : 0;
  return Math.max(projectionVersion, metaVersion);
}

function ensureConversationProjectionVersion(entry) {
  if (!entry) return null;
  const projection = entry.actorProjection || createActorProjection(entry.actorId || null);
  const projectionVersion = Number.isSafeInteger(projection.stateVersion) && projection.stateVersion > 0
    ? projection.stateVersion
    : 0;
  const metaVersion = Number.isSafeInteger(entry.meta?.stateVersion) && entry.meta.stateVersion > 0
    ? entry.meta.stateVersion
    : 0;
  const nextVersion = Math.max(projectionVersion, metaVersion);
  if (nextVersion > projectionVersion) projection.stateVersion = nextVersion;
  entry.actorProjection = projection;
  return projection;
}

function reliableConversationStateVersion(entry) {
  return conversationStateVersion(entry);
}

function actorProjectionFor(entry, actorId = entry?.actorId) {
  if (!entry) return null;
  if (!actorId || actorId === entry.actorId) {
    if (!entry.actorProjection || (!entry.actorProjection.actorId && actorId)) {
      entry.actorProjection = createActorProjection(actorId || null);
    }
    return entry.actorProjection;
  }
  entry.actionActorProjections ||= new Map();
  let projection = entry.actionActorProjections.get(actorId);
  if (!projection) {
    projection = createActorProjection(actorId);
    entry.actionActorProjections.set(actorId, projection);
  }
  return projection;
}

function setActorProjectionFor(entry, actorId, projection) {
  if (!entry || !projection) return projection;
  if (!actorId || actorId === entry.actorId) {
    entry.actorProjection = projection;
  } else {
    entry.actionActorProjections ||= new Map();
    entry.actionActorProjections.set(actorId, projection);
  }
  return projection;
}

function actorIdForEvent(entry, event, { streamActorId = null } = {}) {
  if (event?.type === "action_request") return event.actionRequest?.actorId || null;
  return streamActorId || event?.payload?.actorId || entry?.actorId || null;
}

function reduceActorEventForEntry(entry, event, options = {}) {
  const actorId = actorIdForEvent(entry, event, options);
  if (!entry || !actorId) return null;
  const projection = actorProjectionFor(entry, actorId);
  const routedEvent = event.type === "action_request" && actorId !== entry.actorId
    ? { ...event, sequence: projection.progressSequence }
    : event;
  const next = reduceActorEvent(projection, routedEvent);
  setActorProjectionFor(entry, actorId, next);
  return { actorId, projection: next };
}

function adoptRunStartedConversationActor(
  entry,
  actorId,
  { preserveConversationActor = false } = {},
) {
  if (!entry || !actorId || preserveConversationActor) return false;
  entry.actorId = actorId;
  state.actorId = actorId;
  if (!entry.actorProjection?.actorId) entry.actorProjection = createActorProjection(actorId);
  return true;
}

function actorStateVersion(entry, actorId = entry?.actorId) {
  const projection = actorProjectionFor(entry, actorId);
  const projectionVersion = Number.isSafeInteger(projection?.stateVersion) && projection.stateVersion > 0
    ? projection.stateVersion
    : 0;
  return actorId === entry?.actorId
    ? Math.max(projectionVersion, conversationStateVersion(entry))
    : projectionVersion;
}

function actorProjections(entry) {
  if (!entry) return [];
  const projections = [actorProjectionFor(entry, entry.actorId)];
  for (const projection of entry.actionActorProjections?.values?.() || []) {
    if (projection && projection !== projections[0]) projections.push(projection);
  }
  return projections.filter(Boolean);
}

initializeConversationStates();

function refreshIcons(root = document) {
  if (globalThis.lucide?.createIcons) {
    globalThis.lucide.createIcons({ attrs: { "aria-hidden": "true" }, root });
  }
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

async function init() {
  configureMarkdown();
  bindEvents();
  refreshIcons();
  try {
    const response = await fetch("/api/demo/config", { cache: "no-store" });
    const remote = await response.json();
    state.config = {
      ...state.config,
      ...remote,
      surface: "nyxid-chat",
      workflow: "direct",
      transport: remote.transport || "nyxid-session",
      scopeId: "",
    };
  } catch (error) {
    showToast(`无法读取 demo 配置：${error.message}`);
  }
  await refreshAuthSession({ includeServices: true });
  updateConfigUi();
  renderInspector();
  if (state.auth.authenticated) await refreshRuntimeData();
}

function bindEvents() {
  globalThis.AevatarStudioAuth.onServiceAccessReviewResult?.((result) => {
    void handleServiceAccessReviewResult(result);
  });
  dom.conversationViewButton.addEventListener("click", () => switchWorkspaceView("conversation"));
  dom.traceViewButton.addEventListener("click", () => switchWorkspaceView("traces"));
  dom.conversationViewButton.addEventListener("keydown", moveWorkspaceView);
  dom.traceViewButton.addEventListener("keydown", moveWorkspaceView);
  bindTrajectoryControls();
  dom.composerForm.addEventListener("submit", (event) => {
    event.preventDefault();
    void submitComposer();
  });
  dom.promptInput.addEventListener("input", () => {
    if (state.activeConversation) state.activeConversation.draft = dom.promptInput.value;
    const pending = activePendingInputContext();
    if (pending && dom.promptInput.value.trim() && pending.draft.selectedOptionIds.size) {
      pending.draft.selectedOptionIds.clear();
      renderComposerInputRequest(pending.entry, pending.projection);
    }
    autoResizeComposer();
    renderActorControlUi();
  });
  dom.promptInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      void submitComposer();
    }
  });
  dom.stopButton.addEventListener("click", () => {
    if (state.config.surface === "nyxid-chat") void submitActorControl("stop");
    else cancelRun();
  });
  dom.steerButton.addEventListener("click", () => {
    const instruction = dom.promptInput.value.trim();
    if (instruction) void submitActorControl("steer", null, instruction);
  });
  dom.observationDisconnectButton.addEventListener("click", cancelObservation);
  dom.newChatButton.addEventListener("click", newChat);
  dom.currentSessionButton.addEventListener("click", focusCurrentConversation);
  dom.settingsButton.addEventListener("click", openSettings);
  dom.servicesButton.addEventListener("click", openSettings);
  dom.composerServicesButton.addEventListener("click", toggleComposerServices);
  dom.closeComposerServicesButton.addEventListener("click", closeComposerServices);
  dom.refreshComposerServicesButton.addEventListener("click", () => void loadServices());
  dom.refreshReadinessButton.addEventListener("click", () => void loadReadiness({ fresh: true }));
  dom.readinessRecoveryButton.addEventListener("click", () => {
    const action = dom.readinessRecoveryButton.dataset.action;
    if (action === "login") beginLogin();
    else if (action === "account") openSettings();
    else void loadReadiness({ fresh: true });
  });
  dom.needsYouFilterButton.addEventListener("click", () => {
    state.historyFilter = state.historyFilter === "needs-you" ? "all" : "needs-you";
    renderHistoryList();
  });
  window.addEventListener("focus", () => {
    if (!state.auth.authenticated || !readinessNeedsRefresh()) return;
    void loadReadiness({ fresh: true });
  });
  dom.connectionButton.addEventListener("click", openSettings);
  dom.closeSettingsButton.addEventListener("click", closeSettings);
  dom.cancelSettingsButton.addEventListener("click", closeSettings);
  dom.settingsForm.addEventListener("submit", saveSettings);
  dom.settingsForm.querySelectorAll('input[name="surface"]').forEach((input) => {
    input.addEventListener("change", updateSettingsVisibility);
  });
  dom.testConnectionButton.addEventListener("click", () => {
    if (state.auth.authenticated) void checkConnection(readSettingsForm(), true);
    else beginLogin();
  });
  dom.loginButton.addEventListener("click", beginLogin);
  dom.emptyLoginButton.addEventListener("click", beginLogin);
  dom.logoutButton.addEventListener("click", () => void logout());
  dom.attachButton.addEventListener("click", () => dom.fileInput.click());
  dom.fileInput.addEventListener("change", () => void selectAttachment());
  dom.removeAttachmentButton.addEventListener("click", clearAttachment);
  dom.runTabButton.addEventListener("click", () => setInspectorTab("run"));
  dom.eventsTabButton.addEventListener("click", () => setInspectorTab("events"));
  dom.clearEventsButton.addEventListener("click", clearEvents);
  dom.mobileMenuButton.addEventListener("click", () => openMobilePanel("sidebar"));
  dom.mobileInspectorButton.addEventListener("click", () => openMobilePanel("inspector"));
  dom.closeInspectorButton.addEventListener("click", closeMobilePanels);
  dom.mobileBackdrop.addEventListener("click", closeMobilePanels);
  document.querySelectorAll("[data-prompt]").forEach((button) => {
    button.addEventListener("click", () => void sendPrompt(button.dataset.prompt || ""));
  });
  document.addEventListener("pointerdown", (event) => {
    if (dom.composerServicePanel.classList.contains("hidden")) return;
    if (dom.composerServicePanel.contains(event.target) || dom.composerServicesButton.contains(event.target)) return;
    closeComposerServices();
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeComposerServices();
  });
  setInterval(updateElapsed, 1000);
}

function configureMarkdown() {
  globalThis.marked?.setOptions?.({
    gfm: true,
    breaks: true,
  });
}

function readStorage(key) {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function readJsonStorage(key) {
  const value = readStorage(key);
  if (!value) return null;
  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
}

function writeStorage(key, value) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Storage may be disabled; the demo remains usable for the current page.
  }
}

function removeStorage(key) {
  try {
    localStorage.removeItem(key);
  } catch {
    // Storage may be disabled; the current page can still finish the live journey.
  }
}

function beginLogin() {
  void globalThis.AevatarStudioAuth.beginLogin()
    .catch((error) => showToast(error.message || "无法开始 NyxID 登录"));
}

function openServiceManagement(card = null) {
  const target = state.config.servicesUrl || new URL("/keys", state.config.nyxidWebUrl).toString();
  const opened = window.open(target, "nyxid-services");
  if (opened) {
    try {
      opened.opener = null;
    } catch {
      // The external NyxID window may already be cross-origin.
    }
    opened.focus?.();
  } else {
    window.location.assign(target);
  }
  if (!card) return;
  card.status = "configuring";
  card.statusMessage = "在 NyxID 中配置 service 后，返回这里刷新状态。";
  renderServiceAuthorizationCard(card);
}

async function refreshAuthSession({ includeServices = false } = {}) {
  let readinessSubjectChanged = false;
  try {
    const response = await fetch("/api/auth/session", { cache: "no-store" });
    const payload = await response.json();
    state.auth = payload.authenticated
      ? payload
      : { authenticated: false, user: null, resources: [] };
    state.config.scopeId = payload.scopeId || "";
    const readinessSubject = state.auth.authenticated
      ? state.config.scopeId || String(state.auth.user?.id || "authenticated")
      : "";
    if (readinessSubject !== state.readiness.subject) {
      state.pendingFirstTurn = null;
      state.readiness = {
        subject: readinessSubject,
        snapshot: null,
        loading: false,
        error: null,
        inFlight: null,
      };
      readinessSubjectChanged = true;
    }
    if (state.auth.authenticated && includeServices) await loadServices();
    if (!state.auth.authenticated) {
      state.services = [];
      state.connectors = { connected: [], available: [], loadedAt: 0 };
    }
  } catch {
    state.auth = { authenticated: false, user: null, resources: [] };
    state.services = [];
    state.connectors = { connected: [], available: [], loadedAt: 0 };
    state.readiness = { subject: "", snapshot: null, loading: false, error: null, inFlight: null };
    state.pendingFirstTurn = null;
    state.config.scopeId = "";
  }
  renderAuthUi();
  renderReadiness();
  configureWireInspector();
  if (readinessSubjectChanged && state.auth.authenticated) await loadReadiness();
  return state.auth;
}

async function loadReadiness({ fresh = false } = {}) {
  if (!state.auth.authenticated) return null;
  const readiness = state.readiness;
  if (readiness.inFlight) return readiness.inFlight;
  const load = fetch(`/api/nyxid/readiness${fresh ? "?fresh=1" : ""}`, { cache: "no-store" });
  readiness.inFlight = load;
  readiness.loading = true;
  readiness.error = null;
  renderReadiness();
  try {
    const response = await load;
    if (!response.ok) throw await responseError(response);
    const snapshot = await response.json();
    if (state.readiness !== readiness) return null;
    readiness.snapshot = snapshot;
  } catch (error) {
    if (state.readiness !== readiness) return null;
    readiness.snapshot = null;
    readiness.error = error;
  } finally {
    readiness.inFlight = null;
    readiness.loading = false;
    if (state.readiness === readiness) {
      renderReadiness();
      renderActorProjection(state.activeConversation);
    }
  }
  if (state.readiness !== readiness) return null;
  if (!firstTurnReadinessBlocked()) resumePendingFirstTurn();
  return readiness.snapshot;
}

function resumePendingFirstTurn() {
  if (!state.pendingFirstTurn || state.activeController) return;
  const pending = state.pendingFirstTurn;
  state.pendingFirstTurn = null;
  void sendPrompt(pending.prompt, {
    attachment: pending.attachment,
    clientRequestId: pending.clientRequestId,
    preserveComposer: true,
  });
}

function readinessNeedsRefresh() {
  return Boolean(state.pendingFirstTurn) ||
    Boolean(state.readiness.error) ||
    state.readiness.snapshot?.capabilities?.some((capability) =>
      capability.status !== "available") === true;
}

function openReadinessManagement(url) {
  const opened = window.open(url, "nyxid-readiness");
  if (opened) {
    try {
      opened.opener = null;
    } catch {
      // The validated NyxID window may already be cross-origin.
    }
    opened.focus?.();
  } else {
    window.location.assign(url);
  }
}

const readinessStatusCopy = {
  available: "可用",
  missing: "缺失",
  cannot_use: "不可使用",
  cannot_check: "无法确认",
};
const readinessConnectionCopy = {
  not_connected: "未连接",
  connecting: "连接中",
  verifying: "验证中",
  connected: "已连接",
  expired: "连接已过期",
  revoked: "连接已撤销",
  unknown: "连接状态未知",
};
const readinessGrantCopy = {
  not_required: "无需授权",
  granted: "已授权",
  partial: "部分授权",
  missing: "缺少授权",
  expired: "授权已过期",
  revoked: "授权已撤销",
  unknown: "授权状态未知",
};

// Optional capabilities never gate the workbench, so their status reads as a
// neutral on/off fact instead of the alarming state words reserved for
// capabilities that can actually block the first run.
function readinessStatusLabel(capability) {
  if (!capability.required) return capability.status === "available" ? "可用" : "未启用";
  return readinessStatusCopy[capability.status];
}

function renderReadiness() {
  if (!dom.readinessPanel) return;
  const authenticated = state.auth.authenticated;
  dom.readinessPanel.classList.toggle("hidden", !authenticated);
  if (!authenticated) return;
  dom.refreshReadinessButton.disabled = state.readiness.loading;
  dom.readinessRecovery.classList.add("hidden");
  if (state.readiness.loading) {
    dom.readinessFreshness.textContent = "正在检查";
    dom.readinessList.replaceChildren(el("div", "readiness-empty", "正在读取 NyxID 能力状态…"));
    dom.readinessSummary.textContent = "正在确认首次运行所需能力。";
    return;
  }
  if (state.readiness.error || !state.readiness.snapshot) {
    const failure = describeReadinessFailure(state.readiness.error);
    dom.readinessFreshness.textContent = failure.freshness;
    dom.readinessList.replaceChildren();
    dom.readinessSummary.textContent = failure.summary;
    dom.readinessRecoveryTitle.textContent = "如何恢复";
    dom.readinessRecoveryDetail.textContent = failure.guidance;
    dom.readinessRecoveryButton.dataset.action = failure.action;
    dom.readinessRecoveryButtonLabel.textContent = failure.actionLabel;
    dom.readinessRecovery.classList.remove("hidden");
    refreshIcons(dom.readinessRecovery);
    return;
  }
  const snapshot = state.readiness.snapshot;
  const capabilities = [...snapshot.capabilities].sort((left, right) =>
    Number(right.required) - Number(left.required));
  dom.readinessFreshness.textContent = `检查于 ${new Date(snapshot.evaluatedAt).toLocaleString("zh-CN")}`;
  const capabilityRow = (capability) => {
    const row = el("div", `readiness-row status-${capability.status}${capability.required ? "" : " optional"}`);
    row.dataset.capabilityId = capability.capabilityId;
    row.setAttribute("role", "listitem");
    const copy = el("div", "readiness-copy");
    copy.append(
      el("strong", "", capability.label),
      el("small", "", [
        capability.required ? "必需" : "可选",
        readinessConnectionCopy[capability.connectionState],
        readinessGrantCopy[capability.grantState],
      ].join(" · ")),
    );
    row.append(copy, el("span", "readiness-status", readinessStatusLabel(capability)));
    if (capability.managementUrl) {
      const manage = el("button", "readiness-manage", "前往 NyxID");
      manage.type = "button";
      manage.addEventListener("click", () => openReadinessManagement(capability.managementUrl));
      row.append(manage);
    }
    return row;
  };
  const required = capabilities.filter((capability) => capability.required);
  const optional = capabilities.filter((capability) => !capability.required);
  const requiredReady = required.every((capability) => capability.status === "available");
  const children = required.map(capabilityRow);
  if (optional.length && requiredReady) {
    const inactiveCount = optional.filter((capability) => capability.status !== "available").length;
    const disclosure = el("details", "readiness-optional");
    disclosure.open = state.readinessOptionalOpen === true;
    disclosure.addEventListener("toggle", () => {
      state.readinessOptionalOpen = disclosure.open;
    });
    disclosure.append(
      el("summary", "", inactiveCount
        ? `可选能力 ${optional.length} 项 · ${inactiveCount} 项未启用，不影响使用`
        : `可选能力 ${optional.length} 项 · 全部可用`),
      ...optional.map(capabilityRow),
    );
    children.push(disclosure);
  } else {
    children.push(...optional.map(capabilityRow));
  }
  dom.readinessList.replaceChildren(...children);
  const managementUrlDrops = Array.isArray(snapshot.managementUrlDrops)
    ? snapshot.managementUrlDrops
    : [];
  if (managementUrlDrops.length) {
    dom.readinessList.append(el(
      "div",
      "readiness-note",
      `已隐藏 ${managementUrlDrops.map((drop) => drop.capabilityId).join("、")} 的管理链接：` +
        "其地址不在此控制台信任的 NyxID 域名内。",
    ));
  }
  const blocked = capabilities.some((capability) =>
    capability.required && capability.status !== "available");
  dom.readinessSummary.textContent = blocked
    ? "必需能力尚未就绪，完成配置后才能开始首次运行。"
    : "首次运行所需能力已就绪。";
  refreshIcons(dom.readinessPanel);
}

function firstTurnReadinessBlocked() {
  if (state.config.surface !== "nyxid-chat" || state.actorId) return false;
  if (state.readiness.loading) return true;
  // The readiness check is an advisory pre-flight: when it cannot complete,
  // the first turn proceeds and the run surfaces its own authority errors.
  if (state.readiness.error) return false;
  const capabilities = state.readiness.snapshot?.capabilities;
  if (!Array.isArray(capabilities)) return true;
  return capabilities.some((capability) => capability.required && capability.status !== "available");
}

async function loadServices() {
  if (!state.auth.authenticated) {
    state.services = [];
    renderServiceList();
    return;
  }
  dom.serviceList.replaceChildren(el("div", "service-access-empty", "正在读取 NyxID services…"));
  dom.composerServiceList.replaceChildren(el("div", "service-access-empty", "正在读取 NyxID services…"));
  try {
    const response = await fetch("/api/auth/services", { cache: "no-store" });
    if (!response.ok) throw await responseError(response);
    const payload = await response.json();
    state.services = Array.isArray(payload.services) ? payload.services : [];
  } catch (error) {
    state.services = [];
    dom.serviceList.replaceChildren(el("div", "service-access-empty service-access-error", error.message));
    dom.composerServiceList.replaceChildren(
      el("div", "service-access-empty service-access-error", error.message),
    );
    dom.servicesCount.textContent = "0";
    dom.serviceCount.textContent = "0 / 0";
    dom.composerServiceCount.textContent = "0 / 0 可用";
    return;
  }
  renderServiceList();
  refreshAuthorizationCards();
  void loadConnectors();
}

async function loadConnectors({ fresh = false } = {}) {
  if (!state.auth.authenticated) {
    state.connectors = { connected: [], available: [], loadedAt: 0 };
    return state.connectors;
  }
  try {
    const response = await fetch(`/api/nyxid/connectors${fresh ? "?fresh=1" : ""}`, {
      cache: "no-store",
    });
    if (!response.ok) throw await responseError(response);
    const payload = await response.json();
    state.connectors = {
      connected: Array.isArray(payload.connected) ? payload.connected : [],
      available: Array.isArray(payload.available) ? payload.available : [],
      loadedAt: Date.now(),
    };
    updateLiveConnectCards();
  } catch {
    // The connector catalog only enriches cards; the chat keeps working without it.
    return null;
  }
  return state.connectors;
}

const liveConnectCards = new Set();

function actionCacheKey(actorId, actionRequestId) {
  return `nyxid-chat:v4-action:${actorId}:${actionRequestId}`;
}

function actionEntryKey(actorId, actionRequestId) {
  return `${actorId}:${actionRequestId}`;
}

function cacheActionRequest(entry, request) {
  if (!entry || !request) return;
  entry.actionFrameCache.set(actionEntryKey(request.actorId, request.actionRequestId), request);
  try {
    sessionStorage.setItem(
      actionCacheKey(request.actorId, request.actionRequestId),
      JSON.stringify(request),
    );
  } catch {
    // Session cache is optional; actor state remains the authority.
  }
}

function invalidateActionRequestCache(entry, actorId, actionRequestId) {
  if (!entry || !actorId || !actionRequestId) return;
  entry.actionFrameCache.delete(actionEntryKey(actorId, actionRequestId));
  try {
    sessionStorage.removeItem(actionCacheKey(actorId, actionRequestId));
  } catch {
    // Session cache is optional; the conflicted projection remains disabled.
  }
}

function restoreProjectionActionCaches(entry, actorId = entry?.actorId) {
  let projection = actorProjectionFor(entry, actorId);
  if (!projection?.actions?.size) return projection;
  for (const summary of projection.actions.values()) {
    const cacheActorId = projection.actorId || actorId;
    let cached = entry.actionFrameCache.get(actionEntryKey(cacheActorId, summary.actionRequestId)) || null;
    if (!cached) {
      try {
        const raw = sessionStorage.getItem(actionCacheKey(cacheActorId, summary.actionRequestId));
        cached = raw ? JSON.parse(raw) : null;
      } catch {
        cached = null;
      }
    }
    const request = restoreCachedAction(summary, cached);
    if (!request) continue;
    entry.actionFrameCache.set(actionEntryKey(request.actorId, request.actionRequestId), request);
    projection = reduceActorEvent(projection, {
      type: "action_request",
      sequence: projection.progressSequence,
      actionRequest: request,
    });
  }
  setActorProjectionFor(entry, actorId, projection);
  return projection;
}

function createConnectCard(action, { conversation = null, projection = null } = {}) {
  const request = action?.request || null;
  if (!request) return null;
  const block = buildConnectCardBlock(request, state.connectors);
  const card = {
    action,
    request,
    conversation,
    projectionActorId: projection?.actorId || request.actorId,
    slug: block.catalog_slug,
    root: el("section", "connect-card"),
    block,
    status: action.conflicted ? "conflicted" : block.state,
    keyInputOpen: false,
    busy: false,
    error: "",
    note: "",
    continuation: null,
    report: null,
    externalExpiryTimer: null,
  };
  card.root.dataset.actionRequestId = request.actionRequestId;
  card.root.dataset.actorId = request.actorId;
  card.root.dataset.originTurnId = request.originTurnId;
  card.root.dataset.taskId = request.taskId;
  card.root.dataset.stepId = request.stepId;
  if (card.slug) card.root.dataset.slug = card.slug;
  renderConnectCard(card);
  liveConnectCards.add(card);
  return card;
}

function renderActionCards(entry = conversationContext || state.activeConversation) {
  if (!entry) return;
  const projections = actorProjections(entry);
  if (!projections.some((projection) => projection.actions?.size) && !entry.run.cardElements.size) return;
  const container = entry.run.actionCardsElement || el("div", "action-card-list");
  entry.run.actionCardsElement = container;

  for (const projection of projections) {
    if (!actionActorJourneyReady(entry, projection)) continue;
    for (const action of projection.actions?.values?.() || []) {
      if (!["service.connect", "service.access_review"].includes(action.action) || !action.request) {
        continue;
      }
      const key = actionEntryKey(action.request.actorId, action.actionRequestId);
      let card = entry.run.cardElements.get(key);
      if (!card) {
        card = createConnectCard(action, { conversation: entry, projection });
        if (!card) continue;
        entry.run.cardElements.set(key, card);
      } else {
        card.action = action;
        card.request = action.request;
        card.projectionActorId = projection.actorId || action.request.actorId;
        if (action.conflicted) {
          card.status = "conflicted";
          card.error = "Action identity conflict；该 browser journey 已禁用。";
        } else {
          applyActorActionProof(card, action, projection);
        }
        renderConnectCard(card);
      }
    }
  }

  container.replaceChildren(...[...entry.run.cardElements.values()].map((card) => card.root));
  if (!container.isConnected) ensureAssistantBody().append(container);
  scrollThread();
}

function actionActorJourneyReady(entry, projection) {
  if (!entry || !projection) return false;
  if (projection.actorId === entry.actorId) return true;
  return actorStateVersion(entry, projection.actorId) > 0 && Boolean(projection.task);
}

function actionResourceUserServiceId(resource) {
  return resource?.userService?.userServiceId || resource?.userServiceId || "";
}

function applyActorActionProof(card, action, projection) {
  if (!card.report || card.report.disposition !== "completed") return false;
  const expectedUserServiceId = actionResourceUserServiceId(card.report.resource);
  const proof = action.postconditionResult;
  const proofMatches = proof?.verified === true &&
    proof.actionRequestId === card.request.actionRequestId &&
    proof.disposition === card.report.disposition &&
    actionResourceUserServiceId(proof.resource) === expectedUserServiceId;
  const confirmedStep = [...(projection?.steps?.values?.() || [])].some((step) =>
    step?.actionRequestId === card.request.actionRequestId &&
    step?.kind === "postcondition" &&
    step?.status === "done" &&
    step?.externalEffect === "confirmed");
  if (!proofMatches && !confirmedStep) return false;
  card.status = "verified";
  card.busy = false;
  card.error = "";
  card.note = "Actor 已确认精确的 UserService postcondition。";
  return true;
}

function connectDeepLink(card) {
  const base = state.config.nyxidWebUrl;
  if (!base) return "";
  if (!card.block.known || !card.slug) return new URL("/keys", base).toString();
  const url = new URL("/keys", base);
  url.searchParams.set("slug", card.slug);
  return url.toString();
}

function normalizedEndpoint(value) {
  try {
    const url = new URL(String(value || ""));
    if (url.protocol !== "https:" || url.username || url.password || url.search || url.hash) return "";
    return url.toString().replace(/\/$/, "");
  } catch {
    return "";
  }
}

function matchingUserServiceIds(card, connectors = state.connectors) {
  const catalog = card.request.params.catalogService || null;
  const custom = card.request.params.customService || null;
  const ids = new Set();
  for (const connector of connectors?.connected || []) {
    const connectorMatches = catalog
      ? connector.slug === catalog.serviceSlug
      : connector.custom === true &&
        String(connector.name || "").trim().toLowerCase() === custom.name.trim().toLowerCase();
    if (!connectorMatches) continue;
    for (const service of connector.userServices || []) {
      const userServiceId = String(service?.userServiceId || "");
      if (!userServiceId) continue;
      if (custom) {
        const endpointMatches = normalizedEndpoint(service.endpointUrl) ===
          normalizedEndpoint(custom.endpointUrl);
        const nameMatches = String(service.label || "").trim().toLowerCase() ===
          custom.name.trim().toLowerCase();
        if (!endpointMatches || !nameMatches) continue;
      }
      ids.add(userServiceId);
    }
  }
  return ids;
}

async function openConnectTarget(card) {
  card.busy = true;
  card.error = "";
  card.note = "正在读取 authoritative connector inventory 基线…";
  renderConnectCard(card);
  const connectors = await loadConnectors({ fresh: true });
  card.busy = false;
  if (!connectors) {
    card.status = "needs_connection";
    card.note = "无法读取 connector inventory 基线；为避免误认旧连接，本次未打开 NyxID。";
    renderConnectCard(card);
    return;
  }
  card.externalBaseline = matchingUserServiceIds(card, connectors);
  const target = connectDeepLink(card);
  if (!target) {
    card.status = "needs_connection";
    card.error = "NyxID management is not configured for this deployment.";
    renderConnectCard(card);
    return;
  }
  const opened = window.open(target, "nyxid-connect");
  if (opened) {
    try {
      opened.opener = null;
    } catch {
      // The NyxID window may already be cross-origin.
    }
    opened.focus?.();
  } else {
    window.location.assign(target);
  }
  card.status = "waiting_for_user";
  card.note = "在 NyxID 完成连接后，回到这里刷新状态。";
  clearExternalJourneyTimer(card);
  card.externalExpiryTimer = window.setTimeout(() => {
    card.externalExpiryTimer = null;
    if (card.status === "waiting_for_user") {
      void submitActionContinuation(card, "expired");
    }
  }, 5 * 60_000);
  renderConnectCard(card);
}

function clearExternalJourneyTimer(card) {
  if (card.externalExpiryTimer == null) return;
  window.clearTimeout(card.externalExpiryTimer);
  card.externalExpiryTimer = null;
}

async function refreshNyxIdAuthorizationCatalog(userServiceId) {
  const requiredUserServiceId = String(userServiceId || "").trim();
  if (!requiredUserServiceId) {
    const error = new Error("NyxID UserService.id is required before refreshing the authorization catalog.");
    error.code = "NYXID_USER_SERVICE_ID_MISSING";
    throw error;
  }
  const response = await fetch("/api/auth/nyxid/authorization-catalog:refresh", {
    method: "POST",
    headers: demoHeaders(),
    body: JSON.stringify({ requiredUserServiceIds: [requiredUserServiceId] }),
  });
  const payload = await response.json().catch(() => ({}));
  if (response.ok && payload?.ready === true) return payload;

  const refreshStatus = String(payload?.refreshStatus || "").trim();
  const visibilityStatus = String(payload?.visibilityStatus || "").trim();
  const failureCode = String(
    payload?.visibilityFailureCode || payload?.refreshFailureCode || payload?.code || "",
  ).trim();
  const requiredStateVersion = Number(payload?.requiredStateVersion || 0);
  const visibleStateVersion = Number(payload?.visibleStateVersion || 0);
  const versionDetail = requiredStateVersion > 0
    ? `（可见版本 ${visibleStateVersion}/${requiredStateVersion}）`
    : "";
  const statusDetail = [
    refreshStatus ? `refresh=${refreshStatus}` : "",
    visibilityStatus ? `visibility=${visibilityStatus}` : "",
    failureCode ? `code=${failureCode}` : "",
  ].filter(Boolean).join(", ");
  const pending = response.status === 202 || visibilityStatus === "projection_pending";
  const error = new Error(pending
    ? `NyxID 授权目录尚未可见${versionDetail}，请稍后点击“我已连接，刷新状态”重试。`
    : `无法刷新 NyxID 授权目录${statusDetail ? `（${statusDetail}）` : ""}，请稍后重试。`);
  error.code = "NYXID_AUTHORIZATION_CATALOG_NOT_READY";
  throw error;
}

async function completeServiceConnectAction(card, userServiceId) {
  const requiredUserServiceId = String(userServiceId || "").trim();
  if (!requiredUserServiceId) {
    const error = new Error("NyxID UserService.id is required before completing the connection action.");
    error.code = "NYXID_USER_SERVICE_ID_MISSING";
    throw error;
  }
  await refreshNyxIdAuthorizationCatalog(requiredUserServiceId);
  await submitActionContinuation(card, "completed", {
    userService: { userServiceId: requiredUserServiceId },
  });
  return true;
}

function proxyCatalogContainsService(catalog, userServiceId, serviceSlug, resourceUri = "") {
  const expectedUserServiceId = String(userServiceId || "").trim();
  const expectedServiceSlug = String(serviceSlug || "").trim();
  const expectedResourceUri = String(resourceUri || "").trim();
  if (!expectedUserServiceId || !expectedServiceSlug || !expectedResourceUri) return false;
  const matches = (Array.isArray(catalog?.services) ? catalog.services : []).filter((service) =>
    String(service?.userServiceId || "").trim() === expectedUserServiceId &&
    String(service?.serviceSlug || "").trim() === expectedServiceSlug &&
    String(service?.resourceUri || "").trim() === expectedResourceUri);
  return matches.length === 1;
}

function serviceAccessReviewParams(card) {
  const value = card?.request?.params?.serviceAccessReview || null;
  const params = {
    userServiceId: String(value?.userServiceId || "").trim(),
    serviceSlug: String(value?.serviceSlug || "").trim(),
    resourceUri: String(value?.resourceUri || "").trim(),
  };
  if (!params.userServiceId || !params.serviceSlug || !params.resourceUri) {
    const error = new Error("Typed NyxID service access review params are incomplete.");
    error.code = "NYXID_SERVICE_ACCESS_REVIEW_PARAMS_INVALID";
    throw error;
  }
  return params;
}

function actionContinuationCredentialRefreshParams(card, resource) {
  const review = card?.request?.params?.serviceAccessReview || null;
  const userServiceId = String(
    actionResourceUserServiceId(resource) || review?.userServiceId || "",
  ).trim();
  const serviceSlug = String(
    review?.serviceSlug ||
    card?.request?.params?.catalogService?.serviceSlug ||
    card?.block?.catalog_slug ||
    "",
  ).trim();
  const resourceUri = String(
    review?.resourceUri ||
    globalThis.AevatarStudioAuth.serviceResourceUri?.(serviceSlug) ||
    "",
  ).trim();
  if (!userServiceId || !serviceSlug || !resourceUri) {
    const error = new Error("Cannot resolve the exact NyxID service authorization for this action continuation.");
    error.code = "NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_PARAMS_INVALID";
    throw error;
  }
  return { userServiceId, serviceSlug, resourceUri };
}

async function beginActionContinuationCredentialRefresh(
  card,
  disposition,
  resource,
  { openAuthorization = true } = {},
) {
  try {
    const params = actionContinuationCredentialRefreshParams(card, resource);
    const conversationId = String(card?.conversation?.actorId || "").trim();
    const actorId = String(card?.request?.actorId || "").trim();
    const originTurnId = String(card?.request?.originTurnId || "").trim();
    const actionRequestId = String(card?.request?.actionRequestId || "").trim();
    const action = String(card?.request?.action || "").trim();
    if (!conversationId || !actorId || !originTurnId || !actionRequestId || !action) {
      const error = new Error("Cannot preserve the browser action identity for NyxID consent review.");
      error.code = "NYXID_ACTION_IDENTITY_MISSING";
      throw error;
    }

    const continuation = continuationIntent(card, disposition, resource);
    const report = continuation.actions[0];
    const pending = {
      schemaVersion: 3,
      action,
      conversationId,
      actorId,
      originTurnId,
      actionRequestId,
      disposition: report.disposition,
      resource: report.resource,
      serviceSlug: params.serviceSlug,
      userServiceId: params.userServiceId,
      resourceUri: params.resourceUri,
      clientRequestId: continuation.clientRequestId,
      createdAt: Date.now(),
    };

    globalThis.AevatarStudioAuth.clearServiceAccessReviewToken?.();
    writeStorage(SERVICE_ACCESS_REVIEW_KEY, JSON.stringify(pending));
    card.authorizationRefresh = {
      disposition: report.disposition,
      resource: report.resource,
    };
    card.busy = false;
    card.status = "reauthorizing";
    card.error = "";
    card.note = openAuthorization
      ? `当前 NyxID 登录仍有效；正在打开 NyxID 服务授权以继续 ${card.block.service_name} action。`
      : `当前 NyxID 登录仍有效；需要更新 NyxID 服务授权才能继续 ${card.block.service_name} action。请点击下方按钮。`;
    renderConnectCard(card);
    if (!openAuthorization) return true;
    await globalThis.AevatarStudioAuth.beginServiceAccessReview([params.resourceUri]);
    return true;
  } catch (error) {
    card.busy = false;
    card.status = "reauthorizing";
    card.error = error?.message || "无法开始 NyxID 服务访问审查。";
    card.note = "原 action 和会话状态已保留；可重试更新 NyxID 服务授权。";
    renderConnectCard(card);
    return false;
  }
}

async function beginServiceAccessReviewAction(card) {
  const params = serviceAccessReviewParams(card);
  return beginActionContinuationCredentialRefresh(
    card,
    "completed",
    { userService: { userServiceId: params.userServiceId } },
  );
}

async function loadServiceAccessReviewCatalog() {
  const response = await globalThis.AevatarStudioAuth.fetchServiceAccessReviewCatalog();
  if (!response.ok) throw await responseError(response);
  const payload = await response.json();
  return {
    proxyBaseUrl: String(payload?.proxyBaseUrl || "").trim().replace(/\/+$/, ""),
    resources: Array.isArray(payload?.resources) ? payload.resources : [],
    services: Array.isArray(payload?.services) ? payload.services : [],
  };
}

async function resumePendingServiceAccessReview() {
  const pending = readJsonStorage(SERVICE_ACCESS_REVIEW_KEY);
  if (!pending) return false;
  const valid = pending.schemaVersion === 3 &&
    typeof pending.action === "string" && pending.action &&
    typeof pending.conversationId === "string" && pending.conversationId &&
    typeof pending.actorId === "string" && pending.actorId &&
    typeof pending.originTurnId === "string" && pending.originTurnId &&
    typeof pending.actionRequestId === "string" && pending.actionRequestId &&
    pending.disposition === "completed" &&
    actionResourceUserServiceId(pending.resource) === pending.userServiceId &&
    typeof pending.serviceSlug === "string" && pending.serviceSlug &&
    typeof pending.userServiceId === "string" && pending.userServiceId &&
    typeof pending.resourceUri === "string" && pending.resourceUri &&
    typeof pending.clientRequestId === "string" && pending.clientRequestId;
  if (!valid) {
    removeStorage(SERVICE_ACCESS_REVIEW_KEY);
    globalThis.AevatarStudioAuth.clearServiceAccessReviewToken?.();
    return false;
  }

  try {
    const catalog = await loadServiceAccessReviewCatalog();
    if (!proxyCatalogContainsService(
      catalog,
      pending.userServiceId,
      pending.serviceSlug,
      pending.resourceUri,
    )) {
      return false;
    }
    const conversation = state.conversations.find((item) => item.id === pending.conversationId);
    if (!conversation) return false;
    await loadConversation(conversation);
    const entry = findConversationState(pending.conversationId);
    if (!entry) return false;
    await refreshActionActorState(entry, pending.actorId, { uncursored: true });
    renderActionCards(entry);
    const card = entry.run?.cardElements?.get(
      actionEntryKey(pending.actorId, pending.actionRequestId),
    );
    if (!card?.request || card.request.action !== pending.action) return false;
    const params = actionContinuationCredentialRefreshParams(card, pending.resource);
    if (params.userServiceId !== pending.userServiceId ||
        params.serviceSlug !== pending.serviceSlug ||
        params.resourceUri !== pending.resourceUri) {
      return false;
    }

    const continuation = validateActionContinuation({
      type: "action.continue",
      clientRequestId: pending.clientRequestId,
      originTurnId: pending.originTurnId,
      actions: [{
        actionRequestId: pending.actionRequestId,
        originTurnId: pending.originTurnId,
        disposition: pending.disposition,
        resource: pending.resource,
      }],
    }, { expectedAction: card.request.action });
    card.conversation = entry;
    card.continuation = continuation;
    card.report = continuation.actions[0];
    card.authorizationRefresh = {
      disposition: pending.disposition,
      resource: pending.resource,
    };
    card.status = "reauthorizing";
    card.note = "NyxID 服务授权已更新；正在恢复原 browser action 和会话。";
    renderConnectCard(card);
    const result = await submitActionContinuation(
      card,
      pending.disposition,
      pending.resource,
      { credential: "serviceAccessReview" },
    );
    if (result?.verified !== true || result?.terminalObserved !== true) return false;
    removeStorage(SERVICE_ACCESS_REVIEW_KEY);
    globalThis.AevatarStudioAuth.clearServiceAccessReviewToken();
    return true;
  } catch (error) {
    const entry = findConversationState(pending.conversationId);
    const card = entry?.run?.cardElements?.get(
      actionEntryKey(pending.actorId, pending.actionRequestId),
    );
    if (card) {
      card.busy = false;
      card.status = "reauthorizing";
      card.error = error?.message || "恢复 NyxID browser action 失败。";
      card.note = "原卡片、会话和 clientRequestId 已保留；可直接重试授权，无需刷新页面。";
      renderConnectCard(card);
    }
    return false;
  }
}

function markServiceAccessReviewInterrupted(pending, message) {
  const entry = findConversationState(pending?.conversationId);
  const card = entry?.run?.cardElements?.get(
    actionEntryKey(pending?.actorId, pending?.actionRequestId),
  );
  if (!card) return;
  card.busy = false;
  card.status = "reauthorizing";
  card.error = message || "NyxID 服务授权未完成。";
  card.note = "原 action、会话和 clientRequestId 已保留；可直接重新打开授权窗口。";
  renderConnectCard(card);
}

async function handleServiceAccessReviewResult(result = {}) {
  const pending = readJsonStorage(SERVICE_ACCESS_REVIEW_KEY);
  if (result.status !== "succeeded") {
    markServiceAccessReviewInterrupted(pending, result.message);
    showToast(result.message || "NyxID 服务授权未完成；原任务仍然保留。");
    return false;
  }
  if (!pending) {
    showToast("NyxID 服务授权已更新。");
    void loadServices();
    return true;
  }
  if (serviceAccessReviewResumePromise) return serviceAccessReviewResumePromise;

  setComposerStatus("NyxID 授权已更新，正在恢复原任务…", { working: true });
  serviceAccessReviewResumePromise = (async () => {
    const resumed = await resumePendingServiceAccessReview();
    await loadServices().catch(() => {});
    if (resumed) {
      showToast("NyxID 授权已更新，原任务已继续。");
    } else if (readJsonStorage(SERVICE_ACCESS_REVIEW_KEY)) {
      showToast("NyxID 授权已更新；原任务已恢复，正在等待 Actor 确认。");
    } else {
      showToast("NyxID 授权已更新。");
    }
    return resumed;
  })();
  try {
    return await serviceAccessReviewResumePromise;
  } finally {
    serviceAccessReviewResumePromise = null;
    setComposerStatus(state.activeController
      ? "正在接收生产 Agent 输出 · 停止接收不会撤销已提交操作"
      : state.auth.authenticated
        ? "生产环境 · 使用当前账户的 services，高风险操作需要确认"
        : "登录后使用当前账户已配置的 services", {
      working: Boolean(state.activeController),
    });
    renderActorControlUi();
  }
}

async function submitConnectCredential(card, credential, input = null) {
  const value = String(credential || "").trim();
  if (!value) {
    card.error = "请先粘贴 API key。";
    renderConnectCard(card);
    return;
  }
  card.busy = true;
  card.error = "";
  renderConnectCard(card);
  try {
    if (!(card.externalBaseline instanceof Set)) {
      card.externalBaseline = matchingUserServiceIds(card);
    }
    let response;
    try {
      response = await fetch("/api/nyxid/keys", {
        method: "POST",
        headers: demoHeaders(),
        body: JSON.stringify({
          serviceSlug: card.slug,
          credential: value,
          label: card.block.service_name,
        }),
      });
    } finally {
      if (input) input.value = "";
    }
    if (!response.ok) throw await responseError(response);
    const payload = await response.json();
    const userServiceId = String(payload?.userService?.userServiceId || "");
    if (!userServiceId) {
      const error = new Error("NyxID did not return a UserService.id; action continuation was not sent.");
      error.code = "NYXID_USER_SERVICE_ID_MISSING";
      throw error;
    }
    card.keyInputOpen = false;
    const completed = await completeServiceConnectAction(card, userServiceId);
    if (completed) void loadServices();
  } catch (error) {
    card.busy = false;
    card.error = error.message || "连接失败，请重试。";
    if (error.code === "NYXID_USER_SERVICE_ID_MISSING" ||
        error.code === "NYXID_AUTHORIZATION_CATALOG_NOT_READY") {
      card.status = "error";
      if (error.code === "NYXID_AUTHORIZATION_CATALOG_NOT_READY") {
        card.note = "API key 已保存；授权目录就绪后可直接刷新状态，无需再次提交 key。";
      }
      renderConnectCard(card);
    } else {
      await submitActionContinuation(card, "failed");
    }
  }
}

function continuationIntent(card, disposition, resource = null) {
  const matchesPending = continuationMatches(card, disposition, resource);
  if (matchesPending) return card.continuation;
  const continuation = validateActionContinuation({
    type: "action.continue",
    clientRequestId: createId("client-action"),
    originTurnId: card.request.originTurnId,
    actions: [{
      actionRequestId: card.request.actionRequestId,
      originTurnId: card.request.originTurnId,
      disposition,
      ...(resource ? { resource } : {}),
    }],
  }, { expectedAction: card.request.action });
  card.continuation = continuation;
  card.report = continuation.actions[0];
  return continuation;
}

function continuationMatches(card, disposition, resource = null) {
  return Boolean(card.continuation &&
    card.continuation.actions[0]?.disposition === disposition &&
    JSON.stringify(card.continuation.actions[0]?.resource || null) === JSON.stringify(resource));
}

function actionContinuationReconciliationFrame(event) {
  return [
    "run_started",
    "task_snapshot",
    "task_step_changed",
    "continuation_changed",
    "run_finished",
    "run_error",
    "keepalive",
  ].includes(event?.type);
}

async function reconcileActionContinuation(card, conversation) {
  if (card.status === "verified") return true;
  const refreshed = await refreshActionActorState(conversation, card.request.actorId);
  return withConversationState(conversation, () => {
    const projection = refreshed || actorProjectionFor(conversation, card.request.actorId);
    const projected = projection?.actions?.get(card.request.actionRequestId);
    const verified = applyActorActionProof(card, projected || card.action, projection);
    if (verified) renderConnectCard(card);
    return verified || card.status === "verified";
  });
}

async function submitActionContinuation(card, disposition, resource = null, options = {}) {
  const conversation = card.conversation;
  if (!conversation || card.status === "conflicted") return;
  if (card.continuation && !continuationMatches(card, disposition, resource)) {
    const refreshed = await refreshActionActorState(conversation, card.request.actorId);
    const pending = refreshed?.actions?.get(card.request.actionRequestId);
    if (!pending || !restoreCachedAction(pending, card.request)) {
      card.busy = false;
      card.status = "error";
      card.error = "This action is no longer pending with the same identity; the changed report was not sent.";
      renderConnectCard(card);
      return;
    }
  }
  let continuation;
  try {
    continuation = continuationIntent(card, disposition, resource);
  } catch (error) {
    card.status = "error";
    card.error = error.message || "Action continuation is invalid.";
    renderConnectCard(card);
    return;
  }

  clearExternalJourneyTimer(card);

  const controller = new AbortController();
  conversation.controllers.add(controller);
  if (!conversation.controller) conversation.controller = controller;
  if (conversation === state.activeConversation) {
    state.activeController = conversation.controller;
    setRunningUi(true);
  }
  card.busy = true;
  card.status = "reporting";
  card.error = "";
  card.note = "正在向 Aevatar 报告 browser journey 结果。";
  renderConnectCard(card);
  let terminalObserved = false;
  try {
    const body = {
      surface: "nyxid-chat",
      ...continuation,
      conversationId: card.request.actorId,
    };
    const useServiceAccessReviewCredential =
      options?.credential === "serviceAccessReview" ||
      (card.request.action === "service.access_review" && disposition === "completed");
    const response = useServiceAccessReviewCredential
      ? await globalThis.AevatarStudioAuth.continueServiceAccessReview(body, {
        headers: demoHeaders(),
        signal: controller.signal,
      })
      : await fetch("/api/demo/chat", {
        method: "POST",
        headers: demoHeaders(),
        signal: controller.signal,
        body: JSON.stringify(body),
      });
    if (!response.ok) throw await responseError(response);
    card.status = disposition === "completed" ? "awaiting_verification" : "reported";
    card.note = disposition === "completed"
      ? "Browser journey 已报告；等待 actor 验证 postcondition。"
      : `Browser journey 已报告 ${disposition}；等待 actor 状态确认。`;
    renderConnectCard(card);
    await consumeSse(response, async (raw) => {
      const event = withConversationState(conversation, () => handleFrame(raw, {
        streamActorId: card.request.actorId,
        preserveConversationActor: true,
      }));
      if (disposition === "completed" && actionContinuationReconciliationFrame(event)) {
        await reconcileActionContinuation(card, conversation);
      }
      terminalObserved = ["run_finished", "run_error", "run_stopped"].includes(event?.type);
      return terminalObserved ? false : undefined;
    });
    if (card.status !== "verified") {
      await reconcileActionContinuation(card, conversation);
    }
    withConversationState(conversation, () => {
      if (card.status !== "verified") {
        card.status = disposition === "completed" ? "awaiting_verification" : "reported";
        card.note = disposition === "completed"
          ? "Browser journey 已报告；等待 actor 验证 postcondition。"
          : `Browser journey 已报告 ${disposition}；等待 actor 状态确认。`;
      }
      card.busy = false;
      renderConnectCard(card);
    });
    return {
      verified: card.status === "verified",
      terminalObserved,
    };
  } catch (error) {
    if (error.code === ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED_CODE &&
        disposition === "completed" && actionResourceUserServiceId(resource)) {
      const credentialRefreshStarted = await beginActionContinuationCredentialRefresh(
        card,
        disposition,
        resource,
        { openAuthorization: false },
      );
      return {
        verified: false,
        terminalObserved,
        credentialRefreshStarted,
      };
    }
    withConversationState(conversation, () => {
      card.busy = false;
      card.status = "error";
      card.error = error.name === "AbortError"
        ? "已停止观察 continuation；Actor 可能仍在处理报告。"
        : error.message || "Action continuation 提交失败。";
      renderConnectCard(card);
    });
    return {
      verified: false,
      terminalObserved,
    };
  } finally {
    withConversationState(conversation, () => {
      releaseConversationController(conversation, controller);
      setRunningUi(Boolean(state.activeController));
    });
  }
}

async function refreshConnectCard(card) {
  card.busy = true;
  card.error = "";
  renderConnectCard(card);
  await loadConnectors({ fresh: true });
  card.busy = false;
  card.block = {
    ...buildConnectCardBlock(card.request, state.connectors),
    state: card.block.state,
    steps: card.block.steps,
  };
  if (!(card.externalBaseline instanceof Set)) {
    card.note = "请先从此卡打开 NyxID journey；无法安全确定本次新增的 UserService。";
    renderConnectCard(card);
    return;
  }
  const candidates = [...matchingUserServiceIds(card)]
    .filter((userServiceId) => !card.externalBaseline.has(userServiceId));
  if (candidates.length !== 1) {
    card.status = "waiting_for_user";
    card.note = candidates.length === 0
      ? "没有检测到本次 journey 新增的匹配 UserService；不会猜测已有连接。"
      : "检测到多个新增的匹配 UserService；无法安全选择，请在 NyxID 中消除歧义。";
    renderConnectCard(card);
    return;
  }
  card.busy = true;
  card.note = "已检测到新的 UserService；正在检查当前 NyxID OAuth 服务范围。";
  renderConnectCard(card);
  const userServiceId = candidates[0];
  try {
    const completed = await completeServiceConnectAction(card, userServiceId);
    if (completed) void loadServices();
  } catch (error) {
    card.busy = false;
    card.status = "error";
    card.error = error.message || "无法刷新 NyxID 授权目录，请稍后重试。";
    card.note = "连接已存在于 NyxID；授权目录就绪前不会向 Actor 报告完成。";
    renderConnectCard(card);
    return;
  }
}

function updateLiveConnectCards() {
  for (const card of Array.from(liveConnectCards)) {
    if (!card.root.isConnected) {
      liveConnectCards.delete(card);
      continue;
    }
    if (["reauthorizing", "reporting", "awaiting_verification", "verified", "conflicted"].includes(card.status)) {
      continue;
    }
    if (!card.keyInputOpen && !card.busy) {
      const refreshed = buildConnectCardBlock(card.request, state.connectors);
      card.block = {
        ...refreshed,
        state: card.block.state,
        steps: card.block.steps,
      };
      renderConnectCard(card);
    }
  }
}

function connectCardPill(card) {
  const labels = {
    needs_connection: "未连接",
    needs_review: "需要授权",
    waiting_for_user: "等待连接",
    reauthorizing: "正在更新授权",
    reporting: "正在报告",
    awaiting_verification: "等待 Actor 验证",
    reported: "等待 Actor 确认",
    verified: "已验证",
    conflicted: "身份冲突",
    error: "连接失败",
  };
  const modifier = card.status === "verified" ? " ok" :
    card.status === "error" || card.status === "conflicted" ? " bad" : "";
  return el("span", `cc-pill${modifier}`, card.busy ? "处理中…" : labels[card.status] || "未连接");
}

function renderConnectCard(card) {
  const block = card.block;
  card.root.className = `connect-card ${card.status}`;
  card.root.replaceChildren();

  const head = el("div", "cc-head");
  const brand = el("div", "cc-brand");
  const logo = el("span", "cc-logo");
  if (/^https:\/\//i.test(block.icon_url || "")) {
    const image = document.createElement("img");
    image.src = block.icon_url;
    image.alt = "";
    image.loading = "lazy";
    logo.append(image);
  } else {
    logo.textContent = connectorInitial(block.service_name);
  }
  const brandCopy = el("div", "cc-copy");
  brandCopy.append(el("div", "cc-title", block.service_name));
  const subtitle = card.status === "conflicted"
    ? "同一个 actionRequestId 出现了不一致的 authoritative params"
    : card.status === "reauthorizing"
    ? "正在更新 NyxID 服务授权；原 action 与会话保持不变"
    : card.status === "verified"
    ? "Actor 已验证精确的 typed postcondition"
    : ["reporting", "awaiting_verification", "reported"].includes(card.status)
      ? "Browser journey 不是成功证明；正在等待 actor 状态"
    : block.subtitle || (block.known ? "连接后 Agent 可通过 NyxID proxy 调用" : "该服务不在 NyxID 目录中，可在 NyxID 里手动添加");
  brandCopy.append(el("div", "cc-sub", subtitle));
  brand.append(logo, brandCopy);
  head.append(brand, connectCardPill(card));
  card.root.append(head);

  const progress = el("div", "cc-progress");
  const journeyReported = ["reporting", "awaiting_verification", "reported", "verified"]
    .includes(card.status);
  const verified = card.status === "verified";
  block.steps.forEach((step, index) => {
    const done = index < 2 ? journeyReported : verified;
    const active = index === (journeyReported ? 2 : 0) && !verified;
    const item = el("div", `cc-progress-step${done ? " done" : active ? " active" : ""}`);
    item.title = step.body || step.title;
    const marker = el("span", "cc-progress-marker");
    if (done) marker.append(iconNode("check"));
    else if (active) marker.append(iconNode("loader-circle"));
    else marker.textContent = String(index + 1);
    item.append(marker, el("span", "cc-progress-label", step.title));
    progress.append(item);
  });
  card.root.append(progress);

  if (!journeyReported) {
    card.root.append(renderConnectCardActions(card));
  } else {
    const verification = el("div", `cc-verification${verified ? " verified" : ""}`);
    verification.append(
      iconNode(verified ? "badge-check" : "loader-circle"),
      el("span", "", verified ? card.note || "已验证" : card.note || "等待 Actor 验证"),
    );
    card.root.append(verification);
  }

  if (card.error) {
    const error = el("div", "cc-error");
    error.append(iconNode("circle-alert"), el("span", "", card.error));
    card.root.append(error);
  }

  const foot = el("div", "cc-foot");
  foot.append(iconNode("shield-check"), el("span", "", block.footer));
  card.root.append(foot);
  refreshIcons(card.root);
}

function renderConnectCardActions(card) {
  const wrap = el("div", "cc-action-zone");
  if (card.note) {
    wrap.append(el("div", "cc-hint", card.note));
  }
  const actions = el("div", "cc-actions");
  const serviceAccessReview = card.request.action === "service.access_review";
  const apiKeyFlow = card.block.known && card.block.auth_kind === "api_key";

  if (card.status === "error" && card.continuation && card.report && !card.authorizationRefresh) {
    const retryReport = el("button", "cc-btn primary cc-retry-report", "重试报告");
    retryReport.type = "button";
    retryReport.disabled = card.busy;
    retryReport.addEventListener("click", () => void submitActionContinuation(
      card,
      card.report.disposition,
      card.report.resource || null,
    ));
    actions.append(retryReport);
  }

  if (!serviceAccessReview && card.authorizationRefresh &&
      ["reauthorizing", "error"].includes(card.status)) {
    const review = el("button", "cc-btn primary", "");
    review.type = "button";
    review.append(
      iconNode("shield-check"),
      el("span", "", card.error ? "重试更新 NyxID 服务授权" : "更新 NyxID 服务授权"),
    );
    review.disabled = card.busy || card.status === "conflicted";
    review.addEventListener("click", () => void beginActionContinuationCredentialRefresh(
      card,
      card.authorizationRefresh.disposition,
      card.authorizationRefresh.resource,
    ));
    actions.append(review);
    wrap.append(actions);
    return wrap;
  }

  if (serviceAccessReview) {
    const review = el("button", "cc-btn primary", "");
    review.type = "button";
    review.append(
      iconNode("shield-check"),
      el("span", "", card.status === "reauthorizing"
        ? "重新打开 NyxID 授权"
        : "更新 OAuth client 访问"),
    );
    review.disabled = card.busy || card.status === "conflicted";
    review.addEventListener("click", () => void beginServiceAccessReviewAction(card));
    actions.append(review);

    const decline = el("button", "cc-btn ghost cc-decline", "拒绝授权");
    decline.type = "button";
    decline.disabled = card.busy || card.status === "conflicted";
    decline.addEventListener("click", () => void submitActionContinuation(card, "declined"));
    actions.append(decline);
    wrap.append(actions);
    return wrap;
  }

  if (card.keyInputOpen && apiKeyFlow) {
    const form = el("form", "cc-key-form");
    const input = document.createElement("input");
    input.type = "password";
    input.placeholder = `粘贴 ${card.block.service_name} API key`;
    input.autocomplete = "off";
    input.spellcheck = false;
    input.disabled = card.busy || card.status === "conflicted";
    const submit = el("button", "cc-btn primary", card.busy ? "正在连接…" : "保存并连接");
    submit.type = "submit";
    submit.disabled = card.busy || card.status === "conflicted";
    const cancel = el("button", "cc-btn ghost", "取消");
    cancel.type = "button";
    cancel.disabled = card.busy || card.status === "conflicted";
    cancel.addEventListener("click", () => {
      card.keyInputOpen = false;
      card.error = "";
      renderConnectCard(card);
    });
    form.append(input, submit, cancel);
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      void submitConnectCredential(card, input.value, input);
    });
    wrap.append(form);
    if (card.block.api_key_url) {
      const link = document.createElement("a");
      link.className = "cc-key-link";
      link.href = card.block.api_key_url;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      link.textContent = `获取 ${card.block.service_name} API key ↗`;
      wrap.append(link);
    }
    if (card.block.api_key_instructions) {
      wrap.append(el("div", "cc-hint", card.block.api_key_instructions.slice(0, 240)));
    }
    queueMicrotask(() => input.focus());
    return wrap;
  }

  if (apiKeyFlow) {
    const paste = el("button", "cc-btn primary", "");
    paste.type = "button";
    paste.append(iconNode("key-round"), el("span", "", `粘贴 API key 连接`));
    paste.disabled = card.busy || card.status === "conflicted";
    paste.addEventListener("click", () => {
      card.keyInputOpen = true;
      card.error = "";
      renderConnectCard(card);
    });
    actions.append(paste);
    const external = el("button", "cc-btn ghost", "");
    external.type = "button";
    external.append(iconNode("arrow-up-right"), el("span", "", "在 NyxID 中连接"));
    external.disabled = card.busy || card.status === "conflicted";
    external.addEventListener("click", () => void openConnectTarget(card));
    actions.append(external);
  } else {
    const connect = el("button", "cc-btn primary", "");
    connect.type = "button";
    connect.append(
      iconNode("arrow-up-right"),
      el("span", "", card.status === "waiting_for_user"
        ? "重新打开 NyxID"
        : `在 NyxID 中连接 ${card.block.service_name}`),
    );
    connect.disabled = card.busy || card.status === "conflicted";
    connect.addEventListener("click", () => void openConnectTarget(card));
    actions.append(connect);
  }

  if (card.status === "waiting_for_user") {
    const cancelJourney = el("button", "cc-btn ghost cc-cancel", "取消本次连接");
    cancelJourney.type = "button";
    cancelJourney.disabled = card.busy;
    cancelJourney.addEventListener("click", () => void submitActionContinuation(card, "cancelled"));
    actions.append(cancelJourney);
  } else {
    const decline = el("button", "cc-btn ghost cc-decline", "不连接");
    decline.type = "button";
    decline.disabled = card.busy || card.status === "conflicted";
    decline.addEventListener("click", () => void submitActionContinuation(card, "declined"));
    actions.append(decline);
  }

  const refresh = el("button", "cc-btn ghost", "");
  refresh.type = "button";
  refresh.append(iconNode(card.busy ? "loader-circle" : "refresh-cw"), el("span", "", card.busy ? "正在检查…" : "我已连接，刷新状态"));
  refresh.disabled = card.busy || card.status === "conflicted";
  refresh.addEventListener("click", () => void refreshConnectCard(card));
  actions.append(refresh);

  wrap.append(actions);
  return wrap;
}

function renderAssistantSegments(container, source) {
  const segments = splitMessageSegments(source);
  container.replaceChildren();
  for (const segment of segments) {
    const textElement = el("div", "message-text markdown-body");
    renderMarkdown(textElement, segment.text);
    container.append(textElement);
  }
  refreshIcons(container);
}

function renderAuthUi() {
  const authenticated = state.auth.authenticated;
  const user = state.auth.user || {};
  dom.accountName.textContent = authenticated ? user.name || "NyxID user" : "尚未登录";
  dom.accountEmail.textContent = authenticated
    ? user.email || "已连接 NyxID 站点会话"
    : "登录 NyxID 后使用已配置的 services";
  dom.serviceAccessDescription.textContent = "来自当前 NyxID 账户的可用 services";
  dom.accountAvatar.replaceChildren(authenticated
    ? el("span", "account-initial", (user.name || user.email || "N").slice(0, 1).toUpperCase())
    : iconNode("user-round"));
  dom.loginButton.classList.toggle("hidden", authenticated);
  dom.logoutButton.classList.toggle("hidden", !authenticated);
  dom.authGate.classList.toggle("hidden", authenticated);
  dom.quickActions.classList.toggle("hidden", !authenticated);
  dom.emptyTitle.textContent = authenticated ? "NyxID Assistant" : "连接你的 NyxID 账户";
  dom.emptyDescription.textContent = authenticated
    ? "今天要在 NyxID 上做什么？"
    : "使用 NyxID 站点登录态连接 Assistant";
  dom.promptInput.disabled = !authenticated;
  dom.attachButton.disabled = !authenticated;
  dom.composerServicesButton.disabled = !authenticated;
  dom.sendButton.disabled = !authenticated;
  dom.newChatButton.disabled = !authenticated;
  dom.promptInput.placeholder = authenticated
    ? "告诉 Assistant 你要完成的操作"
    : "请先使用 NyxID 登录";
  if (!authenticated) {
    closeComposerServices();
    setConnectionStatus("idle", "登录 NyxID");
    setRouteState(dom.routeUpstreamState, "waiting");
    setRouteState(dom.routeOrnnState, "waiting");
    setComposerStatus("登录后使用当前账户已配置的 services");
    state.conversations = [];
    state.historyError = null;
    state.historyLoading = false;
    renderHistoryList();
  }
  renderServiceList();
  refreshIcons(dom.settingsDialog);
}

function renderServiceList() {
  const services = state.services;
  const authorizedCount = services.filter((service) => service.authorized).length;
  dom.servicesCount.textContent = String(authorizedCount);
  dom.serviceCount.textContent = `${authorizedCount} / ${services.length}`;
  renderComposerServiceList();
  if (!state.auth.authenticated) {
    dom.serviceList.replaceChildren(el("div", "service-access-empty", "登录后显示 NyxID services"));
    return;
  }
  if (!services.length) {
    dom.serviceList.replaceChildren(el("div", "service-access-empty", "没有可用的 NyxID service"));
    return;
  }
  dom.serviceList.replaceChildren();
  for (const service of services) {
    dom.serviceList.append(createServiceAccessRow(service));
  }
  refreshIcons(dom.serviceList);
}

function createServiceAccessRow(service, { compact = false } = {}) {
  const row = el(
    "div",
    `service-access-row${service.authorized ? " authorized" : ""}${compact ? " compact" : ""}`,
  );
  const icon = el("span", "service-access-icon");
  icon.append(iconNode(service.authorized ? "shield-check" : "lock-keyhole"));
  const copy = el("div", "service-access-copy");
  copy.append(
    el("strong", "", service.label),
    el("small", "", service.core
      ? "Chat runtime · required"
      : `${service.slug}${service.sourceName ? ` · ${service.sourceName}` : ""}`),
  );
  const available = service.active && service.available;
  const status = el(
    "span",
    `service-access-status ${service.authorized ? "granted" : ""}`,
    service.authorized ? "可用" : "需配置",
  );
  row.append(icon, copy, status);
  if (!service.authorized) {
    const authorize = el("button", "service-authorize-button", "配置");
    authorize.type = "button";
    authorize.disabled = !available && service.source === "org";
    authorize.addEventListener("click", () => openServiceManagement());
    row.append(authorize);
  }
  return row;
}

function renderComposerServiceList() {
  const services = state.services;
  const authorizedCount = services.filter((service) => service.authorized).length;
  dom.composerServiceCount.textContent = `${authorizedCount} / ${services.length} 可用`;
  dom.composerServiceList.replaceChildren();
  if (!state.auth.authenticated) {
    dom.composerServiceList.append(el("div", "service-access-empty", "登录后显示 NyxID services"));
    return;
  }
  if (!services.length) {
    dom.composerServiceList.append(el("div", "service-access-empty", "没有可用的 NyxID service"));
    return;
  }
  services.forEach((service) => dom.composerServiceList.append(
    createServiceAccessRow(service, { compact: true }),
  ));
  refreshIcons(dom.composerServiceList);
}

function toggleComposerServices() {
  if (!state.auth.authenticated) {
    beginLogin();
    return;
  }
  const opening = dom.composerServicePanel.classList.contains("hidden");
  dom.composerServicePanel.classList.toggle("hidden", !opening);
  dom.composerServicesButton.setAttribute("aria-expanded", String(opening));
  if (opening) {
    renderComposerServiceList();
    refreshIcons(dom.composerServicePanel);
  }
}

function closeComposerServices() {
  dom.composerServicePanel.classList.add("hidden");
  dom.composerServicesButton.setAttribute("aria-expanded", "false");
}

async function logout() {
  abortAllRuns();
  closeComposerServices();
  dom.logoutButton.disabled = true;
  try {
    await fetch("/api/auth/logout", { method: "POST", headers: demoHeaders() });
  } finally {
    dom.logoutButton.disabled = false;
    state.auth = { authenticated: false, user: null, resources: [] };
    state.services = [];
    state.connectors = { connected: [], available: [], loadedAt: 0 };
    state.readiness = { subject: "", snapshot: null, loading: false, error: null, inFlight: null };
    state.pendingFirstTurn = null;
    state.config.scopeId = "";
    state.health = null;
    closeSettings();
    newChat({ refreshHistory: false });
    for (const entry of Array.from(state.conversationStates.values())) {
      if (entry !== state.activeConversation) removeConversationState(entry);
    }
    renderAuthUi();
    renderReadiness();
    configureWireInspector();
  }
}

function autoResizeComposer() {
  dom.promptInput.rows = 1;
  const styles = getComputedStyle(dom.promptInput);
  const lineHeight = Number.parseFloat(styles.lineHeight) || 20;
  const verticalPadding = (Number.parseFloat(styles.paddingTop) || 0) +
    (Number.parseFloat(styles.paddingBottom) || 0);
  const contentHeight = Math.max(lineHeight, dom.promptInput.scrollHeight - verticalPadding);
  dom.promptInput.rows = Math.max(1, Math.min(7, Math.ceil(contentHeight / lineHeight)));
}

function openSettings() {
  closeComposerServices();
  applyConfigToForm(state.config);
  updateSettingsVisibility();
  if (!dom.settingsDialog.open) dom.settingsDialog.showModal();
  if (state.auth.authenticated) void loadServices();
  closeMobilePanels();
  refreshIcons(dom.settingsDialog);
}

function closeSettings() {
  if (dom.settingsDialog.open) dom.settingsDialog.close();
}

function applyConfigToForm(config) {
  const surface = dom.settingsForm.querySelector(`input[name="surface"][value="${config.surface}"]`);
  if (surface) surface.checked = true;
  dom.workflowInput.value = config.workflow || "direct";
}

function readSettingsForm() {
  return {
    ...state.config,
    surface: "nyxid-chat",
    transport: "nyxid-session",
    workflow: "direct",
  };
}

function updateSettingsVisibility() {
  const surface = dom.settingsForm.querySelector('input[name="surface"]:checked')?.value || "workflow";
  dom.workflowField.classList.toggle("hidden", surface !== "workflow");
}

function saveSettings(event) {
  event.preventDefault();
  const previousConfig = state.config;
  state.config = readSettingsForm();
  const persisted = {
    surface: state.config.surface,
    workflow: state.config.workflow,
  };
  writeStorage(PREFERENCES_KEY, JSON.stringify(persisted));
  const routeChanged = previousConfig.surface !== state.config.surface ||
    previousConfig.workflow !== state.config.workflow;
  if (routeChanged) newChat({ refreshHistory: false });
  updateConfigUi();
  closeSettings();
  void refreshRuntimeData();
}

async function refreshRuntimeData() {
  if (!state.auth.authenticated) return;
  await checkConnection(state.config, false);
  await loadConversations();
  await resumePendingServiceAccessReview();
}

function updateConfigUi() {
  const surface = surfaceLabels[state.config.surface];
  const transport = transportLabels[state.config.transport] || state.config.transport;
  if (dom.sidebarSurface) dom.sidebarSurface.textContent = surface;
  if (dom.sidebarTransport) dom.sidebarTransport.textContent = transport;
  dom.routeTransportValue.textContent = transport;
  dom.routeSurfaceValue.textContent = surfacePaths[state.config.surface];
  dom.routeLabel.textContent = state.config.surface === "workflow"
    ? `${shortTransport()} · /api/chat`
    : `${shortTransport()} · /api/chat`;
  const isNyxIdChat = state.config.surface === "nyxid-chat";
  dom.recentGroup.classList.toggle("hidden", !isNyxIdChat);
  dom.runFactRow.classList.toggle("hidden", isNyxIdChat);
  dom.commandFactRow.classList.toggle("hidden", isNyxIdChat);
  dom.stopButton.setAttribute("aria-label", "停止接收");
  dom.stopButton.title = "停止接收（不会撤销已提交的生产操作）";
  setComposerStatus(state.auth.authenticated
    ? "生产环境 · 使用当前账户的 services，高风险操作需要确认"
    : "登录后使用当前账户已配置的 services");
  renderHistoryList();
}

function shortTransport() {
  return state.config.transport === "nyxid-session" ? "NyxID session" : state.config.transport;
}

async function checkConnection(config, inDialog) {
  if (!state.auth.authenticated) {
    if (!inDialog) setConnectionStatus("idle", "登录 NyxID");
    if (inDialog) setDialogConnection("idle", "尚未连接", "登录后通过 NyxID 站点会话调用 Aevatar");
    return;
  }
  if (!inDialog) {
    setConnectionStatus("checking", "正在检查");
    if (!state.activeController) {
      setRouteState(dom.routeUpstreamState, "checking", "checking");
      setRouteState(dom.routeOrnnState, "checking", "checking");
    }
  }
  if (inDialog) setDialogConnection("checking", "正在测试", "等待响应");
  try {
    const response = await fetch("/api/demo/health", {
      method: "POST",
      headers: demoHeaders(config),
      body: JSON.stringify(configPayload(config)),
    });
    const result = await response.json();
    if (!inDialog) {
      state.health = result.components || null;
      applyHealthRouteState();
    }
    const detail = [
      result.latencyMs !== undefined ? `${result.latencyMs} ms` : "",
      result.detail || "",
    ].filter(Boolean).join(" · ");
    if (!response.ok || !result.ok) {
      if (!inDialog) setConnectionStatus("error", "Production degraded");
      if (inDialog) setDialogConnection("error", "连接异常", detail || `HTTP ${response.status}`);
      return;
    }
    const label = "Production connected";
    if (!inDialog) setConnectionStatus("ok", label);
    if (inDialog) {
      setDialogConnection("ok", label, detail);
    }
  } catch (error) {
    if (!inDialog) {
      state.health = null;
      setConnectionStatus("error", "Disconnected");
      if (!state.activeController) {
        setRouteState(dom.routeUpstreamState, "unavailable", "error");
        setRouteState(dom.routeOrnnState, "unavailable", "error");
      }
    }
    if (inDialog) setDialogConnection("error", "连接失败", error.message);
  }
}

function setRouteState(element, text, status = "") {
  element.textContent = text;
  element.className = `route-state ${status}`.trim();
}

function applyHealthRouteState({ includeAevatar = !state.activeController } = {}) {
  const aevatar = state.health?.aevatar;
  const ornn = state.health?.ornn;
  if (includeAevatar) {
    setRouteState(
      dom.routeUpstreamState,
      aevatar?.ok ? "ready" : "unavailable",
      aevatar?.ok ? "ok" : "error",
    );
  }
  const hasRunningOrnnTool = Array.from(state.run.tools.values()).some((tool) =>
    tool.status === "running" && /ornn_search_skills|use_skill/i.test(tool.name));
  if (!hasRunningOrnnTool && ornn?.status === "authorization-required") {
    setRouteState(dom.routeOrnnState, "authorization needed", "checking");
  } else if (!hasRunningOrnnTool) {
    setRouteState(
      dom.routeOrnnState,
      ornn?.ok ? "ready" : "unavailable",
      ornn?.ok ? "ok" : "error",
    );
  }
}

function setConnectionStatus(status, text) {
  dom.connectionDot.className = `status-dot ${status}`;
  if (dom.sidebarRuntimeDot) dom.sidebarRuntimeDot.className = `status-dot ${status}`;
  dom.connectionText.textContent = text;
  dom.routeClientState.textContent = status === "ok" ? "ready" : status;
  const routeClass = status === "ok" ? "ok" : status === "error" ? "error" : status === "checking" ? "active" : "";
  dom.routeClientState.className = `route-state ${routeClass}`.trim();
}

function setDialogConnection(status, title, detail) {
  const dot = dom.connectionTest.querySelector(".status-dot");
  dot.className = `status-dot ${status}`;
  dom.connectionTest.querySelector("strong").textContent = title;
  const detailElement = dom.connectionTest.querySelector("small");
  detailElement.textContent = detail;
  detailElement.title = detail;
}

function configPayload(config) {
  return {
    surface: config.surface,
    workflow: config.workflow,
  };
}

function demoHeaders() {
  return { "Content-Type": "application/json" };
}

function historyUrl(actorId) {
  const params = new URLSearchParams({
    surface: "nyxid-chat",
    workflow: state.config.workflow,
  });
  const path = actorId
    ? `/api/demo/conversations/${encodeURIComponent(actorId)}`
    : "/api/demo/conversations";
  return `${path}?${params}`;
}

function historyConfigKey() {
  return [state.config.transport, state.config.scopeId, state.config.surface].join(":");
}

async function loadConversations({ silent = false } = {}) {
  if (!state.auth.authenticated || state.config.surface !== "nyxid-chat") {
    state.conversations = [];
    state.historyError = null;
    state.historyLoading = false;
    renderHistoryList();
    return;
  }
  const sequence = ++state.historyRequestSequence;
  const configKey = historyConfigKey();
  state.historyLoading = true;
  state.historyError = null;
  if (!silent) renderHistoryList();
  try {
    const response = await fetch(historyUrl(), {
      headers: demoHeaders(),
      cache: "no-store",
    });
    if (!response.ok) throw await responseError(response);
    const payload = await response.json();
    if (sequence !== state.historyRequestSequence || configKey !== historyConfigKey()) return;
    state.conversations = normalizeConversationIndex(payload)
      .filter((item) => !item.serviceKind || item.serviceKind === "nyxid.chat");
    for (const conversation of state.conversations) {
      const entry = findConversationState(conversation.id);
      if (!entry) continue;
      entry.meta = conversation;
      if (conversation.stateVersion > 0) ensureConversationProjectionVersion(entry);
      if (!entry.controller) entry.title = conversation.title;
      if (entry === state.activeConversation) renderActorProjection(entry);
    }
    const current = state.conversations.find((item) => item.id === state.actorId);
    if (current) {
      state.currentConversationMeta = current;
      if (state.activeConversation) state.activeConversation.meta = current;
      setConversationTitle(current.title);
      if (!state.activeController) {
        dom.sidebarSessionMeta.textContent = `${current.messageCount} 条消息 · ${formatHistoryTime(current.updatedAt)}`;
      }
    }
    state.historyError = null;
  } catch (error) {
    if (sequence !== state.historyRequestSequence || configKey !== historyConfigKey()) return;
    state.historyError = error.message || "无法读取生产会话";
  } finally {
    if (sequence === state.historyRequestSequence) {
      state.historyLoading = false;
      renderHistoryList();
    }
  }
}

function renderHistoryList() {
  if (!dom.recentSessionsList) return;
  dom.recentSessionsList.replaceChildren();
  const needsYou = state.conversations.filter((conversation) =>
    conversation.attentionKind === "input" ||
    conversation.attentionKind === "approval" ||
    conversation.attentionKind === "stalled");
  dom.needsYouCount.textContent = String(needsYou.length);
  const filteringNeedsYou = state.historyFilter === "needs-you";
  dom.needsYouFilterButton.setAttribute("aria-pressed", String(filteringNeedsYou));
  dom.needsYouFilterButton.classList.toggle("active", filteringNeedsYou);
  if (state.config.surface !== "nyxid-chat") return;
  if (!state.auth.authenticated) {
    dom.recentSessionsList.append(el("div", "history-empty", "登录后显示会话"));
    return;
  }
  if (state.historyLoading && !state.conversations.length) {
    dom.recentSessionsList.append(el("div", "history-empty", "正在加载生产会话…"));
    return;
  }
  if (state.historyError) {
    const error = el("div", "history-error");
    error.append(el("span", "", state.historyError));
    const retry = el("button", "history-retry", "重试");
    retry.type = "button";
    retry.addEventListener("click", () => void loadConversations());
    error.append(retry);
    dom.recentSessionsList.append(error);
    return;
  }
  const recent = filteringNeedsYou ? needsYou : state.conversations;
  if (!recent.length) {
    dom.recentSessionsList.append(el(
      "div",
      "history-empty",
      filteringNeedsYou ? "当前没有需要处理的会话" : "暂无其他生产会话",
    ));
    return;
  }
  for (const conversation of recent) {
    const attentionKind = conversation.attentionKind === "input" ||
      conversation.attentionKind === "approval" ||
      conversation.attentionKind === "stalled"
      ? conversation.attentionKind
      : null;
    const row = el(
      "div",
      `history-row${conversation.id === state.activeConversation?.actorId ? " active" : ""}` +
        `${attentionKind ? " needs-you" : ""}`,
    );
    const open = el("button", "history-session");
    open.type = "button";
    open.title = conversation.title;
    const copy = el("span", "history-session-copy");
    const conversationState = findConversationState(conversation.id);
    const running = Boolean(conversationState?.controller);
    const meta = attentionKind
      ? `${attentionKind === "input"
        ? "等待输入"
        : attentionKind === "approval"
          ? "等待批准"
          : "进度停滞"} · ${formatHistoryTime(conversation.attentionSince)}`
      : `${conversation.messageCount} 条消息 · ${formatHistoryTime(conversation.updatedAt)}` +
        (running ? " · 运行中" : "");
    copy.append(el("strong", "", conversation.title), el("small", "", meta));
    if (attentionKind && conversation.activeStepSummary) {
      copy.append(el("span", "history-attention-summary", conversation.activeStepSummary));
    }
    open.append(iconNode("message-circle"), copy);
    open.addEventListener("click", () => void loadConversation(conversation));
    const remove = el("button", "history-delete");
    remove.type = "button";
    remove.title = `删除 ${conversation.title}`;
    remove.setAttribute("aria-label", `删除会话：${conversation.title}`);
    remove.append(iconNode("trash-2"));
    remove.addEventListener("click", (event) => {
      event.stopPropagation();
      void deleteConversation(conversation, remove);
    });
    row.append(open, remove);
    dom.recentSessionsList.append(row);
  }
  refreshIcons(dom.recentSessionsList);
}

async function loadConversation(conversation) {
  const sequence = ++state.conversationLoadSequence;
  const cached = findConversationState(conversation.id);
  if (cached) {
    cached.meta = conversation;
    cached.title = conversation.title;
    if (conversation.stateVersion > 0) ensureConversationProjectionVersion(cached);
    activateConversationState(cached);
    renderActorProjection(cached);
    renderActiveConversationState();
    closeMobilePanels();
    void refreshActorState(cached);
    return;
  }
  const configKey = historyConfigKey();
  try {
    const response = await fetch(historyUrl(conversation.id), {
      headers: demoHeaders(),
      cache: "no-store",
    });
    if (!response.ok) throw await responseError(response);
    const payload = await response.json();
    const messages = normalizeStoredMessages(payload);
    const storedOperations = Array.isArray(payload?.operations) ? payload.operations : [];
    if (sequence !== state.conversationLoadSequence || configKey !== historyConfigKey()) return;
    const existing = findConversationState(conversation.id);
    if (existing) {
      existing.meta = conversation;
      existing.title = conversation.title;
      activateConversationState(existing);
      renderActiveConversationState();
      closeMobilePanels();
      return;
    }
    const entry = createConversationState({
      actorId: conversation.id,
      meta: conversation,
      title: conversation.title,
    });
    if (conversation.stateVersion > 0) ensureConversationProjectionVersion(entry);
    restoreTrajectoryFromStoredOperations(entry, storedOperations, messages);
    activateConversationState(entry);
    renderActorProjection(entry);
    state.run.context = {
      actorId: conversation.id,
    };
    dom.thread.replaceChildren();
    for (const message of messages) renderStoredMessage(message);
    if (!messages.length) {
      const { body } = createMessageShell("assistant");
      body.append(el("div", "info-callout", "该生产会话目前没有已存储消息。"));
    }
    persistConversationState(entry);
    renderActiveConversationState();
    closeMobilePanels();
    refreshIcons(dom.thread);
    scrollThread();
    void refreshActorState(entry);
  } catch (error) {
    if (sequence !== state.conversationLoadSequence) return;
    showToast(error.message || "无法读取生产会话");
  }
}

function actorStateTurnId(projection) {
  return projection?.activeTurn?.turnId ||
    projection?.latestTurn?.turnId ||
    projection?.task?.turnId ||
    "";
}

async function refreshActorState(entry, { uncursored = false } = {}) {
  return refreshActorStateFor(entry, entry?.actorId, { uncursored });
}

async function refreshActionActorState(entry, actorId, { uncursored = false } = {}) {
  if (!entry || !actorId || actorId === entry.actorId) {
    return refreshActorState(entry, { uncursored });
  }
  return refreshActorStateFor(entry, actorId, { uncursored });
}

async function refreshActorStateFor(entry, actorId, { uncursored = false } = {}) {
  if (!entry || !actorId) return null;
  const isConversationActor = actorId === entry.actorId;
  entry.actionStateReloads ||= new Map();
  const inFlight = isConversationActor
    ? entry.stateReloadInFlight
    : entry.actionStateReloads.get(actorId);
  if (inFlight && !uncursored) return inFlight;

  const request = (async () => {
    const params = new URLSearchParams();
    const projection = isConversationActor
      ? ensureConversationProjectionVersion(entry) || createActorProjection(actorId)
      : actorProjectionFor(entry, actorId);
    const turnId = actorStateTurnId(projection);
    if (!uncursored && projection.stateVersion > 0 && turnId) {
      params.set("afterStateVersion", String(projection.stateVersion));
      params.set("turnId", turnId);
    }
    const query = params.size ? `?${params}` : "";
    try {
      const response = await fetch(
        `/api/demo/conversations/${encodeURIComponent(actorId)}/state${query}`,
        { headers: demoHeaders(), cache: "no-store" },
      );
      if (!response.ok) throw await responseError(response);
      const result = applyCurrentStateResult(projection, await response.json());
      setActorProjectionFor(entry, actorId, result.projection);
      // The conversation actor owns the in-flight turn's step ledger. Rebuilding
      // its trace container here is what makes a mid-run reload keep its
      // trajectory; committed turns come from the stored chat history instead.
      if (isConversationActor) restoreTrajectoryFromActorProjection(entry, result.projection);
      if (result.reloadWithoutCursor) {
        if (!uncursored) return refreshActorStateFor(entry, actorId, { uncursored: true });
        setActorStateNotice(entry, actorId, "Actor 要求重新加载状态，请稍后重试。");
      } else if (result.projection.stateVersion === 0 && !result.projection.task) {
        setActorStateNotice(entry, actorId, "该会话没有可恢复的 actor 状态。");
      } else {
        setActorStateNotice(entry, actorId, "");
      }
      restoreProjectionActionCaches(entry, actorId);
      renderActorProjection(entry, actorId);
      renderActionCards(entry);
      if (entry === state.activeConversation) {
        renderActiveConversationState();
        renderRequestTraces(entry);
      }
      return actorProjectionFor(entry, actorId);
    } catch (error) {
      setActorStateNotice(
        entry,
        actorId,
        `无法恢复 actor 状态：${String(error?.message || "unknown error").slice(0, 300)}`,
      );
      renderActorProjection(entry, actorId);
      return null;
    }
  })();

  if (isConversationActor) entry.stateReloadInFlight = request;
  else entry.actionStateReloads.set(actorId, request);
  try {
    return await request;
  } finally {
    if (isConversationActor) {
      if (entry.stateReloadInFlight === request) entry.stateReloadInFlight = null;
    } else if (entry.actionStateReloads.get(actorId) === request) {
      entry.actionStateReloads.delete(actorId);
    }
  }
}

function actorStateNotice(entry, actorId) {
  if (!entry || !actorId || actorId === entry.actorId) return entry?.actorStateNotice || "";
  return entry.actionStateNotices?.get(actorId) || "";
}

function setActorStateNotice(entry, actorId, message) {
  if (!entry || !actorId || actorId === entry.actorId) {
    if (entry) entry.actorStateNotice = message;
    return;
  }
  entry.actionStateNotices ||= new Map();
  if (message) entry.actionStateNotices.set(actorId, message);
  else entry.actionStateNotices.delete(actorId);
}

function entryActorProjection(entry = state.activeConversation) {
  if (!entry) return null;
  actorProjectionFor(entry, entry.actorId);
  ensureConversationProjectionVersion(entry);
  return entry.actorProjection;
}

function actorOperationGeneration(step) {
  const value = step?.operation?.key?.operationGeneration ??
    step?.operation?.generation ??
    step?.operationGeneration;
  return Number.isSafeInteger(value) && value >= 1 ? value : null;
}

function actorControlTurnId(projection) {
  return projection?.activeTurn?.turnId || projection?.task?.turnId || projection?.latestTurn?.turnId || "";
}

function actorTerminalRunStatus(projection) {
  const actorStatus = String(projection?.task?.status || projection?.latestTurn?.status || "").toLowerCase();
  const mapping = {
    succeeded: "complete",
    failed: "error",
    stopped: "stopped",
    blocked: "blocked",
  };
  return mapping[actorStatus] || null;
}

function actorStatusCopy(status) {
  const labels = {
    active: "进行中",
    blocked: "已阻塞",
    stopped: "已停止",
    failed: "失败",
    succeeded: "成功",
    uncertain: "结果不确定",
    waiting: "等待中",
    running: "执行中",
    done: "已完成",
    skipped: "已跳过",
  };
  return labels[String(status || "").toLowerCase()] || String(status || "状态未知");
}

const actorEffectCopy = {
  not_started: {
    label: "尚未开始",
    explanation: "外部执行尚未开始。",
  },
  not_applied: {
    label: "未产生变更",
    explanation: "Actor 证据确认外部系统没有发生变更。",
  },
  confirmed: {
    label: "已确认变更",
    explanation: "Actor 证据确认外部变更已经发生，不应重复执行。",
  },
  may_have_changed: {
    label: "可能已变更",
    explanation: "请求已越过分发边界；这既不是确认成功，也不是已证明失败。",
  },
};

function actorStepEvidenceDetail(step) {
  const operation = step.operation || step.externalOperation || {};
  const message = step.safeMessage || operation.safeMessage || "";
  const code = step.failureCode || operation.terminalCode || "";
  return [message, code].filter(Boolean).join(" · ");
}

function actorStepManagementCapability(step) {
  const serviceId = step?.source?.tool?.serviceId;
  if (typeof serviceId !== "string" || !serviceId) return null;
  return state.readiness.snapshot?.capabilities?.find((capability) =>
    capability.capabilityId === serviceId &&
    capability.status !== "available" &&
    capability.managementUrl) || null;
}

function actorStepSourceLabel(step) {
  const source = step?.source || {};
  if (source.llm) return source.llm.model ? `LLM · ${source.llm.model}` : "LLM";
  if (source.tool) {
    const tool = describeToolOperation(source.tool);
    const kind = {
      nyxIdOperation: "NyxID 连接服务",
      builtIn: "内置工具",
      mcp: "MCP 工具",
      skill: "Skill",
    }[tool.kind] || "工具";
    return [tool.serviceLabel, kind].filter(Boolean).join(" · ");
  }
  if (source.browserAction) return `NyxID Action · ${source.browserAction.action || "browser"}`;
  if (source.postcondition) {
    return `验证 · ${source.postcondition.check || "postcondition"}`;
  }
  if (source.input) return "用户输入";
  if (source.approval) return "审批";
  if (source.web) return "Web";
  return step?.kind || "步骤";
}

function actorStepDisplayName(step) {
  const source = step?.source || {};
  if (source.tool) {
    const tool = describeToolOperation(source.tool);
    return tool.serviceLabel
      ? `通过 ${tool.serviceLabel} 执行 ${tool.displayName}`
      : `执行 ${tool.displayName}`;
  }
  const description = normalizedToolText(step?.description, 400);
  if (description && !containsOpaqueToolInvocation(description)) return description;
  return {
    llm: "生成 AI 回复",
    input: "等待用户输入",
    approval: "等待用户确认",
    browser_action: "执行浏览器操作",
    postcondition: "验证执行结果",
    web: "访问 Web 内容",
  }[String(step?.kind || "").toLowerCase()] || "执行计划步骤";
}

function actorStepEffectLabel(step) {
  return actorEffectCopy[String(step?.externalEffect || "").toLowerCase()]?.label || "外部影响未上报";
}

function actorAddedByLabel(addedBy) {
  return {
    initial: "初始计划",
    replan: "重新规划",
    steering: "用户调整",
  }[addedBy] || "计划步骤";
}

function renderActorRecovery(projection) {
  const steps = [...projection.steps.values()];
  const hasFailure = steps.some((step) =>
    step.status === "failed" || step.status === "uncertain" ||
    step.externalEffect === "may_have_changed");
  if (!hasFailure) return null;

  const groups = [
    ["已完成", steps.filter((step) =>
      step.status === "done" && step.externalEffect !== "confirmed")],
    ["外部已变更", steps.filter((step) => step.externalEffect === "confirmed")],
    ["外部可能已变更", steps.filter((step) => step.externalEffect === "may_have_changed")],
    ["失败", steps.filter((step) => step.status === "failed")],
  ];
  const recovery = el("div", "actor-recovery");
  recovery.setAttribute("role", "status");
  recovery.setAttribute("aria-live", "polite");
  for (const [label, facts] of groups) {
    if (!facts.length) continue;
    const group = el("section", "actor-recovery-fact");
    group.append(el("strong", "actor-recovery-label", label));
    for (const step of facts) {
      const item = el("div", "actor-recovery-item");
      item.append(el("span", "", actorStepDisplayName(step)));
      const detail = actorStepEvidenceDetail(step);
      if (detail) item.append(el("small", "", detail));
      group.append(item);
    }
    recovery.append(group);
  }
  return recovery.childElementCount ? recovery : null;
}

function needsYouKey(kind, requestId, actorId = "") {
  return `${actorId}:${kind}:${requestId}`;
}

function pruneNeedsYouState(entry, projection) {
  const actorId = projection?.actorId || entry?.actorId || "";
  const actorPrefix = `${actorId}:`;
  const activeKeys = new Set();
  if (projection.pendingInput?.requestId) {
    activeKeys.add(needsYouKey("input", projection.pendingInput.requestId, actorId));
  }
  if (projection.pendingApproval?.approvalRequestId) {
    activeKeys.add(needsYouKey("approval", projection.pendingApproval.approvalRequestId, actorId));
  }
  for (const key of entry.needsYouDrafts.keys()) {
    if (key.startsWith(actorPrefix) && !activeKeys.has(key)) entry.needsYouDrafts.delete(key);
  }
  for (const key of entry.needsYouSubmissions.keys()) {
    if (key.startsWith(actorPrefix) && !activeKeys.has(key)) entry.needsYouSubmissions.delete(key);
  }
  const approvalKey = projection.pendingApproval?.approvalRequestId
    ? needsYouKey("approval", projection.pendingApproval.approvalRequestId, actorId)
    : null;
  if (!projection.pendingApproval ||
      entry.approvalConfirmRequestId !== approvalKey) {
    entry.approvalConfirmRequestId = null;
  }
}

function renderComposerInputRequest(entry, projection) {
  if (entry !== state.activeConversation) return;
  const pending = projection?.pendingInput;
  if (!pending?.requestId) {
    dom.composerInputRequest.classList.add("hidden");
    dom.composerInputPrompt.textContent = "";
    dom.composerInputOptions.replaceChildren();
    dom.composerInputOptions.classList.add("hidden");
    dom.promptInput.readOnly = false;
    dom.promptInput.placeholder = "告诉 Assistant 你要完成的操作";
    dom.promptInput.removeAttribute("aria-describedby");
    return;
  }

  const key = needsYouKey("input", pending.requestId, projection.actorId || entry.actorId);
  const draft = entry.needsYouDrafts.get(key) || { selectedOptionIds: new Set(), freeText: "" };
  entry.needsYouDrafts.set(key, draft);
  const submission = entry.needsYouSubmissions.get(key);
  const locked = submission?.status === "pending" || submission?.status === "accepted";
  dom.composerInputRequest.classList.remove("hidden");
  dom.composerInputPrompt.textContent = pending.prompt;
  dom.composerInputOptions.replaceChildren();
  for (const option of pending.options || []) {
    const selected = draft.selectedOptionIds.has(option.optionId);
    const button = el("button", `composer-input-option${selected ? " selected" : ""}`, option.label);
    button.type = "button";
    button.disabled = locked;
    button.setAttribute("aria-pressed", String(selected));
    if (option.description) button.title = option.description;
    button.addEventListener("click", () => {
      if (pending.multiSelect) {
        if (selected) draft.selectedOptionIds.delete(option.optionId);
        else draft.selectedOptionIds.add(option.optionId);
      } else {
        draft.selectedOptionIds.clear();
        if (!selected) draft.selectedOptionIds.add(option.optionId);
      }
      if (draft.selectedOptionIds.size) {
        dom.promptInput.value = "";
        entry.draft = "";
      }
      renderComposerInputRequest(entry, projection);
      autoResizeComposer();
      renderActorControlUi();
    });
    dom.composerInputOptions.append(button);
  }
  dom.composerInputOptions.classList.toggle("hidden", !dom.composerInputOptions.childElementCount);
  dom.promptInput.readOnly = !pending.allowFreeText;
  dom.promptInput.placeholder = pending.allowFreeText
    ? "一次写完所有需要补充的信息"
    : "选择上方选项后提交";
  dom.promptInput.setAttribute("aria-describedby", "composerInputPrompt");
  refreshIcons(dom.composerInputRequest);
}

function renderPendingInput(entry, projection) {
  const pending = projection.pendingInput;
  if (!pending?.requestId) return null;
  const key = needsYouKey("input", pending.requestId, projection.actorId || entry.actorId);
  const draft = entry.needsYouDrafts.get(key) || { selectedOptionIds: new Set(), freeText: "" };
  entry.needsYouDrafts.set(key, draft);
  const submission = entry.needsYouSubmissions.get(key);
  const section = el("section", "needs-you-panel input-required");
  section.dataset.requestId = pending.requestId;
  const heading = el("div", "needs-you-heading");
  heading.append(iconNode("circle-help"), el("strong", "", "需要你的输入"));
  section.append(heading, el("p", "needs-you-prompt", pending.prompt));
  section.append(el(
    "p",
    "needs-you-boundary",
    (pending.options || []).length
      ? "请在下方输入区选择一个建议，或一次写完你的完整回答。"
      : "请在下方输入框一次写完你的完整回答。",
  ));
  if (submission?.message) {
    section.append(el(
      "span",
      `needs-you-state ${submission.status || ""}`,
      submission.message,
    ));
  }
  return section;
}

function renderPendingApproval(entry, projection) {
  const pending = projection.pendingApproval;
  if (!pending?.approvalRequestId) return null;
  const requestId = pending.approvalRequestId;
  const actorId = projection.actorId || entry.actorId;
  const key = needsYouKey("approval", requestId, actorId);
  const draft = entry.needsYouDrafts.get(key) || { reason: "" };
  entry.needsYouDrafts.set(key, draft);
  const submission = entry.needsYouSubmissions.get(key);
  const locked = submission?.status === "pending" || submission?.status === "accepted";
  const reliableVersion = actorStateVersion(entry, actorId);
  const outsideGrant = pending.grantBoundary !== "within_grant";
  const irreversible = pending.reversibility === "irreversible";
  const confirming = irreversible && entry.approvalConfirmRequestId === key;
  const section = el("section", `needs-you-panel approval-required${irreversible ? " dangerous" : ""}`);
  section.dataset.requestId = requestId;
  const heading = el("div", "needs-you-heading");
  heading.append(iconNode(irreversible ? "triangle-alert" : "shield-alert"), el("strong", "", "需要你的批准"));
  section.append(heading);

  const facts = el("dl", "approval-facts");
  const appendFact = (label, value) => {
    if (!value) return;
    facts.append(el("dt", "", label), el("dd", "", value));
  };
  appendFact("操作", pending.action || pending.toolName);
  appendFact("目标", pending.target);
  appendFact("执行者", pending.actorLabel);
  appendFact("可逆性", pending.reversibility === "irreversible" ? "不可逆" :
    pending.reversibility === "reversible" ? "可逆" : "未知");
  appendFact("过期时间", pending.expiresAt ? new Date(pending.expiresAt).toLocaleString("zh-CN") : "");
  section.append(facts);

  if (outsideGrant) {
    section.append(el(
      "p",
      "needs-you-boundary",
      "该操作超出当前授权边界。授权由 NyxID 管理，Studio 不会在此批准。",
    ));
    const manage = el("button", "needs-you-secondary", "前往 NyxID 管理授权");
    manage.type = "button";
    manage.addEventListener("click", () => openServiceManagement());
    section.append(manage);
    return section;
  }

  const reason = document.createElement("textarea");
  reason.className = "needs-you-reason";
  reason.rows = 2;
  reason.placeholder = "说明原因（可选）";
  reason.setAttribute("aria-label", "批准或拒绝原因");
  reason.value = draft.reason;
  reason.disabled = locked;
  reason.addEventListener("input", () => { draft.reason = reason.value; });
  section.append(reason);

  if (confirming) {
    section.append(el("p", "danger-confirmation", "这是不可逆操作。请再次确认操作与目标无误。"));
  }
  const footer = el("div", "needs-you-actions");
  const approve = el(
    "button",
    confirming ? "needs-you-danger" : "needs-you-primary",
    confirming ? "确认批准" : irreversible ? "审查并批准" : "批准",
  );
  approve.type = "button";
  approve.disabled = locked || reliableVersion <= 0;
  approve.addEventListener("click", () => {
    if (irreversible && !confirming) {
      entry.approvalConfirmRequestId = key;
      renderActorProjection(entry, actorId);
      return;
    }
    void submitNeedsYouDecision(entry, "approval", requestId, {
      type: "approval.resolve",
      approved: true,
      reason: draft.reason.trim(),
    }, { actorId, projection });
  });
  const reject = el("button", "needs-you-secondary", "拒绝");
  reject.type = "button";
  reject.disabled = locked || reliableVersion <= 0;
  reject.addEventListener("click", () => void submitNeedsYouDecision(entry, "approval", requestId, {
    type: "approval.resolve",
    approved: false,
    reason: draft.reason.trim(),
  }, { actorId, projection }));
  const status = el("span", `needs-you-state ${submission?.status || ""}`,
    submission?.message || (reliableVersion <= 0 ? "正在同步 Actor 状态…" : ""));
  footer.append(approve, reject, status);
  section.append(footer);
  return section;
}

async function submitNeedsYouDecision(
  entry,
  kind,
  requestId,
  payload,
  { actorId = entry?.actorId, projection = actorProjectionFor(entry, actorId) } = {},
) {
  if (!actorId || !projection || !requestId) return false;
  const key = needsYouKey(kind, requestId, actorId);
  const existing = entry.needsYouSubmissions.get(key);
  if (existing?.status === "pending" || existing?.status === "accepted") return false;
  const clientRequestId = createId(`client-${kind}`);
  entry.needsYouSubmissions.set(key, { status: "pending", message: "正在同步 Actor 状态…" });
  renderActorProjection(entry, actorId);
  try {
    const refreshedProjection = await refreshActorStateFor(
      entry,
      actorId,
      { uncursored: true },
    );
    const reliableVersion = actorStateVersion(entry, actorId);
    if (!refreshedProjection || reliableVersion <= 0) {
      throw new Error("无法同步最新 Actor 状态。");
    }
    const response = await fetch("/api/demo/chat", {
      method: "POST",
      headers: demoHeaders(),
      body: JSON.stringify({
        surface: "nyxid-chat",
        ...payload,
        conversationId: actorId,
        requestId,
        clientRequestId,
        expectedStateVersion: reliableVersion,
      }),
    });
    if (!response.ok) throw await responseError(response);
    await response.json().catch(() => null);
    entry.needsYouSubmissions.set(key, {
      status: "accepted",
      message: "已受理，等待 Actor 确认。",
    });
    renderActorProjection(entry, actorId);
    await refreshActorStateFor(entry, actorId);
    scheduleActorStateRefresh(entry, actorId, 500);
    return true;
  } catch (error) {
    entry.needsYouSubmissions.set(key, {
      status: "error",
      message: `提交失败：${String(error?.message || "unknown error").slice(0, 240)}`,
    });
    renderActorProjection(entry, actorId);
    await refreshActorStateFor(entry, actorId, { uncursored: true });
    return false;
  }
}

function terminalConversationHistoryReady(messages, turnId) {
  if (!Array.isArray(messages) || !messages.length) return false;
  const expectedTurnId = String(turnId || "").trim();
  if (!expectedTurnId) return false;
  const last = messages.at(-1);
  return last?.role === "assistant" &&
    String(last.turnId || "").trim() === expectedTurnId &&
    Boolean(String(last.content || last.error || "").trim());
}

function replaceConversationHistory(
  entry,
  messages,
  projection,
  terminalStatus,
  expectedRun = entry?.run,
) {
  if (!entry?.thread || entry.controller || entry.run !== expectedRun ||
      !terminalConversationHistoryReady(messages, actorStateTurnId(projection))) {
    return false;
  }
  withConversationState(entry, () => {
    const recoveredRun = createRunState();
    recoveredRun.surface = "nyxid-chat";
    recoveredRun.status = terminalStatus;
    recoveredRun.completedAt = Date.now();
    recoveredRun.context = {
      actorId: entry.actorId,
      turnId: actorStateTurnId(projection),
    };
    state.run = recoveredRun;
    entry.run = recoveredRun;
    entry.actorTaskElement?.remove();
    entry.actorTaskElement = null;
    for (const element of entry.actionActorTaskElements?.values?.() || []) element.remove();
    entry.actionActorTaskElements?.clear?.();
    dom.thread.replaceChildren();
    for (const message of messages) renderStoredMessage(message);
    if (entry.meta) {
      entry.meta = {
        ...entry.meta,
        messageCount: messages.length,
      };
    }
    renderActorProjection(entry);
    if (entry === state.activeConversation) {
      renderActiveConversationState();
      scrollThread();
    }
    refreshIcons(entry.thread);
  });
  return true;
}

async function recoverTerminalConversation(entry, projection, terminalStatus) {
  if (!entry?.actorId || !projection) return false;
  if (entry.controller) return true;
  const turnId = actorStateTurnId(projection);
  if (!turnId) return false;
  if (entry.historyRecoveredTurnId === turnId) return true;
  const recoveryRun = entry.run;
  try {
    await loadConversations({ silent: true });
    if (entry.controller || entry.run !== recoveryRun) return true;
    const response = await fetch(historyUrl(entry.actorId), {
      headers: demoHeaders(),
      cache: "no-store",
    });
    if (!response.ok) throw await responseError(response);
    const messages = normalizeStoredMessages(await response.json());
    if (entry.controller || entry.run !== recoveryRun) return true;
    if (!terminalConversationHistoryReady(messages, turnId)) return false;
    if (!replaceConversationHistory(entry, messages, projection, terminalStatus, recoveryRun)) {
      return Boolean(entry.controller || entry.run !== recoveryRun);
    }
    entry.historyRecoveredTurnId = turnId;
    setActorStateNotice(entry, entry.actorId, "");
    return true;
  } catch (error) {
    if (entry.controller || entry.run !== recoveryRun) return true;
    setActorStateNotice(
      entry,
      entry.actorId,
      `Actor 已到终态，但最终回复恢复失败：${String(error?.message || "unknown error").slice(0, 240)}`,
    );
    renderActorProjection(entry);
    return false;
  }
}

function actorStateFollowNeeded(entry, actorId, projection) {
  if (!entry || !actorId || !projection || actorTerminalRunStatus(projection)) return false;
  const attention = projection.pendingInput?.requestId
    ? ["input", projection.pendingInput.requestId]
    : projection.pendingApproval?.approvalRequestId
      ? ["approval", projection.pendingApproval.approvalRequestId]
      : null;
  if (attention) {
    const submission = entry.needsYouSubmissions.get(needsYouKey(attention[0], attention[1], actorId));
    if (!submission || !["pending", "accepted"].includes(submission.status)) return false;
  }
  const status = String(
    projection.task?.status || projection.taskStatus || projection.activeTurn?.status || "",
  ).toLowerCase();
  return ["active", "running", "waiting", "planned"].includes(status);
}

function markActorRunFollowing(entry, actorId) {
  if (!entry?.run || actorId !== entry.actorId) return;
  entry.run.status = "running";
  entry.run.completedAt = null;
  if (entry === state.activeConversation) renderActiveConversationState();
}

function actorStateFollowGeneration(entry, actorId) {
  if (!entry || !actorId) return 0;
  if (actorId === entry.actorId) {
    return Number.isSafeInteger(entry.actorStateRefreshGeneration)
      ? entry.actorStateRefreshGeneration
      : 0;
  }
  entry.actionStateRefreshGenerations ||= new Map();
  const generation = entry.actionStateRefreshGenerations.get(actorId);
  return Number.isSafeInteger(generation) ? generation : 0;
}

function advanceActorStateFollowGeneration(entry, actorId) {
  const generation = actorStateFollowGeneration(entry, actorId) + 1;
  if (actorId === entry.actorId) {
    entry.actorStateRefreshGeneration = generation;
  } else {
    entry.actionStateRefreshGenerations ||= new Map();
    entry.actionStateRefreshGenerations.set(actorId, generation);
  }
  return generation;
}

function actorStateFollowIsCurrent(entry, actorId, generation) {
  return actorStateFollowGeneration(entry, actorId) === generation;
}

async function followActorStateRefresh(entry, actorId, attemptsRemaining, generation) {
  if (!actorStateFollowIsCurrent(entry, actorId, generation)) return;
  const projection = actorId === entry.actorId
    ? await refreshActorState(entry)
    : await refreshActionActorState(entry, actorId);
  if (!actorStateFollowIsCurrent(entry, actorId, generation)) return;
  if (!projection) {
    if (attemptsRemaining > 1) {
      scheduleActorStateRefresh(entry, actorId, 1000, attemptsRemaining - 1, generation);
    }
    return;
  }

  const terminalStatus = actorTerminalRunStatus(projection);
  if (terminalStatus) {
    if (actorId !== entry.actorId) return;
    const turnId = actorStateTurnId(projection);
    const liveTerminalReply = terminalStatus === "complete" &&
      entry.run?.status === "complete" && Boolean(entry.run.assistantText?.trim()) &&
      (!turnId || entry.run?.context?.turnId === turnId);
    const alreadyRecovered = Boolean(turnId) && entry.historyRecoveredTurnId === turnId;
    if (liveTerminalReply || alreadyRecovered) {
      entry.run.status = terminalStatus;
      entry.run.completedAt ||= Date.now();
      if (entry === state.activeConversation) renderActiveConversationState();
      return;
    }
    const recovered = await recoverTerminalConversation(entry, projection, terminalStatus);
    if (!actorStateFollowIsCurrent(entry, actorId, generation)) return;
    if (!recovered && attemptsRemaining > 1) {
      setActorStateNotice(entry, actorId, "Actor 已到终态，正在恢复最终回复…");
      renderActorProjection(entry, actorId);
      scheduleActorStateRefresh(entry, actorId, 750, attemptsRemaining - 1, generation);
    } else if (!recovered) {
      setActorStateNotice(
        entry,
        actorId,
        "Actor 已到终态，但最终回复尚未进入会话历史。重新打开会话可继续恢复。",
      );
      renderActorProjection(entry, actorId);
    }
    return;
  }

  markActorRunFollowing(entry, actorId);
  if (!actorStateFollowNeeded(entry, actorId, projection)) return;
  if (attemptsRemaining > 1) {
    scheduleActorStateRefresh(entry, actorId, 1000, attemptsRemaining - 1, generation);
  } else {
    setActorStateNotice(entry, actorId, "Actor 仍在执行，但自动状态跟随已超时。重新打开会话可继续恢复。");
    renderActorProjection(entry, actorId);
  }
}

function scheduleActorStateRefresh(
  entry,
  actorId,
  delayMs = 500,
  attemptsRemaining = 300,
  generation = null,
) {
  if (!entry || !actorId || attemptsRemaining <= 0) return;
  const followGeneration = Number.isSafeInteger(generation) && generation > 0
    ? generation
    : advanceActorStateFollowGeneration(entry, actorId);
  if (!actorStateFollowIsCurrent(entry, actorId, followGeneration)) return;
  if (actorId === entry.actorId) {
    if (entry.actorStateRefreshTimer) window.clearTimeout(entry.actorStateRefreshTimer);
    const timer = window.setTimeout(async () => {
      if (entry.actorStateRefreshTimer !== timer ||
          !actorStateFollowIsCurrent(entry, actorId, followGeneration)) {
        return;
      }
      entry.actorStateRefreshTimer = null;
      await followActorStateRefresh(entry, actorId, attemptsRemaining, followGeneration);
    }, delayMs);
    entry.actorStateRefreshTimer = timer;
    return;
  }
  entry.actionStateRefreshTimers ||= new Map();
  const current = entry.actionStateRefreshTimers.get(actorId);
  if (current) window.clearTimeout(current);
  const timer = window.setTimeout(async () => {
    if (entry.actionStateRefreshTimers.get(actorId) !== timer ||
        !actorStateFollowIsCurrent(entry, actorId, followGeneration)) {
      return;
    }
    entry.actionStateRefreshTimers.delete(actorId);
    await followActorStateRefresh(entry, actorId, attemptsRemaining, followGeneration);
  }, delayMs);
  entry.actionStateRefreshTimers.set(actorId, timer);
}

function actorTaskElementFor(entry, actorId) {
  if (!entry || !actorId || actorId === entry.actorId) return entry?.actorTaskElement || null;
  return entry.actionActorTaskElements?.get(actorId) || null;
}

function setActorTaskElement(entry, actorId, element) {
  if (!entry || !actorId || actorId === entry.actorId) {
    if (entry) entry.actorTaskElement = element;
    return;
  }
  entry.actionActorTaskElements ||= new Map();
  if (element) entry.actionActorTaskElements.set(actorId, element);
  else entry.actionActorTaskElements.delete(actorId);
}

function actorControlReceiptFor(entry, actorId) {
  if (!entry || !actorId || actorId === entry.actorId) return entry?.actorControlReceipt || null;
  return entry.actionActorControlReceipts?.get(actorId) || null;
}

function renderActorProjection(entry, actorId = entry?.actorId) {
  if (!entry?.thread) return;
  const projection = actorProjectionFor(entry, actorId) || createActorProjection(actorId);
  const notice = actorStateNotice(entry, actorId);
  const isConversationActor = actorId === entry.actorId;
  const hasProjection = Boolean(
    projection.task || projection.pendingInput || projection.pendingApproval ||
    projection.actions.size || projection.conflicts.length || notice,
  );
  if (!hasProjection) {
    actorTaskElementFor(entry, actorId)?.remove();
    setActorTaskElement(entry, actorId, null);
    if (isConversationActor && entry === state.activeConversation) {
      renderComposerInputRequest(entry, projection);
      renderInspector();
    }
    return;
  }

  const root = actorTaskElementFor(entry, actorId) || el("section", "actor-task");
  setActorTaskElement(entry, actorId, root);
  if (root.dataset.collapsed !== "true" && root.dataset.collapsed !== "false") {
    root.dataset.collapsed = "false";
  }
  root.replaceChildren();
  const task = projection.task;
  const status = String(
    task?.status || projection.taskStatus || projection.latestTurn?.status || "unknown",
  ).toLowerCase();
  root.className = `actor-task ${status}${isConversationActor ? "" : " action-actor-task"}`;
  root.classList.toggle("collapsed", root.dataset.collapsed === "true");
  root.dataset.actorId = projection.actorId || actorId || "";
  if (task?.taskId) root.dataset.taskId = task.taskId;
  if (task?.turnId) root.dataset.turnId = task.turnId;
  if (task?.planId) root.dataset.planId = task.planId;

  const taskSteps = [...projection.steps.values()];
  const completedSteps = taskSteps.filter((step) =>
    ["done", "skipped", "cancelled"].includes(String(step.status || "").toLowerCase()));
  const currentStep = taskSteps.find((step) =>
    ["running", "waiting"].includes(String(step.status || "").toLowerCase())) ||
    taskSteps.find((step) => String(step.status || "").toLowerCase() === "planned");

  const header = el("header", "actor-task-header");
  const title = el("div", "actor-task-title");
  title.append(
    el("span", "actor-task-eyebrow", `计划 · Revision ${task?.planRevision || 1}`),
    el("strong", "", task?.title || actorStatusCopy(status)),
  );
  if (currentStep) {
    title.append(el(
      "small",
      "actor-task-current",
      `${actorStatusCopy(currentStep.status)} · ${actorStepDisplayName(currentStep)}`,
    ));
  }
  const summary = el("div", "actor-task-summary");
  if (taskSteps.length) {
    summary.append(el(
      "span",
      "actor-task-progress mono",
      `${completedSteps.length}/${taskSteps.length}`,
    ));
  }
  summary.append(el("span", `actor-task-status ${status}`, actorStatusCopy(status)));
  const toggle = el("button", "actor-task-toggle");
  toggle.type = "button";
  const syncCollapsedState = () => {
    const collapsed = root.dataset.collapsed === "true";
    root.classList.toggle("collapsed", collapsed);
    toggle.setAttribute("aria-expanded", String(!collapsed));
    toggle.title = collapsed ? "展开计划详情" : "收起计划详情";
    toggle.setAttribute("aria-label", toggle.title);
    toggle.replaceChildren(iconNode(collapsed ? "chevron-right" : "chevron-down"));
    refreshIcons(toggle);
  };
  toggle.addEventListener("click", () => {
    root.dataset.collapsed = root.dataset.collapsed === "true" ? "false" : "true";
    syncCollapsedState();
  });
  summary.append(toggle);
  header.append(title, summary);
  root.append(header);
  syncCollapsedState();

  if (task) {
    const meta = el("div", "actor-plan-meta");
    const revision = Number.isSafeInteger(task.planRevision) ? task.planRevision : 1;
    meta.append(el("span", "actor-plan-revision", `Revision ${revision}`));
    if (task.planId) {
      const planId = el("span", "actor-plan-id mono", task.planId);
      planId.title = task.planId;
      meta.append(planId);
    }
    root.append(meta);
  }

  pruneNeedsYouState(entry, projection);
  const pendingInput = renderPendingInput(entry, projection);
  const pendingApproval = renderPendingApproval(entry, projection);
  if (pendingInput) root.append(pendingInput);
  if (pendingApproval) root.append(pendingApproval);

  if (task?.safeMessage) root.append(el("p", "actor-task-message", task.safeMessage));
  if (task) {
    const recovery = renderActorRecovery(projection);
    if (recovery) root.append(recovery);
    const steps = el("div", "actor-steps");
    for (const step of projection.steps.values()) {
      const stepStatus = String(step.status || "unknown").toLowerCase();
      const row = el("article", `actor-step ${stepStatus}`);
      row.dataset.stepId = step.stepId || "";
      row.dataset.stepKind = step.kind || "";
      const copy = el("div", "actor-step-copy");
      copy.append(
        el("strong", "", actorStepDisplayName(step)),
        el("small", "actor-step-source", `${actorStatusCopy(stepStatus)} · ${actorStepSourceLabel(step)}`),
      );
      const annotations = el("div", "actor-step-annotations");
      annotations.append(el("span", `actor-added-by ${step.addedBy || "initial"}`,
        actorAddedByLabel(step.addedBy)));
      const estimateSeconds = Number(step.estimate?.seconds);
      if (step.estimate?.kind === "duration" && Number.isSafeInteger(estimateSeconds) && estimateSeconds > 0) {
        annotations.append(el("span", "actor-estimate", `预计 ${estimateSeconds} 秒`));
      }
      if (Array.isArray(step.dependsOn) && step.dependsOn.length) {
        const dependencies = el("span", "actor-dependencies mono", `依赖 ${step.dependsOn.join(", ")}`);
        dependencies.title = step.dependsOn.join(", ");
        annotations.append(dependencies);
      }
      copy.append(annotations);
      if (Array.isArray(step.substeps) && step.substeps.length) {
        const substeps = el("div", "actor-substeps");
        for (const substep of step.substeps) {
          substeps.append(el(
            "div",
            `actor-substep ${String(substep.status || "running").toLowerCase()}`,
            `${actorStatusCopy(substep.status)} · ${substep.title}`,
          ));
        }
        copy.append(substeps);
      }
      const facts = el("div", "actor-step-facts");
      const effect = actorEffectCopy[step.externalEffect];
      if (effect) {
        const evidence = el(
          "span",
          `actor-effect ${String(step.externalEffect).replaceAll("_", "-")}`,
          effect.label,
        );
        evidence.dataset.effect = step.externalEffect;
        facts.append(evidence, el("span", "actor-effect-explanation", effect.explanation));
      }
      const operation = step.operation || step.externalOperation;
      const generation = actorOperationGeneration(step);
      if (operation?.phase || generation != null) {
        facts.append(el(
          "span",
          "actor-operation",
          `operation: ${operation.phase || "unknown"}` +
            (generation != null ? ` · generation ${generation}` : ""),
        ));
      }
      const controls = el("div", "actor-step-controls");
      const capability = actorStepManagementCapability(step);
      if (capability) {
        facts.append(el("span", "actor-readiness-fact", [
          readinessConnectionCopy[capability.connectionState],
          readinessGrantCopy[capability.grantState],
        ].join(" · ")));
        const manage = el("button", "actor-manage", "前往 NyxID");
        manage.type = "button";
        manage.addEventListener("click", () => openReadinessManagement(capability.managementUrl));
        controls.append(manage);
      }
      if (actorCan(projection, "retry", step.stepId) && step.externalEffect !== "confirmed") {
        const retry = el(
          "button",
          "actor-retry",
          step.externalEffect === "may_have_changed" ? "Actor 授权重试" : "重试",
        );
        retry.type = "button";
        retry.addEventListener("click", () => void submitActorControl("retry", step));
        controls.append(retry);
      }
      if (actorCan(projection, "skip", step.stepId)) {
        const skip = el("button", "actor-skip", "跳过");
        skip.type = "button";
        skip.addEventListener("click", () => void submitActorControl("skip", step));
        controls.append(skip);
      }
      row.append(copy, facts, controls);
      steps.append(row);
    }
    if (steps.childElementCount) root.append(steps);
  }

  if (projection.actions.size) {
    const actions = el("div", "actor-actions");
    for (const action of projection.actions.values()) {
      const item = el("div", `actor-action${action.conflicted ? " conflicted" : ""}`);
      item.dataset.actionRequestId = action.actionRequestId || "";
      item.append(
        el("strong", "", action.actionRequestId || "Unknown action"),
        el("span", "mono", action.action || "unknown action"),
      );
      if (!action.executable) {
        item.append(el(
          "small",
          "actor-action-unavailable",
          action.conflicted
            ? "Action identity conflict；已禁用。"
            : "缺少 actor 发出的 typed params，当前不可执行。",
        ));
      }
      actions.append(item);
    }
    root.append(actions);
  }

  if (projection.conflicts.length) {
    root.append(el(
      "div",
      "actor-control-error",
      `Actor projection conflict: ${projection.conflicts.at(-1).code}`,
    ));
  }
  if (notice) {
    root.append(el("div", "actor-state-notice", notice));
  }
  const controlReceipt = actorControlReceiptFor(entry, actorId);
  if (controlReceipt) {
    const receipt = controlReceipt;
    root.append(el(
      "div",
      `actor-control-receipt ${receipt.status === "error" ? "actor-control-error" : ""}`,
      receipt.message,
    ));
  }
  mountActorTask(entry.thread, root);
  if (isConversationActor && entry === state.activeConversation) {
    renderComposerInputRequest(entry, projection);
    renderInspector();
  }
}

// The plan card narrates the turn the user just opened, so it always sits
// directly after the newest user message (above the assistant reply). Mounting
// by arrival order instead would leave its position to an event race and
// strand the card inside older turns as the conversation grows.
function mountActorTask(thread, root) {
  const anchor = [...thread.querySelectorAll(":scope > .message.user")].at(-1);
  if (!anchor) {
    if (!root.isConnected) thread.append(root);
    return;
  }
  if (anchor.nextElementSibling !== root) anchor.after(root);
}

async function submitActorControl(kind, step = null, instruction = null) {
  if (isReviewingHistoricalTrace()) {
    showToast("历史轨迹仅供查看；请返回当前轨迹后再执行控制。");
    return;
  }
  const entry = state.activeConversation;
  const projection = entryActorProjection(entry);
  if (!entry?.actorId || !projection) return;
  if (kind === "stop" && !actorCan(projection, "stop")) return;
  if ((kind === "retry" || kind === "skip") && !actorCan(projection, kind, step?.stepId)) return;
  if (kind === "steer" && !String(instruction || "").trim()) return;

  const turnId = actorControlTurnId(projection);
  const reliableVersion = reliableConversationStateVersion(entry);
  if (!turnId || reliableVersion <= 0) {
    entry.actorControlReceipt = {
      status: "error",
      message: "Actor control 缺少可靠的 turn/state identity。",
    };
    renderActorProjection(entry);
    return;
  }

  const type = {
    stop: "task.stop",
    steer: "task.steer",
    retry: "step.retry",
    skip: "step.skip",
  }[kind];
  let body;
  if (kind === "stop") {
    body = {
      surface: "nyxid-chat",
      type,
      conversationId: entry.actorId,
      turnId,
      stopRequestId: createId("stop"),
      clientRequestId: createId("client-stop"),
      expectedStateVersion: reliableVersion,
    };
  } else if (kind === "steer") {
    body = {
      surface: "nyxid-chat",
      type,
      conversationId: entry.actorId,
      turnId,
      steeringId: createId("steering"),
      clientRequestId: createId("client-steering"),
      instruction: String(instruction).trim(),
      expectedStateVersion: reliableVersion,
    };
  } else {
    const generation = actorOperationGeneration(step);
    if (!step?.stepId || !projection.task?.taskId || generation == null) {
      entry.actorControlReceipt = {
        status: "error",
        message: "Step control 缺少可靠的 task/step/generation identity。",
      };
      renderActorProjection(entry);
      return;
    }
    body = {
      surface: "nyxid-chat",
      type,
      conversationId: entry.actorId,
      turnId,
      taskId: projection.task.taskId,
      stepId: step.stepId,
      [`${kind}RequestId`]: createId(kind),
      clientRequestId: createId(`client-${kind}`),
      expectedOperationGeneration: generation,
      expectedStateVersion: reliableVersion,
    };
  }

  entry.actorControlReceipt = { status: "pending", message: `${kind} 正在提交…` };
  renderActorProjection(entry);
  try {
    const response = await fetch("/api/demo/chat", {
      method: "POST",
      headers: demoHeaders(),
      body: JSON.stringify(body),
    });
    if (!response.ok) throw await responseError(response);
    const receipt = await response.json().catch(() => ({}));
    entry.actorControlReceipt = {
      status: "accepted",
      message: `${kind} 已受理（${receipt.status || "accepted"}），等待 Actor 状态确认。`,
    };
    if (kind === "steer") {
      dom.promptInput.value = "";
      entry.draft = "";
      autoResizeComposer();
    }
    renderActorProjection(entry);
    await refreshActorState(entry);
    if (entry.actorStateRefreshTimer) window.clearTimeout(entry.actorStateRefreshTimer);
    entry.actorStateRefreshTimer = window.setTimeout(() => {
      entry.actorStateRefreshTimer = null;
      void refreshActorState(entry);
    }, 350);
  } catch (error) {
    entry.actorControlReceipt = {
      status: "error",
      message: `${kind} 提交失败：${String(error?.message || "unknown error").slice(0, 300)}`,
    };
    renderActorProjection(entry);
    await refreshActorState(entry);
  }
}

function renderActiveConversationState() {
  const entry = state.activeConversation;
  if (!entry) return;
  const running = Boolean(entry.controller);
  const actorNeedsYou = entry.actorProjection?.pendingInput
    ? "Input"
    : entry.actorProjection?.pendingApproval
      ? "Approval"
      : null;
  renderComposerInputRequest(entry, entry.actorProjection);
  setConversationTitle(entry.title || entry.meta?.title || "新会话");
  setRunningUi(running);
  const actorStatus = actorTerminalRunStatus(entry.actorProjection);
  const status = actorNeedsYou || entry.run.pendingApproval ? "running" : actorStatus || entry.run.status;
  const labels = {
    idle: "Ready",
    running: actorNeedsYou || entry.run.pendingApproval ? actorNeedsYou || "Approval" : "Running",
    complete: "Complete",
    blocked: "Blocked",
    error: "Error",
    stopped: "Stopped",
    closed: "Closed",
  };
  setRunStatus(status, labels[status] || "Idle");
  dom.sidebarSessionMeta.textContent = actorNeedsYou
    ? actorNeedsYou === "Input" ? "Waiting for input" : "Waiting for approval"
    : entry.run.pendingApproval
      ? "Waiting for approval"
    : actorStatus
      ? labels[actorStatus]
      : running
      ? "Running"
      : entry.meta
        ? `${entry.meta.messageCount} 条消息 · ${formatHistoryTime(entry.meta.updatedAt)}`
        : entry.run.startedAt
          ? labels[entry.run.status] || "Ready"
          : "尚未运行";
  if (running) setRouteState(dom.routeUpstreamState, "streaming", "active");
  else applyHealthRouteState();
  renderInspector();
  renderEventLog();
  renderRequestTraces(entry);
  renderHistoryList();
  renderAttachment();
  refreshIcons(entry.thread);
}

function renderStoredMessage(message) {
  const role = message.role === "user" ? "user" : "assistant";
  const { body } = createMessageShell(role);
  if (message.content) {
    if (role === "assistant") {
      const content = el("div", "message-content");
      renderAssistantSegments(content, message.content);
      body.append(content);
    } else {
      const content = el("div", "message-text");
      content.textContent = message.content;
      body.append(content);
    }
  }
  if (message.error) {
    const callout = el("div", "error-callout");
    callout.append(iconNode("circle-alert"), el("span", "", message.error));
    body.append(callout);
  }
}

async function deleteConversation(conversation, button) {
  const confirmed = globalThis.confirm(
    `确定删除生产会话“${conversation.title}”？\n\n这会删除 Aevatar 中的 NyxID Chat actor 和历史消息，无法撤销。`,
  );
  if (!confirmed) return;
  const entry = findConversationState(conversation.id);
  if (entry) abortConversationRun(entry);
  button.disabled = true;
  try {
    const response = await fetch(historyUrl(conversation.id), {
      method: "DELETE",
      headers: demoHeaders(),
    });
    if (!response.ok) throw await responseError(response);
    if (entry === state.activeConversation) {
      newChat({ refreshHistory: false });
      removeConversationState(entry);
    } else {
      removeConversationState(entry);
    }
    await loadConversations();
    showToast("生产会话已删除。");
  } catch (error) {
    button.disabled = false;
    showToast(error.message || "删除生产会话失败");
  }
}

function scheduleHistoryRefresh() {
  if (state.config.surface !== "nyxid-chat") return;
  void loadConversations({ silent: true });
  clearTimeout(state.historyRefreshTimer);
  state.historyRefreshTimer = setTimeout(() => {
    void loadConversations({ silent: true });
  }, 1500);
}

function focusCurrentConversation() {
  closeMobilePanels();
  switchWorkspaceView("conversation");
  dom.threadViewport.scrollTo({ top: dom.threadViewport.scrollHeight, behavior: "smooth" });
  dom.promptInput.focus();
}

function formatHistoryTime(value) {
  const date = new Date(value || 0);
  if (Number.isNaN(date.getTime())) return "时间未知";
  const now = new Date();
  const sameDay = date.toDateString() === now.toDateString();
  return date.toLocaleString("zh-CN", sameDay
    ? { hour: "2-digit", minute: "2-digit", hour12: false }
    : { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hour12: false });
}

function activePendingInputContext() {
  const entry = state.activeConversation;
  const projection = entryActorProjection(entry);
  const pending = projection?.pendingInput;
  if (!entry || !pending?.requestId) return null;
  const key = needsYouKey("input", pending.requestId, projection.actorId || entry.actorId);
  const draft = entry.needsYouDrafts.get(key) || { selectedOptionIds: new Set(), freeText: "" };
  entry.needsYouDrafts.set(key, draft);
  return { entry, projection, pending, key, draft };
}

async function submitComposer() {
  const pending = activePendingInputContext();
  if (pending) {
    await submitPendingInputFromComposer(pending);
    return;
  }
  const projection = entryActorProjection(state.activeConversation);
  const actorActive = state.config.surface === "nyxid-chat" && Boolean(
    projection?.activeTurn || projection?.task?.status === "active",
  );
  const instruction = dom.promptInput.value.trim();
  if (actorActive && instruction) {
    await submitActorControl("steer", null, instruction);
    return;
  }
  await sendPrompt();
}

async function submitPendingInputFromComposer(context = activePendingInputContext()) {
  if (!context) return;
  const { entry, pending, key, draft } = context;
  const freeText = dom.promptInput.value.trim();
  const selectedOptionIds = [...draft.selectedOptionIds];
  if (!freeText && !selectedOptionIds.length) return;
  const answer = freeText ? { freeText } : { selectedOptionIds };
  const accepted = await submitNeedsYouDecision(entry, "input", pending.requestId, {
    type: "input.resolve",
    answer,
  });
  if (!accepted) return;

  const selectedLabels = (pending.options || [])
    .filter((option) => selectedOptionIds.includes(option.optionId))
    .map((option) => option.label);
  withConversationState(entry, () => {
    addUserMessage(freeText || selectedLabels.join("、"));
    dom.promptInput.value = "";
    entry.draft = "";
    draft.freeText = "";
    draft.selectedOptionIds.clear();
    autoResizeComposer();
    persistConversationState(entry);
  });
  entry.needsYouDrafts.set(key, draft);
  renderComposerInputRequest(entry, entry.actorProjection);
}

async function sendPrompt(overridePrompt, options = {}) {
  if (!state.auth.authenticated) {
    beginLogin();
    return;
  }
  if (state.activeController) {
    showToast("当前运行尚未结束。");
    return;
  }
  const prompt = String(overridePrompt ?? dom.promptInput.value).trim();
  const hasOverrideAttachment = Object.prototype.hasOwnProperty.call(options, "attachment");
  const attachment = hasOverrideAttachment ? options.attachment : state.attachment;
  if (!prompt && !attachment) return;
  if (firstTurnReadinessBlocked()) {
    state.pendingFirstTurn ||= {
      prompt,
      attachment: attachment == null ? null : structuredClone(attachment),
      clientRequestId: createId("client-text"),
    };
    setComposerStatus("完成必需的运行准备后，将继续这条请求。");
    dom.readinessPanel.scrollIntoView?.({ block: "nearest" });
    return;
  }

  const conversation = state.activeConversation;
  state.run = createRunState();
  state.run.status = "running";
  state.run.surface = state.config.surface;
  state.run.config = configPayload(state.config);
  state.run.startedAt = Date.now();
  state.run.request = { prompt, attachment };
  state.run.clientRequestId = options.clientRequestId || createId("client-text");
  // Make the owning conversation point at the new request before the first render. Otherwise the
  // freshly selected trace is briefly mistaken for a historical trace until persistConversationState runs.
  conversation.run = state.run;
  createRequestTrace(conversation, state.run);
  renderRequestTraces(conversation);
  const controller = new AbortController();
  const run = state.run;
  const runSurface = state.config.surface;
  const requestConfig = state.run.config || configPayload(state.config);
  const requestWorkflowSessionId = state.workflowSessionId;
  const requestActorId = state.actorId;
  state.activeController = controller;
  conversation.controller = controller;
  conversation.controllers.add(controller);
  dom.emptyState.classList.add("hidden");
  addUserMessage(prompt, attachment);
  if (!state.actorId) setConversationTitle(prompt || attachment?.name || "附件会话");
  if (!options.preserveComposer) {
    dom.promptInput.value = "";
    conversation.draft = "";
    autoResizeComposer();
    if (!hasOverrideAttachment) clearAttachment();
  }
  setRunningUi(true);
  setRunStatus("running", "Running");
  setRouteState(dom.routeUpstreamState, "streaming", "active");
  applyHealthRouteState();
  startRunProgress();
  renderInspector();
  persistConversationState(conversation);

  try {
    const nyxidBody = {
      surface: "nyxid-chat",
      type: "text",
      ...(requestActorId ? { conversationId: requestActorId } : {}),
      prompt,
      clientRequestId: run.clientRequestId,
      attachment,
    };
    const workflowBody = {
      ...requestConfig,
      prompt,
      sessionId: requestWorkflowSessionId,
      attachment,
    };
    const response = await fetch("/api/demo/chat", {
      method: "POST",
      headers: demoHeaders(),
      signal: controller.signal,
      body: JSON.stringify(runSurface === "nyxid-chat" ? nyxidBody : workflowBody),
    });
    if (!response.ok) throw await responseError(response);
    await consumeSse(response, async (raw) => {
      withConversationState(conversation, () => handleFrame(raw));
    });
    withConversationState(conversation, () => {
      if (state.run.status !== "running") return;
      state.run.status = "closed";
      state.run.completedAt = Date.now();
      removeRunProgress();
      finalizeRunningExecution("done", "Stream closed");
      setRunStatus("idle", "Closed");
      dom.sidebarSessionMeta.textContent = "Stream closed";
      addInfo("SSE 已关闭，但没有收到明确的终止事件。");
    });
  } catch (error) {
    const authExpired = error.code === "AUTH_REQUIRED";
    const authorizationFailure = findServiceAuthorizationFailure({
      code: error.code,
      message: error.message,
      serviceId: error.serviceId,
      serviceSlug: error.serviceSlug,
      resource: error.resource,
    });
    withConversationState(conversation, () => {
      if (error.name === "AbortError") {
        state.run.status = "stopped";
        state.run.completedAt = Date.now();
        removeRunProgress();
        finalizeRunningExecution("error", "Stopped receiving");
        setRunStatus("idle", "Stopped");
        dom.sidebarSessionMeta.textContent = "Stopped receiving";
        addInfo("当前页面已停止接收。已提交的生产操作不会被自动撤销，上游 Agent 可能仍在执行。");
        return;
      }
      state.run.status = "error";
      state.run.completedAt = Date.now();
      removeRunProgress();
      finalizeRunningExecution("error", "Run failed");
      setRunStatus("error", "Error");
      dom.sidebarSessionMeta.textContent = "Failed";
      if (authorizationFailure) {
        addServiceAuthorizationPrompt(
          authorizationFailure.message || error.message || "此操作需要一个尚未配置的 service。",
          {
            serviceId: error.serviceId,
            serviceSlug: error.serviceSlug || authorizationFailure.serviceSlug,
            resource: error.resource,
            resendDraft: run.request,
          },
        );
      } else if (!authExpired) {
        addError(error.message || "请求失败");
      }
    });
    if (authExpired) {
      await refreshAuthSession();
      withConversationState(conversation, () => {
        addServiceAuthorizationPrompt("NyxID 登录已失效，请重新登录。", {
          login: true,
          resendDraft: run.request,
        });
      });
    }
  } finally {
    withConversationState(conversation, () => {
      clearRunProgressTimers(run);
      releaseConversationController(conversation, controller);
      setRunningUi(Boolean(state.activeController));
      setRouteState(
        dom.routeUpstreamState,
        state.run.status === "complete" ? "complete" : state.run.status,
        state.run.status === "complete" ? "ok" : state.run.status === "error" ? "error" : "",
      );
      applyHealthRouteState({ includeAevatar: false });
      renderInspector();
    });
    renderHistoryList();
    queueRequestTraceRender(conversation);
    if (runSurface === "nyxid-chat") scheduleHistoryRefresh();
  }
}

async function responseError(response) {
  try {
    const payload = await response.json();
    const error = new Error(payload.message || payload.detail || payload.error || `HTTP ${response.status}`);
    error.code = payload.code || "";
    error.status = response.status;
    error.reason = typeof payload.reason === "string" ? payload.reason : "";
    error.serviceId = payload.serviceId || payload.service_id || "";
    error.serviceSlug = payload.serviceSlug || payload.service_slug || "";
    error.resource = payload.resource || payload.resourceUri || payload.resource_uri || "";
    return error;
  } catch {
    const error = new Error(`HTTP ${response.status}`);
    error.status = response.status;
    return error;
  }
}

function handleFrame(raw, { streamActorId = null, preserveConversationActor = false } = {}) {
  const event = normalizeFrame(raw);
  applyRequestTraceEvent(conversationContext || state.activeConversation, state.run, event);
  recordEvent(event, raw);
  switch (event.type) {
    case "run_context":
      state.run.context = { ...state.run.context, ...pickContext(event) };
      if (typeof attachRequestTraceServerFacts === "function") {
        attachRequestTraceServerFacts(conversationContext || state.activeConversation, state.run, event);
      }
      updateRunProgress("运行上下文已建立，Agent 正在分析请求…");
      break;
    case "run_started":
      state.run.context.actorId = event.actorId || event.threadId || state.run.context.actorId;
      state.run.context.runId = event.runId || state.run.context.runId;
      state.run.context.turnId = event.turnId || state.run.context.turnId;
      if (typeof attachRequestTraceServerFacts === "function") {
        attachRequestTraceServerFacts(conversationContext || state.activeConversation, state.run, event);
      }
      if (state.run.surface === "nyxid-chat") {
        const owner = conversationContext || state.activeConversation;
        const adopted = adoptRunStartedConversationActor(
          owner,
          state.run.context.actorId,
          { preserveConversationActor },
        );
        if (adopted) {
          entryActorProjection(owner);
          renderHistoryList();
          scheduleHistoryRefresh();
        }
      }
      updateRunProgress("Agent 已启动，正在分析请求…");
      setRunStatus("running", "Running");
      break;
    case "task_snapshot":
    case "task_step_changed":
    case "control_changed":
    case "continuation_changed":
    case "step_control_changed":
    case "input_requested":
    case "input_changed":
    case "approval_requested":
    case "approval_changed":
    case "action_request": {
      const entry = conversationContext || state.activeConversation;
      if (!entry) break;
      const routed = reduceActorEventForEntry(entry, event, { streamActorId });
      if (!routed) break;
      const { actorId, projection } = routed;
      if (event.type === "action_request" && event.actionRequest) {
        const action = projection.actions.get(event.actionRequest.actionRequestId);
        if (action?.conflicted) {
          invalidateActionRequestCache(entry, actorId, event.actionRequest.actionRequestId);
        } else if (action?.request) {
          cacheActionRequest(entry, action.request);
        }
        void refreshActionActorState(entry, actorId);
      }
      if (event.type === "approval_requested") {
        state.run.approvalCard?.card?.remove();
        state.run.approvalCard = null;
        state.run.pendingApproval = null;
      }
      renderActorProjection(entry, actorId);
      renderActionCards(entry);
      if (entry === state.activeConversation) renderActiveConversationState();
      if (["input_requested", "input_changed", "approval_requested", "approval_changed"].includes(event.type)) {
        scheduleActorStateRefresh(entry, actorId, 300);
      }
      break;
    }
    case "step_started":
      startStep(event.stepName || "workflow-step", "step");
      break;
    case "step_finished":
      finishStep(event.stepName || "workflow-step", "step");
      break;
    case "step_request":
      state.run.context.runId = event.runId || state.run.context.runId;
      startStep(event.stepId || event.stepType || "step-request", "step");
      break;
    case "step_completed":
      state.run.context.runId = event.runId || state.run.context.runId;
      finishStep(event.stepId || "step-completed", "step", event.success === false ? "error" : "done");
      break;
    case "tool_start":
      removeRunProgress();
      addTool(event);
      break;
    case "tool_end":
      removeRunProgress();
      finishTool(event);
      break;
    case "role_chat_completed":
      removeRunProgress();
      applyRoleChatCompletion(event);
      break;
    case "text_start":
      removeRunProgress();
      startText();
      break;
    case "text_delta":
      removeRunProgress();
      appendText(event.delta || "");
      break;
    case "text_end":
      finishText();
      break;
    case "approval":
      removeRunProgress();
      renderApproval(event);
      break;
    case "authorization_required": {
      removeRunProgress();
      state.run.authorizationPrompted = true;
      const service = state.services.find((item) =>
        item.id === event.serviceId || item.slug === event.serviceSlug ||
        item.resourceUri === event.resource);
      addServiceAuthorizationPrompt(
        event.message || `连接 ${event.serviceLabel || event.serviceSlug || "此 service"} 后重试该请求。`,
        {
          serviceId: service?.id || event.serviceId || "",
          serviceSlug: service?.slug || event.serviceSlug || "",
          resource: service?.resourceUri || event.resource || event.resourceUri || "",
          resendDraft: state.run.request,
        },
      );
      break;
    }
    case "usage":
      state.run.usage = mergeUsage(state.run.usage, event);
      break;
    case "reasoning":
      updateRunProgress("Agent 正在规划要执行的生产操作…");
      dom.sidebarSessionMeta.textContent = "Agent planning";
      break;
    case "media":
      removeRunProgress();
      renderMedia(event);
      break;
    case "run_finished":
      appendFallbackText(event.result?.output);
      completeRun(event.status);
      break;
    case "run_stopped":
      state.run.status = "stopped";
      state.run.completedAt = Date.now();
      removeRunProgress();
      finalizeRunningExecution("error", "Run stopped");
      setRunStatus("idle", "Stopped");
      dom.sidebarSessionMeta.textContent = "Stopped";
      break;
    case "run_error":
    case "protocol_error":
      state.run.status = "error";
      state.run.completedAt = Date.now();
      removeRunProgress();
      finalizeRunningExecution("error", "Run failed");
      setRunStatus("error", "Error");
      dom.sidebarSessionMeta.textContent = "Failed";
      addError(event.message || "Aevatar stream returned an error.");
      break;
    case "keepalive":
      updateRunProgress("Agent 仍在处理生产请求，请稍候…");
      dom.sidebarSessionMeta.textContent = "Running";
      break;
    default:
      break;
  }
  const owner = conversationContext || state.activeConversation;
  if (typeof queueRequestTraceRender === "function") queueRequestTraceRender(owner);
  else renderInspector();
  return event;
}

function pickContext(event) {
  return {
    actorId: event.actorId,
    runId: event.runId,
    commandId: event.commandId,
    workflowName: event.workflowName,
    workflowSessionId: event.sessionId,
    turnId: event.turnId,
  };
}

function recordEvent(event, raw) {
  state.run.eventSequence += 1;
  const safeRaw = event.type === "reasoning"
    ? { custom: { name: "aevatar.llm.reasoning", payload: "[not displayed]" } }
    : redact(raw);
  state.run.events.push({
    id: state.run.eventSequence,
    at: new Date(),
    type: event.type,
    raw: safeRaw,
  });
  if (state.run.events.length > 120) state.run.events.shift();
  if (typeof queueRequestTraceRender === "function") {
    queueRequestTraceRender(conversationContext || state.activeConversation);
  } else {
    renderEventLog();
  }
}

function addUserMessage(prompt, attachment) {
  const { body } = createMessageShell("user");
  if (prompt) body.append(el("div", "message-text", prompt));
  if (attachment) {
    const file = el("div", "message-file");
    const icon = document.createElement("i");
    icon.dataset.lucide = "file";
    file.append(icon, el("span", "", attachment.name));
    body.append(file);
    refreshIcons(file);
  }
  scrollThread();
}

function createMessageShell(role) {
  const message = el("article", `message ${role}`);
  const avatar = el("div", "message-avatar");
  if (role === "assistant") {
    const icon = document.createElement("i");
    icon.dataset.lucide = "sparkles";
    avatar.append(icon);
  } else {
    avatar.textContent = "ME";
  }
  const body = el("div", "message-body");
  message.append(avatar, body);
  dom.thread.append(message);
  refreshIcons(message);
  return { message, body };
}

function ensureAssistantBody() {
  if (state.run.assistantBody?.isConnected) return state.run.assistantBody;
  state.run.assistantBody = createMessageShell("assistant").body;
  scrollThread();
  return state.run.assistantBody;
}

function ensureActivityCard() {
  if (state.run.activityCard?.isConnected) return state.run.activityCard;
  const card = el("div", "activity-card");
  const header = el("button", "activity-header");
  header.type = "button";
  header.setAttribute("aria-expanded", "true");
  header.setAttribute("aria-label", "收起工具运行详情");
  const disclosure = iconNode("chevron-down");
  disclosure.classList.add("activity-disclosure");
  const icon = document.createElement("i");
  icon.dataset.lucide = "workflow";
  const label = el("span", "", "AI 执行");
  const status = el("span", "", "正在准备");
  header.append(disclosure, icon, label, status);
  header.addEventListener("click", () => {
    const collapsed = card.classList.toggle("collapsed");
    header.setAttribute("aria-expanded", String(!collapsed));
    header.setAttribute("aria-label", collapsed ? "展开工具运行详情" : "收起工具运行详情");
    const nextDisclosure = iconNode(collapsed ? "chevron-right" : "chevron-down");
    nextDisclosure.classList.add("activity-disclosure");
    header.querySelector(".activity-disclosure")?.replaceWith(nextDisclosure);
    refreshIcons(header);
  });
  card.append(header);
  ensureAssistantBody().append(card);
  state.run.activityCard = card;
  state.run.activityStatus = status;
  refreshIcons(card);
  scrollThread();
  return card;
}

function startRunProgress() {
  const conversation = conversationContext || state.activeConversation;
  const card = ensureActivityCard();
  const row = el("div", "tool-row progress-row");
  const stateIcon = el("span", "tool-state-icon");
  stateIcon.append(iconNode("loader-circle"));
  const copy = el("div", "tool-copy");
  const label = el("small", "", state.actorId
    ? "正在连接现有生产会话…"
    : "正在连接 Aevatar 并创建生产会话…");
  copy.append(el("strong", "", surfaceLabels[state.config.surface]), label);
  row.append(stateIcon, copy, el("span", "tool-duration", "…"));
  card.append(row);
  state.run.progressRow = row;
  state.run.progressLabel = label;
  state.run.progressTimers = [
    setTimeout(() => {
      withConversationState(conversation, () => {
        updateRunProgress("Agent 正在分析请求，首次运行可能需要一些时间…");
      });
    }, 15_000),
    setTimeout(() => {
      withConversationState(conversation, () => {
        updateRunProgress("Agent 正在调用生产服务，仍在处理…");
      });
    }, 35_000),
  ];
  dom.sidebarSessionMeta.textContent = "Connecting to production";
  refreshIcons(row);
  scrollThread();
}

function updateRunProgress(message) {
  if (state.run.progressLabel?.isConnected) {
    state.run.progressLabel.textContent = message;
    dom.sidebarSessionMeta.textContent = "Running";
  }
}

function clearRunProgressTimers(run = state.run) {
  for (const timer of run.progressTimers) clearTimeout(timer);
  run.progressTimers = [];
}

function removeRunProgress() {
  clearRunProgressTimers();
  state.run.progressRow?.remove();
  state.run.progressRow = null;
  state.run.progressLabel = null;
  const card = state.run.activityCard;
  if (card?.isConnected && !card.querySelector(".tool-row")) {
    card.remove();
    state.run.activityCard = null;
    state.run.activityStatus = null;
  }
  if (state.run.assistantBody?.isConnected && !state.run.assistantBody.childElementCount) {
    state.run.assistantBody.closest(".message")?.remove();
    state.run.assistantBody = null;
  }
}

function addTool(event) {
  const id = event.toolCallId || createId("tool");
  if (state.run.tools.has(id)) return;
  const presentation = describeToolOperation(event);
  const card = ensureActivityCard();
  const row = el("div", "tool-row");
  row.dataset.toolCallId = id;
  const stateIcon = el("span", "tool-state-icon");
  const icon = document.createElement("i");
  icon.dataset.lucide = "loader-circle";
  stateIcon.append(icon);
  const copy = el("div", "tool-copy");
  copy.append(
    el("strong", "", presentation.title),
    el("small", "", toolActivityRunningCopy(presentation)),
  );
  const duration = el("span", "tool-duration", "…");
  row.append(stateIcon, copy, duration);
  card.append(row);
  state.run.tools.set(id, {
    id,
    name: presentation.title,
    invocationName: presentation.invocationName,
    presentation: presentation.presentation,
    status: "running",
    startedAt: Date.now(),
    row,
    copy: copy.querySelector("small"),
    duration,
  });
  startStep(presentation.title, "tool", id);
  updateActivityProgress();
  if (/ornn_search_skills|use_skill/i.test(presentation.invocationName)) {
    dom.routeOrnnState.textContent = "active";
    dom.routeOrnnState.className = "route-state active";
  }
  refreshIcons(row);
  scrollThread();
}

function updateActivityProgress() {
  if (!state.run.activityStatus?.isConnected) return;
  const tools = Array.from(state.run.tools.values());
  if (!tools.length) return;
  const done = tools.filter((tool) => tool.status !== "running").length;
  const active = tools.filter((tool) => tool.status === "running").at(-1);
  state.run.activityStatus.textContent = active
    ? `正在执行 · ${active.name}`
    : `已完成 ${done} 项`;
  state.run.activityStatus.title = active?.name || `已完成 ${done} 项操作`;
}

function finishTool(event) {
  const id = event.toolCallId || "";
  const tool = state.run.tools.get(id);
  if (!tool) {
    addTool({ ...event, toolName: event.toolName || "tool" });
  }
  const resolved = state.run.tools.get(id) || Array.from(state.run.tools.values()).at(-1);
  if (!resolved) return;
  const status = String(event.status || "").toUpperCase();
  const succeeded = event.success !== false && !/(ERROR|DENIED)/.test(status);
  resolved.status = succeeded ? "done" : "error";
  resolved.completedAt = Date.now();
  resolved.row.classList.remove("done", "error");
  resolved.row.classList.add(resolved.status);
  resolved.row.querySelector(".tool-state-icon").replaceChildren(iconNode(succeeded ? "check" : "x"));
  const result = summarizeToolResult(event.result || event.error);
  const hasResult = result && !/^completed$/i.test(result);
  resolved.copy.textContent = `${succeeded ? "已完成" : "执行失败"}${hasResult ? ` · ${result}` : ""}`;
  const authorizationFailure = findServiceAuthorizationFailure(event.result || event.error);
  if (authorizationFailure && !state.run.authorizationPrompted) {
    state.run.authorizationPrompted = true;
    const service = state.services.find((item) => item.slug === authorizationFailure.serviceSlug);
    addServiceAuthorizationPrompt(authorizationFailure.message, {
      serviceId: service?.id || "",
      serviceSlug: service?.slug || authorizationFailure.serviceSlug,
      resource: service?.resourceUri || "",
      resendDraft: state.run.request,
    });
  }
  resolved.duration.textContent = formatDuration(resolved.completedAt - resolved.startedAt);
  updateActivityProgress();
  finishStep(resolved.name, "tool", resolved.status, resolved.id);
  if (/ornn_search_skills|use_skill/i.test(resolved.invocationName)) {
    applyHealthRouteState();
  }
  refreshIcons(resolved.row);
}

function findServiceAuthorizationFailure(value) {
  if (!value) return null;
  const text = safeJson(value, 0).toLowerCase();
  const matched = [
    "authorization_required",
    "service_not_authorized",
    "service_access_required",
    "service authorization required",
    "not authorized for this service",
    "not authorized for service",
    "does not have access to this service",
    "does not have access to service",
    "service access is not granted",
    "scoped api keys must use configured services",
    "invalid_target",
  ].some((marker) => text.includes(marker));
  if (!matched) return null;
  const explicitSlug = value && typeof value === "object"
    ? value.serviceSlug || value.service_slug || ""
    : "";
  const slug = String(explicitSlug).trim().toLowerCase() ||
    text.match(/(?:service|slug)[\s"':=]+([a-z0-9][a-z0-9-]{1,80})/)?.[1] || "";
  return {
    serviceSlug: slug,
    message: slug
      ? `Aevatar 需要可用的 ${slug} 连接。配置后请重试这条消息。`
      : "Aevatar 需要一个尚未配置的 NyxID service。配置后请重试这条消息。",
  };
}

function finalizeRunningExecution(status, detail) {
  finalizeAssistantSegments();
  const completedAt = Date.now();
  for (const tool of state.run.tools.values()) {
    if (tool.status !== "running") continue;
    tool.status = status;
    tool.completedAt = completedAt;
    tool.row.classList.remove("done", "error");
    tool.row.classList.add(status);
    tool.row.querySelector(".tool-state-icon")?.replaceChildren(
      iconNode(status === "done" ? "check" : "x"),
    );
    tool.copy.textContent = detail;
    tool.duration.textContent = formatDuration(completedAt - tool.startedAt);
    finishStep(tool.name, "tool", status, tool.id);
    refreshIcons(tool.row);
  }
  for (const step of state.run.steps.values()) {
    if (step.status !== "running") continue;
    step.status = status;
    step.completedAt = completedAt;
  }
  if (state.run.activityStatus) {
    if (state.run.tools.size) updateActivityProgress();
    else state.run.activityStatus.textContent = status === "done" ? "已完成" : "已结束";
  }
  applyHealthRouteState();
}

function applyRoleChatCompletion(event) {
  state.run.context.workflowSessionId = event.sessionId || state.run.context.workflowSessionId;
  const calls = Array.isArray(event.toolCalls) ? event.toolCalls : [];
  const receipts = Array.isArray(event.toolReceipts) ? event.toolReceipts : [];
  const receiptsById = new Map(receipts.map((receipt) => [receipt.callId, receipt]));

  for (const call of calls) {
    const receipt = receiptsById.get(call.callId);
    addTool({
      toolCallId: call.callId,
      toolName: call.toolName,
      presentation: call.presentation,
      argumentsJson: call.argumentsJson,
    });
    finishTool({
      toolCallId: call.callId,
      toolName: call.toolName,
      presentation: call.presentation,
      result: receipt?.resultJson,
      error: receipt?.errorMessage || receipt?.errorCode,
      status: receipt?.status,
      success: receipt ? !/(ERROR|DENIED)/i.test(String(receipt.status || "")) : true,
    });
  }

  for (const receipt of receipts) {
    if (calls.some((call) => call.callId === receipt.callId)) continue;
    addTool({
      toolCallId: receipt.callId,
      toolName: receipt.toolName,
      presentation: receipt.presentation,
    });
    finishTool({
      toolCallId: receipt.callId,
      toolName: receipt.toolName,
      presentation: receipt.presentation,
      result: receipt.resultJson,
      error: receipt.errorMessage || receipt.errorCode,
      status: receipt.status,
      success: !/(ERROR|DENIED)/i.test(String(receipt.status || "")),
    });
  }

  appendFallbackText(event.content);
  state.run.usage = mergeUsage(state.run.usage, {
    ...(event.usage || {}),
    model: event.model,
  });
}

function appendFallbackText(content) {
  if (!content || state.run.assistantText.trim()) return;
  appendText(content);
}

function summarizeToolResult(result) {
  if (result === undefined || result === null || result === "") return "Completed";
  const parsed = parseArguments(result);
  if (parsed && typeof parsed === "object") {
    const candidate = parsed.detail || parsed.message || parsed.error || parsed.status || parsed.result;
    if (candidate) return String(candidate).slice(0, 100);
  }
  const text = typeof parsed?.value === "string" ? parsed.value : String(result);
  return text.replace(/\s+/g, " ").slice(0, 100) || "Completed";
}

function startStep(name, kind, explicitId) {
  const key = explicitId || `${kind}:${name}`;
  if (state.run.steps.has(key)) return;
  state.run.steps.set(key, {
    key,
    name,
    kind,
    status: "running",
    startedAt: Date.now(),
  });
}

function finishStep(name, kind, status = "done", explicitId) {
  const key = explicitId || `${kind}:${name}`;
  const step = state.run.steps.get(key) || Array.from(state.run.steps.values()).find((item) => item.name === name && item.kind === kind);
  if (!step) {
    state.run.steps.set(key, {
      key,
      name,
      kind,
      status,
      startedAt: Date.now(),
      completedAt: Date.now(),
    });
    return;
  }
  step.status = status;
  step.completedAt = Date.now();
}

function startText() {
  if (state.run.textElement?.isConnected) return;
  state.run.textElement = el("div", "message-content", "");
  ensureAssistantBody().append(state.run.textElement);
  scrollThread();
}

function appendText(delta) {
  if (!delta) return;
  startText();
  state.run.assistantText += String(delta);
  renderAssistantSegments(state.run.textElement, state.run.assistantText);
  scrollThread();
}

function finishText() {
  finalizeAssistantSegments();
  if (state.run.textElement && !state.run.assistantText.trim() && !state.run.cardElements.size) {
    state.run.textElement.remove();
    state.run.textElement = null;
  }
}

function finalizeAssistantSegments() {
  if (!state.run.textElement?.isConnected) return;
  renderAssistantSegments(state.run.textElement, state.run.assistantText);
}

function renderMarkdown(target, source) {
  const text = String(source || "");
  if (!globalThis.marked?.parse || !globalThis.DOMPurify?.sanitize) {
    target.textContent = text;
    return;
  }
  const rendered = globalThis.marked.parse(text);
  target.innerHTML = globalThis.DOMPurify.sanitize(rendered, {
    USE_PROFILES: { html: true },
    FORBID_ATTR: ["style"],
  });
  for (const link of target.querySelectorAll("a[href]")) {
    const href = link.getAttribute("href") || "";
    if (/^https?:\/\//i.test(href)) {
      link.target = "_blank";
      link.rel = "noopener noreferrer";
    }
  }
}

function renderApproval(event) {
  const actorPending = entryActorProjection(conversationContext || state.activeConversation)?.pendingApproval;
  const requestId = event.requestId || event.approvalRequestId;
  if (actorPending && (!requestId || actorPending.approvalRequestId === requestId)) return;
  state.run.pendingApproval = event;
  state.run.context.runId = event.runId || state.run.context.runId;
  const card = el("section", "approval-card");
  const header = el("div", "approval-header");
  header.append(iconNode("shield-alert"), el("span", "", "需要确认"));
  const toolName = readableToolInvocationName(event.toolName || "workflow continuation");
  const description = event.prompt || `Agent 请求执行 ${toolName}`;
  const paragraph = el("p", "", description);
  const args = event.argumentsJson ? parseArguments(event.argumentsJson) : null;
  card.append(header, paragraph);
  if (args && Object.keys(args).length) card.append(el("pre", "", safeJson(args)));
  const actions = el("div", "approval-actions");
  const approve = el("button", "approve-button", "批准");
  approve.type = "button";
  const deny = el("button", "deny-button", "拒绝");
  deny.type = "button";
  const status = el("span", "approval-state", "Waiting");
  actions.append(approve, deny, status);
  card.append(actions);
  ensureAssistantBody().append(card);
  state.run.approvalCard = { card, approve, deny, status };
  approve.addEventListener("click", () => void submitApproval(true));
  deny.addEventListener("click", () => void submitApproval(false));
  setRunStatus("running", "Approval");
  dom.sidebarSessionMeta.textContent = "Waiting for approval";
  refreshIcons(card);
  scrollThread();
}

async function submitApproval(approved) {
  const conversation = state.activeConversation;
  const pending = state.run.pendingApproval;
  const card = state.run.approvalCard;
  if (!pending || !card) return;
  const controller = new AbortController();
  const requestConfig = state.run.config || configPayload(state.config);
  const workflowRequest = {
    actorId: state.run.context.actorId || state.actorId,
    runId: pending.runId || state.run.context.runId,
    stepId: pending.stepId,
    commandId: pending.commandId || state.run.context.commandId,
    requestId: pending.requestId || pending.approvalRequestId,
    toolApproval: pending.toolApproval || null,
  };
  conversation.controllers.add(controller);
  if (!conversation.controller) {
    conversation.controller = controller;
    state.activeController = controller;
    setRunningUi(true);
  }
  card.approve.disabled = true;
  card.deny.disabled = true;
  card.status.textContent = "Submitting";
  try {
    const assistantApproval = pending.approvalKind === "nyxid-chat";
    const response = await fetch(assistantApproval ? "/api/demo/chat" : "/api/demo/approve", {
      method: "POST",
      headers: demoHeaders(),
      signal: controller.signal,
      body: JSON.stringify(assistantApproval ? {
        surface: "nyxid-chat",
        type: "approval.resolve",
        conversationId: state.run.context.actorId || state.actorId,
        requestId: pending.requestId || pending.approvalRequestId,
        approved,
        reason: approved ? "Approved by user" : "Denied by user",
      } : {
        ...requestConfig,
        sessionId: state.workflowSessionId,
        ...workflowRequest,
        approved,
        reason: approved ? "Approved by user" : "Denied by user",
      }),
    });
    if (!response.ok) throw await responseError(response);
    withConversationState(conversation, () => {
      card.status.textContent = approved ? "Approved" : "Denied";
      card.card.classList.add(approved ? "approved" : "denied");
      state.run.pendingApproval = null;
    });
    const contentType = response.headers.get("content-type") || "";
    if (contentType.includes("text/event-stream")) {
      await consumeSse(response, async (raw) => {
        withConversationState(conversation, () => handleFrame(raw));
      });
    } else {
      await response.json().catch(() => null);
    }
  } catch (error) {
    withConversationState(conversation, () => {
      card.status.textContent = error.name === "AbortError" ? "Stopped" : "Failed";
      card.approve.disabled = false;
      card.deny.disabled = false;
      if (error.name !== "AbortError") addError(error.message || "审批提交失败");
    });
  } finally {
    withConversationState(conversation, () => {
      releaseConversationController(conversation, controller);
      setRunningUi(Boolean(state.activeController));
    });
    renderHistoryList();
  }
}

function renderMedia(event) {
  if (event.kind === "image") {
    const src = event.dataBase64
      ? `data:${event.mediaType || "image/png"};base64,${event.dataBase64}`
      : event.uri;
    if (src && /^(data:image\/|https:\/\/)/i.test(src)) {
      const image = el("img", "media-output");
      image.src = src;
      image.alt = event.name || event.text || "Agent output";
      ensureAssistantBody().append(image);
      scrollThread();
      return;
    }
  }
  addInfo(event.name ? `收到媒体输出：${event.name}` : "收到媒体输出。");
}

function addError(message) {
  const callout = el("div", "error-callout");
  callout.append(iconNode("circle-alert"), el("span", "", String(message).slice(0, 1000)));
  ensureAssistantBody().append(callout);
  refreshIcons(callout);
  scrollThread();
}

function addInfo(message) {
  const callout = el("div", "info-callout");
  callout.append(iconNode("info"), el("span", "", message));
  ensureAssistantBody().append(callout);
  refreshIcons(callout);
  scrollThread();
}

function findNyxidService({ serviceId = "", serviceSlug = "", resource = "" } = {}) {
  const normalizedSlug = String(serviceSlug).trim().toLowerCase();
  return state.services.find((service) =>
    (serviceId && service.id === serviceId) ||
    (normalizedSlug && String(service.slug).toLowerCase() === normalizedSlug) ||
    (resource && service.resourceUri === resource)) || null;
}

function refreshAuthorizationCards() {
  for (const conversation of state.conversationStates.values()) {
    for (const card of conversation.run.authorizationCards) {
      if (card.login) {
        if (!state.auth.authenticated) continue;
      } else {
        const service = findNyxidService(card);
        if (!service?.authorized) continue;
        card.serviceId = service.id;
        card.serviceSlug = service.slug;
        card.resource = service.resourceUri;
      }
      card.status = "granted";
      card.statusMessage = card.login
        ? "NyxID 登录已恢复；可将原请求放回输入框后手动发送。"
        : "Service 已连接；可将原请求放回输入框后手动发送。";
      renderServiceAuthorizationCard(card);
    }
  }
}

function addServiceAuthorizationPrompt(message, {
  login = false,
  serviceId = "",
  serviceSlug = "",
  resource = "",
  resendDraft = null,
} = {}) {
  const service = findNyxidService({ serviceId, serviceSlug, resource });
  const card = {
    root: el("div", "authorization-callout"),
    conversation: conversationContext || state.activeConversation,
    login,
    message: String(message).slice(0, 600),
    resendDraft,
    serviceId: service?.id || serviceId,
    serviceSlug: service?.slug || serviceSlug,
    resource: service?.resourceUri || resource,
    status: service?.authorized ? "granted" : "idle",
    statusMessage: service?.authorized
      ? "Service 已连接；可将原请求放回输入框后手动发送。"
      : "",
  };
  state.run.authorizationCards.push(card);
  ensureAssistantBody().append(card.root);
  renderServiceAuthorizationCard(card);
  scrollThread();
}

function renderServiceAuthorizationCard(card) {
  if (!card.root?.isConnected && !card.root?.parentNode) return;
  const service = card.login ? null : findNyxidService(card);
  if (service) {
    card.serviceId = service.id;
    card.serviceSlug = service.slug;
    card.resource = service.resourceUri;
  }
  const granted = card.status === "granted";
  card.root.className = `authorization-callout ${card.status}`;
  card.root.replaceChildren();
  card.root.append(iconNode(granted ? "shield-check" : card.login ? "log-in" : "key-round"));

  const content = el("div", "authorization-callout-content");
  const copy = el("div", "authorization-callout-copy");
  const titles = {
    configuring: "正在配置 Service",
    checking: "正在刷新 Service 状态",
    error: "连接未完成",
    granted: card.login ? "NyxID 已连接" : "Service 可用",
  };
  copy.append(
    el("strong", "", titles[card.status] || (card.login ? "需要 NyxID 登录" : "需要 Service 连接")),
    el("span", "", card.statusMessage || card.message),
  );
  content.append(copy);

  if (!card.login) {
    if (service || card.serviceSlug) {
      const summary = el("div", "authorization-service-summary");
      const serviceIcon = el("span", "authorization-service-icon");
      serviceIcon.append(iconNode(service?.authorized ? "shield-check" : "box"));
      const serviceCopy = el("span", "authorization-service-copy");
      serviceCopy.append(
        el("strong", "", service?.label || card.serviceSlug || "NyxID Service"),
        el("small", "", service?.slug || card.serviceSlug || "service"),
      );
      const serviceState = el(
        "em",
        service?.authorized ? "granted" : "",
        service?.authorized ? "可用" : service ? "需配置" : "未找到",
      );
      summary.append(serviceIcon, serviceCopy, serviceState);
      content.append(summary);
    }
  }

  const actions = el("div", "authorization-callout-actions");
  if (granted && card.resendDraft && (card.resendDraft.prompt || card.resendDraft.attachment)) {
    const resend = el("button", "service-authorize-button", "重新发送");
    resend.type = "button";
    resend.prepend(iconNode("corner-up-left"));
    resend.addEventListener("click", () => {
      if (card.conversation !== state.activeConversation) {
        showToast("请先切换到该会话，再将请求放回输入框。");
        return;
      }
      dom.promptInput.value = String(card.resendDraft.prompt || "");
      card.conversation.draft = dom.promptInput.value;
      if (card.resendDraft.attachment) {
        state.attachment = card.resendDraft.attachment;
        card.conversation.attachment = card.resendDraft.attachment;
        renderAttachment();
      }
      autoResizeComposer();
      renderActorControlUi();
      dom.promptInput.focus();
      card.statusMessage = "原请求已放回输入框；请核对后手动发送。";
      renderServiceAuthorizationCard(card);
    });
    actions.append(resend);
  } else if (!granted) {
    const refreshing = card.status === "configuring";
    const checking = card.status === "checking";
    const action = el(
      "button",
      "service-authorize-button",
      card.login ? "登录" : checking ? "正在刷新" : refreshing ? "刷新状态" : "管理 Services",
    );
    action.type = "button";
    action.disabled = checking;
    action.prepend(iconNode(card.login ? "log-in" : checking ? "loader-circle" :
      refreshing ? "refresh-cw" : "settings"));
    action.addEventListener("click", () => {
      if (card.login) {
        beginLogin();
        return;
      }
      if (refreshing) {
        void refreshServiceAuthorizationCard(card);
        return;
      }
      openServiceManagement(card);
    });
    actions.append(action);
  }
  const manage = el("button", "icon-button authorization-manage-button");
  manage.type = "button";
  manage.setAttribute("aria-label", "管理全部 NyxID services");
  manage.title = "管理全部 Services";
  manage.append(iconNode("boxes"));
  manage.addEventListener("click", () => openServiceManagement());
  actions.append(manage);

  card.root.append(content, actions);
  refreshIcons(card.root);
}

async function refreshServiceAuthorizationCard(card) {
  card.status = "checking";
  card.statusMessage = "正在从 NyxID 读取最新 service 状态。";
  renderServiceAuthorizationCard(card);
  await loadServices();
  const service = findNyxidService(card);
  if (service?.authorized) return;
  card.status = "error";
  card.statusMessage = service
    ? "尚未检测到可用的 service 凭据，可以重新打开 NyxID 配置。"
    : "当前 NyxID 账户中没有找到该 service。";
  renderServiceAuthorizationCard(card);
}

function completeRun(actorStatus) {
  const normalized = String(actorStatus || "").toLowerCase();
  const isNyxid = state.run.surface === "nyxid-chat";
  const terminal = normalized === "succeeded" ? "complete"
    : normalized === "failed" ? "error"
      : normalized === "stopped" ? "stopped"
        : normalized === "blocked" ? "blocked"
          : isNyxid ? "closed" : "complete";
  state.run.status = terminal;
  state.run.completedAt = Date.now();
  state.run.pendingApproval = null;
  removeRunProgress();
  finalizeRunningExecution(
    terminal === "complete" ? "done" : terminal === "error" ? "error" : "done",
    terminal === "blocked" ? "Actor blocked" : terminal === "closed" ? "Transport finished" : "Completed with run",
  );
  if (!state.run.assistantBody?.childElementCount) {
    addInfo(terminal === "blocked"
      ? "Transport 已结束；Actor 任务处于 blocked，等待后续操作。"
      : terminal === "closed"
        ? "Transport 已结束；尚未收到 Actor 成功证明。"
        : "运行已完成，但 Aevatar 没有返回可展示的文本或工具结果。");
  }
  const labels = {
    complete: "Complete",
    blocked: "Blocked",
    error: "Error",
    stopped: "Stopped",
    closed: "Finished",
  };
  setRunStatus(terminal === "complete" ? "complete" : terminal, labels[terminal]);
  dom.sidebarSessionMeta.textContent = labels[terminal];
  setRouteState(
    dom.routeUpstreamState,
    terminal,
    terminal === "complete" ? "ok" : terminal === "error" ? "error" : "",
  );
}

function inspectorRequestTrace(entry = state.activeConversation) {
  return selectedRequestTrace(entry) || currentRequestTrace(entry);
}

function inspectorRunState(entry = state.activeConversation) {
  return inspectorRequestTrace(entry)?.run || entry?.run || state.run;
}

function inspectorTraceOperation(entry = state.activeConversation, trace = inspectorRequestTrace(entry)) {
  return entry?.mainView === "traces" ? selectedTraceOperation(trace) : null;
}

function paintRunStatus(status, label = requestTraceStatusEnglish(status)) {
  dom.runStatus.className = `run-status ${status || "idle"}`;
  dom.runStatus.querySelector("strong").textContent = label;
}

function renderInspector() {
  if (!isActiveConversationContext()) return;
  const entry = state.activeConversation;
  const trace = inspectorRequestTrace(entry);
  const run = inspectorRunState(entry);
  const context = run.context || {};
  const historical = isReviewingHistoricalTrace(entry);
  const projection = entryActorProjection(entry);
  const isNyxid = state.config.surface === "nyxid-chat";
  const projectionTurnId = projection?.activeTurn?.turnId || projection?.latestTurn?.turnId ||
    projection?.task?.turnId;
  const actorTurnId = historical ? context.turnId : context.turnId || projectionTurnId;
  const projectionStatus = !historical && context.turnId && projectionTurnId === context.turnId
    ? actorTerminalRunStatus(projection)
    : null;
  const visibleStatus = projectionStatus || run.status || "idle";
  const operation = inspectorTraceOperation(entry, trace);
  dom.inspectorEyebrow.textContent = operation ? traceOperationKindLabel(operation.kind) : trace ? "Request trace" : "Current run";
  dom.inspectorTitle.textContent = operation ? operation.title || "操作详情" : trace ? "轨迹详情" : "运行详情";
  paintRunStatus(visibleStatus);
  dom.traceClientRequestFact.textContent = trace?.clientRequestId || run.clientRequestId || "—";
  dom.traceClientRequestFact.title = trace?.clientRequestId || run.clientRequestId || "";
  dom.traceInputFact.textContent = trace
    ? requestTraceInput(trace)
    : run.request?.prompt || (run.request?.attachment?.name ? `附件 ${run.request.attachment.name}` : "尚未发送请求");
  dom.traceStatusFact.textContent = requestTraceStatusLabel(visibleStatus);
  dom.traceEventFact.textContent = String(run.events.length);
  dom.traceToolFact.textContent = String(run.tools.size);
  dom.traceStartedFact.textContent = trace
    ? requestTraceStartTime(trace, { detailed: true })
    : run.startedAt ? new Date(run.startedAt).toLocaleString("zh-CN", { hour12: false }) : "—";
  dom.traceDurationFact.textContent = trace
    ? requestTraceDuration(trace)
    : run.startedAt && !run.completedAt && run.status === "running"
      ? "进行中"
      : run.startedAt && run.completedAt
        ? formatDuration(run.completedAt - run.startedAt)
        : "—";
  dom.traceReadonlyNotice.classList.toggle("hidden", !historical);
  const output = requestTraceOutput(run);
  dom.traceOutputFact.classList.toggle("empty", !output);
  if (output) {
    renderMarkdown(dom.traceOutputFact, output);
  } else {
    dom.traceOutputFact.textContent = visibleStatus === "running"
      ? "等待响应"
      : run.startedAt ? "未返回可展示的文本" : "尚无输出";
  }
  dom.routeSection.classList.toggle("hidden", historical);
  dom.eventCount.textContent = String(run.events.length);
  dom.clearEventsButton.disabled = historical;
  dom.actorFact.textContent = context.actorId || (!historical ? state.actorId : "—") || "—";
  dom.runFact.textContent = context.runId || "—";
  dom.commandFact.textContent = context.commandId || "—";
  dom.runIdentityLabel.textContent = isNyxid ? "Turn" : "Session";
  dom.runIdentityFact.textContent = isNyxid
    ? actorTurnId || "—"
    : context.workflowSessionId || state.workflowSessionId;
  dom.usageTokens.textContent = run.usage?.totalTokens ?? "—";
  const model = run.usage?.model || state.currentConversationMeta?.llmModel;
  const hasConversationData = Boolean(run.startedAt || state.currentConversationMeta);
  dom.usageModel.textContent = model || (hasConversationData ? "not reported" : "—");
  renderSteps(run, { trace, allowActorProjection: !historical });
  renderActorControlUi();
  updateElapsed(run);
}

function renderActorControlUi() {
  if (!isActiveConversationContext()) return;
  if (isReviewingHistoricalTrace()) {
    dom.sendButton.classList.add("hidden");
    dom.steerButton.classList.add("hidden");
    dom.stopButton.classList.add("hidden");
    dom.observationDisconnectButton.classList.add("hidden");
    dom.promptInput.disabled = true;
    dom.attachButton.disabled = true;
    dom.composerServicesButton.disabled = true;
    setComposerStatus("正在查看历史轨迹；返回当前轨迹后可继续操作");
    return;
  }
  const projection = entryActorProjection(state.activeConversation);
  const nyxid = state.config.surface === "nyxid-chat";
  const authoritativeStop = nyxid && actorCan(projection, "stop");
  const actorActive = nyxid && Boolean(
    projection?.activeTurn || projection?.task?.status === "active",
  );
  const pendingInput = nyxid ? activePendingInputContext() : null;
  if (pendingInput) {
    const submission = pendingInput.entry.needsYouSubmissions.get(pendingInput.key);
    const locked = submission?.status === "pending" || submission?.status === "accepted";
    const reliableVersion = reliableConversationStateVersion(state.activeConversation);
    const hasAnswer = Boolean(
      dom.promptInput.value.trim() || pendingInput.draft.selectedOptionIds.size,
    );
    dom.sendButton.classList.remove("hidden");
    dom.sendButton.disabled = !state.auth.authenticated || locked || reliableVersion <= 0 || !hasAnswer;
    dom.sendButton.setAttribute("aria-label", "提交回答");
    dom.sendButton.title = "提交回答";
    dom.steerButton.classList.add("hidden");
    dom.stopButton.classList.toggle("hidden", !authoritativeStop);
    dom.stopButton.disabled = false;
    dom.promptInput.disabled = !state.auth.authenticated;
    dom.attachButton.disabled = true;
    dom.composerServicesButton.disabled = true;
    dom.observationDisconnectButton.classList.toggle("hidden", !state.activeController);
    setComposerStatus(locked
      ? submission.message || "回答已受理，等待 Actor 确认。"
      : reliableVersion > 0
        ? "一次回答全部缺口；提交后 Actor 将继续当前任务"
        : "正在同步 Actor 状态…", { working: locked || reliableVersion <= 0 });
    return;
  }

  const canSteer = state.auth.authenticated && actorActive;
  dom.stopButton.classList.toggle("hidden", nyxid ? !authoritativeStop : !state.activeController);
  dom.stopButton.disabled = false;
  dom.stopButton.setAttribute("aria-label", nyxid ? "停止 Actor 任务" : "停止接收");
  dom.stopButton.title = nyxid ? "向 Actor 提交停止命令" : "停止接收";
  dom.steerButton.classList.toggle("hidden", !actorActive);
  dom.steerButton.disabled = !canSteer || !dom.promptInput.value.trim();
  dom.promptInput.disabled = !state.auth.authenticated || (Boolean(state.activeController) && !canSteer);
  dom.attachButton.disabled = !state.auth.authenticated || Boolean(state.activeController) || actorActive;
  dom.composerServicesButton.disabled = !state.auth.authenticated;
  dom.sendButton.classList.toggle("hidden", actorActive || Boolean(state.activeController));
  dom.sendButton.disabled = !state.auth.authenticated || Boolean(state.activeController);
  dom.sendButton.setAttribute("aria-label", "发送");
  dom.sendButton.title = "发送";
  if (actorActive) {
    setComposerStatus("Agent 正在执行当前任务；仍可输入 steering 指令", { working: true });
  } else if (state.activeController) {
    setComposerStatus("正在接收生产 Agent 输出 · 停止接收不会撤销已提交操作", { working: true });
  } else {
    setComposerStatus(state.auth.authenticated
      ? "生产环境 · 使用当前账户的 services，高风险操作需要确认"
      : "登录后使用当前账户已配置的 services");
  }
  dom.observationDisconnectButton.classList.toggle("hidden", !state.activeController);
}

function renderSteps(run = inspectorRunState(), { trace = null, allowActorProjection = true } = {}) {
  dom.stepList.replaceChildren();
  const projection = entryActorProjection(state.activeConversation);
  const traceTurnId = trace?.serverTurnId || run.context?.turnId || "";
  const projectionMatchesTrace = !trace || !run.startedAt ||
    (traceTurnId && projection?.task?.turnId === traceTurnId);
  const actorSteps = allowActorProjection && projectionMatchesTrace &&
    state.config.surface === "nyxid-chat" && projection?.task
    ? [...projection.steps.values()]
    : null;
  const steps = actorSteps || Array.from(run.steps.values());
  dom.stepCount.textContent = String(steps.length);
  if (!steps.length) {
    dom.stepList.className = "step-list empty-list";
    if (state.config.surface === "nyxid-chat") {
      const labels = {
        idle: state.currentConversationMeta
          ? "已加载历史；发送消息可继续此会话"
          : "发送消息后显示真实工具调用",
        running: "Agent 正在处理，尚未调用可展示工具",
        complete: "本次运行未调用可展示工具",
        error: "运行失败前没有收到工具事件",
        stopped: "停止接收前没有收到工具事件",
        closed: "流关闭前没有收到工具事件",
      };
      dom.stepList.textContent = labels[run.status] || "没有可展示的工具步骤";
    } else {
      dom.stepList.textContent = run.status === "idle"
        ? "发送消息后显示 Workflow 步骤"
        : "尚未收到 Workflow 步骤事件";
    }
    return;
  }
  dom.stepList.className = "step-list";
  for (const step of steps) {
    const row = el("div", `inspector-step ${step.status}`);
    const dot = el("span", "step-dot");
    dot.append(iconNode(step.status === "done" ? "check" : step.status === "error" ? "x" : "loader-circle"));
    const name = el("strong", "", actorSteps
      ? actorStepDisplayName(step)
      : step.description || step.name || "执行步骤");
    if (actorSteps) {
      row.append(dot, name, el(
        "small",
        "",
        `${actorStatusCopy(step.status)} · ${actorStepSourceLabel(step)} · ${actorStepEffectLabel(step)}`,
      ));
    } else {
      const elapsed = step.completedAt
        ? step.completedAt - step.startedAt
        : Date.now() - step.startedAt;
      row.append(dot, name, el("small", "", formatDuration(elapsed)));
    }
    dom.stepList.append(row);
  }
  refreshIcons(dom.stepList);
}

function renderEventLog() {
  if (!isActiveConversationContext() || !dom.eventCount || !dom.eventList) return;
  const run = inspectorRunState();
  const events = run.events;
  dom.eventCount.textContent = String(events.length);
  dom.clearEventsButton.disabled = isReviewingHistoricalTrace();
  if (!events.length) {
    if (dom.eventList.childElementCount !== 1 || !dom.eventList.firstElementChild?.classList.contains("event-empty")) {
      dom.eventList.replaceChildren(el("div", "event-empty", "尚无事件"));
    }
    return;
  }
  const rows = [...events].reverse().map((event) => {
    if (event.element) return event.element;
    const details = el("details", "event-row");
    const summary = document.createElement("summary");
    summary.append(
      el("span", "mono", `#${String(event.id).padStart(3, "0")}`),
      el("span", "event-kind", event.type),
      el("span", "mono", event.at.toLocaleTimeString("zh-CN", { hour12: false })),
    );
    details.append(summary, el("pre", "", safeJson(event.raw)));
    event.element = details;
    return details;
  });
  rows.forEach((row, index) => {
    const current = dom.eventList.children[index];
    if (current !== row) dom.eventList.insertBefore(row, current || null);
  });
  while (dom.eventList.childElementCount > rows.length) dom.eventList.lastElementChild.remove();
}

function clearEvents() {
  if (isReviewingHistoricalTrace()) return;
  inspectorRunState().events = [];
  renderEventLog();
  renderRequestTraces();
  renderInspector();
}

function configureWireInspector() {
  const enabled = state.config.enableStudioWireInspector === true && state.auth.authenticated;
  dom.eventsTabButton?.classList.toggle("hidden", !enabled);
  dom.eventsTabButton?.setAttribute("aria-hidden", String(!enabled));
  if (!enabled) setInspectorTab("run");
}

function updateElapsed(run = inspectorRunState()) {
  const start = run.startedAt;
  if (!start) {
    dom.usageElapsed.textContent = "00:00";
    return;
  }
  if (!run.completedAt) {
    dom.usageElapsed.textContent = run.status === "running" ? "进行中" : "—";
    if (dom.traceDurationFact) dom.traceDurationFact.textContent = run.status === "running" ? "进行中" : "—";
    return;
  }
  const end = run.completedAt;
  const seconds = Math.max(0, Math.floor((end - start) / 1000));
  dom.usageElapsed.textContent = `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
  if (dom.traceDurationFact) dom.traceDurationFact.textContent = formatDuration(end - start);
}

function setRunStatus(status, label) {
  if (!isActiveConversationContext()) return;
  if (isReviewingHistoricalTrace()) return;
  paintRunStatus(status, label);
}

function setComposerStatus(message, { working = false } = {}) {
  dom.composerStatus.textContent = message;
  dom.composerStatus.classList.toggle("working", working);
  dom.composerStatus.setAttribute("aria-busy", String(working));
}

function setRunningUi(running) {
  if (!isActiveConversationContext()) return;
  const canCompose = state.auth.authenticated && !running;
  dom.sendButton.classList.toggle("hidden", running);
  dom.stopButton.classList.toggle("hidden", !running);
  dom.steerButton.classList.add("hidden");
  dom.observationDisconnectButton.classList.toggle("hidden", !running);
  dom.promptInput.disabled = !canCompose;
  dom.attachButton.disabled = !canCompose;
  dom.composerServicesButton.disabled = !state.auth.authenticated;
  dom.sendButton.disabled = !canCompose;
  dom.newChatButton.disabled = !state.auth.authenticated;
  dom.settingsButton.disabled = false;
  dom.servicesButton.disabled = false;
  dom.connectionButton.disabled = false;
  if (!running) dom.stopButton.disabled = false;
  setComposerStatus(running
    ? "正在接收生产 Agent 输出 · 停止接收不会撤销已提交操作"
    : state.auth.authenticated
      ? "生产环境 · 使用当前账户的 services，高风险操作需要确认"
      : "登录后使用当前账户已配置的 services", { working: running });
  renderActorControlUi();
}

function cancelRun() {
  if (isReviewingHistoricalTrace()) return;
  if (!state.activeController) return;
  dom.stopButton.disabled = true;
  setComposerStatus("正在停止当前页面接收…", { working: true });
  abortConversationRun(state.activeConversation);
}

function cancelObservation() {
  if (isReviewingHistoricalTrace()) return;
  if (!state.activeController) return;
  dom.observationDisconnectButton.disabled = true;
  setComposerStatus("正在停止当前页面观察…", { working: true });
  abortConversationRun(state.activeConversation);
}

function abortConversationRun(entry) {
  if (!entry) return;
  for (const controller of entry.controllers) controller.abort();
  if (entry.controller && !entry.controllers.has(entry.controller)) entry.controller.abort();
}

function releaseConversationController(entry, controller) {
  entry.controllers.delete(controller);
  if (entry.controller !== controller) return;
  entry.controller = entry.controllers.values().next().value || null;
  state.activeController = entry.controller;
}

function abortAllRuns() {
  persistConversationState();
  for (const entry of state.conversationStates.values()) abortConversationRun(entry);
}

function newChat(options) {
  state.conversationLoadSequence += 1;
  state.pendingFirstTurn = null;
  const refreshHistory = options?.refreshHistory !== false;
  const previous = state.activeConversation;
  const discardPrevious = previous && !previous.actorId && !previous.controller && !previous.run.startedAt;
  const entry = createConversationState();
  entry.thread.append(dom.emptyState);
  activateConversationState(entry);
  if (discardPrevious) removeConversationState(previous);
  for (const candidate of Array.from(state.conversationStates.values())) {
    if (candidate === entry || candidate.actorId || candidate.controller || candidate.run.startedAt) continue;
    removeConversationState(candidate);
  }
  dom.emptyState.classList.remove("hidden");
  dom.promptInput.value = "";
  renderActiveConversationState();
  refreshIcons(dom.thread);
  closeMobilePanels();
  dom.promptInput.focus();
  if (refreshHistory) void loadConversations({ silent: true });
}

function setConversationTitle(value) {
  const normalized = String(value).replace(/\s+/g, " ").trim();
  const title = normalized.length > 32 ? `${normalized.slice(0, 32)}…` : normalized || "新会话";
  const entry = conversationContext || state.activeConversation;
  if (entry) entry.title = title;
  if (!isActiveConversationContext()) return;
  dom.conversationTitle.textContent = title;
  dom.sidebarSessionTitle.textContent = title;
}

async function selectAttachment() {
  const conversation = state.activeConversation;
  const file = dom.fileInput.files?.[0];
  if (!file) return;
  if (file.size > MAX_ATTACHMENT_BYTES) {
    showToast("附件不能超过 5 MB。");
    dom.fileInput.value = "";
    return;
  }
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = "";
  for (let index = 0; index < bytes.length; index += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
  }
  const attachment = {
    name: file.name,
    mediaType: file.type || "application/octet-stream",
    sizeBytes: file.size,
    dataBase64: btoa(binary),
  };
  withConversationState(conversation, () => {
    state.attachment = attachment;
    renderAttachment();
  });
}

function clearAttachment() {
  state.attachment = null;
  if (conversationContext || state.activeConversation) {
    (conversationContext || state.activeConversation).attachment = null;
  }
  dom.fileInput.value = "";
  dom.attachmentChip.classList.add("hidden");
}

function renderAttachment() {
  if (!isActiveConversationContext()) return;
  dom.fileInput.value = "";
  if (!state.attachment) {
    dom.attachmentChip.classList.add("hidden");
    return;
  }
  dom.attachmentName.textContent = `${state.attachment.name} · ${formatBytes(state.attachment.sizeBytes)}`;
  dom.attachmentChip.classList.remove("hidden");
  refreshIcons(dom.attachmentChip);
}

function setInspectorTab(tab) {
  const events = tab === "events" &&
    state.config.enableStudioWireInspector === true &&
    state.auth.authenticated;
  dom.runPanel.classList.toggle("hidden", events);
  dom.eventsPanel?.classList.toggle("hidden", !events);
  dom.runTabButton.classList.toggle("active", !events);
  dom.eventsTabButton?.classList.toggle("active", events);
  dom.runTabButton.setAttribute("aria-selected", String(!events));
  dom.eventsTabButton?.setAttribute("aria-selected", String(events));
}

function openMobilePanel(panel) {
  dom.sidebar.classList.toggle("open", panel === "sidebar");
  dom.inspector.classList.toggle("open", panel === "inspector");
  dom.mobileBackdrop.classList.remove("hidden");
}

function closeMobilePanels() {
  dom.sidebar.classList.remove("open");
  dom.inspector.classList.remove("open");
  dom.mobileBackdrop.classList.add("hidden");
}

function iconNode(name) {
  const icon = document.createElement("i");
  icon.dataset.lucide = name;
  return icon;
}

function formatDuration(milliseconds) {
  if (milliseconds < 1000) return `${Math.max(0, milliseconds)}ms`;
  return `${(milliseconds / 1000).toFixed(milliseconds < 10_000 ? 1 : 0)}s`;
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function scrollThread() {
  if (!isActiveConversationContext()) return;
  const viewport = dom.threadViewport;
  requestAnimationFrame(() => {
    viewport.scrollTo({ top: viewport.scrollHeight, behavior: "smooth" });
  });
}

function showToast(message) {
  dom.toastText.textContent = message;
  dom.toast.classList.add("show");
  clearTimeout(state.toastTimer);
  state.toastTimer = setTimeout(() => dom.toast.classList.remove("show"), 3200);
}

void init();
