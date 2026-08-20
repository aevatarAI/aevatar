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
import { Alert, Button, Drawer, Input, Modal, Space, Switch, Tag } from 'antd';
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

const scheduleQueryKey = (scopeId: string, workflowId: string) => [
  'workflow-activity-vnext',
  'workflow-schedules',
  scopeId,
  workflowId,
];

function defaultTimezone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
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
  const [editingSchedule, setEditingSchedule] =
    React.useState<WorkflowScheduleSummary | null>(null);
  const [form, setForm] = React.useState<ScheduleForm>(() => emptyForm());
  const [formOpen, setFormOpen] = React.useState(false);
  const [preview, setPreview] = React.useState<WorkflowSchedulePreview | null>(
    null,
  );
  const [previewing, setPreviewing] = React.useState(false);
  const [saving, setSaving] = React.useState(false);
  const [actionScheduleId, setActionScheduleId] = React.useState<string | null>(
    null,
  );
  const [acceptedMessage, setAcceptedMessage] = React.useState<string | null>(
    null,
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
    setAcceptedMessage(null);
  }, [open]);

  const refreshSchedules = React.useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey });
    await queryClient.refetchQueries({ queryKey, type: 'active' });
  }, [queryClient, queryKey]);

  const openCreate = () => {
    setEditingSchedule(null);
    setForm(emptyForm());
    setPreview(null);
    setFormOpen(true);
  };

  const openEdit = (schedule: WorkflowScheduleSummary) => {
    setEditingSchedule(schedule);
    setForm(formFromSchedule(schedule));
    setPreview(null);
    setFormOpen(true);
  };

  const closeForm = () => {
    if (saving || previewing) return;
    setFormOpen(false);
    setEditingSchedule(null);
    setPreview(null);
  };

  const previewSchedule = async () => {
    if (!form.cronExpression.trim() || !form.timezone.trim() || previewing)
      return;
    setPreviewing(true);
    try {
      const result = await workflowScheduleApi.preview(scopeId, workflowId, {
        cronExpression: form.cronExpression,
        timezone: form.timezone,
        count: 5,
      });
      setPreview(result);
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setPreviewing(false);
    }
  };

  const submitForm = async () => {
    if (
      saving ||
      !form.displayName.trim() ||
      !form.cronExpression.trim() ||
      !form.timezone.trim()
    ) {
      return;
    }
    setSaving(true);
    setAcceptedMessage(null);
    const input: WorkflowScheduleConfigurationInput = {
      displayName: form.displayName,
      cronExpression: form.cronExpression,
      timezone: form.timezone,
      enabled: form.enabled,
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
      } else {
        await workflowScheduleApi.create(scopeId, workflowId, input);
      }
      setAcceptedMessage(
        t(
          'workflowActivityVNext.schedule.accepted',
          'Schedule accepted. Refreshing the Workflow schedule list…',
        ),
      );
      closeForm();
      await refreshSchedules();
      toast.success(
        editingSchedule
          ? t(
              'workflowActivityVNext.schedule.updated',
              'Schedule update accepted',
            )
          : t(
              'workflowActivityVNext.schedule.created',
              'Schedule creation accepted',
            ),
      );
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

  const body = (
    <div className="wa-vnext__schedule-surface">
      {!available ? (
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
      ) : (
        <>
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
              <Button
                icon={<PlusOutlined />}
                onClick={openCreate}
                type="primary"
              >
                {t('workflowActivityVNext.schedule.new', 'New schedule')}
              </Button>
            </Space>
          </div>
          {acceptedMessage ? (
            <Alert showIcon type="info" title={acceptedMessage} />
          ) : null}
          {schedules.isPending ? (
            <div
              className="wa-vnext__state wa-vnext__state--compact"
              role="status"
            >
              <p>
                {t(
                  'workflowActivityVNext.schedule.loading',
                  'Loading schedules…',
                )}
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
                  <div
                    className="wa-vnext__schedule-row"
                    key={schedule.scheduleId}
                  >
                    <div className="wa-vnext__schedule-row-main">
                      <div className="wa-vnext__schedule-row-heading">
                        <strong>{schedule.displayName}</strong>
                        <Tag color={schedule.enabled ? 'green' : 'default'}>
                          {schedule.enabled
                            ? t(
                                'workflowActivityVNext.schedule.enabled',
                                'Enabled',
                              )
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
                          {
                            date: formatScheduleDate(schedule.nextFireAt),
                          },
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
                          schedule.enabled ? (
                            <StopOutlined />
                          ) : (
                            <CalendarOutlined />
                          )
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
            <div className="wa-vnext__state wa-vnext__state--compact">
              <h3>
                {t('workflowActivityVNext.schedule.empty', 'No schedules yet')}
              </h3>
              <p>
                {t(
                  'workflowActivityVNext.schedule.emptyDescription',
                  'Create a recurring schedule for this published Workflow.',
                )}
              </p>
              <Button
                icon={<PlusOutlined />}
                onClick={openCreate}
                type="primary"
              >
                {t('workflowActivityVNext.schedule.new', 'New schedule')}
              </Button>
            </div>
          )}
        </>
      )}
      <Modal
        cancelText={t('workflowActivityVNext.common.cancel', 'Cancel')}
        closable={!saving && !previewing}
        confirmLoading={saving}
        destroyOnHidden
        onCancel={closeForm}
        onOk={() => void submitForm()}
        okButtonProps={{
          disabled:
            !form.displayName.trim() ||
            !form.cronExpression.trim() ||
            !form.timezone.trim(),
        }}
        okText={
          editingSchedule
            ? t('workflowActivityVNext.schedule.save', 'Save changes')
            : t('workflowActivityVNext.schedule.create', 'Create schedule')
        }
        open={formOpen}
        title={
          editingSchedule
            ? t('workflowActivityVNext.schedule.editTitle', 'Edit schedule')
            : t('workflowActivityVNext.schedule.createTitle', 'Create schedule')
        }
      >
        <div className="wa-vnext__schedule-form">
          <label
            className="wa-vnext__modal-field"
            htmlFor="workflow-schedule-name"
          >
            <span>{t('workflowActivityVNext.schedule.name', 'Name')}</span>
            <Input
              aria-label={t('workflowActivityVNext.schedule.name', 'Name')}
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
          <label
            className="wa-vnext__modal-field"
            htmlFor="workflow-schedule-timezone"
          >
            <span>
              {t('workflowActivityVNext.schedule.timezone', 'Timezone')}
            </span>
            <Input
              aria-label={t(
                'workflowActivityVNext.schedule.timezone',
                'Timezone',
              )}
              id="workflow-schedule-timezone"
              onChange={(event) =>
                setForm((current) => ({
                  ...current,
                  timezone: event.target.value,
                }))
              }
              value={form.timezone}
            />
          </label>
          <label
            className="wa-vnext__modal-field"
            htmlFor="workflow-schedule-prompt"
          >
            <span>
              {t(
                'workflowActivityVNext.schedule.prompt',
                'Run input (optional)',
              )}
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
          <label
            className="wa-vnext__schedule-enabled"
            htmlFor="workflow-schedule-enabled"
          >
            <span>
              {t('workflowActivityVNext.schedule.enabled', 'Enabled')}
            </span>
            <Switch
              checked={form.enabled}
              id="workflow-schedule-enabled"
              onChange={(enabled) =>
                setForm((current) => ({ ...current, enabled }))
              }
            />
          </label>
          <Space wrap>
            <Button loading={previewing} onClick={() => void previewSchedule()}>
              {t(
                'workflowActivityVNext.schedule.preview',
                'Preview next fires',
              )}
            </Button>
          </Space>
          {preview ? (
            <Alert
              showIcon
              type="info"
              title={t(
                'workflowActivityVNext.schedule.previewTitle',
                'Next scheduled fires',
              )}
              description={
                <ul className="wa-vnext__schedule-preview-list">
                  {preview.nextFireTimes.map((fireAt) => (
                    <li key={fireAt}>{formatScheduleDate(fireAt)}</li>
                  ))}
                </ul>
              }
            />
          ) : null}
        </div>
      </Modal>
    </div>
  );

  if (mode === 'panel') {
    return (
      <Drawer
        destroyOnHidden
        onClose={onClose}
        open={open}
        placement="right"
        rootClassName="wa-vnext-schedule-drawer"
        size={480}
        title={workflowName}
      >
        {body}
      </Drawer>
    );
  }

  return (
    <Modal
      destroyOnHidden
      footer={null}
      onCancel={onClose}
      open={open}
      title={workflowName}
      width={820}
    >
      {body}
    </Modal>
  );
};

export default WorkflowScheduleSurface;
