namespace Aevatar.Mainnet.Host.Api.Voice;

// Voice console: one inline self-contained HTML page (no wwwroot, no build step), mirroring the
// /workflow/observatory and /workflow/studio precedent. Browser OIDC Authorization Code + PKCE against
// nyxid (reuse of the console-web client; same authority/clientId/scope as observatory/studio, isolated
// PKCE storage + callback route). The page is the anonymous static shell; the app is gated behind login
// exactly like its siblings, and carries the shared suite navigation so the five console pages move as one.
//
// This page is a faithful port of the canonical ~/Code/voice.html high-fidelity design: the console shell
// (global header + secondary nav + main), and its views — Agents, Tools & Connectors, Channels,
// Display Outputs, Live Sessions, Test, Settings. The Agents view is the CORRECT model: it is centred on the
// caller's DEFAULT voice agent, the zero-config primary flow that `/ws/voice` auto-provisions with NO actor
// id (observed at runtime: actor=nyxid-chat-<caller>, module=voice_presence_openai, brain=OpenAI Realtime,
// per-session NyxID ephemeral minted via slug openai-realtime). Configuring/enabling voice on a specific
// actor is a SECONDARY/advanced action — the one real voice HTTP write that exists today
// (POST /api/scopes/{scopeId}/gagent-actors/{actorId}/voice-presence/enable) — never a prerequisite.
//
// Honest wiring (aevatar exposes no voice read GETs — no default-agent GET, no provider/model listing, no
// list-sessions, no agent-config write, no connectors GET, no display/sink or WHIP-ingress surface). So the
// design's features that aevatar cannot back render as honest disabled/empty states rather than mock data:
//   - Default-agent config (provider/model, persona, VAD, tools): shown reflecting the REAL runtime defaults
//     (module voice_presence_openai; default tool allowlist nyxid_proxy / nyxid_status / nyxid_services /
//     nyxid_catalog / ornn_search_skills / use_skill — VOICE_TOOL_ALLOWLIST), read-only — no config write API.
//   - Live Sessions / Test live panel: no list-sessions read surface → empty/disabled state.
//   - WHIP / WebRTC ingress: voice-service decomposition sub-project ② — disabled stub.
//   - Display Outputs / vp-control side-channel: sub-project ③ — disabled stub.
//   - Routing policy: no read/write surface from this page → described, not fabricated.
// Real surfaces actually called: GET /api/studio/context (scope chip) and the enable POST above.
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
     aevatar 语音在场 / Voice Presence 控制台 — 控制台套件成员
     高保真界面 + 交互 + 状态。framework-agnostic（语义化 HTML + CSS 变量 + vanilla JS）。
     设计令牌与 observatory / studio / channels 一字不差（暗色默认 + 浅色变体）。
     忠实移植 ~/Code/voice.html 的布局 / 视图 / 组件；aevatar 无法支撑处给出诚实占位，不编造数据。
     ========================================================================= */
  :root {
    --bg:#0f1115; --bg-grad:radial-gradient(1200px 600px at 78% -10%,#161b26 0%,#0f1115 60%);
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
    --nav-w:228px; --topbar-h:56px; --brand-w:268px; color-scheme:dark;
  }
  @media (prefers-color-scheme: light) {
    :root:not([data-theme]) {
      --bg:#f5f7fa; --bg-grad:radial-gradient(1100px 560px at 80% -12%,#eef2f8 0%,#f5f7fa 58%);
      --panel:#ffffff; --panel-2:#f3f5f9; --panel-3:#eaeef4; --border:#dde2ea; --border-soft:#e7ebf1;
      --fg:#1c2128; --fg-strong:#0b0f14; --muted:#57606b; --muted-2:#828b97;
      --accent:#2f63e0; --accent-ink:#ffffff; --accent-soft:rgba(47,99,224,.10); --accent-line:rgba(47,99,224,.34);
      --ok:#1a7f37; --ok-soft:rgba(26,127,55,.12); --warn:#9a6700; --warn-soft:rgba(154,103,0,.12);
      --err:#cf222e; --err-soft:rgba(207,34,46,.10); --run:#2f6fed; --run-soft:rgba(47,111,237,.12);
      --neutral:#57606b; --neutral-soft:rgba(87,96,107,.10);
      --shadow:0 10px 30px rgba(16,24,40,.10); --shadow-sm:0 1px 2px rgba(16,24,40,.06); color-scheme:light;
    }
  }
  :root[data-theme="light"] {
    --bg:#f5f7fa; --bg-grad:radial-gradient(1100px 560px at 80% -12%,#eef2f8 0%,#f5f7fa 58%);
    --panel:#ffffff; --panel-2:#f3f5f9; --panel-3:#eaeef4; --border:#dde2ea; --border-soft:#e7ebf1;
    --fg:#1c2128; --fg-strong:#0b0f14; --muted:#57606b; --muted-2:#828b97;
    --accent:#2f63e0; --accent-ink:#ffffff; --accent-soft:rgba(47,99,224,.10); --accent-line:rgba(47,99,224,.34);
    --ok:#1a7f37; --ok-soft:rgba(26,127,55,.12); --warn:#9a6700; --warn-soft:rgba(154,103,0,.12);
    --err:#cf222e; --err-soft:rgba(207,34,46,.10); --run:#2f6fed; --run-soft:rgba(47,111,237,.12);
    --neutral:#57606b; --neutral-soft:rgba(87,96,107,.10);
    --shadow:0 10px 30px rgba(16,24,40,.10); --shadow-sm:0 1px 2px rgba(16,24,40,.06); color-scheme:light;
  }
  :root[data-theme="dark"] { color-scheme:dark; }

  /* ---- base ---- */
  * { box-sizing:border-box; }
  html,body { height:100%; }
  body { margin:0; background:var(--bg); background-image:var(--bg-grad); background-attachment:fixed; color:var(--fg);
    font:14px/1.55 var(--sans); -webkit-font-smoothing:antialiased; overflow:hidden; }
  ::selection { background:var(--accent-soft); }
  button { font:inherit; color:inherit; cursor:pointer; }
  a { color:var(--accent); text-decoration:none; } a:hover { text-decoration:underline; }
  :focus-visible { outline:none; box-shadow:var(--ring); border-radius:var(--r-sm); }
  .mono { font-family:var(--mono); font-variant-numeric:tabular-nums; }
  .scroll::-webkit-scrollbar { width:10px; height:10px; }
  .scroll::-webkit-scrollbar-thumb { background:var(--panel-3); border-radius:999px; border:2px solid transparent; background-clip:content-box; }

  /* ---- status primitives ---- */
  .dot { width:9px; height:9px; border-radius:50%; flex:0 0 auto; }
  .dot.s-ok,.dot.s-active,.dot.s-ready,.dot.s-connected,.dot.s-reachable { background:var(--ok); }
  .dot.s-run,.dot.s-listening,.dot.s-pending { background:var(--run); animation:pulse 2.4s ease-out infinite; }
  .dot.s-speaking { background:var(--accent); animation:pulse 1.6s ease-out infinite; }
  .dot.s-thinking,.dot.s-degraded,.dot.s-warn { background:var(--warn); }
  .dot.s-err,.dot.s-offline,.dot.s-unreachable,.dot.s-error,.dot.s-failed { background:var(--err); }
  .dot.s-idle,.dot.s-unknown { background:var(--neutral); }
  @keyframes pulse { 0%{box-shadow:0 0 0 0 var(--run-soft);} 70%{box-shadow:0 0 0 7px transparent;} 100%{box-shadow:0 0 0 0 transparent;} }

  .badge { display:inline-flex; align-items:center; gap:6px; padding:3px 9px; border-radius:var(--r-pill); font-size:12px; font-weight:600; border:1px solid transparent; white-space:nowrap; }
  .badge .dot { width:7px; height:7px; }
  .b-ok,.b-active,.b-ready,.b-connected,.b-reachable { color:var(--ok); background:var(--ok-soft); border-color:color-mix(in oklab,var(--ok) 30%,transparent); }
  .b-run,.b-pending,.b-listening { color:var(--run); background:var(--run-soft); border-color:color-mix(in oklab,var(--run) 30%,transparent); }
  .b-warn,.b-thinking,.b-degraded { color:var(--warn); background:var(--warn-soft); border-color:color-mix(in oklab,var(--warn) 30%,transparent); }
  .b-err,.b-offline,.b-error,.b-failed,.b-unreachable { color:var(--err); background:var(--err-soft); border-color:color-mix(in oklab,var(--err) 30%,transparent); }
  .b-idle,.b-muted { color:var(--muted); background:var(--neutral-soft); border-color:var(--border); }
  .owner { font-family:var(--mono); font-size:10px; font-weight:700; letter-spacing:.03em; padding:1px 6px; border-radius:5px; }
  .owner.ACTOR { color:var(--accent); background:var(--accent-soft); border:1px solid var(--accent-line); }
  .owner.CLIENT { color:var(--warn); background:var(--warn-soft); border:1px solid color-mix(in oklab,var(--warn) 35%,transparent); }

  .copybtn { display:inline-flex; align-items:center; gap:5px; font-size:11.5px; color:var(--muted); background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r-sm); padding:4px 9px; white-space:nowrap; }
  .copybtn:hover { color:var(--fg); border-color:var(--muted-2); }
  .copybtn.done { color:var(--ok); border-color:color-mix(in oklab,var(--ok) 40%,transparent); }

  /* =========================================================================
     Global header + shared suite navigation (5 console pages move as one)
     ========================================================================= */
  .topbar { position:sticky; top:0; z-index:40; height:var(--topbar-h); display:flex; align-items:center; gap:12px; padding:0 14px;
    background:color-mix(in oklab,var(--panel) 90%,transparent); backdrop-filter:saturate(140%) blur(12px); -webkit-backdrop-filter:saturate(140%) blur(12px);
    border-bottom:1px solid var(--border); }
  /* Fixed-width brand region so the shared suite-nav's start position is INDEPENDENT of the active
     page title length — the five entries no longer shift/jitter when switching pages. (Layout fix.) */
  .brand { display:flex; align-items:center; gap:10px; flex:0 0 var(--brand-w); width:var(--brand-w); min-width:0; overflow:hidden; }
  .brand-mark { width:26px; height:26px; border-radius:7px; display:grid; place-items:center; color:#fff; box-shadow:var(--shadow-sm); flex:0 0 auto;
    background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); }
  .brand-name { font-weight:650; letter-spacing:-.01em; color:var(--fg-strong); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
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

  .scope-chip { display:inline-flex; align-items:center; gap:7px; padding:4px 10px; border-radius:var(--r-pill); background:var(--panel-2); border:1px solid var(--border); font-size:12px; color:var(--muted); white-space:nowrap; }
  .scope-chip .sid { font-family:var(--mono); font-size:11.5px; color:var(--fg); }
  .scope-chip svg { color:var(--ok); }
  .spacer { flex:1 1 auto; }
  .iconbtn { width:34px; height:34px; display:grid; place-items:center; background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r); color:var(--muted); transition:color .15s,border-color .15s; flex:0 0 auto; }
  .iconbtn:hover { color:var(--fg); border-color:var(--muted-2); }
  .account { display:inline-flex; align-items:center; gap:8px; padding:4px 6px 4px 4px; border-radius:var(--r-pill); background:var(--panel-2); border:1px solid var(--border); flex:0 0 auto; }
  .avatar { width:24px; height:24px; border-radius:50%; flex:0 0 auto; display:grid; place-items:center; font-size:12px; font-weight:700; color:#fff; background:linear-gradient(140deg,#6f9bff,#9b6bff); }
  .account .who { font-size:12.5px; color:var(--fg); max-width:140px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .linkbtn { background:transparent; border:0; color:var(--accent); font-size:12.5px; font-weight:600; padding:4px 6px; border-radius:var(--r-sm); }
  .linkbtn:hover { text-decoration:underline; }

  /* =========================================================================
     Console shell : nav + main
     ========================================================================= */
  .shell { display:grid; grid-template-columns:var(--nav-w) minmax(0,1fr); height:calc(100dvh - var(--topbar-h)); min-height:0; }
  .nav { border-right:1px solid var(--border); padding:12px 10px; display:flex; flex-direction:column; gap:2px; overflow-y:auto; background:color-mix(in oklab,var(--panel) 40%,transparent); }
  .nav-h { font-size:10.5px; font-weight:700; letter-spacing:.07em; text-transform:uppercase; color:var(--muted-2); padding:10px 10px 6px; }
  .nav-item { display:flex; align-items:center; gap:10px; padding:8px 10px; border-radius:var(--r); border:1px solid transparent; color:var(--muted); background:transparent; width:100%; text-align:left; font-size:13.5px; font-weight:550; }
  .nav-item:hover { background:var(--panel-2); color:var(--fg); }
  .nav-item[aria-current="page"] { background:var(--accent-soft); border-color:var(--accent-line); color:var(--fg-strong); }
  .nav-item svg { width:17px; height:17px; flex:0 0 auto; opacity:.9; }
  .nav-item .nv-count { margin-left:auto; font-family:var(--mono); font-size:11px; color:var(--muted-2); }
  .nav-item[aria-current="page"] .nv-count { color:var(--accent); }
  .nav-item .nv-live { margin-left:auto; display:inline-flex; align-items:center; gap:5px; font-size:11px; color:var(--run); }

  .main { overflow-y:auto; min-height:0; }
  .view { max-width:1100px; margin:0 auto; padding:22px 28px 64px; }
  .view.wide { max-width:1340px; }

  .page-head { display:flex; align-items:flex-start; gap:14px; flex-wrap:wrap; margin-bottom:6px; }
  .page-head .ph-l { flex:1 1 auto; min-width:0; }
  .page-h { font-size:21px; font-weight:740; letter-spacing:-.02em; color:var(--fg-strong); margin:0; display:flex; align-items:center; gap:11px; }
  .page-sub { color:var(--muted); font-size:13.5px; margin:8px 0 0; line-height:1.6; max-width:74ch; }
  .ph-actions { display:flex; align-items:center; gap:9px; flex-wrap:wrap; }

  /* panels */
  .panel { border:1px solid var(--border); border-radius:var(--r-lg); background:var(--panel); margin-top:18px; overflow:hidden; }
  .panel-head { display:flex; align-items:center; gap:10px; padding:13px 16px; border-bottom:1px solid var(--border-soft); }
  .panel-head .ph-ic { width:28px; height:28px; border-radius:8px; display:grid; place-items:center; color:var(--accent); background:var(--accent-soft); flex:0 0 auto; }
  .panel-head h2 { font-size:14px; font-weight:660; color:var(--fg-strong); margin:0; }
  .panel-head .ph-desc { font-size:12px; color:var(--muted-2); margin-top:1px; }
  .panel-head .ph-r { margin-left:auto; display:flex; align-items:center; gap:9px; }
  .panel-body { padding:16px; }
  .panel-body.flush { padding:0; }
  .row2 { display:grid; grid-template-columns:1fr 1fr; gap:16px; }
  .grid-2 { display:grid; grid-template-columns:1fr 1fr; gap:18px; align-items:start; }

  /* forms */
  .field { margin-bottom:15px; }
  .field:last-child { margin-bottom:0; }
  .field > label { display:block; font-size:12.5px; font-weight:600; color:var(--fg); margin-bottom:6px; }
  .field .help { font-size:11.5px; color:var(--muted-2); margin-top:6px; line-height:1.5; }
  .input,.textarea,.sel select { width:100%; background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r); padding:9px 11px; color:var(--fg-strong); font-family:var(--mono); font-size:13px; outline:none; transition:border-color .15s,box-shadow .15s; }
  .textarea { font-family:var(--sans); line-height:1.6; resize:vertical; min-height:84px; }
  .input:focus,.textarea:focus,.sel select:focus { border-color:var(--accent-line); box-shadow:0 0 0 3px var(--accent-soft); }
  .input.bad { border-color:color-mix(in oklab,var(--err) 55%,var(--border)); }
  .input:disabled,.textarea:disabled,.sel select:disabled { opacity:.6; cursor:default; }
  .sel { position:relative; }
  .sel select { appearance:none; padding-right:34px; }
  .sel .chev { position:absolute; right:11px; top:50%; transform:translateY(-50%); color:var(--muted-2); pointer-events:none; }

  .toggle { position:relative; width:38px; height:22px; border-radius:999px; background:var(--panel-3); border:1px solid var(--border); flex:0 0 auto; transition:background .15s; }
  .toggle::after { content:""; position:absolute; left:2px; top:1px; width:18px; height:18px; border-radius:50%; background:var(--muted); transition:transform .15s,background .15s; }
  .toggle[aria-pressed="true"] { background:var(--accent-soft); border-color:var(--accent-line); }
  .toggle[aria-pressed="true"]::after { transform:translateX(16px); background:var(--accent); }
  .toggle:disabled { opacity:.55; cursor:default; }
  .togline { display:flex; align-items:center; gap:12px; }
  .togline .tl-t { font-size:13px; color:var(--fg); font-weight:550; }
  .togline .tl-s { font-size:11.5px; color:var(--muted-2); margin-top:1px; }
  .togline .spacer { flex:1; }

  input[type=range] { -webkit-appearance:none; width:100%; height:4px; border-radius:999px; background:var(--panel-3); outline:none; }
  input[type=range]::-webkit-slider-thumb { -webkit-appearance:none; width:16px; height:16px; border-radius:50%; background:var(--accent); border:2px solid var(--panel); box-shadow:var(--shadow-sm); cursor:pointer; }
  input[type=range]:disabled { opacity:.55; }
  .slider-row { display:flex; align-items:center; gap:14px; }
  .slider-row .sval { font-family:var(--mono); font-size:12.5px; color:var(--fg-strong); min-width:42px; text-align:right; }

  /* buttons */
  .btn { display:inline-flex; align-items:center; justify-content:center; gap:8px; padding:8px 14px; border-radius:var(--r); font-size:13px; font-weight:640; border:1px solid transparent; transition:filter .15s,border-color .15s,background .15s; white-space:nowrap; }
  .btn-primary { background:var(--accent); color:var(--accent-ink); } .btn-primary:hover { filter:brightness(1.08); }
  .btn-ghost { background:var(--panel-2); border-color:var(--border); color:var(--fg); } .btn-ghost:hover { border-color:var(--muted-2); }
  .btn-danger { background:transparent; border-color:color-mix(in oklab,var(--err) 45%,transparent); color:var(--err); } .btn-danger:hover { background:var(--err-soft); }
  .btn-sm { padding:5px 10px; font-size:12px; }
  .btn:disabled { opacity:.45; cursor:default; }
  .btn-block { width:100%; }
  .spin { width:13px; height:13px; border-radius:50%; border:1.8px solid color-mix(in oklab,var(--accent-ink) 45%,transparent); border-top-color:var(--accent-ink); animation:spin .7s linear infinite; }
  @keyframes spin { to { transform:rotate(360deg); } }

  /* banners / callouts */
  .banner { display:flex; gap:11px; padding:12px 14px; border-radius:var(--r); font-size:13px; line-height:1.6; margin-bottom:16px; }
  .banner svg { flex:0 0 auto; margin-top:2px; }
  .banner .bt { font-weight:650; color:var(--fg-strong); }
  .banner .bb { color:var(--muted); }
  .banner.info { background:var(--accent-soft); border:1px solid var(--accent-line); } .banner.info svg { color:var(--accent); }
  .banner.warn { background:var(--warn-soft); border:1px solid color-mix(in oklab,var(--warn) 38%,transparent); } .banner.warn svg { color:var(--warn); }
  .banner.err { background:var(--err-soft); border:1px solid color-mix(in oklab,var(--err) 38%,transparent); } .banner.err svg { color:var(--err); }
  .banner.ok { background:var(--ok-soft); border:1px solid color-mix(in oklab,var(--ok) 36%,transparent); } .banner.ok svg { color:var(--ok); }
  .banner .ba { margin-left:auto; display:flex; gap:8px; align-items:center; }

  .kv { display:flex; justify-content:space-between; gap:14px; padding:9px 0; font-size:12.5px; border-bottom:1px solid var(--border-soft); }
  .kv:last-child { border-bottom:0; }
  .kv .k { color:var(--muted); } .kv .v { color:var(--fg); font-family:var(--mono); font-size:12px; text-align:right; word-break:break-all; }
  .kv .v.plain { font-family:var(--sans); }

  .crow { display:grid; grid-template-columns:1fr auto; gap:12px; align-items:center; padding:10px 12px; border:1px solid var(--border); border-radius:var(--r); background:var(--panel-2); margin-bottom:8px; }
  .crow .ck { font-size:11px; color:var(--muted-2); margin-bottom:3px; } .crow .cv { font-family:var(--mono); font-size:12px; color:var(--fg-strong); word-break:break-all; }

  .empty { display:flex; flex-direction:column; align-items:center; justify-content:center; gap:11px; text-align:center; color:var(--muted); padding:50px 24px; border:1px dashed var(--border); border-radius:var(--r-lg); margin-top:18px; }
  .empty .ic { width:42px; height:42px; color:var(--muted-2); opacity:.8; }
  .empty .et { font-weight:650; color:var(--fg); font-size:15px; }
  .empty .es { font-size:13px; max-width:380px; line-height:1.55; }

  /* list rows (connectors / sinks / clients / sessions) */
  .lrow { display:grid; grid-template-columns:auto 1fr auto; gap:13px; align-items:center; padding:12px 14px; border:1px solid var(--border); border-radius:var(--r); background:var(--panel); margin-bottom:8px; }
  .lrow .li { width:32px; height:32px; border-radius:9px; display:grid; place-items:center; color:var(--accent); background:var(--accent-soft); flex:0 0 auto; }
  .lrow .ln { font-weight:620; color:var(--fg-strong); font-size:13.5px; }
  .lrow .lm { font-size:12px; color:var(--muted); margin-top:2px; display:flex; gap:8px; align-items:center; flex-wrap:wrap; }
  .lrow .lm .mono { font-family:var(--mono); font-size:11.5px; color:var(--muted-2); }
  .lrow .lr { display:flex; align-items:center; gap:10px; }

  /* tool allowlist */
  .tool-grp { border:1px solid var(--border); border-radius:var(--r); background:var(--panel); margin-bottom:10px; overflow:hidden; }
  .tool-grp-h { display:flex; align-items:center; gap:11px; padding:11px 13px; background:var(--panel); border-bottom:1px solid var(--border-soft); }
  .tool-grp-h .gi { width:28px; height:28px; border-radius:8px; display:grid; place-items:center; color:var(--accent); background:var(--accent-soft); }
  .tool-grp.off { opacity:.66; }
  .tool-line { display:flex; align-items:center; gap:11px; padding:10px 13px; border-bottom:1px solid var(--border-soft); }
  .tool-line:last-child { border-bottom:0; }
  .tool-line .tn { font-family:var(--mono); font-size:12.5px; color:var(--fg-strong); }
  .tool-line .td { font-size:11.5px; color:var(--muted-2); }
  .tool-line .tr { margin-left:auto; display:flex; align-items:center; gap:10px; }
  .chk { width:18px; height:18px; border-radius:5px; border:1.5px solid var(--border); background:var(--panel-2); display:grid; place-items:center; color:transparent; flex:0 0 auto; }
  .chk[aria-checked="true"] { background:var(--accent); border-color:var(--accent); color:var(--accent-ink); }
  .chk[aria-disabled="true"] { opacity:.7; cursor:default; }

  /* contract rows + inline code (transport / honest stubs) */
  .contract { display:grid; gap:8px; }
  .contract .row { display:flex; gap:10px; align-items:baseline; font-size:12.5px; }
  .contract .row .lbl { flex:0 0 150px; color:var(--muted); }
  .contract .row .val { color:var(--fg); font-family:var(--mono); font-size:12px; word-break:break-word; }
  code.inline { font-family:var(--mono); font-size:12px; background:var(--panel-2); border:1px solid var(--border-soft); border-radius:5px; padding:1px 6px; color:var(--fg); }

  /* result receipt (enable action) */
  .result { margin-top:14px; border-radius:var(--r); border:1px solid var(--border-soft); padding:13px 14px; }
  .result.ok  { background:var(--ok-soft); border-color:color-mix(in oklab,var(--ok) 34%,transparent); }
  .result.err { background:var(--err-soft); border-color:color-mix(in oklab,var(--err) 38%,transparent); }
  .result-head { display:flex; gap:9px; align-items:flex-start; }
  .result-head svg { flex:0 0 auto; margin-top:1px; }
  .result.ok .result-head svg { color:var(--ok); } .result.err .result-head svg { color:var(--err); }
  .result-l { font-weight:650; font-size:13px; }
  .result.ok .result-l { color:var(--ok); } .result.err .result-l { color:var(--err); }
  .result-m { color:var(--muted); font-size:12.5px; margin-top:3px; line-height:1.6; }
  .result-grid { margin-top:11px; }

  /* test panel */
  .test-stage { display:grid; place-items:center; padding:40px 20px; }
  .mic-btn { width:84px; height:84px; border-radius:50%; border:0; display:grid; place-items:center; color:#fff; box-shadow:var(--shadow);
    background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); }
  .mic-btn:hover { filter:brightness(1.07); }

  /* host-gated / disabled stub lock */
  .gated .lock { display:inline-flex; align-items:center; gap:6px; font-size:11px; font-weight:700; letter-spacing:.04em; color:var(--warn); background:var(--warn-soft); border:1px solid color-mix(in oklab,var(--warn) 35%,transparent); border-radius:var(--r-pill); padding:3px 9px; }
  .card.stub { opacity:.96; }
  .card.stub .panel-head { border-bottom-style:dashed; }

  /* routing table */
  .rtable { width:100%; border-collapse:collapse; font-size:13px; }
  .rtable th { text-align:left; font-size:11px; letter-spacing:.04em; text-transform:uppercase; color:var(--muted-2); font-weight:600; padding:10px 12px; border-bottom:1px solid var(--border); }
  .rtable td { padding:11px 12px; border-bottom:1px solid var(--border-soft); color:var(--fg); }
  .rtable td.mono { font-family:var(--mono); font-size:12px; }
  .rtable tr:last-child td { border-bottom:0; }

  /* =========================================================================
     Login gate (mirrors observatory/studio OIDC bearer-token gate)
     ========================================================================= */
  .login { min-height:calc(100dvh - var(--topbar-h)); display:grid; place-items:center; padding:24px; }
  .login-card { width:min(420px,100%); background:var(--panel); border:1px solid var(--border); border-radius:var(--r-lg); padding:32px 30px; box-shadow:var(--shadow); text-align:center; }
  .login-mark { width:52px; height:52px; border-radius:14px; margin:0 auto 18px; background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); display:grid; place-items:center; color:#fff; box-shadow:var(--shadow); }
  .login h1 { font-size:19px; font-weight:700; letter-spacing:-.01em; color:var(--fg-strong); margin:0 0 8px; }
  .login p { color:var(--muted); font-size:13.5px; margin:0 auto 22px; max-width:34ch; line-height:1.6; }
  .login .btn-primary { width:100%; padding:11px 16px; font-size:14px; }
  .login-foot { margin-top:16px; font-size:11.5px; color:var(--muted-2); display:inline-flex; align-items:center; gap:6px; }

  /* =========================================================================
     Responsive
     ========================================================================= */
  @media (max-width:1080px) { .grid-2,.row2 { grid-template-columns:1fr; } }
  @media (max-width:880px) {
    :root { --nav-w:60px; --brand-w:54px; }
    .nav-item span.nlabel, .nav-h, .nav-item .nv-count, .nav-item .nv-live span { display:none; }
    .nav-item { justify-content:center; }
    .brand-name { display:none; }
  }
  @media (max-width:760px) {
    .suite-nav .nav-label { display:none; } .suite-nav a { padding:6px 8px; }
    .scope-chip { display:none; } .account .who { display:none; }
  }
  @media (max-width:620px) { .view { padding:18px 16px 56px; } }
  @media (prefers-reduced-motion: reduce) {
    *,*::before,*::after { animation-duration:.001ms !important; animation-iteration-count:1 !important; transition-duration:.001ms !important; }
    .dot.s-run,.dot.s-speaking,.dot.s-listening,.dot.s-pending { animation:none; box-shadow:0 0 0 3px var(--run-soft); }
  }
