import "./transport.js";
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
} from "./protocol.js";
import {
  buildConnectCardBlock,
  connectCardSteps,
  connectorInitial,
  splitMessageSegments,
} from "./blocks.js";
import {
  actorCan,
  applyCurrentStateResult,
  createActorProjection,
  reduceActorEvent,
  restoreCachedAction,
} from "./actor-state.js";

const PREFERENCES_KEY = "aevatar-studio:assistant-preferences:v4";
const THEME_KEY = "aevatar-studio:assistant-theme";
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
  assistantNavButton: $("#assistantNavButton"),
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
  composerServiceCount: $("#composerServiceCount"),
  composerServiceList: $("#composerServiceList"),
  composerServicePanel: $("#composerServicePanel"),
  composerServicesButton: $("#composerServicesButton"),
  composerStatus: $("#composerStatus"),
  connectionButton: $("#connectionButton"),
  connectionDot: $("#connectionDot"),
  connectionTest: $("#connectionTest"),
  connectionText: $("#connectionText"),
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
  openSettingsNav: $("#openSettingsNav"),
  observationDisconnectButton: $("#observationDisconnectButton"),
  promptInput: $("#promptInput"),
  quickActions: $("#quickActions"),
  refreshComposerServicesButton: $("#refreshComposerServicesButton"),
  recentGroup: $("#recentGroup"),
  recentSessionsList: $("#recentSessionsList"),
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
  themeButton: $("#themeButton"),
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
    directBaseUrl: "https://aevatar-console-backend-api.aevatar.ai",
    proxyBaseUrl: "https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar",
    ornnWebUrl: "https://ornn.chrono-ai.fun",
    nyxidWebUrl: "https://nyx.chrono-ai.fun",
    servicesUrl: "https://nyx.chrono-ai.fun/keys",
    scopeId: "",
    workflow: "direct",
  },
  auth: { authenticated: false, user: null, resources: [] },
  services: [],
  connectors: { connected: [], available: [], loadedAt: 0 },
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
  applyTheme(readStorage(THEME_KEY) || "dark");
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
    void sendPrompt();
  });
  dom.promptInput.addEventListener("input", () => {
    if (state.activeConversation) state.activeConversation.draft = dom.promptInput.value;
    autoResizeComposer();
    renderActorControlUi();
  });
  dom.promptInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      void sendPrompt();
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
  dom.assistantNavButton.addEventListener("click", focusCurrentConversation);
  dom.currentSessionButton.addEventListener("click", focusCurrentConversation);
  dom.settingsButton.addEventListener("click", openSettings);
  dom.openSettingsNav.addEventListener("click", openSettings);
  dom.servicesButton.addEventListener("click", openSettings);
  dom.composerServicesButton.addEventListener("click", toggleComposerServices);
  dom.closeComposerServicesButton.addEventListener("click", closeComposerServices);
  dom.refreshComposerServicesButton.addEventListener("click", () => void loadServices());
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
  dom.themeButton.addEventListener("click", toggleTheme);
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
  try {
    const response = await fetch("/api/auth/session", { cache: "no-store" });
    const payload = await response.json();
    state.auth = payload.authenticated
      ? payload
      : { authenticated: false, user: null, resources: [] };
    state.config.scopeId = payload.scopeId || "";
    if (state.auth.authenticated && includeServices) await loadServices();
    if (!state.auth.authenticated) {
      state.services = [];
      state.connectors = { connected: [], available: [], loadedAt: 0 };
    }
  } catch {
    state.auth = { authenticated: false, user: null, resources: [] };
    state.services = [];
    state.connectors = { connected: [], available: [], loadedAt: 0 };
    state.config.scopeId = "";
  }
  renderAuthUi();
  return state.auth;
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

