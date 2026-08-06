import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import {
  classifyRunFailure,
  RunFailureToastContent,
} from './runFailurePresentation';

describe('Workflow Activity run failure presentation', () => {
  it.each([
    [
      { status: 401, message: 'Your session expired.' },
      'session_expired',
      'error',
      0,
    ],
    [
      {
        status: 403,
        code: 'GROUP_NOT_ALLOWED',
        message: 'This group cannot use the selected model.',
      },
      'access_denied',
      'error',
      0,
    ],
    [
      { status: 429, message: 'Too many requests.', retryAfterSeconds: 12 },
      'rate_limited',
      'warning',
      0,
    ],
    [
      {
        status: 422,
        code: 'INVALID_WORKFLOW',
        message: 'The workflow input is invalid.',
      },
      'invalid_input',
      'warning',
      0,
    ],
    [
      { status: 404, message: 'The run was not found.' },
      'resource_missing',
      'error',
      5,
    ],
    [
      {
        status: 409,
        code: 'STATE_VERSION_CONFLICT',
        message: 'A newer version is available.',
      },
      'state_conflict',
      'warning',
      0,
    ],
    [
      { status: 504, message: 'The provider timed out.' },
      'timeout_or_offline',
      'warning',
      5,
    ],
    [
      {
        status: 503,
        code: 'PROVIDER_UNAVAILABLE',
        message: 'The provider is unavailable.',
      },
      'upstream_unavailable',
      'error',
      5,
    ],
    [
      { code: 'RUN_CANCELLED', message: 'Cancelled by the user.' },
      'cancelled',
      'info',
      3,
    ],
    [
      {
        status: 500,
        message: 'The service could not complete the request.',
        correlationId: 'corr-alpha',
      },
      'internal_failure',
      'error',
      0,
    ],
    [
      { status: 418, code: 'UNRECOGNIZED_CODE', message: '' },
      'internal_failure',
      'error',
      0,
    ],
  ] as const)('classifies %p as %s', (evidence, category, intent, duration) => {
    expect(classifyRunFailure(evidence)).toMatchObject({
      category,
      duration,
      intent,
    });
  });

  it('uses the safe backend message, retry countdown, and accessible action', () => {
    const onAction = jest.fn();
    const presentation = classifyRunFailure({
      status: 429,
      message: 'Try again after the quota window resets.',
      retryAfterSeconds: 12,
    });

    render(
      <RunFailureToastContent
        onAction={onAction}
        presentation={presentation}
      />,
    );

    expect(
      screen.getByText('Try again after the quota window resets.'),
    ).toBeVisible();
    expect(screen.getByText('Try again in 12 seconds.')).toBeVisible();
    const action = screen.getByRole('button', { name: 'Retry' });
    action.focus();
    fireEvent.keyDown(action, { key: 'Enter' });
    fireEvent.click(action);
    expect(onAction).toHaveBeenCalledTimes(1);
  });

  it('copies a typed correlation ID without exposing raw payloads', async () => {
    const writeText = jest.fn().mockResolvedValue(undefined);
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    const presentation = classifyRunFailure({
      status: 500,
      message: 'The service could not complete the request.',
      correlationId: 'corr-alpha',
    });

    render(<RunFailureToastContent presentation={presentation} />);

    fireEvent.click(screen.getByRole('button', { name: 'Copy tracking ID' }));
    expect(writeText).toHaveBeenCalledWith('corr-alpha');
    expect(screen.queryByText(/stack|provider payload|raw json/i)).toBeNull();
  });
});
