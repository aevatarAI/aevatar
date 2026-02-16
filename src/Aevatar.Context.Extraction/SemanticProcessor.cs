using System.Threading.Channels;
using Aevatar.Context.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Extraction;

/// <summary>
/// 异步语义处理器。
/// 自底向上遍历目录树，为每个节点生成 L0/L1 摘要。
/// 使用 Channel 队列化处理请求，避免阻塞主流程。
/// </summary>
public sealed class SemanticProcessor : IDisposable
{
    private readonly IContextStore _store;
    private readonly IContextLayerGenerator _generator;
    private readonly ILogger _logger;
    private readonly Channel<AevatarUri> _queue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;
    private bool _disposed;

    public SemanticProcessor(
        IContextStore store,
        IContextLayerGenerator generator,
        ILogger<SemanticProcessor>? logger = null)
    {
        _store = store;
        _generator = generator;
        _logger = logger ?? NullLogger<SemanticProcessor>.Instance;
        _queue = Channel.CreateUnbounded<AevatarUri>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
    }

    /// <summary>启动后台处理循环。</summary>
    public void Start()
    {
        if (_processingTask != null)
            return;

        _processingTask = Task.Run(ProcessLoopAsync);
        _logger.LogInformation("SemanticProcessor started");
    }

    /// <summary>将 URI（目录或文件）加入处理队列。</summary>
    public ValueTask EnqueueAsync(AevatarUri uri) =>
        _queue.Writer.TryWrite(uri) ? ValueTask.CompletedTask : _queue.Writer.WriteAsync(uri);

    /// <summary>
    /// 同步处理一棵子树：自底向上生成 L0/L1。
    /// 适用于 add_resource 后立即需要索引的场景。
    /// </summary>
    public async Task ProcessTreeAsync(AevatarUri root, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing tree: {Root}", root);
        await ProcessDirectoryAsync(root, ct);
        _logger.LogInformation("Tree processing complete: {Root}", root);
    }

    private async Task ProcessDirectoryAsync(AevatarUri dirUri, CancellationToken ct)
    {
        var entries = await _store.ListAsync(dirUri, ct);
        var childAbstracts = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                await ProcessDirectoryAsync(entry.Uri, ct);

                var childAbstract = await _store.GetAbstractAsync(entry.Uri, ct);
                if (childAbstract != null)
                    childAbstracts.Add(childAbstract);
            }
            else
            {
                await ProcessFileAsync(entry.Uri, ct);

                var fileAbstract = await _store.GetAbstractAsync(entry.Uri, ct);
                if (fileAbstract != null)
                    childAbstracts.Add(fileAbstract);
            }
        }

        if (childAbstracts.Count == 0)
            return;

        var (dirAbstract, dirOverview) = await _generator.GenerateDirectoryLayersAsync(
            dirUri.Name, childAbstracts, ct);

        var abstractUri = dirUri.Join(".abstract.md");
        var overviewUri = dirUri.Join(".overview.md");
        await _store.WriteAsync(abstractUri, dirAbstract, ct);
        await _store.WriteAsync(overviewUri, dirOverview, ct);
    }

    private async Task ProcessFileAsync(AevatarUri fileUri, CancellationToken ct)
    {
        var existing = await _store.GetAbstractAsync(fileUri, ct);
        if (existing != null)
            return;

        try
        {
            var content = await _store.ReadAsync(fileUri, ct);
            var abstractText = await _generator.GenerateAbstractAsync(content, fileUri.Name, ct);

            var parentDir = fileUri.Parent;
            var abstractFileUri = parentDir.Join(".abstract.md");
            await _store.WriteAsync(abstractFileUri, abstractText, ct);

            _logger.LogDebug("Processed file: {Uri}", fileUri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process file: {Uri}", fileUri);
        }
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach (var uri in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await ProcessTreeAsync(uri, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing: {Uri}", uri);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        _queue.Writer.TryComplete();
        _cts.Dispose();
    }
}
