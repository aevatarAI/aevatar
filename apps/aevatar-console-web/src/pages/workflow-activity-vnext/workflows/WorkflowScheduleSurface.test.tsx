import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { workflowScheduleApi } from '@/shared/api/workflowScheduleApi';
import { workflowActivityVNextCss } from '../styles';
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

function createScheduleSummary(overrides: Record<string, unknown> = {}) {
  return {
    scheduleId: 'schedule-alpha',
    displayName: 'Daily workflow run',
    prompt: '',
    cronExpression: '0 9 * * 1-5',
    timezone: 'Asia/Shanghai',
    enabled: true,
    createdAt: '2026-08-18T00:00:00Z',
    updatedAt: '2026-08-20T01:02:00Z',
    nextFireAt: '2026-08-21T01:00:00Z',
    lastFireAt: '2026-08-20T01:00:00Z',
    fireCount: 12,
    failureCount: 1,
    ...overrides,
  };
}

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

  it('allocates four Schedule history columns with a whole-row link affordance', () => {
    const schedulePortalTokens = workflowActivityVNextCss.match(
      /\.wa-vnext-schedule-modal, \.wa-vnext-schedule-drawer \{([^}]*)\}/,
    )?.[1];

    expect(schedulePortalTokens).toContain('--wa-blue-bg: #eff8ff;');
    expect(schedulePortalTokens).toContain('--wa-red: #b42318;');
    expect(schedulePortalTokens).toContain('--wa-red-bg: #fef3f2;');
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__schedule-history-table th:nth-child(1), .wa-vnext__schedule-history-table td:nth-child(1) { width: 28%; }',
    );
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__schedule-history-table th:nth-child(2), .wa-vnext__schedule-history-table td:nth-child(2) { width: 14%; }',
    );
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__schedule-history-table th:nth-child(3), .wa-vnext__schedule-history-table td:nth-child(3) { width: 28%; }',
    );
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__schedule-history-table th:nth-child(4), .wa-vnext__schedule-history-table td:nth-child(4) { width: 30%; }',
    );
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__schedule-history-row--linked:hover td { background: var(--wa-blue-bg); }',
    );
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__schedule-history-row-link { border-radius: var(--wa-radius); inset: 0; position: absolute; z-index: 1; }',
    );
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

  it('keeps committed Schedule rows visible while the list refreshes', async () => {
    const schedule = createScheduleSummary();
    let listCallCount = 0;
    let resolveRefresh: (value: {
      items: ReturnType<typeof createScheduleSummary>[];
      nextCursor: null;
      totalCount: number;
    }) => void = () => undefined;
    const deferredRefresh = new Promise<{
      items: ReturnType<typeof createScheduleSummary>[];
      nextCursor: null;
      totalCount: number;
    }>((resolve) => {
      resolveRefresh = resolve;
    });
    mockedWorkflowScheduleApi.list.mockImplementation(() => {
      listCallCount += 1;
      return listCallCount === 1
        ? Promise.resolve({
            items: [schedule],
            nextCursor: null,
            totalCount: 1,
          })
        : deferredRefresh;
    });

    renderSurface(true, 'modal', jest.fn(), 'list');

    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'View Daily workflow run' }),
      ).toBeVisible(),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Refresh schedules' }));

    const scheduleRegion = await screen.findByRole('region', {
      name: 'Schedules',
    });
    await waitFor(() =>
      expect(workflowScheduleApi.list).toHaveBeenCalledTimes(2),
    );
    expect(
      screen.getByRole('button', { name: 'Refresh schedules' }),
    ).toBeDisabled();
    expect(scheduleRegion).toHaveAttribute('aria-busy', 'true');
    expect(
      within(scheduleRegion).getByRole('button', {
        name: 'View Daily workflow run',
      }),
    ).toBeVisible();
    const refreshStatus = within(scheduleRegion).getByRole('status', {
      name: 'Refreshing schedules…',
    });
    expect(refreshStatus).toHaveClass('aevatar-loading-overlay');
    expect(refreshStatus.querySelectorAll('.aevatar-loading-dot')).toHaveLength(
      3,
    );
    expect(screen.getByRole('button', { name: 'New schedule' })).toBeEnabled();

    await act(async () => {
      resolveRefresh({
        items: [createScheduleSummary({ displayName: 'Updated workflow run' })],
        nextCursor: null,
        totalCount: 1,
      });
      await deferredRefresh;
    });

    expect(
      await screen.findByRole('button', { name: 'View Updated workflow run' }),
    ).toBeVisible();
    expect(scheduleRegion).toHaveAttribute('aria-busy', 'false');
    expect(
      within(scheduleRegion).queryByRole('status', {
        name: 'Refreshing schedules…',
      }),
    ).not.toBeInTheDocument();
  });

  it('opens a selected Schedule on Overview and returns through the stable header', async () => {
    const schedule = createScheduleSummary();
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [
        {
          scheduledFireAt: '2026-08-20T01:00:00Z',
          completedAt: '2026-08-20T01:02:00Z',
          idempotencyKey: 'schedule-alpha:fire:1',
          runActorId: '',
          error: '',
          manual: false,
        },
        {
          scheduledFireAt: '2026-08-19T03:00:00Z',
          completedAt: '2026-08-19T03:01:00Z',
          idempotencyKey: 'schedule-alpha:manual:1',
          runActorId: '',
          error: 'Workflow invocation failed',
          manual: true,
        },
      ],
    });

    renderSurface(true, 'modal', jest.fn(), 'list');

    await waitFor(() =>
      expect(screen.getByText('Daily workflow run')).toBeVisible(),
    );
    expect(
      screen.getByText('Schedules', { selector: '.ant-modal-title' }),
    ).toBeVisible();
    expect(
      screen.getByRole('button', { name: 'View Daily workflow run' }),
    ).toBeVisible();
    expect(screen.getByRole('button', { name: 'New schedule' })).toBeVisible();

    fireEvent.click(
      screen.getByRole('button', { name: 'View Daily workflow run' }),
    );
    await waitFor(() =>
      expect(workflowScheduleApi.get).toHaveBeenCalledWith(
        'scope-alpha',
        'wf-alpha',
        'schedule-alpha',
      ),
    );
    expect(
      screen.getByText('Daily workflow run', {
        selector: '.ant-modal-title *',
      }),
    ).toBeVisible();
    expect(screen.getByText('Weekly review')).toBeVisible();
    expect(screen.getByRole('tab', { name: 'Overview' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    expect(screen.getByRole('tab', { name: 'History' })).toHaveAttribute(
      'aria-selected',
      'false',
    );
    expect(screen.queryByText('Recent attempts')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Back to schedules' }));
    expect(
      screen.getByText('Schedules', { selector: '.ant-modal-title' }),
    ).toBeVisible();
    expect(
      screen.queryByRole('button', { name: 'Back to schedules' }),
    ).not.toBeInTheDocument();
  });

  it('keeps one selected Schedule header while switching detail tabs', async () => {
    const schedule = createScheduleSummary();
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [],
    });

    renderSurface(true, 'modal', jest.fn(), 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );

    const dialog = screen.getByRole('dialog');
    const overviewHeader = dialog.querySelector(
      '.wa-vnext__schedule-selected-heading',
    );
    await waitFor(() => expect(overviewHeader).toBeVisible());

    fireEvent.click(screen.getByRole('tab', { name: 'History' }));
    expect(
      await screen.findByRole('heading', { name: 'Recent attempts' }),
    ).toBeVisible();

    const historyHeader = dialog.querySelector(
      '.wa-vnext__schedule-selected-heading',
    );
    expect(historyHeader).toBe(overviewHeader);
    expect(historyHeader).toHaveTextContent(
      'Daily workflow run · Weekly review',
    );
    expect(historyHeader).toHaveAccessibleName(
      'Schedule Daily workflow run in workflow Weekly review',
    );
    expect(
      within(dialog).queryByText('Schedule history', {
        selector: '.ant-modal-title *',
      }),
    ).not.toBeInTheDocument();
  });

  it('presents Schedule Overview with primary actions and guarded lifecycle actions', async () => {
    const schedule = createScheduleSummary({
      prompt: 'Summarize new feedback.',
    });
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [
        {
          scheduledFireAt: '2026-08-20T01:00:00Z',
          completedAt: '2026-08-20T01:02:00Z',
          idempotencyKey: 'schedule-alpha:fire:1',
          runActorId: '',
          error: '',
          manual: false,
        },
      ],
    });

    renderSurface(true, 'modal', jest.fn(), 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );

    expect(await screen.findByText('Every weekday at 09:00')).toBeVisible();
    expect(screen.getByText('Enabled')).toBeVisible();
    expect(screen.getByText('Timezone')).toBeVisible();
    expect(screen.getByText('Asia/Shanghai')).toBeVisible();
    expect(screen.getByText('Next scheduled')).toBeVisible();
    expect(screen.getByText('Last attempt')).toBeVisible();
    expect(screen.getByText('Total attempts')).toBeVisible();
    expect(screen.getByText('Failed attempts')).toBeVisible();
    expect(screen.getByText('Run input')).toBeVisible();
    expect(screen.getByText('Summarize new feedback.')).toBeVisible();
    expect(screen.getByText('Advanced details')).toBeVisible();
    expect(screen.getByText('0 9 * * 1-5')).not.toBeVisible();

    expect(screen.getByRole('button', { name: 'Run now' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Edit schedule' })).toBeVisible();
    expect(
      screen.queryByRole('button', { name: 'Pause' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Delete schedule' }),
    ).not.toBeInTheDocument();

    fireEvent.click(
      screen.getByRole('button', { name: 'More schedule actions' }),
    );
    expect(
      await screen.findByRole('menuitem', { name: 'Pause' }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete schedule' }));

    expect(
      await screen.findByText('Delete Daily workflow run?', {
        selector: '.ant-modal-confirm-title',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Daily workflow run will stop running on schedule.'),
    ).toBeInTheDocument();
  });

  it('keeps committed Schedule details visible while an action refreshes them', async () => {
    const schedule = createScheduleSummary();
    let resolveDetailRefresh: (value: {
      schedule: ReturnType<typeof createScheduleSummary>;
      recentFires: never[];
    }) => void = () => undefined;
    const deferredDetailRefresh = new Promise<{
      schedule: ReturnType<typeof createScheduleSummary>;
      recentFires: never[];
    }>((resolve) => {
      resolveDetailRefresh = resolve;
    });
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get
      .mockResolvedValue({
        schedule: createScheduleSummary({ fireCount: 13 }),
        recentFires: [],
      })
      .mockResolvedValueOnce({ schedule, recentFires: [] })
      .mockReturnValueOnce(deferredDetailRefresh);
    mockedWorkflowScheduleApi.runNow.mockResolvedValue({
      scheduleId: schedule.scheduleId,
      accepted: true,
    });

    renderSurface(true, 'modal', jest.fn(), 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );
    expect(await screen.findByText('Every weekday at 09:00')).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Run now' }));

    const detailRegion = await screen.findByRole('region', {
      name: 'Schedule overview',
    });
    await waitFor(() =>
      expect(workflowScheduleApi.get).toHaveBeenCalledTimes(2),
    );
    expect(detailRegion).toHaveAttribute('aria-busy', 'true');
    expect(
      within(detailRegion).getByText('Every weekday at 09:00'),
    ).toBeVisible();
    const refreshStatus = within(detailRegion).getByRole('status', {
      name: 'Refreshing schedule details…',
    });
    expect(refreshStatus).toHaveClass('aevatar-loading-overlay');
    expect(refreshStatus.querySelectorAll('.aevatar-loading-dot')).toHaveLength(
      3,
    );
    expect(screen.getByRole('tab', { name: 'Overview' })).toBeVisible();

    await act(async () => {
      resolveDetailRefresh({
        schedule: createScheduleSummary({ fireCount: 13 }),
        recentFires: [],
      });
      await deferredDetailRefresh;
    });

    await waitFor(() =>
      expect(detailRegion).toHaveAttribute('aria-busy', 'false'),
    );
    expect(
      within(detailRegion).queryByRole('status', {
        name: 'Refreshing schedule details…',
      }),
    ).not.toBeInTheDocument();
  });

  it('keeps Edit as a temporary mode that returns to Overview', async () => {
    const schedule = createScheduleSummary();
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [],
    });

    renderSurface(true, 'modal', jest.fn(), 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Edit schedule' }),
    );

    expect(
      screen.getByText('Daily workflow run', {
        selector: '.ant-modal-title *',
      }),
    ).toBeVisible();
    expect(
      screen.getByRole('heading', { name: 'Edit schedule' }),
    ).toBeVisible();
    expect(
      screen.queryByRole('tab', { name: 'History' }),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByRole('tab', { name: 'Overview' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
  });

  it('keeps History focused on recent attempts and hands actual Runs to Activity', async () => {
    const schedule = createScheduleSummary();
    const onClose = jest.fn();
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [
        {
          scheduledFireAt: '2026-08-20T01:00:00Z',
          completedAt: '2026-08-20T01:02:00Z',
          idempotencyKey: 'schedule-alpha:fire:1',
          runActorId: '',
          error: 'Capability admission rejected the scheduled request.',
          manual: false,
        },
        {
          scheduledFireAt: '2026-08-19T03:00:00Z',
          completedAt: '2026-08-19T03:01:00Z',
          idempotencyKey: 'schedule-alpha:manual:1',
          runActorId: 'run-manual-alpha',
          error: '',
          manual: true,
        },
      ],
    });

    renderSurface(true, 'modal', onClose, 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );
    fireEvent.click(await screen.findByRole('tab', { name: 'History' }));

    expect(
      await screen.findByRole('heading', { name: 'Recent attempts' }),
    ).toBeInTheDocument();
    const dialog = screen.getByRole('dialog');
    await waitFor(() => {
      const historyHeading = dialog.querySelector(
        '.wa-vnext__schedule-selected-heading',
      );
      expect(historyHeading).toBeVisible();
      expect(historyHeading).toHaveTextContent(
        'Daily workflow run · Weekly review',
      );
      expect(historyHeading).toHaveAttribute(
        'aria-label',
        'Schedule Daily workflow run in workflow Weekly review',
      );
    });
    expect(
      within(dialog).queryByText('Schedule: Daily workflow run'),
    ).not.toBeInTheDocument();
    expect(
      within(dialog).queryByText('Workflow: Weekly review'),
    ).not.toBeInTheDocument();
    expect(
      await screen.findByRole('columnheader', { name: 'Scheduled time' }),
    ).toBeVisible();
    expect(screen.getByRole('columnheader', { name: 'Source' })).toBeVisible();
    expect(
      screen.getByRole('columnheader', { name: 'Schedule outcome' }),
    ).toBeVisible();
    expect(
      screen.getByRole('columnheader', { name: 'Completed time' }),
    ).toBeVisible();
    expect(
      screen.queryByRole('columnheader', { name: 'Action' }),
    ).not.toBeInTheDocument();
    expect(screen.getByText('Scheduled')).toBeVisible();
    expect(screen.getByText('Manual')).toBeVisible();
    expect(screen.getByText('Failed to start')).toBeVisible();
    expect(screen.getByText('Run created')).toBeVisible();
    expect(screen.queryByText('Run started')).not.toBeInTheDocument();
    expect(screen.queryByText('Succeeded')).not.toBeInTheDocument();
    expect(
      screen.getByText('The scheduled attempt could not start the Workflow.'),
    ).toBeVisible();
    expect(
      screen.queryByText(
        'Capability admission rejected the scheduled request.',
      ),
    ).not.toBeVisible();
    expect(screen.getByText('Technical details')).toBeInTheDocument();
    expect(screen.queryByText('schedule-alpha:fire:1')).not.toBeInTheDocument();
    const runLink = screen.getByRole('link', {
      name: /Open Run created by the Schedule attempt at/,
    });
    expect(runLink).toBeVisible();
    expect(runLink).toHaveClass('wa-vnext__schedule-history-row-link');
    const runRow = runLink.closest('tr');
    expect(runRow).toHaveClass('wa-vnext__schedule-history-row--linked');
    expect(runRow?.querySelectorAll('td')).toHaveLength(4);
    expect(runRow).toContainElement(screen.getByText('Run created'));
    const completedTime = dialog.querySelector<HTMLTimeElement>(
      'time[datetime="2026-08-19T03:01:00Z"]',
    );
    expect(completedTime).toBeInTheDocument();
    expect(completedTime?.closest('td')?.cellIndex).toBe(3);
    const failedRow = screen
      .getByText('The scheduled attempt could not start the Workflow.')
      .closest('tr');
    expect(failedRow).not.toHaveClass('wa-vnext__schedule-history-row--linked');
    const failedCells = failedRow?.querySelectorAll('td');
    expect(failedCells).toHaveLength(4);
    expect(failedRow?.querySelector('a')).toBeNull();

    fireEvent.click(screen.getByText('Technical details'));
    expect(
      screen.getByText('Capability admission rejected the scheduled request.'),
    ).toBeVisible();

    const relatedRunsLink = screen.getByRole('link', {
      name: 'View related runs in Activity',
    });
    expect(relatedRunsLink).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-alpha&schedule=schedule-alpha',
    );
    expect(relatedRunsLink).toHaveAttribute('target', '_blank');
    expect(relatedRunsLink).toHaveAttribute('rel', 'noopener noreferrer');

    fireEvent.click(relatedRunsLink);

    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('opens an authoritative History attempt directly in Activity', async () => {
    const schedule = createScheduleSummary();
    const onClose = jest.fn();
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [
        {
          scheduledFireAt: '2026-08-20T01:00:00Z',
          completedAt: '2026-08-20T01:02:00Z',
          idempotencyKey: 'schedule-alpha:fire:1',
          runActorId: 'run-alpha',
          error: '',
          manual: false,
        },
      ],
    });

    renderSurface(true, 'modal', onClose, 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );
    fireEvent.click(await screen.findByRole('tab', { name: 'History' }));

    const runLink = await screen.findByRole('link', {
      name: /Open Run created by the Schedule attempt at/,
    });
    expect(runLink).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-alpha?workflowId=wf-alpha&schedule=schedule-alpha',
    );
    expect(runLink).toHaveAttribute('target', '_blank');
    expect(runLink).toHaveAttribute('rel', 'noopener noreferrer');

    fireEvent.click(runLink);

    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('keeps a History attempt without Run identity non-interactive', async () => {
    const schedule = createScheduleSummary();
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [
        {
          scheduledFireAt: '2026-08-20T01:00:00Z',
          completedAt: '2026-08-20T01:02:00Z',
          idempotencyKey: 'schedule-alpha:fire:1',
          runActorId: '',
          error: '',
          manual: false,
        },
      ],
    });

    renderSurface(true, 'modal', jest.fn(), 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );
    fireEvent.click(await screen.findByRole('tab', { name: 'History' }));

    await screen.findByRole('heading', { name: 'Recent attempts' });

    expect(
      screen.queryByRole('link', { name: /View related runs from/ }),
    ).not.toBeInTheDocument();
    const attemptRow = screen.getByText('Accepted').closest('tr');
    expect(attemptRow).not.toBeNull();
    const attemptCells = attemptRow?.querySelectorAll('td');
    expect(attemptCells).toHaveLength(4);
    expect(attemptRow).not.toHaveClass(
      'wa-vnext__schedule-history-row--linked',
    );
    expect(attemptRow?.querySelector('a')).toBeNull();

    const relatedRunsLink = screen.getByRole('link', {
      name: 'View related runs in Activity',
    });
    expect(relatedRunsLink).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-alpha&schedule=schedule-alpha',
    );
    expect(relatedRunsLink).toHaveAttribute('target', '_blank');
    expect(relatedRunsLink).toHaveAttribute('rel', 'noopener noreferrer');
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('keeps multiple attempts scannable with one table row per attempt', async () => {
    const schedule = createScheduleSummary({
      fireCount: 3,
      failureCount: 2,
    });
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [
        {
          scheduledFireAt: '2026-08-21T01:00:00Z',
          completedAt: '2026-08-21T01:01:00Z',
          idempotencyKey: 'schedule-alpha:fire:3',
          runActorId: '',
          error: 'Scheduled request was rejected.',
          manual: false,
        },
        {
          scheduledFireAt: '2026-08-20T01:00:00Z',
          completedAt: '2026-08-20T01:01:00Z',
          idempotencyKey: 'schedule-alpha:fire:2',
          runActorId: '',
          error: '',
          manual: false,
        },
        {
          scheduledFireAt: '2026-08-19T03:00:00Z',
          completedAt: '2026-08-19T03:01:00Z',
          idempotencyKey: 'schedule-alpha:manual:1',
          runActorId: '',
          error: 'Manual request was rejected.',
          manual: true,
        },
      ],
    });

    renderSurface(true, 'modal', jest.fn(), 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );
    fireEvent.click(await screen.findByRole('tab', { name: 'History' }));

    await screen.findByRole('heading', { name: 'Recent attempts' });
    await waitFor(() =>
      expect(
        document.querySelectorAll('.wa-vnext__schedule-history-table tbody tr'),
      ).toHaveLength(3),
    );
    expect(screen.getAllByText('Technical details')).toHaveLength(2);
    expect(
      screen.getByText('The scheduled attempt could not start the Workflow.'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('The manual attempt could not start the Workflow.'),
    ).toBeInTheDocument();
  });

  it('shows a bounded History empty state when no attempts have been recorded', async () => {
    const schedule = createScheduleSummary({
      lastFireAt: null,
      fireCount: 0,
      failureCount: 0,
    });
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get.mockResolvedValue({
      schedule,
      recentFires: [],
    });

    renderSurface(true, 'modal', jest.fn(), 'list');
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Daily workflow run' }),
    );
    fireEvent.click(await screen.findByRole('tab', { name: 'History' }));

    expect(await screen.findByText('No attempts yet')).toBeInTheDocument();
    expect(screen.queryByText('No fires yet')).not.toBeInTheDocument();
  });

  it('keeps a detail failure distinct from an empty recent-fire result', async () => {
    const schedule = createScheduleSummary({
      lastFireAt: null,
      fireCount: 0,
      failureCount: 0,
    });
    mockedWorkflowScheduleApi.list.mockResolvedValue({
      items: [schedule],
      nextCursor: null,
      totalCount: 1,
    });
    mockedWorkflowScheduleApi.get
      .mockRejectedValueOnce(new Error('Schedule detail unavailable'))
      .mockResolvedValueOnce({ schedule, recentFires: [] });

    renderSurface(true, 'modal', jest.fn(), 'list');

    await waitFor(() =>
      expect(screen.getByText('Daily workflow run')).toBeVisible(),
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'View Daily workflow run' }),
    );

    await waitFor(() =>
      expect(screen.getByText("Schedule couldn't be loaded")).toBeVisible(),
    );
    expect(screen.queryByText('No attempts yet')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

    await waitFor(() =>
      expect(workflowScheduleApi.get).toHaveBeenCalledTimes(2),
    );
    fireEvent.click(await screen.findByRole('tab', { name: 'History' }));
    expect(await screen.findByText('No attempts yet')).toBeVisible();
    expect(
      screen.queryByText("Schedule couldn't be loaded"),
    ).not.toBeInTheDocument();
  });
});
