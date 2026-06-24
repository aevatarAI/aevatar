namespace Aevatar.Mainnet.Host.Api.Voice;

// Voice console: one inline self-contained HTML page (no wwwroot, no build step), mirroring the
// /workflow/observatory and /workflow/studio precedent. Browser OIDC Authorization Code + PKCE against
// nyxid (reuse of the console-web client; same authority/clientId/scope as observatory/studio, isolated
// PKCE storage + callback route). The page is the anonymous static shell; the app is gated behind login
// exactly like its siblings, and carries the shared suite navigation so the five console pages move as one.
//
// Honest wiring: the page surfaces the ONE real voice HTTP write that exists today —
// POST /api/scopes/{scopeId}/gagent-actors/{actorId}/voice-presence/enable — plus the scope chip from
// GET /api/studio/context. Voice has no read GETs (no module/session/provider listing), so live-session,
// WHIP/WebRTC ingress and the vp-control display side-channel render honest "待后端 ②③" disabled stubs
// rather than mock data; they light up when sub-projects ②③ land their read surfaces.
internal static class VoiceConsolePage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
<title>语音在场 · Voice Presence · aevatar</title>
<style>
  /* =========================================================================
     aevatar 语音在场 / Voice Presence — 控制台套件成员（视觉令牌与 observatory/studio 一致）
     framework-agnostic：语义化 HTML + CSS 变量 + 少量 vanilla JS。
     ========================================================================= */

  /* ---- Design tokens : 暗色（默认，与 observatory/studio 一字不差）-------- */
  :root {
    --bg:#0f1115;
    --bg-grad:radial-gradient(1200px 600px at 78% -10%,#161b26 0%,#0f1115 60%);
    --panel:#171a21; --panel-2:#1f232c; --panel-3:#252b36;
    --border:#2a2f3a; --border-soft:#22272f;
    --fg:#e6e9ef; --fg-strong:#f4f6fb; --muted:#9aa3b2; --muted-2:#6c7585;
    --accent:#5b8cff; --accent-ink:#0b1020; --accent-soft:rgba(91,140,255,.14); --accent-line:rgba(91,140,255,.40);
    --ok:#3fb950; --ok-soft:rgba(63,185,80,.14); --warn:#d29922; --warn-soft:rgba(210,153,34,.15);
    --err:#f85149; --err-soft:rgba(248,81,73,.14); --run:#58a6ff; --run-soft:rgba(88,166,255,.16);
    --neutral:#9aa3b2; --neutral-soft:rgba(154,163,178,.12);
    --shadow:0 8px 30px rgba(0,0,0,.40); --shadow-sm:0 1px 2px rgba(0,0,0,.30);
    --ring:0 0 0 2px var(--bg),0 0 0 4px var(--accent);
    --r-sm:6px; --r:9px; --r-lg:13px; --r-pill:999px;
    --mono:ui-monospace,"SF Mono",SFMono-Regular,Menlo,Consolas,monospace;
    --sans:-apple-system,BlinkMacSystemFont,"Segoe UI",system-ui,"PingFang SC","Microsoft YaHei","Noto Sans CJK SC",sans-serif;
    --topbar-h:56px; color-scheme:dark;
  }
  @media (prefers-color-scheme: light) {
    :root:not([data-theme]) {
      --bg:#f5f7fa;
      --bg-grad:radial-gradient(1100px 560px at 80% -12%,#eef2f8 0%,#f5f7fa 58%);
      --panel:#ffffff; --panel-2:#f3f5f9; --panel-3:#eaeef4; --border:#dde2ea; --border-soft:#e7ebf1;
      --fg:#1c2128; --fg-strong:#0b0f14; --muted:#57606b; --muted-2:#828b97;
      --accent:#2f63e0; --accent-ink:#ffffff; --accent-soft:rgba(47,99,224,.10); --accent-line:rgba(47,99,224,.34);
      --ok:#1a7f37; --ok-soft:rgba(26,127,55,.12); --warn:#9a6700; --warn-soft:rgba(154,103,0,.12);
      --err:#cf222e; --err-soft:rgba(207,34,46,.10); --run:#2f6fed; --run-soft:rgba(47,111,237,.12);
      --neutral:#57606b; --neutral-soft:rgba(87,96,107,.10);
      --shadow:0 10px 30px rgba(16,24,40,.10); --shadow-sm:0 1px 2px rgba(16,24,40,.06);
      color-scheme:light;
    }
  }
  :root[data-theme="light"] {
    --bg:#f5f7fa;
    --bg-grad:radial-gradient(1100px 560px at 80% -12%,#eef2f8 0%,#f5f7fa 58%);
    --panel:#ffffff; --panel-2:#f3f5f9; --panel-3:#eaeef4; --border:#dde2ea; --border-soft:#e7ebf1;
    --fg:#1c2128; --fg-strong:#0b0f14; --muted:#57606b; --muted-2:#828b97;
    --accent:#2f63e0; --accent-ink:#ffffff; --accent-soft:rgba(47,99,224,.10); --accent-line:rgba(47,99,224,.34);
    --ok:#1a7f37; --ok-soft:rgba(26,127,55,.12); --warn:#9a6700; --warn-soft:rgba(154,103,0,.12);
    --err:#cf222e; --err-soft:rgba(207,34,46,.10); --run:#2f6fed; --run-soft:rgba(47,111,237,.12);
    --neutral:#57606b; --neutral-soft:rgba(87,96,107,.10);
    --shadow:0 10px 30px rgba(16,24,40,.10); --shadow-sm:0 1px 2px rgba(16,24,40,.06);
    color-scheme:light;
  }

  /* ---- base ------------------------------------------------------------- */
  * { box-sizing:border-box; }
  html,body { height:100%; }
  body {
    margin:0; background:var(--bg); background-image:var(--bg-grad); background-attachment:fixed;
    color:var(--fg); font:14px/1.55 var(--sans); -webkit-font-smoothing:antialiased; text-rendering:optimizeLegibility;
  }
  ::selection { background:var(--accent-soft); }
  button { font:inherit; color:inherit; cursor:pointer; }
  :focus-visible { outline:none; box-shadow:var(--ring); border-radius:var(--r-sm); }
  .mono { font-family:var(--mono); font-variant-numeric:tabular-nums; }
  .scroll::-webkit-scrollbar { width:10px; height:10px; }
  .scroll::-webkit-scrollbar-thumb { background:var(--panel-3); border-radius:999px; border:2px solid transparent; background-clip:content-box; }

  /* ---- status primitives (observatory) ---------------------------------- */
  .badge { display:inline-flex; align-items:center; gap:6px; padding:3px 9px; border-radius:var(--r-pill); font-size:12px; font-weight:600; border:1px solid transparent; white-space:nowrap; }
  .b-ok      { color:var(--ok); background:var(--ok-soft); border-color:color-mix(in oklab,var(--ok) 30%,transparent); }
  .b-warn    { color:var(--warn); background:var(--warn-soft); border-color:color-mix(in oklab,var(--warn) 30%,transparent); }
  .b-pending { color:var(--muted); background:var(--neutral-soft); border-color:var(--border); }

  /* =========================================================================
     Top bar + shared suite navigation (5 console pages move as one)
     ========================================================================= */
  .topbar {
    position:sticky; top:0; z-index:40; height:var(--topbar-h);
    display:flex; align-items:center; gap:12px; padding:0 14px;
    background:color-mix(in oklab,var(--panel) 88%,transparent);
    backdrop-filter:saturate(140%) blur(12px); -webkit-backdrop-filter:saturate(140%) blur(12px);
    border-bottom:1px solid var(--border);
  }
  .brand { display:flex; align-items:center; gap:10px; min-width:0; }
  .brand-mark { width:26px; height:26px; border-radius:7px; flex:0 0 auto; display:grid; place-items:center; color:#fff; box-shadow:var(--shadow-sm);
    background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); }
  .brand-name { font-weight:650; letter-spacing:-.01em; color:var(--fg-strong); white-space:nowrap; }
  .brand-sub { color:var(--muted-2); font-size:12px; white-space:nowrap; }

  /* shared cross-page nav — identical markup/behaviour on all five pages */
  .suite-nav { display:flex; align-items:center; gap:2px; overflow-x:auto; scrollbar-width:none; }
  .suite-nav::-webkit-scrollbar { display:none; }
  .suite-nav a { display:inline-flex; align-items:center; gap:6px; padding:6px 11px; border-radius:var(--r-pill);
    font-size:12.5px; font-weight:600; color:var(--muted); text-decoration:none; border:1px solid transparent; white-space:nowrap;
    transition:color .15s,background .15s,border-color .15s; }
  .suite-nav a:hover { color:var(--fg); background:var(--panel-2); }
  .suite-nav a[aria-current="page"] { color:var(--accent); background:var(--accent-soft); border-color:var(--accent-line); }
  .suite-nav svg { width:14px; height:14px; flex:0 0 auto; }

  .scope-chip { display:inline-flex; align-items:center; gap:7px; padding:4px 10px; border-radius:var(--r-pill); background:var(--panel-2); border:1px solid var(--border); font-size:12px; color:var(--muted); }
  .scope-chip .sid { font-family:var(--mono); font-size:11.5px; color:var(--fg); }
  .scope-chip svg { color:var(--ok); }
  .spacer { flex:1 1 auto; }
  .iconbtn { width:34px; height:34px; display:grid; place-items:center; background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r); color:var(--muted); transition:color .15s,border-color .15s; }
  .iconbtn:hover { color:var(--fg); border-color:var(--muted-2); }
  .account { display:inline-flex; align-items:center; gap:8px; padding:4px 6px 4px 4px; border-radius:var(--r-pill); background:var(--panel-2); border:1px solid var(--border); }
  .avatar { width:24px; height:24px; border-radius:50%; flex:0 0 auto; display:grid; place-items:center; font-size:12px; font-weight:700; color:#fff; background:linear-gradient(140deg,#6f9bff,#9b6bff); }
  .account .who { font-size:12.5px; color:var(--fg); max-width:140px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .linkbtn { background:transparent; border:0; color:var(--accent); font-size:12.5px; font-weight:600; padding:4px 6px; border-radius:var(--r-sm); }
  .linkbtn:hover { text-decoration:underline; }

  /* =========================================================================
     Page body — single centered column (ops/admin layout)
     ========================================================================= */
  .page { max-width:760px; margin:0 auto; padding:28px 20px 64px; }
  .page-head { margin-bottom:22px; }
  .page-title { font-size:20px; font-weight:680; color:var(--fg-strong); margin:0; letter-spacing:-.01em; }
  .page-sub { font-size:12.5px; color:var(--muted); margin:7px 0 0; line-height:1.6; max-width:60ch; }

  .card { border:1px solid var(--border); border-radius:var(--r-lg); background:var(--panel); padding:18px 18px 20px; margin-bottom:18px; box-shadow:var(--shadow-sm); }
  .card.stub { opacity:.92; border-style:dashed; background:color-mix(in oklab,var(--panel) 60%,transparent); }
  .card-head { display:flex; align-items:center; gap:11px; margin-bottom:6px; }
  .card-ic { width:32px; height:32px; border-radius:9px; display:grid; place-items:center; flex:0 0 auto; color:var(--accent); background:var(--accent-soft); border:1px solid var(--accent-line); }
  .card-ic.muted { color:var(--muted); background:var(--neutral-soft); border-color:var(--border); }
  .card-title { font-weight:680; color:var(--fg-strong); font-size:15px; letter-spacing:-.01em; }
  .card-meta { margin-left:auto; }
  .card-desc { font-size:12.5px; color:var(--muted); line-height:1.6; margin:2px 0 14px; }

  /* form */
  .field { margin-bottom:13px; }
  .field label { display:block; font-size:12px; font-weight:600; color:var(--muted); margin-bottom:5px; }
  .field label .req { color:var(--err); margin-left:3px; }
  .field input { width:100%; background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r); color:var(--fg);
    font:13.5px/1.5 var(--sans); padding:9px 11px; transition:border-color .15s,box-shadow .15s; }
  .field input:focus { outline:none; border-color:var(--accent-line); box-shadow:0 0 0 3px var(--accent-soft); }
  .field input::placeholder { color:var(--muted-2); }
  .field input.mono { font-family:var(--mono); font-size:12.5px; }
  .field .hint { font-size:11px; color:var(--muted-2); margin-top:4px; line-height:1.5; }
  .form-actions { display:flex; align-items:center; gap:12px; margin-top:6px; }
  .btn-primary { padding:10px 18px; border-radius:var(--r); border:1px solid transparent; background:var(--accent); color:var(--accent-ink);
    font-weight:650; font-size:13.5px; display:inline-flex; align-items:center; justify-content:center; gap:8px; transition:filter .15s,opacity .15s; }
  .btn-primary:hover { filter:brightness(1.08); }
  .btn-primary:disabled { opacity:.5; cursor:default; }
  .spin { width:13px; height:13px; border-radius:50%; border:1.8px solid color-mix(in oklab,var(--accent-ink) 45%,transparent); border-top-color:var(--accent-ink); animation:spin .7s linear infinite; }
  @keyframes spin { to { transform:rotate(360deg); } }

  /* key/value receipt + banners */
  .kv { display:flex; justify-content:space-between; gap:12px; padding:6px 0; font-size:12.5px; border-bottom:1px solid var(--border-soft); }
  .kv:last-child { border-bottom:0; }
  .kv .k { color:var(--muted); flex:0 0 auto; }
  .kv .v { color:var(--fg); font-family:var(--mono); font-size:12px; text-align:right; word-break:break-all; }
  .result { margin-top:14px; border-radius:var(--r); border:1px solid var(--border-soft); padding:13px 14px; }
  .result.ok  { background:var(--ok-soft); border-color:color-mix(in oklab,var(--ok) 34%,transparent); }
  .result.err { background:var(--err-soft); border-color:color-mix(in oklab,var(--err) 38%,transparent); }
  .result-head { display:flex; gap:9px; align-items:flex-start; }
  .result-head svg { flex:0 0 auto; margin-top:1px; }
  .result.ok .result-head svg { color:var(--ok); }
  .result.err .result-head svg { color:var(--err); }
  .result-l { font-weight:650; font-size:13px; }
  .result.ok .result-l { color:var(--ok); } .result.err .result-l { color:var(--err); }
  .result-m { color:var(--muted); font-size:12.5px; margin-top:3px; line-height:1.6; }
  .result-grid { margin-top:11px; }

  .note { display:flex; gap:9px; align-items:flex-start; padding:11px 13px; border-radius:var(--r); background:var(--warn-soft);
    border:1px solid color-mix(in oklab,var(--warn) 32%,transparent); margin-top:13px; }
  .note svg { color:var(--warn); flex:0 0 auto; margin-top:1px; }
  .note .nt { font-size:12.5px; color:var(--fg); line-height:1.6; }
  .note .nt b { color:var(--warn); }

  .contract { display:grid; gap:8px; }
  .contract .row { display:flex; gap:10px; align-items:baseline; font-size:12.5px; }
  .contract .row .lbl { flex:0 0 132px; color:var(--muted); }
  .contract .row .val { color:var(--fg); font-family:var(--mono); font-size:12px; word-break:break-word; }
  code.inline { font-family:var(--mono); font-size:12px; background:var(--panel-2); border:1px solid var(--border-soft); border-radius:5px; padding:1px 6px; color:var(--fg); }

  /* login */
  .login { min-height:calc(100dvh - var(--topbar-h)); display:grid; place-items:center; padding:24px; }
  .login-card { width:min(420px,100%); background:var(--panel); border:1px solid var(--border); border-radius:var(--r-lg); padding:32px 30px; box-shadow:var(--shadow); text-align:center; }
  .login-mark { width:52px; height:52px; border-radius:14px; margin:0 auto 18px; background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); display:grid; place-items:center; color:#fff; box-shadow:var(--shadow); }
  .login h1 { font-size:19px; font-weight:700; letter-spacing:-.01em; color:var(--fg-strong); margin:0 0 8px; }
  .login p { color:var(--muted); font-size:13.5px; margin:0 auto 22px; max-width:32ch; line-height:1.6; }
  .login .btn-primary { width:100%; padding:11px 16px; font-size:14px; }
  .login-foot { margin-top:16px; font-size:11.5px; color:var(--muted-2); display:inline-flex; align-items:center; gap:6px; }

  @media (max-width:760px) {
    .brand-sub, .scope-chip { display:none; }
    .suite-nav .nav-label { display:none; }
    .suite-nav a { padding:6px 8px; }
    .account .who { display:none; }
  }
  @media (prefers-reduced-motion: reduce) {
    *,*::before,*::after { animation-duration:.001ms !important; animation-iteration-count:1 !important; transition-duration:.001ms !important; }
  }
</style>
</head>
<body>
  <div id="app"></div>

<script>
/* ===========================================================================
   数据契约（与现有 console 套件一致，无新增后端）：
     - GET  /api/studio/context → {scopeId, scopeResolved, scopeSource}（scope chip）
     - POST /api/scopes/{scopeId}/gagent-actors/{actorId}/voice-presence/enable?agentKind=...
            body: {moduleName}  →  202 受理回执 / 4xx-5xx {error,detail}
   语音目前没有只读 GET（模块/会话/provider 列举），因此「实时会话 / WHIP 入站 /
   显示侧信道」只做诚实占位（待后端 ②③），不编造数据。
   =========================================================================== */

/* ===========================================================================
   Auth layer — mirrors the observatory/studio OIDC PKCE gate verbatim.
   Browser OIDC Authorization Code + PKCE against nyxid (same console-web client;
   authority/clientId/scope identical), isolated PKCE storage + callback route.
   =========================================================================== */
const CFG = {
  authority: "https://nyx.chrono-ai.fun",
  clientId: "37a93189-2734-406e-bca1-7dbdf25c5a53",
  scope: "openid profile email proxy",
  redirectUri: location.origin + "/voice/callback",
  storageKey: "aevatar-voice:nyxid:pkce"
};
const TOKEN_KEY = CFG.storageKey + ":token";
const PKCE_KEY  = CFG.storageKey + ":pkce";

function b64url(buf){ return btoa(String.fromCharCode.apply(null, new Uint8Array(buf))).replace(/\+/g,"-").replace(/\//g,"_").replace(/=+$/,""); }
async function sha256(text){ return b64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text))); }
function randomString(len){ const a=new Uint8Array(len); crypto.getRandomValues(a); return b64url(a.buffer).slice(0, len); }

function getToken(){ const raw=localStorage.getItem(TOKEN_KEY); if(!raw) return null; try { return JSON.parse(raw); } catch(e){ console.warn("voice: token parse failed", e); } return null; }
function setToken(t){ localStorage.setItem(TOKEN_KEY, JSON.stringify(t)); }
function clearToken(){ localStorage.removeItem(TOKEN_KEY); }

async function beginLogin(){
  const verifier = randomString(64);
  const st = randomString(32);
  const challenge = await sha256(verifier);
  sessionStorage.setItem(PKCE_KEY, JSON.stringify({ verifier, state: st, returnTo: location.pathname }));
  const u = new URL(CFG.authority + "/oauth/authorize");
  u.searchParams.set("response_type", "code");
  u.searchParams.set("client_id", CFG.clientId);
  u.searchParams.set("redirect_uri", CFG.redirectUri);
  u.searchParams.set("scope", CFG.scope);
  u.searchParams.set("state", st);
  u.searchParams.set("code_challenge", challenge);
  u.searchParams.set("code_challenge_method", "S256");
  location.assign(u.toString());
}

async function completeLoginIfCallback(){
  const params = new URLSearchParams(location.search);
  const code = params.get("code");
  if(!code) return false;
  const returnedState = params.get("state");
  const pending = JSON.parse(sessionStorage.getItem(PKCE_KEY) || "null");
  history.replaceState({}, "", (pending && pending.returnTo) || "/voice");
  if(!pending || pending.state !== returnedState){ console.warn("voice: login state mismatch"); return false; }
  const form = new URLSearchParams();
  form.set("grant_type", "authorization_code");
  form.set("code", code);
  form.set("redirect_uri", CFG.redirectUri);
  form.set("client_id", CFG.clientId);
  form.set("code_verifier", pending.verifier);
  const res = await fetch(CFG.authority + "/oauth/token", { method:"POST", headers:{ "Content-Type":"application/x-www-form-urlencoded" }, body: form.toString() });
  sessionStorage.removeItem(PKCE_KEY);
  if(!res.ok){ console.warn("voice: token exchange failed", res.status); return false; }
  const token = await res.json();
  token.obtained_at = Date.now();
  setToken(token);
  return true;
}

async function fetchUserInfo(){
  const token = getToken(); if(!token) return null;
  try { const res = await fetch(CFG.authority + "/oauth/userinfo", { headers:{ Authorization:"Bearer " + token.access_token } }); return res.ok ? await res.json() : null; }
  catch(e){ console.warn("voice: userinfo fetch failed", e); }
  return null;
}
function toAccount(info){ return info ? { label: info.email || info.preferred_username || info.sub || "已登录" } : null; }

// scope-gated bearer GET helper, identical to observatory/studio.
async function apiGet(path){
  const token = getToken(); if(!token) throw new Error("not-authenticated");
  const res = await fetch(path, { headers:{ Authorization:"Bearer " + token.access_token } });
  if(res.status === 401){ signOutSilent(); throw new Error("unauthorized"); }
  if(res.status === 404) return null;
  if(!res.ok) throw new Error("api-error-" + res.status);
  return await res.json();
}

function signOut(){ clearToken(); beginLogin(); }
function signOutSilent(){ clearToken(); state.signedIn = false; render(); }

/* ===========================================================================
   App state + DOM helpers
   =========================================================================== */
const $ = (s, r=document) => r.querySelector(s);
function el(tag, attrs={}, html){
  const n=document.createElement(tag);
  for(const k in attrs){
    if(k==="class") n.className=attrs[k];
    else if(k.startsWith("on")&&typeof attrs[k]==="function") n.addEventListener(k.slice(2),attrs[k]);
    else if(attrs[k]!=null) n.setAttribute(k,attrs[k]);
  }
  if(html!=null) n.innerHTML=html;
  return n;
}
function esc(s){ return String(s==null?"":s).replace(/[&<>"']/g,c=>({ "&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;" }[c])); }
function initials(label){ const s=String(label||"?").trim(); return (s[0]||"?").toUpperCase(); }

const state = {
  signedIn:false,
  theme: localStorage.getItem("voice-theme") || null,
  scopeId:null, scopeResolved:false, scopeSource:null,
  submitting:false,
  result:null,                 // { ok, label, message, detail:{} }
  form:{ scopeId:"", actorId:"", agentKind:"", moduleName:"" }
};
let accountLabel = "已登录";

function applyTheme(){ if(state.theme==="light"||state.theme==="dark")document.documentElement.setAttribute("data-theme",state.theme); else document.documentElement.removeAttribute("data-theme"); }
function effDark(){ if(state.theme)return state.theme==="dark"; return !window.matchMedia||!window.matchMedia("(prefers-color-scheme: light)").matches; }

const ICON={
  lock:'<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
  sun:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="12" cy="12" r="4.2"/><path d="M12 2v2.4M12 19.6V22M2 12h2.4M19.6 12H22M4.6 4.6l1.7 1.7M17.7 17.7l1.7 1.7M19.4 4.6l-1.7 1.7M6.3 17.7l-1.7 1.7"/></svg>',
  moon:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A8.5 8.5 0 1 1 11.2 3a6.6 6.6 0 0 0 9.8 9.8Z"/></svg>',
  check:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12.5 4.2 4.2L19 7"/></svg>',
  okc:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="m8.5 12.2 2.4 2.4 4.6-4.8"/></svg>',
  alert:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M12 8.5v4.5M12 16.2v.2"/><path d="M10.3 3.9 2.5 17.5A2 2 0 0 0 4.2 20.5h15.6a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg>',
  spark:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5 18 18M18 6l-2.5 2.5M8.5 15.5 6 18"/></svg>',
  mic:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M8.5 21h7"/></svg>',
  wave:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M4 12h0M8 8v8M12 5v14M16 8v8M20 12h0"/></svg>',
  cast:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8V6a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6"/><path d="M3 16a4 4 0 0 1 4 4M3 12a8 8 0 0 1 8 8"/><circle cx="3.5" cy="19.5" r="1"/></svg>',
  graph:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><circle cx="5" cy="6" r="2.2"/><circle cx="5" cy="18" r="2.2"/><circle cx="19" cy="12" r="2.2"/><path d="M7 6.6 17 11M7 17.4 17 13"/></svg>',
  studio:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="2.4"/><circle cx="6" cy="18" r="2.4"/><circle cx="18" cy="12" r="2.4"/><path d="M8.2 7.2 15.8 11M8.2 16.8 15.8 13"/></svg>',
  clock:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/></svg>',
  hub:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="2.6"/><path d="M12 4.5v4.9M12 14.6v4.9M4.5 12h4.9M14.6 12h4.9"/><circle cx="12" cy="4" r="1.3"/><circle cx="12" cy="20" r="1.3"/><circle cx="4" cy="12" r="1.3"/><circle cx="20" cy="12" r="1.3"/></svg>'
};

/* ===========================================================================
   Shared suite navigation — the five console pages move as one.
   The same five links/labels/order are reused across observatory/studio/
   schedules/channels/voice; the host page passes its own active key.
   =========================================================================== */
const SUITE_NAV=[
  {k:"observatory",href:"/workflow/observatory",label:"观测台",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/></svg>'},
  {k:"studio",href:"/workflow/studio",label:"Studio",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="2.4"/><circle cx="6" cy="18" r="2.4"/><circle cx="18" cy="12" r="2.4"/><path d="M8.2 7.2 15.8 11M8.2 16.8 15.8 13"/></svg>'},
  {k:"schedules",href:"/schedules",label:"定时任务",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/></svg>'},
  {k:"channels",href:"/channels",label:"渠道",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="2.6"/><path d="M12 4.5v4.9M12 14.6v4.9M4.5 12h4.9M14.6 12h4.9"/></svg>'},
  {k:"voice",href:"/voice",label:"语音",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M8.5 21h7"/></svg>'}
];
function suiteNavHtml(active){return '<nav class="suite-nav" aria-label="控制台导航">'+SUITE_NAV.map(function(n){return '<a href="'+n.href+'"'+(n.k===active?' aria-current="page"':'')+' title="'+n.label+'">'+n.svg+'<span class="nav-label">'+n.label+'</span></a>';}).join('')+'</nav>';}

/* ===========================================================================
   Topbar
   =========================================================================== */
function scopeChipHtml(){
  if(state.scopeId){
    return '<div class="scope-chip" title="scope 已解析（来源：'+esc(state.scopeSource||"")+'）">'+ICON.check+'<span>scope</span><span class="sid">'+esc(state.scopeId)+'</span></div>';
  }
  return '<div class="scope-chip" title="正在解析 scope">'+ICON.spark+'<span>scope</span><span class="sid">解析中…</span></div>';
}
function renderTopbar(){
  const bar=el("header",{class:"topbar",role:"banner"});
  bar.innerHTML=
    '<div class="brand">'+
      '<div class="brand-mark" aria-hidden="true">'+ICON.mic+'</div>'+
      '<div><div class="brand-name">语音在场 <span class="brand-sub">Voice Presence</span></div></div>'+
    '</div>'+
    suiteNavHtml("voice")+
    (state.signedIn?scopeChipHtml():"")+
    '<div class="spacer"></div>';
  const tbtn=el("button",{class:"iconbtn",id:"themeBtn","aria-label":"切换主题",title:"切换主题"});
  tbtn.innerHTML=effDark()?ICON.sun:ICON.moon;
  tbtn.addEventListener("click",()=>{ state.theme=effDark()?"light":"dark"; localStorage.setItem("voice-theme",state.theme); applyTheme(); render(); });
  bar.appendChild(tbtn);
  if(state.signedIn){
    const acct=el("div",{class:"account"});
    acct.innerHTML='<span class="avatar" aria-hidden="true">'+esc(initials(accountLabel))+'</span><span class="who" title="'+esc(accountLabel)+'">'+esc(accountLabel)+'</span>';
    const sw=el("button",{class:"linkbtn","aria-label":"切换账户"},"切换账户");
    sw.addEventListener("click",()=>{ signOut(); });
    acct.appendChild(sw);
    bar.appendChild(acct);
  }
  return bar;
}

function renderLogin(){
  const wrap = el("main", { class:"login" });
  const card = el("section", { class:"login-card", role:"region", "aria-label":"登录" });
  card.innerHTML =
    '<div class="login-mark" aria-hidden="true">'+ICON.mic+'</div>'+
    '<h1>登录以进入语音在场</h1>'+
    '<p>管理 GAgent actor 的语音在场能力 —— 登录后即可启用。</p>';
  const btn = el("button", { class:"btn-primary", id:"signinBtn" }, "使用 nyxid 登录");
  btn.addEventListener("click", () => { beginLogin(); });
  card.appendChild(btn);
  card.appendChild(el("div", { class:"login-foot" }, ICON.lock.replace('width="24" height="24"','width="12" height="12"')+'<span>采用 OIDC bearer-token 鉴权 · 通过 nyxid 账户登录</span>'));
  wrap.appendChild(card);
  return wrap;
}

/* ---- Enable card (the one real voice write) ----------------------------- */
function renderEnableCard(){
  const card = el("section", { class:"card", "aria-label":"启用语音在场" });
  card.innerHTML =
    '<div class="card-head">'+
      '<div class="card-ic" aria-hidden="true">'+ICON.mic+'</div>'+
      '<div class="card-title">启用语音在场</div>'+
      '<span class="card-meta badge b-ok">实时可用</span>'+
    '</div>'+
    '<p class="card-desc">为某个 scope 下的 GAgent actor 启用语音在场模块。命令异步受理（202 accepted）；'+
      '语音会话状态在 capability read model 物化后可见。</p>'+
    '<div class="field"><label for="vfScope">Scope ID<span class="req">*</span></label>'+
      '<input id="vfScope" class="mono" autocomplete="off" spellcheck="false" placeholder="scope_…" value="'+esc(state.form.scopeId)+'" /></div>'+
    '<div class="field"><label for="vfActor">Actor ID<span class="req">*</span></label>'+
      '<input id="vfActor" class="mono" autocomplete="off" spellcheck="false" placeholder="GAgent actor id" value="'+esc(state.form.actorId)+'" /></div>'+
    '<div class="field"><label for="vfKind">Agent kind<span class="req">*</span></label>'+
      '<input id="vfKind" class="mono" autocomplete="off" spellcheck="false" placeholder="例如 role / workflow" value="'+esc(state.form.agentKind)+'" />'+
      '<div class="hint">未知 agentKind 会被后端拒绝（400 invalid_input）。</div></div>'+
    '<div class="field"><label for="vfModule">Module name<span class="req">*</span></label>'+
      '<input id="vfModule" class="mono" autocomplete="off" spellcheck="false" placeholder="语音在场模块名" value="'+esc(state.form.moduleName)+'" />'+
      '<div class="hint">由部署的语音 provider 决定；未配置 provider 时返回 503 voice_not_configured，未注册模块返回 404 unknown_module。</div></div>'+
    '<div class="form-actions"></div>'+
    '<div id="vResult"></div>';

  const actions = card.querySelector(".form-actions");
  const btn = el("button", { class:"btn-primary", id:"vEnableBtn", type:"button" });
  btn.innerHTML = state.submitting ? '<span class="spin" aria-hidden="true"></span><span>启用中…</span>' : '<span>启用</span>';
  btn.disabled = state.submitting;
  btn.addEventListener("click", submitEnable);
  actions.appendChild(btn);

  // keep field edits in state (so a re-render from polling/theme does not wipe input)
  ["vfScope","vfActor","vfKind","vfModule"].forEach((id,i)=>{
    const key = ["scopeId","actorId","agentKind","moduleName"][i];
    const inp = card.querySelector("#"+id);
    if(inp) inp.addEventListener("input", e => { state.form[key] = e.target.value; });
  });

  if(state.result) card.querySelector("#vResult").appendChild(renderResult(state.result));
  return card;
}

function renderResult(r){
  const box = el("div", { class:"result "+(r.ok?"ok":"err"), role:"status", "aria-live":"polite" });
  let head =
    '<div class="result-head">'+(r.ok?ICON.okc:ICON.alert)+
      '<div><div class="result-l">'+esc(r.label)+'</div>'+
      (r.message?'<div class="result-m">'+esc(r.message)+'</div>':'')+'</div>'+
    '</div>';
  let grid = "";
  if(r.detail && typeof r.detail === "object"){
    const rows = Object.keys(r.detail).filter(k=>r.detail[k]!=null && r.detail[k]!=="");
    if(rows.length){
      grid = '<div class="result-grid">'+rows.map(k =>
        '<div class="kv"><span class="k">'+esc(k)+'</span><span class="v">'+esc(r.detail[k])+'</span></div>'
      ).join("")+'</div>';
    }
  }
  box.innerHTML = head + grid;
  return box;
}

async function submitEnable(){
  if(state.submitting) return;
  const f = state.form;
  const scopeId = (f.scopeId||"").trim();
  const actorId = (f.actorId||"").trim();
  const agentKind = (f.agentKind||"").trim();
  const moduleName = (f.moduleName||"").trim();
  const missing = [];
  if(!scopeId) missing.push("Scope ID");
  if(!actorId) missing.push("Actor ID");
  if(!agentKind) missing.push("Agent kind");
  if(!moduleName) missing.push("Module name");
  if(missing.length){
    state.result = { ok:false, label:"缺少必填项", message:"请填写：" + missing.join("、") };
    render(); return;
  }
  const token = getToken();
  if(!token){ signOutSilent(); return; }

  state.submitting = true; state.result = null; render();
  const url = "/api/scopes/" + encodeURIComponent(scopeId) + "/gagent-actors/" + encodeURIComponent(actorId)
    + "/voice-presence/enable?agentKind=" + encodeURIComponent(agentKind);
  try {
    const res = await fetch(url, {
      method:"POST",
      headers:{ Authorization:"Bearer " + token.access_token, "Content-Type":"application/json" },
      body: JSON.stringify({ moduleName: moduleName })
    });
    if(res.status === 401){ signOutSilent(); return; }
    let body = null;
    try { body = await res.json(); } catch(e){ /* tolerate empty/non-json */ }

    if(res.status === 202 && body){
      state.result = {
        ok:true,
        label:"已受理启用命令",
        message: body.note || "命令已受理待分发；read model 观测到提交后可重新连接。",
        detail: {
          module_name: body.module_name,
          stage: body.stage,
          agent_kind: body.agent_kind,
          actor_id: body.actor_id,
          command_id: body.command_id,
          correlation_id: body.correlation_id
        }
      };
    } else {
      state.result = mapEnableError(res.status, body);
    }
  } catch(e){
    state.result = { ok:false, label:"请求失败", message: String(e && e.message || e) };
  } finally {
    state.submitting = false; render();
  }
}

function mapEnableError(status, body){
  const code = body && body.error ? body.error : ("http_" + status);
  const detail = body && body.detail ? body.detail : "";
  if(code === "voice_not_configured"){
    return { ok:false, label:"未配置语音 provider", message:"当前部署尚未注册任何语音在场模块（503 voice_not_configured）。需先在 host 配置语音 provider 后才能启用。", detail:{ error:code, detail } };
  }
  if(code === "unknown_module"){
    return { ok:false, label:"模块未注册", message:(detail || "指定的语音模块未注册。") + "（404 unknown_module）", detail:{ error:code } };
  }
  if(code === "admission_denied"){
    return { ok:false, label:"scope 准入被拒", message:(detail || "scope 准入拒绝了启用访问。") + "（403）", detail:{ error:code } };
  }
  if(code === "actor_not_found"){
    return { ok:false, label:"Actor 不存在", message:(detail || "该 actor 未在请求的 scope 中注册。") + "（404）", detail:{ error:code } };
  }
  if(code === "admission_unavailable" || code === "command_dispatch_failed"){
    return { ok:false, label:"暂不可用", message:(detail || "服务暂时不可用，请稍后重试。") + "（503 " + code + "）", detail:{ error:code } };
  }
  return { ok:false, label:"启用失败（" + status + "）", message: detail || "后端返回错误。", detail:{ error:code } };
}

/* ---- Honest stubs / info (no read GETs exist yet) ----------------------- */
function renderTransportCard(){
  const card = el("section", { class:"card", "aria-label":"实时语音传输" });
  card.innerHTML =
    '<div class="card-head">'+
      '<div class="card-ic" aria-hidden="true">'+ICON.wave+'</div>'+
      '<div class="card-title">实时语音传输</div>'+
      '<span class="card-meta badge b-pending">运行态由会话持有</span>'+
    '</div>'+
    '<p class="card-desc">实时会话经 <code class="inline">/ws/voice</code> WebSocket 建立。这里只描述传输契约——'+
      '语音运行态属于会话本身，没有只读查询端点，因此不在此罗列“当前会话”。</p>'+
    '<div class="contract">'+
      '<div class="row"><span class="lbl">下行 (server→client)</span><span class="val">VoiceControlFrame：sessionAccepted / realtimeFrame（PCM16）</span></div>'+
      '<div class="row"><span class="lbl">上行 (client→server)</span><span class="val">VoiceControlFrame：drainAcknowledged / inputImage</span></div>'+
      '<div class="row"><span class="lbl">音频编码</span><span class="val">PCM16</span></div>'+
      '<div class="row"><span class="lbl">鉴权</span><span class="val">Bearer + ChatRoutePolicy 路由解析</span></div>'+
    '</div>'+
    '<div class="note">'+ICON.alert+'<div class="nt">未配置语音 provider 时，<b>/ws/voice</b> 以 <code class="inline">503 voice_not_configured</code> fail-closed。</div></div>';
  return card;
}

function renderStubCard(){
  const card = el("section", { class:"card stub", "aria-label":"浏览器直推与显示侧信道" });
  card.innerHTML =
    '<div class="card-head">'+
      '<div class="card-ic muted" aria-hidden="true">'+ICON.cast+'</div>'+
      '<div class="card-title">浏览器直推 (WHIP) · 显示侧信道 (vp-control)</div>'+
      '<span class="card-meta badge b-warn">待后端 ②③</span>'+
    '</div>'+
    '<p class="card-desc">WHIP/WebRTC 入站（浏览器直推音视频）与显示侧信道 <code class="inline">vp-control</code> 属于'+
      '“通用语音服务”分解中的子项目 ②③。后端就绪并暴露读取/控制端点后，本卡片接入；当前不提供任何可点操作，'+
      '也不展示虚构数据。</p>';
  return card;
}

/* ===========================================================================
   Render
   =========================================================================== */
function render(){
  const app=$("#app"); app.innerHTML="";
  app.appendChild(renderTopbar());
  if(!state.signedIn){ app.appendChild(renderLogin()); return; }
  const page = el("main", { class:"page scroll" });
  page.appendChild(el("div", { class:"page-head" },
    '<h1 class="page-title">语音在场</h1>'+
    '<p class="page-sub">套件成员之一 · 与运行观测台 / Studio / 定时任务 / 渠道接入共享登录与导航。'+
      '本页只接入真实后端端点，未就绪的能力以诚实占位呈现。</p>'));
  page.appendChild(renderEnableCard());
  page.appendChild(renderTransportCard());
  page.appendChild(renderStubCard());
  app.appendChild(page);
}

/* ---- Live: scope chip + prefill the enable form's scope ----------------- */
async function loadContext(){
  try {
    const ctx = await apiGet("/api/studio/context");
    if(ctx){
      state.scopeId = ctx.scopeId || null;
      state.scopeResolved = !!ctx.scopeResolved;
      state.scopeSource = ctx.scopeSource || null;
      if(state.scopeId && !state.form.scopeId) state.form.scopeId = state.scopeId;
      render();
    }
  } catch(e){ console.warn("voice: context load failed", e); }
}

/* init */
applyTheme();
(async function init(){
  try { await completeLoginIfCallback(); }
  catch(e){ console.warn("voice: login callback failed", e); }
  if(!getToken()){ state.signedIn = false; render(); return; }
  state.signedIn = true;
  render();
  loadContext();
  const acct = toAccount(await fetchUserInfo());
  if(acct){ accountLabel = acct.label; render(); }
})();
if(window.matchMedia){ window.matchMedia("(prefers-color-scheme: light)").addEventListener?.("change",()=>{ if(!state.theme)render(); }); }
</script>
</body>
</html>

""";
}
