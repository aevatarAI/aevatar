# Garnet Long Secret TTL Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Garnet-backed secret vault store and rotate 90-day scheduled Agent Keys without changing their exact logical expiration or product lifetime.

**Architecture:** Keep exact expiration in `GarnetSecretVaultRecord` as the authorization fact and treat Garnet expiry only as storage cleanup. At the Garnet adapter boundary, retain millisecond precision through `Int32.MaxValue` milliseconds and encode longer relative expirations as rounded-up whole seconds; apply the same fallback inside compare-and-set Lua after it selects the shorter effective TTL.

**Tech Stack:** .NET 10, C#, StackExchange.Redis 2.10.1, Garnet Redis protocol, xUnit, FluentAssertions, NSubstitute.

## Global Constraints

- Keep the existing 90-day credential policy and exact logical expiration.
- Normalize only long backend TTLs that exceed `Int32.MaxValue` milliseconds to whole seconds.
- Preserve millisecond precision for short TTLs.
- Reject non-persistent TTLs that also exceed Garnet's `Int32.MaxValue` whole-second range.
- Keep the protobuf vault record as the authority for exact expiration; Garnet TTL remains storage cleanup only.
- Do not change Studio, schedule actor, authorization-plan, or NyxID contracts.
- Do not print raw Agent Keys, bearer tokens, or vault ciphertext during verification.
- Do not include the separate generic `scheduled_agent_creator` `AuthorizationFact` fix in this change.

---

### Task 1: Encode Long Set Expirations In Seconds

**Files:**
- Create: `test/Aevatar.Foundation.Runtime.Hosting.Tests/GarnetSecretKeyValueStoreExpirationTests.cs`
- Modify: `src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/GarnetSecretKeyValueStore.cs:5-132`

**Interfaces:**
- Consumes: `IGarnetSecretConnection.GetDatabase(int)` and `IDatabase.StringSetAsync(..., Expiration, ValueCondition, ...)`.
- Produces: `GarnetSecretKeyValueStore.ToExpiration(TimeSpan?)`, preserving the existing private API while returning an `EX` expiration for long TTLs and the existing `PX` expiration for short TTLs.

- [ ] **Step 1: Write the failing wire-encoding tests**

Create `GarnetSecretKeyValueStoreExpirationTests.cs` with the following content:

```csharp
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using FluentAssertions;
using NSubstitute;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class GarnetSecretKeyValueStoreExpirationTests
{
    [Fact]
    public async Task SetIfAbsentAsync_LongRelativeTtl_ShouldUseWholeSeconds()
    {
        Expiration? capturedExpiration = null;
        var store = CreateStore(expiration => capturedExpiration = expiration);

        var created = await store.SetIfAbsentAsync(
            "long-ttl",
            new byte[] { 0x01 },
            TimeSpan.FromDays(90) + TimeSpan.FromMilliseconds(123));

        created.Should().BeTrue();
        capturedExpiration.Should().NotBeNull();
        capturedExpiration!.Value.ToString().Should().Be("EX 7776001");
    }

    [Fact]
    public async Task SetIfAbsentAsync_ShortRelativeTtl_ShouldKeepMillisecondPrecision()
    {
        Expiration? capturedExpiration = null;
        var store = CreateStore(expiration => capturedExpiration = expiration);

        var created = await store.SetIfAbsentAsync(
            "short-ttl",
            new byte[] { 0x01 },
            TimeSpan.FromMilliseconds(1500));

        created.Should().BeTrue();
        capturedExpiration.Should().NotBeNull();
        capturedExpiration!.Value.ToString().Should().Be("PX 1500");
    }

    [Fact]
    public async Task SetIfAbsentAsync_ExpiryBeyondWholeSecondRange_ShouldReject()
    {
        var store = CreateStore(_ => { });

        var act = () => store.SetIfAbsentAsync(
            "unsupported-ttl",
            new byte[] { 0x01 },
            TimeSpan.FromSeconds((double)int.MaxValue + 1));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*whole-second range*");
    }

    private static GarnetSecretKeyValueStore CreateStore(Action<Expiration> captureExpiration)
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(),
                Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                captureExpiration(call.ArgAt<Expiration>(2));
                return Task.FromResult(true);
            });
        var connection = Substitute.For<IGarnetSecretConnection>();
        connection.GetDatabase(Arg.Any<int>()).Returns(database);
        return new GarnetSecretKeyValueStore(connection, CreateOptions());
    }

    private static GarnetSecretStoreOptions CreateOptions() => new()
    {
        KeyringPath = "/unused/keyring.json",
        SecretVaultPrefix = "test:secret-vault",
        RuntimeSecretPrefix = "test:runtime-secrets",
    };
}
```

