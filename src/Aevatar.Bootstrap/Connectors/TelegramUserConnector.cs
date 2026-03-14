using System.Globalization;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;
using TL;

namespace Aevatar.Bootstrap.Connectors;

/// <summary>
/// In-process Telegram user-account connector built on MTProto.
/// Supports /sendMessage and /getUpdates (Bot API compatible response shape).
/// </summary>
public sealed class TelegramUserConnector : IConnector
{
    private const int MaxPollTimeoutSeconds = 25;
    private const int DefaultPollSleepMs = 200;
    private const int MaxBufferedUpdates = 4000;
    private readonly int _apiId;
    private readonly string _apiHash;
    private readonly string _phoneNumber;
    private readonly string _verificationCode;
    private readonly string _password;
    private readonly string _sessionPath;
    private readonly string _deviceModel;
    private readonly string _systemVersion;
    private readonly string _appVersion;
    private readonly string _systemLangCode;
    private readonly string _langCode;
    private readonly HashSet<string> _allowedOperations;
    private readonly int _defaultTimeoutMs;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly object _updatesGate = new();
    private readonly List<TelegramInboundUpdate> _updates = [];
    private long _nextUpdateId = 1;
    private WTelegram.Client? _client;
    private WTelegram.UpdateManager? _manager;

    public TelegramUserConnector(
        string name,
        int apiId,
        string apiHash,
        string phoneNumber,
        string verificationCode,
        string password,
        string sessionPath,
        string deviceModel,
        string systemVersion,
        string appVersion,
        string systemLangCode,
        string langCode,
        IEnumerable<string>? allowedOperations,
        int timeoutMs,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required", nameof(name));
        if (apiId <= 0)
            throw new ArgumentOutOfRangeException(nameof(apiId), apiId, "apiId must be > 0");
        if (string.IsNullOrWhiteSpace(apiHash))
            throw new ArgumentException("apiHash is required", nameof(apiHash));

        Name = name;
        _apiId = apiId;
        _apiHash = apiHash.Trim();
        _phoneNumber = phoneNumber?.Trim() ?? string.Empty;
        _verificationCode = verificationCode?.Trim() ?? string.Empty;
        _password = password?.Trim() ?? string.Empty;
        _sessionPath = sessionPath?.Trim() ?? string.Empty;
        _deviceModel = deviceModel?.Trim() ?? string.Empty;
        _systemVersion = systemVersion?.Trim() ?? string.Empty;
        _appVersion = appVersion?.Trim() ?? string.Empty;
        _systemLangCode = systemLangCode?.Trim() ?? string.Empty;
        _langCode = langCode?.Trim() ?? string.Empty;
        _allowedOperations = new HashSet<string>(
            (allowedOperations ?? ["/sendMessage", "/getUpdates", "/ensureLogin"]).Select(NormalizeOperation),
            StringComparer.OrdinalIgnoreCase);
        _defaultTimeoutMs = Math.Clamp(timeoutMs, 100, 300_000);
        _logger = logger;
    }

    public string Name { get; }
    public string Type => "telegram_user";

    public async Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
    {
        var operation = NormalizeOperation(request.Operation);
        if (!string.Equals(operation, "/ensureLogin", StringComparison.OrdinalIgnoreCase) &&
            !_allowedOperations.Contains(operation))
        {
            return Fail($"telegram_user operation '{operation}' is not allowed");
        }

        var runtimeLogin = ResolveRuntimeLoginOverrides(request.Parameters);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutMs = ResolveTimeoutMs(request.Parameters);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            await EnsureReadyAsync(runtimeLogin, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail($"telegram_user init timeout after {timeoutMs}ms");
        }
        catch (Exception ex)
        {
            return Fail($"telegram_user init failed: {BuildInitFailureMessage(ex)}");
        }

        return operation switch
        {
            "/ensureLogin" => ExecuteEnsureLogin(),
            "/sendMessage" => await ExecuteSendMessageAsync(request, timeoutCts.Token),
            "/getUpdates" => await ExecuteGetUpdatesAsync(request, timeoutCts.Token),
            _ => Fail($"telegram_user unsupported operation '{operation}'"),
        };
    }

