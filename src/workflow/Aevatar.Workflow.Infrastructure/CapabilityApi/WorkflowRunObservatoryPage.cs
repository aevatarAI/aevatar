namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

// 06-19-workflow-run-observatory (C2): one inline self-contained HTML page (no wwwroot, no build step;
// mirrors the /status precedent). Browser OIDC Authorization Code + PKCE against nyxid (reuse of the
// console-web client; no server-side cookie/session, no NyxID change). Read-only: no edit/run/stop
// controls anywhere. CSS variables + dark mode, responsive, keyboard-accessible. Near-live via polling
// (~3s, paused when the tab is hidden); live and history share the one read path.
internal static class WorkflowRunObservatoryPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en" data-theme="dark">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Workflow Run Observatory</title>
<style>
:root {
  --bg: #0f1115; --panel: #171a21; --panel-2: #1f232c; --border: #2a2f3a;
  --text: #e6e9ef; --muted: #9aa3b2; --accent: #5b8cff; --ok: #3fb950; --warn: #d29922;
  --err: #f85149; --running: #58a6ff; --radius: 10px; --gap: 12px;
  --mono: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  --sans: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
}
@media (prefers-color-scheme: light) {
  html:not([data-theme="dark"]) {
    --bg: #f6f7f9; --panel: #ffffff; --panel-2: #f0f2f5; --border: #d8dce3;
    --text: #1b1f27; --muted: #5a6373;
  }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font-family: var(--sans); font-size: 14px; }
header {
  display: flex; align-items: center; gap: var(--gap); padding: 10px 16px;
  border-bottom: 1px solid var(--border); background: var(--panel); position: sticky; top: 0; z-index: 10;
}
header h1 { font-size: 15px; margin: 0; font-weight: 600; }
header .spacer { flex: 1; }
.badge { font-size: 11px; color: var(--muted); border: 1px solid var(--border); padding: 2px 8px; border-radius: 999px; }
button {
  font-family: inherit; font-size: 13px; color: var(--text); background: var(--panel-2);
  border: 1px solid var(--border); border-radius: 8px; padding: 6px 12px; cursor: pointer;
}
button:hover { border-color: var(--accent); }
button:focus-visible, a:focus-visible, [tabindex]:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
main { display: grid; grid-template-columns: 320px 1fr; gap: var(--gap); padding: var(--gap); align-items: start; }
@media (max-width: 760px) { main { grid-template-columns: 1fr; } }
.panel { background: var(--panel); border: 1px solid var(--border); border-radius: var(--radius); overflow: hidden; }
.panel-head { padding: 10px 12px; border-bottom: 1px solid var(--border); display: flex; align-items: center; gap: 8px; }
.panel-head h2 { font-size: 13px; margin: 0; font-weight: 600; }
.panel-head .spacer { flex: 1; }
.run-list { list-style: none; margin: 0; padding: 0; max-height: 72vh; overflow: auto; }
.run-item { display: block; width: 100%; text-align: left; border: 0; border-bottom: 1px solid var(--border);
  background: transparent; border-radius: 0; padding: 10px 12px; cursor: pointer; }
.run-item:hover { background: var(--panel-2); }
.run-item.active { background: var(--panel-2); border-left: 3px solid var(--accent); }
.run-item .name { font-weight: 600; }
.run-item .meta { color: var(--muted); font-size: 12px; margin-top: 2px; }
.status-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 6px; vertical-align: middle; }
.s-running { background: var(--running); } .s-completed { background: var(--ok); }
.s-failed, .s-timed_out { background: var(--err); } .s-stopped, .s-disabled { background: var(--warn); }
.s-unknown, .s-not_found { background: var(--muted); }
select, input { font-family: inherit; font-size: 13px; color: var(--text); background: var(--panel-2);
  border: 1px solid var(--border); border-radius: 8px; padding: 5px 8px; }
