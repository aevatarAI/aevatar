namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

// Workflow Studio: one inline self-contained HTML page (no wwwroot, no build step), mirroring the
// /workflow/observatory precedent. Browser OIDC Authorization Code + PKCE against nyxid (reuse of the
// console-web client; same authority/clientId/scope as observatory, isolated PKCE storage + callback
// route). The page is the anonymous static shell; the app is gated behind login exactly like observatory.
// This increment is mount + login only: the chat/cards still render from in-page mock data until the live
// data wiring (/api/chat, /api/studio/context, /api/schedules) lands in a later increment.
internal static class WorkflowStudioPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
<title>工作室 · Workflow Studio · aevatar</title>
<style>
  /* =========================================================================
     aevatar 工作室 / Workflow Studio — 对话式编排页（高保真视觉交互稿）
     设计令牌与组件观感与「运行观测台 observatory」完全一致（变量名一字不差）。
     framework-agnostic：语义化 HTML + CSS 变量 + 少量 vanilla JS（仅用于状态切换/演示）。
     ========================================================================= */

  /* ---- Design tokens : 暗色（默认，与 observatory 一致）------------------- */
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
    --list-w:300px; --topbar-h:56px; --ctx-w:460px; color-scheme:dark;

    /* 节点类别配色（工作流图）—— 新增 token，统一管理，不散落硬编码 */
    --cat-data:#48b3c7;        --cat-control:#d4a03a;
    --cat-ai:#5b8cff;          --cat-composition:#9b7bff;
    --cat-integration:#45b88f; --cat-human:#e0739b;
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
      --cat-data:#1c7d9b; --cat-control:#9a6a12; --cat-ai:#2f63e0; --cat-composition:#6d4ad6; --cat-integration:#1c8a63; --cat-human:#b03a66;
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
    --cat-data:#1c7d9b; --cat-control:#9a6a12; --cat-ai:#2f63e0; --cat-composition:#6d4ad6; --cat-integration:#1c8a63; --cat-human:#b03a66;
    color-scheme:light;
  }

  /* ---- base ------------------------------------------------------------- */
  * { box-sizing:border-box; }
  html,body { height:100%; }
  body {
    margin:0; background:var(--bg); background-image:var(--bg-grad); background-attachment:fixed;
    color:var(--fg); font:14px/1.55 var(--sans); -webkit-font-smoothing:antialiased; text-rendering:optimizeLegibility;
    overflow:hidden;
  }
  ::selection { background:var(--accent-soft); }
  button { font:inherit; color:inherit; cursor:pointer; }
  :focus-visible { outline:none; box-shadow:var(--ring); border-radius:var(--r-sm); }
  .mono { font-family:var(--mono); font-variant-numeric:tabular-nums; }
  .scroll::-webkit-scrollbar { width:10px; height:10px; }
  .scroll::-webkit-scrollbar-thumb { background:var(--panel-3); border-radius:999px; border:2px solid transparent; background-clip:content-box; }
  .scroll::-webkit-scrollbar-track { background:transparent; }

  /* ---- status primitives (observatory) ---------------------------------- */
  .dot { width:9px; height:9px; border-radius:50%; flex:0 0 auto; }
  .dot.s-running   { background:var(--run); box-shadow:0 0 0 0 var(--run); animation:pulse 2.4s ease-out infinite; }
  .dot.s-completed, .dot.s-bound, .dot.s-registered, .dot.s-done { background:var(--ok); }
  .dot.s-failed { background:var(--err); }
  .dot.s-stopped, .dot.s-pending { background:var(--warn); }
  .dot.s-unknown { background:var(--neutral); }
  @keyframes pulse { 0%{box-shadow:0 0 0 0 var(--run-soft);} 70%{box-shadow:0 0 0 7px transparent;} 100%{box-shadow:0 0 0 0 transparent;} }

  .badge { display:inline-flex; align-items:center; gap:6px; padding:3px 9px; border-radius:var(--r-pill); font-size:12px; font-weight:600; border:1px solid transparent; white-space:nowrap; }
  .badge .dot { width:7px; height:7px; }
  .b-running   { color:var(--run);  background:var(--run-soft);  border-color:color-mix(in oklab,var(--run) 30%,transparent); }
  .b-completed, .b-bound, .b-registered { color:var(--ok); background:var(--ok-soft); border-color:color-mix(in oklab,var(--ok) 30%,transparent); }
  .b-failed { color:var(--err); background:var(--err-soft); border-color:color-mix(in oklab,var(--err) 30%,transparent); }
  .b-stopped, .b-pending { color:var(--warn); background:var(--warn-soft); border-color:color-mix(in oklab,var(--warn) 30%,transparent); }
  .b-unknown { color:var(--muted); background:var(--neutral-soft); border-color:var(--border); }

  /* =========================================================================
     Top bar
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
  .scope-chip { display:inline-flex; align-items:center; gap:7px; padding:4px 10px; border-radius:var(--r-pill); background:var(--panel-2); border:1px solid var(--border); font-size:12px; color:var(--muted); }
  .scope-chip .sid { font-family:var(--mono); font-size:11.5px; color:var(--fg); }
  .scope-chip svg { color:var(--ok); }
  .spacer { flex:1 1 auto; }
  .obs-link { display:inline-flex; align-items:center; gap:7px; padding:6px 12px; border-radius:var(--r-pill);
    background:var(--accent-soft); border:1px solid var(--accent-line); color:var(--accent); font-size:12.5px; font-weight:600; text-decoration:none; }
  .obs-link:hover { background:color-mix(in oklab,var(--accent) 18%,transparent); }
  .iconbtn { width:34px; height:34px; display:grid; place-items:center; background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r); color:var(--muted); transition:color .15s,border-color .15s; }
  .iconbtn:hover { color:var(--fg); border-color:var(--muted-2); }
  .account { display:inline-flex; align-items:center; gap:8px; padding:4px 6px 4px 4px; border-radius:var(--r-pill); background:var(--panel-2); border:1px solid var(--border); }
  .avatar { width:24px; height:24px; border-radius:50%; flex:0 0 auto; display:grid; place-items:center; font-size:12px; font-weight:700; color:#fff; background:linear-gradient(140deg,#6f9bff,#9b6bff); }
  .account .who { font-size:12.5px; color:var(--fg); max-width:140px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }

  /* =========================================================================
     Three-column shell
     ========================================================================= */
  .shell { display:grid; grid-template-columns:var(--list-w) minmax(0,1fr) var(--ctx-w); height:calc(100dvh - var(--topbar-h)); min-height:0; }
  body.ctx-collapsed .shell { grid-template-columns:var(--list-w) minmax(0,1fr) 0; }

  /* ---- Left : session list --------------------------------------------- */
  .sess-pane { border-right:1px solid var(--border); display:flex; flex-direction:column; min-height:0; background:color-mix(in oklab,var(--panel) 40%,transparent); }
  .sess-head { padding:12px; border-bottom:1px solid var(--border-soft); }
  .new-sess { width:100%; display:flex; align-items:center; justify-content:center; gap:8px; padding:9px 12px; border-radius:var(--r); border:1px dashed var(--muted-2); background:transparent; color:var(--fg); font-weight:600; font-size:13px; transition:border-color .15s,background .15s; }
  .new-sess:hover { border-color:var(--accent); background:var(--accent-soft); color:var(--accent); }
  .sess-list { overflow-y:auto; flex:1 1 auto; padding:8px; min-height:0; }
  .sess-row { position:relative; display:grid; grid-template-columns:auto 1fr; gap:3px 11px; width:100%; text-align:left; padding:11px 12px; border-radius:var(--r); border:1px solid transparent; background:transparent; color:inherit; margin-bottom:2px; transition:background .12s,border-color .12s; }
  .sess-row:hover { background:var(--panel-2); }
  .sess-row .dot { margin-top:6px; }
  .sess-row .st { font-weight:640; color:var(--fg-strong); letter-spacing:-.005em; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; padding-right:18px; }
  .sess-row .sm { font-size:12px; color:var(--muted); }
  .sess-badges { grid-column:2; display:flex; gap:5px; flex-wrap:wrap; margin-top:5px; }
  .pbadge { font-family:var(--mono); font-size:10px; font-weight:600; letter-spacing:.02em; padding:1px 6px; border-radius:5px; background:var(--panel-3); color:var(--muted); border:1px solid var(--border-soft); display:inline-flex; align-items:center; gap:3px; }
  .pbadge.wf  { color:var(--cat-ai); border-color:color-mix(in oklab,var(--cat-ai) 30%,transparent); background:color-mix(in oklab,var(--cat-ai) 12%,transparent); }
  .pbadge.team{ color:var(--cat-composition); border-color:color-mix(in oklab,var(--cat-composition) 30%,transparent); background:color-mix(in oklab,var(--cat-composition) 12%,transparent); }
  .pbadge.svc { color:var(--cat-integration); border-color:color-mix(in oklab,var(--cat-integration) 30%,transparent); background:color-mix(in oklab,var(--cat-integration) 12%,transparent); }
  .pbadge.cron{ color:var(--warn); border-color:color-mix(in oklab,var(--warn) 30%,transparent); background:var(--warn-soft); }
  .sess-row[aria-current="true"] { background:var(--accent-soft); border-color:var(--accent-line); box-shadow:inset 3px 0 0 var(--accent); }
  .sess-actions { position:absolute; right:8px; top:9px; display:none; gap:2px; }
  .sess-row:hover .sess-actions, .sess-row[aria-current="true"] .sess-actions { display:flex; }
  .sess-actions button { width:24px; height:24px; display:grid; place-items:center; background:var(--panel); border:1px solid var(--border); border-radius:6px; color:var(--muted); }
  .sess-actions button:hover { color:var(--fg); border-color:var(--muted-2); }
  .sess-actions button.del:hover { color:var(--err); border-color:color-mix(in oklab,var(--err) 40%,transparent); }

  .empty { display:flex; flex-direction:column; align-items:center; justify-content:center; gap:10px; text-align:center; color:var(--muted); padding:44px 22px; height:100%; }
  .empty .ic { width:40px; height:40px; color:var(--muted-2); opacity:.8; }
  .empty .et { font-weight:600; color:var(--fg); font-size:14px; }
  .empty .es { font-size:12.5px; max-width:220px; }

  /* ---- Center : chat ---------------------------------------------------- */
  .chat-pane { display:flex; flex-direction:column; min-height:0; min-width:0; position:relative; }
  .chat-scroll { flex:1 1 auto; overflow-y:auto; min-height:0; padding:28px 0 12px; }
  .chat-inner { max-width:768px; margin:0 auto; padding:0 32px; }

  .welcome { max-width:560px; margin:6vh auto 0; text-align:center; padding:0 24px; }
  .welcome .wmark { width:54px; height:54px; border-radius:15px; margin:0 auto 18px; display:grid; place-items:center; color:#fff; box-shadow:var(--shadow);
    background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); }
  .welcome h1 { font-size:20px; font-weight:700; letter-spacing:-.01em; color:var(--fg-strong); margin:0 0 8px; }
  .welcome p { color:var(--muted); font-size:14px; line-height:1.7; margin:0 auto 24px; max-width:42ch; }
  .prompt-chips { display:flex; flex-direction:column; gap:8px; max-width:440px; margin:0 auto; }
  .prompt-chip { text-align:left; padding:13px 16px; border-radius:var(--r); border:1px solid var(--border); background:var(--panel); color:var(--fg); font-size:13.5px; line-height:1.5; display:flex; gap:11px; align-items:center; transition:border-color .15s,background .15s; }
  .prompt-chip:hover { border-color:var(--accent-line); background:var(--accent-soft); }
  .prompt-chip svg { color:var(--muted-2); flex:0 0 auto; }

  .msg { display:flex; gap:14px; margin-bottom:28px; }
  .msg .m-avatar { width:30px; height:30px; border-radius:9px; flex:0 0 auto; display:grid; place-items:center; font-size:12px; font-weight:700; margin-top:2px; }
  .msg.user .m-avatar { background:var(--panel-3); color:var(--muted); }
  .msg.agent .m-avatar { color:#fff; background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); }
  .m-body { min-width:0; flex:1 1 auto; }
  .m-name { font-size:12.5px; color:var(--muted-2); margin-bottom:7px; display:flex; align-items:center; gap:8px; }
  .m-name b { color:var(--fg); font-weight:600; }
  .msg.user .m-body { display:flex; flex-direction:column; align-items:flex-end; }
  .msg.user .m-name { align-self:flex-end; }
  .bubble-user { background:var(--accent-soft); border:1px solid var(--accent-line); color:var(--fg); padding:12px 16px; border-radius:14px 5px 14px 14px; font-size:14.5px; line-height:1.68; max-width:92%; }
  .bubble-agent { color:var(--fg); font-size:14.5px; line-height:1.78; }
  .bubble-agent p { margin:0 0 13px; }
  .bubble-agent p:last-child { margin-bottom:0; }
  .bubble-agent code { font-family:var(--mono); font-size:12.5px; background:var(--panel-2); border:1px solid var(--border-soft); border-radius:5px; padding:1px 5px; }
  .stream-cursor { display:inline-block; width:7px; height:15px; vertical-align:-2px; margin-left:2px; background:var(--accent); animation:blink 1s steps(2) infinite; border-radius:1px; }
  @keyframes blink { 50% { opacity:0; } }

  /* tool-call block (observatory) */
  .toolcall { margin:13px 0; border:1px solid var(--border); border-radius:var(--r); background:var(--panel); overflow:hidden; }
  .tc-head { display:flex; align-items:center; gap:10px; width:100%; text-align:left; padding:9px 12px; background:var(--panel); border:0; color:inherit; }
  .tc-head:hover { background:var(--panel-2); }
  .tc-chevron { color:var(--muted); transition:transform .18s ease; flex:0 0 auto; }
  .toolcall[data-open="true"] .tc-chevron { transform:rotate(90deg); }
  .tc-name { font-family:var(--mono); font-size:13px; color:var(--fg-strong); font-weight:600; }
  .tc-callid { font-family:var(--mono); font-size:11px; color:var(--muted-2); }
  .tc-status { margin-left:auto; display:inline-flex; align-items:center; gap:5px; font-size:12px; font-weight:600; }
  .tc-status.ok { color:var(--ok); } .tc-status.bad { color:var(--err); } .tc-status.run { color:var(--run); }
  .tc-status.run .spin { width:11px; height:11px; border-radius:50%; border:1.6px solid color-mix(in oklab,var(--run) 35%,transparent); border-top-color:var(--run); animation:spin .7s linear infinite; }
  @keyframes spin { to { transform:rotate(360deg); } }
  .tc-body { display:grid; gap:12px; padding:0 12px 12px; }
  .toolcall[data-open="false"] .tc-body { display:none; }
  .tc-field-head { display:flex; align-items:center; justify-content:space-between; margin-bottom:5px; }
  .tc-field-label { font-size:11px; letter-spacing:.05em; text-transform:uppercase; color:var(--muted-2); font-weight:600; }
  .copybtn { display:inline-flex; align-items:center; gap:5px; font-size:11px; color:var(--muted); background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r-sm); padding:2px 8px; }
  .copybtn:hover { color:var(--fg); border-color:var(--muted-2); }
  .copybtn.done { color:var(--ok); border-color:color-mix(in oklab,var(--ok) 40%,transparent); }
  pre.json { margin:0; padding:11px 12px; max-height:200px; overflow:auto; background:var(--bg); border:1px solid var(--border-soft); border-radius:var(--r-sm); font-family:var(--mono); font-size:12px; line-height:1.55; color:var(--fg); white-space:pre; tab-size:2; }
  .tc-error { color:var(--err); font-family:var(--mono); font-size:12px; }
  .j-key{color:var(--accent);} .j-str{color:var(--ok);} .j-num{color:var(--warn);} .j-bool{color:var(--run);} .j-null{color:var(--muted);}

  /* run-error / chat error */
  .chat-error { display:flex; gap:11px; align-items:flex-start; padding:12px 14px; border-radius:var(--r); background:var(--err-soft); border:1px solid color-mix(in oklab,var(--err) 38%,transparent); margin-bottom:22px; }
  .chat-error svg { color:var(--err); flex:0 0 auto; margin-top:1px; }
  .chat-error .ce-l { font-weight:650; color:var(--err); font-size:13px; }
  .chat-error .ce-m { color:var(--muted); font-size:12.5px; margin-top:2px; }
  .chat-error .ce-retry { margin-top:8px; display:inline-flex; align-items:center; gap:6px; font-size:12.5px; font-weight:600; color:var(--accent); background:transparent; border:1px solid var(--accent-line); border-radius:var(--r-sm); padding:4px 10px; }
  .chat-error .ce-retry:hover { background:var(--accent-soft); }

  /* usage line */
  .usage-line { margin:15px 0 2px; padding-top:11px; border-top:1px dashed var(--border-soft); font-family:var(--mono); font-size:11px; color:var(--muted-2); display:flex; gap:16px; flex-wrap:wrap; }
  .usage-line b { color:var(--muted); font-weight:600; }

  /* composer */
  .composer-wrap { border-top:1px solid var(--border); background:color-mix(in oklab,var(--panel) 70%,transparent); padding:16px 0 18px; }
  .composer { max-width:768px; margin:0 auto; padding:0 32px; }
  .composer-box { display:flex; align-items:flex-end; gap:10px; background:var(--panel); border:1px solid var(--border); border-radius:var(--r-lg); padding:10px 10px 10px 16px; transition:border-color .15s,box-shadow .15s; }
  .composer-box:focus-within { border-color:var(--accent-line); box-shadow:0 0 0 3px var(--accent-soft); }
  .composer textarea { flex:1 1 auto; resize:none; border:0; background:transparent; color:var(--fg); font:14.5px/1.6 var(--sans); padding:7px 0; max-height:140px; min-height:26px; outline:none; }
  .composer textarea::placeholder { color:var(--muted-2); }
  .send-btn { width:34px; height:34px; flex:0 0 auto; display:grid; place-items:center; border:0; border-radius:9px; background:var(--accent); color:var(--accent-ink); transition:filter .15s,opacity .15s; }
  .send-btn:hover { filter:brightness(1.08); }
  .send-btn:disabled { opacity:.4; cursor:default; }
  .stop-btn { display:inline-flex; align-items:center; gap:7px; flex:0 0 auto; height:34px; padding:0 12px; border-radius:9px; background:var(--panel-2); border:1px solid var(--border); color:var(--fg); font-size:12.5px; font-weight:600; }
  .stop-btn:hover { border-color:var(--err); color:var(--err); }
  .composer-hint { max-width:768px; margin:10px auto 0; padding:0 32px; font-size:11px; color:var(--muted-2); display:flex; align-items:center; gap:6px; }
  .kbd { font-family:var(--mono); font-size:10.5px; padding:1px 5px; border-radius:4px; background:var(--panel-2); border:1px solid var(--border); color:var(--muted); }

  /* =========================================================================
     Right : context (graph + artifact cards)
     ========================================================================= */
  .ctx-pane { position:relative; border-left:1px solid var(--border); display:flex; flex-direction:column; min-height:0; min-width:0; background:color-mix(in oklab,var(--panel) 40%,transparent); overflow:hidden; }
  body.ctx-collapsed .ctx-pane { display:none; }

  /* draggable width handle on the panel's left edge */
  .ctx-resizer { position:absolute; left:0; top:0; bottom:0; width:9px; transform:translateX(-4px); cursor:col-resize; z-index:25; touch-action:none; }
  .ctx-resizer::after { content:""; position:absolute; left:4px; top:0; bottom:0; width:2px; background:transparent; transition:background .15s; }
  .ctx-resizer:hover::after, .ctx-resizer.dragging::after { background:var(--accent); }
  body.resizing { cursor:col-resize; user-select:none; }
  body.resizing .graph-viewport { cursor:col-resize; }

  /* underline multi-tabs (图 / Workflow / Team / Service / Schedules) */
  .ctx-head { display:flex; align-items:stretch; padding:0 6px; border-bottom:1px solid var(--border-soft); }
  .ctx-tabs { display:flex; gap:1px; overflow-x:auto; flex:1 1 auto; scrollbar-width:none; }
  .ctx-tabs::-webkit-scrollbar { display:none; }
  .ctx-tab { position:relative; display:inline-flex; align-items:center; gap:7px; padding:12px 13px 10px; border:0; background:transparent; color:var(--muted); font-size:12.5px; font-weight:600; white-space:nowrap; border-bottom:2px solid transparent; transition:color .15s; }
  .ctx-tab svg { width:14px; height:14px; opacity:.85; }
  .ctx-tab:hover { color:var(--fg); }
  .ctx-tab[aria-selected="true"] { color:var(--fg-strong); border-bottom-color:var(--accent); }
  .ctx-tab .tdot { width:6px; height:6px; }

  .ctx-body { position:relative; flex:1 1 auto; min-height:0; overflow:hidden; }
  .ctx-panel { position:absolute; inset:0; overflow-y:auto; padding:16px; }
  .panel-head { display:flex; align-items:center; gap:11px; margin-bottom:15px; }
  .panel-head .ph-ic { width:32px; height:32px; border-radius:9px; display:grid; place-items:center; flex:0 0 auto; }
  .panel-head .ph-title { font-weight:680; color:var(--fg-strong); font-size:15px; letter-spacing:-.01em; }
  .panel-head .ph-meta { margin-left:auto; }
  .panel-card { border:1px solid var(--border); border-radius:var(--r-lg); background:var(--panel); padding:4px 14px; }
  .panel-sub { font-size:11px; font-weight:700; letter-spacing:.05em; text-transform:uppercase; color:var(--muted-2); margin:16px 2px 8px; }
  .graph-viewport { position:absolute; inset:0; overflow:hidden; cursor:grab;
    background:
      linear-gradient(0deg,transparent 23px,var(--border-soft) 23px,var(--border-soft) 24px,transparent 24px),
      linear-gradient(90deg,transparent 23px,var(--border-soft) 23px,var(--border-soft) 24px,transparent 24px);
    background-size:24px 24px; }
  .graph-viewport:active { cursor:grabbing; }
  .graph-layer { position:absolute; left:0; top:0; transform-origin:0 0; }
  .graph-layer svg { position:absolute; inset:0; overflow:visible; pointer-events:none; }

  .gnode { position:absolute; width:244px; height:96px; box-sizing:border-box; background:var(--panel-2); border:1.5px solid var(--border); border-radius:var(--r); padding:11px 13px; box-shadow:var(--shadow-sm); }
  .gnode .gn-top { display:flex; align-items:center; gap:10px; }
  .gnode .gn-ic { width:30px; height:30px; border-radius:8px; display:grid; place-items:center; flex:0 0 auto; color:var(--cat); background:color-mix(in oklab,var(--cat) 16%,transparent); border:1px solid color-mix(in oklab,var(--cat) 35%,transparent); }
  .gnode .gn-label { font-size:14px; font-weight:640; color:var(--fg-strong); letter-spacing:-.005em; }
  .gnode .gn-id { font-family:var(--mono); font-size:11px; color:var(--muted-2); margin-top:1px; }
  .gnode .gn-foot { position:absolute; left:13px; right:13px; bottom:10px; display:flex; align-items:center; justify-content:space-between; }
  .gnode .gn-type { font-family:var(--mono); font-size:10.5px; color:var(--muted); padding:1px 7px; border-radius:5px; background:var(--panel-3); border:1px solid var(--border-soft); }
  .gn-state { font-size:11px; font-weight:600; display:inline-flex; align-items:center; gap:5px; }
  .gnode.st-ready { }
  .gnode.st-done    { border-color:color-mix(in oklab,var(--ok) 50%,var(--border)); }
  .gnode.st-done .gn-state { color:var(--ok); }
  .gnode.st-current { border-color:var(--accent); box-shadow:0 0 0 3px var(--accent-soft); }
  .gnode.st-current .gn-state { color:var(--accent); }
  .gnode.st-failed  { border-color:color-mix(in oklab,var(--err) 55%,var(--border)); }
  .gnode.st-failed .gn-state { color:var(--err); }
  .gnode.st-stopped { border-color:color-mix(in oklab,var(--warn) 55%,var(--border)); }
  .gnode.st-stopped .gn-state { color:var(--warn); }
  .gnode.st-pending { opacity:.7; border-style:dashed; }
  .gnode.st-pending .gn-state { color:var(--muted-2); }
  .edge-label { position:absolute; transform:translate(-50%,-50%); font-family:var(--mono); font-size:10px; color:var(--muted); background:var(--panel); border:1px solid var(--border); border-radius:var(--r-pill); padding:1px 7px; white-space:nowrap; }

  .graph-ctrls { position:absolute; right:10px; bottom:10px; display:flex; gap:4px; background:var(--panel); border:1px solid var(--border); border-radius:var(--r-pill); padding:3px; box-shadow:var(--shadow-sm); z-index:3; }
  .graph-ctrls button { width:28px; height:28px; display:grid; place-items:center; background:transparent; border:0; border-radius:50%; color:var(--muted); font-size:13px; }
  .graph-ctrls button:hover { color:var(--fg); background:var(--panel-2); }
  .graph-ctrls .zlabel { min-width:42px; display:grid; place-items:center; font-family:var(--mono); font-size:11px; color:var(--muted); }
  .graph-legend { position:absolute; left:10px; top:10px; display:flex; flex-wrap:wrap; gap:4px 9px; max-width:60%; z-index:2; }
  .graph-legend .lg { display:inline-flex; align-items:center; gap:5px; font-size:10.5px; color:var(--muted); background:color-mix(in oklab,var(--panel) 80%,transparent); padding:1px 6px; border-radius:var(--r-pill); }
  .graph-legend .sw { width:9px; height:9px; border-radius:3px; background:color-mix(in oklab,var(--cat) 22%,transparent); border:1px solid var(--cat); }

  /* artifact panels render flat inside .ctx-panel (one per tab) */

  .kv { display:flex; justify-content:space-between; gap:12px; padding:5px 0; font-size:12.5px; border-bottom:1px solid var(--border-soft); }
  .kv:last-child { border-bottom:0; }
  .kv .k { color:var(--muted); flex:0 0 auto; }
  .kv .v { color:var(--fg); font-family:var(--mono); font-size:12px; text-align:right; word-break:break-all; }
  .kv .v.plain { font-family:var(--sans); }

  .deeplink { display:inline-flex; align-items:center; gap:6px; margin-top:10px; font-size:12px; font-weight:600; color:var(--accent); text-decoration:none; padding:6px 10px; border:1px solid var(--accent-line); border-radius:var(--r-sm); background:var(--accent-soft); }
  .deeplink:hover { background:color-mix(in oklab,var(--accent) 18%,transparent); }
  .deeplink svg { flex:0 0 auto; }

  .member { display:flex; align-items:center; gap:9px; padding:8px 0; border-bottom:1px solid var(--border-soft); }
  .member:last-child { border-bottom:0; }
  .member .mi { width:26px; height:26px; border-radius:7px; display:grid; place-items:center; background:var(--panel-3); color:var(--muted); flex:0 0 auto; }
  .member .mname { font-weight:600; color:var(--fg-strong); font-size:13px; }
  .member .mkind { font-family:var(--mono); font-size:10.5px; color:var(--muted-2); }
  .member .entry { font-size:10px; font-weight:700; letter-spacing:.04em; color:var(--accent); background:var(--accent-soft); border:1px solid var(--accent-line); border-radius:5px; padding:1px 5px; }

  .sched { padding:11px; border:1px solid var(--border-soft); border-radius:var(--r); background:var(--panel-2); margin-bottom:9px; }
  .sched:last-child { margin-bottom:0; }
  .sched-top { display:flex; align-items:center; gap:8px; margin-bottom:8px; }
  .sched-top .sname { font-weight:640; color:var(--fg-strong); font-size:13px; }
  .sched-cron { font-family:var(--mono); font-size:11px; color:var(--muted); background:var(--panel-3); padding:1px 6px; border-radius:5px; }
  .sched-grid { display:grid; grid-template-columns:1fr 1fr; gap:4px 12px; }
  .sched-grid .sg { display:flex; justify-content:space-between; gap:6px; font-size:11.5px; padding:2px 0; }
  .sched-grid .sg .gk { color:var(--muted-2); } .sched-grid .sg .gv { font-family:var(--mono); color:var(--fg); }
  .sched-grid .sg .gv.bad { color:var(--err); }
  .sched-err { margin-top:8px; font-family:var(--mono); font-size:11px; color:var(--err); background:var(--err-soft); border:1px solid color-mix(in oklab,var(--err) 30%,transparent); border-radius:var(--r-sm); padding:6px 8px; word-break:break-word; }
  .poll-tag { font-size:10.5px; color:var(--muted-2); display:inline-flex; align-items:center; gap:5px; }
  .poll-tag .dot { width:6px; height:6px; background:var(--run); animation:pulse 2.4s ease-out infinite; }

  .ctx-empty { flex:1 1 auto; display:grid; place-items:center; }

  /* skeleton */
  .sk { background:linear-gradient(100deg,var(--panel-2) 30%,var(--panel-3) 50%,var(--panel-2) 70%); background-size:200% 100%; animation:shimmer 1.3s linear infinite; border-radius:5px; }
  @keyframes shimmer { to { background-position:-200% 0; } }

  /* drawer / collapse toggle */
  .ctx-toggle { position:fixed; right:14px; bottom:64px; z-index:55; width:40px; height:40px; display:none; place-items:center; border-radius:50%; background:var(--panel); border:1px solid var(--border); color:var(--muted); box-shadow:var(--shadow); }
  @media (max-width:1180px) {
    :root { --ctx-w:400px; }
  }

  /* =========================================================================
     Responsive — < 760 : master/detail like observatory, chat first
     ========================================================================= */
  @media (max-width:760px) {
    .shell { grid-template-columns:1fr; }
    .sess-pane, .ctx-pane { position:fixed; top:var(--topbar-h); bottom:0; width:min(86vw,340px); z-index:50; box-shadow:var(--shadow); transition:transform .22s ease; }
    .sess-pane { left:0; transform:translateX(-102%); border-right:1px solid var(--border); }
    .ctx-pane { right:0; width:min(94vw,460px); transform:translateX(102%); border-left:1px solid var(--border); display:flex; }
    body.nav-open .sess-pane { transform:none; }
    body.ctx-open .ctx-pane { transform:none; }
    body.ctx-collapsed .ctx-pane { display:flex; }
    .ctx-toggle { display:grid; }
    .brand-sub, .scope-chip { display:none; }
    .scrim { position:fixed; inset:var(--topbar-h) 0 0; background:rgba(0,0,0,.45); z-index:45; }
    .ctx-resizer { display:none; }
  }
  @media (min-width:761px) { .scrim, .mobnav { display:none !important; } }
  .mobnav { display:none; }
  @media (max-width:760px) {
    .mobnav { display:inline-grid; place-items:center; width:34px; height:34px; background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r); color:var(--muted); }
  }



  @media (prefers-reduced-motion: reduce) {
    *,*::before,*::after { animation-duration:.001ms !important; animation-iteration-count:1 !important; transition-duration:.001ms !important; }
    .dot.s-running,.poll-tag .dot { animation:none; box-shadow:0 0 0 3px var(--run-soft); }
    .stream-cursor { animation:none; }
  }

  /* =========================================================================
     Login screen (mirrors observatory: OIDC bearer-token gate)
     ========================================================================= */
  .login {
    min-height: calc(100dvh - var(--topbar-h));
    display: grid; place-items: center; padding: 24px;
  }
  .login-card {
    width: min(420px, 100%); background: var(--panel); border: 1px solid var(--border);
    border-radius: var(--r-lg); padding: 32px 30px; box-shadow: var(--shadow); text-align: center;
  }
  .login-mark {
    width: 52px; height: 52px; border-radius: 14px; margin: 0 auto 18px;
    background: linear-gradient(150deg, var(--accent), color-mix(in oklab, var(--accent) 55%, #7c4dff));
    display: grid; place-items: center; color: #fff; box-shadow: var(--shadow);
  }
  .login h1 { font-size: 19px; font-weight: 700; letter-spacing: -.01em; color: var(--fg-strong); margin: 0 0 8px; }
  .login p { color: var(--muted); font-size: 13.5px; margin: 0 auto 22px; max-width: 30ch; }
  .btn-primary {
    width: 100%; padding: 11px 16px; border-radius: var(--r); border: 1px solid transparent;
    background: var(--accent); color: var(--accent-ink); font-weight: 650; font-size: 14px;
    display: inline-flex; align-items: center; justify-content: center; gap: 8px;
    transition: filter .15s;
  }
  .btn-primary:hover { filter: brightness(1.08); }
  .login-foot { margin-top: 16px; font-size: 11.5px; color: var(--muted-2); display: inline-flex; align-items: center; gap: 6px; }
  .linkbtn {
    background: transparent; border: 0; color: var(--accent); font-size: 12.5px; font-weight: 600;
    padding: 4px 6px; border-radius: var(--r-sm);
  }
  .linkbtn:hover { text-decoration: underline; }
</style>
</head>
<body data-mobile="chat">
  <div id="app"></div>

<script>
/* ===========================================================================
   数据契约（§7）——本页为视觉稿，用真实字段名 + 合理示例数据。
   接真实前端时：
     - chat 走 SSE：/api/studio/chat（text_message_content / tool_call_* / usage / run_*）
     - GET /api/studio/context → {scopeId, scopeResolved, scopeSource}
     - 会话↔产物关联由前端本地维护（这里用 SESSIONS 常量模拟）
   动作（写 workflow / 发布 / 建 team / draft-run / 配 cron）全部由 agent 在对话里用工具完成，
   UI 仅以只读卡片/面板反映状态——本页不出现这些动作按钮。
   =========================================================================== */

/* ===========================================================================
   Auth layer (production wiring) — mirrors the observatory OIDC PKCE gate.
   Browser OIDC Authorization Code + PKCE against nyxid (same console-web client
   as observatory; authority/clientId/scope identical). No server-side cookie/
   session, no NyxID change. This increment gates the page behind login only; the
   chat/cards below still render from the mock constants until live data is wired.
   =========================================================================== */
const CFG = {
  authority: "https://nyx.chrono-ai.fun",
  clientId: "37a93189-2734-406e-bca1-7dbdf25c5a53",
  scope: "openid profile email proxy",
  redirectUri: location.origin + "/workflow/studio/callback",
  storageKey: "aevatar-studio:nyxid:pkce"
};
const TOKEN_KEY = CFG.storageKey + ":token";
const PKCE_KEY  = CFG.storageKey + ":pkce";

function b64url(buf){ return btoa(String.fromCharCode.apply(null, new Uint8Array(buf))).replace(/\+/g,"-").replace(/\//g,"_").replace(/=+$/,""); }
async function sha256(text){ return b64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text))); }
function randomString(len){ const a=new Uint8Array(len); crypto.getRandomValues(a); return b64url(a.buffer).slice(0, len); }

function getToken(){ const raw=localStorage.getItem(TOKEN_KEY); if(!raw) return null; try { return JSON.parse(raw); } catch(e){ console.warn("studio: token parse failed", e); } return null; }
function setToken(t){ localStorage.setItem(TOKEN_KEY, JSON.stringify(t)); }
function clearToken(){ localStorage.removeItem(TOKEN_KEY); }

async function beginLogin(){
  const verifier = randomString(64);
  const st = randomString(32);
  const challenge = await sha256(verifier);
  sessionStorage.setItem(PKCE_KEY, JSON.stringify({ verifier, state: st }));
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
  history.replaceState({}, "", "/workflow/studio");
  if(!pending || pending.state !== returnedState){ console.warn("studio: login state mismatch"); return false; }
  const form = new URLSearchParams();
  form.set("grant_type", "authorization_code");
  form.set("code", code);
  form.set("redirect_uri", CFG.redirectUri);
  form.set("client_id", CFG.clientId);
  form.set("code_verifier", pending.verifier);
  const res = await fetch(CFG.authority + "/oauth/token", { method:"POST", headers:{ "Content-Type":"application/x-www-form-urlencoded" }, body: form.toString() });
  sessionStorage.removeItem(PKCE_KEY);
  if(!res.ok){ console.warn("studio: token exchange failed", res.status); return false; }
  const token = await res.json();
  token.obtained_at = Date.now();
  setToken(token);
  return true;
}

async function fetchUserInfo(){
  const token = getToken(); if(!token) return null;
  try { const res = await fetch(CFG.authority + "/oauth/userinfo", { headers:{ Authorization:"Bearer " + token.access_token } }); return res.ok ? await res.json() : null; }
  catch(e){ console.warn("studio: userinfo fetch failed", e); }
  return null;
}
function toAccount(info){ return info ? { label: info.email || info.preferred_username || info.sub || "已登录" } : null; }

// Reserved for the live-data increment: scope-gated bearer fetch helper, identical to observatory.
async function api(path){
  const token = getToken(); if(!token) throw new Error("not-authenticated");
  const res = await fetch(path, { headers:{ Authorization:"Bearer " + token.access_token } });
  if(res.status === 401){ signOutSilent(); throw new Error("unauthorized"); }
  if(res.status === 404) return null;
  if(!res.ok) throw new Error("api-error-" + res.status);
  return await res.json();
}

function signOut(){ clearToken(); beginLogin(); }
function signOutSilent(){ clearToken(); state.signedIn = false; render(); }

/* Account label shown in the top-bar chip: decoded from the signed-in identity when
   available, falling back to the mock constant. (Scope chip stays mock this increment.) */
let accountLabel = ACCOUNT.label;

const STUDIO_CONTEXT = { scopeId:"scope_alice_personal", scopeResolved:true, scopeSource:"nyxid" };
const ACCOUNT = { label:"alice@example.com" };
const NOW = "2026-06-23T03:05:00Z";   // 快照参考时刻（今天 2026-06-23），相对时间据此计算
const OBS = "/workflow/observatory";   // 运行观测台路由

/* ---- 工作流图：tech-news-digest（6 节点）---- */
const WF_TECH = {
  name:"tech-news-digest",
  nodes:[
    { nodeId:"fetch_rss",     nodeType:"tool",        category:"integration", label:"抓取科技源",   stepId:"s1", x:40,   y:230 },
    { nodeId:"foreach_item",  nodeType:"foreach",     category:"composition", label:"遍历每条新闻", stepId:"s2", x:360,  y:230 },
    { nodeId:"summarize",     nodeType:"llm",         category:"ai",          label:"逐条摘要",     stepId:"s3", x:440,  y:40  },
    { nodeId:"rank_filter",   nodeType:"conditional", category:"control",     label:"按热度筛选",   stepId:"s4", x:760,  y:230 },
    { nodeId:"compose_digest",nodeType:"llm",         category:"ai",          label:"编排中文日报", stepId:"s5", x:1080, y:230 },
    { nodeId:"deliver",       nodeType:"tool",        category:"integration", label:"投递日报",     stepId:"s6", x:1400, y:230 }
  ],
  edges:[
    { edgeId:"e1", fromNodeId:"fetch_rss",      toNodeId:"foreach_item",   edgeType:"NEXT" },
    { edgeId:"e2", fromNodeId:"foreach_item",   toNodeId:"summarize",      edgeType:"CONTAINS_STEP" },
    { edgeId:"e3", fromNodeId:"foreach_item",   toNodeId:"rank_filter",    edgeType:"NEXT" },
    { edgeId:"e4", fromNodeId:"rank_filter",    toNodeId:"compose_digest", edgeType:"NEXT", branchKey:"selected" },
    { edgeId:"e5", fromNodeId:"compose_digest", toNodeId:"deliver",        edgeType:"NEXT" }
  ]
};
// 不同阶段的节点状态映射
const NODE_STATES = {
  authored:{ fetch_rss:"ready", foreach_item:"ready", summarize:"ready", rank_filter:"ready", compose_digest:"ready", deliver:"ready" },
  running: { fetch_rss:"done", foreach_item:"done", summarize:"done", rank_filter:"current", compose_digest:"pending", deliver:"pending" },
  done:    { fetch_rss:"done", foreach_item:"done", summarize:"done", rank_filter:"done", compose_digest:"done", deliver:"done" }
};

/* ---- 完整对话（tech 会话）：每条 agent 消息由 blocks 组成 ---- */
const J = (o) => JSON.stringify(o, null, 2);
const CONVO = [
  { role:"user", text:"帮我做个每天汇总科技新闻的 workflow，每天早上 9 点把当天的科技要闻整理成一份中文日报。" },
  { role:"agent", produces:[], usage:{promptTokens:512, completionTokens:96, totalTokens:608, model:"claude-opus", cost:0.004, latencyMs:1840}, blocks:[
    { t:"text", v:"好的。我先在技能市场上找找可复用的抓取与摘要技能，再把它们组装成一条 workflow。" },
    { t:"tool", call:{ toolName:"ornn_search_skills", callId:"call_8f21", status:"ok",
        args:J({ query:"news rss fetch summarize", tags:["aevatar"], take:5 }),
        result:J({ matches:[ {slug:"rss_fetch", score:0.91}, {slug:"llm_summarize", score:0.88}, {slug:"html_clean", score:0.71} ] }) } },
    { t:"text", v:"找到了 `rss_fetch` 和 `llm_summarize`，足够覆盖抓取和摘要。开始装配 workflow。" }
  ]},
  { role:"agent", produces:["workflow"], usage:{promptTokens:1340, completionTokens:402, totalTokens:1742, model:"claude-opus", cost:0.011, latencyMs:5210}, blocks:[
    { t:"tool", call:{ toolName:"use_skill", callId:"call_9a02", status:"ok",
        args:J({ skill:"rss_fetch", as:"node", nodeId:"fetch_rss" }),
        result:J({ ok:true, nodeType:"tool" }) } },
    { t:"tool", call:{ toolName:"save_workflow", callId:"call_9a07", status:"ok",
        args:J({ name:"tech-news-digest", entry:"fetch_rss" }),
        result:J({ workflowId:"wf_tech_5c1", nodes:6, edges:5, valid:true }) } },
    { t:"text", v:"workflow `tech-news-digest` 已生成，共 6 个节点。右侧拓扑图已更新——可以缩放/平移查看。" }
  ]},
  { role:"agent", produces:["service"], usage:{promptTokens:880, completionTokens:150, totalTokens:1030, model:"claude-opus", cost:0.006, latencyMs:3120}, blocks:[
    { t:"text", v:"接着把它发布成可调用的 service，并注册到 NyxID。" },
    { t:"tool", call:{ toolName:"ornn_publish_skill", callId:"call_b110", status:"ok",
        args:J({ target:"workflow:tech-news-digest", visibility:"scope" }),
        result:J({ serviceSlug:"tech-news-digest-svc", nyx:"pending" }) } },
    { t:"text", v:"发布请求已提交，NyxID 注册中（通常几秒完成）。" }
  ]},
  { role:"agent", produces:["draftrun"], usage:{promptTokens:760, completionTokens:120, totalTokens:880, model:"claude-opus", cost:0.005, latencyMs:2600}, blocks:[
    { t:"text", v:"先跑一次 dry-run 验证流程是否通畅。" },
    { t:"tool", call:{ toolName:"draft_run", callId:"call_c220", status:"ok",
        args:J({ workflowId:"wf_tech_5c1", input:{ sources:["hn","36kr"] } }),
        result:J({ runId:"run_d3f1a2", status:"submitted", threadId:"th_71b" }) } },
    { t:"text", v:"dry-run 已提交（runId `run_d3f1a2`）。到**运行观测台**可以看时间线和拓扑。" }
  ]},
  { role:"agent", produces:["team"], usage:{promptTokens:1020, completionTokens:240, totalTokens:1260, model:"claude-opus", cost:0.008, latencyMs:4400}, blocks:[
    { t:"text", v:"现在建一个 team，把这条 workflow 作为入口 member 绑进去。" },
    { t:"tool", call:{ toolName:"create_team", callId:"call_d301", status:"ok",
        args:J({ displayName:"科技日报团队", description:"每日科技要闻自动汇总" }),
        result:J({ teamId:"team_77af" }) } },
    { t:"tool", call:{ toolName:"create_member", callId:"call_d305", status:"ok",
        args:J({ displayName:"digest-writer", implementationKind:"workflow" }),
        result:J({ memberId:"mem_12c", binding:"pending" }) } },
    { t:"tool", call:{ toolName:"bind_member", callId:"call_d309", status:"ok",
        args:J({ memberId:"mem_12c", workflow:"tech-news-digest" }),
        result:J({ status:"pending", revisions:[] }) } },
    { t:"text", v:"member `digest-writer` 正在绑定 workflow（异步，通常几秒完成）。绑定成功后我会设它为入口。" }
  ]},
  { role:"agent", produces:["teamdone"], usage:{promptTokens:540, completionTokens:80, totalTokens:620, model:"claude-opus", cost:0.004, latencyMs:2100}, blocks:[
    { t:"tool", call:{ toolName:"set_entry_member", callId:"call_e410", status:"ok",
        args:J({ teamId:"team_77af", memberId:"mem_12c" }),
        result:J({ entryMemberId:"mem_12c" }) } },
    { t:"text", v:"绑定完成，并已设为入口 member。团队就绪。" }
  ]},
  { role:"agent", produces:["schedule"], usage:{promptTokens:690, completionTokens:160, totalTokens:850, model:"claude-opus", cost:0.005, latencyMs:2900}, blocks:[
    { t:"text", v:"最后配一个每天早上 9 点的定时任务。" },
    { t:"tool", call:{ toolName:"create_schedule", callId:"call_f520", status:"ok",
        args:J({ service:"tech-news-digest-svc", cronExpression:"0 0 9 * * *", timezone:"Asia/Shanghai", displayName:"每日 09:00 科技日报" }),
        result:J({ scheduleId:"sch_9a1", enabled:true, nextFireAt:"2026-06-24T01:00:00Z" }) } },
    { t:"text", v:"定时任务已创建：每天 09:00（Asia/Shanghai）触发，下一次在明早 9 点。整条流水线完成 ✅" }
  ]},
  { role:"user", text:"定时任务最近跑得怎么样？" },
  { role:"agent", produces:["schedrefresh"], usage:{promptTokens:430, completionTokens:140, totalTokens:570, model:"claude-opus", cost:0.004, latencyMs:1700}, blocks:[
    { t:"tool", call:{ toolName:"list_schedules", callId:"call_1630", status:"ok",
        args:J({ workflowId:"wf_tech_5c1" }),
        result:J({ schedules:[
          { scheduleId:"sch_9a1", fireCount:5, failureCount:0, lastFireAt:"2026-06-23T01:00:00Z" },
          { scheduleId:"sch_4b7", fireCount:5, failureCount:1, lastFireAt:"2026-06-23T01:30:12Z", lastError:"upstream 502 from rss_fetch" }
        ] }) } },
    { t:"text", v:"拉到了最近触发记录：主任务连续 5 次成功；补偿任务有 1 次失败（上游 502），已在下一次恢复。详情见右侧 **Schedules** 卡，或到观测台看每次触发的运行。" }
  ]}
];

/* ---- 右侧产物对象（按状态显式给出，便于逐态呈现）---- */
const SERVICE = { slug:"tech-news-digest-svc", scopeId:"scope_alice_personal", definitionId:"wf_tech_5c1" };
const TEAM = { teamId:"team_77af", displayName:"科技日报团队", entryMemberId:"mem_12c",
  members:[ { memberId:"mem_12c", displayName:"digest-writer", implementationKind:"workflow", publishedServiceId:"tech-news-digest-svc" } ] };
const SCHEDULES_FULL = [
  { scheduleId:"sch_9a1", displayName:"每日 09:00 科技日报", cronExpression:"0 0 9 * * *", timezone:"Asia/Shanghai", enabled:true,
    nextFireAt:"2026-06-24T01:00:00Z", lastFireAt:"2026-06-23T01:00:00Z", fireCount:5, failureCount:0, lastError:"" },
  { scheduleId:"sch_4b7", displayName:"补偿重试 · 09:30", cronExpression:"0 30 9 * * *", timezone:"Asia/Shanghai", enabled:true,
    nextFireAt:"2026-06-24T01:30:00Z", lastFireAt:"2026-06-23T01:30:12Z", fireCount:5, failureCount:1, lastError:"upstream 502 from rss_fetch" }
];

/* ---- 会话列表 ---- */
const SESSIONS = [
  { id:"sess_tech", title:"每日科技新闻日报", updatedAtUtc:"2026-06-23T03:02:00Z", status:"completed",
    badges:{ wf:true, team:true, svc:true, cron:2 } },
  { id:"sess_invoice", title:"发票录入助手", updatedAtUtc:"2026-06-22T08:14:00Z", status:"completed",
    badges:{ wf:true, team:false, svc:true, cron:0 } },
  { id:"sess_lead", title:"线索补全 · 草稿", updatedAtUtc:"2026-06-21T11:40:00Z", status:"stopped",
    badges:{ wf:true, team:false, svc:false, cron:0 } }
];

/* ===========================================================================
   工具函数
   =========================================================================== */
const $ = (s,r=document)=>r.querySelector(s);
const el=(t,a={},h)=>{const n=document.createElement(t);for(const k in a){if(k==="class")n.className=a[k];else if(k.startsWith("on")&&typeof a[k]==="function")n.addEventListener(k.slice(2),a[k]);else if(a[k]!=null)n.setAttribute(k,a[k]);}if(h!=null)n.innerHTML=h;return n;};
const esc=(s)=>String(s==null?"":s).replace(/[&<>"]/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
const parseT=(iso)=>Date.parse(iso);
function nowMs(){ return parseT(NOW); }
function relPast(iso){ let d=Math.max(0,Math.round((nowMs()-parseT(iso))/1000)); if(d<=1)return"刚刚"; if(d<60)return d+" 秒前"; if(d<3600)return Math.floor(d/60)+" 分钟前"; if(d<86400)return Math.floor(d/3600)+" 小时前"; return Math.floor(d/86400)+" 天前"; }
function relFuture(iso){ let d=Math.max(0,Math.round((parseT(iso)-nowMs())/1000)); if(d<60)return"即将"; if(d<3600)return"约 "+Math.floor(d/60)+" 分钟后"; if(d<86400)return"约 "+Math.floor(d/3600)+" 小时后"; return"约 "+Math.floor(d/86400)+" 天后"; }
function clockUTC(iso){ const m=/T(\d{2}:\d{2})/.exec(iso); return m?m[1]:iso; }
function initials(s){ return (s||"?").trim().charAt(0).toUpperCase(); }

function colorJSON(str){
  if(!str) return '<span class="j-null">—</span>';
  let s=esc(str);
  s=s.replace(/(&quot;.*?&quot;)(\s*:)?/g,(m,tok,colon)=>colon?`<span class="j-key">${tok}</span>${colon}`:`<span class="j-str">${tok}</span>`);
  s=s.replace(/(^|[\s:\[,])(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)(?=[\s,\]\}]|$)/g,(m,pre,num)=>`${pre}<span class="j-num">${num}</span>`);
  s=s.replace(/\b(true|false)\b/g,'<span class="j-bool">$1</span>');
  s=s.replace(/\bnull\b/g,'<span class="j-null">null</span>');
  return s;
}
function mdInline(t){ return esc(t).replace(/`([^`]+)`/g,'<code>$1</code>').replace(/\*\*([^*]+)\*\*/g,'<b style="color:var(--fg-strong)">$1</b>'); }

const ICON={
  lock:'<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
  sun:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="12" cy="12" r="4.2"/><path d="M12 2v2.4M12 19.6V22M2 12h2.4M19.6 12H22M4.6 4.6l1.7 1.7M17.7 17.7l1.7 1.7M19.4 4.6l-1.7 1.7M6.3 17.7l-1.7 1.7"/></svg>',
  moon:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A8.5 8.5 0 1 1 11.2 3a6.6 6.6 0 0 0 9.8 9.8Z"/></svg>',
  check:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12.5 4.2 4.2L19 7"/></svg>',
  x:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="m6 6 12 12M18 6 6 18"/></svg>',
  chevron:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 6 6 6-6 6"/></svg>',
  copy:'<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a1 1 0 0 1 1-1h9"/></svg>',
  send:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12 20 4l-7 16-2.5-6.5L4 12Z"/></svg>',
  stop:'<svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>',
  plus:'<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>',
  edit:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M14 4 18.5 8.5 8 19l-4.5 1 1-4.5L14 4Z"/></svg>',
  trash:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/></svg>',
  ext:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/></svg>',
  alert:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M12 8.5v4.5M12 16.2v.2"/><path d="M10.3 3.9 2.5 17.5A2 2 0 0 0 4.2 20.5h15.6a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg>',
  retry:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M20 11a8 8 0 1 0-2.3 5.7M20 5v6h-6"/></svg>',
  graph:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="5" cy="6" r="2.2"/><circle cx="5" cy="18" r="2.2"/><circle cx="19" cy="12" r="2.2"/><path d="M7 6.6 17 11M7 17.4 17 13"/></svg>',
  chat:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"><path d="M4 5h16v11H9l-4 3.5V16H4Z"/></svg>',
  tool:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M14.5 6a3.5 3.5 0 0 0-4.6 4.4L4 16.3 6.7 19l5.9-5.9A3.5 3.5 0 1 0 14.5 6Z"/></svg>',
  foreach:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9a8 8 0 0 1 13-3l3 3M20 15a8 8 0 0 1-13 3l-3-3"/><path d="M20 4v5h-5M4 20v-5h5"/></svg>',
  cond:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3 21 12 12 21 3 12 12 3Z"/></svg>',
  human:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="12" cy="8" r="3.4"/><path d="M5.5 20a6.5 6.5 0 0 1 13 0"/></svg>',
  svc:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="6" rx="1.5"/><rect x="3" y="14" width="18" height="6" rx="1.5"/><path d="M7 7h.01M7 17h.01"/></svg>',
  team:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="9" cy="8" r="3"/><path d="M3.5 19a5.5 5.5 0 0 1 11 0"/><path d="M16 6a3 3 0 0 1 0 5.6M18 13.5a5.5 5.5 0 0 1 3 5"/></svg>',
  clock:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/></svg>',
  spark:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5 18 18M18 6l-2.5 2.5M8.5 15.5 6 18"/></svg>',
  studio:'<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="2.4"/><circle cx="6" cy="18" r="2.4"/><circle cx="18" cy="12" r="2.4"/><path d="M8.2 7.2 15.8 11M8.2 16.8 15.8 13"/></svg>',
  menu:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M4 7h16M4 12h16M4 17h16"/></svg>',
  ctx:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M15 4v16"/></svg>'
};
function catIcon(t){ return t==="llm"?ICON.chat : t==="tool"?ICON.tool : t==="foreach"?ICON.foreach : t==="conditional"?ICON.cond : t==="human"?ICON.human : ICON.tool; }

/* ===========================================================================
   App state
   =========================================================================== */
const state = {
  signedIn:false,
  scenario:"full",
  theme: localStorage.getItem("studio-theme") || null,
  expanded:new Set(),       // 展开的 tool-call / card
  selectedSession:"sess_tech",
  ctxTab:"graph",           // graph | workflow | team | service | schedules
  ctxW: (parseInt(localStorage.getItem("studio-ctxw"),10) || null),  // 用户拖拽过的右栏宽度（px）
  zoom:1, panX:0, panY:0
};
function applyTheme(){ if(state.theme==="light"||state.theme==="dark")document.documentElement.setAttribute("data-theme",state.theme); else document.documentElement.removeAttribute("data-theme"); }
function effDark(){ if(state.theme)return state.theme==="dark"; return !window.matchMedia||!window.matchMedia("(prefers-color-scheme: light)").matches; }

/* ---- 场景定义：每个场景描述本会话进行到哪、右侧产物状态 ---- */
const SCN = {
  "no-sessions": { sessions:false },
  "new-session": { session:"new", end:0, intro:true },
  "streaming":   { session:"tech", end:2, streamLast:true, graph:null },
  "tool-ok":     { session:"tech", end:3, graph:"authored", arts:{wf:"idle"} },
  "tool-failed": { session:"tech", end:4, graph:"authored", failLastTool:true, arts:{wf:"idle", svcFail:true} },
  "workflow":    { session:"tech", end:3, graph:"authored", arts:{wf:"idle"} },
  "publishing":  { session:"tech", end:4, graph:"authored", arts:{wf:"idle", svc:"pending"} },
  "draftrun":    { session:"tech", end:5, graph:"running", arts:{wf:"running", svc:"registered"} },
  "binding":     { session:"tech", end:6, graph:"done", arts:{wf:"completed", svc:"registered", team:"pending"} },
  "scheduled":   { session:"tech", end:8, graph:"done", arts:{wf:"completed", svc:"registered", team:"bound", sched:"fresh"} },
  "sched-status":{ session:"tech", end:10, graph:"done", arts:{wf:"completed", svc:"registered", team:"bound", sched:"full"} },
  "full":        { session:"tech", end:10, graph:"done", arts:{wf:"completed", svc:"registered", team:"bound", sched:"full"} },
  "chat-error":  { session:"tech", end:5, graph:"authored", chatError:true, arts:{wf:"idle", svc:"registered"} }
};
function scn(){ return SCN[state.scenario] || SCN.full; }

/* ===========================================================================
   Topbar
   =========================================================================== */
function renderTopbar(){
  const bar=el("header",{class:"topbar",role:"banner"});
  bar.innerHTML=`
    <button class="mobnav" id="navBtn" aria-label="会话列表">${ICON.menu}</button>
    <div class="brand">
      <div class="brand-mark" aria-hidden="true">${ICON.studio}</div>
      <div><div class="brand-name">工作室 <span class="brand-sub">Workflow Studio</span></div></div>
    </div>
    ${state.signedIn?`<div class="scope-chip" title="scope 已解析（来源：${STUDIO_CONTEXT.scopeSource}）">${ICON.check}<span>scope</span><span class="sid">${esc(STUDIO_CONTEXT.scopeId)}</span></div>`:""}
    <div class="spacer"></div>
    ${state.signedIn?`<a class="obs-link" href="${OBS}" title="跳转到运行观测台（只读查看运行结果）">${ICON.ext}<span>运行观测台</span></a>`:""}`;
  const tbtn=el("button",{class:"iconbtn",id:"themeBtn","aria-label":"切换主题",title:"切换主题"});
  tbtn.innerHTML=effDark()?ICON.sun:ICON.moon;
  tbtn.addEventListener("click",()=>{ state.theme=effDark()?"light":"dark"; localStorage.setItem("studio-theme",state.theme); applyTheme(); render(); });
  bar.appendChild(tbtn);
  if(state.signedIn){
    const acct=el("div",{class:"account"});
    acct.innerHTML=`<span class="avatar" aria-hidden="true">${initials(accountLabel)}</span><span class="who" title="${esc(accountLabel)}">${esc(accountLabel)}</span>`;
    const sw=el("button",{class:"linkbtn","aria-label":"切换账户"},"切换账户");
    sw.addEventListener("click",()=>{ signOut(); });
    acct.appendChild(sw);
    bar.appendChild(acct);
  }
  return bar;
}

/* ===========================================================================
   Left — sessions
   =========================================================================== */
function renderSessions(){
  const pane=el("aside",{class:"sess-pane","aria-label":"会话列表"});
  const head=el("div",{class:"sess-head"});
  const nb=el("button",{class:"new-sess"},`${ICON.plus}<span>新建会话</span>`);
  nb.addEventListener("click",()=>{ state.scenario="new-session"; render(); });
  head.appendChild(nb);
  pane.appendChild(head);

  const list=el("div",{class:"sess-list scroll",role:"list"});
  const s=scn();
  if(s.sessions===false){
    list.appendChild(el("div",{class:"empty"},`<div class="ic" aria-hidden="true">${ICON.chat}</div><div class="et">还没有会话</div><div class="es">开始对话来创建你的第一个工作流。</div>`));
    pane.appendChild(list); return pane;
  }
  const rows = s.session==="new" ? [{id:"sess_new",title:"新会话",updatedAtUtc:NOW,status:"unknown",badges:{}}, ...SESSIONS] : SESSIONS;
  const selId = s.session==="new" ? "sess_new" : "sess_tech";
  rows.forEach(se=>{
    const cur = se.id===selId;
    const row=el("button",{class:"sess-row",role:"listitem","aria-current":String(cur)});
    const b=se.badges||{};
    const badges=[];
    if(b.wf)badges.push('<span class="pbadge wf">WF</span>');
    if(b.team)badges.push('<span class="pbadge team">TEAM</span>');
    if(b.svc)badges.push('<span class="pbadge svc">SVC</span>');
    if(b.cron)badges.push(`<span class="pbadge cron">${ICON.clock.replace('width="14" height="14"','width="10" height="10"')} ${b.cron}</span>`);
    row.innerHTML=`
      <span class="dot s-${se.status}" aria-hidden="true"></span>
      <span class="st">${esc(se.title)}</span>
      <span class="sm">${relPast(se.updatedAtUtc)}</span>
      ${badges.length?`<span class="sess-badges">${badges.join("")}</span>`:""}
      <span class="sess-actions"><button title="重命名" aria-label="重命名会话">${ICON.edit}</button><button class="del" title="删除" aria-label="删除会话">${ICON.trash}</button></span>`;
    row.addEventListener("click",(e)=>{ if(e.target.closest(".sess-actions"))return; document.body.classList.remove("nav-open"); });
    list.appendChild(row);
  });
  pane.appendChild(list);
  return pane;
}

/* ===========================================================================
   Center — chat
   =========================================================================== */
let streamTimer=null;
function renderChat(){
  const pane=el("section",{class:"chat-pane","aria-label":"对话"});
  const scrollArea=el("div",{class:"chat-scroll scroll",id:"chatScroll"});
  const inner=el("div",{class:"chat-inner"});
  const s=scn();

  const isEmpty = s.sessions===false || (s.session==="new");
  if(isEmpty){
    inner.appendChild(renderWelcome());
  } else {
    const msgs = CONVO.slice(0, s.end);
    msgs.forEach((m,i)=>{
      const isLastAgent = (i===msgs.length-1) && m.role==="agent";
      inner.appendChild(renderMessage(m, isLastAgent && s.streamLast, s.failLastTool && isLastAgent));
    });
    if(s.chatError) inner.appendChild(renderChatError());
  }
  scrollArea.appendChild(inner);
  pane.appendChild(scrollArea);
  pane.appendChild(renderComposer(s.streamLast));
  return pane;
}

function renderWelcome(){
  const w=el("div",{class:"welcome"});
  w.innerHTML=`
    <div class="wmark" aria-hidden="true">${ICON.spark}</div>
    <h1>跟 aevatar agent 对话，编排你的工作流</h1>
    <p>这个 agent 已装载 <code>aevatar-platform</code> 技能集——你只需用自然语言描述需求，它会在对话里完成写 workflow、发布 service、建 team、配定时任务。运行结果到「运行观测台」只读查看。</p>`;
  const chips=el("div",{class:"prompt-chips"});
  ["帮我做个每天汇总科技新闻的 workflow","把一个 PDF 发票录入流程发布成可调用的 service","给现有 workflow 配一个每周一早上的定时任务"].forEach(t=>{
    const c=el("button",{class:"prompt-chip"},`${ICON.spark}<span>${esc(t)}</span>`);
    c.addEventListener("click",()=>{ const ta=$("#composerInput"); if(ta){ ta.value=t; ta.focus(); } });
    chips.appendChild(c);
  });
  w.appendChild(chips);
  return w;
}

function renderMessage(m, streamLast, failLast){
  const wrap=el("div",{class:"msg "+(m.role==="user"?"user":"agent")});
  wrap.innerHTML=`<div class="m-avatar" aria-hidden="true">${m.role==="user"?initials(ACCOUNT.label):ICON.studio}</div>`;
  const body=el("div",{class:"m-body"});
  body.appendChild(el("div",{class:"m-name"}, m.role==="user"?`<b>你</b>`:`<b>aevatar</b> · 工作室 agent`));
  if(m.role==="user"){
    body.appendChild(el("div",{class:"bubble-user"}, esc(m.text)));
  } else {
    const agentBox=el("div",{class:"bubble-agent"});
    const blocks=m.blocks||[];
    if(streamLast){
      // 流式生成中：首段文本逐字出现（打字光标）+ 首个工具调用「进行中」（spinner），后续内容尚未到达
      let streamed=false, toolShown=false;
      for(const blk of blocks){
        if(blk.t==="text" && !streamed){
          const p=el("p",{}); p.setAttribute("data-stream", encodeURIComponent(blk.v)); p.innerHTML='<span class="stream-cursor"></span>';
          agentBox.appendChild(p); streamed=true;
        } else if(blk.t==="tool" && !toolShown){
          agentBox.appendChild(renderToolCall(blk.call, true)); toolShown=true; break;
        }
      }
    } else {
      const firstToolIdx = blocks.findIndex(x=>x.t==="tool");
      blocks.forEach((blk,bi)=>{
        if(blk.t==="text"){ agentBox.appendChild(el("p",{}, mdInline(blk.v))); }
        else if(blk.t==="tool"){
          let call=blk.call;
          if(failLast && bi===firstToolIdx){ call={...call, status:"failed", result:"", error:"NyxID 注册失败：registry timeout (504)"}; }
          agentBox.appendChild(renderToolCall(call));
        }
      });
    }
    body.appendChild(agentBox);
    if(m.usage && !streamLast){
      const u=m.usage;
      body.appendChild(el("div",{class:"usage-line"},
        `<span><b>${u.totalTokens.toLocaleString()}</b> tokens</span><span>$${u.cost.toFixed(3)}</span><span>${u.model}</span><span>${u.latencyMs} ms</span>`));
    }
  }
  wrap.appendChild(body);
  return wrap;
}

function renderToolCall(tc, forceRunning){
  const status = forceRunning ? "running" : tc.status;
  const open = state.expanded.has(tc.callId) || status==="failed";
  const box=el("div",{class:"toolcall","data-open":String(open),"data-callid":tc.callId});
  const statusHtml = status==="running"
    ? `<span class="tc-status run"><span class="spin"></span>进行中</span>`
    : status==="failed"
      ? `<span class="tc-status bad">${ICON.x}失败</span>`
      : `<span class="tc-status ok">${ICON.check}成功</span>`;
  const head=el("button",{class:"tc-head","aria-expanded":String(open)});
  head.innerHTML=`<span class="tc-chevron" aria-hidden="true">${ICON.chevron}</span>
    <span class="tc-name">${esc(tc.toolName)}</span><span class="tc-callid">${esc(tc.callId)}</span>${statusHtml}`;
  head.addEventListener("click",()=>{ if(state.expanded.has(tc.callId))state.expanded.delete(tc.callId);else state.expanded.add(tc.callId); const o=state.expanded.has(tc.callId); box.setAttribute("data-open",String(o)); head.setAttribute("aria-expanded",String(o)); });
  box.appendChild(head);
  const body=el("div",{class:"tc-body"});
  body.appendChild(jsonField("arguments", tc.args));
  if(status==="failed"){
    const f=el("div",{}); f.innerHTML=`<div class="tc-field-head"><span class="tc-field-label">error</span></div>`;
    f.appendChild(el("pre",{class:"json scroll"},`<span class="tc-error">${esc(tc.error||"unknown error")}</span>`));
    body.appendChild(f);
  } else if(status==="running"){
    const f=el("div",{}); f.innerHTML=`<div class="tc-field-head"><span class="tc-field-label">result</span></div>`;
    f.appendChild(el("pre",{class:"json"},`<span class="j-null">… 等待返回</span>`));
    body.appendChild(f);
  } else {
    body.appendChild(jsonField("result", tc.result));
  }
  box.appendChild(body);
  return box;
}
function jsonField(label,raw){
  const f=el("div",{});
  const head=el("div",{class:"tc-field-head"});
  head.innerHTML=`<span class="tc-field-label">${esc(label)}</span>`;
  const copy=el("button",{class:"copybtn","aria-label":"复制 "+label},`${ICON.copy}<span>复制</span>`);
  copy.addEventListener("click",async()=>{ try{await navigator.clipboard.writeText(raw||"");}catch(e){const ta=document.createElement("textarea");ta.value=raw||"";document.body.appendChild(ta);ta.select();try{document.execCommand("copy");}catch(_){}ta.remove();} copy.classList.add("done");copy.querySelector("span").textContent="已复制";setTimeout(()=>{copy.classList.remove("done");copy.querySelector("span").textContent="复制";},1400); });
  head.appendChild(copy); f.appendChild(head);
  const pre=el("pre",{class:"json scroll",tabindex:"0"}); pre.innerHTML=colorJSON(raw); f.appendChild(pre);
  return f;
}

function renderChatError(){
  const e=el("div",{class:"chat-error",role:"alert"});
  e.innerHTML=`${ICON.alert}<div><div class="ce-l">生成出错 · run_error</div><div class="ce-m">与 agent 的连接中断（code: stream_closed）。已收到的内容已保留，可重试这条消息。</div></div>`;
  const r=el("button",{class:"ce-retry"},`${ICON.retry}<span>重试</span>`);
  e.querySelector("div").appendChild(r);
  return e;
}

function renderComposer(streaming){
  const wrap=el("div",{class:"composer-wrap"});
  const c=el("div",{class:"composer"});
  const box=el("div",{class:"composer-box"});
  const ta=el("textarea",{id:"composerInput",rows:"1",placeholder:"描述你想要的工作流，或问问当前编排状态…","aria-label":"消息输入框"});
  ta.addEventListener("input",()=>{ ta.style.height="auto"; ta.style.height=Math.min(ta.scrollHeight,140)+"px"; const sb=$("#sendBtn"); if(sb)sb.disabled=!ta.value.trim(); });
  box.appendChild(ta);
  if(streaming){
    const stop=el("button",{class:"stop-btn",type:"button"},`${ICON.stop}<span>停止生成</span>`);
    stop.addEventListener("click",()=>{ if(streamTimer){clearInterval(streamTimer);streamTimer=null;} document.querySelectorAll(".stream-cursor").forEach(n=>n.remove()); const t=document.querySelector("[data-stream]"); if(t){t.innerHTML=mdInline(decodeURIComponent(t.getAttribute("data-stream")));t.removeAttribute("data-stream");} stop.replaceWith(makeSend(ta)); });
    box.appendChild(stop);
  } else {
    box.appendChild(makeSend(ta));
  }
  c.appendChild(box);
  wrap.appendChild(c);
  wrap.appendChild(el("div",{class:"composer-hint"},`<span class="kbd">Enter</span> 发送 · <span class="kbd">Shift+Enter</span> 换行 · agent 用工具完成所有编排动作，UI 只读呈现`));
  return wrap;
}
function makeSend(ta){
  const sb=el("button",{class:"send-btn",id:"sendBtn",type:"button","aria-label":"发送",disabled:"true"},ICON.send);
  return sb;
}

/* typewriter for streaming scenario */
function startStream(){
  if(streamTimer){clearInterval(streamTimer);streamTimer=null;}
  const target=document.querySelector("[data-stream]");
  if(!target)return;
  const full=decodeURIComponent(target.getAttribute("data-stream"));
  const reduce=window.matchMedia&&window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  if(reduce){ target.innerHTML=mdInline(full)+'<span class="stream-cursor"></span>'; return; }
  let i=0;
  streamTimer=setInterval(()=>{
    i+=2;
    const slice=full.slice(0,i);
    target.innerHTML=mdInline(slice)+'<span class="stream-cursor"></span>';
    const sc=$("#chatScroll"); if(sc)sc.scrollTop=sc.scrollHeight;
    if(i>=full.length){ clearInterval(streamTimer); streamTimer=null; }
  },36);
}

/* ===========================================================================
   Right — context (graph + artifact cards)
   =========================================================================== */
function renderContext(){
  const pane=el("aside",{class:"ctx-pane","aria-label":"当前会话上下文"});
  const rez=el("div",{class:"ctx-resizer",title:"拖动调整宽度","aria-hidden":"true"});
  attachResizer(rez);
  pane.appendChild(rez);

  const s=scn();
  const hasWF = s.graph!=null && !(s.session==="new") && s.sessions!==false;
  const a=s.arts||{};
  const has={ team:!!a.team, service:!!(a.svc||a.svcFail), schedules:!!a.sched };
  const dot={
    workflow: hasWF ? (a.wf==="running"?"running":a.wf==="completed"?"completed":a.wf==="failed"?"failed":"unknown") : null,
    team: has.team ? (a.team==="bound"?"completed":a.team==="failed"?"failed":"pending") : null,
    service: has.service ? (a.svcFail?"failed":a.svc==="registered"?"completed":"pending") : null,
    schedules: has.schedules ? "running" : null
  };

  // 多 tab：图 / Workflow / Team / Service / Schedules
  const head=el("div",{class:"ctx-head"});
  const tabs=el("div",{class:"ctx-tabs",role:"tablist","aria-label":"当前会话上下文视图"});
  [["graph","图",ICON.graph,null],
   ["workflow","Workflow",ICON.graph,dot.workflow],
   ["team","Team",ICON.team,dot.team],
   ["service","Service",ICON.svc,dot.service],
   ["schedules","Schedules",ICON.clock,dot.schedules]
  ].forEach(([id,lab,icon,d])=>{
    const t=el("button",{class:"ctx-tab",role:"tab","aria-selected":String(state.ctxTab===id),"aria-label":lab},
      `${icon}<span>${lab}</span>${d?`<span class="tdot dot s-${d}"></span>`:""}`);
    t.addEventListener("click",()=>{ state.ctxTab=id; render(); });
    tabs.appendChild(t);
  });
  head.appendChild(tabs);
  pane.appendChild(head);

  const body=el("div",{class:"ctx-body"});
  const tab=state.ctxTab;
  if(tab==="graph"){
    if(hasWF) body.appendChild(renderGraph(s.graph));
    else body.appendChild(el("div",{style:"position:absolute;inset:0;display:grid;place-items:center"},`<div class="empty"><div class="ic" aria-hidden="true">${ICON.graph}</div><div class="et">当前会话还没有 workflow</div><div class="es">在左侧对话中描述你的需求，agent 会在这里生成拓扑图。</div></div>`));
  } else if(tab==="workflow"){
    body.appendChild(hasWF ? renderWorkflowPanel(a.wf||"idle") : panelEmpty(ICON.graph,"还没有 workflow","在对话中描述需求，agent 会生成 workflow，拓扑会出现在「图」标签。"));
  } else if(tab==="team"){
    body.appendChild(has.team ? renderTeamPanel(a.team) : panelEmpty(ICON.team,"还没有 team","agent 建好 team / member 后会出现在这里。"));
  } else if(tab==="service"){
    body.appendChild(has.service ? renderServicePanel(a.svcFail?"failed":a.svc) : panelEmpty(ICON.svc,"还没有 service","workflow 发布成 service 后会出现在这里。"));
  } else {
    body.appendChild(has.schedules ? renderSchedulesPanel(a.sched) : panelEmpty(ICON.clock,"还没有定时任务","为 service 配置 cron 后会出现在这里。"));
  }
  pane.appendChild(body);
  return pane;
}
function panelEmpty(icon,t,sub){ const p=el("div",{class:"ctx-panel"}); p.appendChild(el("div",{class:"empty"},`<div class="ic" aria-hidden="true">${icon}</div><div class="et">${t}</div><div class="es">${sub}</div>`)); return p; }

/* ---- SVG DAG ---- */
function renderGraph(phase){
  const wrap=el("div",{style:"position:absolute;inset:0"});
  const vp=el("div",{class:"graph-viewport",id:"graphVp"});
  const layer=el("div",{class:"graph-layer",id:"graphLayer"});
  const W=1700, H=380;
  layer.style.width=W+"px"; layer.style.height=H+"px";
  const stMap=NODE_STATES[phase]||NODE_STATES.authored;
  const pos={}; WF_TECH.nodes.forEach(n=>pos[n.nodeId]={x:n.x,y:n.y});

  const svgNS="http://www.w3.org/2000/svg";
  const svg=document.createElementNS(svgNS,"svg"); svg.setAttribute("width",W); svg.setAttribute("height",H);
  const defs=document.createElementNS(svgNS,"defs");
  defs.innerHTML=`<marker id="arr" viewBox="0 0 10 10" refX="8.5" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="var(--muted-2)"/></marker>`;
  svg.appendChild(defs);
  const NW=244, NH=96;
  const labels=[];
  WF_TECH.edges.forEach(e=>{
    const a=pos[e.fromNodeId], b=pos[e.toNodeId]; if(!a||!b)return;
    const contains = e.edgeType==="CONTAINS_STEP";
    let x1,y1,x2,y2;
    if(contains){ x1=a.x+NW/2; y1=a.y; x2=b.x; y2=b.y+NH/2; } // foreach 顶 → body 左
    else { x1=a.x+NW; y1=a.y+NH/2; x2=b.x; y2=b.y+NH/2; }
    const p=document.createElementNS(svgNS,"path");
    let d;
    if(contains){ d=`M${x1},${y1} C${x1},${y1-50} ${x2-60},${y2} ${x2},${y2}`; }
    else { const cx=(x1+x2)/2; d=`M${x1},${y1} C${cx},${y1} ${cx},${y2} ${x2},${y2}`; }
    p.setAttribute("d",d); p.setAttribute("fill","none");
    p.setAttribute("stroke", contains?"var(--cat-composition)":"var(--muted-2)");
    p.setAttribute("stroke-width", contains?"1.4":"1.6");
    if(contains) p.setAttribute("stroke-dasharray","5 4");
    if(stMap[e.toNodeId]==="pending"){ p.setAttribute("stroke-dasharray","4 4"); p.setAttribute("opacity",".55"); }
    p.setAttribute("marker-end","url(#arr)");
    svg.appendChild(p);
    const mx=contains?(x1+x2)/2-30:(x1+x2)/2, my=contains?(y1+y2)/2-10:y1;
    labels.push({mx,my,text:e.branchKey?`${e.edgeType}:${e.branchKey}`:e.edgeType});
  });
  layer.appendChild(svg);

  WF_TECH.nodes.forEach(n=>{
    const st=stMap[n.nodeId]||"ready";
    const stLabel={ready:"已就绪",done:"已完成",current:"运行中",failed:"失败",stopped:"已停止",pending:"待执行"}[st];
    const node=el("div",{class:`gnode st-${st}`,style:`left:${n.x}px;top:${n.y}px;--cat:var(--cat-${n.category})`});
    node.innerHTML=`
      <div class="gn-top">
        <span class="gn-ic" aria-hidden="true">${catIcon(n.nodeType)}</span>
        <div><div class="gn-label">${esc(n.label)}</div><div class="gn-id">${esc(n.nodeId)}</div></div>
      </div>
      <div class="gn-foot"><span class="gn-type">${esc(n.nodeType)}</span><span class="gn-state">${st==="current"?'<span class="dot s-running"></span>':""}${stLabel}</span></div>`;
    layer.appendChild(node);
  });
  labels.forEach(L=> layer.appendChild(el("span",{class:"edge-label",style:`left:${L.mx}px;top:${L.my}px`},esc(L.text))));

  vp.appendChild(layer);
  wrap.appendChild(vp);

  // legend (categories)
  const cats=[["integration","集成"],["composition","编排"],["ai","AI"],["control","控制"]];
  const legend=el("div",{class:"graph-legend"});
  legend.innerHTML=cats.map(([c,l])=>`<span class="lg" style="--cat:var(--cat-${c})"><span class="sw"></span>${l}</span>`).join("");
  wrap.appendChild(legend);

  // zoom/pan/fit/1:1 controls
  const ctr=el("div",{class:"graph-ctrls"});
  ctr.innerHTML=`<button data-z="out" aria-label="缩小">−</button><span class="zlabel" id="zlabel">100%</span><button data-z="in" aria-label="放大">+</button><button data-z="fit" aria-label="适应">⤢</button><button data-z="one" aria-label="1:1">1:1</button>`;
  wrap.appendChild(ctr);
  ctr.addEventListener("click",(e)=>{ const z=e.target.closest("button")?.getAttribute("data-z"); if(!z)return;
    if(z==="in")state.zoom=Math.min(2,state.zoom*1.2);
    else if(z==="out")state.zoom=Math.max(.3,state.zoom/1.2);
    else if(z==="one"){state.zoom=1;state.panX=0;state.panY=0;}
    else if(z==="fit")fitGraph(W,H);
    applyTransform();
  });

  // pan by drag
  let dragging=false,sx,sy;
  vp.addEventListener("pointerdown",(e)=>{ dragging=true; sx=e.clientX-state.panX; sy=e.clientY-state.panY; vp.setPointerCapture(e.pointerId); });
  vp.addEventListener("pointermove",(e)=>{ if(!dragging)return; state.panX=e.clientX-sx; state.panY=e.clientY-sy; applyTransform(); });
  vp.addEventListener("pointerup",()=>dragging=false);
  vp.addEventListener("pointercancel",()=>dragging=false);
  vp.addEventListener("wheel",(e)=>{ e.preventDefault(); const f=e.deltaY<0?1.08:1/1.08; state.zoom=Math.max(.3,Math.min(2,state.zoom*f)); applyTransform(); },{passive:false});

  setTimeout(()=>{ if(state.zoom===1&&state.panX===0&&state.panY===0) fitGraph(W,H); else applyTransform(); },0);
  return wrap;
}
function fitGraph(W,H){
  const vp=$("#graphVp"); if(!vp)return;
  const pad=28; const s=Math.min((vp.clientWidth-pad)/W,(vp.clientHeight-pad)/H);
  state.zoom=Math.max(.3,Math.min(1,s));
  state.panX=(vp.clientWidth-W*state.zoom)/2;
  state.panY=(vp.clientHeight-H*state.zoom)/2;
  applyTransform();
}
function applyTransform(){
  const layer=$("#graphLayer"); if(!layer)return;
  layer.style.transform=`translate(${state.panX}px,${state.panY}px) scale(${state.zoom})`;
  const zl=$("#zlabel"); if(zl)zl.textContent=Math.round(state.zoom*100)+"%";
}

/* ---- artifact cards ---- */
function badge(status,label){ return `<span class="badge b-${status}"><span class="dot s-${status}"></span>${label}</span>`; }

/* 右栏宽度拖拽 */
function attachResizer(handle){
  let startX, startW, dragging=false;
  const onMove=(e)=>{ if(!dragging)return; const dx=startX-e.clientX; const max=Math.min(680,Math.round(window.innerWidth*0.5)); const w=Math.max(340,Math.min(max,startW+dx)); state.ctxW=w; document.documentElement.style.setProperty("--ctx-w",w+"px"); applyTransform(); };
  const onUp=()=>{ if(!dragging)return; dragging=false; handle.classList.remove("dragging"); document.body.classList.remove("resizing"); try{ localStorage.setItem("studio-ctxw",String(state.ctxW)); }catch(_){ } window.removeEventListener("pointermove",onMove); window.removeEventListener("pointerup",onUp); };
  handle.addEventListener("pointerdown",(e)=>{ e.preventDefault(); dragging=true; startX=e.clientX;
    startW=state.ctxW || parseInt(getComputedStyle(document.documentElement).getPropertyValue("--ctx-w"),10) || 460;
    handle.classList.add("dragging"); document.body.classList.add("resizing");
    window.addEventListener("pointermove",onMove); window.addEventListener("pointerup",onUp); });
}

/* 扁平产物面板（每个 tab 一个） */
function ctxPanel(icon,color,title,metaHtml,bodyNode){
  const p=el("div",{class:"ctx-panel scroll"});
  const head=el("div",{class:"panel-head"});
  head.innerHTML=`<span class="ph-ic" style="color:${color};background:color-mix(in oklab,${color} 15%,transparent);border:1px solid color-mix(in oklab,${color} 30%,transparent)">${icon}</span><span class="ph-title">${title}</span><span class="ph-meta">${metaHtml||""}</span>`;
  p.appendChild(head);
  if(bodyNode) p.appendChild(bodyNode);
  return p;
}

function renderWorkflowPanel(draftStatus){
  const map={ idle:["unknown","未运行"], submitted:["pending","已提交"], running:["running","运行中"], completed:["completed","已完成"], failed:["failed","失败"] };
  const [st,lab]=map[draftStatus]||map.idle;
  const b=el("div",{});
  const c=el("div",{class:"panel-card"});
  c.innerHTML=`
    <div class="kv"><span class="k">名称</span><span class="v">${esc(WF_TECH.name)}</span></div>
    <div class="kv"><span class="k">workflowId</span><span class="v">wf_tech_5c1</span></div>
    <div class="kv"><span class="k">节点 / 边</span><span class="v">${WF_TECH.nodes.length} 节点 · ${WF_TECH.edges.length} 边</span></div>
    <div class="kv"><span class="k">draft-run</span><span class="v plain">${badge(st,lab)}</span></div>`;
  b.appendChild(c);
  if(draftStatus!=="idle") b.appendChild(el("a",{class:"deeplink",href:`${OBS}/run_d3f1a2`},`${ICON.ext}<span>到观测台看本次 draft-run</span>`));
  return ctxPanel(ICON.graph,"var(--cat-ai)","Workflow",badge(st,lab),b);
}
function renderTeamPanel(bindStatus){
  const map={ pending:["pending","绑定中"], bound:["bound","已绑定"], failed:["failed","绑定失败"] };
  const [st,lab]=map[bindStatus]||map.pending;
  const b=el("div",{});
  const c1=el("div",{class:"panel-card"});
  c1.innerHTML=`
    <div class="kv"><span class="k">team</span><span class="v plain" style="color:var(--fg-strong);font-weight:600">${esc(TEAM.displayName)}</span></div>
    <div class="kv"><span class="k">teamId</span><span class="v">${esc(TEAM.teamId)}</span></div>`;
  b.appendChild(c1);
  b.appendChild(el("div",{class:"panel-sub"},`Members · ${TEAM.members.length}`));
  const c2=el("div",{class:"panel-card"});
  TEAM.members.forEach(m=>{
    const isEntry=m.memberId===TEAM.entryMemberId;
    const row=el("div",{class:"member"});
    row.innerHTML=`<span class="mi">${ICON.team}</span>
      <div style="min-width:0;flex:1"><div class="mname">${esc(m.displayName)} ${isEntry&&st==="bound"?'<span class="entry">ENTRY</span>':""}</div><div class="mkind">${esc(m.implementationKind)} · ${esc(m.memberId)}</div></div>
      ${badge(st,lab)}`;
    c2.appendChild(row);
  });
  b.appendChild(c2);
  if(st==="pending") b.appendChild(el("div",{style:"margin-top:10px"},`<span class="poll-tag"><span class="dot"></span>异步绑定中，约几秒完成…</span>`));
  return ctxPanel(ICON.team,"var(--cat-composition)","Team & Member",badge(st,lab),b);
}
function renderServicePanel(nyx){
  const map={ pending:["pending","注册中"], registered:["registered","已注册"], failed:["failed","未注册"] };
  const [st,lab]=map[nyx]||map.pending;
  const b=el("div",{});
  const c=el("div",{class:"panel-card"});
  c.innerHTML=`
    <div class="kv"><span class="k">service slug</span><span class="v">${esc(SERVICE.slug)}</span></div>
    <div class="kv"><span class="k">NyxID</span><span class="v plain">${badge(st,lab)}</span></div>
    <div class="kv"><span class="k">definitionId</span><span class="v">${esc(SERVICE.definitionId)}</span></div>`;
  b.appendChild(c);
  if(st==="pending") b.appendChild(el("div",{style:"margin-top:10px"},`<span class="poll-tag"><span class="dot"></span>NyxID 注册中…</span>`));
  if(st==="registered") b.appendChild(el("a",{class:"deeplink",href:`${OBS}?scope=${SERVICE.scopeId}&definition=${SERVICE.definitionId}`},`${ICON.ext}<span>到观测台看该 service 的运行</span>`));
  if(st==="failed") b.appendChild(el("div",{class:"sched-err",style:"margin-top:10px"},"registry timeout (504) — 将自动重试"));
  return ctxPanel(ICON.svc,"var(--cat-integration)","Service",badge(st,lab),b);
}
function renderSchedulesPanel(mode){
  const list = mode==="fresh"
    ? [ {...SCHEDULES_FULL[0], fireCount:0, failureCount:0, lastFireAt:"", lastError:""} ]
    : SCHEDULES_FULL;
  const b=el("div",{});
  list.forEach(s=>{
    const sc=el("div",{class:"sched"});
    const hasFail = s.failureCount>0;
    sc.innerHTML=`
      <div class="sched-top"><span class="dot s-${s.enabled?(hasFail?"failed":"completed"):"stopped"}"></span><span class="sname">${esc(s.displayName)}</span><span class="sched-cron">${esc(s.cronExpression)}</span></div>
      <div class="sched-grid">
        <div class="sg"><span class="gk">timezone</span><span class="gv">${esc(s.timezone)}</span></div>
        <div class="sg"><span class="gk">enabled</span><span class="gv">${s.enabled?"true":"false"}</span></div>
        <div class="sg"><span class="gk">下次触发</span><span class="gv">${s.nextFireAt?relFuture(s.nextFireAt):"—"}</span></div>
        <div class="sg"><span class="gk">上次触发</span><span class="gv">${s.lastFireAt?relPast(s.lastFireAt):"—"}</span></div>
        <div class="sg"><span class="gk">fireCount</span><span class="gv">${s.fireCount}</span></div>
        <div class="sg"><span class="gk">failureCount</span><span class="gv ${hasFail?"bad":""}">${s.failureCount}</span></div>
      </div>
      ${s.lastError?`<div class="sched-err">lastError: ${esc(s.lastError)}</div>`:""}
      <a class="deeplink" style="margin-top:9px" href="${OBS}?scope=${SERVICE.scopeId}&definition=${SERVICE.definitionId}">${ICON.ext}<span>到观测台看触发运行</span></a>`;
    b.appendChild(sc);
  });
  return ctxPanel(ICON.clock,"var(--warn)","Schedules",`<span class="poll-tag"><span class="dot"></span>实时</span>`,b);
}

/* ===========================================================================
   Compose / render
   =========================================================================== */
/* ===========================================================================
   Login screen (mirrors observatory)
   =========================================================================== */
function renderLogin(){
  const wrap = el("main", { class:"login" });
  const card = el("section", { class:"login-card", role:"region", "aria-label":"登录" });
  card.innerHTML = `
    <div class="login-mark" aria-hidden="true">${ICON.lock}</div>
    <h1>登录以进入工作室</h1>
    <p>通过对话装配、发布与编排工作流 —— 登录后即可开始。</p>`;
  const btn = el("button", { class:"btn-primary", id:"signinBtn" }, "使用 nyxid 登录");
  btn.addEventListener("click", () => { beginLogin(); });
  card.appendChild(btn);
  card.appendChild(el("div", { class:"login-foot" }, `${ICON.lock.replace('width="24" height="24"','width="12" height="12"')}<span>采用 OIDC bearer-token 鉴权 · 通过 nyxid 账户登录</span>`));
  wrap.appendChild(card);
  return wrap;
}

function render(){
  const app=$("#app"); app.innerHTML="";
  if(!state.signedIn){ app.appendChild(renderTopbar()); app.appendChild(renderLogin()); return; }
  if(state.ctxW) document.documentElement.style.setProperty("--ctx-w", state.ctxW+"px");
  app.appendChild(renderTopbar());
  const shell=el("div",{class:"shell"});
  shell.appendChild(renderSessions());
  shell.appendChild(renderChat());
  shell.appendChild(renderContext());
  app.appendChild(shell);

  // mobile scrims + toggles
  const navBtn=$("#navBtn"); if(navBtn) navBtn.addEventListener("click",()=>{ document.body.classList.toggle("nav-open"); ensureScrim(); });

  // restore textarea height
  const ta=$("#composerInput"); if(ta){ ta.style.height="auto"; ta.style.height=Math.min(ta.scrollHeight,140)+"px"; }

  // streaming
  if(scn().streamLast) setTimeout(startStream,30);
  // scroll chat to bottom on populated states
  const sc=$("#chatScroll"); if(sc && !scn().streamLast) sc.scrollTop=sc.scrollHeight;
}
function ensureScrim(){
  let sc=$(".scrim");
  if(document.body.classList.contains("nav-open")||document.body.classList.contains("ctx-open")){
    if(!sc){ sc=el("div",{class:"scrim"}); sc.addEventListener("click",()=>{document.body.classList.remove("nav-open","ctx-open"); sc.remove();}); document.body.appendChild(sc); }
  } else if(sc) sc.remove();
}

/* init */
applyTheme();
if(state.ctxW) document.documentElement.style.setProperty("--ctx-w", state.ctxW+"px");
// 窄屏打开右栏上下文的浮动按钮（桌面端隐藏，CSS 控制）
const ctxToggleBtn=el("button",{class:"ctx-toggle","aria-label":"打开当前会话上下文"},ICON.ctx);
ctxToggleBtn.addEventListener("click",()=>{ document.body.classList.toggle("ctx-open"); ensureScrim(); });
document.body.appendChild(ctxToggleBtn);
(async function init(){
  try { await completeLoginIfCallback(); }
  catch(e){ console.warn("studio: login callback failed", e); }
  if(!getToken()){ state.signedIn = false; render(); return; }
  state.signedIn = true;
  render();
  // The chat/cards render from mock constants this increment; only the account label
  // is upgraded from the signed-in identity once available.
  const acct = toAccount(await fetchUserInfo());
  if(acct){ accountLabel = acct.label; render(); }
})();
if(window.matchMedia){ window.matchMedia("(prefers-color-scheme: light)").addEventListener?.("change",()=>{ if(!state.theme)render(); }); }
window.addEventListener("keydown",(e)=>{ if(e.key==="Enter"&&!e.shiftKey&&document.activeElement&&document.activeElement.id==="composerInput"){ e.preventDefault(); /* 视觉稿：发送为占位，不真正改对话状态 */ } });
let _rw; window.addEventListener("resize",()=>{ clearTimeout(_rw); _rw=setTimeout(()=>{ const W=1700,H=380; if($("#graphVp"))fitGraph(W,H); },160); });
</script>
</body>
</html>

""";
}