</style>
</head>
<body>
  <div id="app"></div>

<script>
/* ===========================================================================
   数据契约（与现有 console 套件一致，无新增后端）：
     - GET  /api/studio/context → {scopeId, scopeResolved, scopeSource}（scope chip + 预填 scope）
     - POST /api/scopes/{scopeId}/gagent-actors/{actorId}/voice-presence/enable?agentKind=...
            body: {moduleName}  →  202 受理回执 / 4xx-5xx {error,detail}   ← 次级/进阶动作（在指定 actor 上启用）
   主路径（零配置默认语音 agent）由 /ws/voice 自动开通（无需 actor id；运行时观测：
     actor=nyxid-chat-<caller>，module=voice_presence_openai，大脑=OpenAI Realtime，
     每会话经 NyxID slug openai-realtime mint ephemeral）。该路径无 HTTP 写入端点，本页只描述、不编造。
   语音目前没有只读 GET（默认 agent / 会话 / provider / 连接器 列举），也无 agent 配置写入端点，因此：
     默认 agent 的 provider/persona/VAD/工具按真实运行时默认值「只读」呈现；
     实时会话 / WHIP 入站(②) / 显示侧信道 vp-control(③) 给出诚实占位（disabled/empty），不展示虚构数据。
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

/* Real runtime facts (NOT mock UI data — these mirror the deployed defaults):
   - default voice module = voice_presence_openai (provider openai), the zero-config /ws/voice agent.
   - default tool allowlist (PolicyAwareVoiceEndpoints.DefaultVoiceToolAllowlist / VOICE_TOOL_ALLOWLIST).
   These are surfaced read-only; aevatar exposes no default-agent config GET or write today. */
