namespace Aevatar.Mainnet.Host.Api.Cqrs;

// CQRS / Projection Observatory: one inline self-contained HTML page (no wwwroot, no build step), mirroring
// the /voice + /workflow/observatory + /workflow/studio precedent. Browser OIDC Authorization Code + PKCE
// against nyxid (reuse of the console-web client; same authority/clientId/scope + shared /auto/callback +
// shared storageKey as the rest of the suite). The page is the anonymous static shell; the app is gated
// behind login exactly like its siblings, and carries the shared suite navigation so the six console pages
// move as one.
//
// Honest wiring (DATA HONESTY — never show mock/fixture data as LIVE):
//   - Panel A (pipeline canvas): STATIC. The pipeline structure is an always-true architectural diagram; it
//     fetches nothing. Animated tokens + reduced-motion handling are preserved.
//   - Panel B (scope & lag rail + headline metrics): LIVE via GET /api/cqrs/scopes (platform-admin gated).
//     Scope ids embed the owning nyxid account id (hyphenated UUID); we resolve it to a display_name via the
//     nyxid admin single-user endpoint (GET {authority}/api/v1/admin/users/<id>, admin-only, CORS-open) and
//     surface family + owner facets so 200 scopes across 24 families read as people, not opaque ids. A miss
//     leaves the raw id — never a fabricated name. 401 -> re-login; 403 -> honest "needs platform admin".
//   - Panel C (read-model inventory): LIVE via GET /api/cqrs/readmodels (platform-admin gated). The endpoint
//     exposes the registry (read-model name + owning actor); version/updated are not surfaced by it, so those
//     empty columns are NOT rendered (no "v—"/"更新 —" noise) — count shows only where the backend has one.
//     401 -> re-login; 403 -> honest "needs platform admin" state; nullable fields are omitted, never faked.
//   - Panel D (envelope inspector): the recent-envelope LIST is unbacked (event-stream browse is a gap) ->
//     pending state. The state_root snapshot SHAPE block (field names + types) is structural and kept, as
//     are the two existing pending-backend stubs.
internal static class CqrsObservatoryPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
<title>CQRS / Projection Observatory · aevatar</title>
<style>
  /* =========================================================================
     Aevatar CQRS / Projection Observatory
     让不可见的后端数据流变得可读：命令 → 提交事件(真相源) → 投影管道 → 读模型副本，
     以及哪里健康 / 滞后。属于 Aevatar Backend Console 套件（与 studio/observatory/voice 同源）。
     framework-agnostic：语义化 HTML + CSS 变量 + 纯 vanilla JS。
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
    --topbar-h:56px; color-scheme:dark;
  }
  @media (prefers-color-scheme: light) {
    html:not(.theme-dark) {
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
  html.theme-light {
    --bg:#f5f7fa; --bg-grad:radial-gradient(1100px 560px at 80% -12%,#eef2f8 0%,#f5f7fa 58%);
    --panel:#ffffff; --panel-2:#f3f5f9; --panel-3:#eaeef4; --border:#dde2ea; --border-soft:#e7ebf1;
    --fg:#1c2128; --fg-strong:#0b0f14; --muted:#57606b; --muted-2:#828b97;
    --accent:#2f63e0; --accent-ink:#ffffff; --accent-soft:rgba(47,99,224,.10); --accent-line:rgba(47,99,224,.34);
    --ok:#1a7f37; --ok-soft:rgba(26,127,55,.12); --warn:#9a6700; --warn-soft:rgba(154,103,0,.12);
    --err:#cf222e; --err-soft:rgba(207,34,46,.10); --run:#2f6fed; --run-soft:rgba(47,111,237,.12);
    --neutral:#57606b; --neutral-soft:rgba(87,96,107,.10);
    --shadow:0 10px 30px rgba(16,24,40,.10); --shadow-sm:0 1px 2px rgba(16,24,40,.06); color-scheme:light;
  }
  html.theme-dark { color-scheme:dark; }

  * { box-sizing:border-box; }
  html,body { height:100%; }
  body { margin:0; background:var(--bg); background-image:var(--bg-grad); background-attachment:fixed; color:var(--fg);
    font:14px/1.55 var(--sans); -webkit-font-smoothing:antialiased; }
  ::selection { background:var(--accent-soft); }
  button { font:inherit; color:inherit; cursor:pointer; }
  a { color:var(--accent); text-decoration:none; } a:hover { text-decoration:underline; }
  :focus-visible { outline:none; box-shadow:var(--ring); border-radius:var(--r-sm); }
  .mono { font-family:var(--mono); font-variant-numeric:tabular-nums; }
  .scroll::-webkit-scrollbar { width:10px; height:10px; }
  .scroll::-webkit-scrollbar-thumb { background:var(--panel-3); border-radius:999px; border:2px solid transparent; background-clip:content-box; }

  .dot { width:9px; height:9px; border-radius:50%; flex:0 0 auto; }
  .dot.s-active,.dot.s-ok { background:var(--ok); }
  .dot.s-lag { background:var(--warn); }
  .dot.s-bad { background:var(--err); }
  .dot.s-idle { background:var(--neutral); }
  .dot.s-run { background:var(--run); animation:pulse 2.4s ease-out infinite; }
  @keyframes pulse { 0%{box-shadow:0 0 0 0 var(--run-soft);} 70%{box-shadow:0 0 0 7px transparent;} 100%{box-shadow:0 0 0 0 transparent;} }

  .badge { display:inline-flex; align-items:center; gap:6px; padding:3px 9px; border-radius:var(--r-pill); font-size:12px; font-weight:600; border:1px solid transparent; white-space:nowrap; }
  .badge .dot { width:7px; height:7px; }
  .b-ok,.b-active { color:var(--ok); background:var(--ok-soft); border-color:color-mix(in oklab,var(--ok) 30%,transparent); }
  .b-lag,.b-warn { color:var(--warn); background:var(--warn-soft); border-color:color-mix(in oklab,var(--warn) 30%,transparent); }
  .b-bad,.b-err { color:var(--err); background:var(--err-soft); border-color:color-mix(in oklab,var(--err) 30%,transparent); }
  .b-idle,.b-muted { color:var(--muted); background:var(--neutral-soft); border-color:var(--border); }
  .b-accent { color:var(--accent); background:var(--accent-soft); border-color:var(--accent-line); }

  .pending { display:inline-flex; align-items:center; gap:5px; font-size:10.5px; font-weight:700; letter-spacing:.02em; padding:2px 8px; border-radius:var(--r-pill);
    color:var(--warn); background:var(--warn-soft); border:1px dashed color-mix(in oklab,var(--warn) 45%,transparent); }
  .srctag { display:inline-flex; align-items:center; gap:5px; font-size:10.5px; font-weight:600; padding:2px 8px; border-radius:var(--r-pill); }
  .srctag.live { color:var(--ok); background:var(--ok-soft); border:1px solid color-mix(in oklab,var(--ok) 28%,transparent); }
  .srctag.live .dot { width:6px; height:6px; background:var(--ok); }

  .copybtn { display:inline-flex; align-items:center; gap:5px; font-size:11px; color:var(--muted); background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r-sm); padding:3px 8px; }
  .copybtn:hover { color:var(--fg); border-color:var(--muted-2); } .copybtn.done { color:var(--ok); }

  /* ---- top bar ---- */
  .topbar { position:sticky; top:0; z-index:40; height:var(--topbar-h); display:flex; align-items:center; gap:12px; padding:0 14px;
    background:color-mix(in oklab,var(--panel) 88%,transparent); backdrop-filter:saturate(140%) blur(12px); -webkit-backdrop-filter:saturate(140%) blur(12px); border-bottom:1px solid var(--border); }
  .brand { display:flex; align-items:center; gap:10px; min-width:0; }
  .brand-mark { width:26px; height:26px; border-radius:7px; flex:0 0 auto; display:grid; place-items:center; color:#fff; box-shadow:var(--shadow-sm);
    background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); }
  .brand-name { font-weight:650; letter-spacing:-.01em; color:var(--fg-strong); white-space:nowrap; }
  .brand-sub { color:var(--muted-2); font-size:12px; white-space:nowrap; }

  /* shared cross-page nav — identical markup/behaviour on all six pages */
  .suite-nav { display:flex; align-items:center; gap:2px; overflow-x:auto; scrollbar-width:none; }
  .suite-nav::-webkit-scrollbar { display:none; }
  .suite-nav a { display:inline-flex; align-items:center; gap:6px; padding:6px 11px; border-radius:var(--r-pill);
    font-size:12.5px; font-weight:600; color:var(--muted); text-decoration:none; border:1px solid transparent; white-space:nowrap;
    transition:color .15s,background .15s,border-color .15s; }
  .suite-nav a:hover { color:var(--fg); background:var(--panel-2); }
  .suite-nav a[aria-current="page"] { color:var(--accent); background:var(--accent-soft); border-color:var(--accent-line); }
  .suite-nav svg { width:14px; height:14px; flex:0 0 auto; }

  .spacer { flex:1 1 auto; }
  .scope-chip { display:inline-flex; align-items:center; gap:6px; padding:4px 9px; border-radius:var(--r-pill); background:var(--panel-2); border:1px solid var(--border); font-size:11.5px; color:var(--muted); }
  .scope-chip .sid { font-family:var(--mono); font-size:11px; color:var(--fg); }
  .scope-chip svg { color:var(--ok); }
  .iconbtn { width:32px; height:32px; display:grid; place-items:center; background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r); color:var(--muted); }
  .iconbtn:hover { color:var(--fg); border-color:var(--muted-2); }
  .account { display:inline-flex; align-items:center; gap:8px; padding:4px 6px 4px 4px; border-radius:var(--r-pill); background:var(--panel-2); border:1px solid var(--border); }
  .avatar { width:22px; height:22px; border-radius:50%; flex:0 0 auto; display:grid; place-items:center; font-size:11px; font-weight:700; color:#fff; background:linear-gradient(140deg,#6f9bff,#9b6bff); }
  .account .who { font-size:12px; color:var(--fg); max-width:130px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .linkbtn { background:transparent; border:0; color:var(--accent); font-size:12.5px; font-weight:600; padding:4px 6px; border-radius:var(--r-sm); }
  .linkbtn:hover { text-decoration:underline; }

  /* ---- body ---- */
  .body { max-width:1320px; margin:0 auto; padding:20px 24px 70px; }
  .page-h { font-size:21px; font-weight:740; letter-spacing:-.02em; color:var(--fg-strong); margin:0; }
  .page-sub { color:var(--muted); font-size:13.5px; margin:8px 0 0; line-height:1.6; max-width:84ch; }
  .page-sub b { color:var(--fg); font-weight:600; }

  .panel { border:1px solid var(--border); border-radius:var(--r-lg); background:var(--panel); overflow:hidden; }
  .panel-head { display:flex; align-items:center; gap:10px; padding:12px 15px; border-bottom:1px solid var(--border-soft); flex-wrap:wrap; }
  .panel-head h2 { font-size:14px; font-weight:660; color:var(--fg-strong); margin:0; }
  .panel-head .ph-r { margin-left:auto; display:flex; align-items:center; gap:8px; flex-wrap:wrap; }
  .panel-body { padding:14px 15px; }

  /* ===================== A) Pipeline canvas ===================== */
  .canvas-card { margin-top:18px; }
  .legend { display:flex; gap:16px; flex-wrap:wrap; align-items:center; font-size:11.5px; color:var(--muted); }
  .legend .lg { display:inline-flex; align-items:center; gap:6px; }
  .legend .rail-s { width:16px; height:0; border-top:2px solid var(--border); }
  .legend .tok-s { width:9px; height:9px; border-radius:50%; background:var(--accent); box-shadow:0 0 5px var(--accent); }
  .legend .live-s { width:9px; height:9px; border-radius:50%; background:var(--ok); }

  .canvas-wrap { overflow-x:auto; padding:6px 6px 2px; }
  .canvas { position:relative; width:1240px; height:360px; margin:0 auto; }
  .canvas svg { position:absolute; inset:0; width:1240px; height:360px; overflow:visible; }
  .rail { fill:none; stroke:var(--border); stroke-width:1.6; }
  .rail-glow { fill:none; stroke:var(--accent); stroke-width:1.6; opacity:.18; }
  .tok { fill:var(--accent); filter:drop-shadow(0 0 4px var(--accent)); }

  .cap { position:absolute; font-size:10.5px; font-weight:700; letter-spacing:.04em; text-transform:uppercase; }
  .cap.left { left:8px; top:8px; color:var(--ok); }
  .cap.right { right:8px; top:8px; color:var(--accent); }
  .cap.mid { left:50%; transform:translateX(-50%); top:8px; color:var(--muted-2); font-weight:600; text-transform:none; letter-spacing:0; }

  .snode { position:absolute; border:1px solid var(--border); border-radius:var(--r); background:var(--panel); padding:8px 10px; box-shadow:var(--shadow-sm);
    cursor:pointer; transition:border-color .15s,box-shadow .15s,background .15s; z-index:2; }
  .snode:hover,.snode:focus-visible { border-color:var(--accent-line); box-shadow:0 0 0 3px var(--accent-soft); outline:none; }
  .snode.sel { border-color:var(--accent); box-shadow:0 0 0 3px var(--accent-soft); }
  .snode .sn-num { position:absolute; top:-9px; left:-9px; width:20px; height:20px; border-radius:50%; background:var(--accent); color:var(--accent-ink); font-size:11px; font-weight:700; display:grid; place-items:center; box-shadow:var(--shadow-sm); }
  .snode .sn-title { font-size:12px; font-weight:650; color:var(--fg-strong); letter-spacing:-.005em; }
  .snode .sn-types { font-family:var(--mono); font-size:10px; color:var(--muted-2); margin-top:3px; line-height:1.35; }
  .snode .lbadge { display:inline-flex; align-items:center; gap:4px; font-family:var(--mono); font-size:9.5px; font-weight:700; padding:1px 6px; border-radius:var(--r-pill); margin-top:5px; }
  .snode .lbadge.acc { color:var(--accent); background:var(--accent-soft); border:1px solid var(--accent-line); }
  .snode .lbadge.ok { color:var(--ok); background:var(--ok-soft); border:1px solid color-mix(in oklab,var(--ok) 30%,transparent); }
  .snode .lbadge.lag { color:var(--warn); background:var(--warn-soft); border:1px solid color-mix(in oklab,var(--warn) 30%,transparent); }
  .snode.truth { border-color:color-mix(in oklab,var(--ok) 45%,var(--border)); }
  .snode.hub::after { content:"1 → N"; position:absolute; right:-8px; top:50%; transform:translateY(-50%); font-family:var(--mono); font-size:9px; font-weight:700; color:var(--accent); background:var(--panel); border:1px solid var(--accent-line); border-radius:var(--r-pill); padding:1px 5px; }

  .stage-detail { display:flex; gap:12px; align-items:flex-start; margin:10px 6px 0; padding:12px 14px; border:1px solid var(--border); border-radius:var(--r); background:var(--panel-2); min-height:64px; }
  .stage-detail .sd-ic { width:26px; height:26px; border-radius:7px; display:grid; place-items:center; color:var(--accent); background:var(--accent-soft); flex:0 0 auto; }
  .stage-detail .sd-t { font-size:13px; font-weight:660; color:var(--fg-strong); }
  .stage-detail .sd-types { font-family:var(--mono); font-size:11px; color:var(--accent); margin:2px 0 5px; }
  .stage-detail .sd-r { font-size:12.5px; color:var(--muted); line-height:1.55; }

  /* ===================== grid layout B/C/D ===================== */
  .grid { display:grid; grid-template-columns:minmax(0,1fr) 380px; gap:18px; margin-top:18px; align-items:start; }
  .col-main { display:flex; flex-direction:column; gap:18px; min-width:0; }
  .col-side { position:sticky; top:calc(var(--topbar-h) + 12px); }

  /* headline metrics */
  .headline { display:grid; grid-template-columns:repeat(3,1fr); gap:0; border:1px solid var(--border); border-radius:var(--r-lg); overflow:hidden; background:var(--panel); }
  .hl { padding:13px 16px; border-right:1px solid var(--border-soft); }
  .hl:last-child { border-right:0; }
  .hl .hl-l { font-size:11px; color:var(--muted-2); letter-spacing:.03em; text-transform:uppercase; }
  .hl .hl-v { font-family:var(--mono); font-size:22px; font-weight:600; color:var(--fg-strong); margin-top:3px; }
  .hl.warn .hl-v { color:var(--warn); } .hl.bad .hl-v { color:var(--err); }

  /* B scope rail */
  .sort-h { display:grid; grid-template-columns:1fr 150px 70px 64px; gap:10px; padding:8px 14px; font-size:10.5px; letter-spacing:.04em; text-transform:uppercase; color:var(--muted-2); border-bottom:1px solid var(--border-soft); }
  .sort-h button { background:transparent; border:0; color:inherit; font:inherit; text-transform:inherit; letter-spacing:inherit; display:inline-flex; align-items:center; gap:4px; padding:0; }
  .sort-h button[aria-sort] { color:var(--accent); }
  .scope-row { display:grid; grid-template-columns:1fr 150px 70px 64px; gap:10px; align-items:center; padding:11px 14px; border-bottom:1px solid var(--border-soft); width:100%; text-align:left; background:transparent; border-left:0;border-right:0;border-top:0; }
  .scope-row:last-child { border-bottom:0; }
  .scope-row:hover { background:var(--panel-2); }
  .scope-row[aria-current="true"] { background:var(--accent-soft); box-shadow:inset 3px 0 0 var(--accent); }
  .scope-row .sc-id { font-family:var(--mono); font-size:12px; color:var(--fg-strong); display:flex; align-items:center; gap:8px; min-width:0; }
  .scope-row .sc-id .txt { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  /* scope id, broken into family + owner + tail so the rail reads as people, not opaque ids */
  .sc-main { min-width:0; display:flex; flex-direction:column; gap:3px; }
  .sc-top { display:flex; align-items:center; gap:7px; min-width:0; }
  .sc-fam { font-family:var(--sans); font-size:12px; font-weight:600; color:var(--fg-strong); white-space:nowrap; }
  .sc-owner { display:inline-flex; align-items:center; gap:5px; font-family:var(--sans); font-size:11px; font-weight:600; padding:1px 8px 1px 4px; border-radius:var(--r-pill); color:var(--accent); background:var(--accent-soft); border:1px solid var(--accent-line); white-space:nowrap; }
  .sc-owner .av { width:15px; height:15px; border-radius:50%; flex:0 0 auto; display:grid; place-items:center; font-size:9px; font-weight:700; color:#fff; background:linear-gradient(140deg,#6f9bff,#9b6bff); }
  .sc-owner.unresolved { color:var(--muted-2); background:var(--neutral-soft); border-color:var(--border); }
  .sc-owner.unresolved .av { background:var(--panel-3); color:var(--muted); }
  .sc-tail { font-family:var(--mono); font-size:10.5px; color:var(--muted-2); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  /* B filter bar — family + owner facets so 200 rows across 24 families are navigable */
  .filterbar { display:flex; align-items:center; gap:7px; flex-wrap:wrap; padding:10px 14px; border-bottom:1px solid var(--border-soft); }
  .filterbar .fb-l { font-size:10.5px; letter-spacing:.04em; text-transform:uppercase; color:var(--muted-2); margin-right:1px; }
  .facet { display:inline-flex; align-items:center; gap:5px; font-size:11.5px; font-weight:600; color:var(--muted); background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r-pill); padding:3px 10px; white-space:nowrap; transition:color .15s,border-color .15s,background .15s; }
  .facet:hover { color:var(--fg); border-color:var(--muted-2); }
  .facet[aria-pressed="true"] { color:var(--accent); background:var(--accent-soft); border-color:var(--accent-line); }
  .facet .ct { font-family:var(--mono); font-size:10px; color:var(--muted-2); }
  .facet[aria-pressed="true"] .ct { color:var(--accent); }
  .facet .av { width:14px; height:14px; border-radius:50%; flex:0 0 auto; display:grid; place-items:center; font-size:8.5px; font-weight:700; color:#fff; background:linear-gradient(140deg,#6f9bff,#9b6bff); }
  .gauge { height:7px; border-radius:999px; background:var(--panel-3); overflow:hidden; position:relative; }
  .gauge > i { display:block; height:100%; background:var(--ok); border-radius:999px; }
  .gauge.lag > i { background:var(--warn); } .gauge.bad > i { background:var(--err); }
  .gauge-cap { font-family:var(--mono); font-size:10px; color:var(--muted-2); margin-top:3px; }
  .lagcell { font-family:var(--mono); font-size:13px; font-weight:600; text-align:right; color:var(--muted); }
  .lagcell.pos { color:var(--warn); } .lagcell.bad { color:var(--err); }
  .failcell { font-family:var(--mono); font-size:13px; text-align:right; color:var(--muted-2); }
  .failcell.pos { color:var(--err); }

  /* B states: loading / empty / error / forbidden */
  .pstate { padding:26px 16px; text-align:center; color:var(--muted); font-size:13px; line-height:1.6; }
  .pstate .ps-ic { width:34px; height:34px; border-radius:9px; display:grid; place-items:center; margin:0 auto 11px; }
  .pstate .ps-ic.warn { color:var(--warn); background:var(--warn-soft); border:1px solid color-mix(in oklab,var(--warn) 32%,transparent); }
  .pstate .ps-ic.err { color:var(--err); background:var(--err-soft); border:1px solid color-mix(in oklab,var(--err) 34%,transparent); }
  .pstate .ps-ic.lock { color:var(--muted); background:var(--neutral-soft); border:1px solid var(--border); }
  .pstate .ps-t { font-weight:650; color:var(--fg-strong); font-size:13.5px; }
  .pstate .ps-b { margin-top:5px; max-width:46ch; margin-left:auto; margin-right:auto; }
  .pstate .ps-detail { margin-top:9px; font-family:var(--mono); font-size:11px; color:var(--muted-2); word-break:break-all; }
  .skel { padding:11px 14px; border-bottom:1px solid var(--border-soft); display:flex; align-items:center; gap:10px; }
  .skel:last-child { border-bottom:0; }
  .skel-bar { height:10px; border-radius:999px; background:linear-gradient(90deg,var(--panel-2),var(--panel-3),var(--panel-2)); background-size:200% 100%; animation:shimmer 1.3s linear infinite; }
  @keyframes shimmer { 0%{background-position:200% 0;} 100%{background-position:-200% 0;} }

  /* shared pager — client-side display paging (scope rail list + inventory groups) */
  .pager { display:flex; align-items:center; justify-content:space-between; gap:10px; padding:10px 14px; border-top:1px solid var(--border-soft); }
  .pager.pg-inline { border-top:0; padding:8px 2px 1px; justify-content:flex-end; gap:12px; }
  .pager .pg-info { font-family:var(--mono); font-size:11px; color:var(--muted-2); font-variant-numeric:tabular-nums; }
  .pager .pg-btns { display:inline-flex; gap:6px; }
  .pager button { display:inline-flex; align-items:center; gap:5px; font-size:12px; font-weight:600; color:var(--muted); background:var(--panel-2); border:1px solid var(--border); border-radius:var(--r-sm); padding:5px 10px; transition:color .15s,border-color .15s; }
  .pager button:hover:not(:disabled) { color:var(--fg); border-color:var(--muted-2); }
  .pager button:disabled { opacity:.42; cursor:not-allowed; }

  /* C inventory */
  .inv-group { margin-bottom:14px; } .inv-group:last-child { margin-bottom:0; }
  .inv-group-h { display:flex; align-items:center; gap:9px; margin-bottom:8px; }
  .shape-badge { display:inline-flex; align-items:center; gap:6px; font-size:11.5px; font-weight:650; padding:3px 10px; border-radius:var(--r-pill); border:1px solid transparent; }
  .shape-badge.doc { color:var(--accent); background:var(--accent-soft); border-color:var(--accent-line); }
  .shape-badge.graph { color:#9b7bff; background:color-mix(in oklab,#9b7bff 14%,transparent); border-color:color-mix(in oklab,#9b7bff 35%,transparent); }
  .shape-badge.mem { color:var(--muted); background:var(--neutral-soft); border-color:var(--border); }
  .inv-group-h .gh-eng { font-family:var(--mono); font-size:11px; color:var(--muted-2); }
  .inv-empty { font-size:11.5px; color:var(--muted-2); padding:9px 12px; border:1px dashed var(--border); border-radius:var(--r); background:color-mix(in oklab,var(--panel-2) 60%,transparent); }
  .rm-row { display:grid; grid-template-columns:1fr auto; gap:10px; align-items:center; padding:10px 12px; border:1px solid var(--border); border-radius:var(--r); background:var(--panel); margin-bottom:7px; }
  .rm-row .rm-n { font-family:var(--mono); font-size:12.5px; font-weight:600; color:var(--fg-strong); }
  .rm-row .rm-m { font-size:11.5px; color:var(--muted); margin-top:3px; display:flex; gap:8px; flex-wrap:wrap; }
  .rm-row .rm-m .mono { font-family:var(--mono); color:var(--muted-2); }
  .rm-row .rm-r { text-align:right; }
  .rm-row .rm-v { font-family:var(--mono); font-size:12.5px; color:var(--accent); }
  .rm-row .rm-c { font-family:var(--mono); font-size:11px; color:var(--muted-2); margin-top:2px; }

  /* D inspector */
  .insp-events { display:flex; flex-direction:column; gap:5px; margin-bottom:14px; }
  .insp-ev { display:flex; align-items:center; gap:9px; padding:8px 11px; border:1px solid var(--border); border-radius:var(--r); background:var(--panel-2); width:100%; text-align:left; }
  .insp-ev:hover { border-color:var(--muted-2); }
  .insp-ev[aria-current="true"] { border-color:var(--accent-line); background:var(--accent-soft); }
  .insp-ev .ie-t { font-family:var(--mono); font-size:12px; color:var(--fg-strong); }
  .insp-ev .ie-v { margin-left:auto; font-family:var(--mono); font-size:11.5px; color:var(--accent); }
  .insp-kv { display:flex; justify-content:space-between; gap:12px; padding:8px 0; font-size:12px; border-bottom:1px solid var(--border-soft); }
  .insp-kv:last-of-type { border-bottom:0; }
  .insp-kv .k { color:var(--muted); } .insp-kv .v { font-family:var(--mono); font-size:11.5px; color:var(--fg); text-align:right; word-break:break-all; }
  .shape-tree { font-family:var(--mono); font-size:11.5px; line-height:1.7; color:var(--muted); background:var(--bg); border:1px solid var(--border-soft); border-radius:var(--r-sm); padding:10px 12px; margin-top:6px; white-space:pre; overflow-x:auto; }
  .shape-tree .key { color:var(--accent); } .shape-tree .typ { color:var(--muted-2); }
  .stub { border:1px dashed color-mix(in oklab,var(--warn) 40%,transparent); border-radius:var(--r); background:var(--warn-soft); padding:12px 14px; margin-top:12px; }
  .stub .st-h { display:flex; align-items:center; gap:8px; font-size:12.5px; font-weight:650; color:var(--fg-strong); margin-bottom:5px; }
  .stub .st-b { font-size:12px; color:var(--muted); line-height:1.55; }
  .stub-btn { margin-top:9px; display:inline-flex; align-items:center; gap:7px; font-size:12px; font-weight:600; padding:6px 12px; border-radius:var(--r); border:1px dashed color-mix(in oklab,var(--warn) 45%,transparent); background:transparent; color:var(--muted); cursor:not-allowed; }

  .banner { display:flex; gap:11px; padding:12px 14px; border-radius:var(--r); font-size:13px; line-height:1.6; }
  .banner svg { flex:0 0 auto; margin-top:2px; color:var(--accent); }
  .banner.info { background:var(--accent-soft); border:1px solid var(--accent-line); }
  .banner .bt { font-weight:650; color:var(--fg-strong); } .banner .bb { color:var(--muted); }

  /* login */
  .login { min-height:calc(100dvh - var(--topbar-h)); display:grid; place-items:center; padding:24px; }
  .login-card { width:min(420px,100%); background:var(--panel); border:1px solid var(--border); border-radius:var(--r-lg); padding:32px 30px; box-shadow:var(--shadow); text-align:center; }
  .login-mark { width:52px; height:52px; border-radius:14px; margin:0 auto 18px; background:linear-gradient(150deg,var(--accent),color-mix(in oklab,var(--accent) 55%,#7c4dff)); display:grid; place-items:center; color:#fff; box-shadow:var(--shadow); }
  .login h1 { font-size:19px; font-weight:700; letter-spacing:-.01em; color:var(--fg-strong); margin:0 0 8px; }
  .login p { color:var(--muted); font-size:13.5px; margin:0 auto 22px; max-width:32ch; line-height:1.6; }
  .login .btn-primary { width:100%; padding:11px 16px; font-size:14px; }
  .login-foot { margin-top:16px; font-size:11.5px; color:var(--muted-2); display:inline-flex; align-items:center; gap:6px; }
  .btn-primary { padding:10px 18px; border-radius:var(--r); border:1px solid transparent; background:var(--accent); color:var(--accent-ink);
    font-weight:650; font-size:13.5px; display:inline-flex; align-items:center; justify-content:center; gap:8px; transition:filter .15s,opacity .15s; }
  .btn-primary:hover { filter:brightness(1.08); }

  /* ---- responsive ---- */
  @media (max-width:1000px) {
    .grid { grid-template-columns:1fr; }
    .col-side { position:static; }
  }
  @media (max-width:760px) {
    .brand-sub, .scope-chip { display:none; }
    .suite-nav .nav-label { display:none; }
    .suite-nav a { padding:6px 8px; }
    .account .who { display:none; }
  }
  @media (max-width:620px) {
    .body { padding:16px 14px 56px; }
    .headline { grid-template-columns:1fr; } .hl { border-right:0; border-bottom:1px solid var(--border-soft); }
    .sort-h,.scope-row { grid-template-columns:1fr 64px 56px; } .sort-h .col-gauge,.scope-row .cell-gauge { display:none; }
  }

  @media (prefers-reduced-motion: reduce) {
    *,*::before,*::after { animation-duration:.001ms !important; animation-iteration-count:1 !important; transition-duration:.001ms !important; }
    .dot.s-run { animation:none; box-shadow:0 0 0 3px var(--run-soft); }
  }
</style>
</head>
<body>
  <div id="app"></div>

<script>
/* ===========================================================================
   数据来源标注（DATA HONESTY）：
     LIVE（今天就有）：projection-scope 状态（active / observed vs successful version / failures）
                       经 GET /api/cqrs/scopes（平台管理员可见的平台级健康视图）；scope id 内嵌的
                       nyxid 归属账户 id 经 nyxid admin 单用户端点解析为 display_name（解析失败保留原 id）。
     静态（结构永真，无 fetch）：Panel A 管道画布——一张始终成立的架构图。
     待后端（pending backend，仅做带标签的占位，绝不编造数据）：
                       读模型清单（Panel C）、原始事件流 browse/replay 与最近信封列表（Panel D）、
                       projection-session 自省、每索引 ES/Neo4j 健康。
   =========================================================================== */

/* ===========================================================================
   Auth layer — mirrors the observatory/studio/voice OIDC PKCE gate verbatim.
   Browser OIDC Authorization Code + PKCE against nyxid (same console-web client;
   authority/clientId/scope identical), shared /auto/callback + shared storageKey
   so a single login covers the whole console suite.
   =========================================================================== */
const CFG = {
  authority: "https://nyx.chrono-ai.fun",
  clientId: "37a93189-2734-406e-bca1-7dbdf25c5a53",
  scope: "openid profile email proxy",
  redirectUri: location.origin + "/auto/callback",
  storageKey: "aevatar-console:nyxid:pkce"
};
const TOKEN_KEY = CFG.storageKey + ":token";
const PKCE_KEY  = CFG.storageKey + ":pkce";

function b64url(buf){ return btoa(String.fromCharCode.apply(null, new Uint8Array(buf))).replace(/\+/g,"-").replace(/\//g,"_").replace(/=+$/,""); }
async function sha256(text){ return b64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text))); }
function randomString(len){ const a=new Uint8Array(len); crypto.getRandomValues(a); return b64url(a.buffer).slice(0, len); }

function getToken(){ const raw=localStorage.getItem(TOKEN_KEY); if(!raw) return null; try { return JSON.parse(raw); } catch(e){ console.warn("cqrs: token parse failed", e); } return null; }
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
  history.replaceState({}, "", (pending && pending.returnTo) || "/cqrs");
  if(!pending || pending.state !== returnedState){ console.warn("cqrs: login state mismatch"); return false; }
  const form = new URLSearchParams();
  form.set("grant_type", "authorization_code");
  form.set("code", code);
  form.set("redirect_uri", CFG.redirectUri);
  form.set("client_id", CFG.clientId);
  form.set("code_verifier", pending.verifier);
  const res = await fetch(CFG.authority + "/oauth/token", { method:"POST", headers:{ "Content-Type":"application/x-www-form-urlencoded" }, body: form.toString() });
  sessionStorage.removeItem(PKCE_KEY);
  if(!res.ok){ console.warn("cqrs: token exchange failed", res.status); return false; }
  const token = await res.json();
  token.obtained_at = Date.now();
  setToken(token);
  return true;
}

async function fetchUserInfo(){
  const token = getToken(); if(!token) return null;
  try { const res = await fetch(CFG.authority + "/oauth/userinfo", { headers:{ Authorization:"Bearer " + token.access_token } }); return res.ok ? await res.json() : null; }
  catch(e){ console.warn("cqrs: userinfo fetch failed", e); }
  return null;
}
function toAccount(info){ return info ? { label: info.email || info.preferred_username || info.sub || "已登录" } : null; }

function signOut(){ clearToken(); beginLogin(); }
function signOutSilent(){ clearToken(); state.signedIn = false; render(); }

/* scope-gated bearer fetch helper. Attaches Bearer; 401 -> re-login. Callers inspect res.status
   themselves for non-401 handling (403 forbidden, error detail). */
async function authFetch(path){
  const token = getToken(); if(!token) throw new Error("not-authenticated");
  const res = await fetch(path, { headers:{ Authorization:"Bearer " + token.access_token } });
  if(res.status === 401){ signOutSilent(); throw new Error("unauthorized"); }
  return res;
}

/* ===========================================================================
   nyxid owner resolution — projection scope ids embed the owning nyxid account
   id (the hyphenated 8-4-4-4-12 UUID, e.g. service-runs:…:1514a708-0b2d-…).
   We resolve it to a human display_name so the scope rail reads as PEOPLE, not
   opaque ids. Direct against the nyxid admin single-user endpoint
   (GET {nyx-api}/api/v1/admin/users/<id>) — admin-only, browser-CORS-allowed
   (access-control-allow-origin: *), reusing the same nyxid token the page holds.
   Same nyxid API base + resolution purpose as the rest of the console suite
   (cf. BackendConsole/admin.html NYX_API). Best-effort + cached: a miss leaves
   the row showing its raw id, never a fabricated name. Only the FIRST hyphenated
   uuid in a scope id is the owner (later 32-char hex ids are run/definition/chat
   ids, not accounts). */
const NYXID_API = "https://nyx-api.chrono-ai.fun";
const NYXID_USER_API = NYXID_API + "/api/v1/admin/users/";
const UUID_RE = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
const ownerCache = {};                 // uuid -> display_name | null (null = resolved-but-unknown)
const ownerInFlight = {};              // uuid -> Promise (dedupe concurrent lookups)
function scopeOwnerId(scopeId){ const m = UUID_RE.exec(String(scopeId||"")); return m ? m[0].toLowerCase() : null; }
async function resolveOwner(uuid){
  if(uuid in ownerCache) return ownerCache[uuid];
  if(ownerInFlight[uuid]) return ownerInFlight[uuid];
  const token = getToken(); if(!token){ return null; }
  const p = (async function(){
    try {
      const res = await fetch(NYXID_USER_API + encodeURIComponent(uuid), { headers:{ Authorization:"Bearer " + token.access_token } });
      if(!res.ok){ ownerCache[uuid] = null; return null; }
      const u = await res.json();
      const name = (u && (u.display_name || u.name || u.username || u.email)) || null;
      ownerCache[uuid] = name;
      return name;
    } catch(e){ console.warn("cqrs: owner resolve failed", uuid, e); ownerCache[uuid] = null; return null; }
    finally { delete ownerInFlight[uuid]; }
  })();
  ownerInFlight[uuid] = p;
  return p;
}
/* resolve every distinct owner across the loaded scopes, then re-render once. */
async function resolveAllOwners(rows, rerender){
  const ids = {};
  rows.forEach(r=>{ const id=scopeOwnerId(r.id); if(id) ids[id]=1; });
  const todo = Object.keys(ids).filter(id=>!(id in ownerCache));
  if(!todo.length) return;
  await Promise.all(todo.map(resolveOwner));
  if(rerender) rerender();
}
function ownerName(scopeId){ const id=scopeOwnerId(scopeId); return id ? (ownerCache[id]||null) : null; }

/* ===========================================================================
   App state + DOM helpers
   =========================================================================== */
const $=(s,r=document)=>r.querySelector(s);
const el=(t,a={},h)=>{const n=document.createElement(t);for(const k in a){if(k==="class")n.className=a[k];else if(k.startsWith("on")&&typeof a[k]==="function")n.addEventListener(k.slice(2),a[k]);else if(a[k]!=null)n.setAttribute(k,a[k]);}if(h!=null)n.innerHTML=h;return n;};
const esc=(s)=>String(s==null?"":s).replace(/[&<>"]/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
const initials=(s)=>(s||"?").trim().charAt(0).toUpperCase();
const fmt=(n)=>Number(n||0).toLocaleString("en-US");
function reduced(){ return window.matchMedia&&window.matchMedia("(prefers-reduced-motion: reduce)").matches; }

const ICON={
  sun:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="12" cy="12" r="4.2"/><path d="M12 2v2.4M12 19.6V22M2 12h2.4M19.6 12H22M4.6 4.6l1.7 1.7M17.7 17.7l1.7 1.7M19.4 4.6l-1.7 1.7M6.3 17.7l-1.7 1.7"/></svg>',
  moon:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A8.5 8.5 0 1 1 11.2 3a6.6 6.6 0 0 0 9.8 9.8Z"/></svg>',
  check:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12.5 4.2 4.2L19 7"/></svg>',
  spark:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5 18 18M18 6l-2.5 2.5M8.5 15.5 6 18"/></svg>',
  flow:'<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="5" cy="12" r="2.2"/><circle cx="19" cy="5" r="2.2"/><circle cx="19" cy="19" r="2.2"/><path d="M7.2 11 17 5.8M7.2 13 17 18.2"/></svg>',
  info:'<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 7.6v.2"/></svg>',
  warn:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M12 8.5v4.5M12 16.2v.2"/><path d="M10.3 3.9 2.5 17.5A2 2 0 0 0 4.2 20.5h15.6a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg>',
  lock:'<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
  loginlock:'<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
  sort:'<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m8 9 4-4 4 4M8 15l4 4 4-4"/></svg>',
  cube:'<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"><path d="M12 3 20 7.5v9L12 21 4 16.5v-9L12 3ZM4 7.5 12 12l8-4.5M12 12v9"/></svg>'
};

/* ===========================================================================
   Shared suite navigation — the six console pages move as one.
   Identical markup/labels/order across observatory/studio/schedules/channels/voice/cqrs;
   the host page passes its own active key.
   =========================================================================== */
const SUITE_NAV=[
  {k:"observatory",href:"/workflow/observatory",label:"观测台",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/></svg>'},
  {k:"studio",href:"/workflow/studio",label:"Studio",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="2.4"/><circle cx="6" cy="18" r="2.4"/><circle cx="18" cy="12" r="2.4"/><path d="M8.2 7.2 15.8 11M8.2 16.8 15.8 13"/></svg>'},
  {k:"schedules",href:"/schedules",label:"定时任务",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/></svg>'},
  {k:"channels",href:"/channels",label:"渠道",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="2.6"/><path d="M12 4.5v4.9M12 14.6v4.9M4.5 12h4.9M14.6 12h4.9"/></svg>'},
  {k:"voice",href:"/voice",label:"语音",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M8.5 21h7"/></svg>'},
  {k:"cqrs",href:"/cqrs",label:"投影",svg:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5.5" rx="7.5" ry="3"/><path d="M4.5 5.5v6c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3v-6M4.5 11.5v6c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3v-6"/></svg>'}
];
function suiteNavHtml(active){return '<nav class="suite-nav" aria-label="控制台导航">'+SUITE_NAV.map(function(n){return '<a href="'+n.href+'"'+(n.k===active?' aria-current="page"':'')+' title="'+n.label+'">'+n.svg+'<span class="nav-label">'+n.label+'</span></a>';}).join('')+'</nav>';}

/* ===========================================================================
   state
   =========================================================================== */
const state = {
  signedIn:false,
  theme: localStorage.getItem("cqrs-theme") || null,
  scopeId:null, scopeResolved:false, scopeSource:null,
  sort:"lag", selectedScope:null, selectedEvent:0,
  famFilter:null, ownerFilter:null,   // Panel B facet filters (scope family / nyxid owner id)
  scopePage:1, invPages:{},      // client-side display paging (Panel B list / Panel C per-shape group)
  // scopes panel B: status one of loading | ok | empty | forbidden | error
  scopes:{ status:"loading", rows:[], detail:null },
  // read-model inventory panel C: status one of loading | ok | empty | forbidden | error
  readmodels:{ status:"loading", groups:[], detail:null }
};
let accountLabel = "已登录";

function curTheme(){ const h=document.documentElement; return h.classList.contains("theme-light")?"light":h.classList.contains("theme-dark")?"dark":null; }
function effDark(){ const t=curTheme(); if(t)return t==="dark"; return !window.matchMedia||!window.matchMedia("(prefers-color-scheme: light)").matches; }
function applyTheme(){ const t=state.theme; document.documentElement.classList.remove("theme-light","theme-dark"); if(t)document.documentElement.classList.add("theme-"+t); }
function lagOf(s){ return (s.observed||0) - (s.successful||0); }

/* scope id structure: "projection.durable.scope:<family>:<rest…>". Surface the family
   (the human bit) and a compact tail so each row reads as <family> · <owner> · <tail>. */
const SCOPE_PREFIX = "projection.durable.scope:";
function scopeFamily(id){ const s=String(id||""); const b=s.startsWith(SCOPE_PREFIX)?s.slice(SCOPE_PREFIX.length):s; const i=b.indexOf(":"); return i<0?b:b.slice(0,i); }
function scopeTail(id){
  const s=String(id||""); const b=s.startsWith(SCOPE_PREFIX)?s.slice(SCOPE_PREFIX.length):s;
  const i=b.indexOf(":"); let rest=i<0?"":b.slice(i+1);
  const owner=scopeOwnerId(id);   // drop the resolved owner uuid from the tail (shown as a chip)
  if(owner) rest=rest.replace(new RegExp(owner,"i"),"").replace(/::+/g,":").replace(/^:|:$/g,"");
  return rest;
}

/* D) state_root snapshot shape — structural (field names + types), not data */
const STATE_ROOT_SHAPE = [
  ["Id","Guid"],["ActorId","string"],["StateVersion","long"],["LastEventId","string"],["UpdatedAt","DateTime"],
  ["status","enum"],["steps","StepState[]"],["usage","UsageTotals"]
];

/* A) pipeline stages（结构永真；live 徽章在此为静态架构标注） */
const AUTH_VERSION_NOTE = "单调 VERSION";
const STAGES = [
  { id:"actor", n:1, x:20,  y:142, w:154, h:78, title:"Actor / Command", types:"GAgent · EventEnvelope",
    role:"业务 actor（GAgent）拥有一个实体——状态与行为合一。命令以 EventEnvelope 抵达：通用信封 { id, timestamp, payload, correlationId, causationId, sourceActorId }。" },
  { id:"commit", n:2, x:194, y:142, w:154, h:78, title:"Commit · EventStore", types:"StateEvent · VERSION", cls:"truth", live:["ok","真相源"],
    role:"处理器产生领域事件，以乐观并发追加到事件存储 → 提交后的 StateEvent，一个携单调 VERSION 的不可变事实。这就是唯一真相源（single source of truth）。" },
  { id:"envelope", n:3, x:368, y:142, w:154, h:78, title:"EventEnvelope", types:"CommittedStateEventPublished", live:["acc","发布一次"],
    role:"每个已提交事件包成 CommittedStateEventPublished { state_event, state_root（事件后完整快照）}，装入 EventEnvelope，只发布一次。关键原则：一个入口、一对多扇出；读模型更新与实时 UI 流（AGUI）走同一条管道——绝非两套并行系统。" },
  { id:"activate", n:4, x:542, y:150, w:132, h:62, title:"Activation 扇出", types:"projection scope", cls:"hub", live:["acc","1 → N"],
    role:"一个钩子询问哪些投影 scope 关心此事件（如 workflow:execution:org-123）；dispatcher 唤醒这些 scope actor；每个再扇出到 N 个 projector。兄弟 projector 失败不阻塞其他。" },
  { id:"proj-a", n:5, x:706, y:50,  w:152, h:60, title:"Projector", types:"match WorkflowStepFinished",
    role:"每个 projector 精确匹配一种事件类型，并把它 reduce 进读模型。这一个把 workflow 执行事件归约成 workflow-run 读模型。" },
  { id:"proj-b", n:5, x:706, y:152, w:152, h:60, title:"Projector", types:"match WorkflowEdgeAdded",
    role:"匹配拓扑相关事件，reduce 进 workflow-topology 图读模型。" },
  { id:"proj-c", n:5, x:706, y:254, w:152, h:60, title:"Projector", types:"match ScheduleFired",
    role:"匹配调度事件，reduce 进 schedule 读模型（开发态用 Memory sink）。" },
  { id:"sink-a", n:6, x:890, y:50,  w:152, h:60, title:"Sink · Document", types:"Elasticsearch",
    role:"store dispatcher 写入唯一启用的 sink。Document 形状由 Elasticsearch 承载，适合可查询的当前态文档。" },
  { id:"sink-b", n:6, x:890, y:152, w:152, h:60, title:"Sink · Graph", types:"Neo4j",
    role:"Graph 形状由 Neo4j 承载，适合关系/拓扑读模型。" },
  { id:"sink-c", n:6, x:890, y:254, w:152, h:60, title:"Sink · Memory", types:"dev only",
    role:"Memory 形状仅用于开发态，不持久。三种 sink 形状，但每个读模型只落到一个启用的 sink。" },
  { id:"query", n:7, x:1074, y:142, w:150, h:78, title:"Read-models / Query", types:"{ StateVersion … }", live:["lag","最终一致"],
    role:"读模型是 actor 作用域的当前态副本 { Id, ActorId, StateVersion, LastEventId, UpdatedAt }，StateVersion 把它系回权威 actor 版本。查询只触读模型，绝不碰 actor 内部；最终一致是诚实的——副本可能落后于 actor。" }
];
const RAILS = [
  { id:"p0", d:"M174,181 L542,181" },
  { id:"p1", d:"M674,181 C700,181 700,80 706,80 L1040,80 C1064,80 1064,181 1074,181" },
  { id:"p2", d:"M674,181 L1074,181" },
  { id:"p3", d:"M674,181 C700,181 700,284 706,284 L1040,284 C1064,284 1064,181 1074,181" }
];
const RAIL_MID = { p0:[358,181], p1:[873,80], p2:[874,181], p3:[873,284] };

/* C) read-model sink-shape metadata — static structural labels (display name + count unit)
   keyed by the backend's shape code. NOT data: the shape→engine/name/unit mapping is always-true. */
const SHAPE_META = {
  doc:   { name:"Document", unit:"docs" },
  graph: { name:"Graph",    unit:"nodes" },
  mem:   { name:"Memory",   unit:"entries" }
};
function shapeMeta(shape){ return SHAPE_META[shape] || { name:String(shape||"?"), unit:"" }; }

/* ===========================================================================
   shared client-side pager — display paging for long lists (scope & lag rail rows +
   read-model inventory groups). Pure view state; returns null when the list fits on a
   single page, so short lists render exactly as before.
   =========================================================================== */
const SCOPE_PAGE_SIZE = 10;   // Panel B: scope & lag-rail rows per page
const RM_PAGE_SIZE = 6;       // Panel C: read-model items per sink-shape group per page
function pageCount(total, size){ return Math.max(1, Math.ceil((total||0)/size)); }
function pageClamp(page, total, size){ return Math.min(Math.max(1, (page|0)||1), pageCount(total, size)); }
function renderPager(page, total, size, onGo, inline){
  const pages=pageCount(total, size);
  if(pages<=1) return null;
  const cur=pageClamp(page, total, size);
  const start=(cur-1)*size+1, end=Math.min(cur*size, total);
  const bar=el("div",{class:"pager"+(inline?" pg-inline":""),role:"navigation","aria-label":"分页"});
  bar.appendChild(el("div",{class:"pg-info"}, start+"–"+end+" / "+total));
  const btns=el("div",{class:"pg-btns"});
  const prev=el("button",{type:"button","aria-label":"上一页"},"‹ 上一页");
  if(cur<=1) prev.setAttribute("disabled","true"); else prev.addEventListener("click",function(){ onGo(cur-1); });
  const next=el("button",{type:"button","aria-label":"下一页"},"下一页 ›");
  if(cur>=pages) next.setAttribute("disabled","true"); else next.addEventListener("click",function(){ onGo(cur+1); });
  btns.appendChild(prev); btns.appendChild(next);
  bar.appendChild(btns);
  return bar;
}

/* ===========================================================================
   topbar
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
      '<div class="brand-mark" aria-hidden="true">'+ICON.flow+'</div>'+
      '<div><div class="brand-name">Aevatar Backend Console</div></div>'+
    '</div>'+
    suiteNavHtml("cqrs")+
    (state.signedIn?scopeChipHtml():"")+
    '<div class="spacer"></div>';
  const tb=el("button",{class:"iconbtn",id:"themeBtn","aria-label":"切换主题",title:"切换主题"}); tb.innerHTML=effDark()?ICON.sun:ICON.moon;
  tb.addEventListener("click",()=>{ state.theme=effDark()?"light":"dark"; localStorage.setItem("cqrs-theme",state.theme); applyTheme(); render(); });
  bar.appendChild(tb);
  if(state.signedIn){
    const ac=el("div",{class:"account"});
    ac.innerHTML='<span class="avatar" aria-hidden="true">'+esc(initials(accountLabel))+'</span><span class="who" title="'+esc(accountLabel)+'">'+esc(accountLabel)+'</span>';
    const sw=el("button",{class:"linkbtn","aria-label":"切换账户"},"切换账户");
    sw.addEventListener("click",()=>{ signOut(); });
    ac.appendChild(sw);
    bar.appendChild(ac);
  }
  return bar;
}

function renderLogin(){
  const wrap = el("main", { class:"login" });
  const card = el("section", { class:"login-card", role:"region", "aria-label":"登录" });
  card.innerHTML =
    '<div class="login-mark" aria-hidden="true">'+ICON.flow+'</div>'+
    '<h1>登录以进入投影观测台</h1>'+
    '<p>把命令 → 提交事件 → 投影管道 → 读模型副本的数据流可视化 —— 登录后即可查看。</p>';
  const btn = el("button", { class:"btn-primary", id:"signinBtn" }, "使用 nyxid 登录");
  btn.addEventListener("click", () => { beginLogin(); });
  card.appendChild(btn);
  card.appendChild(el("div", { class:"login-foot" }, ICON.loginlock+'<span>采用 OIDC bearer-token 鉴权 · 通过 nyxid 账户登录</span>'));
  wrap.appendChild(card);
  return wrap;
}

/* ===========================================================================
   A) pipeline canvas — STATIC structure (no fetch); the diagram is always-true
   =========================================================================== */
function srcTag(kind){ return kind==="static" ? '<span class="srctag live" style="color:var(--accent);background:var(--accent-soft);border-color:var(--accent-line)"><span class="dot" style="background:var(--accent)"></span>结构 · 永真</span>' : '<span class="pending">'+ICON.warn+' 待后端</span>'; }

function renderCanvas(){
  const card=el("section",{class:"panel canvas-card"});
  const head=el("div",{class:"panel-head"});
  head.innerHTML='<h2>管道画布 · Pipeline</h2><div class="ph-r">'+
    '<span class="legend"><span class="lg"><span class="rail-s"></span>结构（静态轨道）</span><span class="lg"><span class="tok-s"></span>数据（实时事件 token）</span><span class="lg"><span class="live-s"></span>实时徽章</span></span>'+
    srcTag("static")+'</div>';
  card.appendChild(head);

  const wrap=el("div",{class:"canvas-wrap scroll"});
  const canvas=el("div",{class:"canvas",role:"group","aria-label":"CQRS 投影管道：命令 → 提交事件 → 信封 → 扇出 → projector → sink → 读模型/查询"});

  /* svg rails + tokens */
  const svg=document.createElementNS("http://www.w3.org/2000/svg","svg");
  svg.setAttribute("viewBox","0 0 1240 360"); svg.setAttribute("aria-hidden","true");
  let inner="";
  RAILS.forEach(r=>{ inner+='<path id="'+r.id+'" class="rail" d="'+r.d+'"/>'; });
  if(reduced()){
    RAILS.forEach(r=>{ const m=RAIL_MID[r.id]; inner+='<circle class="tok" cx="'+m[0]+'" cy="'+m[1]+'" r="4"/>'; });
  } else {
    inner+='<circle class="tok" r="4.2"><animateMotion dur="2.0s" repeatCount="indefinite" keyPoints="0;1" keyTimes="0;1" calcMode="linear"><mpath href="#p0"/></animateMotion></circle>';
    [["p1","0.15s"],["p2","0.3s"],["p3","0.45s"]].forEach(([id,beg])=>{
      inner+='<circle class="tok" r="4.2"><animateMotion dur="2.4s" begin="'+beg+'" repeatCount="indefinite"><mpath href="#'+id+'"/></animateMotion></circle>';
    });
    // 让扇出有“分裂”节奏：再补一组错峰 token
    [["p1","1.3s"],["p2","1.45s"],["p3","1.6s"]].forEach(([id,beg])=>{
      inner+='<circle class="tok" r="3.4"><animateMotion dur="2.4s" begin="'+beg+'" repeatCount="indefinite"><mpath href="#'+id+'"/></animateMotion></circle>';
    });
  }
  svg.innerHTML=inner;
  canvas.appendChild(svg);

  /* captions */
  canvas.insertAdjacentHTML("beforeend",
    '<div class="cap left">单一真相源</div><div class="cap mid">一条管道：读模型更新 + 实时 UI（AGUI）流</div><div class="cap right">多副本 · 查询</div>');

  /* nodes */
  STAGES.forEach(s=>{
    const node=el("div",{class:"snode "+(s.cls||""), style:"left:"+s.x+"px;top:"+s.y+"px;width:"+s.w+"px;height:"+s.h+"px",
      tabindex:"0", role:"button", "aria-label":"阶段 "+s.n+"："+s.title });
    const live = s.live ? '<span class="lbadge '+s.live[0]+'">'+esc(s.live[1])+'</span>' : "";
    node.innerHTML='<span class="sn-num">'+s.n+'</span><div class="sn-title">'+esc(s.title)+'</div><div class="sn-types">'+esc(s.types)+'</div>'+live;
    const show=()=>showStage(s); const hide=()=>showStage(null);
    node.addEventListener("mouseenter",show); node.addEventListener("focus",show);
    node.addEventListener("mouseleave",hide); node.addEventListener("blur",hide);
    if(s.id==="envelope"||s.id==="commit") node.addEventListener("click",()=>{ document.getElementById("inspector")?.scrollIntoView({block:"nearest"}); });
    canvas.appendChild(node);
  });
  wrap.appendChild(canvas);
  card.appendChild(wrap);

  /* stage detail strip (aria-live; hover/focus 更新) */
  const strip=el("div",{class:"stage-detail",id:"stageDetail","aria-live":"polite"});
  strip.innerHTML=defaultStrip();
  card.appendChild(strip);
  return card;
}
function defaultStrip(){
  return '<div class="sd-ic">'+ICON.info+'</div><div><div class="sd-t">悬停或聚焦任一阶段，查看其职责与真实类型名</div>'+
    '<div class="sd-r">严格左→右：一个权威真相源向多个读副本扇出。<b style="color:var(--fg)">结构</b>（静态轨道）永真；<b style="color:var(--fg)">数据</b>（移动 token、版本/滞后徽章）才是实时的。</div></div>';
}
function showStage(s){
  const strip=document.getElementById("stageDetail"); if(!strip)return;
  document.querySelectorAll(".snode").forEach(n=>n.classList.remove("sel"));
  if(!s){ strip.innerHTML=defaultStrip(); return; }
  strip.innerHTML='<div class="sd-ic">'+ICON.cube+'</div><div><div class="sd-t">'+s.n+'. '+esc(s.title)+'</div><div class="sd-types">'+esc(s.types)+'</div><div class="sd-r">'+esc(s.role)+'</div></div>';
}

/* ===========================================================================
   B) scope & lag rail — LIVE via GET /api/cqrs/scopes (platform-admin gated)
   =========================================================================== */
function filteredScopes(){
  let arr=state.scopes.rows;
  if(state.famFilter) arr=arr.filter(s=>scopeFamily(s.id)===state.famFilter);
  if(state.ownerFilter) arr=arr.filter(s=>scopeOwnerId(s.id)===state.ownerFilter);
  return arr;
}
function sortedScopes(){
  const arr=filteredScopes().slice();
  if(state.sort==="lag") arr.sort((a,b)=>lagOf(b)-lagOf(a) || b.failures-a.failures);
  else if(state.sort==="fail") arr.sort((a,b)=>b.failures-a.failures || lagOf(b)-lagOf(a));
  else arr.sort((a,b)=>String(a.id).localeCompare(String(b.id)));
  return arr;
}
/* facet tallies over the FULL (unfiltered) row set, so chips always show real totals. */
function scopeFacets(){
  const fam={}, own={};
  state.scopes.rows.forEach(s=>{
    const f=scopeFamily(s.id); fam[f]=(fam[f]||0)+1;
    const o=scopeOwnerId(s.id); if(o) own[o]=(own[o]||0)+1;
  });
  return { fam, own };
}
function renderFilterBar(){
  const f=scopeFacets();
  const bar=el("div",{class:"filterbar",role:"group","aria-label":"按 family / 归属人筛选 scope"});
  const fams=Object.keys(f.fam).sort((a,b)=>f.fam[b]-f.fam[a]);
  const owners=Object.keys(f.own).sort((a,b)=>f.own[b]-f.own[a]);
  // "全部" reset
  const all=el("button",{class:"facet",type:"button","aria-pressed":String(!state.famFilter&&!state.ownerFilter)},"全部 <span class=\"ct\">"+state.scopes.rows.length+"</span>");
  all.addEventListener("click",()=>{ state.famFilter=null; state.ownerFilter=null; state.scopePage=1; render(); });
  bar.appendChild(all);
  if(fams.length){
    bar.appendChild(el("span",{class:"fb-l"},"FAMILY"));
    fams.forEach(fam=>{
      const b=el("button",{class:"facet",type:"button","aria-pressed":String(state.famFilter===fam),title:fam},esc(fam)+' <span class="ct">'+f.fam[fam]+'</span>');
      b.addEventListener("click",()=>{ state.famFilter=(state.famFilter===fam?null:fam); state.scopePage=1; render(); });
      bar.appendChild(b);
    });
  }
  if(owners.length){
    bar.appendChild(el("span",{class:"fb-l"},"归属人"));
    owners.forEach(o=>{
      const nm=ownerCache[o]; const label=nm||o.slice(0,8);
      const b=el("button",{class:"facet",type:"button","aria-pressed":String(state.ownerFilter===o),title:nm?(nm+" · "+o):o},
        '<span class="av">'+esc(initials(nm||"?"))+'</span>'+esc(label)+' <span class="ct">'+f.own[o]+'</span>');
      b.addEventListener("click",()=>{ state.ownerFilter=(state.ownerFilter===o?null:o); state.scopePage=1; render(); });
      bar.appendChild(b);
    });
  }
  return bar;
}
function renderScopeRail(){
  const card=el("section",{class:"panel"});
  const head=el("div",{class:"panel-head"});
  const tag = state.scopes.status==="ok"
    ? '<span class="srctag live"><span class="dot"></span>数据来源 · LIVE</span>'
    : (state.scopes.status==="loading" ? '<span class="srctag live" style="color:var(--muted);background:var(--neutral-soft);border-color:var(--border)"><span class="dot" style="background:var(--muted)"></span>加载中…</span>' : '<span class="pending">'+ICON.warn+' 平台级</span>');
  head.innerHTML='<h2>Scope &amp; Lag Rail</h2><div class="ph-r">'+tag+'</div>';
  card.appendChild(head);

  const rows = state.scopes.rows;
  const st = state.scopes.status;

  /* headline metrics — only meaningful when we actually have rows */
  if(st==="ok"){
    const totalLag=rows.reduce((s,x)=>s+lagOf(x),0);
    const lagging=rows.filter(x=>lagOf(x)>0).length;
    const totalFail=rows.reduce((s,x)=>s+(x.failures||0),0);
    const hl=el("div",{class:"panel-body",style:"padding-bottom:0"});
    const hb=el("div",{class:"headline"});
    hb.innerHTML=
      '<div class="hl '+(totalLag>0?"warn":"")+'"><div class="hl-l">总投影滞后 Σ lag</div><div class="hl-v">'+totalLag+'</div></div>'+
      '<div class="hl '+(lagging>0?"warn":"")+'"><div class="hl-l">滞后的 scope</div><div class="hl-v">'+lagging+' / '+rows.length+'</div></div>'+
      '<div class="hl '+(totalFail>0?"bad":"")+'"><div class="hl-l">失败计数 Σ failures</div><div class="hl-v">'+totalFail+'</div></div>';
    hl.appendChild(hb);
    hl.appendChild(el("div",{style:"font-size:11.5px;color:var(--muted-2);margin-top:8px"},'<b style="color:var(--muted)">lag + failures = 头条指标。</b> last_observed_version（已见）− last_successful_version（已物化）= 投影滞后。'));
    card.appendChild(hl);

    /* family / owner facet bar — resolves opaque scope ids into navigable groups + people */
    card.appendChild(renderFilterBar());

    /* sortable list */
    const sh=el("div",{class:"sort-h"});
    const sortBtn=(key,label)=>'<button data-sort="'+key+'" '+(state.sort===key?'aria-sort="descending"':"")+'>'+label+' '+(state.sort===key?ICON.sort:"")+'</button>';
    sh.innerHTML='<span><button data-sort="id" '+(state.sort==="id"?'aria-sort="ascending"':"")+'>scope · 归属人</button></span>'+
      '<span class="col-gauge">observed → materialized</span>'+
      '<span style="text-align:right">'+sortBtn("lag","lag")+'</span>'+
      '<span style="text-align:right">'+sortBtn("fail","fails")+'</span>';
    sh.querySelectorAll("button").forEach(b=>b.addEventListener("click",()=>{ state.sort=b.getAttribute("data-sort"); state.scopePage=1; render(); }));
    card.appendChild(sh);

    const allScopes=sortedScopes();
    state.scopePage=pageClamp(state.scopePage, allScopes.length, SCOPE_PAGE_SIZE);
    const scopeStart=(state.scopePage-1)*SCOPE_PAGE_SIZE;
    const list=el("div",{role:"list"});
    allScopes.slice(scopeStart, scopeStart+SCOPE_PAGE_SIZE).forEach(s=>{
      const lag=lagOf(s); const pct=s.observed? Math.round(s.successful/s.observed*100):100;
      const gcls = lag===0?"":((s.failures>0||lag>15)?"bad":"lag");
      const dotCls = s.active?(lag>0||s.failures>0?(s.failures>0?"bad":"lag"):"active"):"idle";
      const fam=scopeFamily(s.id), tail=scopeTail(s.id), oid=scopeOwnerId(s.id), oname=oid?ownerCache[oid]:undefined;
      let ownerHtml="";
      if(oid){
        if(oname) ownerHtml='<span class="sc-owner" title="nyxid '+esc(oid)+'"><span class="av">'+esc(initials(oname))+'</span>'+esc(oname)+'</span>';
        else ownerHtml='<span class="sc-owner unresolved" title="nyxid '+esc(oid)+'（解析中或未知）"><span class="av">·</span>'+esc(oid.slice(0,8))+'</span>';
      }
      const row=el("button",{class:"scope-row",role:"listitem","aria-current":String(state.selectedScope===s.id)});
      row.innerHTML=
        '<span class="sc-id"><span class="dot s-'+dotCls+'"></span>'+
          '<span class="sc-main"><span class="sc-top"><span class="sc-fam" title="'+esc(s.id)+'">'+esc(fam)+'</span>'+ownerHtml+'</span>'+
          (tail?'<span class="sc-tail" title="'+esc(s.id)+'">'+esc(tail)+'</span>':'')+'</span></span>'+
        '<span class="cell-gauge"><div class="gauge '+gcls+'"><i style="width:'+pct+'%"></i></div><div class="gauge-cap">'+fmt(s.successful)+' / '+fmt(s.observed)+'</div></span>'+
        '<span class="lagcell '+(lag>0?(lag>15?"bad":"pos"):"")+'">'+(lag>0?"+"+lag:"0")+'</span>'+
        '<span class="failcell '+(s.failures>0?"pos":"")+'">'+(s.failures||0)+'</span>';
      row.addEventListener("click",()=>{ state.selectedScope=s.id; render(); });
      list.appendChild(row);
    });
    card.appendChild(list);
    const scopePager=renderPager(state.scopePage, allScopes.length, SCOPE_PAGE_SIZE, function(p){ state.scopePage=p; render(); });
    if(scopePager) card.appendChild(scopePager);
    return card;
  }

  /* non-ok states */
  if(st==="loading"){
    const body=el("div",{});
    for(let i=0;i<4;i++){
      const sk=el("div",{class:"skel"});
      sk.innerHTML='<span class="skel-bar" style="width:'+(36+i*8)+'%"></span><span class="skel-bar" style="width:64px;margin-left:auto"></span>';
      body.appendChild(sk);
    }
    card.appendChild(body);
    return card;
  }
  if(st==="empty"){
    card.appendChild(el("div",{class:"pstate"},
      '<div class="ps-ic lock">'+ICON.info+'</div><div class="ps-t">暂无投影 scope</div>'+
      '<div class="ps-b">平台当前没有任何已物化的投影 scope 状态记录。一旦有投影管道开始消费已提交事件，scope 行会在此出现。</div>'));
    return card;
  }
  if(st==="forbidden"){
    card.appendChild(el("div",{class:"pstate"},
      '<div class="ps-ic lock">'+ICON.lock+'</div><div class="ps-t">需要平台管理员权限</div>'+
      '<div class="ps-b">projection scope 状态为<b style="color:var(--fg)">平台级数据</b>（其键是投影 scope/actor id，并非你的租户 scope）。需平台管理员/运维角色方可查看。</div>'+
      (state.scopes.detail?'<div class="ps-detail">'+esc(state.scopes.detail)+'</div>':'')));
    return card;
  }
  /* error */
  card.appendChild(el("div",{class:"pstate"},
    '<div class="ps-ic err">'+ICON.warn+'</div><div class="ps-t">读取投影 scope 失败</div>'+
    '<div class="ps-b">后端返回错误。请稍后重试；若持续失败，请检查 projection-scope-status 读模型与平台鉴权。</div>'+
    (state.scopes.detail?'<div class="ps-detail">'+esc(state.scopes.detail)+'</div>':'')));
  return card;
}

/* ===========================================================================
   C) read-model inventory — LIVE via GET /api/cqrs/readmodels (platform-admin gated)
   401 -> re-login; 403 -> honest "needs platform admin" state; otherwise surfaces backend error detail.
   Nullable version/updated/count render as "—" — never fabricated.
   =========================================================================== */
function rmVersion(v){ return (v==null) ? "—" : "v"+v; }
function rmCount(c, shape){ if(c==null) return "—"; const u=shapeMeta(shape).unit; return fmt(c)+(u?" "+u:""); }
function rmUpdated(iso){
  if(!iso) return "—";
  const d=new Date(iso); if(isNaN(d.getTime())) return "—";
  const p=(n)=>String(n).padStart(2,"0");
  return d.getFullYear()+"-"+p(d.getMonth()+1)+"-"+p(d.getDate())+" "+p(d.getHours())+":"+p(d.getMinutes());
}
function renderInventory(){
  const card=el("section",{class:"panel"});
  const head=el("div",{class:"panel-head"});
  const tag = state.readmodels.status==="ok"
    ? '<span class="srctag live"><span class="dot"></span>数据来源 · LIVE</span>'
    : (state.readmodels.status==="loading" ? '<span class="srctag live" style="color:var(--muted);background:var(--neutral-soft);border-color:var(--border)"><span class="dot" style="background:var(--muted)"></span>加载中…</span>' : '<span class="pending">'+ICON.warn+' 平台级</span>');
  head.innerHTML='<h2>Read-model Inventory <span style="font-size:11px;font-weight:500;color:var(--muted-2)">· 已注册读模型 → 拥有 actor</span></h2><div class="ph-r">'+tag+'</div>';
  card.appendChild(head);

  const st = state.readmodels.status;
  const groups = state.readmodels.groups;

  if(st==="ok"){
    const body=el("div",{class:"panel-body"});
    groups.forEach(g=>{
      const meta=shapeMeta(g.shape);
      const items=Array.isArray(g.items)?g.items:[];
      const grp=el("div",{class:"inv-group"});
      grp.appendChild(el("div",{class:"inv-group-h"},
        '<span class="shape-badge '+esc(g.shape)+'">'+ICON.cube+' '+esc(meta.name)+'</span>'+
        '<span class="gh-eng">'+esc(g.engine||"")+'</span>'+
        '<span style="margin-left:auto;font-size:11px;color:var(--muted-2)">'+items.length+' 个读模型</span>'));
      if(items.length===0){
        grp.appendChild(el("div",{class:"inv-empty"},'该 sink 形状下暂无已注册读模型。'));
      } else {
        const invKey=String(g.shape);
        state.invPages[invKey]=pageClamp(state.invPages[invKey], items.length, RM_PAGE_SIZE);
        const invStart=(state.invPages[invKey]-1)*RM_PAGE_SIZE;
        items.slice(invStart, invStart+RM_PAGE_SIZE).forEach(it=>{
          const row=el("div",{class:"rm-row"});
          const actor=it.actor==null?"":String(it.actor);
          /* this endpoint exposes the registry (name + owning actor). version/updated are not
             surfaced here, so we DON'T render empty "v—"/"更新 —" noise — we show the actor that
             owns the read model, and the materialized count only when the backend actually has one. */
          const left='<div><div class="rm-n">'+esc(it.name)+'</div>'+
            '<div class="rm-m">'+(actor?'<span class="mono" title="拥有此读模型的 actor：'+esc(actor)+'">'+esc(actor)+'</span>':'<span class="mono">—</span>')+
            (it.version!=null?'<span>'+esc(rmVersion(it.version))+'</span>':'')+
            (it.updated?'<span>更新 '+esc(rmUpdated(it.updated))+'</span>':'')+'</div></div>';
          const right=it.count!=null?'<div class="rm-r"><div class="rm-v">'+esc(rmCount(it.count, g.shape))+'</div></div>':'';
          row.innerHTML=left+right;
          grp.appendChild(row);
        });
        const invPager=renderPager(state.invPages[invKey], items.length, RM_PAGE_SIZE, function(p){ state.invPages[invKey]=p; render(); }, true);
        if(invPager) grp.appendChild(invPager);
      }
      body.appendChild(grp);
    });
    card.appendChild(body);
    return card;
  }

  /* non-ok states — same markup as Panel B */
  if(st==="loading"){
    const body=el("div",{});
    for(let i=0;i<4;i++){
      const sk=el("div",{class:"skel"});
      sk.innerHTML='<span class="skel-bar" style="width:'+(40+i*7)+'%"></span><span class="skel-bar" style="width:48px;margin-left:auto"></span>';
      body.appendChild(sk);
    }
    card.appendChild(body);
    return card;
  }
  if(st==="empty"){
    card.appendChild(el("div",{class:"pstate"},
      '<div class="ps-ic lock">'+ICON.info+'</div><div class="ps-t">无已注册读模型</div>'+
      '<div class="ps-b">平台当前没有任何已注册的投影读模型。一旦有 projector 把已提交事件物化到某个 sink，读模型会在此按形状分组出现。</div>'));
    return card;
  }
  if(st==="forbidden"){
    card.appendChild(el("div",{class:"pstate"},
      '<div class="ps-ic lock">'+ICON.lock+'</div><div class="ps-t">需要平台管理员权限</div>'+
      '<div class="ps-b">projection 读模型清单为<b style="color:var(--fg)">平台级数据</b>。需平台管理员/运维角色方可查看。</div>'+
      (state.readmodels.detail?'<div class="ps-detail">'+esc(state.readmodels.detail)+'</div>':'')));
    return card;
  }
  /* error */
  card.appendChild(el("div",{class:"pstate"},
    '<div class="ps-ic err">'+ICON.warn+'</div><div class="ps-t">读取读模型清单失败</div>'+
    '<div class="ps-b">后端返回错误。请稍后重试；若持续失败，请检查 read-model 清单读取端点与平台鉴权。</div>'+
    (state.readmodels.detail?'<div class="ps-detail">'+esc(state.readmodels.detail)+'</div>':'')));
  return card;
}

/* ===========================================================================
   D) envelope inspector — list is unbacked (pending); state_root SHAPE kept
   =========================================================================== */
function renderInspector(){
  const card=el("section",{class:"panel",id:"inspector"});
  card.appendChild(el("div",{class:"panel-head"},'<h2>Envelope Inspector</h2><div class="ph-r"><span class="pending">'+ICON.warn+' 待后端</span></div>'));
  const body=el("div",{class:"panel-body"});

  /* recent-envelope list: unbacked (event-stream browse is a gap) → pending placeholder, no fake data */
  body.appendChild(el("div",{style:"font-size:11.5px;color:var(--muted-2);margin-bottom:9px"},"最近已提交信封"));
  body.appendChild(el("div",{class:"inv-empty",style:"margin-bottom:14px"},
    "最近信封列表（按版本浏览已提交事件元数据）尚未由后端暴露。上线后此处列出最近 committed envelope 并可点选查看其相关键。"));

  /* state_root snapshot SHAPE — structural (field names + types), always-true */
  body.appendChild(el("div",{style:"font-size:11px;letter-spacing:.04em;text-transform:uppercase;color:var(--muted-2);margin:4px 0 4px"},"state_root 快照形状"));
  const tree=el("div",{class:"shape-tree"});
  tree.innerHTML="{\n"+STATE_ROOT_SHAPE.map(([k,t])=>'  <span class="key">'+esc(k)+'</span>: <span class="typ">'+esc(t)+'</span>').join("\n")+"\n}";
  body.appendChild(tree);
  body.appendChild(el("div",{style:"font-size:11px;color:var(--muted-2);margin-top:6px"},"仅展示形状（字段+类型）。完整 payload 值为待后端。"));

  /* pending-backend stubs (kept verbatim in intent) */
  const stub1=el("div",{class:"stub"});
  stub1.innerHTML='<div class="st-h">'+ICON.lock+' 原始事件流 · browse / replay <span class="pending">'+ICON.warn+' 待后端</span></div>'+
    '<div class="st-b">浏览/回放底层事件流尚未由后端暴露。上线后可在此按版本区间回放，并把某个 scope 的投影重建。</div>';
  stub1.appendChild(el("button",{class:"stub-btn",disabled:"true","aria-disabled":"true"},ICON.lock+'<span>浏览原始事件流</span>'));
  body.appendChild(stub1);
  const stub2=el("div",{class:"stub"});
  stub2.innerHTML='<div class="st-h">'+ICON.lock+' 投影会话自省 · 每索引健康 <span class="pending">'+ICON.warn+' 待后端</span></div>'+
    '<div class="st-b">projection-session 自省、以及每个 ES / Neo4j 索引的健康，尚未暴露。当前可用：scope 级 active / observed vs successful / failures（见左侧 Scope Rail）。</div>';
  body.appendChild(stub2);

  card.appendChild(body);
  return card;
}

/* ===========================================================================
   compose
   =========================================================================== */
function render(){
  const app=$("#app"); app.innerHTML="";
  app.appendChild(renderTopbar());
  if(!state.signedIn){ app.appendChild(renderLogin()); return; }
  const body=el("div",{class:"body"});
  body.appendChild(el("h1",{class:"page-h"},"投影观测台 · 让数据流可读"));
  body.appendChild(el("p",{class:"page-sub"},'一个命令如何变更 actor、成为不可变的已提交事件（带单调版本的<b>单一真相源</b>），流经投影管道，物化成可查询的读模型副本——以及哪里健康、哪里滞后。读模型更新与实时 UI 流走<b>同一条管道</b>，不是两套系统。'));
  body.appendChild(renderCanvas());

  const grid=el("div",{class:"grid"});
  const main=el("div",{class:"col-main"});
  main.appendChild(renderScopeRail());
  main.appendChild(renderInventory());
  grid.appendChild(main);
  const side=el("div",{class:"col-side"});
  side.appendChild(renderInspector());
  grid.appendChild(side);
  body.appendChild(grid);

  app.appendChild(body);
}

/* ===========================================================================
   Live: scope chip (GET /api/studio/context) + projection scopes (GET /api/cqrs/scopes)
   =========================================================================== */
async function loadContext(){
  try {
    const res = await authFetch("/api/studio/context");
    if(res.status === 404){ return; }
    if(!res.ok){ console.warn("cqrs: context status", res.status); return; }
    const ctx = await res.json();
    if(ctx){
      state.scopeId = ctx.scopeId || null;
      state.scopeResolved = !!ctx.scopeResolved;
      state.scopeSource = ctx.scopeSource || null;
      render();
    }
  } catch(e){ console.warn("cqrs: context load failed", e); }
}

async function loadScopes(){
  state.scopes = { status:"loading", rows:[], detail:null };
  state.scopePage = 1;
  render();
  try {
    const res = await authFetch("/api/cqrs/scopes");
    if(res.status === 403){
      let detail=null;
      try { const b=await res.json(); detail = b && (b.message || b.detail || b.code); } catch(e){ /* tolerate */ }
      state.scopes = { status:"forbidden", rows:[], detail };
      render(); return;
    }
    if(!res.ok){
      let detail="HTTP "+res.status;
      try { const b=await res.json(); if(b && (b.message||b.detail||b.error||b.code)) detail = b.message||b.detail||b.error||b.code; } catch(e){ /* tolerate */ }
      state.scopes = { status:"error", rows:[], detail };
      render(); return;
    }
    const data = await res.json();
    const rows = (data && Array.isArray(data.scopes) ? data.scopes : []).map(s=>({
      id: s.id,
      active: !!s.active,
      observed: Number(s.observed||0),
      successful: Number(s.successful||0),
      failures: Number(s.failures||0)
    }));
    state.scopes = { status: rows.length? "ok":"empty", rows, detail:null };
    render();
    /* resolve nyxid owners embedded in scope ids, then re-render (best-effort, cached). */
    if(rows.length) resolveAllOwners(rows, render);
  } catch(e){
    if(e && e.message === "unauthorized") return; // signOutSilent already re-rendered
    state.scopes = { status:"error", rows:[], detail:String(e && e.message || e) };
    render();
  }
}

async function loadReadModels(){
  state.readmodels = { status:"loading", groups:[], detail:null };
  state.invPages = {};
  render();
  try {
    const res = await authFetch("/api/cqrs/readmodels");
    if(res.status === 403){
      let detail=null;
      try { const b=await res.json(); detail = b && (b.message || b.detail || b.code); } catch(e){ /* tolerate */ }
      state.readmodels = { status:"forbidden", groups:[], detail };
      render(); return;
    }
    if(!res.ok){
      let detail="HTTP "+res.status;
      try { const b=await res.json(); if(b && (b.message||b.detail||b.error||b.code)) detail = b.message||b.detail||b.error||b.code; } catch(e){ /* tolerate */ }
      state.readmodels = { status:"error", groups:[], detail };
      render(); return;
    }
    const data = await res.json();
    const groups = (data && Array.isArray(data.groups) ? data.groups : []).map(g=>({
      shape: g.shape,
      engine: g.engine || null,
      items: (Array.isArray(g.items) ? g.items : []).map(it=>({
        name: it.name,
        actor: it.actor,
        version: (it.version==null ? null : Number(it.version)),
        updated: it.updated || null,
        count: (it.count==null ? null : Number(it.count))
      }))
    }));
    const hasItems = groups.some(g=>g.items.length>0);
    state.readmodels = { status: hasItems ? "ok" : "empty", groups, detail:null };
    render();
  } catch(e){
    if(e && e.message === "unauthorized") return; // signOutSilent already re-rendered
    state.readmodels = { status:"error", groups:[], detail:String(e && e.message || e) };
    render();
  }
}

/* init */
applyTheme();
(async function init(){
  try { await completeLoginIfCallback(); }
  catch(e){ console.warn("cqrs: login callback failed", e); }
  if(!getToken()){ state.signedIn = false; render(); return; }
  state.signedIn = true;
  render();
  loadContext();
  loadScopes();
  loadReadModels();
  const acct = toAccount(await fetchUserInfo());
  if(acct){ accountLabel = acct.label; render(); }
})();
if(window.matchMedia){ window.matchMedia("(prefers-color-scheme: light)").addEventListener?.("change",()=>{ if(!state.theme)render(); }); }
</script>
</body>
</html>

""";
}
