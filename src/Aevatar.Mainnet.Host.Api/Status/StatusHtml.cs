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
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<title>Aevatar Status</title>
<style>
  :root {
    color-scheme: light dark;
    --bg: #f4f2ea;
    --paper: #fffdf5;
    --paper-soft: #f8f3e6;
    --paper-strong: #eef8e8;
    --border: #ddd4bf;
    --border-strong: #b9d9ad;
    --ink: #25231f;
    --muted: #80796b;
    --faint: #e8dfcc;
    --ok: #438f30;
    --ok-soft: #dff3d2;
    --degraded: #9a6a20;
    --degraded-soft: #ffe0ad;
    --down: #bd3b32;
    --down-soft: #f9cfc9;
    --unknown: #b6ac96;
    --focus: #315bba;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #12100d;
      --paper: #1c1914;
      --paper-soft: #242017;
      --paper-strong: #1f2c1c;
      --border: #423a2c;
      --border-strong: #42633a;
      --ink: #f2eadc;
      --muted: #b4aa99;
      --faint: #332d22;
      --ok-soft: rgba(67, 143, 48, 0.24);
      --degraded-soft: rgba(154, 106, 32, 0.26);
      --down-soft: rgba(189, 59, 50, 0.24);
      --unknown: #766d5c;
      --focus: #8fb3ff;
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    min-height: 100vh;
    background: var(--bg);
    color: var(--ink);
    font-family: "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC", "Helvetica Neue", Arial, sans-serif;
  }
  .shell {
    width: min(1320px, calc(100% - 32px));
    margin: 0 auto;
    padding: 34px 0 28px;
  }
  header {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 18px;
    align-items: end;
    margin-bottom: 24px;
  }
  h1 {
    margin: 0;
    font-size: 27px;
    line-height: 1.15;
    font-weight: 800;
    letter-spacing: 0;
  }
  .toolbar {
    display: flex;
    align-items: center;
    gap: 14px;
    color: var(--muted);
    font-size: 13px;
    font-weight: 700;
    white-space: nowrap;
  }
  a { color: inherit; text-decoration-thickness: 1px; text-underline-offset: 3px; }
  .overall {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    gap: 16px;
    align-items: center;
    min-height: 78px;
    margin-bottom: 24px;
    padding: 20px 28px;
    border: 1px solid var(--border-strong);
    border-radius: 8px;
    background: var(--paper-strong);
  }
  .overall.down { background: var(--down-soft); border-color: rgba(189, 59, 50, 0.45); }
  .overall.degraded { background: var(--degraded-soft); border-color: rgba(154, 106, 32, 0.45); }
  .overall.unknown { background: var(--paper-soft); border-color: var(--border); }
  .badge-dot {
    display: inline-grid;
    place-items: center;
    width: 30px;
    height: 30px;
    border-radius: 50%;
    color: #fff;
    background: var(--unknown);
    font-size: 18px;
    font-weight: 900;
  }
  .badge-dot.ok { background: var(--ok); }
  .badge-dot.degraded { background: var(--degraded); }
  .badge-dot.down { background: var(--down); }
  .overall-title {
    margin: 0;
    font-size: 18px;
    font-weight: 800;
  }
  .overall-sub {
    margin-top: 4px;
    color: var(--muted);
    font-size: 13px;
    font-weight: 650;
  }
  .updated {
    color: var(--muted);
    font-size: 14px;
    font-weight: 700;
    text-align: right;
    white-space: nowrap;
  }
  main {
    display: flex;
    flex-direction: column;
    gap: 22px;
  }
  .group {
    border: 1px solid var(--border);
    border-radius: 10px;
    background: var(--paper);
    padding: 22px;
  }
  .group-head {
    display: flex;
    justify-content: space-between;
    gap: 12px;
    align-items: center;
    margin-bottom: 18px;
  }
  .group h2 {
    margin: 0;
    font-size: 17px;
    line-height: 1.25;
    font-weight: 800;
  }
  .group-count {
    color: var(--muted);
    font-size: 13px;
    font-weight: 700;
  }
  .target-list {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .target {
    border-radius: 8px;
    background: color-mix(in srgb, var(--paper) 86%, var(--paper-soft));
  }
  .target.open {
    background: color-mix(in srgb, var(--paper-soft) 70%, var(--paper));
  }
  .target-summary {
    width: 100%;
    display: grid;
    grid-template-columns: minmax(300px, 390px) minmax(520px, 1fr) 132px 28px;
    gap: 18px;
    align-items: center;
    padding: 16px 14px;
    border: 0;
    border-radius: 8px;
    background: transparent;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .target-summary:focus-visible {
    outline: 3px solid color-mix(in srgb, var(--focus) 40%, transparent);
    outline-offset: 2px;
  }
  .target.disabled {
    opacity: 0.58;
  }
  .identity {
    display: grid;
    grid-template-columns: 84px minmax(0, 1fr);
    gap: 16px;
    align-items: center;
    min-width: 0;
  }
  .availability {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 84px;
    min-height: 34px;
    border-radius: 999px;
    background: var(--ok-soft);
    color: var(--ok);
    font-size: 15px;
    font-weight: 900;
    font-variant-numeric: tabular-nums;
  }
  .target.down .availability { background: var(--down-soft); color: var(--down); }
  .target.degraded .availability { background: var(--degraded-soft); color: var(--degraded); }
  .target.unknown .availability { background: var(--faint); color: var(--muted); }
  .name {
    font-size: 16px;
    line-height: 1.35;
    font-weight: 800;
    overflow-wrap: anywhere;
  }
  .signal {
    margin-top: 5px;
    color: var(--muted);
    font-size: 12px;
    line-height: 1.35;
    font-weight: 700;
    overflow-wrap: anywhere;
  }
  .timeline {
    min-width: 0;
  }
  .bars-wrap {
    min-width: 0;
    overflow-x: auto;
    scrollbar-width: thin;
    padding: 2px 0 1px;
  }
  .bars {
    display: grid;
    grid-template-columns: repeat(120, minmax(3px, 1fr));
    gap: 3px;
    min-width: 650px;
    height: 39px;
    align-items: stretch;
  }
  .bar {
    border-radius: 2px;
    background: var(--unknown);
  }
  .bar.ok { background: var(--ok); }
  .bar.degraded { background: var(--degraded); }
  .bar.down { background: var(--down); }
  .bar.empty { background: var(--faint); }
  .axis {
    display: flex;
    justify-content: space-between;
    gap: 10px;
    margin-top: 6px;
    color: var(--muted);
    font-size: 12px;
    font-weight: 700;
  }
  .latest {
    display: grid;
    gap: 4px;
    justify-items: end;
    min-width: 0;
  }
  .status {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    font-size: 12px;
    font-weight: 900;
    text-transform: uppercase;
    letter-spacing: 0.04em;
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
  .age {
    color: var(--muted);
    font-size: 12px;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    text-align: right;
  }
  .chevron {
    display: grid;
    place-items: center;
    width: 28px;
    height: 28px;
    border-radius: 50%;
    color: var(--muted);
    font-size: 18px;
    font-weight: 900;
  }
  .target.open .chevron {
    background: var(--faint);
  }
  .target-details {
    margin: 0 14px 16px;
    padding: 16px 18px;
    border: 1px solid var(--faint);
    border-radius: 7px;
    background: color-mix(in srgb, var(--paper) 70%, var(--paper-soft));
  }
  .details-grid {
    display: grid;
    grid-template-columns: 116px minmax(0, 1fr);
    gap: 9px 18px;
    font-size: 14px;
    line-height: 1.45;
  }
  .details-grid dt {
    color: var(--muted);
    font-weight: 800;
  }
  .details-grid dd {
    margin: 0;
    font-weight: 700;
    overflow-wrap: anywhere;
  }
  .message {
    margin-top: 14px;
    padding: 12px;
    border-radius: 6px;
    background: var(--paper-soft);
    color: var(--muted);
    font-family: "SF Mono", ui-monospace, Menlo, Consolas, monospace;
    font-size: 12px;
    line-height: 1.5;
    overflow-wrap: anywhere;
  }
  .empty {
    padding: 34px 20px;
    border: 1px dashed var(--border);
    border-radius: 8px;
    background: var(--paper);
    color: var(--muted);
    text-align: center;
    font-weight: 700;
  }
  footer {
    margin-top: 26px;
    color: var(--muted);
    font-size: 12px;
    font-weight: 700;
  }
  @media (max-width: 980px) {
    .shell { width: min(100% - 22px, 1320px); padding-top: 20px; }
    header {
      grid-template-columns: 1fr;
      align-items: start;
    }
    .toolbar { white-space: normal; flex-wrap: wrap; }
    .overall {
      grid-template-columns: auto minmax(0, 1fr);
      padding: 18px;
    }
    .updated {
      grid-column: 1 / -1;
      text-align: left;
    }
    .group { padding: 16px 12px; }
    .target-summary {
      grid-template-columns: 1fr;
      gap: 12px;
      padding: 14px 10px;
    }
    .identity { grid-template-columns: 78px minmax(0, 1fr); }
    .availability { min-width: 78px; }
    .latest { justify-items: start; }
    .age { text-align: left; }
    .chevron {
      position: absolute;
      right: 22px;
      margin-top: -42px;
    }
    .target-details { margin: 0 10px 12px; }
  }
  @media (max-width: 560px) {
    h1 { font-size: 23px; }
    .overall-title { font-size: 16px; }
    .details-grid {
      grid-template-columns: 1fr;
      gap: 4px;
    }
    .details-grid dd { margin-bottom: 8px; }
  }
</style>
</head>
<body>
<div class="shell">
  <header>
    <h1>Aevatar 服务状态</h1>
    <nav class="toolbar" aria-label="status links">
      <span>最近 120 分钟</span>
      <span>每 60 秒探测</span>
      <a href="/api/status">原始 JSON</a>
    </nav>
  </header>
  <section id="overall" class="overall unknown" aria-live="polite">
    <span class="badge-dot unknown">?</span>
    <div>
      <p class="overall-title">正在读取服务状态</p>
      <div class="overall-sub">等待 /api/status 返回</div>
    </div>
    <div id="updated" class="updated">--</div>
  </section>
  <main id="main"></main>
  <footer>Aevatar Mainnet 服务状态</footer>
</div>
<script>
(() => {
  const WINDOW_SAMPLES = 120;
  const refreshMs = 60_000;
  const openTargets = new Set();
  const timeFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const dateFmt = new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  });
  const overall = document.getElementById('overall');
  const main = document.getElementById('main');
  const updated = document.getElementById('updated');

  function escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }
  function safeId(s) {
    return String(s ?? 'target').replace(/[^a-zA-Z0-9_-]/g, '-');
  }
  function statusLabel(status) {
    if (status === 'ok') return '正常';
    if (status === 'degraded') return '降级';
    if (status === 'down') return '故障';
    return '未知';
  }
  function groupTitle(cat) {
    if (cat === 'self') return 'A. 自身服务';
    if (cat === 'feature') return 'B. 功能入口';
    return 'C. 上游依赖';
  }
  function ago(iso) {
    if (!iso) return '暂无';
    const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
    if (sec < 60) return sec + ' 秒前';
    if (sec < 3600) return Math.round(sec / 60) + ' 分钟前';
    return Math.round(sec / 3600) + ' 小时前';
  }
  function fullTime(iso) {
    return iso ? dateFmt.format(new Date(iso)) : '--';
  }
  function availabilityText(value) {
    return typeof value === 'number' ? value.toFixed(value % 1 === 0 ? 0 : 1) + '%' : 'n/a';
  }
  function samplesForTarget(t) {
    if (Array.isArray(t.history) && t.history.length) return t.history.slice(-WINDOW_SAMPLES);
    if (!t.last_check_at) return [];
    return [{
      status: t.status || 'unknown',
      latency_ms: t.latency_ms || 0,
      detail: t.detail,
      error_message: t.error_message,
      observed_at: t.last_check_at
    }];
  }
  function historyStats(samples) {
    const known = samples.filter(s => (s.status || 'unknown') !== 'unknown');
    const ok = known.filter(s => s.status === 'ok');
    const failed = known.filter(s => s.status === 'down' || s.status === 'degraded');
    const lastFailure = [...failed].reverse()[0];
    const lastSuccess = [...ok].reverse()[0];
    return { known: known.length, ok: ok.length, failed: failed.length, lastFailure, lastSuccess };
  }
  function renderBars(samples) {
    const slots = Array(Math.max(0, WINDOW_SAMPLES - samples.length)).fill(null).concat(samples);
    return slots.map(sample => {
      if (!sample) return '<span class="bar empty" title="无样本"></span>';
      const status = sample.status || 'unknown';
      const when = sample.observed_at ? timeFmt.format(new Date(sample.observed_at)) : '未知时间';
      const detail = sample.detail ? ' · ' + sample.detail : '';
      const latency = Number.isFinite(sample.latency_ms) ? ' · ' + sample.latency_ms + 'ms' : '';
      return `<span class="bar ${escapeHtml(status)}" title="${escapeHtml(when + ' · ' + statusLabel(status) + latency + detail)}"></span>`;
    }).join('');
  }
  function overallTitle(status, counts) {
    if (!counts.total) return '还没有探针数据';
    if (status === 'ok') return '全部服务运行正常';
    if (status === 'down') return counts.down + ' 个服务故障';
    if (status === 'degraded') return counts.degraded + ' 个服务降级';
    return counts.unknown + ' 个服务状态未知';
  }
  function renderOverall(data) {
    const status = data.overall || 'unknown';
    const counts = data.counts || { ok: 0, degraded: 0, down: 0, unknown: 0, total: 0 };
    overall.className = `overall ${escapeHtml(status)}`;
    overall.innerHTML = `
      <span class="badge-dot ${escapeHtml(status)}">${status === 'ok' ? '✓' : status === 'unknown' ? '?' : '!'}</span>
      <div>
        <p class="overall-title">${escapeHtml(overallTitle(status, counts))}</p>
        <div class="overall-sub">${escapeHtml(counts.ok + ' / ' + counts.total + ' 正常 · ' + counts.degraded + ' 降级 · ' + counts.down + ' 故障 · ' + counts.unknown + ' 未知')}</div>
      </div>
      <div id="updated" class="updated">更新于 ${escapeHtml(fullTime(data.generated_at))}</div>`;
  }
  function renderDetails(t, samples, stats) {
    const lastFailureAt = stats.lastFailure?.observed_at;
    const lastFailureText = lastFailureAt ? fullTime(lastFailureAt) : '--';
    const lastFailureSignal = stats.lastFailure?.detail ? ' · ' + stats.lastFailure.detail : '';
    const latestError = t.error_message || stats.lastFailure?.error_message;
    return `
      <dl class="details-grid">
        <dt>ID</dt><dd>${escapeHtml(t.slug)}</dd>
        <dt>名称</dt><dd>${escapeHtml(t.name)}</dd>
        <dt>分组</dt><dd>${escapeHtml(groupTitle(t.category))}</dd>
        <dt>探针</dt><dd>${escapeHtml(t.probe)}</dd>
        <dt>最近窗口</dt><dd>${escapeHtml(stats.ok + ' / ' + stats.known + ' 成功，保留 ' + samples.length + ' 个样本')}</dd>
        <dt>当前信号</dt><dd>${escapeHtml((t.detail || '--') + ' · ' + (t.latency_ms ?? 0) + 'ms')}</dd>
        <dt>连续失败</dt><dd>${escapeHtml(t.consecutive_failures ?? 0)}</dd>
        <dt>末次检查</dt><dd>${escapeHtml(fullTime(t.last_check_at))}</dd>
        <dt>末次成功</dt><dd>${escapeHtml(fullTime(t.last_success_at || stats.lastSuccess?.observed_at))}</dd>
        <dt>末次失败</dt><dd>${escapeHtml(lastFailureText + lastFailureSignal)}</dd>
      </dl>
      ${latestError ? `<div class="message">${escapeHtml(latestError)}</div>` : ''}`;
  }
  function render(data) {
    renderOverall(data);
    main.innerHTML = '';
    const targets = Array.isArray(data.targets) ? data.targets : [];
    if (!targets.length) {
      const e = document.createElement('div');
      e.className = 'empty';
      e.textContent = '暂无探针目标。';
      main.appendChild(e);
      return;
    }

    const groups = {};
    for (const t of targets) (groups[t.category] = groups[t.category] || []).push(t);
    const order = ['self', 'feature', 'upstream'];
    for (const cat of order.concat(Object.keys(groups).filter(c => !order.includes(c)))) {
      const groupTargets = groups[cat];
      if (!groupTargets) continue;
      const sec = document.createElement('section');
      sec.className = 'group';
      sec.innerHTML = `
        <div class="group-head">
          <h2>${escapeHtml(groupTitle(cat))}</h2>
          <span class="group-count">${groupTargets.length} 个探针</span>
        </div>
        <div class="target-list"></div>`;
      const list = sec.querySelector('.target-list');
      for (const t of groupTargets) {
        const samples = samplesForTarget(t);
        const stats = historyStats(samples);
        const id = safeId(t.slug);
        const isOpen = openTargets.has(t.slug);
        const signal = t.detail || t.probe || '--';
        const row = document.createElement('article');
        row.className = `target ${escapeHtml(t.status || 'unknown')}${t.enabled === false ? ' disabled' : ''}${isOpen ? ' open' : ''}`;
        row.innerHTML = `
          <button class="target-summary" type="button" aria-expanded="${isOpen ? 'true' : 'false'}" aria-controls="details-${escapeHtml(id)}">
            <div class="identity">
              <span class="availability">${escapeHtml(availabilityText(t.availability_percent))}</span>
              <div>
                <div class="name">${escapeHtml(t.name || t.slug)}</div>
                <div class="signal">${escapeHtml(signal)}</div>
              </div>
            </div>
            <div class="timeline">
              <div class="bars-wrap"><div class="bars">${renderBars(samples)}</div></div>
              <div class="axis"><span>2 小时前</span><span>${escapeHtml(stats.lastFailure ? ago(stats.lastFailure.observed_at) + ' 曾失败' : '现在')}</span></div>
            </div>
            <div class="latest">
              <span class="status"><span class="dot ${escapeHtml(t.status || 'unknown')}"></span>${escapeHtml(statusLabel(t.status))}</span>
              <span class="age">${escapeHtml((t.latency_ms ?? 0) + 'ms · ' + ago(t.last_check_at))}</span>
            </div>
            <span class="chevron" aria-hidden="true">${isOpen ? '-' : '+'}</span>
          </button>
          <div id="details-${escapeHtml(id)}" class="target-details" ${isOpen ? '' : 'hidden'}>${renderDetails(t, samples, stats)}</div>`;
        const toggle = row.querySelector('.target-summary');
        const details = row.querySelector('.target-details');
        const chevron = row.querySelector('.chevron');
        toggle.addEventListener('click', () => {
          const expanded = toggle.getAttribute('aria-expanded') === 'true';
          toggle.setAttribute('aria-expanded', expanded ? 'false' : 'true');
          details.hidden = expanded;
          row.classList.toggle('open', !expanded);
          chevron.textContent = expanded ? '+' : '-';
          if (expanded) openTargets.delete(t.slug);
          else openTargets.add(t.slug);
        });
        list.appendChild(row);
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
      overall.className = 'overall down';
      overall.innerHTML = `
        <span class="badge-dot down">!</span>
        <div>
          <p class="overall-title">/api/status 无响应</p>
          <div class="overall-sub">${escapeHtml(String(e.message || e))}</div>
        </div>
        <div class="updated">--</div>`;
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
