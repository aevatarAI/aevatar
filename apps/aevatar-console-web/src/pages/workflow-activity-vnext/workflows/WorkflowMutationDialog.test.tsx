import { act, fireEvent, screen } from '@testing-library/react';
import * as React from 'react';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import WorkflowMutationDialog from './WorkflowMutationDialog';

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
    archiveWorkflow: jest.fn(),
    queryWorkflowCatalogue: jest.fn(),
  },
}));
jest.mock('@/shared/studio/api', () => ({
  isStudioApiStatus: () => false,
  studioApi: { deleteWorkflowDraft: jest.fn() },
}));
const scopes = jest.requireMock('@/shared/api/scopesApi').scopesApi as {
  archiveWorkflow: jest.Mock;
  queryWorkflowCatalogue: jest.Mock;
};
const studio = jest.requireMock('@/shared/studio/api').studioApi as {
  deleteWorkflowDraft: jest.Mock;
};
const row = (deploymentStatus = 'Active') => ({
  workflowId: 'wf-alpha',
  hasCommittedSource: true,
  committed: {
    deploymentStatus,
    activeRevisionId: 'rev-alpha',
    deploymentId: 'dep-alpha',
  },
});
const catalogue = (items: ReturnType<typeof row>[]) => ({
  items,
  nextPageToken: null,
});
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
const onObserved = jest.fn();
const onClose = jest.fn();
function Harness({ kind }: { kind: 'archive' | 'delete' }) {
  const [open, setOpen] = React.useState(true);
  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>
        Reopen
      </button>
      <WorkflowMutationDialog
        kind={kind}
        scopeId="scope-alpha"
        workflowId="wf-alpha"
        workflowName="My workflow"
        open={open}
        onObserved={onObserved}
        onClose={() => {
          setOpen(false);
          onClose();
        }}
      />
    </>
  );
}

beforeEach(() => {
  jest.clearAllMocks();
  scopes.archiveWorkflow.mockResolvedValue({ accepted: true });
  studio.deleteWorkflowDraft.mockResolvedValue(undefined);
  jest.useFakeTimers();
});
afterEach(() => {
  jest.useRealTimers();
});

it('automatically confirms archival after the old observation window without another command', async () => {
  let archived = false;
  scopes.queryWorkflowCatalogue.mockImplementation(async () =>
    catalogue(archived ? [row('Deactivated')] : []),
  );
  renderWithQueryClient(<Harness kind="archive" />);
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Archive workflow' }));
  });
  await act(async () => {
    await jest.advanceTimersByTimeAsync(4500);
  });
  expect(
    screen.getByText('Request accepted. Checking for completion…'),
  ).toBeInTheDocument();
  expect(onObserved).not.toHaveBeenCalled();
  archived = true;
  await act(async () => {
    await jest.advanceTimersByTimeAsync(3000);
  });
  expect(onObserved).toHaveBeenCalledTimes(1);
  expect(onClose).toHaveBeenCalledTimes(1);
  expect(scopes.archiveWorkflow).toHaveBeenCalledTimes(1);
});

it('keeps a failed deletion check distinct from failure to delete and retries only the read', async () => {
  scopes.queryWorkflowCatalogue
    .mockRejectedValueOnce(new Error('network offline'))
    .mockResolvedValue(catalogue([]));
  renderWithQueryClient(<Harness kind="delete" />);
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));
  });
  expect(screen.getByRole('dialog')).toHaveTextContent(
    "Couldn't check whether the change has finished",
  );
  expect(onObserved).not.toHaveBeenCalled();
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
  });
  expect(onObserved).toHaveBeenCalledTimes(1);
  expect(studio.deleteWorkflowDraft).toHaveBeenCalledTimes(1);
  expect(scopes.queryWorkflowCatalogue).toHaveBeenCalledTimes(2);
});

it('bounds a hung check and ignores its late result after closing and reopening the accepted operation', async () => {
  const oldRead = deferred<ReturnType<typeof catalogue>>();
  const newRead = deferred<ReturnType<typeof catalogue>>();
  scopes.queryWorkflowCatalogue
    .mockReturnValueOnce(oldRead.promise)
    .mockReturnValueOnce(newRead.promise);
  renderWithQueryClient(<Harness kind="delete" />);
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));
  });
  const signal = scopes.queryWorkflowCatalogue.mock.calls[0][1];
  await act(async () => {
    await jest.advanceTimersByTimeAsync(30_000);
  });
  expect(signal.aborted).toBe(true);
  expect(
    screen.getByText(
      'This is taking longer than expected. Completion is still unconfirmed.',
    ),
  ).toBeInTheDocument();
  fireEvent.click(
    screen.getAllByRole('button', { name: 'Close' }).slice(-1)[0],
  );
  fireEvent.click(screen.getByRole('button', { name: 'Reopen' }));
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
  });
  await act(async () => {
    oldRead.resolve(catalogue([]));
  });
  expect(onObserved).not.toHaveBeenCalled();
  expect(
    screen.getByText('Request accepted. Checking for completion…'),
  ).toBeInTheDocument();
  await act(async () => {
    newRead.resolve(catalogue([]));
  });
  expect(onObserved).toHaveBeenCalledTimes(1);
  expect(studio.deleteWorkflowDraft).toHaveBeenCalledTimes(1);
});
