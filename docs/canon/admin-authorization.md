---
title: "Aevatar Admin Authorization"
status: active
owner: eanzhao
---

# Aevatar Admin Authorization

NyxID is the server-side current-user identity source. Aevatar admin authorization is an aevatar-owned policy decision under `Aevatar:AdminAccess`.

## Phase 1 Contract

`IPlatformAdminAuthorizer` remains the compatibility seam for existing admin-gated endpoints. Its implementation may call NyxID `/api/v1/users/me` with the caller bearer, but that call only resolves who the caller is.

The admin grant is decided in this order:

1. `Aevatar:AdminAccess:AllowedUserIds`
2. `Aevatar:AdminAccess:AllowedEmails` after trim + lowercase normalization
3. `Aevatar:AdminAccess:TrustNyxIdPlatformRole=true` transitional fallback for NyxID `admin` / `operator`

The default transitional fallback is `true` for Phase 1. Production hardening sets it to `false` in Phase 3 after explicit allowlists are deployed.

`PlatformCaller.GrantSource` reports which grant admitted the caller: `allowed_user_id`, `allowed_email`, or `nyxid_platform_role`. Denials use an empty grant source.

## Failure Semantics

Admin authorization fails closed:

- missing or blank bearer is not elevated
- missing authorizer on an admin-only surface returns `503`
- NyxID provider errors are not elevated
- malformed identity responses are not elevated
- identity responses without user id and email are not elevated
- denials are never cached

Positive grants may be cached per bearer token for `AdminRoleCacheTtlSeconds`. The cache is positive-only so transient provider failures or newly granted access are not pinned as denials.

## Removed Rebuild Token Authority

`POST /api/oauth/aevatar-client/rebuild` is authorized only through `IPlatformAdminAuthorizer`. The legacy `X-Aevatar-Admin-Token` / `ChannelIdentity:Admin:RebuildToken` path is not an authority and must not authorize rebuild.

The rebuild endpoint still returns an honest accepted ACK: `202` only means the provision command was accepted for dispatch. It does not mean the OAuth client actor committed or the read model observed the new snapshot.

## Future Migration

Phase 2 should rename the seam to an authentication-boundary model such as `IAevatarAdminAuthorizer` plus `ICallerIdentityResolver`, removing the platform/AI-tool-provider naming debt. Phase 3 should set production `TrustNyxIdPlatformRole=false`.
