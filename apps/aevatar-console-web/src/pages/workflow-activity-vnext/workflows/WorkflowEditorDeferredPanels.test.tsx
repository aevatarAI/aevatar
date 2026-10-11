import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import { ConfigProvider } from 'antd';
import * as React from 'react';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopesApi } from '@/shared/api/scopesApi';
import { studioApi } from '@/shared/studio/api';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import WorkflowEditorPage from './WorkflowEditorPage';

function mockCreateChunkGate() {
  let release: () => void = () => {
    throw new Error('Chunk gate has not been initialized.');
  };
  const gate: {
    pending: Promise<void> | null;
    error: Error | null;
    finish: (error?: Error) => void;
  } = {
    pending: new Promise<void>((resolve) => {
      release = resolve;
    }),
    error: null,
    finish(error) {
      gate.pending = null;
      gate.error = error ?? null;
      release();
    },
  };
  return gate;
}

let mockLogsChunk = mockCreateChunkGate();
let mockSchedulesChunk = mockCreateChunkGate();

// Control module readiness while retaining the actual loaded panel behavior.
jest.mock('@/shared/workflows/WorkflowExecutionLogsPanel', () => {
  const actual = jest.requireActual<
    typeof import('@/shared/workflows/WorkflowExecutionLogsPanel')
  >('@/shared/workflows/WorkflowExecutionLogsPanel');
  return {
    ...actual,
    __esModule: true,
    default: (props: import('react').ComponentProps<typeof actual.default>) => {
      if (mockLogsChunk.pending) throw mockLogsChunk.pending;
      if (mockLogsChunk.error) throw mockLogsChunk.error;
      return <actual.default {...props} />;
    },
  };
});

jest.mock('./WorkflowScheduleSurface', () => {
  const actual = jest.requireActual<typeof import('./WorkflowScheduleSurface')>(
    './WorkflowScheduleSurface',
  );
  return {
    ...actual,
    __esModule: true,
    default: (props: import('react').ComponentProps<typeof actual.default>) => {
      if (mockSchedulesChunk.pending) throw mockSchedulesChunk.pending;
      if (mockSchedulesChunk.error) throw mockSchedulesChunk.error;
      return <actual.default {...props} />;
    },
  };
});

// Canvas layout is independent of the drawer and logs keyboard contracts.
jest.mock(
  '@/pages/team-member-workflow-studio/components/WorkflowStudioCanvas',
  () => ({
    __esModule: true,
    default: () => <section aria-label="Workflow canvas" />,
  }),
);

const workflowYaml =
  'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: transform\n';

beforeEach(() => {
  mockLogsChunk = mockCreateChunkGate();
  mockSchedulesChunk = mockCreateChunkGate();
  window.history.replaceState(
    null,
    '',
    '/scopes/scope-alpha/workflows/wf-alpha',
  );
  jest.spyOn(studioApi, 'getAuthSession').mockResolvedValue({
    authenticated: true,
    enabled: true,
    name: 'Operator',
    scopeId: 'scope-alpha',
  });
  jest.spyOn(studioApi, 'getWorkspaceSettings').mockResolvedValue({
    runtimeBaseUrl: '',
    directories: [],
  });
  jest.spyOn(studioApi, 'getWorkflow').mockResolvedValue({
    workflowId: 'wf-alpha',
    name: 'Workflow alpha',
    fileName: 'workflow-alpha.yaml',
    filePath: '',
    directoryId: '',
    directoryLabel: '',
    yaml: workflowYaml,
    updatedAtUtc: '2026-10-11T10:00:00Z',
    document: {
      name: 'workflow_alpha',
      roles: [],
      steps: [{ id: 'step-alpha', type: 'transform' }],
    },
    draftExists: true,
    findings: [],
  });
  jest.spyOn(scopesApi, 'getWorkflowDetail').mockResolvedValue({
    available: true,
    scopeId: 'scope-alpha',
    workflow: {
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      displayName: 'Workflow alpha',
      serviceKey: 'workflow-alpha',
      workflowName: 'workflow_alpha',
      actorId: 'actor-alpha',
      activeRevisionId: 'revision-alpha',
      deploymentId: 'deployment-alpha',
      deploymentStatus: 'Available',
      updatedAt: '2026-10-11T10:00:00Z',
      publishedServiceId: 'svc-alpha',
      serviceAppId: 'workflow-app',
      serviceNamespace: 'workflow-namespace',
    },
    source: {
      workflowYaml,
      definitionActorId: 'definition-alpha',
      inlineWorkflowYamls: null,
    },
  });
  jest
    .spyOn(runtimeRunsApi, 'streamChat')
    .mockRejectedValue(new Error('Run request unavailable'));
});