const DEFAULT_MODULE = "voice_presence_openai";
const DEFAULT_TOOL_ALLOWLIST = ["nyxid_proxy","nyxid_status","nyxid_services","nyxid_catalog","ornn_search_skills","use_skill"];

const state = {
  signedIn:false,
  theme: localStorage.getItem("voice-theme") || null,
  view:"agents",
  scopeId:null, scopeResolved:false, scopeSource:null,
  // advanced "enable on a specific actor" action (the one real voice write):
  enableOpen:false,
  submitting:false,
  result:null,                 // { ok, label, message, detail:{} }
  form:{ scopeId:"", actorId:"", agentKind:"", moduleName:DEFAULT_MODULE }
};
let accountLabel = "已登录";

function applyTheme(){ if(state.theme==="light"||state.theme==="dark")document.documentElement.setAttribute("data-theme",state.theme); else document.documentElement.removeAttribute("data-theme"); }
function effDark(){ if(state.theme)return state.theme==="dark"; return !window.matchMedia||!window.matchMedia("(prefers-color-scheme: light)").matches; }

const ICON={
  lock:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
  lockLg:'<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
  sun:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="12" cy="12" r="4.2"/><path d="M12 2v2.4M12 19.6V22M2 12h2.4M19.6 12H22M4.6 4.6l1.7 1.7M17.7 17.7l1.7 1.7M19.4 4.6l-1.7 1.7M6.3 17.7l-1.7 1.7"/></svg>',
  moon:'<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A8.5 8.5 0 1 1 11.2 3a6.6 6.6 0 0 0 9.8 9.8Z"/></svg>',
  check:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12.5 4.2 4.2L19 7"/></svg>',
  okc:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="m8.5 12.2 2.4 2.4 4.6-4.8"/></svg>',
  alert:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M12 8.5v4.5M12 16.2v.2"/><path d="M10.3 3.9 2.5 17.5A2 2 0 0 0 4.2 20.5h15.6a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg>',
  info:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 7.6v.2"/></svg>',
  spark:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5 18 18M18 6l-2.5 2.5M8.5 15.5 6 18"/></svg>',
  chev:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9 6 6 6-6"/></svg>',
  ext:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/></svg>',
  copy:'<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a1 1 0 0 1 1-1h9"/></svg>',
  refresh:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M20 11a8 8 0 1 0-2.3 5.7M20 5v6h-6"/></svg>',
  add:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>',
  speaker:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9v6h4l5 4V5L8 9H4ZM17 9a4 4 0 0 1 0 6"/></svg>',
  link:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M10 14a4 4 0 0 0 5.6 0l3-3a4 4 0 1 0-5.6-5.6L11 7M14 10a4 4 0 0 0-5.6 0l-3 3A4 4 0 1 0 11 18.6L12.8 17"/></svg>',
  // nav + brand glyphs
  mic:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M8.5 21h7"/></svg>',
  agent:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="8" width="14" height="11" rx="3"/><path d="M12 3v5M8.5 13h.01M15.5 13h.01M9 17h6"/></svg>',
  sessions:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h3l2-6 4 14 3-9 2 3h4"/></svg>',
  tools:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M14.5 6a3.5 3.5 0 0 0-4.6 4.4L4 16.3 6.7 19l5.9-5.9A3.5 3.5 0 1 0 14.5 6Z"/></svg>',
  channels:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="12" r="2.5"/><circle cx="18" cy="6" r="2.5"/><circle cx="18" cy="18" r="2.5"/><path d="M8.2 11 15.8 7M8.2 13l7.6 4"/></svg>',
  display:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8M12 17v4"/></svg>',
  routing:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3v6a3 3 0 0 0 3 3h6a3 3 0 0 1 3 3v6"/><circle cx="6" cy="4" r="1.6"/><circle cx="18" cy="20" r="1.6"/></svg>',
  test:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="3" width="6" height="10" rx="3"/><path d="M6 11a6 6 0 0 0 12 0M12 17v3"/></svg>',
  settings:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"><circle cx="12" cy="12" r="3.2"/><path d="M12 2.5v2.6M12 18.9v2.6M21.5 12h-2.6M5.1 12H2.5M18.7 5.3l-1.8 1.8M7.1 16.9l-1.8 1.8M18.7 18.7l-1.8-1.8M7.1 7.1 5.3 5.3"/></svg>',
  cast:'<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8V6a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6"/><path d="M3 16a4 4 0 0 1 4 4M3 12a8 8 0 0 1 8 8"/><circle cx="3.5" cy="19.5" r="1"/></svg>',
  wave:'<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M4 12h0M8 8v8M12 5v14M16 8v8M20 12h0"/></svg>'
};
function navIcon(id){ return ({agents:ICON.agent,tools:ICON.tools,channels:ICON.channels,outputs:ICON.display,sessions:ICON.sessions,test:ICON.test,settings:ICON.settings})[id]; }

