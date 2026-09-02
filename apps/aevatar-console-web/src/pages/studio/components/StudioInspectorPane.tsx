import { Alert, Button, Empty, Input, Select, Space, Tag, Typography } from 'antd';
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
  buildNodeConfigFields,
  formatNodeConfigFieldCopy,
  updateNodeConfigFieldParametersText,
  validateNodeConfigParametersText,
  type NodeConfigField,
} from '@/shared/studio/nodeConfigFields';
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
import ConsoleOperationNotice from '@/shared/ui/ConsoleOperationNotice';
import { t } from "@/shared/i18n/messages";

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

type NodeConfigFieldsEditorProps = {
  readonly fields: readonly NodeConfigField[];
  readonly onChangeFieldValue: (field: NodeConfigField, value: string) => void;
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

const fieldDescriptionStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-secondary)',
  fontSize: 12,
  lineHeight: '18px',
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

const NodeConfigFieldsEditor: React.FC<NodeConfigFieldsEditorProps> = ({
  fields,
  onChangeFieldValue,
}) => {
  if (fields.length === 0) {
    return (
      <Typography.Text type="secondary">
        {t("pages.studio.studioinspectorpane.no.structured.parameters", "No structured parameters inferred. Edit the raw JSON below.")}
      </Typography.Text>
    );
  }

  return (
    <div style={formGridStyle}>
      {fields.map((field) => {
        const inputId = `studio-node-config-field-${field.name}`;
        const label = formatNodeConfigFieldCopy(field.label);
        const description = formatNodeConfigFieldCopy(field.description);
        const placeholder = formatNodeConfigFieldCopy(field.placeholder);
        const ariaLabel = `Parameter ${label}`;

        return (
          <div key={field.name} style={cardStackStyle}>
            <Typography.Text strong>
              {label}
              {field.required ? ' *' : ''}
            </Typography.Text>
            {field.kind === 'select' ? (
              <Select
                allowClear={!field.required}
                aria-label={ariaLabel}
                id={inputId}
                options={field.options.map((option) => ({
                  label: formatNodeConfigFieldCopy(option.label),
                  value: option.value,
                }))}
                placeholder={placeholder}
                value={field.value || undefined}
                onChange={(value) => onChangeFieldValue(field, String(value || ''))}
              />
            ) : field.kind === 'json' ? (
              <Input.TextArea
                aria-label={ariaLabel}
                autoSize={{ minRows: 3, maxRows: 8 }}
                id={inputId}
                placeholder={placeholder}
                value={field.value}
                onChange={(event) =>
                  onChangeFieldValue(field, event.target.value)
                }
              />
            ) : (
              <Input
                aria-label={ariaLabel}
                id={inputId}
                placeholder={placeholder}
                value={field.value}
                onChange={(event) =>
                  onChangeFieldValue(field, event.target.value)
                }
              />
            )}
            <Typography.Text style={fieldDescriptionStyle}>
              {description}
            </Typography.Text>
          </div>
        );
      })}
    </div>
  );
};

