import { render, screen } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import AIChatPage from './chat';

function createChatWorkspaceContext() {
  return {
    context: {
      scopeId: 'scope-alpha',
      consistency: 'independent_read_models',
      pages: { chat: '/ai/chat' },
      apis: { chat: '/api/chat' },
      features: {
        chat: {
          availability: 'available',
          page: '/ai/chat',
          api: '/api/chat',
        },
      },
    },
    scopeId: 'scope-alpha',
  };
}

let mockWorkspaceContext = createChatWorkspaceContext();

jest.mock('@/pages/chat', () => {
  const mockReact = jest.requireActual('react');
  return {
    __esModule: true,
    default: () => mockReact.createElement('div', null, 'Canonical Chat'),
  };
});

jest.mock('./components/AIWorkspaceShell', () => {
  const mockReact = jest.requireActual('react');
  return {
    __esModule: true,
    default: ({ children }: { children: never }) =>
      mockReact.createElement(mockReact.Fragment, null, children),
    useAIWorkspaceContext: () => mockWorkspaceContext,
  };
});

describe('AIChatPage', () => {
  beforeEach(() => {
    setLocale('en-US', false);
    mockWorkspaceContext = createChatWorkspaceContext();
  });

  it('mounts the canonical Chat only when the context advertises it', () => {
    render(React.createElement(AIChatPage));

    expect(screen.getByText('Canonical Chat')).toBeInTheDocument();
  });

  it('does not mount Chat when the capability contract is incomplete', () => {
    mockWorkspaceContext = {
      ...createChatWorkspaceContext(),
      context: {
        ...createChatWorkspaceContext().context,
        apis: { chat: '' },
      },
    };

    render(React.createElement(AIChatPage));

    expect(screen.getByText('Chat not available')).toBeInTheDocument();
    expect(screen.queryByText('Canonical Chat')).toBeNull();
  });
});