/* ===========================================================================
   Shared suite navigation — the five console pages move as one.
   Same five links/labels/order reused across observatory/studio/schedules/
   channels/voice; the host page passes its own active key. The fixed-width
   .brand region (CSS) keeps these entry positions stable across pages.
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
      '<div style="min-width:0"><div class="brand-name">语音在场 <span class="brand-sub">Voice Presence</span></div></div>'+
    '</div>'+
    suiteNavHtml("voice")+
    '<div class="spacer"></div>'+
    (state.signedIn?scopeChipHtml():"");
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
    '<p>配置默认语音 agent、查看接入与显示能力 —— 登录后即可使用。</p>';
  const btn = el("button", { class:"btn-primary", id:"signinBtn" }, "使用 nyxid 登录");
  btn.addEventListener("click", () => { beginLogin(); });
  card.appendChild(btn);
  card.appendChild(el("div", { class:"login-foot" }, ICON.lock+'<span>采用 OIDC bearer-token 鉴权 · 通过 nyxid 账户登录</span>'));
  wrap.appendChild(card);
  return wrap;
}

/* ===========================================================================
   Shared view bits
   =========================================================================== */
const NAV=[
  ["agents","Agents"],["tools","Tools & Connectors"],["channels","Channels"],
  ["outputs","Display Outputs"],["sessions","Live Sessions"],["test","Test"],["settings","Settings"]
];
function renderSideNav(){
  const nav=el("nav",{class:"nav scroll","aria-label":"语音控制台导航"});
  nav.appendChild(el("div",{class:"nav-h"},"语音服务"));
  NAV.forEach(([id,label])=>{
    const it=el("button",{class:"nav-item","aria-current":state.view===id?"page":null});
    it.innerHTML=navIcon(id)+'<span class="nlabel">'+label+'</span>';
    it.addEventListener("click",()=>{ state.view=id; render(); });
    nav.appendChild(it);
  });
  return nav;
}
function pageHead(title,sub,actions){
  const h=el("div",{class:"page-head"});
  h.innerHTML='<div class="ph-l"><h1 class="page-h">'+title+'</h1>'+(sub?'<p class="page-sub">'+sub+'</p>':"")+'</div>';
  if(actions){ const a=el("div",{class:"ph-actions"}); actions.forEach(n=>a.appendChild(n)); h.appendChild(a); }
  return h;
}
function panel(icon,title,desc,right,bodyNode,opts){
  opts=opts||{};
  const p=el("section",{class:"panel"+(opts.stub?" stub":"")});
  const head=el("div",{class:"panel-head"});
  head.innerHTML='<span class="ph-ic'+(opts.muted?' muted':'')+'">'+icon+'</span><div><h2>'+title+'</h2>'+(desc?'<div class="ph-desc">'+desc+'</div>':"")+'</div>';
  if(right){ const r=el("div",{class:"ph-r"}); (Array.isArray(right)?right:[right]).forEach(n=>r.appendChild(typeof n==="string"?el("span",{},n):n)); head.appendChild(r); }
  p.appendChild(head);
  const b=el("div",{class:"panel-body"+(opts.flush?" flush":"")}); if(bodyNode)b.appendChild(bodyNode); p.appendChild(b);
  return p;
}
function badge(s,l){ return '<span class="badge b-'+s+'"><span class="dot s-'+s+'"></span>'+l+'</span>'; }
function btn(label,kind,icon,onClick,opts){ opts=opts||{}; const b=el("button",{class:"btn btn-"+kind+(opts.sm?" btn-sm":"")+(opts.block?" btn-block":"")},(icon?icon+" ":"")+'<span>'+label+'</span>'); if(opts.disabled)b.disabled=true; if(onClick)b.addEventListener("click",onClick); return b; }
function bindCopy(b,v){ b.addEventListener("click",async()=>{ try{await navigator.clipboard.writeText(v);}catch(e){const t=document.createElement("textarea");t.value=v;document.body.appendChild(t);t.select();try{document.execCommand("copy");}catch(_){}t.remove();} b.classList.add("done");const s=b.querySelector("span");const o=s?s.textContent:"";if(s)s.textContent="已复制";setTimeout(()=>{b.classList.remove("done");if(s)s.textContent=o;},1300); }); }
function copyRow(label,value){ const r=el("div",{class:"crow"}); r.innerHTML='<div style="min-width:0"><div class="ck">'+esc(label)+'</div><div class="cv">'+esc(value)+'</div></div>'; const b=el("button",{class:"copybtn","aria-label":"复制"},ICON.copy+'<span>复制</span>'); bindCopy(b,value); r.appendChild(b); return r; }
function emptyState(icon,t,s,cta){ const e=el("div",{class:"empty"}); e.innerHTML='<div class="ic">'+icon+'</div><div class="et">'+t+'</div><div class="es">'+s+'</div>'; if(cta)e.appendChild(cta); return e; }
function banner(kind,icon,title,body,actions){ const b=el("div",{class:"banner "+kind}); b.innerHTML=icon+'<div><div class="bt">'+title+'</div><div class="bb">'+body+'</div></div>'; if(actions){ const a=el("div",{class:"ba"}); actions.forEach(n=>a.appendChild(n)); b.appendChild(a); } return b; }

