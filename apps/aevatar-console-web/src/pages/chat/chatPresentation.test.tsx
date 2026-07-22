import { fireEvent, render, screen, within } from '@testing-library/react';
import React, { useState } from 'react';
import { ChatInput, ChatMessageBubble } from './chatPresentation';
import type { ChatMessage } from './chatTypes';

function ChatInputHarness({ onSend }: { onSend: (value: string) => void }) {
  const [value, setValue] = useState('');

  return (
    <ChatInput
      disabled={false}
      isStreaming={false}
      onChange={setValue}
      onSend={() => onSend(value)}
      onStop={() => undefined}
      placeholder="Chat prompt"
      value={value}
    />
  );
}

describe('ChatInput', () => {
  it('keeps composed text in the input when Enter confirms an IME candidate', () => {
    const onSend = jest.fn();
    render(<ChatInputHarness onSend={onSend} />);

    const composer = screen.getByPlaceholderText('Chat prompt');

    fireEvent.compositionStart(composer);
    fireEvent.change(composer, { target: { value: 'workflow' } });
    fireEvent.keyDown(composer, { code: 'Enter', key: 'Enter' });
    fireEvent.compositionEnd(composer);

    expect(onSend).not.toHaveBeenCalled();
    expect(composer).toHaveValue('workflow');

    fireEvent.keyDown(composer, { code: 'Enter', key: 'Enter' });

    expect(onSend).toHaveBeenCalledTimes(1);
    expect(onSend).toHaveBeenLastCalledWith('workflow');
  });

  it('ignores a legacy IME Enter reported after composition ends', () => {
    const onSend = jest.fn();
    render(<ChatInputHarness onSend={onSend} />);

    const composer = screen.getByPlaceholderText('Chat prompt');
    fireEvent.compositionStart(composer);
    fireEvent.change(composer, { target: { value: 'workflow' } });
    fireEvent.compositionEnd(composer);
    fireEvent.keyDown(composer, {
      code: 'Enter',
      key: 'Enter',
      keyCode: 229,
    });

    expect(onSend).not.toHaveBeenCalled();
    expect(composer).toHaveValue('workflow');
  });

  it('ignores Enter while the native keyboard event is composing', () => {
    const onSend = jest.fn();
    render(<ChatInputHarness onSend={onSend} />);

    const composer = screen.getByPlaceholderText('Chat prompt');
    fireEvent.change(composer, { target: { value: 'workflow' } });
    fireEvent.keyDown(composer, {
      code: 'Enter',
      isComposing: true,
      key: 'Enter',
    });

    expect(onSend).not.toHaveBeenCalled();
    expect(composer).toHaveValue('workflow');

    fireEvent.keyDown(composer, { code: 'Enter', key: 'Enter' });

    expect(onSend).toHaveBeenCalledTimes(1);
    expect(onSend).toHaveBeenLastCalledWith('workflow');
  });

  it('keeps Shift+Enter available for multiline input', () => {
    const onSend = jest.fn();
    render(<ChatInputHarness onSend={onSend} />);

    const composer = screen.getByPlaceholderText('Chat prompt');
    fireEvent.change(composer, { target: { value: 'workflow' } });

    expect(
      fireEvent.keyDown(composer, {
        code: 'Enter',
        key: 'Enter',
        shiftKey: true,
      }),
    ).toBe(true);
    expect(onSend).not.toHaveBeenCalled();
  });
});

describe('ChatMessageBubble', () => {
  it('renders GFM tables with accessible headers and responsive containment', () => {
    const message: ChatMessage = {
      id: 'message-table',
      role: 'assistant',
      content: `| 名称 | member_id | workflow_id |
| --- | :---: | ---: |
| Observatory | \`m-alpha\` | \`wf-alpha-with-a-long-identifier\` |`,
      timestamp: 1,
      status: 'complete',
    };

    render(<ChatMessageBubble message={message} />);

    const region = screen.getByRole('region', { name: 'Message table' });
    expect(region).toHaveStyle({ maxWidth: '100%', overflowX: 'auto' });

    const table = within(region).getByRole('table');
    const headers = within(table).getAllByRole('columnheader');
    expect(headers).toHaveLength(3);
    expect(headers[0]).toHaveAttribute('scope', 'col');
    expect(headers[2]).toHaveStyle({ textAlign: 'right' });

    const workflowId = within(table).getByText(
      'wf-alpha-with-a-long-identifier',
    );
    expect(workflowId.tagName).toBe('CODE');
    expect(workflowId.closest('td')).toHaveStyle({ overflowWrap: 'anywhere' });
  });
});