- [ ] **Step 2: Run the tests and verify the long-TTL test fails for the production reason**

Run:

```bash
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~GarnetSecretKeyValueStoreExpirationTests"
```

Expected: `SetIfAbsentAsync_LongRelativeTtl_ShouldUseWholeSeconds` fails because the captured value is `PX 7776000123`, and the unsupported-range test fails because no local exception is raised; the short-TTL test passes with `PX 1500`.

- [ ] **Step 3: Implement the minimal long-TTL conversion**

In `GarnetSecretKeyValueStore`, add the exact compatibility threshold and replace `ToExpiration`:

```csharp
private const long MaximumRelativeExpiryMilliseconds = int.MaxValue;
private const long MaximumRelativeExpiryTicks =
    MaximumRelativeExpiryMilliseconds * TimeSpan.TicksPerMillisecond;

private static Expiration ToExpiration(TimeSpan? expiry)
{
    if (!expiry.HasValue || expiry.Value == TimeSpan.MaxValue)
        return Expiration.Default;

    var ttl = expiry.Value;
    if (ttl.Ticks <= MaximumRelativeExpiryTicks)
        return new Expiration(ttl);

    return new Expiration(TimeSpan.FromSeconds(ToGarnetCompatibleWholeSeconds(ttl)));
}

private static long ToGarnetCompatibleWholeSeconds(TimeSpan ttl)
{
    var wholeSeconds = ttl.Ticks / TimeSpan.TicksPerSecond;
    if (ttl.Ticks % TimeSpan.TicksPerSecond != 0)
        wholeSeconds = checked(wholeSeconds + 1);
    if (wholeSeconds > int.MaxValue)
    {
        throw new ArgumentOutOfRangeException(
            nameof(ttl),
            "Expiry exceeds Garnet's supported whole-second range.");
    }
    return wholeSeconds;
}
```

Keep zero and negative behavior unchanged: they continue into StackExchange.Redis validation rather than being silently normalized.

- [ ] **Step 4: Run the focused tests and verify both pass**

Run:

```bash
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~GarnetSecretKeyValueStoreExpirationTests"
```

Expected: 3 passed, 0 failed.

- [ ] **Step 5: Commit Task 1**

```bash
git add src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/GarnetSecretKeyValueStore.cs test/Aevatar.Foundation.Runtime.Hosting.Tests/GarnetSecretKeyValueStoreExpirationTests.cs
git commit -m "Fix Garnet long secret TTL encoding"
```

### Task 2: Preserve Long Expirations During Compare-And-Set

**Files:**
- Modify: `test/Aevatar.Foundation.Runtime.Hosting.Tests/GarnetSecretKeyValueStoreExpirationTests.cs`
- Modify: `src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/GarnetSecretKeyValueStore.cs:7-24,84-101`

**Interfaces:**
- Consumes: Task 1's `MaximumRelativeExpiryTicks` boundary and existing millisecond `PTTL`/`ToExpiryMilliseconds` values.
- Produces: compare-and-set Lua that uses `PSETEX` for supported millisecond TTLs and `SET ... EX` for longer effective TTLs while preserving the shorter existing backend expiry.

- [ ] **Step 1: Add failing CAS contract, precision regression, and Garnet integration tests**

Add these tests to `GarnetSecretKeyValueStoreExpirationTests`:

```csharp
[Fact]
public async Task CompareSetAsync_LongRelativeTtl_ShouldCarrySecondsFallbackIntoLua()
{
    string? capturedScript = null;
    RedisValue[]? capturedValues = null;
    var database = Substitute.For<IDatabase>();
    database.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
        .Returns(call =>
        {
            capturedScript = call.ArgAt<string>(0);
            capturedValues = call.ArgAt<RedisValue[]>(2);
            return Task.FromResult(RedisResult.Create((RedisValue)1));
        });
    var connection = Substitute.For<IGarnetSecretConnection>();
    connection.GetDatabase(Arg.Any<int>()).Returns(database);
    var store = new GarnetSecretKeyValueStore(connection, CreateOptions());

    var replaced = await store.CompareSetAsync(
        "long-cas-ttl",
        new byte[] { 0x01 },
        new byte[] { 0x02 },
        TimeSpan.FromDays(90) + TimeSpan.FromMilliseconds(123));

    replaced.Should().BeTrue();
    capturedScript.Should().Contain("effectiveTtl > maximumRelativeMilliseconds");
    capturedScript.Should().Contain("'EX'");
    capturedValues.Should().NotBeNull();
    capturedValues.Should().HaveCount(4);
    capturedValues![3].ToString().Should().Be(int.MaxValue.ToString());
}

[Fact]
public async Task CompareSetAsync_HighSupportedTtlWithSubMillisecondTail_ShouldRoundUpRequestedMilliseconds()
{
    RedisValue[]? capturedValues = null;
    var database = Substitute.For<IDatabase>();
    database.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
        .Returns(call =>
        {
            capturedValues = call.ArgAt<RedisValue[]>(2);
            return Task.FromResult(RedisResult.Create((RedisValue)1));
        });
    var connection = Substitute.For<IGarnetSecretConnection>();
    connection.GetDatabase(Arg.Any<int>()).Returns(database);
    var store = new GarnetSecretKeyValueStore(connection, CreateOptions());
    var expiry = TimeSpan.FromTicks(
        ((long)int.MaxValue - 1) * TimeSpan.TicksPerSecond + 1);

    var replaced = await store.CompareSetAsync(
        "high-range-cas-ttl",
        new byte[] { 0x01 },
        new byte[] { 0x02 },
        expiry);

    replaced.Should().BeTrue();
    capturedValues.Should().NotBeNull();
    capturedValues![2].ToString().Should().Be("2147483646001");
}

[GarnetIntegrationFact]
public async Task SetIfAbsentAndCompareSet_LongRelativeTtl_ShouldRemainNearNinetyDays()
{
    var options = CreateOptions();
    options.ConnectionString =
        Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");
    using var connection = new GarnetSecretConnectionMultiplexer(options);
    var store = new GarnetSecretKeyValueStore(connection, options);
    var key = $"{options.SecretVaultPrefix}:long-ttl:{Guid.NewGuid():N}";
    var original = new byte[] { 0x01 };
    var updated = new byte[] { 0x02 };
    var initialTtl = TimeSpan.FromDays(90) + TimeSpan.FromMilliseconds(123);
    var requestedTtl = TimeSpan.FromDays(120) + TimeSpan.FromMilliseconds(456);

    try
    {
        (await store.SetIfAbsentAsync(key, original, initialTtl)).Should().BeTrue();
        var before = await connection.GetDatabase(options.Database).KeyTimeToLiveAsync(key);
        before.Should().NotBeNull();
        before.Should().BeGreaterThan(TimeSpan.FromDays(89));
        before.Should().BeLessThanOrEqualTo(initialTtl + TimeSpan.FromSeconds(1));
        (await store.CompareSetAsync(key, original, updated, requestedTtl)).Should().BeTrue();

        var after = await connection.GetDatabase(options.Database).KeyTimeToLiveAsync(key);
        after.Should().NotBeNull();
        after.Should().BeGreaterThan(TimeSpan.FromDays(89));
        after.Should().BeLessThanOrEqualTo(before!.Value + TimeSpan.FromSeconds(1));
    }
    finally
    {
        await connection.GetDatabase(options.Database).KeyDeleteAsync(key);
    }
}
```

- [ ] **Step 2: Run the focused tests and verify the CAS unit test fails**

Run:

```bash
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~GarnetSecretKeyValueStoreExpirationTests"
```

Expected: the long-TTL CAS contract test fails because the current script has no `maximumRelativeMilliseconds` branch and passes only three Lua values. The high-range precision regression fails because `ARGV[3]` is `2147483646000` instead of the required ceiling `2147483646001`. The Garnet integration test is skipped when `AEVATAR_TEST_GARNET_CONNECTION_STRING` is unavailable.

- [ ] **Step 3: Add the long-TTL Lua fallback**

Define the shared millisecond threshold beside `MaximumRelativeExpiryTicks`:

```csharp
private const long MaximumRelativeExpiryMilliseconds = int.MaxValue;
private const long MaximumRelativeExpiryTicks =
    MaximumRelativeExpiryMilliseconds * TimeSpan.TicksPerMillisecond;
```

Replace the expiry block in `CompareSetScript` with:

```lua
local maximumRelativeMilliseconds = tonumber(ARGV[4])
if effectiveTtl == -1 then
    redis.call('SET', KEYS[1], ARGV[2])
elseif effectiveTtl > maximumRelativeMilliseconds then
    redis.call('SET', KEYS[1], ARGV[2], 'EX', math.ceil(effectiveTtl / 1000))
else
    redis.call('PSETEX', KEYS[1], math.max(1, effectiveTtl), ARGV[2])
end
```

Pass the threshold as the fourth Lua value in `CompareSetAsync`:

```csharp
[
    expectedValue.ToArray(),
    newValue.ToArray(),
    expiry.HasValue ? ToExpiryMilliseconds(expiry.Value) : -1,
    MaximumRelativeExpiryMilliseconds,
]
```