.tabs { display: flex; gap: 6px; }
.tab { padding: 5px 10px; border-radius: 8px; cursor: pointer; border: 1px solid transparent; color: var(--muted); }
.tab[aria-selected="true"] { color: var(--text); border-color: var(--border); background: var(--panel-2); }
.timeline { list-style: none; margin: 0; padding: 8px 12px; max-height: 72vh; overflow: auto; }
.event { padding: 8px 10px; border: 1px solid var(--border); border-radius: 8px; margin-bottom: 8px; background: var(--panel-2); }
.event .row { display: flex; gap: 8px; align-items: baseline; }
.event .kind { font-weight: 600; }
.event .ts { color: var(--muted); font-size: 12px; margin-left: auto; }
.event .msg { color: var(--text); margin-top: 4px; word-break: break-word; }
.kpill { font-size: 11px; padding: 1px 7px; border-radius: 999px; border: 1px solid var(--border); }
.k-RunStarted, .k-RunFinished { color: var(--ok); }
.k-RunError, .k-RunStopped { color: var(--err); }
.k-ToolCall { color: var(--accent); }
.k-HumanInputRequest { color: var(--warn); }
.tool { margin-top: 6px; border-top: 1px dashed var(--border); padding-top: 6px; font-family: var(--mono); font-size: 12px; }
.tool .lbl { color: var(--muted); }
pre { margin: 4px 0 0; padding: 6px 8px; background: var(--bg); border: 1px solid var(--border); border-radius: 6px;
  overflow: auto; max-height: 180px; font-family: var(--mono); font-size: 12px; white-space: pre-wrap; word-break: break-word; }
.empty { padding: 24px; color: var(--muted); text-align: center; }
.graph { padding: 12px; max-height: 72vh; overflow: auto; font-family: var(--mono); font-size: 12px; }
.usage { display: flex; gap: 14px; padding: 8px 12px; color: var(--muted); border-top: 1px solid var(--border); flex-wrap: wrap; }
.login { display: grid; place-items: center; min-height: 60vh; }
.login .card { background: var(--panel); border: 1px solid var(--border); border-radius: var(--radius); padding: 28px; text-align: center; max-width: 420px; }
.hidden { display: none !important; }
.err-banner { color: var(--err); padding: 8px 16px; border-bottom: 1px solid var(--border); }
</style>
</head>
<body>
<header>
  <h1>Workflow Run Observatory</h1>
  <span class="badge">read-only</span>
  <span class="spacer"></span>
  <span id="account" class="badge hidden"></span>
  <button id="switch-account" class="hidden" type="button">Switch account</button>
  <button id="login-btn" class="hidden" type="button">Sign in with nyxid</button>
</header>
<div id="err-banner" class="err-banner hidden" role="alert"></div>

<section id="login-view" class="login hidden">
  <div class="card">
    <h2>Sign in to view your workflow runs</h2>
    <p style="color:var(--muted)">Read-only access to runs under your nyxid account scope.</p>
    <button id="login-cta" type="button">Sign in with nyxid</button>
  </div>
</section>

<main id="app-view" class="hidden">
  <div class="panel" aria-label="Runs">
    <div class="panel-head">
      <h2>Runs (mine)</h2>
      <span class="spacer"></span>
      <label class="badge" for="status-filter">status</label>
      <select id="status-filter" aria-label="Filter runs by status">
        <option value="">all</option>
        <option value="running">running</option>
        <option value="completed">completed</option>
        <option value="failed">failed</option>
        <option value="stopped">stopped</option>
      </select>
    </div>
    <ul id="run-list" class="run-list" aria-live="polite"></ul>
    <div id="run-list-empty" class="empty">No runs yet.</div>
  </div>

  <div class="panel" aria-label="Run detail">
    <div class="panel-head">
      <h2 id="detail-title">Select a run</h2>
      <span class="spacer"></span>
      <div class="tabs" role="tablist" aria-label="Run views">
        <span class="tab" id="tab-timeline" role="tab" tabindex="0" aria-selected="true">Timeline</span>
        <span class="tab" id="tab-graph" role="tab" tabindex="0" aria-selected="false">Graph</span>
      </div>
    </div>
    <ul id="timeline" class="timeline" role="tabpanel" aria-labelledby="tab-timeline"></ul>
    <div id="graph" class="graph hidden" role="tabpanel" aria-labelledby="tab-graph"></div>
    <div id="detail-empty" class="empty">Select a run on the left to watch its execution.</div>
    <div id="usage" class="usage hidden"></div>
  </div>
