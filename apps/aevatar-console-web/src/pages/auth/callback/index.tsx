import { Button, Result } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import {
  type AuthFlow,
  type NyxIDAuthCallbackErrorReason,
  NyxIDAuthClient,
} from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { loadStoredAuthSession, sanitizeReturnTo } from '@/shared/auth/session';
import { t } from '@/shared/i18n/messages';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';
import { describeError } from '@/shared/ui/errorText';

type CallbackErrorState = {
  readonly flow: AuthFlow;
  readonly message: string;
  readonly reason: NyxIDAuthCallbackErrorReason;
  readonly returnTo: string;
};

const callbackErrorReasons = new Set<NyxIDAuthCallbackErrorReason>([
  'oauthDenied',
  'requiredServiceAccessMissing',
  'issuedBindingInvalid',
  'issuedBindingProbeFailed',
  'bindingProbeFailed',
  'serviceAccessReviewRequired',
  'serviceAccessReviewUnavailable',
  'serviceAccessReviewFailed',
  'signInFailed',
]);

function describeCallbackError(
  error: unknown,
  reason: NyxIDAuthCallbackErrorReason,
): string {
  switch (reason) {
    case 'oauthDenied':
      return t(
        'pages.auth.callback.index.service.access.review.cancelled',
        'NyxID service access review was cancelled or denied. Your current Studio session is still active.',
      );
    case 'requiredServiceAccessMissing':
      return t(
        'pages.auth.callback.index.required.service.access.missing',
        'Required service access is still missing. Return to NyxID and keep every service marked as required by Aevatar selected.',
      );
    case 'issuedBindingInvalid':
      return t(
        'pages.auth.callback.index.issued.binding.invalid',
        'The new NyxID authorization expired before Aevatar could use it. Start the review again.',
      );
    case 'issuedBindingProbeFailed':
      return t(
        'pages.auth.callback.index.issued.binding.probe.failed',
        'NyxID could not verify the new service authorization. Try again in a moment.',
      );
    case 'bindingProbeFailed':
      return t(
        'pages.auth.callback.index.binding.probe.failed',
        'NyxID could not verify the current service authorization. Try again in a moment.',
      );
    case 'serviceAccessReviewRequired':
      return t(
        'pages.auth.callback.index.service.access.review.required',
        'Service access review needs to be run again.',
      );
    case 'serviceAccessReviewUnavailable':
      return t(
        'pages.auth.callback.index.service.access.review.unavailable',
        'Service access review is temporarily unavailable. Try again in a moment.',
      );
    case 'serviceAccessReviewFailed':
      return t(
        'pages.auth.callback.index.service.access.review.failed',
        'Service access review failed. Try again.',
      );
    case 'signInFailed':
      return describeSignInCallbackError(error);
  }
}

function normalizeCallbackErrorMessage(value: string): string {
  return value.replace(/\s+/g, ' ').trim();
}

function readCallbackErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return normalizeCallbackErrorMessage(error.message || error.name || '');
  }

  if (error && typeof error === 'object' && !Array.isArray(error)) {
    const record = error as { message?: unknown };
    if (typeof record.message === 'string') {
      return normalizeCallbackErrorMessage(record.message);
    }
  }

  return normalizeCallbackErrorMessage(String(error ?? ''));
}

function isAuthFinalizationNetworkDetail(message: string): boolean {
  const lower = message.toLowerCase();
  const isNetworkOrProxyFailure =
    lower.includes('error occurred while trying to proxy') ||
    lower.includes('failed to fetch') ||
    lower.includes('network error') ||
    lower.includes('econnreset');

  return isNetworkOrProxyFailure && lower.includes('/api/auth/nyxid/finalize');
}

function describeSignInCallbackError(error: unknown): string {
  const message = readCallbackErrorMessage(error);
  if (isAuthFinalizationNetworkDetail(message)) {
    return message;
  }

  return describeError(error);
}

function readCallbackErrorState(error: unknown): CallbackErrorState {
  const record =
    error && typeof error === 'object' && !Array.isArray(error)
      ? (error as { flow?: unknown; reason?: unknown; returnTo?: unknown })
      : null;
  const flow =
    record?.flow === 'serviceAccessReview' ? 'serviceAccessReview' : 'signIn';
  const reason = callbackErrorReasons.has(
    record?.reason as NyxIDAuthCallbackErrorReason,
  )
    ? (record?.reason as NyxIDAuthCallbackErrorReason)
    : flow === 'serviceAccessReview'
      ? 'serviceAccessReviewFailed'
      : 'signInFailed';
  const fallbackReturnTo =
    flow === 'serviceAccessReview' ? CONSOLE_HOME_ROUTE : '/login';
  const returnTo =
    typeof record?.returnTo === 'string'
      ? sanitizeReturnTo(record.returnTo)
      : fallbackReturnTo;

  return {
    flow,
    message: describeCallbackError(error, reason),
    reason,
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
      if (callbackError.flow === 'serviceAccessReview') {
        await client.loginWithRedirect({
          flow: 'serviceAccessReview',
          returnTo: callbackError.returnTo,
        });
      } else {
        await client.loginWithRedirect({
          flow: 'signIn',
          ...(callbackError.reason === 'requiredServiceAccessMissing'
            ? { prompt: 'consent' as const }
            : {}),
          returnTo: callbackError.returnTo,
        });
      }
    } catch (error) {
      setRetrying(false);
      setCallbackError({
        ...callbackError,
        message:
          callbackError.flow === 'serviceAccessReview'
            ? t(
                'pages.auth.callback.index.service.access.review.restart.failed',
                'Could not restart service access review. Try again.',
              )
            : describeError(error),
      });
    }
  };

  if (callbackError) {
    const isReviewFlow = callbackError.flow === 'serviceAccessReview';
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
              ? t(
                  'pages.auth.callback.index.retry.service.access.review',
                  'Retry service access review',
                )
              : t(
                  'pages.auth.callback.index.try.sign.in.again',
                  'Try sign-in again',
                )}
          </Button>,
          <Button
            href={isReviewFlow ? callbackError.returnTo : '/login'}
            key="back"
          >
            {isReviewFlow
              ? t(
                  'pages.auth.callback.index.back.to.account.settings',
                  'Back to Account settings',
                )
              : t('pages.auth.callback.index.back.to.login', 'Back to login')}
          </Button>,
        ]}
        status="error"
        subTitle={
          <span aria-live="assertive" role="alert">
            {callbackError.message}
          </span>
        }
        title={t(
          'pages.auth.callback.index.nyxid.callback.failed',
          'NyxID callback failed',
        )}
      />
    );
  }

  return (
    <AevatarPageLoading
      fullscreen
      tip={t(
        'pages.auth.callback.index.completing.nyxid.authorization',
        'Completing NyxID authorization...',
      )}
    />
  );
};

export default CallbackPage;
