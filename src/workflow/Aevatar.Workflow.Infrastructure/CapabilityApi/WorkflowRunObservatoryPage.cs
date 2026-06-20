namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

// 06-19-workflow-run-observatory (C2): one inline self-contained HTML page (no wwwroot, no build step;
// mirrors the /status precedent). Browser OIDC Authorization Code + PKCE against nyxid (reuse of the
// console-web client; no server-side cookie/session, no NyxID change). Read-only: no edit/run/stop
// controls anywhere. CSS variables + dark/light, responsive, keyboard-accessible. Near-live via polling
// (~3s, paused when the tab is hidden); live and history share the one read path.
internal static class WorkflowRunObservatoryPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
<title>Workflow Run Observatory · 工作流运行观测台</title>
<style>
  /* =========================================================================
     Workflow Run Observatory — 只读运行观测台
     单文件、零外部依赖。深色为主，浅色全量支持。
     所有颜色走 CSS 自定义属性（设计令牌），不散落硬编码。
     ========================================================================= */

  /* ---- Design tokens : 深色（主） --------------------------------------- */
  :root {
    --bg:        #0f1115;
    --bg-grad:   radial-gradient(1200px 600px at 78% -10%, #161b26 0%, #0f1115 60%);
    --panel:     #171a21;
    --panel-2:   #1f232c;
    --panel-3:   #252b36;
    --border:    #2a2f3a;
    --border-soft:#22272f;
    --fg:        #e6e9ef;
    --fg-strong: #f4f6fb;
    --muted:     #9aa3b2;
    --muted-2:   #6c7585;
    --accent:    #5b8cff;
    --accent-ink:#0b1020;
    --accent-soft: rgba(91,140,255,.14);
    --accent-line: rgba(91,140,255,.40);

    --ok:    #3fb950;  --ok-soft:   rgba(63,185,80,.14);
    --warn:  #d29922;  --warn-soft: rgba(210,153,34,.15);
    --err:   #f85149;  --err-soft:  rgba(248,81,73,.14);
    --run:   #58a6ff;  --run-soft:  rgba(88,166,255,.16);
    --neutral:#9aa3b2; --neutral-soft: rgba(154,163,178,.12);

    --shadow: 0 8px 30px rgba(0,0,0,.40);
    --shadow-sm: 0 1px 2px rgba(0,0,0,.30);
    --ring: 0 0 0 2px var(--bg), 0 0 0 4px var(--accent);

    --r-sm: 6px; --r: 9px; --r-lg: 13px; --r-pill: 999px;
    --mono: ui-monospace, "SF Mono", SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace;
    --sans: -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, "PingFang SC",
            "Hiragino Sans GB", "Microsoft YaHei", "Noto Sans CJK SC", sans-serif;

    --list-w: 320px;
    --topbar-h: 56px;
    color-scheme: dark;
  }

  /* ---- Design tokens : 浅色 --------------------------------------------- */
  @media (prefers-color-scheme: light) {
    :root:not([data-theme]) { --auto-light: 1; }
  }
  :root[data-theme="light"], :root:not([data-theme]):has(#noop) { }

  /* light palette applied either by system pref (no manual theme) or manual toggle */
  @media (prefers-color-scheme: light) {
    :root:not([data-theme]) {
      --bg:        #f5f7fa;
      --bg-grad:   radial-gradient(1100px 560px at 80% -12%, #eef2f8 0%, #f5f7fa 58%);
      --panel:     #ffffff;
      --panel-2:   #f3f5f9;
      --panel-3:   #eaeef4;
      --border:    #dde2ea;
      --border-soft:#e7ebf1;
      --fg:        #1c2128;
      --fg-strong: #0b0f14;
      --muted:     #57606b;
      --muted-2:   #828b97;
      --accent:    #2f63e0;
      --accent-ink:#ffffff;
      --accent-soft: rgba(47,99,224,.10);
      --accent-line: rgba(47,99,224,.34);
      --ok:#1a7f37; --ok-soft:rgba(26,127,55,.12);
      --warn:#9a6700; --warn-soft:rgba(154,103,0,.12);
      --err:#cf222e; --err-soft:rgba(207,34,46,.10);
      --run:#2f6fed; --run-soft:rgba(47,111,237,.12);
      --neutral:#57606b; --neutral-soft:rgba(87,96,107,.10);
      --shadow: 0 10px 30px rgba(16,24,40,.10);
      --shadow-sm: 0 1px 2px rgba(16,24,40,.06);
      color-scheme: light;
    }
  }
  :root[data-theme="light"] {
    --bg:        #f5f7fa;
    --bg-grad:   radial-gradient(1100px 560px at 80% -12%, #eef2f8 0%, #f5f7fa 58%);
    --panel:     #ffffff;
    --panel-2:   #f3f5f9;
    --panel-3:   #eaeef4;
    --border:    #dde2ea;
    --border-soft:#e7ebf1;
    --fg:        #1c2128;
    --fg-strong: #0b0f14;
    --muted:     #57606b;
    --muted-2:   #828b97;
    --accent:    #2f63e0;
    --accent-ink:#ffffff;
    --accent-soft: rgba(47,99,224,.10);
    --accent-line: rgba(47,99,224,.34);
    --ok:#1a7f37; --ok-soft:rgba(26,127,55,.12);
    --warn:#9a6700; --warn-soft:rgba(154,103,0,.12);
    --err:#cf222e; --err-soft:rgba(207,34,46,.10);
    --run:#2f6fed; --run-soft:rgba(47,111,237,.12);
    --neutral:#57606b; --neutral-soft:rgba(87,96,107,.10);
    --shadow: 0 10px 30px rgba(16,24,40,.10);
    --shadow-sm: 0 1px 2px rgba(16,24,40,.06);
    color-scheme: light;
  }

  /* ---- Reset / base ----------------------------------------------------- */
  * { box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    margin: 0;
    background: var(--bg);
    background-image: var(--bg-grad);
    background-attachment: fixed;
    color: var(--fg);
    font: 14px/1.55 var(--sans);
    -webkit-font-smoothing: antialiased;
    text-rendering: optimizeLegibility;
  }
  ::selection { background: var(--accent-soft); }
  button { font: inherit; color: inherit; cursor: pointer; }
  :focus-visible {
    outline: none;
    box-shadow: var(--ring);
    border-radius: var(--r-sm);
  }
  .mono { font-family: var(--mono); font-variant-numeric: tabular-nums; }
  .scroll::-webkit-scrollbar { width: 10px; height: 10px; }
  .scroll::-webkit-scrollbar-thumb { background: var(--panel-3); border-radius: 999px; border: 2px solid transparent; background-clip: content-box; }
  .scroll::-webkit-scrollbar-track { background: transparent; }

  /* ---- Status primitives ------------------------------------------------ */
  .dot { width: 9px; height: 9px; border-radius: 50%; flex: 0 0 auto; box-shadow: 0 0 0 3px transparent; }
  .dot.s-running   { background: var(--run);  box-shadow: 0 0 0 0 var(--run); animation: pulse 2.4s ease-out infinite; }
  .dot.s-completed { background: var(--ok); }
  .dot.s-failed, .dot.s-timed_out { background: var(--err); }
  .dot.s-stopped, .dot.s-disabled { background: var(--warn); }
  .dot.s-unknown, .dot.s-not_found { background: var(--neutral); }
  @keyframes pulse {
    0%   { box-shadow: 0 0 0 0 var(--run-soft); }
    70%  { box-shadow: 0 0 0 7px transparent; }
    100% { box-shadow: 0 0 0 0 transparent; }
  }

  .badge {
    display: inline-flex; align-items: center; gap: 6px;
    padding: 3px 9px; border-radius: var(--r-pill);
    font-size: 12px; font-weight: 600; letter-spacing: .01em;
    border: 1px solid transparent; white-space: nowrap;
  }
  .badge .dot { width: 7px; height: 7px; }
  .b-running   { color: var(--run);  background: var(--run-soft);  border-color: color-mix(in oklab, var(--run) 30%, transparent); }
  .b-completed { color: var(--ok);   background: var(--ok-soft);   border-color: color-mix(in oklab, var(--ok) 30%, transparent); }
  .b-failed, .b-timed_out { color: var(--err); background: var(--err-soft); border-color: color-mix(in oklab, var(--err) 30%, transparent); }
  .b-stopped, .b-disabled { color: var(--warn); background: var(--warn-soft); border-color: color-mix(in oklab, var(--warn) 30%, transparent); }
  .b-unknown, .b-not_found { color: var(--muted); background: var(--neutral-soft); border-color: var(--border); }

  /* =========================================================================
     Top bar
     ========================================================================= */
  .topbar {
    position: sticky; top: 0; z-index: 40;
    height: var(--topbar-h);
    display: flex; align-items: center; gap: 12px;
    padding: 0 16px;
    background: color-mix(in oklab, var(--panel) 88%, transparent);
    backdrop-filter: saturate(140%) blur(12px);
    -webkit-backdrop-filter: saturate(140%) blur(12px);
    border-bottom: 1px solid var(--border);
  }
  .brand { display: flex; align-items: center; gap: 10px; min-width: 0; }
  .brand-mark {
    width: 26px; height: 26px; border-radius: 7px; flex: 0 0 auto;
    background: linear-gradient(150deg, var(--accent), color-mix(in oklab, var(--accent) 55%, #7c4dff));
    display: grid; place-items: center; color: #fff; box-shadow: var(--shadow-sm);
  }
  .brand-name { font-weight: 650; letter-spacing: -.01em; color: var(--fg-strong); white-space: nowrap; }
  .brand-sub { color: var(--muted-2); font-size: 12px; white-space: nowrap; }
  .pill-ro {
    font-size: 11px; font-weight: 650; letter-spacing: .04em;
    padding: 2px 8px; border-radius: var(--r-pill);
    color: var(--muted); background: var(--neutral-soft); border: 1px solid var(--border);
    text-transform: uppercase;
  }
  .spacer { flex: 1 1 auto; }

  .live {
    display: inline-flex; align-items: center; gap: 8px;
    padding: 5px 11px 5px 9px; border-radius: var(--r-pill);
    background: var(--panel-2); border: 1px solid var(--border);
    font-size: 12px; color: var(--muted);
  }
  .live .dot { width: 7px; height: 7px; background: var(--ok); }
  .live.is-live .dot { background: var(--run); animation: pulse 2.4s ease-out infinite; }
  .live .freshness { font-family: var(--mono); font-size: 11.5px; color: var(--fg); }
  .live.is-polling { border-color: var(--accent-line); }

  .iconbtn {
    width: 34px; height: 34px; display: grid; place-items: center;
    background: var(--panel-2); border: 1px solid var(--border); border-radius: var(--r);
    color: var(--muted); transition: color .15s, border-color .15s, background .15s;
  }
  .iconbtn:hover { color: var(--fg); border-color: var(--muted-2); }

  .account {
    display: inline-flex; align-items: center; gap: 8px;
    padding: 4px 6px 4px 4px; border-radius: var(--r-pill);
    background: var(--panel-2); border: 1px solid var(--border);
  }
  .avatar {
    width: 24px; height: 24px; border-radius: 50%; flex: 0 0 auto;
    background: linear-gradient(140deg, #6f9bff, #9b6bff); color: #fff;
    display: grid; place-items: center; font-size: 12px; font-weight: 700;
  }
  .account .who { font-size: 12.5px; color: var(--fg); max-width: 150px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .linkbtn {
    background: transparent; border: 0; color: var(--accent); font-size: 12.5px; font-weight: 600;
    padding: 4px 6px; border-radius: var(--r-sm);
  }
  .linkbtn:hover { text-decoration: underline; }

  /* =========================================================================
     Error banner (role=alert)
     ========================================================================= */
  .banner {
    display: flex; align-items: center; gap: 10px;
    margin: 10px 16px 0; padding: 10px 12px;
    background: var(--err-soft); border: 1px solid color-mix(in oklab, var(--err) 36%, transparent);
    border-radius: var(--r); color: var(--fg);
    font-size: 13px;
  }
  .banner svg { color: var(--err); flex: 0 0 auto; }
  .banner .b-title { font-weight: 650; color: var(--err); }
  .banner .b-msg { color: var(--muted); }

  /* =========================================================================
     Workspace shell (master / detail)
     ========================================================================= */
  .shell {
    display: grid;
    grid-template-columns: var(--list-w) 1fr;
    height: calc(100dvh - var(--topbar-h));
    min-height: 0;
  }
  .shell.has-banner { height: auto; min-height: calc(100dvh - var(--topbar-h)); }

  /* ---- Runs list -------------------------------------------------------- */
  .list-pane {
    border-right: 1px solid var(--border);
    display: flex; flex-direction: column; min-height: 0;
    background: color-mix(in oklab, var(--panel) 40%, transparent);
  }
  .list-head { padding: 14px 16px 10px; border-bottom: 1px solid var(--border-soft); }
  .list-title-row { display: flex; align-items: baseline; justify-content: space-between; }
  .list-title { font-size: 13px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; color: var(--muted); }
  .list-count { font-family: var(--mono); font-size: 12px; color: var(--muted-2); }
  .filters { display: flex; gap: 6px; margin-top: 11px; flex-wrap: wrap; }
  .chip {
    padding: 4px 10px; border-radius: var(--r-pill); font-size: 12px; font-weight: 550;
    background: transparent; border: 1px solid var(--border); color: var(--muted);
    transition: color .12s, background .12s, border-color .12s;
  }
  .chip:hover { color: var(--fg); border-color: var(--muted-2); }
  .chip[aria-pressed="true"] { color: var(--fg-strong); background: var(--panel-3); border-color: var(--muted-2); }

  .runlist { overflow-y: auto; flex: 1 1 auto; padding: 8px; min-height: 0; }
  .run-row {
    display: grid; grid-template-columns: auto 1fr; gap: 2px 11px;
    align-items: start; width: 100%; text-align: left;
    padding: 11px 12px; border-radius: var(--r); border: 1px solid transparent;
    background: transparent; color: inherit; margin-bottom: 2px;
    transition: background .12s, border-color .12s;
  }
  .run-row:hover { background: var(--panel-2); }
  .run-row .dot { margin-top: 5px; grid-row: span 2; }
  .run-row .rn { font-weight: 640; color: var(--fg-strong); letter-spacing: -.005em; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .run-row .rm { font-size: 12px; color: var(--muted); display: flex; gap: 7px; align-items: center; flex-wrap: wrap; }
  .run-row .rm .id { font-family: var(--mono); font-size: 11.5px; color: var(--muted-2); }
  .run-row .rm .sep { color: var(--border); }
  .run-row[aria-current="true"] {
    background: var(--accent-soft);
    border-color: var(--accent-line);
    box-shadow: inset 3px 0 0 var(--accent);
  }
  .run-row[aria-current="true"] .rn { color: var(--fg-strong); }

  /* skeleton */
  .sk { background: linear-gradient(100deg, var(--panel-2) 30%, var(--panel-3) 50%, var(--panel-2) 70%); background-size: 200% 100%; animation: shimmer 1.3s linear infinite; border-radius: 5px; }
  @keyframes shimmer { to { background-position: -200% 0; } }
  .sk-row { padding: 13px 12px; display: grid; grid-template-columns: auto 1fr; gap: 8px 11px; }
  .sk-dot { width: 9px; height: 9px; border-radius: 50%; margin-top: 4px; }
  .sk-line { height: 10px; }

  .empty {
    display: flex; flex-direction: column; align-items: center; justify-content: center;
    gap: 10px; text-align: center; color: var(--muted); padding: 48px 24px; height: 100%;
  }
  .empty .ic { width: 40px; height: 40px; color: var(--muted-2); opacity: .8; }
  .empty .et { font-weight: 600; color: var(--fg); }
  .empty .es { font-size: 12.5px; max-width: 220px; }

  /* =========================================================================
     Run detail
     ========================================================================= */
  .detail-pane { overflow-y: auto; min-height: 0; display: flex; flex-direction: column; }
  .detail-inner { max-width: 980px; width: 100%; margin: 0 auto; padding: 22px 26px 64px; flex: 1 1 auto; }

  .backbar { display: none; }

  .run-header { display: flex; flex-direction: column; gap: 13px; }
  .rh-top { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
  .rh-title { font-size: 21px; font-weight: 700; letter-spacing: -.02em; color: var(--fg-strong); display: flex; align-items: center; gap: 10px; margin: 0; }
  .rh-title .dot { width: 11px; height: 11px; }
  .rh-meta { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; color: var(--muted); font-size: 12.5px; }
  .rh-meta .k { color: var(--muted-2); }
  .rh-meta .v { font-family: var(--mono); color: var(--fg); }
  .rh-meta .grp { display: inline-flex; gap: 6px; align-items: baseline; }

  .usage-strip {
    display: flex; gap: 0; flex-wrap: wrap;
    border: 1px solid var(--border); border-radius: var(--r-lg); overflow: hidden;
    background: var(--panel);
  }
  .usage-cell { flex: 1 1 auto; min-width: 110px; padding: 12px 16px; border-right: 1px solid var(--border-soft); }
  .usage-cell:last-child { border-right: 0; }
  .usage-cell .ul { font-size: 11px; color: var(--muted-2); letter-spacing: .04em; text-transform: uppercase; }
  .usage-cell .uv { font-family: var(--mono); font-size: 16px; font-weight: 600; color: var(--fg-strong); margin-top: 3px; font-variant-numeric: tabular-nums; }
  .usage-cell .uv .unit { font-size: 11px; color: var(--muted); font-weight: 500; margin-left: 2px; }
  .usage-cell.accent .uv { color: var(--accent); }

  /* ---- Tabs ------------------------------------------------------------- */
  .tabs { display: inline-flex; gap: 3px; padding: 3px; margin: 20px 0 0; background: var(--panel-2); border: 1px solid var(--border); border-radius: var(--r-pill); }
  .tab {
    padding: 6px 16px; border-radius: var(--r-pill); border: 0; background: transparent;
    color: var(--muted); font-size: 13px; font-weight: 600; transition: color .15s, background .15s;
    display: inline-flex; align-items: center; gap: 7px;
  }
  .tab:hover { color: var(--fg); }
  .tab[aria-selected="true"] { background: var(--panel); color: var(--fg-strong); box-shadow: var(--shadow-sm); }
  .tabpanel { margin-top: 18px; }
  .tabpanel[hidden] { display: none; }

  /* =========================================================================
     Timeline — the centerpiece
     ========================================================================= */
  .timeline { position: relative; padding-left: 0; }
  .tl-rail-note { font-size: 11.5px; color: var(--muted-2); margin: 0 0 12px 2px; }

  .ev {
    position: relative;
    display: grid;
    grid-template-columns: 132px 1fr;
    gap: 14px;
    padding: 0 0 4px;
  }
  .ev .gutter { position: relative; display: flex; justify-content: flex-end; padding-top: 2px; }
  .ev .ts { font-family: var(--mono); font-size: 11.5px; color: var(--muted-2); white-space: nowrap; }
  .ev .node-rail { position: relative; padding-left: 22px; padding-bottom: 16px; }
  /* connecting line */
  .ev .node-rail::before {
    content: ""; position: absolute; left: 5px; top: 2px; bottom: -2px; width: 2px;
    background: var(--border);
  }
  .ev:last-child .node-rail::before { bottom: auto; height: 18px; }
  .ev .marker {
    position: absolute; left: 0; top: 2px; width: 12px; height: 12px; border-radius: 50%;
    background: var(--panel); border: 2px solid var(--neutral); z-index: 1;
  }
  .ev.k-RunStarted .marker, .ev.k-RunFinished .marker { border-color: var(--ok); background: var(--ok-soft); }
  .ev.k-RunError .marker, .ev.k-RunStopped .marker { border-color: var(--err); background: var(--err-soft); }
  .ev.k-RunStopped .marker { border-color: var(--warn); background: var(--warn-soft); }
  .ev.k-ToolCall .marker { border-color: var(--accent); background: var(--accent-soft); }
  .ev.k-HumanInputRequest .marker { border-color: var(--warn); background: var(--warn-soft); }
  .ev.k-TextMessage .marker { border-color: var(--run); background: var(--run-soft); }
  .ev.k-StepStarted .marker, .ev.k-StepFinished .marker { border-color: var(--muted-2); }

  .ev-head { display: flex; align-items: center; gap: 9px; flex-wrap: wrap; min-height: 18px; }
  .kind {
    display: inline-flex; align-items: center; gap: 6px;
    font-size: 11.5px; font-weight: 650; padding: 2px 9px; border-radius: var(--r-pill);
    border: 1px solid transparent; letter-spacing: .005em;
  }
  .kind svg { width: 13px; height: 13px; }
  .k-RunStarted .kind, .k-RunFinished .kind { color: var(--ok); background: var(--ok-soft); border-color: color-mix(in oklab, var(--ok) 28%, transparent); }
  .k-RunError .kind { color: var(--err); background: var(--err-soft); border-color: color-mix(in oklab, var(--err) 38%, transparent); }
  .k-RunStopped .kind { color: var(--warn); background: var(--warn-soft); border-color: color-mix(in oklab, var(--warn) 32%, transparent); }
  .k-StepStarted .kind, .k-StepFinished .kind { color: var(--muted); background: var(--neutral-soft); border-color: var(--border); }
  .k-TextMessage .kind { color: var(--run); background: var(--run-soft); border-color: color-mix(in oklab, var(--run) 28%, transparent); }
  .k-ToolCall .kind { color: var(--accent); background: var(--accent-soft); border-color: var(--accent-line); }
  .k-HumanInputRequest .kind { color: var(--warn); background: var(--warn-soft); border-color: color-mix(in oklab, var(--warn) 38%, transparent); }

  .stepbadge {
    font-family: var(--mono); font-size: 11px; color: var(--muted);
    padding: 1px 7px; border-radius: 5px; background: var(--panel-2); border: 1px solid var(--border);
  }
  .stepbadge .st { color: var(--muted-2); }
  .agentbadge {
    font-size: 11px; color: var(--muted); padding: 1px 7px; border-radius: 5px;
    background: var(--neutral-soft); border: 1px solid var(--border-soft);
  }
  .ev-msg { color: var(--fg); font-size: 13.5px; margin-top: 5px; }
  .ev-cost { margin-top: 5px; }
  .costchip { font-family: var(--mono); font-size: 12px; color: var(--muted); }
  .costchip b { color: var(--fg); font-weight: 600; }

  /* TextMessage bubble */
  .bubble {
    margin-top: 7px; padding: 11px 14px; border-radius: 4px 12px 12px 12px;
    background: var(--panel); border: 1px solid var(--border);
    border-left: 2px solid var(--run);
    color: var(--fg); font-size: 13.5px; line-height: 1.6; max-width: 62ch;
  }

  /* HumanInputRequest highlight */
  .needs-attn {
    margin-top: 7px; padding: 12px 14px; border-radius: var(--r);
    background: var(--warn-soft); border: 1px solid color-mix(in oklab, var(--warn) 40%, transparent);
    display: flex; gap: 11px; align-items: flex-start;
  }
  .needs-attn svg { color: var(--warn); flex: 0 0 auto; margin-top: 1px; }
  .needs-attn .na-l { font-size: 11px; font-weight: 700; letter-spacing: .05em; text-transform: uppercase; color: var(--warn); }
  .needs-attn .na-m { color: var(--fg); font-size: 13.5px; margin-top: 2px; }
  .needs-attn .na-ro { font-size: 11.5px; color: var(--muted); margin-top: 6px; display: inline-flex; align-items: center; gap: 5px; }

  /* ToolCall block */
  .toolcall { margin-top: 8px; border: 1px solid var(--border); border-radius: var(--r); background: var(--panel); overflow: hidden; max-width: 100%; }
  .tc-head {
    display: flex; align-items: center; gap: 10px; width: 100%; text-align: left;
    padding: 10px 12px; background: var(--panel); border: 0; color: inherit;
  }
  .tc-head:hover { background: var(--panel-2); }
  .tc-chevron { color: var(--muted); transition: transform .18s ease; flex: 0 0 auto; }
  .toolcall[data-open="true"] .tc-chevron { transform: rotate(90deg); }
  .tc-name { font-family: var(--mono); font-size: 13px; color: var(--fg-strong); font-weight: 600; }
  .tc-callid { font-family: var(--mono); font-size: 11px; color: var(--muted-2); }
  .tc-status { margin-left: auto; display: inline-flex; align-items: center; gap: 5px; font-size: 12px; font-weight: 600; }
  .tc-status.ok { color: var(--ok); }
  .tc-status.bad { color: var(--err); }
  .tc-body { display: grid; gap: 12px; padding: 0 12px 12px; }
  .toolcall[data-open="false"] .tc-body { display: none; }
  .tc-field { }
  .tc-field-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 5px; }
  .tc-field-label { font-size: 11px; letter-spacing: .05em; text-transform: uppercase; color: var(--muted-2); font-weight: 600; }
  .copybtn {
    display: inline-flex; align-items: center; gap: 5px; font-size: 11px; color: var(--muted);
    background: var(--panel-2); border: 1px solid var(--border); border-radius: var(--r-sm); padding: 2px 8px;
  }
  .copybtn:hover { color: var(--fg); border-color: var(--muted-2); }
  .copybtn.done { color: var(--ok); border-color: color-mix(in oklab, var(--ok) 40%, transparent); }
  pre.json {
    margin: 0; padding: 11px 12px; max-height: 220px; overflow: auto;
    background: var(--bg); border: 1px solid var(--border-soft); border-radius: var(--r-sm);
    font-family: var(--mono); font-size: 12px; line-height: 1.55; color: var(--fg);
    white-space: pre; tab-size: 2;
  }
  .tc-error { color: var(--err); font-family: var(--mono); font-size: 12px; }
  /* json syntax tint */
  .j-key { color: var(--accent); } .j-str { color: var(--ok); } .j-num { color: var(--warn); } .j-bool { color: var(--run); } .j-null { color: var(--muted); }
  :root[data-theme="light"] .j-str { color:#0a7b34; } :root[data-theme="light"] .j-key { color:#2f63e0; }

  /* newly arrived event highlight */
  @keyframes arrive { 0% { background: var(--accent-soft); } 100% { background: transparent; } }
  .ev.is-new .node-rail > .ev-body { animation: arrive 2.2s ease-out 1; border-radius: var(--r); }

  /* tail (running) live cue */
  .tl-tail { display: flex; align-items: center; gap: 9px; margin: 4px 0 0 154px; color: var(--muted); font-size: 12px; }
  .tl-tail .dot { width: 7px; height: 7px; background: var(--run); animation: pulse 2.4s ease-out infinite; }

  /* =========================================================================
     Graph (DAG)
     ========================================================================= */
  .graph-wrap {
    border: 1px solid var(--border); border-radius: var(--r-lg);
    background:
      linear-gradient(0deg, transparent 0, transparent 23px, var(--border-soft) 23px, var(--border-soft) 24px),
      linear-gradient(90deg, transparent 0, transparent 23px, var(--border-soft) 23px, var(--border-soft) 24px);
    background-size: 24px 24px; background-color: var(--panel);
    overflow: hidden; position: relative; height: 480px; max-height: 70dvh;
    touch-action: none; user-select: none; cursor: grab;
  }
  .graph-wrap.is-panning { cursor: grabbing; }
  .graph-viewport { position: absolute; top: 0; left: 0; transform-origin: 0 0; will-change: transform; }
  .graph-canvas { position: relative; }
  .graph-canvas svg { position: absolute; inset: 0; overflow: visible; pointer-events: none; }

  /* zoom / fit controls (overlay) */
  .graph-controls {
    position: absolute; top: 10px; right: 10px; z-index: 7;
    display: flex; flex-direction: column; gap: 4px; padding: 4px;
    background: color-mix(in oklab, var(--panel) 86%, transparent);
    border: 1px solid var(--border); border-radius: var(--r);
    backdrop-filter: blur(8px); -webkit-backdrop-filter: blur(8px);
  }
  .graph-controls button {
    width: 30px; height: 30px; display: grid; place-items: center;
    background: var(--panel-2); border: 1px solid var(--border); border-radius: var(--r-sm); color: var(--muted);
  }
  .graph-controls button:hover { color: var(--fg); border-color: var(--muted-2); }
  .graph-zoom-label { text-align: center; font-family: var(--mono); font-size: 10px; color: var(--muted-2); }

  .gnode {
    position: absolute; width: 244px; box-sizing: border-box; text-align: left;
    background: var(--panel-2); border: 1.5px solid var(--border); border-radius: var(--r);
    padding: 12px 14px; box-shadow: var(--shadow-sm); cursor: pointer; color: inherit; font: inherit;
    transition: border-color .12s, box-shadow .12s, transform .12s;
  }
  .gnode:hover { border-color: var(--muted-2); transform: translateY(-1px); }
  .gnode .gn-top { display: flex; align-items: center; gap: 10px; }
  .gnode .gn-ic { width: 30px; height: 30px; border-radius: 8px; display: grid; place-items: center; background: var(--panel-3); color: var(--muted); flex: 0 0 auto; }
  .gnode .gn-main { min-width: 0; flex: 1 1 auto; }
  .gnode .gn-id { font-family: var(--mono); font-size: 13px; font-weight: 600; color: var(--fg-strong); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .gnode .gn-type { font-family: var(--mono); font-size: 11px; color: var(--muted-2); margin-top: 2px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .gnode .gn-foot { margin-top: 11px; display: flex; align-items: center; gap: 6px; font-size: 11px; }
  /* category tint on the icon chip only — status border/color stays authoritative */
  .gnode[data-cat="data"]        .gn-ic { color: var(--run);  background: var(--run-soft); }
  .gnode[data-cat="control"]     .gn-ic { color: #a78bfa; background: rgba(167,139,250,.16); }
  .gnode[data-cat="ai"]          .gn-ic { color: #f472b6; background: rgba(244,114,182,.16); }
  .gnode[data-cat="composition"] .gn-ic { color: var(--warn); background: var(--warn-soft); }
  .gnode[data-cat="integration"] .gn-ic { color: var(--ok);  background: var(--ok-soft); }
  .gnode[data-cat="human"]       .gn-ic { color: #22d3ee; background: rgba(34,211,238,.16); }
  .gnode.st-done   { border-color: color-mix(in oklab, var(--ok) 45%, var(--border)); }
  .gnode.st-done .gn-ic { color: var(--ok); background: var(--ok-soft); }
  .gnode.st-current{ border-color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }
  .gnode.st-current .gn-ic { color: var(--accent); background: var(--accent-soft); }
  .gnode.st-failed { border-color: color-mix(in oklab, var(--err) 50%, var(--border)); }
  .gnode.st-failed .gn-ic { color: var(--err); background: var(--err-soft); }
  .gnode.st-stopped{ border-color: color-mix(in oklab, var(--warn) 50%, var(--border)); }
  .gnode.st-stopped .gn-ic { color: var(--warn); background: var(--warn-soft); }
  .gnode.st-pending{ opacity: .72; border-style: dashed; }
  .gnode.selected { border-color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft), var(--shadow); }
  .gn-state { font-size: 11px; font-weight: 600; }
  .gnode.st-done .gn-state { color: var(--ok); } .gnode.st-current .gn-state { color: var(--accent); }
  .gnode.st-failed .gn-state { color: var(--err); } .gnode.st-stopped .gn-state { color: var(--warn); }
  .gnode.st-pending .gn-state { color: var(--muted-2); }
  .edge-label {
    position: absolute; transform: translate(-50%,-50%);
    font-family: var(--mono); font-size: 10.5px; color: var(--muted);
    background: var(--panel); border: 1px solid var(--border); border-radius: var(--r-pill);
    padding: 1px 7px; white-space: nowrap;
  }

  /* node detail drawer (right on desktop, bottom sheet on mobile) */
  .gnode-detail {
    position: absolute; top: 8px; right: 8px; bottom: 8px; width: 324px; max-width: calc(100% - 16px);
    background: var(--panel); border: 1px solid var(--border); border-radius: var(--r);
    box-shadow: var(--shadow); display: flex; flex-direction: column; overflow: hidden; z-index: 8;
  }
  .gnode-detail .nd-head { display: flex; align-items: center; gap: 9px; padding: 12px; border-bottom: 1px solid var(--border-soft); }
  .gnode-detail .nd-ic { width: 28px; height: 28px; border-radius: 7px; display: grid; place-items: center; background: var(--panel-3); color: var(--muted); flex: 0 0 auto; }
  .gnode-detail .nd-id { font-family: var(--mono); font-size: 13px; font-weight: 650; color: var(--fg-strong); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 1 1 auto; }
  .gnode-detail .nd-close { width: 26px; height: 26px; flex: 0 0 auto; display: grid; place-items: center; background: var(--panel-2); border: 1px solid var(--border); border-radius: var(--r-sm); color: var(--muted); }
  .gnode-detail .nd-close:hover { color: var(--fg); border-color: var(--muted-2); }
  .gnode-detail .nd-body { overflow-y: auto; padding: 12px; display: grid; gap: 13px; }
  .nd-meta { display: grid; grid-template-columns: auto 1fr; gap: 5px 12px; font-size: 12.5px; align-items: baseline; }
  .nd-meta .k { color: var(--muted-2); }
  .nd-meta .v { color: var(--fg); font-family: var(--mono); word-break: break-word; }
  .nd-sec-title { font-size: 11px; letter-spacing: .05em; text-transform: uppercase; color: var(--muted-2); font-weight: 600; margin-bottom: 6px; }
  .nd-ev { padding: 8px 10px; border: 1px solid var(--border-soft); border-radius: var(--r-sm); background: var(--panel-2); margin-bottom: 6px; }
  .nd-ev-head { display: flex; align-items: center; gap: 7px; flex-wrap: wrap; }
  .nd-ev-ts { font-family: var(--mono); font-size: 11px; color: var(--muted-2); margin-left: auto; }
  .nd-ev-msg { font-size: 12.5px; color: var(--fg); margin-top: 5px; line-height: 1.5; }
  .nd-ev-msg.err { color: var(--err); font-weight: 600; }
  .nd-empty { color: var(--muted); font-size: 12.5px; text-align: center; padding: 18px 8px; }
  .graph-legend { display: flex; gap: 16px; flex-wrap: wrap; margin-top: 14px; font-size: 12px; color: var(--muted); }
  .graph-legend .lg { display: inline-flex; align-items: center; gap: 6px; }
  .graph-legend .sw { width: 11px; height: 11px; border-radius: 3px; border: 1.5px solid var(--border); }
  .graph-legend .sw.done { border-color: var(--ok); background: var(--ok-soft); }
  .graph-legend .sw.current { border-color: var(--accent); background: var(--accent-soft); }
  .graph-legend .sw.failed { border-color: var(--err); background: var(--err-soft); }
  .graph-legend .sw.stopped { border-color: var(--warn); background: var(--warn-soft); }
  .graph-legend .sw.pending { border-style: dashed; }

  /* detail placeholder / not-found */
  .detail-empty {
    display: flex; flex-direction: column; align-items: center; justify-content: center;
    gap: 12px; text-align: center; color: var(--muted); flex: 1 1 auto; padding: 40px;
  }
  .detail-empty .ic { width: 46px; height: 46px; color: var(--muted-2); opacity: .7; }
  .detail-empty .dt { font-size: 16px; font-weight: 650; color: var(--fg); }
  .detail-empty .ds { font-size: 13px; max-width: 320px; }
  .detail-empty.nf .ic { color: var(--err); opacity: .9; }

  .detail-sk { padding: 24px 26px; max-width: 980px; margin: 0 auto; width: 100%; display: grid; gap: 18px; }

  /* =========================================================================
     Login screen
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


  /* =========================================================================
     Responsive — collapse to single column ≤ 760px
     ========================================================================= */
  @media (max-width: 760px) {
    .shell { grid-template-columns: 1fr; }
    .list-pane { border-right: 0; }
    .brand-sub { display: none; }
    .account .who { max-width: 92px; }
    /* mobile master-detail: one region visible at a time */
    body[data-mobile-view="list"] .detail-pane { display: none; }
    body[data-mobile-view="detail"] .list-pane { display: none; }
    body[data-mobile-view="detail"] .backbar { display: flex; }
    .backbar {
      align-items: center; gap: 8px; padding: 10px 14px;
      border-bottom: 1px solid var(--border); position: sticky; top: 0; z-index: 5;
      background: color-mix(in oklab, var(--panel) 92%, transparent); backdrop-filter: blur(8px);
    }
    .backbar button { display: inline-flex; align-items: center; gap: 6px; background: transparent; border: 0; color: var(--accent); font-weight: 600; font-size: 13px; }
    .detail-inner { padding: 16px 16px 56px; }
    .ev { grid-template-columns: 1fr; gap: 0; }
    .ev .gutter { justify-content: flex-start; padding: 0 0 4px 22px; }
    .tl-tail { margin-left: 22px; }
    .usage-strip { flex-direction: column; }
    .usage-cell { border-right: 0; border-bottom: 1px solid var(--border-soft); }
    .usage-cell:last-child { border-bottom: 0; }
    .graph-wrap { height: 62dvh; }
    /* node detail becomes a bottom sheet */
    .gnode-detail { top: auto; left: 8px; right: 8px; bottom: 8px; width: auto; max-width: none; max-height: 64%; }
  }
  @media (min-width: 761px) { .backbar { display: none !important; } }

  /* reduced motion */
  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { animation-duration: .001ms !important; animation-iteration-count: 1 !important; transition-duration: .001ms !important; }
    .dot.s-running, .live.is-live .dot, .tl-tail .dot { animation: none; box-shadow: 0 0 0 3px var(--run-soft); }
  }
</style>
</head>
<body data-mobile-view="list">

  <!-- ===================== App root ===================== -->
  <div id="app" aria-busy="false"></div>


<script>
/* ===========================================================================
   Data + auth layer (production wiring).
   Browser OIDC Authorization Code + PKCE against nyxid (reuse of the console-web
   client; no server-side cookie/session, no NyxID change). All data flows through
   scope-gated bearer APIs; the page itself is the anonymous static shell.
   A small client cache backs the synchronous render contract (DataSource.*):
     listRuns(status) <- GET /api/workflow/observatory/runs
     getRun(runId)    <- GET /api/workflow/observatory/runs/{runId}
     getGraph(runId)  <- GET /api/workflow/observatory/runs/{runId}/graph
   Read-only: there are no edit/run/stop calls anywhere.
   =========================================================================== */
const CFG = {
  authority: "https://nyx.chrono-ai.fun",
  clientId: "37a93189-2734-406e-bca1-7dbdf25c5a53",
  scope: "openid profile email proxy",
  redirectUri: location.origin + "/workflow/observatory/callback",
  storageKey: "aevatar-observatory:nyxid:pkce",
  pollMs: 3000
};
const TOKEN_KEY = CFG.storageKey + ":token";
const PKCE_KEY  = CFG.storageKey + ":pkce";

function b64url(buf){ return btoa(String.fromCharCode.apply(null, new Uint8Array(buf))).replace(/\+/g,"-").replace(/\//g,"_").replace(/=+$/,""); }
async function sha256(text){ return b64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text))); }
function randomString(len){ const a=new Uint8Array(len); crypto.getRandomValues(a); return b64url(a.buffer).slice(0, len); }

function getToken(){ const raw=localStorage.getItem(TOKEN_KEY); if(!raw) return null; try { return JSON.parse(raw); } catch(e){ console.warn("observatory: token parse failed", e); } return null; }
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
  history.replaceState({}, "", "/workflow/observatory");
  if(!pending || pending.state !== returnedState){ console.warn("observatory: login state mismatch"); return false; }
  const form = new URLSearchParams();
  form.set("grant_type", "authorization_code");
  form.set("code", code);
  form.set("redirect_uri", CFG.redirectUri);
  form.set("client_id", CFG.clientId);
  form.set("code_verifier", pending.verifier);
  const res = await fetch(CFG.authority + "/oauth/token", { method:"POST", headers:{ "Content-Type":"application/x-www-form-urlencoded" }, body: form.toString() });
  sessionStorage.removeItem(PKCE_KEY);
  if(!res.ok){ console.warn("observatory: token exchange failed", res.status); return false; }
  const token = await res.json();
  token.obtained_at = Date.now();
  setToken(token);
  return true;
}

async function fetchUserInfo(){
  const token = getToken(); if(!token) return null;
  try { const res = await fetch(CFG.authority + "/oauth/userinfo", { headers:{ Authorization:"Bearer " + token.access_token } }); return res.ok ? await res.json() : null; }
  catch(e){ console.warn("observatory: userinfo fetch failed", e); }
  return null;
}
function toAccount(info){ return info ? { label: info.email || info.preferred_username || info.sub || "已登录" } : null; }

async function api(path){
  const token = getToken(); if(!token) throw new Error("not-authenticated");
  const res = await fetch(path, { headers:{ Authorization:"Bearer " + token.access_token } });
  if(res.status === 401){ signOutSilent(); throw new Error("unauthorized"); }
  if(res.status === 404) return null;
  if(!res.ok) throw new Error("api-error-" + res.status);
  return await res.json();
}

/* client cache backing the synchronous render contract */
const cache = { account:null, runs:[], details:{}, graphs:{} };
const DataSource = {
  now: () => new Date().toISOString(),
  getAccount: () => cache.account || { label:"已登录" },
  listRuns: (status) => (!status || status === "all") ? cache.runs.slice() : cache.runs.filter(r => r.status === status),
  getRun: (runId) => cache.details[runId] || null,
  getGraph: (runId) => cache.graphs[runId] || null
};

function clearSession(){ cache.account=null; cache.runs=[]; cache.details={}; cache.graphs={}; }
function signOut(){ clearToken(); clearSession(); beginLogin(); }
function signOutSilent(){ clearToken(); clearSession(); state.signedIn=false; state.scenario="login"; render(); }

/* The API graph carries only nodes/edges; per-node status is reconstructed from the
   committed timeline (never invented) so the DAG can show real progress honestly. */
function deriveNodeStatus(graph, detail){
  const status = {};
  if(!graph || !graph.nodes) return status;
  const events = (detail && detail.timeline) || [];
  const started=new Set(), finished=new Set(), failedStep=new Set(), stoppedStep=new Set();
  let runFailed=false, runStopped=false;
  for(const e of events){
    if(e.stepId){
      if(e.kind === "StepStarted" || e.kind === "HumanInputRequest") started.add(e.stepId);
      else if(e.kind === "StepFinished") finished.add(e.stepId);
      else if(e.kind === "RunError") failedStep.add(e.stepId);
      else if(e.kind === "RunStopped") stoppedStep.add(e.stepId);
    }
    if(e.kind === "RunError") runFailed=true;
    if(e.kind === "RunStopped") runStopped=true;
  }
  const running = detail && detail.summary && detail.summary.status === "running";
  for(const nd of graph.nodes){
    const id = nd.nodeId;
    if(failedStep.has(id)) status[id] = "failed";
    else if(stoppedStep.has(id)) status[id] = "stopped";
    else if(finished.has(id)) status[id] = "done";
    else if(started.has(id)) status[id] = running ? "current" : (runFailed ? "failed" : runStopped ? "stopped" : "done");
    else status[id] = "pending";
  }
  return status;
}

/* ===========================================================================
   工具函数
   =========================================================================== */
const $ = (sel, root=document) => root.querySelector(sel);
const el = (tag, attrs={}, html) => { const n=document.createElement(tag); for(const k in attrs){ if(k==="class")n.className=attrs[k]; else if(k.startsWith("on")&&typeof attrs[k]==="function")n.addEventListener(k.slice(2),attrs[k]); else if(attrs[k]!=null)n.setAttribute(k,attrs[k]); } if(html!=null)n.innerHTML=html; return n; };
const esc = (s) => String(s==null?"":s).replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));

const STATUS_LABEL = { running:"运行中", completed:"已完成", failed:"失败", timed_out:"已超时", stopped:"已停止", disabled:"已禁用", unknown:"未知", not_found:"未找到" };
const KIND = {
  RunStarted:        { label:"运行开始" },
  RunFinished:       { label:"运行完成" },
  RunError:          { label:"运行错误" },
  RunStopped:        { label:"运行停止" },
  StepStarted:       { label:"步骤开始" },
  StepFinished:      { label:"步骤完成" },
  TextMessage:       { label:"模型回复" },
  ToolCall:          { label:"工具调用" },
  HumanInputRequest: { label:"待人工确认" }
};
const STEPTYPE_LABEL = { llm:"模型", tool:"工具", human:"人工" };

function parseT(iso){ return Date.parse(iso); }
function clockUTC(iso){ // 取 ISO 中的 HH:MM:SS（UTC），保持确定性
  const m = /T(\d{2}:\d{2}:\d{2})/.exec(iso); return m ? m[1] : iso;
}
function relTime(iso, nowMs){
  let d = Math.max(0, Math.round((nowMs - parseT(iso))/1000));
  if (d <= 1)   return "刚刚";
  if (d < 60)   return d + " 秒前";
  if (d < 3600) return Math.floor(d/60) + " 分钟前";
  if (d < 86400)return Math.floor(d/3600) + " 小时前";
  return Math.floor(d/86400) + " 天前";
}
function fmtDur(ms){
  let s = Math.max(0, Math.round(ms/1000));
  const m = Math.floor(s/60); s = s%60;
  return m > 0 ? `${m}分${String(s).padStart(2,"0")}秒` : `${s}秒`;
}
function fmtNum(n){ return n==null ? "—" : n.toLocaleString("en-US"); }
function fmtCost(c){ return c==null ? "—" : "$"+Number(c).toFixed(3); }
function initials(label){ return (label||"?").trim().charAt(0).toUpperCase(); }
function midTrunc(s, max){ s = String(s==null?"":s); if(s.length <= max) return s; const h = Math.ceil((max-1)*0.62), t = (max-1) - h; return s.slice(0, h) + "…" + s.slice(s.length - t); }

/* JSON 着色（仅用于显示）。注意：先 esc() 转义，引号已变成 &quot;，
   因此高亮要匹配 &quot; 而非字面引号。本数据中字符串均为单行且不含 & 实体。 */
function colorJSON(str){
  if(!str) return "";
  let s = esc(str);
  // 字符串 / 键（键后跟冒号）
  s = s.replace(/(&quot;.*?&quot;)(\s*:)?/g, (m, tok, colon) =>
    colon ? `<span class="j-key">${tok}</span>${colon}` : `<span class="j-str">${tok}</span>`);
  // 数字（前面是空白/冒号/括号/逗号，后面是分隔符或行尾）
  s = s.replace(/(^|[\s:\[,])(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)(?=[\s,\]\}]|$)/g,
    (m, pre, num) => `${pre}<span class="j-num">${num}</span>`);
  // 布尔 / null
  s = s.replace(/\b(true|false)\b/g, '<span class="j-bool">$1</span>');
  s = s.replace(/\bnull\b/g, '<span class="j-null">null</span>');
  return s;
}

/* SVG icon set (inline, currentColor) */
const ICON = {
  sun:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="12" cy="12" r="4.2"/><path d="M12 2v2.4M12 19.6V22M2 12h2.4M19.6 12H22M4.6 4.6l1.7 1.7M17.7 17.7l1.7 1.7M19.4 4.6l-1.7 1.7M6.3 17.7l-1.7 1.7"/></svg>',
  moon:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A8.5 8.5 0 1 1 11.2 3a6.6 6.6 0 0 0 9.8 9.8Z"/></svg>',
  alert:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M12 8.5v4.5M12 16.2v.2"/><path d="M10.3 3.9 2.5 17.5A2 2 0 0 0 4.2 20.5h15.6a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg>',
  play:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><circle cx="12" cy="12" r="9"/><path d="M10 8.5 16 12l-6 3.5Z" fill="currentColor" stroke="none"/></svg>',
  flag:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M5 21V4M5 5h11l-1.5 3L16 11H5"/></svg>',
  x:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="m6 6 12 12M18 6 6 18"/></svg>',
  stop:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>',
  step:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><path d="M5 6h9M5 12h14M5 18h9"/></svg>',
  chat:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"><path d="M4 5h16v11H9l-4 3.5V16H4Z"/></svg>',
  tool:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M14.5 6a3.5 3.5 0 0 0-4.6 4.4L4 16.3 6.7 19l5.9-5.9A3.5 3.5 0 1 0 14.5 6Z"/></svg>',
  human:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="12" cy="8" r="3.4"/><path d="M5.5 20a6.5 6.5 0 0 1 13 0"/></svg>',
  check:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12.5 4.2 4.2L19 7"/></svg>',
  chevron:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 6 6 6-6 6"/></svg>',
  copy:'<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a1 1 0 0 1 1-1h9"/></svg>',
  inbox:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"><path d="M3 13.5 5.5 5h13L21 13.5V19a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1Z"/><path d="M3 13.5h5l1 2.5h6l1-2.5h5"/></svg>',
  cursor:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"><path d="M5 3l5.5 16 2.2-6.3L19 10.5 5 3Z"/></svg>',
  ghost:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 20V11a7 7 0 0 1 14 0v9l-2.3-1.6L14.4 20l-2.4-1.6L9.6 20 7.3 18.4 5 20Z"/><path d="M9.5 10h.01M14.5 10h.01"/></svg>',
  lock:'<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
  back:'<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m14 6-6 6 6 6"/></svg>'
};
function kindIcon(kind){
  switch(kind){
    case "RunStarted": return ICON.play;
    case "RunFinished": return ICON.flag;
    case "RunError": return ICON.x;
    case "RunStopped": return ICON.stop;
    case "StepStarted": case "StepFinished": return ICON.step;
    case "TextMessage": return ICON.chat;
    case "ToolCall": return ICON.tool;
    case "HumanInputRequest": return ICON.human;
    default: return ICON.step;
  }
}
function stepTypeIcon(t){ return t==="llm"?ICON.chat : t==="tool"?ICON.tool : t==="human"?ICON.human : ICON.step; }

/* ===========================================================================
   App state
   =========================================================================== */
const state = {
  signedIn: false,
  scenario: "login",   // login | listLoading | empty | globalError | detailLoading | notFound | normal
  filter: "all",       // all | running | completed | failed | stopped
  selectedRunId: null,
  activeTab: "timeline",
  expanded: new Set(),  // tool-call ids expanded by default for the running run
  theme: localStorage.getItem("wro-theme") || null, // null = follow system
  selectedNodeId: null, // graph node whose detail drawer is open
  graphView: { zoom: 1, panX: 0, panY: 0, fitted: false }, // pan/zoom transform, survives polling re-renders
};
function nowMs(){ return Date.now(); }

/* apply theme attr */
function applyTheme(){
  if(state.theme === "light" || state.theme === "dark") document.documentElement.setAttribute("data-theme", state.theme);
  else document.documentElement.removeAttribute("data-theme");
}
function effectiveDark(){
  if(state.theme) return state.theme === "dark";
  return !window.matchMedia || !window.matchMedia("(prefers-color-scheme: light)").matches;
}

/* ===========================================================================
   Render — top bar
   =========================================================================== */
function renderTopbar(){
  const acct = DataSource.getAccount();
  const bar = el("header", { class:"topbar", role:"banner" });
  bar.innerHTML = `
    <div class="brand">
      <div class="brand-mark" aria-hidden="true">
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="2.4"/><circle cx="6" cy="18" r="2.4"/><circle cx="18" cy="12" r="2.4"/><path d="M8.2 7.2 15.8 11M8.2 16.8 15.8 13"/></svg>
      </div>
      <div style="min-width:0">
        <div class="brand-name">运行观测台 <span class="brand-sub">Workflow Run Observatory</span></div>
      </div>
      <span class="pill-ro" title="只读视图，不可修改任何运行">只读</span>
    </div>
    <div class="spacer"></div>`;

  // live indicator (hidden on login)
  if(state.signedIn){
    const sel = currentDetail();
    const isRunning = sel && sel.summary.status === "running";
    const live = el("div", { class:"live"+(isRunning?" is-live":""), role:"status", "aria-live":"polite", id:"liveChip", title:"页面每约 3 秒轮询一次" });
    live.innerHTML = `<span class="dot" aria-hidden="true"></span>
      <span>${isRunning?"实时":"已暂停"}</span>
      <span class="freshness" data-since="${sel?sel.summary.updatedAtUtc:DataSource.now()}">${sel?relTime(parseT(sel.summary.updatedAtUtc), nowMs()):""} 更新</span>`;
    bar.appendChild(live);
  }

  // theme toggle
  const tbtn = el("button", { class:"iconbtn", id:"themeBtn", "aria-label":"切换主题（深色 / 浅色）", title:"切换主题" });
  tbtn.innerHTML = effectiveDark() ? ICON.sun : ICON.moon;
  tbtn.addEventListener("click", () => {
    state.theme = effectiveDark() ? "light" : "dark";
    localStorage.setItem("wro-theme", state.theme);
    applyTheme(); render();
  });
  bar.appendChild(tbtn);

  if(state.signedIn){
    const chip = el("div", { class:"account" });
    chip.innerHTML = `<span class="avatar" aria-hidden="true">${initials(acct.label)}</span><span class="who" title="${esc(acct.label)}">${esc(acct.label)}</span>`;
    bar.appendChild(chip);
    const sw = el("button", { class:"linkbtn", "aria-label":"切换账户" }, "切换账户");
    sw.addEventListener("click", () => { signOut(); });
    bar.appendChild(sw);
  }
  return bar;
}

/* ===========================================================================
   Render — error banner
   =========================================================================== */
function renderBanner(){
  const b = el("div", { class:"banner", role:"alert" });
  b.innerHTML = `${ICON.alert}<span class="b-title">加载运行失败</span>
     <span class="b-msg">无法连接到观测服务（HTTP 503）。已显示上次成功获取的快照；将在下次轮询时自动重试。</span>`;
  return b;
}

/* ===========================================================================
   Render — login screen
   =========================================================================== */
function renderLogin(){
  const wrap = el("main", { class:"login" });
  const card = el("section", { class:"login-card", role:"region", "aria-label":"登录" });
  card.innerHTML = `
    <div class="login-mark" aria-hidden="true">${ICON.lock}</div>
    <h1>登录以查看你的工作流运行</h1>
    <p>只读访问你账户下的运行 —— 观察执行过程，不可修改任何内容。</p>`;
  const btn = el("button", { class:"btn-primary", id:"signinBtn" }, "使用 nyxid 登录");
  btn.addEventListener("click", () => { beginLogin(); });
  card.appendChild(btn);
  card.appendChild(el("div", { class:"login-foot" }, `${ICON.lock.replace('width="24" height="24"','width="12" height="12"')}<span>采用 OIDC bearer-token 鉴权 · 通过 nyxid 账户登录</span>`));
  wrap.appendChild(card);
  return wrap;
}

/* ===========================================================================
   Render — runs list
   =========================================================================== */
const FILTERS = [["all","全部"],["running","运行中"],["completed","已完成"],["failed","失败"],["stopped","已停止"]];

function renderList(){
  const pane = el("aside", { class:"list-pane", "aria-label":"运行列表" });
  const head = el("div", { class:"list-head" });
  const total = state.scenario==="empty" ? 0 : DataSource.listRuns("all").length;
  head.innerHTML = `<div class="list-title-row"><span class="list-title">运行</span><span class="list-count">${state.scenario==="empty"?0:DataSource.listRuns(state.filter).length} / ${total}</span></div>`;
  const filters = el("div", { class:"filters", role:"group", "aria-label":"按状态筛选" });
  FILTERS.forEach(([val,lab]) => {
    const c = el("button", { class:"chip", "aria-pressed": String(state.filter===val) }, lab);
    c.addEventListener("click", () => { state.filter = val; render(); });
    filters.appendChild(c);
  });
  head.appendChild(filters);
  pane.appendChild(head);

  const listbox = el("div", { class:"runlist scroll", role:"list", "aria-label":"运行", tabindex:"-1" });

  if(state.scenario === "listLoading"){
    for(let i=0;i<5;i++){
      const r = el("div", { class:"sk-row" });
      r.innerHTML = `<div class="sk sk-dot"></div><div><div class="sk sk-line" style="width:62%"></div><div class="sk sk-line" style="width:42%;margin-top:8px;height:8px"></div></div>`;
      listbox.appendChild(r);
    }
    pane.appendChild(listbox);
    return pane;
  }

  const runs = state.scenario === "empty" ? [] : DataSource.listRuns(state.filter);

  if(runs.length === 0){
    const isFiltered = state.scenario !== "empty" && state.filter !== "all";
    const fm = FILTERS.find(f => f[0] === state.filter);
    const flabel = fm ? fm[1] : "当前条件";
    listbox.appendChild(el("div", { class:"empty" }, `
      <div class="ic" aria-hidden="true">${ICON.inbox}</div>
      <div class="et">${isFiltered ? "没有匹配的运行" : "暂无运行"}</div>
      <div class="es">${isFiltered ? `「${flabel}」筛选下没有运行，换个状态再看看。` : "你的账户下还没有任何工作流运行。一旦有运行启动，就会出现在这里。"}</div>`));
    pane.appendChild(listbox);
    return pane;
  }

  runs.forEach(r => {
    const sel = state.selectedRunId === r.runId && state.scenario==="normal";
    const row = el("button", { class:"run-row", role:"listitem", "aria-current": String(sel) });
    row.innerHTML = `
      <span class="dot s-${r.status}" aria-hidden="true"></span>
      <span class="rn">${esc(r.workflowName)}</span>
      <span class="rm">
        <span>${STATUS_LABEL[r.status]||r.status}</span><span class="sep">·</span>
        <span class="id">${esc(r.runId)}</span><span class="sep">·</span>
        <span data-since="${r.updatedAtUtc}">${relTime(parseT(r.updatedAtUtc), nowMs())}</span>
      </span>`;
    row.addEventListener("click", () => selectRun(r.runId));
    listbox.appendChild(row);
  });
  pane.appendChild(listbox);
  return pane;
}

/* ===========================================================================
   Render — detail
   =========================================================================== */
function currentDetail(){
  if(state.scenario === "notFound") return null;
  return DataSource.getRun(state.selectedRunId);
}

function renderDetail(){
  const pane = el("section", { class:"detail-pane scroll", "aria-label":"运行详情" });

  // mobile back bar
  const back = el("div", { class:"backbar" });
  const backBtn = el("button", {}, `${ICON.back}<span>运行</span>`);
  backBtn.addEventListener("click", () => { document.body.setAttribute("data-mobile-view","list"); });
  back.appendChild(backBtn);
  pane.appendChild(back);

  if(state.scenario === "detailLoading"){
    const sk = el("div", { class:"detail-sk" });
    sk.innerHTML = `
      <div class="sk sk-line" style="width:240px;height:22px"></div>
      <div class="sk sk-line" style="width:70%;height:12px"></div>
      <div class="sk" style="height:60px;border-radius:13px"></div>
      <div class="sk sk-line" style="width:160px;height:30px;border-radius:999px"></div>
      <div class="sk" style="height:120px;border-radius:9px;margin-top:6px"></div>
      <div class="sk" style="height:90px;border-radius:9px"></div>`;
    pane.appendChild(sk);
    return pane;
  }

  if(state.scenario === "notFound"){
    pane.appendChild(el("div", { class:"detail-empty nf" }, `
      <div class="ic" aria-hidden="true">${ICON.ghost}</div>
      <div class="dt">运行未找到</div>
      <div class="ds">运行 <span class="mono">${esc(state.selectedRunId||"run_unknown")}</span> 不存在，或已超出你账户的可见范围。可能已被清理，或属于其他账户。</div>`));
    return pane;
  }

  const detail = currentDetail();
  if(!detail){
    pane.appendChild(el("div", { class:"detail-empty" }, `
      <div class="ic" aria-hidden="true">${ICON.cursor}</div>
      <div class="dt">选择一个运行以查看执行过程</div>
      <div class="ds">从左侧列表选择任意运行，自上而下阅读它的执行故事 —— 每一步做了什么、模型说了什么、调用了哪些工具。</div>`));
    return pane;
  }

  const inner = el("div", { class:"detail-inner" });
  inner.appendChild(renderRunHeader(detail));
  inner.appendChild(renderTabs());

  const tp = el("div", {});
  const timelinePanel = el("div", { class:"tabpanel", id:"panel-timeline", role:"tabpanel", "aria-labelledby":"tab-timeline", tabindex:"0" });
  timelinePanel.appendChild(renderTimeline(detail));
  if(state.activeTab !== "timeline") timelinePanel.hidden = true;

  const graphPanel = el("div", { class:"tabpanel", id:"panel-graph", role:"tabpanel", "aria-labelledby":"tab-graph", tabindex:"0" });
  graphPanel.appendChild(renderGraph(detail));
  if(state.activeTab !== "graph") graphPanel.hidden = true;

  tp.appendChild(timelinePanel);
  tp.appendChild(graphPanel);
  inner.appendChild(tp);

  pane.appendChild(inner);
  return pane;
}

function renderRunHeader(detail){
  const s = detail.summary;
  const started = parseT(s.startedAtUtc);
  const isRunning = s.status === "running";
  const endMs = isRunning ? nowMs() : parseT(s.updatedAtUtc);
  const u = detail.usageTotals;

  const head = el("header", { class:"run-header" });
  head.innerHTML = `
    <div class="rh-top">
      <h1 class="rh-title"><span class="dot s-${s.status}" aria-hidden="true"></span>${esc(s.workflowName)}</h1>
      <span class="badge b-${s.status}"><span class="dot s-${s.status}" aria-hidden="true"></span>${STATUS_LABEL[s.status]||s.status}</span>
    </div>
    <div class="rh-meta">
      <span class="grp"><span class="k">运行 ID</span><span class="v">${esc(s.runId)}</span></span>
      <span class="grp"><span class="k">用时</span><span class="v" ${isRunning?`data-duration="${s.startedAtUtc}"`:""}>${fmtDur(endMs-started)}</span></span>
      <span class="grp"><span class="k">开始</span><span class="v">${clockUTC(s.startedAtUtc)} UTC</span></span>
    </div>`;

  // usage strip (compact summary cards)
  const strip = el("div", { class:"usage-strip", "aria-label":"用量汇总" });
  strip.innerHTML = `
    <div class="usage-cell"><div class="ul">输入 Token</div><div class="uv">${fmtNum(u.promptTokens)}</div></div>
    <div class="usage-cell"><div class="ul">输出 Token</div><div class="uv">${fmtNum(u.completionTokens)}</div></div>
    <div class="usage-cell"><div class="ul">合计 Token</div><div class="uv">${fmtNum(u.totalTokens)}</div></div>
    <div class="usage-cell accent"><div class="ul">花费</div><div class="uv">${fmtCost(u.cost)}</div></div>
    <div class="usage-cell"><div class="ul">状态版本</div><div class="uv">v${s.stateVersion}</div></div>`;
  head.appendChild(strip);
  return head;
}

function renderTabs(){
  const tablist = el("div", { class:"tabs", role:"tablist", "aria-label":"详情视图" });
  [["timeline","时间线",ICON.step],["graph","拓扑图",'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><circle cx="5" cy="6" r="2.2"/><circle cx="5" cy="18" r="2.2"/><circle cx="19" cy="12" r="2.2"/><path d="M7 6.6 17 11M7 17.4 17 13"/></svg>']].forEach(([id,lab,ic]) => {
    const t = el("button", { class:"tab", id:"tab-"+id, role:"tab", "aria-selected": String(state.activeTab===id), "aria-controls":"panel-"+id, tabindex: state.activeTab===id?"0":"-1" }, `${ic}<span>${lab}</span>`);
    t.addEventListener("click", () => { state.activeTab = id; if(id === "graph") loadGraph(state.selectedRunId); render(); });
    t.addEventListener("keydown", (e) => {
      if(e.key==="ArrowRight"||e.key==="ArrowLeft"){ e.preventDefault(); state.activeTab = state.activeTab==="timeline"?"graph":"timeline"; if(state.activeTab === "graph") loadGraph(state.selectedRunId); render(); $("#tab-"+state.activeTab)?.focus(); }
    });
    tablist.appendChild(t);
  });
  return tablist;
}

/* ---- Timeline ---- */
function renderTimeline(detail){
  const wrap = el("div", {});
  wrap.appendChild(el("p", { class:"tl-rail-note" }, "按时间自上而下 · 时间戳为 UTC"));
  const tl = el("ol", { class:"timeline", "aria-label":"事件时间线", style:"list-style:none;margin:0;padding:0" });
  const isRunning = detail.summary.status === "running";
  const lastIdx = detail.timeline.length - 1;

  detail.timeline.forEach((ev, i) => {
    const isLast = i === lastIdx;
    const li = el("li", { class:`ev k-${ev.kind}${(isRunning && isLast)?" is-new":""}` });

    // gutter timestamp
    const gutter = el("div", { class:"gutter" }, `<span class="ts">${clockUTC(ev.timestampUtc)}</span>`);

    // rail + body
    const rail = el("div", { class:"node-rail" });
    rail.appendChild(el("span", { class:"marker", "aria-hidden":"true" }));
    const body = el("div", { class:"ev-body" });

    const headRow = el("div", { class:"ev-head" });
    headRow.innerHTML = `<span class="kind">${kindIcon(ev.kind)}<span>${KIND[ev.kind]?.label||ev.kind}</span></span>`;
    if(ev.stepId){
      const stType = ev.stepType ? ` <span class="st">${STEPTYPE_LABEL[ev.stepType]||ev.stepType}</span>` : "";
      headRow.insertAdjacentHTML("beforeend", `<span class="stepbadge">${esc(ev.stepId)}${stType}</span>`);
    }
    if(ev.agentId) headRow.insertAdjacentHTML("beforeend", `<span class="agentbadge">@${esc(ev.agentId)}</span>`);
    body.appendChild(headRow);

    // content by kind
    if(ev.kind === "TextMessage"){
      body.appendChild(el("div", { class:"bubble" }, esc(ev.message)));
    } else if(ev.kind === "ToolCall"){
      body.appendChild(renderToolCall(ev.toolCall));
    } else if(ev.kind === "HumanInputRequest"){
      const na = el("div", { class:"needs-attn", role:"note" });
      na.innerHTML = `${ICON.human}<div><div class="na-l">需要关注 · 等待人工确认</div><div class="na-m">${esc(ev.message)}</div><div class="na-ro">${ICON.lock.replace('width="24" height="24"','width="11" height="11"')} 只读视图 —— 请到审批系统中处理</div></div>`;
      body.appendChild(na);
    } else if(ev.kind === "StepFinished" && ev.message){
      const parts = String(ev.message).split("·").map(x=>x.trim());
      body.appendChild(el("div", { class:"ev-cost" }, `<span class="costchip">${parts.map(p=>`<b>${esc(p)}</b>`).join(' <span style="color:var(--border)">·</span> ')}</span>`));
    } else if(ev.message){
      const cls = (ev.kind==="RunError") ? "ev-msg" : "ev-msg";
      const node = el("div", { class:cls }, esc(ev.message));
      if(ev.kind==="RunError") node.style.color = "var(--err)", node.style.fontWeight = "600";
      body.appendChild(node);
    }

    rail.appendChild(body);
    li.appendChild(gutter);
    li.appendChild(rail);
    tl.appendChild(li);
  });

  wrap.appendChild(tl);

  if(isRunning){
    const tail = el("div", { class:"tl-tail", role:"status", "aria-live":"polite" });
    tail.innerHTML = `<span class="dot" aria-hidden="true"></span><span>运行进行中 —— 新事件将自动追加在此</span>`;
    wrap.appendChild(tail);
  }
  return wrap;
}

function renderToolCall(tc){
  const open = state.expanded.has(tc.callId);
  const box = el("div", { class:"toolcall", "data-open": String(open), "data-callid": tc.callId });
  const headBtn = el("button", { class:"tc-head", "aria-expanded": String(open), "aria-controls":"tcbody-"+tc.callId });
  headBtn.innerHTML = `
    <span class="tc-chevron" aria-hidden="true">${ICON.chevron}</span>
    <span class="tc-name">${esc(tc.toolName)}</span>
    <span class="tc-callid">${esc(tc.callId)}</span>
    <span class="tc-status ${tc.success?"ok":"bad"}">${tc.success?ICON.check:ICON.x}${tc.success?"成功":"失败"}</span>`;
  headBtn.addEventListener("click", () => {
    if(state.expanded.has(tc.callId)) state.expanded.delete(tc.callId); else state.expanded.add(tc.callId);
    const o = state.expanded.has(tc.callId);
    box.setAttribute("data-open", String(o));
    headBtn.setAttribute("aria-expanded", String(o));
  });
  box.appendChild(headBtn);

  const bodyEl = el("div", { class:"tc-body", id:"tcbody-"+tc.callId });
  bodyEl.appendChild(jsonField("参数 · arguments", tc.argumentsJson));
  if(tc.success){
    bodyEl.appendChild(jsonField("结果 · result", tc.resultJson));
  } else {
    const f = el("div", { class:"tc-field" });
    f.innerHTML = `<div class="tc-field-head"><span class="tc-field-label">错误 · error</span></div>`;
    f.appendChild(el("pre", { class:"json scroll" }, `<span class="tc-error">${esc(tc.error||"unknown error")}</span>`));
    bodyEl.appendChild(f);
  }
  box.appendChild(bodyEl);
  return box;
}

function jsonField(label, raw){
  const f = el("div", { class:"tc-field" });
  const head = el("div", { class:"tc-field-head" });
  head.innerHTML = `<span class="tc-field-label">${esc(label)}</span>`;
  const copy = el("button", { class:"copybtn", "aria-label":"复制 "+label }, `${ICON.copy}<span>复制</span>`);
  copy.addEventListener("click", async () => {
    try { await navigator.clipboard.writeText(raw||""); } catch(e){
      const ta=document.createElement("textarea"); ta.value=raw||""; document.body.appendChild(ta); ta.select(); try{document.execCommand("copy");}catch(_){ } ta.remove();
    }
    copy.classList.add("done"); copy.querySelector("span").textContent="已复制";
    setTimeout(()=>{ copy.classList.remove("done"); copy.querySelector("span").textContent="复制"; }, 1400);
  });
  head.appendChild(copy);
  f.appendChild(head);
  const pre = el("pre", { class:"json scroll", tabindex:"0" });
  pre.innerHTML = (raw && raw.trim()) ? colorJSON(raw) : '<span class="j-null">—</span>';
  f.appendChild(pre);
  return f;
}

/* ---- Graph (DAG) — interactive: zoom / pan / fit-to-view + click-a-node detail ----
   Vanilla-JS reimagining of the console-web Studio graph (no React / no @xyflow). The API graph
   carries only nodes/edges; per-node status comes from deriveNodeStatus (committed timeline, never
   invented). Node detail is a pure client-side join of the node against the run timeline events for
   the same stepId — no extra backend call. */
const GRAPH_CATEGORY = {
  transform:"data", assign:"data", retrieve_facts:"data", cache:"data",
  guard:"control", conditional:"control", switch:"control", while:"control", delay:"control", wait_signal:"control", checkpoint:"control",
  llm:"ai", llm_call:"ai", tool:"ai", tool_call:"ai", evaluate:"ai", reflect:"ai",
  foreach:"composition", parallel:"composition", race:"composition", map_reduce:"composition", workflow_call:"composition", dynamic_workflow:"composition", vote:"composition",
  connector_call:"integration", emit:"integration",
  human:"human", human_input:"human", human_approval:"human"
};
function nodeCategory(t){ return GRAPH_CATEGORY[String(t||"").toLowerCase()] || ""; }
const GRAPH_ZOOM_MIN = 0.3, GRAPH_ZOOM_MAX = 2.0;
const clampZoom = z => Math.max(GRAPH_ZOOM_MIN, Math.min(GRAPH_ZOOM_MAX, z));
const ST_LABEL = { done:"已完成", current:"进行中", failed:"失败", stopped:"已停止", pending:"待执行" };
const ST_BADGE = { done:"completed", current:"running", failed:"failed", stopped:"stopped", pending:"unknown" };

function applyGraphTransform(viewport){
  const v = state.graphView;
  viewport.style.transform = `translate(${v.panX}px,${v.panY}px) scale(${v.zoom})`;
  const lab = document.getElementById("graphZoomLabel");
  if(lab) lab.textContent = Math.round(v.zoom*100) + "%";
}
/* Fit the whole graph into the viewport. readableFloor (optional) keeps the FIRST view legible: a wide
   horizontal flow in a narrow pane would otherwise fit at ~30% (unreadable), so when the true-fit zoom
   falls below the floor we open at the floor zoom anchored to the flow start and let the user pan. The
   explicit Fit control passes no floor → true fit-everything overview. */
function fitGraphView(wrap, viewport, W, H, readableFloor){
  const vw = wrap.clientWidth, vh = wrap.clientHeight;
  if(!vw || !vh) return;
  const pad = 28;
  const fit = clampZoom(Math.min((vw-pad*2)/W, (vh-pad*2)/H, 1));
  if(readableFloor && fit < readableFloor){
    const zoom = readableFloor;
    state.graphView = { zoom, panX: pad, panY: Math.max(pad, (vh - H*zoom)/2), fitted:true };
  } else {
    state.graphView = { zoom: fit, panX:(vw - W*fit)/2, panY:(vh - H*fit)/2, fitted:true };
  }
  applyGraphTransform(viewport);
}
/* Auto-fit once the graph first becomes visible. Deferred via setTimeout (not rAF — rAF is paused on
   hidden/background tabs, which would leave the graph unfitted); the node is attached after the
   synchronous render so reading clientWidth forces layout. Retry until the viewport is measurable. */
function scheduleAutoFit(host, viewport, W, H){
  let tries = 0;
  const tick = () => {
    if(state.graphView.fitted || state.activeTab !== "graph") return;
    if(host.clientWidth > 0 && host.clientHeight > 0){ fitGraphView(host, viewport, W, H, 0.55); return; }
    if(++tries < 12) setTimeout(tick, 30);
  };
  setTimeout(tick, 0);
}
function zoomGraphAt(viewport, factor, cx, cy){
  const v = state.graphView;
  const nz = clampZoom(v.zoom * factor);
  const k = nz / v.zoom;
  v.panX = cx - (cx - v.panX) * k;
  v.panY = cy - (cy - v.panY) * k;
  v.zoom = nz; v.fitted = true;
  applyGraphTransform(viewport);
}
function attachGraphInteractions(wrap, viewport){
  wrap.addEventListener("wheel", (e) => {
    e.preventDefault();
    const rect = wrap.getBoundingClientRect();
    zoomGraphAt(viewport, e.deltaY < 0 ? 1.1 : 1/1.1, e.clientX - rect.left, e.clientY - rect.top);
  }, { passive:false });

  let dragging=false, pid=null, sx=0, sy=0, ox=0, oy=0;
  wrap.addEventListener("pointerdown", (e) => {
    if(e.button!==0 || e.target.closest(".gnode, .graph-controls, .gnode-detail")) return;
    dragging=true; pid=e.pointerId; sx=e.clientX; sy=e.clientY; ox=state.graphView.panX; oy=state.graphView.panY;
    wrap.classList.add("is-panning");
    try { wrap.setPointerCapture(e.pointerId); } catch(_){}
  });
  wrap.addEventListener("pointermove", (e) => {
    if(!dragging || e.pointerId!==pid) return;
    state.graphView.panX = ox + (e.clientX - sx);
    state.graphView.panY = oy + (e.clientY - sy);
    state.graphView.fitted = true;
    applyGraphTransform(viewport);
  });
  const end = () => { if(!dragging) return; dragging=false; wrap.classList.remove("is-panning"); try { wrap.releasePointerCapture(pid); } catch(_){} };
  wrap.addEventListener("pointerup", end);
  wrap.addEventListener("pointercancel", end);
}

function openNodeDetail(nodeId){ state.selectedNodeId = nodeId; syncNodeDetail(); }
function closeNodeDetail(){ state.selectedNodeId = null; syncNodeDetail(); }
/* Toggle the drawer + node halo in place so an open panel and the zoom/pan transform survive without a
   full page re-render. */
function syncNodeDetail(){
  const host = document.querySelector(".graph-wrap");
  if(!host) return;
  host.querySelectorAll(".gnode").forEach(nd => nd.classList.toggle("selected", nd.getAttribute("data-node")===state.selectedNodeId));
  const existing = host.querySelector(".gnode-detail");
  if(existing) existing.remove();
  const detail = currentDetail();
  const g = detail && DataSource.getGraph(detail.summary.runId);
  if(state.selectedNodeId && detail && g && g.nodes.some(nd => nd.nodeId === state.selectedNodeId))
    host.appendChild(renderNodeDetailPanel(detail, g, state.selectedNodeId));
}

function renderNodeDetailPanel(detail, g, nodeId){
  const node = g.nodes.find(nd => nd.nodeId === nodeId);
  const st = (g.nodeStatus && g.nodeStatus[nodeId]) || "pending";
  const events = (detail.timeline || []).filter(e => e.stepId === nodeId)
    .slice().sort((a,b) => parseT(a.timestampUtc) - parseT(b.timestampUtc));
  const tsList = events.map(e => parseT(e.timestampUtc)).filter(x => !isNaN(x));
  const startTs = tsList.length ? Math.min.apply(null, tsList) : null;
  const endTs = tsList.length ? Math.max.apply(null, tsList) : null;
  const cat = nodeCategory(node && node.nodeType);

  const panel = el("aside", { class:"gnode-detail", role:"region", "aria-label":`节点详情 ${nodeId}`, tabindex:"-1" });
  panel.addEventListener("keydown", (e) => { if(e.key === "Escape"){ e.stopPropagation(); closeNodeDetail(); } });

  const head = el("div", { class:"nd-head" });
  head.innerHTML = `<span class="nd-ic" data-cat="${cat}" aria-hidden="true">${stepTypeIcon(node && node.nodeType)}</span>
    <span class="nd-id" title="${esc(nodeId)}">${esc(nodeId)}</span>`;
  const close = el("button", { class:"nd-close", type:"button", "aria-label":"关闭节点详情" }, ICON.x);
  close.addEventListener("click", (ev) => { ev.stopPropagation(); closeNodeDetail(); });
  head.appendChild(close);
  panel.appendChild(head);

  const body = el("div", { class:"nd-body scroll" });
  const meta = el("div", { class:"nd-meta" });
  meta.innerHTML = `
    <span class="k">状态</span><span class="v"><span class="badge b-${ST_BADGE[st]}">${ST_LABEL[st]}</span></span>
    <span class="k">类型</span><span class="v">${esc((node && node.nodeType) || "—")}</span>
    ${startTs!=null ? `<span class="k">开始</span><span class="v">${clockUTC(new Date(startTs).toISOString())} UTC</span>` : ""}
    ${(startTs!=null && endTs!=null && endTs>startTs) ? `<span class="k">用时</span><span class="v">${fmtDur(endTs-startTs)}</span>` : ""}`;
  body.appendChild(meta);

  const sec = el("div", {});
  sec.appendChild(el("div", { class:"nd-sec-title" }, `执行事件 · ${events.length}`));
  if(events.length === 0){
    sec.appendChild(el("div", { class:"nd-empty" }, "该节点尚无执行事件（待执行，或无可观察记录）。"));
  } else {
    events.forEach(ev => {
      const row = el("div", { class:"nd-ev" });
      const evHead = el("div", { class:"nd-ev-head" });
      evHead.innerHTML = `<span class="kind k-${ev.kind}">${kindIcon(ev.kind)}<span>${KIND[ev.kind]?.label||ev.kind}</span></span><span class="nd-ev-ts">${clockUTC(ev.timestampUtc)}</span>`;
      row.appendChild(evHead);
      if(ev.kind === "ToolCall" && ev.toolCall){
        row.appendChild(renderToolCall(ev.toolCall));
      } else if(ev.message){
        row.appendChild(el("div", { class:"nd-ev-msg"+(ev.kind==="RunError"?" err":"") }, esc(ev.message)));
      }
      sec.appendChild(row);
    });
  }
  body.appendChild(sec);
  panel.appendChild(body);
  requestAnimationFrame(() => { try { panel.focus(); } catch(_){} });
  return panel;
}

function renderGraph(detail){
  const g = DataSource.getGraph(detail.summary.runId);
  const wrap = el("div", {});
  if(!g){ wrap.appendChild(el("div", { class:"detail-empty" }, `<div class="dt">暂无拓扑数据</div>`)); return wrap; }

  const host = el("div", { class:"graph-wrap", role:"application", "aria-label":"工作流拓扑图：滚轮缩放、拖拽平移、点击节点查看详情" });
  const viewport = el("div", { class:"graph-viewport" });
  const canvas = el("div", { class:"graph-canvas" });

  const horizontal = window.innerWidth > 720;
  const NW = 244, NH = 96, GX = 150, GY = 84, PAD = 28;
  const n = g.nodes.length;
  const pos = {};
  let W, H;
  if(horizontal){
    W = PAD*2 + n*NW + (n-1)*GX;
    H = PAD*2 + NH;
    g.nodes.forEach((nd,i) => pos[nd.nodeId] = { x: PAD + i*(NW+GX), y: PAD });
  } else {
    W = PAD*2 + NW;
    H = PAD*2 + n*NH + (n-1)*GY;
    g.nodes.forEach((nd,i) => pos[nd.nodeId] = { x: PAD, y: PAD + i*(NH+GY) });
  }
  canvas.style.width = W+"px"; canvas.style.height = H+"px";

  // SVG edges
  const svgNS = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(svgNS,"svg");
  svg.setAttribute("width", W); svg.setAttribute("height", H);
  const defs = document.createElementNS(svgNS,"defs");
  defs.innerHTML = `<marker id="arrow" viewBox="0 0 10 10" refX="8.5" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
      <path d="M0 0 L10 5 L0 10 z" fill="var(--muted-2)"/></marker>`;
  svg.appendChild(defs);

  const edgeLabels = [];
  g.edges.forEach(e => {
    const a = pos[e.fromNodeId], b = pos[e.toNodeId];
    if(!a||!b) return;
    let x1,y1,x2,y2,mx,my;
    if(horizontal){ x1=a.x+NW; y1=a.y+NH/2; x2=b.x; y2=b.y+NH/2; mx=(x1+x2)/2; my=y1; }
    else { x1=a.x+NW/2; y1=a.y+NH; x2=b.x+NW/2; y2=b.y; mx=x1; my=(y1+y2)/2; }
    const path = document.createElementNS(svgNS,"path");
    let d;
    if(horizontal){ const cx=(x1+x2)/2; d=`M${x1},${y1} C${cx},${y1} ${cx},${y2} ${x2},${y2}`; }
    else { const cy=(y1+y2)/2; d=`M${x1},${y1} C${x1},${cy} ${x2},${cy} ${x2},${y2}`; }
    path.setAttribute("d", d);
    path.setAttribute("fill","none");
    path.setAttribute("stroke","var(--muted-2)");
    path.setAttribute("stroke-width","1.6");
    path.setAttribute("marker-end","url(#arrow)");
    if(g.nodeStatus && g.nodeStatus[e.toNodeId]==="pending"){ path.setAttribute("stroke-dasharray","4 4"); path.setAttribute("opacity",".6"); }
    svg.appendChild(path);
    edgeLabels.push({ mx, my, type:e.edgeType });
  });
  canvas.appendChild(svg);

  // nodes (clickable buttons → detail drawer)
  g.nodes.forEach(nd => {
    const st = (g.nodeStatus && g.nodeStatus[nd.nodeId]) || "pending";
    const cat = nodeCategory(nd.nodeType);
    const node = el("button", {
      type:"button",
      class:`gnode st-${st}${state.selectedNodeId===nd.nodeId?" selected":""}`,
      "data-cat": cat || null,
      "data-node": nd.nodeId,
      "aria-label":`节点 ${nd.nodeId}，${ST_LABEL[st]}，点击查看详情`,
      style:`left:${pos[nd.nodeId].x}px;top:${pos[nd.nodeId].y}px`
    });
    node.innerHTML = `
      <div class="gn-top">
        <span class="gn-ic" aria-hidden="true">${stepTypeIcon(nd.nodeType)}</span>
        <div class="gn-main"><div class="gn-id">${esc(midTrunc(nd.nodeId, 26))}</div><div class="gn-type">${esc(midTrunc(nd.nodeType, 28) || "—")}</div></div>
      </div>
      <div class="gn-foot"><span class="gn-state">${ST_LABEL[st]}</span></div>`;
    node.addEventListener("click", (ev) => { ev.stopPropagation(); openNodeDetail(nd.nodeId); });
    canvas.appendChild(node);
  });

  // edge labels
  edgeLabels.forEach(L => {
    canvas.appendChild(el("span", { class:"edge-label", style:`left:${L.mx}px;top:${L.my}px` }, esc(L.type)));
  });

  viewport.appendChild(canvas);
  host.appendChild(viewport);

  // zoom / fit controls
  const controls = el("div", { class:"graph-controls", role:"group", "aria-label":"缩放控制" });
  const fitIcon = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9V5a1 1 0 0 1 1-1h4M20 9V5a1 1 0 0 0-1-1h-4M4 15v4a1 1 0 0 0 1 1h4M20 15v4a1 1 0 0 0-1 1h-4"/></svg>';
  const mkBtn = (label, title, fn) => { const b = el("button", { type:"button", "aria-label":title, title }, label); b.addEventListener("click", (ev) => { ev.stopPropagation(); fn(); }); return b; };
  controls.appendChild(mkBtn("+", "放大", () => zoomGraphAt(viewport, 1.2, host.clientWidth/2, host.clientHeight/2)));
  controls.appendChild(el("div", { class:"graph-zoom-label", id:"graphZoomLabel" }, Math.round(state.graphView.zoom*100)+"%"));
  controls.appendChild(mkBtn("−", "缩小", () => zoomGraphAt(viewport, 1/1.2, host.clientWidth/2, host.clientHeight/2)));
  controls.appendChild(mkBtn(fitIcon, "适应窗口", () => fitGraphView(host, viewport, W, H)));
  controls.appendChild(mkBtn("1:1", "实际大小", () => { state.graphView.zoom = 1; state.graphView.fitted = true; applyGraphTransform(viewport); }));
  host.appendChild(controls);

  // node detail drawer (re-opened after a polling re-render when a node stays selected)
  if(state.selectedNodeId && g.nodes.some(nd => nd.nodeId === state.selectedNodeId))
    host.appendChild(renderNodeDetailPanel(detail, g, state.selectedNodeId));

  attachGraphInteractions(host, viewport);
  applyGraphTransform(viewport);
  // auto-fit once when the graph first becomes visible; keep the first view legible on narrow panes.
  if(!state.graphView.fitted) scheduleAutoFit(host, viewport, W, H);

  wrap.appendChild(host);

  const legend = el("div", { class:"graph-legend", "aria-hidden":"false" });
  legend.innerHTML = `
    <span class="lg"><span class="sw done"></span>已完成</span>
    <span class="lg"><span class="sw current"></span>进行中</span>
    <span class="lg"><span class="sw failed"></span>失败</span>
    <span class="lg"><span class="sw stopped"></span>已停止</span>
    <span class="lg"><span class="sw pending"></span>待执行</span>`;
  wrap.appendChild(legend);
  return wrap;
}

/* ===========================================================================
   Compose
   =========================================================================== */
function render(){
  const app = $("#app");
  app.innerHTML = "";
  app.appendChild(renderTopbar());

  const showBanner = state.signedIn && state.scenario === "globalError";
  if(showBanner) app.appendChild(renderBanner());

  if(!state.signedIn){
    app.appendChild(renderLogin());
    return;
  }

  const shell = el("div", { class:"shell"+(showBanner?" has-banner":"") });
  shell.appendChild(renderList());
  shell.appendChild(renderDetail());
  app.appendChild(shell);
}

/* ===========================================================================
   Selection + data loading (real fetch; near-live via polling)
   =========================================================================== */
function selectRun(runId){
  state.selectedRunId = runId;
  state.scenario = "detailLoading";
  state.activeTab = "timeline";
  state.selectedNodeId = null;
  state.graphView = { zoom: 1, panX: 0, panY: 0, fitted: false };
  document.body.setAttribute("data-mobile-view","detail");
  render();
  loadDetail(runId);
}

async function refreshRuns(){
  try {
    const runs = await api("/api/workflow/observatory/runs");
    cache.runs = runs || [];
    if(state.scenario === "listLoading" || state.scenario === "empty" || state.scenario === "globalError"){
      state.scenario = (cache.runs.length === 0) ? "empty" : "normal";
    }
    return true;
  } catch(e){
    if(e.message !== "unauthorized") state.scenario = "globalError";
    return false;
  }
}

async function refreshDetail(runId){
  try {
    const detail = await api("/api/workflow/observatory/runs/" + encodeURIComponent(runId));
    if(detail === null){
      delete cache.details[runId];
      if(state.selectedRunId === runId) state.scenario = "notFound";
      return false;
    }
    cache.details[runId] = detail;
    if(cache.graphs[runId]) cache.graphs[runId].nodeStatus = deriveNodeStatus(cache.graphs[runId], detail);
    if(state.selectedRunId === runId && (state.scenario === "detailLoading" || state.scenario === "notFound")) state.scenario = "normal";
    return true;
  } catch(e){
    if(e.message !== "unauthorized" && state.scenario === "detailLoading") state.scenario = "globalError";
    return false;
  }
}

async function loadDetail(runId){
  await refreshDetail(runId);
  lastDetailSig = detailSig(cache.details[runId]);
  render();
}

async function loadGraph(runId){
  if(!runId || cache.graphs[runId]) return;
  try {
    const g = await api("/api/workflow/observatory/runs/" + encodeURIComponent(runId) + "/graph");
    if(g){
      g.nodeStatus = deriveNodeStatus(g, cache.details[runId]);
      cache.graphs[runId] = g;
      if(state.activeTab === "graph" && state.selectedRunId === runId) render();
    }
  } catch(e){ /* graph view is optional; ignore fetch errors */ }
}

/* ---- near-live polling (~3s, paused when tab hidden); history & live share it ---- */
function runsSig(runs){ return runs.map(r => r.runId + ":" + r.status + ":" + r.updatedAtUtc + ":" + r.stateVersion).join("|"); }
function detailSig(d){ return d ? (d.summary.status + ":" + d.summary.stateVersion + ":" + ((d.timeline && d.timeline.length) || 0)) : "none"; }
let lastRunsSig = "", lastDetailSig = "";

async function poll(){
  if(document.hidden || !state.signedIn) return;
  let changed = false;
  const before = state.scenario;
  await refreshRuns();
  const rs = runsSig(cache.runs);
  if(rs !== lastRunsSig){ lastRunsSig = rs; changed = true; }
  if(state.selectedRunId && state.scenario !== "notFound"){
    await refreshDetail(state.selectedRunId);
    const ds = detailSig(cache.details[state.selectedRunId]);
    if(ds !== lastDetailSig){ lastDetailSig = ds; changed = true; }
  }
  if(state.scenario !== before) changed = true;
  if(changed){ render(); return; }
  const chip = document.getElementById("liveChip");
  if(chip && chip.classList.contains("is-live")){ chip.classList.add("is-polling"); setTimeout(() => chip.classList.remove("is-polling"), 600); }
}
let pollTimer = null;
function startPolling(){ stopPolling(); pollTimer = setInterval(poll, CFG.pollMs); }
function stopPolling(){ if(pollTimer){ clearInterval(pollTimer); pollTimer = null; } }

/* lightweight 1s ticker: refresh relative-time / duration labels in place without
   reflowing the timeline (preserves tool-call expand state + focus). */
function updateRelTimes(){
  const now = nowMs();
  document.querySelectorAll("[data-since]").forEach(node => {
    const iso = node.getAttribute("data-since");
    const base = relTime(parseT(iso), now);
    node.textContent = node.classList.contains("freshness") ? base + " 更新" : base;
  });
  document.querySelectorAll("[data-duration]").forEach(node => {
    node.textContent = fmtDur(now - parseT(node.getAttribute("data-duration")));
  });
}
setInterval(() => { if(state.signedIn && !document.hidden) updateRelTimes(); }, 1000);

document.addEventListener("visibilitychange", () => { if(!document.hidden) poll(); });

/* ===========================================================================
   Init
   =========================================================================== */
applyTheme();
(async function init(){
  try { await completeLoginIfCallback(); }
  catch(e){ console.warn("observatory: login callback failed", e); }
  if(!getToken()){ state.signedIn = false; state.scenario = "login"; render(); return; }
  state.signedIn = true;
  state.scenario = "listLoading";
  render();
  cache.account = toAccount(await fetchUserInfo()) || cache.account;
  await refreshRuns();
  lastRunsSig = runsSig(cache.runs);
  render();
  startPolling();
})();

/* follow system theme changes only when the user hasn't manually chosen a theme */
if(window.matchMedia){
  const mq = window.matchMedia("(prefers-color-scheme: light)");
  if(mq.addEventListener) mq.addEventListener("change", () => { if(!state.theme) render(); });
}
/* re-render on breakpoint cross so the graph relayouts for the new orientation */
let _rw; window.addEventListener("resize", () => { clearTimeout(_rw); _rw = setTimeout(() => { if(state.signedIn && state.scenario === "normal") render(); }, 160); });
</script>
</body>
</html>
""";
}