Validate the long seconds representation before passing the requested millisecond value into Lua:

```csharp
private static long ToExpiryMilliseconds(TimeSpan expiry)
{
    if (expiry <= TimeSpan.Zero)
        throw new ArgumentOutOfRangeException(nameof(expiry), "Expiry must be positive.");
    if (expiry.Ticks > MaximumRelativeExpiryTicks)
        _ = ToGarnetCompatibleWholeSeconds(expiry);

    var wholeMilliseconds = expiry.Ticks / TimeSpan.TicksPerMillisecond;
    if (expiry.Ticks % TimeSpan.TicksPerMillisecond != 0)
        wholeMilliseconds = checked(wholeMilliseconds + 1);

    return wholeMilliseconds;
}
```

- [ ] **Step 4: Run focused and full secret-store tests**

Run:

```bash
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~GarnetSecretKeyValueStoreExpirationTests|FullyQualifiedName~GarnetSecretStoreTests"
```

Expected: all non-environmental tests pass; Garnet integration facts are reported skipped when the connection variable is absent.

When a Garnet endpoint is available, also run:

```bash
AEVATAR_TEST_GARNET_CONNECTION_STRING="localhost:6379,abortConnect=false" dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~SetIfAbsentAndCompareSet_LongRelativeTtl"
```

Expected: 1 passed, 0 failed, proving a near-90-day initial `SET NX EX` and preservation of that shorter existing TTL when CAS requests a supported 120-day TTL.

- [ ] **Step 5: Commit Task 2**

```bash
git add src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/GarnetSecretKeyValueStore.cs test/Aevatar.Foundation.Runtime.Hosting.Tests/GarnetSecretKeyValueStoreExpirationTests.cs
git commit -m "Preserve long Garnet TTLs during secret rotation"
```

### Task 3: Verify, Push, And Re-run Production Acceptance

**Files:**
- Verify: `aevatar.slnx`
- Verify: `tools/ci/test_stability_guards.sh`
- Verify: `tools/ci/architecture_guards.sh`
- Verify: production Studio automation and NyxID Agent Key lifecycle through the local `nyxid` CLI.

**Interfaces:**
- Consumes: Tasks 1-2 and the already deployed NyxID `scope-plan` contract.
- Produces: pushed `origin/feature/integrate` commits plus production evidence for provisioning, run-now execution, revocation, and cleanup.

- [ ] **Step 1: Run repository verification**

```bash
dotnet build aevatar.slnx --nologo
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
git diff --check
```

Expected: every command exits 0. Any environment-gated Garnet facts may be skipped, but no executed test may fail.

- [ ] **Step 2: Review the final diff against the approved spec**

```bash
git diff origin/feature/integrate...HEAD -- src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/GarnetSecretKeyValueStore.cs test/Aevatar.Foundation.Runtime.Hosting.Tests/GarnetSecretKeyValueStoreExpirationTests.cs docs/superpowers/specs/2026-07-22-garnet-long-secret-ttl-compatibility-design.md docs/superpowers/plans/2026-07-22-garnet-long-secret-ttl-compatibility.md
git status --short --branch
```

Expected: only the approved design/plan and TTL implementation/tests are ahead of `origin/feature/integrate`; the worktree is clean.

- [ ] **Step 3: Push the verified commits**

```bash
git push origin feature/integrate
```

Expected: the remote branch advances to the local verified head.

- [ ] **Step 4: Repeat production verification after deployment**

Use `/Users/eanzhao/.local/bin/nyxid` against `https://nyx-api.chrono-ai.fun` and the authenticated Aevatar scope `5d0d7b72-acff-49af-bb1b-9f30bbb7c102`:

1. Snapshot `nyxid api-key list --output json` without displaying raw keys.
2. Create a uniquely named temporary Team and deterministic assign-only workflow member.
3. Wait for the new binding run and service revision to report `succeeded` and `invokeReady=true`.
4. Refresh `/api/auth/nyxid/authorization-catalog:refresh` and call canonical automation preflight.
5. Create the automation with the returned permission digest and policy version.
6. Verify the automation reaches `active` and one new constrained key ID appears with both `allow_all_*` values false.
7. Call `run-now` and verify a new member workflow run completes with the deterministic output.
8. Delete the automation and verify its dedicated key ID is no longer active.
9. Retire the temporary revision, delete the member, archive the Team, and confirm the automation list is empty.

Expected: the former Garnet integer error does not recur; provisioning, execution, revocation, and cleanup all complete. If Kubernetes log access still returns 403, report API/read-model evidence and the log-access limitation separately.
