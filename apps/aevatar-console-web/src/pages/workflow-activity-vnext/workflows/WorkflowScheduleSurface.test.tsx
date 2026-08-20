import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { workflowScheduleApi } from '@/shared/api/workflowScheduleApi';
import WorkflowScheduleSurface from './WorkflowScheduleSurface';

jest.mock('@/shared/api/workflowScheduleApi', () => ({
  workflowScheduleApi: {
    create: jest.fn(),
    delete: jest.fn(),
    disable: jest.fn(),
    enable: jest.fn(),
    get: jest.fn(),
    list: jest.fn(),
    preview: jest.fn(),
    runNow: jest.fn(),
    update: jest.fn(),
  },
}));

const mockedWorkflowScheduleApi = jest.mocked(workflowScheduleApi);

const mockToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@/shared/ui/ConsoleToast', () => ({
  useConsoleToast: () => mockToast,
}));

function renderSurface(available: boolean) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { gcTime: 0, retry: false },
    },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <WorkflowScheduleSurface
        available={available}
        mode="modal"
        onClose={jest.fn()}
        open
        scopeId="scope-alpha"
        workflowId="wf-alpha"
        workflowName="Weekly review"
      />
    </QueryClientProvider>,
  );
}

describe('WorkflowScheduleSurface', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [],
      nextCursor: null,
      totalCount: 0,
    });
    mockedWorkflowScheduleApi.create.mockResolvedValue({
      scheduleId: 'schedule-alpha',
      accepted: true,
    });
  });

  it('does not query schedules for an unpublished Workflow', () => {
    renderSurface(false);

    expect(
      screen.getByText(
        'Schedule is unavailable until this Workflow is published',
      ),
    ).toBeInTheDocument();
    expect(workflowScheduleApi.list).not.toHaveBeenCalled();
  });

  it('creates a schedule with workflow-only fields and refreshes the list', async () => {
    renderSurface(true);

    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'New schedule' }),
      ).toBeVisible(),
    );
    fireEvent.click(screen.getByRole('button', { name: 'New schedule' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Daily workflow run' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create schedule' }));

    await waitFor(() =>
      expect(workflowScheduleApi.create).toHaveBeenCalledWith(
        'scope-alpha',
        'wf-alpha',
        expect.objectContaining({
          displayName: 'Daily workflow run',
          cronExpression: '0 9 * * 1-5',
          enabled: true,
          prompt: '',
          timezone: expect.any(String),
        }),
      ),
    );
    expect(
      mockedWorkflowScheduleApi.create.mock.calls[0][2],
    ).not.toHaveProperty('owner');
    expect(
      mockedWorkflowScheduleApi.create.mock.calls[0][2],
    ).not.toHaveProperty('workflowChatTarget');
    await waitFor(() =>
      expect(
        mockedWorkflowScheduleApi.list.mock.calls.length,
      ).toBeGreaterThanOrEqual(2),
    );
  });

  it('keeps the empty schedule state compact with one create action', async () => {
    renderSurface(true);

    await waitFor(() =>
      expect(
        screen.getByRole('heading', { name: 'No schedules yet' }),
      ).toBeVisible(),
    );

    expect(
      screen.getAllByRole('button', { name: 'New schedule' }),
    ).toHaveLength(1);
    expect(
      document.querySelector('.wa-vnext-schedule-modal'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'No schedules yet' }),
    ).toHaveClass('wa-vnext__schedule-empty-title');
  });
});
