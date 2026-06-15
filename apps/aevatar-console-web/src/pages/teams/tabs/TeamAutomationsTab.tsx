import {
  ClockCircleOutlined,
  DeleteOutlined,
  EditOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  PlusOutlined,
} from "@ant-design/icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Button,
  Input,
  Modal,
  Select,
  Skeleton,
  Space,
  Switch,
  Tooltip,
  Typography,
  message,
  theme,
} from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import {
  scheduledDispatchApi,
  type ScheduledDispatchConfigurationInput,
  type ScheduledDispatchPreview,
  type ScheduledDispatchSummary,
} from "@/shared/api/scheduledDispatchApi";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import type { ServiceIdentity } from "@/shared/models/services";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  FactLine,
} from "../components/TeamDetailPrimitives";

export type TeamAutomationMemberRow = {
  readonly automationsHref: string;
  readonly canAutomateMember: boolean;
  readonly disabledReason: string;
  readonly implementationKind: string;
  readonly isSelectedMember?: boolean;
  readonly key: string;
  readonly lifecycleLabel: string;
  readonly lifecycleStyle: React.CSSProperties;
  readonly memberId: string;
  readonly name: string;
  readonly serviceId: string;
  readonly serviceIdentity?: ServiceIdentity;
  readonly workflowSupported: boolean;
};

type TeamAutomationsTabProps = {
  readonly members?: readonly TeamAutomationMemberRow[];
  readonly scopeId: string;
  readonly serviceIdentitiesLoading?: boolean;
  readonly teamId: string;
};

type AutomationFormState = {
  readonly cronExpression: string;
  readonly displayName: string;
  readonly enabled: boolean;
  readonly memberId: string;
  readonly preset: string;
  readonly prompt: string;
  readonly timezone: string;
};

const scheduleListTake = 200;
const customPreset = "custom";
const defaultPreset = "weekdays-0900";
const defaultCronExpression = "0 9 * * 1-5";

const pageGridStyle: React.CSSProperties = {
  alignItems: "start",
  display: "grid",
  gap: 18,
  gridTemplateColumns: "minmax(0, 1fr) minmax(300px, 360px)",
};

const responsiveStyle = `
@media (max-width: 960px) {
  .team-automations-layout {
    grid-template-columns: minmax(0, 1fr) !important;
  }

  .team-automation-row {
    grid-template-columns: minmax(0, 1fr) !important;
  }
}
`;

const panelHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  flexWrap: "wrap",
  gap: 12,
  justifyContent: "space-between",
  minWidth: 0,
};

const titleGroupStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 4,
  minWidth: 0,
};

const titleStyle: React.CSSProperties = {
  fontSize: 16,
  fontWeight: 800,
  lineHeight: "24px",
  margin: 0,
};

const commitmentGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 12,
};

const commitmentRowStyle: React.CSSProperties = {
  alignItems: "start",
  display: "grid",
  gap: 14,
  gridTemplateColumns:
    "minmax(220px, 1fr) minmax(170px, 0.68fr) minmax(155px, 0.58fr) max-content",
  minWidth: 0,
  padding: "16px 0",
};

const upcomingRowStyle: React.CSSProperties = {
  alignItems: "start",
  display: "grid",
  gap: 10,
  gridTemplateColumns: "28px minmax(0, 1fr)",
  minWidth: 0,
};

const modalFieldStyle: React.CSSProperties = {
  display: "grid",
  gap: 8,
};