/* ===========================================================================
   View: Agents — the DEFAULT voice agent (zero-config primary flow)
   Centred on the caller's default voice agent that /ws/voice auto-provisions
   with NO actor id. Config (provider/persona/VAD/tools) shown reflecting the
   REAL runtime defaults, read-only (aevatar has no default-agent config write).
   Enabling on a SPECIFIC actor is the secondary/advanced action (the real POST).
   =========================================================================== */
function renderAgents(){
  const v=el("div",{class:"view"});
  v.appendChild(pageHead(
    '默认语音 Agent <span class="badge b-ready" style="font-size:11px"><span class="dot s-ready"></span>零配置可用</span>',
    '终端打开 <code class="inline">/ws/voice</code> 即由本服务<b>自动开通一个默认语音 agent</b>（无需 actor id）：'+
      '可插拔的实时 provider 作大脑、persona、VAD、以及来自 NyxID 连接器的工具。这是首要、零配置的接入方式。'));

  v.appendChild(banner("info",ICON.info,
    "这是零配置的主路径",
    '默认 agent 在首次 <code class="inline">/ws/voice</code> 连接时自动开通（运行时观测：'+
    '<span class="mono">actor=nyxid-chat-&lt;caller&gt;</span> · <span class="mono">module='+esc(DEFAULT_MODULE)+'</span>）。'+
    '无需先「在某个 actor 上启用」——那只是下方的进阶动作。'));

  const grid=el("div",{class:"grid-2"});

  /* provider + model (real default: openai-realtime via module voice_presence_openai) */
  const pm=el("div",{});
  pm.innerHTML=
    '<div class="field"><label>实时 provider / module（可插拔大脑）</label>'+
      '<input class="input" value="'+esc(DEFAULT_MODULE)+'（OpenAI Realtime）" disabled aria-label="provider module" />'+
      '<div class="help">默认 agent 的大脑是可插拔 realtime provider；当前部署默认 <span class="mono">'+esc(DEFAULT_MODULE)+'</span>。'+
        '凭据为<b>每会话经 NyxID slug <span class="mono">openai-realtime</span> mint 的 ephemeral</b>，aevatar 不持有长期密钥。'+
        '本页只读：aevatar 暂无「默认 agent 配置」写入端点。</div></div>'+
    '<div class="field"><label>model</label>'+
      '<input class="input" value="由 provider 决定（如 gpt-realtime）" disabled aria-label="model" /></div>';
  grid.appendChild(panel(ICON.agent,"Provider & Model","绑定实时语音后端",badge("ok","可达"),pm));

  /* persona (read-only; no config write endpoint) */
  const persona=el("div",{});
  const voiceRow=el("div",{class:"field"});
  voiceRow.innerHTML='<label>voice 音色</label>';
  const vline=el("div",{style:"display:flex;gap:10px"});
  const vin=el("input",{class:"input",value:"由 provider/部署默认决定",disabled:"","aria-label":"voice",style:"flex:1"});
  vline.appendChild(vin); vline.appendChild(btn("试听","ghost",ICON.speaker,null,{disabled:true}));
  voiceRow.appendChild(vline);
  persona.appendChild(voiceRow);
  const instr=el("div",{class:"field"});
  instr.innerHTML='<label>系统指令 instructions</label>'+
    '<textarea class="textarea" aria-label="instructions" disabled>由部署的 persona 决定（VoiceSessionConfig 的一部分）。aevatar 暂无配置写入端点，故此处只读。</textarea>'+
    '<div class="help">决定 agent 的角色与口吻。要修改默认 persona，需在 host 侧 provider 配置中调整。</div>';
  persona.appendChild(instr);
  grid.appendChild(panel(ICON.speaker,"Persona","音色 + 系统指令",badge("muted","只读"),persona));
  v.appendChild(grid);

  /* VAD (read-only; provider-managed) */
  const vad=el("div",{});
  vad.appendChild(banner("info",ICON.info,"VAD / 打断由 provider 管理",
    '语音活动检测与 barge-in（打断）在实时 provider 侧处理。aevatar 当前不暴露默认 agent 的 VAD 配置读/写端点，'+
    '故此处不提供可编辑控件，也不展示虚构数值。'));
  v.appendChild(panel(ICON.test,"VAD / 打断","语音活动检测与 barge-in 参数",badge("muted","provider 托管"),vad));

  /* advanced: enable on a specific actor (the one real voice write) */
  v.appendChild(renderEnableAdvanced());
  return v;
}