async function openRunLogs() {
  renderWithQueryClient(
    <WorkflowEditorPage scopeId="scope-alpha" workflowId="wf-alpha" />,
  );
  const run = await screen.findByRole('button', { name: 'Run' });
  await waitFor(() => expect(run).toBeEnabled());
  fireEvent.click(run);
  fireEvent.click(
    await screen.findByRole('button', { name: 'Start published run' }),
  );
  fireEvent.click(
    await screen.findByRole('button', { name: 'Collapse workflow logs' }),
  );
  const expand = screen.getByRole('button', { name: 'Expand workflow logs' });
  await waitFor(() => expect(expand).toHaveFocus());
  fireEvent.click(expand);
  await waitFor(() =>
    expect(
      screen.getByRole('button', { name: 'Collapse workflow logs' }),
    ).toHaveFocus(),
  );
}

function expectScheduleDialogFocus() {
  // A lazy boundary can replace its fallback while the module settles.
  // Query the live dialog on every retry instead of retaining a prior node.
  const dialog = screen.getByRole('dialog', {
    name: 'Schedules for Workflow alpha',
  });
  expect(dialog).toBeVisible();
  const focused = document.activeElement;
  expect(focused).not.toBe(document.body);
  expect(focused).not.toBe(document.documentElement);
  // Drawer may focus its enclosing keyboard container or a dialog control.
  if (!focused || !(dialog.contains(focused) || focused.contains(dialog))) {
    throw new Error(
      `Schedule dialog does not own focus. Focused element: ${focused?.outerHTML ?? 'none'}`,
    );
  }
  return dialog;
}

it('keeps keyboard focus through deferred logs loading, failure and retry without losing edits', async () => {
  const consoleError = jest
    .spyOn(console, 'error')
    .mockImplementation(() => {});
  try {
    await openRunLogs();
    fireEvent.change(screen.getByRole('textbox', { name: 'Workflow name' }), {
      target: { value: 'Unsaved workflow name' },
    });
    await act(async () => mockLogsChunk.finish(new Error('Chunk unavailable')));
    const retry = await screen.findByRole('button', { name: 'Retry' });
    mockLogsChunk = mockCreateChunkGate();
    retry.focus();
    fireEvent.click(retry);
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Collapse workflow logs' }),
      ).toHaveFocus(),
    );
    await act(async () => mockLogsChunk.finish());
    const logs = await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    await waitFor(() =>
      expect(
        within(logs).getByRole('button', { name: 'Collapse workflow logs' }),
      ).toHaveFocus(),
    );
    expect(screen.getByRole('textbox', { name: 'Workflow name' })).toHaveValue(
      'Unsaved workflow name',
    );
  } finally {
    consoleError.mockRestore();
  }
});

it('keeps Schedule focus and Escape dismissal available while its module loads or fails', async () => {
  const consoleError = jest
    .spyOn(console, 'error')
    .mockImplementation(() => {});
  try {
    renderWithQueryClient(
      // rc-util replaces layout effects with passive effects in tests, so a
      // newly created portal would attach after Drawer's autofocus effect.
      // Use an attached container to retain the browser's focus ordering.
      <ConfigProvider getPopupContainer={() => document.body}>
        <WorkflowEditorPage scopeId="scope-alpha" workflowId="wf-alpha" />
      </ConfigProvider>,
    );
    const schedules = await screen.findByRole('button', {
      name: 'Manage schedules for Workflow alpha',
    });
    await waitFor(() => expect(schedules).toBeEnabled());
    schedules.focus();
    fireEvent.click(schedules);
    const loadingDialog = await waitFor(expectScheduleDialogFocus);
    fireEvent.keyDown(document.activeElement ?? loadingDialog, {
      key: 'Escape',
      keyCode: 27,
    });
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    await waitFor(() => expect(schedules).toHaveFocus());

    fireEvent.click(schedules);
    await act(async () =>
      mockSchedulesChunk.finish(new Error('Chunk unavailable')),
    );
    const failedDialog = await screen.findByRole('dialog', {
      name: 'Schedules for Workflow alpha',
    });
    expect(
      await within(failedDialog).findByRole('button', { name: 'Retry' }),
    ).toBeVisible();
    await waitFor(expectScheduleDialogFocus);
    fireEvent.keyDown(document.activeElement ?? failedDialog, {
      key: 'Escape',
      keyCode: 27,
    });
    await waitFor(() =>
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument(),
    );
    await waitFor(() => expect(schedules).toHaveFocus());
  } finally {
    consoleError.mockRestore();
  }
});
