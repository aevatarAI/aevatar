import { PageLoading } from '@ant-design/pro-components';
import { Button, Result } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { prefixAppBasePath, replaceAppLocation } from '@/shared/navigation/appPath';
import { loadStoredAuthSession } from '@/shared/auth/session';

const CallbackPage: React.FC = () => {
  const [errorText, setErrorText] = useState<string | undefined>(undefined);
  const config = useMemo(() => getNyxIDRuntimeConfig(), []);

  useEffect(() => {
    let cancelled = false;

    if (loadStoredAuthSession()) {
      replaceAppLocation('/overview');
      return () => {
        cancelled = true;
      };
    }

    const finishLogin = async () => {
      try {
        const client = new NyxIDAuthClient(config);
        const result = await client.handleRedirectCallback();
        if (cancelled) {
          return;
        }

        replaceAppLocation(result.returnTo);
      } catch (error) {
        if (cancelled) {
          return;
        }

        setErrorText(error instanceof Error ? error.message : String(error));
      }
    };

    void finishLogin();

    return () => {
      cancelled = true;
    };
  }, [config]);

  if (errorText) {
    return (
      <Result
        extra={[
          <Button href={prefixAppBasePath('/login')} key="retry" type="primary">
            Back to login
          </Button>,
        ]}
        status="error"
        subTitle={errorText}
        title="NyxID callback failed"
      />
    );
  }

  return <PageLoading fullscreen tip="Completing NyxID sign-in..." />;
};

export default CallbackPage;
