import {
  type WorkflowActivityApiError,
  workflowActivityApi,
} from './workflowActivityApi';

describe('workflowActivityApi', () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  function jsonResponse(payload: unknown, status = 200): Partial<Response> {
    return {
      json: async () => payload,
      ok: status >= 200 && status < 300,
      status,
      statusText: status === 403 ? 'Forbidden' : 'OK',
      text: async () => JSON.stringify(payload),
    };
  }

  it('encodes supported server filters and preserves an unknown status', async () => {
    const fetchMock = jest.fn().mockResolvedValue(
      jsonResponse([
        {
          runId: 'run-alpha',
          workflowName: 'Support triage',
          status: 'future_terminal_state',
          success: null,
          startedAtUtc: null,
          updatedAtUtc: '2026-08-04T10:00:00Z',
          stateVersion: 17,
          scopeId: 'scope-alpha',
          runOrigin: 'draft',
        },
      ]),
    );
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      workflowActivityApi.listRuns('scope-alpha', {
        status: 'failed',
        origins: ['draft', 'member-invoke'],
        definitionActorIds: ['definition-alpha'],
        scheduleIds: ['schedule-alpha'],
        fromUtc: '2026-08-01T00:00:00Z',
        toUtc: '2026-08-05T00:00:00Z',
        take: 25,
      }),
    ).resolves.toEqual([
      expect.objectContaining({
        runId: 'run-alpha',
        stateVersion: 17,
        status: 'future_terminal_state',
      }),
    ]);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/workflow/observatory/runs?scope=scope-alpha&status=failed&origin=draft%2Cmember-invoke&definition=definition-alpha&schedule=schedule-alpha&from=2026-08-01T00%3A00%3A00Z&to=2026-08-05T00%3A00%3A00Z&take=25',
    );
  });

  it('loads immutable detail and graph through independent resources', async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(
        jsonResponse({
          summary: {
            runId: 'run-alpha',
            workflowName: 'Support triage',
            status: 'failed',
            success: false,
            startedAtUtc: '2026-08-04T09:58:00Z',
            updatedAtUtc: '2026-08-04T10:00:00Z',
            stateVersion: 18,
            scopeId: 'scope-alpha',
            runOrigin: 'draft',
          },
          input: 'ticket-42',
          finalOutput: '',
          finalError: 'Connector unavailable',
          diagnostics: [],
          steps: [
            {
              stepId: 'step-failed',
              stepType: 'connector_call',
              targetRole: '',
              requestedAtUtc: '2026-08-04T09:59:00Z',
              completedAtUtc: '2026-08-04T10:00:00Z',
              success: false,
              durationMs: 1000,
              outputPreview: '',
              error: 'Connector unavailable',
              requestParameters: {},
              nextStepId: '',
              branchKey: '',
              suspensionType: '',
              suspensionPrompt: '',
              suspensionContent: '',
              suspensionTimeoutSeconds: null,
              toolApproval: null,
              usage: {
                promptTokens: 1,
                completionTokens: 0,
                totalTokens: 1,
                cost: 0,
              },
            },
          ],
          timeline: [],
          statistics: {
            totalSteps: 1,
            requestedSteps: 1,
            completedSteps: 1,
            roleReplyCount: 0,
            stepTypeCounts: { connector_call: 1 },
          },
          usageTotals: {
            promptTokens: 1,
            completionTokens: 0,
            totalTokens: 1,
            cost: 0,
          },
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          rootNodeId: 'node-root',
          nodes: [
            {
              nodeId: 'node-step',
              nodeType: 'WorkflowStep',
              stepId: 'step-failed',
            },
          ],
          edges: [],
        }),
      );
    global.fetch = fetchMock as typeof global.fetch;

    const detail = await workflowActivityApi.getRun('scope-alpha', 'run-alpha');
    const graph = await workflowActivityApi.getRunGraph(
      'scope-alpha',
      'run-alpha',
    );

    expect(detail.steps[0]?.stepId).toBe('step-failed');
    expect(graph.nodes[0]?.stepId).toBe('step-failed');
    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      '/api/workflow/observatory/runs/run-alpha?scope=scope-alpha',
      '/api/workflow/observatory/runs/run-alpha/graph?scope=scope-alpha',
    ]);
  });

  it('submits a fork without treating its actor identity as a run identity', async () => {
    const fetchMock = jest.fn().mockResolvedValue(
      jsonResponse(
        {
          accepted: true,
          sourceRunId: 'run-alpha',
          newRunActorId: 'actor-new',
          workflowName: 'Support triage',
          acceptedCommandId: 'command-fork',
          correlationId: 'correlation-fork',
          statusUrl: '/api/workflow-actors/actor-new/current-state',
        },
        202,
      ),
    );
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      workflowActivityApi.forkRun({
        sourceRunId: 'run-alpha',
        startAtStepId: 'step-failed',
        input: 'ticket-42',
      }),
    ).resolves.toEqual(
      expect.objectContaining({
        newRunActorId: 'actor-new',
        sourceRunId: 'run-alpha',
      }),
    );

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/workflow/runs/fork',
      expect.objectContaining({
        body: JSON.stringify({
          sourceRunId: 'run-alpha',
          startAtStepId: 'step-failed',
          input: 'ticket-42',
        }),
        method: 'POST',
      }),
    );
  });

  it('preserves authorization status and rejects malformed authoritative identity', async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(
        jsonResponse(
          { code: 'SCOPE_ACCESS_DENIED', message: 'Forbidden' },
          403,
        ),
      )
      .mockResolvedValueOnce(
        jsonResponse([
          {
            runId: '',
            workflowName: 'Support triage',
            status: 'running',
            success: null,
            startedAtUtc: null,
            updatedAtUtc: '2026-08-04T10:00:00Z',
            stateVersion: 1,
            scopeId: 'scope-alpha',
            runOrigin: 'draft',
          },
        ]),
      );
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      workflowActivityApi.listRuns('scope-other'),
    ).rejects.toMatchObject<Partial<WorkflowActivityApiError>>({
      code: 'SCOPE_ACCESS_DENIED',
      status: 403,
    });
    await expect(workflowActivityApi.listRuns('scope-alpha')).rejects.toThrow(
      'runId must not be blank',
    );
  });

  it('preserves typed failure guidance for actionable run toasts', async () => {
    global.fetch = jest.fn().mockResolvedValue(
      jsonResponse(
        {
          code: 'RATE_LIMITED',
          correlationId: 'corr-alpha',
          message: 'The request quota has been reached.',
          retryAfterSeconds: 17,
        },
        429,
      ),
    ) as typeof global.fetch;

    await expect(
      workflowActivityApi.getRun('scope-alpha', 'run-alpha'),
    ).rejects.toMatchObject({
      code: 'RATE_LIMITED',
      correlationId: 'corr-alpha',
      message: 'The request quota has been reached.',
      retryAfterSeconds: 17,
      status: 429,
    });
  });
});
