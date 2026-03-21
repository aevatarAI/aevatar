import {
  Alert,
  Button,
  Descriptions,
  Empty,
  Input,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import type { StudioNodeInspectorDraft } from '@/shared/studio/document';
import type {
  StudioGraphRole,
  StudioGraphStep,
} from '@/shared/studio/graph';
import type {
  StudioConnectorDefinition,
  StudioRoleDefinition,
  StudioValidationFinding,
} from '@/shared/studio/models';
import { STUDIO_GRAPH_CATEGORIES as STUDIO_GRAPH_PRIMITIVE_CATEGORIES } from '@/shared/studio/graph';
import {
  cardStackStyle,
  embeddedPanelStyle,
} from '@/shared/ui/proComponents';

type StudioInspectorTab = 'node' | 'roles' | 'yaml';

type InspectorNoticeLike = {
  readonly type: 'success' | 'warning' | 'error';
  readonly message: string;
};

type StudioInspectorPaneProps = {
  readonly draftYaml: string;
  readonly inspectorTab: StudioInspectorTab;
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

function hasValidationError(findings: StudioValidationFinding[]): boolean {
  return findings.some((item) =>
    String(item.level ?? '').toLowerCase().includes('error'),
  );
}

function renderFindings(findings: StudioValidationFinding[]): React.ReactNode {
  if (findings.length === 0) {
    return (
      <Alert
        showIcon
        type="success"
        title="Validated by Studio editor"
        description="The active YAML parsed cleanly through the workflow editor service."
      />
    );
  }

  const hasError = hasValidationError(findings);
  return (
    <Alert
      showIcon
      type={hasError ? 'error' : 'warning'}
      title={`${findings.length} validation finding(s)`}
      description={
        <div style={cardStackStyle}>
          {findings.slice(0, 6).map((item) => (
            <div
              key={`${item.code || 'finding'}-${item.path || '/'}-${item.message}`}
            >
              <Typography.Text strong>{item.path || '/'}</Typography.Text>
              <Typography.Text type="secondary">
                {' '}
                · {item.message}
              </Typography.Text>
            </div>
          ))}
        </div>
      }
    />
  );
}

const StudioInspectorPane: React.FC<StudioInspectorPaneProps> = ({
  draftYaml,
  inspectorTab,
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

  const filteredSavedRoles = React.useMemo(() => {
    const keyword = roleSearch.trim().toLowerCase();
    if (!keyword) {
      return savedRoles;
    }

    return savedRoles.filter((role) =>
      [
        role.id,
        role.name,
        role.provider,
        role.model,
      ].some((value) => value.toLowerCase().includes(keyword)),
    );
  }, [roleSearch, savedRoles]);

  const filteredWorkflowRoles = React.useMemo(() => {
    const keyword = roleSearch.trim().toLowerCase();
    if (!keyword) {
      return workflowRoles;
    }

    return workflowRoles.filter((role) =>
      [
        role.id,
        role.name,
        role.provider,
        role.model,
      ].some((value) => value.toLowerCase().includes(keyword)),
    );
  }, [roleSearch, workflowRoles]);

  const nodeInspectorContent =
    selectedGraphStep && nodeInspectorDraft?.kind === 'step' ? (
      <div style={cardStackStyle}>
        <Descriptions column={1} size="small" bordered>
          <Descriptions.Item label="Current step">
            <Typography.Text code>{selectedGraphStep.id}</Typography.Text>
          </Descriptions.Item>
          <Descriptions.Item label="Current type">
            <Tag color="processing">{selectedGraphStep.type}</Tag>
          </Descriptions.Item>
        </Descriptions>
        <div style={cardStackStyle}>
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
          {nodeInspectorDraft.type === 'connector_call' ? (
            <div style={cardStackStyle}>
              <Typography.Text strong>Connector</Typography.Text>
              <Select
                aria-label="Studio connector call connector"
                allowClear
                placeholder="Select connector"
                value={(() => {
                  try {
                    return String(
                      JSON.parse(nodeInspectorDraft.parametersText || '{}').connector || '',
                    ) || undefined;
                  } catch {
                    return undefined;
                  }
                })()}
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
                        {
                          connector: value || '',
                        },
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
          <div style={cardStackStyle}>
            <Typography.Text strong>Connections</Typography.Text>
            {selectedStepConnections.length > 0 ? (
              <div style={cardStackStyle}>
                {selectedStepConnections.map((connection) => (
                  <div key={connection.key} style={embeddedPanelStyle}>
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 12,
                        justifyContent: 'space-between',
                      }}
                    >
                      <div style={cardStackStyle}>
                        <Typography.Text strong>{connection.label}</Typography.Text>
                        <Typography.Text type="secondary">
                          {connection.targetStepId}
                        </Typography.Text>
                      </div>
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
                ))}
              </div>
            ) : (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="No outgoing connections"
              />
            )}
          </div>
          {inspectorNotice ? (
            <Alert
              showIcon
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
          ) : null}
          <Space wrap size={[8, 8]}>
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
          </Space>
        </div>
      </div>
    ) : selectedGraphRole && nodeInspectorDraft?.kind === 'role' ? (
      <div style={cardStackStyle}>
        <Descriptions column={1} size="small" bordered>
          <Descriptions.Item label="Current role">
            <Typography.Text code>{selectedGraphRole.id}</Typography.Text>
          </Descriptions.Item>
          <Descriptions.Item label="Provider">
            {selectedGraphRole.provider || 'n/a'}
          </Descriptions.Item>
          <Descriptions.Item label="Model">
            {selectedGraphRole.model || 'n/a'}
          </Descriptions.Item>
        </Descriptions>
        <div style={cardStackStyle}>
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
          {inspectorNotice ? (
            <Alert
              showIcon
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
          ) : null}
          <Space wrap size={[8, 8]}>
            <Button
              type="primary"
              loading={inspectorPending}
              onClick={onApplyNodeChanges}
            >
              Apply node changes
            </Button>
            <Button onClick={onResetSelectedNode}>Reset fields</Button>
          </Space>
        </div>
      </div>
    ) : (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="Select a role or step in the workflow graph."
      />
    );

  const rolesInspectorContent =
    workflowRoles.length > 0 || savedRoles.length > 0 ? (
      <div style={cardStackStyle}>
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <Typography.Text strong>Roles</Typography.Text>
          <Button onClick={onAddWorkflowRole}>Add role</Button>
        </div>

        <Input
          allowClear
          aria-label="Studio roles search"
          placeholder="Search saved roles"
          value={roleSearch}
          onChange={(event) => setRoleSearch(event.target.value)}
        />

        <div style={embeddedPanelStyle}>
          <div style={cardStackStyle}>
            <div
              style={{
                alignItems: 'center',
                display: 'flex',
                justifyContent: 'space-between',
              }}
            >
              <Typography.Text strong>Saved roles</Typography.Text>
            </div>
            {filteredSavedRoles.length > 0 ? (
              <div style={cardStackStyle}>
                {filteredSavedRoles.map((role) => (
                  <div key={`saved:${role.id}`} style={embeddedPanelStyle}>
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 12,
                        justifyContent: 'space-between',
                      }}
                    >
                      <div style={cardStackStyle}>
                        <Typography.Text strong>
                          {role.name || role.id}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          {role.id}
                          {role.provider ? ` · ${role.provider}` : ''}
                          {role.model ? ` · ${role.model}` : ''}
                        </Typography.Text>
                      </div>
                      <Button onClick={() => onUseSavedRole(role.id)}>Use</Button>
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
        </div>

        <div style={embeddedPanelStyle}>
          <div style={cardStackStyle}>
            <Typography.Text strong>Workflow roles</Typography.Text>
            {filteredWorkflowRoles.length > 0 ? (
              <div style={cardStackStyle}>
                {filteredWorkflowRoles.map((role) => {
                  const expanded = expandedRoleId === role.id;
                  return (
                    <div key={`workflow:${role.id}`} style={embeddedPanelStyle}>
                      <div
                        style={{
                          alignItems: 'center',
                          display: 'flex',
                          gap: 12,
                          justifyContent: 'space-between',
                        }}
                      >
                        <Button
                          type="text"
                          onClick={() =>
                            setExpandedRoleId((current) =>
                              current === role.id ? null : role.id,
                            )
                          }
                          style={{
                            flex: 1,
                            height: 'auto',
                            justifyContent: 'flex-start',
                            minWidth: 0,
                            padding: 0,
                            textAlign: 'left',
                          }}
                        >
                          <div style={cardStackStyle}>
                            <Typography.Text strong>{role.id}</Typography.Text>
                            <Typography.Text type="secondary">
                              {role.name || role.id}
                            </Typography.Text>
                          </div>
                        </Button>
                        <Button
                          danger
                          size="small"
                          onClick={() => onDeleteWorkflowRole(role.id)}
                        >
                          Remove
                        </Button>
                      </div>

                      {expanded ? (
                        <div style={{ ...cardStackStyle, marginTop: 12 }}>
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
      </div>
    ) : (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="No workflow roles were parsed from the active draft."
      />
    );

  const yamlInspectorContent = (
    <div style={cardStackStyle}>
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          gap: 12,
          justifyContent: 'space-between',
        }}
      >
        <Typography.Text strong>Workflow YAML</Typography.Text>
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
      </div>

      <Input.TextArea
        aria-label="Studio workflow yaml panel"
        autoSize={{ minRows: 14, maxRows: 24 }}
        spellCheck={false}
        value={draftYaml}
        onChange={(event) => onSetDraftYaml(event.target.value)}
        style={{
          fontFamily:
            "'SFMono-Regular', 'SF Mono', Consolas, 'Liberation Mono', Menlo, monospace",
          fontSize: 13,
        }}
      />

      {draftYaml ? (
        validationError ? (
          <Alert
            showIcon
            type="error"
            title="Studio YAML validation failed"
            description={
              validationError instanceof Error
                ? validationError.message
                : String(validationError)
            }
          />
        ) : validationLoading ? (
          <Alert
            showIcon
            type="info"
            title="Validating workflow YAML"
            description="Studio is parsing the active YAML through the workflow editor service."
          />
        ) : (
          renderFindings(validationFindings)
        )
      ) : (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="Validation and YAML summary will appear after a draft is loaded."
        />
      )}

      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label="Parsed workflow">
          {parsedWorkflowName || activeWorkflowName || 'n/a'}
        </Descriptions.Item>
        <Descriptions.Item label="Description">
          {activeWorkflowDescription || 'No description'}
        </Descriptions.Item>
        <Descriptions.Item label="Parsed roles">
          {workflowRoles.length}
        </Descriptions.Item>
        <Descriptions.Item label="Parsed steps">
          {workflowSteps.length}
        </Descriptions.Item>
      </Descriptions>
    </div>
  );

  return (
    <div style={cardStackStyle}>
      <datalist id="studio-workflow-step-options">
        {workflowStepIds.map((stepId) => (
          <option key={stepId} value={stepId} />
        ))}
      </datalist>

      <Space wrap size={[8, 8]}>
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
      </Space>

      {inspectorTab === 'node'
        ? nodeInspectorContent
        : inspectorTab === 'roles'
          ? rolesInspectorContent
          : yamlInspectorContent}
    </div>
  );
};

export default StudioInspectorPane;
