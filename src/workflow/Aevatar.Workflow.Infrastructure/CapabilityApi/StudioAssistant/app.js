import {
  verifyKeyCreateReadBack,
  verifyKeyRotateReadBack,
  verifyPersonalServiceReadBack,
} from "./transport.js?v=20260813-p0-key-actions";
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
} from "./protocol.js?v=20260813-p0-key-actions";
import {
  buildConnectCardBlock,
  buildKeyActionCardBlock,
  connectCardSteps,
  connectorInitial,
  splitMessageSegments,
} from "./blocks.js?v=20260813-p0-key-actions";
import {
  actorCan,
  applyCurrentStateResult,
  createActorProjection,
  reduceActorEvent,
  restoreCachedAction,
} from "./actor-state.js?v=20260813-p0-key-actions";
import { describeReadinessFailure } from "./readiness.js?v=20260807-m40-thread-polish";

const PREFERENCES_KEY = "aevatar-studio:assistant-preferences:v4";
const MAX_ATTACHMENT_BYTES = 5 * 1024 * 1024;

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
  closeKeyActionDialogButton: $("#closeKeyActionDialogButton"),
  closeComposerServicesButton: $("#closeComposerServicesButton"),
  closeInspectorButton: $("#closeInspectorButton"),
  closeSettingsButton: $("#closeSettingsButton"),
  commandFact: $("#commandFact"),
  commandFactRow: $("#commandFactRow"),
  completeKeyActionButton: $("#completeKeyActionButton"),
  composerForm: $("#composerForm"),
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
  copyKeyActionSecretButton: $("#copyKeyActionSecretButton"),
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
  executeKeyActionButton: $("#executeKeyActionButton"),
  eventsPanel: $("#eventsPanel"),
  eventsTabButton: $("#eventsTabButton"),
  fileInput: $("#fileInput"),
  inspector: $("#inspector"),
  keyActionDialog: $("#keyActionDialog"),
  keyActionDialogDescription: $("#keyActionDialogDescription"),
  keyActionDialogError: $("#keyActionDialogError"),
  keyActionDialogFacts: $("#keyActionDialogFacts"),
  keyActionDialogStatus: $("#keyActionDialogStatus"),
  keyActionDialogTitle: $("#keyActionDialogTitle"),
  keyActionReplayKeyId: $("#keyActionReplayKeyId"),
  keyActionReplayPanel: $("#keyActionReplayPanel"),
  keyActionSavedConfirm: $("#keyActionSavedConfirm"),
  keyActionSavedConfirmRow: $("#keyActionSavedConfirmRow"),
  keyActionSecretInput: $("#keyActionSecretInput"),
  keyActionSecretPanel: $("#keyActionSecretPanel"),
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
  retryKeyActionReadBackButton: $("#retryKeyActionReadBackButton"),
  recentGroup: $("#recentGroup"),
  recentSessionsList: $("#recentSessionsList"),
  needsYouCount: $("#needsYouCount"),
  needsYouFilterButton: $("#needsYouFilterButton"),
  removeAttachmentButton: $("#removeAttachmentButton"),
  routeClientState: $("#routeClientState"),
  routeLabel: $("#routeLabel"),
  routeOrnnState: $("#routeOrnnState"),
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
  cancelKeyActionButton: $("#cancelKeyActionButton"),
  thread: $("#thread"),
  toast: $("#toast"),
  toastText: $("#toastText"),
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
    actionFrameCache: new Map(),
    actorStateNotice: "",
    actorTaskElement: null,
    actorControlReceipt: null,
    actorStateRefreshTimer: null,
    needsYouDrafts: new Map(),
    needsYouSubmissions: new Map(),
    approvalConfirmRequestId: null,
    meta,
    title,
    draft: "",
    attachment: null,
    run: createRunState(),
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
  state.run = entry.run;
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
  dom.closeKeyActionDialogButton.addEventListener("click", () => closeKeyActionDialog());
  dom.executeKeyActionButton.addEventListener("click", () => void executeKeyActionDialog());
  dom.retryKeyActionReadBackButton.addEventListener("click", () => void retryKeyActionReadBack());
  dom.completeKeyActionButton.addEventListener("click", () => void completeKeyActionDialog());
  dom.cancelKeyActionButton.addEventListener("click", () => void cancelKeyActionDialog());
  dom.copyKeyActionSecretButton.addEventListener("click", () => void copyKeyActionSecret());
  dom.keyActionSavedConfirm.addEventListener("change", () => {
    if (!activeKeyActionDialog) return;
    activeKeyActionDialog.savedConfirmed = dom.keyActionSavedConfirm.checked;
    activeKeyActionDialog.error = "";
    renderKeyActionDialog(activeKeyActionDialog);
  });
  dom.keyActionDialog.addEventListener("cancel", (event) => {
    event.preventDefault();
    closeKeyActionDialog();
  });
  dom.keyActionDialog.addEventListener("close", () => {
    if (activeKeyActionDialog) clearActiveKeyActionDialog();
  });
  window.addEventListener("pagehide", () => clearActiveKeyActionDialog());
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

function cacheActionRequest(entry, request) {
  if (!entry || !request) return;
  entry.actionFrameCache.set(request.actionRequestId, request);
  try {
    sessionStorage.setItem(
      actionCacheKey(request.actorId, request.actionRequestId),
      JSON.stringify(request),
    );
  } catch {
    // Session cache is optional; actor state remains the authority.
  }
}

function invalidateActionRequestCache(entry, actionRequestId) {
  if (!entry || !actionRequestId) return;
  entry.actionFrameCache.delete(actionRequestId);
  try {
    sessionStorage.removeItem(actionCacheKey(
      entry.actorProjection?.actorId || entry.actorId,
      actionRequestId,
    ));
  } catch {
    // Session cache is optional; the conflicted projection remains disabled.
  }
}

function restoreProjectionActionCaches(entry) {
  let projection = entry?.actorProjection;
  if (!projection?.actions?.size) return projection;
  for (const summary of projection.actions.values()) {
    let cached = entry.actionFrameCache.get(summary.actionRequestId) || null;
    if (!cached) {
      try {
        const raw = sessionStorage.getItem(actionCacheKey(
          projection.actorId || entry.actorId,
          summary.actionRequestId,
        ));
        cached = raw ? JSON.parse(raw) : null;
      } catch {
        cached = null;
      }
    }
    const request = restoreCachedAction(summary, cached);
    if (!request) continue;
    entry.actionFrameCache.set(request.actionRequestId, request);
    projection = reduceActorEvent(projection, {
      type: "action_request",
      sequence: projection.progressSequence,
      actionRequest: request,
    });
  }
  entry.actorProjection = projection;
  return projection;
}

