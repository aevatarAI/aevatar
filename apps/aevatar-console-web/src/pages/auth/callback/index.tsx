import { PageLoading } from '@ant-design/pro-components';
import { Button, Result } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import { useModel } from '@umijs/max';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';

const CallbackPage: React.FC = () => {
  const { initialState, setInitialState } = useModel('@@initialState');
  const [errorText, setErrorText] = useState<string | undefined>(undefined);
  const config = useMemo(() => getNyxIDRuntimeConfig(), []);

  useEffect(() => {
    let cancelled = false;

    const finishLogin = async () => {
      try {
        const client = new NyxIDAuthClient(config);
        const result = await client.handleRedirectCallback();
        if (cancelled) {
          return;
        }

        setInitialState((current) =>
          current
            ? {
                ...current,
                auth: {
                  enabled: true,
                  isAuthenticated: true,
                  config,
                  session: result.session,
                },
              }
            : current,
        );
        window.location.replace(result.returnTo);
      } catch (error) {
        if (cancelled) {
          return;
        }

        setErrorText(error instanceof Error ? error.message : String(error));
      }
    };

    if (!initialState?.auth?.isAuthenticated) {
      void finishLogin();
    } else {
      window.location.replace('/overview');
    }

    return () => {
      cancelled = true;
    };
  }, [config, initialState?.auth?.isAuthenticated, setInitialState]);

  if (errorText) {
    return (
      <Result
        extra={[
          <Button href="/login" key="retry" type="primary">
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