</main>

<script>
"use strict";
const CFG = {
  authority: "https://nyx.chrono-ai.fun",
  clientId: "37a93189-2734-406e-bca1-7dbdf25c5a53",
  scope: "openid profile email proxy",
  redirectUri: location.origin + "/workflow/observatory/callback",
  storageKey: "aevatar-observatory:nyxid:pkce",
  pollMs: 3000,
};
const TOKEN_KEY = CFG.storageKey + ":token";
const PKCE_KEY = CFG.storageKey + ":pkce";

const $ = (id) => document.getElementById(id);
function showError(msg) { const b = $("err-banner"); b.textContent = msg; b.classList.remove("hidden"); }
function clearError() { $("err-banner").classList.add("hidden"); }

function base64url(buf) {
  return btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g,"-").replace(/\//g,"_").replace(/=+$/,"");
}
async function sha256(text) { return base64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text))); }
function randomString(len) {
  const a = new Uint8Array(len); crypto.getRandomValues(a);
  return base64url(a.buffer).slice(0, len);
}

function getToken() {
  const raw = localStorage.getItem(TOKEN_KEY);
  if (!raw) return null;
  try { return JSON.parse(raw); } catch (e) { console.warn("observatory: token parse failed", e); }
  return null;
}
function setToken(t) { localStorage.setItem(TOKEN_KEY, JSON.stringify(t)); }
function clearToken() { localStorage.removeItem(TOKEN_KEY); }

async function beginLogin() {
  const verifier = randomString(64);
  const state = randomString(32);
  const challenge = await sha256(verifier);
  sessionStorage.setItem(PKCE_KEY, JSON.stringify({ verifier, state }));
  const u = new URL(CFG.authority + "/oauth/authorize");
  u.searchParams.set("response_type", "code");
  u.searchParams.set("client_id", CFG.clientId);
  u.searchParams.set("redirect_uri", CFG.redirectUri);
  u.searchParams.set("scope", CFG.scope);
  u.searchParams.set("state", state);
  u.searchParams.set("code_challenge", challenge);
  u.searchParams.set("code_challenge_method", "S256");
  location.assign(u.toString());
}

async function completeLoginIfCallback() {
  const params = new URLSearchParams(location.search);
  const code = params.get("code");
  if (!code) return false;
  const returnedState = params.get("state");
  const pending = JSON.parse(sessionStorage.getItem(PKCE_KEY) || "null");
  history.replaceState({}, "", "/workflow/observatory");
  if (!pending || pending.state !== returnedState) { showError("Login state mismatch. Please retry."); return false; }
  const form = new URLSearchParams();
  form.set("grant_type", "authorization_code");
  form.set("code", code);
  form.set("redirect_uri", CFG.redirectUri);
  form.set("client_id", CFG.clientId);
  form.set("code_verifier", pending.verifier);
  const res = await fetch(CFG.authority + "/oauth/token", {
    method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: form.toString(),
  });
  sessionStorage.removeItem(PKCE_KEY);
  if (!res.ok) { showError("Token exchange failed (" + res.status + ")."); return false; }
  const token = await res.json();
  token.obtained_at = Date.now();
  setToken(token);
  return true;
}

async function fetchUserInfo() {
  const token = getToken(); if (!token) return null;
  try {
    const res = await fetch(CFG.authority + "/oauth/userinfo", { headers: { Authorization: "Bearer " + token.access_token } });
    return res.ok ? await res.json() : null;
  } catch (e) { console.warn("observatory: userinfo fetch failed", e); }
  return null;
}

