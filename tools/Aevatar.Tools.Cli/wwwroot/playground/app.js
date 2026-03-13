(() => {
  "use strict";

  // ── State ──
  let workflows = [];
  let primitives = [];
  let llmStatus = { available: false };
  let selectedWorkflow = null;
  let selectedWorkflowMeta = null;
  let selectedPrimitive = null;
  let yamlViewerSource = "workflow";
  let workflowDef = null;
  let stepStates = {};
  let turingMachineState = {};
  let eventSource = null;
  let pendingInteraction = null;
  let wfLogEntries = [];
  let wfThinkingBuffers = new Map();
  let pgLogEntries = [];
  let pgThinkingBuffers = new Map();
  let persistStateTimer = null;
  let persistenceHooksBound = false;
  let workflowGroupCollapsed = {};
  let appSidebarCollapsed = false;
  let workflowPlanCollapsed = false;
  const UI_STATE_STORAGE_KEY = "aevatar.workflow.web.ui.v1";
  const UI_STATE_STORAGE_VERSION = 1;
  const PG_MODAL_KEYS = new Set(["save", "log", "result", "interaction"]);

  const TYPE_COLORS = {
    transform: "#3b82f6", guard: "#f97316", conditional: "#f59e0b", switch: "#f59e0b",
    while: "#f59e0b", llm_call: "#a855f7", parallel: "#22c55e", parallel_fanout: "#22c55e",
    race: "#14b8a6", map_reduce: "#84cc16", foreach: "#84cc16", for_each: "#84cc16",
    evaluate: "#ec4899", reflect: "#ec4899", cache: "#06b6d4", assign: "#94a3b8",
    retrieve_facts: "#3b82f6", emit: "#f43f5e", delay: "#f59e0b", checkpoint: "#6366f1",
    wait_signal: "#f59e0b", human_approval: "#f97316", human_input: "#f97316", secure_input: "#dc2626",
    workflow_call: "#6366f1", vote_consensus: "#22c55e", tool_call: "#a855f7",
    connector_call: "#14b8a6", secure_connector_call: "#0f766e", workflow_loop: "#64748b",
  };
  const WORKFLOW_GROUP_ORDER = [
    "your-workflows",
    "starter-workflows",
    "ai-workflows",
    "integration-workflows",
    "advanced-patterns",
  ];
  const WORKFLOW_GROUP_LABELS = {
    "your-workflows": "Your Workflows",
    "starter-workflows": "Starter Workflows",
    "ai-workflows": "AI & Human Workflows",
    "integration-workflows": "Integrations & Tools",
    "advanced-patterns": "Advanced Patterns",
  };
  const WORKFLOW_GROUP_DEFAULT_EXPANDED = new Set([
    "your-workflows",
    "starter-workflows",
    "ai-workflows",
  ]);
  const PRIMITIVE_CATEGORY_ORDER = [
    "data",
    "control",
    "composition",
    "ai",
    "human",
    "integration",
    "general",
  ];
  const PRIMITIVE_CATEGORY_LABELS = {
    data: "Data & State",
    control: "Control Flow",
    composition: "Composition",
    ai: "AI Reasoning",
    human: "Human in the Loop",
    integration: "Integrations",
    general: "Runtime",
  };
  const NAV_VIEWS = new Set(["overview", "workflows", "yaml", "primitives", "playground"]);

  // ── DOM refs ──
  const $ = (sel) => document.querySelector(sel);
  const $$ = (sel) => document.querySelectorAll(sel);

  // ── Init ──
  document.addEventListener("DOMContentLoaded", async () => {
    const startupChatPrompt = consumeStartupChatPrompt();
    const persistedState = readPersistedUiState();
    setupNavigation();
    await Promise.all([loadWorkflows(), loadLlmStatus(), loadPrimitives()]);
    bindStaticActions();
    if (persistedState) {
      await restoreUiState(persistedState);
    } else {
      activateNavView("playground");
      scheduleUiStatePersist();
    }
    await applyStartupChatPrompt(startupChatPrompt);
  });

  function setupNavigation() {
    $$(".nav-btn").forEach((btn) => {
      btn.addEventListener("click", () => {
        $$(".nav-btn").forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        const view = normalizeNavView(btn.dataset.view);
        applyNavView(view);
        scheduleUiStatePersist();
      });
    });
    $("#app-sidebar-toggle")?.addEventListener("click", () => {
      setAppSidebarCollapsed(!appSidebarCollapsed);
    });
    setAppSidebarCollapsed(false, { force: true, skipPersist: true });
    setupWorkflowPlanSectionToggle();
    $("#btn-run").addEventListener("click", runWorkflow);
    $("#btn-reset").addEventListener("click", resetExecution);
    $("#config-open")?.addEventListener("click", () => void openConfigUi());
    $("#btn-open-yaml")?.addEventListener("click", () => {
      yamlViewerSource = "workflow";
      renderYamlBrowser();
      activateNavView("yaml");
    });
    $("#yaml-use-workflow")?.addEventListener("click", () => {
      yamlViewerSource = "workflow";
      renderYamlBrowser();
    });
    $("#yaml-use-playground")?.addEventListener("click", () => {
      yamlViewerSource = "playground";
      renderYamlBrowser();
    });
    $("#playground-open-yaml")?.addEventListener("click", () => {
      yamlViewerSource = pgCurrentYaml ? "playground" : "workflow";
      renderYamlBrowser();
      activateNavView("yaml");
    });
    setupPlayground();
    setupStatePersistence();
  }

  async function openConfigUi() {
    const button = $("#config-open");
    if (button) button.disabled = true;
    try {
      const payload = await postJson("/api/app/config/open", {});
      const targetUrl = String(payload?.configUrl || "").trim();
      if (!targetUrl)
        throw new Error("Config UI URL is missing in response.");
      window.location.assign(targetUrl);
    } catch (error) {
      const message = error?.message || String(error);
      window.alert(message);
    } finally {
      if (button) button.disabled = false;
    }
  }

  function consumeStartupChatPrompt() {
    const params = new URLSearchParams(window.location.search || "");
    const prompt = String(params.get("chat") ?? params.get("prompt") ?? "").trim();
    if (!prompt) return "";

    params.delete("chat");
    params.delete("prompt");

    const query = params.toString();
    const cleanedUrl = `${window.location.pathname}${query ? `?${query}` : ""}${window.location.hash || ""}`;
    try {
      window.history.replaceState(window.history.state, "", cleanedUrl);
    } catch {
      // Ignore history API issues; prompt handling should still proceed.
    }

    return prompt;
  }

  async function applyStartupChatPrompt(prompt) {
    if (!prompt) return;
    activateNavView("playground");

    const input = $("#pg-input");
    if (!input) return;
    input.value = prompt;
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
    await pgSend();
  }

  function bindStaticActions() {
    bindNavAction("#overview-go-workflows", "workflows");
    bindNavAction("#overview-go-primitives", "primitives");
    bindNavAction("#overview-go-playground", "playground");
    bindNavAction("#workflows-go-playground", "playground");
    bindNavAction("#primitives-go-workflows", "workflows");
    bindNavAction("#primitives-go-playground", "playground");
    bindNavAction("#workflows-go-yaml", "yaml");
  }

  function bindNavAction(selector, view) {
    const el = $(selector);
    if (!el) return;
    el.addEventListener("click", () => activateNavView(view));
  }

  function normalizeNavView(view) {
    const normalized = String(view || "").trim().toLowerCase();
    return NAV_VIEWS.has(normalized) ? normalized : "overview";
  }

  function applyNavView(view) {
    updateSidebarSections(view);
    if (view === "overview") {
      renderOverviewPage();
      showView("overview");
      return;
    }

    if (view === "workflows") {
      renderWorkflowHome();
      showView(selectedWorkflow ? "workflow" : "workflows-home");
      return;
    }

    if (view === "yaml") {
      renderYamlBrowser();
      showView("yaml");
      return;
    }

    if (view === "primitives") {
      renderPrimitivesHome();
      showView(selectedPrimitive ? "primitive" : "primitives-home");
      return;
    }

    showView("playground");
  }

  function updateSidebarSections(view) {
    const normalized = normalizeNavView(view);
    const showWorkflowSidebar = normalized === "overview" || normalized === "workflows" || normalized === "yaml";
    $("#sidebar-workflows")?.classList.toggle("hidden", !showWorkflowSidebar);
    $("#sidebar-primitives")?.classList.toggle("hidden", normalized !== "primitives");
  }

  function setAppSidebarCollapsed(collapsed, options = {}) {
    const force = options.force === true;
    const next = collapsed === true;
    if (!force && next === appSidebarCollapsed) return;
    appSidebarCollapsed = next;

    $("#app")?.classList.toggle("sidebar-collapsed", appSidebarCollapsed);
    const toggleBtn = $("#app-sidebar-toggle");
    if (toggleBtn) {
      const collapsed = appSidebarCollapsed === true;
      toggleBtn.textContent = collapsed ? "›" : "‹";
      toggleBtn.setAttribute("aria-expanded", String(!appSidebarCollapsed));
      toggleBtn.setAttribute("aria-label", collapsed ? "Expand sidebar" : "Collapse sidebar");
      toggleBtn.title = collapsed ? "Expand sidebar" : "Collapse sidebar";
      toggleBtn.classList.toggle("is-active", collapsed);
    }

    if (options.skipPersist !== true)
      scheduleUiStatePersist();
  }

  function setupWorkflowPlanSectionToggle() {
    const toggle = $("#wf-plan-toggle");
    if (!toggle) return;
    toggle.addEventListener("click", () => {
      setWorkflowPlanCollapsed(!workflowPlanCollapsed);
    });
    setWorkflowPlanCollapsed(false, { force: true, skipPersist: true });
  }

  function setWorkflowPlanCollapsed(collapsed, options = {}) {
    const force = options.force === true;
    const next = collapsed === true;
    if (!force && next === workflowPlanCollapsed) return;
    workflowPlanCollapsed = next;

    $("#plan-section")?.classList.toggle("is-collapsed", workflowPlanCollapsed);
    $("#wf-plan-body")?.classList.toggle("hidden", workflowPlanCollapsed);
    const toggle = $("#wf-plan-toggle");
    if (toggle) {
      const expanded = workflowPlanCollapsed !== true;
      toggle.setAttribute("aria-expanded", String(expanded));
      toggle.setAttribute("aria-label", expanded ? "Collapse workflow plan" : "Expand workflow plan");
      toggle.title = expanded ? "Collapse workflow plan" : "Expand workflow plan";
    }

    if (options.skipPersist !== true)
      scheduleUiStatePersist();
  }

  // ── Data Loading ──
  async function loadWorkflows() {
    try {
      workflows = await fetchWorkflowCatalog();
      renderWorkflowList();
      renderOverviewPage();
      renderWorkflowHome();
      renderYamlBrowser();
    } catch (e) { console.error("Failed to load workflows", e); }
  }

  async function loadLlmStatus() {
    try {
      const res = await fetch("/api/llm/status");
      llmStatus = await res.json();
      renderLlmStatus();
      renderOverviewPage();
    } catch (e) { console.error("Failed to load LLM status", e); }
  }

  async function loadPrimitives() {
    try {
      const res = await fetch("/api/primitives");
      primitives = await res.json();
      renderPrimitivesList();
      renderOverviewPage();
      renderPrimitivesHome();
    } catch (e) { console.error("Failed to load primitives", e); }
  }

  async function fetchWorkflowCatalog() {
    const endpoints = ["/api/workflow-catalog", "/api/workflows"];
    let lastError = null;

    for (const endpoint of endpoints) {
      try {
        const res = await fetch(endpoint);
        if (!res.ok) {
          lastError = new Error(`HTTP ${res.status} for ${endpoint}`);
          continue;
        }

        const payload = await res.json();
        return normalizeWorkflowCatalog(payload);
      } catch (error) {
        lastError = error;
      }
    }

    throw lastError || new Error("Failed to fetch workflow catalog");
  }

  function normalizeWorkflowCatalog(payload) {
    if (!Array.isArray(payload)) return [];

    if (payload.every((item) => typeof item === "string")) {
      return payload.map((name) => ({
        name,
        description: "",
        category: "deterministic",
        group: "starter-workflows",
        groupLabel: WORKFLOW_GROUP_LABELS["starter-workflows"],
        sortOrder: 10_000,
        source: "builtin",
        sourceLabel: "Built-in",
        primitives: [],
        showInLibrary: true,
      }));
    }

    return payload
      .filter((item) => item && typeof item === "object")
      .map((item) => ({
        name: String(item.name || item.Name || ""),
        description: String(item.description || item.Description || ""),
        category: String(item.category || item.Category || "deterministic"),
        group: String(item.group || item.Group || ""),
        groupLabel: String(item.groupLabel || item.GroupLabel || ""),
        sortOrder: Number.isFinite(Number(item.sortOrder || item.SortOrder)) ? Number(item.sortOrder || item.SortOrder) : 10_000,
        source: String(item.source || item.Source || ""),
        sourceLabel: String(item.sourceLabel || item.SourceLabel || getWorkflowSourceLabel(item.source || item.Source || "")),
        primitives: Array.isArray(item.primitives || item.Primitives) ? (item.primitives || item.Primitives).map((value) => String(value)) : [],
        defaultInput: String(item.defaultInput || item.DefaultInput || ""),
        showInLibrary: item.showInLibrary !== false && item.ShowInLibrary !== false,
        isPrimitiveExample: item.isPrimitiveExample === true || item.IsPrimitiveExample === true,
      }))
      .filter((item) => item.name.length > 0);
  }

  function getVisibleWorkflows() {
    return workflows.filter((item) => item.showInLibrary !== false);
  }

  function getVisiblePrimitives() {
    return primitives.filter((item) => item.name !== "workflow_loop");
  }

  function groupWorkflows(items = getVisibleWorkflows()) {
    const grouped = new Map();
    for (const wf of items) {
      const fallbackGroup = wf.category === "llm" ? "ai-workflows" : "starter-workflows";
      const groupKey = wf.group || fallbackGroup;
      const groupLabel = wf.groupLabel || WORKFLOW_GROUP_LABELS[groupKey] || "Other";
      if (!grouped.has(groupKey)) {
        grouped.set(groupKey, { key: groupKey, label: groupLabel, items: [] });
      }
      grouped.get(groupKey).items.push(wf);
    }

    return Array.from(grouped.values())
      .map((group) => ({
        ...group,
        items: group.items.sort((a, b) => {
          const leftOrder = Number.isFinite(a.sortOrder) ? a.sortOrder : 10_000;
          const rightOrder = Number.isFinite(b.sortOrder) ? b.sortOrder : 10_000;
          return leftOrder - rightOrder || a.name.localeCompare(b.name);
        }),
      }))
      .sort((left, right) => {
        const leftIdx = WORKFLOW_GROUP_ORDER.indexOf(left.key);
        const rightIdx = WORKFLOW_GROUP_ORDER.indexOf(right.key);
        const leftOrder = leftIdx === -1 ? 999 : leftIdx;
        const rightOrder = rightIdx === -1 ? 999 : rightIdx;
        return leftOrder - rightOrder || left.key.localeCompare(right.key);
      });
  }

  function groupPrimitives(items = getVisiblePrimitives()) {
    const grouped = new Map();
    for (const primitive of items) {
      const category = primitive.category || "general";
      if (!grouped.has(category)) {
        grouped.set(category, []);
      }
      grouped.get(category).push(primitive);
    }

    return Array.from(grouped.entries())
      .map(([key, values]) => ({
        key,
        label: PRIMITIVE_CATEGORY_LABELS[key] || key,
        items: values.sort((left, right) => left.name.localeCompare(right.name)),
      }))
      .sort((left, right) => {
        const leftIdx = PRIMITIVE_CATEGORY_ORDER.indexOf(left.key);
        const rightIdx = PRIMITIVE_CATEGORY_ORDER.indexOf(right.key);
        const leftOrder = leftIdx === -1 ? 999 : leftIdx;
        const rightOrder = rightIdx === -1 ? 999 : rightIdx;
        return leftOrder - rightOrder || left.key.localeCompare(right.key);
      });
  }

  function getFeaturedWorkflows(limit = 6) {
    return getVisibleWorkflows()
      .slice()
      .sort((a, b) => {
        const leftGroup = WORKFLOW_GROUP_ORDER.indexOf(a.group || "");
        const rightGroup = WORKFLOW_GROUP_ORDER.indexOf(b.group || "");
        const leftOrder = leftGroup === -1 ? 999 : leftGroup;
        const rightOrder = rightGroup === -1 ? 999 : rightGroup;
        const leftSort = Number.isFinite(a.sortOrder) ? a.sortOrder : 10_000;
        const rightSort = Number.isFinite(b.sortOrder) ? b.sortOrder : 10_000;
        return leftOrder - rightOrder || leftSort - rightSort || a.name.localeCompare(b.name);
      })
      .slice(0, limit);
  }

  function getFeaturedPrimitives(limit = 6) {
    return getVisiblePrimitives()
      .slice()
      .sort((left, right) => {
        const leftCategory = PRIMITIVE_CATEGORY_ORDER.indexOf(left.category || "");
        const rightCategory = PRIMITIVE_CATEGORY_ORDER.indexOf(right.category || "");
        const leftOrder = leftCategory === -1 ? 999 : leftCategory;
        const rightOrder = rightCategory === -1 ? 999 : rightCategory;
        const leftExamples = Array.isArray(left.exampleWorkflows) ? left.exampleWorkflows.length : 0;
        const rightExamples = Array.isArray(right.exampleWorkflows) ? right.exampleWorkflows.length : 0;
        return leftOrder - rightOrder || rightExamples - leftExamples || left.name.localeCompare(right.name);
      })
      .slice(0, limit);
  }

  // ── Render: Sidebar ──
  function renderWorkflowList() {
    const groupsContainer = $("#workflow-groups");
    if (!groupsContainer) return;
    groupsContainer.innerHTML = "";
    const grouped = groupWorkflows();

    const nextCollapsedState = {};
    for (const group of grouped) {
      const groupKey = group.key;
      const sortedItems = group.items;
      const hasActiveWorkflow = sortedItems.some((item) => item.name === selectedWorkflow);
      let collapsed = workflowGroupCollapsed[groupKey];
      if (typeof collapsed !== "boolean")
        collapsed = !WORKFLOW_GROUP_DEFAULT_EXPANDED.has(groupKey);
      if (hasActiveWorkflow)
        collapsed = false;
      nextCollapsedState[groupKey] = collapsed;

      const section = document.createElement("section");
      section.className = "workflow-group";
      section.dataset.group = groupKey;
      section.classList.toggle("collapsed", collapsed);

      const header = document.createElement("button");
      header.type = "button";
      header.className = "workflow-group-toggle";
      header.setAttribute("aria-expanded", String(!collapsed));

      const title = document.createElement("span");
      title.className = "workflow-group-title";
      title.textContent = group.label;

      const count = document.createElement("span");
      count.className = "workflow-group-count";
      count.textContent = String(sortedItems.length);

      const chevron = document.createElement("span");
      chevron.className = "workflow-group-chevron";
      chevron.textContent = "▾";

      header.appendChild(title);
      header.appendChild(count);
      header.appendChild(chevron);
      header.addEventListener("click", () => {
        const collapsedNow = section.classList.toggle("collapsed");
        workflowGroupCollapsed[groupKey] = collapsedNow;
        header.setAttribute("aria-expanded", String(!collapsedNow));
        scheduleUiStatePersist();
      });

      const list = document.createElement("ul");
      list.className = "workflow-list";

      for (const wf of sortedItems) {
        const li = document.createElement("li");
        li.dataset.name = wf.name;
        li.classList.toggle("active", selectedWorkflow === wf.name);
        li.innerHTML = `
          <div class="wf-title-row">
            <span class="wf-name" title="${esc(wf.name)}">${esc(wf.name)}</span>
            ${wf.sourceLabel ? `<span class="wf-badge">${esc(wf.sourceLabel)}</span>` : ""}
          </div>
          <div class="wf-primitives">
            ${wf.primitives.map((p) => `<span class="prim-dot" style="background:${TYPE_COLORS[p] || "#64748b"}" title="${p}"></span>`).join("")}
          </div>`;
        li.addEventListener("click", () => handleWorkflowSidebarSelection(wf.name));
        list.appendChild(li);
      }

      section.appendChild(header);
      section.appendChild(list);
      groupsContainer.appendChild(section);
    }
    workflowGroupCollapsed = nextCollapsedState;
  }

  function handleWorkflowSidebarSelection(name) {
    const activeView = getActiveNavView();
    if (activeView === "yaml") {
      selectWorkflow(name, { preferredView: "yaml" });
      return;
    }

    activateNavView("workflows");
    selectWorkflow(name, { preferredView: "workflows" });
  }

  function renderLlmStatus() {
    const el = $("#llm-status");
    if (llmStatus.available) {
      el.className = "llm-badge available";
      el.textContent = `LLM: ${llmStatus.provider}/${llmStatus.model}`;
    } else {
      el.className = "llm-badge unavailable";
      el.textContent = "LLM: not configured";
    }
  }

  function renderPrimitivesList() {
    const ul = $("#primitives-list");
    ul.innerHTML = "";

    const visiblePrimitives = primitives.filter((p) => p.name !== "workflow_loop");
    const grouped = new Map();
    for (const primitive of visiblePrimitives) {
      const category = primitive.category || "general";
      if (!grouped.has(category))
        grouped.set(category, []);
      grouped.get(category).push(primitive);
    }

    const orderedCategories = Array.from(grouped.keys()).sort((left, right) => {
      const leftIdx = PRIMITIVE_CATEGORY_ORDER.indexOf(left);
      const rightIdx = PRIMITIVE_CATEGORY_ORDER.indexOf(right);
      const leftOrder = leftIdx === -1 ? 999 : leftIdx;
      const rightOrder = rightIdx === -1 ? 999 : rightIdx;
      return leftOrder - rightOrder || left.localeCompare(right);
    });

    for (const category of orderedCategories) {
      const heading = document.createElement("li");
      heading.className = "primitives-group-label";
      heading.textContent = PRIMITIVE_CATEGORY_LABELS[category] || category;
      ul.appendChild(heading);

      const items = grouped.get(category)
        .sort((left, right) => left.name.localeCompare(right.name));

      for (const p of items) {
        const li = document.createElement("li");
        li.classList.toggle("active", selectedPrimitive === p.name);
        const exampleCount = Array.isArray(p.exampleWorkflows) ? p.exampleWorkflows.length : 0;
        li.innerHTML = `
          <span class="prim-color" style="background:${TYPE_COLORS[p.name] || "#64748b"}"></span>
          <span class="prim-info">
            <div class="prim-row">
              <div class="prim-label">${esc(p.name)}</div>
              <div class="prim-count">${exampleCount > 0 ? `${exampleCount} example${exampleCount > 1 ? "s" : ""}` : "reference"}</div>
            </div>
            <div class="prim-cat">${esc(PRIMITIVE_CATEGORY_LABELS[p.category] || p.category)}</div>
          </span>`;
        li.addEventListener("click", () => showPrimitiveDetail(p));
        ul.appendChild(li);
      }
    }
  }

  function renderOverviewPage() {
    const statsEl = $("#overview-stats");
    const workflowGroupsEl = $("#overview-workflow-groups");
    const primitiveGroupsEl = $("#overview-primitive-groups");
    const featuredEl = $("#overview-featured-workflows");
    if (!statsEl || !workflowGroupsEl || !primitiveGroupsEl || !featuredEl) return;

    const visibleWorkflows = getVisibleWorkflows();
    const workflowGroups = groupWorkflows(visibleWorkflows);
    const visiblePrimitives = getVisiblePrimitives();
    const primitiveGroups = groupPrimitives(visiblePrimitives);

    statsEl.innerHTML = [
      renderStatCard(String(visibleWorkflows.length), "Workflows"),
      renderStatCard(String(workflowGroups.length), "Workflow Groups"),
      renderStatCard(String(visiblePrimitives.length), "Primitives"),
      renderStatCard(llmStatus.available ? "LLM ready" : "LLM needed", "Playground"),
    ].join("");

    workflowGroupsEl.innerHTML = workflowGroups
      .map((group) => renderGroupCard(
        group.label,
        `${group.items.length} workflow${group.items.length === 1 ? "" : "s"}`,
        group.items.slice(0, 3).map((item) => item.name),
        "workflow",
        group.items[0]?.name || ""))
      .join("");

    primitiveGroupsEl.innerHTML = primitiveGroups
      .map((group) => renderGroupCard(
        group.label,
        `${group.items.length} primitive${group.items.length === 1 ? "" : "s"}`,
        group.items.slice(0, 3).map((item) => item.name),
        "primitive",
        group.items[0]?.name || ""))
      .join("");

    featuredEl.innerHTML = getFeaturedWorkflows(6)
      .map((workflow) => renderWorkflowFeatureCard(workflow))
      .join("");

    bindDynamicActionButtons($("#view-overview"));
  }

  function renderWorkflowHome() {
    const groupsEl = $("#workflows-home-groups");
    const featuredEl = $("#workflows-home-featured");
    if (!groupsEl || !featuredEl) return;

    const grouped = groupWorkflows();
    groupsEl.innerHTML = grouped.map((group) => renderWorkflowLibraryGroupCard(group)).join("");
    featuredEl.innerHTML = getFeaturedWorkflows(8)
      .map((workflow) => renderWorkflowFeatureCard(workflow, { compact: false }))
      .join("");
    bindDynamicActionButtons($("#view-workflows-home"));
  }

  function renderPrimitivesHome() {
    const groupsEl = $("#primitives-home-groups");
    const featuredEl = $("#primitives-home-featured");
    if (!groupsEl || !featuredEl) return;

    groupsEl.innerHTML = groupPrimitives()
      .map((group) => renderPrimitiveCategoryCard(group))
      .join("");
    featuredEl.innerHTML = getFeaturedPrimitives(8)
      .map((primitive) => renderPrimitiveFeatureCard(primitive))
      .join("");
    bindDynamicActionButtons($("#view-primitives-home"));
  }

  function renderStatCard(value, label) {
    return `
      <article class="stat-card">
        <div class="stat-value">${esc(value)}</div>
        <div class="stat-label">${esc(label)}</div>
      </article>`;
  }

  function renderGroupCard(title, meta, highlights, actionType, actionValue) {
    const actionAttr = actionType === "workflow"
      ? `data-open-workflow="${esc(actionValue)}"`
      : `data-open-primitive="${esc(actionValue)}"`;
    return `
      <article class="info-card">
        <div class="info-card-title">${esc(title)}</div>
        <div class="info-card-meta">${esc(meta)}</div>
        <div class="tag-list">${highlights.map((item) => `<span class="tag">${esc(item)}</span>`).join("")}</div>
        ${actionValue ? `<button type="button" class="btn btn-ghost info-card-action" ${actionAttr}>Open ${actionType === "workflow" ? "Workflow" : "Primitive"}</button>` : ""}
      </article>`;
  }

  function renderWorkflowLibraryGroupCard(group) {
    const examples = group.items.slice(0, 4);
    return `
      <article class="library-card">
        <div class="library-card-head">
          <div>
            <div class="library-card-title">${esc(group.label)}</div>
          </div>
          <div class="library-card-count">${group.items.length}</div>
        </div>
        <div class="library-card-list">
          ${examples.map((workflow) => `
            <button type="button" class="library-list-item" data-open-workflow="${esc(workflow.name)}">
              <span>${esc(workflow.name)}</span>
              <span class="library-list-meta">${esc(workflow.sourceLabel || "")}</span>
            </button>`).join("")}
        </div>
      </article>`;
  }

  function renderWorkflowFeatureCard(workflow) {
    return `
      <article class="feature-card">
        <div class="feature-card-head">
          <div>
            <div class="feature-card-title">${esc(workflow.name)}</div>
            <div class="feature-card-subtitle">${esc(workflow.groupLabel || WORKFLOW_GROUP_LABELS[workflow.group] || "Workflow")}</div>
          </div>
          ${workflow.sourceLabel ? `<span class="wf-badge">${esc(workflow.sourceLabel)}</span>` : ""}
        </div>
        <div class="tag-list">${(workflow.primitives || []).slice(0, 5).map((name) => `<span class="tag">${esc(name)}</span>`).join("")}</div>
        <div class="feature-card-actions">
          <button type="button" class="btn btn-ghost" data-open-workflow="${esc(workflow.name)}">Open Workflow</button>
          <button type="button" class="btn btn-secondary" data-open-workflow-yaml="${esc(workflow.name)}">View YAML</button>
        </div>
      </article>`;
  }

  function renderPrimitiveCategoryCard(group) {
    const examples = group.items.slice(0, 4);
    return `
      <article class="library-card">
        <div class="library-card-head">
          <div>
            <div class="library-card-title">${esc(group.label)}</div>
          </div>
          <div class="library-card-count">${group.items.length}</div>
        </div>
        <div class="library-card-list">
          ${examples.map((primitive) => `
            <button type="button" class="library-list-item" data-open-primitive="${esc(primitive.name)}">
              <span>${esc(primitive.name)}</span>
              <span class="library-list-meta">${Array.isArray(primitive.exampleWorkflows) ? `${primitive.exampleWorkflows.length} examples` : "reference"}</span>
            </button>`).join("")}
        </div>
      </article>`;
  }

  function renderPrimitiveFeatureCard(primitive) {
    const categoryLabel = PRIMITIVE_CATEGORY_LABELS[primitive.category] || primitive.category || "Primitive";
    return `
      <article class="feature-card">
        <div class="feature-card-head">
          <div>
            <div class="feature-card-title">${esc(primitive.name)}</div>
            <div class="feature-card-subtitle">${esc(categoryLabel)}</div>
          </div>
          <span class="feature-card-chip">${Array.isArray(primitive.exampleWorkflows) ? `${primitive.exampleWorkflows.length} examples` : "Reference"}</span>
        </div>
        <div class="tag-list">${(primitive.aliases || []).slice(0, 4).map((name) => `<span class="tag">${esc(name)}</span>`).join("")}</div>
        <div class="feature-card-actions">
          <button type="button" class="btn btn-ghost" data-open-primitive="${esc(primitive.name)}">Open Primitive</button>
        </div>
      </article>`;
  }

  function bindDynamicActionButtons(root) {
    if (!root) return;

    root.querySelectorAll("[data-open-workflow]").forEach((button) => {
      button.addEventListener("click", async () => {
        const name = button.getAttribute("data-open-workflow");
        if (!name) return;
        activateNavView("workflows");
        await selectWorkflow(name, { preferredView: "workflows" });
      });
    });

    root.querySelectorAll("[data-open-workflow-yaml]").forEach((button) => {
      button.addEventListener("click", async () => {
        const name = button.getAttribute("data-open-workflow-yaml");
        if (!name) return;
        yamlViewerSource = "workflow";
        activateNavView("yaml");
        await selectWorkflow(name, { preferredView: "yaml" });
      });
    });

    root.querySelectorAll("[data-open-primitive]").forEach((button) => {
      button.addEventListener("click", () => {
        const name = button.getAttribute("data-open-primitive");
        const primitive = primitives.find((item) => item.name === name);
        if (!primitive) return;
        activateNavView("primitives");
        showPrimitiveDetail(primitive);
      });
    });
  }

  function setWorkflowDescription(text) {
    const description = String(text || "").trim();
    const descriptionEl = $("#wf-description");
    const descriptionWrapEl = $("#wf-description-wrap");
    if (descriptionEl)
      descriptionEl.textContent = description;
    if (descriptionWrapEl)
      descriptionWrapEl.classList.toggle("hidden", description.length === 0);
  }

  // ── Select Workflow ──
  async function selectWorkflow(name, options = {}) {
    stopExecution();
    const preferredView = options.preferredView || "workflows";
    selectedWorkflow = name;
    selectedWorkflowMeta = workflows.find((w) => w.name === name) || null;
    yamlViewerSource = "workflow";
    workflowDef = null;
    if (selectedWorkflowMeta?.group)
      workflowGroupCollapsed[selectedWorkflowMeta.group] = false;
    renderWorkflowList();
    renderOverviewPage();
    renderWorkflowHome();
    renderYamlSource("");
    renderYamlBrowser();

    if (preferredView === "workflows")
      showView("workflow");
    $("#btn-run").disabled = true;
    $("#wf-name").textContent = name;
    setWorkflowDescription("");

    try {
      const res = await fetch(`/api/workflows/${name}`);
      workflowDef = await res.json();
      if (workflowDef?.catalog && typeof workflowDef.catalog === "object") {
        selectedWorkflowMeta = {
          ...(selectedWorkflowMeta || {}),
          ...workflowDef.catalog,
          defaultInput: selectedWorkflowMeta?.defaultInput || "",
        };
        renderWorkflowList();
      }
      const def = workflowDef.definition;

      setWorkflowDescription(def.description || "");

      $("#wf-input").value = selectedWorkflowMeta?.defaultInput || "";
      renderTuringDemoPanel(selectedWorkflowMeta);
      renderWorkflowPlan(selectedWorkflowMeta, workflowDef);
      resetTuringMachineState();

      if (def.roles && def.roles.length > 0) {
        $("#roles-section").classList.remove("hidden");
        renderWorkflowConfig(def.configuration);
        renderRoleCards(def.roles);
      } else {
        $("#workflow-config").classList.add("hidden");
        $("#roles-section").classList.add("hidden");
      }

      renderYamlSource(workflowDef.yaml);
      resetExecution();
      renderFlowDiagram();
      renderYamlBrowser();

      $("#btn-run").disabled = workflowRequiresLlmProvider(selectedWorkflowMeta, workflowDef) && !llmStatus.available;
      if (preferredView === "yaml")
        showView("yaml");
      else if (preferredView === "workflows")
        showView("workflow");
      scheduleUiStatePersist();
    } catch (e) {
      console.error("Failed to load workflow", e);
      setWorkflowDescription("Failed to load workflow definition.");
      renderTuringDemoPanel(null);
      renderWorkflowPlan(null, null);
      renderYamlBrowser();
      scheduleUiStatePersist();
    }
  }

  // ── Views ──
  function showView(name) {
    $("#view-overview").classList.toggle("hidden", name !== "overview");
    $("#view-workflows-home").classList.toggle("hidden", name !== "workflows-home");
    $("#view-workflow").classList.toggle("hidden", name !== "workflow");
    $("#view-yaml").classList.toggle("hidden", name !== "yaml");
    $("#view-primitives-home").classList.toggle("hidden", name !== "primitives-home");
    $("#view-primitive-detail").classList.toggle("hidden", name !== "primitive");
    $("#view-playground").classList.toggle("hidden", name !== "playground");
  }

  function showPrimitiveDetail(p, options = {}) {
    selectedPrimitive = p.name;
    renderPrimitivesList();
    renderOverviewPage();
    renderPrimitivesHome();
    if (options.show !== false)
      showView("primitive");
    $("#prim-name").textContent = p.name;
    $("#prim-description").textContent = p.description;
    const aliases = Array.isArray(p.aliases) ? p.aliases : [];
    $("#prim-aliases").innerHTML = aliases.map((a) => `<span class="tag">${a}</span>`).join("");
    const cat = $("#prim-category");
    cat.textContent = PRIMITIVE_CATEGORY_LABELS[p.category] || p.category;
    cat.className = `category-badge cat-${p.category}`;

    const section = $("#prim-params-section");
    const tbody = $("#prim-params tbody");
    if (p.parameters && p.parameters.length > 0) {
      section.classList.remove("hidden");
      tbody.innerHTML = p.parameters.map((pm) => {
        const vals = pm.values
          ? pm.values.split(",").map((v) => `<span class="val-chip">${esc(v.trim())}</span>`).join(" ")
          : "";
        return `<tr>
          <td class="param-name">${esc(pm.name)}</td>
          <td class="param-desc">${esc(pm.description)}</td>
          <td class="param-default">${pm.default ? esc(pm.default) : ""}</td>
          <td class="param-values">${vals}</td>
        </tr>`;
      }).join("");
    } else {
      section.classList.add("hidden");
      tbody.innerHTML = "";
    }

    const examplesSection = $("#prim-examples-section");
    const examplesContainer = $("#prim-examples");
    const examples = Array.isArray(p.exampleWorkflows) ? p.exampleWorkflows : [];
    if (examples.length > 0) {
      examplesSection.classList.remove("hidden");
      examplesContainer.innerHTML = examples.map((example, index) => `
        <div class="prim-example-card" data-example-index="${index}">
          <div class="prim-example-head">
            <div class="prim-example-name">${esc(example.name || "")}</div>
            <div class="prim-example-kind">${esc(example.kindLabel || "Workflow")}</div>
          </div>
          ${example.description ? `<div class="prim-example-desc">${esc(example.description)}</div>` : ""}
          <div class="prim-example-actions">
            <button type="button" class="btn btn-ghost prim-example-open">Open Workflow</button>
          </div>
        </div>`).join("");

      examplesContainer.querySelectorAll(".prim-example-open").forEach((button, index) => {
        button.addEventListener("click", async () => {
          activateNavView("workflows");
          await selectWorkflow(examples[index].name, { preferredView: "workflows" });
        });
      });
    } else {
      examplesSection.classList.add("hidden");
      examplesContainer.innerHTML = "";
    }
    scheduleUiStatePersist();
  }

  // ── Flow Diagram (SVG) ──
  function renderFlowDiagram() {
    if (!workflowDef) return;
    renderFlowInto($("#flow-container"), workflowDef.definition.steps, workflowDef.edges, stepStates);
  }

  function renderFlowInto(container, steps, edges, states) {
    if (!steps || steps.length === 0) {
      container.innerHTML = '<p style="color:var(--text-muted)">No steps</p>';
      return;
    }

    const NODE_W = 180, NODE_H = 56, GAP_Y = 40, GAP_X = 30, PAD = 40;

    // Layout: detect branching steps
    const layout = computeLayout(steps, edges, NODE_W, NODE_H, GAP_X, GAP_Y);
    const svgW = layout.width + PAD * 2;
    const svgH = layout.height + PAD * 2;

    const ns = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("width", svgW);
    svg.setAttribute("height", svgH);
    svg.setAttribute("viewBox", `0 0 ${svgW} ${svgH}`);

    // Arrowhead marker
    const defs = document.createElementNS(ns, "defs");
    defs.innerHTML = `<marker id="arrowhead" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0, 8 3, 0 6" fill="#64748b"/>
    </marker>`;
    svg.appendChild(defs);

    // Draw edges
    for (const e of edges) {
      const fromPos = layout.positions[e.from];
      const toPos = layout.positions[e.to];
      if (!fromPos || !toPos) continue;

      const x1 = fromPos.x + NODE_W / 2 + PAD;
      const y1 = fromPos.y + NODE_H + PAD;
      const x2 = toPos.x + NODE_W / 2 + PAD;
      const y2 = toPos.y + PAD;

      const path = document.createElementNS(ns, "path");
      if (Math.abs(x1 - x2) < 2) {
        path.setAttribute("d", `M${x1},${y1} L${x2},${y2}`);
      } else {
        const midY = (y1 + y2) / 2;
        path.setAttribute("d", `M${x1},${y1} C${x1},${midY} ${x2},${midY} ${x2},${y2}`);
      }
      path.setAttribute("class", "edge-line");
      svg.appendChild(path);

      if (e.label && e.label !== "child") {
        const lx = (x1 + x2) / 2;
        const ly = (y1 + y2) / 2 - 4;
        const text = document.createElementNS(ns, "text");
        text.setAttribute("x", lx);
        text.setAttribute("y", ly);
        text.setAttribute("class", "edge-label");
        text.setAttribute("text-anchor", "middle");
        text.textContent = e.label;
        svg.appendChild(text);
      }
    }

    // Draw nodes
    for (const step of steps) {
      const pos = layout.positions[step.id];
      if (!pos) continue;
      const g = document.createElementNS(ns, "g");
      g.setAttribute("class", `step-node state-${(states && states[step.id]) || "pending"}`);
      g.setAttribute("data-step", step.id);
      g.setAttribute("transform", `translate(${pos.x + PAD}, ${pos.y + PAD})`);

      const color = TYPE_COLORS[step.type] || "#64748b";
      const rect = document.createElementNS(ns, "rect");
      rect.setAttribute("width", NODE_W);
      rect.setAttribute("height", NODE_H);
      rect.setAttribute("fill", `${color}22`);
      rect.setAttribute("stroke", color);
      g.appendChild(rect);

      const label = document.createElementNS(ns, "text");
      label.setAttribute("x", 12);
      label.setAttribute("y", 22);
      label.setAttribute("class", "step-label");
      label.textContent = step.id;
      g.appendChild(label);

      const badge = document.createElementNS(ns, "text");
      badge.setAttribute("x", 12);
      badge.setAttribute("y", 40);
      badge.setAttribute("class", "step-type-badge");
      badge.textContent = step.type;
      g.appendChild(badge);

      if (step.targetRole) {
        const role = document.createElementNS(ns, "text");
        role.setAttribute("x", NODE_W - 8);
        role.setAttribute("y", 40);
        role.setAttribute("class", "step-role");
        role.setAttribute("text-anchor", "end");
        role.textContent = `@${step.targetRole}`;
        g.appendChild(role);
      }

      const status = document.createElementNS(ns, "text");
      status.setAttribute("x", NODE_W - 8);
      status.setAttribute("y", 20);
      status.setAttribute("class", "step-status");
      status.setAttribute("text-anchor", "end");
      status.setAttribute("font-size", "14");
      const state = states && states[step.id];
      if (state === "completed") status.textContent = "\u2713";
      else if (state === "failed") status.textContent = "\u2717";
      else if (state === "running") status.textContent = "\u25CB";
      else status.textContent = "";
      g.appendChild(status);

      svg.appendChild(g);
    }

    // Children (sub-steps for parallel, etc.)
    for (const step of steps) {
      if (!step.children || step.children.length === 0) continue;
      const parentPos = layout.positions[step.id];
      if (!parentPos) continue;
      for (const child of step.children) {
        const childPos = layout.positions[child.id];
        if (!childPos) continue;
        const g = document.createElementNS(ns, "g");
        g.setAttribute("class", `step-node state-${(states && states[child.id]) || "pending"}`);
        g.setAttribute("data-step", child.id);
        g.setAttribute("transform", `translate(${childPos.x + PAD}, ${childPos.y + PAD})`);

        const color = TYPE_COLORS[child.type] || "#64748b";
        const rect = document.createElementNS(ns, "rect");
        rect.setAttribute("width", NODE_W);
        rect.setAttribute("height", NODE_H);
        rect.setAttribute("fill", `${color}22`);
        rect.setAttribute("stroke", color);
        rect.setAttribute("stroke-dasharray", "4,2");
        g.appendChild(rect);

        const label = document.createElementNS(ns, "text");
        label.setAttribute("x", 12);
        label.setAttribute("y", 22);
        label.setAttribute("class", "step-label");
        label.textContent = child.id;
        g.appendChild(label);

        const badge = document.createElementNS(ns, "text");
        badge.setAttribute("x", 12);
        badge.setAttribute("y", 40);
        badge.setAttribute("class", "step-type-badge");
        badge.textContent = child.type;
        g.appendChild(badge);

        svg.appendChild(g);
      }
    }

    container.innerHTML = "";
    container.appendChild(svg);
  }

  function computeLayout(steps, edges, nodeW, nodeH, gapX, gapY) {
    const positions = {};
    const branchEdges = {};
    let currentY = 0;

    for (const e of edges) {
      if (e.label && e.label !== "child") {
        if (!branchEdges[e.from]) branchEdges[e.from] = [];
        branchEdges[e.from].push(e);
      }
    }

    // Identify branch target steps (steps that are targets of labeled edges)
    const branchTargets = new Set();
    for (const from in branchEdges) {
      for (const e of branchEdges[from]) branchTargets.add(e.to);
    }

    // Identify merge points (steps with next:"done" or similar convergence)
    const mergeTargets = new Set();
    for (const s of steps) {
      if (s.next && branchTargets.has(s.id)) mergeTargets.add(s.next);
    }

    let maxWidth = nodeW;
    const placed = new Set();

    for (let i = 0; i < steps.length; i++) {
      const step = steps[i];
      if (placed.has(step.id)) continue;

      if (branchEdges[step.id]) {
        // This step has branches: place it centered, then branch targets side by side
        const targets = branchEdges[step.id].map((e) => e.to);
        const uniqueTargets = [...new Set(targets)];
        const branchCount = uniqueTargets.length;
        const totalBranchW = branchCount * nodeW + (branchCount - 1) * gapX;
        const parentX = Math.max(0, (totalBranchW - nodeW) / 2);

        positions[step.id] = { x: parentX, y: currentY };
        placed.add(step.id);
        currentY += nodeH + gapY;

        let bx = 0;
        for (const targetId of uniqueTargets) {
          positions[targetId] = { x: bx, y: currentY };
          placed.add(targetId);
          bx += nodeW + gapX;
        }
        maxWidth = Math.max(maxWidth, totalBranchW);
        currentY += nodeH + gapY;
      } else if (mergeTargets.has(step.id)) {
        // Merge point: center it
        positions[step.id] = { x: Math.max(0, (maxWidth - nodeW) / 2), y: currentY };
        placed.add(step.id);
        currentY += nodeH + gapY;
      } else if (!branchTargets.has(step.id)) {
        // Regular step: center it
        positions[step.id] = { x: Math.max(0, (maxWidth - nodeW) / 2), y: currentY };
        placed.add(step.id);

        // Place children (for parallel) side by side below
        if (step.children && step.children.length > 0) {
          currentY += nodeH + gapY;
          const childCount = step.children.length;
          const totalChildW = childCount * nodeW + (childCount - 1) * gapX;
          let cx = Math.max(0, (maxWidth - totalChildW) / 2);
          maxWidth = Math.max(maxWidth, totalChildW);
          for (const child of step.children) {
            positions[child.id] = { x: cx, y: currentY };
            cx += nodeW + gapX;
          }
        }
        currentY += nodeH + gapY;
      }
    }

    // Place any remaining unplaced steps
    for (const step of steps) {
      if (!placed.has(step.id)) {
        positions[step.id] = { x: 0, y: currentY };
        currentY += nodeH + gapY;
      }
    }

    return { positions, width: Math.max(maxWidth, nodeW), height: Math.max(currentY - gapY, nodeH) };
  }

  // ── YAML Source ──
  function renderYamlSource(yaml) {
    const el = $("#yaml-source");
    if (!yaml) { el.textContent = ""; return; }
    el.innerHTML = yaml.split("\n").map(highlightYamlLine).join("\n");
  }

  function renderYamlBrowser() {
    const titleEl = $("#yaml-browser-title");
    const descriptionEl = $("#yaml-browser-description");
    const metaEl = $("#yaml-browser-meta");
    const actionsEl = $("#yaml-browser-actions");
    const sourceEl = $("#yaml-browser-source");
    const graphEl = $("#yaml-browser-graph");
    const workflowBtn = $("#yaml-use-workflow");
    const playgroundBtn = $("#yaml-use-playground");
    if (!titleEl || !descriptionEl || !metaEl || !actionsEl || !sourceEl || !graphEl) return;

    const workflowAvailable = Boolean(selectedWorkflow && workflowDef?.yaml);
    const playgroundAvailable = Boolean(pgCurrentYaml && pgCurrentYaml.trim());

    if (!workflowAvailable && !playgroundAvailable) {
      titleEl.textContent = "No YAML selected";
      descriptionEl.textContent = "";
      metaEl.innerHTML = "";
      actionsEl.innerHTML = `
        <button type="button" class="btn btn-secondary" data-yaml-nav="workflows">Browse Workflows</button>
        <button type="button" class="btn btn-secondary" data-yaml-nav="playground">Open Playground</button>`;
      sourceEl.textContent = "";
      graphEl.innerHTML = `<p class="placeholder-copy">No graph</p>`;
      bindYamlActions(actionsEl);
      if (workflowBtn) workflowBtn.disabled = true;
      if (playgroundBtn) playgroundBtn.disabled = true;
      return;
    }

    if (yamlViewerSource === "workflow" && !workflowAvailable)
      yamlViewerSource = playgroundAvailable ? "playground" : "workflow";
    if (yamlViewerSource === "playground" && !playgroundAvailable)
      yamlViewerSource = workflowAvailable ? "workflow" : "playground";

    if (workflowBtn) workflowBtn.disabled = !workflowAvailable;
    if (playgroundBtn) playgroundBtn.disabled = !playgroundAvailable;
    workflowBtn?.classList.toggle("btn-primary", yamlViewerSource === "workflow");
    workflowBtn?.classList.toggle("btn-secondary", yamlViewerSource !== "workflow");
    playgroundBtn?.classList.toggle("btn-primary", yamlViewerSource === "playground");
    playgroundBtn?.classList.toggle("btn-secondary", yamlViewerSource !== "playground");

    if (yamlViewerSource === "playground" && playgroundAvailable) {
      titleEl.textContent = $("#pg-save-filename")?.value?.trim() || "Playground Draft";
      descriptionEl.textContent = "";
      metaEl.innerHTML = [
        `<span class="tag">${pgYamlValidated ? "Validated draft" : "Draft YAML"}</span>`,
        `<span class="tag">${pgYamlGeneratedByAi ? "AI generated" : "Manual or imported"}</span>`,
      ].join("");
      actionsEl.innerHTML = `
        <button type="button" class="btn btn-ghost" data-yaml-nav="playground">Back To Playground</button>
        <button type="button" class="btn btn-secondary" data-yaml-source="workflow"${workflowAvailable ? "" : " disabled"}>Switch To Library Workflow</button>`;
      sourceEl.innerHTML = pgCurrentYaml.split("\n").map(highlightYamlLine).join("\n");
      if (pgParsedDef?.definition?.steps) {
        renderFlowInto(graphEl, pgParsedDef.definition.steps, pgParsedDef.edges || [], pgStepStates);
      } else {
        graphEl.innerHTML = `<p class="placeholder-copy">No graph</p>`;
      }
      bindYamlActions(actionsEl);
      return;
    }

    titleEl.textContent = selectedWorkflowMeta?.name || selectedWorkflow || "Workflow YAML";
    descriptionEl.textContent = "";
    metaEl.innerHTML = [
      selectedWorkflowMeta?.groupLabel ? `<span class="tag">${esc(selectedWorkflowMeta.groupLabel)}</span>` : "",
      selectedWorkflowMeta?.sourceLabel ? `<span class="tag">${esc(selectedWorkflowMeta.sourceLabel)}</span>` : "",
      selectedWorkflowMeta?.category ? `<span class="tag">${esc(selectedWorkflowMeta.category)}</span>` : "",
    ].join("");
    actionsEl.innerHTML = `
      <button type="button" class="btn btn-ghost" data-open-selected-workflow>Open Workflow Details</button>
      <button type="button" class="btn btn-secondary" data-yaml-nav="playground"${playgroundAvailable ? "" : " disabled"}>Open Playground Draft</button>`;
    sourceEl.innerHTML = workflowDef?.yaml
      ? workflowDef.yaml.split("\n").map(highlightYamlLine).join("\n")
      : "";
    if (workflowDef?.definition?.steps) {
      renderFlowInto(graphEl, workflowDef.definition.steps, workflowDef.edges || [], stepStates);
    } else {
      graphEl.innerHTML = `<p class="placeholder-copy">No graph</p>`;
    }
    bindYamlActions(actionsEl);
  }

  function bindYamlActions(root) {
    if (!root) return;
    root.querySelectorAll("[data-yaml-nav]").forEach((button) => {
      button.addEventListener("click", () => {
        const view = button.getAttribute("data-yaml-nav");
        if (!view) return;
        if (view === "playground" && pgCurrentYaml) {
          yamlViewerSource = "playground";
        }
        activateNavView(view);
      });
    });
    root.querySelectorAll("[data-yaml-source]").forEach((button) => {
      button.addEventListener("click", () => {
        const source = button.getAttribute("data-yaml-source");
        if (!source) return;
        yamlViewerSource = source;
        renderYamlBrowser();
        scheduleUiStatePersist();
      });
    });
    root.querySelectorAll("[data-open-selected-workflow]").forEach((button) => {
      button.addEventListener("click", () => activateNavView("workflows"));
    });
  }

  function highlightYamlLine(line) {
    if (/^\s*#/.test(line))
      return `<span class="y-comment">${esc(line)}</span>`;

    return line.replace(
      /^(\s*)(- )?([a-zA-Z_][\w.]*\s*:)(.*)$/,
      (_, indent, dash, key, rest) => {
        const d = dash ? `<span class="y-punct">${esc(dash)}</span>` : "";
        const k = `<span class="y-key">${esc(key)}</span>`;
        return esc(indent) + d + k + highlightValue(rest);
      }
    ) || highlightValue(line);
  }

  function highlightValue(s) {
    if (!s) return "";
    const trimmed = s.trim();
    if (!trimmed || trimmed === "|" || trimmed === ">") return esc(s);

    if (/^(true|false|yes|no|on|off)$/i.test(trimmed))
      return s.replace(trimmed, `<span class="y-bool">${esc(trimmed)}</span>`);
    if (/^-?\d+(\.\d+)?$/.test(trimmed))
      return s.replace(trimmed, `<span class="y-number">${esc(trimmed)}</span>`);
    if (/^(null|~)$/i.test(trimmed))
      return s.replace(trimmed, `<span class="y-null">${esc(trimmed)}</span>`);
    if (/^["'].*["']$/.test(trimmed))
      return s.replace(trimmed, `<span class="y-string">${esc(trimmed)}</span>`);
    if (/^[&*]/.test(trimmed))
      return s.replace(trimmed, `<span class="y-anchor">${esc(trimmed)}</span>`);

    if (trimmed.startsWith("- "))
      return s.replace(/^(\s*)(- )(.*)$/, (_, ws, d, v) =>
        esc(ws) + `<span class="y-punct">${esc(d)}</span>` + highlightValue(v));

    return esc(s);
  }

  // ── Execution ──
  function runWorkflow() {
    if (!selectedWorkflow || !workflowDef) return;
    stopExecution();
    resetExecution();
    resetTuringMachineState();
    setWorkflowInteraction(null);

    const input = $("#wf-input").value;
    const autoResume = $("#wf-auto-resume")?.checked === true;
    const encodedInput = encodeURIComponent(input);
    const url = `/api/workflows/${selectedWorkflow}/run?input=${encodedInput}&autoResume=${autoResume ? "true" : "false"}`;

    $("#btn-run").disabled = true;
    $("#btn-reset").classList.remove("hidden");
    $("#exec-log").classList.remove("hidden");
    $("#log-entries").innerHTML = "";

    // Set all steps to pending
    if (workflowDef.definition.steps) {
      for (const s of workflowDef.definition.steps) {
        stepStates[s.id] = "pending";
      }
    }
    renderFlowDiagram();
    scheduleUiStatePersist();

    eventSource = new EventSource(url);

    eventSource.addEventListener("step.request", (e) => {
      const data = JSON.parse(e.data);
      stepStates[data.stepId] = "running";
      updateStepNode(data.stepId);
      addLogEntry("request", `\u25B6 ${data.stepId} (${data.stepType})`, data.input);
    });

    eventSource.addEventListener("step.completed", (e) => {
      const data = JSON.parse(e.data);
      stepStates[data.stepId] = data.success ? "completed" : "failed";
      updateStepNode(data.stepId);
      if (isSelectedWorkflowTuring()) {
        updateTuringMachineState(data.metadata);
      }
      if (data.success) {
        appendStepCompletedExecutionLog(data, addLogEntry);
        appendTelegramReplyExecutionLog(data, addLogEntry);
      } else {
        appendStepCompletedExecutionLog(data, addLogEntry);
      }
    });

    eventSource.addEventListener("llm.response", (e) => {
      const data = JSON.parse(e.data);
      const finalContent = absorbInlineThinking(wfThinkingBuffers, data.role, data.content);
      flushThinkingToLog(wfThinkingBuffers, data.role, addLogEntry);
      if (finalContent) {
        addLogEntry("llm", `\uD83E\uDD16 ${data.role}`, finalContent);
      }
    });

    eventSource.addEventListener("llm.thinking", (e) => {
      const data = JSON.parse(e.data);
      appendThinkingDelta(wfThinkingBuffers, data.role, data.content);
    });

    eventSource.addEventListener("workflow.suspended", (e) => {
      const data = JSON.parse(e.data);
      addLogEntry("suspended", `\u23F8 ${data.stepId} (${data.suspensionType})`, data.prompt || "Waiting for human action");
      setWorkflowInteraction({
        type: data.suspensionType || "human_input",
        actorId: data.actorId || "",
        runId: data.runId || "",
        stepId: data.stepId || "",
        prompt: data.prompt || "",
        timeoutSeconds: data.timeoutSeconds || 0,
        metadata: data.metadata || {},
      });
    });

    eventSource.addEventListener("workflow.waiting_signal", (e) => {
      const data = JSON.parse(e.data);
      addLogEntry("waiting", `\u23F3 ${data.stepId} (${data.signalName})`, data.prompt || "Waiting for external signal");
      setWorkflowInteraction({
        type: "wait_signal",
        actorId: data.actorId || "",
        runId: data.runId || "",
        stepId: data.stepId || "",
        signalName: data.signalName || "",
        prompt: data.prompt || "",
        timeoutMs: data.timeoutMs || 0,
      });
    });

    eventSource.addEventListener("workflow.completed", (e) => {
      const data = JSON.parse(e.data);
      addLogEntry("done", data.success ? "\u2705 Workflow completed" : "\u274C Workflow failed", "");
      showResult(data);
      setWorkflowInteraction(null);
      stopExecution();
      $("#btn-run").disabled = false;
    });

    eventSource.addEventListener("workflow.error", (e) => {
      const data = JSON.parse(e.data);
      addLogEntry("error", "\u274C Error", data.error);
      setWorkflowInteraction(null);
      stopExecution();
      $("#btn-run").disabled = false;
    });

    eventSource.onerror = () => {
      if (eventSource && eventSource.readyState === EventSource.CLOSED) {
        setWorkflowInteraction(null);
        stopExecution();
        $("#btn-run").disabled = false;
      }
    };
  }

  function stopExecution() {
    if (eventSource) {
      eventSource.close();
      eventSource = null;
    }
  }

  function resetExecution() {
    stopExecution();
    stepStates = {};
    wfLogEntries = [];
    wfThinkingBuffers = new Map();
    resetTuringMachineState();
    setWorkflowInteraction(null);
    $("#exec-log").classList.add("hidden");
    $("#result-section").classList.add("hidden");
    $("#log-entries").innerHTML = "";
    $("#btn-reset").classList.add("hidden");
    $("#btn-run").disabled = false;

    if (workflowRequiresLlmProvider(selectedWorkflowMeta, workflowDef) && !llmStatus.available) {
      $("#btn-run").disabled = true;
    }

    renderFlowDiagram();
    scheduleUiStatePersist();
  }

  function updateStepNode(stepId) {
    const node = $(`.step-node[data-step="${stepId}"]`);
    if (!node) return;
    node.className = `step-node state-${stepStates[stepId] || "pending"}`;
    const status = node.querySelector(".step-status");
    if (status) {
      const state = stepStates[stepId];
      if (state === "completed") status.textContent = "\u2713";
      else if (state === "failed") status.textContent = "\u2717";
      else if (state === "running") status.textContent = "\u25CB";
      else status.textContent = "";
    }
  }

  function workflowRequiresLlmProvider(workflowMeta, detail) {
    const explicit = workflowMeta?.requiresLlmProvider;
    if (typeof explicit === "boolean") return explicit;
    const catalogExplicit = detail?.catalog?.requiresLlmProvider;
    if (typeof catalogExplicit === "boolean") return catalogExplicit;

    const definition = detail?.definition;
    const steps = Array.isArray(definition?.steps) ? definition.steps : [];
    const roles = Array.isArray(definition?.roles) ? definition.roles : [];
    if (!steps.length) return workflowMeta?.category === "llm";

    const rolesById = new Map();
    for (const role of roles) {
      const roleId = String(role?.id || "").trim();
      if (roleId) rolesById.set(roleId, role);
    }

    return steps.some((step) => stepRequiresLlmProvider(step, rolesById));
  }

  function stepRequiresLlmProvider(step, rolesById) {
    const type = String(step?.type || "").trim().toLowerCase();
    if (type === "evaluate" || type === "reflect") return true;
    if (type === "llm_call") {
      const roleId = String(step?.targetRole || "").trim();
      if (!roleId) return true;
      const role = rolesById.get(roleId);
      const modules = Array.isArray(role?.eventModules) ? role.eventModules : [];
      return modules.length === 0;
    }

    const children = Array.isArray(step?.children) ? step.children : [];
    return children.some((child) => stepRequiresLlmProvider(child, rolesById));
  }

  function buildLogDetailHtml(detail, options = {}) {
    if (detail === null || detail === undefined || detail === "") return "";

    const text = String(detail);
    const foldThreshold = 480;
    const shouldFold = options.forceFold === true || text.length > foldThreshold;
    if (!shouldFold) {
      return `<div class="log-detail">${esc(text)}</div>`;
    }

    const openAttr = options.detailOpen === false ? "" : " open";
    const toggleLabel = options.toggleLabel || "Full text";
    const actionLabel = options.detailOpen === false ? "expand" : "collapse";
    return `<details class="log-detail-fold"${openAttr}><summary><span class="log-detail-toggle">${esc(toggleLabel)} (${text.length} chars) - click to ${actionLabel}</span></summary><pre class="log-detail-full">${esc(text)}</pre></details>`;
  }

  function thinkingBufferKey(role) {
    const normalized = String(role || "").trim();
    return normalized || "assistant";
  }

  function appendThinkingDelta(buffers, role, delta) {
    const text = String(delta || "");
    if (!text) return;
    const key = thinkingBufferKey(role);
    const previous = buffers.get(key) || "";
    buffers.set(key, previous + text);
    scheduleUiStatePersist();
  }

  function splitInlineThinking(content) {
    const text = String(content || "");
    if (!text) return { thinking: "", answer: "" };

    const regex = /<think(?:ing)?>\s*([\s\S]*?)\s*<\/think(?:ing)?>/gi;
    const segments = [];
    let match;
    while ((match = regex.exec(text)) !== null) {
      const segment = String(match[1] || "").trim();
      if (segment) segments.push(segment);
    }

    if (segments.length === 0) {
      return { thinking: "", answer: text };
    }

    const answer = text.replace(/<think(?:ing)?>[\s\S]*?<\/think(?:ing)?>/gi, "").trim();
    return {
      thinking: segments.join("\n\n"),
      answer,
    };
  }

  function absorbInlineThinking(buffers, role, content) {
    const parsed = splitInlineThinking(content);
    if (parsed.thinking) appendThinkingDelta(buffers, role, parsed.thinking);
    return parsed.answer || "";
  }

  function flushThinkingToLog(buffers, role, logFn) {
    const key = thinkingBufferKey(role);
    const content = buffers.get(key) || "";
    if (!content) return;
    buffers.delete(key);
    logFn(
      "thinking",
      `\uD83E\uDDE0 ${key} thinking`,
      content,
      {
        forceFold: true,
        detailOpen: false,
        toggleLabel: "Thinking trace",
      });
  }

  function addLogEntry(type, title, detail, options = {}) {
    const normalizedDetail = detail === null || detail === undefined ? "" : String(detail);
    if (!options.skipStore) {
      wfLogEntries.push({
        type: String(type || "action"),
        title: String(title || ""),
        detail: normalizedDetail,
      });
      scheduleUiStatePersist();
    }

    const el = document.createElement("div");
    el.className = `log-entry log-${type}`;
    const icons = { request: "\u25B6", completed: "\u2713", failed: "\u2717", llm: "\uD83E\uDD16", thinking: "\uD83E\uDDE0", suspended: "\u23F8", waiting: "\u23F3", action: "\u270D", done: "\u2705", error: "\u274C" };
    const detailHtml = buildLogDetailHtml(normalizedDetail, options);
    el.innerHTML = `<span class="log-icon">${icons[type] || ""}</span><span class="log-text"><strong>${esc(title)}</strong>${detailHtml ? `<br>${detailHtml}` : ""}</span>`;
    const container = $("#log-entries");
    container.appendChild(el);
    container.scrollTop = container.scrollHeight;
  }

  function showResult(data) {
    $("#result-section").classList.remove("hidden");
    const pre = $("#wf-result");
    pre.textContent = data.success ? data.output : (data.error || "Failed");
    pre.className = `result-pre ${data.success ? "success" : "failure"}`;
    scheduleUiStatePersist();
  }

  function setWorkflowInteraction(interaction) {
    pendingInteraction = interaction;
    const section = $("#interaction-section");
    const panel = $("#interaction-panel");
    if (!section || !panel) return;

    if (!interaction) {
      section.classList.add("hidden");
      panel.innerHTML = "";
      if (pgActiveModal === "interaction")
        pgCloseModal({ force: true, skipPersist: true });
      pgUpdateWorkspaceChromeState();
      scheduleUiStatePersist();
      return;
    }

    section.classList.remove("hidden");
    renderInteractionPanel(panel, "wf", interaction, addLogEntry, () => setWorkflowInteraction(null));
    scheduleUiStatePersist();
  }

  function setPlaygroundInteraction(interaction) {
    pgPendingInteraction = interaction;
    const section = $("#pg-interaction-section");
    const panel = $("#pg-interaction-panel");
    if (!section || !panel) return;

    if (!interaction) {
      section.classList.add("hidden");
      panel.innerHTML = "";
      pgRenderRunControlSummary();
      scheduleUiStatePersist();
      return;
    }

    section.classList.remove("hidden");
    if (interaction.type === "human_approval") {
      renderAutoApprovalPanel(panel, interaction);
    } else {
      renderInteractionPanel(panel, "pg", interaction, pgAddLog, () => setPlaygroundInteraction(null));
    }
    pgOpenModal("interaction", { skipPersist: true });
    pgUpdateWorkspaceChromeState();
    pgRenderRunControlSummary();
    scheduleUiStatePersist();
  }

  function renderInteractionPanel(container, prefix, interaction, logFn, clearFn) {
    const type = String(interaction.type || "").toLowerCase();
    const actorId = interaction.actorId || "";
    const runId = interaction.runId || "";
    const commandId = interaction.commandId || "";
    const stepId = interaction.stepId || "";
    const signalName = interaction.signalName || "";
    const prompt = interaction.prompt || "";
    const timeoutSeconds = Number(interaction.timeoutSeconds || 0);
    const timeoutMs = Number(interaction.timeoutMs || 0);
    const timeoutLabel = timeoutMs > 0
      ? `${Math.ceil(timeoutMs / 1000)}s`
      : (timeoutSeconds > 0 ? `${timeoutSeconds}s` : "none");

    const kindLabel = type === "wait_signal"
      ? "wait_signal"
      : (type === "human_approval" ? "human_approval" : "human_input");
    const title = type === "wait_signal"
      ? "External signal required"
      : (type === "human_approval" ? "Manual approval required" : "Manual input required");
    const missingSession = !runId || !actorId;
    const disabledAttr = missingSession ? " disabled" : "";
    const decisionOptions = type === "human_input"
      ? extractDecisionOptions(prompt)
      : [];

    let controlHtml = "";
    if (type === "human_approval") {
      controlHtml = `
        <textarea id="${prefix}-interaction-comment" class="interaction-input" rows="2" placeholder="Optional comment..."${disabledAttr}></textarea>
        <div class="interaction-actions">
          <button id="${prefix}-interaction-approve" class="btn btn-primary"${disabledAttr}>Approve</button>
          <button id="${prefix}-interaction-reject" class="btn btn-danger"${disabledAttr}>Reject</button>
        </div>`;
    } else if (type === "wait_signal") {
      controlHtml = `
        <textarea id="${prefix}-interaction-payload" class="interaction-input" rows="2" placeholder="Signal payload (optional)"${disabledAttr}></textarea>
        <div class="interaction-actions">
          <button id="${prefix}-interaction-signal" class="btn btn-primary"${disabledAttr}>Send signal</button>
        </div>`;
    } else {
      const variableName = interaction.metadata?.variable ? ` (${interaction.metadata.variable})` : "";
      const secure = isSecureInteraction(interaction);
      const optionButtons = decisionOptions.length > 0
        ? `
        <div class="interaction-note">Choose one action below. Add a comment only when you want the workflow to revise or narrow scope.</div>
        <div class="interaction-actions">
          ${decisionOptions.map((option) => `
            <button
              id="${prefix}-interaction-option-${esc(option.token)}"
              class="btn ${option.token === "approve" ? "btn-primary" : (option.token === "stop" ? "btn-danger" : "btn-secondary")}"
              data-token="${esc(option.token)}"${disabledAttr}>${esc(option.label)}</button>`).join("")}
        </div>`
        : "";
      const submitButtonHtml = decisionOptions.length > 0
        ? ""
        : `<div class="interaction-actions">
          <button id="${prefix}-interaction-submit" class="btn btn-primary"${disabledAttr}>Submit input</button>
        </div>`;
      controlHtml = `
        ${secure
          ? `<input id="${prefix}-interaction-input" class="interaction-input" type="password" placeholder="Secure input for human step${esc(variableName)}" autocomplete="off"${disabledAttr}>`
          : `<textarea id="${prefix}-interaction-input" class="interaction-input" rows="3" placeholder="${esc(decisionOptions.length > 0 ? "Optional comment for revise / narrow scope" : `Input for human step${variableName}`)}"${disabledAttr}></textarea>`}
        ${secure ? `<div class="interaction-note">Sensitive input is masked locally and will not be echoed back into the workflow log.</div>` : ""}
        ${optionButtons}
        ${submitButtonHtml}`;
    }

    container.innerHTML = `
      <div class="interaction-summary">
        <strong>${esc(title)}</strong><br>
        ${esc(prompt || "No prompt provided by workflow.")}
      </div>
      <div class="interaction-meta">
        <span class="interaction-chip">${esc(kindLabel)}</span>
        ${stepId ? `<span class="interaction-chip">step ${esc(stepId)}</span>` : ""}
        ${type === "wait_signal" ? `<span class="interaction-chip">signal ${esc(signalName || "n/a")}</span>` : ""}
        <span class="interaction-chip">timeout ${esc(timeoutLabel)}</span>
      </div>
      ${controlHtml}
      ${missingSession ? `<div class="interaction-note">This run has not exposed a resumable session yet. Wait for the next event or restart the run if the controls stay disabled.</div>` : ""}
      ${(actorId || runId || commandId) ? `
        <details class="interaction-tech">
          <summary>Technical context</summary>
          <div class="interaction-meta">
            ${actorId ? `<span class="interaction-chip">actor ${esc(actorId)}</span>` : ""}
            ${runId ? `<span class="interaction-chip">run ${esc(runId)}</span>` : ""}
            ${commandId ? `<span class="interaction-chip">command ${esc(commandId)}</span>` : ""}
          </div>
        </details>` : ""}
    `;

    bindInteractionActions(prefix, interaction, logFn, clearFn);
  }

  function bindInteractionActions(prefix, interaction, logFn, clearFn) {
    const type = String(interaction.type || "").toLowerCase();
    const actorId = interaction.actorId || "";
    const runId = interaction.runId || "";
    const commandId = interaction.commandId || "";
    const stepId = interaction.stepId || "";
    const signalName = interaction.signalName || "";
    const prompt = interaction.prompt || "";

    const clearIfStillCurrentInteraction = () => {
      const current = prefix === "pg" ? pgPendingInteraction : pendingInteraction;
      if (!current) return;
      if (String(current.runId || "") !== String(runId || "")) return;
      if (String(current.stepId || "") !== String(stepId || "")) return;
      if (String(current.type || "").toLowerCase() !== type) return;
      if (type === "wait_signal" &&
          String(current.signalName || "") !== String(signalName || ""))
        return;
      clearFn();
    };

    if (!runId || !actorId) return;

    if (type === "human_approval") {
      const approveBtn = $(`#${prefix}-interaction-approve`);
      const rejectBtn = $(`#${prefix}-interaction-reject`);
      const commentEl = $(`#${prefix}-interaction-comment`);
      if (!approveBtn || !rejectBtn || !commentEl || !stepId) return;

      const submitApproval = async (approved) => {
        approveBtn.disabled = true;
        rejectBtn.disabled = true;
        const comment = commentEl.value.trim();
        try {
          await postJson("/api/workflows/resume", {
            actorId,
            runId,
            stepId,
            commandId,
            approved,
            userInput: comment,
          });
          logFn("action", approved ? "✍ Approval submitted" : "✍ Rejection submitted", comment || "(no comment)");
          clearIfStillCurrentInteraction();
        } catch (e) {
          logFn("error", "\u274C Resume failed", e.message || String(e));
        } finally {
          approveBtn.disabled = false;
          rejectBtn.disabled = false;
        }
      };

      approveBtn.addEventListener("click", () => submitApproval(true));
      rejectBtn.addEventListener("click", () => submitApproval(false));
      return;
    }

    if (type === "wait_signal") {
      const sendBtn = $(`#${prefix}-interaction-signal`);
      const payloadEl = $(`#${prefix}-interaction-payload`);
      if (!sendBtn || !payloadEl || !signalName) return;

      sendBtn.addEventListener("click", async () => {
        sendBtn.disabled = true;
        const payload = payloadEl.value;
        try {
          const signalRequest = {
            actorId,
            runId,
            signalName,
            commandId,
            payload,
          };
          if (stepId) {
            signalRequest.stepId = stepId;
          }
          await postJson("/api/workflows/signal", signalRequest);
          logFn("action", "✍ Signal submitted", payload || "(empty payload)");
          clearIfStillCurrentInteraction();
        } catch (e) {
          logFn("error", "\u274C Signal failed", e.message || String(e));
        } finally {
          sendBtn.disabled = false;
        }
      });
      return;
    }

      const submitBtn = $(`#${prefix}-interaction-submit`);
      const inputEl = $(`#${prefix}-interaction-input`);
      if (!inputEl || !stepId) return;

      const decisionOptions = type === "human_input"
        ? extractDecisionOptions(prompt)
        : [];

      const submitHumanInput = async (userInput, actionLabel, actionDetail) => {
        try {
          await postJson("/api/workflows/resume", {
            actorId,
            runId,
            stepId,
            commandId,
            approved: true,
            userInput,
          });
          logFn(
            "action",
            actionLabel,
            actionDetail);
          clearIfStillCurrentInteraction();
        } catch (e) {
          logFn("error", "\u274C Resume failed", e.message || String(e));
        }
      };

      decisionOptions.forEach((option) => {
        const btn = $(`#${prefix}-interaction-option-${option.token}`);
        if (!btn) return;
        btn.addEventListener("click", async () => {
          if (submitBtn) submitBtn.disabled = true;
          decisionOptions.forEach((item) => {
            const itemBtn = $(`#${prefix}-interaction-option-${item.token}`);
            if (itemBtn) itemBtn.disabled = true;
          });
          const comment = inputEl.value.trim();
          const payload = buildDecisionInput(option.token, comment);
          const detail = isSecureInteraction(interaction)
            ? "(secure input hidden)"
            : payload;
          try {
            await submitHumanInput(payload, `✍ ${option.label}`, detail);
          } finally {
            if (submitBtn) submitBtn.disabled = false;
            decisionOptions.forEach((item) => {
              const itemBtn = $(`#${prefix}-interaction-option-${item.token}`);
              if (itemBtn) itemBtn.disabled = false;
            });
          }
        });
      });

      if (!submitBtn) return;

      submitBtn.addEventListener("click", async () => {
        submitBtn.disabled = true;
        const userInput = inputEl.value.trim();
        if (!userInput) {
          logFn("error", "❌ Input required", "Choose an action button or enter a response before submitting.");
          submitBtn.disabled = false;
          return;
        }
        try {
          await submitHumanInput(
            userInput,
            isSecureInteraction(interaction) ? "✍ Secure input submitted" : "✍ Human input submitted",
            isSecureInteraction(interaction) ? "(secure input hidden)" : (userInput || "(empty input)"));
        } catch (e) {
          logFn("error", "\u274C Resume failed", e.message || String(e));
        } finally {
          submitBtn.disabled = false;
      }
    });
  }

  async function postJson(url, payload) {
    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });

    const raw = await res.text();
    let data = null;
    if (raw) {
      try {
        data = JSON.parse(raw);
      } catch {
        data = null;
      }
    }

    if (!res.ok) {
      const errorMessage = data?.error || raw || `HTTP ${res.status}`;
      throw new Error(errorMessage);
    }

    return data;
  }

  function extractDecisionOptions(prompt) {
    const text = String(prompt || "");
    if (!text) return [];
    const options = [];
    const seen = new Set();
    const lines = text.split(/\r?\n/);
    for (const rawLine of lines) {
      const line = rawLine.trim();
      const match = line.match(/^-\s*([a-z_]+)(?::.*)?$/i);
      if (!match) continue;
      const token = String(match[1] || "").trim().toLowerCase();
      if (!["approve", "revise", "narrow_scope", "stop"].includes(token)) continue;
      if (seen.has(token)) continue;
      seen.add(token);
      options.push({
        token,
        label: decisionLabelForToken(token),
      });
    }
    return options;
  }

  function decisionLabelForToken(token) {
    switch (String(token || "").toLowerCase()) {
      case "approve":
        return "Approve";
      case "revise":
        return "Revise";
      case "narrow_scope":
        return "Narrow scope";
      case "stop":
        return "Stop";
      default:
        return token || "Submit";
    }
  }

  function buildDecisionInput(token, comment) {
    const normalized = String(token || "").trim().toLowerCase();
    if (!comment) return normalized;
    if (normalized === "approve" || normalized === "stop") return normalized;
    return `${normalized}: ${comment}`;
  }

  function renderWorkflowConfig(configuration) {
    const el = $("#workflow-config");
    if (!configuration) {
      el.classList.add("hidden");
      el.innerHTML = "";
      return;
    }

    const closedWorld = configuration.closedWorldMode === true;
    el.classList.remove("hidden");
    el.innerHTML = `<span class="config-chip ${closedWorld ? "on" : "off"}">closed_world_mode: ${closedWorld}</span>`;
  }

  function renderRoleCards(roles) {
    const container = $("#roles-list");
    container.innerHTML = roles.map((role) => renderRoleCard(role)).join("");
  }

  function renderWorkflowPlan(workflowMeta, detail) {
    const section = $("#plan-section");
    const bodyEl = $("#wf-plan-body");
    const summaryEl = $("#wf-plan-summary");
    const metaEl = $("#wf-plan-meta");
    const inputsEl = $("#wf-plan-inputs");
    const primitivesEl = $("#wf-plan-primitives");
    const stepsEl = $("#wf-plan-steps");
    if (!section || !bodyEl || !summaryEl || !metaEl || !inputsEl || !primitivesEl || !stepsEl) return;

    if (!workflowMeta || !detail?.definition) {
      section.classList.add("hidden");
      section.classList.remove("is-collapsed");
      bodyEl.classList.remove("hidden");
      setWorkflowPlanCollapsed(false, { force: true, skipPersist: true });
      summaryEl.textContent = "";
      metaEl.innerHTML = "";
      inputsEl.textContent = "";
      primitivesEl.innerHTML = "";
      stepsEl.innerHTML = "";
      return;
    }

    const steps = Array.isArray(detail.definition.steps) ? detail.definition.steps : [];
    const roles = Array.isArray(detail.definition.roles) ? detail.definition.roles : [];
    const primitives = Array.isArray(workflowMeta.primitives) ? workflowMeta.primitives : [];
    const interactiveSteps = steps.filter((step) =>
      ["human_input", "secure_input", "human_approval", "wait_signal"].includes(String(step.type || "")));
    const interactivePrompts = extractInteractivePrompts(steps);
    const connectorSteps = steps.filter((step) =>
      ["connector_call", "secure_connector_call"].includes(String(step.type || ""))).length;

    summaryEl.textContent = buildWorkflowPlanSummary(steps, roles, interactiveSteps.length, connectorSteps);
    metaEl.innerHTML = [
      renderPlanMetric("Library", workflowMeta.sourceLabel || getWorkflowSourceLabel(workflowMeta.source)),
      renderPlanMetric("Collection", workflowMeta.groupLabel || workflowMeta.group || "Workflow"),
      renderPlanMetric("Pattern", workflowMeta.category || "deterministic"),
      renderPlanMetric("Roles / Steps", `${roles.length} / ${steps.length}`),
      renderPlanMetric("Human Checkpoints", interactivePrompts.length > 0 ? `${interactivePrompts.length} expected` : "none"),
    ].join("");

    inputsEl.innerHTML = interactivePrompts.length > 0
      ? renderPlanInteractionSummary(interactivePrompts)
      : "This flow can run end-to-end once you press Run. The user mainly needs to understand the graph, YAML, and expected output.";

    primitivesEl.innerHTML = primitives
      .filter((name) => name && name !== "workflow_loop")
      .map((name) => `<span class="tag">${esc(name)}</span>`)
      .join("");

    const visibleSteps = steps.slice(0, 6);
    stepsEl.innerHTML = visibleSteps.map((step, index) => renderPlanStep(step, index)).join("");
    if (steps.length > visibleSteps.length) {
      stepsEl.innerHTML += `<div class="plan-step"><span class="plan-step-index">+</span><div class="plan-step-main"><div class="plan-step-title"><span class="plan-step-id">${steps.length - visibleSteps.length} more step${steps.length - visibleSteps.length > 1 ? "s" : ""}</span></div><div class="plan-step-detail">Open the YAML panel for the full control-plane flow.</div></div></div>`;
    }

    section.classList.remove("hidden");
    setWorkflowPlanCollapsed(workflowPlanCollapsed, { force: true, skipPersist: true });
  }

  function buildWorkflowPlanSummary(steps, roles, interactiveCount, connectorSteps) {
    const parts = [`Aevatar will run ${steps.length} step${steps.length === 1 ? "" : "s"} across ${roles.length} role${roles.length === 1 ? "" : "s"}.`];
    if (connectorSteps > 0)
      parts.push(`It verifies ${connectorSteps} connector step${connectorSteps === 1 ? "" : "s"}.`);
    if (interactiveCount > 0)
      parts.push(`It can ask for ${interactiveCount} human decision${interactiveCount === 1 ? "" : "s"} in chat.`);
    return parts.join(" ");
  }

  function renderPlanMetric(label, value) {
    return `<div class="plan-metric"><span class="plan-metric-label">${esc(label)}</span><span class="plan-metric-value">${esc(value || "n/a")}</span></div>`;
  }

  function renderPlanStep(step, index) {
    const role = step.targetRole ? `@${step.targetRole}` : "";
    const detail = describePlanStep(step);
    return `<div class="plan-step"><span class="plan-step-index">${index + 1}</span><div class="plan-step-main"><div class="plan-step-title"><span class="plan-step-id">${esc(step.id || `step_${index + 1}`)}</span><span class="plan-step-type">${esc(step.type || "step")}${role ? ` · ${esc(role)}` : ""}</span></div>${detail ? `<div class="plan-step-detail">${esc(detail)}</div>` : ""}</div></div>`;
  }

  function describePlanStep(step) {
    const interaction = describeInteractiveStep(step);
    if (interaction)
      return interaction.prompt;
    const branchCount = step?.branches && typeof step.branches === "object" ? Object.keys(step.branches).length : 0;
    const childCount = Array.isArray(step?.children) ? step.children.length : 0;
    if (branchCount > 0)
      return `${branchCount} explicit branch${branchCount > 1 ? "es" : ""}.`;
    if (childCount > 0)
      return `${childCount} child step${childCount > 1 ? "s" : ""} execute inside this stage.`;
    if (step?.next)
      return `Then continues to ${step.next}.`;
    return "Sequential stage in the current workflow.";
  }

  function renderPlanInteractionSummary(items) {
    const visible = items.slice(0, 4);
    const cards = visible.map((item) => `
      <div class="plan-question">
        <div class="plan-question-head">
          <span class="plan-question-kind">${esc(formatInteractiveKind(item.type))}</span>
          <span class="plan-question-step">${esc(item.id)}</span>
        </div>
        <div class="plan-question-prompt">${esc(item.prompt)}</div>
        ${item.meta ? `<div class="plan-question-meta">${esc(item.meta)}</div>` : ""}
      </div>`).join("");
    const more = items.length > visible.length
      ? `<div class="plan-question"><div class="plan-question-prompt">${items.length - visible.length} more chat question${items.length - visible.length > 1 ? "s" : ""} are expected later in this workflow.</div></div>`
      : "";
    return `<div>Aevatar can pause in chat for ${items.length} human step${items.length > 1 ? "s" : ""}. Use the interaction panel below to approve, answer, or continue the run.</div><div class="plan-question-list">${cards}${more}</div>`;
  }

  function extractInteractivePrompts(steps) {
    if (!Array.isArray(steps)) return [];
    return steps
      .map((step, index) => describeInteractiveStep(step, index))
      .filter((item) => Boolean(item));
  }

  function describeInteractiveStep(step, index = 0) {
    const type = String(step?.type || "").toLowerCase();
    if (!["human_input", "secure_input", "human_approval", "wait_signal"].includes(type))
      return null;

    const parameters = step?.parameters && typeof step.parameters === "object" ? step.parameters : {};
    const prompt = readPlanParameter(parameters, "prompt", "message");
    const variable = readPlanParameter(parameters, "variable");
    const signalName = readPlanParameter(parameters, "signal_name", "signal");
    const timeout = readPlanParameter(parameters, "timeout_ms", "timeout_seconds", "timeout");
    const secure = type === "secure_input";

    return {
      id: String(step?.id || `step_${index + 1}`),
      type,
      prompt: buildInteractivePrompt(type, prompt, variable, signalName, secure),
      meta: buildInteractiveMeta(type, variable, signalName, timeout, secure),
    };
  }

  function buildInteractivePrompt(type, prompt, variable, signalName, secure) {
    if (prompt) return prompt;
    if (type === "human_approval")
      return "Aevatar will ask for approval before applying the next control-plane action.";
    if (type === "wait_signal")
      return `Aevatar will wait for signal '${signalName || "signal"}' before continuing.`;
    if (secure && variable)
      return `Aevatar will collect '${variable}' as a masked secret before continuing.`;
    if (variable)
      return `Aevatar will collect '${variable}' in chat before continuing.`;
    return "Aevatar will pause in chat for more input before continuing.";
  }

  function buildInteractiveMeta(type, variable, signalName, timeout, secure) {
    const details = [];
    if (variable && (type === "human_input" || type === "secure_input"))
      details.push(`${secure ? "stores securely as" : "stores as"} ${variable}`);
    if (secure)
      details.push("masked");
    if (signalName && type === "wait_signal")
      details.push(`signal ${signalName}`);
    if (timeout)
      details.push(`timeout ${timeout}`);
    return details.join(" · ");
  }

  function readPlanParameter(parameters, ...keys) {
    for (const key of keys) {
      if (!Object.prototype.hasOwnProperty.call(parameters, key))
        continue;
      const value = parameters[key];
      if (value === null || value === undefined)
        continue;
      const text = String(value).trim();
      if (text)
        return text;
    }
    return "";
  }

  function formatInteractiveKind(type) {
    if (type === "human_approval")
      return "approval";
    if (type === "wait_signal")
      return "signal";
    if (type === "secure_input")
      return "secure input";
    return "input";
  }

  function renderRoleCard(role) {
    const displayName = role.name || role.id || "unnamed";
    const displayId = role.id || role.name || "n/a";
    const providerModel = `${role.provider || "default"} / ${role.model || "default"}`;
    const limits = [
      `temperature=${formatRoleValue(role.temperature)}`,
      `max_tokens=${formatRoleValue(role.maxTokens)}`,
      `max_tool_rounds=${formatRoleValue(role.maxToolRounds)}`,
      `max_history_messages=${formatRoleValue(role.maxHistoryMessages)}`,
      `stream_buffer_capacity=${formatRoleValue(role.streamBufferCapacity)}`,
    ].join(" · ");

    const modules = splitCsv(role.eventModules);
    const connectors = Array.isArray(role.connectors)
      ? role.connectors.map((item) => String(item).trim()).filter((item) => item.length > 0)
      : [];
    const systemPrompt = role.systemPrompt ? truncate(role.systemPrompt, 220) : "";
    const routes = role.eventRoutes ? role.eventRoutes : "";

    return `
      <article class="role-card">
        <div class="role-head">
          <div class="role-title">${esc(displayName)}</div>
          <div class="role-id">@${esc(displayId)}</div>
        </div>
        <div class="role-meta">
          <div><span class="role-key">model</span>${esc(providerModel)}</div>
          <div><span class="role-key">limits</span>${esc(limits)}</div>
        </div>
        ${systemPrompt ? `
          <div class="role-section">
            <div class="role-key">system_prompt</div>
            <div class="role-prompt">${esc(systemPrompt)}</div>
          </div>` : ""}
        <div class="role-section">
          <div class="role-key">event_modules</div>
          <div class="role-tags">${renderRoleTags(modules)}</div>
        </div>
        <div class="role-section">
          <div class="role-key">connectors</div>
          <div class="role-tags">${renderRoleTags(connectors)}</div>
        </div>
        ${routes ? `
          <div class="role-section">
            <div class="role-key">event_routes</div>
            <pre class="role-routes">${esc(routes)}</pre>
          </div>` : ""}
      </article>
    `;
  }

  function splitCsv(value) {
    if (!value || typeof value !== "string") return [];
    return value
      .split(",")
      .map((item) => item.trim())
      .filter((item) => item.length > 0);
  }

  function renderRoleTags(items) {
    if (!items || items.length === 0) {
      return `<span class="role-tag empty">none</span>`;
    }

    return items.map((item) => `<span class="role-tag">${esc(item)}</span>`).join("");
  }

  function formatRoleValue(value) {
    if (value === null || value === undefined || value === "") {
      return "default";
    }
    return String(value);
  }

  function renderTuringDemoPanel(workflowMeta) {
    const panel = $("#turing-proof");
    if (!workflowMeta || workflowMeta.category !== "turing") {
      panel.classList.add("hidden");
      return;
    }

    panel.classList.remove("hidden");
    $("#turing-note").textContent = getTuringNote(workflowMeta.name);
    $("#turing-primitives").innerHTML = (workflowMeta.primitives || [])
      .filter((name) => name && name !== "workflow_loop")
      .map((name) => `<span class="tag">${esc(name)}</span>`)
      .join("");
  }

  function isSelectedWorkflowTuring() {
    return selectedWorkflowMeta?.category === "turing";
  }

  function updateTuringMachineState(metadata) {
    if (!metadata) return;
    const target = metadata["assign.target"];
    if (!target) return;

    const value = Object.prototype.hasOwnProperty.call(metadata, "assign.value")
      ? metadata["assign.value"]
      : "";

    turingMachineState[target] = value ?? "";
    renderTuringMachineState();
  }

  function resetTuringMachineState() {
    turingMachineState = {};
    renderTuringMachineState();
  }

  function renderTuringMachineState() {
    const stateContainer = $("#turing-state");
    if (!stateContainer) return;

    const entries = Object.entries(turingMachineState)
      .sort(([left], [right]) => left.localeCompare(right));

    if (entries.length === 0) {
      stateContainer.textContent = "Run the workflow to see counters change.";
      return;
    }

    stateContainer.innerHTML = entries
      .map(([key, value]) => `<div class="turing-state-row"><span class="turing-state-key">${esc(key)}</span><code class="turing-state-value">${esc(value)}</code></div>`)
      .join("");
  }

  function getTuringNote(workflowName) {
    if (workflowName === "counter-addition" || workflowName === "counter_addition") {
      return "Closed-world two-counter addition. The branch-back edge (jz_b -> inc_a -> dec_b -> jz_b) demonstrates conditional jump with mutable memory.";
    }
    if (workflowName === "minsky-inc-dec-jz" || workflowName === "minsky_inc_dec_jz") {
      return "Closed-world INC/DEC/JZ transfer program. It encodes a minimal Minsky-style machine using assign + conditional + branch routing.";
    }

    return "Closed-world workflow that encodes counter-machine style state transition with primitive modules.";
  }

  // ── Helpers ──
  function esc(s) {
    if (!s) return "";
    const div = document.createElement("div");
    div.textContent = s;
    return div.innerHTML;
  }

  function truncate(s, max) {
    if (!s) return "";
    return s.length > max ? s.slice(0, max) + "..." : s;
  }

  function getWorkflowSourceLabel(source) {
    const normalized = String(source || "").trim().toLowerCase();
    if (normalized === "home") return "Saved";
    if (normalized === "cwd") return "Workspace";
    if (normalized === "repo") return "Starter";
    if (normalized === "demo") return "Demo";
    if (normalized === "turing") return "Advanced";
    if (normalized === "file") return "File";
    if (normalized === "builtin") return "Built-in";
    return normalized ? normalized : "Workflow";
  }

  function setupStatePersistence() {
    if (persistenceHooksBound) return;
    persistenceHooksBound = true;

    const bind = (selector, eventName = "input") => {
      const el = $(selector);
      if (el) el.addEventListener(eventName, scheduleUiStatePersist);
    };

    bind("#wf-input");
    bind("#pg-input");
    bind("#pg-run-input");
    bind("#pg-save-filename");
    bind("#wf-auto-resume", "change");
    bind("#pg-auto-resume", "change");
    window.addEventListener("beforeunload", flushUiStatePersist);
  }

  function scheduleUiStatePersist() {
    if (persistStateTimer !== null) return;
    persistStateTimer = window.setTimeout(() => {
      persistStateTimer = null;
      persistUiState();
    }, 200);
  }

  function flushUiStatePersist() {
    if (persistStateTimer !== null) {
      window.clearTimeout(persistStateTimer);
      persistStateTimer = null;
    }
    persistUiState();
  }

  function readPersistedUiState() {
    try {
      const raw = sessionStorage.getItem(UI_STATE_STORAGE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw);
      if (!parsed || parsed.version !== UI_STATE_STORAGE_VERSION) return null;
      return parsed;
    } catch {
      return null;
    }
  }

  function persistUiState() {
    const snapshot = buildPersistedUiState();
    const working = JSON.parse(JSON.stringify(snapshot));

    while (true) {
      try {
        sessionStorage.setItem(UI_STATE_STORAGE_KEY, JSON.stringify(working));
        return;
      } catch (e) {
        if (!isQuotaExceededError(e)) {
          console.warn("Failed to persist UI state", e);
          return;
        }

        const wfLogs = working.workflow?.logs || [];
        const pgLogs = working.playground?.logs || [];
        if (wfLogs.length === 0 && pgLogs.length === 0) {
          console.warn("Unable to persist UI state: storage quota exceeded.");
          return;
        }

        if (pgLogs.length >= wfLogs.length) {
          pgLogs.splice(0, Math.max(1, Math.ceil(pgLogs.length * 0.2)));
        } else {
          wfLogs.splice(0, Math.max(1, Math.ceil(wfLogs.length * 0.2)));
        }
      }
    }
  }

  function buildPersistedUiState() {
    const wfResultPre = $("#wf-result");
    const pgResultPre = $("#pg-result");
    const pgAutoStatus = $("#pg-auto-status");
    const tones = ["info", "warn", "success", "error"];
    const activeTone = tones.find((tone) => pgAutoStatus?.classList?.contains(tone)) || "info";

    return {
      version: UI_STATE_STORAGE_VERSION,
      activeView: getActiveNavView(),
      selectedWorkflow,
      selectedPrimitive,
      yamlViewerSource,
      sidebar: {
        groupCollapsed: { ...workflowGroupCollapsed },
        collapsed: appSidebarCollapsed === true,
      },
      workflow: {
        input: $("#wf-input")?.value || "",
        autoResume: $("#wf-auto-resume")?.checked === true,
        planCollapsed: workflowPlanCollapsed === true,
        logs: wfLogEntries.map((entry) => ({ ...entry })),
        stepStates: { ...(stepStates || {}) },
        result: {
          visible: !$("#result-section")?.classList.contains("hidden"),
          text: wfResultPre?.textContent || "",
          className: wfResultPre?.className || "result-pre",
        },
      },
      playground: {
        sidebarCollapsed: pgSidebarCollapsed === true,
        chatHistoryCollapsed: pgChatHistoryCollapsed === true,
        activeModal: PG_MODAL_KEYS.has(pgActiveModal) ? pgActiveModal : "",
        input: $("#pg-input")?.value || "",
        runInput: $("#pg-run-input")?.value || "",
        saveFilename: $("#pg-save-filename")?.value || "",
        messages: Array.isArray(pgMessages)
          ? pgMessages.map((x) => ({ role: String(x.role || "assistant"), content: String(x.content || "") }))
          : [],
        currentYaml: pgCurrentYaml || "",
        yamlGeneratedByAi: pgYamlGeneratedByAi === true,
        yamlValidated: pgYamlValidated === true,
        logs: pgLogEntries.map((entry) => ({ ...entry })),
        stepStates: { ...(pgStepStates || {}) },
        result: {
          visible: !$("#pg-result-section")?.classList.contains("hidden"),
          text: pgResultPre?.textContent || "",
          className: pgResultPre?.className || "result-pre",
        },
        autoStatus: {
          text: pgAutoStatus?.classList.contains("hidden") ? "" : (pgAutoStatus?.textContent || ""),
          tone: activeTone,
        },
        autoCanRunFinal: pgAutoCanRunFinal === true,
      },
    };
  }

  async function restoreUiState(snapshot) {
    if (!snapshot || typeof snapshot !== "object") return;

    workflowGroupCollapsed = normalizeGroupCollapsed(snapshot.sidebar?.groupCollapsed);
    setAppSidebarCollapsed(snapshot.sidebar?.collapsed === true, { force: true, skipPersist: true });
    renderWorkflowList();
    yamlViewerSource = snapshot.yamlViewerSource === "playground" ? "playground" : "workflow";

    const targetWorkflow = typeof snapshot.selectedWorkflow === "string" ? snapshot.selectedWorkflow : "";
    if (targetWorkflow && workflows.some((w) => w.name === targetWorkflow)) {
      await selectWorkflow(targetWorkflow, { preferredView: "none" });
      restoreWorkflowUiState(snapshot.workflow);
    }

    const targetPrimitive = typeof snapshot.selectedPrimitive === "string" ? snapshot.selectedPrimitive : "";
    const primitive = primitives.find((item) => item.name === targetPrimitive);
    if (primitive) {
      showPrimitiveDetail(primitive, { show: false });
    }

    await restorePlaygroundUiState(snapshot.playground);
    renderOverviewPage();
    renderWorkflowHome();
    renderPrimitivesHome();
    renderYamlBrowser();
    activateNavView(normalizeNavView(snapshot.activeView));
    scheduleUiStatePersist();
  }

  function restoreWorkflowUiState(snapshot) {
    if (!snapshot || !selectedWorkflow || !workflowDef) return;

    if (typeof snapshot.input === "string")
      $("#wf-input").value = snapshot.input;
    if (typeof snapshot.autoResume === "boolean" && $("#wf-auto-resume"))
      $("#wf-auto-resume").checked = snapshot.autoResume;

    if (snapshot.stepStates && typeof snapshot.stepStates === "object")
      stepStates = { ...snapshot.stepStates };
    renderFlowDiagram();

    const logs = normalizePersistedLogEntries(snapshot.logs);
    wfLogEntries = logs.map((entry) => ({ ...entry }));
    $("#log-entries").innerHTML = "";
    if (logs.length > 0) {
      $("#exec-log").classList.remove("hidden");
      for (const entry of logs)
        addLogEntry(entry.type, entry.title, entry.detail, { skipStore: true });
    }

    if (snapshot.result?.visible) {
      $("#result-section").classList.remove("hidden");
      const pre = $("#wf-result");
      pre.textContent = String(snapshot.result.text || "");
      pre.className = String(snapshot.result.className || "result-pre");
    }

    setWorkflowPlanCollapsed(snapshot.planCollapsed === true, { force: true, skipPersist: true });

    if (logs.length > 0 || snapshot.result?.visible)
      $("#btn-reset").classList.remove("hidden");
  }

  async function restorePlaygroundUiState(snapshot) {
    if (!snapshot || typeof snapshot !== "object") return;

    pgSetSidebarCollapsed(snapshot.sidebarCollapsed === true, { force: true, skipPersist: true });
    pgSetChatHistoryCollapsed(snapshot.chatHistoryCollapsed === true, { force: true, skipPersist: true });
    pgCloseModal({ force: true, skipPersist: true });

    if (typeof snapshot.input === "string")
      $("#pg-input").value = snapshot.input;
    if (typeof snapshot.runInput === "string")
      $("#pg-run-input").value = snapshot.runInput;
    if (typeof snapshot.saveFilename === "string")
      $("#pg-save-filename").value = snapshot.saveFilename;

    const restoredMessages = Array.isArray(snapshot.messages)
      ? snapshot.messages
          .filter((x) => x && typeof x === "object")
          .map((x) => ({ role: String(x.role || "assistant"), content: String(x.content || "") }))
      : [];
    pgMessages = restoredMessages;
    restorePlaygroundMessages(restoredMessages);

    const restoredYaml = typeof snapshot.currentYaml === "string" ? snapshot.currentYaml : "";
    if (restoredYaml.trim())
      await pgApplyYaml(restoredYaml, {
        generatedByAi: snapshot.yamlGeneratedByAi === true,
        preferredFilename: typeof snapshot.saveFilename === "string" ? snapshot.saveFilename : "",
      });

    const logs = normalizePersistedLogEntries(snapshot.logs);
    pgLogEntries = logs.map((entry) => ({ ...entry }));
    $("#pg-log-entries").innerHTML = "";
    if (logs.length > 0) {
      $("#pg-exec-log").classList.remove("hidden");
      for (const entry of logs)
        pgAddLog(entry.type, entry.title, entry.detail, { skipStore: true });
    }

    if (snapshot.stepStates && typeof snapshot.stepStates === "object")
      pgStepStates = { ...snapshot.stepStates };
    if (pgParsedDef?.definition?.steps)
      renderFlowInto($("#pg-graph"), pgParsedDef.definition.steps, pgParsedDef.edges, pgStepStates);

    if (snapshot.result?.visible) {
      $("#pg-result-section").classList.remove("hidden");
      const pre = $("#pg-result");
      pre.textContent = String(snapshot.result.text || "");
      pre.className = String(snapshot.result.className || "result-pre");
    }

    if (snapshot.autoStatus?.text) {
      pgSetAutoStatus(String(snapshot.autoStatus.text), String(snapshot.autoStatus.tone || "info"));
    } else {
      pgSetAutoStatus("", "info");
    }

    if (PG_MODAL_KEYS.has(snapshot.activeModal)) {
      pgOpenModal(snapshot.activeModal, { force: true, skipPersist: true });
    } else {
      pgCloseModal({ force: true, skipPersist: true });
    }
    pgUpdateWorkspaceChromeState();

    pgAutoCanRunFinal = false;
    pgUpdateRunBarVisibility();
    pgUpdateSaveUi();
    renderYamlBrowser();
  }

  function restorePlaygroundMessages(messages) {
    const container = $("#pg-messages");
    container.innerHTML = "";
    for (const message of messages) {
      if (message.role === "assistant") {
        const assistantEl = pgAddBubble("assistant", "");
        pgUpdateAssistantBubble(assistantEl, message.content, false);
      } else if (message.role === "error") {
        pgAddBubble("error", message.content);
      } else {
        pgAddBubble("user", message.content);
      }
    }
  }

  function normalizePersistedLogEntries(entries) {
    if (!Array.isArray(entries)) return [];
    return entries
      .filter((entry) => entry && typeof entry === "object")
      .map((entry) => ({
        type: String(entry.type || "action"),
        title: String(entry.title || ""),
        detail: entry.detail === null || entry.detail === undefined ? "" : String(entry.detail),
      }));
  }

  function normalizeGroupCollapsed(value) {
    if (!value || typeof value !== "object") return {};
    const normalized = {};
    for (const [key, collapsed] of Object.entries(value)) {
      if (!key) continue;
      normalized[key] = collapsed === true;
    }
    return normalized;
  }

  function getActiveNavView() {
    return document.querySelector(".nav-btn.active")?.dataset.view || "overview";
  }

  function activateNavView(view) {
    const normalized = normalizeNavView(view);
    const btn = document.querySelector(`.nav-btn[data-view="${normalized}"]`);
    if (btn) btn.click();
  }

  function isQuotaExceededError(error) {
    return error && (
      error.name === "QuotaExceededError" ||
      error.name === "NS_ERROR_DOM_QUOTA_REACHED" ||
      error.code === 22 ||
      error.code === 1014
    );
  }

  // ══════════════════════════════════════════════
  // ── Playground ──
  // ══════════════════════════════════════════════

  let pgMessages = [];
  let pgCurrentYaml = "";
  let pgParsedDef = null;
  let pgStepStates = {};
  let pgYamlGeneratedByAi = false;
  let pgYamlValidated = false;
  let pgSaveBusy = false;
  let pgEventSource = null;
  let pgRunning = false;
  let pgPendingInteraction = null;

  let pgAutoPhase = "idle";
  let pgAutoLlmContent = "";
  let pgAutoValidatedYaml = "";
  let pgAutoAbort = null;
  let pgAutoAssistantBubble = null;
  let pgAutoRunning = false;
  let pgAutoRound = 0;
  let pgAutoCanRunFinal = false;
  let pgAutoActorId = "";
  let pgAutoRunId = "";
  let pgAutoCommandId = "";
  let pgAutoStatusMessage = "";
  let pgAutoStatusTone = "info";
  let pgAutoMessageBuffers = new Map();
  let pgRunActorId = "";
  let pgRunRunId = "";
  let pgRunCommandId = "";
  let pgRunMessageBuffers = new Map();
  let pgLogToastTimer = null;
  let pgSidebarCollapsed = false;
  let pgChatHistoryCollapsed = false;
  let pgActiveModal = "";

  function setupPlayground() {
    $("#pg-send").addEventListener("click", () => pgSend());
    $("#pg-input").addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); pgSend(); }
    });
    $("#pg-save-btn").addEventListener("click", () => pgSaveWorkflow());
    $("#pg-run-btn").addEventListener("click", pgRunWorkflow);
    $("#pg-run-reset").addEventListener("click", pgResetRun);
    $("#pg-input").placeholder = "Describe what you want to do...";

    $("#pg-sidebar-edge-toggle")?.addEventListener("click", () => {
      pgSetSidebarCollapsed(!pgSidebarCollapsed);
    });
    const historyToggle = $("#pg-chat-history-toggle");
    historyToggle?.addEventListener("click", () => {
      pgSetChatHistoryCollapsed(!pgChatHistoryCollapsed);
    });
    historyToggle?.addEventListener("keydown", (event) => {
      if (event.key !== "Enter" && event.key !== " ")
        return;

      event.preventDefault();
      pgSetChatHistoryCollapsed(!pgChatHistoryCollapsed);
    });
    $("#pg-open-save-modal")?.addEventListener("click", () => pgOpenModal("save"));
    $("#pg-open-log-modal")?.addEventListener("click", () => pgOpenModal("log"));
    $("#pg-open-result-modal")?.addEventListener("click", () => pgOpenModal("result"));
    $("#pg-open-interaction-modal")?.addEventListener("click", () => pgOpenModal("interaction"));
    $("#pg-quick-open-log")?.addEventListener("click", () => pgOpenModal("log"));
    $("#pg-quick-open-result")?.addEventListener("click", () => pgOpenModal("result"));
    $("#pg-quick-open-interaction")?.addEventListener("click", () => pgOpenModal("interaction"));
    $("#pg-log-toast")?.addEventListener("click", () => {
      pgOpenModal("log");
    });
    $("#pg-modal-overlay")?.addEventListener("click", () => pgCloseModal());
    $$(".pg-modal-close").forEach((btn) => {
      btn.addEventListener("click", () => pgCloseModal());
    });
    document.addEventListener("keydown", (event) => {
      if (event.key === "Escape")
        pgCloseModal();
    });

    pgSetSidebarCollapsed(false, { force: true, skipPersist: true });
    pgSetChatHistoryCollapsed(false, { force: true, skipPersist: true });
    pgCloseModal({ force: true, skipPersist: true });
    pgUpdateWorkspaceChromeState();
    pgUpdateRunBarVisibility();
    pgUpdateSaveUi();
    pgRenderRunControlSummary();
  }

  function pgSetSidebarCollapsed(collapsed, options = {}) {
    const force = options.force === true;
    const next = collapsed === true;
    if (!force && next === pgSidebarCollapsed) return;
    pgSidebarCollapsed = next;

    $("#pg-workspace-main")?.classList.toggle("pg-sidebar-collapsed", pgSidebarCollapsed);
    const toggleBtn = $("#pg-sidebar-edge-toggle");
    if (toggleBtn) {
      toggleBtn.textContent = pgSidebarCollapsed ? "›" : "‹";
      toggleBtn.setAttribute("aria-expanded", String(!pgSidebarCollapsed));
      toggleBtn.setAttribute("aria-label", pgSidebarCollapsed ? "Expand run controls panel" : "Collapse run controls panel");
      toggleBtn.title = pgSidebarCollapsed ? "Expand run controls panel" : "Collapse run controls panel";
    }

    if (options.skipPersist !== true)
      scheduleUiStatePersist();
  }

  function pgSetChatHistoryCollapsed(collapsed, options = {}) {
    const force = options.force === true;
    const next = collapsed === true;
    if (!force && next === pgChatHistoryCollapsed) return;
    pgChatHistoryCollapsed = next;

    $("#pg-chat-dock")?.classList.toggle("pg-history-collapsed", pgChatHistoryCollapsed);
    const toggleBtn = $("#pg-chat-history-toggle");
    if (toggleBtn) {
      toggleBtn.setAttribute("aria-expanded", String(!pgChatHistoryCollapsed));
      toggleBtn.setAttribute("aria-label", pgChatHistoryCollapsed ? "Show prompt history" : "Hide prompt history");
      toggleBtn.title = pgChatHistoryCollapsed ? "Show prompt history" : "Hide prompt history";
      toggleBtn.classList.toggle("is-collapsed", pgChatHistoryCollapsed);
    }

    if (options.skipPersist !== true)
      scheduleUiStatePersist();
  }

  function pgOpenModal(name, options = {}) {
    if (!PG_MODAL_KEYS.has(name)) return;
    const force = options.force === true;
    if (!force && pgActiveModal === name) return;

    for (const key of PG_MODAL_KEYS)
      $(`#pg-modal-${key}`)?.classList.add("hidden");

    $(`#pg-modal-${name}`)?.classList.remove("hidden");
    $("#pg-modal-overlay")?.classList.remove("hidden");
    pgHideRecentLogToast();
    pgActiveModal = name;
    pgUpdateWorkspaceChromeState();
    $(`#pg-modal-${name} .pg-modal-close`)?.focus();

    if (options.skipPersist !== true)
      scheduleUiStatePersist();
  }

  function pgCloseModal(options = {}) {
    const force = options.force === true;
    if (!force && !pgActiveModal) return;

    for (const key of PG_MODAL_KEYS)
      $(`#pg-modal-${key}`)?.classList.add("hidden");

    $("#pg-modal-overlay")?.classList.add("hidden");
    pgActiveModal = "";
    pgUpdateWorkspaceChromeState();
    $("#pg-input")?.focus();

    if (options.skipPersist !== true)
      scheduleUiStatePersist();
  }

  function pgUpdateWorkspaceChromeState() {
    $("#pg-open-save-modal")?.classList.toggle("is-active", pgActiveModal === "save");
    $("#pg-open-log-modal")?.classList.toggle("is-active", pgActiveModal === "log");
    $("#pg-open-result-modal")?.classList.toggle("is-active", pgActiveModal === "result");
    $("#pg-open-interaction-modal")?.classList.toggle("is-active", pgActiveModal === "interaction");
  }

  function pgLogToastIcon(type) {
    const icons = {
      request: "\u25B6",
      completed: "\u2713",
      failed: "\u2717",
      suspended: "\u23F8",
      waiting: "\u23F3",
      action: "\u270D",
      done: "\u2705",
      error: "\u274C",
    };
    return icons[String(type || "").toLowerCase()] || "\uD83D\uDCDD";
  }

  function pgShowRecentLogToast(type, title, detail) {
    const toast = $("#pg-log-toast");
    if (!toast) return;
    if (pgActiveModal) return;

    const titleText = String(title || "Execution update").replace(/\s+/g, " ").trim() || "Execution update";
    const detailText = shorten(String(detail || "").replace(/\s+/g, " ").trim(), 96);

    toast.innerHTML = `
      <span class="pg-log-toast-badge">${esc(pgLogToastIcon(type))}</span>
      <span class="pg-log-toast-copy">
        <span class="pg-log-toast-title">${esc(titleText)}</span>
        <span class="pg-log-toast-detail">${esc(detailText || "Recent execution log update")}</span>
      </span>
      <span class="pg-log-toast-hint">View all</span>`;
    toast.classList.remove("hidden");

    if (pgLogToastTimer !== null) {
      window.clearTimeout(pgLogToastTimer);
      pgLogToastTimer = null;
    }
    pgLogToastTimer = window.setTimeout(() => {
      pgHideRecentLogToast();
    }, 5000);
  }

  function pgHideRecentLogToast() {
    const toast = $("#pg-log-toast");
    if (!toast) return;
    toast.classList.add("hidden");
    if (pgLogToastTimer !== null) {
      window.clearTimeout(pgLogToastTimer);
      pgLogToastTimer = null;
    }
  }

  function pgUpdateRunBarVisibility() {
    const runBar = $(".pg-run-bar");
    const runBtn = $("#pg-run-btn");
    if (!runBar || !runBtn) return;

    const hasRunnableDraft = typeof pgCurrentYaml === "string" &&
      pgCurrentYaml.trim().length > 0 &&
      !!pgParsedDef?.definition?.steps?.length;
    runBar.style.display = "grid";
    runBtn.textContent = pgRunning ? "Running..." : "Run Draft";
    runBtn.disabled = !hasRunnableDraft || pgRunning;
    runBtn.title = !hasRunnableDraft
      ? "Draft workflow is not ready yet. Generate, load, or validate YAML first."
      : (pgAutoRunning
        ? "Stop current AI drafting session and run this draft workflow."
        : (pgRunning
          ? "Workflow is currently running."
          : "Run validated draft workflow."));
    pgRenderRunControlSummary();
  }

  function pgRenderRunControlSummary() {
    const stateChip = $("#pg-run-state-chip");
    const stateNote = $("#pg-run-state-note");
    const workflowEl = $("#pg-run-meta-workflow");
    const stepEl = $("#pg-run-meta-steps");
    const logEl = $("#pg-run-meta-logs");

    const hasRunnableDraft = typeof pgCurrentYaml === "string" &&
      pgCurrentYaml.trim().length > 0 &&
      !!pgParsedDef?.definition?.steps?.length;
    const workflowName = String(pgParsedDef?.definition?.name || "").trim();
    if (workflowEl) {
      workflowEl.textContent = workflowName || (hasRunnableDraft ? "Unsaved draft" : "No draft loaded");
    }

    if (stepEl) {
      const totalSteps = Array.isArray(pgParsedDef?.definition?.steps) ? pgParsedDef.definition.steps.length : 0;
      const completedSteps = Object.values(pgStepStates || {}).filter((state) => state === "completed").length;
      const failedSteps = Object.values(pgStepStates || {}).filter((state) => state === "failed").length;
      if (totalSteps === 0) {
        stepEl.textContent = "0 / 0 steps";
      } else if (failedSteps > 0) {
        stepEl.textContent = `${completedSteps} + ${failedSteps} failed / ${totalSteps}`;
      } else {
        stepEl.textContent = `${completedSteps} / ${totalSteps} steps`;
      }
    }

    if (logEl) {
      const count = pgLogEntries.length;
      logEl.textContent = `${count} ${count === 1 ? "entry" : "entries"}`;
    }

    if (!stateChip || !stateNote) return;

    let label = "Idle";
    let tone = "idle";
    let note = "Ready.";
    if (pgPendingInteraction) {
      const interactionType = String(pgPendingInteraction.type || "").toLowerCase();
      label = interactionType === "wait_signal" ? "Waiting Signal" : "Needs Input";
      tone = "warn";
      note = interactionType === "wait_signal"
        ? "Waiting for signal."
        : "Waiting for input.";
    } else if (pgRunning || (pgAutoRunning && pgAutoPhase === "executing")) {
      label = "Running";
      tone = "info";
      note = pgAutoStatusMessage || "Running.";
    } else if (pgAutoRunning && pgAutoPhase === "approval") {
      label = "Review";
      tone = "warn";
      note = pgAutoStatusMessage || "Review draft.";
    } else if (pgAutoRunning) {
      label = "Planning";
      tone = "info";
      note = pgAutoStatusMessage || "Planning.";
    } else if (pgAutoStatusTone === "error") {
      label = "Failed";
      tone = "error";
      note = pgAutoStatusMessage || "Run failed.";
    } else if (pgAutoStatusTone === "success") {
      label = "Completed";
      tone = "success";
      note = pgAutoStatusMessage || "Run completed.";
    } else if (hasRunnableDraft) {
      label = "Ready";
      tone = "ready";
      note = "Draft ready.";
    }

    stateChip.textContent = label;
    stateChip.className = `pg-run-state-chip tone-${tone}`;
    stateNote.textContent = note;
  }

  function pgSetAutoStatus(message, tone = "info") {
    const el = $("#pg-auto-status");
    if (!el) return;
    if (!message) {
      pgAutoStatusMessage = "";
      pgAutoStatusTone = "info";
      el.textContent = "";
      el.className = "pg-auto-status hidden";
      pgRenderRunControlSummary();
      scheduleUiStatePersist();
      return;
    }
    pgAutoStatusMessage = String(message);
    pgAutoStatusTone = String(tone || "info");
    el.textContent = message;
    el.className = `pg-auto-status ${tone}`;
    pgRenderRunControlSummary();
    scheduleUiStatePersist();
  }

  function pgSetSaveStatus(message, tone = "info") {
    const el = $("#pg-save-status");
    if (!el) return;
    if (!message) {
      el.textContent = "";
      el.className = "pg-save-status hidden";
      return;
    }

    el.textContent = message;
    el.className = `pg-save-status ${tone}`;
  }

  function pgCanSaveCurrentYaml() {
    return !pgSaveBusy &&
      !pgAutoRunning &&
      !pgRunning &&
      typeof pgCurrentYaml === "string" &&
      pgCurrentYaml.trim().length > 0 &&
      pgYamlGeneratedByAi === true &&
      pgYamlValidated === true;
  }

  function pgUpdateSaveUi() {
    const saveBtn = $("#pg-save-btn");
    if (saveBtn) saveBtn.disabled = !pgCanSaveCurrentYaml();
  }

  async function pgSaveWorkflow(overwrite = false) {
    if (!pgCanSaveCurrentYaml()) return;

    const filenameInput = $("#pg-save-filename");
    const requestedFilename = filenameInput?.value?.trim() || "";
    pgSaveBusy = true;
    pgUpdateSaveUi();
    pgSetSaveStatus(overwrite ? "正在覆盖已存在的 workflow 文件…" : "正在保存到 ~/.aevatar/workflows …", "info");

    try {
      const data = await postJson("/api/playground/workflows", {
        yaml: pgCurrentYaml,
        filename: requestedFilename,
        overwrite,
      });

      if (filenameInput && data?.filename)
        filenameInput.value = data.filename;
      pgSetSaveStatus(`已保存到 ${data?.path || "~/.aevatar/workflows"}`, "success");
      await loadWorkflows();
      renderYamlBrowser();
    } catch (e) {
      const message = e?.message || String(e);
      if (!overwrite && message.includes("already exists")) {
        const confirmed = window.confirm(`${message}\n\n是否覆盖现有文件？`);
        if (confirmed) {
          pgSaveBusy = false;
          pgUpdateSaveUi();
          await pgSaveWorkflow(true);
          return;
        }
        pgSetSaveStatus(message, "warn");
      } else {
        pgSetSaveStatus(message, "error");
      }
    } finally {
      pgSaveBusy = false;
      pgUpdateSaveUi();
      scheduleUiStatePersist();
    }
  }

  function pgSuggestFilename(name) {
    const normalized = String(name || "")
      .trim()
      .toLowerCase()
      .replace(/\.(ya?ml)$/i, "")
      .replace(/[^a-z0-9_-]+/g, "_")
      .replace(/_+/g, "_")
      .replace(/^_+|_+$/g, "");
    return normalized ? `${normalized}.yaml` : "";
  }

  function pgExtractLastYaml(text) {
    const regex = /```(?:ya?ml)?\s*\n([\s\S]*?)```/gi;
    let lastYaml = null;
    let m;
    while ((m = regex.exec(text)) !== null) lastYaml = m[1].trim();
    return lastYaml;
  }

  async function pgSend() {
    await pgAutoSend();
  }

  function pgAddBubble(role, text) {
    const container = $("#pg-messages");
    const el = document.createElement("div");
    el.className = `pg-msg ${role}`;
    if (role === "error") {
      el.textContent = text;
    } else if (role === "user") {
      el.textContent = text;
    }
    container.appendChild(el);
    container.scrollTop = container.scrollHeight;
    return el;
  }

  function pgUpdateAssistantBubble(el, text, streaming) {
    const parts = pgSplitYamlBlocks(text);
    let html = "";
    for (const part of parts) {
      if (part.type === "yaml") {
        html += `<div class="pg-msg-yaml" data-yaml="${esc(part.content).replace(/"/g, "&quot;")}">${esc(part.content)}</div>`;
      } else {
        html += esc(part.content);
      }
    }
    if (streaming) html += '<span class="pg-typing"></span>';
    el.innerHTML = html;

    el.querySelectorAll(".pg-msg-yaml").forEach((yamlEl) => {
      yamlEl.addEventListener("click", () => {
        const yaml = yamlEl.dataset.yaml;
        if (!yaml) return;
        pgApplyYaml(yaml, { generatedByAi: true });
      });
    });

    const container = $("#pg-messages");
    container.scrollTop = container.scrollHeight;
  }

  function pgSplitYamlBlocks(text) {
    const parts = [];
    const regex = /```(?:ya?ml)?\s*\n([\s\S]*?)```/gi;
    let last = 0;
    let m;
    while ((m = regex.exec(text)) !== null) {
      if (m.index > last) parts.push({ type: "text", content: text.slice(last, m.index) });
      parts.push({ type: "yaml", content: m[1].trim() });
      last = m.index + m[0].length;
    }
    if (last < text.length) parts.push({ type: "text", content: text.slice(last) });
    return parts;
  }

  async function pgExtractAndRenderYaml(text, options = {}) {
    const lastYaml = pgExtractLastYaml(text);
    if (!lastYaml) return { valid: false, error: "No YAML block found." };
    return pgApplyYaml(lastYaml, options);
  }

  function pgApplyValidatedAutoYaml(markdownText) {
    const yaml = pgExtractLastYaml(markdownText || "");
    if (!yaml) return false;
    pgAutoValidatedYaml = yaml;
    pgApplyYaml(yaml, { generatedByAi: true }).catch(() => { });
    return true;
  }

  async function pgApplyYaml(yaml, options = {}) {
    pgCurrentYaml = yaml;
    pgYamlGeneratedByAi = options.generatedByAi === true;
    pgYamlValidated = false;
    pgParsedDef = null;
    pgResetRun();
    pgSetSaveStatus("", "info");

    const yamlEl = $("#pg-yaml");
    yamlEl.innerHTML = yaml.split("\n").map(highlightYamlLine).join("\n");
    renderYamlBrowser();

    try {
      const res = await fetch("/api/playground/parse", {
        method: "POST",
        headers: { "Content-Type": "text/plain" },
        body: yaml,
      });
      const data = await res.json();
      if (data.valid && data.definition?.steps) {
        pgParsedDef = data;
        pgYamlValidated = true;
        renderFlowInto($("#pg-graph"), data.definition.steps, data.edges, null);
        pgUpdateRunBarVisibility();
        const suggestedFilename = options.preferredFilename || pgSuggestFilename(data.definition.name);
        if (suggestedFilename)
          $("#pg-save-filename").value = suggestedFilename;
        pgUpdateSaveUi();
        renderYamlBrowser();
        scheduleUiStatePersist();
        return { valid: true, definition: data.definition };
      } else {
        const errorMessage = data.error || "Invalid YAML";
        pgParsedDef = null;
        pgYamlValidated = false;
        pgUpdateRunBarVisibility();
        $("#pg-graph").innerHTML = `<p style="color:var(--state-failed);padding:16px;font-size:12px">${esc(errorMessage)}</p>`;
        if (pgYamlGeneratedByAi) {
          pgSetSaveStatus(errorMessage || "当前 YAML 尚未通过校验，暂时不能保存。", "warn");
        }
        pgUpdateSaveUi();
        renderYamlBrowser();
        scheduleUiStatePersist();
        return { valid: false, error: errorMessage };
      }
    } catch (e) {
      pgParsedDef = null;
      pgYamlValidated = false;
      pgUpdateRunBarVisibility();
      $("#pg-graph").innerHTML = `<p style="color:var(--state-failed);padding:16px;font-size:12px">${esc(e.message)}</p>`;
      if (pgYamlGeneratedByAi) {
        pgSetSaveStatus(e.message || "当前 YAML 尚未通过校验，暂时不能保存。", "error");
      }
      pgUpdateSaveUi();
      renderYamlBrowser();
      scheduleUiStatePersist();
      return { valid: false, error: e.message || String(e) };
    }
  }

  async function streamWorkflowChatRun(payload, onFrame, options = {}) {
    const res = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
      signal: options.signal,
    });

    if (!res.ok) {
      const raw = await res.text();
      if (raw) {
        try {
          const parsed = JSON.parse(raw);
          const code = parsed?.code ? `[${parsed.code}] ` : "";
          const message = parsed?.message || raw;
          throw new Error(`${code}${message}`);
        } catch {
          throw new Error(raw);
        }
      }

      throw new Error(`HTTP ${res.status}`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buf = "";

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += decoder.decode(value, { stream: true });
      let nlIdx;
      while ((nlIdx = buf.indexOf("\n")) !== -1) {
        const line = buf.slice(0, nlIdx).trim();
        buf = buf.slice(nlIdx + 1);
        if (!line.startsWith("data: ")) continue;
        const raw = line.slice(6);
        if (!raw) continue;
        try {
          const frame = JSON.parse(raw);
          onFrame(frame);
        } catch { }
      }
    }
  }

  function pgNormalizeCustomPayload(value) {
    if (!value || typeof value !== "object") return {};
    return value;
  }

  function readCustomValue(value, camelKey, pascalKey) {
    if (!value || typeof value !== "object") return "";
    return value[camelKey] ?? value[pascalKey] ?? "";
  }

  function normalizePlainObject(value) {
    if (!value || typeof value !== "object" || Array.isArray(value)) return {};
    return value;
  }

  function normalizeExecutionMetadata(value) {
    return normalizePlainObject(value);
  }

  function isTelegramWaitReplyStep(data) {
    const metadata = normalizeExecutionMetadata(data?.metadata || {});
    const operation = String(
      metadata["llm.operation"] ||
      metadata.operation ||
      "")
      .trim()
      .toLowerCase();
    if (operation === "/waitreply" || operation === "wait_reply" || operation === "/wait_reply") {
      return true;
    }

    // Backward-compatible fallback for built-in OpenClaw demo workflow.
    const stepId = String(data?.stepId || "").trim().toLowerCase();
    return stepId === "wait_openclaw_group_stream";
  }

  function appendStepCompletedExecutionLog(data, logFn) {
    if (!data || typeof logFn !== "function") return;

    const success = data.success === true;
    const type = success ? "completed" : "failed";
    const title = `${success ? "\u2713" : "\u2717"} ${data.stepId || ""}`.trim();
    const detail = success
      ? (data.output ?? "")
      : (data.error || "Failed");

    if (success && isTelegramWaitReplyStep(data)) {
      logFn(type, title, detail, {
        forceFold: true,
        detailOpen: true,
        toggleLabel: "Step output (full)",
        skipToast: true,
      });
      return;
    }

    logFn(type, title, detail);
  }

  function appendTelegramReplyExecutionLog(data, logFn) {
    if (!data || data.success !== true || !isTelegramWaitReplyStep(data)) return;
    const replyText = data.output === null || data.output === undefined
      ? ""
      : String(data.output);
    if (!replyText.trim()) return;
    logFn("llm", "\uD83D\uDCAC Telegram stream reply", replyText, {
      forceFold: true,
      detailOpen: true,
      toggleLabel: "Telegram full reply",
      skipToast: true,
    });
  }

  function isSecureInteraction(interaction) {
    if (!interaction || typeof interaction !== "object") return false;
    if (String(interaction.type || "").toLowerCase() === "secure_input") return true;
    return String(interaction.metadata?.secure || "").toLowerCase() === "true";
  }

  function pgExtractRunOutput(result) {
    if (result == null) return "";
    if (typeof result === "string") return result;
    if (typeof result === "object" && typeof result.output === "string") return result.output;
    try { return JSON.stringify(result, null, 2); } catch { return String(result); }
  }

  function pgMapCustomFrameToEvent(frame) {
    const name = String(frame.name || "");
    const value = pgNormalizeCustomPayload(frame.value);
    if (!name) return null;

    if (name === "aevatar.step.request") {
      return {
        eventType: "step.request",
        data: {
          runId: readCustomValue(value, "runId", "RunId"),
          stepId: readCustomValue(value, "stepId", "StepId"),
          stepType: readCustomValue(value, "stepType", "StepType"),
          input: readCustomValue(value, "input", "Input"),
          targetRole: readCustomValue(value, "targetRole", "TargetRole"),
        },
      };
    }

    if (name === "aevatar.step.completed") {
      return {
        eventType: "step.completed",
        data: {
          runId: readCustomValue(value, "runId", "RunId"),
          stepId: readCustomValue(value, "stepId", "StepId"),
          success: value.success === true,
          output: readCustomValue(value, "output", "Output"),
          error: value.error ?? value.Error ?? null,
          metadata: normalizeExecutionMetadata(value.metadata || value.Metadata || {}),
        },
      };
    }

    if (name === "aevatar.human_input.request") {
      return {
        eventType: "workflow.suspended",
        data: {
          runId: readCustomValue(value, "runId", "RunId"),
          stepId: readCustomValue(value, "stepId", "StepId"),
          suspensionType: readCustomValue(value, "suspensionType", "SuspensionType") || "human_input",
          prompt: readCustomValue(value, "prompt", "Prompt"),
          timeoutSeconds: value.timeoutSeconds ?? value.TimeoutSeconds ?? 0,
          metadata: normalizePlainObject(value.metadata || value.Metadata),
        },
      };
    }

    if (name === "aevatar.workflow.waiting_signal") {
      return {
        eventType: "workflow.waiting_signal",
        data: {
          runId: readCustomValue(value, "runId", "RunId"),
          stepId: readCustomValue(value, "stepId", "StepId"),
          signalName: readCustomValue(value, "signalName", "SignalName"),
          prompt: readCustomValue(value, "prompt", "Prompt"),
          timeoutMs: value.timeoutMs ?? value.TimeoutMs ?? 0,
        },
      };
    }

    if (name === "aevatar.llm.reasoning") {
      return {
        eventType: "llm.thinking",
        data: {
          role: readCustomValue(value, "role", "Role") || "assistant",
          content: readCustomValue(value, "delta", "Delta"),
        },
      };
    }

    return null;
  }

  function pgAutoHandleWorkflowFrame(frame) {
    const type = String(frame.type || "");
    if (!type) return;

    if (type === "RUN_STARTED") {
      pgAutoActorId = frame.threadId || pgAutoActorId;
      return;
    }

    if (type === "TEXT_MESSAGE_START") {
      const messageId = frame.messageId || "__default__";
      pgAutoMessageBuffers.set(messageId, "");
      return;
    }

    if (type === "TEXT_MESSAGE_CONTENT") {
      const messageId = frame.messageId || "__default__";
      const prev = pgAutoMessageBuffers.get(messageId) || "";
      const next = prev + (frame.delta || "");
      pgAutoMessageBuffers.set(messageId, next);
      if (pgAutoPhase === "planning" || pgAutoPhase === "approval") {
        pgAutoHandleSseEvent("llm.response", { role: frame.role || "assistant", content: next });
      }
      return;
    }

    if (type === "TEXT_MESSAGE_END") {
      const messageId = frame.messageId || "__default__";
      const content = pgAutoMessageBuffers.get(messageId) || "";
      pgAutoHandleSseEvent("llm.response", { role: frame.role || "assistant", content });
      pgAutoMessageBuffers.delete(messageId);
      return;
    }

    if (type === "CUSTOM") {
      const customName = String(frame.name || "");
      if (customName === "aevatar.run.context") {
        const value = pgNormalizeCustomPayload(frame.value);
        const commandId = readCustomValue(value, "commandId", "CommandId");
        const actorId = readCustomValue(value, "actorId", "ActorId");
        if (commandId) pgAutoCommandId = commandId;
        if (actorId) pgAutoActorId = actorId;
        return;
      }

      const mapped = pgMapCustomFrameToEvent(frame);
      if (!mapped) return;
      if (mapped.data?.runId &&
          (mapped.eventType === "step.request" ||
            mapped.eventType === "step.completed" ||
            mapped.eventType === "workflow.suspended")) {
        pgAutoRunId = mapped.data.runId;
      }
      mapped.data.actorId = pgAutoActorId || "";
      pgAutoHandleSseEvent(mapped.eventType, mapped.data);
      return;
    }

    if (type === "RUN_FINISHED") {
      pgAutoHandleSseEvent("workflow.completed", {
        runId: pgAutoRunId || "",
        success: true,
        output: pgExtractRunOutput(frame.result),
      });
      return;
    }

    if (type === "RUN_ERROR") {
      pgAutoHandleSseEvent("workflow.error", {
        runId: pgAutoRunId || "",
        error: frame.message || "Workflow run failed.",
      });
    }
  }

  function pgHandleWorkflowFrame(frame) {
    const type = String(frame.type || "");
    if (!type) return;

    if (type === "RUN_STARTED") {
      pgRunActorId = frame.threadId || pgRunActorId;
      return;
    }

    if (type === "TEXT_MESSAGE_START") {
      const messageId = frame.messageId || "__default__";
      pgRunMessageBuffers.set(messageId, "");
      return;
    }

    if (type === "TEXT_MESSAGE_CONTENT") {
      const messageId = frame.messageId || "__default__";
      const prev = pgRunMessageBuffers.get(messageId) || "";
      pgRunMessageBuffers.set(messageId, prev + (frame.delta || ""));
      return;
    }

    if (type === "TEXT_MESSAGE_END") {
      const messageId = frame.messageId || "__default__";
      const content = pgRunMessageBuffers.get(messageId) || "";
      pgRunMessageBuffers.delete(messageId);
      pgHandleSseEvent("llm.response", { role: frame.role || "assistant", content });
      return;
    }

    if (type === "CUSTOM") {
      const customName = String(frame.name || "");
      if (customName === "aevatar.run.context") {
        const value = pgNormalizeCustomPayload(frame.value);
        const commandId = readCustomValue(value, "commandId", "CommandId");
        const actorId = readCustomValue(value, "actorId", "ActorId");
        if (commandId) pgRunCommandId = commandId;
        if (actorId) pgRunActorId = actorId;
        return;
      }

      const mapped = pgMapCustomFrameToEvent(frame);
      if (!mapped) return;
      if (mapped.data?.runId &&
          (mapped.eventType === "step.request" ||
            mapped.eventType === "step.completed" ||
            mapped.eventType === "workflow.suspended")) {
        pgRunRunId = mapped.data.runId;
      }
      mapped.data.actorId = pgRunActorId || "";
      pgHandleSseEvent(mapped.eventType, mapped.data);
      return;
    }

    if (type === "RUN_FINISHED") {
      pgHandleSseEvent("workflow.completed", {
        runId: pgRunRunId || "",
        success: true,
        output: pgExtractRunOutput(frame.result),
      });
      return;
    }

    if (type === "RUN_ERROR") {
      pgHandleSseEvent("workflow.error", {
        runId: pgRunRunId || "",
        error: frame.message || "Workflow run failed.",
      });
    }
  }

  // ── Playground: Auto Mode ──

  async function pgAutoSend() {
    const input = $("#pg-input");
    const text = input.value.trim();
    if (!text || pgAutoRunning) return;

    input.value = "";
    pgAutoReset();
    pgMessages.push({ role: "user", content: text });
    pgAddBubble("user", text);
    scheduleUiStatePersist();

    pgAutoRunning = true;
    pgAutoPhase = "planning";
    pgAutoRound = 0;
    pgAutoLlmContent = "";
    pgAutoAssistantBubble = null;
    pgAutoCanRunFinal = false;
    pgAutoActorId = "";
    pgAutoRunId = "";
    pgAutoCommandId = "";
    pgAutoMessageBuffers = new Map();
    pgAutoAbort = new AbortController();
    pgUpdateRunBarVisibility();
    pgSetAutoStatus("AI 正在分析需求并生成 workflow 草稿…", "info");
    $("#pg-send").disabled = true;
    $("#pg-exec-log").classList.remove("hidden");
    pgSetChatHistoryCollapsed(true, { skipPersist: true });

    try {
      await streamWorkflowChatRun(
        {
          prompt: text,
          metadata: {
            "workflow.authoring.enabled": "true",
            "workflow.intent": "workflow_authoring",
          },
        },
        pgAutoHandleWorkflowFrame,
        { signal: pgAutoAbort.signal });
    } catch (e) {
      if (e.name !== "AbortError") pgAddBubble("error", e.message);
    }

    if (pgAutoRunning) {
      pgAutoRunning = false;
      pgAutoPhase = "idle";
      pgSetAutoStatus("", "info");
      $("#pg-send").disabled = false;
      scheduleUiStatePersist();
    }
    pgAutoAbort = null;
  }

  function pgAutoHandleSseEvent(eventType, data) {
    if (eventType === "step.request") {
      if (data.runId) pgAutoRunId = data.runId;
      pgStepStates[data.stepId] = "running";
      pgUpdateGraphNode(data.stepId);
      pgAddLog("request", `\u25B6 ${data.stepId} (${data.stepType})`, data.input);
    } else if (eventType === "step.completed") {
      if (data.runId) pgAutoRunId = data.runId;
      pgStepStates[data.stepId] = data.success ? "completed" : "failed";
      pgUpdateGraphNode(data.stepId);
      appendStepCompletedExecutionLog(data, pgAddLog);
      appendTelegramReplyExecutionLog(data, pgAddLog);
      if (data.stepId === "validate_yaml" && data.success && data.output) {
        pgApplyValidatedAutoYaml(data.output);
      }
    } else if (eventType === "llm.response") {
      if (pgAutoPhase === "planning" || pgAutoPhase === "approval") {
        pgAutoLlmContent = data.content || "";
        if (!pgAutoAssistantBubble) {
          pgAutoAssistantBubble = pgAddBubble("assistant", "");
        }
        pgUpdateAssistantBubble(pgAutoAssistantBubble, pgAutoLlmContent, false);
      } else {
        const finalContent = absorbInlineThinking(pgThinkingBuffers, data.role, data.content);
        flushThinkingToLog(pgThinkingBuffers, data.role, pgAddLog);
        if (finalContent) {
          pgAddLog("llm", `\uD83E\uDD16 ${data.role}`, finalContent);
        }
      }
    } else if (eventType === "llm.thinking") {
      appendThinkingDelta(pgThinkingBuffers, data.role, data.content);
    } else if (eventType === "workflow.suspended") {
      if (data.runId) pgAutoRunId = data.runId;
      pgAddLog("suspended",
        `\u23F8 ${data.stepId} (${data.suspensionType})`,
        data.prompt || "Waiting for human action");
      if (data.suspensionType === "human_approval" && pgAutoPhase === "planning") {
        pgAutoPhase = "approval";
        pgAutoRound += 1;
        pgSetAutoStatus(`进入人工确认（第 ${pgAutoRound} 轮）：可继续优化，或同意后直接执行。`, "warn");
      }
      setPlaygroundInteraction({
        type: data.suspensionType || "human_input",
        actorId: data.actorId || pgAutoActorId || "",
        runId: data.runId || "",
        commandId: pgAutoCommandId || "",
        stepId: data.stepId || "",
        prompt: data.prompt || "",
        timeoutSeconds: data.timeoutSeconds || 0,
        metadata: data.metadata || {},
      });
    } else if (eventType === "workflow.waiting_signal") {
      if (data.runId && !pgAutoRunId) pgAutoRunId = data.runId;
      pgAddLog("waiting",
        `\u23F3 ${data.stepId} (${data.signalName})`,
        data.prompt || "Waiting for external signal");
      setPlaygroundInteraction({
        type: "wait_signal",
        actorId: data.actorId || pgAutoActorId || "",
        runId: data.runId || "",
        commandId: pgAutoCommandId || "",
        stepId: data.stepId || "",
        signalName: data.signalName || "",
        prompt: data.prompt || "",
        timeoutMs: data.timeoutMs || 0,
      });
    } else if (eventType === "workflow.completed") {
      pgAddLog("done", data.success ? "\u2705 Completed" : "\u274C Failed", "");
      $("#pg-result-section").classList.remove("hidden");
      const pre = $("#pg-result");
      pre.textContent = data.success ? data.output : (data.error || "Failed");
      pre.className = `result-pre ${data.success ? "success" : "failure"}`;
      pgOpenModal("result", { skipPersist: true });
      if (data.success) {
        const finalText = data.output || "";
        const finalYaml = pgAutoValidatedYaml;
        if (finalYaml) {
          pgApplyYaml(finalYaml).catch(() => { });
        }
        pgAutoCanRunFinal = false;
        pgUpdateRunBarVisibility();
        pgSetAutoStatus("执行完成：定稿 workflow 已自动运行。", "success");
        if (finalText) {
          pgAddBubble("assistant", finalText);
        }
      }
      setPlaygroundInteraction(null);
      pgAutoRunning = false;
      pgAutoPhase = "idle";
      $("#pg-send").disabled = false;
    } else if (eventType === "workflow.error") {
      pgAddLog("error", "\u274C Error", data.error);
      pgSetAutoStatus("执行出错，请调整需求或反馈后重试。", "error");
      setPlaygroundInteraction(null);
      pgAutoRunning = false;
      pgAutoPhase = "idle";
      $("#pg-send").disabled = false;
    }
  }

  function pgAutoReset() {
    pgStepStates = {};
    pgLogEntries = [];
    pgThinkingBuffers = new Map();
    pgAutoLlmContent = "";
    pgAutoValidatedYaml = "";
    pgAutoAssistantBubble = null;
    pgAutoPhase = "idle";
    pgAutoRunning = false;
    pgAutoRound = 0;
    pgAutoCanRunFinal = false;
    pgAutoActorId = "";
    pgAutoRunId = "";
    pgAutoCommandId = "";
    pgAutoMessageBuffers = new Map();
    setPlaygroundInteraction(null);
    $("#pg-exec-log").classList.add("hidden");
    $("#pg-result-section").classList.add("hidden");
    $("#pg-log-entries").innerHTML = "";
    $("#pg-yaml").innerHTML = "";
    $("#pg-graph").innerHTML = "";
    pgHideRecentLogToast();
    pgSetAutoStatus("", "info");
    pgSetSaveStatus("", "info");
    pgCurrentYaml = "";
    pgParsedDef = null;
    pgYamlGeneratedByAi = false;
    pgYamlValidated = false;
    $("#pg-save-filename").value = "";
    pgUpdateRunBarVisibility();
    if (pgActiveModal && PG_MODAL_KEYS.has(pgActiveModal))
      pgCloseModal({ force: true, skipPersist: true });
    pgUpdateWorkspaceChromeState();
    pgUpdateSaveUi();
    renderYamlBrowser();
    scheduleUiStatePersist();
  }

  function renderAutoApprovalPanel(container, interaction) {
    const actorId = interaction.actorId || "";
    const runId = interaction.runId || "";
    const commandId = interaction.commandId || pgAutoCommandId || "";
    const stepId = interaction.stepId || "";
    const prompt = interaction.prompt || "Review the generated workflow.";

    container.innerHTML = `
      <div class="auto-approval-card">
        <div class="auto-approval-header">
          <span class="auto-approval-icon">\uD83D\uDCCB</span>
          <strong>Review Generated Workflow</strong>
        </div>
        <div class="auto-approval-prompt">${esc(prompt)}</div>
        <div class="auto-approval-hint">Review the YAML and flow graph above. Approve to execute immediately, or reject with feedback to refine.</div>
        <textarea id="pg-auto-feedback" class="interaction-input" rows="2" placeholder="补充你的约束、偏好或修改建议…"></textarea>
        <div class="interaction-actions">
          <button id="pg-auto-approve" class="btn btn-primary">同意定稿</button>
          <button id="pg-auto-reject" class="btn btn-danger">继续优化</button>
        </div>
      </div>
    `;

    const approveBtn = $("#pg-auto-approve");
    const rejectBtn = $("#pg-auto-reject");
    const feedbackEl = $("#pg-auto-feedback");

    if (!approveBtn || !rejectBtn || !feedbackEl || !runId || !actorId) return;

    const submit = async (approved) => {
      approveBtn.disabled = true;
      rejectBtn.disabled = true;
      const feedback = feedbackEl.value.trim();
      try {
        await postJson("/api/workflows/resume", {
          actorId,
          runId,
          stepId,
          commandId,
          approved,
          // Approve should execute current draft directly; avoid replacing YAML with comment text.
          userInput: approved ? "" : feedback,
        });
        if (approved) {
          pgAddBubble("user", feedback || "同意，直接执行这个版本。");
        } else {
          pgAddBubble("user", feedback || "继续优化这个 workflow。");
        }
        pgAddLog(
          "action",
          approved ? "\u270D 已同意定稿" : "\u270D 已提交优化建议",
          feedback || "(no feedback)");
        if (approved) {
          if (pgAutoAbort) {
            try { pgAutoAbort.abort(); } catch { }
            pgAutoAbort = null;
          }
          pgAutoRunning = false;
          pgAutoPhase = "executing";
          pgAutoCanRunFinal = false;
          pgUpdateRunBarVisibility();
          pgSetAutoStatus("已同意，正在运行定稿 workflow…", "info");
          if (pgAutoValidatedYaml) {
            pgCurrentYaml = pgAutoValidatedYaml;
            await pgRunWorkflow();
          }
        } else {
          pgAutoPhase = "planning";
          pgAutoLlmContent = "";
          pgAutoAssistantBubble = null;
          pgSetAutoStatus("已提交优化建议，等待 AI 给出新草稿…", "info");
        }
        setPlaygroundInteraction(null);
      } catch (e) {
        pgAddLog("error", "\u274C Resume failed", e.message || String(e));
        approveBtn.disabled = false;
        rejectBtn.disabled = false;
      }
    };

    approveBtn.addEventListener("click", () => submit(true));
    rejectBtn.addEventListener("click", () => submit(false));
  }

  // ── Playground: Run Workflow ──

  async function pgRunWorkflow() {
    if (!pgCurrentYaml || pgRunning) return;
    if (pgAutoRunning) {
      if (pgAutoAbort) {
        try { pgAutoAbort.abort(); } catch { }
        pgAutoAbort = null;
      }
      pgAutoRunning = false;
      pgAutoPhase = "idle";
      pgAutoCanRunFinal = false;
      pgAutoMessageBuffers = new Map();
      $("#pg-send").disabled = false;
      setPlaygroundInteraction(null);
      pgSetAutoStatus("Switched to manual run for the current draft.", "info");
    }
    pgResetRun();
    pgRunning = true;
    pgRunActorId = "";
    pgRunRunId = "";
    pgRunCommandId = "";
    pgRunMessageBuffers = new Map();
    setPlaygroundInteraction(null);
    pgUpdateRunBarVisibility();
    $("#pg-run-reset").classList.remove("hidden");
    $("#pg-exec-log").classList.remove("hidden");
    pgSetChatHistoryCollapsed(true, { skipPersist: true });

    pgStepStates = {};
    if (pgParsedDef?.definition?.steps) {
      for (const s of pgParsedDef.definition.steps) pgStepStates[s.id] = "pending";
    }
    renderFlowInto($("#pg-graph"), pgParsedDef?.definition?.steps || [], pgParsedDef?.edges || [], pgStepStates);

    const input = $("#pg-run-input").value || "Hello, world!";
    pgSetAutoStatus("正在运行定稿 workflow…", "info");

    try {
      await streamWorkflowChatRun(
        {
          prompt: input,
          workflowYamls: [pgCurrentYaml],
        },
        pgHandleWorkflowFrame);
      if (pgRunning) {
        pgRunning = false;
        pgUpdateRunBarVisibility();
        pgSetAutoStatus("定稿 workflow 已运行完成。", "success");
      }
    } catch (e) {
      pgAddLog("error", "\u274C Error", e.message);
      pgSetAutoStatus("运行失败，请返回继续优化。", "error");
      pgRunning = false;
      pgUpdateRunBarVisibility();
    }
  }

  function pgHandleSseEvent(eventType, data) {
    if (eventType === "step.request") {
      if (data.runId) pgRunRunId = data.runId;
      pgStepStates[data.stepId] = "running";
      pgUpdateGraphNode(data.stepId);
      pgAddLog("request", `\u25B6 ${data.stepId} (${data.stepType})`, data.input);
    } else if (eventType === "step.completed") {
      if (data.runId) pgRunRunId = data.runId;
      pgStepStates[data.stepId] = data.success ? "completed" : "failed";
      pgUpdateGraphNode(data.stepId);
      appendStepCompletedExecutionLog(data, pgAddLog);
      appendTelegramReplyExecutionLog(data, pgAddLog);
    } else if (eventType === "llm.response") {
      const finalContent = absorbInlineThinking(pgThinkingBuffers, data.role, data.content);
      flushThinkingToLog(pgThinkingBuffers, data.role, pgAddLog);
      if (finalContent) {
        pgAddLog("llm", `\uD83E\uDD16 ${data.role}`, finalContent);
      }
    } else if (eventType === "llm.thinking") {
      appendThinkingDelta(pgThinkingBuffers, data.role, data.content);
    } else if (eventType === "workflow.suspended") {
      if (data.runId) pgRunRunId = data.runId;
      pgAddLog("suspended", `\u23F8 ${data.stepId} (${data.suspensionType})`, data.prompt || "Waiting for human action");
      setPlaygroundInteraction({
        type: data.suspensionType || "human_input",
        actorId: data.actorId || pgRunActorId || "",
        runId: data.runId || "",
        commandId: pgRunCommandId || "",
        stepId: data.stepId || "",
        prompt: data.prompt || "",
        timeoutSeconds: data.timeoutSeconds || 0,
        metadata: data.metadata || {},
      });
    } else if (eventType === "workflow.waiting_signal") {
      if (data.runId && !pgRunRunId) pgRunRunId = data.runId;
      pgAddLog("waiting", `\u23F3 ${data.stepId} (${data.signalName})`, data.prompt || "Waiting for external signal");
      setPlaygroundInteraction({
        type: "wait_signal",
        actorId: data.actorId || pgRunActorId || "",
        runId: data.runId || "",
        commandId: pgRunCommandId || "",
        stepId: data.stepId || "",
        signalName: data.signalName || "",
        prompt: data.prompt || "",
        timeoutMs: data.timeoutMs || 0,
      });
    } else if (eventType === "workflow.completed") {
      pgAddLog("done", data.success ? "\u2705 Workflow completed" : "\u274C Workflow failed", "");
      $("#pg-result-section").classList.remove("hidden");
      const pre = $("#pg-result");
      pre.textContent = data.success ? data.output : (data.error || "Failed");
      pre.className = `result-pre ${data.success ? "success" : "failure"}`;
      pgOpenModal("result", { skipPersist: true });
      setPlaygroundInteraction(null);
      pgRunning = false;
      pgUpdateRunBarVisibility();
      pgSetAutoStatus(data.success ? "定稿 workflow 已运行完成。" : "运行失败，请返回继续优化。", data.success ? "success" : "error");
    } else if (eventType === "workflow.error") {
      pgAddLog("error", "\u274C Error", data.error);
      setPlaygroundInteraction(null);
      pgRunning = false;
      pgUpdateRunBarVisibility();
      pgSetAutoStatus("运行失败，请返回继续优化。", "error");
    }
  }

  function pgUpdateGraphNode(stepId) {
    const node = document.querySelector(`#pg-graph .step-node[data-step="${stepId}"]`);
    if (!node) return;
    node.setAttribute("class", `step-node state-${pgStepStates[stepId] || "pending"}`);
    const status = node.querySelector(".step-status");
    if (status) {
      const s = pgStepStates[stepId];
      status.textContent = s === "completed" ? "\u2713" : s === "failed" ? "\u2717" : s === "running" ? "\u25CB" : "";
    }
  }

  function pgAddLog(type, title, detail, options = {}) {
    const normalizedDetail = detail === null || detail === undefined ? "" : String(detail);
    if (!options.skipStore) {
      pgLogEntries.push({
        type: String(type || "action"),
        title: String(title || ""),
        detail: normalizedDetail,
      });
      scheduleUiStatePersist();
    }

    const el = document.createElement("div");
    el.className = `log-entry log-${type}`;
    const icons = { request: "\u25B6", completed: "\u2713", failed: "\u2717", llm: "\uD83E\uDD16", thinking: "\uD83E\uDDE0", suspended: "\u23F8", waiting: "\u23F3", action: "\u270D", done: "\u2705", error: "\u274C" };
    const detailHtml = buildLogDetailHtml(normalizedDetail, options);
    el.innerHTML = `<span class="log-icon">${icons[type] || ""}</span><span class="log-text"><strong>${esc(title)}</strong>${detailHtml ? `<br>${detailHtml}` : ""}</span>`;
    const container = $("#pg-log-entries");
    container.appendChild(el);
    container.scrollTop = container.scrollHeight;
    if (!options.skipStore) {
      const showTypes = new Set(["request", "completed", "failed", "suspended", "waiting", "action", "done", "error"]);
      if (!options.skipToast && showTypes.has(String(type || "").toLowerCase()))
        pgShowRecentLogToast(type, title, normalizedDetail);
    }
    pgRenderRunControlSummary();
    pgUpdateWorkspaceChromeState();
  }

  function pgResetRun() {
    pgRunning = false;
    pgStepStates = {};
    pgLogEntries = [];
    pgThinkingBuffers = new Map();
    pgRunActorId = "";
    pgRunRunId = "";
    pgRunCommandId = "";
    pgRunMessageBuffers = new Map();
    setPlaygroundInteraction(null);
    $("#pg-exec-log").classList.add("hidden");
    $("#pg-result-section").classList.add("hidden");
    $("#pg-log-entries").innerHTML = "";
    $("#pg-run-reset").classList.add("hidden");
    pgHideRecentLogToast();
    if (pgCurrentYaml && pgParsedDef) {
      renderFlowInto($("#pg-graph"), pgParsedDef.definition.steps, pgParsedDef.edges, null);
    }
    if (pgActiveModal && PG_MODAL_KEYS.has(pgActiveModal))
      pgCloseModal({ force: true, skipPersist: true });
    pgUpdateWorkspaceChromeState();
    pgUpdateRunBarVisibility();
    pgUpdateSaveUi();
    scheduleUiStatePersist();
  }
})();
