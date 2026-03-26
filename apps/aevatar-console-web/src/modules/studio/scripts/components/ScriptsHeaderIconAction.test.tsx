import { ExperimentOutlined, SaveOutlined } from '@ant-design/icons';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import ScriptsHeaderIconAction from './ScriptsHeaderIconAction';

describe('ScriptsHeaderIconAction', () => {
  it('shows the tooltip for an enabled action', async () => {
    render(
      <ScriptsHeaderIconAction
        ariaLabel="Validate"
        onClick={() => {}}
        tooltip="Validate"
      >
        <ExperimentOutlined />
      </ScriptsHeaderIconAction>,
    );

    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Validate' }).parentElement!);

    await waitFor(() => {
      expect(screen.getByText('Validate')).toBeTruthy();
    });
  });

  it('shows the tooltip for a disabled action through the wrapper', async () => {
    render(
      <ScriptsHeaderIconAction
        ariaLabel="Save"
        disabled
        onClick={() => {}}
        tooltip="Requires a resolved scope"
      >
        <SaveOutlined />
      </ScriptsHeaderIconAction>,
    );

    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Save' }).parentElement!);

    await waitFor(() => {
      expect(screen.getByText('Requires a resolved scope')).toBeTruthy();
    });
  });

  it('marks emphasis actions with the emphasis class', () => {
    render(
      <ScriptsHeaderIconAction
        ariaLabel="Save"
        onClick={() => {}}
        tone="emphasis"
        tooltip="Save"
      >
        <SaveOutlined />
      </ScriptsHeaderIconAction>,
    );

    expect(screen.getByRole('button', { name: 'Save' }).className).toContain(
      'console-scripts-header-action-emphasis',
    );
  });
});
