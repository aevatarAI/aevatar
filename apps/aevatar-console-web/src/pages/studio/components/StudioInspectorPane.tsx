import { Button, Empty, Input, Select, Space, Tag, Typography } from 'antd';
import React from 'react';
import type { StudioNodeInspectorDraft } from '@/shared/studio/document';
import {
  STUDIO_GRAPH_CATEGORIES as STUDIO_GRAPH_PRIMITIVE_CATEGORIES,
  type StudioGraphRole,
  type StudioGraphStep,
} from '@/shared/studio/graph';
import type {
  StudioConnectorDefinition,
  StudioRoleDefinition,
  StudioValidationFinding,
} from '@/shared/studio/models';
import {
  cardListActionStyle,
  cardListHeaderStyle,
  cardListItemStyle,
  cardListMainStyle,
  cardListStyle,
  cardStackStyle,
  embeddedPanelStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from '@/shared/ui/proComponents';
import { AevatarHelpTooltip } from '@/shared/ui/aevatarPageShells';

type StudioInspectorTab = 'node' | 'roles' | 'yaml';

type InspectorNoticeLike = {
  readonly type: 'success' | 'warning' | 'error';
  readonly message: string;
};

type StudioInspectorPaneProps = {
  readonly draftYaml: string;
  readonly inspectorTab: StudioInspectorTab;
  readonly showTabSwitcher?: boolean;
  readonly workflowRoleIds: string[];
  readonly workflowStepIds: string[];
  readonly workflowRoles: StudioGraphRole[];
  readonly workflowSteps: StudioGraphStep[];
  readonly connectors: StudioConnectorDefinition[];
  readonly savedRoles: StudioRoleDefinition[];
  readonly selectedGraphRole: StudioGraphRole | null;
  readonly selectedGraphStep: StudioGraphStep | null;
  readonly nodeInspectorDraft: StudioNodeInspectorDraft | null;
  readonly inspectorPending: boolean;
  readonly inspectorNotice: InspectorNoticeLike | null;
  readonly validationLoading: boolean;
  readonly validationError: unknown;
  readonly validationFindings: StudioValidationFinding[];
  readonly parsedWorkflowName: string;
  readonly activeWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly onSetInspectorTab: (tab: StudioInspectorTab) => void;
  readonly onSetDraftYaml: (value: string) => void;
  readonly onValidateDraft: () => void;
  readonly onChangeNodeInspectorDraft: (
    draft: StudioNodeInspectorDraft,
  ) => void;
  readonly onApplyNodeChanges: () => void;
  readonly onInsertStep: () => void;
  readonly onAddWorkflowRole: () => void;
  readonly onUseSavedRole: (roleId: string) => void;
  readonly onUpdateWorkflowRole: (
    currentRoleId: string,
    nextRole: {
      readonly id: string;
      readonly name: string;
      readonly provider: string;
      readonly model: string;
      readonly systemPrompt: string;
      readonly connectors: readonly string[];
    },
  ) => void;
  readonly onDeleteConnection: (
    targetStepId: string,
    branchLabel?: string | null,
  ) => void;
  readonly onDeleteWorkflowRole: (roleId: string) => void;
  readonly onDeleteStep: () => void;
  readonly onResetSelectedNode: () => void;
};

type SummaryFieldProps = {
  copyable?: boolean;
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  tone?: 'default' | 'info' | 'success' | 'warning' | 'error';
  value: React.ReactNode;
};

type SectionHeaderProps = {
  action?: React.ReactNode;
  description?: React.ReactNode;
  help?: React.ReactNode;
  title: string;
};

type NoticeTone = {
  background: string;
  borderColor: string;
  tagColor: 'default' | 'processing' | 'success' | 'warning' | 'error';
  tagLabel: string;
};

type NoticePanelProps = {
  action?: React.ReactNode;
  children?: React.ReactNode;
  description?: React.ReactNode;
  title: React.ReactNode;
  type?: 'default' | 'info' | 'success' | 'warning' | 'error';
};

const summaryMetricToneMap: Record<
  NonNullable<SummaryMetricProps['tone']>,
  { color: string }
> = {
  default: { color: 'var(--ant-color-text)' },
  error: { color: 'var(--ant-color-error)' },
  info: { color: 'var(--ant-color-primary)' },
  success: { color: 'var(--ant-color-success)' },
  warning: { color: 'var(--ant-color-warning)' },
};

const sectionHeaderStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  gap: 12,
  justifyContent: 'space-between',
  width: '100%',
};

const sectionPanelStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const formGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
};

const yamlEditorStyle: React.CSSProperties = {
  background: 'var(--ant-color-fill-quaternary)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 10,
  fontFamily:
    "'SFMono-Regular', 'SF Mono', Consolas, 'Liberation Mono', Menlo, monospace",
  fontSize: 13,
  lineHeight: 1.5,
};

