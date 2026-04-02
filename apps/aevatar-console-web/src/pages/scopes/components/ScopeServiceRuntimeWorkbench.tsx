import {
  ApiOutlined,
  BranchesOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
  LinkOutlined,
  PlusOutlined,
  RetweetOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Input,
  Modal,
  Select,
  Space,
  Tabs,
  Typography,
  message,
} from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import type { ServiceBindingSnapshot } from "@/shared/models/governance";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  describeScopeServiceBindingTarget,
  getScopeServiceCurrentRevision,
  type ScopeServiceBindingInput,
  type ScopeServiceRunSummary,
} from "@/shared/models/runtime/scopeServices";
import {
  describeStudioScopeBindingRevisionContext,
  describeStudioScopeBindingRevisionTarget,
  formatStudioScopeBindingImplementationKind,
} from "@/shared/studio/models";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
  AevatarStatusTag,
} from "@/shared/ui/aevatarPageShells";

type ScopeServiceRuntimeWorkbenchProps = {
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
  readonly selectedServiceId: string;
  readonly selectedEndpointId: string;
  readonly onSelectService: (serviceId: string) => void;
  readonly onUseEndpoint: (serviceId: string, endpointId: string) => void;
};

type ServiceRuntimeTab = "overview" | "bindings" | "revisions" | "runs";
type BindingEditorMode = "create" | "edit";

type BindingEditorState = {
  readonly mode: BindingEditorMode;
  readonly bindingId?: string;
} | null;

type BindingEditorDraft = {
  readonly bindingId: string;
  readonly displayName: string;
  readonly bindingKind: string;
  readonly policyIdsText: string;
  readonly targetServiceId: string;
  readonly targetEndpointId: string;
  readonly connectorType: string;
  readonly connectorId: string;
  readonly secretName: string;
};

type RunAuditTarget = {
  readonly runId: string;
  readonly actorId: string;
} | null;

const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";

function buildScopedServiceCatalogHref(scopeId: string, serviceId: string): string {
  const params = new URLSearchParams();
  params.set("tenantId", scopeId.trim());
  params.set("appId", scopeServiceAppId);
  params.set("namespace", scopeServiceNamespace);
  params.set("serviceId", serviceId.trim());
  return `/services?${params.toString()}`;
}

function createEmptyBindingDraft(): BindingEditorDraft {
  return {
    bindingId: "",
    displayName: "",
    bindingKind: "service",
    policyIdsText: "",
    targetServiceId: "",
    targetEndpointId: "",
    connectorType: "",
    connectorId: "",
    secretName: "",
  };
}

function createBindingDraftFromSnapshot(
  binding: ServiceBindingSnapshot,
): BindingEditorDraft {
  return {
    bindingId: binding.bindingId,
    displayName: binding.displayName,
    bindingKind: binding.bindingKind,
    policyIdsText: binding.policyIds.join(", "),
    targetServiceId: binding.serviceRef?.identity.serviceId || "",
    targetEndpointId: binding.serviceRef?.endpointId || "",
    connectorType: binding.connectorRef?.connectorType || "",
    connectorId: binding.connectorRef?.connectorId || "",
    secretName: binding.secretRef?.secretName || "",
  };
}

function parsePolicyIds(value: string): string[] {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function buildBindingPayload(draft: BindingEditorDraft): ScopeServiceBindingInput {
  const bindingKind = draft.bindingKind.trim() || "service";
  return {
    bindingId: draft.bindingId.trim(),
    displayName: draft.displayName.trim(),
    bindingKind,
    policyIds: parsePolicyIds(draft.policyIdsText),
    service:
      bindingKind === "service"
        ? {
            serviceId: draft.targetServiceId.trim(),
            endpointId: draft.targetEndpointId.trim() || null,
          }
        : null,
    connector:
      bindingKind === "connector"
        ? {
            connectorType: draft.connectorType.trim(),
            connectorId: draft.connectorId.trim(),
          }
        : null,
    secret:
      bindingKind === "secret"
        ? {
            secretName: draft.secretName.trim(),
          }
        : null,
  };
}

function getBindingKindLabel(kind: string): string {
  switch (kind.trim().toLowerCase()) {
    case "service":
      return "Service";
    case "connector":
      return "Connector";
    case "secret":
      return "Secret";
    default:
      return kind || "Binding";
  }
}

const RuntimeMetricCard: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 4,
      minWidth: 0,
      padding: 12,
    }}
  >
    <Typography.Text type="secondary">{label}</Typography.Text>
    <Typography.Text strong>{value}</Typography.Text>
  </div>
);

