import {
  createRunSession,
  reduceEvent,
  type RunSessionState,
} from '@aevatar-react-sdk/agui';
import { CustomEventName, type AGUIEvent } from '@aevatar-react-sdk/types';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  startTransition,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  getLatestCustomEventData,
  parseRunContextData,
} from '@/shared/agui/customEventData';
import type {
  MissionActionFeedback,
  MissionControlRouteContext,
  MissionInterventionActionKind,
  MissionInterventionActionRequest,
  MissionInterventionState,
  MissionRuntimeConnectionStatus,
  MissionRuntimeViewState,
} from '../models';
import {
  buildMissionRuntimePlaceholderSnapshot,
  buildMissionSnapshotFromRuntime,
} from '../runtimeAdapter';
import type { MissionControlRuntimeArtifacts } from '../services/api';
import {
  fetchMissionControlRuntimeArtifacts,
  hasMissionControlLiveContext,
  readMissionControlRouteContext,
  readMissionObservedRunContext,
  streamMissionControlEvents,
  submitMissionControlIntervention,
} from '../services/api';

const QUERY_KEY_PREFIX = 'mission-control-runtime';
const STREAM_FLUSH_DELAY_MS = 120;
const STREAM_STALE_AFTER_MS = 15_000;
const TIMELINE_TICK_MS = 1_000;
const RUNTIME_REFETCH_INTERVAL_MS = 8_000;
const MAX_RECENT_EVENTS = 240;
const MAX_RECENT_MESSAGES = 48;

export type UseMissionControlRuntimeResult = MissionRuntimeViewState & {
  refresh: () => Promise<void>;
  routeContext: MissionControlRouteContext;
  submitIntervention: (
    intervention: MissionInterventionState,
    action: MissionInterventionActionRequest,
  ) => Promise<void>;
};

function buildMissionControlQueryKey(context: MissionControlRouteContext) {
  return [
    QUERY_KEY_PREFIX,
    context.actorId || '',
    context.scopeId || '',
    context.runId || '',
    context.serviceId || '',
  ] as const;
}

function compactRunSession(session: RunSessionState): RunSessionState {
  const nextEvents =
    session.events.length > MAX_RECENT_EVENTS
      ? session.events.slice(-MAX_RECENT_EVENTS)
      : session.events;
  const nextMessages =
    session.messages.length > MAX_RECENT_MESSAGES
      ? session.messages.slice(-MAX_RECENT_MESSAGES)
      : session.messages;

  if (nextEvents === session.events && nextMessages === session.messages) {
    return session;
  }

  return {
    ...session,
    events: nextEvents,
    messages: nextMessages,
  };
}

function reduceQueuedEvents(
  session: RunSessionState,
  events: readonly AGUIEvent[],
): RunSessionState {
  let next = session;
  for (const event of events) {
    next = reduceEvent(next, event);
  }

  return compactRunSession(next);
}

function buildConnectionMessage(
  connectionStatus: MissionRuntimeConnectionStatus,
  options: {
    liveMode: boolean;
    queryError?: string;
    streamEnabled: boolean;
    streamError?: string;
  },
): string {
  if (!options.liveMode) {
    return 'Mission Control needs a live run context before it can load a real decision path; open it from Runs.';
  }

  if (connectionStatus === 'connecting') {
    return 'Connecting to runtime and loading the current run topology and key events.';
  }

  if (connectionStatus === 'disconnected') {
    return options.queryError || options.streamError || 'Runtime connection lost. Waiting for service recovery.';
  }

  if (connectionStatus === 'degraded') {
    return (
      options.streamError ||
      options.queryError ||
      'The live event stream was interrupted. Showing the most recent successful snapshot.'
    );
  }

  if (!options.streamEnabled) {
    return 'Live streaming is disabled, so Mission Control is polling runtime state.';
  }

  return 'Runtime is streaming live; node freshness and edge flow update with each event.';
}

function buildAcceptedFeedback(
  kind: MissionInterventionActionKind,
  accepted: boolean,
  signalName?: string,
): MissionActionFeedback {
  if (!accepted) {
      return {
        message: 'Runtime did not accept the intervention request. Please try again.',
        tone: 'warning',
      };
  }

  switch (kind) {
    case 'signal':
      return {
        message: `Signal ${signalName || 'continue'} was accepted. Waiting for runtime to continue.`,
        tone: 'success',
      };
    case 'approve':
      return {
        message: 'Approval was accepted. Waiting for the run to advance.',
        tone: 'success',
      };
    case 'reject':
      return {
        message: 'Rejection was submitted. Waiting for runtime to confirm stop or rollback.',
        tone: 'warning',
      };
    default:
      return {
        message: 'Resume was accepted. Waiting for the next runtime snapshot.',
        tone: 'success',
      };
  }
}

