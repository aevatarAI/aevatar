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

function renderSurface(available: boolean, mode: 'modal' | 'panel' = 'modal') {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { gcTime: 0, retry: false },
    },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <WorkflowScheduleSurface
        available={available}
        mode={mode}
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
    mockedWorkflowScheduleApi.preview.mockResolvedValue({
      cronExpression: '0 9 * * 1-5',
      timezone: 'Asia/Shanghai',
      nextFireTimes: [
        '2026-08-21T01:00:00Z',
        '2026-08-24T01:00:00Z',
        '2026-08-25T01:00:00Z',
        '2026-08-26T01:00:00Z',
        '2026-08-27T01:00:00Z',
      ],
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

  it('opens modal creation directly with one design-aligned configure surface', async () => {
    renderSurface(true);

    await waitFor(() =>
      expect(
        screen.getByText('New schedule', { selector: 'h2' }),
      ).toBeVisible(),
    );
    expect(screen.getByText('Workflow')).toBeVisible();
    expect(screen.getByText('Weekly review')).toBeVisible();
    expect(screen.getByText('How often')).toBeVisible();
    expect(screen.getByText('What it needs')).toBeVisible();
    expect(screen.getByText('What will happen')).toBeVisible();
    expect(
      screen.getByRole('button', { name: 'Review schedule' }),
    ).toBeVisible();
    expect(screen.queryByText('Schedules')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Refresh schedules' }),
    ).not.toBeInTheDocument();
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
  });

  it('previews five server fire times before creating and keeps accepted state visible', async () => {
    renderSurface(true);

    fireEvent.change(screen.getByRole('textbox', { name: 'Schedule name' }), {
      target: { value: 'Daily workflow run' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Review schedule' }));

    await waitFor(() =>
      expect(workflowScheduleApi.preview).toHaveBeenCalledWith(
        'scope-alpha',
        'wf-alpha',
        expect.objectContaining({
          count: 5,
          cronExpression: '0 9 * * 1-5',
          timezone: expect.any(String),
        }),
      ),
    );
    await waitFor(() =>
      expect(
        screen.getByText('Review schedule', { selector: 'h2' }),
      ).toBeVisible(),
    );
    expect(screen.getByText('Daily workflow run')).toBeVisible();
    expect(screen.getByText('Weekdays at 09:00')).toBeVisible();
    expect(screen.getByText('Enabled after creation')).toBeVisible();
    for (const fireAt of [
      '2026-08-21T01:00:00Z',
      '2026-08-24T01:00:00Z',
      '2026-08-25T01:00:00Z',
      '2026-08-26T01:00:00Z',
      '2026-08-27T01:00:00Z',
    ]) {
      expect(
        document.querySelector(`time[datetime="${fireAt}"]`),
      ).toBeVisible();
    }

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
    expect(screen.getByText('Schedule request accepted')).toBeVisible();
    expect(screen.getByText('Refreshing Workflow schedules')).toBeVisible();
    expect(mockToast.success).not.toHaveBeenCalled();
  });

  it('keeps the empty schedule state compact with one create action', async () => {
    renderSurface(true, 'panel');

    await waitFor(() =>
      expect(
        screen.getByRole('heading', { name: 'No schedules yet' }),
      ).toBeVisible(),
    );

    expect(
      screen.getAllByRole('button', { name: 'New schedule' }),
    ).toHaveLength(1);
    expect(
      document.querySelector('.wa-vnext-schedule-drawer'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'No schedules yet' }),
    ).toHaveClass('wa-vnext__schedule-empty-title');
  });
});
