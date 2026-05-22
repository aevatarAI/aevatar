namespace Aevatar.Mainnet.Host.Api.Status;

/// <summary>
/// Inline sub2api-style status dashboard. Single self-contained HTML page,
/// no build step, no external assets — fetches <c>/api/status</c> and renders
/// grouped cards by category.
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
    --bg: #0d1117;
    --surface: #161b22;
    --surface-soft: #1c232c;
    --border: #30363d;
    --text: #e6edf3;
    --muted: #8b949e;
    --ok: #3fb950;
    --degraded: #d29922;
    --down: #f85149;
    --unknown: #8b949e;
  }
  @media (prefers-color-scheme: light) {
    :root {
      --bg: #f6f8fa;
      --surface: #ffffff;
      --surface-soft: #f0f3f6;
      --border: #d0d7de;
      --text: #1f2328;
      --muted: #57606a;
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 24px;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
    background: var(--bg); color: var(--text);
    min-height: 100vh;
  }
  header {
    max-width: 960px; margin: 0 auto 28px auto;
    display: flex; align-items: center; justify-content: space-between; gap: 16px;
    flex-wrap: wrap;
  }
  h1 { margin: 0; font-size: 22px; font-weight: 600; letter-spacing: -0.01em; }
  .overall {
    display: inline-flex; align-items: center; gap: 10px;
    padding: 8px 14px; border-radius: 999px;
    background: var(--surface); border: 1px solid var(--border);
    font-size: 14px; font-weight: 500;
  }
  .dot { width: 10px; height: 10px; border-radius: 50%; }
  .dot.ok { background: var(--ok); }
  .dot.degraded { background: var(--degraded); }
  .dot.down { background: var(--down); }
  .dot.unknown { background: var(--unknown); }
  .meta { color: var(--muted); font-size: 12px; }
  main { max-width: 960px; margin: 0 auto; display: flex; flex-direction: column; gap: 24px; }
  section.group { display: flex; flex-direction: column; gap: 8px; }
  section.group > h2 {
    margin: 0; font-size: 12px; text-transform: uppercase; letter-spacing: 0.08em;
    color: var(--muted); font-weight: 600;
  }
  .card {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 4px 16px;
    padding: 14px 18px;
    background: var(--surface); border: 1px solid var(--border); border-radius: 10px;
  }
  .card .name { font-weight: 500; font-size: 15px; }
  .card .detail { color: var(--muted); font-size: 12px; }
  .card .status {
    display: inline-flex; align-items: center; gap: 8px;
    font-size: 13px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em;
  }
  .card .right { text-align: right; }
  .card .right .meta { margin-top: 2px; }
  .card.disabled { opacity: 0.5; }
  .error {
    margin-top: 8px; padding: 10px 12px;
    background: var(--surface-soft); border-radius: 6px;
    color: var(--muted); font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    font-size: 11px; white-space: pre-wrap; word-break: break-word;
  }
  .empty {
    padding: 32px; border: 1px dashed var(--border); border-radius: 10px;
    text-align: center; color: var(--muted);
  }
  footer {
    max-width: 960px; margin: 32px auto 0 auto; color: var(--muted); font-size: 11px;
    display: flex; gap: 12px; flex-wrap: wrap; justify-content: space-between;
  }
  a { color: inherit; }
</style>
</head>
<body>
<header>
  <h1>Aevatar Status</h1>
  <div id="overall" class="overall"><span class="dot unknown"></span> loading…</div>
</header>
<main id="main"></main>
<footer>
  <span id="updated" class="meta">—</span>
  <span class="meta">auto-refresh 30s · <a href="/api/status">/api/status</a></span>
</footer>
<script>
(() => {
  const fmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const overall = document.getElementById('overall');
  const main = document.getElementById('main');
  const updated = document.getElementById('updated');
  function ago(iso) {
    if (!iso) return '—';
    const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
    if (sec < 60) return sec + 's ago';
    if (sec < 3600) return Math.round(sec/60) + 'm ago';
    return Math.round(sec/3600) + 'h ago';
  }
  function groupTitle(cat) {
    if (cat === 'self') return 'Self';
    if (cat === 'feature') return 'Features';
    return 'Upstream';
  }
  function render(data) {
    overall.innerHTML = `<span class="dot ${data.overall}"></span> ${data.overall.toUpperCase()} · ${data.counts.ok}/${data.counts.total} ok`;
    updated.textContent = 'generated ' + fmt.format(new Date(data.generated_at));
    main.innerHTML = '';
    if (!data.targets.length) {
      const e = document.createElement('div');
      e.className = 'empty';
      e.textContent = 'No probe targets configured. Set Aevatar:Status:Targets in configuration.';
      main.appendChild(e);
      return;
    }
    const groups = {};
    for (const t of data.targets) {
      (groups[t.category] = groups[t.category] || []).push(t);
    }
    const order = ['self', 'upstream', 'feature'];
    for (const cat of order.concat(Object.keys(groups).filter(c => !order.includes(c)))) {
      if (!groups[cat]) continue;
      const sec = document.createElement('section');
      sec.className = 'group';
      const h = document.createElement('h2');
      h.textContent = groupTitle(cat);
      sec.appendChild(h);
      for (const t of groups[cat]) {
        const card = document.createElement('div');
        card.className = 'card' + (t.enabled ? '' : ' disabled');
        const left = document.createElement('div');
        const right = document.createElement('div');
        right.className = 'right';
        left.innerHTML = `<div class="name">${escapeHtml(t.name)}</div>` +
                          `<div class="detail">${escapeHtml(t.probe)}${t.detail ? ' · ' + escapeHtml(t.detail) : ''}</div>`;
        right.innerHTML = `<div class="status"><span class="dot ${t.status}"></span>${t.status}</div>` +
                          `<div class="meta">${t.latency_ms}ms · ${ago(t.last_check_at)}</div>`;
        card.appendChild(left);
        card.appendChild(right);
        if (t.error_message) {
          const err = document.createElement('div');
          err.className = 'error';
          err.style.gridColumn = '1 / -1';
          err.textContent = t.error_message;
          card.appendChild(err);
        }
        sec.appendChild(card);
      }
      main.appendChild(sec);
    }
  }
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }
  async function refresh() {
    try {
      const r = await fetch('/api/status', { cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      render(await r.json());
    } catch (e) {
      overall.innerHTML = `<span class="dot down"></span> /api/status unreachable`;
      updated.textContent = String(e.message || e);
    }
  }
  refresh();
  setInterval(refresh, 30_000);
})();
</script>
</body>
</html>
""";
}