async function api(path) {
  const token = getToken();
  if (!token) throw new Error("not-authenticated");
  const res = await fetch(path, { headers: { Authorization: "Bearer " + token.access_token } });
  if (res.status === 401) { clearToken(); renderAuthState(); throw new Error("unauthorized"); }
  if (res.status === 404) return null;
  if (!res.ok) throw new Error("api-error-" + res.status);
  return await res.json();
}

const fmtTime = (iso) => { try { return new Date(iso).toLocaleTimeString(); } catch { return iso || ""; } };
const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

let state = { runs: [], selectedRunId: null, activeTab: "timeline", pollTimer: null };

function renderRunList() {
  const ul = $("run-list");
  ul.innerHTML = "";
  $("run-list-empty").classList.toggle("hidden", state.runs.length > 0);
  for (const run of state.runs) {
    const li = document.createElement("li");
    const btn = document.createElement("button");
    btn.className = "run-item" + (run.runId === state.selectedRunId ? " active" : "");
    btn.type = "button";
    btn.setAttribute("aria-pressed", String(run.runId === state.selectedRunId));
    btn.innerHTML =
      '<div class="name"><span class="status-dot s-' + esc(run.status) + '"></span>' + esc(run.workflowName || run.runId) + "</div>" +
      '<div class="meta">' + esc(run.status) + " &middot; " + esc(run.runId) + "</div>";
    btn.addEventListener("click", () => selectRun(run.runId));
    li.appendChild(btn);
    ul.appendChild(li);
  }
}

function renderEvent(ev) {
  let tool = "";
  if (ev.toolCall) {
    const t = ev.toolCall;
    tool = '<div class="tool"><div><span class="lbl">tool</span> ' + esc(t.toolName) +
      ' &middot; <span class="lbl">success</span> ' + (t.success ? "✓" : "✗") + "</div>" +
      (t.argumentsJson ? '<div><span class="lbl">arguments</span><pre>' + esc(t.argumentsJson) + "</pre></div>" : "") +
      (t.resultJson ? '<div><span class="lbl">result</span><pre>' + esc(t.resultJson) + "</pre></div>" : "") +
      (t.error ? '<div><span class="lbl">error</span><pre>' + esc(t.error) + "</pre></div>" : "") + "</div>";
  }
  return '<li class="event"><div class="row"><span class="kind"><span class="kpill k-' + esc(ev.kind) + '">' +
    esc(ev.kind) + "</span></span>" +
    (ev.stepId ? '<span class="badge">' + esc(ev.stepId) + "</span>" : "") +
    '<span class="ts">' + esc(fmtTime(ev.timestampUtc)) + "</span></div>" +
    (ev.message ? '<div class="msg">' + esc(ev.message) + "</div>" : "") + tool + "</li>";
}

function renderDetail(detail) {
  const hasRun = !!detail;
  $("detail-empty").classList.toggle("hidden", hasRun);
  if (!hasRun) {
    $("detail-title").textContent = "Select a run";
    $("timeline").innerHTML = ""; $("graph").innerHTML = ""; $("usage").classList.add("hidden");
    return;
  }
  const s = detail.summary;
  $("detail-title").innerHTML = '<span class="status-dot s-' + esc(s.status) + '"></span>' +
    esc(s.workflowName || s.runId) + ' <span class="badge">' + esc(s.status) + "</span>";
  $("timeline").innerHTML = (detail.timeline || []).map(renderEvent).join("") ||
    '<div class="empty">No events yet.</div>';
  const u = detail.usageTotals || {};
  $("usage").classList.remove("hidden");
  $("usage").innerHTML =
    "<span>prompt: " + (u.promptTokens || 0) + "</span>" +
    "<span>completion: " + (u.completionTokens || 0) + "</span>" +
    "<span>total: " + (u.totalTokens || 0) + "</span>" +
    "<span>cost: " + (u.cost || 0) + "</span>" +
    "<span>state v" + (s.stateVersion || 0) + "</span>";
}