    private static ConnectorResponse ExecuteEnsureLogin()
    {
        var output = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["result"] = new Dictionary<string, object?>
            {
                ["status"] = "authorized",
            },
        });

        return Ok(output, new Dictionary<string, string>
        {
            ["connector.telegram_user.operation"] = "/ensureLogin",
            ["connector.telegram_user.auth"] = "authorized",
        });
    }

    private async Task EnsureReadyAsync(TelegramRuntimeLoginOverrides runtimeLogin, CancellationToken ct)
    {
        if (_client != null && _manager != null)
            return;

        await _initGate.WaitAsync(ct);
        try
        {
            if (_client != null && _manager != null)
                return;

            EnsureSessionDirectory();
            WTelegram.Client? newClient = null;
            try
            {
                newClient = new WTelegram.Client(key => GetConfigValue(key, runtimeLogin));
                var manager = newClient.WithUpdateManager(HandleUpdateAsync);
                var me = await newClient.LoginUserIfNeeded();

                // Prime known users/chats with access hashes for future sendMessage resolutions.
                var dialogs = await newClient.Messages_GetAllDialogs();
                dialogs.CollectUsersChats(manager.Users, manager.Chats);

                _client = newClient;
                _manager = manager;
                _logger.LogInformation(
                    "telegram_user connector '{Connector}' initialized with account id={UserId}",
                    Name,
                    me.id);
                newClient = null;
            }
            catch
            {
                // If initialization fails, release file lock immediately (session file).
                newClient?.Dispose();
                throw;
            }
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<ConnectorResponse> ExecuteSendMessageAsync(ConnectorRequest request, CancellationToken ct)
    {
        if (!TryReadSendMessagePayload(request.Payload, out var payload, out var error))
            return Fail(error);
        if (string.IsNullOrWhiteSpace(payload.ChatId))
            return Fail("telegram_user payload.chat_id is required");

        if (!long.TryParse(payload.ChatId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var botStyleChatId))
            return Fail("telegram_user payload.chat_id is invalid");

        await RefreshKnownPeersAsync();
        if (!TryResolveInputPeer(botStyleChatId, out var peer))
            return Fail($"telegram_user cannot resolve chat_id '{payload.ChatId}' in account dialogs");

        var client = _client ?? throw new InvalidOperationException("telegram_user client not initialized");
        var text = payload.Text ?? string.Empty;
        MessageEntity[]? entities = null;
        if (string.Equals(payload.ParseMode, "Markdown", StringComparison.OrdinalIgnoreCase))
            entities = client.MarkdownToEntities(ref text);
        else if (string.Equals(payload.ParseMode, "HTML", StringComparison.OrdinalIgnoreCase))
            entities = client.HtmlToEntities(ref text);

        Message sent;
        try
        {
            sent = await client.SendMessageAsync(peer, text, entities: entities);
        }
        catch (Exception ex)
        {
            return Fail($"telegram_user sendMessage failed: {ex.Message}");
        }

        var output = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["result"] = new Dictionary<string, object?>
            {
                ["message_id"] = sent.id,
                    ["date"] = ToUnixSeconds(sent.date),
                ["text"] = sent.message ?? text,
                ["chat"] = new Dictionary<string, object?>
                {
                    ["id"] = botStyleChatId,
                },
            },
        });

        return Ok(output, new Dictionary<string, string>
        {
            ["connector.telegram_user.operation"] = "/sendMessage",
            ["connector.telegram_user.chat_id"] = botStyleChatId.ToString(CultureInfo.InvariantCulture),
        });
    }

    private async Task<ConnectorResponse> ExecuteGetUpdatesAsync(ConnectorRequest request, CancellationToken ct)
    {
        var getUpdates = ReadGetUpdatesPayload(request.Payload);
        var offset = Math.Max(0, getUpdates.Offset);
        var timeoutSeconds = Math.Clamp(getUpdates.TimeoutSeconds, 0, MaxPollTimeoutSeconds);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        List<TelegramInboundUpdate> batch;
        while (true)
        {
            batch = SnapshotUpdates(offset);
            if (batch.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                break;

            var remaining = deadline - DateTimeOffset.UtcNow;
            var sleepMs = (int)Math.Clamp(remaining.TotalMilliseconds, 1, DefaultPollSleepMs);
            await Task.Delay(sleepMs, ct);
        }

        var output = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["result"] = batch.Select(ToBotApiUpdate).ToList(),
        });

        return Ok(output, new Dictionary<string, string>
        {
            ["connector.telegram_user.operation"] = "/getUpdates",
            ["connector.telegram_user.offset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["connector.telegram_user.count"] = batch.Count.ToString(CultureInfo.InvariantCulture),
        });
    }

    private async Task HandleUpdateAsync(Update update)
    {
        try
        {
            switch (update)
            {
                case UpdateNewMessage unm:
                    EnqueueUpdate(unm.message);
                    break;
                case UpdateEditMessage uem:
                    EnqueueUpdate(uem.message);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "telegram_user failed to process update callback");
        }

        await Task.CompletedTask;
    }

    private void EnqueueUpdate(MessageBase messageBase)
    {
        if (messageBase is not Message msg)
            return;
        if (string.IsNullOrWhiteSpace(msg.message))
            return;
        if (msg.Peer == null)
            return;

        var botStyleChatId = ToBotStyleChatId(msg.Peer);
        if (string.IsNullOrWhiteSpace(botStyleChatId))
            return;

        var fromPeer = msg.From ?? msg.Peer;
        var update = new TelegramInboundUpdate(
            UpdateId: Interlocked.Increment(ref _nextUpdateId),
            MessageId: msg.id,
            DateUnix: ToUnixSeconds(msg.date),
            ChatId: botStyleChatId,
            FromUserId: ToPlainPeerId(fromPeer),
            FromUsername: ResolveUsername(fromPeer),
            Content: msg.message ?? string.Empty);

        lock (_updatesGate)
        {
            _updates.Add(update);
            if (_updates.Count > MaxBufferedUpdates)
            {
                var removeCount = _updates.Count - MaxBufferedUpdates;
                _updates.RemoveRange(0, removeCount);
            }
        }
    }

    private List<TelegramInboundUpdate> SnapshotUpdates(long offset)
    {
        lock (_updatesGate)
        {
            return _updates
                .Where(x => x.UpdateId >= offset)
                .OrderBy(x => x.UpdateId)
                .Take(200)
                .ToList();
        }
    }

    private object ToBotApiUpdate(TelegramInboundUpdate update)
    {
        return new Dictionary<string, object?>
        {
            ["update_id"] = update.UpdateId,
            ["message"] = new Dictionary<string, object?>
            {
                ["message_id"] = update.MessageId,
                ["date"] = update.DateUnix,
                ["chat"] = new Dictionary<string, object?>
                {
                    ["id"] = ParseNumberOrKeepString(update.ChatId),
                },
                ["from"] = new Dictionary<string, object?>
                {
                    ["id"] = ParseNumberOrKeepString(update.FromUserId),
                    ["username"] = update.FromUsername,
                },
                ["text"] = update.Content,
            },
        };
    }

    private string ResolveUsername(Peer peer)
    {
        var manager = _manager;
        if (manager == null)
            return string.Empty;

        return manager.UserOrChat(peer) switch
        {
            User user => user.username ?? string.Empty,
            Channel channel => channel.username ?? string.Empty,
            _ => string.Empty,
        };
    }

    private async Task RefreshKnownPeersAsync()
    {
        var client = _client;
        var manager = _manager;
        if (client == null || manager == null)
            return;

        var dialogs = await client.Messages_GetAllDialogs();
        dialogs.CollectUsersChats(manager.Users, manager.Chats);
    }

    private bool TryResolveInputPeer(long botStyleChatId, out InputPeer inputPeer)
    {
        inputPeer = null!;
        var manager = _manager;
        if (manager == null)
            return false;

        if (botStyleChatId <= -1_000_000_000_000L)
        {
            var channelId = -1_000_000_000_000L - botStyleChatId;
            if (manager.Chats.TryGetValue(channelId, out var channel))
            {
                inputPeer = channel;
                return true;
            }
        }
        else if (botStyleChatId < 0)
        {
            var chatId = -botStyleChatId;
            if (manager.Chats.TryGetValue(chatId, out var chat))
            {
                inputPeer = chat;
                return true;
            }
        }
        else
        {
            if (manager.Users.TryGetValue(botStyleChatId, out var user))
            {
                inputPeer = user;
                return true;
            }

            if (manager.Chats.TryGetValue(botStyleChatId, out var chat))
            {
                inputPeer = chat;
                return true;
            }
        }

        return false;
    }

    private string? GetConfigValue(string key, TelegramRuntimeLoginOverrides runtimeLogin)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return key switch
        {
            "api_id" => _apiId.ToString(CultureInfo.InvariantCulture),
            "api_hash" => _apiHash,
            "phone_number" => Coalesce(
                runtimeLogin.PhoneNumber,
                Environment.GetEnvironmentVariable("AEVATAR_TELEGRAM_USER_PHONE_NUMBER"),
                _phoneNumber),
            "verification_code" => NormalizeVerificationCode(Coalesce(
                runtimeLogin.VerificationCode,
                Environment.GetEnvironmentVariable("AEVATAR_TELEGRAM_USER_VERIFICATION_CODE"),
                Environment.GetEnvironmentVariable("TELEGRAM_VERIFICATION_CODE"),
                _verificationCode)),
            "password" => Coalesce(
                runtimeLogin.Password,
                Environment.GetEnvironmentVariable("AEVATAR_TELEGRAM_USER_2FA_PASSWORD"),
                Environment.GetEnvironmentVariable("TELEGRAM_2FA_PASSWORD"),
                _password),
            "session_pathname" => _sessionPath,
            "device_model" => NullIfWhitespace(_deviceModel),
            "system_version" => NullIfWhitespace(_systemVersion),
            "app_version" => NullIfWhitespace(_appVersion),
            "system_lang_code" => NullIfWhitespace(_systemLangCode),
            "lang_code" => NullIfWhitespace(_langCode),
            _ => null,
        };
    }

    private int ResolveTimeoutMs(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("timeout_ms", out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return Math.Clamp(parsed, 100, 300_000);
        }

        return _defaultTimeoutMs;
    }

    private static TelegramRuntimeLoginOverrides ResolveRuntimeLoginOverrides(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters == null || parameters.Count == 0)
            return TelegramRuntimeLoginOverrides.Empty;

        return new TelegramRuntimeLoginOverrides(
            PhoneNumber: ReadConnectorParameterValue(
                parameters,
                "phone_number",
                "telegram.phone_number",
                "telegram_user.phone_number"),
            VerificationCode: NormalizeVerificationCode(ReadConnectorParameterValue(
                parameters,
                "verification_code",
                "telegram.verification_code",
                "telegram_user.verification_code")),
            Password: ReadConnectorParameterValue(
                parameters,
                "password",
                "2fa_password",
                "telegram.password",
                "telegram.2fa_password",
                "telegram_user.password",
                "telegram_user.2fa_password"));
    }

    private static string ReadConnectorParameterValue(
        IReadOnlyDictionary<string, string> parameters,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out var exact) && !string.IsNullOrWhiteSpace(exact))
                return exact.Trim();
        }

        foreach (var (existingKey, value) in parameters)
        {
            foreach (var key in keys)
            {
                if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return string.Empty;
    }

    private static bool TryReadSendMessagePayload(string payload, out SendMessagePayload parsed, out string error)
    {
        parsed = new SendMessagePayload();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "telegram_user payload is required for /sendMessage";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "telegram_user /sendMessage payload must be JSON object";
                return false;
            }

            parsed = new SendMessagePayload
            {
                ChatId = ReadJsonStringOrNumber(doc.RootElement, "chat_id"),
                Text = ReadJsonString(doc.RootElement, "text"),
                ParseMode = ReadJsonString(doc.RootElement, "parse_mode"),
            };
            return true;
        }
        catch (Exception ex)
        {
            error = $"telegram_user /sendMessage payload parse failed: {ex.Message}";
            return false;
        }
    }

    private static GetUpdatesPayload ReadGetUpdatesPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new GetUpdatesPayload();

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new GetUpdatesPayload();

            return new GetUpdatesPayload
            {
                Offset = ReadJsonInt64(doc.RootElement, "offset"),
                TimeoutSeconds = (int)Math.Clamp(ReadJsonInt64(doc.RootElement, "timeout"), 0, MaxPollTimeoutSeconds),
            };
        }
        catch
        {
            return new GetUpdatesPayload();
        }
    }

    private void EnsureSessionDirectory()
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            return;

        var directory = Path.GetDirectoryName(_sessionPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static string ToBotStyleChatId(Peer peer)
    {
        return peer switch
        {
            PeerChannel channel => (-1_000_000_000_000L - channel.channel_id).ToString(CultureInfo.InvariantCulture),
            PeerChat chat => (-chat.chat_id).ToString(CultureInfo.InvariantCulture),
            PeerUser user => user.user_id.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private static string ToPlainPeerId(Peer peer)
    {
        return peer switch
        {
            PeerChannel channel => channel.channel_id.ToString(CultureInfo.InvariantCulture),
            PeerChat chat => chat.chat_id.ToString(CultureInfo.InvariantCulture),
            PeerUser user => user.user_id.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private static string NormalizeOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            return "/sendMessage";
        var normalized = operation.Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        return normalized;
    }

    private static string ReadJsonString(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static string ReadJsonStringOrNumber(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static long ReadJsonInt64(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static object ParseNumberOrKeepString(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number;
        return value;
    }

    private static long ToUnixSeconds(DateTime dateTime)
    {
        var utc = dateTime.Kind == DateTimeKind.Utc
            ? dateTime
            : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }

    private static string Coalesce(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static string NormalizeVerificationCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Telegram code is often copied with spaces between digits.
        return new string(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private static string BuildInitFailureMessage(Exception ex)
    {
        var detail = ex.Message?.Trim() ?? "unknown error";
        if (detail.Contains("PHONE_CODE_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            return $"{detail}. Use a fresh login code and retry login (workflow human_input or AEVATAR_TELEGRAM_USER_VERIFICATION_CODE).";
        }

        if (detail.Contains("SESSION_PASSWORD_NEEDED", StringComparison.OrdinalIgnoreCase))
        {
            return $"{detail}. Provide Telegram 2FA password (workflow human_input or AEVATAR_TELEGRAM_USER_2FA_PASSWORD).";
        }

        return detail;
    }

    private static string? NullIfWhitespace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static ConnectorResponse Fail(string error) =>
        new()
        {
            Success = false,
            Error = error,
        };

    private static ConnectorResponse Ok(string output, Dictionary<string, string> metadata) =>
        new()
        {
            Success = true,
            Output = output,
            Metadata = metadata,
        };

    private sealed record TelegramRuntimeLoginOverrides(
        string PhoneNumber,
        string VerificationCode,
        string Password)
    {
        public static TelegramRuntimeLoginOverrides Empty { get; } =
            new(string.Empty, string.Empty, string.Empty);
    }

    private sealed record SendMessagePayload
    {
        public string ChatId { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public string ParseMode { get; init; } = string.Empty;
    }

    private sealed record GetUpdatesPayload
    {
        public long Offset { get; init; }
        public int TimeoutSeconds { get; init; }
    }

    private sealed record TelegramInboundUpdate(
        long UpdateId,
        int MessageId,
        long DateUnix,
        string ChatId,
        string FromUserId,
        string FromUsername,
        string Content);
}
