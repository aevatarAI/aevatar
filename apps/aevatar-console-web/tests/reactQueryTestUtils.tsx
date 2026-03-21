import { enUSIntl, ProConfigProvider } from '@ant-design/pro-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RenderResult } from '@testing-library/react';
import { render } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import enUS from 'antd/locale/en_US';
import type { ReactElement } from 'react';
import React from 'react';

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
): RenderResult & { queryClient: QueryClient } {
  const queryClient = createTestQueryClient();
  const view = render(
    <ConfigProvider locale={enUS}>
      <ProConfigProvider intl={enUSIntl}>
        <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
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
