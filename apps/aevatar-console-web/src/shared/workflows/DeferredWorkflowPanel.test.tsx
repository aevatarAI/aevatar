import { act, fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { DeferredWorkflowPanel } from './DeferredWorkflowPanel';

type PanelProps = { readonly open: boolean };
type PanelModule = { default: React.ComponentType<PanelProps> };

function ConfigurationPanel({ open }: PanelProps) {
  const [configuration, setConfiguration] = React.useState('');
  return open ? (
    <input
      aria-label="Step configuration"
      onChange={(event) => setConfiguration(event.target.value)}
      value={configuration}
    />
  ) : null;
}

function Editor({ load }: { readonly load: () => Promise<PanelModule> }) {
  const [open, setOpen] = React.useState(false);
  const [workflowName, setWorkflowName] = React.useState('Saved workflow');
  return (
    <>
      <input
        aria-label="Workflow name"
        onChange={(event) => setWorkflowName(event.target.value)}
        value={workflowName}
      />
      <button onClick={() => setOpen((value) => !value)} type="button">
        {open ? 'Close configuration' : 'Open configuration'}
      </button>
      <DeferredWorkflowPanel
        active={open}
        load={load}
        renderFallback={(retry) =>
          retry ? (
            <button onClick={retry} type="button">
              Retry configuration
            </button>
          ) : (
            <p role="status">Loading configuration</p>
          )
        }
      >
        {(Panel) => <Panel open={open} />}
      </DeferredWorkflowPanel>
    </>
  );
}

describe('DeferredWorkflowPanel', () => {
  it('loads only on demand and retains unapplied configuration across closing and reopening', async () => {
    let resolveModule: (module: PanelModule) => void = () => {
      throw new Error('The panel import has not started.');
    };
    const load = jest.fn(
      () =>
        new Promise<PanelModule>((resolve) => {
          resolveModule = resolve;
        }),
    );
    render(<Editor load={load} />);
    expect(load).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Open configuration' }));
    expect(screen.getByRole('status')).toHaveTextContent(
      'Loading configuration',
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Close configuration' }),
    );
    expect(screen.queryByRole('status')).not.toBeInTheDocument();

    await act(async () => resolveModule({ default: ConfigurationPanel }));
    fireEvent.click(screen.getByRole('button', { name: 'Open configuration' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Step configuration' }),
      {
        target: { value: 'Unapplied step settings' },
      },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Close configuration' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Open configuration' }));

    expect(
      screen.getByRole('textbox', { name: 'Step configuration' }),
    ).toHaveValue('Unapplied step settings');
    expect(load).toHaveBeenCalledTimes(1);
  });

  it('retries a failed panel import without discarding the unsaved workflow', async () => {
    const consoleError = jest
      .spyOn(console, 'error')
      .mockImplementation(() => {});
    try {
      const load = jest
        .fn<Promise<PanelModule>, []>()
        .mockRejectedValueOnce(new Error('Chunk unavailable'))
        .mockResolvedValue({ default: ConfigurationPanel });
      render(<Editor load={load} />);
      fireEvent.change(screen.getByRole('textbox', { name: 'Workflow name' }), {
        target: { value: 'Unsaved workflow name' },
      });
      fireEvent.click(
        screen.getByRole('button', { name: 'Open configuration' }),
      );
      fireEvent.click(
        await screen.findByRole('button', { name: 'Retry configuration' }),
      );

      expect(
        await screen.findByRole('textbox', { name: 'Step configuration' }),
      ).toBeVisible();
      expect(
        screen.getByRole('textbox', { name: 'Workflow name' }),
      ).toHaveValue('Unsaved workflow name');
      expect(load).toHaveBeenCalledTimes(2);
    } finally {
      consoleError.mockRestore();
    }
  });
});
