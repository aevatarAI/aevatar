import { pathToFileURL } from 'node:url';

const managedTag = 'aevatar-console:monitoring';

function sqlLiteral(value) {
  return `'${value.replaceAll('\\', '\\\\').replaceAll("'", "\\'")}'`;
}

export function createPlan(environment = 'production') {
  if (!/^[a-zA-Z0-9_-]{1,20}$/.test(environment)) {
    throw new Error(
      'POSTHOG_ENVIRONMENT must contain 1–20 letters, digits, _ or -.',
    );
  }
  const filter = `properties.app = 'aevatar-console' AND properties.environment = ${sqlLiteral(environment)}`;
  const counts = (window) => `SELECT
  countIf(event = 'workflow_started') AS started,
  countIf(event = 'workflow_finished' AND properties.status = 'success') AS succeeded,
  countIf(event = 'sse_disconnect') AS disconnected
FROM events
WHERE ${filter}
  AND ${window}
  AND event IN ('workflow_started', 'workflow_finished', 'sse_disconnect')`;
  const dailyCounts = counts(
    'timestamp >= now() - INTERVAL 24 HOUR AND timestamp < now()',
  );
  const hourlyCounts = counts(
    'timestamp >= toStartOfHour(now()) - INTERVAL 1 HOUR AND timestamp < toStartOfHour(now())',
  );
  const insight = (key, name, query, dashboard = true) => ({
    key,
    dashboard,
    body: {
      name: `Aevatar Console (${environment}) — ${name}`,
      description: `${managedTag}/${environment}/${key}. Managed by scripts/posthog-provision.mjs. Duration is milliseconds; ratios count events in the stated window.`,
      tags: [managedTag, `aevatar-env:${environment}`, `aevatar-metric:${key}`],
      query: {
        kind: 'DataVisualizationNode',
        source: { kind: 'HogQLQuery', query },
        display: dashboard ? 'BoldNumber' : 'ActionsTable',
      },
    },
  });
  return {
    dashboard: {
      name: `Aevatar Console (${environment}) — Workflow health`,
      description: `${managedTag}/${environment}. Rolling 24-hour metrics. Default Error Tracking and Web Analytics panels cover errors, crash-free users and Web Vitals; see docs/posthog.md.`,
      tags: [managedTag, `aevatar-env:${environment}`],
    },
    insights: [
      insight(
        'success',
        'Workflow success % · 24h',
        `SELECT round(100.0 * succeeded / nullIf(started, 0), 2) AS success_percent\nFROM (${dailyCounts})`,
      ),
      ...[
        [0.5, 'p50'],
        [0.95, 'p95'],
      ].map(([percentile, key]) =>
        insight(
          key,
          `Workflow duration ${key.toUpperCase()} (ms) · 24h`,
          `SELECT if(count() = 0, NULL, quantile(${percentile})(toFloat(properties.duration))) AS duration_${key}_ms\nFROM events\nWHERE ${filter}\n  AND timestamp >= now() - INTERVAL 24 HOUR AND timestamp < now()\n  AND event = 'workflow_finished' AND toFloat(properties.duration) >= 0`,
        ),
      ),
      insight(
        'disconnect',
        'SSE disconnect % · 24h',
        `SELECT round(100.0 * disconnected / nullIf(started, 0), 2) AS disconnect_percent\nFROM (${dailyCounts})`,
      ),
      insight(
        'success-alert',
        'Success alert evidence · previous complete hour',
        `SELECT started, succeeded,\n  100.0 * succeeded / nullIf(started, 0) AS success_percent,\n  if(started > 0 AND 100.0 * succeeded < 90.0 * started, 1, 0) AS alert_breached\nFROM (${hourlyCounts})`,
        false,
      ),
      insight(
        'disconnect-alert',
        'SSE alert evidence · previous complete hour',
        `SELECT started, disconnected,\n  100.0 * disconnected / nullIf(started, 0) AS disconnect_percent,\n  if(started > 0 AND 100.0 * disconnected > 5.0 * started, 1, 0) AS alert_breached\nFROM (${hourlyCounts})`,
        false,
      ),
    ],
    alerts: [
      {
        insightKey: 'success-alert',
        name: `[aevatar-console/${environment}] Workflow success below 90%`,
      },
      {
        insightKey: 'disconnect-alert',
        name: `[aevatar-console/${environment}] SSE disconnect above 5%`,
      },
    ].map((alert) => ({
      ...alert,
      enabled: true,
      calculation_interval: 'hourly',
      condition: { type: 'absolute_value' },
      config: {
        type: 'HogQLAlertConfig',
        evaluation: 'last_row',
        column: 'alert_breached',
      },
      threshold: { configuration: { type: 'absolute', bounds: { upper: 0 } } },
    })),
    manual:
      'Enable native Error Tracking spike notifications and review default error/crash-free/Web Vitals panels using docs/posthog.md.',
  };
}

