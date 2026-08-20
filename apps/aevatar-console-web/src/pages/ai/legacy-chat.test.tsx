import { render, waitFor } from '@testing-library/react';
import React from 'react';
import LegacyChatRedirectPage from './legacy-chat';

describe('LegacyChatRedirectPage', () => {
  it('redirects to canonical AI Chat and preserves route context', async () => {
    window.history.replaceState(
      {},
      '',
      '/chat?conversationId=conversation-alpha#trajectory',
    );

    render(<LegacyChatRedirectPage />);

    await waitFor(() => {
      expect(window.location.pathname).toBe('/ai/chat');
    });
    expect(window.location.search).toBe('?conversationId=conversation-alpha');
    expect(window.location.hash).toBe('#trajectory');
  });
});