const ScopeServiceRuntimeWorkbench: React.FC<ScopeServiceRuntimeWorkbenchProps> = ({
  scopeId,
  services,
  selectedServiceId,
  selectedEndpointId,
  onSelectService,
  onUseEndpoint,
}) => {
  const [messageApi, messageContextHolder] = message.useMessage();
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<ServiceRuntimeTab>("overview");
  const [bindingEditorState, setBindingEditorState] =
    useState<BindingEditorState>(null);
  const [bindingEditorDraft, setBindingEditorDraft] =
    useState<BindingEditorDraft>(createEmptyBindingDraft());
  const [bindingEditorSubmitting, setBindingEditorSubmitting] = useState(false);
  const [selectedRevisionId, setSelectedRevisionId] = useState("");
  const [selectedRunAuditTarget, setSelectedRunAuditTarget] =
    useState<RunAuditTarget>(null);
  const [retiringBindingId, setRetiringBindingId] = useState("");

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;

  const bindingsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: ["scope-runtime", "bindings", scopeId, selectedService?.serviceId],
    queryFn: () =>
      scopeRuntimeApi.getServiceBindings(scopeId, selectedService?.serviceId || ""),
  });

  const revisionsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: ["scope-runtime", "revisions", scopeId, selectedService?.serviceId],
    queryFn: () =>
      scopeRuntimeApi.getServiceRevisions(scopeId, selectedService?.serviceId || ""),
  });

  const runsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: ["scope-runtime", "runs", scopeId, selectedService?.serviceId],
    queryFn: () =>
      scopeRuntimeApi.listServiceRuns(scopeId, selectedService?.serviceId || "", {
        take: 12,
      }),
  });

  const selectedRevisionQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId && selectedRevisionId),
    queryKey: [
      "scope-runtime",
      "revision",
      scopeId,
      selectedService?.serviceId,
      selectedRevisionId,
    ],
    queryFn: () =>
      scopeRuntimeApi.getServiceRevision(
        scopeId,
        selectedService?.serviceId || "",
        selectedRevisionId,
      ),
  });

  const selectedRunAuditQuery = useQuery({
    enabled: Boolean(
      scopeId &&
        selectedService?.serviceId &&
        selectedRunAuditTarget?.runId.trim(),
    ),
    queryKey: [
      "scope-runtime",
      "run-audit",
      scopeId,
      selectedService?.serviceId,
      selectedRunAuditTarget?.runId,
      selectedRunAuditTarget?.actorId,
    ],
    queryFn: () =>
      scopeRuntimeApi.getServiceRunAudit(
        scopeId,
        selectedService?.serviceId || "",
        selectedRunAuditTarget?.runId || "",
        {
          actorId: selectedRunAuditTarget?.actorId || undefined,
        },
      ),
  });

  const retireRevisionMutation = useMutation({
    mutationFn: (revisionId: string) =>
      scopeRuntimeApi.retireServiceRevision(
        scopeId,
        selectedService?.serviceId || "",
        revisionId,
      ),
    onSuccess: async (result) => {
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["scope-runtime", "revisions", scopeId, selectedService?.serviceId],
        }),
        queryClient.invalidateQueries({
          queryKey: ["scopes", "invoke", "services", scopeId],
        }),
      ]);
      messageApi.success(
        `Revision ${result.revisionId} was accepted for retirement.`,
      );
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to retire revision.",
      );
    },
  });

  useEffect(() => {
    setBindingEditorState(null);
    setBindingEditorDraft(createEmptyBindingDraft());
    setSelectedRunAuditTarget(null);
    setRetiringBindingId("");
  }, [selectedService?.serviceId]);

  useEffect(() => {
    const revisions = revisionsQuery.data?.revisions ?? [];
    if (!revisions.length) {
      setSelectedRevisionId("");
      return;
    }

    if (
      selectedRevisionId &&
      revisions.some((revision) => revision.revisionId === selectedRevisionId)
    ) {
      return;
    }

    setSelectedRevisionId(
      getScopeServiceCurrentRevision(revisionsQuery.data)?.revisionId ||
        revisions[0]?.revisionId ||
        "",
    );
  }, [revisionsQuery.data, selectedRevisionId]);

  const selectedBindingTargetService =
    services.find(
      (service) => service.serviceId === bindingEditorDraft.targetServiceId.trim(),
    ) ?? null;

  const bindingTargetEndpointOptions = (
    selectedBindingTargetService?.endpoints ?? []
  ).map((endpoint) => ({
    label: endpoint.displayName || endpoint.endpointId,
    value: endpoint.endpointId,
  }));

  const bindingList = bindingsQuery.data?.bindings ?? [];
  const revisionList = revisionsQuery.data?.revisions ?? [];
  const currentRevision =
    selectedRevisionQuery.data ||
    revisionList.find((revision) => revision.revisionId === selectedRevisionId) ||
    getScopeServiceCurrentRevision(revisionsQuery.data);
  const recentRuns = runsQuery.data?.runs ?? [];
  const auditTimeline = selectedRunAuditQuery.data?.audit.timeline ?? [];
  const auditSteps = selectedRunAuditQuery.data?.audit.steps ?? [];
  const auditSummary = selectedRunAuditQuery.data?.audit.summary;

  const bindingCards = bindingList.length ? (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      {bindingList.map((binding) => (
        <div
          key={binding.bindingId}
          style={{
            border: "1px solid var(--ant-color-border-secondary)",
            borderRadius: 12,
            display: "flex",
            flexDirection: "column",
            gap: 10,
            padding: 12,
          }}
        >
          <Space wrap size={[8, 8]}>
            <Typography.Text strong>
              {binding.displayName || binding.bindingId}
            </Typography.Text>
            <AevatarStatusTag
              domain="governance"
              status={binding.retired ? "retired" : "active"}
              label={getBindingKindLabel(binding.bindingKind)}
            />
          </Space>
          <Typography.Text type="secondary">
            Target {describeScopeServiceBindingTarget(binding)}
          </Typography.Text>
          <Typography.Text type="secondary">
            Policies{" "}
            {binding.policyIds.length > 0 ? binding.policyIds.join(", ") : "none"}
          </Typography.Text>
          <Space wrap>
            <Button
              disabled={binding.retired}
              icon={<EyeOutlined />}
              onClick={() => {
                setBindingEditorDraft(createBindingDraftFromSnapshot(binding));
                setBindingEditorState({
                  mode: "edit",
                  bindingId: binding.bindingId,
                });
              }}
            >
              Edit binding
            </Button>
            <Button
              danger
              disabled={binding.retired}
              loading={retiringBindingId === binding.bindingId}
              onClick={async () => {
                setRetiringBindingId(binding.bindingId);
                try {
                  await scopeRuntimeApi.retireServiceBinding(
                    scopeId,
                    selectedService?.serviceId || "",
                    binding.bindingId,
                  );
                  await queryClient.invalidateQueries({
                    queryKey: [
                      "scope-runtime",
                      "bindings",
                      scopeId,
                      selectedService?.serviceId,
                    ],
                  });
                  messageApi.success(
                    `Binding ${binding.bindingId} was accepted for retirement.`,
                  );
                } catch (error) {
                  messageApi.error(
                    error instanceof Error
                      ? error.message
                      : "Failed to retire binding.",
                  );
                } finally {
                  setRetiringBindingId("");
                }
              }}
            >
              Retire
            </Button>
          </Space>
        </div>
      ))}
    </div>
  ) : (
    <Empty
      description="No scope-specific bindings are published for this service yet."
      image={Empty.PRESENTED_IMAGE_SIMPLE}
    />
  );

  const revisionCards = revisionList.length ? (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      {revisionList.map((revision) => {
        const isSelected = revision.revisionId === selectedRevisionId;
        return (
          <div
            key={revision.revisionId}
            style={{
              border: isSelected
                ? "1px solid var(--ant-color-primary)"
                : "1px solid var(--ant-color-border-secondary)",
              borderRadius: 12,
              display: "flex",
              flexDirection: "column",
              gap: 10,
              padding: 12,
            }}
          >
            <Space wrap size={[8, 8]}>
              <Typography.Text strong>{revision.revisionId}</Typography.Text>
              <AevatarStatusTag
                domain="governance"
                status={revision.status || "draft"}
                label={formatStudioScopeBindingImplementationKind(
                  revision.implementationKind,
                )}
              />
              {revision.isDefaultServing ? <AevatarStatusTag domain="governance" status="active" label="default" /> : null}
              {revision.isActiveServing ? <AevatarStatusTag domain="run" status="running" label="active" /> : null}
              {revision.retiredAt ? <AevatarStatusTag domain="governance" status="retired" /> : null}
            </Space>
            <Typography.Text type="secondary">
              {describeStudioScopeBindingRevisionTarget(revision)} ·{" "}
              {describeStudioScopeBindingRevisionContext(revision) || "No detail"}
            </Typography.Text>
            <Typography.Text type="secondary">
              Serving {revision.servingState || revision.status} · Published{" "}
              {formatDateTime(revision.publishedAt)}
            </Typography.Text>
            <Space wrap>
              <Button
                icon={<EyeOutlined />}
                onClick={() => setSelectedRevisionId(revision.revisionId)}
                type={isSelected ? "primary" : "default"}
              >
                {isSelected ? "Inspecting" : "Inspect"}
              </Button>
              <Button
                danger
                disabled={Boolean(revision.retiredAt) || revision.isDefaultServing}
                loading={
                  retireRevisionMutation.isPending &&
                  retireRevisionMutation.variables === revision.revisionId
                }
                onClick={() => retireRevisionMutation.mutate(revision.revisionId)}
              >
                Retire revision
              </Button>
            </Space>
          </div>
        );
      })}
    </div>
  ) : (
    <Empty
      description="No published revisions are available for this service."
      image={Empty.PRESENTED_IMAGE_SIMPLE}
    />
  );

  const runCards = recentRuns.length ? (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      {recentRuns.map((run) => (
        <RunSummaryCard
          key={`${run.runId}:${run.actorId}`}
          run={run}
          selected={
            selectedRunAuditTarget?.runId === run.runId &&
            selectedRunAuditTarget?.actorId === run.actorId
          }
          onInspectAudit={() =>
            setSelectedRunAuditTarget({
              runId: run.runId,
              actorId: run.actorId,
            })
          }
          onOpenExplorer={() =>
            history.push(
              buildRuntimeExplorerHref({
                actorId: run.actorId,
                runId: run.runId,
                scopeId,
                serviceId: selectedService?.serviceId,
              }),
            )
          }
          onOpenRuns={() =>
            history.push(
              buildRuntimeRunsHref({
                actorId: run.actorId,
                scopeId,
                serviceId: selectedService?.serviceId,
              }),
            )
          }
        />
      ))}
    </div>
  ) : (
    <Empty
      description="No recent scope runs were found for this service."
      image={Empty.PRESENTED_IMAGE_SIMPLE}
    />
  );

  const tabItems = [
    {
      key: "overview",
      label: "Overview",
      children: selectedService ? (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <AevatarPanel
            title="Runtime Posture"
            titleHelp="This service-level posture is the fastest way to confirm what the selected project service is actually serving right now."
          >
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <RuntimeMetricCard
                label="Service key"
                value={selectedService.serviceKey}
              />
              <RuntimeMetricCard
                label="Serving revision"
                value={
                  selectedService.activeServingRevisionId ||
                  selectedService.defaultServingRevisionId ||
                  "n/a"
                }
              />
              <RuntimeMetricCard
                label="Deployment"
                value={selectedService.deploymentStatus || "draft"}
              />
              <RuntimeMetricCard
                label="Primary actor"
                value={selectedService.primaryActorId || "n/a"}
              />
            </div>
            <Space wrap>
              <Button
                icon={<ApiOutlined />}
                onClick={() =>
                  history.push(
                    buildScopedServiceCatalogHref(scopeId, selectedService.serviceId),
                  )
                }
              >
                Open Services
              </Button>
              <Button
                icon={<DeploymentUnitOutlined />}
                onClick={() => setActiveTab("runs")}
                type="primary"
              >
                Review runs
              </Button>
              <Button
                icon={<SafetyCertificateOutlined />}
                onClick={() => setActiveTab("bindings")}
              >
                Review bindings
              </Button>
            </Space>
          </AevatarPanel>

          <AevatarPanel
            title="Endpoint Surface"
            titleHelp="Operators can switch endpoints from here without losing the current scope and service context."
          >
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              {selectedService.endpoints.length > 0 ? (
                selectedService.endpoints.map((endpoint) => (
                  <div
                    key={endpoint.endpointId}
                    style={{
                      border: "1px solid var(--ant-color-border-secondary)",
                      borderRadius: 12,
                      display: "flex",
                      flexDirection: "column",
                      gap: 8,
                      padding: 12,
                    }}
                  >
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>
                        {endpoint.displayName || endpoint.endpointId}
                      </Typography.Text>
                      <AevatarStatusTag
                        domain="observation"
                        label={endpoint.kind || "endpoint"}
                        status={
                          endpoint.endpointId === selectedEndpointId
                            ? "streaming"
                            : "snapshot_available"
                        }
                      />
                    </Space>
                    <Typography.Text type="secondary">
                      {endpoint.description || "No endpoint description."}
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      Request {endpoint.requestTypeUrl || "n/a"}
                    </Typography.Text>
                    <Space wrap>
                      <Button
                        onClick={() =>
                          onUseEndpoint(selectedService.serviceId, endpoint.endpointId)
                        }
                        type={
                          endpoint.endpointId === selectedEndpointId
                            ? "primary"
                            : "default"
                        }
                      >
                        {endpoint.endpointId === selectedEndpointId
                          ? "Selected"
                          : "Use endpoint"}
                      </Button>
                    </Space>
                  </div>
                ))
              ) : (
                <Empty
                  description="No endpoint catalog is available for this service."
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </div>
          </AevatarPanel>
        </div>
      ) : (
        <AevatarInspectorEmpty description="Choose a published service to inspect runtime posture, bindings, revisions, and recent runs." />
      ),
    },
    {
      key: "bindings",
      label: `Bindings (${bindingList.length})`,
      children: selectedService ? (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <AevatarPanel
            extra={
              <Button
                icon={<PlusOutlined />}
                onClick={() => {
                  setBindingEditorDraft(createEmptyBindingDraft());
                  setBindingEditorState({ mode: "create" });
                }}
                type="primary"
              >
                Add binding
              </Button>
            }
            title="Dependency Surface"
            titleHelp="Scope-specific bindings describe which services, connectors, or secrets this published service is allowed to depend on inside the project."
          >
            {bindingsQuery.error ? (
              <Alert
                showIcon
                title={
                  bindingsQuery.error instanceof Error
                    ? bindingsQuery.error.message
                    : "Failed to load scope bindings."
                }
                type="error"
              />
            ) : bindingsQuery.isLoading ? (
              <AevatarInspectorEmpty description="Loading scope bindings." />
            ) : (
              bindingCards
            )}
          </AevatarPanel>
        </div>
      ) : (
        <AevatarInspectorEmpty description="Choose a service first." />
      ),
    },
    {
      key: "revisions",
      label: `Revisions (${revisionList.length})`,
      children: selectedService ? (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <AevatarPanel
            title="Revision Catalog"
            titleHelp="Revisions tell you which artifact is serving now and which historical versions are still available or ready to retire."
          >
            {revisionsQuery.error ? (
              <Alert
                showIcon
                title={
                  revisionsQuery.error instanceof Error
                    ? revisionsQuery.error.message
                    : "Failed to load revisions."
                }
                type="error"
              />
            ) : revisionsQuery.isLoading ? (
              <AevatarInspectorEmpty description="Loading service revisions." />
            ) : (
              revisionCards
            )}
          </AevatarPanel>

          {currentRevision ? (
            <AevatarPanel
              title="Selected Revision"
              titleHelp="The selected revision stays expanded here so operators can compare implementation target, serving posture, and actor assignment without leaving the tab."
            >
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                }}
              >
                <RuntimeMetricCard
                  label="Revision"
                  value={currentRevision.revisionId}
                />
                <RuntimeMetricCard
                  label="Implementation"
                  value={formatStudioScopeBindingImplementationKind(
                    currentRevision.implementationKind,
                  )}
                />
                <RuntimeMetricCard
                  label="Target"
                  value={describeStudioScopeBindingRevisionTarget(currentRevision)}
                />
                <RuntimeMetricCard
                  label="Actor"
                  value={
                    currentRevision.primaryActorId ||
                    currentRevision.staticPreferredActorId ||
                    "n/a"
                  }
                />
              </div>
              {describeStudioScopeBindingRevisionContext(currentRevision) ? (
                <Alert
                  description={describeStudioScopeBindingRevisionContext(
                    currentRevision,
                  )}
                  showIcon
                  title="Revision detail"
                  type="info"
                />
              ) : null}
            </AevatarPanel>
          ) : null}
        </div>
      ) : (
        <AevatarInspectorEmpty description="Choose a service first." />
      ),
    },
    {
      key: "runs",
      label: `Runs (${recentRuns.length})`,
      children: selectedService ? (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <AevatarPanel
            title="Recent Runs"
            titleHelp="Recent runs are the shortest path from a published service to a traceable execution posture."
          >
            {runsQuery.error ? (
              <Alert
                showIcon
                title={
                  runsQuery.error instanceof Error
                    ? runsQuery.error.message
                    : "Failed to load runs."
                }
                type="error"
              />
            ) : runsQuery.isLoading ? (
              <AevatarInspectorEmpty description="Loading recent runs." />
            ) : (
              runCards
            )}
          </AevatarPanel>

          {selectedRunAuditTarget ? (
            <AevatarPanel
              title="Run Audit"
              titleHelp="Audit detail keeps the latest selected run in view so operators can understand failure posture or completion depth before opening full Runs."
            >
              {selectedRunAuditQuery.error ? (
                <Alert
                  showIcon
                  title={
                    selectedRunAuditQuery.error instanceof Error
                      ? selectedRunAuditQuery.error.message
                      : "Failed to load run audit."
                  }
                  type="error"
                />
              ) : selectedRunAuditQuery.isLoading ? (
                <AevatarInspectorEmpty description="Loading run audit." />
              ) : selectedRunAuditQuery.data ? (
                <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <RuntimeMetricCard
                      label="Completion"
                      value={selectedRunAuditQuery.data.audit.completionStatus}
                    />
                    <RuntimeMetricCard
                      label="Duration"
                      value={`${Math.round(selectedRunAuditQuery.data.audit.durationMs)} ms`}
                    />
                    <RuntimeMetricCard
                      label="Steps"
                      value={`${auditSummary?.completedSteps ?? 0}/${auditSummary?.totalSteps ?? 0}`}
                    />
                    <RuntimeMetricCard
                      label="Role replies"
                      value={auditSummary?.roleReplyCount ?? 0}
                    />
                  </div>
                  {selectedRunAuditQuery.data.audit.finalOutput ? (
                    <Alert
                      description={selectedRunAuditQuery.data.audit.finalOutput}
                      showIcon
                      title="Final output"
                      type="success"
                    />
                  ) : null}
                  {selectedRunAuditQuery.data.audit.finalError ? (
                    <Alert
                      description={selectedRunAuditQuery.data.audit.finalError}
                      showIcon
                      title="Final error"
                      type="error"
                    />
                  ) : null}
                  <div
                    style={{
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))",
                    }}
                  >
                    <AevatarPanel title="Timeline Highlights">
                      {auditTimeline.length > 0 ? (
                        <div
                          style={{
                            display: "flex",
                            flexDirection: "column",
                            gap: 10,
                          }}
                        >
                          {auditTimeline.slice(0, 8).map((event, index) => (
                            <div
                              key={`${event.timestamp || "event"}-${index}`}
                              style={{
                                border: "1px solid var(--ant-color-border-secondary)",
                                borderRadius: 12,
                                display: "flex",
                                flexDirection: "column",
                                gap: 6,
                                padding: 12,
                              }}
                            >
                              <Typography.Text strong>
                                {event.stage || event.eventType || "event"}
                              </Typography.Text>
                              <Typography.Text type="secondary">
                                {event.message || "No message"}
                              </Typography.Text>
                              <Typography.Text type="secondary">
                                {formatDateTime(event.timestamp)}
                              </Typography.Text>
                            </div>
                          ))}
                        </div>
                      ) : (
                        <Empty
                          description="No timeline events were captured."
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      )}
                    </AevatarPanel>
                    <AevatarPanel title="Step Highlights">
                      {auditSteps.length > 0 ? (
                        <div
                          style={{
                            display: "flex",
                            flexDirection: "column",
                            gap: 10,
                          }}
                        >
                          {auditSteps.slice(0, 6).map((step) => (
                            <div
                              key={step.stepId}
                              style={{
                                border: "1px solid var(--ant-color-border-secondary)",
                                borderRadius: 12,
                                display: "flex",
                                flexDirection: "column",
                                gap: 6,
                                padding: 12,
                              }}
                            >
                              <Typography.Text strong>
                                {step.stepId}
                              </Typography.Text>
                              <Typography.Text type="secondary">
                                {step.stepType || "step"} ·{" "}
                                {step.targetRole || "unassigned"}
                              </Typography.Text>
                              <Typography.Text type="secondary">
                                {step.outputPreview || step.error || "No step preview."}
                              </Typography.Text>
                            </div>
                          ))}
                        </div>
                      ) : (
                        <Empty
                          description="No step traces were captured."
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      )}
                    </AevatarPanel>
                  </div>
                </div>
              ) : null}
            </AevatarPanel>
          ) : null}
        </div>
      ) : (
        <AevatarInspectorEmpty description="Choose a service first." />
      ),
    },
  ];

  return (
    <>
      {messageContextHolder}
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <AevatarPanel
          title="Published Services"
          titleHelp="Operators stay in the same drawer while switching between published services in the current project."
        >
          {services.length > 0 ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              {services.map((service) => (
                <div
                  key={service.serviceKey}
                  style={{
                    border:
                      service.serviceId === selectedServiceId
                        ? "1px solid var(--ant-color-primary)"
                        : "1px solid var(--ant-color-border-secondary)",
                    borderRadius: 12,
                    display: "flex",
                    flexDirection: "column",
                    gap: 8,
                    padding: 12,
                  }}
                >
                  <Space wrap size={[8, 8]}>
                    <Typography.Text strong>
                      {service.displayName || service.serviceId}
                    </Typography.Text>
                    <AevatarStatusTag
                      domain="governance"
                      status={service.deploymentStatus || "draft"}
                    />
                  </Space>
                  <Typography.Text type="secondary">
                    {service.endpoints.length} endpoints · Revision{" "}
                    {service.activeServingRevisionId ||
                      service.defaultServingRevisionId ||
                      "n/a"}
                  </Typography.Text>
                  <Space wrap>
                    <Button
                      onClick={() => onSelectService(service.serviceId)}
                      type={
                        service.serviceId === selectedServiceId
                          ? "primary"
                          : "default"
                      }
                    >
                      {service.serviceId === selectedServiceId
                        ? "Selected"
                        : "Inspect service"}
                    </Button>
                    <Button
                      icon={<LinkOutlined />}
                      onClick={() =>
                        history.push(
                          buildScopedServiceCatalogHref(scopeId, service.serviceId),
                        )
                      }
                    >
                      Open Services
                    </Button>
                  </Space>
                </div>
              ))}
            </div>
          ) : (
            <Empty
              description="No published services were discovered for this project."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          )}
        </AevatarPanel>

        {selectedService ? (
          <Tabs
            activeKey={activeTab}
            items={tabItems}
            onChange={(value) => setActiveTab(value as ServiceRuntimeTab)}
          />
        ) : null}
      </div>

      <Modal
        destroyOnHidden
        okButtonProps={{ loading: bindingEditorSubmitting }}
        okText={
          bindingEditorState?.mode === "edit" ? "Save binding" : "Create binding"
        }
        onCancel={() => {
          setBindingEditorState(null);
          setBindingEditorDraft(createEmptyBindingDraft());
        }}
        onOk={async () => {
          if (!selectedService) {
            return;
          }

          const payload = buildBindingPayload(bindingEditorDraft);
          if (!payload.bindingId) {
            messageApi.error("Binding id is required.");
            return;
          }

          if (payload.bindingKind === "service" && !payload.service?.serviceId) {
            messageApi.error("Select a target service for a service binding.");
            return;
          }

          if (
            payload.bindingKind === "connector" &&
            (!payload.connector?.connectorType || !payload.connector.connectorId)
          ) {
            messageApi.error("Connector type and connector id are required.");
            return;
          }

          if (payload.bindingKind === "secret" && !payload.secret?.secretName) {
            messageApi.error("Secret name is required.");
            return;
          }

          setBindingEditorSubmitting(true);
          try {
            if (bindingEditorState?.mode === "edit" && bindingEditorState.bindingId) {
              await scopeRuntimeApi.updateServiceBinding(
                scopeId,
                selectedService.serviceId,
                bindingEditorState.bindingId,
                payload,
              );
            } else {
              await scopeRuntimeApi.createServiceBinding(
                scopeId,
                selectedService.serviceId,
                payload,
              );
            }
            await queryClient.invalidateQueries({
              queryKey: [
                "scope-runtime",
                "bindings",
                scopeId,
                selectedService.serviceId,
              ],
            });
            messageApi.success(
              bindingEditorState?.mode === "edit"
                ? `Binding ${payload.bindingId} was updated.`
                : `Binding ${payload.bindingId} was created.`,
            );
            setBindingEditorState(null);
            setBindingEditorDraft(createEmptyBindingDraft());
          } catch (error) {
            messageApi.error(
              error instanceof Error ? error.message : "Failed to save binding.",
            );
          } finally {
            setBindingEditorSubmitting(false);
          }
        }}
        open={Boolean(bindingEditorState)}
        title={
          bindingEditorState?.mode === "edit"
            ? `Edit binding ${bindingEditorState.bindingId || ""}`
            : "Create scope binding"
        }
      >
        <Space direction="vertical" size={12} style={{ width: "100%" }}>
          <Input
            disabled={bindingEditorState?.mode === "edit"}
            onChange={(event) =>
              setBindingEditorDraft((current) => ({
                ...current,
                bindingId: event.target.value,
              }))
            }
            placeholder="binding id"
            value={bindingEditorDraft.bindingId}
          />
          <Input
            onChange={(event) =>
              setBindingEditorDraft((current) => ({
                ...current,
                displayName: event.target.value,
              }))
            }
            placeholder="display name"
            value={bindingEditorDraft.displayName}
          />
          <Select
            onChange={(value) =>
              setBindingEditorDraft((current) => ({
                ...createEmptyBindingDraft(),
                bindingId: current.bindingId,
                displayName: current.displayName,
                policyIdsText: current.policyIdsText,
                bindingKind: value,
              }))
            }
            options={[
              { label: "Service", value: "service" },
              { label: "Connector", value: "connector" },
              { label: "Secret", value: "secret" },
            ]}
            value={bindingEditorDraft.bindingKind}
          />
          {bindingEditorDraft.bindingKind === "service" ? (
            <>
              <Select
                onChange={(value) =>
                  setBindingEditorDraft((current) => ({
                    ...current,
                    targetEndpointId: "",
                    targetServiceId: value,
                  }))
                }
                options={services
                  .filter((service) => service.serviceId !== selectedService?.serviceId)
                  .map((service) => ({
                    label: service.displayName || service.serviceId,
                    value: service.serviceId,
                  }))}
                placeholder="target service"
                value={bindingEditorDraft.targetServiceId || undefined}
              />
              <Select
                allowClear
                onChange={(value) =>
                  setBindingEditorDraft((current) => ({
                    ...current,
                    targetEndpointId: value || "",
                  }))
                }
                options={bindingTargetEndpointOptions}
                placeholder="target endpoint (optional)"
                value={bindingEditorDraft.targetEndpointId || undefined}
              />
            </>
          ) : null}
          {bindingEditorDraft.bindingKind === "connector" ? (
            <>
              <Input
                onChange={(event) =>
                  setBindingEditorDraft((current) => ({
                    ...current,
                    connectorType: event.target.value,
                  }))
                }
                placeholder="connector type"
                value={bindingEditorDraft.connectorType}
              />
              <Input
                onChange={(event) =>
                  setBindingEditorDraft((current) => ({
                    ...current,
                    connectorId: event.target.value,
                  }))
                }
                placeholder="connector id"
                value={bindingEditorDraft.connectorId}
              />
            </>
          ) : null}
          {bindingEditorDraft.bindingKind === "secret" ? (
            <Input
              onChange={(event) =>
                setBindingEditorDraft((current) => ({
                  ...current,
                  secretName: event.target.value,
                }))
              }
              placeholder="secret name"
              value={bindingEditorDraft.secretName}
            />
          ) : null}
          <Input.TextArea
            onChange={(event) =>
              setBindingEditorDraft((current) => ({
                ...current,
                policyIdsText: event.target.value,
              }))
            }
            placeholder="policy ids, separated by commas"
            rows={3}
            value={bindingEditorDraft.policyIdsText}
          />
        </Space>
      </Modal>
    </>
  );
};