function createConnectCard(action, { conversation = null } = {}) {
  const request = action?.request || null;
  if (!request) return null;
  const block = buildConnectCardBlock(request, state.connectors);
  const card = {
    action,
    request,
    conversation,
    slug: block.catalog_slug,
    root: el("section", "connect-card"),
    block,
    status: action.conflicted ? "conflicted" : "needs_connection",
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
  card.block = {
    ...card.block,
    state: "needs_connection",
    steps: connectCardSteps(card.block.service_name, card.block.auth_kind),
  };
  renderConnectCard(card);
  liveConnectCards.add(card);
  return card;
}

function projectedActionReport(action) {
  if (!Array.isArray(action?.reports)) return null;
  return [...action.reports].reverse().find((report) =>
    report?.actionRequestId === action.actionRequestId &&
    report?.originTurnId === action.originTurnId) || null;
}

function createKeyActionCard(action, { conversation = null } = {}) {
  const request = action?.request || null;
  if (!request || !KEY_ACTION_CARD_ACTIONS.includes(request.action)) return null;
  const block = buildKeyActionCardBlock(request);
  const report = projectedActionReport(action);
  const completed = report?.disposition === "completed";
  const card = {
    action,
    request,
    conversation,
    root: el("section", "connect-card key-action-card"),
    block,
    status: action.conflicted
      ? "conflicted"
      : report
        ? completed ? "awaiting_verification" : "reported"
        : "ready",
    busy: false,
    error: "",
    note: completed
      ? "Browser journey 已报告；等待 Actor 验证精确的 key postcondition。"
      : report
        ? `Browser journey 已报告 ${report.disposition}；等待 Actor 状态确认。`
        : "",
    continuation: null,
    report,
    effectKeyId: completed ? keyActionResourceId(report.resource) : "",
    replayed: null,
    requestedAt: "",
    browserVerified: completed,
    externalExpiryTimer: null,
  };
  card.root.dataset.actionRequestId = request.actionRequestId;
  card.root.dataset.actorId = request.actorId;
  card.root.dataset.originTurnId = request.originTurnId;
  card.root.dataset.taskId = request.taskId;
  card.root.dataset.stepId = request.stepId;
  if (!action.conflicted) applyActorActionProof(card, action, conversation?.actorProjection);
  renderKeyActionCard(card);
  return card;
}

function renderActionCard(card) {
  if (KEY_ACTION_CARD_ACTIONS.includes(card?.request?.action)) {
    renderKeyActionCard(card);
  } else {
    renderConnectCard(card);
  }
}

function renderActionCards(entry = conversationContext || state.activeConversation) {
  if (!entry) return;
  const projection = entry.actorProjection;
  if (!projection?.actions?.size && !entry.run.cardElements.size) return;
  const container = entry.run.actionCardsElement || el("div", "action-card-list");
  entry.run.actionCardsElement = container;

  for (const action of projection?.actions?.values?.() || []) {
    if (!["service.connect", ...KEY_ACTION_CARD_ACTIONS].includes(action.action) || !action.request) {
      continue;
    }
    let card = entry.run.cardElements.get(action.actionRequestId);
    if (!card) {
      card = action.action === "service.connect"
        ? createConnectCard(action, { conversation: entry })
        : createKeyActionCard(action, { conversation: entry });
      if (!card) continue;
      entry.run.cardElements.set(action.actionRequestId, card);
    } else {
      card.action = action;
      card.request = action.request;
      if (action.conflicted) {
        card.status = "conflicted";
        card.error = "Action identity conflict；该 browser journey 已禁用。";
      } else {
        const report = projectedActionReport(action);
        if (report) {
          card.report = report;
          if (report.disposition === "completed") {
            card.effectKeyId ||= keyActionResourceId(report.resource);
            card.browserVerified = true;
            if (card.status !== "verified") card.status = "awaiting_verification";
          } else if (card.status !== "verified") {
            card.status = "reported";
          }
        }
        applyActorActionProof(card, action, projection);
      }
      renderActionCard(card);
    }
  }

  container.replaceChildren(...[...entry.run.cardElements.values()].map((card) => card.root));
  if (!container.isConnected) ensureAssistantBody().append(container);
  scrollThread();
}

function actionResourceUserServiceId(resource) {
  return resource?.userService?.userServiceId || resource?.userServiceId || "";
}

const KEY_ACTION_CARD_ACTIONS = Object.freeze(["key.create", "key.rotate"]);
const KEY_ACTION_ERROR_MESSAGE = "NyxID 密钥操作证据暂时不可用；未向 Aevatar 报告结果。";

let activeKeyActionDialog = null;

export class KeyActionCardError extends Error {
  constructor(code = "NYXID_KEY_ACTION_CARD_INVALID") {
    super("NyxID key action is not ready to report.");
    this.name = "KeyActionCardError";
    this.code = code;
  }
}

function keyActionResourceId(resource) {
  if (!resource || typeof resource !== "object" || Array.isArray(resource)) return "";
  const hasNestedKey = Object.prototype.hasOwnProperty.call(resource, "key");
  const hasFlatKey = Object.prototype.hasOwnProperty.call(resource, "keyId");
  const hasUserService = Object.prototype.hasOwnProperty.call(resource, "userService") ||
    Object.prototype.hasOwnProperty.call(resource, "userServiceId");
  if (hasUserService || hasNestedKey === hasFlatKey) return "";
  const keyId = hasNestedKey ? resource.key?.keyId : resource.keyId;
  return typeof keyId === "string" ? keyId : "";
}

export function buildKeyActionCompletedResource(request, effect, state) {
  if (!KEY_ACTION_CARD_ACTIONS.includes(request?.action) ||
      state?.browserVerified !== true ||
      (!effect?.replayed && state?.savedConfirmed !== true)) {
    throw new KeyActionCardError();
  }
  const keyId = String(effect?.resource?.keyId || "");
  if (!keyId || effect?.replayed !== true && typeof effect?.fullKey !== "string") {
    throw new KeyActionCardError();
  }
  return { key: { keyId } };
}

function createKeyActionDialogState(card) {
  if (!KEY_ACTION_CARD_ACTIONS.includes(card?.request?.action)) {
    throw new KeyActionCardError();
  }
  return {
    card,
    request: card.request,
    effect: null,
    phase: "confirm",
    busy: false,
    browserVerified: false,
    savedConfirmed: false,
    error: "",
  };
}

function keyActionDialogCanClose(dialogState) {
  if (!dialogState?.effect || dialogState.effect.replayed) return true;
  return dialogState.savedConfirmed === true;
}

function keyActionDialogCompletedResource(dialogState) {
  return buildKeyActionCompletedResource(
    dialogState?.request,
    dialogState?.effect,
    dialogState,
  );
}

function clearKeyActionDialogState(dialogState) {
  if (!dialogState) return;
  dialogState.effect = null;
  dialogState.browserVerified = false;
  dialogState.savedConfirmed = false;
  dialogState.busy = false;
  dialogState.error = "";
  dialogState.phase = "cleared";
}

async function readKeyActionIoJson(fetchImpl, path, init = {}) {
  try {
    const response = await fetchImpl(path, init);
    if (!response?.ok) throw new KeyActionCardError("NYXID_KEY_ACTION_IO_UNAVAILABLE");
    const payload = await response.json();
    if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
      throw new KeyActionCardError("NYXID_KEY_ACTION_IO_INVALID");
    }
    return payload;
  } catch {
    throw new KeyActionCardError("NYXID_KEY_ACTION_IO_UNAVAILABLE");
  }
}

