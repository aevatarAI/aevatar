import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import posthog from 'posthog-js';
import * as React from 'react';
import { initializeConsoleAnalytics } from '@/shared/analytics/posthog';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import { useWorkflowEditor } from './useWorkflowEditor';

jest.mock('posthog-js', () => ({
  __esModule: true,
  default: { init: jest.fn(), register: jest.fn(), capture: jest.fn() },
}));
jest.mock('@/shared/studio/api', () => ({
  studioApi: { getWorkflow: jest.fn(), getWorkspaceSettings: jest.fn() },
}));
jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: { streamChat: jest.fn() },
}));
jest.mock('@/shared/api/workflowActivityApi', () => ({
  workflowActivityApi: { getRun: jest.fn() },
}));

const { studioApi } = jest.requireMock('@/shared/studio/api');
const { runtimeRunsApi } = jest.requireMock('@/shared/api/runtimeRunsApi');
const { workflowActivityApi } = jest.requireMock(
  '@/shared/api/workflowActivityApi',
);
const startedAt = Date.parse('2026-10-10T10:00:00Z');
const originalPosthogKey = process.env.AEVATAR_POSTHOG_KEY;
const originalPosthogHost = process.env.AEVATAR_POSTHOG_HOST;

function RunHarness() {
  const editor = useWorkflowEditor('scope-alpha', 'wf-alpha');
  return (
    <>
      <button
        type="button"
        disabled={!editor.document || editor.runRequestActive}
        onClick={() =>
          void editor.run({
            nodeCount: 2,
            workflowId: 'wf-alpha',
            publishedServiceId: 'svc-alpha',
            revisionId: 'revision-alpha',
          })
        }
      >
        Run published workflow
      </button>
      <output aria-label="Run phase">{editor.runPhase}</output>
    </>
  );
}

function createStream() {
  let controller: ReadableStreamDefaultController<Uint8Array>;
  const response = {
    ok: true,
    body: new ReadableStream<Uint8Array>({
      start(value) {
        controller = value;
      },
    }),
  } as Response;
  return {
    response,
    frame: (frame: unknown) =>
      controller.enqueue(
        new TextEncoder().encode(`data: ${JSON.stringify(frame)}\n\n`),
      ),
    close: () => controller.close(),
    fail: () => controller.error(new Error('Network failed: sensitive input')),
  };
}

async function startRun() {
  const view = renderWithQueryClient(<RunHarness />);
  const button = screen.getByRole('button', { name: 'Run published workflow' });
  await waitFor(() => expect(button).toBeEnabled());
  fireEvent.click(button);
  await waitFor(() =>
    expect(screen.getByLabelText('Run phase')).toHaveTextContent('accepted'),
  );
  return view;
}

beforeAll(() => {
  process.env.AEVATAR_POSTHOG_KEY = 'phc_test';
  process.env.AEVATAR_POSTHOG_HOST = 'https://us.i.posthog.com';
  initializeConsoleAnalytics();
});

afterAll(() => {
  if (originalPosthogKey === undefined) delete process.env.AEVATAR_POSTHOG_KEY;
  else process.env.AEVATAR_POSTHOG_KEY = originalPosthogKey;
  if (originalPosthogHost === undefined)
    delete process.env.AEVATAR_POSTHOG_HOST;
  else process.env.AEVATAR_POSTHOG_HOST = originalPosthogHost;
});

beforeEach(() => {
  jest.clearAllMocks();
  studioApi.getWorkspaceSettings.mockResolvedValue({ directories: [] });
  studioApi.getWorkflow.mockResolvedValue({
    workflowId: 'wf-alpha',
    name: 'Workflow',
    yaml: 'name: workflow',
    updatedAtUtc: '2026-10-10T09:00:00Z',
    findings: [],
    draftExists: true,
    document: {
      name: 'workflow',
      roles: [],
      steps: [
        { id: 'step-alpha', type: 'assign' },
        { id: 'step-beta', type: 'assign' },
        { id: 'step-unpublished', type: 'assign' },
      ],
    },
  });
  workflowActivityApi.getRun.mockReturnValue(new Promise(() => {}));
});

