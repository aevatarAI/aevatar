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
  });
  expect(posthog.capture).toHaveBeenNthCalledWith(2, 'workflow_finished', {
    workflowId: 'wf-alpha',
    status: 'success',
    duration: 1250,
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
