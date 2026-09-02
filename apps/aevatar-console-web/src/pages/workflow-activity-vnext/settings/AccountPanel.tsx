import {
  LoginOutlined,
  LogoutOutlined,
  SafetyCertificateOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { Avatar, Button, Descriptions, Space, Typography } from 'antd';
import React from 'react';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import {
  clearStoredAuthSession,
  sanitizeReturnTo,
} from '@/shared/auth/session';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { AevatarCompactText } from '@/shared/ui/compactText';
import type { AccountField, AccountIdentity } from './accountIdentity';

type AccountPanelProps = {
  readonly identity: AccountIdentity;
  readonly returnTo: string;
};

function accountFieldValue(field: AccountField): string | null {
  return field.kind === 'value' ? field.value : null;
}

function accountInitials(displayName: string): string {
  const parts = displayName.split(/\s+/).filter(Boolean);
  if (parts.length > 1) {
    return parts
      .slice(0, 2)
      .map((part) => Array.from(part)[0])
      .join('')
      .toUpperCase();
  }
  return Array.from(parts[0] ?? '')
    .slice(0, 2)
    .join('')
    .toUpperCase();
}

function sessionStateLabel(identity: AccountIdentity): string {
  switch (identity.sessionState) {
    case 'active':
      return t('workflowActivityVNext.settings.sessionActive', 'Active');
    case 'expiring_soon':
      return t(
        'workflowActivityVNext.settings.sessionExpiringSoon',
        'Expiring soon',
      );
    case 'expired':
      return t('workflowActivityVNext.settings.sessionExpired', 'Expired');
    case 'invalid':
      return t('workflowActivityVNext.settings.sessionInvalid', 'Invalid');
  }
}

function buildLoginHref(returnTo: string): string {
  return `/login?${new URLSearchParams({
    redirect: sanitizeReturnTo(returnTo),
  }).toString()}`;
}

const AccountPanel: React.FC<AccountPanelProps> = ({ identity, returnTo }) => {
  const toast = useConsoleToast();
  const [reviewPending, setReviewPending] = React.useState(false);
  const recoverable =
    identity.sessionState === 'expired' || identity.sessionState === 'invalid';
  const displayName = accountFieldValue(identity.displayName);
  const email = accountFieldValue(identity.email);
  const provider = accountFieldValue(identity.provider);
  const scope = accountFieldValue(identity.scope);
  const expiry = accountFieldValue(identity.expiry);
  const identityDetails = [
    ...(identity.support.subject
      ? [
          {
            key: 'subject',
            label: t('workflowActivityVNext.settings.userId', 'User ID'),
            children: (
              <AevatarCompactText
                copyable
                head={12}
                hideMachineIdentifier={false}
                monospace
                tail={8}
                value={identity.support.subject}
              />
            ),
          },
        ]
      : []),
    ...(identity.support.roles.length
      ? [
          {
            key: 'roles',
            label: t('workflowActivityVNext.settings.roles', 'Roles'),
            children: identity.support.roles.join(', '),
          },
        ]
      : []),
    ...(identity.support.groups.length
      ? [
          {
            key: 'groups',
            label: t('workflowActivityVNext.settings.groups', 'Groups'),
            children: identity.support.groups.join(', '),
          },
        ]
      : []),
    ...(email && identity.emailVerified !== null
      ? [
          {
            key: 'email-verification',
            label: t(
              'workflowActivityVNext.settings.emailVerification',
              'Email verification',
            ),
            children: identity.emailVerified
              ? t('workflowActivityVNext.settings.verified', 'Verified')
              : t('workflowActivityVNext.settings.notVerified', 'Not verified'),
          },
        ]
      : []),
  ];
  const sessionDetails = [
    {
      key: 'session',
      label: t('workflowActivityVNext.settings.sessionState', 'Session state'),
      children: sessionStateLabel(identity),
    },
    ...(provider
      ? [
          {
            key: 'provider',
            label: t(
              'workflowActivityVNext.settings.provider',
              'Sign-in method',
            ),
            children: provider,
          },
        ]
      : []),
    ...(scope
      ? [
          {
            key: 'scope',
            label: t('workflowActivityVNext.settings.scope', 'Scope'),
            children: scope,
          },
        ]
      : []),
    ...(expiry
      ? [
          {
            key: 'expiry',
            label: t('workflowActivityVNext.settings.expires', 'Expires'),
            children: expiry,
          },
        ]
      : []),
  ];

  const signInAgain = () => {
    clearStoredAuthSession();
    history.push(buildLoginHref(returnTo));
  };

  const signOut = () => {
    clearStoredAuthSession();
    window.location.replace('/login');
  };

  const reviewServiceAccess = async () => {
    try {
      setReviewPending(true);
      await new NyxIDAuthClient(getNyxIDRuntimeConfig()).loginWithRedirect({
        flow: 'serviceAccessReview',
        returnTo,
      });
    } catch {
      setReviewPending(false);
      toast.error(
        t(
          'workflowActivityVNext.settings.serviceAccessFailed',
          'Could not start service access review. Try again.',
        ),
      );
    }
  };

  return (
    <div className="wa-vnext__account">
      <div className="wa-vnext__account-profile">
        <div className="wa-vnext__account-profile-identity">
          <Avatar
            icon={displayName ? undefined : <UserOutlined />}
            size={56}
            src={identity.picture}
          >
            {displayName ? accountInitials(displayName) : null}
          </Avatar>
          <div>
            <Typography.Title level={3}>
              {displayName ||
                t('workflowActivityVNext.settings.authenticated', 'Signed in')}
            </Typography.Title>
            <Typography.Text type="secondary">
              {email ||
                t(
                  'workflowActivityVNext.settings.profileUnavailable',
                  'Profile details are unavailable.',
                )}
            </Typography.Text>
          </div>
        </div>
        {!recoverable ? (
          <Button danger icon={<LogoutOutlined />} onClick={signOut}>
            {t('workflowActivityVNext.settings.signOut', 'Sign out')}
          </Button>
        ) : null}
      </div>

      {identityDetails.length ? (
        <section className="wa-vnext__account-section">
          <h3>{t('workflowActivityVNext.settings.profile', 'Profile')}</h3>
          <Descriptions
            bordered
            column={{ xs: 1, sm: 2, md: 2, lg: 2, xl: 2, xxl: 2 }}
            items={identityDetails}
          />
        </section>
      ) : null}

      <section className="wa-vnext__account-section">
        <div className="wa-vnext__account-section-heading">
          <h3>
            {t(
              'workflowActivityVNext.settings.sessionAccess',
              'Session & access',
            )}
          </h3>
          {!recoverable ? (
            <Button
              icon={<SafetyCertificateOutlined />}
              loading={reviewPending}
              onClick={() => void reviewServiceAccess()}
            >
              {t(
                'workflowActivityVNext.settings.manageServiceAccess',
                'Manage service access',
              )}
            </Button>
          ) : null}
        </div>
        <Descriptions
          bordered
          column={{ xs: 1, sm: 2, md: 2, lg: 2, xl: 2, xxl: 2 }}
          items={sessionDetails}
        />
        {recoverable ? (
          <Space className="wa-vnext__account-recovery" wrap>
            <Button
              icon={<LoginOutlined />}
              onClick={signInAgain}
              type="primary"
            >
              {t('workflowActivityVNext.settings.signInAgain', 'Sign in again')}
            </Button>
          </Space>
        ) : null}
      </section>
    </div>
  );
};

export default AccountPanel;