export function useMissionControlRuntime(): UseMissionControlRuntimeResult {
  const queryClient = useQueryClient();
  const routeContext = useMemo(() => readMissionControlRouteContext(), []);
  const [resolvedActorId, setResolvedActorId] = useState<string | undefined>(
    routeContext.actorId,
  );
  const runtimeContext = useMemo(
    () => ({
      ...routeContext,
      actorId: resolvedActorId || routeContext.actorId,
    }),
    [resolvedActorId, routeContext],
  );
  const liveMode = useMemo(
    () => hasMissionControlLiveContext(routeContext),
    [routeContext],
  );
  const streamEnabled = Boolean(
    liveMode &&
      routeContext.autoStream !== false &&
      routeContext.scopeId &&
      routeContext.prompt,
  );

  const [nowMs, setNowMs] = useState(() => Date.now());
  const [session, setSession] = useState<RunSessionState>(() => createRunSession());
  const [streamError, setStreamError] = useState<string | undefined>();
  const [submittingActionKind, setSubmittingActionKind] = useState<
    MissionInterventionActionKind | undefined
  >();
  const [actionFeedback, setActionFeedback] = useState<
    MissionActionFeedback | undefined
  >();
  const eventQueueRef = useRef<AGUIEvent[]>([]);
  const flushTimerRef = useRef<number | undefined>(undefined);
  const lastStreamEventAtRef = useRef<number | undefined>(undefined);
  const lastArtifactsRef = useRef<MissionControlRuntimeArtifacts | undefined>(undefined);

  const runtimeQuery = useQuery({
    queryKey: buildMissionControlQueryKey(runtimeContext),
    queryFn: () => fetchMissionControlRuntimeArtifacts(runtimeContext),
    enabled: liveMode,
    refetchInterval: liveMode ? RUNTIME_REFETCH_INTERVAL_MS : false,
    retry: 1,
    staleTime: 2_000,
  });

  useEffect(() => {
    const timerId = window.setInterval(() => {
      setNowMs(Date.now());
    }, TIMELINE_TICK_MS);

    return () => {
      window.clearInterval(timerId);
    };
  }, []);

  useEffect(() => {
    if (!runtimeQuery.data) {
      return;
    }

    lastArtifactsRef.current = runtimeQuery.data;
  }, [runtimeQuery.data]);

  useEffect(() => {
    const queryActorId = runtimeQuery.data?.actorId?.trim();
    if (!queryActorId || queryActorId === resolvedActorId) {
      return;
    }

    setResolvedActorId(queryActorId);
  }, [resolvedActorId, runtimeQuery.data?.actorId]);

  const flushQueuedEvents = useCallback(() => {
    flushTimerRef.current = undefined;
    const queuedEvents = eventQueueRef.current.splice(0, eventQueueRef.current.length);
    if (queuedEvents.length === 0) {
      return;
    }

    startTransition(() => {
      setSession((previousSession) => reduceQueuedEvents(previousSession, queuedEvents));
    });
  }, []);

  const enqueueEvent = useCallback(
    (event: AGUIEvent) => {
      eventQueueRef.current.push(event);
      if (flushTimerRef.current !== undefined) {
        return;
      }

      flushTimerRef.current = window.setTimeout(
        flushQueuedEvents,
        STREAM_FLUSH_DELAY_MS,
      );
    },
    [flushQueuedEvents],
  );

  useEffect(() => {
    return () => {
      if (flushTimerRef.current !== undefined) {
        window.clearTimeout(flushTimerRef.current);
      }
    };
  }, []);

  useEffect(() => {
    eventQueueRef.current = [];
    if (flushTimerRef.current !== undefined) {
      window.clearTimeout(flushTimerRef.current);
      flushTimerRef.current = undefined;
    }
    lastStreamEventAtRef.current = undefined;
    setActionFeedback(undefined);
    setResolvedActorId(routeContext.actorId);
    setStreamError(undefined);
    setSession(createRunSession());
  }, [
    liveMode,
    routeContext.actorId,
    routeContext.runId,
    routeContext.scopeId,
    routeContext.serviceId,
  ]);

  useEffect(() => {
    const observedContext =
      session.context ||
      getLatestCustomEventData(
        session.events,
        CustomEventName.RunContext,
        parseRunContextData,
      ) ||
      (session.events.length > 0
        ? readMissionObservedRunContext(session.events[session.events.length - 1])
        : undefined);
    const nextActorId = observedContext?.actorId?.trim();
    if (!nextActorId || nextActorId === resolvedActorId) {
      return;
    }

    setResolvedActorId(nextActorId);
  }, [resolvedActorId, session.context, session.events]);

  useEffect(() => {
    if (!streamEnabled) {
      return;
    }

    const controller = new AbortController();
    let disposed = false;

    const consume = async () => {
      try {
        for await (const event of streamMissionControlEvents(routeContext, controller.signal)) {
          if (disposed) {
            return;
          }

          lastStreamEventAtRef.current = Date.now();
          setStreamError(undefined);
          enqueueEvent(event);
        }
      } catch (error) {
        if (disposed || controller.signal.aborted) {
          return;
        }

        setStreamError(
          error instanceof Error ? error.message : 'The live event stream was interrupted.',
        );
      }
    };

    void consume();

    return () => {
      disposed = true;
      controller.abort();
    };
  }, [enqueueEvent, routeContext, streamEnabled]);

  const currentArtifacts = runtimeQuery.data ?? lastArtifactsRef.current;
  const queryError =
    runtimeQuery.error instanceof Error ? runtimeQuery.error.message : undefined;
  const terminalRuntime =
    currentArtifacts?.graph.snapshot.completionStatusValue === 1 ||
    currentArtifacts?.graph.snapshot.completionStatusValue === 3 ||
    currentArtifacts?.graph.snapshot.completionStatusValue === 4 ||
    session.status === 'finished' ||
    session.status === 'error';

  const connectionStatus = useMemo<MissionRuntimeConnectionStatus>(() => {
    if (!liveMode) {
      return 'idle';
    }

    if (!currentArtifacts && runtimeQuery.isLoading) {
      return 'connecting';
    }

    if (!currentArtifacts && runtimeQuery.isError) {
      return 'disconnected';
    }

    if (runtimeQuery.isError) {
      return 'degraded';
    }

    if (
      streamEnabled &&
      !terminalRuntime &&
      lastStreamEventAtRef.current !== undefined &&
      nowMs - lastStreamEventAtRef.current > STREAM_STALE_AFTER_MS
    ) {
      return 'degraded';
    }

    if (streamEnabled && streamError) {
      return currentArtifacts ? 'degraded' : 'disconnected';
    }

    return currentArtifacts ? 'live' : 'connecting';
  }, [
    currentArtifacts,
    liveMode,
    nowMs,
    runtimeQuery.isError,
    runtimeQuery.isLoading,
    streamEnabled,
    streamError,
    terminalRuntime,
  ]);

  const snapshot = useMemo(() => {
    if (!liveMode) {
      return buildMissionRuntimePlaceholderSnapshot({
        connectionStatus: 'idle',
        context: runtimeContext,
        nowMs,
      });
    }

    if (!currentArtifacts) {
      return buildMissionRuntimePlaceholderSnapshot({
        connectionStatus,
        context: runtimeContext,
        nowMs,
      });
    }

    return buildMissionSnapshotFromRuntime({
      connectionStatus,
      nowMs,
      recentEvents: session.events,
      resources: {
        artifacts: currentArtifacts,
        session,
      },
      routeContext: runtimeContext,
    });
  }, [
    connectionStatus,
    currentArtifacts,
    liveMode,
    nowMs,
    runtimeContext,
    session,
  ]);

  const refresh = useCallback(async () => {
    if (!liveMode) {
      return;
    }

    await runtimeQuery.refetch();
  }, [liveMode, runtimeQuery]);

  const submitIntervention = useCallback(
    async (
      intervention: MissionInterventionState,
      action: MissionInterventionActionRequest,
    ) => {
      if (!liveMode) {
        return;
      }

      try {
        setSubmittingActionKind(action.kind);
        setActionFeedback(undefined);
        const result = await submitMissionControlIntervention(
          runtimeContext,
          intervention,
          action,
        );
        setActionFeedback(
          buildAcceptedFeedback(action.kind, result.accepted, result.signalName),
        );
        await queryClient.invalidateQueries({
          queryKey: buildMissionControlQueryKey(runtimeContext),
        });
        await runtimeQuery.refetch();
      } catch (error) {
        setActionFeedback({
          message:
            error instanceof Error ? error.message : 'The intervention action failed.',
          tone: 'error',
        });
      } finally {
        setSubmittingActionKind(undefined);
      }
    },
    [liveMode, queryClient, runtimeContext, runtimeQuery],
  );

  return {
    actionFeedback,
    connectionMessage: buildConnectionMessage(connectionStatus, {
      liveMode,
      queryError,
      streamEnabled,
      streamError,
    }),
    connectionStatus,
    liveMode,
    loading: liveMode ? runtimeQuery.isLoading && !currentArtifacts : false,
    refresh,
    resuming:
      submittingActionKind === 'approve' ||
      submittingActionKind === 'reject' ||
      submittingActionKind === 'resume',
    routeContext: runtimeContext,
    signaling: submittingActionKind === 'signal',
    snapshot,
    submitIntervention,
    submittingActionKind,
  };
}
