import {
  HistoryOutlined,
  MenuOutlined,
  PartitionOutlined,
  SettingOutlined,
} from '@ant-design/icons';
import { Button, Drawer } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import {
  ConsoleAuthActions,
  ConsoleLanguageSwitch,
} from '@/shared/ui/ConsoleHeaderActions';
import { useWorkflowActivityAccount } from './account/useWorkflowActivityAccount';
import {
  buildWorkflowActivitySectionHref,
  type WorkflowActivitySection,
} from './navigation';
import { workflowActivityVNextCss } from './styles';

type ShellProps = {
  readonly activeSection: WorkflowActivitySection;
  readonly children: React.ReactNode;
  readonly contentClassName?: string;
  readonly description?: string;
  readonly footer?: React.ReactNode;
  readonly headerActions?: React.ReactNode;
  readonly heading?: React.ReactNode;
  readonly mainClassName?: string;
  readonly mainRef?: React.Ref<HTMLElement>;
  readonly onNavigate?: (target: string) => void;
  readonly scopeId: string;
  readonly title: string;
};

const items = [
  {
    key: 'workflows' as const,
    icon: <PartitionOutlined aria-hidden="true" />,
    labelKey: 'workflows',
    fallback: 'Workflows',
  },
  {
    key: 'activity' as const,
    icon: <HistoryOutlined aria-hidden="true" />,
    labelKey: 'activity',
    fallback: 'Activity',
  },
  {
    key: 'settings' as const,
    icon: <SettingOutlined aria-hidden="true" />,
    labelKey: 'settings',
    fallback: 'Settings',
  },
];

function shouldHandleClientNavigation(
  event: React.MouseEvent<HTMLAnchorElement>,
): boolean {
  return (
    event.button === 0 &&
    !event.altKey &&
    !event.ctrlKey &&
    !event.metaKey &&
    !event.shiftKey
  );
}

function Navigation({
  activeSection,
  afterNavigate,
  onNavigate,
  scopeId,
}: Pick<ShellProps, 'activeSection' | 'onNavigate' | 'scopeId'> & {
  readonly afterNavigate?: () => void;
}) {
  return (
    <nav
      aria-label={t('workflowActivityVNext.nav.aria', 'Workflow workbench')}
      className="wa-vnext__nav"
    >
      {items.map((item) => (
        <a
          aria-current={item.key === activeSection ? 'page' : undefined}
          className="wa-vnext__nav-button"
          href={buildWorkflowActivitySectionHref(scopeId, item.key)}
          key={item.key}
          onClick={(event) => {
            if (!shouldHandleClientNavigation(event)) return;
            event.preventDefault();
            const target = buildWorkflowActivitySectionHref(scopeId, item.key);
            if (onNavigate) onNavigate(target);
            else history.push(target);
            afterNavigate?.();
          }}
        >
          {item.icon}
          <span>
            {t(`workflowActivityVNext.nav.${item.labelKey}`, item.fallback)}
          </span>
        </a>
      ))}
    </nav>
  );
}

const WorkflowActivityVNextShell: React.FC<ShellProps> = ({
  activeSection,
  children,
  contentClassName,
  description,
  footer,
  headerActions,
  heading,
  mainClassName,
  mainRef,
  onNavigate,
  scopeId,
  title,
}) => {
  const { principal: accountPrincipal } = useWorkflowActivityAccount();
  const [mobileNavigationOpen, setMobileNavigationOpen] = React.useState(false);
  const activeItem = items.find((item) => item.key === activeSection);
  const activeLabel = activeItem
    ? t(`workflowActivityVNext.nav.${activeItem.labelKey}`, activeItem.fallback)
    : title;
  const mainBody = (
    <>
      <header className="wa-vnext__header">
        <div
          className={`wa-vnext__heading-copy${heading ? ' wa-vnext__heading-copy--custom' : ''}`}
        >
          <h1>{heading ?? title}</h1>
          {description ? <p>{description}</p> : null}
        </div>
        {headerActions ? (
          <div className="wa-vnext__header-actions">{headerActions}</div>
        ) : null}
      </header>
      <div
        className={`wa-vnext__content${contentClassName ? ` ${contentClassName}` : ''}`}
      >
        {children}
      </div>
    </>
  );

  return (
    <div className="wa-vnext">
      <style>{workflowActivityVNextCss}</style>
      <header className="wa-vnext__topbar">
        <div className="wa-vnext__topbar-leading">
          <Button
            aria-label={t(
              'workflowActivityVNext.nav.openMenu',
              'Open navigation',
            )}
            className="wa-vnext__menu-button"
            icon={<MenuOutlined />}
            onClick={() => setMobileNavigationOpen(true)}
            type="text"
          />
          <a
            className="wa-vnext__brand"
            href={buildWorkflowActivitySectionHref(scopeId, 'workflows')}
            onClick={(event) => {
              if (!shouldHandleClientNavigation(event)) return;
              event.preventDefault();
              const target = buildWorkflowActivitySectionHref(
                scopeId,
                'workflows',
              );
              if (onNavigate) onNavigate(target);
              else history.push(target);
            }}
          >
            {t('common.appName', 'Aevatar')}
          </a>
          <span aria-hidden="true" className="wa-vnext__topbar-divider" />
          <span className="wa-vnext__topbar-context">{activeLabel}</span>
        </div>
        <div className="wa-vnext__topbar-actions">
          <ConsoleLanguageSwitch />
          <ConsoleAuthActions principal={accountPrincipal} />
        </div>
      </header>
      <aside className="wa-vnext__rail">
        <Navigation
          activeSection={activeSection}
          onNavigate={onNavigate}
          scopeId={scopeId}
        />
      </aside>
      <main
        className={`wa-vnext__main${footer ? ' wa-vnext__main--with-footer' : ''}${mainClassName ? ` ${mainClassName}` : ''}`}
        ref={mainRef}
      >
        {footer ? (
          <div className="wa-vnext__main-scroll">{mainBody}</div>
        ) : (
          mainBody
        )}
        {footer ? <div className="wa-vnext__main-footer">{footer}</div> : null}
      </main>
      <Drawer
        className="wa-vnext__drawer"
        closable
        onClose={() => setMobileNavigationOpen(false)}
        open={mobileNavigationOpen}
        placement="left"
        rootClassName="wa-vnext-drawer"
        size={240}
        title={t('workflowActivityVNext.brand.subtitle', 'Automation ledger')}
      >
        <Navigation
          activeSection={activeSection}
          afterNavigate={() => setMobileNavigationOpen(false)}
          onNavigate={onNavigate}
          scopeId={scopeId}
        />
        <div className="wa-vnext__drawer-actions">
          <ConsoleLanguageSwitch />
          <ConsoleAuthActions principal={accountPrincipal} />
        </div>
      </Drawer>
    </div>
  );
};

export default WorkflowActivityVNextShell;
