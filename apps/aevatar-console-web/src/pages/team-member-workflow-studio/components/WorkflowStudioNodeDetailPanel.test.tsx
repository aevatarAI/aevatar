import { fireEvent, screen } from '@testing-library/react';
import React from 'react';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import type { WorkflowPrimitiveDescriptor } from '@/shared/models/runtime/query';
import type { StudioStepInspectorDraft } from '@/shared/studio/document';
import {
  createTestQueryClient,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import WorkflowStudioNodeDetailPanel from './WorkflowStudioNodeDetailPanel';

jest.mock('@/shared/api/runtimeQueryApi', () => ({
  runtimeQueryApi: { listPrimitives: jest.fn() },
}));

function renderPanel(
  stepType: string,
  parameters: Record<string, unknown>,
  descriptors: WorkflowPrimitiveDescriptor[],
) {
  jest.mocked(runtimeQueryApi.listPrimitives).mockResolvedValue(descriptors);
  const queryClient = createTestQueryClient();
  queryClient.setQueryData(['primitive-library'], descriptors);
  const onConfigurationChange = jest.fn();
  const stepDraft: StudioStepInspectorDraft = {
    kind: 'step',
    id: 'step-alpha',
    type: stepType,
    targetRole: '',
    next: '',
    branchesText: '{}',
    parametersText: JSON.stringify(parameters),
  };
  renderWithQueryClient(
    <WorkflowStudioNodeDetailPanel
      onClose={jest.fn()}
      onConfigurationChange={onConfigurationChange}
      onConfigurationErrorChange={jest.fn()}
      stepDraft={stepDraft}
    />,
    queryClient,
  );
  return onConfigurationChange;
}

describe('WorkflowStudioNodeDetailPanel configuration sources', () => {
  it('uses built-in fields when the catalogue has no matching descriptor and preserves extra parameters', () => {
    const onConfigurationChange = renderPanel(
      'llm_call',
      {
        prompt_prefix: 'Original instruction',
        future_option: { enabled: true },
      },
      [],
    );

    expect(
      screen.getByText('Showing built-in settings for this step.'),
    ).toBeVisible();
    fireEvent.change(screen.getByRole('textbox', { name: 'Instruction' }), {
      target: { value: 'Updated instruction' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Update node' }));

    expect(JSON.parse(onConfigurationChange.mock.calls[0][0])).toEqual({
      prompt_prefix: 'Updated instruction',
      future_option: { enabled: true },
    });
    expect(screen.getByText('Advanced raw configuration')).toBeVisible();
  });

  it('uses descriptor aliases and keeps boolean, number, and enum expressions editable', () => {
    const descriptor: WorkflowPrimitiveDescriptor = {
      name: 'remote_step',
      aliases: ['remote_alias'],
      category: 'control',
      description: '',
      exampleWorkflows: [],
      parameters: [
        {
          name: 'enabled',
          label: 'Enabled value',
          type: 'boolean',
          required: false,
          description: '',
          default: '',
          enumValues: [],
        },
        {
          name: 'count',
          label: 'Count value',
          type: 'int32',
          required: false,
          description: '',
          default: '',
          enumValues: [],
        },
        {
          name: 'mode',
          label: 'Mode value',
          type: 'string',
          required: false,
          description: '',
          default: '',
          enumValues: ['first', 'all'],
        },
      ],
    };
    const onConfigurationChange = renderPanel(
      'remote_alias',
      {
        enabled: '$input.enabled',
        count: '$input.count',
        mode: '{{input.mode}}',
      },
      [descriptor],
    );

    expect(screen.getByRole('textbox', { name: 'Enabled value' })).toHaveValue(
      '$input.enabled',
    );
    expect(screen.getByRole('textbox', { name: 'Count value' })).toHaveValue(
      '$input.count',
    );
    expect(screen.getByRole('textbox', { name: 'Mode value' })).toHaveValue(
      '{{input.mode}}',
    );
    fireEvent.change(screen.getByRole('textbox', { name: 'Enabled value' }), {
      target: { value: '$result.enabled' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Update node' }));

    expect(JSON.parse(onConfigurationChange.mock.calls[0][0])).toEqual({
      enabled: '$result.enabled',
      count: '$input.count',
      mode: '{{input.mode}}',
    });
  });

  it('keeps an unknown node editable as raw JSON without inferring guided fields', async () => {
    const onConfigurationChange = renderPanel(
      'unknown_step',
      { enabled: true },
      [],
    );
    expect(
      screen.getByText(
        'Guided settings are unavailable for this step. Use advanced JSON.',
      ),
    ).toBeVisible();
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Advanced raw configuration'));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Raw node configuration' }),
      {
        target: { value: '{"enabled":false,"future_option":[1,2]}' },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Apply raw JSON' }));

    expect(JSON.parse(onConfigurationChange.mock.calls[0][0])).toEqual({
      enabled: false,
      future_option: [1, 2],
    });
  });
});
