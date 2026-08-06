import {
  LoginOutlined,
  LogoutOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { Alert, Avatar, Button, Descriptions, Space, Typography } from 'antd';
import React from 'react';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import {
  clearStoredAuthSession,
  sanitizeReturnTo,
} from '@/shared/auth/session';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { AevatarCompactText } from '@/shared/ui/compactText';
import TechnicalDetails from '../TechnicalDetails';
import type { AccountField, AccountIdentity } from './accountIdentity';

type AccountPanelProps = {
  readonly identity: AccountIdentity;
  readonly onRefresh: () => void;
  readonly returnTo: string;
};

function accountFieldValue(field: AccountField): React.ReactNode {
  if (field.kind === 'value') return field.value;
  return t('workflowActivityVNext.settings.notProvided', 'Not provided');
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

const AccountPanel: React.FC<AccountPanelProps> = ({
  identity,
  onRefresh,
  returnTo,
}) => {
  const [reviewPending, setReviewPending] = React.useState(false);
  const [reviewError, setReviewError] = React.useState('');
  const recoverable =
    identity.sessionState === 'expired' || identity.sessionState === 'invalid';
  const displayName = accountFieldValue(identity.displayName);

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
      setReviewError('');
      await new NyxIDAuthClient(getNyxIDRuntimeConfig()).loginWithRedirect({
        flow: 'serviceAccessReview',
        returnTo,
      });
    } catch {
      setReviewPending(false);
      setReviewError(
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
        <Avatar icon={<UserOutlined />} size={56} src={identity.picture} />
        <div>
          <Typography.Title level={3}>{displayName}</Typography.Title>
          <Typography.Text type="secondary">
            {accountFieldValue(identity.email)}
          </Typography.Text>
        </div>
      </div>

      <Descriptions
        bordered
        column={{ xs: 1, sm: 2, md: 2, lg: 2, xl: 2, xxl: 2 }}
        items={[
          {
            key: 'session',
            label: t(
              'workflowActivityVNext.settings.sessionState',
              'Session state',
            ),
            children: sessionStateLabel(identity),
          },
          {
            key: 'scope',
            label: t(
              'workflowActivityVNext.settings.workspaceContext',
              'Workspace context',
            ),
            children: accountFieldValue(identity.scope),
          },
          {
            key: 'expiry',
            label: t('workflowActivityVNext.settings.expires', 'Expires'),
            children: accountFieldValue(identity.expiry),
          },
          {
            key: 'access',
            label: t(
              'workflowActivityVNext.settings.productAccess',
              'Product access',
            ),
            children: t(
              'workflowActivityVNext.settings.accessNotLoaded',
              'Not loaded',
            ),
          },
        ]}
      />

      <Space wrap>
        {recoverable ? (
          <Button icon={<LoginOutlined />} onClick={signInAgain} type="primary">
            {t('workflowActivityVNext.settings.signInAgain', 'Sign in again')}
          </Button>
        ) : (
          <>
            <Button icon={<ReloadOutlined />} onClick={onRefresh}>
              {t(
                'workflowActivityVNext.settings.refreshStatus',
                'Refresh status',
              )}
            </Button>
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
            <Button danger icon={<LogoutOutlined />} onClick={signOut}>
              {t('workflowActivityVNext.settings.signOut', 'Sign out')}
            </Button>
          </>
        )}
      </Space>

      {reviewError ? (
        <Alert message={reviewError} role="alert" showIcon type="error" />
      ) : null}

      <TechnicalDetails
        summary={t(
          'workflowActivityVNext.settings.supportDetails',
          'Support details',
        )}
      >
        <Descriptions
          column={1}
          items={[
            {
              key: 'provider',
              label: t(
                'workflowActivityVNext.settings.provider',
                'Sign-in method',
              ),
              children:
                identity.provider.kind === 'value' ? (
                  <Typography.Text
                    copyable={{ text: identity.provider.value }}
                  >
                    {identity.provider.value}
                  </Typography.Text>
                ) : (
                  accountFieldValue(identity.provider)
                ),
            },
            ...(identity.support.subject
              ? [
                  {
                    key: 'subject',
                    label: t(
                      'workflowActivityVNext.settings.userId',
                      'User ID',
                    ),
                    children: (
                      <AevatarCompactText
                        copyable
                        head={12}
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
                    label: t(
                      'workflowActivityVNext.settings.roles',
                      'Access claims',
                    ),
                    children: (
                      <Typography.Text
                        copyable={{ text: identity.support.roles.join(', ') }}
                      >
                        {identity.support.roles.join(', ')}
                      </Typography.Text>
                    ),
                  },
                ]
              : []),
            ...(identity.support.groups.length
              ? [
                  {
                    key: 'groups',
                    label: t(
                      'workflowActivityVNext.settings.groups',
                      'Group claims',
                    ),
                    children: (
                      <Typography.Text
                        copyable={{ text: identity.support.groups.join(', ') }}
                      >
                        {identity.support.groups.join(', ')}
                      </Typography.Text>
                    ),
                  },
                ]
              : []),
          ]}
        />
      </TechnicalDetails>

      <Typography.Paragraph
        className="wa-vnext__account-contract-note"
        type="secondary"
      >
        {t(
          'workflowActivityVNext.settings.capabilityContractMissing',
          'Capability details are not provided by the current account service.',
        )}
      </Typography.Paragraph>
    </div>
  );
};

export default AccountPanel;
