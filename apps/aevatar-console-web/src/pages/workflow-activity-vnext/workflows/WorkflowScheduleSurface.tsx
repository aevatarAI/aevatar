import {
  CalendarOutlined,
  DeleteOutlined,
  EditOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  ReloadOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Drawer,
  Input,
  Modal,
  Select,
  Space,
  Switch,
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
import { AEVATAR_INTERACTIVE_BUTTON_CLASS } from '@/shared/ui/interactionStandards';

type WorkflowScheduleSurfaceProps = {
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

type CreationStep = 'configure' | 'previewing' | 'review' | 'accepted';
type ScheduleSurfaceView = 'list' | 'form';
type RepeatPreset = 'daily' | 'weekdays';

const scheduleQueryKey = (scopeId: string, workflowId: string) => [
  'workflow-activity-vnext',
  'workflow-schedules',
  scopeId,
  workflowId,
];

function defaultTimezone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
}

function cronFromRepeat(preset: RepeatPreset, time: string): string {
  const [hour = '9', minute = '0'] = time.split(':');
  return `${Number(minute)} ${Number(hour)} * * ${preset === 'weekdays' ? '1-5' : '*'}`;
}

function repeatFromCron(cronExpression: string): {
  readonly preset: RepeatPreset;
  readonly time: string;
} | null {
  const match = cronExpression.match(/^(\d{1,2}) (\d{1,2}) \* \* (\*|1-5)$/);
  if (!match) return null;
  const minute = Number(match[1]);
  const hour = Number(match[2]);
  if (minute > 59 || hour > 23) return null;
  return {
    preset: match[3] === '1-5' ? 'weekdays' : 'daily',
    time: `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`,
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

function formatScheduleDate(value: string | null): string {
  if (!value)
    return t('workflowActivityVNext.common.unavailable', 'Unavailable');
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(date);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

const WorkflowScheduleSurface: React.FC<WorkflowScheduleSurfaceProps> = ({
  available,
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
    mode === 'modal' ? 'form' : 'list',
  );
  const [creationStep, setCreationStep] =
    React.useState<CreationStep>('configure');
  const [editingSchedule, setEditingSchedule] =
    React.useState<WorkflowScheduleSummary | null>(null);
  const [form, setForm] = React.useState<ScheduleForm>(() => emptyForm());
  const [repeatPreset, setRepeatPreset] =
    React.useState<RepeatPreset>('weekdays');
  const [repeatTime, setRepeatTime] = React.useState('09:00');
  const [cronMode, setCronMode] = React.useState(false);
  const [preview, setPreview] = React.useState<WorkflowSchedulePreview | null>(
    null,
  );
  const [saving, setSaving] = React.useState(false);
  const [actionScheduleId, setActionScheduleId] = React.useState<string | null>(
    null,
  );
  const [acceptedMessage, setAcceptedMessage] = React.useState<string | null>(
    null,
  );

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
    refetchOnMount: 'always',
    retry: false,
  });

  React.useEffect(() => {
    if (!open) return;
    setSurfaceView(mode === 'modal' ? 'form' : 'list');
    setCreationStep('configure');
    setEditingSchedule(null);
    setForm(emptyForm());
    setRepeatPreset('weekdays');
    setRepeatTime('09:00');
    setCronMode(false);
    setPreview(null);
    setAcceptedMessage(null);
  }, [mode, open, workflowId]);

  const refreshSchedules = React.useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey });
    await queryClient.refetchQueries({ queryKey, type: 'active' });
  }, [queryClient, queryKey]);

  const openCreate = () => {
    setEditingSchedule(null);
    setForm(emptyForm());
    setRepeatPreset('weekdays');
    setRepeatTime('09:00');
    setCronMode(false);
    setPreview(null);
    setCreationStep('configure');
    setSurfaceView('form');
  };

  const openEdit = (schedule: WorkflowScheduleSummary) => {
    const repeat = repeatFromCron(schedule.cronExpression);
    setEditingSchedule(schedule);
    setForm(formFromSchedule(schedule));
    setRepeatPreset(repeat?.preset ?? 'weekdays');
    setRepeatTime(repeat?.time ?? '09:00');
    setCronMode(!repeat);
    setPreview(null);
    setCreationStep('configure');
    setSurfaceView('form');
  };

  const leaveForm = () => {
    if (busy) return;
    if (mode === 'modal') {
      onClose();
      return;
    }
    setSurfaceView('list');
    setEditingSchedule(null);
    setCreationStep('configure');
    setPreview(null);
  };

  const updateHumanRepeat = (nextPreset: RepeatPreset, nextTime: string) => {
    setRepeatPreset(nextPreset);
    setRepeatTime(nextTime);
    setPreview(null);
    setForm((current) => ({
      ...current,
      cronExpression: cronFromRepeat(nextPreset, nextTime),
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
        setAcceptedMessage(
          t(
            'workflowActivityVNext.schedule.updateAccepted',
            'Schedule update accepted. Refreshing Workflow schedules.',
          ),
        );
        setSurfaceView('list');
        setEditingSchedule(null);
        await refreshSchedules();
      } else {
        await workflowScheduleApi.create(scopeId, workflowId, input);
        setCreationStep('accepted');
        await refreshSchedules();
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
      setAcceptedMessage(
        t(
          'workflowActivityVNext.schedule.actionAccepted',
          'Schedule action accepted. Refreshing the Workflow schedule list…',
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
      setAcceptedMessage(
        t(
          'workflowActivityVNext.schedule.deleteAccepted',
          'Schedule deletion accepted. Refreshing the Workflow schedule list…',
        ),
      );
      await refreshSchedules();
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setActionScheduleId(null);
    }
  };

  const recurrenceSummary = cronMode
    ? t('workflowActivityVNext.schedule.cronSummary', 'Cron: {cron}', {
        cron: form.cronExpression,
      })
    : t('workflowActivityVNext.schedule.repeatSummary', '{repeat} at {time}', {
        repeat:
          repeatPreset === 'weekdays'
            ? t('workflowActivityVNext.schedule.weekdays', 'Weekdays')
            : t('workflowActivityVNext.schedule.daily', 'Every day'),
        time: repeatTime,
      });

  const surfaceTitle = editingSchedule
    ? t('workflowActivityVNext.schedule.editTitle', 'Edit schedule')
    : surfaceView === 'form'
      ? t('workflowActivityVNext.schedule.new', 'New schedule')
      : workflowName;

  const workflowContext = (
    <div className="wa-vnext__schedule-context">
      <span>{t('workflowActivityVNext.schedule.workflow', 'Workflow')}</span>
      <strong>{workflowName}</strong>
    </div>
  );

  const formFields = (
    <>
      {workflowContext}
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
        <div className="wa-vnext__schedule-repeat-grid">
          <label className="wa-vnext__modal-field" htmlFor="schedule-repeat">
            <span>{t('workflowActivityVNext.schedule.repeat', 'Repeat')}</span>
            <Select
              aria-label={t('workflowActivityVNext.schedule.repeat', 'Repeat')}
              disabled={cronMode}
              id="schedule-repeat"
              onChange={(preset: RepeatPreset) =>
                updateHumanRepeat(preset, repeatTime)
              }
              options={[
                {
                  label: t(
                    'workflowActivityVNext.schedule.weekdays',
                    'Weekdays',
                  ),
                  value: 'weekdays',
                },
                {
                  label: t('workflowActivityVNext.schedule.daily', 'Every day'),
                  value: 'daily',
                },
              ]}
              value={repeatPreset}
            />
          </label>
          <label className="wa-vnext__modal-field" htmlFor="schedule-time">
            <span>{t('workflowActivityVNext.schedule.time', 'Time')}</span>
            <Input
              aria-label={t('workflowActivityVNext.schedule.time', 'Time')}
              disabled={cronMode}
              id="schedule-time"
              onChange={(event) =>
                updateHumanRepeat(repeatPreset, event.target.value)
              }
              type="time"
              value={repeatTime}
            />
          </label>
          <label className="wa-vnext__modal-field" htmlFor="schedule-timezone">
            <span>
              {t('workflowActivityVNext.schedule.timezone', 'Timezone')}
            </span>
            <Input
              aria-label={t(
                'workflowActivityVNext.schedule.timezone',
                'Timezone',
              )}
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
        </div>
        <Button
          className="wa-vnext__schedule-cron-toggle"
          onClick={() => setCronMode((current) => !current)}
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
          <label
            className="wa-vnext__modal-field wa-vnext__schedule-cron-field"
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
        ) : null}
        <div className="wa-vnext__schedule-server-preview">
          <strong>
            {t(
              'workflowActivityVNext.schedule.serverPreviewTitle',
              'Previewed by the server',
            )}
          </strong>
          <span>
            {t(
              'workflowActivityVNext.schedule.serverPreviewDescription',
              'The next five fire times are calculated when you review.',
            )}
          </span>
        </div>
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
        <h3>
          {t('workflowActivityVNext.schedule.whatItNeeds', 'What it needs')}
        </h3>
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
      </section>
      <section className="wa-vnext__schedule-section">
        <h3>
          {t(
            'workflowActivityVNext.schedule.whatWillHappen',
            'What will happen',
          )}
        </h3>
        <p className="wa-vnext__schedule-explanation">
          {editingSchedule
            ? t(
                'workflowActivityVNext.schedule.updateExplanation',
                'The updated schedule will keep its current enabled state.',
              )
            : t(
                'workflowActivityVNext.schedule.createExplanation',
                'This Workflow will receive the configured input on each scheduled fire.',
              )}
        </p>
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
      <h2 className="wa-vnext__schedule-form-title">{surfaceTitle}</h2>
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
        <h2>
          {t('workflowActivityVNext.schedule.reviewTitle', 'Review schedule')}
        </h2>
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
          {form.prompt.trim() ? (
            <div>
              <dt>
                {t('workflowActivityVNext.schedule.promptReview', 'Run input')}
              </dt>
              <dd>{form.prompt}</dd>
            </div>
          ) : null}
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

  const acceptedView = (
    <div className="wa-vnext__schedule-accepted" role="status">
      {workflowContext}
      <div className="wa-vnext__schedule-accepted-panel">
        <h2>
          {t(
            'workflowActivityVNext.schedule.requestAccepted',
            'Schedule request accepted',
          )}
        </h2>
        <p>
          {t(
            'workflowActivityVNext.schedule.refreshingSchedules',
            'Refreshing Workflow schedules',
          )}
        </p>
        <span>
          {t(
            'workflowActivityVNext.schedule.acceptedDescription',
            'The request is continuing in the background. Closing this view will not cancel it.',
          )}
        </span>
      </div>
      <footer className="wa-vnext__schedule-footer">
        <Button onClick={leaveForm} type="primary">
          {mode === 'panel'
            ? t(
                'workflowActivityVNext.schedule.backToSchedules',
                'Back to schedules',
              )
            : t('workflowActivityVNext.common.close', 'Close')}
        </Button>
      </footer>
    </div>
  );

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
      {acceptedMessage ? (
        <Alert showIcon type="info" title={acceptedMessage} />
      ) : null}
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
          {schedules.data.items.map((schedule) => {
            const actionPending = actionScheduleId === schedule.scheduleId;
            return (
              <div className="wa-vnext__schedule-row" key={schedule.scheduleId}>
                <div className="wa-vnext__schedule-row-main">
                  <div className="wa-vnext__schedule-row-heading">
                    <strong>{schedule.displayName}</strong>
                    <Tag color={schedule.enabled ? 'green' : 'default'}>
                      {schedule.enabled
                        ? t('workflowActivityVNext.schedule.enabled', 'Enabled')
                        : t(
                            'workflowActivityVNext.schedule.disabled',
                            'Disabled',
                          )}
                    </Tag>
                  </div>
                  <code>{schedule.cronExpression}</code>
                  <span>
                    {schedule.timezone} ·{' '}
                    {t(
                      'workflowActivityVNext.schedule.nextFire',
                      'Next {date}',
                      { date: formatScheduleDate(schedule.nextFireAt) },
                    )}
                  </span>
                </div>
                <Space wrap>
                  <Button
                    aria-label={t(
                      'workflowActivityVNext.schedule.editAria',
                      'Edit {name}',
                      { name: schedule.displayName },
                    )}
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    icon={<EditOutlined />}
                    onClick={() => openEdit(schedule)}
                  />
                  <Button
                    aria-label={t(
                      schedule.enabled
                        ? 'workflowActivityVNext.schedule.disableAria'
                        : 'workflowActivityVNext.schedule.enableAria',
                      schedule.enabled ? 'Disable {name}' : 'Enable {name}',
                      { name: schedule.displayName },
                    )}
                    disabled={actionPending}
                    icon={
                      schedule.enabled ? <StopOutlined /> : <CalendarOutlined />
                    }
                    loading={actionPending}
                    onClick={() =>
                      void changeState(
                        schedule,
                        schedule.enabled ? 'disable' : 'enable',
                      )
                    }
                  />
                  <Button
                    aria-label={t(
                      'workflowActivityVNext.schedule.runNowAria',
                      'Run {name} now',
                      { name: schedule.displayName },
                    )}
                    disabled={actionPending || !schedule.enabled}
                    icon={<PlayCircleOutlined />}
                    onClick={() => void changeState(schedule, 'runNow')}
                  />
                  <Button
                    aria-label={t(
                      'workflowActivityVNext.schedule.deleteAria',
                      'Delete {name}',
                      { name: schedule.displayName },
                    )}
                    danger
                    disabled={actionPending}
                    icon={<DeleteOutlined />}
                    onClick={() => void deleteSchedule(schedule)}
                  />
                </Space>
              </div>
            );
          })}
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
  ) : creationStep === 'accepted' ? (
    acceptedView
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