const RunSummaryCard: React.FC<{
  run: ScopeServiceRunSummary;
  selected: boolean;
  onInspectAudit: () => void;
  onOpenExplorer: () => void;
  onOpenRuns: () => void;
}> = ({ run, selected, onInspectAudit, onOpenExplorer, onOpenRuns }) => (
  <div
    style={{
      border: selected
        ? "1px solid var(--ant-color-primary)"
        : "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 10,
      padding: 12,
    }}
  >
    <Space wrap size={[8, 8]}>
      <Typography.Text strong>{run.runId}</Typography.Text>
      <AevatarStatusTag
        domain="run"
        status={run.completionStatus || "unknown"}
        label={run.completionStatus || "unknown"}
      />
    </Space>
    <Typography.Text type="secondary">
      Workflow {run.workflowName || "n/a"} · Revision {run.revisionId || "n/a"}
    </Typography.Text>
    <Typography.Text type="secondary">
      Updated {formatDateTime(run.lastUpdatedAt)} · Actor {run.actorId || "n/a"}
    </Typography.Text>
    <Typography.Text type="secondary">
      {run.lastError || run.lastOutput || "No output snapshot has been captured yet."}
    </Typography.Text>
    <Space wrap>
      <Button icon={<EyeOutlined />} onClick={onInspectAudit} type={selected ? "primary" : "default"}>
        {selected ? "Inspecting" : "Load audit"}
      </Button>
      <Button icon={<BranchesOutlined />} onClick={onOpenExplorer}>
        Runtime
      </Button>
      <Button icon={<RetweetOutlined />} onClick={onOpenRuns}>
        Open Runs
      </Button>
    </Space>
  </div>
);

export default ScopeServiceRuntimeWorkbench;