it('counts observed starts with the published node snapshot and one successful terminal event', async () => {
  const stream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(stream.response);
  await startRun();
  expect(posthog.capture).not.toHaveBeenCalled();

  await act(async () => {
    stream.frame({ timestamp: startedAt, runStarted: { runId: 'run-alpha' } });
    stream.frame({ timestamp: startedAt, runStarted: { runId: 'run-alpha' } });
    stream.frame({ timestamp: startedAt + 1250, runFinished: {} });
    stream.frame({ timestamp: startedAt + 1250, runFinished: {} });
    stream.close();
  });
  expect(posthog.capture).toHaveBeenNthCalledWith(1, 'workflow_started', {
    workflowId: 'wf-alpha',
    nodeCount: 2,
    scopeId: 'scope-alpha',
    invocationId: expect.any(String),
    start_source: 'run_started',
  });
  expect(posthog.capture).toHaveBeenNthCalledWith(2, 'workflow_finished', {
    workflowId: 'wf-alpha',
    status: 'success',
    duration: 1250,
    scopeId: 'scope-alpha',
    invocationId: expect.any(String),
    start_source: 'run_started',
  });
  expect(posthog.capture).toHaveBeenCalledTimes(2);
});

it('preserves explicit failed completion status and does not count duplicate errors as disconnects', async () => {
  const stream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(stream.response);
  await startRun();
  await act(async () => {
    stream.frame({ timestamp: startedAt, runStarted: { runId: 'run-alpha' } });
    stream.frame({
      timestamp: startedAt + 900,
      runFinished: { status: 'RUN_COMPLETION_STATUS_FAILED' },
    });
    stream.frame({
      timestamp: startedAt + 900,
      runError: { code: 'WORKFLOW_FAILED', message: 'sensitive input' },
    });
    stream.close();
  });
  expect(posthog.capture).toHaveBeenLastCalledWith('workflow_finished', {
    workflowId: 'wf-alpha',
    status: 'failed',
    duration: 900,
    scopeId: 'scope-alpha',
    invocationId: expect.any(String),
    start_source: 'run_started',
  });
  expect(posthog.capture).toHaveBeenCalledTimes(2);
});

it('reports premature EOF without inventing failure and reconciles the exact run from Activity', async () => {
  let resolveRun: (value: unknown) => void = () => {};
  workflowActivityApi.getRun.mockReturnValue(
    new Promise((resolve) => {
      resolveRun = resolve;
    }),
  );
  const stream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(stream.response);
  await startRun();
  await act(async () => {
    stream.frame({ timestamp: startedAt, runStarted: { runId: 'run-alpha' } });
    stream.close();
  });
  await waitFor(() =>
    expect(workflowActivityApi.getRun).toHaveBeenCalledWith(
      'scope-alpha',
      'run-alpha',
    ),
  );
  expect(posthog.capture).toHaveBeenLastCalledWith('sse_disconnect', {
    workflowId: 'wf-alpha',
    reason: 'unexpected_eof',
  });
  expect(posthog.capture).toHaveBeenCalledTimes(2);

  await act(async () =>
    resolveRun({
      summary: {
        runId: 'run-alpha',
        scopeId: 'scope-alpha',
        workflowName: 'Workflow',
        stateVersion: 8,
        status: 'completed',
        success: true,
        runOrigin: 'service',
        startedAtUtc: '2026-10-10T10:00:00Z',
        updatedAtUtc: '2026-10-10T10:00:02Z',
      },
    }),
  );
  await waitFor(() =>
    expect(posthog.capture).toHaveBeenLastCalledWith('workflow_finished', {
      workflowId: 'wf-alpha',
      status: 'success',
      duration: 2000,
      scopeId: 'scope-alpha',
      invocationId: expect.any(String),
      start_source: 'run_started',
    }),
  );
  expect(posthog.capture).toHaveBeenCalledTimes(3);
});