function hasValidationError(findings: StudioValidationFinding[]): boolean {
  return findings.some((item) =>
    String(item.level ?? '').toLowerCase().includes('error'),
  );
}

function getNoticeTone(
  type: NonNullable<NoticePanelProps['type']>,
): NoticeTone {
  switch (type) {
    case 'error':
      return {
        background: 'rgba(255, 241, 240, 0.96)',
        borderColor: 'rgba(255, 77, 79, 0.28)',
        tagColor: 'error',
        tagLabel: 'Error',
      };
    case 'info':
      return {
        background: 'rgba(240, 245, 255, 0.96)',
        borderColor: 'rgba(22, 119, 255, 0.24)',
        tagColor: 'processing',
        tagLabel: 'Info',
      };
    case 'success':
      return {
        background: 'rgba(246, 255, 237, 0.96)',
        borderColor: 'rgba(82, 196, 26, 0.28)',
        tagColor: 'success',
        tagLabel: 'Success',
      };
    case 'warning':
      return {
        background: 'rgba(255, 251, 230, 0.96)',
        borderColor: 'rgba(250, 173, 20, 0.28)',
        tagColor: 'warning',
        tagLabel: 'Warning',
      };
    default:
      return {
        background: 'var(--ant-color-fill-quaternary)',
        borderColor: 'var(--ant-color-border-secondary)',
        tagColor: 'default',
        tagLabel: 'Status',
      };
  }
}

function renderTextValue(
  value: React.ReactNode,
  copyable?: boolean,
): React.ReactNode {
  if (typeof value === 'string') {
    if (!value) {
      return <Typography.Text type="secondary">n/a</Typography.Text>;
    }

    return copyable ? (
      <Typography.Text copyable>{value}</Typography.Text>
    ) : (
      <Typography.Text>{value}</Typography.Text>
    );
  }

  if (typeof value === 'number') {
    return <Typography.Text>{value}</Typography.Text>;
  }

  return value;
}

const SummaryField: React.FC<SummaryFieldProps> = ({
  copyable,
  label,
  value,
}) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {renderTextValue(value, copyable)}
  </div>
);

const SummaryMetric: React.FC<SummaryMetricProps> = ({
  label,
  tone = 'default',
  value,
}) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text
      style={{
        ...summaryMetricValueStyle,
        color: summaryMetricToneMap[tone].color,
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

const SectionHeader: React.FC<SectionHeaderProps> = ({
  action,
  description,
  help,
  title,
}) => (
  <div style={sectionHeaderStyle}>
    <div style={{ minWidth: 0 }}>
      <div
        style={{
          alignItems: 'center',
          display: 'inline-flex',
          flexWrap: 'wrap',
          gap: 6,
          maxWidth: '100%',
        }}
      >
        <Typography.Text strong>{title}</Typography.Text>
        {help ? <AevatarHelpTooltip content={help} /> : null}
      </div>
      {description ? (
        <Typography.Paragraph style={{ margin: '4px 0 0' }} type="secondary">
          {description}
        </Typography.Paragraph>
      ) : null}
    </div>
    {action}
  </div>
);

const NoticePanel: React.FC<NoticePanelProps> = ({
  action,
  children,
  description,
  title,
  type = 'default',
}) => {
  const tone = getNoticeTone(type);

  return (
    <div
      style={{
        ...sectionPanelStyle,
        background: tone.background,
        borderColor: tone.borderColor,
      }}
    >
      <div style={sectionHeaderStyle}>
        <div style={{ minWidth: 0 }}>
          <Space wrap size={[8, 8]}>
            <Tag color={tone.tagColor}>{tone.tagLabel}</Tag>
            <Typography.Text strong>{title}</Typography.Text>
          </Space>
          {description ? (
            typeof description === 'string' ? (
              <Typography.Paragraph style={{ margin: '8px 0 0' }} type="secondary">
                {description}
              </Typography.Paragraph>
            ) : (
              <div style={{ marginTop: 8 }}>{description}</div>
            )
          ) : null}
        </div>
        {action}
      </div>
      {children}
    </div>
  );
};

function renderConnectorTags(connectors: readonly string[]): React.ReactNode {
  if (connectors.length === 0) {
    return <Typography.Text type="secondary">No connectors listed.</Typography.Text>;
  }

  return (
    <Space wrap size={[6, 6]}>
      {connectors.slice(0, 3).map((connector) => (
        <Tag key={connector}>{connector}</Tag>
      ))}
      {connectors.length > 3 ? <Tag>+{connectors.length - 3} more</Tag> : null}
    </Space>
  );
}

function renderInspectorNotice(
  inspectorNotice: InspectorNoticeLike | null,
): React.ReactNode {
  if (!inspectorNotice) {
    return null;
  }

  return (
    <NoticePanel
      type={inspectorNotice.type}
      title={
        inspectorNotice.type === 'success'
          ? 'Node changes applied'
          : inspectorNotice.type === 'warning'
            ? 'Node changes applied with warnings'
            : 'Node changes failed'
      }
      description={inspectorNotice.message}
    />
  );
}

function renderValidationState(
  draftYaml: string,
  validationError: unknown,
  validationLoading: boolean,
  validationFindings: StudioValidationFinding[],
): React.ReactNode {
  if (!draftYaml) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="Validation and YAML summary will appear after a draft is loaded."
      />
    );
  }

  if (validationError) {
    return (
      <NoticePanel
        type="error"
        title="Studio YAML validation failed"
        description={
          validationError instanceof Error
            ? validationError.message
            : String(validationError)
        }
      />
    );
  }

  if (validationLoading) {
    return (
      <NoticePanel
        type="info"
        title="Validating workflow YAML"
        description="Studio is parsing the active YAML through the workflow editor service."
      />
    );
  }

  if (validationFindings.length === 0) {
    return (
      <NoticePanel
        type="success"
        title="Validated by Studio editor"
        description="The active YAML parsed cleanly through the workflow editor service."
      />
    );
  }

  const preview = validationFindings.slice(0, 8);
  const hasError = hasValidationError(validationFindings);

  return (
    <NoticePanel
      type={hasError ? 'error' : 'warning'}
      title={`${validationFindings.length} validation finding(s)`}
      description="Review the highest-signal issues first before saving or running this workflow."
    >
      <div style={cardListStyle}>
        {preview.map((item) => (
          <div
            key={`${item.code || 'finding'}-${item.path || '/'}-${item.message}`}
            style={cardListItemStyle}
          >
            <div style={cardListHeaderStyle}>
              <div style={cardListMainStyle}>
                <Typography.Text strong>{item.path || '/'}</Typography.Text>
                <Typography.Text type="secondary">
                  {item.code || 'validation-finding'}
                </Typography.Text>
              </div>
              <Tag
                color={
                  String(item.level ?? '').toLowerCase().includes('error')
                    ? 'error'
                    : 'warning'
                }
              >
                {String(item.level || 'warning')}
              </Tag>
            </div>
            <Typography.Paragraph style={{ margin: 0 }}>
              {item.message}
            </Typography.Paragraph>
          </div>
        ))}
      </div>
      {validationFindings.length > preview.length ? (
        <Typography.Text type="secondary">
          +{validationFindings.length - preview.length} more finding(s) hidden.
        </Typography.Text>
      ) : null}
    </NoticePanel>
  );
}

