import { persistAuthSession } from '@/shared/auth/session';
import { workflowScheduleApi } from './workflowScheduleApi';

describe('workflowScheduleApi', () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: { sub: 'user-1' },
    });
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  function createSummary(overrides: Record<string, unknown> = {}) {
    return {
      scheduleId: 'schedule-alpha',
      displayName: 'Daily workflow run',
      targetKind: 'ServiceInvocation',
      targetActorId: 'actor-alpha',
      payloadTypeUrl: 'type.googleapis.com/aevatar.ChatRequestEvent',
      serviceKey: 'scope-alpha:default:default:service-alpha',
      serviceId: 'service-alpha',
      serviceEndpointId: 'chat',
      prompt: 'Run the workflow',
      cronExpression: '17 9 * * *',
      timezone: 'Asia/Shanghai',
      enabled: true,
      createdAt: '2026-08-20T08:00:00Z',
      updatedAt: '2026-08-20T08:00:00Z',
      nextFireAt: '2026-08-21T01:17:00Z',
      lastFireAt: null,
      lastTargetActorId: '',
      lastCommandId: '',
      lastCorrelationId: '',
      lastError: '',
      fireCount: 0,
      failureCount: 0,
      headers: {},
      scheduleActorId: 'schedule-actor-alpha',
      scheduleKind: 'Workflow',
      deleted: false,
      ...overrides,
    };
  }

  function createReceipt() {
    return {
      scheduleId: 'schedule-alpha',
      scheduleActorId: 'schedule-actor-alpha',
      accepted: true,
      commandId: 'command-alpha',
      correlationId: 'correlation-alpha',
      ackedAt: '2026-08-20T08:00:00Z',
      ackStage: 'accepted',
    };
  }

  function createRunNowReceipt() {
    return {
      ...createReceipt(),
      scheduledFireAt: '2026-08-20T08:01:00Z',
      idempotencyKey: 'schedule-alpha:manual:1',
    };
  }

  function mockJson(body: unknown) {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => body,
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;
    return fetchMock;
  }

  it('lists exact workflow schedules without service or owner query fields', async () => {
    const fetchMock = mockJson({
      items: [createSummary()],
      nextCursor: null,
      totalCount: 1,
    });

    await expect(
      workflowScheduleApi.list('scope/alpha', 'wf+alpha', {
        includeTotalCount: true,
        take: 25,
      }),
    ).resolves.toEqual({
      items: [expect.objectContaining({ scheduleId: 'schedule-alpha' })],
      nextCursor: null,
      totalCount: 1,
    });

    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      '/api/scopes/scope%2Falpha/workflows/wf%2Balpha/schedules?includeTotalCount=true&take=25',
    );
  });

  it('uses workflow-scoped preview and create payloads', async () => {
    const fetchMock = mockJson({
      cronExpression: '17 9 * * *',
      timezone: 'Asia/Shanghai',
      nextFireTimes: ['2026-08-21T01:17:00Z'],
    });

    await workflowScheduleApi.preview('scope-alpha', 'wf-alpha', {
      cronExpression: '17 9 * * *',
      timezone: 'Asia/Shanghai',
      count: 1,
    });
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      '/api/scopes/scope-alpha/workflows/wf-alpha/schedules/preview',
    );
    expect(fetchMock.mock.calls[0]?.[1]).toEqual(
      expect.objectContaining({ method: 'POST' }),
    );
    expect(JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body))).toEqual({
      cronExpression: '17 9 * * *',
      timezone: 'Asia/Shanghai',
      count: 1,
    });

    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 202,
      json: async () => createReceipt(),
    } as Response);
    await workflowScheduleApi.create('scope-alpha', 'wf-alpha', {
      displayName: 'Daily workflow run',
      cronExpression: '17 9 * * *',
      timezone: 'Asia/Shanghai',
      enabled: true,
      prompt: 'Run the workflow',
    });
    expect(fetchMock.mock.calls[1]?.[0]).toBe(
      '/api/scopes/scope-alpha/workflows/wf-alpha/schedules',
    );
    expect(JSON.parse(String(fetchMock.mock.calls[1]?.[1]?.body))).toEqual({
      displayName: 'Daily workflow run',
      cronExpression: '17 9 * * *',
      timezone: 'Asia/Shanghai',
      enabled: true,
      prompt: 'Run the workflow',
      headers: {},
    });
  });

  it('maps the scheduled dispatch target actor to the authoritative Run destination', async () => {
    mockJson({
      schedule: createSummary(),
      recentFires: [
        {
          scheduledFireAt: '2026-08-20T08:00:00Z',
          completedAt: '2026-08-20T08:01:00Z',
          idempotencyKey: 'schedule-alpha:fire:1',
          targetActorId: 'run-alpha',
          error: '',
          manual: false,
        },
      ],
    });

    await expect(
      workflowScheduleApi.get('scope-alpha', 'wf-alpha', 'schedule-alpha'),
    ).resolves.toEqual({
      schedule: expect.objectContaining({ scheduleId: 'schedule-alpha' }),
      recentFires: [expect.objectContaining({ runActorId: 'run-alpha' })],
    });
  });

  it('keeps detail and mutation routes under the exact workflow identity', async () => {
    const fetchMock = mockJson({
      schedule: createSummary(),
      recentFires: [],
    });
    fetchMock.mockReset();
    fetchMock
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          schedule: createSummary(),
          recentFires: [
            {
              ScheduledFireAt: '2026-08-20T08:00:00Z',
              CompletedAt: '2026-08-20T08:01:00Z',
              IdempotencyKey: 'schedule-alpha:fire:1',
              RunActorId: 'run-alpha',
              Error: '',
              Manual: false,
            },
            {
              ScheduledFireAt: '2026-08-20T09:00:00Z',
              CompletedAt: '2026-08-20T09:01:00Z',
              IdempotencyKey: 'schedule-alpha:fire:2',
              RunActorId: null,
              Error: 'Run identity was not recorded',
              Manual: false,
            },
            {
              ScheduledFireAt: '2026-08-20T10:00:00Z',
              CompletedAt: '2026-08-20T10:01:00Z',
              IdempotencyKey: 'schedule-alpha:fire:3',
              Error: '',
              Manual: false,
            },
          ],
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => createReceipt(),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => createReceipt(),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => createReceipt(),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => createRunNowReceipt(),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => createReceipt(),
      } as Response);

    const detail = await workflowScheduleApi.get(
      'scope-alpha',
      'wf-alpha',
      'schedule-alpha',
    );
    await workflowScheduleApi.update(
      'scope-alpha',
      'wf-alpha',
      'schedule-alpha',
      {
        displayName: 'Updated workflow run',
        cronExpression: '0 10 * * 1',
        timezone: 'Asia/Shanghai',
        enabled: true,
      },
    );
    await workflowScheduleApi.enable(
      'scope-alpha',
      'wf-alpha',
      'schedule-alpha',
      'resume',
    );
    await workflowScheduleApi.disable(
      'scope-alpha',
      'wf-alpha',
      'schedule-alpha',
    );
    await workflowScheduleApi.runNow(
      'scope-alpha',
      'wf-alpha',
      'schedule-alpha',
    );
    await workflowScheduleApi.delete(
      'scope-alpha',
      'wf-alpha',
      'schedule-alpha',
      'remove schedule',
    );

    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      '/api/scopes/scope-alpha/workflows/wf-alpha/schedules/schedule-alpha',
      '/api/scopes/scope-alpha/workflows/wf-alpha/schedules/schedule-alpha',
      '/api/scopes/scope-alpha/workflows/wf-alpha/schedules/schedule-alpha:enable',
      '/api/scopes/scope-alpha/workflows/wf-alpha/schedules/schedule-alpha:disable',
      '/api/scopes/scope-alpha/workflows/wf-alpha/schedules/schedule-alpha:run-now',
      '/api/scopes/scope-alpha/workflows/wf-alpha/schedules/schedule-alpha?reason=remove+schedule',
    ]);
    expect(
      fetchMock.mock.calls.slice(1).every(([, init]) => init?.method),
    ).toBe(true);
    expect(detail.recentFires).toEqual([
      expect.objectContaining({ runActorId: 'run-alpha' }),
      expect.objectContaining({ runActorId: '' }),
      expect.objectContaining({ runActorId: '' }),
    ]);
  });
});
