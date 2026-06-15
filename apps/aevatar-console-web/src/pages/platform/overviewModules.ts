import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import {
  buildPlatformDeploymentsHref,
  buildPlatformGovernanceHref,
  buildPlatformServicesHref,
} from "@/shared/navigation/platformRoutes";
import type { PlatformOverviewModule } from "@/shared/models/platform";
import { t } from "@/shared/i18n/messages";

export function getPlatformOverviewModules(): readonly PlatformOverviewModule[] {
  return [
    {
      key: "capabilities",
      title: t("pages.platform.overview.modules.capabilities.title", "Capabilities"),
      description: t(
        "pages.platform.overview.modules.capabilities.description",
        "Find the published service entry for a team member, confirm its contract, and continue into invoke or governance work.",
      ),
      summary: t(
        "pages.platform.overview.modules.capabilities.summary",
        "Reads the service catalog when you open the workbench.",
      ),
      ctaLabel: t(
        "pages.platform.overview.modules.capabilities.cta",
        "Open capabilities",
      ),
      href: buildPlatformServicesHref(),
    },
    {
      key: "accessRules",
      title: t("pages.platform.overview.modules.accessRules.title", "Access & Rules"),
      description: t(
        "pages.platform.overview.modules.accessRules.description",
        "Review bindings, policies, endpoint exposure, and governance changes around a selected capability.",
      ),
      summary: t(
        "pages.platform.overview.modules.accessRules.summary",
        "Shows governance facts after a scope or service is selected.",
      ),
      ctaLabel: t(
        "pages.platform.overview.modules.accessRules.cta",
        "Open access and rules",
      ),
      href: buildPlatformGovernanceHref(),
    },
    {
      key: "releases",
      title: t("pages.platform.overview.modules.releases.title", "Releases"),
      description: t(
        "pages.platform.overview.modules.releases.description",
        "Check rollout evidence, serving target, revision handoff, and release action readiness before traffic moves.",
      ),
      summary: t(
        "pages.platform.overview.modules.releases.summary",
        "Uses deployment and release records from the release workbench.",
      ),
      ctaLabel: t(
        "pages.platform.overview.modules.releases.cta",
        "Open releases",
      ),
      href: buildPlatformDeploymentsHref(),
    },
    {
      key: "runs",
      title: t("pages.platform.overview.modules.runs.title", "Runs"),
      description: t(
        "pages.platform.overview.modules.runs.description",
        "Launch or inspect a real execution, follow streaming output, and recover runs that need human input or signals.",
      ),
      summary: t(
        "pages.platform.overview.modules.runs.summary",
        "Keeps recent local run handoffs visible and then reads run facts on demand.",
      ),
      ctaLabel: t("pages.platform.overview.modules.runs.cta", "Open runs"),
      href: buildRuntimeRunsHref(),
    },
    {
      key: "runtimeMap",
      title: t("pages.platform.overview.modules.runtimeMap.title", "Runtime Map"),
      description: t(
        "pages.platform.overview.modules.runtimeMap.description",
        "Map an actor, run, or service context into topology, timeline, edge, and snapshot views for diagnostics.",
      ),
      summary: t(
        "pages.platform.overview.modules.runtimeMap.summary",
        "Starts from explicit runtime context; no actor graph is guessed on the overview.",
      ),
      ctaLabel: t(
        "pages.platform.overview.modules.runtimeMap.cta",
        "Open runtime map",
      ),
      href: buildRuntimeExplorerHref(),
    },
  ];
}