/* ---- Advanced: enable voice presence on a SPECIFIC actor (real POST) ----- */
function renderEnableAdvanced(){
  const body=el("div",{});
  body.appendChild(el("p",{class:"card-desc",style:"font-size:12.5px;color:var(--muted);line-height:1.6;margin:0 0 12px"},
    '进阶 · 可选。把语音在场模块附加到<b>某个已存在的 GAgent actor</b> 上（而非零配置默认 agent）。'+
    '命令异步受理（202 accepted）；语音会话状态在 capability read model 物化后可见。'));

  if(!state.enableOpen){
    body.appendChild(btn("在指定 actor 上启用…","ghost",ICON.add,()=>{ state.enableOpen=true; render(); }));
    return panel(ICON.agent,"在指定 Actor 上启用语音（进阶）","非零配置默认流程；很少需要",badge("muted","可选"),body);
  }

  body.appendChild(el("div",{class:"field"},'<label>Scope ID<span style="color:var(--err);margin-left:3px">*</span></label><input id="vfScope" class="input" autocomplete="off" spellcheck="false" placeholder="scope_…" value="'+esc(state.form.scopeId)+'" />'));
  body.appendChild(el("div",{class:"field"},'<label>Actor ID<span style="color:var(--err);margin-left:3px">*</span></label><input id="vfActor" class="input" autocomplete="off" spellcheck="false" placeholder="GAgent actor id" value="'+esc(state.form.actorId)+'" />'));
  body.appendChild(el("div",{class:"field"},'<label>Agent kind<span style="color:var(--err);margin-left:3px">*</span></label><input id="vfKind" class="input" autocomplete="off" spellcheck="false" placeholder="例如 role / workflow" value="'+esc(state.form.agentKind)+'" /><div class="help">未知 agentKind 会被后端拒绝（400 invalid_input）。</div>'));
  body.appendChild(el("div",{class:"field"},'<label>Module name<span style="color:var(--err);margin-left:3px">*</span></label><input id="vfModule" class="input" autocomplete="off" spellcheck="false" placeholder="语音在场模块名" value="'+esc(state.form.moduleName)+'" /><div class="help">由部署的语音 provider 决定（默认 <span class="mono">'+esc(DEFAULT_MODULE)+'</span>）；未配置 provider 时返回 503 voice_not_configured，未注册模块返回 404 unknown_module。</div>'));

  const actions=el("div",{class:"ph-actions",style:"margin-top:4px"});
  const submit = el("button", { class:"btn btn-primary", id:"vEnableBtn", type:"button" });
  submit.innerHTML = state.submitting ? '<span class="spin" aria-hidden="true"></span><span>启用中…</span>' : ICON.check+'<span>启用</span>';
  submit.disabled = state.submitting;
  submit.addEventListener("click", submitEnable);
  actions.appendChild(submit);
  actions.appendChild(btn("取消","ghost",null,()=>{ state.enableOpen=false; state.result=null; render(); }));
  body.appendChild(actions);

  ["vfScope","vfActor","vfKind","vfModule"].forEach((id,i)=>{
    const key=["scopeId","actorId","agentKind","moduleName"][i];
    const inp=body.querySelector("#"+id);
    if(inp) inp.addEventListener("input", e=>{ state.form[key]=e.target.value; });
  });
  const res=el("div",{id:"vResult"});
  if(state.result) res.appendChild(renderResult(state.result));
  body.appendChild(res);

  return panel(ICON.agent,"在指定 Actor 上启用语音（进阶）","非零配置默认流程；很少需要",badge("ok","实时可用"),body);
}
function renderResult(r){
  const box = el("div", { class:"result "+(r.ok?"ok":"err"), role:"status", "aria-live":"polite" });
  let head = '<div class="result-head">'+(r.ok?ICON.okc:ICON.alert)+'<div><div class="result-l">'+esc(r.label)+'</div>'+(r.message?'<div class="result-m">'+esc(r.message)+'</div>':'')+'</div></div>';
  let grid = "";
  if(r.detail && typeof r.detail === "object"){
    const rows = Object.keys(r.detail).filter(k=>r.detail[k]!=null && r.detail[k]!=="");
    if(rows.length){ grid = '<div class="result-grid">'+rows.map(k => '<div class="kv"><span class="k">'+esc(k)+'</span><span class="v">'+esc(r.detail[k])+'</span></div>').join("")+'</div>'; }
  }
  box.innerHTML = head + grid;
  return box;
}
async function submitEnable(){
  if(state.submitting) return;
  const f = state.form;
  const scopeId=(f.scopeId||"").trim(), actorId=(f.actorId||"").trim(), agentKind=(f.agentKind||"").trim(), moduleName=(f.moduleName||"").trim();
  const missing=[];
  if(!scopeId) missing.push("Scope ID");
  if(!actorId) missing.push("Actor ID");
  if(!agentKind) missing.push("Agent kind");
  if(!moduleName) missing.push("Module name");
  if(missing.length){ state.result={ ok:false, label:"缺少必填项", message:"请填写：" + missing.join("、") }; render(); return; }
  const token = getToken(); if(!token){ signOutSilent(); return; }

  state.submitting = true; state.result = null; render();
  const url = "/api/scopes/" + encodeURIComponent(scopeId) + "/gagent-actors/" + encodeURIComponent(actorId) + "/voice-presence/enable?agentKind=" + encodeURIComponent(agentKind);
  try {
    const res = await fetch(url, { method:"POST", headers:{ Authorization:"Bearer " + token.access_token, "Content-Type":"application/json" }, body: JSON.stringify({ moduleName: moduleName }) });
    if(res.status === 401){ signOutSilent(); return; }
    let body = null;
    try { body = await res.json(); } catch(e){ /* tolerate empty/non-json */ }
    if(res.status === 202 && body){
      state.result = { ok:true, label:"已受理启用命令", message: body.note || "命令已受理待分发；read model 观测到提交后可重新连接。",
        detail: { module_name: body.module_name, stage: body.stage, agent_kind: body.agent_kind, actor_id: body.actor_id, command_id: body.command_id, correlation_id: body.correlation_id } };
    } else { state.result = mapEnableError(res.status, body); }
  } catch(e){ state.result = { ok:false, label:"请求失败", message: String(e && e.message || e) }; }
  finally { state.submitting = false; render(); }
}
function mapEnableError(status, body){
  const code = body && body.error ? body.error : ("http_" + status);
  const detail = body && body.detail ? body.detail : "";
  if(code === "voice_not_configured") return { ok:false, label:"未配置语音 provider", message:"当前部署尚未注册任何语音在场模块（503 voice_not_configured）。需先在 host 配置语音 provider 后才能启用。", detail:{ error:code, detail } };
  if(code === "unknown_module") return { ok:false, label:"模块未注册", message:(detail || "指定的语音模块未注册。") + "（404 unknown_module）", detail:{ error:code } };
  if(code === "admission_denied") return { ok:false, label:"scope 准入被拒", message:(detail || "scope 准入拒绝了启用访问。") + "（403）", detail:{ error:code } };
  if(code === "actor_not_found") return { ok:false, label:"Actor 不存在", message:(detail || "该 actor 未在请求的 scope 中注册。") + "（404）", detail:{ error:code } };
  if(code === "admission_unavailable" || code === "command_dispatch_failed") return { ok:false, label:"暂不可用", message:(detail || "服务暂时不可用，请稍后重试。") + "（503 " + code + "）", detail:{ error:code } };
  return { ok:false, label:"启用失败（" + status + "）", message: detail || "后端返回错误。", detail:{ error:code } };
}

