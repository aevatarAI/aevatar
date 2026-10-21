import { useQuery } from '@tanstack/react-query';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import { findNodeConfigPrimitiveDescriptor } from './nodeConfigFields';

export function useNodeConfigurationDescriptor(stepType: string | undefined) {
  const query = useQuery({
    queryKey: ['primitive-library'],
    queryFn: () => runtimeQueryApi.listPrimitives(),
    enabled: Boolean(stepType),
    retry: false,
  });

  return {
    descriptor: stepType
      ? findNodeConfigPrimitiveDescriptor(query.data ?? [], stepType)
      : null,
    query,
  };
}
