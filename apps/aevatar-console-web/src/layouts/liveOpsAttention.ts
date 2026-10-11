import { buildMissionSnapshotFromRuntime } from '@/pages/MissionControl/runtimeAdapter';
import { readMissionControlRouteContext } from '@/pages/MissionControl/services/api';
import { runtimeActorsApi } from '@/shared/api/runtimeActorsApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { loadRecentRuns } from '@/shared/runs/recentRuns';

export type LiveOpsAttentionSnapshot = {
  hasPendingAttention: boolean;
  pendingCount: number;
};

type LiveOpsAttentionCandidate = {
  actorId?: string;
  runId?: string;
  scopeId?: string;
  serviceId?: string;
};

const LIVE_OPS_ATTENTION_MAX_CANDIDATES = 6;
const LIVE_OPS_ATTENTION_MAX_AGE_MS = 12 * 60 * 60 * 1000;

function trimOptional(value?: string | null): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function buildLiveOpsAttentionCandidateKey(
  candidate: LiveOpsAttentionCandidate,
): string {
  const actorId = trimOptional(candidate.actorId);
  if (actorId) {
    return `actor:${actorId}`;
  }

  return [
    'run',
    trimOptional(candidate.scopeId) || '',
    trimOptional(candidate.serviceId) || '',
    trimOptional(candidate.runId) || '',
  ].join(':');
}

function collectLiveOpsAttentionCandidates(
  pathname: string,
  search: string,
): LiveOpsAttentionCandidate[] {
  const nowMs = Date.now();
  const deduped = new Map<string, LiveOpsAttentionCandidate>();

  for (const entry of loadRecentRuns()) {
    const recordedAtMs = Date.parse(entry.recordedAt);
    if (
      Number.isFinite(recordedAtMs) &&
      nowMs - recordedAtMs > LIVE_OPS_ATTENTION_MAX_AGE_MS
    ) {
      continue;
    }

    if (entry.status === 'finished' || entry.status === 'error') {
      continue;
    }

    const candidate: LiveOpsAttentionCandidate = {
      actorId: trimOptional(entry.actorId),
      runId: trimOptional(entry.runId),
      scopeId: trimOptional(entry.scopeId),
      serviceId: trimOptional(entry.serviceOverrideId),
    };
    const key = buildLiveOpsAttentionCandidateKey(candidate);
    if (!deduped.has(key)) {
      deduped.set(key, candidate);
    }

    if (deduped.size >= LIVE_OPS_ATTENTION_MAX_CANDIDATES) {
      break;
    }
  }

  if (pathname === '/runtime/mission-control') {
    const context = readMissionControlRouteContext(search);
    const candidate: LiveOpsAttentionCandidate = {
      actorId: trimOptional(context.actorId),
      runId: trimOptional(context.runId),
      scopeId: trimOptional(context.scopeId),
      serviceId: trimOptional(context.serviceId),
    };
    const key = buildLiveOpsAttentionCandidateKey(candidate);
    if (
      (candidate.actorId || (candidate.scopeId && candidate.runId)) &&
      !deduped.has(key)
    ) {
      deduped.set(key, candidate);
    }
  }

  return Array.from(deduped.values()).slice(
    0,
    LIVE_OPS_ATTENTION_MAX_CANDIDATES,
  );
}

async function resolveLiveOpsAttentionActorId(
  candidate: LiveOpsAttentionCandidate,
): Promise<string | undefined> {
  const actorId = trimOptional(candidate.actorId);
  if (actorId) {
    return actorId;
  }

  const scopeId = trimOptional(candidate.scopeId);
  const runId = trimOptional(candidate.runId);
  if (!scopeId || !runId) {
    return undefined;
  }

  try {
    const summary = await runtimeRunsApi.getRunSummary(scopeId, runId, {
      serviceId: trimOptional(candidate.serviceId),
    });
    return trimOptional(summary.actorId);
  } catch {
    return undefined;
  }
}

async function runNeedsLiveOpsAttention(
  candidate: LiveOpsAttentionCandidate,
): Promise<boolean> {
  const actorId = await resolveLiveOpsAttentionActorId(candidate);
  if (!actorId) {
    return false;
  }

  try {
    const fetchedAtMs = Date.now();
    const [graph, timeline] = await Promise.all([
      runtimeActorsApi.getActorGraphEnriched(actorId, {
        depth: 4,
        direction: 'Both',
        take: 120,
      }),
      runtimeActorsApi.getActorTimeline(actorId, {
        take: 120,
      }),
    ]);

    const snapshot = buildMissionSnapshotFromRuntime({
      connectionStatus: 'degraded',
      nowMs: fetchedAtMs,
      recentEvents: [],
      routeContext: {
        actorId,
        runId: trimOptional(candidate.runId),
        scopeId: trimOptional(candidate.scopeId),
        serviceId: trimOptional(candidate.serviceId),
      },
      resources: {
        artifacts: {
          fetchedAtMs,
          graph,
          timeline,
        },
        session: {
          runId: trimOptional(candidate.runId),
          status: 'running',
        },
      },
    });

    return (
      snapshot.intervention?.required === true &&
      (snapshot.intervention.kind === 'human_approval' ||
        snapshot.intervention.kind === 'human_input')
    );
  } catch {
    return false;
  }
}

export async function loadLiveOpsAttentionSnapshot(
  pathname: string,
  search: string,
): Promise<LiveOpsAttentionSnapshot> {
  const candidates = collectLiveOpsAttentionCandidates(pathname, search);
  if (candidates.length === 0) {
    return { hasPendingAttention: false, pendingCount: 0 };
  }

  const results = await Promise.allSettled(
    candidates.map((candidate) => runNeedsLiveOpsAttention(candidate)),
  );
  const pendingCount = results.reduce((count, result) => {
    if (result.status === 'fulfilled' && result.value) {
      return count + 1;
    }

    return count;
  }, 0);

  return {
    hasPendingAttention: pendingCount > 0,
    pendingCount,
  };
}