/* ===========================================================================
   View: Tools & Connectors — read-only default allowlist (no write / no GET)
   =========================================================================== */
function renderTools(){
  const v=el("div",{class:"view"});
  v.appendChild(pageHead("Tools & Connectors",
    '默认语音 agent 的能力来自 <b>NyxID 下游连接器/服务</b>（与「接入通道 Channels」是两回事）。'+
    '下面是当前部署的<b>默认会话工具 allowlist</b>（来自 <code class="inline">VOICE_TOOL_ALLOWLIST</code>，否则取内置默认）。'));

  v.appendChild(banner("info",ICON.info,"工具来源 = NyxID 下游服务",
    'aevatar 暂不向本页暴露「连接器列举 / allowlist 写入」端点：allowlist 由 host 环境变量 '+
    '<code class="inline">VOICE_TOOL_ALLOWLIST</code> 决定，因此这里<b>只读</b>展示真实默认，不提供添加/勾选操作，也不编造连接器清单。'));

  const grp=el("div",{class:"tool-grp"});
  const h=el("div",{class:"tool-grp-h"});
  h.innerHTML='<span class="gi">'+ICON.link+'</span><div style="flex:1"><div style="font-weight:640;color:var(--fg-strong);font-size:13.5px">默认会话工具 allowlist</div><div style="font-size:11.5px;color:var(--muted-2);font-family:var(--mono)">VOICE_TOOL_ALLOWLIST · '+DEFAULT_TOOL_ALLOWLIST.length+' 工具</div></div>'+badge("active","默认启用");
  grp.appendChild(h);
  const descs={ nyxid_proxy:"经 NyxID 代理调用已连接服务", nyxid_status:"查询 NyxID 连接状态", nyxid_services:"列举已连接的 NyxID 服务", nyxid_catalog:"浏览可连接的 NyxID 服务目录", ornn_search_skills:"检索已发布的 Ornn 技能", use_skill:"加载并运行某个技能" };
  DEFAULT_TOOL_ALLOWLIST.forEach(name=>{
    const line=el("div",{class:"tool-line"});
    line.innerHTML='<span class="chk" role="checkbox" aria-checked="true" aria-disabled="true" aria-label="'+esc(name)+'">'+ICON.check+'</span>'+
      '<div style="min-width:0"><div class="tn">'+esc(name)+'</div><div class="td">'+esc(descs[name]||"NyxID 下游工具")+'</div></div>'+
      '<div class="tr"><span class="owner ACTOR" title="执行方">ACTOR</span></div>';
    grp.appendChild(line);
  });
  const wrap=el("div",{style:"margin-top:18px"}); wrap.appendChild(grp); v.appendChild(wrap);
  v.appendChild(el("div",{style:"margin-top:10px;font-size:12.5px;color:var(--muted)"},'当前默认 allowlist：<span class="mono" style="color:var(--accent)">'+DEFAULT_TOOL_ALLOWLIST.map(esc).join(", ")+'</span>'));
  return v;
}

/* ===========================================================================
   View: Channels / Transports — WS real (described); WHIP = sub-project ②
   =========================================================================== */
function renderChannels(){
  const v=el("div",{class:"view"});
  v.appendChild(pageHead("Channels / Transports",
    '终端如何接入默认语音 agent。WebSocket（<code class="inline">/ws/voice</code>）始终可用；'+
    'WHIP/WebRTC 浏览器直推属于语音服务分解的子项目 ②，后端就绪后接入。'));

  /* WS — real endpoint + contract; no live-client list GET, so we describe, not fabricate. */
  const wsBody=el("div",{});
  wsBody.appendChild(copyRow("WS 接入地址", location.origin.replace(/^http/,"ws") + "/ws/voice?codec=pcm16&mode=half_duplex"));
  wsBody.appendChild(copyRow("鉴权", "Authorization: Bearer <NyxID token>（ChatRoutePolicy 解析路由）"));
  wsBody.appendChild(copyRow("可选 · 指定模块", "?voice_module_name=" + DEFAULT_MODULE));
  const contract=el("div",{class:"contract",style:"margin-top:14px"});
  contract.innerHTML=
    '<div class="row"><span class="lbl">下行 (server→client)</span><span class="val">VoiceControlFrame：sessionAccepted / realtimeFrame（PCM16）</span></div>'+
    '<div class="row"><span class="lbl">上行 (client→server)</span><span class="val">VoiceControlFrame：drainAcknowledged / inputImage</span></div>'+
    '<div class="row"><span class="lbl">音频编码</span><span class="val">PCM16</span></div>'+
    '<div class="row"><span class="lbl">会话自动开通</span><span class="val">无需 actor id · 自动开通默认 agent（'+esc(DEFAULT_MODULE)+'）</span></div>';
  wsBody.appendChild(contract);
  const wsWarn=banner("warn",ICON.alert,"未配置 provider 时 fail-closed",
    '未配置语音 provider 时，<code class="inline">/ws/voice</code> 以 <code class="inline">503 voice_not_configured</code> 拒绝连接。');
  wsWarn.style.marginTop="16px"; wsWarn.style.marginBottom="0";
  wsBody.appendChild(wsWarn);
  v.appendChild(panel(ICON.channels,"WebSocket","双向消息 + 音频帧",badge("active","可用"),wsBody));

  /* WHIP — honest disabled stub (sub-project ②) */
  const whipBody=el("div",{});
  whipBody.appendChild(emptyState(ICON.cast,"WHIP / WebRTC 入站 — 待后端 ②",
    "浏览器直推音视频（WHIP/WebRTC 入站）属于「通用语音服务」分解中的子项目 ②。后端就绪并暴露入站端点后本卡片接入；当前不提供可点操作，也不展示虚构地址。"));
  v.appendChild(panel(ICON.channels,"WHIP / WebRTC","低延迟实时音频",badge("warn","待后端 ②"),whipBody,{stub:true,muted:true}));
  return v;
}

