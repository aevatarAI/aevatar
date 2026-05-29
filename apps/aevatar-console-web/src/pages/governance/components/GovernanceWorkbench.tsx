import {
  DeploymentUnitOutlined,
  PlusOutlined,
} from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useIntl } from "@umijs/max";
import {
  Alert,
  Button,
  Space,
  Tag,
  Table,
  Tabs,
  Typography,
  theme,
} from "antd";
import type { ColumnsType } from "antd/es/table";
import React, { useCallback, useEffect, useMemo, useState } from "react";
import { governanceApi } from "@/shared/api/governanceApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { history } from "@/shared/navigation/history";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
import type {
  ActivationCapabilityView,
  GovernanceIdentityInput,
  ServiceBindingInput,
  ServiceBindingSnapshot,
  ServiceEndpointExposureInput,
  ServiceEndpointExposureSnapshot,
  ServicePolicyInput,
  ServicePolicySnapshot,
} from "@/shared/models/governance";
import type {
  ServiceCatalogSnapshot,
  ServiceRevisionSnapshot,
} from "@/shared/models/services";
import {
  AEVATAR_GLOBAL_UI_SPEC,
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  buildAevatarViewportStyle,
  formatAevatarStatusLabel,
  resolveAevatarSemanticTone,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import { AevatarCompactText } from "@/shared/ui/compactText";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import GovernanceAuditTimeline, {
  type GovernanceAuditEvent,
} from "./GovernanceAuditTimeline";
import GovernanceInspectorDrawer, {
  type GovernanceInspectorTarget,
} from "./GovernanceInspectorDrawer";
import GovernanceQueryCard from "./GovernanceQueryCard";
import type { GovernanceRevisionOption } from "./GovernanceQueryCard";
import {
  buildGovernanceCompactValue,
  formatGovernanceTimestamp,
  GovernanceSelectionNotice,
  GovernanceSummaryPanel,
} from "./GovernanceResultPanels";
import {
  buildGovernanceWorkbenchHref,
  buildGovernanceServiceOptions,
  type GovernanceWorkbenchView,
  hasGovernanceScope,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
  readGovernanceWorkbenchView,
  type GovernanceDraft,
} from "./governanceQuery";
import { t } from "@/shared/i18n/messages";
import type { ConsoleMessageDescriptor } from "@/shared/i18n/messages";

type GovernanceNotice = {
  message: string;
  tone: "error" | "info" | "success" | "warning";
};

type GovernanceViewMeta = {
  description: string;
  title: ConsoleMessageDescriptor;
};

const defaultScopeServiceAppId = "default";
const defaultScopeServiceNamespace = "default";

type GovernanceViewActionConfig = {
  label: string;
  icon?: React.ReactNode;
  onClick: () => void;
  type?: "default" | "primary";
};

const governanceViewMeta: Record<GovernanceWorkbenchView, GovernanceViewMeta> = {
  overview: {
    description: "",
    title: {
      defaultMessage: "Overview",
      id: "pages.governance.governanceworkbench.views.overview",
    },
  },
  activation: {
    description: "",
    title: {
      defaultMessage: "Activation diagnostics",
      id: "pages.governance.governanceworkbench.views.activation",
    },
  },
  bindings: {
    description: "",
    title: {
      defaultMessage: "Bindings",
      id: "pages.governance.governanceworkbench.views.bindings",
    },
  },
  changes: {
    description: "",
    title: {
      defaultMessage: "Change summary",
      id: "pages.governance.governanceworkbench.views.changes",
    },
  },
  endpoints: {
    description: "",
    title: {
      defaultMessage: "Endpoints",
      id: "pages.governance.governanceworkbench.views.endpoints",
    },
  },
  policies: {
    description: "",
    title: {
      defaultMessage: "Policies",
      id: "pages.governance.governanceworkbench.views.policies",
    },
  },
};

function pickPreferredRevision(
  revisions: readonly ServiceRevisionSnapshot[],
): string {
  const preferredRevision = revisions.find((revision) =>
    ["published", "ready", "active"].some((status) =>
      revision.status.toLowerCase().includes(status),
    ),
  );

  return preferredRevision?.revisionId ?? revisions[0]?.revisionId ?? "";
}

function matchSelectedService(
  services: readonly ServiceCatalogSnapshot[],
  draft: GovernanceDraft,
): ServiceCatalogSnapshot | null {
  const normalizedServiceId = draft.serviceId.trim();
  if (!normalizedServiceId) {
    return null;
  }

  const exactMatch = services.find(
    (service) =>
      service.serviceId === normalizedServiceId &&
      service.tenantId === draft.tenantId.trim() &&
      service.namespace === draft.namespace.trim() &&
      (!draft.appId.trim() || service.appId === draft.appId.trim()),
  );

  if (exactMatch) {
    return exactMatch;
  }

  return services.find((service) => service.serviceId === normalizedServiceId) ?? null;
}

function buildGovernanceIdentity(
  draft: GovernanceDraft,
): GovernanceIdentityInput | null {
  if (!hasGovernanceScope(draft) || !draft.serviceId.trim()) {
    return null;
  }

  return {
    tenantId: draft.tenantId.trim(),
    appId: draft.appId.trim(),
    namespace: draft.namespace.trim(),
  };
}

function buildBlankPolicy(): ServicePolicySnapshot {
  return {
    activationRequiredBindingIds: [],
    displayName: "",
    invokeAllowedCallerServiceKeys: [],
    invokeRequiresActiveDeployment: false,
    policyId: "",
    retired: false,
  };
}

function buildBlankBinding(): ServiceBindingSnapshot {
  return {
    bindingId: "",
    bindingKind: "service",
    connectorRef: null,
    displayName: "",
    policyIds: [],
    retired: false,
    secretRef: null,
    serviceRef: null,
  };
}

function buildBlankEndpoint(): ServiceEndpointExposureSnapshot {
  return {
    description: "",
    displayName: "",
    endpointId: "",
    exposureKind: "internal",
    kind: "command",
    policyIds: [],
    requestTypeUrl: "",
    responseTypeUrl: "",
  };
}

function buildBindingTargetLabel(record: ServiceBindingSnapshot): string {
  if (record.serviceRef) {
    return `${record.serviceRef.identity.serviceId}:${record.serviceRef.endpointId || "*"}`;
  }

  if (record.connectorRef) {
    return `${record.connectorRef.connectorType}:${record.connectorRef.connectorId}`;
  }

  if (record.secretRef) {
    return record.secretRef.secretName;
  }

  return "n/a";
}

function buildPolicySummary(record: ServicePolicySnapshot): string {
  const segments: string[] = [];

  if (record.activationRequiredBindingIds.length > 0) {
    segments.push(
      `Requires ${record.activationRequiredBindingIds.length} activation binding${record.activationRequiredBindingIds.length === 1 ? "" : "s"}`,
    );
  }

  if (record.invokeAllowedCallerServiceKeys.length > 0) {
    segments.push(
      `${record.invokeAllowedCallerServiceKeys.length} caller allowlist entr${record.invokeAllowedCallerServiceKeys.length === 1 ? "y" : "ies"}`,
    );
  }

  if (record.invokeRequiresActiveDeployment) {
    segments.push("Blocks invokes without active deployment");
  }

  return segments.join(" · ") || "No activation or caller restrictions configured.";
}

function buildEndpointSummary(record: ServiceEndpointExposureSnapshot): string {
  const segments = [
    record.requestTypeUrl || "No request contract",
    record.policyIds.length > 0
      ? `${record.policyIds.length} attached polic${record.policyIds.length === 1 ? "y" : "ies"}`
      : "No policy attachments",
  ];

  return segments.join(" · ");
}

function resolveLatestGovernanceTimestamp(
  ...values: Array<string | undefined | null>
): string | undefined {
  return values
    .map((value) => value?.trim() ?? "")
    .filter(Boolean)
    .sort((left, right) => new Date(right).getTime() - new Date(left).getTime())[0];
}

function buildAuditEvents(input: {
  activationView: ActivationCapabilityView | undefined;
  bindings: ServiceBindingSnapshot[];
  bindingsUpdatedAt?: string;
  endpoints: ServiceEndpointExposureSnapshot[];
  endpointsUpdatedAt?: string;
  policies: ServicePolicySnapshot[];
  policiesUpdatedAt?: string;
  revisions: ServiceRevisionSnapshot[];
  selectedService: ServiceCatalogSnapshot | null;
}): GovernanceAuditEvent[] {
  const {
    activationView,
    bindings,
    bindingsUpdatedAt,
    endpoints,
    endpointsUpdatedAt,
    policies,
    policiesUpdatedAt,
    revisions,
    selectedService,
  } = input;

  const events: GovernanceAuditEvent[] = [];

  if (selectedService) {
    events.push({
      action: "Governance scope attached",
      actor: "Service Registry",
      at: selectedService.updatedAt,
      id: `service-${selectedService.serviceId}-${selectedService.updatedAt}`,
      status: selectedService.deploymentStatus || "active",
      summary: `Governance is now anchored to ${selectedService.displayName || selectedService.serviceId}.`,
      targetId: selectedService.serviceId,
      targetKind: "service",
      targetLabel: selectedService.displayName || selectedService.serviceId,
    });
  }

  for (const revision of revisions) {
    if (revision.publishedAt) {
      events.push({
        action: "Revision published",
        actor: "Release Manager",
        at: revision.publishedAt,
        id: `revision-published-${revision.revisionId}`,
        status: "published",
        summary: `Revision ${revision.revisionId} was published for governance evaluation.`,
        targetId: revision.revisionId,
        targetKind: "activation",
        targetLabel: revision.revisionId,
      });
    } else if (revision.preparedAt) {
      events.push({
        action: "Revision prepared",
        actor: "Release Manager",
        at: revision.preparedAt,
        id: `revision-prepared-${revision.revisionId}`,
        status: revision.status || "pending",
        summary: `Revision ${revision.revisionId} is prepared and waiting for promotion decisions.`,
        targetId: revision.revisionId,
        targetKind: "activation",
        targetLabel: revision.revisionId,
      });
    }
  }

  if (bindingsUpdatedAt) {
    events.push({
      action: "Binding catalog synchronized",
      actor: "Binding Registry",
      at: bindingsUpdatedAt,
      id: `binding-catalog-${bindingsUpdatedAt}`,
      status: bindings.some((binding) => binding.retired) ? "retired" : "active",
      summary: `${bindings.length} binding${bindings.length === 1 ? "" : "s"} are currently tracked for this service.`,
      targetId: "binding-catalog",
      targetKind: "binding",
      targetLabel: `${bindings.length} bindings`,
    });
  }

  for (const binding of bindings.filter((item) => item.retired)) {
    events.push({
      action: "Binding retired",
      actor: "Binding Registry",
      at: bindingsUpdatedAt || selectedService?.updatedAt || "",
      id: `binding-retired-${binding.bindingId}`,
      status: "retired",
      summary: `${binding.displayName || binding.bindingId} was removed from the active dependency surface.`,
      targetId: binding.bindingId,
      targetKind: "binding",
      targetLabel: binding.displayName || binding.bindingId,
    });
  }

  if (policiesUpdatedAt) {
    events.push({
      action: "Policy catalog synchronized",
      actor: "Policy Engine",
      at: policiesUpdatedAt,
      id: `policy-catalog-${policiesUpdatedAt}`,
      status: policies.some((policy) => policy.retired) ? "retired" : "active",
      summary: `${policies.length} governance polic${policies.length === 1 ? "y" : "ies"} are materialized for this service.`,
      targetId: "policy-catalog",
      targetKind: "policy",
      targetLabel: `${policies.length} policies`,
    });
  }

  for (const policy of policies.filter(
    (item) =>
      item.retired ||
      item.invokeRequiresActiveDeployment ||
      item.activationRequiredBindingIds.length > 0,
  )) {
    events.push({
      action: policy.retired ? "Policy retired" : "Policy gate enforced",
      actor: "Policy Engine",
      at: policiesUpdatedAt || selectedService?.updatedAt || "",
      id: `policy-${policy.policyId}-${policy.retired ? "retired" : "enforced"}`,
      status: policy.retired ? "retired" : "active",
      summary: buildPolicySummary(policy),
      targetId: policy.policyId,
      targetKind: "policy",
      targetLabel: policy.displayName || policy.policyId,
    });
  }

  if (endpointsUpdatedAt) {
    events.push({
      action: "Endpoint catalog synchronized",
      actor: "Exposure Controller",
      at: endpointsUpdatedAt,
      id: `endpoint-catalog-${endpointsUpdatedAt}`,
      status: endpoints.some((endpoint) => endpoint.exposureKind === "disabled")
        ? "disabled"
        : "active",
      summary: `${endpoints.length} endpoint${endpoints.length === 1 ? "" : "s"} are under governance exposure control.`,
      targetId: "endpoint-catalog",
      targetKind: "endpoint",
      targetLabel: `${endpoints.length} endpoints`,
    });
  }

  for (const endpoint of endpoints.filter(
    (item) =>
      item.exposureKind === "public" || item.exposureKind === "disabled",
  )) {
    events.push({
      action:
        endpoint.exposureKind === "public"
          ? "Endpoint opened"
          : "Endpoint disabled",
      actor: "Exposure Controller",
      at: endpointsUpdatedAt || selectedService?.updatedAt || "",
      id: `endpoint-${endpoint.endpointId}-${endpoint.exposureKind}`,
      status: endpoint.exposureKind,
      summary: buildEndpointSummary(endpoint),
      targetId: endpoint.endpointId,
      targetKind: "endpoint",
      targetLabel: endpoint.displayName || endpoint.endpointId,
    });
  }

  if (activationView) {
    events.push({
      action:
        activationView.missingPolicyIds.length > 0
          ? "Activation blocked"
          : "Activation verified",
      actor: "Activation Guard",
      at: selectedService?.updatedAt || policiesUpdatedAt || endpointsUpdatedAt || "",
      id: `activation-${activationView.revisionId || "unresolved"}`,
      status:
        activationView.missingPolicyIds.length > 0 ? "blocked" : "ready",
      summary:
        activationView.missingPolicyIds.length > 0
          ? `Revision ${activationView.revisionId || "unresolved"} is missing policies: ${activationView.missingPolicyIds.join(", ")}.`
          : `Revision ${activationView.revisionId || "unresolved"} has a complete governance envelope.`,
      targetId: activationView.revisionId || "activation",
      targetKind: "activation",
      targetLabel: activationView.revisionId || "Activation view",
    });
  }

  return events
    .filter((event) => event.at.trim().length > 0)
    .sort(
      (left, right) =>
        new Date(right.at).getTime() - new Date(left.at).getTime(),
    );
}

const WorkbenchStatusTag: React.FC<{
  status: string;
}> = ({ status }) => {
  const { token } = theme.useToken();

  return (
    <span
      style={buildAevatarTagStyle(
        token as AevatarThemeSurfaceToken,
        "governance",
        status,
      )}
    >
      {formatAevatarStatusLabel(status)}
    </span>
  );
};

const GovernanceWorkbench: React.FC = () => {
  const intl = useIntl();
  const locationSearch = React.useSyncExternalStore(
    (listener) => {
      if (typeof window === "undefined") {
        return () => undefined;
      }

      window.addEventListener("popstate", listener);
      return () => {
        window.removeEventListener("popstate", listener);
      };
    },
    () => (typeof window === "undefined" ? "" : window.location.search),
    () => "",
  );
  const view = useMemo(
    () => readGovernanceWorkbenchView(locationSearch),
    [locationSearch],
  );
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const queryClient = useQueryClient();

  const initialDraft = useMemo(() => readGovernanceDraft(), []);
  const [draft, setDraft] = useState<GovernanceDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<GovernanceDraft>(initialDraft);
  const [notice, setNotice] = useState<GovernanceNotice | null>(null);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [drawerTarget, setDrawerTarget] = useState<GovernanceInspectorTarget | null>(
    null,
  );
  const authSessionQuery = useQuery({
    queryKey: ["governance", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  const serviceQuery = useMemo(() => normalizeGovernanceQuery(draft), [draft]);
  const activeQuery = useMemo(
    () => normalizeGovernanceQuery(activeDraft),
    [activeDraft],
  );
  const serviceSearchEnabled = useMemo(() => hasGovernanceScope(draft), [draft]);
  const activeIdentity = useMemo(
    () => buildGovernanceIdentity(activeDraft),
    [activeDraft],
  );
  const hasSelectedServiceContext = Boolean(
    activeIdentity && activeDraft.serviceId.trim(),
  );

  const servicesQuery = useQuery({
    enabled: serviceSearchEnabled,
    queryFn: () => servicesApi.listServices({ ...serviceQuery, take: 200 }),
    queryKey: ["governance", "services", serviceQuery],
  });

  const selectedService = useMemo(
    () => matchSelectedService(servicesQuery.data ?? [], activeDraft),
    [activeDraft, servicesQuery.data],
  );

  const bindingsQuery = useQuery({
    enabled: hasSelectedServiceContext,
    queryFn: () => governanceApi.getBindings(activeDraft.serviceId, activeQuery),
    queryKey: ["governance", "bindings", activeDraft.serviceId, activeQuery],
  });

  const policiesQuery = useQuery({
    enabled: hasSelectedServiceContext,
    queryFn: () => governanceApi.getPolicies(activeDraft.serviceId, activeQuery),
    queryKey: ["governance", "policies", activeDraft.serviceId, activeQuery],
  });

  const endpointsQuery = useQuery({
    enabled: hasSelectedServiceContext,
    queryFn: () =>
      governanceApi.getEndpointCatalog(activeDraft.serviceId, activeQuery),
    queryKey: ["governance", "endpoints", activeDraft.serviceId, activeQuery],
  });

  const revisionsQuery = useQuery({
    enabled: hasSelectedServiceContext,
    queryFn: () => servicesApi.getRevisions(activeDraft.serviceId, activeQuery),
    queryKey: ["governance", "revisions", activeDraft.serviceId, activeQuery],
  });

  const preferredRevisionId = useMemo(
    () => pickPreferredRevision(revisionsQuery.data?.revisions ?? []),
    [revisionsQuery.data],
  );

  const activationRevisionId =
    view === "activation"
      ? activeDraft.revisionId.trim()
      : activeDraft.revisionId.trim() || preferredRevisionId;

  const activationQuery = useQuery({
    enabled: hasSelectedServiceContext && activationRevisionId.length > 0,
    queryFn: () =>
      governanceApi.getActivationCapability(activeDraft.serviceId, {
        ...activeQuery,
        revisionId: activationRevisionId,
      }),
    queryKey: [
      "governance",
      "activation",
      activeDraft.serviceId,
      activeQuery,
      activationRevisionId,
    ],
  });

  const serviceOptions = useMemo(
    () => buildGovernanceServiceOptions(servicesQuery.data ?? []),
    [servicesQuery.data],
  );

  const revisionOptions = useMemo<GovernanceRevisionOption[]>(
    () =>
      (revisionsQuery.data?.revisions ?? []).map((revision) => ({
        label: `${revision.revisionId} · ${revision.status}`,
        value: revision.revisionId,
      })),
    [revisionsQuery.data],
  );

  useEffect(() => {
    const scopeId = resolvedScope?.scopeId?.trim();
    if (!scopeId) {
      return;
    }

    if (
      draft.tenantId.trim() ||
      draft.appId.trim() ||
      draft.namespace.trim()
    ) {
      return;
    }

    const nextDraft = {
      ...draft,
      appId: defaultScopeServiceAppId,
      namespace: defaultScopeServiceNamespace,
      tenantId: scopeId,
    };
    setDraft(nextDraft);
    if (
      !activeDraft.tenantId.trim() &&
      !activeDraft.appId.trim() &&
      !activeDraft.namespace.trim() &&
      !activeDraft.serviceId.trim()
    ) {
      setActiveDraft(nextDraft);
      }
  }, [activeDraft, draft, resolvedScope?.scopeId]);

  useEffect(() => {
    if (
      view !== "activation" ||
      !preferredRevisionId.trim() ||
      !activeDraft.serviceId.trim() ||
      activeDraft.revisionId.trim()
    ) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.serviceId.trim() && !currentDraft.revisionId.trim()
        ? {
            ...currentDraft,
            revisionId: preferredRevisionId,
          }
        : currentDraft,
    );
    setActiveDraft((currentDraft) =>
      currentDraft.serviceId.trim() && !currentDraft.revisionId.trim()
        ? {
            ...currentDraft,
            revisionId: preferredRevisionId,
          }
        : currentDraft,
    );
  }, [
    activeDraft.revisionId,
    activeDraft.serviceId,
    preferredRevisionId,
    view,
  ]);

  useEffect(() => {
    const revisions = revisionsQuery.data?.revisions ?? [];
    if (!draft.serviceId.trim() || revisions.length === 0) {
      return;
    }

    const currentRevisionId = draft.revisionId.trim();
    const revisionExists = revisions.some(
      (revision) => revision.revisionId === currentRevisionId,
    );
    if (currentRevisionId && revisionExists) {
      return;
    }

    const nextRevisionId = pickPreferredRevision(revisions);
    if (!nextRevisionId) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.serviceId.trim() !== draft.serviceId.trim()
        ? currentDraft
        : {
            ...currentDraft,
            revisionId: nextRevisionId,
          },
    );
  }, [draft.revisionId, draft.serviceId, revisionsQuery.data]);

  const auditEvents = useMemo(
    () =>
      buildAuditEvents({
        activationView: activationQuery.data,
        bindings: bindingsQuery.data?.bindings ?? [],
        bindingsUpdatedAt: bindingsQuery.data?.updatedAt,
        endpoints: endpointsQuery.data?.endpoints ?? [],
        endpointsUpdatedAt: endpointsQuery.data?.updatedAt,
        policies: policiesQuery.data?.policies ?? [],
        policiesUpdatedAt: policiesQuery.data?.updatedAt,
        revisions: revisionsQuery.data?.revisions ?? [],
        selectedService,
      }),
    [
      activationQuery.data,
      bindingsQuery.data,
      endpointsQuery.data,
      policiesQuery.data,
      revisionsQuery.data,
      selectedService,
    ],
  );

  const activePolicies = useMemo(
    () => (policiesQuery.data?.policies ?? []).filter((policy) => !policy.retired),
    [policiesQuery.data],
  );

  const activeBindings = useMemo(
    () => (bindingsQuery.data?.bindings ?? []).filter((binding) => !binding.retired),
    [bindingsQuery.data],
  );

  const publicEndpoints = useMemo(
    () =>
      (endpointsQuery.data?.endpoints ?? []).filter(
        (endpoint) => endpoint.exposureKind === "public",
      ),
    [endpointsQuery.data],
  );

  const internalEndpoints = useMemo(
    () =>
      (endpointsQuery.data?.endpoints ?? []).filter(
        (endpoint) => endpoint.exposureKind === "internal",
      ),
    [endpointsQuery.data],
  );

  const disabledEndpoints = useMemo(
    () =>
      (endpointsQuery.data?.endpoints ?? []).filter(
        (endpoint) => endpoint.exposureKind === "disabled",
      ),
    [endpointsQuery.data],
  );

  const latestGovernanceUpdatedAt = useMemo(
    () =>
      resolveLatestGovernanceTimestamp(
        selectedService?.updatedAt,
        bindingsQuery.data?.updatedAt,
        policiesQuery.data?.updatedAt,
        endpointsQuery.data?.updatedAt,
      ),
    [
      bindingsQuery.data,
      endpointsQuery.data,
      policiesQuery.data,
      selectedService?.updatedAt,
    ],
  );

  const governanceMetrics = useMemo(
    () => [
      {
        label: t("pages.governance.governanceworkbench.active.strategies", "Active strategies"),
        tone:
          activePolicies.length > 0
            ? ("default" as const)
            : ("warning" as const),
        value: String(activePolicies.length),
      },
      {
        label: t("pages.governance.governanceworkbench.active.binding", "Active binding"),
        tone:
          activeBindings.length > 0
            ? ("default" as const)
            : ("warning" as const),
        value: String(activeBindings.length),
      },
      {
        label: t("pages.governance.governanceworkbench.public.entrance", "public entrance"),
        tone: "success" as const,
        value: String(publicEndpoints.length),
      },
      {
        label: t("pages.governance.governanceworkbench.activate.blocking", "activate blocking"),
        tone:
          (activationQuery.data?.missingPolicyIds.length ?? 0) > 0
            ? ("warning" as const)
            : ("success" as const),
        value: String(activationQuery.data?.missingPolicyIds.length ?? 0),
      },
    ],
    [activationQuery.data, activeBindings.length, activePolicies.length, publicEndpoints.length],
  );

  const governanceTabItems = useMemo(
    () =>
      Object.entries(governanceViewMeta).map(([key, meta]) => ({
        key,
        label: intl.formatMessage(meta.title),
      })),
    [intl],
  );

  const navigateToGovernanceView = useCallback(
    (
      nextView: GovernanceWorkbenchView,
      nextDraft: GovernanceDraft = activeDraft,
    ) => {
      history.replace(buildGovernanceWorkbenchHref(nextDraft, nextView));
    },
    [activeDraft],
  );

  const governanceViewActions = useMemo<
    Partial<Record<GovernanceWorkbenchView, GovernanceViewActionConfig>>
  >(
    () => ({
      overview: hasSelectedServiceContext
        ? {
            icon: <DeploymentUnitOutlined />,
            label: t("pages.governance.governanceworkbench.check.activation", "Check activation"),
            onClick: () =>
              navigateToGovernanceView("activation", {
                ...activeDraft,
                revisionId: activationRevisionId,
              }),
            type: "primary",
          }
        : undefined,
      activation:
        activationQuery.data != null
          ? {
              icon: <DeploymentUnitOutlined />,
              label: t("pages.governance.governanceworkbench.open.diagnostics", "Open diagnostics"),
              onClick: () =>
                setDrawerTarget({
                  kind: "activation",
                  record: activationQuery.data,
                }),
            }
          : undefined,
      policies: hasSelectedServiceContext
        ? {
            icon: <PlusOutlined />,
            label: t("pages.governance.governanceworkbench.new.strategy", "New strategy"),
            onClick: () =>
              setDrawerTarget({
                kind: "policy",
                mode: "create",
                record: buildBlankPolicy(),
              }),
            type: "primary",
          }
        : undefined,
      bindings: hasSelectedServiceContext
        ? {
            icon: <PlusOutlined />,
            label: t("pages.governance.governanceworkbench.new.binding", "New binding"),
            onClick: () =>
              setDrawerTarget({
                kind: "binding",
                mode: "create",
                record: buildBlankBinding(),
              }),
            type: "primary",
          }
        : undefined,
      endpoints: hasSelectedServiceContext
        ? {
            icon: <PlusOutlined />,
            label: t("pages.governance.governanceworkbench.new.entrance", "New entrance"),
            onClick: () =>
              setDrawerTarget({
                kind: "endpoint",
                mode: "create",
                record: buildBlankEndpoint(),
              }),
            type: "primary",
          }
        : undefined,
    }),
    [
      activationQuery.data,
      activationRevisionId,
      activeDraft,
      hasSelectedServiceContext,
      navigateToGovernanceView,
    ],
  );

  const stageTableShellStyle = useMemo(
    () => ({
      ...buildAevatarPanelStyle(surfaceToken, {
        background: "rgba(255, 255, 255, 0.98)",
      }),
      borderRadius: 16,
      boxShadow: "none",
      overflow: "hidden",
    }),
    [surfaceToken],
  );

  const policyTableColumns = useMemo<ColumnsType<ServicePolicySnapshot>>(
    () => [
      {
        key: "policy",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.strategy",
          defaultMessage: "Strategy",
        }),
        render: (_, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>
              {record.displayName || record.policyId}
            </Typography.Text>
            <AevatarCompactText
              color="var(--ant-color-text-secondary)"
              monospace
              value={record.policyId}
            />
          </Space>
        ),
      },
      {
        key: "bindings",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.activate.dependencies",
          defaultMessage: "Activate dependencies",
        }),
        render: (_, record) =>
          record.activationRequiredBindingIds.length > 0
            ? intl.formatMessage(
                {
                  id: "pages.governance.governanceworkbench.bindings",
                  defaultMessage: "{value1} bindings",
                },
                { value1: record.activationRequiredBindingIds.length },
              )
            : intl.formatMessage({
                id: "pages.governance.governanceworkbench.no.pre.binding",
                defaultMessage: "No pre-binding",
              }),
      },
      {
        key: "callers",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.call.limit",
          defaultMessage: "call limit",
        }),
        render: (_, record) =>
          record.invokeAllowedCallerServiceKeys.length > 0
            ? intl.formatMessage(
                {
                  id: "pages.governance.governanceworkbench.items.allowlist",
                  defaultMessage: "{value1} items allowlist",
                },
                { value1: record.invokeAllowedCallerServiceKeys.length },
              )
            : intl.formatMessage({
                id: "pages.governance.governanceworkbench.unlimited.callers",
                defaultMessage: "Unlimited callers",
              }),
      },
      {
        key: "status",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.state",
          defaultMessage: "state",
        }),
        width: 220,
        render: (_, record) => (
          <Space wrap size={[8, 8]}>
            <WorkbenchStatusTag status={record.retired ? "retired" : "active"} />
            {record.invokeRequiresActiveDeployment ? (
              <Tag color="gold">
                {intl.formatMessage({
                  id: "pages.governance.governanceworkbench.requires.deployment.to.be",
                  defaultMessage: "Requires deployment to be activated",
                })}
              </Tag>
            ) : null}
          </Space>
        ),
      },
      {
        key: "actions",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.operate",
          defaultMessage: "operate",
        }),
        width: 120,
        render: (_, record) => (
          <Button
            size="small"
            type="link"
            onClick={() =>
              setDrawerTarget({
                kind: "policy",
                mode: "edit",
                record,
              })
            }
          >
            {intl.formatMessage({
              id: "pages.governance.governanceworkbench.configuration",
              defaultMessage: "Configuration",
            })}
          </Button>
        ),
      },
    ],
    [intl],
  );

  const bindingTableColumns = useMemo<ColumnsType<ServiceBindingSnapshot>>(
    () => [
      {
        key: "binding",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.binding",
          defaultMessage: "binding",
        }),
        render: (_, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>
              {record.displayName || record.bindingId}
            </Typography.Text>
            <AevatarCompactText
              color="var(--ant-color-text-secondary)"
              monospace
              value={record.bindingId}
            />
          </Space>
        ),
      },
      {
        dataIndex: "bindingKind",
        key: "bindingKind",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.type",
          defaultMessage: "type",
        }),
        width: 120,
        render: (_, record) => formatAevatarStatusLabel(record.bindingKind),
      },
      {
        key: "target",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.target",
          defaultMessage: "Target",
        }),
        render: (_, record) => (
          <AevatarCompactText
            maxWidth={240}
            monospace
            value={buildBindingTargetLabel(record)}
          />
        ),
      },
      {
        key: "policies",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.mount.strategy",
          defaultMessage: "Mount strategy",
        }),
        render: (_, record) =>
          record.policyIds.length > 0
            ? intl.formatMessage(
                {
                  id: "pages.governance.governanceworkbench.items",
                  defaultMessage: "{value1} items",
                },
                { value1: record.policyIds.length },
              )
            : intl.formatMessage({
                id: "pages.governance.governanceworkbench.unlisted.strategy",
                defaultMessage: "Unlisted strategy",
              }),
      },
      {
        key: "status",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.state.2",
          defaultMessage: "state",
        }),
        width: 120,
        render: (_, record) => (
          <WorkbenchStatusTag status={record.retired ? "retired" : "active"} />
        ),
      },
      {
        key: "actions",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.operate.2",
          defaultMessage: "operate",
        }),
        width: 120,
        render: (_, record) => (
          <Button
            size="small"
            type="link"
            onClick={() =>
              setDrawerTarget({
                kind: "binding",
                mode: "edit",
                record,
              })
            }
          >
            {intl.formatMessage({
              id: "pages.governance.governanceworkbench.configuration.2",
              defaultMessage: "Configuration",
            })}
          </Button>
        ),
      },
    ],
    [intl],
  );

  const endpointTableColumns = useMemo<ColumnsType<ServiceEndpointExposureSnapshot>>(
    () => [
      {
        key: "endpoint",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.entrance",
          defaultMessage: "Entrance",
        }),
        render: (_, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>
              {record.displayName || record.endpointId}
            </Typography.Text>
            <AevatarCompactText
              color="var(--ant-color-text-secondary)"
              monospace
              value={record.endpointId}
            />
          </Space>
        ),
      },
      {
        dataIndex: "kind",
        key: "kind",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.type.2",
          defaultMessage: "type",
        }),
        width: 120,
        render: (_, record) => formatAevatarStatusLabel(record.kind),
      },
      {
        dataIndex: "exposureKind",
        key: "exposureKind",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.exposure.status",
          defaultMessage: "exposure status",
        }),
        width: 140,
        render: (_, record) => (
          <WorkbenchStatusTag status={record.exposureKind || "internal"} />
        ),
      },
      {
        key: "policies",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.mount.strategy.2",
          defaultMessage: "Mount strategy",
        }),
        render: (_, record) =>
          record.policyIds.length > 0
            ? intl.formatMessage(
                {
                  id: "pages.governance.governanceworkbench.items.2",
                  defaultMessage: "{value1} items",
                },
                { value1: record.policyIds.length },
              )
            : intl.formatMessage({
                id: "pages.governance.governanceworkbench.unlisted.strategy.2",
                defaultMessage: "Unlisted strategy",
              }),
      },
      {
        key: "requestTypeUrl",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.request.contract",
          defaultMessage: "request contract",
        }),
        render: (_, record) =>
          record.requestTypeUrl ? (
            <AevatarCompactText
              maxChars={28}
              mode="tail"
              monospace
              value={record.requestTypeUrl}
            />
          ) : (
            intl.formatMessage({
              id: "pages.governance.governanceworkbench.not.declared",
              defaultMessage: "Not declared",
            })
          ),
      },
      {
        key: "actions",
        title: intl.formatMessage({
          id: "pages.governance.governanceworkbench.operate.3",
          defaultMessage: "operate",
        }),
        width: 120,
        render: (_, record) => (
          <Button
            size="small"
            type="link"
            onClick={() =>
              setDrawerTarget({
                kind: "endpoint",
                mode: "edit",
                record,
              })
            }
          >
            {intl.formatMessage({
              id: "pages.governance.governanceworkbench.configuration.3",
              defaultMessage: "Configuration",
            })}
          </Button>
        ),
      },
    ],
    [intl],
  );

  const invalidateGovernanceQueries = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["governance"] }),
      queryClient.invalidateQueries({ queryKey: ["services"] }),
    ]);
  }, [queryClient]);

  const runGovernanceAction = useCallback(
    async (
      action: string,
      successMessage: string,
      task: () => Promise<unknown>,
      closeDrawer = false,
    ) => {
      setBusyAction(action);
      try {
        await task();
        setNotice({
          message: successMessage,
          tone: resolveAevatarSemanticTone("governance", action).startsWith("error")
            ? "warning"
            : "success",
        });
        await invalidateGovernanceQueries();
        if (closeDrawer) {
          setDrawerTarget(null);
        }
      } catch (error) {
        setNotice({
          message:
            error instanceof Error
              ? error.message
              : "Governance action failed.",
          tone: "error",
        });
      } finally {
        setBusyAction(null);
      }
    },
    [invalidateGovernanceQueries],
  );

  const handleCreatePolicy = useCallback(
    async (input: ServicePolicyInput) => {
      await runGovernanceAction(
        "create-policy",
        `Policy ${input.policyId} was accepted for governance creation.`,
        () => governanceApi.createPolicy(activeDraft.serviceId, input),
        true,
      );
    },
    [activeDraft.serviceId, runGovernanceAction],
  );

  const handleCreateBinding = useCallback(
    async (input: ServiceBindingInput) => {
      await runGovernanceAction(
        "create-binding",
        `Binding ${input.bindingId} was accepted for governance creation.`,
        () => governanceApi.createBinding(activeDraft.serviceId, input),
        true,
      );
    },
    [activeDraft.serviceId, runGovernanceAction],
  );

  const handleUpdateBinding = useCallback(
    async (bindingId: string, input: ServiceBindingInput) => {
      await runGovernanceAction(
        "save-binding",
        `Binding ${bindingId} was accepted for update.`,
        () => governanceApi.updateBinding(activeDraft.serviceId, bindingId, input),
        true,
      );
    },
    [activeDraft.serviceId, runGovernanceAction],
  );

  const handleUpdatePolicy = useCallback(
    async (policyId: string, input: ServicePolicyInput) => {
      await runGovernanceAction(
        "save-policy",
        `Policy ${policyId} was accepted for update.`,
        () => governanceApi.updatePolicy(activeDraft.serviceId, policyId, input),
        true,
      );
    },
    [activeDraft.serviceId, runGovernanceAction],
  );

  const handleRetirePolicy = useCallback(
    async (policyId: string) => {
      if (!activeIdentity) {
        return;
      }

      await runGovernanceAction(
        "retire-policy",
        `Policy ${policyId} was accepted for retirement.`,
        () => governanceApi.retirePolicy(activeDraft.serviceId, policyId, activeIdentity),
        true,
      );
    },
    [activeDraft.serviceId, activeIdentity, runGovernanceAction],
  );

  const handleRetireBinding = useCallback(
    async (bindingId: string) => {
      if (!activeIdentity) {
        return;
      }

      await runGovernanceAction(
        "retire-binding",
        `Binding ${bindingId} was accepted for retirement.`,
        () =>
          governanceApi.retireBinding(
            activeDraft.serviceId,
            bindingId,
            activeIdentity,
          ),
        true,
      );
    },
    [activeDraft.serviceId, activeIdentity, runGovernanceAction],
  );

  const handleSetEndpointExposure = useCallback(
    async (endpointId: string, exposureKind: string) => {
      if (!activeIdentity || !endpointsQuery.data) {
        return;
      }

      const payload = {
        ...activeIdentity,
        endpoints: endpointsQuery.data.endpoints.map((endpoint) =>
          endpoint.endpointId === endpointId
            ? {
                ...endpoint,
                exposureKind,
              }
            : endpoint,
        ),
      };

      await runGovernanceAction(
        `set-endpoint-exposure:${exposureKind}`,
        `Endpoint ${endpointId} was accepted for ${formatAevatarStatusLabel(exposureKind).toLowerCase()} exposure.`,
        () => governanceApi.updateEndpointCatalog(activeDraft.serviceId, payload),
        true,
      );
    },
    [activeDraft.serviceId, activeIdentity, endpointsQuery.data, runGovernanceAction],
  );

  const handleCreateEndpoint = useCallback(
    async (input: ServiceEndpointExposureInput) => {
      if (!activeIdentity) {
        return;
      }

      const currentEndpoints = endpointsQuery.data?.endpoints ?? [];
      const payload = {
        ...activeIdentity,
        endpoints: [...currentEndpoints, input],
      };

      await runGovernanceAction(
        "create-endpoint",
        `Endpoint ${input.endpointId} was accepted for governance creation.`,
        () =>
          endpointsQuery.data
            ? governanceApi.updateEndpointCatalog(activeDraft.serviceId, payload)
            : governanceApi.createEndpointCatalog(activeDraft.serviceId, payload),
        true,
      );
    },
    [activeDraft.serviceId, activeIdentity, endpointsQuery.data, runGovernanceAction],
  );

  const handleUpdateEndpoint = useCallback(
    async (endpointId: string, input: ServiceEndpointExposureInput) => {
      if (!activeIdentity || !endpointsQuery.data) {
        return;
      }

      const payload = {
        ...activeIdentity,
        endpoints: endpointsQuery.data.endpoints.map((endpoint) =>
          endpoint.endpointId === endpointId ? input : endpoint,
        ),
      };

      await runGovernanceAction(
        "save-endpoint",
        `Endpoint ${endpointId} was accepted for update.`,
        () => governanceApi.updateEndpointCatalog(activeDraft.serviceId, payload),
        true,
      );
    },
    [activeDraft.serviceId, activeIdentity, endpointsQuery.data, runGovernanceAction],
  );

  const openAuditEvent = useCallback(
    (event: GovernanceAuditEvent) => {
      if (event.targetKind === "policy") {
        const record = (policiesQuery.data?.policies ?? []).find(
          (policy) => policy.policyId === event.targetId,
        );
        if (record) {
          setDrawerTarget({
            kind: "policy",
            mode: "edit",
            record,
          });
          return;
        }
      }

      if (event.targetKind === "binding") {
        const record = (bindingsQuery.data?.bindings ?? []).find(
          (binding) => binding.bindingId === event.targetId,
        );
        if (record) {
          setDrawerTarget({
            kind: "binding",
            mode: "edit",
            record,
          });
          return;
        }
      }

      if (event.targetKind === "endpoint") {
        const record = (endpointsQuery.data?.endpoints ?? []).find(
          (endpoint) => endpoint.endpointId === event.targetId,
        );
        if (record) {
          setDrawerTarget({
            kind: "endpoint",
            mode: "edit",
            record,
          });
          return;
        }
      }

      if (event.targetKind === "activation" && activationQuery.data) {
        setDrawerTarget({
          kind: "activation",
          record: activationQuery.data,
        });
        return;
      }

      setDrawerTarget({
        kind: "audit",
        event,
      });
    },
    [
      activationQuery.data,
      bindingsQuery.data,
      endpointsQuery.data,
      policiesQuery.data,
    ],
  );

  const renderStageForView = useCallback((targetView: GovernanceWorkbenchView) => {
    if (!hasSelectedServiceContext) {
      return (
        <GovernanceSelectionNotice
          title={t("pages.governance.governanceworkbench.choose.service", "Choose a service")}
          highlights={[
            {
              label: t("pages.governance.governanceworkbench.team", "team"),
              value: draft.tenantId || t("pages.governance.governanceworkbench.to.be.selected", "To be selected"),
            },
            {
              label: t("pages.governance.governanceworkbench.application", "application"),
              value: draft.appId || t("pages.governance.governanceworkbench.to.be.selected.2", "To be selected"),
            },
            {
              label: t("pages.governance.governanceworkbench.namespace", "namespace"),
              value: draft.namespace || t("pages.governance.governanceworkbench.to.be.selected.3", "To be selected"),
            },
          ]}
        />
      );
    }

    if (targetView === "overview") {
      const missingPolicyCount = activationQuery.data?.missingPolicyIds.length ?? 0;
      const serviceBindings = activeBindings.filter(
        (binding) => binding.bindingKind === "service",
      ).length;
      const connectorBindings = activeBindings.filter(
        (binding) => binding.bindingKind === "connector",
      ).length;
      const secretBindings = activeBindings.filter(
        (binding) => binding.bindingKind === "secret",
      ).length;

      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <GovernanceSummaryPanel
            draft={activeDraft}
            includeDefaultFields={false}
            extraFields={[
              {
                label: t("pages.governance.governanceworkbench.service.key", "Service Key"),
                value:
                  selectedService?.serviceKey?.trim()
                    ? buildGovernanceCompactValue(selectedService.serviceKey, {
                        head: 10,
                        tail: 10,
                      })
                    : t("pages.governance.governanceworkbench.to.be.selected.4", "To be selected"),
              },
              {
                label: t("pages.governance.governanceworkbench.recent.governance.snapshot", "Recent governance snapshot"),
                value: formatGovernanceTimestamp(latestGovernanceUpdatedAt),
              },
            ]}
            metrics={governanceMetrics.map((metric) => ({
              label: metric.label,
              tone:
                metric.tone === "warning"
                  ? "warning"
                  : metric.tone === "success"
                    ? "success"
                    : "default",
              value: metric.value,
            }))}
            revisionId={activationRevisionId || undefined}
            status={{
              color: missingPolicyCount > 0 ? "warning" : "success",
              label: missingPolicyCount > 0 ? t("pages.governance.governanceworkbench.there.is.activation.blocking", "There is activation blocking") : t("pages.governance.governanceworkbench.complete.governance.closed.loop", "Complete governance closed loop"),
            }}
            title={t("pages.governance.governanceworkbench.governance.overview", "Governance Overview")}
          />

          <div
            style={{
              display: "grid",
              gap: 16,
              gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
            }}
          >
            <GovernanceSelectionNotice
              title={t("pages.governance.governanceworkbench.entrance.exposed", "Entrance exposed")}
              highlights={[
                { label: t("pages.governance.governanceworkbench.public", "public"), value: publicEndpoints.length },
                { label: t("pages.governance.governanceworkbench.internal", "internal"), value: internalEndpoints.length },
                { label: t("pages.governance.governanceworkbench.deactivate", "deactivate"), value: disabledEndpoints.length },
                {
                  label: t("pages.governance.governanceworkbench.latest.updates", "Latest updates"),
                  value: formatGovernanceTimestamp(endpointsQuery.data?.updatedAt),
                },
              ]}
            />
            <GovernanceSelectionNotice
              title={t("pages.governance.governanceworkbench.policy.coverage", "Policy coverage")}
              highlights={[
                { label: t("pages.governance.governanceworkbench.active.strategies.2", "Active strategies"), value: activePolicies.length },
                {
                  label: t("pages.governance.governanceworkbench.requires.deployment.to.be.2", "Requires deployment to be activated"),
                  value: activePolicies.filter(
                    (policy) => policy.invokeRequiresActiveDeployment,
                  ).length,
                },
                {
                  label: t("pages.governance.governanceworkbench.missing.strategy", "missing strategy"),
                  value: missingPolicyCount,
                },
                {
                  label: t("pages.governance.governanceworkbench.latest.updates.2", "Latest updates"),
                  value: formatGovernanceTimestamp(policiesQuery.data?.updatedAt),
                },
              ]}
            />
            <GovernanceSelectionNotice
              title={t("pages.governance.governanceworkbench.binding.dependencies", "Binding dependencies")}
              highlights={[
                { label: "Service", value: serviceBindings },
                { label: "Connector", value: connectorBindings },
                { label: "Secret", value: secretBindings },
                {
                  label: t("pages.governance.governanceworkbench.latest.updates.3", "Latest updates"),
                  value: formatGovernanceTimestamp(bindingsQuery.data?.updatedAt),
                },
              ]}
            />
            <GovernanceSelectionNotice
              title={t("pages.governance.governanceworkbench.suggestions.for.next.steps", "Suggestions for next steps")}
              highlights={[
                {
                  label: t("pages.governance.governanceworkbench.current.version", "Current version"),
                  value: activationRevisionId
                    ? buildGovernanceCompactValue(activationRevisionId)
                    : t("pages.governance.governanceworkbench.to.be.selected.5", "To be selected"),
                },
                {
                  label: t("pages.governance.governanceworkbench.recommended.action", "Recommended action"),
                  value:
                    missingPolicyCount > 0
                      ? t("pages.governance.governanceworkbench.first.complete.the.missing", "First complete the missing policies, and then check whether the bindings are complete")
                      : publicEndpoints.length === 0
                        ? t("pages.governance.governanceworkbench.first.confirm.whether.the", "First confirm whether the entrance needs to be made public, and then check the endpoint exposure")
                        : t("pages.governance.governanceworkbench.enter.activation.diagnostics.and", "Enter activation diagnostics and confirm that the revision can be activated"),
                },
              ]}
            />
          </div>
        </div>
      );
    }

    if (targetView === "activation" && !activationRevisionId.trim()) {
      return (
        <GovernanceSelectionNotice
          title={t("pages.governance.governanceworkbench.choose.version", "Choose a version")}
        />
      );
    }

    if (targetView === "changes") {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.summary.of.changes", "Summary of changes")}
            highlights={[
              { label: t("pages.governance.governanceworkbench.number.of.events", "number of events"), value: auditEvents.length },
              {
                label: t("pages.governance.governanceworkbench.latest.updates.4", "Latest updates"),
                value: formatGovernanceTimestamp(latestGovernanceUpdatedAt),
              },
            ]}
          />
          <GovernanceAuditTimeline
            events={auditEvents}
            loading={
              bindingsQuery.isLoading ||
              policiesQuery.isLoading ||
              endpointsQuery.isLoading
            }
            onSelect={openAuditEvent}
          />
        </div>
      );
    }

    if (targetView === "policies") {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.policy.directory", "policy directory")}
            highlights={[
              { label: t("pages.governance.governanceworkbench.active.strategies.3", "Active strategies"), value: activePolicies.length },
              {
                label: t("pages.governance.governanceworkbench.retired", "Retired"),
                value: (policiesQuery.data?.policies ?? []).filter(
                  (policy) => policy.retired,
                ).length,
              },
              {
                label: t("pages.governance.governanceworkbench.requires.deployment.to.be.3", "Requires deployment to be activated"),
                value: activePolicies.filter(
                  (policy) => policy.invokeRequiresActiveDeployment,
                ).length,
              },
            ]}
          />
          <div style={stageTableShellStyle}>
            <Table<ServicePolicySnapshot>
              columns={policyTableColumns}
              dataSource={policiesQuery.data?.policies ?? []}
              locale={{
                emptyText: policiesQuery.isLoading
                  ? t("pages.governance.governanceworkbench.loading.strategies", "Loading strategies...")
                  : t("pages.governance.governanceworkbench.currently.the.service.does", "Currently the service does not have a governance policy."),
              }}
              pagination={{ pageSize: 8, showSizeChanger: false }}
              rowKey="policyId"
              size="middle"
            />
          </div>
        </div>
      );
    }

    if (targetView === "bindings") {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.bind.directory", "bind directory")}
            highlights={[
              {
                label: "Service",
                value: activeBindings.filter(
                  (binding) => binding.bindingKind === "service",
                ).length,
              },
              {
                label: "Connector",
                value: activeBindings.filter(
                  (binding) => binding.bindingKind === "connector",
                ).length,
              },
              {
                label: "Secret",
                value: activeBindings.filter(
                  (binding) => binding.bindingKind === "secret",
                ).length,
              },
            ]}
          />
          <div style={stageTableShellStyle}>
            <Table<ServiceBindingSnapshot>
              columns={bindingTableColumns}
              dataSource={bindingsQuery.data?.bindings ?? []}
              locale={{
                emptyText: bindingsQuery.isLoading
                  ? t("pages.governance.governanceworkbench.loading.bindings", "Loading bindings...")
                  : t("pages.governance.governanceworkbench.the.current.service.has", "The current service has no bound dependencies."),
              }}
              pagination={{ pageSize: 8, showSizeChanger: false }}
              rowKey="bindingId"
              size="middle"
            />
          </div>
        </div>
      );
    }

    if (targetView === "endpoints") {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.entry.directory", "Entry directory")}
            highlights={[
              { label: t("pages.governance.governanceworkbench.public.2", "public"), value: publicEndpoints.length },
              { label: t("pages.governance.governanceworkbench.internal.2", "internal"), value: internalEndpoints.length },
              { label: t("pages.governance.governanceworkbench.deactivate.2", "deactivate"), value: disabledEndpoints.length },
            ]}
          />
          <div style={stageTableShellStyle}>
            <Table<ServiceEndpointExposureSnapshot>
              columns={endpointTableColumns}
              dataSource={endpointsQuery.data?.endpoints ?? []}
              locale={{
                emptyText: endpointsQuery.isLoading
                  ? t("pages.governance.governanceworkbench.loading.entry.directory", "Loading entry directory...")
                  : t("pages.governance.governanceworkbench.there.is.currently.no", "There is currently no entry directory for the service."),
              }}
              pagination={{ pageSize: 8, showSizeChanger: false }}
              rowKey="endpointId"
              size="middle"
            />
          </div>
        </div>
      );
    }

    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <GovernanceSummaryPanel
          draft={activeDraft}
          includeDefaultFields={false}
          metrics={[
            {
              label: t("pages.governance.governanceworkbench.missing.strategy.2", "missing strategy"),
              tone:
                (activationQuery.data?.missingPolicyIds.length ?? 0) > 0
                  ? "warning"
                  : "success",
              value: String(activationQuery.data?.missingPolicyIds.length ?? 0),
            },
            {
              label: t("pages.governance.governanceworkbench.visible.binding", "visible binding"),
              value: String((activationQuery.data?.bindings ?? []).length),
            },
            {
              label: t("pages.governance.governanceworkbench.visible.entrance", "visible entrance"),
              value: String((activationQuery.data?.endpoints ?? []).length),
            },
            {
              label: t("pages.governance.governanceworkbench.visible.strategy", "visible strategy"),
              value: String((activationQuery.data?.policies ?? []).length),
            },
          ]}
          revisionId={activationRevisionId}
          status={{
            color:
              (activationQuery.data?.missingPolicyIds.length ?? 0) > 0
                ? "warning"
                : "success",
            label:
              (activationQuery.data?.missingPolicyIds.length ?? 0) > 0
                ? t("pages.governance.governanceworkbench.there.is.activation.blocking.2", "There is activation blocking")
                : t("pages.governance.governanceworkbench.can.enter.activation", "Can enter activation"),
          }}
          title={t("pages.governance.governanceworkbench.activate.diagnostics", "Activate diagnostics")}
        />

        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))",
          }}
        >
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.missing.strategy.3", "missing strategy")}
            highlights={
              (activationQuery.data?.missingPolicyIds ?? []).length > 0
                ? activationQuery.data?.missingPolicyIds.map((policyId) => ({
                    key: policyId,
                    label: buildGovernanceCompactValue(policyId),
                    value: t("pages.governance.governanceworkbench.missing", "Missing"),
                  })) ?? []
                : [{ label: t("pages.governance.governanceworkbench.state.3", "state"), value: t("pages.governance.governanceworkbench.no.missing.strategy", "no missing strategy") }]
            }
          />

          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.scope.binding", "scope binding")}
            highlights={
              (activationQuery.data?.bindings ?? []).length > 0
                ? (activationQuery.data?.bindings ?? []).slice(0, 4).map((binding) => ({
                    key: binding.bindingId,
                    label: buildGovernanceCompactValue(binding.bindingId),
                    value: `${binding.displayName || binding.bindingId} · ${formatAevatarStatusLabel(binding.bindingKind)}`,
                  }))
                : [{ label: t("pages.governance.governanceworkbench.state.4", "state"), value: t("pages.governance.governanceworkbench.there.are.currently.no", "There are currently no visible bindings") }]
            }
          />

          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.current.entrance.coverage", "Current entrance coverage")}
            highlights={
              (activationQuery.data?.endpoints ?? []).length > 0
                ? (activationQuery.data?.endpoints ?? []).slice(0, 4).map((endpoint) => ({
                    key: endpoint.endpointId,
                    label: buildGovernanceCompactValue(endpoint.endpointId),
                    value: `${endpoint.displayName || endpoint.endpointId} · ${formatAevatarStatusLabel(endpoint.exposureKind)}`,
                  }))
                : [{ label: t("pages.governance.governanceworkbench.state.5", "state"), value: t("pages.governance.governanceworkbench.there.is.currently.no.2", "There is currently no visible entrance") }]
            }
          />
        </div>
      </div>
    );
  }, [
    activeBindings,
    activeDraft,
    activePolicies,
    activationQuery.data,
    activationRevisionId,
    auditEvents,
    bindingsQuery.data,
    bindingsQuery.isLoading,
    disabledEndpoints,
    endpointsQuery.data,
    endpointsQuery.isLoading,
    governanceMetrics,
    hasSelectedServiceContext,
    internalEndpoints,
    latestGovernanceUpdatedAt,
    openAuditEvent,
    policyTableColumns,
    policiesQuery.data,
    policiesQuery.isLoading,
    publicEndpoints,
    selectedService?.serviceKey,
    bindingTableColumns,
    endpointTableColumns,
    stageTableShellStyle,
    surfaceToken,
    draft,
  ]);

  return (
    <ConsoleMenuPageShell
      breadcrumb={t("pages.governance.governanceworkbench.aevatar.platform", "Aevatar / Platform")}
      title={t("pages.governance.governanceworkbench.governance", "Governance")}
    >
      <div style={buildAevatarViewportStyle(surfaceToken)}>
        {notice ? (
          <Alert
            closable
            message={notice.message}
            showIcon
            type={notice.tone}
            onClose={() => setNotice(null)}
          />
        ) : null}

        <GovernanceQueryCard
          draft={draft}
          includeRevision={view === "activation"}
          loadLabel={
            view === "activation" ? t("pages.governance.governanceworkbench.load.activation.diagnostics", "Load activation diagnostics") : t("pages.governance.governanceworkbench.load.management.workbench", "Load management workbench")
          }
          onChange={setDraft}
          onLoad={() => {
            const nextActiveDraft = normalizeGovernanceDraft(draft);
            setDraft(nextActiveDraft);
            setActiveDraft(nextActiveDraft);
            navigateToGovernanceView(view, nextActiveDraft);
          }}
          onReset={() => {
            const nextDraft = resolvedScope?.scopeId?.trim()
              ? {
                  ...readGovernanceDraft(""),
                  appId: defaultScopeServiceAppId,
                  namespace: defaultScopeServiceNamespace,
                  tenantId: resolvedScope.scopeId.trim(),
                }
              : readGovernanceDraft("");
            setDraft(nextDraft);
            setActiveDraft(nextDraft);
            navigateToGovernanceView(view, nextDraft);
          }}
          revisionOptions={revisionOptions}
          revisionOptionsLoading={revisionsQuery.isLoading}
          serviceOptions={serviceOptions}
          serviceSearchEnabled={serviceSearchEnabled}
        />

        <div
          style={{
            display: "flex",
            flexDirection: "column",
            minWidth: 0,
          }}
        >
          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorBgContainer,
                minHeight: 640,
              }),
              borderRadius: 18,
              boxShadow: "0 18px 40px rgba(15, 23, 42, 0.06)",
              display: "flex",
              flexDirection: "column",
            }}
          >
            {(() => {
              const activeAction = governanceViewActions[view];

              return (
                <>
                  <div
                    style={{
                      background:
                        "linear-gradient(180deg, rgba(24, 144, 255, 0.06) 0%, rgba(255, 255, 255, 0.98) 100%)",
                      borderBottom: `1px solid ${surfaceToken.colorBorderSecondary}`,
                      display: "flex",
                      flexDirection: "column",
                      gap: 14,
                      padding: "18px 20px 0",
                    }}
                  >
                    <div
                      style={{
                        alignItems: "stretch",
                        columnGap: 12,
                        display: "grid",
                        gridTemplateColumns: "minmax(0, 1fr) auto",
                        minHeight: 80,
                      }}
                    >
                      <Space orientation="vertical" size={2} style={{ minWidth: 0 }}>
                        <Typography.Text
                          style={{
                            color: surfaceToken.colorPrimary,
                            fontSize: 12,
                            fontWeight: 700,
                            letterSpacing: "0.08em",
                            textTransform: "uppercase",
                          }}
                        >
                          {t("pages.governance.governanceworkbench.governance.workspace", "Governance workspace")}</Typography.Text>
                        <Typography.Text
                          strong
                          style={{ color: surfaceToken.colorTextHeading, fontSize: 20 }}
                        >
                          {t(
                            governanceViewMeta[view].title.id,
                            governanceViewMeta[view].title.defaultMessage,
                          )}
                        </Typography.Text>
                        {governanceViewMeta[view].description ? (
                          <Typography.Text
                            type="secondary"
                            style={{ fontSize: 14, lineHeight: 1.65 }}
                          >
                            {governanceViewMeta[view].description}
                          </Typography.Text>
                        ) : null}
                      </Space>
                      <div
                        style={{
                          alignItems: "flex-start",
                          display: "flex",
                          justifyContent: "flex-end",
                          minHeight: 32,
                          minWidth: 172,
                        }}
                      >
                        {activeAction ? (
                          <Button
                            icon={activeAction.icon}
                            onClick={activeAction.onClick}
                            type={activeAction.type}
                          >
                            {activeAction.label}
                          </Button>
                        ) : null}
                      </div>
                    </div>
                    <Tabs
                      activeKey={view}
                      items={governanceTabItems}
                      style={{ marginBottom: -1 }}
                      onChange={(nextView) =>
                        navigateToGovernanceView(
                          nextView as GovernanceWorkbenchView,
                        )
                      }
                    />
                  </div>

                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                    }}
                  >
                    <div
                      style={{
                        padding: "20px 20px 22px",
                      }}
                    >
                      {renderStageForView(view)}
                    </div>
                  </div>
                </>
              );
            })()}
          </div>
        </div>

        <GovernanceInspectorDrawer
          busyAction={busyAction}
          endpointCatalog={endpointsQuery.data ?? null}
          identity={activeIdentity}
          onClose={() => setDrawerTarget(null)}
          onCreateBinding={handleCreateBinding}
          onCreateEndpoint={handleCreateEndpoint}
          onCreatePolicy={handleCreatePolicy}
          onRetireBinding={handleRetireBinding}
          onRetirePolicy={handleRetirePolicy}
          onSetEndpointExposure={handleSetEndpointExposure}
          onUpdateEndpoint={handleUpdateEndpoint}
          onUpdateBinding={handleUpdateBinding}
          onUpdatePolicy={handleUpdatePolicy}
          open={Boolean(drawerTarget)}
          policyOptions={activePolicies.map((policy) => policy.policyId)}
          serviceId={activeDraft.serviceId}
          target={drawerTarget}
        />
      </div>
    </ConsoleMenuPageShell>
  );
};

export default GovernanceWorkbench;
