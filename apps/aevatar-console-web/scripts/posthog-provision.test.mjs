import assert from 'node:assert/strict';
import test from 'node:test';
import { applyPlan, createClient, createPlan } from './posthog-provision.mjs';

test('reapplying updates managed resources without duplicating or removing another dashboard membership', async () => {
  const resources = { dashboards: [], insights: [], alerts: [] };
  const writes = [];
  let nextId = 1;
  const client = createClient(
    'https://us.posthog.com',
    '42',
    'test-only-key',
    async (url, options) => {
      const [, , , , resource, id] = url.pathname.split('/');
      if (options.method === 'GET')
        return Response.json({ results: resources[resource], next: null });
      writes.push({ method: options.method, resource });
      const body = JSON.parse(options.body);
      let saved;
      if (options.method === 'POST') {
        saved = { ...body, id: nextId++ };
        resources[resource].push(saved);
      } else {
        saved = resources[resource].find((item) => String(item.id) === id);
        assert.ok(
          saved,
          'PATCH must reference a resource previously read from this project',
        );
        Object.assign(saved, body);
      }
      if (body.dashboards)
        saved.dashboard_tiles = body.dashboards.map((dashboardId) => ({
          dashboard_id: dashboardId,
        }));
      return Response.json(saved);
    },
  );
  const plan = createPlan();
  await applyPlan(plan, client, [7], () => {});
  assert.deepEqual(
    Object.values(resources).map((items) => items.length),
    [1, 6, 2],
  );
  resources.insights[0].dashboard_tiles.push({ dashboard_id: 99 });
  const firstWriteCount = writes.length;
  await applyPlan(plan, client, [7], () => {});
  assert.ok(
    writes.slice(firstWriteCount).every((write) => write.method === 'PATCH'),
  );
  assert.ok(resources.insights[0].dashboards.includes(99));
  assert.ok(
    resources.alerts.every((alert) =>
      resources.insights.some((insight) => insight.id === alert.insight),
    ),
  );
});

test('duplicate ownership markers abort before any API mutation', async () => {
  const plan = createPlan();
  const client = {
    list: async (resource) =>
      resource === 'dashboards'
        ? [
            { ...plan.dashboard, id: 1 },
            { ...plan.dashboard, id: 2 },
          ]
        : [],
    call: async () =>
      assert.fail('duplicate discovery must not mutate resources'),
  };
  await assert.rejects(
    applyPlan(plan, client, [7]),
    /Multiple managed resources/,
  );
});

test('pagination cannot forward the personal API credential to another origin or project', async () => {
  for (const next of [
    'https://untrusted.example/api/projects/42/alerts/',
    'https://us.posthog.com/api/projects/84/alerts/',
  ]) {
    let requests = 0;
    const client = createClient(
      'https://us.posthog.com',
      '42',
      'test-only-key',
      async () => {
        requests += 1;
        return Response.json({ results: [], next });
      },
    );
    await assert.rejects(
      client.list('alerts'),
      /outside the configured project/,
    );
    assert.equal(requests, 1);
  }
});
