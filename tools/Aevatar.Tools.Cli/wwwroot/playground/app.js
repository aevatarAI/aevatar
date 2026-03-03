(() => {
  "use strict";

  const $ = (selector) => document.querySelector(selector);

  const chatMessagesEl = $("#chat-messages");
  const chatInputEl = $("#chat-input");
  const chatSendEl = $("#chat-send");
  const runContextEl = $("#yaml-view");
  const graphViewEl = $("#graph-view");
  const runInputEl = $("#run-input");
  const runBtnEl = $("#run-btn");
  const resetBtnEl = $("#reset-btn");
  const autoResumeEl = $("#auto-resume");
  const interactionCardEl = $("#interaction-card");
  const interactionContentEl = $("#interaction-content");
  const logViewEl = $("#log-view");
  const resultViewEl = $("#result-view");

  const TYPE_COLORS = {
    assign: "#94a3b8",
    conditional: "#f59e0b",
    llm_call: "#a855f7",
    workflow_yaml_validate: "#f97316",
    human_approval: "#f97316",
    dynamic_workflow: "#6366f1",
    tool_call: "#a855f7",
    connector_call: "#14b8a6",
    wait_signal: "#f59e0b",
    emit: "#f43f5e",
    transform: "#3b82f6",
  };

  let chatStreaming = false;
  let runActorId = "";
  let runRunId = "";
  let runCommandId = "";
  let runWorkflowName = "";
  let stepStates = {};
  let stepTypes = {};
  let stepOrder = [];
  let stepEdges = [];
  let stepEdgeKeys = new Set();
  let lastRequestedStepId = "";
  let messageBuffers = new Map();

  chatSendEl.addEventListener("click", () => sendPrompt(chatInputEl.value));
  chatInputEl.addEventListener("keydown", (event) => {
    if ((event.metaKey || event.ctrlKey) && event.key === "Enter") {
      event.preventDefault();
      sendPrompt(chatInputEl.value);
    }
  });
  runBtnEl.addEventListener("click", () => {
    const text = runInputEl.value.trim();
    if (!text) return;
    runInputEl.value = "";
    sendPrompt(text);
  });
  resetBtnEl.addEventListener("click", resetRunState);

  updateRunContextView();
  renderGraph();
  addLog("Ready.");
  autoSendFromQuery();

  async function autoSendFromQuery() {
    const params = new URLSearchParams(window.location.search);
    const text = (params.get("chat") || "").trim();
    if (!text) return;

    chatInputEl.value = text;
    history.replaceState(null, "", window.location.pathname);
    await sendPrompt(text);
  }

  async function sendPrompt(rawPrompt) {
    const prompt = (rawPrompt || "").trim();
    if (!prompt || chatStreaming) return;

    chatInputEl.value = "";
    addMessage("user", prompt);
    prepareForNewRun();

    chatStreaming = true;
    chatSendEl.disabled = true;
    runBtnEl.disabled = true;

    try {
      await streamSse("/api/chat", { prompt }, (payload) => {
        if (payload === "[DONE]") return;
        const frame = parseJson(payload);
        if (!frame) return;
        handleRunFrame(frame);
      });
    } catch (err) {
      const message = err?.message || String(err);
      addMessage("error", message);
      addLog(`RUN_ERROR: ${message}`);
      resultViewEl.textContent = message;
    } finally {
      chatStreaming = false;
      chatSendEl.disabled = false;
      runBtnEl.disabled = false;
    }
  }

  function prepareForNewRun() {
    runActorId = "";
    runRunId = "";
    runCommandId = "";
    runWorkflowName = "";
    stepStates = {};
    stepTypes = {};
    stepOrder = [];
    stepEdges = [];
    stepEdgeKeys = new Set();
    lastRequestedStepId = "";
    messageBuffers = new Map();
    interactionCardEl.classList.add("hidden");
    interactionContentEl.innerHTML = "";
    resultViewEl.textContent = "";
    logViewEl.innerHTML = "";
    renderGraph();
    updateRunContextView();
    addLog("Run started.");
  }

  function resetRunState() {
    runActorId = "";
    runRunId = "";
    runCommandId = "";
    runWorkflowName = "";
    stepStates = {};
    stepTypes = {};
    stepOrder = [];
    stepEdges = [];
    stepEdgeKeys = new Set();
    lastRequestedStepId = "";
    messageBuffers = new Map();
    interactionCardEl.classList.add("hidden");
    interactionContentEl.innerHTML = "";
    resultViewEl.textContent = "";
    logViewEl.innerHTML = "";
    updateRunContextView();
    renderGraph();
    addLog("State reset.");
  }

  function handleRunFrame(frame) {
    const type = String(frame.type || "");
    if (!type) return;

    if (type === "RUN_STARTED") {
      runActorId = frame.threadId || runActorId;
      updateRunContextView();
      addLog(`RUN_STARTED actor=${runActorId || "-"}`);
      return;
    }

    if (type === "TEXT_MESSAGE_START") {
      const key = frame.messageId || "__default__";
      messageBuffers.set(key, {
        role: frame.role || "assistant",
        content: "",
      });
      return;
    }

    if (type === "TEXT_MESSAGE_CONTENT") {
      const key = frame.messageId || "__default__";
      const entry = messageBuffers.get(key) || { role: frame.role || "assistant", content: "" };
      entry.content += frame.delta || "";
      messageBuffers.set(key, entry);
      return;
    }

    if (type === "TEXT_MESSAGE_END") {
      const key = frame.messageId || "__default__";
      const entry = messageBuffers.get(key);
      const content = (entry?.content || frame.content || "").trim();
      messageBuffers.delete(key);
      if (content) {
        addMessage("assistant", content);
        addLog(`LLM: ${content}`);
      }
      return;
    }

    if (type === "RUN_FINISHED") {
      addLog("RUN_FINISHED");
      resultViewEl.textContent = stringifyResult(frame.result);
      interactionCardEl.classList.add("hidden");
      return;
    }

    if (type === "RUN_ERROR") {
      const error = frame.message || "run error";
      addLog(`RUN_ERROR: ${error}`);
      addMessage("error", error);
      resultViewEl.textContent = error;
      interactionCardEl.classList.add("hidden");
      return;
    }

    if (type === "CUSTOM") {
      handleCustomFrame(frame);
    }
  }

  function handleCustomFrame(frame) {
    const name = String(frame.name || "");
    const value = frame.value && typeof frame.value === "object" ? frame.value : {};
    if (!name) return;

    if (name === "aevatar.run.context") {
      runActorId = readValue(value, "actorId", "ActorId") || runActorId;
      runCommandId = readValue(value, "commandId", "CommandId") || runCommandId;
      runWorkflowName = readValue(value, "workflowName", "WorkflowName") || runWorkflowName;
      updateRunContextView();
      return;
    }

    if (name === "aevatar.step.request") {
      const stepId = readValue(value, "stepId", "StepId");
      const stepType = readValue(value, "stepType", "StepType");
      runRunId = readValue(value, "runId", "RunId") || runRunId;
      recordStepTransition(stepId);
      markStep(stepId, "running", stepType);
      updateRunContextView();
      if (stepId) addLog(`STEP_REQUEST: ${stepId}`);
      return;
    }

    if (name === "aevatar.step.completed") {
      const stepId = readValue(value, "stepId", "StepId");
      const stepType = readValue(value, "stepType", "StepType");
      runRunId = readValue(value, "runId", "RunId") || runRunId;
      const success = value.success === true || value.Success === true;
      markStep(stepId, success ? "completed" : "failed", stepType);
      updateRunContextView();
      if (stepId) addLog(`STEP_DONE: ${stepId} (${success ? "ok" : "failed"})`);
      return;
    }

    if (name === "aevatar.human_input.request") {
      runRunId = readValue(value, "runId", "RunId") || runRunId;
      updateRunContextView();
      const interaction = {
        type: readValue(value, "suspensionType", "SuspensionType") || "human_input",
        actorId: readValue(value, "actorId", "ActorId") || runActorId,
        runId: readValue(value, "runId", "RunId") || runRunId,
        stepId: readValue(value, "stepId", "StepId"),
        prompt: readValue(value, "prompt", "Prompt") || "Please continue the workflow.",
      };
      addLog(`SUSPENDED: ${interaction.stepId || "-"} (${interaction.type})`);
      showInteraction(interaction);
      if (autoResumeEl.checked) autoResume(interaction);
      return;
    }

    if (name === "aevatar.workflow.waiting_signal") {
      const interaction = {
        type: "wait_signal",
        actorId: readValue(value, "actorId", "ActorId") || runActorId,
        runId: readValue(value, "runId", "RunId") || runRunId,
        stepId: readValue(value, "stepId", "StepId"),
        signalName: readValue(value, "signalName", "SignalName") || "resume",
        prompt: readValue(value, "prompt", "Prompt") || "Waiting for external signal.",
      };
      addLog(`WAIT_SIGNAL: ${interaction.stepId || "-"} (${interaction.signalName})`);
      showInteraction(interaction);
      return;
    }

    if (name === "aevatar.llm.reasoning") {
      const delta = readValue(value, "delta", "Delta");
      if (delta) addLog(`THINKING: ${delta}`);
    }
  }

  function markStep(stepId, status, stepType) {
    if (!stepId) return;

    if (!Object.prototype.hasOwnProperty.call(stepStates, stepId)) {
      stepOrder.push(stepId);
      stepStates[stepId] = "pending";
    }

    stepStates[stepId] = status || "pending";
    if (stepType) stepTypes[stepId] = stepType;
    renderGraph();
  }

  function recordStepTransition(stepId) {
    if (!stepId) return;
    if (lastRequestedStepId && lastRequestedStepId !== stepId) {
      const key = `${lastRequestedStepId}=>${stepId}`;
      if (!stepEdgeKeys.has(key)) {
        stepEdgeKeys.add(key);
        stepEdges.push({ from: lastRequestedStepId, to: stepId });
      }
    }
    lastRequestedStepId = stepId;
  }

  function renderGraph() {
    graphViewEl.innerHTML = "";
    if (stepOrder.length === 0) {
      graphViewEl.innerHTML = '<p class="graph-empty">No runtime steps yet.</p>';
      return;
    }

    const steps = stepOrder.map((stepId) => ({
      id: stepId,
      type: stepTypes[stepId] || "step",
    }));

    renderFlowInto(graphViewEl, steps, stepEdges, stepStates);
  }

  function renderFlowInto(container, steps, edges, states) {
    if (!steps || steps.length === 0) {
      container.innerHTML = '<p class="graph-empty">No runtime steps yet.</p>';
      return;
    }

    const NODE_W = 180;
    const NODE_H = 56;
    const GAP_Y = 40;
    const GAP_X = 30;
    const PAD = 30;

    const layout = computeLayout(steps, edges, NODE_W, NODE_H, GAP_X, GAP_Y);
    const svgW = layout.width + PAD * 2;
    const svgH = layout.height + PAD * 2;

    const ns = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("width", String(svgW));
    svg.setAttribute("height", String(svgH));
    svg.setAttribute("viewBox", `0 0 ${svgW} ${svgH}`);
    svg.setAttribute("class", "flow-svg");

    const defs = document.createElementNS(ns, "defs");
    defs.innerHTML = `<marker id="graph-arrowhead" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="currentColor"/></marker>`;
    svg.appendChild(defs);

    for (const edge of edges) {
      const fromPos = layout.positions[edge.from];
      const toPos = layout.positions[edge.to];
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
    }

    for (const step of steps) {
      const pos = layout.positions[step.id];
      if (!pos) continue;

      const g = document.createElementNS(ns, "g");
      g.setAttribute("class", `step-node state-${(states && states[step.id]) || "pending"}`);
      g.setAttribute("data-step", step.id);
      g.setAttribute("transform", `translate(${pos.x + PAD}, ${pos.y + PAD})`);

      const color = TYPE_COLORS[step.type] || "#64748b";
      const rect = document.createElementNS(ns, "rect");
      rect.setAttribute("width", String(NODE_W));
      rect.setAttribute("height", String(NODE_H));
      rect.setAttribute("fill", `${color}22`);
      rect.setAttribute("stroke", color);
      g.appendChild(rect);

      const label = document.createElementNS(ns, "text");
      label.setAttribute("x", "12");
      label.setAttribute("y", "22");
      label.setAttribute("class", "step-label");
      label.textContent = step.id;
      g.appendChild(label);

      const badge = document.createElementNS(ns, "text");
      badge.setAttribute("x", "12");
      badge.setAttribute("y", "40");
      badge.setAttribute("class", "step-type-badge");
      badge.textContent = step.type;
      g.appendChild(badge);

      const status = document.createElementNS(ns, "text");
      status.setAttribute("x", String(NODE_W - 8));
      status.setAttribute("y", "20");
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

    container.innerHTML = "";
    container.appendChild(svg);
  }

  function computeLayout(steps, edges, nodeW, nodeH, gapX, gapY) {
    const positions = {};
    const outgoing = {};
    const incomingCount = {};
    let currentY = 0;
    let maxWidth = nodeW;

    for (const step of steps) {
      outgoing[step.id] = [];
      incomingCount[step.id] = 0;
    }

    for (const edge of edges) {
      if (!outgoing[edge.from]) outgoing[edge.from] = [];
      outgoing[edge.from].push(edge.to);
      if (!Object.prototype.hasOwnProperty.call(incomingCount, edge.to))
        incomingCount[edge.to] = 0;
      incomingCount[edge.to] += 1;
    }

    const branchTargets = new Set();
    const mergeTargets = new Set();
    for (const step of steps) {
      const nextTargets = [...new Set(outgoing[step.id] || [])];
      if (nextTargets.length > 1) {
        for (const target of nextTargets) branchTargets.add(target);
      }
      if ((incomingCount[step.id] || 0) > 1)
        mergeTargets.add(step.id);
    }

    const placed = new Set();
    for (const step of steps) {
      if (placed.has(step.id)) continue;

      const nextTargets = [...new Set(outgoing[step.id] || [])]
        .filter((target) => steps.some((x) => x.id === target));

      if (nextTargets.length > 1) {
        const totalBranchW = nextTargets.length * nodeW + (nextTargets.length - 1) * gapX;
        const parentX = Math.max(0, (totalBranchW - nodeW) / 2);
        positions[step.id] = { x: parentX, y: currentY };
        placed.add(step.id);
        currentY += nodeH + gapY;

        let bx = 0;
        for (const target of nextTargets) {
          if (!placed.has(target)) {
            positions[target] = { x: bx, y: currentY };
            placed.add(target);
          }
          bx += nodeW + gapX;
        }

        maxWidth = Math.max(maxWidth, totalBranchW);
        currentY += nodeH + gapY;
        continue;
      }

      if (mergeTargets.has(step.id) || !branchTargets.has(step.id)) {
        positions[step.id] = { x: Math.max(0, (maxWidth - nodeW) / 2), y: currentY };
        placed.add(step.id);
        currentY += nodeH + gapY;
      }
    }

    for (const step of steps) {
      if (!placed.has(step.id)) {
        positions[step.id] = { x: Math.max(0, (maxWidth - nodeW) / 2), y: currentY };
        placed.add(step.id);
        currentY += nodeH + gapY;
      }
    }

    return {
      positions,
      width: Math.max(maxWidth, nodeW),
      height: Math.max(currentY - gapY, nodeH),
    };
  }

  function updateRunContextView() {
    runContextEl.textContent = JSON.stringify(
      {
        actorId: runActorId || "",
        runId: runRunId || "",
        commandId: runCommandId || "",
        workflowName: runWorkflowName || "",
      },
      null,
      2
    );
  }

  function showInteraction(interaction) {
    interactionCardEl.classList.remove("hidden");

    if (interaction.type === "human_approval") {
      interactionContentEl.innerHTML = `
        <div>${escapeHtml(interaction.prompt)}</div>
        <textarea id="approval-input" rows="3" placeholder="Optional feedback..."></textarea>
        <div class="interaction-actions">
          <button id="approve-btn">Approve</button>
          <button id="reject-btn" class="btn-danger">Reject</button>
        </div>
      `;
      $("#approve-btn").addEventListener("click", () => submitApproval(interaction, true));
      $("#reject-btn").addEventListener("click", () => submitApproval(interaction, false));
      return;
    }

    if (interaction.type === "wait_signal") {
      interactionContentEl.innerHTML = `
        <div>${escapeHtml(interaction.prompt)}</div>
        <input id="signal-input" type="text" placeholder="Signal payload (optional)" />
        <div class="interaction-actions">
          <button id="signal-btn">Send Signal (${escapeHtml(interaction.signalName)})</button>
        </div>
      `;
      $("#signal-btn").addEventListener("click", () => submitSignal(interaction));
      return;
    }

    interactionContentEl.innerHTML = `
      <div>${escapeHtml(interaction.prompt)}</div>
      <textarea id="human-input" rows="3" placeholder="Provide input..."></textarea>
      <div class="interaction-actions">
        <button id="human-submit-btn">Submit</button>
      </div>
    `;
    $("#human-submit-btn").addEventListener("click", () => submitHumanInput(interaction));
  }

  async function submitApproval(interaction, approved) {
    const feedbackEl = $("#approval-input");
    const userInput = feedbackEl ? feedbackEl.value.trim() : "";
    await postJson("/api/workflows/resume", {
      actorId: interaction.actorId || runActorId,
      runId: interaction.runId || runRunId,
      stepId: interaction.stepId,
      commandId: runCommandId || undefined,
      approved,
      userInput: approved ? "" : userInput,
    });
    addLog(`RESUME: ${approved ? "approved" : "rejected"}`);
    interactionCardEl.classList.add("hidden");
  }

  async function submitHumanInput(interaction) {
    const inputEl = $("#human-input");
    const userInput = inputEl ? inputEl.value.trim() : "";
    await postJson("/api/workflows/resume", {
      actorId: interaction.actorId || runActorId,
      runId: interaction.runId || runRunId,
      stepId: interaction.stepId,
      commandId: runCommandId || undefined,
      approved: true,
      userInput,
    });
    addLog("RESUME: human_input submitted");
    interactionCardEl.classList.add("hidden");
  }

  async function submitSignal(interaction) {
    const signalEl = $("#signal-input");
    const payload = signalEl ? signalEl.value.trim() : "";
    await postJson("/api/workflows/signal", {
      actorId: interaction.actorId || runActorId,
      runId: interaction.runId || runRunId,
      signalName: interaction.signalName || "resume",
      commandId: runCommandId || undefined,
      payload,
    });
    addLog(`SIGNAL: ${interaction.signalName}`);
    interactionCardEl.classList.add("hidden");
  }

  async function autoResume(interaction) {
    try {
      if (interaction.type === "human_approval") {
        await postJson("/api/workflows/resume", {
          actorId: interaction.actorId || runActorId,
          runId: interaction.runId || runRunId,
          stepId: interaction.stepId,
          commandId: runCommandId || undefined,
          approved: true,
          userInput: "",
        });
        addLog("AUTO_RESUME: approval");
      } else if (interaction.type === "human_input") {
        await postJson("/api/workflows/resume", {
          actorId: interaction.actorId || runActorId,
          runId: interaction.runId || runRunId,
          stepId: interaction.stepId,
          commandId: runCommandId || undefined,
          approved: true,
          userInput: "auto_input",
        });
        addLog("AUTO_RESUME: human_input");
      }
      interactionCardEl.classList.add("hidden");
    } catch (err) {
      addLog(`AUTO_RESUME failed: ${err?.message || String(err)}`);
    }
  }

  function addMessage(role, text) {
    const el = document.createElement("div");
    el.className = `msg ${role}`;
    el.textContent = text;
    chatMessagesEl.appendChild(el);
    chatMessagesEl.scrollTop = chatMessagesEl.scrollHeight;
    return el;
  }

  function addLog(text) {
    const line = document.createElement("div");
    line.className = "log-line";
    line.textContent = `[${new Date().toLocaleTimeString()}] ${text}`;
    logViewEl.appendChild(line);
    logViewEl.scrollTop = logViewEl.scrollHeight;
  }

  async function postJson(url, payload) {
    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(text || `HTTP ${res.status}`);
    }
    return res.json();
  }

  async function streamSse(url, payload, onData) {
    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });

    if (!res.ok) {
      const text = await res.text();
      throw new Error(text || `HTTP ${res.status}`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let newlineIndex;
      while ((newlineIndex = buffer.indexOf("\n")) !== -1) {
        const line = buffer.slice(0, newlineIndex).trim();
        buffer = buffer.slice(newlineIndex + 1);
        if (!line.startsWith("data:")) continue;
        const payloadText = line.slice(5).trim();
        onData(payloadText);
      }
    }
  }

  function readValue(obj, camel, pascal) {
    if (!obj || typeof obj !== "object") return "";
    return obj[camel] ?? obj[pascal] ?? "";
  }

  function parseJson(text) {
    try {
      return JSON.parse(text);
    } catch {
      return null;
    }
  }

  function stringifyResult(result) {
    if (result == null) return "";
    if (typeof result === "string") return result;
    if (typeof result.output === "string") return result.output;
    try {
      return JSON.stringify(result, null, 2);
    } catch {
      return String(result);
    }
  }

  function escapeHtml(value) {
    return String(value || "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#39;");
  }
})();
