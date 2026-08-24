import {
  DownOutlined,
  GlobalOutlined,
  LoginOutlined,
  LogoutOutlined,
  SettingOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { getLocale, setLocale, useIntl } from '@umijs/max';
import { Avatar, Button, Dropdown, Typography } from 'antd';
import React from 'react';
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
  sanitizeReturnTo,
} from '@/shared/auth/session';
import { normalizeConsoleLocale } from '@/shared/i18n/localeProvider';
import { history } from '@/shared/navigation/history';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';

type ConsoleLocaleOption = {
  readonly key: 'zh-CN' | 'en-US';
  readonly messageId: 'common.language.zhCN' | 'common.language.english';
};

const CONSOLE_LOCALE_OPTIONS: readonly ConsoleLocaleOption[] = [
  { key: 'zh-CN', messageId: 'common.language.zhCN' },
  { key: 'en-US', messageId: 'common.language.english' },
];

function getCurrentReturnTo(): string {
  if (typeof window === 'undefined') {
    return '/';
  }

  return `${window.location.pathname}${window.location.search}${window.location.hash}`;
}

function buildLoginRoute(returnTo: string): string {
  const params = new URLSearchParams({
    redirect: sanitizeReturnTo(returnTo),
  });
  return `/login?${params.toString()}`;
}

type ConsoleHeaderActionThemeProps = {
  readonly dropdownRootClassName?: string;
};

type ConsoleAuthActionsProps = ConsoleHeaderActionThemeProps & {
  readonly principal?: {
    readonly authenticated: boolean;
    readonly displayName: string;
    readonly picture: string | null;
  } | null;
};

export const ConsoleLanguageSwitch: React.FC<ConsoleHeaderActionThemeProps> = ({
  dropdownRootClassName,
}) => {
  const intl = useIntl();
  const selectedLocale = normalizeConsoleLocale(intl.locale || getLocale());
  const selectedOption =
    CONSOLE_LOCALE_OPTIONS.find((option) => option.key === selectedLocale) ||
    CONSOLE_LOCALE_OPTIONS[0];

  return (
    <Dropdown
      classNames={
        dropdownRootClassName
          ? {
              root: dropdownRootClassName,
            }
          : undefined
      }
      menu={{
        items: CONSOLE_LOCALE_OPTIONS.map((option) => ({
          key: option.key,
          label: intl.formatMessage({ id: option.messageId }),
        })),
        onClick: ({ key }) => {
          const nextLocale = key === 'en-US' ? 'en-US' : 'zh-CN';
          if (nextLocale === selectedLocale) {
            return;
          }

          setLocale(nextLocale, false);
        },
        selectedKeys: [selectedLocale],
      }}
      placement="bottomRight"
      trigger={['click']}
    >
      <Button
        aria-label={intl.formatMessage({ id: 'common.language.switch' })}
        className="console-header-actions__language"
        icon={<GlobalOutlined />}
        style={{
          alignItems: 'center',
          display: 'inline-flex',
          height: 36,
        }}
        type="text"
      >
        {intl.formatMessage({ id: selectedOption.messageId })}
      </Button>
    </Dropdown>
  );
};

export const ConsoleAuthActions: React.FC<ConsoleAuthActionsProps> = ({
  dropdownRootClassName,
  principal,
}) => {
  const intl = useIntl();
  const storedSession = loadRestorableAuthSession();
  const hasAuthoritativePrincipal = principal !== undefined;
  const signedIn = hasAuthoritativePrincipal
    ? Boolean(principal?.authenticated)
    : Boolean(storedSession);
  if (!signedIn) {
    return (
      <Button
        className="console-header-actions__login"
        icon={<LoginOutlined />}
        onClick={() => {
          if (hasAuthoritativePrincipal && storedSession) {
            clearStoredAuthSession();
          }
          history.push(buildLoginRoute(getCurrentReturnTo()));
        }}
        type="link"
      >
        {intl.formatMessage({
          defaultMessage: 'Sign in',
          id: 'common.user.signIn',
        })}
      </Button>
    );
  }

  const displayName = hasAuthoritativePrincipal
    ? principal?.displayName ||
      intl.formatMessage({
        defaultMessage: 'Account',
        id: 'common.user.account',
      })
    : storedSession?.user.name ||
      storedSession?.user.email ||
      storedSession?.user.sub ||
      intl.formatMessage({
        defaultMessage: 'Account',
        id: 'common.user.account',
      });
  const picture = hasAuthoritativePrincipal
    ? principal?.picture
    : storedSession?.user.picture;

  return (
    <Dropdown
      classNames={
        dropdownRootClassName
          ? {
              root: dropdownRootClassName,
            }
          : undefined
      }
      menu={{
        items: [
          {
            key: 'settings',
            icon: <SettingOutlined />,
            label: intl.formatMessage({ id: 'common.user.settings' }),
          },
          {
            key: 'logout',
            icon: <LogoutOutlined />,
            label: intl.formatMessage({ id: 'common.user.logout' }),
          },
        ],
        onClick: ({ key }) => {
          if (key === 'settings') {
            history.push('/settings');
            return;
          }

          if (key === 'logout') {
            clearStoredAuthSession();
            window.location.replace('/login');
          }
        },
      }}
      placement="bottomRight"
      trigger={['click']}
    >
      <span
        className="console-header-actions__user"
        style={{
          alignItems: 'center',
          background: 'var(--ant-color-fill-tertiary)',
          border: '1px solid var(--ant-color-border-secondary)',
          borderRadius: 999,
          cursor: 'pointer',
          display: 'inline-flex',
          gap: 8,
          height: 36,
          maxWidth: 220,
          padding: '0 10px 0 6px',
        }}
      >
        <Avatar icon={<UserOutlined />} size={24} src={picture} />
        <AevatarTooltip title={displayName}>
          <Typography.Text
            className="console-header-actions__user-name"
            ellipsis
            style={{
              flex: 1,
              color: 'var(--ant-color-text)',
              lineHeight: '20px',
              marginBottom: 0,
              maxWidth: 160,
              minWidth: 0,
              whiteSpace: 'nowrap',
            }}
          >
            {displayName}
          </Typography.Text>
        </AevatarTooltip>
        <DownOutlined
          className="console-header-actions__user-caret"
          style={{
            color: 'var(--ant-color-text-tertiary)',
            fontSize: 11,
          }}
        />
      </span>
    </Dropdown>
  );
};

export const ConsoleHeaderActions: React.FC<{
  readonly className?: string;
  readonly dropdownRootClassName?: string;
}> = ({ className, dropdownRootClassName }) => {
  const rootClassName = ['console-header-actions', className]
    .filter(Boolean)
    .join(' ');

  return (
    <div
      className={rootClassName}
      data-dropdown-root-class-name={dropdownRootClassName}
    >
      <ConsoleLanguageSwitch dropdownRootClassName={dropdownRootClassName} />
      <ConsoleAuthActions dropdownRootClassName={dropdownRootClassName} />
    </div>
  );
};