function renderGraph(graph) {
  if (!graph) { $("graph").innerHTML = '<div class="empty">No graph.</div>'; return; }
  const nodes = (graph.nodes || []).map((n) => "&bull; " + esc(n.nodeId) + " (" + esc(n.nodeType) + ")").join("<br>");
  const edges = (graph.edges || []).map((e) => esc(e.fromNodeId) + " &rarr; " + esc(e.toNodeId) + " [" + esc(e.edgeType) + "]").join("<br>");
  $("graph").innerHTML = "<strong>Nodes</strong><br>" + (nodes || "—") + "<br><br><strong>Edges</strong><br>" + (edges || "—");
}

async function refreshRuns() {
  try {
    const status = $("status-filter").value;
    const q = new URLSearchParams(); if (status) q.set("status", status);
    const runs = await api("/api/workflow/observatory/runs" + (q.toString() ? "?" + q.toString() : ""));
    state.runs = runs || [];
    renderRunList();
    clearError();
  } catch (e) { if (e.message !== "unauthorized") showError("Failed to load runs."); }
}

async function refreshDetail() {
  if (!state.selectedRunId) { renderDetail(null); return; }
  try {
    if (state.activeTab === "graph") {
      const graph = await api("/api/workflow/observatory/runs/" + encodeURIComponent(state.selectedRunId) + "/graph");
      renderGraph(graph);
    }
    const detail = await api("/api/workflow/observatory/runs/" + encodeURIComponent(state.selectedRunId));
    if (detail === null) { showError("Run not found."); renderDetail(null); return; }
    renderDetail(detail);
  } catch (e) { if (e.message !== "unauthorized") showError("Failed to load run."); }
}

function selectRun(runId) {
  state.selectedRunId = runId;
  renderRunList();
  $("graph").classList.add("hidden"); $("timeline").classList.remove("hidden");
  setTab("timeline");
  refreshDetail();
}

function setTab(tab) {
  state.activeTab = tab;
  const isTimeline = tab === "timeline";
  $("tab-timeline").setAttribute("aria-selected", String(isTimeline));
  $("tab-graph").setAttribute("aria-selected", String(!isTimeline));
  $("timeline").classList.toggle("hidden", !isTimeline);
  $("graph").classList.toggle("hidden", isTimeline);
  refreshDetail();
}

async function pollTick() {
  if (document.hidden) return;
  await refreshRuns();
  await refreshDetail();
}
function startPolling() { stopPolling(); state.pollTimer = setInterval(pollTick, CFG.pollMs); }
function stopPolling() { if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; } }

async function renderAuthState() {
  const token = getToken();
  const authed = !!token;
  $("app-view").classList.toggle("hidden", !authed);
  $("login-view").classList.toggle("hidden", authed);
  $("login-btn").classList.toggle("hidden", authed);
  $("account").classList.toggle("hidden", !authed);
  $("switch-account").classList.toggle("hidden", !authed);
  if (authed) {
    const info = await fetchUserInfo();
    $("account").textContent = info ? (info.email || info.preferred_username || info.sub || "signed in") : "signed in";
    await refreshRuns();
    startPolling();
  } else {
    stopPolling();
  }
}

function wireEvents() {
  $("login-btn").addEventListener("click", beginLogin);
  $("login-cta").addEventListener("click", beginLogin);
  $("switch-account").addEventListener("click", () => { clearToken(); beginLogin(); });
  $("status-filter").addEventListener("change", refreshRuns);
  for (const [id, tab] of [["tab-timeline", "timeline"], ["tab-graph", "graph"]]) {
    const el = $(id);
    el.addEventListener("click", () => setTab(tab));
    el.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); setTab(tab); } });
  }
  document.addEventListener("visibilitychange", () => { if (!document.hidden) pollTick(); });
}

(async function init() {
  wireEvents();
  try { if (await completeLoginIfCallback()) { /* token stored */ } }
  catch (e) { showError("Login failed."); }
  await renderAuthState();
})();
</script>
</body>
</html>
""";
}
