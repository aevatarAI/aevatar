import { screen, waitFor } from '@testing-library/react';
import React from 'react';
import { savePlaygroundDraft } from '@/shared/playground/playgroundDraft';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import PlaygroundPage from './index';

const mockReplace = jest.fn();
const mockPush = jest.fn();

jest.mock('@/shared/navigation/history', () => ({
  history: {
    replace: (...args: unknown[]) => mockReplace(...args),
    push: (...args: unknown[]) => mockPush(...args),
  },
}));

describe('PlaygroundPage', () => {
  beforeEach(() => {
    window.localStorage.clear();
    window.history.pushState({}, '', '/playground');
    mockReplace.mockReset();
    mockPush.mockReset();
  });

  it('redirects legacy playground template routes into the Studio editor', async () => {
    window.history.pushState(
      {},
      '',
      '/playground?template=direct&import=1&prompt=Continue%20this%20workflow',
    );

    renderWithQueryClient(React.createElement(PlaygroundPage));

    expect(await screen.findByText('Opening Studio')).toBeTruthy();

    await waitFor(() => {
      expect(mockReplace).toHaveBeenCalledWith(
        '/studio?template=direct&tab=studio&prompt=Continue+this+workflow',
      );
    });
  });

  it('redirects browser-stored legacy drafts into Studio with legacy handoff', async () => {
    savePlaygroundDraft({
      yaml: 'name: local_draft\nsteps: []\n',
      prompt: 'Review the imported draft.',
      sourceWorkflow: 'demo_template',
    });

    renderWithQueryClient(React.createElement(PlaygroundPage));

    await waitFor(() => {
      expect(mockReplace).toHaveBeenCalledWith(
        '/studio?tab=studio&draft=new&prompt=Review+the+imported+draft.&legacy=playground',
      );
    });
  });
});
