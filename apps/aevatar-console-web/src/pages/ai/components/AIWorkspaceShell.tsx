import {
  DatabaseOutlined,
  HistoryOutlined,
  HomeOutlined,
  MessageOutlined,
  RobotOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Typography } from 'antd';
import React from 'react';
import {
  AIWorkspaceApiError,
  type AIWorkspaceApiLinks,
  type AIWorkspaceContext as AIWorkspaceContextResponse,
  type AIWorkspacePageLinks,
  aiWorkspaceApi,
} from '@/shared/api/aiWorkspaceApi';
import {
  loadStoredAuthSession,
  type NyxIDAuthSession,
} from '@/shared/auth/session';
import { t } from '@/shared/i18n/messages';
import {
  AI_ACTIVITY_ROUTE,
  AI_AGENTS_ROUTE,
  AI_CHAT_ROUTE,
  AI_MODELS_ROUTE,
  AI_OVERVIEW_ROUTE,
} from '@/shared/navigation/aiRoutes';
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from '@/shared/navigation/history';
import {
  type AIWorkspaceSessionAuthority,
  aiWorkspaceQueryKeys,
  readAIWorkspaceSessionAuthority,
} from '@/shared/query/aiWorkspaceQueryKeys';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';
import { describeError } from '@/shared/ui/errorText';
import InventoryReadinessState from '@/shared/ui/InventoryReadinessState';
import '../index.less';

type AIWorkspaceContextValue = {
  context: AIWorkspaceContextResponse;
  queryAuthority: AIWorkspaceSessionAuthority;
  scopeId: string;
};

type AIWorkspaceShellProps = {
  children: React.ReactNode;
};

type AIWorkspaceNavigationItem = {
  icon: React.ReactNode;
  label: string;
  path: string;
};

type AIWorkspaceNavigationCandidate = AIWorkspaceNavigationItem & {
  apiKey?: keyof AIWorkspaceApiLinks;
  key: keyof AIWorkspacePageLinks;
};

const AIWorkspaceReactContext =
  React.createContext<AIWorkspaceContextValue | null>(null);

function isPlainPrimaryClick(
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

function readAccountLabel(session?: NyxIDAuthSession | null): string {
  return session?.user.name?.trim() || session?.user.email?.trim() || '';
}

function normalizePathname(pathname: string): string {
  return pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
}

function AIWorkspaceHeader({
  context,
}: {
  context?: AIWorkspaceContextResponse;
}): React.ReactElement {
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    getLocationSnapshot,
  );
  const pathname = normalizePathname(
    locationSnapshot.split('?')[0]?.split('#')[0] ?? '',
  );
  const activeNavigationLinkRef = React.useRef<HTMLAnchorElement | null>(null);
  const session = loadStoredAuthSession();
  const navigationCandidates: AIWorkspaceNavigationCandidate[] = [
    {
      apiKey: 'overview',
      icon: <HomeOutlined />,
      key: 'overview',
      label: t('pages.ai.shell.navigation.overview', 'Overview'),
      path: AI_OVERVIEW_ROUTE,
    },
    {
      apiKey: 'chat',
      icon: <MessageOutlined />,
      key: 'chat',
      label: t('pages.ai.shell.navigation.chat', 'Chat'),
      path: AI_CHAT_ROUTE,
    },
    {
      apiKey: 'agents',
      icon: <TeamOutlined />,
      key: 'agents',
      label: t('pages.ai.shell.navigation.agents', 'Agents'),
      path: AI_AGENTS_ROUTE,
    },
    {
      apiKey: 'models',
      icon: <DatabaseOutlined />,
      key: 'models',
      label: t('pages.ai.shell.navigation.models', 'Models'),
      path: AI_MODELS_ROUTE,
    },
    {
      apiKey: 'activity',
      icon: <HistoryOutlined />,
      key: 'activity',
      label: t('pages.ai.shell.navigation.activity', 'Activity'),
      path: AI_ACTIVITY_ROUTE,
    },
  ];
  const navigationItems: AIWorkspaceNavigationItem[] = navigationCandidates
    .filter((item) => {
      const feature = context?.features[item.key];
      return (
        context?.pages[item.key] === item.path &&
        feature?.availability === 'available' &&
        feature.page === item.path &&
        (!item.apiKey ||
          (context.apis[item.apiKey] === feature.api && Boolean(feature.api)))
      );
    })
    .map((item) => ({
      icon: item.icon,
      label: item.label,
      path: item.path,
    }));
  const activeNavigationPath = navigationItems.find(
    (item) =>
      pathname === item.path ||
      (item.path !== AI_OVERVIEW_ROUTE && pathname.startsWith(`${item.path}/`)),
  )?.path;
  const accountLabel = readAccountLabel(session);
  const scopeId = context?.scopeId;

  React.useEffect(() => {
    activeNavigationLinkRef.current?.scrollIntoView?.({
      block: 'nearest',
      inline: 'nearest',
    });
  }, [activeNavigationPath]);

  return (
    <header className="ai-workspace-header">
      <div className="ai-workspace-brand">
        <span aria-hidden="true" className="ai-workspace-brand-icon">
          <RobotOutlined />
        </span>
        <Typography.Text className="ai-workspace-brand-label" strong>
          {t('pages.ai.shell.title', 'AI Workspace')}
        </Typography.Text>
      </div>

      <nav
        aria-label={t(
          'pages.ai.shell.navigation.label',
          'AI workspace navigation',
        )}
        className="ai-workspace-navigation"
      >
        {navigationItems.map((item) => {
          const active = item.path === activeNavigationPath;
          return (
            <a
              aria-current={active ? 'page' : undefined}
              className={`ai-workspace-navigation-link${
                active ? ' ai-workspace-navigation-link-active' : ''
              }`}
              href={item.path}
              key={item.path}
              ref={active ? activeNavigationLinkRef : undefined}
              onClick={(event) => {
                if (!isPlainPrimaryClick(event)) {
                  return;
                }

                event.preventDefault();
                history.push(item.path);
              }}
            >
              {item.icon}
              <span>{item.label}</span>
            </a>
          );
        })}
      </nav>

      <div className="ai-workspace-account-context">
        {accountLabel ? (
          <Typography.Text className="ai-workspace-account-name" ellipsis>
            {accountLabel}
          </Typography.Text>
        ) : null}
        <Typography.Text
          className="ai-workspace-scope-label"
          ellipsis={{ tooltip: scopeId || undefined }}
        >
          {scopeId
            ? t('pages.ai.shell.scope', 'Scope {scopeId}', { scopeId })
            : t('pages.ai.shell.scope.loading', 'Resolving scope')}
        </Typography.Text>
      </div>
    </header>
  );
}

