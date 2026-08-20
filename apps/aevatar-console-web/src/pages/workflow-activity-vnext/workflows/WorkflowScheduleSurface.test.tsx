import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
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

function renderSurface(
  available: boolean,
  mode: 'modal' | 'panel' = 'modal',
  onClose = jest.fn(),
  initialView?: 'form' | 'list',
) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { gcTime: 0, retry: false },
    },
  });
  return {
    ...render(
      <QueryClientProvider client={queryClient}>
        <WorkflowScheduleSurface
          available={available}
          initialView={initialView}
          mode={mode}
          onClose={onClose}
          open
          scopeId="scope-alpha"
          workflowId="wf-alpha"
          workflowName="Weekly review"
        />
      </QueryClientProvider>,
    ),
    onClose,
  };
}

function findScheduleOption(label: string): HTMLElement {
  const option = Array.from(
    document.querySelectorAll<HTMLElement>('.ant-select-item-option'),
  ).find((element) => element.textContent?.trim() === label);
  if (!option) throw new Error(`Schedule option not found: ${label}`);
  return option;
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

    await waitFor(() => expect(screen.getByText('New schedule')).toBeVisible());
    expect(screen.getByText('Weekly review · Published')).toBeVisible();
    expect(screen.getByText('How often')).toBeVisible();
    expect(screen.getByText('Run input (optional)')).toBeVisible();
    expect(screen.getByText('Enabled after creation')).toBeVisible();
    expect(screen.queryByText('WORKFLOW SCHEDULE')).not.toBeInTheDocument();
    expect(screen.queryByText('What it needs')).not.toBeInTheDocument();
    expect(screen.queryByText('What will happen')).not.toBeInTheDocument();
    expect(
      screen.queryByText('Previewed by the server'),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Review schedule' }),
    ).toBeVisible();
    expect(screen.queryByText('Schedules')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Refresh schedules' }),
    ).not.toBeInTheDocument();
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
  });

  it('previews five fire times, closes the modal, and shows a success toast', async () => {
    const onClose = jest.fn();
    renderSurface(true, 'modal', onClose);

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
        screen.getByText('Review schedule', { selector: '.ant-modal-title' }),
      ).toBeVisible(),
    );
    expect(
      screen.queryByRole('heading', { name: 'Review schedule' }),
    ).not.toBeInTheDocument();
    expect(screen.getByText('Daily workflow run')).toBeVisible();
    expect(screen.getByText('Weekdays at 09:00')).toBeVisible();
    expect(screen.getByText('Run input')).toBeVisible();
    expect(screen.getByText('No prompt')).toBeVisible();
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
    expect(mockToast.success).toHaveBeenCalledWith(
      'Schedule request accepted. It will appear in the list shortly.',
    );
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByText('Refreshing Workflow schedules'),
    ).not.toBeInTheDocument();
  });

  it('returns the editor panel to the list while schedule refresh continues', async () => {
    let listCallCount = 0;
    let resolveRefresh: (value: {
      items: never[];
      nextCursor: null;
      totalCount: number;
    }) => void = () => undefined;
    const deferredRefresh = new Promise<{
      items: never[];
      nextCursor: null;
      totalCount: number;
    }>((resolve) => {
      resolveRefresh = resolve;
    });
    mockedWorkflowScheduleApi.list.mockImplementation(() => {
      listCallCount += 1;
      return listCallCount === 1
        ? Promise.resolve({ items: [], nextCursor: null, totalCount: 0 })
        : deferredRefresh;
    });
    const view = renderSurface(true, 'panel');
    await waitFor(() =>
      expect(
        screen.getByRole('heading', { name: 'No schedules yet' }),
      ).toBeVisible(),
    );
    fireEvent.click(screen.getByRole('button', { name: 'New schedule' }));

    fireEvent.change(screen.getByRole('textbox', { name: 'Schedule name' }), {
      target: { value: 'Deferred refresh schedule' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Review schedule' }));
    await waitFor(() =>
      expect(
        screen.getByText('Review schedule', { selector: '.ant-drawer-title' }),
      ).toBeVisible(),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Create schedule' }));
    await waitFor(() =>
      expect(mockToast.success).toHaveBeenCalledWith(
        'Schedule request accepted. It will appear in the list shortly.',
      ),
    );
    expect(
      screen.getByText('Weekly review', { selector: '.ant-drawer-title' }),
    ).toBeVisible();
    await waitFor(() => expect(listCallCount).toBeGreaterThanOrEqual(2));
    resolveRefresh({ items: [], nextCursor: null, totalCount: 0 });
    view.unmount();
  });

  it('keeps refreshing until the accepted schedule is observed', async () => {
    let listCallCount = 0;
    const observedSchedule = {
      scheduleId: 'schedule-alpha',
      displayName: 'Observed schedule',
      prompt: '',
      cronExpression: '0 9 * * 1-5',
      timezone: 'Asia/Shanghai',
      enabled: true,
      createdAt: '2026-08-20T00:00:00Z',
      updatedAt: '2026-08-20T00:00:00Z',
      nextFireAt: null,
      lastFireAt: null,
      fireCount: 0,
      failureCount: 0,
    };
    mockedWorkflowScheduleApi.list.mockImplementation(() => {
      listCallCount += 1;
      return Promise.resolve({
        items: listCallCount >= 3 ? [observedSchedule] : [],
        nextCursor: null,
        totalCount: listCallCount >= 3 ? 1 : 0,
      });
    });
    const view = renderSurface(true, 'panel');

    await waitFor(() =>
      expect(
        screen.getByRole('heading', { name: 'No schedules yet' }),
      ).toBeVisible(),
    );
    fireEvent.click(screen.getByRole('button', { name: 'New schedule' }));

    fireEvent.change(screen.getByRole('textbox', { name: 'Schedule name' }), {
      target: { value: 'Observed schedule' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Review schedule' }));
    await waitFor(() =>
      expect(
        screen.getByText('Review schedule', { selector: '.ant-drawer-title' }),
      ).toBeVisible(),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Create schedule' }));

    await waitFor(() => expect(listCallCount).toBeGreaterThanOrEqual(2));
    expect(mockToast.success).toHaveBeenCalledWith(
      'Schedule request accepted. It will appear in the list shortly.',
    );

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 1100));
    });
    await waitFor(() => expect(listCallCount).toBeGreaterThanOrEqual(3));

    const observedCallCount = listCallCount;
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 1100));
    });
    expect(listCallCount).toBe(observedCallCount);
    view.unmount();
  });

  it('synchronizes a custom weekly cron back to the repeat builder before review', async () => {
    renderSurface(true);

    fireEvent.change(screen.getByRole('textbox', { name: 'Schedule name' }), {
      target: { value: 'Cron toggle schedule' },
    });
    fireEvent.click(
      screen.getByRole('button', { name: 'write it as cron instead' }),
    );
    fireEvent.change(screen.getByRole('textbox', { name: 'Cron expression' }), {
      target: { value: '15 14 * * 2' },
    });
    fireEvent.click(
      screen.getByRole('button', { name: 'use the repeat builder' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Review schedule' }));

    await waitFor(() =>
      expect(workflowScheduleApi.preview).toHaveBeenCalledWith(
        'scope-alpha',
        'wf-alpha',
        expect.objectContaining({ cronExpression: '15 14 * * 2' }),
      ),
    );
    expect(screen.getAllByRole('definition')[2]).toHaveTextContent(
      'Every Tuesday at 14:15',
    );
    fireEvent.click(screen.getByRole('button', { name: 'Create schedule' }));
    await waitFor(() =>
      expect(workflowScheduleApi.create).toHaveBeenCalledWith(
        'scope-alpha',
        'wf-alpha',
        expect.objectContaining({ cronExpression: '15 14 * * 2' }),
      ),
    );
  });

  it('renders only the controls for the active cadence mode', () => {
    renderSurface(true);

    expect(
      screen.getByRole('combobox', { name: 'Repeat' }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Time')).toBeInTheDocument();
    expect(
      screen.queryByRole('textbox', { name: 'Cron expression' }),
    ).not.toBeInTheDocument();

    fireEvent.click(
      screen.getByRole('button', { name: 'write it as cron instead' }),
    );

    expect(
      screen.queryByRole('combobox', { name: 'Repeat' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Time')).not.toBeInTheDocument();
    expect(
      screen.getByRole('textbox', { name: 'Cron expression' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('textbox', { name: 'Timezone' }),
    ).toBeInTheDocument();

    fireEvent.click(
      screen.getByRole('button', { name: 'use the repeat builder' }),
    );

    expect(
      screen.getByRole('combobox', { name: 'Repeat' }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Time')).toBeInTheDocument();
    expect(
      screen.queryByRole('textbox', { name: 'Cron expression' }),
    ).not.toBeInTheDocument();
  });

  it('offers hourly, daily, weekday, weekly, and monthly repeat presets', async () => {
    renderSurface(true);

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Repeat' }));

    for (const option of [
      'Every hour',
      'Every day',
      'Weekdays',
      'Every week',
      'Every month',
    ]) {
      await waitFor(() =>
        expect(findScheduleOption(option)).toBeInTheDocument(),
      );
    }
  });

  it('builds a weekly cron from the selected day', async () => {
    renderSurface(true);

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Repeat' }));
    fireEvent.click(screen.getByText('Every week'));
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Day of week' }));
    fireEvent.click(screen.getByText('Wednesday'));
    fireEvent.change(screen.getByRole('textbox', { name: 'Schedule name' }), {
      target: { value: 'Weekly workflow run' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Review schedule' }));

    await waitFor(() =>
      expect(workflowScheduleApi.preview).toHaveBeenCalledWith(
        'scope-alpha',
        'wf-alpha',
        expect.objectContaining({ cronExpression: '0 9 * * 3' }),
      ),
    );
    await waitFor(() =>
      expect(
        screen.getByText('Review schedule', { selector: '.ant-modal-title' }),
      ).toBeVisible(),
    );
    expect(screen.getAllByRole('definition')[2]).toHaveTextContent(
      'Every Wednesday at 09:00',
    );
  });

  it('builds a monthly cron from the selected day and fixes hourly cadence', async () => {
    renderSurface(true);

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Repeat' }));
    fireEvent.click(findScheduleOption('Every month'));
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Day of month' }));
    fireEvent.click(findScheduleOption('5'));
    fireEvent.change(screen.getByRole('textbox', { name: 'Schedule name' }), {
      target: { value: 'Monthly workflow run' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Review schedule' }));

    await waitFor(() =>
      expect(workflowScheduleApi.preview).toHaveBeenCalledWith(
        'scope-alpha',
        'wf-alpha',
        expect.objectContaining({ cronExpression: '0 9 5 * *' }),
      ),
    );
    await waitFor(() =>
      expect(
        screen.getByText('Review schedule', { selector: '.ant-modal-title' }),
      ).toBeVisible(),
    );
    expect(screen.getAllByRole('definition')[2]).toHaveTextContent(
      'Every month on day 5 at 09:00',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Repeat' }));
    fireEvent.click(findScheduleOption('Every hour'));
    await waitFor(() => expect(screen.getByLabelText('Time')).toBeDisabled());
    fireEvent.change(screen.getByRole('textbox', { name: 'Schedule name' }), {
      target: { value: 'Hourly workflow run' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Review schedule' }));

    await waitFor(() =>
      expect(workflowScheduleApi.preview).toHaveBeenLastCalledWith(
        'scope-alpha',
        'wf-alpha',
        expect.objectContaining({ cronExpression: '0 * * * *' }),
      ),
    );
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

  it('opens the modal list with existing schedules ready to edit', async () => {
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [
        {
          scheduleId: 'schedule-alpha',
          displayName: 'Daily workflow run',
          prompt: '',
          cronExpression: '0 9 * * 1-5',
          timezone: 'Asia/Shanghai',
          enabled: true,
          createdAt: '2026-08-20T00:00:00Z',
          updatedAt: '2026-08-20T00:00:00Z',
          nextFireAt: '2026-08-21T01:00:00Z',
          lastFireAt: null,
          fireCount: 0,
          failureCount: 0,
        },
      ],
      nextCursor: null,
      totalCount: 1,
    });

    renderSurface(true, 'modal', jest.fn(), 'list');

    await waitFor(() =>
      expect(screen.getByText('Daily workflow run')).toBeVisible(),
    );
    expect(
      screen.getByText('Schedules', { selector: '.ant-modal-title' }),
    ).toBeVisible();
    expect(
      screen.getByRole('button', { name: 'Edit Daily workflow run' }),
    ).toBeVisible();
    expect(screen.getByRole('button', { name: 'New schedule' })).toBeVisible();

    fireEvent.click(
      screen.getByRole('button', { name: 'Edit Daily workflow run' }),
    );
    expect(screen.getByText('Edit schedule')).toBeVisible();
    expect(screen.getByRole('textbox', { name: 'Schedule name' })).toHaveValue(
      'Daily workflow run',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByText('Daily workflow run')).toBeVisible();
    expect(
      screen.getByText('Schedules', { selector: '.ant-modal-title' }),
    ).toBeVisible();
  });
});
