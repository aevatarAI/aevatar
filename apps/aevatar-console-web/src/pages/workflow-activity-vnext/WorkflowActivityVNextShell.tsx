import {
  ControlOutlined,
  HistoryOutlined,
  PartitionOutlined,
} from "@ant-design/icons";
import React from "react";
import { ConsoleAuthActions, ConsoleLanguageSwitch } from "@/shared/ui/ConsoleHeaderActions";
import { history } from "@/shared/navigation/history";
import { t } from "@/shared/i18n/messages";
import {
  buildWorkflowActivitySectionHref,
  type WorkflowActivitySection,
} from "./navigation";
import { workflowActivityVNextCss } from "./styles";

type ShellProps = {
  readonly activeSection: WorkflowActivitySection;
  readonly children: React.ReactNode;
  readonly description: string;
  readonly headerActions?: React.ReactNode;
  readonly onNavigate?: (target: string) => void;
  readonly scopeId: string;
  readonly title: string;
};

const items = [
  { key: "workflows" as const, icon: <PartitionOutlined />, labelKey: "workflows", fallback: "Workflows" },
  { key: "activity" as const, icon: <HistoryOutlined />, labelKey: "activity", fallback: "Activity" },
  { key: "settings" as const, icon: <ControlOutlined />, labelKey: "settings", fallback: "Settings" },
];

function Navigation({ activeSection, onNavigate, scopeId }: Pick<ShellProps, "activeSection" | "onNavigate" | "scopeId">) {
  return (
    <nav aria-label={t("workflowActivityVNext.nav.aria", "Workflow workbench")} className="wa-vnext__nav">
      {items.map((item) => (
        <a
          aria-current={item.key === activeSection ? "page" : undefined}
          className="wa-vnext__nav-button"
          href={buildWorkflowActivitySectionHref(scopeId, item.key)}
          key={item.key}
          onClick={(event) => {
            event.preventDefault();
            const target = buildWorkflowActivitySectionHref(scopeId, item.key);
            if (onNavigate) onNavigate(target);
            else history.push(target);
          }}
        >
          {item.icon}
          <span>{t(`workflowActivityVNext.nav.${item.labelKey}`, item.fallback)}</span>
        </a>
      ))}
    </nav>
  );
}

const WorkflowActivityVNextShell: React.FC<ShellProps> = ({
  activeSection,
  children,
  description,
  headerActions,
  onNavigate,
  scopeId,
  title,
}) => (
  <div className="wa-vnext">
    <style>{workflowActivityVNextCss}</style>
    <aside className="wa-vnext__rail">
      <div className="wa-vnext__brand">
        <strong>{t("common.appName", "Aevatar")}</strong>
        <span>{t("workflowActivityVNext.brand.subtitle", "AUTOMATION LEDGER")}</span>
      </div>
      <Navigation activeSection={activeSection} onNavigate={onNavigate} scopeId={scopeId} />
      <div className="wa-vnext__rail-foot">
        <span>{t("workflowActivityVNext.scope", "Scope")}</span>
        <strong>{scopeId}</strong>
      </div>
    </aside>
    <main className="wa-vnext__main">
      <div className="wa-vnext__mobile-nav">
        <Navigation activeSection={activeSection} onNavigate={onNavigate} scopeId={scopeId} />
      </div>
      <header className="wa-vnext__header">
        <div>
          <h1>{title}</h1>
          <p>{description}</p>
        </div>
        <div className="wa-vnext__header-actions">
          {headerActions}
          <ConsoleLanguageSwitch />
          <ConsoleAuthActions />
        </div>
      </header>
      <div className="wa-vnext__content">{children}</div>
    </main>
  </div>
);

export default WorkflowActivityVNextShell;
