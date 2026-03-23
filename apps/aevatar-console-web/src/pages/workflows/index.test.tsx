import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { runtimeCatalogApi } from '@/shared/api/runtimeCatalogApi';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import WorkflowsPage from './index';

jest.mock('@/shared/api/runtimeCatalogApi', () => ({
  runtimeCatalogApi: {
    listWorkflowCatalog: jest.fn(async () => [
      {
        name: 'demo_flow',
        description: 'Demo workflow',
        category: 'demo',
        group: 'demo',
        groupLabel: 'Demo',
        sortOrder: 1,
        source: 'BuiltIn',
        sourceLabel: 'Built-in',
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: false,
        primitives: ['human_input'],
      },
    ]),
    getWorkflowDetail: jest.fn(async () => ({
      catalog: {
        name: 'demo_flow',
        description: 'Demo workflow',
        category: 'demo',
        group: 'demo',
        groupLabel: 'Demo',
        sortOrder: 1,
        source: 'BuiltIn',
        sourceLabel: 'Built-in',
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: false,
        primitives: ['human_input'],
      },
      yaml: 'name: demo_flow\nsteps: []\n',
      definition: {
        name: 'demo_flow',
        description: 'Demo workflow',
        closedWorldMode: false,
        roles: [
          {
            id: 'planner',
            name: 'Planner',
            systemPrompt: 'Plan the work.',
            provider: 'openai',
            model: 'gpt-4.1',
            temperature: 0.2,
            maxTokens: 1024,
            maxToolRounds: 4,
            maxHistoryMessages: 12,
            streamBufferCapacity: 8,
            eventModules: ['approval'],
            eventRoutes: 'approval -> planner',
            connectors: ['memory'],
          },
        ],
        steps: [
          {
            id: 'step_prepare',
            type: 'prompt',
            targetRole: 'planner',
            parameters: { input: '{{prompt}}' },
            next: 'step_finish',
            branches: {},
            children: [],
          },
          {
            id: 'step_finish',
            type: 'emit',
            targetRole: 'planner',
            parameters: {},
            next: '',
            branches: { done: 'complete' },
            children: [],
          },
        ],
      },
      edges: [{ from: 'step_prepare', to: 'step_finish', label: 'next' }],
    })),
  },
}));

jest.mock('@/shared/graphs/GraphCanvas', () => ({
  __esModule: true,
  default: ({ selectedNodeId }: { selectedNodeId?: string }) => {
    const React = require('react');
    return React.createElement(
      'div',
      null,
      `Graph node: ${selectedNodeId || 'none'}`,
    );
  },
}));

describe('WorkflowsPage', () => {
  beforeEach(() => {
    HTMLElement.prototype.scrollIntoView = jest.fn();
  });

  it('keeps advanced filters collapsed until requested', async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    await waitFor(() => {
      expect(runtimeCatalogApi.listWorkflowCatalog).toHaveBeenCalled();
    });
    expect(await screen.findAllByText('demo_flow')).toBeTruthy();

    expect(screen.queryByRole('combobox', { name: 'Groups' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Advanced filters' }));

    expect(
      await screen.findByRole('combobox', { name: 'Groups' }),
    ).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Inspect' })).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Copy workflow YAML' }),
    ).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Open YAML fullscreen' }),
    ).toBeTruthy();
  });

  it('collapses and expands the workflow library panel', async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    await waitFor(() => {
      expect(runtimeCatalogApi.listWorkflowCatalog).toHaveBeenCalled();
    });

    expect(
      await screen.findByPlaceholderText(
        /Filter by name, description, group, category, or primitive/i,
      ),
    ).toBeTruthy();

    fireEvent.click(
      screen.getByRole('button', { name: 'Collapse workflow library' }),
    );

    expect(
      screen.queryByPlaceholderText(
        /Filter by name, description, group, category, or primitive/i,
      ),
    ).toBeNull();
    expect(screen.getByText('Library panel is collapsed.')).toBeTruthy();

    fireEvent.click(
      screen.getByRole('button', { name: 'Expand workflow library' }),
    );

    expect(
      await screen.findByPlaceholderText(
        /Filter by name, description, group, category, or primitive/i,
      ),
    ).toBeTruthy();
  });

  it('opens the workflow graph in a fullscreen modal', async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    await waitFor(() => {
      expect(runtimeCatalogApi.getWorkflowDetail).toHaveBeenCalledWith(
        'demo_flow',
      );
    });

    fireEvent.click(await screen.findByRole('tab', { name: 'Graph' }));
    fireEvent.click(
      await screen.findByRole('button', { name: 'Open graph fullscreen' }),
    );

    expect(await screen.findByText('Fullscreen graph view')).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Close graph fullscreen' }),
    ).toBeTruthy();

    fireEvent.click(
      screen.getByRole('button', { name: 'Close graph fullscreen' }),
    );

    await waitFor(() => {
      expect(screen.queryByText('Fullscreen graph view')).toBeNull();
    });
  });

  it('focuses the selected step in graph view', async () => {
    renderWithQueryClient(React.createElement(WorkflowsPage));

    await waitFor(() => {
      expect(runtimeCatalogApi.getWorkflowDetail).toHaveBeenCalledWith(
        'demo_flow',
      );
    });

    fireEvent.click(await screen.findByRole('tab', { name: 'Steps (2)' }));
    fireEvent.click(screen.getByRole('button', { name: /step_prepare/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Focus in graph' }));

    await waitFor(() => {
      expect(
        screen
          .getByRole('tab', { name: 'Graph' })
          .getAttribute('aria-selected'),
      ).toBe('true');
    });
    expect(screen.getByText('Graph node: step_prepare')).toBeTruthy();
    expect(
      screen
        .getByTestId('workflow-graph-tab-label')
        .getAttribute('data-highlighted'),
    ).toBe('true');
    expect(HTMLElement.prototype.scrollIntoView).toHaveBeenCalled();
  });
});