/* ===========================================================================
   View: Display Outputs — vp-control side-channel = sub-project ③ (stub)
   =========================================================================== */
function renderOutputs(){
  const v=el("div",{class:"view"});
  v.appendChild(pageHead("Display Outputs / Sinks",
    'agent 通过显示侧信道（<code class="inline">vp-control</code>）把 <span class="mono">show_text</span> / '+
    '<span class="mono">show_image</span> 等视觉内容推到 sink。该能力属于语音服务分解的子项目 ③。'));
  const body=el("div",{});
  body.appendChild(emptyState(ICON.cast,"显示侧信道 (vp-control) — 待后端 ③",
    "显示侧信道与 sink 路由（show_text / show_image → 浏览器 / 设备 / 连接器通知）属于子项目 ③。后端暴露读取/控制端点后本视图接入；当前不展示任何 sink 或路由规则的虚构数据。"));
  v.appendChild(panel(ICON.display,"Sinks & 路由","显示输出目标与 show_* 去向",badge("warn","待后端 ③"),body,{stub:true,muted:true}));
  return v;
}

/* ===========================================================================
   View: Live Sessions — no list-sessions read surface → honest empty state
   =========================================================================== */
function renderSessions(){
  const v=el("div",{class:"view"});
  v.appendChild(pageHead("Live Sessions",
    '实时观测每个接入会话：双轨转写、说话/打断/思考状态、工具调用、延迟与租约。'));
  v.appendChild(emptyState(ICON.sessions,"暂无会话观测端点",
    "语音运行态属于会话本身，aevatar 当前没有「列举/读取实时会话」的端点，因此这里不展示会话列表（不编造数据）。"+
    "该读取面在语音服务分解的后端就绪后接入；要发起一次真实会话，可经 /ws/voice 接入终端。"));
  return v;
}

/* ===========================================================================
   View: Test — keep the connect affordance pointing at the real /ws/voice,
   but no fabricated live observation panel (no session read surface).
   =========================================================================== */
function renderTest(){
  const v=el("div",{class:"view"});
  v.appendChild(pageHead("内置测试台 Test",
    '本服务无服务端会话读取面，因此页面内不内嵌「实时观测」面板（不编造）。'+
    '要联调默认语音 agent，用 WHIP/WS 测试客户端连到 <code class="inline">/ws/voice</code>。'));
  const stage=el("section",{class:"panel"});
  const body=el("div",{class:"panel-body"});
  body.appendChild(banner("info",ICON.info,"如何联调",
    '默认 agent 零配置可用：浏览器/设备携带 NyxID bearer 打开 '+
    '<code class="inline">/ws/voice?codec=pcm16&amp;mode=half_duplex</code> 即开始一次实时语音会话。'+
    '（参考 voice-presence 的 <span class="mono">/test.html</span> / <span class="mono">/unified-test.html</span> WHIP 测试页。）'));
  const tstage=el("div",{class:"test-stage"});
  const mb=el("div",{class:"mic-btn","aria-hidden":"true"},ICON.mic.replace('width="16" height="16"','width="34" height="34"'));
  tstage.appendChild(mb);
  tstage.appendChild(el("div",{style:"margin-top:16px;color:var(--muted);font-size:13px;text-align:center;max-width:420px"},
    "页面内连麦测试需要会话观测端点（暂无）。当前请用外部 WHIP/WS 测试客户端联调；连上后会话即由默认 agent 处理。"));
  body.appendChild(tstage);
  body.appendChild(el("div",{style:"text-align:center;margin-top:6px"},btn("查看 WS 接入地址","ghost",ICON.link,()=>{ state.view="channels"; render(); }).outerHTML));
  stage.appendChild(body);
  v.appendChild(stage);
  return v;
}

/* ===========================================================================
   View: Settings — provider credentials info + host-gated publish (honest)
   =========================================================================== */
function renderSettings(){
  const v=el("div",{class:"view"});
  v.appendChild(pageHead("Settings",
    'provider 凭据来源、以及把本语音服务发布给他人调用（受平台门控）。'));

  /* credentials — describe the real per-session ephemeral chain; no secret UI. */
  const cred=el("div",{});
  cred.innerHTML=
    '<div class="contract">'+
    '<div class="row"><span class="lbl">默认 provider</span><span class="val">'+esc(DEFAULT_MODULE)+'（OpenAI Realtime）</span></div>'+
    '<div class="row"><span class="lbl">凭据模型</span><span class="val">每会话 NyxID ephemeral（slug <span class="mono">openai-realtime</span>）</span></div>'+
    '<div class="row"><span class="lbl">长期密钥</span><span class="val">仅存于 NyxID；aevatar 不持有</span></div>'+
    '</div>'+
    '<div class="banner info" style="margin-top:14px;margin-bottom:0">'+ICON.info+'<div><div class="bt">凭据在 NyxID 侧管理</div>'+
      '<div class="bb">语音会话的 provider 凭据由调用者的 NyxID 身份所拥有的服务（slug <span class="mono">openai-realtime</span>，端点为 OpenAI 裸主机）提供。'+
      'aevatar 不存储 provider 密钥，故此处无凭据输入项。</div></div></div>';
  v.appendChild(panel(ICON.settings,"Provider 凭据","每会话 NyxID ephemeral 据此 mint",badge("ok","NyxID 托管"),cred));

  /* host-gated publish (honest disabled) */
  const pub=el("div",{});
  pub.appendChild(banner("warn",ICON.lock,"发布 / 暴露受平台门控（host-gated）",
    '把本语音服务注册为 NyxID 连接器供他人调用，需要平台开启权限 —— operator 不能自助开启。'));
  const card=el("div",{style:"opacity:.7;border:1px dashed var(--border);border-radius:var(--r);padding:16px"});
  card.innerHTML='<div class="togline"><div><div class="tl-t">注册为 NyxID 连接器（对外可调用）</div><div class="tl-s">暴露后其他 scope 可经 NyxID 调用本语音服务</div></div><div class="spacer"></div><span class="gated"><span class="lock">'+ICON.lock+' host-gated</span></span></div>';
  pub.appendChild(card);
  v.appendChild(panel(ICON.lock,"发布 / 暴露","让本服务可被他人调用",badge("muted","受限"),pub,{stub:true,muted:true}));
  return v;
}

/* ===========================================================================
   Render
   =========================================================================== */
function render(){
  const app=$("#app"); app.innerHTML="";
  app.appendChild(renderTopbar());
  if(!state.signedIn){ app.appendChild(renderLogin()); return; }
  const shell=el("div",{class:"shell"});
  shell.appendChild(renderSideNav());
  const main=el("div",{class:"main scroll"});
  const r={ agents:renderAgents, tools:renderTools, channels:renderChannels, outputs:renderOutputs, sessions:renderSessions, test:renderTest, settings:renderSettings }[state.view] || renderAgents;
  main.appendChild(r());
  shell.appendChild(main);
  app.appendChild(shell);
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