function trimText(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function resolveDefaultTimezone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

function formatScheduleTime(value: string | null | undefined, fallback: string): string {
  return formatCompactDateTime(value, fallback);
}

function sortByNextFire(
  left: ScheduledDispatchSummary,
  right: ScheduledDispatchSummary,
): number {
  const leftTime = left.nextFireAt ? Date.parse(left.nextFireAt) : Number.MAX_SAFE_INTEGER;
  const rightTime = right.nextFireAt ? Date.parse(right.nextFireAt) : Number.MAX_SAFE_INTEGER;
  return leftTime - rightTime;
}

const TeamAutomationsTab: React.FC<TeamAutomationsTabProps> = ({
  members = [],
  scopeId,
  serviceIdentitiesLoading = false,
  teamId,
}) => {
  const intl = useIntl();
  const queryClient = useQueryClient();
  const { token } = theme.useToken();
  const automatableMembers = React.useMemo(
    () => members.filter((member) => member.canAutomateMember),
    [members],
  );
  const selectedMember =
    automatableMembers.find((member) => member.isSelectedMember) ??
    automatableMembers[0] ??
    null;
  const unavailableMembers = members.filter((member) => !member.canAutomateMember);
  const [createOpen, setCreateOpen] = React.useState(false);
  const [editingSchedule, setEditingSchedule] =
    React.useState<ScheduledDispatchSummary | null>(null);
  const [preview, setPreview] = React.useState<ScheduledDispatchPreview | null>(null);
  const [formState, setFormState] = React.useState<AutomationFormState>(() => ({
    cronExpression: defaultCronExpression,
    displayName: "",
    enabled: true,
    memberId: "",
    preset: defaultPreset,
    prompt: "",
    timezone: resolveDefaultTimezone(),
  }));
  const scheduleQueryKey = React.useMemo(
    () => ["scheduled-dispatches", "team", scopeId, teamId] as const,
    [scopeId, teamId],
  );
  const serviceIdToMember = React.useMemo(() => {
    const next = new Map<string, TeamAutomationMemberRow>();
    for (const member of automatableMembers) {
      const serviceId = trimText(member.serviceId);
      if (serviceId && serviceId !== "--") {
        next.set(serviceId, member);
      }
    }

    return next;
  }, [automatableMembers]);
  const schedulesQuery = useQuery({
    enabled: scopeId.length > 0 && teamId.length > 0,
    queryFn: () =>
      scheduledDispatchApi.list({
        includeTotalCount: true,
        take: scheduleListTake,
      }),
    queryKey: scheduleQueryKey,
    retry: false,
  });
  const teamSchedules = React.useMemo(
    () =>
      (schedulesQuery.data?.items ?? [])
        .filter(
          (schedule) =>
            !schedule.deleted && serviceIdToMember.has(trimText(schedule.serviceId)),
        )
        .sort(sortByNextFire),
    [schedulesQuery.data?.items, serviceIdToMember],
  );
  const activeFormMember =
    automatableMembers.find((member) => member.memberId === formState.memberId) ??
    selectedMember;
  const editingScheduleId = trimText(editingSchedule?.scheduleId);
  const isEditingAutomation = editingScheduleId.length > 0;
  const cronPresets = React.useMemo(
    () => [
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.weekdaysMorning",
          defaultMessage: "Weekdays · 09:00",
        }),
        value: defaultPreset,
        cronExpression: defaultCronExpression,
      },
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.dailyMorning",
          defaultMessage: "Daily · 09:00",
        }),
        value: "daily-0900",
        cronExpression: "0 9 * * *",
      },
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.weeklyMonday",
          defaultMessage: "Monday · 09:00",
        }),
        value: "weekly-monday-0900",
        cronExpression: "0 9 * * 1",
      },
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.hourly",
          defaultMessage: "Hourly",
        }),
        value: "hourly",
        cronExpression: "0 * * * *",
      },
      {
        label: intl.formatMessage({
          id: "teams.automations.form.preset.custom",
          defaultMessage: "Custom cron",
        }),
        value: customPreset,
        cronExpression: "",
      },
    ],
    [intl],
  );

  const invalidateSchedules = React.useCallback(async () => {
    await queryClient.invalidateQueries({
      queryKey: ["scheduled-dispatches"],
    });
  }, [queryClient]);

  const previewMutation = useMutation({
    mutationFn: scheduledDispatchApi.preview,
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.previewFailed",
            defaultMessage: "Preview failed: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: (result) => {
      setPreview(result);
    },
  });
  const createMutation = useMutation({
    mutationFn: scheduledDispatchApi.create,
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.createFailed",
            defaultMessage: "Automation was not created: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async () => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.createSuccess",
          defaultMessage: "Automation created.",
        }),
      );
      setCreateOpen(false);
      setPreview(null);
      await invalidateSchedules();
    },
  });
  const updateMutation = useMutation({
    mutationFn: ({
      input,
      scheduleId,
    }: {
      readonly input: ScheduledDispatchConfigurationInput;
      readonly scheduleId: string;
    }) => scheduledDispatchApi.update(scheduleId, input),
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.updateFailed",
            defaultMessage: "Automation was not updated: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async () => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.updateSuccess",
          defaultMessage: "Automation updated.",
        }),
      );
      setCreateOpen(false);
      setEditingSchedule(null);
      setPreview(null);
      await invalidateSchedules();
    },
  });
  const runNowMutation = useMutation({
    mutationFn: scheduledDispatchApi.runNow,
    onError: (error) => {
      void message.error(
        intl.formatMessage(
          {
            id: "teams.automations.messages.runNowFailed",
            defaultMessage: "Run request failed: {message}",
          },
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: async () => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.runNowSuccess",
          defaultMessage: "Run requested.",
        }),
      );
      await invalidateSchedules();
    },
  });
  const enableMutation = useMutation({
    mutationFn: (scheduleId: string) =>
      scheduledDispatchApi.enable(
        scheduleId,
        "Enabled from Team Automations",
      ),
    onSuccess: invalidateSchedules,
  });
  const disableMutation = useMutation({
    mutationFn: (scheduleId: string) =>
      scheduledDispatchApi.disable(
        scheduleId,
        "Disabled from Team Automations",
      ),
    onSuccess: invalidateSchedules,
  });
  const deleteMutation = useMutation({
    mutationFn: (scheduleId: string) =>
      scheduledDispatchApi.delete(
        scheduleId,
        "Deleted from Team Automations",
      ),
    onSuccess: async () => {
      void message.success(
        intl.formatMessage({
          id: "teams.automations.messages.deleteSuccess",
          defaultMessage: "Automation deleted.",
        }),
      );
      await invalidateSchedules();
    },
  });

  const openCreate = React.useCallback(() => {
    const member = selectedMember;
    setEditingSchedule(null);
    setFormState({
      cronExpression: defaultCronExpression,
      displayName: "",
      enabled: true,
      memberId: member?.memberId ?? "",
      preset: defaultPreset,
      prompt: "",
      timezone: resolveDefaultTimezone(),
    });
    setPreview(null);
    setCreateOpen(true);
  }, [selectedMember]);

  const openEdit = React.useCallback(
    (schedule: ScheduledDispatchSummary) => {
      const member =
        serviceIdToMember.get(trimText(schedule.serviceId)) ?? selectedMember;
      const cronExpression = trimText(schedule.cronExpression);
      const preset =
        cronPresets.find((item) => item.cronExpression === cronExpression)?.value ??
        customPreset;
      setEditingSchedule(schedule);
      setFormState({
        cronExpression,
        displayName: trimText(schedule.displayName),
        enabled: schedule.enabled,
        memberId: member?.memberId ?? "",
        preset,
        prompt: "",
        timezone: trimText(schedule.timezone) || resolveDefaultTimezone(),
      });
      setPreview(null);
      setCreateOpen(true);
    },
    [cronPresets, selectedMember, serviceIdToMember],
  );

  const updateForm = React.useCallback(
    (patch: Partial<AutomationFormState>) => {
      setFormState((current) => ({
        ...current,
        ...patch,
      }));
      setPreview(null);
    },
    [],
  );

  const previewNextRuns = React.useCallback(async () => {
    const cronExpression = formState.cronExpression.trim();
    if (!cronExpression) {
      void message.error(
        intl.formatMessage({
          id: "teams.automations.messages.cronRequired",
          defaultMessage: "Enter a cron expression first.",
        }),
      );
      return;
    }

    await previewMutation.mutateAsync({
      cronExpression,
      timezone: trimText(formState.timezone) || undefined,
      count: 5,
    });
  }, [
    formState.cronExpression,
    formState.timezone,
    intl,
    previewMutation,
  ]);

  const saveAutomation = React.useCallback(async () => {
    const member = activeFormMember;
    const serviceIdentity = member?.serviceIdentity;
    const prompt = formState.prompt.trim();
    const cronExpression = formState.cronExpression.trim();
    if (!member || !serviceIdentity) {
      void message.error(
        serviceIdentitiesLoading
          ? intl.formatMessage({
              id: "teams.automations.messages.serviceIdentityLoading",
              defaultMessage: "Service identity is still loading.",
            })
          : intl.formatMessage({
              id: "teams.automations.messages.serviceIdentityMissing",
              defaultMessage:
                "The selected member does not have a service identity yet.",
            }),
      );
      return;
    }
    if (!prompt) {
      void message.error(
        intl.formatMessage({
          id: "teams.automations.messages.promptRequired",
          defaultMessage: "Describe the recurring work before saving it.",
        }),
      );
      return;
    }
    if (!cronExpression) {
      void message.error(
        intl.formatMessage({
          id: "teams.automations.messages.cronRequired",
          defaultMessage: "Enter a cron expression first.",
        }),
      );
      return;
    }

    const input: ScheduledDispatchConfigurationInput = {
      displayName:
        formState.displayName.trim() ||
        intl.formatMessage(
          {
            id: "teams.automations.form.defaultTitle",
            defaultMessage: "{memberName} recurring work",
          },
          { memberName: member.name },
        ),
      cronExpression,
      timezone: trimText(formState.timezone) || undefined,
      enabled: formState.enabled,
      headers: {
        source: "team-automations",
      },
      workflowChatTarget: {
        identity: serviceIdentity,
        prompt,
      },
    };

    if (isEditingAutomation) {
      await updateMutation.mutateAsync({
        input,
        scheduleId: editingScheduleId,
      });
      return;
    }

    await createMutation.mutateAsync(input);
  }, [
    activeFormMember,
    createMutation,
    editingScheduleId,
    formState.cronExpression,
    formState.displayName,
    formState.enabled,
    formState.prompt,
    formState.timezone,
    intl,
    isEditingAutomation,
    serviceIdentitiesLoading,
    updateMutation,
  ]);

  const renderStatusPill = (schedule: ScheduledDispatchSummary) => {
    const attention = !schedule.enabled || Boolean(trimText(schedule.lastError));
    return (
      <DetailPill
        compact
        style={{
          background: attention ? token.colorWarningBg : token.colorSuccessBg,
          border: `1px solid ${
            attention ? token.colorWarningBorder : token.colorSuccessBorder
          }`,
          color: attention ? token.colorWarning : token.colorSuccess,
        }}
        text={
          schedule.enabled
            ? intl.formatMessage({
                id: "teams.automations.status.active",
                defaultMessage: "Active",
              })
            : intl.formatMessage({
                id: "teams.automations.status.paused",
                defaultMessage: "Paused",
              })
        }
      />
    );
  };

  const renderAutomationRows = () => {
    if (schedulesQuery.isLoading) {
      return (
        <div style={{ display: "grid", gap: 12 }}>
          {[0, 1, 2].map((index) => (
            <Skeleton.Input
              active
              block
              key={index}
              style={{ borderRadius: 8, height: 78 }}
            />
          ))}
        </div>
      );
    }

    if (schedulesQuery.isError) {
      return (
        <AevatarInspectorEmpty
          compact
          title={intl.formatMessage({
            id: "teams.automations.error.title",
            defaultMessage: "Automations could not load",
          })}
          description={intl.formatMessage({
            id: "teams.automations.error.description",
            defaultMessage:
              "Refresh the page or try again after the schedule service is available.",
          })}
        />
      );
    }

    if (teamSchedules.length === 0) {
      return (
        <AevatarInspectorEmpty
          compact
          title={intl.formatMessage({
            id: "teams.automations.empty.title",
            defaultMessage: "No recurring work yet",
          })}
          description={intl.formatMessage({
            id: "teams.automations.empty.description",
            defaultMessage:
              "Create an automation from a published member so this team has visible recurring commitments.",
          })}
        />
      );
    }

    return (
      <div style={commitmentGridStyle}>
        {teamSchedules.map((schedule, index) => {
          const member = serviceIdToMember.get(trimText(schedule.serviceId));
          const scheduleId = trimText(schedule.scheduleId);
          const statusMutation =
            schedule.enabled ? disableMutation : enableMutation;

          return (
            <div
              className="team-automation-row"
              key={scheduleId}
              style={{
                ...commitmentRowStyle,
                borderTop:
                  index === 0
                    ? "none"
                    : `1px solid ${token.colorBorderSecondary}`,
              }}
            >
              <div style={{ display: "grid", gap: 7, minWidth: 0 }}>
                <Typography.Text strong>
                  {trimText(schedule.displayName) ||
                    intl.formatMessage({
                      id: "teams.automations.untitled",
                      defaultMessage: "Untitled automation",
                    })}
                </Typography.Text>
                <FactLine
                  secondary
                  text={intl.formatMessage(
                    {
                      id: "teams.automations.row.target",
                      defaultMessage: "Workflow chat · {endpoint}",
                    },
                    { endpoint: schedule.serviceEndpointId || "chat" },
                  )}
                />
                <Space size={[8, 6]} wrap>
                  {renderStatusPill(schedule)}
                  {schedule.lastError ? (
                    <DetailPill
                      compact
                      style={{
                        background: token.colorErrorBg,
                        border: `1px solid ${token.colorErrorBorder}`,
                        color: token.colorError,
                      }}
                      text={intl.formatMessage({
                        id: "teams.automations.status.error",
                        defaultMessage: "Error",
                      })}
                    />
                  ) : null}
                </Space>
              </div>
              <div style={{ display: "grid", gap: 5, minWidth: 0 }}>
                <Typography.Text strong>
                  {member?.name ||
                    intl.formatMessage({
                      id: "teams.automations.member.unknown",
                      defaultMessage: "Unknown member",
                    })}
                </Typography.Text>
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {intl.formatMessage(
                    {
                      id: "teams.automations.preview.runsThroughService",
                      defaultMessage: "Runs through {serviceId}",
                    },
                    { serviceId: schedule.serviceId },
                  )}
                </Typography.Text>
              </div>
              <div style={{ display: "grid", gap: 5, minWidth: 0 }}>
                <Typography.Text strong>{schedule.cronExpression}</Typography.Text>
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {schedule.nextFireAt
                    ? intl.formatMessage(
                        {
                          id: "teams.automations.row.nextRun",
                          defaultMessage: "Next {time}",
                        },
                        {
                          time: formatScheduleTime(schedule.nextFireAt, "--"),
                        },
                      )
                    : intl.formatMessage({
                        id: "teams.automations.row.noNextRun",
                        defaultMessage: "No next run",
                      })}
                </Typography.Text>
              </div>
              <Space size={6} wrap>
                <Button
                  icon={<EditOutlined />}
                  onClick={() => openEdit(schedule)}
                  size="small"
                >
                  {intl.formatMessage({
                    id: "teams.automations.actions.edit",
                    defaultMessage: "Edit",
                  })}
                </Button>
                <Button
                  icon={<PlayCircleOutlined />}
                  loading={
                    runNowMutation.isPending &&
                    runNowMutation.variables === scheduleId
                  }
                  onClick={() => runNowMutation.mutate(scheduleId)}
                  size="small"
                >
                  {intl.formatMessage({
                    id: "teams.automations.actions.runNow",
                    defaultMessage: "Run now",
                  })}
                </Button>
                <Button
                  icon={
                    schedule.enabled ? (
                      <PauseCircleOutlined />
                    ) : (
                      <PlayCircleOutlined />
                    )
                  }
                  loading={
                    statusMutation.isPending &&
                    statusMutation.variables === scheduleId
                  }
                  onClick={() => statusMutation.mutate(scheduleId)}
                  size="small"
                >
                  {schedule.enabled
                    ? intl.formatMessage({
                        id: "teams.automations.actions.pause",
                        defaultMessage: "Pause",
                      })
                    : intl.formatMessage({
                        id: "teams.automations.actions.resume",
                        defaultMessage: "Resume",
                      })}
                </Button>
                <Tooltip
                  title={intl.formatMessage({
                    id: "teams.automations.actions.delete",
                    defaultMessage: "Delete",
                  })}
                >
                  <Button
                    danger
                    icon={<DeleteOutlined />}
                    loading={
                      deleteMutation.isPending &&
                      deleteMutation.variables === scheduleId
                    }
                    onClick={() => deleteMutation.mutate(scheduleId)}
                    size="small"
                  />
                </Tooltip>
              </Space>
            </div>
          );
        })}
      </div>
    );
  };

  const upcomingSchedules = teamSchedules
    .filter((schedule) => schedule.enabled && schedule.nextFireAt)
    .slice(0, 3);
  const canCreateAutomation = Boolean(activeFormMember?.serviceIdentity);
  const formSubmitting = createMutation.isPending || updateMutation.isPending;
  const formTitle = isEditingAutomation
    ? intl.formatMessage({
        id: "teams.automations.form.editTitle",
        defaultMessage: "Edit automation",
      })
    : intl.formatMessage({
        id: "teams.automations.form.title",
        defaultMessage: "New member automation",
      });
  const formOkText = isEditingAutomation
    ? intl.formatMessage({
        id: "teams.automations.form.save",
        defaultMessage: "Save changes",
      })
    : intl.formatMessage({
        id: "teams.automations.form.create",
        defaultMessage: "Create automation",
      });

  return (
    <div className="team-automations-layout" style={pageGridStyle}>
      <style>{responsiveStyle}</style>
      <AevatarPanel>
        <div style={panelHeaderStyle}>
          <div style={titleGroupStyle}>
            <Typography.Title level={3} style={titleStyle}>
              {intl.formatMessage({
                id: "teams.automations.title",
                defaultMessage: "Automations",
              })}
            </Typography.Title>
            <Typography.Text style={{ maxWidth: 680 }} type="secondary">
              {intl.formatMessage({
                id: "teams.automations.description",
                defaultMessage:
                  "Recurring work belongs to a member. The team view shows every commitment so operators can see what will run next and what needs attention.",
              })}
            </Typography.Text>
          </div>
          <Button
            disabled={!selectedMember}
            icon={<PlusOutlined />}
            onClick={openCreate}
            style={{ borderRadius: 999, fontWeight: 650 }}
            type="primary"
          >
            {intl.formatMessage({
              id: "teams.automations.actions.create",
              defaultMessage: "New automation",
            })}
          </Button>
        </div>
        {renderAutomationRows()}
      </AevatarPanel>

      <div style={{ display: "grid", gap: 16 }}>
        <AevatarPanel>
          <div style={{ display: "grid", gap: 14 }}>
            <div style={{ display: "grid", gap: 4 }}>
              <Typography.Title level={3} style={titleStyle}>
                {intl.formatMessage({
                  id: "teams.automations.createPanel.title",
                  defaultMessage: "Give a member recurring work",
                })}
              </Typography.Title>
              <Typography.Text type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.createPanel.description",
                  defaultMessage:
                    "Pick a published member, describe the job, choose a cadence, and preview the next runs before creating it.",
                })}
              </Typography.Text>
            </div>
            {selectedMember ? (
              <div
                style={{
                  background: token.colorFillQuaternary,
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 8,
                  display: "grid",
                  gap: 10,
                  padding: 14,
                }}
              >
                <Typography.Text strong>{selectedMember.name}</Typography.Text>
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {selectedMember.serviceId}
                </Typography.Text>
                <DetailPill
                  compact
                  style={selectedMember.lifecycleStyle}
                  text={selectedMember.lifecycleLabel}
                />
              </div>
            ) : (
              <AevatarInspectorEmpty
                compact
                title={intl.formatMessage({
                  id: "teams.automations.noPublishedMember.title",
                  defaultMessage: "Publish a member first",
                })}
                description={intl.formatMessage({
                  id: "teams.automations.noPublishedMember.description",
                  defaultMessage:
                    "Automations need a member with a published service identity before they can run.",
                })}
              />
            )}
            <Button
              block
              disabled={!selectedMember}
              icon={<ClockCircleOutlined />}
              onClick={openCreate}
              type="primary"
            >
              {intl.formatMessage({
                id: "teams.automations.actions.addRecurringWork",
                defaultMessage: "Add recurring work",
              })}
            </Button>
          </div>
        </AevatarPanel>

        <AevatarPanel>
          <div style={{ display: "grid", gap: 12 }}>
            <Typography.Title level={3} style={titleStyle}>
              {intl.formatMessage({
                id: "teams.automations.upcoming.title",
                defaultMessage: "Upcoming",
              })}
            </Typography.Title>
            {upcomingSchedules.length > 0 ? (
              upcomingSchedules.map((schedule) => {
                const member = serviceIdToMember.get(schedule.serviceId);

                return (
                  <div key={schedule.scheduleId} style={upcomingRowStyle}>
                    <div
                      style={{
                        alignItems: "center",
                        background: token.colorSuccessBg,
                        border: `1px solid ${token.colorSuccessBorder}`,
                        borderRadius: 999,
                        color: token.colorSuccess,
                        display: "inline-flex",
                        height: 28,
                        justifyContent: "center",
                        width: 28,
                      }}
                    >
                      <ClockCircleOutlined />
                    </div>
                    <div style={{ display: "grid", gap: 2, minWidth: 0 }}>
                      <Typography.Text strong>
                        {formatScheduleTime(schedule.nextFireAt, "--")}
                      </Typography.Text>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {member?.name
                          ? intl.formatMessage(
                              {
                                id: "teams.automations.upcoming.memberCaption",
                                defaultMessage: "{memberName} recurring work",
                              },
                              { memberName: member.name },
                            )
                          : intl.formatMessage({
                              id: "teams.automations.upcoming.scheduled.caption",
                              defaultMessage: "Scheduled teammate commitment",
                            })}
                      </Typography.Text>
                    </div>
                  </div>
                );
              })
            ) : (
              <Typography.Text type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.upcoming.empty",
                  defaultMessage: "No upcoming runs are visible yet.",
                })}
              </Typography.Text>
            )}
          </div>
        </AevatarPanel>

        {unavailableMembers.length > 0 ? (
          <AevatarPanel>
            <div style={{ display: "grid", gap: 10 }}>
              <Typography.Title level={3} style={titleStyle}>
                {intl.formatMessage({
                  id: "teams.automations.unavailable.title",
                  defaultMessage: "Not ready for automation",
                })}
              </Typography.Title>
              {unavailableMembers.slice(0, 3).map((member) => (
                <div key={member.key} style={{ display: "grid", gap: 2 }}>
                  <Typography.Text strong>{member.name}</Typography.Text>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {member.disabledReason}
                  </Typography.Text>
                </div>
              ))}
            </div>
          </AevatarPanel>
        ) : null}
      </div>

      <Modal
        confirmLoading={formSubmitting}
        okButtonProps={{
          disabled:
            !activeFormMember ||
            !formState.prompt.trim() ||
            !formState.cronExpression.trim() ||
                (!canCreateAutomation && !serviceIdentitiesLoading),
        }}
        okText={formOkText}
        onCancel={() => {
          if (!formSubmitting) {
            setCreateOpen(false);
            setEditingSchedule(null);
            setPreview(null);
          }
        }}
        onOk={() => void saveAutomation()}
        open={createOpen}
        title={formTitle}
        width={720}
      >
        <div style={{ display: "grid", gap: 16 }}>
          <div style={modalFieldStyle}>
            <Typography.Text strong>
              {intl.formatMessage({
                id: "teams.automations.form.member",
                defaultMessage: "Member",
              })}
            </Typography.Text>
            <Select
              aria-label={intl.formatMessage({
                id: "teams.automations.form.memberAria",
                defaultMessage: "Automation member",
              })}
              disabled={formSubmitting}
              onChange={(memberId) => updateForm({ memberId })}
              options={automatableMembers.map((member) => ({
                label: member.name,
                value: member.memberId,
              }))}
              value={activeFormMember?.memberId}
            />
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              {activeFormMember?.serviceIdentity
                ? intl.formatMessage(
                    {
                      id: "teams.automations.form.identityReady",
                      defaultMessage: "Targets published service {serviceId}.",
                    },
                    { serviceId: activeFormMember.serviceIdentity.serviceId },
                  )
                : intl.formatMessage({
                    id: "teams.automations.form.identityMissing",
                    defaultMessage:
                      "Waiting for this member's published service identity.",
                  })}
            </Typography.Text>
          </div>

          <div style={modalFieldStyle}>
            <Typography.Text strong>
              {intl.formatMessage({
                id: "teams.automations.form.displayName",
                defaultMessage: "Name",
              })}
            </Typography.Text>
            <Input
              aria-label={intl.formatMessage({
                id: "teams.automations.form.displayNameAria",
                defaultMessage: "Automation name",
              })}
              disabled={formSubmitting}
              onChange={(event) => updateForm({ displayName: event.target.value })}
              placeholder={intl.formatMessage({
                id: "teams.automations.form.displayNamePlaceholder",
                defaultMessage: "Daily escalation digest",
              })}
              value={formState.displayName}
            />
          </div>

          <div style={modalFieldStyle}>
            <Typography.Text strong>
              {intl.formatMessage({
                id: "teams.automations.form.prompt",
                defaultMessage: "Recurring prompt",
              })}
            </Typography.Text>
            <Input.TextArea
              aria-label={intl.formatMessage({
                id: "teams.automations.form.promptAria",
                defaultMessage: "Recurring prompt",
              })}
              autoSize={{ minRows: 4, maxRows: 7 }}
              disabled={formSubmitting}
              onChange={(event) => updateForm({ prompt: event.target.value })}
              placeholder={intl.formatMessage({
                id: "teams.automations.form.promptPlaceholder",
                defaultMessage:
                  "Summarize escalations, blocked accounts, and follow-up owners.",
              })}
              value={formState.prompt}
            />
            {isEditingAutomation ? (
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.form.editPromptHint",
                  defaultMessage: "Re-enter the recurring prompt to save changes.",
                })}
              </Typography.Text>
            ) : null}
          </div>

          <div
            style={{
              display: "grid",
              gap: 12,
              gridTemplateColumns: "minmax(0, 1fr) minmax(180px, 0.54fr)",
            }}
          >
            <div style={modalFieldStyle}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.cadence",
                  defaultMessage: "Cadence",
                })}
              </Typography.Text>
              <Select
                aria-label={intl.formatMessage({
                  id: "teams.automations.form.cadenceAria",
                  defaultMessage: "Automation cadence",
                })}
                disabled={formSubmitting}
                onChange={(preset) => {
                  const match = cronPresets.find((item) => item.value === preset);
                  updateForm({
                    preset,
                    cronExpression:
                      preset === customPreset
                        ? formState.cronExpression
                        : match?.cronExpression ?? formState.cronExpression,
                  });
                }}
                options={cronPresets.map(({ label, value }) => ({ label, value }))}
                value={formState.preset}
              />
            </div>
            <div style={modalFieldStyle}>
              <Typography.Text strong>
                {intl.formatMessage({
                  id: "teams.automations.form.enabled",
                  defaultMessage: "Enabled",
                })}
              </Typography.Text>
              <Switch
                checked={formState.enabled}
                disabled={formSubmitting}
                onChange={(enabled) => updateForm({ enabled })}
              />
            </div>
          </div>

          <div style={modalFieldStyle}>
            <Typography.Text strong>
              {intl.formatMessage({
                id: "teams.automations.form.cron",
                defaultMessage: "Cron expression",
              })}
            </Typography.Text>
            <Input
              aria-label={intl.formatMessage({
                id: "teams.automations.form.cronAria",
                defaultMessage: "Cron expression",
              })}
              disabled={formSubmitting}
              onChange={(event) =>
                updateForm({
                  cronExpression: event.target.value,
                  preset: customPreset,
                })
              }
              value={formState.cronExpression}
            />
          </div>

          <div style={modalFieldStyle}>
            <Typography.Text strong>
              {intl.formatMessage({
                id: "teams.automations.form.timezone",
                defaultMessage: "Timezone",
              })}
            </Typography.Text>
            <Input
              aria-label={intl.formatMessage({
                id: "teams.automations.form.timezoneAria",
                defaultMessage: "Timezone",
              })}
              disabled={formSubmitting}
              onChange={(event) => updateForm({ timezone: event.target.value })}
              value={formState.timezone}
            />
          </div>

          <div
            style={{
              background: token.colorFillQuaternary,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 8,
              display: "grid",
              gap: 10,
              padding: 12,
            }}
          >
            <Space align="center" wrap>
              <Button
                icon={<ClockCircleOutlined />}
                loading={previewMutation.isPending}
                onClick={() => void previewNextRuns()}
              >
                {intl.formatMessage({
                  id: "teams.automations.form.preview",
                  defaultMessage: "Preview next runs",
                })}
              </Button>
              <Typography.Text type="secondary">
                {intl.formatMessage({
                  id: "teams.automations.form.previewHint",
                  defaultMessage: "Preview uses the schedule service before saving.",
                })}
              </Typography.Text>
            </Space>
            {preview ? (
              <div style={{ display: "grid", gap: 4 }}>
                {preview.nextFireTimes.map((time) => (
                  <Typography.Text key={time} style={{ fontSize: 12 }}>
                    {formatScheduleTime(time, time)}
                  </Typography.Text>
                ))}
              </div>
            ) : null}
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default TeamAutomationsTab;