const StudioInspectorPane: React.FC<StudioInspectorPaneProps> = ({
  draftYaml,
  inspectorTab,
  showTabSwitcher = true,
  workflowRoleIds,
  workflowStepIds,
  workflowRoles,
  workflowSteps,
  connectors,
  savedRoles,
  selectedGraphRole,
  selectedGraphStep,
  nodeInspectorDraft,
  inspectorPending,
  inspectorNotice,
  validationLoading,
  validationError,
  validationFindings,
  parsedWorkflowName,
  activeWorkflowName,
  activeWorkflowDescription,
  onSetInspectorTab,
  onSetDraftYaml,
  onValidateDraft,
  onChangeNodeInspectorDraft,
  onApplyNodeChanges,
  onInsertStep,
  onAddWorkflowRole,
  onUseSavedRole,
  onUpdateWorkflowRole,
  onDeleteConnection,
  onDeleteWorkflowRole,
  onDeleteStep,
  onResetSelectedNode,
}) => {
  const [roleSearch, setRoleSearch] = React.useState('');
  const [expandedRoleId, setExpandedRoleId] = React.useState<string | null>(null);

  const selectedStepConnections = React.useMemo(() => {
    if (!selectedGraphStep) {
      return [];
    }

    const items: Array<{
      key: string;
      label: string;
      targetStepId: string;
      branchLabel?: string;
    }> = [];

    if (selectedGraphStep.next) {
      items.push({
        key: `next:${selectedGraphStep.next}`,
        label: 'next',
        targetStepId: selectedGraphStep.next,
      });
    }

    Object.entries(selectedGraphStep.branches ?? {}).forEach(
      ([branchLabel, targetStepId]) => {
        if (!targetStepId) {
          return;
        }

        items.push({
          key: `branch:${branchLabel}:${targetStepId}`,
          label: branchLabel,
          targetStepId,
          branchLabel,
        });
      },
    );

    return items;
  }, [selectedGraphStep]);

  const selectedConnectorName = React.useMemo(() => {
    if (nodeInspectorDraft?.kind !== 'step' || nodeInspectorDraft.type !== 'connector_call') {
      return '';
    }

    try {
      return String(
        JSON.parse(nodeInspectorDraft.parametersText || '{}').connector || '',
      );
    } catch {
      return '';
    }
  }, [nodeInspectorDraft]);

  const filteredSavedRoles = React.useMemo(() => {
    const keyword = roleSearch.trim().toLowerCase();
    if (!keyword) {
      return savedRoles;
    }

    return savedRoles.filter((role) =>
      [role.id, role.name, role.provider, role.model].some((value) =>
        value.toLowerCase().includes(keyword),
      ),
    );
  }, [roleSearch, savedRoles]);

  const filteredWorkflowRoles = React.useMemo(() => {
    const keyword = roleSearch.trim().toLowerCase();
    if (!keyword) {
      return workflowRoles;
    }

    return workflowRoles.filter((role) =>
      [role.id, role.name, role.provider, role.model].some((value) =>
        value.toLowerCase().includes(keyword),
      ),
    );
  }, [roleSearch, workflowRoles]);

  const nodeInspectorContent =
    selectedGraphStep && nodeInspectorDraft?.kind === 'step' ? (
      <div style={cardStackStyle}>
        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Step summary"
            help="A compact view of the currently selected step before you edit fields."
          />
          <div style={summaryMetricGridStyle}>
            <SummaryMetric label="Primitive" tone="info" value={selectedGraphStep.type} />
            <SummaryMetric
              label="Connections"
              value={selectedStepConnections.length}
            />
            <SummaryMetric
              label="Branches"
              value={Object.keys(selectedGraphStep.branches ?? {}).length}
            />
            <SummaryMetric
              label="Connector mode"
              tone={nodeInspectorDraft.type === 'connector_call' ? 'warning' : 'default'}
              value={
                nodeInspectorDraft.type === 'connector_call'
                  ? selectedConnectorName || 'Pending'
                  : 'Direct'
              }
            />
          </div>
          <div style={summaryFieldGridStyle}>
            <SummaryField
              label="Current step"
              value={<Typography.Text code>{selectedGraphStep.id}</Typography.Text>}
            />
            <SummaryField
              label="Target role"
              value={selectedGraphStep.targetRole || 'Unassigned'}
            />
            <SummaryField
              label="Next step"
              value={selectedGraphStep.next || 'None'}
            />
            <SummaryField
              label="Draft step ID"
              value={<Typography.Text code>{nodeInspectorDraft.id}</Typography.Text>}
            />
          </div>
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Identity and routing"
            help="Update the step name, primitive type, and graph links."
          />
          <div style={formGridStyle}>
            <div style={cardStackStyle}>
              <Typography.Text strong>Step ID</Typography.Text>
              <Input
                aria-label="Studio step id"
                value={nodeInspectorDraft.id}
                onChange={(event) =>
                  onChangeNodeInspectorDraft({
                    ...nodeInspectorDraft,
                    id: event.target.value,
                  })
                }
              />
            </div>
            <div style={cardStackStyle}>
              <Typography.Text strong>Primitive</Typography.Text>
              <Select
                aria-label="Studio step type"
                value={nodeInspectorDraft.type}
                options={STUDIO_GRAPH_PRIMITIVE_CATEGORIES.map((category) => ({
                  label: category.label,
                  options: category.items.map((item) => ({
                    label: item,
                    value: item,
                  })),
                }))}
                onChange={(value) =>
                  onChangeNodeInspectorDraft({
                    ...nodeInspectorDraft,
                    type: value,
                  })
                }
              />
            </div>
            <div style={cardStackStyle}>
              <Typography.Text strong>Target role</Typography.Text>
              <Select
                aria-label="Studio step target role"
                allowClear
                placeholder="optional"
                value={nodeInspectorDraft.targetRole}
                options={workflowRoleIds.map((roleId) => ({
                  label: roleId,
                  value: roleId,
                }))}
                onChange={(value) =>
                  onChangeNodeInspectorDraft({
                    ...nodeInspectorDraft,
                    targetRole: value || '',
                  })
                }
              />
            </div>
            <div style={cardStackStyle}>
              <Typography.Text strong>Next step</Typography.Text>
              <Input
                aria-label="Studio step next"
                list="studio-workflow-step-options"
                placeholder="optional"
                value={nodeInspectorDraft.next}
                onChange={(event) =>
                  onChangeNodeInspectorDraft({
                    ...nodeInspectorDraft,
                    next: event.target.value,
                  })
                }
              />
            </div>
          </div>
          <div style={cardStackStyle}>
            <Typography.Text strong>Branches</Typography.Text>
            <Input.TextArea
              aria-label="Studio step branches"
              autoSize={{ minRows: 5, maxRows: 12 }}
              value={nodeInspectorDraft.branchesText}
              onChange={(event) =>
                onChangeNodeInspectorDraft({
                  ...nodeInspectorDraft,
                  branchesText: event.target.value,
                })
              }
            />
            <Typography.Text type="secondary">
              Edit branches as a JSON object of label to target step ID.
            </Typography.Text>
          </div>
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Parameters"
            help="Keep runtime inputs readable and wire connector calls explicitly."
          />
          {nodeInspectorDraft.type === 'connector_call' ? (
            <div style={cardStackStyle}>
              <Typography.Text strong>Connector</Typography.Text>
              <Select
                aria-label="Studio connector call connector"
                allowClear
                placeholder="Select connector"
                value={selectedConnectorName || undefined}
                options={connectors.map((connector) => ({
                  label: `${connector.name} · ${connector.type}`,
                  value: connector.name,
                }))}
                onChange={(value) => {
                  try {
                    const parsed = JSON.parse(nodeInspectorDraft.parametersText || '{}');
                    onChangeNodeInspectorDraft({
                      ...nodeInspectorDraft,
                      parametersText: JSON.stringify(
                        {
                          ...(parsed && typeof parsed === 'object' ? parsed : {}),
                          connector: value || '',
                        },
                        null,
                        2,
                      ),
                    });
                  } catch {
                    onChangeNodeInspectorDraft({
                      ...nodeInspectorDraft,
                      parametersText: JSON.stringify(
                        { connector: value || '' },
                        null,
                        2,
                      ),
                    });
                  }
                }}
              />
            </div>
          ) : null}
          <div style={cardStackStyle}>
            <Typography.Text strong>Parameters</Typography.Text>
            <Input.TextArea
              aria-label="Studio step parameters"
              autoSize={{ minRows: 8, maxRows: 16 }}
              value={nodeInspectorDraft.parametersText}
              onChange={(event) =>
                onChangeNodeInspectorDraft({
                  ...nodeInspectorDraft,
                  parametersText: event.target.value,
                })
              }
            />
            <Typography.Text type="secondary">
              Edit parameters as a JSON object.
            </Typography.Text>
          </div>
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Outgoing connections"
            help="Inspect and remove the graph links owned by this step."
          />
          {selectedStepConnections.length > 0 ? (
            <div style={cardListStyle}>
              {selectedStepConnections.map((connection) => (
                <div key={connection.key} style={cardListItemStyle}>
                  <div style={cardListHeaderStyle}>
                    <div style={cardListMainStyle}>
                      <Typography.Text strong>{connection.label}</Typography.Text>
                      <Typography.Text type="secondary">
                        {connection.targetStepId}
                      </Typography.Text>
                    </div>
                    <div style={cardListActionStyle}>
                      <Button
                        danger
                        size="small"
                        onClick={() =>
                          onDeleteConnection(
                            connection.targetStepId,
                            connection.branchLabel,
                          )
                        }
                      >
                        Remove
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="No outgoing connections"
            />
          )}
        </div>

        {renderInspectorNotice(inspectorNotice)}

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Step actions"
            help="Apply the edited draft or update the workflow graph around this step."
          />
          <div style={cardListActionStyle}>
            <Button
              type="primary"
              loading={inspectorPending}
              onClick={onApplyNodeChanges}
            >
              Apply node changes
            </Button>
            <Button loading={inspectorPending} onClick={onInsertStep}>
              Add step after
            </Button>
            <Button danger loading={inspectorPending} onClick={onDeleteStep}>
              Delete step
            </Button>
            <Button onClick={onResetSelectedNode}>Reset fields</Button>
          </div>
        </div>
      </div>
    ) : selectedGraphRole && nodeInspectorDraft?.kind === 'role' ? (
      <div style={cardStackStyle}>
        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Role summary"
            help="Review the selected role before editing provider, model, or prompt details."
          />
          <div style={summaryMetricGridStyle}>
            <SummaryMetric
              label="Provider"
              tone="info"
              value={selectedGraphRole.provider || 'n/a'}
            />
            <SummaryMetric label="Model" value={selectedGraphRole.model || 'n/a'} />
            <SummaryMetric
              label="Connectors"
              value={selectedGraphRole.connectors.length}
            />
            <SummaryMetric
              label="Prompt"
              value={selectedGraphRole.systemPrompt ? 'Configured' : 'Empty'}
              tone={selectedGraphRole.systemPrompt ? 'success' : 'warning'}
            />
          </div>
          <div style={summaryFieldGridStyle}>
            <SummaryField
              label="Current role"
              value={<Typography.Text code>{selectedGraphRole.id}</Typography.Text>}
            />
            <SummaryField label="Role name" value={selectedGraphRole.name || 'n/a'} />
          </div>
          <div>
            <Typography.Text style={summaryFieldLabelStyle}>Allowed connectors</Typography.Text>
            <div style={{ marginTop: 8 }}>{renderConnectorTags(selectedGraphRole.connectors)}</div>
          </div>
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Role details"
            help="Keep role identity, model configuration, and prompt text in one place."
          />
          <div style={formGridStyle}>
            <div style={cardStackStyle}>
              <Typography.Text strong>Role ID</Typography.Text>
              <Input
                aria-label="Studio role id"
                value={nodeInspectorDraft.id}
                onChange={(event) =>
                  onChangeNodeInspectorDraft({
                    ...nodeInspectorDraft,
                    id: event.target.value,
                  })
                }
              />
            </div>
            <div style={cardStackStyle}>
              <Typography.Text strong>Role name</Typography.Text>
              <Input
                aria-label="Studio role name"
                value={nodeInspectorDraft.name}
                onChange={(event) =>
                  onChangeNodeInspectorDraft({
                    ...nodeInspectorDraft,
                    name: event.target.value,
                  })
                }
              />
            </div>
            <div style={cardStackStyle}>
              <Typography.Text strong>Provider</Typography.Text>
              <Input
                aria-label="Studio role provider"
                value={nodeInspectorDraft.provider}
                onChange={(event) =>
                  onChangeNodeInspectorDraft({
                    ...nodeInspectorDraft,
                    provider: event.target.value,
                  })
                }
              />
            </div>
            <div style={cardStackStyle}>
              <Typography.Text strong>Model</Typography.Text>
              <Input
                aria-label="Studio role model"
                value={nodeInspectorDraft.model}
                onChange={(event) =>
                  onChangeNodeInspectorDraft({
                    ...nodeInspectorDraft,
                    model: event.target.value,
                  })
                }
              />
            </div>
          </div>
          <div style={cardStackStyle}>
            <Typography.Text strong>System prompt</Typography.Text>
            <Input.TextArea
              aria-label="Studio role system prompt"
              autoSize={{ minRows: 4, maxRows: 10 }}
              value={nodeInspectorDraft.systemPrompt}
              onChange={(event) =>
                onChangeNodeInspectorDraft({
                  ...nodeInspectorDraft,
                  systemPrompt: event.target.value,
                })
              }
            />
          </div>
          <div style={cardStackStyle}>
            <Typography.Text strong>Allowed connectors</Typography.Text>
            <Input.TextArea
              aria-label="Studio role connectors"
              autoSize={{ minRows: 3, maxRows: 8 }}
              value={nodeInspectorDraft.connectorsText}
              onChange={(event) =>
                onChangeNodeInspectorDraft({
                  ...nodeInspectorDraft,
                  connectorsText: event.target.value,
                })
              }
            />
            <Typography.Text type="secondary">
              One connector per line, or use commas.
            </Typography.Text>
          </div>
        </div>

        {renderInspectorNotice(inspectorNotice)}

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Role actions"
            help="Apply the updated role fields back into the workflow draft."
          />
          <div style={cardListActionStyle}>
            <Button
              type="primary"
              loading={inspectorPending}
              onClick={onApplyNodeChanges}
            >
              Apply node changes
            </Button>
            <Button onClick={onResetSelectedNode}>Reset fields</Button>
          </div>
        </div>
      </div>
    ) : (
      <div
        style={{
          alignItems: 'center',
          color: '#8C8C8C',
          display: 'flex',
          flexDirection: 'column',
          gap: 8,
          justifyContent: 'center',
          minHeight: 180,
          textAlign: 'center',
        }}
      >
        <Typography.Text type="secondary">
          请先在画布里选择一个步骤或角色。
        </Typography.Text>
      </div>
    );

  const rolesInspectorContent =
    workflowRoles.length > 0 || savedRoles.length > 0 ? (
      <div style={cardStackStyle}>
        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Role library"
            help="Search saved roles, add new workflow roles, and expand the ones that need edits."
            action={<Button onClick={onAddWorkflowRole}>Add role</Button>}
          />
          <div style={summaryMetricGridStyle}>
            <SummaryMetric label="Saved roles" value={savedRoles.length} />
            <SummaryMetric label="Workflow roles" value={workflowRoles.length} />
            <SummaryMetric
              label="Filtered saved"
              value={filteredSavedRoles.length}
            />
            <SummaryMetric
              label="Filtered workflow"
              value={filteredWorkflowRoles.length}
            />
          </div>
          <Input
            allowClear
            aria-label="Studio roles search"
            placeholder="Search saved roles"
            value={roleSearch}
            onChange={(event) => setRoleSearch(event.target.value)}
          />
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Saved roles"
            help="Promote a catalog role into the active workflow when you want a reusable starting point."
          />
          {filteredSavedRoles.length > 0 ? (
            <div style={cardListStyle}>
              {filteredSavedRoles.map((role) => (
                <div key={`saved:${role.id}`} style={cardListItemStyle}>
                  <div style={cardListHeaderStyle}>
                    <div style={cardListMainStyle}>
                      <Typography.Text strong>{role.name || role.id}</Typography.Text>
                      <Typography.Text type="secondary">{role.id}</Typography.Text>
                      <Space wrap size={[6, 6]}>
                        {role.provider ? <Tag>{role.provider}</Tag> : null}
                        {role.model ? <Tag>{role.model}</Tag> : null}
                        {role.connectors.length > 0 ? (
                          <Tag color="processing">
                            {role.connectors.length} connector(s)
                          </Tag>
                        ) : null}
                      </Space>
                    </div>
                    <div style={cardListActionStyle}>
                      <Button onClick={() => onUseSavedRole(role.id)}>Use</Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="No saved roles matched"
            />
          )}
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Workflow roles"
            help="Expand a role to edit its ID, model, system prompt, and connector allow-list."
          />
          {filteredWorkflowRoles.length > 0 ? (
            <div style={cardListStyle}>
              {filteredWorkflowRoles.map((role) => {
                const expanded = expandedRoleId === role.id;

                return (
                  <div key={`workflow:${role.id}`} style={cardListItemStyle}>
                    <div style={cardListHeaderStyle}>
                      <div style={cardListMainStyle}>
                        <Typography.Text strong>{role.name || role.id}</Typography.Text>
                        <Typography.Text type="secondary">{role.id}</Typography.Text>
                        <Space wrap size={[6, 6]}>
                          {role.provider ? <Tag>{role.provider}</Tag> : null}
                          {role.model ? <Tag>{role.model}</Tag> : null}
                          {role.connectors.length > 0 ? (
                            <Tag color="processing">
                              {role.connectors.length} connector(s)
                            </Tag>
                          ) : null}
                        </Space>
                      </div>
                      <div style={cardListActionStyle}>
                        <Button
                          type="link"
                          style={{ paddingInline: 0 }}
                          onClick={() =>
                            setExpandedRoleId((current) =>
                              current === role.id ? null : role.id,
                            )
                          }
                        >
                          {expanded ? 'Collapse' : 'Edit'}
                        </Button>
                        <Button
                          danger
                          size="small"
                          onClick={() => onDeleteWorkflowRole(role.id)}
                        >
                          Remove
                        </Button>
                      </div>
                    </div>

                    {expanded ? (
                      <div style={{ ...cardStackStyle, gap: 12 }}>
                        <div style={formGridStyle}>
                          <Input
                            aria-label={`Workflow role id ${role.id}`}
                            value={role.id}
                            onChange={(event) =>
                              onUpdateWorkflowRole(role.id, {
                                id: event.target.value,
                                name: role.name,
                                provider: role.provider,
                                model: role.model,
                                systemPrompt: role.systemPrompt,
                                connectors: role.connectors,
                              })
                            }
                          />
                          <Input
                            aria-label={`Workflow role name ${role.id}`}
                            value={role.name}
                            onChange={(event) =>
                              onUpdateWorkflowRole(role.id, {
                                id: role.id,
                                name: event.target.value,
                                provider: role.provider,
                                model: role.model,
                                systemPrompt: role.systemPrompt,
                                connectors: role.connectors,
                              })
                            }
                          />
                          <Input
                            aria-label={`Workflow role provider ${role.id}`}
                            value={role.provider}
                            onChange={(event) =>
                              onUpdateWorkflowRole(role.id, {
                                id: role.id,
                                name: role.name,
                                provider: event.target.value,
                                model: role.model,
                                systemPrompt: role.systemPrompt,
                                connectors: role.connectors,
                              })
                            }
                          />
                          <Input
                            aria-label={`Workflow role model ${role.id}`}
                            value={role.model}
                            onChange={(event) =>
                              onUpdateWorkflowRole(role.id, {
                                id: role.id,
                                name: role.name,
                                provider: role.provider,
                                model: event.target.value,
                                systemPrompt: role.systemPrompt,
                                connectors: role.connectors,
                              })
                            }
                          />
                        </div>
                        <Input.TextArea
                          aria-label={`Workflow role system prompt ${role.id}`}
                          autoSize={{ minRows: 4, maxRows: 10 }}
                          value={role.systemPrompt}
                          onChange={(event) =>
                            onUpdateWorkflowRole(role.id, {
                              id: role.id,
                              name: role.name,
                              provider: role.provider,
                              model: role.model,
                              systemPrompt: event.target.value,
                              connectors: role.connectors,
                            })
                          }
                        />
                        <Input.TextArea
                          aria-label={`Workflow role connectors ${role.id}`}
                          autoSize={{ minRows: 3, maxRows: 8 }}
                          value={role.connectors.join('\n')}
                          onChange={(event) =>
                            onUpdateWorkflowRole(role.id, {
                              id: role.id,
                              name: role.name,
                              provider: role.provider,
                              model: role.model,
                              systemPrompt: role.systemPrompt,
                              connectors: event.target.value
                                .split(/\r?\n|,/)
                                .map((item) => item.trim())
                                .filter(Boolean),
                            })
                          }
                        />
                      </div>
                    ) : null}
                  </div>
                );
              })}
            </div>
          ) : (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="No workflow roles matched"
            />
          )}
        </div>
      </div>
    ) : (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="No workflow roles were parsed from the active draft."
      />
    );

  const yamlInspectorContent = (
    <div style={cardStackStyle}>
      <div style={sectionPanelStyle}>
        <SectionHeader
          title="YAML workspace"
          help="Edit the source document directly, then validate it before saving or running."
          action={
            <Space wrap size={[8, 8]}>
              <Button onClick={onValidateDraft}>Validate</Button>
              <Button
                onClick={() => {
                  void navigator.clipboard?.writeText(draftYaml || '');
                }}
              >
                Copy
              </Button>
            </Space>
          }
        />
        <div style={summaryMetricGridStyle}>
          <SummaryMetric
            label="Parsed roles"
            value={workflowRoles.length}
            tone="info"
          />
          <SummaryMetric
            label="Parsed steps"
            value={workflowSteps.length}
            tone="info"
          />
          <SummaryMetric
            label="Findings"
            value={validationFindings.length}
            tone={
              validationLoading
                ? 'info'
                : validationFindings.length === 0
                  ? 'success'
                  : hasValidationError(validationFindings)
                    ? 'error'
                    : 'warning'
            }
          />
          <SummaryMetric
            label="Draft"
            value={draftYaml ? 'Loaded' : 'Empty'}
            tone={draftYaml ? 'success' : 'warning'}
          />
        </div>
        <div style={summaryFieldGridStyle}>
          <SummaryField
            label="Parsed workflow"
            value={parsedWorkflowName || activeWorkflowName || 'n/a'}
          />
          <SummaryField
            label="Validation status"
            value={
              validationLoading
                ? 'In progress'
                : validationFindings.length === 0
                  ? 'Clean'
                  : hasValidationError(validationFindings)
                    ? 'Needs fixes'
                    : 'Warnings only'
            }
          />
        </div>
        <div>
          <Typography.Text style={summaryFieldLabelStyle}>Description</Typography.Text>
          <Typography.Paragraph
            ellipsis={{ rows: 3, expandable: true, symbol: 'more' }}
            style={{ margin: '8px 0 0', whiteSpace: 'pre-wrap' }}
          >
            {activeWorkflowDescription || 'No description'}
          </Typography.Paragraph>
        </div>
      </div>

      <div style={sectionPanelStyle}>
        <SectionHeader
          title="Workflow YAML"
          help="Direct source editing stays available here, but validation and summary stay separated above and below."
        />
        <Input.TextArea
          aria-label="Studio workflow yaml panel"
          autoSize={{ minRows: 14, maxRows: 24 }}
          spellCheck={false}
          value={draftYaml}
          onChange={(event) => onSetDraftYaml(event.target.value)}
          style={yamlEditorStyle}
        />
      </div>

      <div style={sectionPanelStyle}>
        <SectionHeader
          title="Validation digest"
          help="Studio keeps the most important parsing feedback visible without taking over the whole inspector."
        />
        {renderValidationState(
          draftYaml,
          validationError,
          validationLoading,
          validationFindings,
        )}
      </div>
    </div>
  );

  const hasSelectedNode = Boolean(selectedGraphRole || selectedGraphStep);
  const selectedNodeLabel = selectedGraphStep
    ? `Step · ${selectedGraphStep.id}`
    : selectedGraphRole
      ? `Role · ${selectedGraphRole.id}`
      : 'No selection';

  return (
    <div style={cardStackStyle}>
      <datalist id="studio-workflow-step-options">
        {workflowStepIds.map((stepId) => (
          <option key={stepId} value={stepId} />
        ))}
      </datalist>

      {showTabSwitcher ? (
        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Inspector views"
            help="Switch between node edits, reusable roles, and the underlying YAML without leaving the current drawer."
          />
          <Space wrap size={[8, 8]}>
            <Button
              type={inspectorTab === 'node' ? 'primary' : 'default'}
              disabled={!hasSelectedNode}
              onClick={() => onSetInspectorTab('node')}
            >
              Node
            </Button>
            <Button
              type={inspectorTab === 'roles' ? 'primary' : 'default'}
              onClick={() => onSetInspectorTab('roles')}
            >
              Roles
            </Button>
            <Button
              type={inspectorTab === 'yaml' ? 'primary' : 'default'}
              onClick={() => onSetInspectorTab('yaml')}
            >
              YAML
            </Button>
            <Tag color={hasSelectedNode ? 'processing' : 'default'}>
              {selectedNodeLabel}
            </Tag>
          </Space>
        </div>
      ) : null}

      {inspectorTab === 'node'
        ? nodeInspectorContent
        : inspectorTab === 'roles'
          ? rolesInspectorContent
          : yamlInspectorContent}
    </div>
  );
};

export default StudioInspectorPane;
