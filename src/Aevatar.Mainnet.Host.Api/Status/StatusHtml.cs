namespace Aevatar.Mainnet.Host.Api.Status;

/// <summary>
/// Inline status dashboard. Single self-contained HTML page, no build step,
/// no external assets. It reads <c>/api/status</c> and renders current target
/// state plus the retained two-hour probe history.
/// </summary>
internal static class StatusHtml
{
    internal const string Page = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<title>Aevatar Status</title>
<style>
  :root {
    color-scheme: light dark;
    --bg: #f3f5f7;
    --panel: #ffffff;
    --panel-soft: #f8fafb;
    --border: #d7dde3;
    --ink: #151b23;
    --muted: #66717f;
    --soft: #eef2f5;
    --ok: #24a66a;
    --ok-soft: #dff6ea;
    --degraded: #c88413;
    --degraded-soft: #fff1cf;
    --down: #d83b42;
    --down-soft: #ffe1e3;
    --unknown: #a9b3bf;
    --shadow: 0 12px 32px rgba(17, 24, 39, 0.08);
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #0e1216;
      --panel: #171d23;
      --panel-soft: #1d242c;
      --border: #303945;
      --ink: #edf2f7;
      --muted: #99a5b3;
      --soft: #252d36;
      --ok-soft: rgba(36, 166, 106, 0.18);
      --degraded-soft: rgba(200, 132, 19, 0.18);
      --down-soft: rgba(216, 59, 66, 0.18);
      --shadow: 0 18px 42px rgba(0, 0, 0, 0.24);
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    min-height: 100vh;
    background: var(--bg);
    color: var(--ink);
    font-family: Aptos, "Helvetica Neue", Helvetica, sans-serif;
  }
  .shell {
    width: min(1120px, calc(100% - 32px));
    margin: 0 auto;
    padding: 30px 0 26px;
  }
  header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 20px;
    margin-bottom: 20px;
  }
  h1 {
    margin: 0;
    font-size: 28px;
    line-height: 1.1;
    font-weight: 720;
    letter-spacing: 0;
  }
  .subtitle {
    margin-top: 6px;
    color: var(--muted);
    font-size: 13px;
  }
  .overall {
    display: inline-flex;
    align-items: center;
    gap: 10px;
    min-height: 42px;
    padding: 0 16px;
    border: 1px solid var(--border);
    border-radius: 999px;
    background: var(--panel);
    box-shadow: var(--shadow);
    font-size: 14px;
    font-weight: 700;
    white-space: nowrap;
  }
  .dot {
    width: 10px;
    height: 10px;
    border-radius: 50%;
    background: var(--unknown);
  }
  .dot.ok { background: var(--ok); }
  .dot.degraded { background: var(--degraded); }
  .dot.down { background: var(--down); }
  .dot.unknown { background: var(--unknown); }
  .summary {
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: 10px;
    margin-bottom: 26px;
  }
  .metric {
    min-height: 72px;
    padding: 14px 16px;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--panel);
  }
  .metric b {
    display: block;
    font-size: 22px;
    line-height: 1;
  }
  .metric span {
    display: block;
    margin-top: 7px;
    color: var(--muted);
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.08em;
  }
  main {
    display: flex;
    flex-direction: column;
    gap: 24px;
  }
  .group {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .group-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 0 2px;
  }
  .group h2 {
    margin: 0;
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.1em;
    color: var(--muted);
    font-weight: 800;
  }
  .group-count {
    color: var(--muted);
    font-size: 12px;
  }
  .target {
    display: grid;
    grid-template-columns: minmax(220px, 260px) minmax(360px, 1fr) 112px;
    align-items: center;
    gap: 18px;
    padding: 13px 16px;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--panel);
  }
  .target.disabled {
    opacity: 0.54;
  }
  .identity {
    display: grid;
    grid-template-columns: 58px minmax(0, 1fr);
    gap: 12px;
    align-items: center;
    min-width: 0;
  }
  .availability {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    height: 36px;
    border-radius: 999px;
    background: var(--ok-soft);
    color: var(--ok);
    font-size: 13px;
    font-weight: 800;
    font-variant-numeric: tabular-nums;
  }
  .target.down .availability { background: var(--down-soft); color: var(--down); }
  .target.degraded .availability { background: var(--degraded-soft); color: var(--degraded); }
  .target.unknown .availability { background: var(--soft); color: var(--muted); }
  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 14px;
    font-weight: 720;
  }
  .detail {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    margin-top: 4px;
    color: var(--muted);
    font-size: 12px;
  }
  .bars-wrap {
    min-width: 0;
    overflow-x: auto;
    padding: 2px 0;
  }
  .bars {
    display: grid;
    grid-template-columns: repeat(120, minmax(2px, 1fr));
    gap: 2px;
    min-width: 430px;
    height: 34px;
    align-items: stretch;
  }
  .bar {
    border-radius: 2px;
    background: var(--unknown);
    opacity: 0.9;
  }
  .bar.ok { background: var(--ok); }
  .bar.degraded { background: var(--degraded); }
  .bar.down { background: var(--down); }
  .bar.empty {
    background: var(--soft);
    opacity: 1;
  }
  .latest {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    gap: 4px;
    min-width: 0;
  }
  .status {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    font-size: 12px;
    font-weight: 800;
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }
  .age {
    color: var(--muted);
    font-size: 12px;
    font-variant-numeric: tabular-nums;
  }
  .message {
    grid-column: 1 / -1;
    padding: 10px 12px;
    border-radius: 6px;
    background: var(--panel-soft);
    color: var(--muted);
    font-family: "SF Mono", ui-monospace, Menlo, Consolas, monospace;
    font-size: 11px;
    line-height: 1.45;
    word-break: break-word;
  }
  .empty {
    padding: 34px 20px;
    border: 1px dashed var(--border);
    border-radius: 8px;
    background: var(--panel);
    color: var(--muted);
    text-align: center;
  }
  footer {
    display: flex;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
    margin-top: 28px;
    color: var(--muted);
    font-size: 12px;
  }
  a { color: inherit; }
  @media (max-width: 820px) {
    .shell { width: min(100% - 22px, 1120px); padding-top: 18px; }
    header { align-items: stretch; flex-direction: column; }
    .overall { width: max-content; box-shadow: none; }
    .summary { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .target {
      grid-template-columns: 1fr;
      gap: 11px;
      padding: 13px;
    }
    .identity { grid-template-columns: 54px minmax(0, 1fr); }
    .latest { align-items: flex-start; }
    .message { grid-column: auto; }
  }
</style>
</head>
<body>
<div class="shell">
  <header>
    <div>
      <h1>Aevatar Status</h1>
      <div class="subtitle">Mainnet service probes, retained for the latest 2 hours.</div>
    </div>
    <div id="overall" class="overall"><span class="dot unknown"></span> loading</div>
  </header>
  <div id="summary" class="summary"></div>
  <main id="main"></main>
  <footer>
    <span id="updated">waiting for /api/status</span>
    <span>auto-refresh 60s · <a href="/api/status">/api/status</a></span>
  </footer>
</div>
<script>
(() => {
  const WINDOW_SAMPLES = 120;
  const refreshMs = 60_000;
  const fmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const overall = document.getElementById('overall');
  const summary = document.getElementById('summary');
  const main = document.getElementById('main');
  const updated = document.getElementById('updated');

  function escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }
  function age(iso) {
    if (!iso) return 'n/a';
    const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
    if (sec < 60) return sec + 's ago';
    if (sec < 3600) return Math.round(sec / 60) + 'm ago';
    return Math.round(sec / 3600) + 'h ago';
  }
  function groupTitle(cat) {
    if (cat === 'self') return 'Self';
    if (cat === 'feature') return 'Features';
    return 'Upstream';
  }
  function availabilityText(value) {
    return typeof value === 'number' ? value.toFixed(value % 1 === 0 ? 0 : 1) + '%' : 'n/a';
  }
  function renderBars(history) {
    const samples = Array.isArray(history) ? history.slice(-WINDOW_SAMPLES) : [];
    const slots = Array(Math.max(0, WINDOW_SAMPLES - samples.length)).fill(null).concat(samples);
    return slots.map(sample => {
      if (!sample) return '<span class="bar empty" title="no sample"></span>';
      const status = sample.status || 'unknown';
      const when = sample.observed_at ? fmt.format(new Date(sample.observed_at)) : 'unknown time';
      const detail = sample.detail ? ' · ' + sample.detail : '';
      const latency = Number.isFinite(sample.latency_ms) ? ' · ' + sample.latency_ms + 'ms' : '';
      return `<span class="bar ${escapeHtml(status)}" title="${escapeHtml(when + ' · ' + status + latency + detail)}"></span>`;
    }).join('');
  }
  function metric(label, value) {
    return `<div class="metric"><b>${escapeHtml(value)}</b><span>${escapeHtml(label)}</span></div>`;
  }
  function render(data) {
    const total = data.counts.total || 0;
    overall.innerHTML = `<span class="dot ${escapeHtml(data.overall)}"></span>${escapeHtml(data.overall.toUpperCase())} · ${data.counts.ok}/${total} ok`;
    summary.innerHTML =
      metric('ok', data.counts.ok) +
      metric('degraded', data.counts.degraded) +
      metric('down', data.counts.down) +
      metric('unknown', data.counts.unknown);
    updated.textContent = 'generated ' + fmt.format(new Date(data.generated_at));
    main.innerHTML = '';
    if (!data.targets.length) {
      const e = document.createElement('div');
      e.className = 'empty';
      e.textContent = 'No probe targets configured.';
      main.appendChild(e);
      return;
    }

    const groups = {};
    for (const t of data.targets) (groups[t.category] = groups[t.category] || []).push(t);
    const order = ['self', 'feature', 'upstream'];
    for (const cat of order.concat(Object.keys(groups).filter(c => !order.includes(c)))) {
      const targets = groups[cat];
      if (!targets) continue;
      const sec = document.createElement('section');
      sec.className = 'group';
      sec.innerHTML = `<div class="group-head"><h2>${escapeHtml(groupTitle(cat))}</h2><span class="group-count">${targets.length} targets</span></div>`;
      for (const t of targets) {
        const row = document.createElement('article');
        row.className = `target ${escapeHtml(t.status)}${t.enabled ? '' : ' disabled'}`;
        const signal = t.detail ? t.detail : t.probe;
        row.innerHTML = `
          <div class="identity">
            <div class="availability">${escapeHtml(availabilityText(t.availability_percent))}</div>
            <div>
              <div class="name" title="${escapeHtml(t.name)}">${escapeHtml(t.name)}</div>
              <div class="detail" title="${escapeHtml(signal)}">${escapeHtml(signal)}</div>
            </div>
          </div>
          <div class="bars-wrap"><div class="bars">${renderBars(t.history)}</div></div>
          <div class="latest">
            <div class="status"><span class="dot ${escapeHtml(t.status)}"></span>${escapeHtml(t.status)}</div>
            <div class="age">${escapeHtml(t.latency_ms + 'ms · ' + age(t.last_check_at))}</div>
          </div>`;
        if (t.error_message) {
          const msg = document.createElement('div');
          msg.className = 'message';
          msg.textContent = t.error_message;
          row.appendChild(msg);
        }
        sec.appendChild(row);
      }
      main.appendChild(sec);
    }
  }
  async function refresh() {
    try {
      const r = await fetch('/api/status', { cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      render(await r.json());
    } catch (e) {
      overall.innerHTML = '<span class="dot down"></span>/api/status unreachable';
      updated.textContent = String(e.message || e);
    }
  }
  refresh();
  setInterval(refresh, refreshMs);
})();
</script>
</body>
</html>
""";
}
