import { waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ScopeWorkflowsPage from './workflows';

describe('ScopeWorkflowsPage', () => {
  beforeEach(() => {
    window.history.replaceState(
      {},
      '',
      '/scopes/workflows?scopeId=scope-a&workflowId=workflow-alpha',
    );
  });

  it('redirects workflow deep links to the unified project assets page', async () => {
    renderWithQueryClient(React.createElement(ScopeWorkflowsPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/scopes/assets');
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get('scopeId')).toBe('scope-a');
    expect(params.get('tab')).toBe('workflows');
    expect(params.get('workflowId')).toBe('workflow-alpha');
  });
});
