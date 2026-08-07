# Standardized Failure Toast Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render Aevatar failure toasts as compact, dismissible top-right notifications that pause on hover and close automatically after eight seconds.

**Architecture:** Keep `ConsoleToast` as the single shared toast API and replace its Ant Design `message` adapter with the context-bound `notification` adapter. Keep business failure classification in `runFailurePresentation`, where every typed failure receives the approved eight-second duration and the existing content is reorganized into a token-backed compact hierarchy.

**Tech Stack:** React 19, TypeScript, Ant Design 6 `App`/`notification`/`Typography`, Jest 29, Testing Library, Biome

---

### Task 1: Route the shared toast API through Ant Design notification

**Files:**
- Create: `apps/aevatar-console-web/src/shared/ui/ConsoleToast.test.tsx`
- Modify: `apps/aevatar-console-web/src/shared/ui/ConsoleToast.tsx`

- [x] **Step 1: Write the failing shared-adapter test**

Create `ConsoleToast.test.tsx` with a typed notification stub. The first test must call the public hook and prove that an error is sent to the top-right notification API with a close control, hover pause, the supplied duration/key/callback, and compact token-backed surface styles.

```tsx
import { act, renderHook } from '@testing-library/react';
import { App } from 'antd';
import type { NotificationInstance } from 'antd/es/notification/interface';
import React from 'react';
import { useConsoleToast } from './ConsoleToast';

function createNotificationStub(): jest.Mocked<NotificationInstance> {
  return {
    destroy: jest.fn(),
    error: jest.fn(),
    info: jest.fn(),
    open: jest.fn(),
    success: jest.fn(),
    warning: jest.fn(),
  };
}

describe('ConsoleToast', () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('opens a compact dismissible top-right error notification', () => {
    const notification = createNotificationStub();
    const onClose = jest.fn();
    const content = <span>Request failed</span>;
    jest.spyOn(App, 'useApp').mockReturnValue({
      notification,
    } as ReturnType<typeof App.useApp>);

    const { result } = renderHook(() => useConsoleToast());
    act(() => {
      result.current.error(content, {
        duration: 8,
        key: 'request-failure',
        onClose,
      });
    });

    expect(notification.error).toHaveBeenCalledTimes(1);
    const config = notification.error.mock.calls[0][0];
    expect(config).toMatchObject({
      className: 'aevatar-console-toast',
      closable: true,
      duration: 8,
      key: 'request-failure',
      onClose,
      pauseOnHover: true,
      placement: 'topRight',
      role: 'alert',
    });
    expect(config.title).toBe(content);
    expect(config.styles).toMatchObject({
      root: {
        maxWidth: 'calc(100vw - 32px)',
        width: 360,
      },
      title: {
        marginBottom: 0,
      },
    });
  });

  it('uses status semantics and the short default for success', () => {
    const notification = createNotificationStub();
    jest.spyOn(App, 'useApp').mockReturnValue({
      notification,
    } as ReturnType<typeof App.useApp>);

    const { result } = renderHook(() => useConsoleToast());
    act(() => {
      result.current.success('Saved');
    });

    expect(notification.success).toHaveBeenCalledWith(
      expect.objectContaining({ duration: 3, role: 'status' }),
    );
  });
});
```

- [x] **Step 2: Run the test and verify RED**

Run from `apps/aevatar-console-web`:

```bash
pnpm exec jest src/shared/ui/ConsoleToast.test.tsx --runInBand
```

Expected: FAIL because `ConsoleToast` still calls the message instance and never calls `notification.error`.

- [x] **Step 3: Implement the notification adapter**

Replace the message types with notification types, read `notification` and theme tokens from the existing Ant Design contexts, and keep the public toast methods unchanged.

