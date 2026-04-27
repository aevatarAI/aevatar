import type {
  ServiceCatalogSnapshot,
  ServiceEndpointSnapshot,
} from '@/shared/models/services';
import { isChatServiceEndpoint } from '@/shared/runs/scopeConsole';
import type {
  StudioAuthSession,
  StudioMemberBindingRevision,
} from '@/shared/studio/models';

export type StudioBindStreaming = {
  readonly aguiFrames: boolean;
  readonly sse: boolean;
  readonly webSocket: boolean;
};

export type StudioBindContract = {
  readonly authAuthenticated: boolean;
  readonly authEnabled: boolean;
  readonly authHint: string;
  readonly authLabel: string;
  readonly deploymentStatus: string;
  readonly endpointDescription: string;
  readonly endpointDisplayName: string;
  readonly endpointId: string;
  readonly invokePath: string;
  readonly invokeUrl: string;
  readonly method: 'POST';
  readonly primaryActorId: string;
  readonly requestTypeUrl: string;
  readonly responseTypeUrl: string;
  readonly revisionId: string;
  readonly scopeLabel: string;
  readonly scopeSource: string;
  readonly serviceDisplayName: string;
  readonly serviceId: string;
  readonly serviceKey: string;
  readonly streaming: StudioBindStreaming;
};

type BuildStudioBindContractInput = {
  readonly authSession?: StudioAuthSession | null;
  readonly endpoint: ServiceEndpointSnapshot | null;
  readonly memberId?: string | null;
  readonly origin?: string;
  readonly revision?: StudioMemberBindingRevision | null;
  readonly scopeId: string;
  readonly service: ServiceCatalogSnapshot | null;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function encodeSegment(value: string): string {
  return encodeURIComponent(value.trim());
}

function resolveOrigin(explicitOrigin?: string): string {
  const normalizedExplicitOrigin = trimOptional(explicitOrigin);
  if (normalizedExplicitOrigin) {
    return normalizedExplicitOrigin;
  }

  if (typeof window !== 'undefined' && trimOptional(window.location.origin)) {
    return window.location.origin.trim();
  }

  return '';
}

function buildAuthDescriptor(authSession?: StudioAuthSession | null): Pick<
  StudioBindContract,
  'authAuthenticated' | 'authEnabled' | 'authHint' | 'authLabel'
> {
  if (!authSession?.enabled) {
    return {
      authAuthenticated: false,
      authEnabled: false,
      authHint: 'Studio auth is not enabled for this environment.',
      authLabel: 'Auth disabled',
    };
  }

  if (!authSession.authenticated) {
    return {
      authAuthenticated: false,
      authEnabled: true,
      authHint: 'Studio is not authenticated for this scope yet.',
      authLabel: 'Sign-in required',
    };
  }

  const identity =
    trimOptional(authSession.name) ||
    trimOptional(authSession.email) ||
    trimOptional(authSession.scopeId);

  return {
    authAuthenticated: true,
    authEnabled: true,
    authHint: identity ? `Authenticated as ${identity}.` : 'Studio is authenticated.',
    authLabel: 'Authenticated',
  };
}

export function buildStudioBindInvokePath(
  scopeId: string,
  endpointId: string,
  memberId?: string,
  serviceId?: string,
  endpoint?: Pick<ServiceEndpointSnapshot, 'endpointId' | 'kind'> | null,
): string {
  const encodedScopeId = encodeSegment(scopeId);
  const encodedEndpointId = encodeSegment(endpointId);
  const normalizedMemberId = trimOptional(memberId);
  const normalizedServiceId = trimOptional(serviceId);

  if (isChatServiceEndpoint(endpoint)) {
    if (normalizedMemberId) {
      return `/api/scopes/${encodedScopeId}/members/${encodeSegment(
        normalizedMemberId,
      )}/invoke/chat:stream`;
    }

    return normalizedServiceId
      ? `/api/scopes/${encodedScopeId}/services/${encodeSegment(
          normalizedServiceId,
        )}/invoke/chat:stream`
      : `/api/scopes/${encodedScopeId}/invoke/chat:stream`;
  }

  if (normalizedMemberId) {
    return `/api/scopes/${encodedScopeId}/members/${encodeSegment(
      normalizedMemberId,
    )}/invoke/${encodedEndpointId}`;
  }

  return normalizedServiceId
    ? `/api/scopes/${encodedScopeId}/services/${encodeSegment(
        normalizedServiceId,
      )}/invoke/${encodedEndpointId}`
    : `/api/scopes/${encodedScopeId}/invoke/${encodedEndpointId}`;
}

export function buildStudioBindInvokeUrl(
  scopeId: string,
  endpointId: string,
  memberId?: string,
  serviceId?: string,
  endpoint?: Pick<ServiceEndpointSnapshot, 'endpointId' | 'kind'> | null,
  origin?: string,
): string {
  const path = buildStudioBindInvokePath(
    scopeId,
    endpointId,
    memberId,
    serviceId,
    endpoint,
  );
  const resolvedOrigin = resolveOrigin(origin);
  return resolvedOrigin ? `${resolvedOrigin}${path}` : path;
}

export function buildStudioBindContract(
  input: BuildStudioBindContractInput,
): StudioBindContract | null {
  if (!input.service || !input.endpoint) {
    return null;
  }

  const invokePath = buildStudioBindInvokePath(
    input.scopeId,
    input.endpoint.endpointId,
    trimOptional(input.memberId) || undefined,
    input.service.serviceId,
    input.endpoint,
  );
  const invokeUrl = buildStudioBindInvokeUrl(
    input.scopeId,
    input.endpoint.endpointId,
    trimOptional(input.memberId) || undefined,
    input.service.serviceId,
    input.endpoint,
    input.origin,
  );
  const isChatEndpoint = isChatServiceEndpoint(input.endpoint);
  const authDescriptor = buildAuthDescriptor(input.authSession);
  const revisionId =
    trimOptional(input.revision?.revisionId) ||
    trimOptional(input.service.activeServingRevisionId) ||
    trimOptional(input.service.defaultServingRevisionId) ||
    'n/a';

  return {
    authAuthenticated: authDescriptor.authAuthenticated,
    authEnabled: authDescriptor.authEnabled,
    authHint: authDescriptor.authHint,
    authLabel: authDescriptor.authLabel,
    deploymentStatus:
      trimOptional(input.service.deploymentStatus) || 'draft',
    endpointDescription:
      trimOptional(input.endpoint.description) || 'No endpoint description.',
    endpointDisplayName:
      trimOptional(input.endpoint.displayName) ||
      trimOptional(input.endpoint.endpointId) ||
      'Endpoint',
    endpointId: trimOptional(input.endpoint.endpointId),
    invokePath,
    invokeUrl,
    method: 'POST',
    primaryActorId:
      trimOptional(input.service.primaryActorId),
    requestTypeUrl: trimOptional(input.endpoint.requestTypeUrl),
    responseTypeUrl: trimOptional(input.endpoint.responseTypeUrl),
    revisionId,
    scopeLabel: trimOptional(input.scopeId),
    scopeSource: trimOptional(input.authSession?.scopeSource),
    serviceDisplayName:
      trimOptional(input.service.displayName) ||
      trimOptional(input.service.serviceId) ||
      'Member service',
    serviceId: trimOptional(input.service.serviceId),
    serviceKey:
      trimOptional(input.service.serviceKey) ||
      `${trimOptional(input.scopeId)}:${trimOptional(input.service.serviceId)}`,
    streaming: {
      aguiFrames: isChatEndpoint,
      sse: isChatEndpoint,
      webSocket: false,
    },
  };
}
