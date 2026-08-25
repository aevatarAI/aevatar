import {
  ArrowLeftOutlined,
  ArrowRightOutlined,
  CalendarOutlined,
  DeleteOutlined,
  DownOutlined,
  EditOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  ReloadOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { getLocale } from '@umijs/max';
import {
  Alert,
  Button,
  Drawer,
  Dropdown,
  Input,
  Modal,
  Select,
  Space,
  Switch,
  Tabs,
  Tag,
} from 'antd';
import React from 'react';
import {
  type WorkflowScheduleConfigurationInput,
  type WorkflowSchedulePreview,
  type WorkflowScheduleSummary,
  workflowScheduleApi,
} from '@/shared/api/workflowScheduleApi';
import { t } from '@/shared/i18n/messages';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import {
  buildWorkflowActivityRunHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';

type WorkflowScheduleSurfaceProps = {
  readonly initialView?: ScheduleSurfaceView;
  readonly mode: 'modal' | 'panel';
  readonly open: boolean;
  readonly onClose: () => void;
  readonly scopeId: string;
  readonly workflowId: string;
  readonly workflowName: string;
  readonly available: boolean;
};

type ScheduleForm = {
  readonly displayName: string;
  readonly cronExpression: string;
  readonly timezone: string;
  readonly enabled: boolean;
  readonly prompt: string;
};

type CreationStep = 'configure' | 'previewing' | 'review';
type ScheduleSurfaceView = 'list' | 'detail' | 'form';
type ScheduleDetailTab = 'overview' | 'history';
type RepeatPreset = 'hourly' | 'daily' | 'weekdays' | 'weekly' | 'monthly';

const weekdayValues = ['1', '2', '3', '4', '5', '6', '0'] as const;
type WeekdayValue = (typeof weekdayValues)[number];

const monthlyDayValues = Array.from({ length: 31 }, (_, index) =>
  String(index + 1),
);

const scheduleQueryKey = (scopeId: string, workflowId: string) => [
  'workflow-activity-vnext',
  'workflow-schedules',
  scopeId,
  workflowId,
];

function defaultTimezone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
}

function cronFromRepeat(
  preset: RepeatPreset,
  time: string,
  weeklyDay: WeekdayValue,
  monthlyDay: string,
): string {
  const [hour = '9', minute = '0'] = time.split(':');
  if (preset === 'hourly') return '0 * * * *';
  if (preset === 'weekdays') {
    return `${Number(minute)} ${Number(hour)} * * 1-5`;
  }
  if (preset === 'weekly') {
    return `${Number(minute)} ${Number(hour)} * * ${weeklyDay}`;
  }
  if (preset === 'monthly') {
    return `${Number(minute)} ${Number(hour)} ${Number(monthlyDay)} * *`;
  }
  return `${Number(minute)} ${Number(hour)} * * *`;
}

function repeatFromCron(cronExpression: string): {
  readonly preset: RepeatPreset;
  readonly time: string;
  readonly weeklyDay: WeekdayValue;
  readonly monthlyDay: string;
} | null {
  const normalized = cronExpression.trim().replace(/\s+/g, ' ');
  if (normalized === '0 * * * *') {
    return {
      preset: 'hourly',
      time: '09:00',
      weeklyDay: '1',
      monthlyDay: '1',
    };
  }
  const match = normalized.match(
    /^(\d{1,2}) (\d{1,2}) (\*|\d{1,2}) (\*|\d{1,2}) (\*|1-5|[0-7])$/,
  );
  if (!match) return null;
  const minute = Number(match[1]);
  const hour = Number(match[2]);
  if (minute > 59 || hour > 23) return null;
  const dayOfMonth = match[3];
  const month = match[4];
  const dayOfWeek = match[5];
  if (month !== '*' || (dayOfMonth !== '*' && dayOfWeek !== '*')) {
    return null;
  }
  if (dayOfWeek === '1-5') {
    return {
      preset: 'weekdays',
      time: `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`,
      weeklyDay: '1',
      monthlyDay: '1',
    };
  }
  if (dayOfMonth !== '*') {
    const parsedDay = Number(dayOfMonth);
    if (parsedDay < 1 || parsedDay > 31 || dayOfWeek !== '*') return null;
    return {
      preset: 'monthly',
      time: `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`,
      weeklyDay: '1',
      monthlyDay: String(parsedDay),
    };
  }
  if (dayOfWeek !== '*') {
    const parsedDay = dayOfWeek === '7' ? '0' : dayOfWeek;
    if (!weekdayValues.includes(parsedDay as WeekdayValue)) return null;
    return {
      preset: 'weekly',
      time: `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`,
      weeklyDay: parsedDay as WeekdayValue,
      monthlyDay: '1',
    };
  }
  return {
    preset: 'daily',
    time: `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`,
    weeklyDay: '1',
    monthlyDay: '1',
  };
}

function emptyForm(): ScheduleForm {
  return {
    displayName: '',
    cronExpression: '0 9 * * 1-5',
    timezone: defaultTimezone(),
    enabled: true,
    prompt: '',
  };
}

function formFromSchedule(schedule: WorkflowScheduleSummary): ScheduleForm {
  return {
    displayName: schedule.displayName,
    cronExpression: schedule.cronExpression,
    timezone: schedule.timezone,
    enabled: schedule.enabled,
    prompt: schedule.prompt,
  };
}

function formatScheduleDate(value: string | null, timezone?: string): string {
  if (!value)
    return t('workflowActivityVNext.common.unavailable', 'Unavailable');
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;

  try {
    return new Intl.DateTimeFormat(getLocale(), {
      dateStyle: 'medium',
      timeStyle: 'short',
      ...(timezone ? { timeZone: timezone } : {}),
    }).format(date);
  } catch {
    return new Intl.DateTimeFormat(getLocale(), {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(date);
  }
}

function scheduleRecurrenceSummary(cronExpression: string): string {
  const repeat = repeatFromCron(cronExpression);
  if (!repeat) {
    return t(
      'workflowActivityVNext.schedule.customRecurrence',
      'Custom schedule',
    );
  }
  if (repeat.preset === 'hourly') {
    return t('workflowActivityVNext.schedule.hourly', 'Every hour');
  }
  if (repeat.preset === 'weekdays') {
    return t(
      'workflowActivityVNext.schedule.everyWeekdaySummary',
      'Every weekday at {time}',
      { time: repeat.time },
    );
  }
  if (repeat.preset === 'weekly') {
    return t(
      'workflowActivityVNext.schedule.weeklySummary',
      'Every {day} at {time}',
      {
        day: t(
          `workflowActivityVNext.schedule.weekday.${repeat.weeklyDay}`,
          repeat.weeklyDay,
        ),
        time: repeat.time,
      },
    );
  }
  if (repeat.preset === 'monthly') {
    return t(
      'workflowActivityVNext.schedule.monthlySummary',
      'Every month on day {day} at {time}',
      { day: repeat.monthlyDay, time: repeat.time },
    );
  }
  return t(
    'workflowActivityVNext.schedule.everyDaySummary',
    'Every day at {time}',
    { time: repeat.time },
  );
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

const WorkflowScheduleSurface: React.FC<WorkflowScheduleSurfaceProps> = ({
  available,
  initialView,
  mode,
  onClose,
  open,
  scopeId,
  workflowId,
  workflowName,
}) => {
  const toast = useConsoleToast();
  const queryClient = useQueryClient();
  const queryKey = React.useMemo(
    () => scheduleQueryKey(scopeId, workflowId),
    [scopeId, workflowId],
  );
  const [surfaceView, setSurfaceView] = React.useState<ScheduleSurfaceView>(
    initialView ?? (mode === 'modal' ? 'form' : 'list'),
  );
  const [creationStep, setCreationStep] =
    React.useState<CreationStep>('configure');
  const [editingSchedule, setEditingSchedule] =
    React.useState<WorkflowScheduleSummary | null>(null);
  const [selectedSchedule, setSelectedSchedule] =
    React.useState<WorkflowScheduleSummary | null>(null);
  const [detailTab, setDetailTab] =
    React.useState<ScheduleDetailTab>('overview');
  const [form, setForm] = React.useState<ScheduleForm>(() => emptyForm());
  const [repeatPreset, setRepeatPreset] =
    React.useState<RepeatPreset>('weekdays');
  const [repeatTime, setRepeatTime] = React.useState('09:00');
  const [weeklyDay, setWeeklyDay] = React.useState<WeekdayValue>('1');
  const [monthlyDay, setMonthlyDay] = React.useState('1');
  const [cronMode, setCronMode] = React.useState(false);
  const [preview, setPreview] = React.useState<WorkflowSchedulePreview | null>(
    null,
  );
  const [saving, setSaving] = React.useState(false);
  const [actionScheduleId, setActionScheduleId] = React.useState<string | null>(
    null,
  );
  const [pendingObservationScheduleId, setPendingObservationScheduleId] =
    React.useState<string | null>(null);

  const previewing = creationStep === 'previewing';
  const busy = previewing || saving;
  const formValid = Boolean(
    form.displayName.trim() &&
      form.cronExpression.trim() &&
      form.timezone.trim(),
  );

  const schedules = useQuery({
    enabled: open && available,
    queryKey,
    queryFn: () =>
      workflowScheduleApi.list(scopeId, workflowId, {
        includeTotalCount: true,
        take: 50,
      }),
    refetchInterval: pendingObservationScheduleId ? 1000 : false,
    refetchOnMount: 'always',
    retry: false,
  });

  const scheduleDetail = useQuery({
    enabled: open && available && Boolean(selectedSchedule),
    queryKey: [...queryKey, 'detail', selectedSchedule?.scheduleId],
    queryFn: () => {
      if (!selectedSchedule) {
        throw new Error('A Schedule must be selected.');
      }
      return workflowScheduleApi.get(
        scopeId,
        workflowId,
        selectedSchedule.scheduleId,
      );
    },
    retry: false,
  });

  React.useEffect(() => {
    if (!open) return;
    setSurfaceView(initialView ?? (mode === 'modal' ? 'form' : 'list'));
    setCreationStep('configure');
    setEditingSchedule(null);
    setSelectedSchedule(null);
    setDetailTab('overview');
    setForm(emptyForm());
    setRepeatPreset('weekdays');
    setRepeatTime('09:00');
    setWeeklyDay('1');
    setMonthlyDay('1');
    setCronMode(false);
    setPreview(null);
    setPendingObservationScheduleId(null);
  }, [initialView, mode, open, workflowId]);

  React.useEffect(() => {
    if (!pendingObservationScheduleId || !schedules.data) return;
    if (
      schedules.data.items.some(
        (schedule) => schedule.scheduleId === pendingObservationScheduleId,
      )
    ) {
      setPendingObservationScheduleId(null);
    }
  }, [pendingObservationScheduleId, schedules.data]);

  const refreshSchedules = React.useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey });
    await queryClient.refetchQueries({ queryKey, type: 'active' });
  }, [queryClient, queryKey]);

  const openCreate = () => {
    setEditingSchedule(null);
    setSelectedSchedule(null);
    setForm(emptyForm());
    setRepeatPreset('weekdays');
    setRepeatTime('09:00');
    setWeeklyDay('1');
    setMonthlyDay('1');
    setCronMode(false);
    setPreview(null);
    setCreationStep('configure');
    setSurfaceView('form');
  };

  const openDetail = (schedule: WorkflowScheduleSummary) => {
    setSelectedSchedule(schedule);
    setEditingSchedule(null);
    setDetailTab('overview');
    setSurfaceView('detail');
  };

  const openEdit = () => {
    const schedule = scheduleDetail.data?.schedule;
    if (!schedule) return;
    const repeat = repeatFromCron(schedule.cronExpression);
    setEditingSchedule(schedule);
    setForm(formFromSchedule(schedule));
    setRepeatPreset(repeat?.preset ?? 'weekdays');
    setRepeatTime(repeat?.time ?? '09:00');
    setWeeklyDay(repeat?.weeklyDay ?? '1');
    setMonthlyDay(repeat?.monthlyDay ?? '1');
    setCronMode(!repeat);
    setPreview(null);
    setCreationStep('configure');
    setSurfaceView('form');
  };

  const leaveForm = () => {
    if (busy) return;
    if (editingSchedule && selectedSchedule) {
      setEditingSchedule(null);
      setCreationStep('configure');
      setPreview(null);
      setSurfaceView('detail');
      return;
    }
    if (mode === 'modal' && initialView !== 'list') {
      onClose();
      return;
    }
    setSurfaceView('list');
    setEditingSchedule(null);
    setCreationStep('configure');
    setPreview(null);
  };

  const updateHumanRepeat = (
    nextPreset: RepeatPreset,
    nextTime: string,
    nextWeeklyDay = weeklyDay,
    nextMonthlyDay = monthlyDay,
  ) => {
    setRepeatPreset(nextPreset);
    setRepeatTime(nextTime);
    setWeeklyDay(nextWeeklyDay);
    setMonthlyDay(nextMonthlyDay);
    setPreview(null);
    setForm((current) => ({
      ...current,
      cronExpression: cronFromRepeat(
        nextPreset,
        nextTime,
        nextWeeklyDay,
        nextMonthlyDay,
      ),
    }));
  };

  const previewSchedule = async () => {
    if (!formValid || previewing) return;
    setCreationStep('previewing');
    try {
      const result = await workflowScheduleApi.preview(scopeId, workflowId, {
        cronExpression: form.cronExpression,
        timezone: form.timezone,
        count: 5,
      });
      setPreview(result);
      setCreationStep('review');
    } catch (error) {
      setCreationStep('configure');
      toast.error(errorMessage(error));
    }
  };

  const submitForm = async () => {
    if (saving || !formValid) return;
    setSaving(true);
    const input: WorkflowScheduleConfigurationInput = {
      displayName: form.displayName,
      cronExpression: form.cronExpression,
      timezone: form.timezone,
      enabled: editingSchedule ? editingSchedule.enabled : form.enabled,
      prompt: form.prompt,
    };
    try {
      if (editingSchedule) {
        await workflowScheduleApi.update(
          scopeId,
          workflowId,
          editingSchedule.scheduleId,
          input,
        );
        toast.success(
          t(
            'workflowActivityVNext.schedule.updateAccepted',
            'Schedule update accepted.',
          ),
        );
        setSurfaceView('detail');
        setEditingSchedule(null);
        await refreshSchedules();
      } else {
        const receipt = await workflowScheduleApi.create(
          scopeId,
          workflowId,
          input,
        );
        setPendingObservationScheduleId(receipt.scheduleId);
        setCreationStep('configure');
        setPreview(null);
        setEditingSchedule(null);
        setSurfaceView(mode === 'modal' ? 'form' : 'list');
        toast.success(
          t(
            'workflowActivityVNext.schedule.created',
            'Schedule request accepted. It will appear in the list shortly.',
          ),
        );
        if (mode === 'modal') onClose();
        void refreshSchedules().catch((error) => {
          toast.error(errorMessage(error));
        });
      }
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  const changeState = async (
    schedule: WorkflowScheduleSummary,
    action: 'disable' | 'enable' | 'runNow',
  ) => {
    if (actionScheduleId) return;
    setActionScheduleId(schedule.scheduleId);
    try {
      if (action === 'enable')
        await workflowScheduleApi.enable(
          scopeId,
          workflowId,
          schedule.scheduleId,
        );
      if (action === 'disable')
        await workflowScheduleApi.disable(
          scopeId,
          workflowId,
          schedule.scheduleId,
        );
      if (action === 'runNow')
        await workflowScheduleApi.runNow(
          scopeId,
          workflowId,
          schedule.scheduleId,
        );
      toast.success(
        t(
          'workflowActivityVNext.schedule.actionAccepted',
          'Schedule action accepted.',
        ),
      );
      await refreshSchedules();
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setActionScheduleId(null);
    }
  };

  const deleteSchedule = async (schedule: WorkflowScheduleSummary) => {
    if (actionScheduleId) return;
    setActionScheduleId(schedule.scheduleId);
    try {
      await workflowScheduleApi.delete(
        scopeId,
        workflowId,
        schedule.scheduleId,
      );
      toast.success(
        t(
          'workflowActivityVNext.schedule.deleteAccepted',
          'Schedule deletion accepted.',
        ),
      );
      await refreshSchedules();
      setSelectedSchedule(null);
      setSurfaceView('list');
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setActionScheduleId(null);
    }
  };

  const confirmDeleteSchedule = (schedule: WorkflowScheduleSummary) => {
    Modal.confirm({
      cancelText: t(
        'workflowActivityVNext.schedule.keepSchedule',
        'Keep schedule',
      ),
      content: t(
        'workflowActivityVNext.schedule.deleteConfirmDescription',
        '{name} will stop running on schedule.',
        { name: schedule.displayName },
      ),
      okButtonProps: { danger: true },
      okText: t(
        'workflowActivityVNext.schedule.deleteAction',
        'Delete schedule',
      ),
      onOk: () => deleteSchedule(schedule),
      focusable: { autoFocusButton: 'cancel' },
      title: t(
        'workflowActivityVNext.schedule.deleteConfirmTitle',
        'Delete {name}?',
        { name: schedule.displayName },
      ),
    });
  };

  const weekdayLabel = (value: WeekdayValue) =>
    t(`workflowActivityVNext.schedule.weekday.${value}`, value);

  const recurrenceSummary = cronMode
    ? t('workflowActivityVNext.schedule.cronSummary', 'Cron: {cron}', {
        cron: form.cronExpression,
      })
    : repeatPreset === 'hourly'
      ? t('workflowActivityVNext.schedule.hourly', 'Every hour')
      : repeatPreset === 'weekly'
        ? t(
            'workflowActivityVNext.schedule.weeklySummary',
            'Every {day} at {time}',
            { day: weekdayLabel(weeklyDay), time: repeatTime },
          )
        : repeatPreset === 'monthly'
          ? t(
              'workflowActivityVNext.schedule.monthlySummary',
              'Every month on day {day} at {time}',
              { day: monthlyDay, time: repeatTime },
            )
          : t(
              'workflowActivityVNext.schedule.repeatSummary',
              '{repeat} at {time}',
              {
                repeat:
                  repeatPreset === 'weekdays'
                    ? t('workflowActivityVNext.schedule.weekdays', 'Weekdays')
                    : t('workflowActivityVNext.schedule.daily', 'Every day'),
                time: repeatTime,
              },
            );

  const toggleCronMode = () => {
    setPreview(null);
    if (cronMode) {
      const repeat = repeatFromCron(form.cronExpression);
      if (repeat) {
        setRepeatPreset(repeat.preset);
        setRepeatTime(repeat.time);
        setWeeklyDay(repeat.weeklyDay);
        setMonthlyDay(repeat.monthlyDay);
      }
      setForm((current) => ({
        ...current,
        cronExpression: cronFromRepeat(
          repeat?.preset ?? repeatPreset,
          repeat?.time ?? repeatTime,
          repeat?.weeklyDay ?? weeklyDay,
          repeat?.monthlyDay ?? monthlyDay,
        ),
      }));
    }
    setCronMode((current) => !current);
  };

  const returnToScheduleList = () => {
    setSelectedSchedule(null);
    setEditingSchedule(null);
    setDetailTab('overview');
    setSurfaceView('list');
  };

  const showingHistory =
    surfaceView === 'detail' && detailTab === 'history' && selectedSchedule;
  const selectedScheduleTitle = selectedSchedule ? (
    <div className="wa-vnext__schedule-selected-title">
      <Button
        aria-label={t(
          'workflowActivityVNext.schedule.backToSchedules',
          'Back to schedules',
        )}
        icon={<ArrowLeftOutlined />}
        onClick={returnToScheduleList}
        type="text"
      />
      {showingHistory ? (
        <h2
          aria-label={t(
            'workflowActivityVNext.schedule.historyContextAria',
            'Schedule history for schedule {scheduleName} in workflow {workflowName}',
            {
              scheduleName: selectedSchedule.displayName,
              workflowName,
            },
          )}
          className="wa-vnext__schedule-selected-heading wa-vnext__schedule-selected-heading--history"
        >
          <strong>
            {t(
              'workflowActivityVNext.schedule.historyTitle',
              'Schedule history',
            )}
          </strong>
          <span aria-hidden="true"> · </span>
          <span
            className="wa-vnext__schedule-selected-heading-context"
            title={selectedSchedule.displayName}
          >
            {selectedSchedule.displayName}
          </span>
          <span aria-hidden="true"> · </span>
          <span
            className="wa-vnext__schedule-selected-heading-context"
            title={workflowName}
          >
            {workflowName}
          </span>
        </h2>
      ) : (
        <div className="wa-vnext__schedule-selected-heading">
          <strong>{selectedSchedule.displayName}</strong>
          <span>{workflowName}</span>
        </div>
      )}
    </div>
  ) : null;

  const surfaceTitle =
    selectedScheduleTitle ??
    (surfaceView === 'list'
      ? mode === 'modal'
        ? t('workflowActivityVNext.schedule.title', 'Schedules')
        : workflowName
      : creationStep === 'review'
        ? t('workflowActivityVNext.schedule.reviewTitle', 'Review schedule')
        : t('workflowActivityVNext.schedule.new', 'New schedule'));

  const workflowContext = (
    <div className="wa-vnext__schedule-context">
      <strong>
        {workflowName} ·{' '}
        {t('workflowActivityVNext.schedule.published', 'Published')}
      </strong>
      {editingSchedule ? (
        <Tag color={editingSchedule.enabled ? 'green' : 'default'}>
          {editingSchedule.enabled
            ? t('workflowActivityVNext.schedule.enabled', 'Enabled')
            : t('workflowActivityVNext.schedule.disabled', 'Disabled')}
        </Tag>
      ) : null}
    </div>
  );

  const timezoneField = (
    <label className="wa-vnext__modal-field" htmlFor="schedule-timezone">
      <span>{t('workflowActivityVNext.schedule.timezone', 'Timezone')}</span>
      <Input
        aria-label={t('workflowActivityVNext.schedule.timezone', 'Timezone')}
        id="schedule-timezone"
        onChange={(event) => {
          setPreview(null);
          setForm((current) => ({
            ...current,
            timezone: event.target.value,
          }));
        }}
        value={form.timezone}
      />
    </label>
  );

  const formFields = (
    <>
      {editingSchedule ? (
        <h2 className="wa-vnext__schedule-form-title">
          {t('workflowActivityVNext.schedule.editTitle', 'Edit schedule')}
        </h2>
      ) : (
        workflowContext
      )}
      <section className="wa-vnext__schedule-section">
        <label
          className="wa-vnext__modal-field"
          htmlFor="workflow-schedule-name"
        >
          <span>
            {t('workflowActivityVNext.schedule.scheduleName', 'Schedule name')}
          </span>
          <Input
            aria-label={t(
              'workflowActivityVNext.schedule.scheduleName',
              'Schedule name',
            )}
            id="workflow-schedule-name"
            onChange={(event) =>
              setForm((current) => ({
                ...current,
                displayName: event.target.value,
              }))
            }
            value={form.displayName}
          />
        </label>
      </section>
      <section className="wa-vnext__schedule-section">
        <h3>{t('workflowActivityVNext.schedule.howOften', 'How often')}</h3>
        {!cronMode ? (
          <div className="wa-vnext__schedule-repeat-grid">
            <label className="wa-vnext__modal-field" htmlFor="schedule-repeat">
              <span>
                {t('workflowActivityVNext.schedule.repeat', 'Repeat')}
              </span>
              <Select
                aria-label={t(
                  'workflowActivityVNext.schedule.repeat',
                  'Repeat',
                )}
                id="schedule-repeat"
                onChange={(preset: RepeatPreset) =>
                  updateHumanRepeat(preset, repeatTime)
                }
                options={[
                  {
                    label: t(
                      'workflowActivityVNext.schedule.hourly',
                      'Every hour',
                    ),
                    value: 'hourly',
                  },
                  {
                    label: t(
                      'workflowActivityVNext.schedule.daily',
                      'Every day',
                    ),
                    value: 'daily',
                  },
                  {
                    label: t(
                      'workflowActivityVNext.schedule.weekdays',
                      'Weekdays',
                    ),
                    value: 'weekdays',
                  },
                  {
                    label: t(
                      'workflowActivityVNext.schedule.weekly',
                      'Every week',
                    ),
                    value: 'weekly',
                  },
                  {
                    label: t(
                      'workflowActivityVNext.schedule.monthly',
                      'Every month',
                    ),
                    value: 'monthly',
                  },
                ]}
                value={repeatPreset}
              />
            </label>
            {repeatPreset === 'weekly' ? (
              <label
                className="wa-vnext__modal-field wa-vnext__schedule-repeat-detail"
                htmlFor="schedule-weekday"
              >
                <span>
                  {t('workflowActivityVNext.schedule.dayOfWeek', 'Day of week')}
                </span>
                <Select
                  aria-label={t(
                    'workflowActivityVNext.schedule.dayOfWeek',
                    'Day of week',
                  )}
                  id="schedule-weekday"
                  onChange={(day: WeekdayValue) =>
                    updateHumanRepeat(repeatPreset, repeatTime, day)
                  }
                  options={weekdayValues.map((day) => ({
                    label: weekdayLabel(day),
                    value: day,
                  }))}
                  value={weeklyDay}
                />
              </label>
            ) : null}
            {repeatPreset === 'monthly' ? (
              <label
                className="wa-vnext__modal-field wa-vnext__schedule-repeat-detail"
                htmlFor="schedule-monthday"
              >
                <span>
                  {t(
                    'workflowActivityVNext.schedule.dayOfMonth',
                    'Day of month',
                  )}
                </span>
                <Select
                  aria-label={t(
                    'workflowActivityVNext.schedule.dayOfMonth',
                    'Day of month',
                  )}
                  id="schedule-monthday"
                  onChange={(day: string) =>
                    updateHumanRepeat(repeatPreset, repeatTime, weeklyDay, day)
                  }
                  options={monthlyDayValues.map((day) => ({
                    label: day,
                    value: day,
                  }))}
                  value={monthlyDay}
                />
              </label>
            ) : null}
            <label className="wa-vnext__modal-field" htmlFor="schedule-time">
              <span>{t('workflowActivityVNext.schedule.time', 'Time')}</span>
              <Input
                aria-label={t('workflowActivityVNext.schedule.time', 'Time')}
                disabled={repeatPreset === 'hourly'}
                id="schedule-time"
                onChange={(event) =>
                  updateHumanRepeat(repeatPreset, event.target.value)
                }
                type="time"
                value={repeatTime}
              />
            </label>
            {timezoneField}
          </div>
        ) : null}
        <Button
          className="wa-vnext__schedule-cron-toggle"
          onClick={toggleCronMode}
          type="link"
        >
          {cronMode
            ? t(
                'workflowActivityVNext.schedule.useRepeatBuilder',
                'use the repeat builder',
              )
            : t(
                'workflowActivityVNext.schedule.writeCron',
                'write it as cron instead',
              )}
        </Button>
        {cronMode ? (
          <div className="wa-vnext__schedule-cron-grid">
            <label
              className="wa-vnext__modal-field"
              htmlFor="workflow-schedule-cron"
            >
              <span>
                {t('workflowActivityVNext.schedule.cron', 'Cron expression')}
              </span>
              <Input
                aria-label={t(
                  'workflowActivityVNext.schedule.cron',
                  'Cron expression',
                )}
                id="workflow-schedule-cron"
                onChange={(event) => {
                  setPreview(null);
                  setForm((current) => ({
                    ...current,
                    cronExpression: event.target.value,
                  }));
                }}
                value={form.cronExpression}
              />
            </label>
            {timezoneField}
          </div>
        ) : null}
        {previewing ? (
          <div className="wa-vnext__schedule-previewing" role="status">
            {t(
              'workflowActivityVNext.schedule.previewing',
              'Previewing schedule…',
            )}
          </div>
        ) : null}
      </section>
      <section className="wa-vnext__schedule-section">
        <label
          className="wa-vnext__modal-field"
          htmlFor="workflow-schedule-prompt"
        >
          <span>
            {t('workflowActivityVNext.schedule.prompt', 'Run input (optional)')}
          </span>
          <Input.TextArea
            aria-label={t(
              'workflowActivityVNext.schedule.prompt',
              'Run input (optional)',
            )}
            autoSize={{ minRows: 3, maxRows: 6 }}
            id="workflow-schedule-prompt"
            onChange={(event) =>
              setForm((current) => ({
                ...current,
                prompt: event.target.value,
              }))
            }
            value={form.prompt}
          />
        </label>
        {!editingSchedule ? (
          <label
            className="wa-vnext__schedule-enabled"
            htmlFor="workflow-schedule-enabled"
          >
            <span>
              {t(
                'workflowActivityVNext.schedule.enabledAfterCreation',
                'Enabled after creation',
              )}
            </span>
            <Switch
              checked={form.enabled}
              id="workflow-schedule-enabled"
              onChange={(enabled) =>
                setForm((current) => ({ ...current, enabled }))
              }
            />
          </label>
        ) : null}
      </section>
    </>
  );

  const configureView = (
    <div className="wa-vnext__schedule-create">
      {formFields}
      <footer className="wa-vnext__schedule-footer">
        <Button disabled={busy} onClick={leaveForm}>
          {t('workflowActivityVNext.common.cancel', 'Cancel')}
        </Button>
        <Button
          disabled={!formValid || busy}
          loading={previewing || saving}
          onClick={() =>
            editingSchedule ? void submitForm() : void previewSchedule()
          }
          type="primary"
        >
          {editingSchedule
            ? t('workflowActivityVNext.schedule.save', 'Save changes')
            : t(
                'workflowActivityVNext.schedule.reviewAction',
                'Review schedule',
              )}
        </Button>
      </footer>
    </div>
  );

  const reviewView = preview ? (
    <div className="wa-vnext__schedule-review">
      {workflowContext}
      <section className="wa-vnext__schedule-review-panel">
        <dl className="wa-vnext__schedule-review-details">
          <div>
            <dt>{t('workflowActivityVNext.schedule.workflow', 'Workflow')}</dt>
            <dd>{workflowName}</dd>
          </div>
          <div>
            <dt>
              {t(
                'workflowActivityVNext.schedule.scheduleName',
                'Schedule name',
              )}
            </dt>
            <dd>{form.displayName}</dd>
          </div>
          <div>
            <dt>{t('workflowActivityVNext.schedule.repeat', 'Repeat')}</dt>
            <dd>{recurrenceSummary}</dd>
          </div>
          <div>
            <dt>{t('workflowActivityVNext.schedule.timezone', 'Timezone')}</dt>
            <dd>{form.timezone}</dd>
          </div>
          <div>
            <dt>
              {t(
                'workflowActivityVNext.schedule.enabledAfterCreation',
                'Enabled after creation',
              )}
            </dt>
            <dd>
              {form.enabled
                ? t('workflowActivityVNext.common.yes', 'Yes')
                : t('workflowActivityVNext.common.no', 'No')}
            </dd>
          </div>
          <div>
            <dt>
              {t('workflowActivityVNext.schedule.promptReview', 'Run input')}
            </dt>
            <dd>
              {form.prompt.trim()
                ? form.prompt
                : t('workflowActivityVNext.schedule.noPrompt', 'No prompt')}
            </dd>
          </div>
        </dl>
        <div className="wa-vnext__schedule-fire-preview">
          <strong>
            {t(
              'workflowActivityVNext.schedule.nextFiveFires',
              'Next five fire times',
            )}
          </strong>
          <ol className="wa-vnext__schedule-preview-list">
            {preview.nextFireTimes.map((fireAt) => (
              <li key={fireAt}>
                <time dateTime={fireAt}>{formatScheduleDate(fireAt)}</time>
              </li>
            ))}
          </ol>
        </div>
      </section>
      <footer className="wa-vnext__schedule-footer">
        <Button disabled={saving} onClick={() => setCreationStep('configure')}>
          {t('workflowActivityVNext.common.back', 'Back')}
        </Button>
        <Button
          loading={saving}
          onClick={() => void submitForm()}
          type="primary"
        >
          {t('workflowActivityVNext.schedule.create', 'Create schedule')}
        </Button>
      </footer>
    </div>
  ) : null;

  const listView = (
    <div className="wa-vnext__schedule-surface">
      <div className="wa-vnext__schedule-toolbar">
        <div>
          <strong>
            {t('workflowActivityVNext.schedule.title', 'Schedules')}
          </strong>
          <p>
            {t(
              'workflowActivityVNext.schedule.subtitle',
              'Recurring runs for {name}',
              { name: workflowName },
            )}
          </p>
        </div>
        <Space>
          <Button
            aria-label={t(
              'workflowActivityVNext.schedule.refreshAria',
              'Refresh schedules',
            )}
            icon={<ReloadOutlined />}
            loading={schedules.isFetching}
            onClick={() => void schedules.refetch()}
          />
          <Button icon={<PlusOutlined />} onClick={openCreate} type="primary">
            {t('workflowActivityVNext.schedule.new', 'New schedule')}
          </Button>
        </Space>
      </div>
      {schedules.isPending ? (
        <div className="wa-vnext__state wa-vnext__state--compact" role="status">
          <p>
            {t('workflowActivityVNext.schedule.loading', 'Loading schedules…')}
          </p>
        </div>
      ) : schedules.isError ? (
        <Alert
          showIcon
          type="error"
          title={t(
            'workflowActivityVNext.schedule.loadFailed',
            "Schedules couldn't be loaded",
          )}
          description={errorMessage(schedules.error)}
          action={
            <Button onClick={() => void schedules.refetch()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          }
        />
      ) : schedules.data?.items.length ? (
        <div className="wa-vnext__schedule-list">
          {schedules.data.items.map((schedule) => (
            <button
              aria-label={t(
                'workflowActivityVNext.schedule.viewAria',
                'View {name}',
                { name: schedule.displayName },
              )}
              className="wa-vnext__schedule-row"
              key={schedule.scheduleId}
              onClick={() => openDetail(schedule)}
              type="button"
            >
              <span className="wa-vnext__schedule-row-main">
                <span className="wa-vnext__schedule-row-heading">
                  <strong>{schedule.displayName}</strong>
                  <Tag color={schedule.enabled ? 'green' : 'default'}>
                    {schedule.enabled
                      ? t('workflowActivityVNext.schedule.enabled', 'Enabled')
                      : t('workflowActivityVNext.schedule.paused', 'Paused')}
                  </Tag>
                </span>
                <span>
                  {scheduleRecurrenceSummary(schedule.cronExpression)}
                </span>
                <span>
                  {schedule.timezone} ·{' '}
                  {schedule.enabled && schedule.nextFireAt
                    ? t(
                        'workflowActivityVNext.schedule.nextFire',
                        'Next {date}',
                        {
                          date: formatScheduleDate(
                            schedule.nextFireAt,
                            schedule.timezone,
                          ),
                        },
                      )
                    : t(
                        'workflowActivityVNext.schedule.noUpcomingAttempt',
                        'No upcoming attempt',
                      )}
                </span>
              </span>
              <span aria-hidden="true" className="wa-vnext__schedule-row-arrow">
                ›
              </span>
            </button>
          ))}
        </div>
      ) : (
        <div className="wa-vnext__schedule-empty">
          <h3 className="wa-vnext__schedule-empty-title">
            {t('workflowActivityVNext.schedule.empty', 'No schedules yet')}
          </h3>
          <p>
            {t(
              'workflowActivityVNext.schedule.emptyDescription',
              'Create a recurring schedule for this published Workflow.',
            )}
          </p>
        </div>
      )}
    </div>
  );

  const activityHref = scheduleDetail.data
    ? `${buildWorkflowActivitySectionHref(scopeId, 'activity')}?${new URLSearchParams(
        [
          ['workflowId', workflowId],
          ['schedule', scheduleDetail.data.schedule.scheduleId],
        ],
      ).toString()}`
    : null;

  const historyView = scheduleDetail.data ? (
    <section className="wa-vnext__schedule-history">
      <header className="wa-vnext__schedule-history-header">
        <div>
          <h2>
            {t(
              'workflowActivityVNext.schedule.recentAttempts',
              'Recent attempts',
            )}
          </h2>
          <p>
            {t(
              'workflowActivityVNext.schedule.historyDescription',
              'Schedule attempts can fail before a Workflow Run exists.',
            )}
          </p>
        </div>
        {activityHref ? (
          <a href={activityHref} rel="noopener noreferrer" target="_blank">
            {t(
              'workflowActivityVNext.schedule.viewRelatedRuns',
              'View related runs in Activity',
            )}
          </a>
        ) : null}
      </header>
      {scheduleDetail.data.recentFires.length ? (
        <div className="wa-vnext__schedule-history-table-wrap">
          <table className="wa-vnext__schedule-history-table">
            <thead>
              <tr>
                <th scope="col">
                  {t(
                    'workflowActivityVNext.schedule.scheduledTime',
                    'Scheduled time',
                  )}
                </th>
                <th scope="col">
                  {t('workflowActivityVNext.schedule.source', 'Source')}
                </th>
                <th scope="col">
                  {t('workflowActivityVNext.schedule.result', 'Result')}
                </th>
                <th scope="col">
                  {t(
                    'workflowActivityVNext.schedule.completedTime',
                    'Completed time',
                  )}
                </th>
                <th scope="col">
                  {t('workflowActivityVNext.schedule.action', 'Action')}
                </th>
              </tr>
            </thead>
            <tbody>
              {scheduleDetail.data.recentFires.map((fire) => {
                const failed = Boolean(fire.error.trim());
                const source = fire.manual
                  ? t('workflowActivityVNext.schedule.manual', 'Manual')
                  : t('workflowActivityVNext.schedule.scheduled', 'Scheduled');
                const formattedScheduledAt = formatScheduleDate(
                  fire.scheduledFireAt,
                  scheduleDetail.data.schedule.timezone,
                );
                const runActorId = fire.runActorId.trim();
                const runHref = runActorId
                  ? buildWorkflowActivityRunHref(scopeId, runActorId, {
                      workflowId,
                      schedule: scheduleDetail.data.schedule.scheduleId,
                    })
                  : null;
                const attemptHref = runHref ?? (!failed ? activityHref : null);
                const attemptLabel = runHref
                  ? t(
                      'workflowActivityVNext.schedule.openRunAria',
                      'Open Run from {date}',
                      { date: formattedScheduledAt },
                    )
                  : attemptHref
                    ? t(
                        'workflowActivityVNext.schedule.viewRelatedRunsAria',
                        'View related runs from {date}',
                        { date: formattedScheduledAt },
                      )
                    : null;
                return (
                  <tr key={`${fire.idempotencyKey}:${fire.completedAt}`}>
                    <td>
                      <time dateTime={fire.scheduledFireAt}>
                        {formattedScheduledAt}
                      </time>
                    </td>
                    <td>{source}</td>
                    <td>
                      <div className="wa-vnext__schedule-history-result">
                        <Tag color={failed ? 'red' : 'green'}>
                          {failed
                            ? t(
                                'workflowActivityVNext.schedule.failed',
                                'Failed',
                              )
                            : t(
                                'workflowActivityVNext.schedule.runStarted',
                                'Run started',
                              )}
                        </Tag>
                        {failed ? (
                          <>
                            <p className="wa-vnext__schedule-history-failure">
                              {t(
                                fire.manual
                                  ? 'workflowActivityVNext.schedule.manualAttemptFailed'
                                  : 'workflowActivityVNext.schedule.scheduledAttemptFailed',
                                fire.manual
                                  ? 'The manual attempt could not start the Workflow.'
                                  : 'The scheduled attempt could not start the Workflow.',
                              )}
                            </p>
                            <details
                              onClick={(event) => event.stopPropagation()}
                              onKeyDown={(event) => event.stopPropagation()}
                            >
                              <summary>
                                {t(
                                  'workflowActivityVNext.schedule.technicalDetails',
                                  'Technical details',
                                )}
                              </summary>
                              <code>{fire.error}</code>
                            </details>
                          </>
                        ) : null}
                      </div>
                    </td>
                    <td>
                      <time
                        className="wa-vnext__schedule-history-completed"
                        dateTime={fire.completedAt}
                      >
                        {formatScheduleDate(
                          fire.completedAt,
                          scheduleDetail.data.schedule.timezone,
                        )}
                      </time>
                    </td>
                    <td className="wa-vnext__schedule-history-action">
                      {attemptHref && attemptLabel ? (
                        <a
                          aria-label={attemptLabel}
                          className="wa-vnext__schedule-history-attempt-link"
                          href={attemptHref}
                          rel="noopener noreferrer"
                          target="_blank"
                        >
                          <ArrowRightOutlined aria-hidden="true" />
                        </a>
                      ) : null}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="wa-vnext__schedule-empty wa-vnext__schedule-empty--history">
          <h3>
            {t('workflowActivityVNext.schedule.noAttempts', 'No attempts yet')}
          </h3>
        </div>
      )}
    </section>
  ) : null;

  const detailView = (
    <div className="wa-vnext__schedule-detail">
      <Tabs
        activeKey={detailTab}
        items={[
          {
            key: 'overview',
            label: t('workflowActivityVNext.schedule.overview', 'Overview'),
          },
          {
            key: 'history',
            label: t('workflowActivityVNext.schedule.history', 'History'),
          },
        ]}
        onChange={(key) => setDetailTab(key as ScheduleDetailTab)}
      />
      {scheduleDetail.isPending ? (
        <div className="wa-vnext__state wa-vnext__state--compact" role="status">
          <p>
            {t(
              detailTab === 'history'
                ? 'workflowActivityVNext.schedule.historyLoading'
                : 'workflowActivityVNext.schedule.detailLoading',
              detailTab === 'history'
                ? 'Loading history…'
                : 'Loading schedule details…',
            )}
          </p>
        </div>
      ) : scheduleDetail.isError ? (
        <Alert
          action={
            <Button onClick={() => void scheduleDetail.refetch()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          }
          description={errorMessage(scheduleDetail.error)}
          showIcon
          title={t(
            detailTab === 'history'
              ? 'workflowActivityVNext.schedule.historyLoadFailed'
              : 'workflowActivityVNext.schedule.detailLoadFailed',
            detailTab === 'history'
              ? "History couldn't be loaded"
              : "Schedule couldn't be loaded",
          )}
          type="error"
        />
      ) : scheduleDetail.data ? (
        detailTab === 'overview' ? (
          <div className="wa-vnext__schedule-overview">
            <section className="wa-vnext__schedule-overview-summary">
              <Tag
                color={
                  scheduleDetail.data.schedule.enabled ? 'green' : 'default'
                }
              >
                {scheduleDetail.data.schedule.enabled
                  ? t('workflowActivityVNext.schedule.enabled', 'Enabled')
                  : t('workflowActivityVNext.schedule.paused', 'Paused')}
              </Tag>
              <h2>
                {scheduleRecurrenceSummary(
                  scheduleDetail.data.schedule.cronExpression,
                )}
              </h2>
              <p>
                <span>
                  {t('workflowActivityVNext.schedule.timezone', 'Timezone')}
                </span>{' '}
                {scheduleDetail.data.schedule.timezone}
              </p>
            </section>
            <div className="wa-vnext__schedule-overview-actions">
              <Button
                disabled={
                  actionScheduleId ===
                    scheduleDetail.data.schedule.scheduleId ||
                  !scheduleDetail.data.schedule.enabled
                }
                icon={<PlayCircleOutlined />}
                onClick={() =>
                  void changeState(scheduleDetail.data.schedule, 'runNow')
                }
                type="primary"
              >
                {t('workflowActivityVNext.schedule.runNow', 'Run now')}
              </Button>
              <Button icon={<EditOutlined />} onClick={openEdit}>
                {t(
                  'workflowActivityVNext.schedule.editAction',
                  'Edit schedule',
                )}
              </Button>
              <Dropdown
                menu={{
                  items: [
                    {
                      icon: scheduleDetail.data.schedule.enabled ? (
                        <StopOutlined />
                      ) : (
                        <CalendarOutlined />
                      ),
                      key: 'toggle',
                      label: scheduleDetail.data.schedule.enabled
                        ? t('workflowActivityVNext.schedule.pause', 'Pause')
                        : t('workflowActivityVNext.schedule.enable', 'Enable'),
                    },
                    { type: 'divider' },
                    {
                      danger: true,
                      icon: <DeleteOutlined />,
                      key: 'delete',
                      label: t(
                        'workflowActivityVNext.schedule.deleteAction',
                        'Delete schedule',
                      ),
                    },
                  ],
                  onClick: ({ key }) => {
                    if (key === 'delete') {
                      confirmDeleteSchedule(scheduleDetail.data.schedule);
                      return;
                    }
                    void changeState(
                      scheduleDetail.data.schedule,
                      scheduleDetail.data.schedule.enabled
                        ? 'disable'
                        : 'enable',
                    );
                  },
                }}
                trigger={['click']}
              >
                <Button
                  aria-label={t(
                    'workflowActivityVNext.schedule.moreActionsAria',
                    'More schedule actions',
                  )}
                  disabled={
                    actionScheduleId === scheduleDetail.data.schedule.scheduleId
                  }
                  icon={<DownOutlined />}
                  iconPlacement="end"
                >
                  {t('workflowActivityVNext.schedule.more', 'More')}
                </Button>
              </Dropdown>
            </div>
            <dl className="wa-vnext__schedule-detail-facts">
              <div>
                <dt>
                  {t(
                    'workflowActivityVNext.schedule.nextScheduled',
                    'Next scheduled',
                  )}
                </dt>
                <dd>
                  {scheduleDetail.data.schedule.enabled &&
                  scheduleDetail.data.schedule.nextFireAt
                    ? formatScheduleDate(
                        scheduleDetail.data.schedule.nextFireAt,
                        scheduleDetail.data.schedule.timezone,
                      )
                    : t(
                        'workflowActivityVNext.schedule.noUpcomingAttempt',
                        'No upcoming attempt',
                      )}
                </dd>
              </div>
              <div>
                <dt>
                  {t(
                    'workflowActivityVNext.schedule.lastAttempt',
                    'Last attempt',
                  )}
                </dt>
                <dd>
                  {formatScheduleDate(
                    scheduleDetail.data.schedule.lastFireAt,
                    scheduleDetail.data.schedule.timezone,
                  )}
                  {scheduleDetail.data.recentFires[0] ? (
                    <span>
                      {' · '}
                      {scheduleDetail.data.recentFires[0].error.trim()
                        ? t('workflowActivityVNext.schedule.failed', 'Failed')
                        : t(
                            'workflowActivityVNext.schedule.runStarted',
                            'Run started',
                          )}
                    </span>
                  ) : null}
                </dd>
              </div>
              <div>
                <dt>
                  {t(
                    'workflowActivityVNext.schedule.totalAttempts',
                    'Total attempts',
                  )}
                </dt>
                <dd>{scheduleDetail.data.schedule.fireCount}</dd>
              </div>
              <div>
                <dt>
                  {t(
                    'workflowActivityVNext.schedule.failedAttempts',
                    'Failed attempts',
                  )}
                </dt>
                <dd>{scheduleDetail.data.schedule.failureCount}</dd>
              </div>
            </dl>
            {scheduleDetail.data.schedule.prompt.trim() ? (
              <section className="wa-vnext__schedule-run-input">
                <h3>
                  {t(
                    'workflowActivityVNext.schedule.promptReview',
                    'Run input',
                  )}
                </h3>
                <p>{scheduleDetail.data.schedule.prompt}</p>
              </section>
            ) : null}
            <details className="wa-vnext__schedule-advanced-details">
              <summary>
                {t(
                  'workflowActivityVNext.schedule.advancedDetails',
                  'Advanced details',
                )}
              </summary>
              <dl>
                <div>
                  <dt>
                    {t(
                      'workflowActivityVNext.schedule.cron',
                      'Cron expression',
                    )}
                  </dt>
                  <dd>
                    <code>{scheduleDetail.data.schedule.cronExpression}</code>
                  </dd>
                </div>
              </dl>
            </details>
          </div>
        ) : (
          historyView
        )
      ) : null}
    </div>
  );

  const activeBody = !available ? (
    <Alert
      showIcon
      type="info"
      title={t(
        'workflowActivityVNext.schedule.unavailableTitle',
        'Schedule is unavailable until this Workflow is published',
      )}
      description={t(
        'workflowActivityVNext.schedule.unavailableDescription',
        'Publish a runnable Workflow before creating a recurring schedule.',
      )}
    />
  ) : surfaceView === 'list' ? (
    listView
  ) : surfaceView === 'detail' ? (
    detailView
  ) : creationStep === 'review' ? (
    reviewView
  ) : (
    configureView
  );

  if (mode === 'panel') {
    return (
      <Drawer
        closable={!busy}
        destroyOnHidden
        onClose={busy ? undefined : onClose}
        open={open}
        placement="right"
        rootClassName="wa-vnext-schedule-drawer"
        size={520}
        title={surfaceTitle}
      >
        {activeBody}
      </Drawer>
    );
  }

  return (
    <Modal
      closable={!busy}
      destroyOnHidden
      footer={null}
      onCancel={busy ? undefined : onClose}
      open={open}
      rootClassName="wa-vnext-schedule-modal"
      title={surfaceTitle}
      width={820}
    >
      {activeBody}
    </Modal>
  );
};

export default WorkflowScheduleSurface;
