---
title: "Secret Vault"
status: active
owner: eanzhao
---

# Secret Vault

本文定义 Garnet-backed secret vault/runtime secret store 的 keyring 与维护工具口径。secret 运行时只负责加密存取与解析；密钥生成、加入新数据密钥、在线重加密 sweep 属于离线/运维工具职责，不通过正常 runtime DI 暴露 maintenance port。

## Keyring Schema

生产 keyring 必须使用 canonical JSON：

```json
{
  "activeKeyId": "key-2026-07",
  "keys": {
    "key-2026-07": "<base64 32-byte AES-256-GCM key>"
  },
  "fingerprintKey": "<base64 32-byte HMAC key>"
}
```

约束：

- `activeKeyId` 必须引用 `keys` 字典中的一个数据密钥；
- `keys` 的每个值必须是 32 字节 base64；
- `fingerprintKey` 必填，必须是独立的 32 字节 base64 HMAC key；
- 缺失 `fingerprintKey` 时运行时必须 fail fast，不得回退复用 active 数据密钥；
- 旧的 `keys: [{ keyId, keyBase64 }]` 与 `fingerprintKeyBase64` 形状不是 canonical schema。

## Tool Commands

`tools/secret-store` 只提供三类命令：

```bash
dotnet run --project tools/secret-store -- generate-keyring --output ~/.aevatar/secret-store-keyring.json --active-key-id key-2026-07
dotnet run --project tools/secret-store -- add-key --keyring ~/.aevatar/secret-store-keyring.json --key-id key-2026-08
dotnet run --project tools/secret-store -- reencrypt-sweep --keyring ~/.aevatar/secret-store-keyring.json --connection-string "$GARNET" --verify --checkpoint ./secret-sweep.checkpoint.json
```

命令边界：

- `generate-keyring` 生成一个 active 数据密钥和一个独立 fingerprint key；
- `add-key` 只新增数据密钥并把它设为 active，必须保留原 fingerprint key；
- `reencrypt-sweep` 扫描 secret vault 与 runtime secret 前缀，把旧 `key_id` 记录重加密到当前 active key；
- 本轮不提供 `remove-key`，旧 key 删除必须等 `reencrypt-sweep --verify` 确认旧 `key_id` 记录为 0 后，通过受控原子 keyring 编辑完成。

## Re-encryption Sweep

`reencrypt-sweep` 的生产路径必须由工具侧直接维护 Garnet 数据：

- 使用 Redis `SCAN` 按 `SecretVaultPrefix` 与 `RuntimeSecretPrefix` 分别扫描；
- 每条记录先按 Protobuf 解析并用 keyring 解密，再用当前 active 数据密钥重新 AES-256-GCM 加密；
- 写回必须用 Redis `WATCH/MULTI` compare-and-set，对比原始 value，避免覆盖并发写入；
- compare-and-set 前读取剩余 TTL，事务写回沿用该 expiration；原记录无 TTL 时继续保持无期限；
- 支持 `--dry-run`、`--checkpoint`、`--resume`、`--verify`；
- sweep 失败、CAS 冲突、verify 失败都必须显式暴露，不能静默当成功。

## Caller-allocated references

Callers that coordinate external provisioning may allocate a secret reference before the vault write. `RequestedRef` writes are create-only and idempotent for the exact same secret descriptor. Alias conflicts fail closed; the vault never overwrites a different record under a requested reference.

Raw scheduled keys remain inside an opaque redacted holder until the vault consumer executes. Public DTOs, actor state, projections, command results, exception text, and logging must carry only `SecretReference` and stable external key identifiers.
