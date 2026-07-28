import {
  DownOutlined,
  GlobalOutlined,
  LoginOutlined,
  LogoutOutlined,
  SettingOutlined,
  UserOutlined,
} from "@ant-design/icons";
import { getLocale, setLocale, useIntl } from "@umijs/max";
import { Avatar, Button, Dropdown, Typography } from "antd";
import React from "react";
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
  sanitizeReturnTo,
} from "@/shared/auth/session";
import { normalizeConsoleLocale } from "@/shared/i18n/localeProvider";
import { history } from "@/shared/navigation/history";

type ConsoleLocaleOption = {
  readonly key: "zh-CN" | "en-US";
  readonly messageId: "common.language.zhCN" | "common.language.english";
};

const CONSOLE_LOCALE_OPTIONS: readonly ConsoleLocaleOption[] = [
  { key: "zh-CN", messageId: "common.language.zhCN" },
  { key: "en-US", messageId: "common.language.english" },
];

function getCurrentReturnTo(): string {
  if (typeof window === "undefined") {
    return "/";
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

const consoleHeaderActionsCss = `
.console-header-actions {
  align-items: center;
  display: flex;
  flex: 0 0 auto;
  flex-wrap: nowrap;
  gap: 8px;
  max-width: 100%;
  min-width: 0;
}

@media (max-width: 480px) {
  .console-header-actions {
    --console-header-action-flex: 0 0 44px;
    --console-header-action-height: 44px;
    --console-header-action-min-width: 44px;
    --console-header-action-padding-inline: 0;
    --console-header-action-width: 44px;
    --console-header-user-padding: 0;
    gap: 4px;
  }

  .console-header-actions__language-label,
  .console-header-actions__login-label,
  .console-header-actions__user-name,
  .console-header-actions__user-caret {
    display: none;
  }
}
`;

const compactActionStyle: React.CSSProperties = {
  boxSizing: "border-box",
  flex: "var(--console-header-action-flex, 0 1 auto)",
  height: "var(--console-header-action-height, 36px)",
  justifyContent: "center",
  minWidth: "var(--console-header-action-min-width, auto)",
  paddingInline: "var(--console-header-action-padding-inline)",
  width: "var(--console-header-action-width, auto)",
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
          const nextLocale = key === "en-US" ? "en-US" : "zh-CN";
          if (nextLocale === selectedLocale) {
            return;
          }

          setLocale(nextLocale, false);
        },
        selectedKeys: [selectedLocale],
      }}
      placement="bottomRight"
      trigger={["click"]}
    >
      <Button
        aria-label={intl.formatMessage({ id: "common.language.switch" })}
        className="console-header-actions__language"
        icon={<GlobalOutlined />}
        style={{
          ...compactActionStyle,
          alignItems: "center",
          display: "inline-flex",
        }}
        type="text"
      >
        <span
          className="console-header-actions__language-label"
          data-compact-label="hidden"
        >
          {intl.formatMessage({ id: selectedOption.messageId })}
        </span>
      </Button>
    </Dropdown>
  );
};

export const ConsoleAuthActions: React.FC<ConsoleHeaderActionThemeProps> = ({
  dropdownRootClassName,
}) => {
  const intl = useIntl();
  const session = loadRestorableAuthSession();
  if (!session) {
    return (
      <Button
        className="console-header-actions__login"
        icon={<LoginOutlined />}
        onClick={() => {
          history.push(buildLoginRoute(getCurrentReturnTo()));
        }}
        style={compactActionStyle}
        type="link"
      >
        <span className="console-header-actions__login-label" data-compact-label="hidden">
          {intl.formatMessage({
            defaultMessage: "Sign in",
            id: "common.user.signIn",
          })}
        </span>
      </Button>
    );
  }

  const displayName =
    session.user.name || session.user.email || session.user.sub;

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
            key: "settings",
            icon: <SettingOutlined />,
            label: intl.formatMessage({ id: "common.user.settings" }),
          },
          {
            key: "logout",
            icon: <LogoutOutlined />,
            label: intl.formatMessage({ id: "common.user.logout" }),
          },
        ],
        onClick: ({ key }) => {
          if (key === "settings") {
            history.push("/settings");
            return;
          }

          if (key === "logout") {
            clearStoredAuthSession();
            window.location.replace("/login");
          }
        },
      }}
      placement="bottomRight"
      trigger={["click"]}
    >
      <span
        className="console-header-actions__user"
        style={{
          ...compactActionStyle,
          alignItems: "center",
          background: "var(--ant-color-fill-tertiary)",
          border: "1px solid var(--ant-color-border-secondary)",
          borderRadius: 999,
          cursor: "pointer",
          display: "inline-flex",
          gap: 8,
          maxWidth: 220,
          padding: "var(--console-header-user-padding, 0 10px 0 6px)",
        }}
        title={displayName}
      >
        <Avatar
          icon={<UserOutlined />}
          size={24}
          src={session.user.picture}
        />
        <Typography.Text
          className="console-header-actions__user-name"
          data-compact-label="hidden"
          style={{
            flex: 1,
            color: "var(--ant-color-text)",
            lineHeight: "20px",
            marginBottom: 0,
            maxWidth: 160,
            minWidth: 0,
            whiteSpace: "nowrap",
          }}
          ellipsis={{ tooltip: displayName }}
        >
          {displayName}
        </Typography.Text>
        <DownOutlined
          className="console-header-actions__user-caret"
          style={{
            color: "var(--ant-color-text-tertiary)",
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
  const rootClassName = ["console-header-actions", className]
    .filter(Boolean)
    .join(" ");

  return (
    <>
      <style>{consoleHeaderActionsCss}</style>
      <div
        className={rootClassName}
        data-dropdown-root-class-name={dropdownRootClassName}
        data-responsive-layout="compact-header-actions"
      >
        <ConsoleLanguageSwitch dropdownRootClassName={dropdownRootClassName} />
        <ConsoleAuthActions dropdownRootClassName={dropdownRootClassName} />
      </div>
    </>
  );
};
