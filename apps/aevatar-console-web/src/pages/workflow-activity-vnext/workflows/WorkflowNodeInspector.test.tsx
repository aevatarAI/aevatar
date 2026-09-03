import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import React from 'react';
import type { StudioStepInspectorDraft } from '@/shared/studio/document';
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
    expect(screen.getByRole('textbox', { name: 'Arguments JSON' })).toHaveValue(
      '{"query":{"request":"$input"}}',
    );
    expect(screen.getByText('Required')).toBeVisible();
    expect(screen.getByText('Optional')).toBeVisible();
    expect(
      screen.getByText(
        'Use the property names documented by this tool. The value is passed as JSON text.',
      ),
    ).toBeVisible();
  });

  it('keeps edited tool arguments string-shaped when applying the step', async () => {
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderInspector(toolDraft, onConfigurationChange);

    fireEvent.change(screen.getByRole('textbox', { name: 'Arguments JSON' }), {
      target: { value: '{"query":{"request":"$result"}}' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Apply step' }));

    await waitFor(() => expect(onConfigurationChange).toHaveBeenCalledTimes(1));
    const parametersText = onConfigurationChange.mock.calls[0][0] as string;
    expect(JSON.parse(parametersText)).toEqual({
      arguments: '{"query":{"request":"$result"}}',
      tool: 'nyxid_proxy',
    });
  });

  it('states when a step has no settings instead of leaving an empty panel', () => {
    renderInspector(voteDraft);

    expect(
      screen.getByText('No settings are needed for this step.'),
    ).toBeVisible();
  });
});
