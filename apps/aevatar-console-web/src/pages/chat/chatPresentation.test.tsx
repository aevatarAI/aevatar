import { fireEvent, render, screen } from '@testing-library/react';
import React, { useState } from 'react';
import { ChatInput } from './chatPresentation';

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