function renderConnectorTags(connectors: readonly string[]): React.ReactNode {
  if (connectors.length === 0) {
    return <Typography.Text type="secondary">{t("pages.studio.studioinspectorpane.no.connectors.listed", "No connectors listed.")}</Typography.Text>;
  }

  return (
    <Space wrap size={[6, 6]}>
      {connectors.slice(0, 3).map((connector) => (
        <Tag key={connector}>{connector}</Tag>
      ))}
      {connectors.length > 3 ? <Tag>+{connectors.length - 3} {t("pages.studio.studioinspectorpane.more", "more")}</Tag> : null}
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
    <ConsoleOperationNotice
      errorMessage={t(
        'pages.studio.studioinspectorpane.nodeActionFailed',
        'Could not apply node changes. Try again.',
      )}
      notice={inspectorNotice}
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
        description={t("pages.studio.studioinspectorpane.validation.and.yaml.summary.will", "Validation and YAML summary will appear after a draft is loaded.")}
      />
    );
  }

  if (validationError) {
    return (
      <NoticePanel
        type="error"
        title={t("pages.studio.studioinspectorpane.studio.yaml.validation.failed", "Studio YAML validation failed")}
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
        title={t("pages.studio.studioinspectorpane.validating.workflow.yaml", "Validating workflow YAML")}
        description={t("pages.studio.studioinspectorpane.studio.is.parsing.the.active", "Studio is parsing the active YAML through the workflow editor service.")}
      />
    );
  }

  if (validationFindings.length === 0) {
    return (
      <NoticePanel
        type="success"
        title={t("pages.studio.studioinspectorpane.validated.by.studio.editor", "Validated by Studio editor")}
        description={t("pages.studio.studioinspectorpane.the.active.yaml.parsed.cleanly", "The active YAML parsed cleanly through the workflow editor service.")}
      />
    );
  }

  const preview = validationFindings.slice(0, 8);
  const hasError = hasValidationError(validationFindings);

  return (
    <NoticePanel
      type={hasError ? 'error' : 'warning'}
      title={`${validationFindings.length} validation finding(s)`}
      description={t("pages.studio.studioinspectorpane.review.the.highest.signal.issues", "Review the highest-signal issues first before saving or running this workflow.")}
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
          +{validationFindings.length - preview.length} {t("pages.studio.studioinspectorpane.more.finding.hidden", "more finding(s) hidden.")}</Typography.Text>
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

  const nodeConfigFieldSet = React.useMemo(
    () =>
      nodeInspectorDraft?.kind === 'step'
        ? buildNodeConfigFields({
            connectors,
            nodeType: nodeInspectorDraft.type,
            parametersText: nodeInspectorDraft.parametersText,
          })
        : null,
    [connectors, nodeInspectorDraft],
  );
  const parameterDraftError = React.useMemo(
    () =>
      nodeInspectorDraft?.kind === 'step'
        ? validateNodeConfigParametersText(nodeInspectorDraft.parametersText)
        : '',
    [nodeInspectorDraft],
  );
  const selectedConnectorName = React.useMemo(() => {
    if (!nodeConfigFieldSet?.parameters) {
      return '';
    }

    return String(nodeConfigFieldSet.parameters.connector || '');
  }, [nodeConfigFieldSet]);
  const handleApplyNodeChanges = React.useCallback(() => {
    if (parameterDraftError) {
      return;
    }

    onApplyNodeChanges();
  }, [onApplyNodeChanges, parameterDraftError]);
  const handleChangeNodeConfigFieldValue = React.useCallback(
    (field: NodeConfigField, value: string) => {
      if (nodeInspectorDraft?.kind !== 'step') {
        return;
      }

      onChangeNodeInspectorDraft({
        ...nodeInspectorDraft,
        parametersText: updateNodeConfigFieldParametersText({
          field,
          nodeType: nodeInspectorDraft.type,
          parametersText: nodeInspectorDraft.parametersText,
          rawValue: value,
        }),
      });
    },
    [nodeInspectorDraft, onChangeNodeInspectorDraft],
  );

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
            title={t("pages.studio.studioinspectorpane.step.summary", "Step summary")}
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
              label={t("pages.studio.studioinspectorpane.connector.mode", "Connector mode")}
              tone={selectedConnectorName ? 'warning' : 'default'}
              value={
                selectedConnectorName || 'Direct'
              }
            />
          </div>
          <div style={summaryFieldGridStyle}>
            <SummaryField
              label={t("pages.studio.studioinspectorpane.current.step", "Current step")}
              value={<Typography.Text code>{selectedGraphStep.id}</Typography.Text>}
            />
            <SummaryField
              label={t("pages.studio.studioinspectorpane.target.role", "Target role")}
              value={selectedGraphStep.targetRole || 'Unassigned'}
            />
            <SummaryField
              label={t("pages.studio.studioinspectorpane.next.step", "Next step")}
              value={selectedGraphStep.next || 'None'}
            />
            <SummaryField
              label={t("pages.studio.studioinspectorpane.draft.step.id", "Draft step ID")}
              value={<Typography.Text code>{nodeInspectorDraft.id}</Typography.Text>}
            />
          </div>
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.identity.and.routing", "Identity and routing")}
            help="Update the step name, primitive type, and graph links."
          />
          <div style={formGridStyle}>
            <div style={cardStackStyle}>
              <Typography.Text strong>{t("pages.studio.studioinspectorpane.step.id", "Step ID")}</Typography.Text>
              <Input
                aria-label={t("pages.studio.studioinspectorpane.studio.step.id", "Studio step id")}
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
              <Typography.Text strong>{t("pages.studio.studioinspectorpane.primitive", "Primitive")}</Typography.Text>
              <Select
                aria-label={t("pages.studio.studioinspectorpane.studio.step.type", "Studio step type")}
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
              <Typography.Text strong>{t("pages.studio.studioinspectorpane.target.role.2", "Target role")}</Typography.Text>
              <Select
                aria-label={t("pages.studio.studioinspectorpane.studio.step.target.role", "Studio step target role")}
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
              <Typography.Text strong>{t("pages.studio.studioinspectorpane.next.step.2", "Next step")}</Typography.Text>
              <Input
                aria-label={t("pages.studio.studioinspectorpane.studio.step.next", "Studio step next")}
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
            <Typography.Text strong>{t("pages.studio.studioinspectorpane.branches", "Branches")}</Typography.Text>
            <Input.TextArea
              aria-label={t("pages.studio.studioinspectorpane.studio.step.branches", "Studio step branches")}
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
              {t("pages.studio.studioinspectorpane.edit.branches.as.json.object", "Edit branches as a JSON object of label to target step ID.")}</Typography.Text>
          </div>
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title="Parameters"
            help="Keep runtime inputs readable and wire connector calls explicitly."
          />
          <NodeConfigFieldsEditor
            fields={nodeConfigFieldSet?.fields ?? []}
            onChangeFieldValue={handleChangeNodeConfigFieldValue}
          />
          {nodeConfigFieldSet?.parseError ? (
            <Alert
              message={nodeConfigFieldSet.parseError}
              showIcon
              type="error"
            />
          ) : null}
          <div style={cardStackStyle}>
            <Typography.Text strong>{t("pages.studio.studioinspectorpane.parameters", "Parameters")}</Typography.Text>
            <Input.TextArea
              aria-label={t("pages.studio.studioinspectorpane.studio.step.parameters", "Studio step parameters")}
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
              {t("pages.studio.studioinspectorpane.edit.parameters.as.json.object", "Edit parameters as a JSON object.")}</Typography.Text>
          </div>
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.outgoing.connections", "Outgoing connections")}
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
                        {t("pages.studio.studioinspectorpane.remove", "Remove")}</Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={t("pages.studio.studioinspectorpane.no.outgoing.connections", "No outgoing connections")}
            />
          )}
        </div>

        {renderInspectorNotice(inspectorNotice)}

        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.step.actions", "Step actions")}
            help="Apply the edited draft or update the workflow graph around this step."
          />
          <div style={cardListActionStyle}>
            <Button
              type="primary"
              disabled={Boolean(parameterDraftError)}
              loading={inspectorPending}
              onClick={handleApplyNodeChanges}
            >
              {t("pages.studio.studioinspectorpane.apply.node.changes", "Apply node changes")}</Button>
            <Button loading={inspectorPending} onClick={onInsertStep}>
              {t("pages.studio.studioinspectorpane.add.step.after", "Add step after")}</Button>
            <Button danger loading={inspectorPending} onClick={onDeleteStep}>
              {t("pages.studio.studioinspectorpane.delete.step", "Delete step")}</Button>
            <Button onClick={onResetSelectedNode}>{t("pages.studio.studioinspectorpane.reset.fields", "Reset fields")}</Button>
          </div>
        </div>
      </div>
    ) : selectedGraphRole && nodeInspectorDraft?.kind === 'role' ? (
      <div style={cardStackStyle}>
        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.role.summary", "Role summary")}
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
              label={t("pages.studio.studioinspectorpane.current.role", "Current role")}
              value={<Typography.Text code>{selectedGraphRole.id}</Typography.Text>}
            />
            <SummaryField label={t("pages.studio.studioinspectorpane.role.name", "Role name")} value={selectedGraphRole.name || 'n/a'} />
          </div>
          <div>
            <Typography.Text style={summaryFieldLabelStyle}>{t("pages.studio.studioinspectorpane.allowed.connectors", "Allowed connectors")}</Typography.Text>
            <div style={{ marginTop: 8 }}>{renderConnectorTags(selectedGraphRole.connectors)}</div>
          </div>
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.role.details", "Role details")}
            help="Keep role identity, model configuration, and prompt text in one place."
          />
          <div style={formGridStyle}>
            <div style={cardStackStyle}>
              <Typography.Text strong>{t("pages.studio.studioinspectorpane.role.id", "Role ID")}</Typography.Text>
              <Input
                aria-label={t("pages.studio.studioinspectorpane.studio.role.id", "Studio role id")}
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
              <Typography.Text strong>{t("pages.studio.studioinspectorpane.role.name.2", "Role name")}</Typography.Text>
              <Input
                aria-label={t("pages.studio.studioinspectorpane.studio.role.name", "Studio role name")}
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
              <Typography.Text strong>{t("pages.studio.studioinspectorpane.provider", "Provider")}</Typography.Text>
              <Input
                aria-label={t("pages.studio.studioinspectorpane.studio.role.provider", "Studio role provider")}
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
              <Typography.Text strong>{t("pages.studio.studioinspectorpane.model", "Model")}</Typography.Text>
              <Input
                aria-label={t("pages.studio.studioinspectorpane.studio.role.model", "Studio role model")}
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
            <Typography.Text strong>{t("pages.studio.studioinspectorpane.system.prompt", "System prompt")}</Typography.Text>
            <Input.TextArea
              aria-label={t("pages.studio.studioinspectorpane.studio.role.system.prompt", "Studio role system prompt")}
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
            <Typography.Text strong>{t("pages.studio.studioinspectorpane.allowed.connectors.2", "Allowed connectors")}</Typography.Text>
            <Input.TextArea
              aria-label={t("pages.studio.studioinspectorpane.studio.role.connectors", "Studio role connectors")}
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
              {t("pages.studio.studioinspectorpane.one.connector.per.line.or", "One connector per line, or use commas.")}</Typography.Text>
          </div>
        </div>

        {renderInspectorNotice(inspectorNotice)}

        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.role.actions", "Role actions")}
            help="Apply the updated role fields back into the workflow draft."
          />
          <div style={cardListActionStyle}>
            <Button
              type="primary"
              loading={inspectorPending}
              onClick={onApplyNodeChanges}
            >
              {t("pages.studio.studioinspectorpane.apply.node.changes.2", "Apply node changes")}</Button>
            <Button onClick={onResetSelectedNode}>{t("pages.studio.studioinspectorpane.reset.fields.2", "Reset fields")}</Button>
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
          {t("pages.studio.studioinspectorpane.please.select.step.or", "Please select a step or role in the canvas first.")}</Typography.Text>
      </div>
    );

  const rolesInspectorContent =
    workflowRoles.length > 0 || savedRoles.length > 0 ? (
      <div style={cardStackStyle}>
        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.role.library", "Role library")}
            help="Search saved roles, add new workflow roles, and expand the ones that need edits."
            action={<Button onClick={onAddWorkflowRole}>{t("pages.studio.studioinspectorpane.add.role", "Add role")}</Button>}
          />
          <div style={summaryMetricGridStyle}>
            <SummaryMetric label={t("pages.studio.studioinspectorpane.saved.roles", "Saved roles")} value={savedRoles.length} />
            <SummaryMetric label={t("pages.studio.studioinspectorpane.workflow.roles", "Workflow roles")} value={workflowRoles.length} />
            <SummaryMetric
              label={t("pages.studio.studioinspectorpane.filtered.saved", "Filtered saved")}
              value={filteredSavedRoles.length}
            />
            <SummaryMetric
              label={t("pages.studio.studioinspectorpane.filtered.workflow", "Filtered workflow")}
              value={filteredWorkflowRoles.length}
            />
          </div>
          <Input
            allowClear
            aria-label={t("pages.studio.studioinspectorpane.studio.roles.search", "Studio roles search")}
            placeholder={t("pages.studio.studioinspectorpane.search.saved.roles", "Search saved roles")}
            value={roleSearch}
            onChange={(event) => setRoleSearch(event.target.value)}
          />
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.saved.roles.2", "Saved roles")}
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
                            {role.connectors.length} {t("pages.studio.studioinspectorpane.connector", "connector(s)")}</Tag>
                        ) : null}
                      </Space>
                    </div>
                    <div style={cardListActionStyle}>
                      <Button onClick={() => onUseSavedRole(role.id)}>{t("pages.studio.studioinspectorpane.use", "Use")}</Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={t("pages.studio.studioinspectorpane.no.saved.roles.matched", "No saved roles matched")}
            />
          )}
        </div>

        <div style={sectionPanelStyle}>
          <SectionHeader
            title={t("pages.studio.studioinspectorpane.workflow.roles.2", "Workflow roles")}
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
                              {role.connectors.length} {t("pages.studio.studioinspectorpane.connector.2", "connector(s)")}</Tag>
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
                          {expanded
                            ? t("pages.studio.studioinspectorpane.collapse", "Collapse")
                            : t("pages.studio.studioinspectorpane.edit", "Edit")}
                        </Button>
                        <Button
                          danger
                          size="small"
                          onClick={() => onDeleteWorkflowRole(role.id)}
                        >
                          {t("pages.studio.studioinspectorpane.remove.2", "Remove")}</Button>
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
              description={t("pages.studio.studioinspectorpane.no.workflow.roles.matched", "No workflow roles matched")}
            />
          )}
        </div>
      </div>
    ) : (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description={t("pages.studio.studioinspectorpane.no.workflow.roles.were.parsed", "No workflow roles were parsed from the active draft.")}
      />
    );

  const yamlInspectorContent = (
    <div style={cardStackStyle}>
      <div style={sectionPanelStyle}>
        <SectionHeader
          title={t("pages.studio.studioinspectorpane.yaml.workspace", "YAML workspace")}
          help="Edit the source document directly, then validate it before saving or running."
          action={
            <Space wrap size={[8, 8]}>
              <Button onClick={onValidateDraft}>{t("pages.studio.studioinspectorpane.validate", "Validate")}</Button>
              <Button
                onClick={() => {
                  void navigator.clipboard?.writeText(draftYaml || '');
                }}
              >
                {t("pages.studio.studioinspectorpane.copy", "Copy")}</Button>
            </Space>
          }
        />
        <div style={summaryMetricGridStyle}>
          <SummaryMetric
            label={t("pages.studio.studioinspectorpane.parsed.roles", "Parsed roles")}
            value={workflowRoles.length}
            tone="info"
          />
          <SummaryMetric
            label={t("pages.studio.studioinspectorpane.parsed.steps", "Parsed steps")}
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
            label={t("pages.studio.studioinspectorpane.parsed.workflow", "Parsed workflow")}
            value={parsedWorkflowName || activeWorkflowName || 'n/a'}
          />
          <SummaryField
            label={t("pages.studio.studioinspectorpane.validation.status", "Validation status")}
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
          <Typography.Text style={summaryFieldLabelStyle}>{t("pages.studio.studioinspectorpane.description", "Description")}</Typography.Text>
          <Typography.Paragraph
            ellipsis={{ rows: 3, expandable: true, symbol: 'more' }}
            style={{ margin: '8px 0 0', whiteSpace: 'pre-wrap' }}
          >
            {activeWorkflowDescription ||
              t("pages.studio.studioinspectorpane.no.description", "No description")}
          </Typography.Paragraph>
        </div>
      </div>

      <div style={sectionPanelStyle}>
        <SectionHeader
          title={t("pages.studio.studioinspectorpane.workflow.yaml", "Workflow YAML")}
          help="Direct source editing stays available here, but validation and summary stay separated above and below."
        />
        <Input.TextArea
          aria-label={t("pages.studio.studioinspectorpane.studio.workflow.yaml.panel", "Studio workflow yaml panel")}
          autoSize={{ minRows: 14, maxRows: 24 }}
          spellCheck={false}
          value={draftYaml}
          onChange={(event) => onSetDraftYaml(event.target.value)}
          style={yamlEditorStyle}
        />
      </div>

      <div style={sectionPanelStyle}>
        <SectionHeader
          title={t("pages.studio.studioinspectorpane.validation.digest", "Validation digest")}
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
            title={t("pages.studio.studioinspectorpane.inspector.views", "Inspector views")}
            help="Switch between node edits, reusable roles, and the underlying YAML without leaving the current drawer."
          />
          <Space wrap size={[8, 8]}>
            <Button
              type={inspectorTab === 'node' ? 'primary' : 'default'}
              disabled={!hasSelectedNode}
              onClick={() => onSetInspectorTab('node')}
            >
              {t("pages.studio.studioinspectorpane.node", "Node")}</Button>
            <Button
              type={inspectorTab === 'roles' ? 'primary' : 'default'}
              onClick={() => onSetInspectorTab('roles')}
            >
              {t("pages.studio.studioinspectorpane.roles", "Roles")}</Button>
            <Button
              type={inspectorTab === 'yaml' ? 'primary' : 'default'}
              onClick={() => onSetInspectorTab('yaml')}
            >
              {t("pages.studio.studioinspectorpane.yaml", "YAML")}</Button>
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
