import { EditOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Button, Spin } from "antd";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { getScopeServiceCurrentRevision } from "@/shared/models/runtime/scopeServices";
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  buildTeamMemberWorkflowStudioHref,
} from "@/shared/navigation/teamRoutes";
import {
  buildScopeConsoleServiceOptions,
  scopeServiceAppId,
} from "@/shared/runs/scopeConsole";
import { studioApi } from "@/shared/studio/api";
import {
  normalizeStudioMemberBindingImplementationKind,
} from "@/shared/studio/models";
import {
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { describeError } from "@/shared/ui/errorText";
import StudioMemberInvokePanel from "../studio/components/StudioMemberInvokePanel";
import { t } from "@/shared/i18n/messages";

type TeamMemberInvokeRouteState = {
  readonly memberId: string;
  readonly scopeId: string;
  readonly teamId: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
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
  const workflowStudioHref = buildTeamMemberWorkflowStudioHref({
    memberId: route.memberId,
    mode: "edit-member",
    scopeId: route.scopeId,
    teamId: route.teamId,
  });

  const memberQuery = useQuery({
    queryKey: ["team-member-invoke", "member", route.scopeId, route.memberId],
    enabled: Boolean(route.scopeId && route.memberId),
    queryFn: () => studioApi.getMember(route.scopeId, route.memberId),
  });
  const bindingQuery = useQuery({
    queryKey: ["team-member-invoke", "binding", route.scopeId, route.memberId],
    enabled: Boolean(route.scopeId && route.memberId),
    queryFn: () => studioApi.getMemberBinding(route.scopeId, route.memberId),
  });
  const servicesQuery = useQuery({
    queryKey: ["team-member-invoke", "services", route.scopeId],
    enabled: Boolean(route.scopeId),
    queryFn: () =>
      scopeRuntimeApi.listServices(route.scopeId, {
        appId: scopeServiceAppId,
      }),
  });

  const memberSummary = memberQuery.data?.summary ?? null;
  const memberKind = normalizeStudioMemberBindingImplementationKind(
    memberSummary?.implementationKind,
  );
  const lastBinding = bindingQuery.data?.lastBinding ?? memberQuery.data?.lastBinding ?? null;
  const publishedServiceId =
    trimOptional(lastBinding?.publishedServiceId) ||
    trimOptional(memberSummary?.publishedServiceId);
  const selectedService = React.useMemo(
    () =>
      publishedServiceId
        ? (servicesQuery.data ?? []).find(
            (service) => trimOptional(service.serviceId) === publishedServiceId,
          ) ?? null
        : null,
    [publishedServiceId, servicesQuery.data],
  );
  const invokeServices = React.useMemo(
    () =>
      selectedService
        ? buildScopeConsoleServiceOptions([selectedService], selectedService.serviceId, {
            sortBy: "serviceId",
          }).filter((service) => service.serviceId === selectedService.serviceId)
        : [],
    [selectedService],
  );
  const serviceRevisionQuery = useQuery({
    queryKey: [
      "team-member-invoke",
      "service-revisions",
      route.scopeId,
      publishedServiceId,
    ],
    enabled: Boolean(route.scopeId && publishedServiceId),
    queryFn: () => scopeRuntimeApi.getServiceRevisions(route.scopeId, publishedServiceId),
  });
  const memberRevision =
    getScopeServiceCurrentRevision(serviceRevisionQuery.data) ??
    (lastBinding
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
  const memberLabel =
    trimOptional(memberSummary?.displayName) ||
    t("pages.teammemberinvoke.member", "Member");
  const isLoading =
    memberQuery.isLoading || bindingQuery.isLoading || servicesQuery.isLoading;
  const loadError =
    memberQuery.error || bindingQuery.error || servicesQuery.error || null;
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

    if (memberSummary && !publishedServiceId) {
      return {
        description: t(
          "pages.teammemberinvoke.unbound.description",
          "Bind this workflow member first so it has a published callable service and endpoint contract.",
        ),
        message: t("pages.teammemberinvoke.unbound", "This workflow member is not bound yet."),
        type: "warning" as const,
      };
    }

    if (memberSummary && publishedServiceId && !selectedService && !servicesQuery.isLoading) {
      return {
        description: t(
          "pages.teammemberinvoke.service.pending.description",
          "The member binding exists, but the service catalog has not exposed its callable endpoints yet.",
        ),
        message: t("pages.teammemberinvoke.service.pending", "Published service is not visible yet."),
        type: "info" as const,
      };
    }

    if (selectedService && invokeServices.length === 0) {
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
    memberKind,
    memberSummary,
    publishedServiceId,
    route.memberId,
    route.scopeId,
    route.teamId,
    selectedService,
    servicesQuery.isLoading,
  ]);

  return (
    <AevatarPageShell
      breadcrumbRender={false}
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
              <Button
                icon={<EditOutlined />}
                onClick={() => history.push(workflowStudioHref)}
                type="primary"
              >
                {t("pages.teammemberinvoke.open.studio", "Workflow Studio")}
              </Button>
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
            initialServiceId={publishedServiceId}
            memberId={route.memberId}
            memberRevision={memberRevision}
            runtimeTarget="service"
            teamId={route.teamId}
            scopeId={route.scopeId}
            selectedMemberLabel={memberLabel}
            services={invokeServices}
            targetSummaryVariant="member-run"
          />
        )}
      </div>
    </AevatarPageShell>
  );
};

export default TeamMemberInvokePage;
