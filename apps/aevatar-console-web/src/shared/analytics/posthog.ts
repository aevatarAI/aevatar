import type { CaptureResult, PostHogConfig } from 'posthog-js';
import posthog from 'posthog-js';
import type { ConsoleEventProperties } from './types';

let initialized = false;

const SECRET_PROPERTY =
  /^(authorization|cookie|password|access_?token|refresh_?token|id_?token|api_?key)$/i;

function withoutUrlParameters(value: string): string {
  try {
    const url = new URL(value, window.location.href);
    if (url.protocol !== 'http:' && url.protocol !== 'https:')
      return '[redacted]';
    return `${url.origin}${url.pathname}`;
  } catch {
    return '[redacted]';
  }
}

function sanitizeTelemetryText(value: string): string {
  return value
    .replace(/https?:\/\/[^\s<>"')\]}]+/g, withoutUrlParameters)
    .replace(
      /(^|[\s("'=])((?:\/|\.\.?\/)[^\s<>"')\]}]*[?#][^\s<>"')\]}]*)/g,
      (_match, prefix: string, url: string) =>
        `${prefix}${url.split(/[?#]/, 1)[0]}`,
    )
    .replace(
      /(\b(?:authorization|cookie|password|access_?token|refresh_?token|id_?token|api_?key)["']?\s*[:=]\s*)("[^"]*"|'[^']*'|(?:Bearer\s+)?(?:\[redacted\]|[^\s,;&}\]]+))/gi,
      (_match, prefix: string, secret: string) => {
        const quote = secret[0];
        return `${prefix}${quote === '"' || quote === "'" ? `${quote}[redacted]${quote}` : '[redacted]'}`;
      },
    )
    .replace(/Bearer\s+[\w.+/=-]+/gi, 'Bearer [redacted]')
    .replace(/\beyJ[\w-]+\.[\w-]+\.[\w-]+\b/g, '[redacted]');
}

function sanitizeTelemetryValue(value: unknown): unknown {
  if (typeof value === 'string') {
    return sanitizeTelemetryText(value);
  }
  if (Array.isArray(value)) return value.map(sanitizeTelemetryValue);
  if (value && typeof value === 'object') {
    return sanitizeProperties(value);
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

function sanitizeProperties(properties: object): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(properties).map(([key, value]) => [
      key,
      SECRET_PROPERTY.test(key) ? '[redacted]' : sanitizeTelemetryValue(value),
    ]),
  );
}

function maskReplayAttribute(name: string, value: string): string {
  const attribute = name.toLowerCase();
  if (attribute === 'style' || attribute === '_csstext') {
    // rrweb compresses snapshots before before_send, so scrub CSS at capture.
    return sanitizeTelemetryText(value)
      .replace(
        /url\(\s*(['"]?)(.*?)\1\s*\)/gi,
        (_match, _quote, url: string) =>
          `url("${withoutUrlParameters(url.trim())}")`,
      )
      .replace(
        /(@import\s+)(['"])(.*?)\2/gi,
        (_match, prefix, _quote, url) =>
          `${prefix}"${withoutUrlParameters(url)}"`,
      );
  }
  if (/^(src|href|xlink:href|poster|rr_src)$/.test(attribute)) {
    return withoutUrlParameters(value);
  }
  if (
    /^(class|id|role|type|rel|media|width|height|viewbox|d|fill|stroke|stroke-width|xmlns|x|y|x1|x2|y1|y2|cx|cy|r|rx|ry|points|transform|preserveaspectratio|colspan|rowspan|tabindex|aria-hidden|aria-expanded|aria-selected|rr_width|rr_height|rr_left|rr_top|rr_position|rr_transform|rr_display|rr_scrollleft|rr_scrolltop|rr_mediastate|rr_open_mode)$/.test(
      attribute,
    )
  ) {
    return sanitizeTelemetryText(value);
  }
  return '*'.repeat(value.length);
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
        // The coarse SDK option also masks class/style and destroys layout.
        maskAllElementAttributes: false,
        maskAttributeFn: maskReplayAttribute,
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
