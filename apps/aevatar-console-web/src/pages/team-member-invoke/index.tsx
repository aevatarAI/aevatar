import { EditOutlined, HistoryOutlined, ReloadOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Button, Space, Spin } from "antd";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { t } from "@/shared/i18n/messages";
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  buildTeamMemberPublishedRunsHref,
  buildTeamMemberWorkflowStudioHref,
} from "@/shared/navigation/teamRoutes";
import {
  type ScopeConsoleServiceOption,
  scopeServiceNamespace,
} from "@/shared/runs/scopeConsole";
import { studioApi } from "@/shared/studio/api";
import {
  normalizeStudioMemberBindingImplementationKind,
  normalizeStudioMemberLifecycleStage,
  type StudioMemberBindingContract,
} from "@/shared/studio/models";
import { resolveStudioMemberDraftWorkflowId } from "@/shared/studio/memberWorkflowIdentity";
import {
  AevatarInspectorEmpty,
  type AevatarBreadcrumbItem,
  AevatarPageShell,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { describeError } from "@/shared/ui/errorText";
import StudioMemberInvokePanel from "../studio/components/StudioMemberInvokePanel";

type TeamMemberInvokeRouteState = {
  readonly memberId: string;
  readonly scopeId: string;
  readonly teamId: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function getCompletedBindingRevisionId(
  lastBinding: StudioMemberBindingContract | null | undefined,
  lastBoundRevisionId: string | null | undefined,
): string {
  return trimOptional(lastBinding?.revisionId) || trimOptional(lastBoundRevisionId);
}

function decodePathSegment(value: string): string {
  try {
    return decodeURIComponent(value).trim();
  } catch {
    return value.trim();
  }
}

function readTeamMemberInvokeRouteState(
  pathname = typeof window === "undefined" ? "" : window.location.pathname,
): TeamMemberInvokeRouteState {
  const segments = pathname.split("/").filter(Boolean).map(decodePathSegment);
  const isScopedInvokePath =
    segments[0] === "scopes" &&
    segments[2] === "teams" &&
    segments[4] === "members" &&
    segments[6] === "invoke";
  if (isScopedInvokePath) {
    return {
      memberId: trimOptional(segments[5]),
      scopeId: trimOptional(segments[1]),
      teamId: trimOptional(segments[3]),
    };
  }

  return {
    memberId: "",
    scopeId: "",
    teamId: "",
  };
}

const invokeStageStyle: React.CSSProperties = {
  minHeight: 520,
};

const TeamMemberInvokePage: React.FC = () => {
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => "",
  );
  const route = React.useMemo(
    () => readTeamMemberInvokeRouteState(),
    [locationSnapshot],
  );
  const backHref = buildTeamDetailHref({
    memberId: route.memberId || undefined,
    scopeId: route.scopeId,
    tab: "members",
    teamId: route.teamId,
  });
  const publishedRunsHref = buildTeamMemberPublishedRunsHref({
    memberId: route.memberId || undefined,
    scopeId: route.scopeId,
    teamId: route.teamId,
  });

  const memberQuery = useQuery({
    queryKey: ["team-member-invoke", "member", route.scopeId, route.memberId],
    enabled: Boolean(route.scopeId && route.memberId),
    queryFn: () => studioApi.getMember(route.scopeId, route.memberId),
    refetchOnMount: "always",
    staleTime: 0,
  });
  const bindingQuery = useQuery({
    queryKey: ["team-member-invoke", "binding", route.scopeId, route.memberId],
    enabled: Boolean(route.scopeId && route.memberId),
    queryFn: () => studioApi.getMemberBinding(route.scopeId, route.memberId),
    refetchOnMount: "always",
    staleTime: 0,
  });
  const memberBreadcrumbLabel =
    trimOptional(memberQuery.data?.summary.displayName) ||
    trimOptional(route.memberId) ||
    t("pages.teammemberinvoke.memberBreadcrumb", "Member");
  const breadcrumbItems: AevatarBreadcrumbItem[] = [
    {
      href: backHref,
      onClick: (event) => {
        event.preventDefault();
        history.push(backHref);
      },
      title: t("pages.teammemberinvoke.teamsBreadcrumb", "Teams"),
    },
    {
      title: memberBreadcrumbLabel,
    },
    {
      current: true,
      title: t("pages.teammemberinvoke.invokeBreadcrumb", "Invoke"),
    },
  ];

  const memberSummary = memberQuery.data?.summary ?? null;
  const memberDraftWorkflowId = resolveStudioMemberDraftWorkflowId(
    memberQuery.data,
  );
  const workflowStudioHref = buildTeamMemberWorkflowStudioHref({
    memberId: route.memberId,
    mode: "edit-member",
    scopeId: route.scopeId,
    teamId: route.teamId,
    workflowId: memberDraftWorkflowId || undefined,
  });
  const memberKind = normalizeStudioMemberBindingImplementationKind(
    memberSummary?.implementationKind,
  );
  const memberLifecycleStage = normalizeStudioMemberLifecycleStage(
    memberSummary?.lifecycleStage,
  );
  const lastBinding = bindingQuery.data?.lastBinding ?? memberQuery.data?.lastBinding ?? null;
  const completedBindingRevisionId = getCompletedBindingRevisionId(
    lastBinding,
    memberSummary?.lastBoundRevisionId,
  );
  const memberBindingCompleted =
    memberLifecycleStage === "bind_ready" && Boolean(completedBindingRevisionId);
  const bindingPublishedServiceId = memberBindingCompleted
    ? trimOptional(lastBinding?.publishedServiceId) ||
      trimOptional(memberSummary?.publishedServiceId)
    : "";
  const memberLabel =
    trimOptional(memberSummary?.displayName) ||
    t("pages.teammemberinvoke.member", "Member");
  const endpointContractQuery = useQuery({
    queryKey: [
      "team-member-invoke",
      "endpoint-contract",
      route.scopeId,
      route.memberId,
      "chat",
    ],
    enabled: Boolean(route.scopeId && route.memberId && memberBindingCompleted),
    queryFn: () =>
      scopeRuntimeApi.getMemberEndpointContract(route.scopeId, route.memberId, "chat"),
    refetchOnMount: "always",
    staleTime: 0,
  });
  const endpointContract = endpointContractQuery.data ?? null;
  const endpointPublishedServiceId = trimOptional(endpointContract?.publishedServiceId);
  const endpointRevisionId = trimOptional(endpointContract?.revisionId);
  const readinessRevisionId = trimOptional(
    endpointContract?.invocationReadiness.revisionId,
  );
  const identityQueriesFetching =
    memberQuery.isFetching ||
    bindingQuery.isFetching ||
    endpointContractQuery.isFetching;
  const endpointContractFresh = Boolean(
    memberBindingCompleted &&
      endpointContractQuery.isSuccess &&
      !identityQueriesFetching,
  );
  const endpointContractIdentityMismatch = Boolean(
    endpointContract &&
      (trimOptional(endpointContract.scopeId) !== route.scopeId ||
        trimOptional(endpointContract.memberId) !== route.memberId ||
        !endpointPublishedServiceId ||
        (bindingPublishedServiceId &&
          endpointPublishedServiceId !== bindingPublishedServiceId) ||
        trimOptional(endpointContract.endpointId) !== "chat"),
  );
  const endpointContractProtocolMismatch = Boolean(
    endpointContract &&
      (!endpointContract.supportsSse ||
        !endpointRevisionId ||
        (readinessRevisionId && readinessRevisionId !== endpointRevisionId)),
  );
  const endpointContractSourceVersionPending = Boolean(
    endpointContract &&
      (endpointContract.publishedServiceStateVersion <= 0 ||
        endpointContract.boundRevisionStateVersion <= 0),
  );
  const endpointInvocationUnavailable = Boolean(
    endpointContract &&
      !endpointContractIdentityMismatch &&
      !endpointContractProtocolMismatch &&
      (endpointContractSourceVersionPending ||
        !endpointContract.invocationReadiness.canInvoke ||
        trimOptional(endpointContract.invocationReadiness.status) !== "ready"),
  );
  const endpointContractUsable = Boolean(
    endpointContractFresh &&
      endpointContract &&
      !endpointContractIdentityMismatch &&
      !endpointContractProtocolMismatch &&
      !endpointInvocationUnavailable,
  );
  const verifiedPublishedServiceId = endpointContractUsable
    ? endpointPublishedServiceId
    : "";
  const canOpenPublishedRuns = Boolean(
    route.scopeId &&
      route.teamId &&
      route.memberId &&
      memberBindingCompleted &&
      endpointContractFresh &&
      !endpointContractIdentityMismatch &&
      (bindingPublishedServiceId || verifiedPublishedServiceId),
  );
  const publishedRunsPlaceholderReason = canOpenPublishedRuns
    ? t(
        "pages.teammemberinvoke.publishedRuns.open",
        "View runs from the published member service.",
      )
    : t(
        "pages.teammemberinvoke.publishedRuns.publishFirst",
        "Publish this member to start recording published runs.",
      );
  const invokeServices = React.useMemo<ScopeConsoleServiceOption[]>(() => {
    if (
      !endpointContract ||
      !endpointContractUsable ||
      !verifiedPublishedServiceId
    ) {
      return [];
    }

    return [
      {
        deploymentStatus: endpointContract.deploymentStatus,
        displayName: memberLabel,
        endpoints: [
          {
            description: "",
            displayName: "Chat",
            endpointId: endpointContract.endpointId,
            kind: "chat",
            requestTypeUrl: endpointContract.requestTypeUrl,
            responseTypeUrl: endpointContract.responseTypeUrl,
          },
        ],
        kind: "service",
        namespace: scopeServiceNamespace,
        serviceId: verifiedPublishedServiceId,
      },
    ];
  }, [
    endpointContract,
    endpointContractUsable,
    memberLabel,
    verifiedPublishedServiceId,
  ]);
  const serviceRevisionQuery = useQuery({
    queryKey: [
      "team-member-invoke",
      "service-revisions",
      route.scopeId,
      verifiedPublishedServiceId,
      endpointRevisionId,
    ],
    enabled: Boolean(route.scopeId && verifiedPublishedServiceId),
    queryFn: () =>
      scopeRuntimeApi.getServiceRevisions(route.scopeId, verifiedPublishedServiceId),
    refetchOnMount: "always",
    staleTime: 0,
  });
  const memberRevision =
    serviceRevisionQuery.data?.revisions.find(
      (revision) => revision.revisionId === endpointRevisionId,
    ) ??
    (lastBinding && trimOptional(lastBinding.revisionId) === endpointRevisionId
      ? {
          allocationWeight: 100,
          artifactHash: "",
          createdAt: lastBinding.boundAt || null,
          deploymentId: "",
          failureReason: "",
          implementationKind: lastBinding.implementationKind,
          inlineWorkflowCount: 0,
          isActiveServing: true,
          isDefaultServing: true,
          isServingTarget: true,
          preparedAt: null,
          primaryActorId: "",
          publishedAt: lastBinding.boundAt || null,
          retiredAt: null,
          revisionId: lastBinding.revisionId,
          scriptDefinitionActorId: "",
          scriptId: "",
          scriptRevision: "",
          scriptSourceHash: "",
          servingState: "Active",
          staticActorTypeName: "",
          status: "Published",
          workflowDefinitionActorId: "",
          workflowName: "",
        }
      : null);
  const isLoading =
    memberQuery.isFetching ||
    bindingQuery.isFetching ||
    (memberBindingCompleted && endpointContractQuery.isFetching) ||
    serviceRevisionQuery.isFetching;
  const loadError =
    memberQuery.error ||
    bindingQuery.error ||
    (memberBindingCompleted ? endpointContractQuery.error : null) ||
    null;
  const blockedState = React.useMemo(() => {
    if (!route.scopeId || !route.teamId || !route.memberId) {
      return {
        description: t(
          "pages.teammemberinvoke.route.missing.description",
          "Open this page from a concrete team member so the invoke target stays stable.",
        ),
        message: t("pages.teammemberinvoke.route.missing", "Missing member route"),
        type: "error" as const,
      };
    }

    if (loadError) {
      return {
        description: describeError(loadError),
        message: t("pages.teammemberinvoke.load.failed", "Member invoke context could not be loaded."),
        type: "error" as const,
      };
    }

    if (memberSummary && memberKind !== "workflow") {
      return {
        description: t(
          "pages.teammemberinvoke.workflow.only.description",
          "This page only runs workflow members. Use the member's own surface for other implementation kinds.",
        ),
        message: t("pages.teammemberinvoke.workflow.only", "Invoke is available for workflow members only."),
        type: "warning" as const,
      };
    }

    if (memberSummary && !memberBindingCompleted) {
      return {
        description: t(
          "pages.teammemberinvoke.unbound.description",
          "Bind this workflow member first so it has a published callable service and endpoint contract.",
        ),
        message: t("pages.teammemberinvoke.unbound", "This workflow member is not bound yet."),
        type: "warning" as const,
      };
    }

    if (
      memberSummary &&
      memberBindingCompleted &&
      (endpointContractIdentityMismatch || endpointContractProtocolMismatch)
    ) {
      return {
        description: t(
          "pages.teammemberinvoke.endpoint.missing.description",
          "The published service has no callable endpoints available to this page.",
        ),
        message: t("pages.teammemberinvoke.endpoint.missing", "No callable endpoint is available."),
        type: "error" as const,
      };
    }

    if (
      memberSummary &&
      memberBindingCompleted &&
      !endpointContract &&
      !endpointContractQuery.isFetching
    ) {
      return {
        description: t(
          "pages.teammemberinvoke.service.pending.description",
          "The member binding exists, but the service catalog has not exposed its callable endpoints yet.",
        ),
        message: t("pages.teammemberinvoke.service.pending", "Published service is not visible yet."),
        type: "info" as const,
      };
    }

    if (
      memberSummary &&
      memberBindingCompleted &&
      endpointContract &&
      endpointInvocationUnavailable
    ) {
      return {
        description:
          (endpointContractSourceVersionPending
            ? t(
                "pages.teammemberinvoke.endpoint.versionPending.description",
                "Committed service and revision source versions are not visible yet.",
              )
            : trimOptional(endpointContract.invocationReadiness.message)) ||
          t(
            "pages.teammemberinvoke.endpoint.notReady.description",
            "The endpoint contract is valid, but its runtime is not ready to accept invocations.",
          ),
        message: t(
          "pages.teammemberinvoke.endpoint.notReady",
          "Member endpoint is not ready.",
        ),
        type: "warning" as const,
      };
    }

    if (endpointContract && invokeServices.length === 0) {
      return {
        description: t(
          "pages.teammemberinvoke.endpoint.missing.description",
          "The published service has no callable endpoints available to this page.",
        ),
        message: t("pages.teammemberinvoke.endpoint.missing", "No callable endpoint is available."),
        type: "warning" as const,
      };
    }

    return null;
  }, [
    invokeServices.length,
    loadError,
    memberBindingCompleted,
    memberKind,
    memberSummary,
    endpointContract,
    endpointContractIdentityMismatch,
    endpointContractProtocolMismatch,
    endpointContractQuery.isFetching,
    endpointContractSourceVersionPending,
    endpointInvocationUnavailable,
    route.memberId,
    route.scopeId,
    route.teamId,
  ]);

  return (
    <AevatarPageShell
      backAriaLabel={t("pages.teammemberinvoke.backToTeam", "Back to team")}
      backTitle={t("pages.teammemberinvoke.backToTeam", "Back to team")}
      breadcrumbItems={breadcrumbItems}
      breadcrumbRender={false}
      extra={
        <Space data-testid="team-member-invoke-header-actions" size={8} wrap>
          <Button
            disabled={!canOpenPublishedRuns}
            icon={<HistoryOutlined />}
            onClick={() => {
              if (canOpenPublishedRuns) {
                history.push(publishedRunsHref);
              }
            }}
            title={publishedRunsPlaceholderReason}
          >
            {t("pages.teammemberinvoke.publishedRuns", "Published runs")}
          </Button>
          <Button
            icon={<EditOutlined />}
            onClick={() => history.push(workflowStudioHref)}
          >
            {t("pages.teammemberinvoke.open.studio", "Workflow Studio")}
          </Button>
        </Space>
      }
      layoutMode="document"
      onBack={() => history.push(backHref)}
      title={t("pages.teammemberinvoke.title", "Run workflow member")}
    >
      <div style={invokeStageStyle}>
        {isLoading ? (
          <div
            style={{
              alignItems: "center",
              display: "flex",
              justifyContent: "center",
              minHeight: 360,
            }}
          >
            <Spin
              description={t(
                "pages.teammemberinvoke.loading",
                "Loading invoke context...",
              )}
            />
          </div>
        ) : blockedState ? (
          <AevatarPanel
            description={blockedState.description}
            extra={
              <Space size={8} wrap>
                {memberBindingCompleted ? (
                  <Button
                    icon={<ReloadOutlined />}
                    onClick={() => {
                      void Promise.all([
                        memberQuery.refetch(),
                        bindingQuery.refetch(),
                        endpointContractQuery.refetch(),
                      ]);
                    }}
                  >
                    {t(
                      "pages.teammemberinvoke.refresh",
                      "Refresh status",
                    )}
                  </Button>
                ) : null}
                <Button
                  icon={<EditOutlined />}
                  onClick={() => history.push(workflowStudioHref)}
                  type="primary"
                >
                  {t("pages.teammemberinvoke.open.studio", "Workflow Studio")}
                </Button>
              </Space>
            }
            title={blockedState.message}
          >
            <AevatarInspectorEmpty
              compact
              description={blockedState.description}
              title={t("pages.teammemberinvoke.next.step", "Next step")}
            />
          </AevatarPanel>
        ) : (
          <StudioMemberInvokePanel
            authoritativeEndpointContract={endpointContract}
            enableFileAttachments
            initialServiceId={verifiedPublishedServiceId}
            memberId={route.memberId}
            memberRevision={memberRevision}
            presentation="member-run"
            runtimeTarget="member"
            teamId={route.teamId}
            scopeId={route.scopeId}
            selectedMemberLabel={memberLabel}
            services={invokeServices}
          />
        )}
      </div>
    </AevatarPageShell>
  );
};

export default TeamMemberInvokePage;