export function useAIWorkspaceContext(): AIWorkspaceContextValue {
  const context = React.useContext(AIWorkspaceReactContext);
  if (!context) {
    throw new Error(
      'useAIWorkspaceContext must be used inside AIWorkspaceShell.',
    );
  }

  return context;
}

export const AIWorkspaceShell: React.FC<AIWorkspaceShellProps> = ({
  children,
}) => {
  const queryAuthority = readAIWorkspaceSessionAuthority();
  const contextQuery = useQuery({
    queryFn: ({ signal }) => aiWorkspaceApi.getContext(signal),
    queryKey: aiWorkspaceQueryKeys.context(queryAuthority),
    retry: false,
  });
  const context = contextQuery.data;
  const scopeId = context?.scopeId ?? '';
  const authenticationRequired =
    contextQuery.error instanceof AIWorkspaceApiError &&
    (contextQuery.error.status === 401 || contextQuery.error.status === 403);

  let content: React.ReactNode;
  if (contextQuery.isLoading) {
    content = (
      <AevatarPageLoading
        ariaLabel={t(
          'pages.ai.shell.loading.ariaLabel',
          'Loading AI workspace',
        )}
        tip={t(
          'pages.ai.shell.loading.description',
          'Loading the authenticated workspace scope',
        )}
      />
    );
  } else if (authenticationRequired) {
    content = (
      <div className="ai-workspace-boundary">
        <InventoryReadinessState
          description={t(
            'pages.ai.shell.auth.required.description',
            'Sign in with an account that has access to an Aevatar scope.',
          )}
          kind="error"
          title={t(
            'pages.ai.shell.auth.required.title',
            'Authentication required',
          )}
        />
      </div>
    );
  } else if (contextQuery.isError) {
    content = (
      <div className="ai-workspace-boundary">
        <InventoryReadinessState
          action={{
            label: t('pages.ai.shell.retry', 'Retry'),
            onClick: () => void contextQuery.refetch(),
          }}
          description={describeError(
            contextQuery.error,
            t(
              'pages.ai.shell.auth.error.description',
              'The authenticated AI workspace context could not be loaded.',
            ),
          )}
          kind="error"
          title={t(
            'pages.ai.shell.auth.error.title',
            'AI workspace unavailable',
          )}
        />
      </div>
    );
  } else if (!scopeId) {
    content = (
      <div className="ai-workspace-boundary">
        <InventoryReadinessState
          action={{
            label: t('pages.ai.shell.retry', 'Retry'),
            onClick: () => void contextQuery.refetch(),
          }}
          description={t(
            'pages.ai.shell.scope.empty.description',
            'This account is authenticated, but no authorized scope was returned.',
          )}
          kind="empty"
          title={t('pages.ai.shell.scope.empty.title', 'No AI workspace scope')}
        />
      </div>
    );
  } else if (context) {
    content = (
      <AIWorkspaceReactContext.Provider
        value={{ context, queryAuthority, scopeId }}
      >
        {children}
      </AIWorkspaceReactContext.Provider>
    );
  }

  return (
    <div className="ai-workspace-shell">
      <AIWorkspaceHeader context={context} />
      <div className="ai-workspace-body">{content}</div>
    </div>
  );
};

export default AIWorkspaceShell;
