import { ProConfigProvider } from '@ant-design/pro-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RenderResult } from '@testing-library/react';
import { render } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import type { ReactElement } from 'react';
import React from 'react';
import {
  resolveAntdLocale,
  resolveProIntl,
} from '../src/shared/i18n/localeProvider';
import { ConsoleToastProvider } from '../src/shared/ui/ConsoleToast';

const activeQueryClients = new Set<QueryClient>();

export function createTestQueryClient(): QueryClient {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: Infinity,
        staleTime: 0,
        refetchOnWindowFocus: false,
      },
    },
  });

  activeQueryClients.add(queryClient);
  return queryClient;
}

export function renderWithQueryClient(
  ui: ReactElement,
  providedQueryClient?: QueryClient,
): RenderResult & { queryClient: QueryClient } {
  const queryClient = providedQueryClient ?? createTestQueryClient();
  if (providedQueryClient) {
    activeQueryClients.add(queryClient);
  }
  const view = render(
    <ConfigProvider locale={resolveAntdLocale('en-US')}>
      <ProConfigProvider intl={resolveProIntl('en-US')}>
        <ConsoleToastProvider>
          <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
        </ConsoleToastProvider>
      </ProConfigProvider>
    </ConfigProvider>,
  );

  return {
    ...view,
    queryClient,
  };
}

export function cleanupTestQueryClients(): void {
  for (const queryClient of activeQueryClients) {
    queryClient.clear();
  }

  activeQueryClients.clear();
}
