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

## System Agent Profile Writes

System Agent Profile 的 draft 与写操作使用比通用 Admin 面更窄的端点级授权。每个 `/api/admin/agent-profiles*` endpoint 都必须在任何 query 或 dispatch 之前：

1. 要求认证并提取 bearer；
2. 调用 `IPlatformAdminAuthorizer.ResolveCallerAsync`；
3. 要求 `caller.IsElevated` 且 `caller.UserId` 非空；
4. 要求 `caller.GrantSource == PlatformAdminGrantSources.AllowedUserId`。

因此，仅由 `allowed_email` 或 `nyxid_platform_role` 获得 elevated 的 caller 不能创建、编辑、校验、发布 system Profile，也不能设置 system default 或 rollout。这个收紧不修改全局 authorizer 配置，其他 Admin surface 继续按各自 contract 使用兼容 grant。

System 写审计只记录 allowlisted `userId` 的稳定 hash，不记录 raw userId、Profile instructions、skill body、token、credential 或远端响应。普通登录用户仍可读取 published system 摘要，并把允许的 system Profile 绑定为自己的默认；这不授予 system draft 可见性或写权限。

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
