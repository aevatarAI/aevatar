import { Button, Result } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import {
  NyxIDAuthClient,
  SERVICE_ACCESS_REVIEW_RETURN_TO,
  type AuthFlow,
} from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { loadStoredAuthSession } from '@/shared/auth/session';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';
import { describeError } from '@/shared/ui/errorText';
import { t } from "@/shared/i18n/messages";

type CallbackErrorState = {
  readonly flow: AuthFlow;
  readonly message: string;
  readonly returnTo: string;
};

function readCallbackErrorState(error: unknown): CallbackErrorState {
  const record =
    error && typeof error === "object" && !Array.isArray(error)
      ? (error as { flow?: unknown; returnTo?: unknown })
      : null;
  const flow =
    record?.flow === "serviceAccessReview" ? "serviceAccessReview" : "signIn";
  const fallbackReturnTo =
    flow === "serviceAccessReview" ? SERVICE_ACCESS_REVIEW_RETURN_TO : "/login";
  const returnTo =
    typeof record?.returnTo === "string" && record.returnTo.startsWith("/")
      ? record.returnTo
      : fallbackReturnTo;

  return {
    flow,
    message: describeError(error),
    returnTo,
  };
}

const CallbackPage: React.FC = () => {
  const [callbackError, setCallbackError] = useState<
    CallbackErrorState | undefined
  >(undefined);
  const [retrying, setRetrying] = useState(false);
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

        window.location.replace(result.returnTo);
      } catch (error) {
        if (cancelled) {
          return;
        }

        setCallbackError(readCallbackErrorState(error));
      }
    };

    const callbackParams = new URLSearchParams(window.location.search);
    const hasCallbackPayload =
      callbackParams.has('code') ||
      callbackParams.has('state') ||
      callbackParams.has('error');

    if (hasCallbackPayload || !loadStoredAuthSession()) {
      void finishLogin();
    } else {
      window.location.replace(CONSOLE_HOME_ROUTE);
    }

    return () => {
      cancelled = true;
    };
  }, [config]);

  const retryCallback = async () => {
    if (!callbackError) {
      return;
    }

    try {
      setRetrying(true);
      const client = new NyxIDAuthClient(config);
      await client.loginWithRedirect({
        flow: callbackError.flow,
        returnTo: callbackError.returnTo,
      });
    } catch (error) {
      setRetrying(false);
      setCallbackError({
        ...callbackError,
        message: describeError(error),
      });
    }
  };

  if (callbackError) {
    const isReviewFlow = callbackError.flow === "serviceAccessReview";
    return (
      <Result
        extra={[
          <Button
            key="retry"
            loading={retrying}
            onClick={() => void retryCallback()}
            type="primary"
          >
            {isReviewFlow
              ? t("pages.auth.callback.index.retry.service.access.review", "Retry service access review")
              : t("pages.auth.callback.index.try.sign.in.again", "Try sign-in again")}
          </Button>,
          <Button
            href={isReviewFlow ? callbackError.returnTo : "/login"}
            key="back"
          >
            {isReviewFlow
              ? t("pages.auth.callback.index.back.to.account.settings", "Back to Account settings")
              : t("pages.auth.callback.index.back.to.login", "Back to login")}
          </Button>,
        ]}
        status="error"
        subTitle={callbackError.message}
        title={t("pages.auth.callback.index.nyxid.callback.failed", "NyxID callback failed")}
      />
    );
  }

  return (
    <AevatarPageLoading
      fullscreen
      tip={
        retrying
          ? t("pages.auth.callback.index.restarting.nyxid.authorization", "Restarting NyxID authorization...")
          : t("pages.auth.callback.index.completing.nyxid.sign.in", "Completing NyxID sign-in...")
      }
    />
  );
};

export default CallbackPage;
