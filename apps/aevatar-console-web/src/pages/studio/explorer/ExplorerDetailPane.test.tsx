import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import ExplorerDetailPane from './ExplorerDetailPane';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@/shared/ui/ConsoleToast', () => ({
  useConsoleToast: () => mockConsoleToast,
}));

function renderPane(
  overrides: {
    readonly onDeleteFile?: (key: string) => Promise<void>;
    readonly onSaveFile?: (key: string, content: string) => Promise<void>;
  } = {},
) {
  return render(
    <ExplorerDetailPane
      content="original"
      contentErrorMessage={null}
      contentLoading={false}
      errorMessage={null}
      onDeleteFile={overrides.onDeleteFile}
      onOpenScriptInStudio={jest.fn()}
      onOpenWorkflowInStudio={jest.fn()}
      onSaveFile={overrides.onSaveFile}
      scopeId="scope-alpha"
      selectedEntry={{
        key: 'configs/app.txt',
        name: 'app.txt',
        type: 'config',
      }}
    />,
  );
}

describe('ExplorerDetailPane operation feedback', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('reports save failures with a toast without exposing raw details', async () => {
    const onSaveFile = jest
      .fn()
      .mockRejectedValue(new Error('PUT /api/explorer returned 500'));
    renderPane({ onSaveFile });

    fireEvent.change(screen.getByLabelText('Explorer file editor'), {
      target: { value: 'changed' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Could not save file. Try again.',
      ),
    );
    expect(screen.queryByText('Could not save file')).not.toBeInTheDocument();
    expect(
      screen.queryByText('PUT /api/explorer returned 500'),
    ).not.toBeInTheDocument();
  });

  it('reports delete failures with a toast and keeps the retry action', async () => {
    const onDeleteFile = jest
      .fn()
      .mockRejectedValue(new Error('DELETE /api/explorer returned 500'));
    renderPane({ onDeleteFile });

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete now' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Could not delete file. Try again.',
      ),
    );
    expect(screen.queryByText('Could not delete file')).not.toBeInTheDocument();
    expect(
      screen.queryByText('DELETE /api/explorer returned 500'),
    ).not.toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /Delete now/ })).toBeEnabled(),
    );
  });
});
