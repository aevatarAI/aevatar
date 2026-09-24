import {
  DeploymentUnitOutlined,
  PlusOutlined,
} from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
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
import { buildPlatformDeploymentsHref } from "@/shared/navigation/platformRoutes";
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
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  buildAevatarViewportStyle,
  formatAevatarStatusLabel,
  resolveAevatarSemanticTone,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import { AevatarCompactText } from "@/shared/ui/compactText";
import AevatarContentSkeleton from "@/shared/ui/AevatarContentSkeleton";
import type { AevatarBreadcrumbItem } from "@/shared/ui/aevatarPageShells";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import ConsoleOperationNotice from "@/shared/ui/ConsoleOperationNotice";
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
import {
  buildGovernanceCommandReceipt,
  observeGovernanceReceipt,
  type GovernanceCatalogKind,
  type GovernanceCommandReceipt,
} from "./governanceCommandReceipt";
import {
  formatConsoleMessage,
  t,
  type ConsoleMessageDescriptor,
} from "@/shared/i18n/messages";
import { getUserFacingIdentifierLabel } from "@/shared/ui/userFacingIdentifiers";

type GovernanceNotice = {
  message: string;
  tone: "error" | "info" | "success" | "warning";
};

type GovernanceViewMeta = {
  description?: ConsoleMessageDescriptor;
  title: ConsoleMessageDescriptor;
};

const defaultScopeServiceAppId = "default";
const defaultScopeServiceNamespace = "default";

function formatGovernanceVersionLabel(value: string | null | undefined): string {
  return value?.trim()
    ? t("pages.governance.governanceworkbench.version.ready", "Version ready")
    : t("pages.governance.governanceworkbench.version.pending", "Version pending");
}

function formatGovernanceResourceLabel(
  value: string | null | undefined,
  fallback: string,
): string {
  return getUserFacingIdentifierLabel(value, fallback);
}

type GovernanceViewActionConfig = {
  label: string;
  icon?: React.ReactNode;
  onClick: () => void;
  type?: "default" | "primary";
};

