(() => {
  "use strict";

  // ── State ──
  let workflows = [];
  let primitives = [];
  let llmStatus = { available: false };
  let selectedWorkflow = null;
  let workflowDef = null;
  let stepStates = {};
  let eventSource = null;

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
        if (view === "primitives" && !selectedWorkflow) showView("empty");
      });
    });
    $("#btn-run").addEventListener("click", runWorkflow);
    $("#btn-reset").addEventListener("click", resetExecution);
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
    const detList = $("#list-deterministic");
    const llmList = $("#list-llm");
    detList.innerHTML = "";
    llmList.innerHTML = "";

    for (const wf of workflows) {
      const li = document.createElement("li");
      li.dataset.name = wf.name;
      li.innerHTML = `
        <div>${wf.name}</div>
        <div class="wf-primitives">
          ${wf.primitives.map((p) => `<span class="prim-dot" style="background:${TYPE_COLORS[p] || "#64748b"}" title="${p}"></span>`).join("")}
        </div>`;
      li.addEventListener("click", () => selectWorkflow(wf.name));
      (wf.category === "deterministic" ? detList : llmList).appendChild(li);
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

      const wf = workflows.find((w) => w.name === name);
      $("#wf-input").value = wf?.defaultInput || "";

      if (def.roles && def.roles.length > 0) {
        $("#roles-section").classList.remove("hidden");
        $("#roles-list").innerHTML = def.roles
          .map((r) => `<span class="role-chip" title="${esc(r.systemPrompt)}">${esc(r.name)}</span>`)
          .join("");
      } else {
        $("#roles-section").classList.add("hidden");
      }

      renderYamlSource(workflowDef.yaml);
      resetExecution();
      renderFlowDiagram();

      const isDet = wf?.category === "deterministic";
      $("#btn-run").disabled = !isDet && !llmStatus.available;
    } catch (e) {
      console.error("Failed to load workflow", e);
      $("#wf-description").textContent = "Failed to load workflow definition.";
    }
  }

  // ── Views ──
  function showView(name) {
    $("#view-empty").classList.toggle("hidden", name !== "empty");
    $("#view-workflow").classList.toggle("hidden", name !== "workflow");
    $("#view-primitive-detail").classList.toggle("hidden", name !== "primitive");
  }

  function showPrimitiveDetail(p) {
    showView("primitive");
    $("#prim-name").textContent = p.name;
    $("#prim-description").textContent = p.description;
    $("#prim-aliases").innerHTML = p.aliases.map((a) => `<span class="tag">${a}</span>`).join("");
    const cat = $("#prim-category");
    cat.textContent = p.category;
    cat.className = `category-badge cat-${p.category}`;
  }

  // ── Flow Diagram (SVG) ──
  function renderFlowDiagram() {
    if (!workflowDef) return;
    const { definition, edges } = workflowDef;
    const steps = definition.steps;
    if (!steps || steps.length === 0) {
      $("#flow-container").innerHTML = '<p style="color:var(--text-muted)">No steps</p>';
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
      g.setAttribute("class", `step-node state-${stepStates[step.id] || "pending"}`);
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

      // Status indicator
      const status = document.createElementNS(ns, "text");
      status.setAttribute("x", NODE_W - 8);
      status.setAttribute("y", 20);
      status.setAttribute("class", "step-status");
      status.setAttribute("text-anchor", "end");
      status.setAttribute("font-size", "14");
      const state = stepStates[step.id];
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
        g.setAttribute("class", `step-node state-${stepStates[child.id] || "pending"}`);
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

    $("#flow-container").innerHTML = "";
    $("#flow-container").appendChild(svg);
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

    const input = $("#wf-input").value;
    const encodedInput = encodeURIComponent(input);
    const url = `/api/workflows/${selectedWorkflow}/run?input=${encodedInput}`;

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

    eventSource.addEventListener("workflow.completed", (e) => {
      const data = JSON.parse(e.data);
      addLogEntry("done", data.success ? "\u2705 Workflow completed" : "\u274C Workflow failed", "");
      showResult(data);
      stopExecution();
      $("#btn-run").disabled = false;
    });

    eventSource.addEventListener("workflow.error", (e) => {
      const data = JSON.parse(e.data);
      addLogEntry("error", "\u274C Error", data.error);
      stopExecution();
      $("#btn-run").disabled = false;
    });

    eventSource.onerror = () => {
      if (eventSource && eventSource.readyState === EventSource.CLOSED) {
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
    $("#exec-log").classList.add("hidden");
    $("#result-section").classList.add("hidden");
    $("#log-entries").innerHTML = "";
    $("#btn-reset").classList.add("hidden");
    $("#btn-run").disabled = false;

    const wf = workflows.find((w) => w.name === selectedWorkflow);
    const isDet = wf?.category === "deterministic";
    if (!isDet && !llmStatus.available) {
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
    const icons = { request: "\u25B6", completed: "\u2713", failed: "\u2717", llm: "\uD83E\uDD16", done: "\u2705", error: "\u274C" };
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
})();
