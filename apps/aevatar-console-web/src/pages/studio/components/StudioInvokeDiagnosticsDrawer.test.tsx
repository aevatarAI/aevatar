import { render, screen } from '@testing-library/react';
import React from 'react';
import {
  createIdleInvokeResult,
  type InvokeResultState,
} from './StudioMemberInvokePanel.currentRun';
import StudioInvokeDiagnosticsDrawer from './StudioInvokeDiagnosticsDrawer';

describe('StudioInvokeDiagnosticsDrawer', () => {
  it('keeps diagnostics, raw payload, and history detail inside the drawer', () => {
    const invokeResult: InvokeResultState = {
      ...createIdleInvokeResult(),
      actorId: 'actor-alpha',
      commandId: 'cmd-alpha',
      eventCount: 2,
      events: [
        {
          delta: 'partial answer',
          timestamp: 1_790_000_000_100,
          type: 'TEXT_MESSAGE_CONTENT',
        },
        {
          result: 'final answer',
          timestamp: 1_790_000_000_500,
          type: 'RUN_FINISHED',
        },
      ] as unknown as InvokeResultState['events'],
      finalOutput: 'final answer',
      runId: 'run-alpha',
      status: 'success' as const,
    };

    render(
      React.createElement(StudioInvokeDiagnosticsDrawer, {
        activeRunCompletedAt: 1_790_000_000_500,
        chatMessages: [],
        currentRawOutput: '{"runId":"run-alpha"}',
        currentRunHasData: true,
        currentRunRequest: {
          mode: 'stream',
          payloadBase64: '',
          payloadTypeUrl: '',
          prompt: 'Summarize the ticket',
          startedAt: 1_790_000_000_000,
        },
        endpointLabel: 'Chat',
        historyEntry: {
          completedAt: 1_790_000_000_500,
          createdAt: 1_790_000_000_000,
          endpointId: 'chat',
          endpointLabel: 'Chat',
          errorDetail: '',
          eventCount: 2,
          id: 'history-alpha',
          mode: 'stream',
          payloadBase64: '',
          payloadTypeUrl: '',
          prompt: 'Summarize the ticket',
          runId: 'run-alpha',
          serviceId: 'svc-alpha',
          startedAt: 1_790_000_000_000,
          status: 'success',
          summary: 'Summarize the ticket',
          snapshot: {
            chatMessages: [],
            result: invokeResult,
          },
        },
        invokeResult,
        isChatEndpoint: true,
        open: true,
        payloadBase64: '',
        payloadTypeUrl: '',
        runElapsedLabel: '00:00',
        runViewMode: 'historical',
        onClose: jest.fn(),
        onPayloadBase64Change: jest.fn(),
        onPayloadTypeUrlChange: jest.fn(),
      }),
    );

    expect(screen.getByTestId('studio-invoke-diagnostics-drawer')).toBeTruthy();
    expect(screen.getByText('Run diagnostics')).toBeTruthy();
    expect(screen.getByText('Historical run detail')).toBeTruthy();
    expect(screen.getByText('History detail')).toBeTruthy();
    expect(screen.getByText('Timeline')).toBeTruthy();
    expect(screen.getByText('Events')).toBeTruthy();
    expect(screen.getByText('Run details')).toBeTruthy();
    expect(screen.getByText('Event payload')).toBeTruthy();
    expect(screen.queryByText('run-alpha')).toBeNull();
    expect(screen.queryByText('cmd-alpha')).toBeNull();
    expect(screen.queryByText('actor-alpha')).toBeNull();
    expect(screen.getByText('Summarize the ticket')).toBeTruthy();
    expect(screen.getAllByText('Chat').length).toBeGreaterThan(0);
    expect(screen.queryByRole('tablist')).toBeNull();
  });
});