const governanceViewMeta: Record<GovernanceWorkbenchView, GovernanceViewMeta> = {
  overview: {
    title: {
      defaultMessage: "Overview",
      id: "pages.governance.governanceworkbench.views.overview",
    },
  },
  activation: {
    title: {
      defaultMessage: "Activation diagnostics",
      id: "pages.governance.governanceworkbench.views.activation",
    },
  },
  bindings: {
    title: {
      defaultMessage: "Bindings",
      id: "pages.governance.governanceworkbench.views.bindings",
    },
  },
  changes: {
    title: {
      defaultMessage: "Change summary",
      id: "pages.governance.governanceworkbench.views.changes",
    },
  },
  endpoints: {
    title: {
      defaultMessage: "Endpoints",
      id: "pages.governance.governanceworkbench.views.endpoints",
    },
  },
  policies: {
    title: {
      defaultMessage: "Policies",
      id: "pages.governance.governanceworkbench.views.policies",
    },
  },
};
const platformBreadcrumbItems: AevatarBreadcrumbItem[] = [
  {
    title: "Platform",
  },
  {
    current: true,
    title: "Governance",
  },
];

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
    return record.serviceRef.endpointId
      ? t("pages.governance.governanceworkbench.service.endpoint.target", "Service endpoint target")
      : t("pages.governance.governanceworkbench.service.target", "Service target");
  }

  if (record.connectorRef) {
    return formatAevatarStatusLabel(record.connectorRef.connectorType || "connector");
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
      action: t("pages.governance.governanceworkbench.governance.scope.attached.2", "Governance scope attached"),
      actor: t("pages.governance.governanceworkbench.service.registry.2", "Service Registry"),
      at: selectedService.updatedAt,
      id: `service-${selectedService.serviceId}-${selectedService.updatedAt}`,
      status: selectedService.deploymentStatus || "active",
      summary: t("pages.governance.governanceworkbench.governance.is.now.anchored.to", "Governance is now anchored to {value1}.", {
        value1: formatGovernanceResourceLabel(
          selectedService.displayName || selectedService.serviceId,
          t("pages.governance.governanceworkbench.service", "Service"),
        ),
      }),
      targetId: selectedService.serviceId,
      targetKind: "service",
      targetLabel: formatGovernanceResourceLabel(
        selectedService.displayName || selectedService.serviceId,
        t("pages.governance.governanceworkbench.service", "Service"),
      ),
    });
  }

  for (const revision of revisions) {
    if (revision.publishedAt) {
      events.push({
        action: t("pages.governance.governanceworkbench.revision.published.2", "Revision published"),
        actor: t("pages.governance.governanceworkbench.release.manager.2", "Release Manager"),
        at: revision.publishedAt,
        id: `revision-published-${revision.revisionId}`,
        status: "published",
        summary: t("pages.governance.governanceworkbench.revision.was.published.for.governance.evaluation", "Revision was published for governance evaluation."),
        targetId: revision.revisionId,
        targetKind: "activation",
        targetLabel: formatGovernanceVersionLabel(revision.revisionId),
      });
    } else if (revision.preparedAt) {
      events.push({
        action: t("pages.governance.governanceworkbench.revision.prepared.2", "Revision prepared"),
        actor: t("pages.governance.governanceworkbench.release.manager.3", "Release Manager"),
        at: revision.preparedAt,
        id: `revision-prepared-${revision.revisionId}`,
        status: revision.status || "pending",
        summary: t("pages.governance.governanceworkbench.revision.is.prepared.and.waiting.for", "Revision is prepared and waiting for promotion decisions."),
        targetId: revision.revisionId,
        targetKind: "activation",
        targetLabel: formatGovernanceVersionLabel(revision.revisionId),
      });
    }
  }

  if (bindingsUpdatedAt) {
    events.push({
      action: t("pages.governance.governanceworkbench.binding.catalog.synchronized.2", "Binding catalog synchronized"),
      actor: t("pages.governance.governanceworkbench.binding.registry.2", "Binding Registry"),
      at: bindingsUpdatedAt,
      id: `binding-catalog-${bindingsUpdatedAt}`,
      status: bindings.some((binding) => binding.retired) ? "retired" : "active",
      summary: t("pages.governance.governanceworkbench.binding.are.currently.tracked.for.this", "{value1} binding{value2} are currently tracked for this service.", { value1: bindings.length, value2: bindings.length === 1 ? "" : "s" }),
      targetId: "binding-catalog",
      targetKind: "binding",
      targetLabel: `${bindings.length} bindings`,
    });
  }

  for (const binding of bindings.filter((item) => item.retired)) {
    events.push({
      action: t("pages.governance.governanceworkbench.binding.retired.2", "Binding retired"),
      actor: t("pages.governance.governanceworkbench.binding.registry.3", "Binding Registry"),
      at: bindingsUpdatedAt || selectedService?.updatedAt || "",
      id: `binding-retired-${binding.bindingId}`,
      status: "retired",
      summary: t("pages.governance.governanceworkbench.was.removed.from.the.active.dependency", "{value1} was removed from the active dependency surface.", {
        value1: formatGovernanceResourceLabel(
          binding.displayName || binding.bindingId,
          t("pages.governance.governanceworkbench.binding", "Binding"),
        ),
      }),
      targetId: binding.bindingId,
      targetKind: "binding",
      targetLabel: formatGovernanceResourceLabel(
        binding.displayName || binding.bindingId,
        t("pages.governance.governanceworkbench.binding", "Binding"),
      ),
    });
  }

  if (policiesUpdatedAt) {
    events.push({
      action: t("pages.governance.governanceworkbench.policy.catalog.synchronized.2", "Policy catalog synchronized"),
      actor: t("pages.governance.governanceworkbench.policy.engine.2", "Policy Engine"),
      at: policiesUpdatedAt,
      id: `policy-catalog-${policiesUpdatedAt}`,
      status: policies.some((policy) => policy.retired) ? "retired" : "active",
      summary: t("pages.governance.governanceworkbench.governance.polic.are.materialized.for.this", "{value1} governance polic{value2} are materialized for this service.", { value1: policies.length, value2: policies.length === 1 ? "y" : "ies" }),
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
      actor: t("pages.governance.governanceworkbench.policy.engine.3", "Policy Engine"),
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
      action: t("pages.governance.governanceworkbench.endpoint.catalog.synchronized.2", "Endpoint catalog synchronized"),
      actor: t("pages.governance.governanceworkbench.exposure.controller.2", "Exposure Controller"),
      at: endpointsUpdatedAt,
      id: `endpoint-catalog-${endpointsUpdatedAt}`,
      status: endpoints.some((endpoint) => endpoint.exposureKind === "disabled")
        ? "disabled"
        : "active",
      summary: t("pages.governance.governanceworkbench.endpoint.are.under.governance.exposure.control", "{value1} endpoint{value2} are under governance exposure control.", { value1: endpoints.length, value2: endpoints.length === 1 ? "" : "s" }),
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
      actor: t("pages.governance.governanceworkbench.exposure.controller.3", "Exposure Controller"),
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
      actor: t("pages.governance.governanceworkbench.activation.guard.2", "Activation Guard"),
      at: selectedService?.updatedAt || policiesUpdatedAt || endpointsUpdatedAt || "",
      id: `activation-${activationView.revisionId || "unresolved"}`,
      status:
        activationView.missingPolicyIds.length > 0 ? "blocked" : "ready",
      summary:
        activationView.missingPolicyIds.length > 0
          ? t(
              "pages.governance.governanceworkbench.revision.missing.policies",
              "Revision is missing {value1} required policies.",
              { value1: activationView.missingPolicyIds.length },
            )
          : t(
              "pages.governance.governanceworkbench.revision.complete.envelope",
              "Revision has a complete governance envelope.",
            ),
      targetId: activationView.revisionId || "activation",
      targetKind: "activation",
      targetLabel: activationView.revisionId
        ? formatGovernanceVersionLabel(activationView.revisionId)
        : "Activation view",
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
  const [commandReceipt, setCommandReceipt] =
    useState<GovernanceCommandReceipt | null>(null);
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
        label: t("pages.governance.governanceworkbench.copy.110", "{value1} · {value2}", {
          value1: formatGovernanceVersionLabel(revision.revisionId),
          value2: revision.status,
        }),
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
  const commandReceiptObservation = useMemo(() => {
    if (!commandReceipt) {
      return null;
    }

    if (commandReceipt.catalogKind === "bindings") {
      return observeGovernanceReceipt(commandReceipt, bindingsQuery.data);
    }

    if (commandReceipt.catalogKind === "endpoints") {
      return observeGovernanceReceipt(commandReceipt, endpointsQuery.data);
    }

    return observeGovernanceReceipt(commandReceipt, policiesQuery.data);
  }, [
    bindingsQuery.data,
    commandReceipt,
    endpointsQuery.data,
    policiesQuery.data,
  ]);

  const governanceMetrics = useMemo(
    () => [
      {
        label: t("pages.governance.governanceworkbench.copy", "Active policies"),
        tone:
          activePolicies.length > 0
            ? ("default" as const)
            : ("warning" as const),
        value: String(activePolicies.length),
      },
      {
        label: t("pages.governance.governanceworkbench.copy.2", "Active bindings"),
        tone:
          activeBindings.length > 0
            ? ("default" as const)
            : ("warning" as const),
        value: String(activeBindings.length),
      },
      {
        label: t("pages.governance.governanceworkbench.copy.3", "Public endpoints"),
        tone: "success" as const,
        value: String(publicEndpoints.length),
      },
      {
        label: t("pages.governance.governanceworkbench.copy.4", "Activation blockers"),
        tone:
          (activationQuery.data?.missingPolicyIds.length ?? 0) > 0
            ? ("warning" as const)
            : ("success" as const),
        value: String(activationQuery.data?.missingPolicyIds.length ?? 0),
      },
    ],
    [activationQuery.data, activeBindings.length, activePolicies.length, publicEndpoints.length],
  );

  const governanceTabItems = Object.entries(governanceViewMeta).map(([key, meta]) => ({
    key,
    label: formatConsoleMessage(meta.title),
  }));

  const navigateToGovernanceView = useCallback(
    (
      nextView: GovernanceWorkbenchView,
      nextDraft: GovernanceDraft = activeDraft,
    ) => {
      history.replace(buildGovernanceWorkbenchHref(nextDraft, nextView));
    },
    [activeDraft],
  );

  const openDeploymentsHandoff = useCallback(() => {
    history.push(
      buildPlatformDeploymentsHref({
        appId: activeDraft.appId,
        deploymentId: selectedService?.deploymentId || undefined,
        namespace: activeDraft.namespace,
        serviceId: activeDraft.serviceId,
        tenantId: activeDraft.tenantId,
      }),
    );
  }, [activeDraft, selectedService?.deploymentId]);

  const releaseHandoffAction = useMemo(
    () =>
      hasSelectedServiceContext ? (
        <Button icon={<DeploymentUnitOutlined />} onClick={openDeploymentsHandoff}>
          {t("pages.governance.governanceworkbench.deployments", "Open Deployments")}</Button>
      ) : null,
    [hasSelectedServiceContext, openDeploymentsHandoff],
  );

  const headerReleaseHandoffAction =
    view === "overview" || view === "activation" ? null : releaseHandoffAction;

  const governanceViewActions = useMemo<
    Partial<Record<GovernanceWorkbenchView, GovernanceViewActionConfig>>
  >(
    () => ({
      overview: hasSelectedServiceContext
        ? {
            icon: <DeploymentUnitOutlined />,
            label: t("pages.governance.governanceworkbench.copy.5", "Check activation"),
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
              label: t("pages.governance.governanceworkbench.copy.6", "Open diagnostics"),
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
            label: t("pages.governance.governanceworkbench.copy.7", "New policy"),
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
            label: t("pages.governance.governanceworkbench.copy.8", "New binding"),
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
            label: t("pages.governance.governanceworkbench.copy.9", "New endpoint"),
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

  const policyTableColumns: ColumnsType<ServicePolicySnapshot> = [
      {
        key: "policy",
        title: t("pages.governance.governanceworkbench.copy.10", "Policy"),
        render: (_, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>
              {formatGovernanceResourceLabel(
                record.displayName || record.policyId,
                t("pages.governance.governanceworkbench.policy", "Policy"),
              )}
            </Typography.Text>
          </Space>
        ),
      },
      {
        key: "bindings",
        title: t("pages.governance.governanceworkbench.copy.11", "Activate dependencies"),
        render: (_, record) =>
          record.activationRequiredBindingIds.length > 0
            ? t("pages.governance.governanceworkbench.copy.12", "{value1} bindings", { value1: record.activationRequiredBindingIds.length })
            : t("pages.governance.governanceworkbench.copy.13", "No pre-binding"),
      },
      {
        key: "callers",
        title: t("pages.governance.governanceworkbench.copy.14", "call limit"),
        render: (_, record) =>
          record.invokeAllowedCallerServiceKeys.length > 0
            ? t("pages.governance.governanceworkbench.allowlist", "{value1} allowlist entries", { value1: record.invokeAllowedCallerServiceKeys.length })
            : t("pages.governance.governanceworkbench.caller", "Caller unrestricted"),
      },
      {
        key: "status",
        title: t("pages.governance.governanceworkbench.copy.15", "Status"),
        width: 220,
        render: (_, record) => (
          <Space wrap size={[8, 8]}>
            <WorkbenchStatusTag status={record.retired ? "retired" : "active"} />
            {record.invokeRequiresActiveDeployment ? (
              <Tag color="gold">{t("pages.governance.governanceworkbench.copy.16", "Requires active deployment")}</Tag>
            ) : null}
          </Space>
        ),
      },
      {
        key: "actions",
        title: t("pages.governance.governanceworkbench.copy.17", "Actions"),
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
            {record.retired ? t("pages.governance.governanceworkbench.copy.18", "View") : t("pages.governance.governanceworkbench.copy.19", "Configure")}
          </Button>
        ),
      },
  ];

  const bindingTableColumns: ColumnsType<ServiceBindingSnapshot> = [
      {
        key: "binding",
        title: t("pages.governance.governanceworkbench.copy.20", "Binding"),
        render: (_, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>
              {formatGovernanceResourceLabel(
                record.displayName || record.bindingId,
                t("pages.governance.governanceworkbench.binding", "Binding"),
              )}
            </Typography.Text>
          </Space>
        ),
      },
      {
        dataIndex: "bindingKind",
        key: "bindingKind",
        title: t("pages.governance.governanceworkbench.copy.21", "Type"),
        width: 120,
        render: (_, record) => formatAevatarStatusLabel(record.bindingKind),
      },
      {
        key: "target",
        title: t("pages.governance.governanceworkbench.copy.22", "Target"),
        render: (_, record) => buildBindingTargetLabel(record),
      },
      {
        key: "policies",
        title: t("pages.governance.governanceworkbench.copy.23", "Mount strategy"),
        render: (_, record) =>
          record.policyIds.length > 0
            ? t("pages.governance.governanceworkbench.copy.24", "{value1} items", { value1: record.policyIds.length })
            : t("pages.governance.governanceworkbench.copy.25", "Unlisted strategy"),
      },
      {
        key: "status",
        title: t("pages.governance.governanceworkbench.copy.26", "Status"),
        width: 120,
        render: (_, record) => (
          <WorkbenchStatusTag status={record.retired ? "retired" : "active"} />
        ),
      },
      {
        key: "actions",
        title: t("pages.governance.governanceworkbench.copy.27", "Actions"),
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
            {record.retired ? t("pages.governance.governanceworkbench.copy.28", "View") : t("pages.governance.governanceworkbench.copy.29", "Configure")}
          </Button>
        ),
      },
  ];

  const endpointTableColumns: ColumnsType<ServiceEndpointExposureSnapshot> = [
      {
        key: "endpoint",
        title: t("pages.governance.governanceworkbench.copy.30", "Endpoint"),
        render: (_, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>
              {formatGovernanceResourceLabel(
                record.displayName || record.endpointId,
                t("pages.governance.governanceworkbench.endpoint", "Endpoint"),
              )}
            </Typography.Text>
          </Space>
        ),
      },
      {
        dataIndex: "kind",
        key: "kind",
        title: t("pages.governance.governanceworkbench.copy.31", "Type"),
        width: 120,
        render: (_, record) => formatAevatarStatusLabel(record.kind),
      },
      {
        dataIndex: "exposureKind",
        key: "exposureKind",
        title: t("pages.governance.governanceworkbench.copy.32", "Exposure status"),
        width: 140,
        render: (_, record) => (
          <WorkbenchStatusTag status={record.exposureKind || "internal"} />
        ),
      },
      {
        key: "policies",
        title: t("pages.governance.governanceworkbench.copy.33", "Mount strategy"),
        render: (_, record) =>
          record.policyIds.length > 0
            ? t("pages.governance.governanceworkbench.copy.34", "{value1} items", { value1: record.policyIds.length })
            : t("pages.governance.governanceworkbench.copy.35", "Unlisted strategy"),
      },
      {
        key: "requestTypeUrl",
        title: t("pages.governance.governanceworkbench.copy.36", "Request contract"),
        render: (_, record) =>
          record.requestTypeUrl ? (
            <AevatarCompactText
              maxChars={28}
              mode="tail"
              monospace
              value={record.requestTypeUrl}
            />
          ) : (
            t("pages.governance.governanceworkbench.copy.37", "Not declared")
          ),
      },
      {
        key: "actions",
        title: t("pages.governance.governanceworkbench.copy.38", "Actions"),
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
            {t("pages.governance.governanceworkbench.copy.39", "Configure")}</Button>
        ),
      },
  ];

  const invalidateGovernanceQueries = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["governance"] }),
      queryClient.invalidateQueries({ queryKey: ["services"] }),
    ]);
  }, [queryClient]);

  const runGovernanceAction = useCallback(
    async (
      action: string,
      catalogKind: GovernanceCatalogKind,
      targetId: string,
      successMessage: string,
      task: () => Promise<unknown>,
      closeDrawer = false,
    ) => {
      setBusyAction(action);
      try {
        await task();
        setCommandReceipt(
          buildGovernanceCommandReceipt({
            catalogKind,
            commandLabel: successMessage,
            targetId,
          }),
        );
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
        "policies",
        input.policyId,
        t(
          "pages.governance.governanceworkbench.policy.accepted.for.creation",
          "Policy was accepted for governance creation.",
        ),
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
        "bindings",
        input.bindingId,
        t(
          "pages.governance.governanceworkbench.binding.accepted.for.creation",
          "Binding was accepted for governance creation.",
        ),
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
        "bindings",
        bindingId,
        t(
          "pages.governance.governanceworkbench.binding.accepted.for.update",
          "Binding was accepted for update.",
        ),
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
        "policies",
        policyId,
        t(
          "pages.governance.governanceworkbench.policy.accepted.for.update",
          "Policy was accepted for update.",
        ),
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
        "policies",
        policyId,
        t(
          "pages.governance.governanceworkbench.policy.accepted.for.retirement",
          "Policy was accepted for retirement.",
        ),
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
        "bindings",
        bindingId,
        t(
          "pages.governance.governanceworkbench.binding.accepted.for.retirement",
          "Binding was accepted for retirement.",
        ),
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
        "endpoints",
        endpointId,
        t(
          "pages.governance.governanceworkbench.endpoint.accepted.for.exposure",
          "Endpoint was accepted for {value1} exposure.",
          { value1: formatAevatarStatusLabel(exposureKind).toLowerCase() },
        ),
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
        "endpoints",
        input.endpointId,
        t(
          "pages.governance.governanceworkbench.endpoint.accepted.for.creation",
          "Endpoint was accepted for governance creation.",
        ),
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
        "endpoints",
        endpointId,
        t(
          "pages.governance.governanceworkbench.endpoint.accepted.for.update",
          "Endpoint was accepted for update.",
        ),
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
          title={t("pages.governance.governanceworkbench.copy.40", "Select a service")}
          highlights={[
            {
              label: t("pages.governance.governanceworkbench.copy.41", "Team"),
              value: draft.tenantId || t("pages.governance.governanceworkbench.copy.42", "Pending selection"),
            },
            {
              label: t("pages.governance.governanceworkbench.copy.43", "App"),
              value: draft.appId || t("pages.governance.governanceworkbench.copy.44", "Pending selection"),
            },
            {
              label: t("pages.governance.governanceworkbench.copy.45", "Namespace"),
              value: draft.namespace || t("pages.governance.governanceworkbench.copy.46", "Pending selection"),
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
            actions={releaseHandoffAction}
            draft={activeDraft}
            includeDefaultFields={false}
            extraFields={[
              {
                label: t("pages.governance.governanceworkbench.key", "Service Key"),
                value:
                  selectedService?.serviceKey?.trim()
                    ? buildGovernanceCompactValue(selectedService.serviceKey, {
                        head: 10,
                        tail: 10,
                      })
                    : t("pages.governance.governanceworkbench.copy.47", "Pending selection"),
              },
              {
                label: t("pages.governance.governanceworkbench.copy.48", "Latest governance snapshot"),
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
              label: missingPolicyCount > 0 ? t("pages.governance.governanceworkbench.copy.49", "Activation blockers present") : t("pages.governance.governanceworkbench.copy.50", "Governance loop complete"),
            }}
            title={t("pages.governance.governanceworkbench.copy.51", "Governance overview")}
          />

          <div
            style={{
              display: "grid",
              gap: 16,
              gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
            }}
          >
            <GovernanceSelectionNotice
              title={t("pages.governance.governanceworkbench.copy.52", "Endpoint exposure")}
              highlights={[
                { label: t("pages.governance.governanceworkbench.copy.53", "Public"), value: publicEndpoints.length },
                { label: t("pages.governance.governanceworkbench.copy.54", "Internal"), value: internalEndpoints.length },
                { label: t("pages.governance.governanceworkbench.copy.55", "Deactivate"), value: disabledEndpoints.length },
                {
                  label: t("pages.governance.governanceworkbench.copy.56", "Latest update"),
                  value: formatGovernanceTimestamp(endpointsQuery.data?.updatedAt),
                },
              ]}
            />
            <GovernanceSelectionNotice
              title={t("pages.governance.governanceworkbench.copy.57", "Policy coverage")}
              highlights={[
                { label: t("pages.governance.governanceworkbench.copy.58", "Active policies"), value: activePolicies.length },
                {
                  label: t("pages.governance.governanceworkbench.copy.59", "Requires active deployment"),
                  value: activePolicies.filter(
                    (policy) => policy.invokeRequiresActiveDeployment,
                  ).length,
                },
                {
                  label: t("pages.governance.governanceworkbench.copy.60", "Missing policies"),
                  value: missingPolicyCount,
                },
                {
                  label: t("pages.governance.governanceworkbench.copy.61", "Latest update"),
                  value: formatGovernanceTimestamp(policiesQuery.data?.updatedAt),
                },
              ]}
            />
            <GovernanceSelectionNotice
              title={t("pages.governance.governanceworkbench.copy.62", "Binding dependencies")}
              highlights={[
                { label: "Service", value: serviceBindings },
                { label: "Connector", value: connectorBindings },
                { label: "Secret", value: secretBindings },
                {
                  label: t("pages.governance.governanceworkbench.copy.63", "Latest update"),
                  value: formatGovernanceTimestamp(bindingsQuery.data?.updatedAt),
                },
              ]}
            />
            <GovernanceSelectionNotice
              actions={releaseHandoffAction}
              title={t("pages.governance.governanceworkbench.copy.64", "Recommended next step")}
              highlights={[
                {
                  label: t("pages.governance.governanceworkbench.copy.65", "Current version"),
                  value: activationRevisionId
                    ? buildGovernanceCompactValue(activationRevisionId)
                    : t("pages.governance.governanceworkbench.copy.66", "Pending selection"),
                },
                {
                  label: t("pages.governance.governanceworkbench.copy.67", "Recommended action"),
                  value:
                    missingPolicyCount > 0
                      ? t("pages.governance.governanceworkbench.copy.68", "Add the missing policies first, then check whether bindings are complete")
                      : publicEndpoints.length === 0
                        ? t("pages.governance.governanceworkbench.endpoint", "First confirm whether the entrance needs to be made public, and then check the endpoint exposure")
                        : t("pages.governance.governanceworkbench.revision", "Enter activation diagnostics and confirm that the revision can be activated"),
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
          title={t("pages.governance.governanceworkbench.copy.69", "Select a version")}
        />
      );
    }

    if (targetView === "changes") {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.copy.70", "Change summary")}
            highlights={[
              { label: t("pages.governance.governanceworkbench.copy.71", "Event count"), value: auditEvents.length },
              {
                label: t("pages.governance.governanceworkbench.copy.72", "Latest update"),
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
            title={t("pages.governance.governanceworkbench.copy.73", "Policy catalog")}
            highlights={[
              { label: t("pages.governance.governanceworkbench.copy.74", "Active policies"), value: activePolicies.length },
              {
                label: t("pages.governance.governanceworkbench.copy.75", "Retired"),
                value: (policiesQuery.data?.policies ?? []).filter(
                  (policy) => policy.retired,
                ).length,
              },
              {
                label: t("pages.governance.governanceworkbench.copy.76", "Requires active deployment"),
                value: activePolicies.filter(
                  (policy) => policy.invokeRequiresActiveDeployment,
                ).length,
              },
            ]}
          />
          <div style={stageTableShellStyle}>
            {policiesQuery.isLoading ? (
              <AevatarContentSkeleton
                ariaLabel={t("pages.governance.governanceworkbench.copy.77", "Loading policies...")}
                columnWidths={["1.4fr", "1fr", "1fr", 120]}
                rows={4}
                variant="table"
              />
            ) : (
              <Table<ServicePolicySnapshot>
                columns={policyTableColumns}
                dataSource={policiesQuery.data?.policies ?? []}
                locale={{
                  emptyText: t("pages.governance.governanceworkbench.copy.78", "This service has no governance policies yet."),
                }}
                pagination={{ pageSize: 8, showSizeChanger: false }}
                rowKey="policyId"
                size="middle"
              />
            )}
          </div>
        </div>
      );
    }

    if (targetView === "bindings") {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.copy.79", "Binding catalog")}
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
            {bindingsQuery.isLoading ? (
              <AevatarContentSkeleton
                ariaLabel={t("pages.governance.governanceworkbench.copy.80", "Loading bindings...")}
                columnWidths={["1.2fr", "1fr", "1.4fr", 120]}
                rows={4}
                variant="table"
              />
            ) : (
              <Table<ServiceBindingSnapshot>
                columns={bindingTableColumns}
                dataSource={bindingsQuery.data?.bindings ?? []}
                locale={{
                  emptyText: t("pages.governance.governanceworkbench.copy.81", "This service has no binding dependencies yet."),
                }}
                pagination={{ pageSize: 8, showSizeChanger: false }}
                rowKey="bindingId"
                size="middle"
              />
            )}
          </div>
        </div>
      );
    }

    if (targetView === "endpoints") {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.copy.82", "Endpoint catalog")}
            highlights={[
              { label: t("pages.governance.governanceworkbench.copy.83", "Public"), value: publicEndpoints.length },
              { label: t("pages.governance.governanceworkbench.copy.84", "Internal"), value: internalEndpoints.length },
              { label: t("pages.governance.governanceworkbench.copy.85", "Deactivate"), value: disabledEndpoints.length },
            ]}
          />
          <div style={stageTableShellStyle}>
            {endpointsQuery.isLoading ? (
              <AevatarContentSkeleton
                ariaLabel={t("pages.governance.governanceworkbench.copy.86", "Loading endpoint catalog...")}
                columnWidths={["1.2fr", "1fr", "1.4fr", "1fr", 120]}
                rows={4}
                variant="table"
              />
            ) : (
              <Table<ServiceEndpointExposureSnapshot>
                columns={endpointTableColumns}
                dataSource={endpointsQuery.data?.endpoints ?? []}
                locale={{
                  emptyText: t("pages.governance.governanceworkbench.copy.87", "This service has no endpoint catalog yet."),
                }}
                pagination={{ pageSize: 8, showSizeChanger: false }}
                rowKey="endpointId"
                size="middle"
              />
            )}
          </div>
        </div>
      );
    }

    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <GovernanceSummaryPanel
          actions={releaseHandoffAction}
          draft={activeDraft}
          includeDefaultFields={false}
          metrics={[
            {
              label: t("pages.governance.governanceworkbench.copy.88", "Missing policies"),
              tone:
                (activationQuery.data?.missingPolicyIds.length ?? 0) > 0
                  ? "warning"
                  : "success",
              value: String(activationQuery.data?.missingPolicyIds.length ?? 0),
            },
            {
              label: t("pages.governance.governanceworkbench.copy.89", "visible binding"),
              value: String((activationQuery.data?.bindings ?? []).length),
            },
            {
              label: t("pages.governance.governanceworkbench.copy.90", "visible entrance"),
              value: String((activationQuery.data?.endpoints ?? []).length),
            },
            {
              label: t("pages.governance.governanceworkbench.copy.91", "visible strategy"),
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
                ? t("pages.governance.governanceworkbench.copy.92", "Activation blockers present")
                : t("pages.governance.governanceworkbench.copy.93", "Can enter activation"),
          }}
          title={t("pages.governance.governanceworkbench.copy.94", "Activate diagnostics")}
        />

        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))",
          }}
        >
          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.copy.95", "Missing policies")}
            highlights={
              (activationQuery.data?.missingPolicyIds ?? []).length > 0
                ? activationQuery.data?.missingPolicyIds.map((policyId) => ({
                    key: policyId,
                    label: t("pages.governance.governanceworkbench.policy", "Policy"),
                    value: t("pages.governance.governanceworkbench.copy.96", "Missing"),
                  })) ?? []
                : [{ label: t("pages.governance.governanceworkbench.copy.97", "Status"), value: t("pages.governance.governanceworkbench.copy.98", "No missing policies") }]
            }
          />

          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.copy.99", "Scoped bindings")}
            highlights={
              (activationQuery.data?.bindings ?? []).length > 0
                ? (activationQuery.data?.bindings ?? []).slice(0, 4).map((binding) => ({
                    key: binding.bindingId,
                    label: formatGovernanceResourceLabel(
                      binding.displayName || binding.bindingId,
                      t("pages.governance.governanceworkbench.binding", "Binding"),
                    ),
                    value: formatAevatarStatusLabel(binding.bindingKind),
                  }))
                : [{ label: t("pages.governance.governanceworkbench.copy.100", "Status"), value: t("pages.governance.governanceworkbench.copy.101", "There are currently no visible bindings") }]
            }
          />

          <GovernanceSelectionNotice
            title={t("pages.governance.governanceworkbench.copy.102", "Current entrance coverage")}
            highlights={
              (activationQuery.data?.endpoints ?? []).length > 0
                ? (activationQuery.data?.endpoints ?? []).slice(0, 4).map((endpoint) => ({
                    key: endpoint.endpointId,
                    label: formatGovernanceResourceLabel(
                      endpoint.displayName || endpoint.endpointId,
                      t("pages.governance.governanceworkbench.endpoint", "Endpoint"),
                    ),
                    value: formatAevatarStatusLabel(endpoint.exposureKind),
                  }))
                : [{ label: t("pages.governance.governanceworkbench.copy.103", "Status"), value: t("pages.governance.governanceworkbench.copy.104", "There is currently no visible entrance") }]
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
    releaseHandoffAction,
    selectedService?.serviceKey,
    bindingTableColumns,
    endpointTableColumns,
    stageTableShellStyle,
    surfaceToken,
    draft,
  ]);

  return (
    <ConsoleMenuPageShell
      breadcrumbItems={platformBreadcrumbItems}
      title="Governance"
    >
      <div style={buildAevatarViewportStyle(surfaceToken)}>
        <ConsoleOperationNotice
          errorMessage={t(
            "pages.governance.governanceworkbench.actionFailed",
            "Governance action could not be completed. Try again.",
          )}
          notice={
            notice ? { message: notice.message, type: notice.tone } : null
          }
          onClose={() => setNotice(null)}
        />
        {commandReceipt && commandReceiptObservation ? (
          <Alert
            closable
            description={t("pages.governance.governanceworkbench.copy.105", "{value1} target {value2}. {value3}", { value1: commandReceipt.commandLabel, value2: commandReceipt.targetId, value3: commandReceiptObservation.summary })}
            message={t("pages.governance.governanceworkbench.copy.106", "Governance order received")}
            showIcon
            type={commandReceiptObservation.observed ? "success" : "info"}
            onClose={() => setCommandReceipt(null)}
          />
        ) : null}

        <GovernanceQueryCard
          draft={draft}
          includeRevision={view === "activation"}
          loadLabel={
            view === "activation" ? t("pages.governance.governanceworkbench.copy.107", "Load activation diagnostics") : t("pages.governance.governanceworkbench.copy.108", "Load management workbench")
          }
          onChange={setDraft}
          onLoad={() => {
            const nextActiveDraft = normalizeGovernanceDraft(draft);
            setDraft(nextActiveDraft);
            setActiveDraft(nextActiveDraft);
            setCommandReceipt(null);
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
            setCommandReceipt(null);
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
                          {t("pages.governance.governanceworkbench.copy.109", "Governance workspace")}</Typography.Text>
                        <Typography.Text
                          strong
                          style={{ color: surfaceToken.colorTextHeading, fontSize: 20 }}
                        >
                          {formatConsoleMessage(governanceViewMeta[view].title)}
                        </Typography.Text>
                        {governanceViewMeta[view].description ? (
                          <Typography.Text
                            type="secondary"
                            style={{ fontSize: 14, lineHeight: 1.65 }}
                          >
                            {formatConsoleMessage(governanceViewMeta[view].description)}
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
                        <Space wrap size={[8, 8]} style={{ justifyContent: "flex-end" }}>
                          {headerReleaseHandoffAction}
                          {activeAction ? (
                            <Button
                              icon={activeAction.icon}
                              onClick={activeAction.onClick}
                              type={activeAction.type}
                            >
                              {activeAction.label}
                            </Button>
                          ) : null}
                        </Space>
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
