import { useConsoleLocation } from '../hooks/useConsoleLocation';
import { parseWorkflowActivityRoute } from './routeParameters';

export function useWorkflowActivityRoute() {
  const { pathname, search } = useConsoleLocation();
  return parseWorkflowActivityRoute(pathname, search);
}