function renderActionCards(entry = conversationContext || state.activeConversation) {
  if (!entry) return;
  const projection = entry.actorProjection;
  if (!projection?.actions?.size && !entry.run.cardElements.size) return;
  const container = entry.run.actionCardsElement || el("div", "action-card-list");
  entry.run.actionCardsElement = container;

  for (const action of projection?.actions?.values?.() || []) {
    if (action.action !== "service.connect" || !action.request) continue;
    let card = entry.run.cardElements.get(action.actionRequestId);
    if (!card) {
      card = createConnectCard(action, { conversation: entry });
      if (!card) continue;
      entry.run.cardElements.set(action.actionRequestId, card);
    } else {
      card.action = action;
      card.request = action.request;
      if (action.conflicted) {
        card.status = "conflicted";
        card.error = "Action identity conflict；该 browser journey 已禁用。";
      } else {
        applyActorActionProof(card, action, projection);
      }
      renderConnectCard(card);
    }
  }

  container.replaceChildren(...[...entry.run.cardElements.values()].map((card) => card.root));
  if (!container.isConnected) ensureAssistantBody().append(container);
  scrollThread();
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
  const base = state.config.nyxidWebUrl || "https://nyx.chrono-ai.fun";
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
    renderConnectCard(card);
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
      renderConnectCard(card);
    });
  } catch (error) {
    withConversationState(conversation, () => {
      card.busy = false;
      card.status = "error";
      card.error = error.name === "AbortError"
        ? "已停止观察 continuation；Actor 可能仍在处理报告。"
        : error.message || "Action continuation 提交失败。";
      renderConnectCard(card);
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

  const steps = el("div", "cc-steps");
  const journeyReported = ["reporting", "awaiting_verification", "reported", "verified"]
    .includes(card.status);
  const verified = card.status === "verified";
  block.steps.forEach((step, index) => {
    const done = index < 2 ? journeyReported : verified;
    const active = index === (journeyReported ? 2 : 0) && !verified;
    const row = el("div", `cc-step${done ? " done" : active ? " active" : ""}`);
    row.append(el("span", "cc-num", done ? "" : String(index + 1)));
    const body = el("div", "cc-step-body");
    body.append(el("div", "cc-step-title", step.title), el("div", "cc-step-desc", step.body));
    if (index === 0 && !journeyReported) body.append(renderConnectCardActions(card));
    if (index === 2 && journeyReported) {
      const success = el("div", "cc-success");
      success.classList.toggle("pending", !verified);
      success.append(iconNode(verified ? "check" : "loader-circle"), el(
        "span",
        "",
        verified ? card.note || "已验证" : card.note || "等待 actor 验证",
      ));
      body.append(success);
    }
    row.append(body);
    steps.append(row);
  });
  card.root.append(steps);

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
    state.config.scopeId = "";
    state.health = null;
    closeSettings();
    newChat({ refreshHistory: false });
    for (const entry of Array.from(state.conversationStates.values())) {
      if (entry !== state.activeConversation) removeConversationState(entry);
    }
    renderAuthUi();
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
  dom.sidebarSurface.textContent = surface;
  dom.sidebarTransport.textContent = transport;
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
  dom.sidebarRuntimeDot.className = `status-dot ${status}`;
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
  const recent = state.conversations;
  if (!recent.length) {
    dom.recentSessionsList.append(el("div", "history-empty", "暂无其他生产会话"));
    return;
  }
  for (const conversation of recent) {
    const row = el("div", `history-row${conversation.id === state.activeConversation?.actorId ? " active" : ""}`);
    const open = el("button", "history-session");
    open.type = "button";
    open.title = conversation.title;
    const copy = el("span", "history-session-copy");
    const conversationState = findConversationState(conversation.id);
    const running = Boolean(conversationState?.controller);
    copy.append(
      el("strong", "", conversation.title),
      el("small", "", `${conversation.messageCount} 条消息 · ${formatHistoryTime(conversation.updatedAt)}` +
        (conversationState?.run.pendingApproval ? " · 待确认" : running ? " · 运行中" : "")),
    );
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
    dom.promptInput.focus();
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
      dom.promptInput.focus();
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
    dom.promptInput.focus();
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
      const result = applyCurrentStateResult(projection, await response.json());
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

function renderActorProjection(entry) {
  if (!entry?.thread) return;
  const projection = entry.actorProjection || createActorProjection(entry.actorId);
  const hasProjection = Boolean(
    projection.task || projection.actions.size || projection.conflicts.length || entry.actorStateNotice,
  );
  if (!hasProjection) {
    entry.actorTaskElement?.remove();
    entry.actorTaskElement = null;
    if (entry === state.activeConversation) renderInspector();
    return;
  }

  const root = entry.actorTaskElement || el("section", "actor-task");
  entry.actorTaskElement = root;
  root.replaceChildren();
  const task = projection.task;
  const status = String(task?.status || projection.latestTurn?.status || "unknown").toLowerCase();
  root.className = `actor-task ${status}`;
  root.dataset.actorId = projection.actorId || entry.actorId || "";
  if (task?.taskId) root.dataset.taskId = task.taskId;
  if (task?.turnId) root.dataset.turnId = task.turnId;

  const header = el("header", "actor-task-header");
  const title = el("div", "actor-task-title");
  title.append(
    el("span", "actor-task-eyebrow", "Actor task"),
    el("strong", "", actorStatusCopy(status)),
  );
  header.append(title, el("span", `actor-task-status ${status}`, status));
  root.append(header);

  if (task?.safeMessage) root.append(el("p", "actor-task-message", task.safeMessage));
  if (task) {
    const steps = el("div", "actor-steps");
    for (const step of projection.steps.values()) {
      const stepStatus = String(step.status || "unknown").toLowerCase();
      const row = el("article", `actor-step ${stepStatus}`);
      row.dataset.stepId = step.stepId || "";
      const copy = el("div", "actor-step-copy");
      copy.append(
        el("strong", "", step.description || step.stepId || "Actor step"),
        el("small", "", `${actorStatusCopy(stepStatus)} · ${step.kind || "step"}`),
      );
      const facts = el("div", "actor-step-facts");
      if (step.externalEffect) {
        facts.append(el(
          "span",
          `actor-effect ${String(step.externalEffect).replaceAll("_", "-")}`,
          `effect: ${step.externalEffect}`,
        ));
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
      if (actorCan(projection, "retry", step.stepId)) {
        const retry = el("button", "actor-retry", "重试");
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
  if (!root.isConnected) entry.thread.append(root);
  if (entry === state.activeConversation) renderInspector();
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
  setConversationTitle(entry.title || entry.meta?.title || "新会话");
  setRunningUi(running);
  const actorStatus = actorTerminalRunStatus(entry.actorProjection);
  const status = entry.run.pendingApproval ? "running" : actorStatus || entry.run.status;
  const labels = {
    idle: "Ready",
    running: entry.run.pendingApproval ? "Approval" : "Running",
    complete: "Complete",
    blocked: "Blocked",
    error: "Error",
    stopped: "Stopped",
    closed: "Closed",
  };
  setRunStatus(status, labels[status] || "Idle");
  dom.sidebarSessionMeta.textContent = entry.run.pendingApproval
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

  const conversation = state.activeConversation;
  state.run = createRunState();
  state.run.status = "running";
  state.run.surface = state.config.surface;
  state.run.config = configPayload(state.config);
  state.run.startedAt = Date.now();
  state.run.request = { prompt, attachment };
  state.run.clientRequestId = createId("client-text");
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
      renderActorProjection(entry);
      renderActionCards(entry);
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
  const header = el("div", "activity-header");
  const icon = document.createElement("i");
  icon.dataset.lucide = "workflow";
  const label = el("span", "", "Run");
  const status = el("span", "", "Running");
  header.append(icon, label, status);
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
  const canSteer = state.auth.authenticated && actorActive;
  dom.stopButton.classList.toggle("hidden", nyxid ? !authoritativeStop : !state.activeController);
  dom.stopButton.disabled = false;
  dom.stopButton.setAttribute("aria-label", nyxid ? "停止 Actor 任务" : "停止接收");
  dom.stopButton.title = nyxid ? "向 Actor 提交停止命令" : "停止接收";
  dom.steerButton.classList.toggle("hidden", !actorActive);
  dom.steerButton.disabled = !canSteer || !dom.promptInput.value.trim();
  dom.promptInput.disabled = !state.auth.authenticated || (Boolean(state.activeController) && !canSteer);
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
  if (!isActiveConversationContext()) return;
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
  dom.openSettingsNav.disabled = false;
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
  const events = tab === "events";
  dom.runPanel.classList.toggle("hidden", events);
  dom.eventsPanel.classList.toggle("hidden", !events);
  dom.runTabButton.classList.toggle("active", !events);
  dom.eventsTabButton.classList.toggle("active", events);
  dom.runTabButton.setAttribute("aria-selected", String(!events));
  dom.eventsTabButton.setAttribute("aria-selected", String(events));
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

function toggleTheme() {
  const next = document.documentElement.dataset.theme === "light" ? "dark" : "light";
  applyTheme(next);
  writeStorage(THEME_KEY, next);
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme === "light" ? "light" : "dark";
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
