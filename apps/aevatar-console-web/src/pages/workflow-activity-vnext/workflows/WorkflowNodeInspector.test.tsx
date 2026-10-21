import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import React from 'react';
import { createIntl } from 'react-intl';
import enUSMessages from '@/locales/messages/en-US';
import zhCNMessages from '@/locales/messages/zh-CN';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import type { WorkflowPrimitiveDescriptor } from '@/shared/models/runtime/query';
import {
  createTestQueryClient,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import { workflowNodeDescriptorFixtures } from '../../../../tests/workflowPrimitiveDescriptorFixtures';
import type { StudioStepInspectorDraft } from '@/shared/studio/document';
import { workflowActivityVNextCss } from '../styles';
import WorkflowNodeInspector from './WorkflowNodeInspector';

jest.mock('@/shared/api/runtimeQueryApi', () => ({
  runtimeQueryApi: { listPrimitives: jest.fn() },
}));

const llmDraft: StudioStepInspectorDraft = {
  kind: 'step',
  id: 'summarize_result',
  type: 'llm_call',
  targetRole: 'analyst',
  next: '',
  branchesText: '{}',
  parametersText: '{\n  "prompt_prefix": "Summarize the input"\n}',
};

const toolDraft: StudioStepInspectorDraft = {
  kind: 'step',
  id: 'fetch_dashboard',
  type: 'tool_call',
  targetRole: 'analyst',
  next: 'summarize_result',
  branchesText: '{}',
  parametersText:
    '{\n  "tool": "nyxid_proxy",\n  "arguments": "{\\"query\\":{\\"request\\":\\"$input\\"}}"\n}',
};

const voteDraft: StudioStepInspectorDraft = {
  kind: 'step',
  id: 'choose_result',
  type: 'vote',
  targetRole: '',
  next: '',
  branchesText: '{}',
  parametersText: '{}',
};

function renderInspector(
  stepDraft: StudioStepInspectorDraft,
  onConfigurationChange = jest.fn().mockReturnValue(true),
  descriptors = workflowNodeDescriptorFixtures,
) {
  const queryClient = createTestQueryClient();
  queryClient.setQueryData(['primitive-library'], descriptors);
  renderWithQueryClient(
    <WorkflowNodeInspector
      onClose={jest.fn()}
      onConfigurationChange={onConfigurationChange}
      onConfigurationErrorChange={jest.fn()}
      stepDraft={stepDraft}
    />,
    queryClient,
  );
  return { onConfigurationChange, queryClient };
}

describe('WorkflowNodeInspector', () => {
  beforeEach(() => {
    jest
      .mocked(runtimeQueryApi.listPrimitives)
      .mockResolvedValue(workflowNodeDescriptorFixtures);
  });
  it('leads with purpose and keeps technical details secondary', () => {
    renderInspector(llmDraft);

    const inspector = screen.getByRole('complementary', {
      name: 'Configure summarize_result',
    });
    const purpose = within(inspector).getByText(
      'Send an instruction and workflow input to an AI model.',
    );
    const settings = within(inspector).getByRole('heading', {
      name: 'Settings',
    });

    expect(
      purpose.compareDocumentPosition(settings) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(within(inspector).getByText('Technical details')).toBeVisible();
    expect(within(inspector).getByText('Advanced JSON')).toBeVisible();
    expect(
      within(inspector).getByRole('button', { name: 'Apply step' }),
    ).toBeVisible();
  });

  it('explains existing tool-call parameters without capability discovery', () => {
    renderInspector(toolDraft);

    expect(screen.getByRole('textbox', { name: 'Tool name' })).toHaveValue(
      'nyxid_proxy',
    );
    expect(screen.getByRole('radio', { name: 'Fields' })).toBeChecked();
    expect(
      screen.getByRole('textbox', { name: 'Value for query.request' }),
    ).toHaveValue('$input');
    expect(screen.getByText('Arguments')).toBeVisible();
    expect(screen.getByText('Required')).toBeVisible();
    expect(screen.getByText('Optional')).toBeVisible();
    expect(
      screen.getByText(
        "Property names and value types must match the tool's expected input.",
      ),
    ).toBeVisible();
  });

  it('keeps edited tool arguments string-shaped when applying the step', async () => {
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderInspector(toolDraft, onConfigurationChange);

    fireEvent.change(
      screen.getByRole('textbox', { name: 'Value for query.request' }),
      { target: { value: '$result' } },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Apply step' }));

    await waitFor(() => expect(onConfigurationChange).toHaveBeenCalledTimes(1));
    const parametersText = onConfigurationChange.mock.calls[0][0] as string;
    const parameters = JSON.parse(parametersText) as Record<string, unknown>;
    expect(parameters.tool).toBe('nyxid_proxy');
    expect(typeof parameters.arguments).toBe('string');
    expect(JSON.parse(parameters.arguments as string)).toEqual({
      query: { request: '$result' },
    });
  });

  it('does not apply a tool call while its arguments JSON is invalid', async () => {
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderInspector(toolDraft, onConfigurationChange);

    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Arguments JSON' }), {
      target: { value: '{"query":' },
    });

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Apply step' })).toBeDisabled(),
    );
    expect(onConfigurationChange).not.toHaveBeenCalled();
  });

  it('allows argument expressions after replacing invalid arguments JSON', async () => {
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderInspector(toolDraft, onConfigurationChange);

    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Arguments JSON' }), {
      target: { value: '{"query":' },
    });
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Apply step' })).toBeDisabled(),
    );

    fireEvent.click(screen.getByText('Advanced JSON'));
    fireEvent.change(screen.getByRole('textbox', { name: 'Raw configuration' }), {
      target: {
        value: JSON.stringify({ tool: 'nyxid_proxy', arguments: '$input' }),
      },
    });

    expect(screen.getByRole('textbox', { name: 'Arguments' })).toHaveValue('$input');
    expect(
      screen.queryByRole('textbox', { name: 'Arguments JSON' }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply step' })).toBeEnabled();

    fireEvent.change(screen.getByRole('textbox', { name: 'Arguments' }), {
      target: { value: `\${args}` },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Apply step' }));

    await waitFor(() => expect(onConfigurationChange).toHaveBeenCalledTimes(1));
    expect(JSON.parse(onConfigurationChange.mock.calls[0][0] as string)).toEqual({
      tool: 'nyxid_proxy',
      arguments: `\${args}`,
    });
  });

  it('does not apply a tool call after a duplicate argument name is rejected', async () => {
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    const duplicateDraft = {
      ...toolDraft,
      parametersText:
        '{\n  "tool": "nyxid_proxy",\n  "arguments": "{\\"name\\":\\"Ada\\",\\"count\\":2}"\n}',
    };
    renderInspector(duplicateDraft, onConfigurationChange);

    fireEvent.change(
      screen.getByRole('textbox', { name: 'Property name for count' }),
      { target: { value: 'name' } },
    );

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Apply step' })).toBeDisabled(),
    );
    expect(
      screen
        .getAllByRole('alert')
        .every((alert) =>
          alert.textContent?.includes('Property names must be unique.'),
        ),
    ).toBe(true);
    expect(onConfigurationChange).not.toHaveBeenCalled();
  });

  it('states when a step has no settings instead of leaving an empty panel', () => {
    renderInspector(voteDraft);

    expect(
      screen.getByText('No settings are needed for this step.'),
    ).toBeVisible();
  });

  it('keeps the mobile inspector between the top bar and logs dock', () => {
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__node-inspector { bottom: 56px; left: 12px; max-height: calc(100dvh - 120px); max-width: none; position: fixed; right: 12px; top: auto; width: auto; }',
    );
  });

  it('uses backend fields instead of merging the built-in fields and preserves unknown values', async () => {
    const remoteDescriptor: WorkflowPrimitiveDescriptor = {
      ...workflowNodeDescriptorFixtures[0],
      parameters: [
        {
          name: 'server_note',
          label: 'Server-defined setting',
          type: 'string',
          required: false,
          description: '',
          default: '',
          enumValues: [],
        },
      ],
    };
    jest
      .mocked(runtimeQueryApi.listPrimitives)
      .mockResolvedValue([remoteDescriptor]);
    const { onConfigurationChange } = renderInspector(llmDraft, undefined, [
      remoteDescriptor,
    ]);

    expect(
      screen.queryByRole('textbox', { name: 'Instruction' }),
    ).not.toBeInTheDocument();
    fireEvent.change(
      screen.getByRole('textbox', { name: 'Server-defined setting' }),
      {
        target: { value: 'From the API' },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Apply step' }));

    await waitFor(() => expect(onConfigurationChange).toHaveBeenCalledTimes(1));
    expect(JSON.parse(onConfigurationChange.mock.calls[0][0])).toEqual({
      prompt_prefix: 'Summarize the input',
      server_note: 'From the API',
    });
  });

  it('keeps an invalid built-in edit when retry loads fields with canonical parameter names', async () => {
    const signalDescriptor: WorkflowPrimitiveDescriptor = {
      name: 'wait_signal',
      aliases: [],
      category: 'control',
      description: '',
      exampleWorkflows: [],
      parameters: [
        {
          name: 'signal_name',
          label: 'Signal name',
          type: 'string',
          required: true,
          description: '',
          default: '',
          enumValues: [],
        },
        {
          name: 'timeout_ms',
          label: 'Timeout ms',
          type: 'int32',
          required: false,
          description: '',
          default: '',
          enumValues: [],
        },
      ],
    };
    jest
      .mocked(runtimeQueryApi.listPrimitives)
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce([signalDescriptor]);
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderWithQueryClient(
      <WorkflowNodeInspector
        onClose={jest.fn()}
        onConfigurationChange={onConfigurationChange}
        onConfigurationErrorChange={jest.fn()}
        stepDraft={{
          ...llmDraft,
          type: 'wait_signal',
          parametersText: '{"signal_name":"approval","timeout_ms":100}',
        }}
      />,
    );

    expect(
      await screen.findByText(
        "Couldn't load the latest settings. Showing built-in settings.",
      ),
    ).toBeVisible();
    fireEvent.change(screen.getByRole('textbox', { name: 'Signal name' }), {
      target: { value: '' },
    });
    expect(screen.getByRole('button', { name: 'Apply step' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(
        screen.queryByText(
          "Couldn't load the latest settings. Showing built-in settings.",
        ),
      ).not.toBeInTheDocument(),
    );
    expect(screen.getByRole('textbox', { name: 'Signal name' })).toHaveValue(
      '',
    );
    expect(screen.getByRole('button', { name: 'Apply step' })).toBeDisabled();
    fireEvent.change(screen.getByRole('textbox', { name: 'Signal name' }), {
      target: { value: 'resumed' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Apply step' }));

    await waitFor(() => expect(onConfigurationChange).toHaveBeenCalledTimes(1));
    expect(JSON.parse(onConfigurationChange.mock.calls[0][0])).toEqual({
      signal_name: 'resumed',
      timeout_ms: 100,
    });
  });

  it('preserves removed field edits as advanced JSON and clears obsolete validation when the descriptor is empty', async () => {
    const descriptor: WorkflowPrimitiveDescriptor = {
      name: 'wait_signal',
      aliases: [],
      category: 'control',
      description: '',
      exampleWorkflows: [],
      parameters: [],
    };
    jest
      .mocked(runtimeQueryApi.listPrimitives)
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce([descriptor]);
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderWithQueryClient(
      <WorkflowNodeInspector
        onClose={jest.fn()}
        onConfigurationChange={onConfigurationChange}
        onConfigurationErrorChange={jest.fn()}
        stepDraft={{
          ...llmDraft,
          type: 'wait_signal',
          parametersText: '{"signal_name":"approval","timeout_ms":100}',
        }}
      />,
    );
    await screen.findByText(
      "Couldn't load the latest settings. Showing built-in settings.",
    );
    fireEvent.change(screen.getByRole('textbox', { name: 'Signal name' }), {
      target: { value: '' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Timeout ms' }), {
      target: { value: 'changed while invalid' },
    });
    expect(screen.getByRole('button', { name: 'Apply step' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(
      await screen.findByText('No settings are needed for this step.'),
    ).toBeVisible();
    expect(
      screen.queryByRole('textbox', { name: 'Signal name' }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply step' })).toBeEnabled();
    fireEvent.click(screen.getByText('Advanced JSON'));
    expect(
      JSON.parse(
        (
          (await screen.findByRole('textbox', {
            name: 'Raw configuration',
          })) as HTMLTextAreaElement
        ).value,
      ),
    ).toEqual({
      signal_name: '',
      timeout_ms: 'changed while invalid',
    });
    fireEvent.click(screen.getByRole('button', { name: 'Apply step' }));
    await waitFor(() => expect(onConfigurationChange).toHaveBeenCalledTimes(1));
    expect(JSON.parse(onConfigurationChange.mock.calls[0][0])).toEqual({
      signal_name: '',
      timeout_ms: 'changed while invalid',
    });
  });

  it('keeps advanced JSON edits when the initial descriptor arrives', async () => {
    let resolveCatalog!: (value: WorkflowPrimitiveDescriptor[]) => void;
    jest.mocked(runtimeQueryApi.listPrimitives).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveCatalog = resolve;
        }),
    );
    renderWithQueryClient(
      <WorkflowNodeInspector
        onClose={jest.fn()}
        onConfigurationChange={jest.fn()}
        onConfigurationErrorChange={jest.fn()}
        stepDraft={llmDraft}
      />,
    );
    expect(screen.getByText('Loading settings')).toBeVisible();
    fireEvent.click(screen.getByText('Advanced JSON'));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Raw configuration' }),
      {
        target: {
          value: '{"prompt_prefix":"Edited before loading","future_option":7}',
        },
      },
    );

    await act(async () => {
      resolveCatalog(workflowNodeDescriptorFixtures);
    });

    expect(
      await screen.findByRole('textbox', { name: 'Instruction' }),
    ).toHaveValue('Edited before loading');
    expect(
      screen.getByRole('textbox', { name: 'Raw configuration' }),
    ).toHaveValue(
      '{"prompt_prefix":"Edited before loading","future_option":7}',
    );
    expect(screen.getByRole('button', { name: 'Apply step' })).toBeEnabled();
  });
});