```tsx
import { App, theme } from 'antd';
import type {
  ArgsProps,
  NotificationInstance,
} from 'antd/es/notification/interface';
import React from 'react';

type ConsoleToastOptions = Pick<ArgsProps, 'duration' | 'key' | 'onClose'>;
type ConsoleToastIntent = 'error' | 'info' | 'success' | 'warning';

type ConsoleToastSurfaceToken = Pick<
  ReturnType<typeof theme.useToken>['token'],
  | 'borderRadiusLG'
  | 'boxShadowSecondary'
  | 'colorBorderSecondary'
  | 'fontSize'
  | 'lineHeight'
  | 'padding'
  | 'paddingSM'
>;

export type ConsoleToastApi = {
  readonly [Intent in ConsoleToastIntent]: (
    content: React.ReactNode,
    options?: ConsoleToastOptions,
  ) => void;
};

const TOAST_DURATION: Readonly<Record<ConsoleToastIntent, number>> = {
  error: 5,
  info: 3,
  success: 3,
  warning: 5,
};

function createConsoleToastApi(
  notificationApi: NotificationInstance,
  token: ConsoleToastSurfaceToken,
): ConsoleToastApi {
  const show = (
    intent: ConsoleToastIntent,
    content: React.ReactNode,
    options?: ConsoleToastOptions,
  ) => {
    notificationApi[intent]({
      className: 'aevatar-console-toast',
      closable: true,
      duration: options?.duration ?? TOAST_DURATION[intent],
      key: options?.key,
      onClose: options?.onClose,
      pauseOnHover: true,
      placement: 'topRight',
      role: intent === 'error' || intent === 'warning' ? 'alert' : 'status',
      styles: {
        root: {
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: token.borderRadiusLG,
          boxShadow: token.boxShadowSecondary,
          maxWidth: 'calc(100vw - 32px)',
          padding: `${token.paddingSM}px ${token.padding}px`,
          width: 360,
        },
        title: {
          fontSize: token.fontSize,
          lineHeight: token.lineHeight,
          marginBottom: 0,
        },
      },
      title: content,
    });
  };

  return {
    error: (content, options) => show('error', content, options),
    info: (content, options) => show('info', content, options),
    success: (content, options) => show('success', content, options),
    warning: (content, options) => show('warning', content, options),
  };
}

export const ConsoleToastProvider: React.FC<{
  readonly children: React.ReactNode;
}> = ({ children }) => (
  <App component="div" style={{ display: 'contents' }}>
    {children}
  </App>
);

export function useConsoleToast(): ConsoleToastApi {
  const { notification } = App.useApp();
  const { token } = theme.useToken();
  return React.useMemo(
    () => createConsoleToastApi(notification, token),
    [notification, token],
  );
}
```

- [x] **Step 4: Run the test and verify GREEN**

```bash
pnpm exec jest src/shared/ui/ConsoleToast.test.tsx --runInBand
```

Expected: PASS with 2 tests and 0 failures.

- [x] **Step 5: Commit the shared adapter**

```bash
git add apps/aevatar-console-web/src/shared/ui/ConsoleToast.tsx \
  apps/aevatar-console-web/src/shared/ui/ConsoleToast.test.tsx
git commit -m "Standardize console toast notifications"
```

### Task 2: Apply the approved failure hierarchy and eight-second duration

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/runFailurePresentation.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/runFailurePresentation.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`

- [x] **Step 1: Write the failing duration and hierarchy expectations**

Change every expected duration in the classification table to `8`. Extend the accessible-action test with explicit compact hierarchy expectations:

```tsx
const message = screen.getByText('Try again after the quota window resets.');
const guidance = screen.getByText(
  'Wait for the quota window to reset before trying again.',
);
expect(message).toBeVisible();
expect(message).toHaveClass('ant-typography');
expect(guidance).toHaveClass('ant-typography-secondary');
```

- [x] **Step 2: Run the test and verify RED**

```bash
pnpm exec jest src/pages/workflow-activity-vnext/activity/runFailurePresentation.test.tsx --runInBand
```

Expected: FAIL because current categories still use `0`, `3`, or `5` seconds and the guidance is a plain `div`.

- [x] **Step 3: Implement one failure duration and token-backed hierarchy**

Import `Typography`, define one duration constant, replace every category-specific duration with it, and render message/guidance/retry timing with Ant Design typography.

```tsx
import { Button, Space, Typography } from 'antd';

const RUN_FAILURE_TOAST_DURATION_SECONDS = 8;

// In every category definition:
duration: RUN_FAILURE_TOAST_DURATION_SECONDS,

