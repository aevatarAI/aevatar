import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import React from 'react';
import { createIntl } from 'react-intl';
import enUSMessages from '@/locales/en-US';
import zhCNMessages from '@/locales/zh-CN';
import type { StudioStepInspectorDraft } from '@/shared/studio/document';
import { getStudioNodeConfigurationSchema } from '@/shared/studio/nodeConfigFieldSchemas';
import { workflowActivityVNextCss } from '../styles';
import WorkflowNodeInspector from './WorkflowNodeInspector';

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
) {
  render(
    <WorkflowNodeInspector
      onClose={jest.fn()}
      onConfigurationChange={onConfigurationChange}
      onConfigurationErrorChange={jest.fn()}
      stepDraft={stepDraft}
    />,
  );
  return { onConfigurationChange };
}

describe('WorkflowNodeInspector', () => {
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

  it('formats the JSON arguments example without ICU errors', () => {
    const argumentsPlaceholder = getStudioNodeConfigurationSchema(
      'tool_call',
      {},
    ).fields.find((field) => field.name === 'arguments')?.placeholder;

    expect(argumentsPlaceholder).toBeDefined();
    if (!argumentsPlaceholder) throw new Error('Arguments placeholder missing');

    for (const [locale, messages] of [
      [
        'en-US',
        {
          [argumentsPlaceholder.id]: argumentsPlaceholder.defaultMessage,
        },
      ],
      ['en-US', enUSMessages],
      ['zh-CN', zhCNMessages],
    ] as const) {
      const onError = jest.fn();
      const intl = createIntl({ locale, messages, onError });

      expect(intl.formatMessage(argumentsPlaceholder)).toBe(
        '{"query":"$input"}',
      );
      expect(onError).not.toHaveBeenCalled();
    }
  });
});