export function createKeyActionIo(fetchImpl) {
  if (typeof fetchImpl !== "function") throw new KeyActionCardError();
  return {
    async readService(serviceId) {
      return await readKeyActionIoJson(
        fetchImpl,
        `/api/nyxid/keys/${encodeURIComponent(serviceId)}`,
        { cache: "no-store" },
      );
    },
    verifyService(serviceId, snapshot) {
      return verifyPersonalServiceReadBack(serviceId, snapshot);
    },
    async mutate(request) {
      const create = request.action === "key.create";
      const path = create
        ? "/api/nyxid/assistant-actions/key-create"
        : request.action === "key.rotate"
          ? "/api/nyxid/assistant-actions/key-rotate"
          : "";
      if (!path) throw new KeyActionCardError();
      const body = create
        ? {
            actionRequestId: request.actionRequestId,
            name: request.params.name,
            platform: request.params.platform,
            allowedServiceIds: [...request.params.allowedServiceIds],
          }
        : {
            actionRequestId: request.actionRequestId,
            keyId: request.params.keyId,
          };
      return await readKeyActionIoJson(fetchImpl, path, {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
    },
    async readKey(keyId) {
      return await readKeyActionIoJson(
        fetchImpl,
        `/api/nyxid/api-keys/${encodeURIComponent(keyId)}`,
        { cache: "no-store" },
      );
    },
    verifyCreate(request, effect, snapshot) {
      return verifyKeyCreateReadBack(request, effect, snapshot);
    },
    verifyRotate(request, effect, snapshot) {
      return verifyKeyRotateReadBack(request, effect, snapshot);
    },
  };
}

const keyActionIo = createKeyActionIo((...args) => fetch(...args));

async function runKeyActionReadBack(dialogState, io) {
  if (!dialogState?.effect) throw new KeyActionCardError();
  dialogState.busy = true;
  dialogState.browserVerified = false;
  dialogState.error = "";
  dialogState.phase = "verifying";
  try {
    const keyId = String(dialogState.effect.resource?.keyId || "");
    const snapshot = await io.readKey(keyId);
    if (dialogState.request.action === "key.create") {
      io.verifyCreate(dialogState.request, dialogState.effect, snapshot);
    } else {
      io.verifyRotate(dialogState.request, dialogState.effect, snapshot);
    }
    dialogState.browserVerified = true;
    dialogState.phase = "verified";
    const card = dialogState.card;
    card.effectKeyId = keyId;
    card.replayed = dialogState.effect.replayed;
    card.requestedAt = dialogState.effect.requestedAt || "";
    card.browserVerified = true;
    if (!card.report) card.status = "browser_verified";
    card.error = "";
    card.note = dialogState.effect.replayed
      ? "已精确读取既有 key identity；原始一次性密钥不可恢复。"
      : "浏览器已精确验证 key；确认安全保存后才能报告。";
    return true;
  } catch {
    dialogState.error = KEY_ACTION_ERROR_MESSAGE;
    dialogState.phase = "verification_error";
    dialogState.card.browserVerified = false;
    throw new KeyActionCardError("NYXID_KEY_ACTION_READ_BACK_UNAVAILABLE");
  } finally {
    dialogState.busy = false;
  }
}

async function runKeyActionMutation(dialogState, io) {
  if (!dialogState || dialogState.effect || dialogState.busy) {
    throw new KeyActionCardError();
  }
  dialogState.busy = true;
  dialogState.error = "";
  dialogState.phase = "mutating";
  try {
    if (dialogState.request.action === "key.create") {
      for (const serviceId of dialogState.request.params.allowedServiceIds) {
        io.verifyService(serviceId, await io.readService(serviceId));
      }
    }
    dialogState.effect = await io.mutate(dialogState.request);
  } catch {
    dialogState.error = KEY_ACTION_ERROR_MESSAGE;
    dialogState.phase = "mutation_error";
    throw new KeyActionCardError("NYXID_KEY_ACTION_MUTATION_UNAVAILABLE");
  } finally {
    dialogState.busy = false;
  }
  return await runKeyActionReadBack(dialogState, io);
}

function keyActionCardPill(card) {
  const labels = {
    ready: "待执行",
    browser_verified: "浏览器已验证",
    reporting: "正在报告",
    awaiting_verification: "等待 Actor 验证",
    reported: "等待 Actor 确认",
    verified: "已验证",
    conflicted: "身份冲突",
    error: "操作失败",
  };
  const modifier = card.status === "verified"
    ? " ok"
    : ["error", "conflicted"].includes(card.status)
      ? " bad"
      : "";
  return el("span", `cc-pill${modifier}`, card.busy ? "处理中…" : labels[card.status] || "待执行");
}

function renderKeyActionCard(card) {
  const reported = ["reporting", "awaiting_verification", "reported", "verified"]
    .includes(card.status);
  const browserVerified = card.browserVerified === true || reported;
  const verified = card.status === "verified";
  card.root.className = `connect-card key-action-card ${card.status}`;
  card.root.replaceChildren();

  const head = el("div", "cc-head");
  const brand = el("div", "cc-brand");
  const logo = el("span", "cc-logo");
  logo.append(iconNode(card.request.action === "key.rotate" ? "refresh-cw" : "key-round"));
  const copy = el("div", "cc-copy");
  const subtitle = card.status === "conflicted"
    ? "同一个 actionRequestId 出现了不一致的 authoritative params"
    : verified
      ? "Actor 已验证精确的 key postcondition"
      : reported
        ? "Browser journey 已完成；这还不是 Action 成功证明"
        : browserVerified
          ? "浏览器已完成精确 read-back；尚未取得 Actor 验证"
          : card.block.subtitle;
  copy.append(el("div", "cc-title", card.block.title), el("div", "cc-sub", subtitle));
  brand.append(logo, copy);
  head.append(brand, keyActionCardPill(card));
  card.root.append(head);

  const facts = el("dl", "key-action-card-facts");
  for (const fact of card.block.facts) {
    const row = el("div", "key-action-card-fact");
    row.append(el("dt", "", fact.label), el("dd", "mono", fact.value));
    facts.append(row);
  }
  if (card.effectKeyId) {
    const row = el("div", "key-action-card-fact");
    row.append(el("dt", "", "Effect Key ID"), el("dd", "mono", card.effectKeyId));
    facts.append(row);
  }
  card.root.append(facts);

  const progress = el("div", "cc-progress");
  card.block.steps.forEach((step, index) => {
    const done = index < 2 ? browserVerified : verified;
    const active = !verified && (
      (!browserVerified && index === 0) ||
      (browserVerified && index === 2)
    );
    const item = el("div", `cc-progress-step${done ? " done" : active ? " active" : ""}`);
    item.title = step.body || step.title;
    const marker = el("span", "cc-progress-marker");
    if (done) marker.append(iconNode("check"));
    else if (active) marker.append(iconNode(reported ? "loader-circle" : "circle"));
    else marker.textContent = String(index + 1);
    item.append(marker, el("span", "cc-progress-label", step.title));
    progress.append(item);
  });
  card.root.append(progress);

  if (reported) {
    const verification = el("div", `cc-verification${verified ? " verified" : ""}`);
    verification.append(
      iconNode(verified ? "badge-check" : "loader-circle"),
      el("span", "", card.note || (verified ? "已验证" : "等待 Actor 验证")),
    );
    card.root.append(verification);
  } else if (card.status !== "conflicted") {
    const zone = el("div", "cc-action-zone");
    if (card.note) zone.append(el("div", "cc-hint", card.note));
    const actions = el("div", "cc-actions");
    if (card.status === "error" && card.continuation && card.report) {
      const retry = el("button", "cc-btn primary", "重试安全报告");
      retry.type = "button";
      retry.disabled = card.busy;
      retry.addEventListener("click", () => void submitActionContinuation(
        card,
        card.report.disposition,
        card.report.resource || null,
      ));
      actions.append(retry);
    } else {
      const execute = el("button", "cc-btn primary", "");
      execute.type = "button";
      execute.disabled = card.busy;
      execute.append(
        iconNode(card.request.action === "key.rotate" ? "refresh-cw" : "key-round"),
        el("span", "", browserVerified ? "重新读取并上报" : card.block.title),
      );
      execute.addEventListener("click", () => openKeyActionDialog(card));
      actions.append(execute);
      if (!browserVerified) {
        const decline = el("button", "cc-btn ghost", "不执行");
        decline.type = "button";
        decline.disabled = card.busy;
        decline.addEventListener("click", () => void submitActionContinuation(card, "declined"));
        actions.append(decline);
      }
    }
    zone.append(actions);
    card.root.append(zone);
  }

  if (card.error) {
    const error = el("div", "cc-error");
    error.append(iconNode("circle-alert"), el("span", "", card.error));
    card.root.append(error);
  }
  const foot = el("div", "cc-foot");
  foot.append(iconNode("shield-check"), el("span", "", card.block.footer));
  card.root.append(foot);
  refreshIcons(card.root);
}

function openKeyActionDialog(card) {
  if (!card || card.busy || card.status === "conflicted") return;
  if (activeKeyActionDialog && !closeKeyActionDialog()) return;
  activeKeyActionDialog = createKeyActionDialogState(card);
  renderKeyActionDialog(activeKeyActionDialog);
  if (!dom.keyActionDialog.open) dom.keyActionDialog.showModal();
  refreshIcons(dom.keyActionDialog);
}

function renderKeyActionDialog(dialogState = activeKeyActionDialog) {
  if (!dialogState) return;
  const create = dialogState.request.action === "key.create";
  const effect = dialogState.effect;
  const hasSecret = effect?.replayed === false && typeof effect.fullKey === "string";
  const replayed = effect?.replayed === true;
  const phaseText = {
    confirm: "确认以下请求后，浏览器将使用当前 OIDC 会话直接调用 NyxID。",
    mutating: "正在执行 NyxID 密钥操作…",
    verifying: "正在读取同一 key identity 并精确校验…",
    verified: replayed
      ? "已验证 replay 返回的 key identity；原始一次性密钥不可恢复。"
      : "浏览器 read-back 已通过；确认已安全保存后才能报告。",
    mutation_error: "密钥操作未取得可验证证据，尚未向 Aevatar 报告。",
    verification_error: "Read-back 未通过，保留同一 key identity 供安全重试。",
  }[dialogState.phase] || "";

  dom.keyActionDialogTitle.textContent = create ? "创建 API key" : "轮换 API key";
  dom.keyActionDialogDescription.textContent = create
    ? "NyxID 将创建仅允许所列 Services、scope 为 proxy 的 API key。"
    : "NyxID 将轮换指定 predecessor key，并验证 replacement lineage。";
  dom.keyActionDialogStatus.textContent = dialogState.copied
    ? `${phaseText} 一次性密钥已复制，请确认保存位置。`
    : phaseText;
  dom.keyActionDialogFacts.replaceChildren();
  for (const fact of dialogState.card.block.facts) {
    const row = el("div", "key-action-dialog-fact");
    row.append(el("dt", "", fact.label), el("dd", "mono", fact.value));
    dom.keyActionDialogFacts.append(row);
  }

  dom.keyActionSecretPanel.classList.toggle("hidden", !hasSecret);
  dom.keyActionSecretInput.value = hasSecret ? effect.fullKey : "";
  dom.copyKeyActionSecretButton.disabled = dialogState.busy || !hasSecret;
  dom.keyActionReplayPanel.classList.toggle("hidden", !replayed);
  dom.keyActionReplayKeyId.textContent = replayed ? String(effect.resource?.keyId || "") : "";
  dom.keyActionSavedConfirmRow.classList.toggle("hidden", !hasSecret);
  dom.keyActionSavedConfirm.checked = dialogState.savedConfirmed === true;
  dom.keyActionSavedConfirm.disabled = dialogState.busy || !hasSecret;
  dom.keyActionDialogError.textContent = dialogState.error || "";
  dom.keyActionDialogError.classList.toggle("hidden", !dialogState.error);

  const canComplete = dialogState.browserVerified === true &&
    (replayed || dialogState.savedConfirmed === true);
  dom.executeKeyActionButton.classList.toggle("hidden", Boolean(effect));
  dom.executeKeyActionButton.disabled = dialogState.busy || Boolean(effect);
  dom.executeKeyActionButton.textContent = dialogState.phase === "mutation_error"
    ? "重试执行"
    : create ? "创建密钥" : "轮换密钥";
  dom.retryKeyActionReadBackButton.classList.toggle(
    "hidden",
    !effect || dialogState.browserVerified === true,
  );
  dom.retryKeyActionReadBackButton.disabled = dialogState.busy || !effect;
  dom.completeKeyActionButton.classList.toggle("hidden", !dialogState.browserVerified);
  dom.completeKeyActionButton.disabled = dialogState.busy || !canComplete;
  dom.cancelKeyActionButton.disabled = dialogState.busy;
  dom.closeKeyActionDialogButton.disabled = dialogState.busy;
  refreshIcons(dom.keyActionDialog);
}

function clearActiveKeyActionDialog() {
  const dialogState = activeKeyActionDialog;
  if (!dialogState) {
    if (dom.keyActionSecretInput) dom.keyActionSecretInput.value = "";
    return;
  }
  const card = dialogState.card;
  dom.keyActionSecretInput.value = "";
  clearKeyActionDialogState(dialogState);
  activeKeyActionDialog = null;
  if (card) renderActionCard(card);
}

function closeKeyActionDialog({ force = false } = {}) {
  const dialogState = activeKeyActionDialog;
  if (!dialogState) {
    if (dom.keyActionDialog.open) dom.keyActionDialog.close();
    return true;
  }
  if (!force && !keyActionDialogCanClose(dialogState)) {
    dialogState.error = "一次性密钥仍在当前对话框中；请先安全保存并勾选确认。";
    renderKeyActionDialog(dialogState);
    return false;
  }
  clearActiveKeyActionDialog();
  if (dom.keyActionDialog.open) dom.keyActionDialog.close();
  return true;
}

async function executeKeyActionDialog() {
  const dialogState = activeKeyActionDialog;
  if (!dialogState || dialogState.busy || dialogState.effect) return;
  const operation = runKeyActionMutation(dialogState, keyActionIo);
  renderKeyActionDialog(dialogState);
  try {
    await operation;
  } catch {
    // The journey helper records only the stable, secret-free error state.
  }
  if (activeKeyActionDialog === dialogState) {
    renderKeyActionDialog(dialogState);
    renderActionCard(dialogState.card);
  }
}

async function retryKeyActionReadBack() {
  const dialogState = activeKeyActionDialog;
  if (!dialogState || dialogState.busy || !dialogState.effect) return;
  const operation = runKeyActionReadBack(dialogState, keyActionIo);
  renderKeyActionDialog(dialogState);
  try {
    await operation;
  } catch {
    // The journey helper records only the stable, secret-free error state.
  }
  if (activeKeyActionDialog === dialogState) {
    renderKeyActionDialog(dialogState);
    renderActionCard(dialogState.card);
  }
}

async function copyKeyActionSecret() {
  const dialogState = activeKeyActionDialog;
  if (!dialogState ||
      dialogState.effect?.replayed !== false ||
      !dom.keyActionSecretInput.value ||
      dialogState.busy) return;
  try {
    await navigator.clipboard.writeText(dom.keyActionSecretInput.value);
    dialogState.copied = true;
    dialogState.error = "";
  } catch {
    dialogState.error = "浏览器无法写入剪贴板；请从只读字段手动选择并保存。";
  }
  if (activeKeyActionDialog === dialogState) renderKeyActionDialog(dialogState);
}

async function completeKeyActionDialog() {
  const dialogState = activeKeyActionDialog;
  if (!dialogState || dialogState.busy) return;
  let resource;
  try {
    resource = keyActionDialogCompletedResource(dialogState);
  } catch {
    dialogState.error = "浏览器证据或安全保存确认尚未完成。";
    renderKeyActionDialog(dialogState);
    return;
  }
  const card = dialogState.card;
  if (!closeKeyActionDialog({ force: true })) return;
  await submitActionContinuation(card, "completed", resource);
}

async function cancelKeyActionDialog() {
  const dialogState = activeKeyActionDialog;
  if (!dialogState || dialogState.busy) return;
  const card = dialogState.card;
  if (!closeKeyActionDialog()) return;
  await submitActionContinuation(card, "cancelled");
}

function applyActorActionProof(card, action, projection) {
  if (!card.report || card.report.disposition !== "completed") return false;
  const keyAction = KEY_ACTION_CARD_ACTIONS.includes(card.request.action);
  const expectedResourceId = keyAction
    ? keyActionResourceId(card.report.resource)
    : actionResourceUserServiceId(card.report.resource);
  const proof = action.postconditionResult;
  const proofMatches = Boolean(expectedResourceId) && proof?.verified === true &&
    proof.actionRequestId === card.request.actionRequestId &&
    proof.disposition === card.report.disposition &&
    (keyAction
      ? keyActionResourceId(proof.resource) === expectedResourceId
      : actionResourceUserServiceId(proof.resource) === expectedResourceId);
  const confirmedStep = [...(projection?.steps?.values?.() || [])].some((step) =>
    step?.actionRequestId === card.request.actionRequestId &&
    step?.kind === "postcondition" &&
    step?.status === "done" &&
    step?.externalEffect === "confirmed");
  if (!proofMatches && (keyAction || !confirmedStep)) return false;
  card.status = "verified";
  card.busy = false;
  card.error = "";
  card.note = keyAction
    ? "Actor 已确认精确的 key postcondition。"
    : "Actor 已确认精确的 UserService postcondition。";
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
    await submitActionContinuation(card, "completed", {
      userService: { userServiceId },
    });
    void loadServices();
  } catch (error) {
    card.busy = false;
    card.error = error.message || "连接失败，请重试。";
    if (error.code === "NYXID_USER_SERVICE_ID_MISSING") {
      card.status = "error";
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

async function submitActionContinuation(card, disposition, resource = null) {
  const conversation = card.conversation;
  if (!conversation || card.status === "conflicted") return;
  if (card.continuation && !continuationMatches(card, disposition, resource)) {
    const refreshed = await refreshActorState(conversation);
    const pending = refreshed?.actions?.get(card.request.actionRequestId);
    if (!pending || !restoreCachedAction(pending, card.request)) {
      card.busy = false;
      card.status = "error";
      card.error = "This action is no longer pending with the same identity; the changed report was not sent.";
      renderActionCard(card);
      return;
    }
  }
  let continuation;
  try {
    continuation = continuationIntent(card, disposition, resource);
  } catch (error) {
    card.status = "error";
    card.error = error.message || "Action continuation is invalid.";
    renderActionCard(card);
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
  renderActionCard(card);
  try {
    const response = await fetch("/api/demo/chat", {
      method: "POST",
      headers: demoHeaders(),
      signal: controller.signal,
      body: JSON.stringify({
        surface: "nyxid-chat",
        ...continuation,
        conversationId: card.request.actorId,
      }),
    });
    if (!response.ok) throw await responseError(response);
    card.status = disposition === "completed" ? "awaiting_verification" : "reported";
    card.note = disposition === "completed"
      ? "Browser journey 已报告；等待 actor 验证 postcondition。"
      : `Browser journey 已报告 ${disposition}；等待 actor 状态确认。`;
    renderActionCard(card);
    await consumeSse(response, async (raw) => {
      withConversationState(conversation, () => handleFrame(raw));
    });
    await refreshActorState(conversation);
    withConversationState(conversation, () => {
      const projected = conversation.actorProjection.actions.get(card.request.actionRequestId);
      applyActorActionProof(card, projected || card.action, conversation.actorProjection);
      if (card.status !== "verified") {
        card.status = disposition === "completed" ? "awaiting_verification" : "reported";
        card.note = disposition === "completed"
          ? "Browser journey 已报告；等待 actor 验证 postcondition。"
          : `Browser journey 已报告 ${disposition}；等待 actor 状态确认。`;
      }
      card.busy = false;
      renderActionCard(card);
    });
  } catch (error) {
    withConversationState(conversation, () => {
      card.busy = false;
      card.status = "error";
      card.error = error.name === "AbortError"
        ? "已停止观察 continuation；Actor 可能仍在处理报告。"
        : error.message || "Action continuation 提交失败。";
      renderActionCard(card);
    });
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
  await submitActionContinuation(card, "completed", {
    userService: { userServiceId: candidates[0] },
  });
  void loadServices();
}

function updateLiveConnectCards() {
  for (const card of Array.from(liveConnectCards)) {
    if (!card.root.isConnected) {
      liveConnectCards.delete(card);
      continue;
    }
    if (["reporting", "awaiting_verification", "verified", "conflicted"].includes(card.status)) {
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
    waiting_for_user: "等待连接",
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
    : card.status === "verified"
    ? "Actor 已验证精确的连接 postcondition"
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
  const apiKeyFlow = card.block.known && card.block.auth_kind === "api_key";

  if (card.status === "error" && card.continuation && card.report) {
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
    dom.composerStatus.textContent = "登录后使用当前账户已配置的 services";
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
  dom.composerStatus.textContent = state.auth.authenticated
    ? "生产环境 · 使用当前账户的 services，高风险操作需要确认"
    : "登录后使用当前账户已配置的 services";
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
      if (!entry.controller) entry.title = conversation.title;
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
    activateConversationState(cached);
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
    const messages = normalizeStoredMessages(await response.json());
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
    activateConversationState(entry);
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

function actorStateWithActionHistory(envelope) {
  const snapshot = envelope?.status === "current" ? envelope.snapshot : null;
  if (!snapshot || typeof snapshot !== "object" || Array.isArray(snapshot)) return envelope;

  const actions = [];
  const positions = new Map();
  for (const summary of [
    ...(Array.isArray(snapshot.pendingActions) ? snapshot.pendingActions : []),
    ...(Array.isArray(snapshot.recentActions) ? snapshot.recentActions : []),
  ]) {
    const actionRequestId = typeof summary?.actionRequestId === "string"
      ? summary.actionRequestId
      : "";
    if (!actionRequestId || !positions.has(actionRequestId)) {
      if (actionRequestId) positions.set(actionRequestId, actions.length);
      actions.push(summary);
      continue;
    }
    actions[positions.get(actionRequestId)] = summary;
  }

  return {
    ...envelope,
    snapshot: {
      ...snapshot,
      pendingActions: actions,
    },
  };
}

function restoreCurrentStateActionRequests(entry, envelope) {
  const snapshot = envelope?.status === "current" ? envelope.snapshot : null;
  const summaries = snapshot?.pendingActions;
  const actorId = typeof snapshot?.actorId === "string" ? snapshot.actorId : "";
  if (!entry?.actionFrameCache || !Array.isArray(summaries) ||
      !actorId || actorId !== entry.actorId) return;
  for (const summary of summaries) {
    const request = restoreCachedAction({ ...summary, actorId }, summary?.request);
    if (request) entry.actionFrameCache.set(request.actionRequestId, request);
  }
}

async function refreshActorState(entry, { uncursored = false } = {}) {
  if (!entry?.actorId) return null;
  if (entry.stateReloadInFlight && !uncursored) return entry.stateReloadInFlight;

  const request = (async () => {
    const params = new URLSearchParams();
    const projection = entry.actorProjection || createActorProjection(entry.actorId);
    const turnId = actorStateTurnId(projection);
    if (!uncursored && projection.stateVersion > 0 && turnId) {
      params.set("afterStateVersion", String(projection.stateVersion));
      params.set("turnId", turnId);
    }
    const query = params.size ? `?${params}` : "";
    try {
      const response = await fetch(
        `/api/demo/conversations/${encodeURIComponent(entry.actorId)}/state${query}`,
        { headers: demoHeaders(), cache: "no-store" },
      );
      if (!response.ok) throw await responseError(response);
      const envelope = actorStateWithActionHistory(await response.json());
      restoreCurrentStateActionRequests(entry, envelope);
      const result = applyCurrentStateResult(projection, envelope);
      entry.actorProjection = result.projection;
      if (result.reloadWithoutCursor) {
        if (!uncursored) return refreshActorState(entry, { uncursored: true });
        entry.actorStateNotice = "Actor 要求重新加载状态，请稍后重试。";
      } else if (result.projection.stateVersion === 0 && !result.projection.task) {
        entry.actorStateNotice = "该会话没有可恢复的 actor 状态。";
      } else {
        entry.actorStateNotice = "";
      }
      restoreProjectionActionCaches(entry);
      renderActorProjection(entry);
      renderActionCards(entry);
      if (entry === state.activeConversation) renderActiveConversationState();
      return entry.actorProjection;
    } catch (error) {
      entry.actorStateNotice = `无法恢复 actor 状态：${String(error?.message || "unknown error").slice(0, 300)}`;
      renderActorProjection(entry);
      return null;
    }
  })();

  entry.stateReloadInFlight = request;
  try {
    return await request;
  } finally {
    if (entry.stateReloadInFlight === request) entry.stateReloadInFlight = null;
  }
}

function entryActorProjection(entry = state.activeConversation) {
  if (!entry) return null;
  if (!entry.actorProjection || (!entry.actorProjection.actorId && entry.actorId)) {
    entry.actorProjection = createActorProjection(entry.actorId || null);
  }
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
    return [source.tool.serviceSlug || source.tool.serviceId, source.tool.toolName]
      .filter(Boolean).join(" · ") || "工具";
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
      item.append(el("span", "", step.description || step.stepId || "Actor step"));
      const detail = actorStepEvidenceDetail(step);
      if (detail) item.append(el("small", "", detail));
      group.append(item);
    }
    recovery.append(group);
  }
  return recovery.childElementCount ? recovery : null;
}

// The gate status is actor-owned. An absent status is rendered as unknown rather than as
// satisfied, so a decoder that has not seen the fact never implies the plan may run.
function actorPlanGateStatus(task) {
  const status = String(task?.gate?.status || "").toLowerCase();
  return status === "pending" || status === "satisfied" || status === "rejected" ? status : "";
}

function actorPlanGateCopy(status) {
  return {
    pending: "待确认",
    satisfied: "已确认",
    rejected: "已拒绝",
  }[status] || "需确认";
}

// A plan gate decision is an Aevatar-scoped local admission, not a NyxID authorization: it
// admits the already-communicated plan and never grants service access, approves a tool
// request, or proves an external effect.
function actorPendingPlanGate(projection) {
  const gate = projection?.task?.gate;
  if (!gate || actorPlanGateStatus(projection.task) !== "pending") return null;
  const identified = (value) => typeof value === "string" && value.trim().length > 0;
  if (!identified(gate.requestId) || !identified(projection.task.taskId)) return null;
  return gate;
}

function needsYouKey(kind, requestId) {
  return `${kind}:${requestId}`;
}

function pruneNeedsYouState(entry, projection) {
  const activeKeys = new Set();
  if (projection.pendingInput?.requestId) {
    activeKeys.add(needsYouKey("input", projection.pendingInput.requestId));
  }
  if (projection.pendingApproval?.approvalRequestId) {
    activeKeys.add(needsYouKey("approval", projection.pendingApproval.approvalRequestId));
  }
  const pendingGate = actorPendingPlanGate(projection);
  if (pendingGate) activeKeys.add(needsYouKey("plan", pendingGate.requestId));
  for (const key of entry.needsYouDrafts.keys()) {
    if (!activeKeys.has(key)) entry.needsYouDrafts.delete(key);
  }
  for (const key of entry.needsYouSubmissions.keys()) {
    if (!activeKeys.has(key)) entry.needsYouSubmissions.delete(key);
  }
  if (!projection.pendingApproval ||
      entry.approvalConfirmRequestId !== projection.pendingApproval.approvalRequestId) {
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

  const key = needsYouKey("input", pending.requestId);
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
  const key = needsYouKey("input", pending.requestId);
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

function renderPlanGateDecision(entry, projection) {
  const gate = actorPendingPlanGate(projection);
  if (!gate) return null;
  const requestId = gate.requestId;
  const key = needsYouKey("plan", requestId);
  const submission = entry.needsYouSubmissions.get(key);
  const locked = submission?.status === "pending" || submission?.status === "accepted";
  const reliableVersion = Number.isSafeInteger(projection.stateVersion) && projection.stateVersion > 0;
  const section = el("section", "needs-you-panel plan-gate-required");
  section.dataset.requestId = requestId;
  const heading = el("div", "needs-you-heading");
  heading.append(iconNode("list-checks"), el("strong", "", "需要你确认计划"));
  section.append(heading);

  section.append(el(
    "p",
    "needs-you-boundary",
    "确认后 Actor 才会执行已说明的计划。这是 Aevatar 本地准入，不授予 NyxID 访问权限，" +
    "也不代表外部变更已经发生。",
  ));
  if (gate.reason) section.append(el("p", "actor-plan-gate-reason", gate.reason));

  const facts = el("dl", "approval-facts");
  const appendFact = (label, value) => {
    if (!value) return;
    facts.append(el("dt", "", label), el("dd", "", value));
  };
  appendFact("计划", gate.planId || projection.task.planId);
  appendFact("修订", Number.isSafeInteger(gate.planRevision) && gate.planRevision > 0
    ? `Revision ${gate.planRevision}`
    : "");
  // Admissions are the exact operations this decision admits. Naming them keeps the
  // confirmation specific instead of a blanket "go ahead".
  const admissions = Array.isArray(gate.admissions) ? gate.admissions : [];
  appendFact("准入操作", admissions.length ? String(admissions.length) : "");
  if (facts.childElementCount) section.append(facts);

  for (const admission of admissions.slice(0, 8)) {
    const label = admission?.toolName || admission?.stepId;
    if (!label) continue;
    section.append(el("div", "actor-plan-admission mono", label));
  }

  const footer = el("div", "needs-you-actions");
  const confirm = el("button", "needs-you-primary", "确认执行");
  confirm.type = "button";
  confirm.disabled = locked || !reliableVersion;
  confirm.addEventListener("click", () => void submitPlanGateDecision(entry, gate, true));
  const reject = el("button", "needs-you-secondary", "拒绝");
  reject.type = "button";
  reject.disabled = locked || !reliableVersion;
  reject.addEventListener("click", () => void submitPlanGateDecision(entry, gate, false));
  const status = el("span", `needs-you-state ${submission?.status || ""}`,
    submission?.message || (!reliableVersion ? "正在同步 Actor 状态…" : ""));
  footer.append(confirm, reject, status);
  section.append(footer);
  // Free-text objection is steering, not a gate decision: the composer already routes a
  // message during an active task to task.steer, which re-plans instead of admitting.
  section.append(el(
    "p",
    "needs-you-hint",
    "想改计划就直接发消息，Actor 会按调整重新规划。",
  ));
  return section;
}

function submitPlanGateDecision(entry, gate, confirmed) {
  return submitNeedsYouDecision(entry, "plan", gate.requestId, {
    type: "plan.resolve",
    confirmed,
    taskId: entryActorProjection(entry)?.task?.taskId,
    planId: gate.planId,
    planRevision: gate.planRevision,
  });
}

function renderPendingApproval(entry, projection) {
  const pending = projection.pendingApproval;
  if (!pending?.approvalRequestId) return null;
  const requestId = pending.approvalRequestId;
  const key = needsYouKey("approval", requestId);
  const draft = entry.needsYouDrafts.get(key) || { reason: "" };
  entry.needsYouDrafts.set(key, draft);
  const submission = entry.needsYouSubmissions.get(key);
  const locked = submission?.status === "pending" || submission?.status === "accepted";
  const reliableVersion = Number.isSafeInteger(projection.stateVersion) && projection.stateVersion > 0;
  const outsideGrant = pending.grantBoundary !== "within_grant";
  const irreversible = pending.reversibility === "irreversible";
  const confirming = irreversible && entry.approvalConfirmRequestId === requestId;
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
  approve.disabled = locked || !reliableVersion;
  approve.addEventListener("click", () => {
    if (irreversible && !confirming) {
      entry.approvalConfirmRequestId = requestId;
      renderActorProjection(entry);
      return;
    }
    void submitNeedsYouDecision(entry, "approval", requestId, {
      type: "approval.resolve",
      approved: true,
      reason: draft.reason.trim(),
    });
  });
  const reject = el("button", "needs-you-secondary", "拒绝");
  reject.type = "button";
  reject.disabled = locked || !reliableVersion;
  reject.addEventListener("click", () => void submitNeedsYouDecision(entry, "approval", requestId, {
    type: "approval.resolve",
    approved: false,
    reason: draft.reason.trim(),
  }));
  const status = el("span", `needs-you-state ${submission?.status || ""}`,
    submission?.message || (!reliableVersion ? "正在同步 Actor 状态…" : ""));
  footer.append(approve, reject, status);
  section.append(footer);
  return section;
}

async function submitNeedsYouDecision(entry, kind, requestId, payload) {
  const projection = entryActorProjection(entry);
  if (!entry?.actorId || !projection || !requestId ||
      !Number.isSafeInteger(projection.stateVersion) || projection.stateVersion <= 0) return false;
  const key = needsYouKey(kind, requestId);
  const existing = entry.needsYouSubmissions.get(key);
  if (existing?.status === "pending" || existing?.status === "accepted") return false;
  const clientRequestId = createId(`client-${kind}`);
  entry.needsYouSubmissions.set(key, { status: "pending", message: "正在提交…" });
  renderActorProjection(entry);
  try {
    const response = await fetch("/api/demo/chat", {
      method: "POST",
      headers: demoHeaders(),
      body: JSON.stringify({
        surface: "nyxid-chat",
        ...payload,
        conversationId: entry.actorId,
        requestId,
        clientRequestId,
        expectedStateVersion: projection.stateVersion,
      }),
    });
    if (!response.ok) throw await responseError(response);
    await response.json().catch(() => null);
    entry.needsYouSubmissions.set(key, {
      status: "accepted",
      message: "已受理，等待 Actor 确认。",
    });
    renderActorProjection(entry);
    await refreshActorState(entry);
    if (entry.actorStateRefreshTimer) window.clearTimeout(entry.actorStateRefreshTimer);
    entry.actorStateRefreshTimer = window.setTimeout(() => {
      entry.actorStateRefreshTimer = null;
      void refreshActorState(entry);
      void loadConversations({ silent: true });
    }, 500);
    return true;
  } catch (error) {
    entry.needsYouSubmissions.set(key, {
      status: "error",
      message: `提交失败：${String(error?.message || "unknown error").slice(0, 240)}`,
    });
    renderActorProjection(entry);
    await refreshActorState(entry, { uncursored: true });
    return false;
  }
}

function renderActorProjection(entry) {
  if (!entry?.thread) return;
  const projection = entry.actorProjection || createActorProjection(entry.actorId);
  const hasProjection = Boolean(
    projection.task || projection.pendingInput || projection.pendingApproval ||
    projection.actions.size || projection.conflicts.length || entry.actorStateNotice,
  );
  if (!hasProjection) {
    entry.actorTaskElement?.remove();
    entry.actorTaskElement = null;
    if (entry === state.activeConversation) {
      renderComposerInputRequest(entry, projection);
      renderInspector();
    }
    return;
  }

  const root = entry.actorTaskElement || el("section", "actor-task");
  entry.actorTaskElement = root;
  if (root.dataset.collapsed !== "true" && root.dataset.collapsed !== "false") {
    root.dataset.collapsed = "false";
  }
  root.replaceChildren();
  const task = projection.task;
  const status = String(
    task?.status || projection.taskStatus || projection.latestTurn?.status || "unknown",
  ).toLowerCase();
  root.className = `actor-task ${status}`;
  root.classList.toggle("collapsed", root.dataset.collapsed === "true");
  root.dataset.actorId = projection.actorId || entry.actorId || "";
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
      `${actorStatusCopy(currentStep.status)} · ${currentStep.description || currentStep.stepId}`,
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
    const gateMode = String(task.gate?.mode || "auto").toLowerCase();
    const gateStatus = actorPlanGateStatus(task);
    const gate = el(
      "span",
      `actor-plan-gate ${gateMode}${gateStatus ? ` ${gateStatus}` : ""}`,
      gateMode === "confirm" ? actorPlanGateCopy(gateStatus) : "自动执行",
    );
    if (task.gate?.reason) gate.title = task.gate.reason;
    meta.append(gate);
    root.append(meta);
    if (task.gate?.reason) root.append(el("p", "actor-plan-gate-reason", task.gate.reason));
  }

  pruneNeedsYouState(entry, projection);
  // The plan gate is the "may I start" decision, so it precedes the per-step decisions.
  const planGate = renderPlanGateDecision(entry, projection);
  const pendingInput = renderPendingInput(entry, projection);
  const pendingApproval = renderPendingApproval(entry, projection);
  if (planGate) root.append(planGate);
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
        el("strong", "", step.description || step.stepId || "Actor step"),
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
  if (entry.actorStateNotice) {
    root.append(el("div", "actor-state-notice", entry.actorStateNotice));
  }
  if (entry.actorControlReceipt) {
    const receipt = entry.actorControlReceipt;
    root.append(el(
      "div",
      `actor-control-receipt ${receipt.status === "error" ? "actor-control-error" : ""}`,
      receipt.message,
    ));
  }
  mountActorTask(entry.thread, root);
  if (entry === state.activeConversation) {
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
  const entry = state.activeConversation;
  const projection = entryActorProjection(entry);
  if (!entry?.actorId || !projection) return;
  if (kind === "stop" && !actorCan(projection, "stop")) return;
  if ((kind === "retry" || kind === "skip") && !actorCan(projection, kind, step?.stepId)) return;
  if (kind === "steer" && !String(instruction || "").trim()) return;

  const turnId = actorControlTurnId(projection);
  if (!turnId || !Number.isSafeInteger(projection.stateVersion) || projection.stateVersion < 0) {
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
      expectedStateVersion: projection.stateVersion,
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
      expectedStateVersion: projection.stateVersion,
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
      expectedStateVersion: projection.stateVersion,
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
  const key = needsYouKey("input", pending.requestId);
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
    dom.composerStatus.textContent = "完成必需的运行准备后，将继续这条请求。";
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

function handleFrame(raw) {
  const event = normalizeFrame(raw);
  recordEvent(event, raw);
  switch (event.type) {
    case "run_context":
      state.run.context = { ...state.run.context, ...pickContext(event) };
      updateRunProgress("运行上下文已建立，Agent 正在分析请求…");
      break;
    case "run_started":
      state.run.context.actorId = event.actorId || event.threadId || state.run.context.actorId;
      state.run.context.runId = event.runId || state.run.context.runId;
      state.run.context.turnId = event.turnId || state.run.context.turnId;
      state.actorId = state.run.surface === "nyxid-chat"
        ? state.run.context.actorId || state.actorId
        : state.actorId;
      if (state.run.surface === "nyxid-chat") {
        const owner = conversationContext || state.activeConversation;
        if (owner) {
          owner.actorId = state.actorId;
          entryActorProjection(owner);
        }
        renderHistoryList();
        scheduleHistoryRefresh();
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
      const projection = entryActorProjection(entry);
      entry.actorProjection = reduceActorEvent(projection, event);
      if (event.type === "action_request" && event.actionRequest) {
        const action = entry.actorProjection.actions.get(event.actionRequest.actionRequestId);
        if (action?.conflicted) {
          invalidateActionRequestCache(entry, event.actionRequest.actionRequestId);
        } else if (action?.request) {
          cacheActionRequest(entry, action.request);
        }
      }
      if (event.type === "approval_requested") {
        state.run.approvalCard?.card?.remove();
        state.run.approvalCard = null;
        state.run.pendingApproval = null;
      }
      renderActorProjection(entry);
      renderActionCards(entry);
      if (entry === state.activeConversation) renderActiveConversationState();
      if (["input_requested", "input_changed", "approval_requested", "approval_changed"].includes(event.type)) {
        if (entry.actorStateRefreshTimer) window.clearTimeout(entry.actorStateRefreshTimer);
        entry.actorStateRefreshTimer = window.setTimeout(() => {
          entry.actorStateRefreshTimer = null;
          void refreshActorState(entry);
          void loadConversations({ silent: true });
        }, 300);
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
  renderInspector();
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
  renderEventLog();
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
  const label = el("span", "", "Run");
  const status = el("span", "", "Running");
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
  const name = event.toolName || "tool";
  const card = ensureActivityCard();
  const row = el("div", "tool-row");
  row.dataset.toolCallId = id;
  const stateIcon = el("span", "tool-state-icon");
  const icon = document.createElement("i");
  icon.dataset.lucide = "loader-circle";
  stateIcon.append(icon);
  const copy = el("div", "tool-copy");
  copy.append(el("strong", "", name), el("small", "", "Running"));
  const duration = el("span", "tool-duration", "…");
  row.append(stateIcon, copy, duration);
  card.append(row);
  state.run.tools.set(id, {
    id,
    name,
    status: "running",
    startedAt: Date.now(),
    row,
    copy: copy.querySelector("small"),
    duration,
  });
  startStep(name, "tool", id);
  updateActivityProgress();
  if (/ornn_search_skills|use_skill/i.test(name)) {
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
  state.run.activityStatus.textContent =
    `${done} / ${tools.length} steps${done === tools.length ? " · complete" : ""}`;
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
  resolved.copy.textContent = summarizeToolResult(event.result || event.error);
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
  if (/ornn_search_skills|use_skill/i.test(resolved.name)) {
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
    else state.run.activityStatus.textContent = status === "done" ? "Complete" : "Ended";
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
      argumentsJson: call.argumentsJson,
    });
    finishTool({
      toolCallId: call.callId,
      toolName: call.toolName,
      result: receipt?.resultJson,
      error: receipt?.errorMessage || receipt?.errorCode,
      status: receipt?.status,
      success: receipt ? !/(ERROR|DENIED)/i.test(String(receipt.status || "")) : true,
    });
  }

  for (const receipt of receipts) {
    if (calls.some((call) => call.callId === receipt.callId)) continue;
    addTool({ toolCallId: receipt.callId, toolName: receipt.toolName });
    finishTool({
      toolCallId: receipt.callId,
      toolName: receipt.toolName,
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
  const toolName = event.toolName || "workflow continuation";
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

function renderInspector() {
  if (!isActiveConversationContext()) return;
  const context = state.run.context;
  const projection = entryActorProjection(state.activeConversation);
  const isNyxid = state.config.surface === "nyxid-chat";
  const actorTurnId = projection?.activeTurn?.turnId || projection?.latestTurn?.turnId ||
    projection?.task?.turnId || context.turnId;
  dom.actorFact.textContent = context.actorId || state.actorId || "—";
  dom.runFact.textContent = context.runId || "—";
  dom.commandFact.textContent = context.commandId || "—";
  dom.runIdentityLabel.textContent = isNyxid ? "Turn" : "Session";
  dom.runIdentityFact.textContent = isNyxid
    ? actorTurnId || "—"
    : context.workflowSessionId || state.workflowSessionId;
  dom.usageTokens.textContent = state.run.usage?.totalTokens ?? "—";
  const model = state.run.usage?.model || state.currentConversationMeta?.llmModel;
  const hasConversationData = Boolean(state.run.startedAt || state.currentConversationMeta);
  dom.usageModel.textContent = model || (hasConversationData ? "not reported" : "—");
  renderSteps();
  renderActorControlUi();
  updateElapsed();
}

function renderActorControlUi() {
  if (!isActiveConversationContext()) return;
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
    const reliableVersion = Number.isSafeInteger(projection?.stateVersion) && projection.stateVersion > 0;
    const hasAnswer = Boolean(
      dom.promptInput.value.trim() || pendingInput.draft.selectedOptionIds.size,
    );
    dom.sendButton.classList.remove("hidden");
    dom.sendButton.disabled = !state.auth.authenticated || locked || !reliableVersion || !hasAnswer;
    dom.sendButton.setAttribute("aria-label", "提交回答");
    dom.sendButton.title = "提交回答";
    dom.steerButton.classList.add("hidden");
    dom.stopButton.classList.toggle("hidden", !authoritativeStop);
    dom.stopButton.disabled = false;
    dom.promptInput.disabled = !state.auth.authenticated;
    dom.attachButton.disabled = true;
    dom.composerServicesButton.disabled = true;
    dom.observationDisconnectButton.classList.toggle("hidden", !state.activeController);
    dom.composerStatus.textContent = locked
      ? submission.message || "回答已受理，等待 Actor 确认。"
      : reliableVersion
        ? "一次回答全部缺口；提交后 Actor 将继续当前任务"
        : "正在同步 Actor 状态…";
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
    dom.composerStatus.textContent = "当前任务执行中；输入内容将作为 steering 指令提交";
  }
  dom.observationDisconnectButton.classList.toggle("hidden", !state.activeController);
}

function renderSteps() {
  dom.stepList.replaceChildren();
  const projection = entryActorProjection(state.activeConversation);
  const actorSteps = state.config.surface === "nyxid-chat" && projection?.task
    ? [...projection.steps.values()]
    : null;
  const steps = actorSteps || Array.from(state.run.steps.values());
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
      dom.stepList.textContent = labels[state.run.status] || "没有可展示的工具步骤";
    } else {
      dom.stepList.textContent = state.run.status === "idle"
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
    const name = el("strong", "", step.description || step.name || step.stepId || "Actor step");
    if (actorSteps) {
      row.append(dot, name, el("small", "", `${step.status || "unknown"} · ${step.externalEffect || "no effect fact"}`));
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
  dom.eventCount.textContent = String(state.run.events.length);
  dom.eventList.replaceChildren();
  if (!state.run.events.length) {
    dom.eventList.append(el("div", "event-empty", "尚无事件"));
    return;
  }
  for (const event of [...state.run.events].reverse()) {
    const details = el("details", "event-row");
    const summary = document.createElement("summary");
    summary.append(
      el("span", "mono", `#${String(event.id).padStart(3, "0")}`),
      el("span", "event-kind", event.type),
      el("span", "mono", event.at.toLocaleTimeString("zh-CN", { hour12: false })),
    );
    details.append(summary, el("pre", "", safeJson(event.raw)));
    dom.eventList.append(details);
  }
}

function clearEvents() {
  state.run.events = [];
  renderEventLog();
}

function configureWireInspector() {
  const enabled = state.config.enableStudioWireInspector === true && state.auth.authenticated;
  dom.eventsTabButton?.classList.toggle("hidden", !enabled);
  dom.eventsTabButton?.setAttribute("aria-hidden", String(!enabled));
  if (!enabled) setInspectorTab("run");
}

function updateElapsed() {
  const start = state.run.startedAt;
  if (!start) {
    dom.usageElapsed.textContent = "00:00";
    return;
  }
  const end = state.run.completedAt || Date.now();
  const seconds = Math.max(0, Math.floor((end - start) / 1000));
  dom.usageElapsed.textContent = `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
}

function setRunStatus(status, label) {
  if (!isActiveConversationContext()) return;
  dom.runStatus.className = `run-status ${status}`;
  dom.runStatus.querySelector("strong").textContent = label;
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
  dom.composerStatus.textContent = running
    ? "正在接收生产 Agent 输出 · 停止接收不会撤销已提交操作"
    : state.auth.authenticated
      ? "生产环境 · 使用当前账户的 services，高风险操作需要确认"
      : "登录后使用当前账户已配置的 services";
  renderActorControlUi();
}

function cancelRun() {
  if (!state.activeController) return;
  dom.stopButton.disabled = true;
  dom.composerStatus.textContent = "正在停止当前页面接收…";
  abortConversationRun(state.activeConversation);
}

function cancelObservation() {
  if (!state.activeController) return;
  dom.observationDisconnectButton.disabled = true;
  dom.composerStatus.textContent = "正在停止当前页面观察…";
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