it('records a sanitized stream failure but ignores navigation aborts and never retries the run POST', async () => {
  const stream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(stream.response);
  const view = await startRun();
  await act(async () => {
    stream.frame({ timestamp: startedAt, runStarted: { runId: 'run-alpha' } });
  });
  await act(async () => stream.fail());
  expect(posthog.capture).toHaveBeenLastCalledWith('sse_disconnect', {
    workflowId: 'wf-alpha',
    reason: 'stream_error',
  });
  expect(posthog.capture).toHaveBeenCalledTimes(2);
  expect(runtimeRunsApi.streamChat).toHaveBeenCalledTimes(1);
  view.unmount();

  const nextStream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(nextStream.response);
  const nextView = await startRun();
  nextView.unmount();
  await act(async () => nextStream.close());
  expect(posthog.capture).toHaveBeenCalledTimes(2);
  expect(runtimeRunsApi.streamChat).toHaveBeenCalledTimes(2);
});

it('counts executed steps when a published stream omits RUN_STARTED and deduplicates completion', async () => {
  const stream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(stream.response);
  await startRun();
  await act(async () => {
    stream.frame({
      timestamp: startedAt,
      custom: {
        name: 'aevatar.run.context',
        payload: { actorId: 'actor-alpha', commandId: 'cmd-alpha' },
      },
    });
  });
  expect(posthog.capture).not.toHaveBeenCalled();
  await act(async () => {
    stream.frame({
      timestamp: startedAt + 200,
      stepStarted: { stepName: 'first' },
    });
    stream.frame({
      timestamp: startedAt + 400,
      stepStarted: { stepName: 'second' },
    });
    stream.frame({ timestamp: startedAt + 1400, runFinished: {} });
    stream.frame({ timestamp: startedAt + 1400, runFinished: {} });
    stream.close();
  });
  expect(posthog.capture).toHaveBeenNthCalledWith(1, 'workflow_started', {
    workflowId: 'wf-alpha',
    nodeCount: 2,
    scopeId: 'scope-alpha',
    invocationId: expect.any(String),
    start_source: 'first_step',
  });
  expect(posthog.capture).toHaveBeenNthCalledWith(2, 'workflow_finished', {
    workflowId: 'wf-alpha',
    status: 'success',
    duration: 1200,
    scopeId: 'scope-alpha',
    invocationId: expect.any(String),
    start_source: 'first_step',
  });
  expect(posthog.capture).toHaveBeenCalledTimes(2);
  expect(workflowActivityApi.getRun).not.toHaveBeenCalled();
});

it('reports a real failure after the first step without inventing a start from acceptance alone', async () => {
  const rejected = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(rejected.response);
  const view = await startRun();
  await act(async () => {
    rejected.frame({
      timestamp: startedAt,
      custom: {
        name: 'aevatar.run.context',
        payload: { actorId: 'actor-alpha' },
      },
    });
    rejected.frame({
      timestamp: startedAt + 100,
      runError: {
        code: 'ADMISSION_REJECTED',
        message: 'Rejected before execution',
      },
    });
    rejected.close();
  });
  expect(posthog.capture).not.toHaveBeenCalled();
  view.unmount();

  const executing = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(executing.response);
  await startRun();
  await act(async () => {
    executing.frame({
      timestamp: startedAt + 200,
      stepStarted: { stepName: 'first' },
    });
    executing.frame({
      timestamp: startedAt + 900,
      runError: { code: 'WORKFLOW_FAILED', message: 'Execution failed' },
    });
    executing.frame({
      timestamp: startedAt + 900,
      runError: { code: 'WORKFLOW_FAILED' },
    });
    executing.close();
  });
  expect(posthog.capture).toHaveBeenLastCalledWith('workflow_finished', {
    workflowId: 'wf-alpha',
    status: 'failed',
    duration: 700,
    scopeId: 'scope-alpha',
    invocationId: expect.any(String),
    start_source: 'first_step',
  });
  expect(posthog.capture).toHaveBeenCalledTimes(2);
});

