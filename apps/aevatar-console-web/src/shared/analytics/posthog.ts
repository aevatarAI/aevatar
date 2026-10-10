import type { CaptureResult, PostHogConfig } from 'posthog-js';
import posthog from 'posthog-js';
import type { ConsoleEventProperties } from './types';

let initialized = false;

function withoutUrlParameters(value: string): string {
  try {
    const url = new URL(value);
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return value;
    return `${url.origin}${url.pathname}`;
  } catch {
    return value;
  }
}

function sanitizeTelemetryValue(value: unknown): unknown {
  if (typeof value === 'string') {
    return value
      .replace(/https?:\/\/[^\s<>"']+/g, withoutUrlParameters)
      .replace(/Bearer\s+[\w.+/=-]+/gi, 'Bearer [redacted]')
      .replace(/\beyJ[\w-]+\.[\w-]+\.[\w-]+\b/g, '[redacted]');
  }
  if (Array.isArray(value)) return value.map(sanitizeTelemetryValue);
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value).map(([key, entry]) => [
        key,
        /^(authorization|cookie|password|access_?token|refresh_?token|id_?token|api_?key)$/i.test(
          key,
        )
          ? '[redacted]'
          : sanitizeTelemetryValue(entry),
      ]),
    );
  }
  return value;
}

function sanitizeEvent(event: CaptureResult | null): CaptureResult | null {
  if (!event) return null;
  // OAuth callback data must never enter analytics or replay.
  if (/\/auth\/callback\/?$/.test(window.location.pathname)) return null;
  return {
    ...event,
    ...(event.$set ? { $set: sanitizeProperties(event.$set) } : {}),
    ...(event.$set_once
      ? { $set_once: sanitizeProperties(event.$set_once) }
      : {}),
    properties: {
      ...sanitizeProperties(event.properties),
      app: 'aevatar-console',
      environment:
        process.env.AEVATAR_POSTHOG_ENVIRONMENT?.trim() ||
        process.env.NODE_ENV ||
        'development',
    },
  };
}

function sanitizeProperties(properties: CaptureResult['properties']) {
  return Object.fromEntries(
    Object.entries(properties).map(([key, value]) => [
      key,
      sanitizeTelemetryValue(value),
    ]),
  );
}

export function initializeConsoleAnalytics(): void {
  if (initialized || typeof window === 'undefined') return;
  const key = process.env.AEVATAR_POSTHOG_KEY?.trim();
  const host = process.env.AEVATAR_POSTHOG_HOST?.trim();
  if (!key || !host) return;

  try {
    const config: Partial<PostHogConfig> = {
      api_host: host,
      capture_pageview: 'history_change',
      capture_pageleave: true,
      // Explicit product events avoid collecting arbitrary console content.
      autocapture: false,
      capture_performance: {
        web_vitals: true,
        web_vitals_allowed_metrics: ['LCP', 'INP', 'CLS'],
        web_vitals_attribution: false,
      },
      capture_exceptions: true,
      disable_session_recording: false,
      enable_recording_console_log: false,
      session_recording: {
        maskAllInputs: true,
        maskTextSelector: '*',
        maskAllElementAttributes: true,
        blockSelector: '.ph-no-capture, .monaco-editor',
        recordHeaders: false,
        recordBody: false,
        maskCapturedNetworkRequestFn: (request) => ({
          ...request,
          name: withoutUrlParameters(request.name),
          requestHeaders: undefined,
          responseHeaders: undefined,
          requestBody: undefined,
          responseBody: undefined,
        }),
      },
      before_send: sanitizeEvent,
      save_campaign_params: false,
      save_referrer: false,
      person_profiles: 'identified_only',
    };
    posthog.init(key, config);
    initialized = true;
  } catch {
    // Monitoring must not block authentication or workflow execution.
  }
}

export function syncConsoleAnalyticsUser(userId?: string): void {
  if (!initialized) return;
  try {
    const identifiedUserId = posthog.get_property('$user_id');
    if (userId === identifiedUserId) return;
    if (identifiedUserId) posthog.reset();
    if (userId) posthog.identify(userId);
  } catch {
    // Identity transitions must remain usable if analytics is unavailable.
  }
}

export function captureConsoleEvent<Event extends keyof ConsoleEventProperties>(
  event: Event,
  properties: ConsoleEventProperties[Event],
): void {
  if (!initialized) return;
  try {
    posthog.capture(event, properties);
  } catch {
    // A blocked or failing collector cannot change the result of a run.
  }
}
