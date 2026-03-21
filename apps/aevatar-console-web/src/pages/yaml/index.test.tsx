import { screen, waitFor } from '@testing-library/react';
import React from 'react';
import { savePlaygroundDraft } from '@/shared/playground/playgroundDraft';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import YamlPage from './index';

const mockReplace = jest.fn();
const mockPush = jest.fn();

jest.mock('@/shared/navigation/history', () => ({
  history: {
    replace: (...args: unknown[]) => mockReplace(...args),
    push: (...args: unknown[]) => mockPush(...args),
  },
}));

describe('YamlPage', () => {
  beforeEach(() => {
    window.localStorage.clear();
    window.history.pushState({}, '', '/yaml');
    mockReplace.mockReset();
    mockPush.mockReset();
  });

  it('redirects workflow YAML routes into the Studio editor', async () => {
    window.history.pushState({}, '', '/yaml?workflow=demo_template');

    renderWithQueryClient(React.createElement(YamlPage));

    expect(await screen.findByText('Opening Studio')).toBeTruthy();

    await waitFor(() => {
      expect(mockReplace).toHaveBeenCalledWith(
        '/studio?template=demo_template&tab=studio',
      );
    });
  });

  it('redirects legacy playground YAML inspection into a Studio draft handoff', async () => {
    savePlaygroundDraft({
      yaml: 'name: local_draft\nsteps: []\n',
      prompt: 'Use the local draft instead.',
      sourceWorkflow: 'demo_template',
    });
    window.history.pushState(
      {},
      '',
      '/yaml?workflow=demo_template&source=playground',
    );

    renderWithQueryClient(React.createElement(YamlPage));

    await waitFor(() => {
      expect(mockReplace).toHaveBeenCalledWith(
        '/studio?tab=studio&draft=new&prompt=Use+the+local+draft+instead.&legacy=playground',
      );
    });
  });
});