it('upgrades a first-step baseline from a delayed authoritative start without counting twice', async () => {
  const stream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(stream.response);
  await startRun();
  await act(async () => {
    stream.frame({
      timestamp: startedAt + 200,
      stepStarted: { stepName: 'first' },
    });
    stream.frame({ timestamp: startedAt, runStarted: { runId: 'run-alpha' } });
    stream.frame({
      timestamp: startedAt + 1400,
      runFinished: { runId: 'run-alpha' },
    });
    stream.close();
  });
  expect(posthog.capture).toHaveBeenLastCalledWith('workflow_finished', {
    workflowId: 'wf-alpha',
    status: 'success',
    duration: 1400,
    scopeId: 'scope-alpha',
    invocationId: expect.any(String),
    start_source: 'run_started',
  });
  expect(posthog.capture).toHaveBeenCalledTimes(2);
});

it('preserves the first-step baseline when a late RUN_STARTED lacks its server timestamp', async () => {
  const stream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(stream.response);
  await startRun();
  jest.spyOn(Date, 'now').mockReturnValue(startedAt + 1000);
  await act(async () => {
    stream.frame({
      timestamp: startedAt + 200,
      stepStarted: { stepName: 'first' },
    });
    stream.frame({ runStarted: { runId: 'run-alpha' } });
    stream.frame({
      timestamp: startedAt + 1400,
      runFinished: { runId: 'run-alpha' },
    });
    stream.close();
  });
  expect(posthog.capture).toHaveBeenLastCalledWith('workflow_finished', {
    workflowId: 'wf-alpha',
    status: 'success',
    duration: 1200,
    scopeId: 'scope-alpha',
    invocationId: expect.any(String),
    start_source: 'first_step',
  });
  expect(posthog.capture).toHaveBeenCalledTimes(2);
});

it.each([
  'available',
  'throwing',
] as const)('correlates each invocation separately without blocking runs when crypto is %s', async (cryptoAvailability) => {
  if (cryptoAvailability === 'throwing') {
    jest.spyOn(globalThis.crypto, 'randomUUID').mockImplementation(() => {
      throw new Error('Crypto unavailable');
    });
    // Repeated runs remain distinct even within one clock/random sample.
    jest.spyOn(Date, 'now').mockReturnValue(startedAt);
    jest.spyOn(Math, 'random').mockReturnValue(0.5);
  }

  const firstStream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(firstStream.response);
  await startRun();
  await act(async () => {
    firstStream.frame({
      timestamp: startedAt,
      stepStarted: { stepName: 'first' },
    });
    firstStream.frame({ timestamp: startedAt + 100, runFinished: {} });
    firstStream.close();
  });

  const secondStream = createStream();
  runtimeRunsApi.streamChat.mockResolvedValue(secondStream.response);
  const runButton = screen.getByRole('button', {
    name: 'Run published workflow',
  });
  await waitFor(() => expect(runButton).toBeEnabled());
  fireEvent.click(runButton);
  await waitFor(() =>
    expect(screen.getByLabelText('Run phase')).toHaveTextContent('accepted'),
  );
  await act(async () => {
    secondStream.frame({
      timestamp: startedAt,
      stepStarted: { stepName: 'first' },
    });
    secondStream.frame({ timestamp: startedAt + 200, runFinished: {} });
    secondStream.close();
  });

  expect(runtimeRunsApi.streamChat).toHaveBeenCalledTimes(2);
  expect(screen.getByLabelText('Run phase')).toHaveTextContent('stream_ended');
  expect(posthog.capture).toHaveBeenCalledTimes(4);
  const captures = jest.mocked(posthog.capture).mock.calls;
  const firstInvocationId = captures[0][1]?.invocationId;
  const secondInvocationId = captures[2][1]?.invocationId;
  expect(firstInvocationId).toEqual(expect.any(String));
  expect(firstInvocationId).not.toBe('');
  expect(secondInvocationId).toEqual(expect.any(String));
  expect(secondInvocationId).not.toBe(firstInvocationId);
  expect(captures[1]).toEqual([
    'workflow_finished',
    expect.objectContaining({
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      invocationId: firstInvocationId,
      status: 'success',
    }),
  ]);
  expect(captures[3]).toEqual([
    'workflow_finished',
    expect.objectContaining({
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      invocationId: secondInvocationId,
      status: 'success',
    }),
  ]);
});