export function createClient(host, projectId, apiKey, request = fetch) {
  const origin = new URL(host);
  if (
    origin.protocol !== 'https:' ||
    origin.username ||
    origin.password ||
    origin.search ||
    origin.hash ||
    origin.pathname !== '/'
  ) {
    throw new Error(
      'POSTHOG_MANAGEMENT_HOST must be an HTTPS origin such as https://us.posthog.com.',
    );
  }
  if (!/^\d+$/.test(projectId)) {
    throw new Error('POSTHOG_PROJECT_ID must be a numeric project ID.');
  }
  const base = new URL(`/api/projects/${projectId}/`, origin);
  async function call(path, method = 'GET', body) {
    const url = new URL(path, base);
    if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) {
      throw new Error(
        'Refusing a PostHog API URL outside the configured project.',
      );
    }
    const response = await request(url, {
      method,
      redirect: 'error',
      signal: AbortSignal.timeout(30000),
      headers: {
        Authorization: `Bearer ${apiKey}`,
        'Content-Type': 'application/json',
      },
      ...(body ? { body: JSON.stringify(body) } : {}),
    });
    if (!response.ok) {
      throw new Error(
        `PostHog ${method} ${url.pathname} failed (HTTP ${response.status}). Check project permissions and API compatibility; no response body was logged.`,
      );
    }
    try {
      return await response.json();
    } catch {
      throw new Error(
        `PostHog ${method} ${url.pathname} returned invalid JSON; no response body was logged.`,
      );
    }
  }
  async function list(resource) {
    const items = [];
    const visited = new Set();
    let next = `${resource}/?limit=100`;
    while (next) {
      if (visited.has(next))
        throw new Error('PostHog returned a repeated pagination URL.');
      visited.add(next);
      const page = await call(next);
      if (!Array.isArray(page.results))
        throw new Error(
          `PostHog ${resource} returned an unexpected list shape.`,
        );
      items.push(...page.results.filter((item) => !item.deleted));
      next = page.next;
    }
    return items;
  }
  return { call, list };
}

function oneMatch(items, matches, name) {
  const found = items.filter(matches);
  if (found.length > 1)
    throw new Error(
      `Multiple managed resources match ${name}; resolve the duplicate before applying.`,
    );
  return found[0];
}

export async function applyPlan(
  plan,
  client,
  subscriberIds,
  log = console.log,
) {
  if (
    !subscriberIds.length ||
    subscriberIds.some((id) => !Number.isSafeInteger(id) || id <= 0)
  ) {
    throw new Error(
      'POSTHOG_ALERT_USER_IDS must contain at least one numeric PostHog user ID.',
    );
  }
  // Discover every resource and reject duplicates before the first write.
  const [dashboards, insights, alerts] = await Promise.all([
    client.list('dashboards'),
    client.list('insights'),
    client.list('alerts'),
  ]);
  const existingDashboard = oneMatch(
    dashboards,
    (item) => plan.dashboard.tags.every((tag) => item.tags?.includes(tag)),
    plan.dashboard.name,
  );
  const existingInsights = new Map(
    plan.insights.map((item) => [
      item.key,
      oneMatch(
        insights,
        (candidate) =>
          item.body.tags.every((tag) => candidate.tags?.includes(tag)),
        item.body.name,
      ),
    ]),
  );
  const existingAlerts = new Map(
    plan.alerts.map((alert) => [
      alert.insightKey,
      oneMatch(
        alerts,
        (candidate) => candidate.name === alert.name,
        alert.name,
      ),
    ]),
  );
  const dashboard = await client.call(
    existingDashboard ? `dashboards/${existingDashboard.id}/` : 'dashboards/',
    existingDashboard ? 'PATCH' : 'POST',
    plan.dashboard,
  );
  log(`Dashboard: ${dashboard.id}`);
  const insightIds = new Map();
  for (const item of plan.insights) {
    const existing = existingInsights.get(item.key);
    const body = { ...item.body };
    if (item.dashboard) {
      const memberships =
        existing?.dashboard_tiles?.map((tile) => tile.dashboard_id) ??
        existing?.dashboards ??
        [];
      body.dashboards = [...new Set([...memberships, dashboard.id])];
    }
    const saved = await client.call(
      existing ? `insights/${existing.id}/` : 'insights/',
      existing ? 'PATCH' : 'POST',
      body,
    );
    insightIds.set(item.key, saved.id);
    log(`Insight ${item.key}: ${saved.id}`);
  }
  for (const alert of plan.alerts) {
    const { insightKey, ...body } = alert;
    const existing = existingAlerts.get(insightKey);
    const saved = await client.call(
      existing ? `alerts/${existing.id}/` : 'alerts/',
      existing ? 'PATCH' : 'POST',
      {
        ...body,
        insight: insightIds.get(insightKey),
        subscribed_users: subscriberIds,
      },
    );
    log(`Alert ${insightKey}: ${saved.id}`);
  }
  log(plan.manual);
}

async function main() {
  const args = process.argv.slice(2);
  if (args.some((arg) => arg !== '--apply'))
    throw new Error('Usage: node scripts/posthog-provision.mjs [--apply]');
  const plan = createPlan(process.env.POSTHOG_ENVIRONMENT || 'production');
  if (!args.includes('--apply')) {
    console.log(
      JSON.stringify(
        { mode: 'dry-run; no network requests', ...plan },
        null,
        2,
      ),
    );
    return;
  }
  for (const variable of [
    'POSTHOG_MANAGEMENT_HOST',
    'POSTHOG_PROJECT_ID',
    'POSTHOG_PERSONAL_API_KEY',
    'POSTHOG_ALERT_USER_IDS',
  ]) {
    if (!process.env[variable])
      throw new Error(`${variable} is required for --apply.`);
  }
  const client = createClient(
    process.env.POSTHOG_MANAGEMENT_HOST,
    process.env.POSTHOG_PROJECT_ID,
    process.env.POSTHOG_PERSONAL_API_KEY,
  );
  await applyPlan(
    plan,
    client,
    process.env.POSTHOG_ALERT_USER_IDS.split(',').map((id) =>
      Number(id.trim()),
    ),
  );
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  main().catch((error) => {
    console.error(
      error instanceof Error ? error.message : 'PostHog provisioning failed.',
    );
    process.exitCode = 1;
  });
}