export const RunFailureToastContent: React.FC<{
  readonly onAction?: (action: RunFailureAction) => void;
  readonly presentation: RunFailurePresentation;
}> = ({ onAction, presentation }) => {
  const correlationId = presentation.correlationId;
  return (
    <div
      style={{
        alignItems: 'flex-start',
        display: 'flex',
        flexDirection: 'column',
        gap: 4,
        textAlign: 'left',
      }}
    >
      <Typography.Text strong>{presentation.message}</Typography.Text>
      <Typography.Text type="secondary">
        {presentation.guidance}
      </Typography.Text>
      {presentation.retryAfterSeconds !== undefined ? (
        <Typography.Text type="secondary">
          {t(
            'workflowActivityVNext.failure.retryAfter',
            'Try again in {seconds} seconds.',
            { seconds: presentation.retryAfterSeconds },
          )}
        </Typography.Text>
      ) : null}
      <Space size="small" wrap>
        {onAction ? (
          <Button
            onClick={() => onAction(presentation.action)}
            size="small"
            type="link"
          >
            {presentation.actionLabel}
          </Button>
        ) : null}
        {correlationId ? (
          <Button
            onClick={() => {
              void navigator.clipboard?.writeText(correlationId);
            }}
            size="small"
            type="link"
          >
            {t(
              'workflowActivityVNext.failure.copyTrackingId',
              'Copy tracking ID',
            )}
          </Button>
        ) : null}
      </Space>
    </div>
  );
};
```

- [x] **Step 4: Run the failure-presentation test and verify GREEN**

```bash
pnpm exec jest src/pages/workflow-activity-vnext/activity/runFailurePresentation.test.tsx --runInBand
```

Expected: PASS with 13 tests and 0 failures.

- [x] **Step 5: Commit the failure policy**

```bash
git add \
  apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/runFailurePresentation.tsx \
  apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/runFailurePresentation.test.tsx
git commit -m "Refine workflow failure toast presentation"
```

### Task 3: Run focused verification and deliver the pull request

**Files:**
- Verify all files changed on the branch.

- [x] **Step 1: Analyze the affected frontend scope**

From the repository root:

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . \
  --base origin/feat/2026-08-04_workflow-activity-vnext
```

Expected: Jest is the affected runner; the two changed production files and three changed test files are listed as frontend scope.

- [x] **Step 2: Run dependency-related tests for changed production files**

From `apps/aevatar-console-web`, using the analyzer's changed source list:

```bash
pnpm exec jest --findRelatedTests \
  src/shared/ui/ConsoleToast.tsx \
  src/pages/workflow-activity-vnext/activity/runFailurePresentation.tsx \
  --runInBand
```

Expected: all dependency-related tests selected by Jest pass. Do not replace this command with the full frontend suite.

- [x] **Step 3: Run the changed test files explicitly**

```bash
pnpm exec jest \
  src/shared/ui/ConsoleToast.test.tsx \
  src/pages/workflow-activity-vnext/activity/runFailurePresentation.test.tsx \
  src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx \
  --runInBand
```

Expected: all three changed test suites pass.

- [x] **Step 4: Run required test and changed-file static guards**

From the repository root:

```bash
bash tools/ci/test_stability_guards.sh
pnpm --dir apps/aevatar-console-web exec biome check \
  src/shared/ui/ConsoleToast.tsx \
  src/shared/ui/ConsoleToast.test.tsx \
  src/pages/workflow-activity-vnext/activity/runFailurePresentation.tsx \
  src/pages/workflow-activity-vnext/activity/runFailurePresentation.test.tsx \
  src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
```

Expected: the stability guard and Biome checks pass. Skip local full typecheck and build; GitHub CI owns them.

- [x] **Step 5: Review the complete task diff**

```bash
git status --short
git diff --check origin/feat/2026-08-04_workflow-activity-vnext...HEAD
git diff --stat origin/feat/2026-08-04_workflow-activity-vnext...HEAD
git diff origin/feat/2026-08-04_workflow-activity-vnext...HEAD -- \
  docs/superpowers/specs/2026-08-06-standardize-failure-toast-design.md \
  docs/superpowers/plans/2026-08-06-standardize-failure-toast.md \
  apps/aevatar-console-web/src/shared/ui/ConsoleToast.tsx \
  apps/aevatar-console-web/src/shared/ui/ConsoleToast.test.tsx \
  apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/runFailurePresentation.tsx \
  apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/runFailurePresentation.test.tsx \
  apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
```

Expected: only the toast design, plan, shared adapter, failure presentation, and their tests are present.

- [x] **Step 6: Push and create the focused pull request**

```bash
git push -u origin fix/2026-08-06_standardize-failure-toast
gh pr create \
  --base feat/2026-08-04_workflow-activity-vnext \
  --head fix/2026-08-06_standardize-failure-toast \
  --title "Standardize Workflow Activity failure toasts" \
  --body-file /tmp/standardize-failure-toast-pr.md
```

The pull request body must contain the problem and selected solution, the seven changed paths, the exact focused verification commands and results, and this statement:

```markdown
- Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```

Stop after reporting the PR URL. Do not wait for CI unless the user asks.
