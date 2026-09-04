import {
  LogoutOutlined,
  SafetyCertificateOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Avatar, Button, Empty, Space, Typography, theme } from 'antd';
import React, { useMemo, useState } from 'react';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
} from '@/shared/auth/session';
import { t } from '@/shared/i18n/messages';
import { studioApi } from '@/shared/studio/api';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { AevatarCompactText } from '@/shared/ui/compactText';
import {
  summaryFieldGridStyle,
  summaryMetricGridStyle,
} from '@/shared/ui/proComponents';
import { buildSettingsPanelStyle, SummaryField, SummaryMetric } from './shared';

const LEGACY_ACCOUNT_SETTINGS_HREF = '/settings?section=account';

function formatSessionExpiry(value?: number): string {
  if (!value) {
    return 'Unavailable';
  }

  return new Intl.DateTimeFormat('zh-CN', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(value);
}

function formatIsoSessionExpiry(value?: string | null): string {
  if (!value) {
    return 'Unavailable';
  }

  const parsed = Date.parse(value);
  if (Number.isNaN(parsed)) {
    return 'Unavailable';
  }

  return formatSessionExpiry(parsed);
}

type AccountSettingsContentProps = {
  readonly showInlineSignOut?: boolean;
};

const AccountSettingsContent: React.FC<AccountSettingsContentProps> = ({
  showInlineSignOut = true,
}) => {
  const { token } = theme.useToken();
  const settingsPanelStyle = buildSettingsPanelStyle(token);
  const authSession = useMemo(() => loadRestorableAuthSession(), []);
  const authConfig = useMemo(() => getNyxIDRuntimeConfig(), []);
  const toast = useConsoleToast();
  const [serviceAccessReviewPending, setServiceAccessReviewPending] =
    useState(false);
  const authMeQuery = useQuery({
    queryKey: ['settings', 'auth-me'],
    queryFn: () => studioApi.getAuthSession(),
  });
  const backendProfile = authMeQuery.data?.profile;
  const backendSession = authMeQuery.data?.session;
  const authenticated = Boolean(authMeQuery.data?.authenticated || authSession);

  const accountDisplayName = useMemo(
    () =>
      backendProfile?.name ||
      backendProfile?.email ||
      backendProfile?.subject ||
      authSession?.user.name ||
      authSession?.user.email ||
      authSession?.user.sub ||
      'No active session',
    [authSession, backendProfile],
  );
  const accountSecondaryText = useMemo(() => {
    if (!authenticated) {
      return 'This browser does not have a restorable sign-in session.';
    }

    if (backendProfile?.email || backendProfile?.subject) {
      return backendProfile.email || backendProfile.subject;
    }

    return authSession?.user.email || authSession?.user.sub || 'Unavailable';
  }, [authSession, authenticated, backendProfile]);
  const rolesLabel =
    (backendProfile?.roles && backendProfile.roles.length > 0
      ? backendProfile.roles.join(', ')
      : authSession?.user.roles?.join(', ')) || 'No roles';
  const groupsLabel =
    (backendProfile?.groups && backendProfile.groups.length > 0
      ? backendProfile.groups.join(', ')
      : authSession?.user.groups?.join(', ')) || 'No groups';
  const userId = backendProfile?.subject || authSession?.user.sub || '';
  const picture = backendProfile?.picture || authSession?.user.picture;
  const emailVerified =
    backendProfile?.emailVerified ?? authSession?.user.email_verified ?? null;

  const handleSignOut = () => {
    clearStoredAuthSession();
    window.location.replace('/login');
  };

  const startServiceAccessReview = async () => {
    try {
      setServiceAccessReviewPending(true);
      const client = new NyxIDAuthClient(authConfig);
      await client.loginWithRedirect({
        flow: 'serviceAccessReview',
        returnTo: LEGACY_ACCOUNT_SETTINGS_HREF,
      });
    } catch {
      setServiceAccessReviewPending(false);
      toast.error(
        t(
          'pages.settings.accountcontent.service.access.review.start.failed',
          'Could not start service access review. Try again.',
        ),
      );
    }
  };

  return (
    <>
      <AevatarPanel
        extra={
          authenticated && showInlineSignOut ? (
            <Button danger icon={<LogoutOutlined />} onClick={handleSignOut}>
              {t('pages.settings.accountcontent.sign.out', 'Sign out')}
            </Button>
          ) : null
        }
        style={settingsPanelStyle}
        title="Profile"
      >
        {authenticated ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Space align="start" size={14}>
              <Avatar icon={<UserOutlined />} size={52} src={picture} />
              <div style={{ minWidth: 0 }}>
                <Typography.Text
                  strong
                  style={{ display: 'block', fontSize: 18 }}
                >
                  {accountDisplayName}
                </Typography.Text>
                <Typography.Text type="secondary">
                  {accountSecondaryText}
                </Typography.Text>
              </div>
            </Space>

            <div style={summaryMetricGridStyle}>
              <SummaryMetric
                label="Session"
                tone={
                  backendSession?.authenticated || authSession
                    ? 'success'
                    : 'warning'
                }
                value={
                  backendSession?.authenticated || authSession
                    ? 'Active'
                    : 'Browser only'
                }
              />
              <SummaryMetric
                label="Email"
                tone={emailVerified ? 'success' : 'warning'}
                value={emailVerified ? 'Verified' : 'Needs review'}
              />
            </div>

            <div style={summaryFieldGridStyle}>
              <SummaryField
                label={t('pages.settings.accountcontent.user.id', 'User ID')}
                value={
                  <AevatarCompactText
                    copyable
                    head={8}
                    maxWidth="100%"
                    monospace
                    tail={6}
                    value={userId}
                  />
                }
              />
              <SummaryField label="Roles" value={rolesLabel} />
              <SummaryField label="Groups" value={groupsLabel} />
            </div>
          </div>
        ) : (
          <Empty
            description={t(
              'pages.settings.accountcontent.this.browser.does.not.have',
              'This browser does not have a restorable sign-in session.',
            )}
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          >
            <Button
              type="primary"
              onClick={() => window.location.replace('/login')}
            >
              {t('pages.settings.accountcontent.sign.in', 'Sign in')}
            </Button>
          </Empty>
        )}
      </AevatarPanel>

      <AevatarPanel
        extra={
          authenticated ? (
            <Button
              icon={<SafetyCertificateOutlined />}
              loading={serviceAccessReviewPending}
              onClick={() => void startServiceAccessReview()}
            >
              {t(
                'pages.settings.accountcontent.manage.service.access',
                'Manage service access',
              )}
            </Button>
          ) : null
        }
        style={settingsPanelStyle}
        title="Authentication"
      >
        <Space orientation="vertical" size={12} style={{ width: '100%' }}>
          <div style={summaryFieldGridStyle}>
            <SummaryField
              label={t(
                'pages.settings.accountcontent.session.expires',
                'Session expires',
              )}
              value={
                backendSession?.expiresAtUtc
                  ? formatIsoSessionExpiry(backendSession.expiresAtUtc)
                  : formatSessionExpiry(authSession?.tokens.expiresAt)
              }
            />
            <SummaryField
              label="Provider"
              value={authMeQuery.data?.providerDisplayName || 'Unavailable'}
            />
            <SummaryField
              label="Scope"
              value={
                authMeQuery.data?.scopeId ||
                authSession?.tokens.scope ||
                'Unavailable'
              }
            />
            <SummaryField
              label={t(
                'pages.settings.accountcontent.browser.token.refresh',
                'Browser token refresh',
              )}
              value={t(
                'pages.settings.accountcontent.browser.token.refresh.disabled',
                'Disabled',
              )}
            />
          </div>
        </Space>
      </AevatarPanel>
    </>
  );
};

export default AccountSettingsContent;
