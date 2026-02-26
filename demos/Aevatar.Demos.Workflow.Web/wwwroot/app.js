(() => {
  "use strict";

  // ── State ──
  let workflows = [];
  let primitives = [];
  let llmStatus = { available: false };
  let selectedWorkflow = null;
  let selectedWorkflowMeta = null;
  let workflowDef = null;
  let stepStates = {};
  let turingMachineState = {};
  let eventSource = null;
  let pendingInteraction = null;

  const TYPE_COLORS = {
    transform: "#3b82f6", guard: "#f97316", conditional: "#f59e0b", switch: "#f59e0b",
    while: "#f59e0b", llm_call: "#a855f7", parallel: "#22c55e", parallel_fanout: "#22c55e",
    race: "#14b8a6", map_reduce: "#84cc16", foreach: "#84cc16", for_each: "#84cc16",
    evaluate: "#ec4899", reflect: "#ec4899", cache: "#06b6d4", assign: "#94a3b8",
    retrieve_facts: "#3b82f6", emit: "#f43f5e", delay: "#f59e0b", checkpoint: "#6366f1",
    wait_signal: "#f59e0b", human_approval: "#f97316", human_input: "#f97316",
    workflow_call: "#6366f1", vote_consensus: "#22c55e", tool_call: "#a855f7",
    connector_call: "#14b8a6", workflow_loop: "#64748b",
  };
  const WORKFLOW_GROUP_ORDER = [
    "start-here",
    "custom-step-modules",
    "role-event-modules",
    "human-interaction-manual",
    "human-interaction-legacy",
    "turing-completeness",
    "llm-workflows",
    "deterministic-other",
    "other",
  ];
  const WORKFLOW_GROUP_LABELS = {
    "start-here": "Start Here (Deterministic Basics)",
    "custom-step-modules": "Custom Step Modules",
    "role-event-modules": "Role Event Modules",
    "human-interaction-manual": "Human Interaction (Manual)",
    "human-interaction-legacy": "Human Interaction (Legacy Auto)",
    "turing-completeness": "Turing Completeness",
    "llm-workflows": "LLM Workflows",
    "deterministic-other": "Other Deterministic Demos",
    "other": "Other",
  };

  // ── DOM refs ──
  const $ = (sel) => document.querySelector(sel);
  const $$ = (sel) => document.querySelectorAll(sel);

  // ── Init ──
  document.addEventListener("DOMContentLoaded", async () => {
    setupNavigation();
    await Promise.all([loadWorkflows(), loadLlmStatus(), loadPrimitives()]);
  });

  function setupNavigation() {
    $$(".nav-btn").forEach((btn) => {
      btn.addEventListener("click", () => {
        $$(".nav-btn").forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        const view = btn.dataset.view;
        $("#sidebar-workflows").classList.toggle("hidden", view !== "workflows");
        $("#sidebar-primitives").classList.toggle("hidden", view !== "primitives");
        if (view === "playground") showView("playground");
        else if (view === "primitives" && !selectedWorkflow) showView("empty");
      });
    });
    $("#btn-run").addEventListener("click", runWorkflow);
    $("#btn-reset").addEventListener("click", resetExecution);
    setupPlayground();
  }

  // ── Data Loading ──
  async function loadWorkflows() {
    try {
      const res = await fetch("/api/workflows");
      workflows = await res.json();
      renderWorkflowList();
    } catch (e) { console.error("Failed to load workflows", e); }
  }

  async function loadLlmStatus() {
    try {
      const res = await fetch("/api/llm/status");
      llmStatus = await res.json();
      renderLlmStatus();
    } catch (e) { console.error("Failed to load LLM status", e); }
  }

  async function loadPrimitives() {
    try {
      const res = await fetch("/api/primitives");
      primitives = await res.json();
      renderPrimitivesList();
    } catch (e) { console.error("Failed to load primitives", e); }
  }

  // ── Render: Sidebar ──
  function renderWorkflowList() {
    const groupsContainer = $("#workflow-groups");
    if (!groupsContainer) return;
    groupsContainer.innerHTML = "";

    const grouped = new Map();
    for (const wf of workflows) {
      const fallbackGroup = wf.category === "turing"
        ? "turing-completeness"
        : wf.category === "llm"
          ? "llm-workflows"
          : "start-here";
      const groupKey = wf.group || fallbackGroup;
      const groupLabel = wf.groupLabel || WORKFLOW_GROUP_LABELS[groupKey] || "Other";
      if (!grouped.has(groupKey)) {
        grouped.set(groupKey, { label: groupLabel, items: [] });
      }
      grouped.get(groupKey).items.push(wf);
    }

    const orderedKeys = Array.from(grouped.keys()).sort((left, right) => {
      const leftIdx = WORKFLOW_GROUP_ORDER.indexOf(left);
      const rightIdx = WORKFLOW_GROUP_ORDER.indexOf(right);
      const leftOrder = leftIdx === -1 ? 999 : leftIdx;
      const rightOrder = rightIdx === -1 ? 999 : rightIdx;
      return leftOrder - rightOrder || left.localeCompare(right);
    });

    for (const groupKey of orderedKeys) {
      const group = grouped.get(groupKey);
      if (!group) continue;

      const label = document.createElement("div");
      label.className = "section-label";
      label.textContent = group.label;
      groupsContainer.appendChild(label);

      const list = document.createElement("ul");
      list.className = "workflow-list";

      const sortedItems = group.items.sort((a, b) => {
        const leftOrder = Number.isFinite(a.sortOrder) ? a.sortOrder : 10_000;
        const rightOrder = Number.isFinite(b.sortOrder) ? b.sortOrder : 10_000;
        return leftOrder - rightOrder || a.name.localeCompare(b.name);
      });

      for (const wf of sortedItems) {
        const li = document.createElement("li");
        li.dataset.name = wf.name;
        li.classList.toggle("active", selectedWorkflow === wf.name);
        const summary = truncate(String(wf.description || "").replace(/\s+/g, " ").trim(), 88);
        li.innerHTML = `
          <div>${wf.name}</div>
          ${summary ? `<div class="wf-summary">${esc(summary)}</div>` : ""}
          <div class="wf-primitives">
            ${wf.primitives.map((p) => `<span class="prim-dot" style="background:${TYPE_COLORS[p] || "#64748b"}" title="${p}"></span>`).join("")}
          </div>`;
        li.addEventListener("click", () => selectWorkflow(wf.name));
        list.appendChild(li);
      }

      groupsContainer.appendChild(list);
    }
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
    for (const p of primitives) {
      if (p.name === "workflow_loop") continue;
      const li = document.createElement("li");
      li.innerHTML = `
        <span class="prim-color" style="background:${TYPE_COLORS[p.name] || "#64748b"}"></span>
        <span class="prim-info">
          <div class="prim-label">${p.name}</div>
          <div class="prim-cat">${p.category}</div>
        </span>`;
      li.addEventListener("click", () => showPrimitiveDetail(p));
      ul.appendChild(li);
    }
  }

  // ── Select Workflow ──
  async function selectWorkflow(name) {
    stopExecution();
    selectedWorkflow = name;
    selectedWorkflowMeta = workflows.find((w) => w.name === name) || null;

    $$(".workflow-list li").forEach((li) =>
      li.classList.toggle("active", li.dataset.name === name));

    showView("workflow");
    $("#btn-run").disabled = true;
    $("#wf-name").textContent = name;
    $("#wf-description").textContent = "Loading...";

    try {
      const res = await fetch(`/api/workflows/${name}`);
      workflowDef = await res.json();
      const def = workflowDef.definition;

      $("#wf-description").textContent = def.description || "";

      $("#wf-input").value = selectedWorkflowMeta?.defaultInput || "";
      renderTuringDemoPanel(selectedWorkflowMeta);
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

      $("#btn-run").disabled = selectedWorkflowMeta?.category === "llm" && !llmStatus.available;
    } catch (e) {
      console.error("Failed to load workflow", e);
      $("#wf-description").textContent = "Failed to load workflow definition.";
      renderTuringDemoPanel(null);
    }
  }

  // ── Views ──
  function showView(name) {
    $("#view-empty").classList.toggle("hidden", name !== "empty");
    $("#view-workflow").classList.toggle("hidden", name !== "workflow");
    $("#view-primitive-detail").classList.toggle("hidden", name !== "primitive");
    $("#view-playground").classList.toggle("hidden", name !== "playground");
  }

  function showPrimitiveDetail(p) {
    showView("primitive");
    $("#prim-name").textContent = p.name;
    $("#prim-description").textContent = p.description;
    $("#prim-aliases").innerHTML = p.aliases.map((a) => `<span class="tag">${a}</span>`).join("");
    const cat = $("#prim-category");
    cat.textContent = p.category;
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

    eventSource = new EventSource(url);

    eventSource.addEventListener("step.request", (e) => {
      const data = JSON.parse(e.data);
      stepStates[data.stepId] = "running";
      updateStepNode(data.stepId);
      addLogEntry("request", `\u25B6 ${data.stepId} (${data.stepType})`, truncate(data.input, 200));
    });

    eventSource.addEventListener("step.completed", (e) => {
      const data = JSON.parse(e.data);
      stepStates[data.stepId] = data.success ? "completed" : "failed";
      updateStepNode(data.stepId);
      if (isSelectedWorkflowTuring()) {
        updateTuringMachineState(data.metadata);
      }
      if (data.success) {
        addLogEntry("completed", `\u2713 ${data.stepId}`, truncate(data.output, 300));
      } else {
        addLogEntry("failed", `\u2717 ${data.stepId}`, data.error || "Failed");
      }
    });

    eventSource.addEventListener("llm.response", (e) => {
      const data = JSON.parse(e.data);
      addLogEntry("llm", `\uD83E\uDD16 ${data.role}`, truncate(data.content, 400));
    });

    eventSource.addEventListener("workflow.suspended", (e) => {
      const data = JSON.parse(e.data);
      addLogEntry("suspended", `\u23F8 ${data.stepId} (${data.suspensionType})`, data.prompt || "Waiting for human action");
      setWorkflowInteraction({
        type: data.suspensionType || "human_input",
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
    resetTuringMachineState();
    setWorkflowInteraction(null);
    $("#exec-log").classList.add("hidden");
    $("#result-section").classList.add("hidden");
    $("#log-entries").innerHTML = "";
    $("#btn-reset").classList.add("hidden");
    $("#btn-run").disabled = false;

    if (selectedWorkflowMeta?.category === "llm" && !llmStatus.available) {
      $("#btn-run").disabled = true;
    }

    renderFlowDiagram();
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

  function addLogEntry(type, title, detail) {
    const el = document.createElement("div");
    el.className = `log-entry log-${type}`;
    const icons = { request: "\u25B6", completed: "\u2713", failed: "\u2717", llm: "\uD83E\uDD16", suspended: "\u23F8", waiting: "\u23F3", action: "\u270D", done: "\u2705", error: "\u274C" };
    el.innerHTML = `<span class="log-icon">${icons[type] || ""}</span><span class="log-text"><strong>${esc(title)}</strong>${detail ? "<br>" + esc(detail) : ""}</span>`;
    const container = $("#log-entries");
    container.appendChild(el);
    container.scrollTop = container.scrollHeight;
  }

  function showResult(data) {
    $("#result-section").classList.remove("hidden");
    const pre = $("#wf-result");
    pre.textContent = data.success ? data.output : (data.error || "Failed");
    pre.className = `result-pre ${data.success ? "success" : "failure"}`;
  }

  function setWorkflowInteraction(interaction) {
    pendingInteraction = interaction;
    const section = $("#interaction-section");
    const panel = $("#interaction-panel");
    if (!section || !panel) return;

    if (!interaction) {
      section.classList.add("hidden");
      panel.innerHTML = "";
      return;
    }

    section.classList.remove("hidden");
    renderInteractionPanel(panel, "wf", interaction, addLogEntry, () => setWorkflowInteraction(null));
  }

  function setPlaygroundInteraction(interaction) {
    pgPendingInteraction = interaction;
    const section = $("#pg-interaction-section");
    const panel = $("#pg-interaction-panel");
    if (!section || !panel) return;

    if (!interaction) {
      section.classList.add("hidden");
      panel.innerHTML = "";
      return;
    }

    section.classList.remove("hidden");
    renderInteractionPanel(panel, "pg", interaction, pgAddLog, () => setPlaygroundInteraction(null));
  }

  function renderInteractionPanel(container, prefix, interaction, logFn, clearFn) {
    const type = String(interaction.type || "").toLowerCase();
    const runId = interaction.runId || "";
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

    let controlHtml = "";
    if (type === "human_approval") {
      controlHtml = `
        <textarea id="${prefix}-interaction-comment" class="interaction-input" rows="2" placeholder="Optional comment..."></textarea>
        <div class="interaction-actions">
          <button id="${prefix}-interaction-approve" class="btn btn-primary">Approve</button>
          <button id="${prefix}-interaction-reject" class="btn btn-danger">Reject</button>
        </div>`;
    } else if (type === "wait_signal") {
      controlHtml = `
        <textarea id="${prefix}-interaction-payload" class="interaction-input" rows="2" placeholder="Signal payload (optional)"></textarea>
        <div class="interaction-actions">
          <button id="${prefix}-interaction-signal" class="btn btn-primary">Send signal</button>
        </div>`;
    } else {
      const variableName = interaction.metadata?.variable ? ` (${interaction.metadata.variable})` : "";
      controlHtml = `
        <textarea id="${prefix}-interaction-input" class="interaction-input" rows="3" placeholder="Input for human step${esc(variableName)}"></textarea>
        <div class="interaction-actions">
          <button id="${prefix}-interaction-submit" class="btn btn-primary">Submit input</button>
        </div>`;
    }

    container.innerHTML = `
      <div class="interaction-summary">
        <strong>${esc(title)}</strong><br>
        ${esc(prompt || "No prompt provided by workflow.")}
      </div>
      <div class="interaction-meta">
        <span class="interaction-chip">type: ${esc(kindLabel)}</span>
        <span class="interaction-chip">step: ${esc(stepId || "n/a")}</span>
        <span class="interaction-chip">run: ${esc(runId || "missing")}</span>
        ${type === "wait_signal" ? `<span class="interaction-chip">signal: ${esc(signalName || "n/a")}</span>` : ""}
        <span class="interaction-chip">timeout: ${esc(timeoutLabel)}</span>
      </div>
      ${controlHtml}
      ${runId ? "" : `<div class="interaction-note">Cannot submit: missing runId in SSE context.</div>`}
    `;

    bindInteractionActions(prefix, interaction, logFn, clearFn);
  }

  function bindInteractionActions(prefix, interaction, logFn, clearFn) {
    const type = String(interaction.type || "").toLowerCase();
    const runId = interaction.runId || "";
    const stepId = interaction.stepId || "";
    const signalName = interaction.signalName || "";

    if (!runId) return;

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
            runId,
            stepId,
            approved,
            userInput: comment,
          });
          logFn("action", approved ? "✍ Approval submitted" : "✍ Rejection submitted", comment || "(no comment)");
          clearFn();
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
          await postJson("/api/workflows/signal", {
            runId,
            signalName,
            payload,
          });
          logFn("action", "✍ Signal submitted", truncate(payload, 220) || "(empty payload)");
          clearFn();
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
    if (!submitBtn || !inputEl || !stepId) return;

    submitBtn.addEventListener("click", async () => {
      submitBtn.disabled = true;
      const userInput = inputEl.value.trim();
      try {
        await postJson("/api/workflows/resume", {
          runId,
          stepId,
          approved: true,
          userInput,
        });
        logFn("action", "✍ Human input submitted", truncate(userInput, 220) || "(empty input)");
        clearFn();
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

  // ══════════════════════════════════════════════
  // ── Playground ──
  // ══════════════════════════════════════════════

  let pgMessages = [];
  let pgStreaming = false;
  let pgAbort = null;
  let pgCurrentYaml = "";
  let pgParsedDef = null;
  let pgStepStates = {};
  let pgEventSource = null;
  let pgRunning = false;
  let pgPendingInteraction = null;

  function setupPlayground() {
    $("#pg-send").addEventListener("click", () => pgSend());
    $("#pg-input").addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); pgSend(); }
    });
    $("#pg-run-btn").addEventListener("click", pgRunWorkflow);
    $("#pg-run-reset").addEventListener("click", pgResetRun);
  }

  async function pgSend() {
    const input = $("#pg-input");
    const text = input.value.trim();
    if (!text || pgStreaming) return;

    input.value = "";
    pgMessages.push({ role: "user", content: text });
    pgAddBubble("user", text);

    pgStreaming = true;
    $("#pg-send").disabled = true;
    const assistantEl = pgAddBubble("assistant", "");
    const cursorEl = document.createElement("span");
    cursorEl.className = "pg-typing";
    assistantEl.appendChild(cursorEl);

    let fullText = "";
    pgAbort = new AbortController();

    try {
      const res = await fetch("/api/playground/chat", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ messages: pgMessages }),
        signal: pgAbort.signal,
      });

      if (!res.ok) {
        const err = await res.text();
        pgAddBubble("error", err);
        pgMessages.pop();
        pgStreaming = false;
        $("#pg-send").disabled = false;
        assistantEl.remove();
        return;
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
          const payload = line.slice(6);
          if (payload === "[DONE]") break;
          try {
            const obj = JSON.parse(payload);
            if (obj.delta) {
              fullText += obj.delta;
              pgUpdateAssistantBubble(assistantEl, fullText, true);
            }
            if (obj.error) {
              pgAddBubble("error", obj.error);
            }
          } catch { }
        }
      }
    } catch (e) {
      if (e.name !== "AbortError") pgAddBubble("error", e.message);
    }

    pgUpdateAssistantBubble(assistantEl, fullText, false);
    if (fullText) {
      pgMessages.push({ role: "assistant", content: fullText });
      pgExtractAndRenderYaml(fullText);
    }

    pgStreaming = false;
    $("#pg-send").disabled = false;
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
        pgApplyYaml(yaml);
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

  function pgExtractAndRenderYaml(text) {
    const regex = /```(?:ya?ml)?\s*\n([\s\S]*?)```/gi;
    let lastYaml = null;
    let m;
    while ((m = regex.exec(text)) !== null) lastYaml = m[1].trim();
    if (lastYaml) pgApplyYaml(lastYaml);
  }

  async function pgApplyYaml(yaml) {
    pgCurrentYaml = yaml;
    pgResetRun();

    const yamlEl = $("#pg-yaml");
    yamlEl.innerHTML = yaml.split("\n").map(highlightYamlLine).join("\n");

    try {
      const res = await fetch("/api/playground/parse", {
        method: "POST",
        headers: { "Content-Type": "text/plain" },
        body: yaml,
      });
      const data = await res.json();
      if (data.valid && data.definition?.steps) {
        pgParsedDef = data;
        renderFlowInto($("#pg-graph"), data.definition.steps, data.edges, null);
        $("#pg-run-btn").disabled = false;
      } else {
        pgParsedDef = null;
        $("#pg-run-btn").disabled = true;
        $("#pg-graph").innerHTML = `<p style="color:var(--state-failed);padding:16px;font-size:12px">${esc(data.error || "Invalid YAML")}</p>`;
      }
    } catch (e) {
      pgParsedDef = null;
      $("#pg-run-btn").disabled = true;
      $("#pg-graph").innerHTML = `<p style="color:var(--state-failed);padding:16px;font-size:12px">${esc(e.message)}</p>`;
    }
  }

  // ── Playground: Run Workflow ──

  function pgRunWorkflow() {
    if (!pgCurrentYaml || pgRunning) return;
    pgResetRun();
    pgRunning = true;
    setPlaygroundInteraction(null);
    $("#pg-run-btn").disabled = true;
    $("#pg-run-reset").classList.remove("hidden");
    $("#pg-exec-log").classList.remove("hidden");

    pgStepStates = {};
    if (pgParsedDef?.definition?.steps) {
      for (const s of pgParsedDef.definition.steps) pgStepStates[s.id] = "pending";
    }
    renderFlowInto($("#pg-graph"), pgParsedDef?.definition?.steps || [], pgParsedDef?.edges || [], pgStepStates);

    const input = $("#pg-run-input").value || "Hello, world!";
    const autoResume = $("#pg-auto-resume")?.checked === true;
    const body = JSON.stringify({ yaml: pgCurrentYaml, input, autoResume });

    fetch("/api/playground/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body,
    }).then(async (res) => {
      if (!res.ok) {
        const err = await res.text();
        pgAddLog("error", "\u274C Error", err);
        pgRunning = false;
        $("#pg-run-btn").disabled = false;
        return;
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
          if (!line.startsWith("event: ") && !line.startsWith("data: ")) continue;
          if (line.startsWith("event: ")) {
            var currentEvent = line.slice(7);
            continue;
          }
          if (!line.startsWith("data: ")) continue;
          try {
            const data = JSON.parse(line.slice(6));
            pgHandleSseEvent(currentEvent, data);
          } catch { }
        }
      }
      if (!pgRunning) return;
      pgRunning = false;
      $("#pg-run-btn").disabled = false;
    }).catch((e) => {
      pgAddLog("error", "\u274C Error", e.message);
      pgRunning = false;
      $("#pg-run-btn").disabled = false;
    });
  }

  function pgHandleSseEvent(eventType, data) {
    if (eventType === "step.request") {
      pgStepStates[data.stepId] = "running";
      pgUpdateGraphNode(data.stepId);
      pgAddLog("request", `\u25B6 ${data.stepId} (${data.stepType})`, truncate(data.input, 200));
    } else if (eventType === "step.completed") {
      pgStepStates[data.stepId] = data.success ? "completed" : "failed";
      pgUpdateGraphNode(data.stepId);
      pgAddLog(data.success ? "completed" : "failed",
        `${data.success ? "\u2713" : "\u2717"} ${data.stepId}`,
        data.success ? truncate(data.output, 300) : (data.error || "Failed"));
    } else if (eventType === "llm.response") {
      pgAddLog("llm", `\uD83E\uDD16 ${data.role}`, truncate(data.content, 400));
    } else if (eventType === "workflow.suspended") {
      pgAddLog("suspended", `\u23F8 ${data.stepId} (${data.suspensionType})`, data.prompt || "Waiting for human action");
      setPlaygroundInteraction({
        type: data.suspensionType || "human_input",
        runId: data.runId || "",
        stepId: data.stepId || "",
        prompt: data.prompt || "",
        timeoutSeconds: data.timeoutSeconds || 0,
        metadata: data.metadata || {},
      });
    } else if (eventType === "workflow.waiting_signal") {
      pgAddLog("waiting", `\u23F3 ${data.stepId} (${data.signalName})`, data.prompt || "Waiting for external signal");
      setPlaygroundInteraction({
        type: "wait_signal",
        runId: data.runId || "",
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
      setPlaygroundInteraction(null);
      pgRunning = false;
      $("#pg-run-btn").disabled = false;
    } else if (eventType === "workflow.error") {
      pgAddLog("error", "\u274C Error", data.error);
      setPlaygroundInteraction(null);
      pgRunning = false;
      $("#pg-run-btn").disabled = false;
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

  function pgAddLog(type, title, detail) {
    const el = document.createElement("div");
    el.className = `log-entry log-${type}`;
    const icons = { request: "\u25B6", completed: "\u2713", failed: "\u2717", llm: "\uD83E\uDD16", suspended: "\u23F8", waiting: "\u23F3", action: "\u270D", done: "\u2705", error: "\u274C" };
    el.innerHTML = `<span class="log-icon">${icons[type] || ""}</span><span class="log-text"><strong>${esc(title)}</strong>${detail ? "<br>" + esc(detail) : ""}</span>`;
    const container = $("#pg-log-entries");
    container.appendChild(el);
    container.scrollTop = container.scrollHeight;
  }

  function pgResetRun() {
    pgRunning = false;
    pgStepStates = {};
    setPlaygroundInteraction(null);
    $("#pg-exec-log").classList.add("hidden");
    $("#pg-result-section").classList.add("hidden");
    $("#pg-log-entries").innerHTML = "";
    $("#pg-run-reset").classList.add("hidden");
    if (pgCurrentYaml && pgParsedDef) {
      $("#pg-run-btn").disabled = false;
      renderFlowInto($("#pg-graph"), pgParsedDef.definition.steps, pgParsedDef.edges, null);
    }
  }
})();
