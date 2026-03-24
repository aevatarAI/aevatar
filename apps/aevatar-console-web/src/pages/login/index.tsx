import { LockOutlined } from '@ant-design/icons';
import { Button, Card, Result, Space, Typography } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import BrandLogo from '@/components/BrandLogo';
import {
  ensureActiveAuthSession,
  hasRestorableAuthSession,
  NyxIDAuthClient,
} from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { replaceAppLocation } from '@/shared/navigation/appPath';
import {
  sanitizeReturnTo,
} from '@/shared/auth/session';

const pageStyle: React.CSSProperties = {
  minHeight: '100vh',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  padding: 24,
  background:
    'linear-gradient(135deg, rgba(22,119,255,0.08) 0%, rgba(245,247,250,1) 45%, rgba(22,119,255,0.16) 100%)',
};

const cardStyle: React.CSSProperties = {
  width: '100%',
  maxWidth: 520,
  borderRadius: 20,
  boxShadow: '0 24px 64px rgba(15, 23, 42, 0.12)',
};

const brandBlockStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  width: '100%',
};

const LoginPage: React.FC = () => {
  const [pending, setPending] = useState(false);
  const [errorText, setErrorText] = useState<string | undefined>(undefined);
  const config = useMemo(() => getNyxIDRuntimeConfig(), []);
  const redirectTarget = useMemo(() => {
    if (typeof window === 'undefined') {
      return '/overview';
    }

    const params = new URLSearchParams(window.location.search);
    return sanitizeReturnTo(params.get('redirect'));
  }, []);

  useEffect(() => {
    if (!hasRestorableAuthSession()) {
      return;
    }

    let cancelled = false;
    void ensureActiveAuthSession(config).then((session) => {
      if (cancelled || !session) {
        return;
      }

      replaceAppLocation(redirectTarget);
    });

    return () => {
      cancelled = true;
    };
  }, [config, redirectTarget]);

  const startLogin = async () => {
    try {
      setPending(true);
      setErrorText(undefined);
      const client = new NyxIDAuthClient(config);
      await client.loginWithRedirect({
        returnTo: redirectTarget,
      });
    } catch (error) {
      setPending(false);
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  };

  if (!config.enabled) {
    return (
      <div style={pageStyle}>
        <Card style={cardStyle}>
          <Result
            status="warning"
            title={
              config.configurationError
                ? 'NyxID login configuration is invalid'
                : 'NyxID login is not configured'
            }
            subTitle={
              config.configurationError
                ? config.configurationError
                : 'Set NYXID_CLIENT_ID and NYXID_BASE_URL in apps/aevatar-console-web/.env.local before starting the console.'
            }
          />
        </Card>
      </div>
    );
  }

  return (
    <div style={pageStyle}>
      <Card style={cardStyle}>
        <Space
          direction="vertical"
          size={24}
          style={{ width: '100%', textAlign: 'center' }}
        >
          <div style={brandBlockStyle}>
            <BrandLogo size={52} />
            <Typography.Title level={2} style={{ marginBottom: 0 }}>
              Aevatar Console
            </Typography.Title>
          </div>
          <Space direction="vertical" size={8} style={{ width: '100%' }}>
            <Typography.Title level={4} style={{ marginBottom: 0 }}>
              Sign in with NyxID
            </Typography.Title>
            <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
              Authenticate with NyxID to access workflows, runs, actors, and
              configuration surfaces.
            </Typography.Paragraph>
          </Space>
          <Button
            icon={<LockOutlined />}
            loading={pending}
            onClick={() => void startLogin()}
            size="large"
            type="primary"
          >
            Continue with NyxID
          </Button>
          {errorText ? (
            <Typography.Text type="danger">{errorText}</Typography.Text>
          ) : null}
        </Space>
      </Card>
    </div>
  );
};

export default LoginPage;
