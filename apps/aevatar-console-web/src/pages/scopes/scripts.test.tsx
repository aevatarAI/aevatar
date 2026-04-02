import { waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ScopeScriptsPage from './scripts';

describe('ScopeScriptsPage', () => {
  beforeEach(() => {
    window.history.replaceState(
      {},
      '',
      '/scopes/scripts?scopeId=scope-a&scriptId=script-alpha',
    );
  });

  it('redirects script deep links to the unified project assets page', async () => {
    renderWithQueryClient(React.createElement(ScopeScriptsPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/scopes/assets');
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get('scopeId')).toBe('scope-a');
    expect(params.get('tab')).toBe('scripts');
    expect(params.get('scriptId')).toBe('script-alpha');
  });
});
