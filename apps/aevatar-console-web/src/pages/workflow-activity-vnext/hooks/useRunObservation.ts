import { useQuery } from "@tanstack/react-query";
import { workflowActivityApi } from "@/shared/api/workflowActivityApi";

export function useRunObservation(scopeId: string, runId: string) {
  return useQuery({
    enabled: Boolean(scopeId.trim() && runId.trim()),
    queryKey: ["workflow-activity-vnext", "run", scopeId, runId],
    queryFn: () => workflowActivityApi.getRun(scopeId, runId),
    retry: false,
  });
}
