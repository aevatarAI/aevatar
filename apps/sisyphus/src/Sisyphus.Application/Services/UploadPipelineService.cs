using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models;
using Sisyphus.Application.Models.Upload;

namespace Sisyphus.Application.Services;

public sealed class UploadPipelineService(
    TarGzParserService parser,
    ChronoGraphWriteService graphWriter,
    IWorkflowRunCommandService workflowRunService,
    GraphIdProvider graphIdProvider,
    IOptions<UploadOptions> uploadOptions,
    ILogger<UploadPipelineService> logger)
{
    internal static readonly string[] ValidBlueNodeTypes =
        ["theorem", "lemma", "definition", "proof", "corollary", "conjecture", "proposition", "remark",
         "conclusion", "example", "notation", "axiom", "observation", "note"];

    internal static readonly string[] ValidEdgeTypes = ["proves", "references"];

    private const string NodePurgeWorkflow = "sisyphus_node_purge";
    private const string EdgePurgeWorkflow = "sisyphus_edge_purge";

    /// <summary>
    /// Runs the full 3-phase upload pipeline with SSE emission.
    /// </summary>
    public async Task ExecuteAsync(
        Stream tarGzStream,
        Func<UploadSseEventType, object, CancellationToken, ValueTask> emitSseAsync,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var opts = uploadOptions.Value;

        // Parse tar.gz
        logger.LogInformation("Starting upload pipeline");
        var parseResult = parser.ParseAndValidate(tarGzStream);

        // Pre-validation check
        if (parseResult.UnresolvedLabels.Count > 0)
        {
            logger.LogWarning("Pre-validation failed: {Count} unresolved labels", parseResult.UnresolvedLabels.Count);
            await emitSseAsync(UploadSseEventType.VALIDATION_ERROR,
                UploadSsePayloads.ValidationError(
                    "Unresolved parent references",
                    parseResult.UnresolvedLabels,
                    parseResult.UnresolvedLabels.Count),
                ct);
            await emitSseAsync(UploadSseEventType.STREAM_END, UploadSsePayloads.StreamEnd(), ct);
            return;
        }

        var nodes = parseResult.Nodes;
        var edges = parseResult.Edges;

        // Handle empty upload
        if (nodes.Count == 0)
        {
            logger.LogInformation("Empty tar.gz — completing with zeros");
            await emitSseAsync(UploadSseEventType.UPLOAD_DONE,
                UploadSsePayloads.UploadDone(0, 0, 0, 0, "0s"), ct);
            await emitSseAsync(UploadSseEventType.STREAM_END, UploadSsePayloads.StreamEnd(), ct);
            return;
        }

        var graphId = await graphIdProvider.WaitWriteAsync(ct);

        // Phase 1: Upload all red nodes + edges
        var phaseOne = await ExecutePhaseOneAsync(nodes, edges, graphId, opts, emitSseAsync, ct);

        // Phase 2: Purify all red nodes -> blue nodes (batch mode)
        var phaseTwo = await ExecutePhaseTwoAsync(nodes, phaseOne.KgIdToUuid, graphId, opts, emitSseAsync, ct);

        // Phase 3: Purify all red edges -> blue edges (batch mode)
        var phaseThree = await ExecutePhaseThreeAsync(
            edges, phaseTwo.RedToBlueMap, phaseTwo.BlueNodeUuidSet, graphId, opts, emitSseAsync, ct);

        // Summary
        sw.Stop();
        var totalFailures = phaseTwo.FailureCount + phaseThree.FailureCount;
        var totalBlueEdges = phaseTwo.BlueEdgeCount + phaseThree.BlueEdgeCount;
        var duration = FormatDuration(sw.Elapsed);

        logger.LogInformation(
            "Upload pipeline complete: RedNodes={RedNodes}, BlueNodes={BlueNodes}, BlueEdges={BlueEdges}, Failures={Failures}, Duration={Duration}",
            nodes.Count, phaseTwo.BlueNodeCount, totalBlueEdges, totalFailures, duration);

        await emitSseAsync(UploadSseEventType.UPLOAD_DONE,
            UploadSsePayloads.UploadDone(nodes.Count, phaseTwo.BlueNodeCount, totalBlueEdges, totalFailures, duration),
            ct);
        await emitSseAsync(UploadSseEventType.STREAM_END, UploadSsePayloads.StreamEnd(), ct);
    }

    // ─── Phase 1: Upload all red nodes + edges ───

    private async Task<PhaseOneResult> ExecutePhaseOneAsync(
        List<RedNode> nodes,
        List<RedEdge> edges,
        string graphId,
        UploadOptions opts,
        Func<UploadSseEventType, object, CancellationToken, ValueTask> emitSseAsync,
        CancellationToken ct)
    {
        logger.LogInformation("Phase 1: Uploading {NodeCount} red nodes and {EdgeCount} red edges",
            nodes.Count, edges.Count);
        await emitSseAsync(UploadSseEventType.PHASE_START,
            UploadSsePayloads.PhaseStart(1, "Uploading red nodes"), ct);

        var kgIdToUuid = new Dictionary<string, string>();

        // Upload red nodes in batches
        var nodeBatches = Batch(nodes, opts.ApiBatchSize);
        var totalNodeBatches = nodeBatches.Count;

        for (var i = 0; i < totalNodeBatches; i++)
        {
            var batch = nodeBatches[i];
            var nodeProps = batch.Select(n => BuildRedNodeItem(n)).ToList();

            var uuids = await graphWriter.CreateNodesAsync(graphId, nodeProps, ct);

            for (var j = 0; j < batch.Count && j < uuids.Count; j++)
            {
                batch[j].GraphUuid = uuids[j];
                kgIdToUuid[batch[j].KgId] = uuids[j];
                logger.LogDebug("Red node {KgId} assigned UUID {Uuid}", batch[j].KgId, uuids[j]);
            }

            await emitSseAsync(UploadSseEventType.BATCH_DONE,
                UploadSsePayloads.BatchDone(1, i + 1, totalNodeBatches), ct);
        }

        // Resolve edge UUIDs and upload red edges in batches
        foreach (var edge in edges)
        {
            kgIdToUuid.TryGetValue(edge.SourceKgId, out var sourceUuid);
            kgIdToUuid.TryGetValue(edge.TargetKgId, out var targetUuid);
            edge.SourceUuid = sourceUuid;
            edge.TargetUuid = targetUuid;
        }

        var edgeBatches = Batch(edges, opts.ApiBatchSize);
        var totalEdgeBatches = edgeBatches.Count;

        for (var i = 0; i < totalEdgeBatches; i++)
        {
            var batch = edgeBatches[i];
            var edgeProps = batch
                .Where(e => e.SourceUuid is not null && e.TargetUuid is not null)
                .Select(e => BuildRedEdgeItem(e))
                .ToList();

            if (edgeProps.Count > 0)
                await graphWriter.CreateEdgesAsync(graphId, edgeProps, ct);

            await emitSseAsync(UploadSseEventType.BATCH_DONE,
                UploadSsePayloads.BatchDone(1, totalNodeBatches + i + 1, totalNodeBatches + totalEdgeBatches), ct);
        }

        logger.LogInformation("Phase 1 complete: {NodeCount} nodes, {EdgeCount} edges uploaded",
            nodes.Count, edges.Count);
        await emitSseAsync(UploadSseEventType.PHASE_DONE,
            UploadSsePayloads.PhaseDone(1, redNodes: nodes.Count, redEdges: edges.Count), ct);

        return new PhaseOneResult(kgIdToUuid, nodes.Count, edges.Count);
    }

    // ─── Phase 2: Batch-purify all red nodes → blue nodes ───

    private async Task<PhaseTwoResult> ExecutePhaseTwoAsync(
        List<RedNode> nodes,
        Dictionary<string, string> kgIdToUuid,
        string graphId,
        UploadOptions opts,
        Func<UploadSseEventType, object, CancellationToken, ValueTask> emitSseAsync,
        CancellationToken ct)
    {
        var totalBatches = (int)Math.Ceiling((double)nodes.Count / opts.PurgeBatchSize);
        logger.LogInformation("Phase 2: Purifying {Count} red nodes in {Batches} batches of {BatchSize}",
            nodes.Count, totalBatches, opts.PurgeBatchSize);
        await emitSseAsync(UploadSseEventType.PHASE_START,
            UploadSsePayloads.PhaseStart(2, "Purifying red nodes"), ct);

        var redToBlueMap = new Dictionary<string, List<string>>();
        var blueNodeUuidSet = new HashSet<string>();
        var blueNodeCount = 0;
        var blueEdgeCount = 0;
        var failureCount = 0;
        var completedCount = 0;

        var nodeBatches = Batch(nodes, opts.PurgeBatchSize);
        var semaphore = new SemaphoreSlim(opts.PurgeConcurrency, opts.PurgeConcurrency);

        var tasks = nodeBatches.Select(async batch =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var batchResult = await PurgeNodeBatchAsync(batch, opts.PurgeMaxRetries, ct);

                if (batchResult is null)
                {
                    // Whole batch failed — mark all nodes as failed
                    foreach (var node in batch)
                    {
                        var idx = Interlocked.Increment(ref completedCount);
                        Interlocked.Increment(ref failureCount);
                        logger.LogWarning("Node purge failed (batch failure) for {KgId}", node.KgId);
                        await emitSseAsync(UploadSseEventType.NODE_FAILED,
                            UploadSsePayloads.NodeFailed(2, idx, node.KgId, "Batch purge failed after max retries"),
                            ct);
                    }
                    return;
                }

                // Index results by kg_id
                var resultsByKgId = new Dictionary<string, BatchNodePurgeEntry>();
                foreach (var entry in batchResult.Results)
                {
                    if (!string.IsNullOrEmpty(entry.KgId))
                        resultsByKgId.TryAdd(entry.KgId, entry);
                }

                // Process each node individually
                foreach (var node in batch)
                {
                    if (!resultsByKgId.TryGetValue(node.KgId, out var entry))
                    {
                        var idx = Interlocked.Increment(ref completedCount);
                        Interlocked.Increment(ref failureCount);
                        logger.LogWarning("Node purge: no result returned for {KgId}", node.KgId);
                        await emitSseAsync(UploadSseEventType.NODE_FAILED,
                            UploadSsePayloads.NodeFailed(2, idx, node.KgId, "No result in batch response"),
                            ct);
                        continue;
                    }

                    // Validate individual entry using existing validation
                    var singleResult = new NodePurgeResult
                    {
                        BlueNodes = entry.BlueNodes,
                        BlueEdges = entry.BlueEdges,
                    };
                    var errors = ValidateNodePurgeResult(singleResult);
                    if (errors.Count > 0)
                    {
                        var idx = Interlocked.Increment(ref completedCount);
                        Interlocked.Increment(ref failureCount);
                        logger.LogWarning("Node purge validation failed for {KgId}: {Errors}",
                            node.KgId, string.Join("; ", errors));
                        await emitSseAsync(UploadSseEventType.NODE_FAILED,
                            UploadSsePayloads.NodeFailed(2, idx, node.KgId, $"Validation: {string.Join("; ", errors)}"),
                            ct);
                        continue;
                    }

                    // Write blue nodes to graph
                    var blueNodeProps = entry.BlueNodes.Select(bn => new Dictionary<string, object>
                    {
                        ["type"] = bn.Type,
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["abstract"] = bn.Abstract,
                            ["body"] = bn.Body,
                            [SisyphusStatus.PropertyName] = SisyphusStatus.Purified,
                        },
                    }).ToList();

                    var blueUuids = await graphWriter.CreateNodesAsync(graphId, blueNodeProps, ct);

                    for (var i = 0; i < entry.BlueNodes.Count && i < blueUuids.Count; i++)
                    {
                        entry.BlueNodes[i].GraphUuid = blueUuids[i];
                        logger.LogDebug("Blue node {TempId} -> UUID {Uuid}, type={Type}, source red={KgId}",
                            entry.BlueNodes[i].TempId, blueUuids[i], entry.BlueNodes[i].Type, node.KgId);
                    }

                    // Track mapping
                    var blueUuidList = blueUuids.ToList();
                    lock (redToBlueMap)
                    {
                        redToBlueMap[node.KgId] = blueUuidList;
                    }
                    lock (blueNodeUuidSet)
                    {
                        foreach (var uuid in blueUuidList)
                            blueNodeUuidSet.Add(uuid);
                    }
                    Interlocked.Add(ref blueNodeCount, blueUuids.Count);

                    // Create PURIFIED_FROM edges (blue -> red)
                    var redUuid = kgIdToUuid.GetValueOrDefault(node.KgId);
                    if (redUuid is not null)
                    {
                        var purifiedFromEdges = blueUuids.Select(blueUuid => new Dictionary<string, object>
                        {
                            ["source"] = blueUuid,
                            ["target"] = redUuid,
                            ["type"] = "PURIFIED_FROM",
                            ["properties"] = new Dictionary<string, object>
                            {
                                [SisyphusStatus.PropertyName] = SisyphusStatus.Purified,
                            },
                        }).ToList();

                        if (purifiedFromEdges.Count > 0)
                        {
                            await graphWriter.CreateEdgesAsync(graphId, purifiedFromEdges, ct);
                            logger.LogDebug("Created {Count} PURIFIED_FROM edges for red node {KgId}",
                                purifiedFromEdges.Count, node.KgId);
                        }
                    }

                    // Create internal blue edges from node purge
                    if (entry.BlueEdges.Count > 0)
                    {
                        var tempIdToUuid = new Dictionary<string, string>();
                        for (var i = 0; i < entry.BlueNodes.Count && i < blueUuids.Count; i++)
                            tempIdToUuid[entry.BlueNodes[i].TempId] = blueUuids[i];

                        var internalEdges = entry.BlueEdges
                            .Where(be => be.Source is not null && be.Target is not null
                                && tempIdToUuid.ContainsKey(be.Source) && tempIdToUuid.ContainsKey(be.Target))
                            .Select(be => new Dictionary<string, object>
                            {
                                ["source"] = tempIdToUuid[be.Source!],
                                ["target"] = tempIdToUuid[be.Target!],
                                ["type"] = be.EdgeType,
                                ["properties"] = new Dictionary<string, object>
                                {
                                    [SisyphusStatus.PropertyName] = SisyphusStatus.Purified,
                                },
                            }).ToList();

                        if (internalEdges.Count > 0)
                        {
                            await graphWriter.CreateEdgesAsync(graphId, internalEdges, ct);
                            Interlocked.Add(ref blueEdgeCount, internalEdges.Count);
                            logger.LogDebug("Created {Count} internal blue edges for red node {KgId}",
                                internalEdges.Count, node.KgId);
                        }
                    }

                    var completed = Interlocked.Increment(ref completedCount);
                    await emitSseAsync(UploadSseEventType.NODE_PURGED,
                        UploadSsePayloads.NodePurged(2, completed, nodes.Count, node.KgId, entry.BlueNodes.Count),
                        ct);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        logger.LogInformation(
            "Phase 2 complete: BlueNodes={BlueNodeCount}, BlueEdges={BlueEdgeCount}, Failures={Failures}",
            blueNodeCount, blueEdgeCount, failureCount);
        await emitSseAsync(UploadSseEventType.PHASE_DONE, UploadSsePayloads.PhaseDone(2), ct);

        return new PhaseTwoResult(redToBlueMap, blueNodeUuidSet, blueNodeCount, blueEdgeCount, failureCount);
    }

    // ─── Phase 3: Batch-purify all red edges → blue edges ───

    private async Task<PhaseThreeResult> ExecutePhaseThreeAsync(
        List<RedEdge> edges,
        Dictionary<string, List<string>> redToBlueMap,
        HashSet<string> validBlueUuids,
        string graphId,
        UploadOptions opts,
        Func<UploadSseEventType, object, CancellationToken, ValueTask> emitSseAsync,
        CancellationToken ct)
    {
        logger.LogInformation("Phase 3: Purifying {Count} red edges in batches of {BatchSize}",
            edges.Count, opts.PurgeBatchSize);
        await emitSseAsync(UploadSseEventType.PHASE_START,
            UploadSsePayloads.PhaseStart(3, "Purifying red edges"), ct);

        var blueEdgeCount = 0;
        var failureCount = 0;
        var completedCount = 0;

        // Pre-filter edges that have valid blue node groups on both sides
        var validEdges = new List<(RedEdge Edge, List<string> SourceBlueUuids, List<string> TargetBlueUuids)>();
        foreach (var edge in edges)
        {
            var sourceBlueUuids = redToBlueMap.GetValueOrDefault(edge.SourceKgId) ?? [];
            var targetBlueUuids = redToBlueMap.GetValueOrDefault(edge.TargetKgId) ?? [];

            if (sourceBlueUuids.Count == 0 || targetBlueUuids.Count == 0)
            {
                var idx = Interlocked.Increment(ref completedCount);
                Interlocked.Increment(ref failureCount);
                logger.LogWarning("Skipping edge purge for {Source}->{Target}: missing blue node group",
                    edge.SourceKgId, edge.TargetKgId);
                await emitSseAsync(UploadSseEventType.EDGE_FAILED,
                    UploadSsePayloads.EdgeFailed(3, idx,
                        $"Missing blue node group for {edge.SourceKgId} or {edge.TargetKgId}"),
                    ct);
                continue;
            }

            validEdges.Add((edge, sourceBlueUuids, targetBlueUuids));
        }

        logger.LogInformation("Phase 3: {Valid}/{Total} edges have valid blue groups",
            validEdges.Count, edges.Count);

        var edgeBatches = Batch(validEdges, opts.PurgeBatchSize);
        var semaphore = new SemaphoreSlim(opts.PurgeConcurrency, opts.PurgeConcurrency);

        var tasks = edgeBatches.Select(async batch =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var batchResult = await PurgeEdgeBatchAsync(batch, validBlueUuids, opts.PurgeMaxRetries, ct);

                if (batchResult is null)
                {
                    // Whole batch failed
                    foreach (var (edge, _, _) in batch)
                    {
                        var idx = Interlocked.Increment(ref completedCount);
                        Interlocked.Increment(ref failureCount);
                        logger.LogWarning("Edge purge failed (batch failure) for {Source}->{Target}",
                            edge.SourceKgId, edge.TargetKgId);
                        await emitSseAsync(UploadSseEventType.EDGE_FAILED,
                            UploadSsePayloads.EdgeFailed(3, idx, "Batch purge failed after max retries"),
                            ct);
                    }
                    return;
                }

                // Index results by source+target kg_id pair
                var resultsByKey = new Dictionary<string, BatchEdgePurgeEntry>();
                foreach (var entry in batchResult.Results)
                {
                    var key = $"{entry.SourceKgId}|{entry.TargetKgId}";
                    resultsByKey.TryAdd(key, entry);
                }

                foreach (var (edge, _, _) in batch)
                {
                    var key = $"{edge.SourceKgId}|{edge.TargetKgId}";
                    if (!resultsByKey.TryGetValue(key, out var entry))
                    {
                        var idx = Interlocked.Increment(ref completedCount);
                        Interlocked.Increment(ref failureCount);
                        logger.LogWarning("Edge purge: no result returned for {Source}->{Target}",
                            edge.SourceKgId, edge.TargetKgId);
                        await emitSseAsync(UploadSseEventType.EDGE_FAILED,
                            UploadSsePayloads.EdgeFailed(3, idx, "No result in batch response"),
                            ct);
                        continue;
                    }

                    // Validate individual entry
                    var singleResult = new EdgePurgeResult { BlueEdges = entry.BlueEdges };
                    var errors = ValidateEdgePurgeResult(singleResult, validBlueUuids);
                    if (errors.Count > 0)
                    {
                        var idx = Interlocked.Increment(ref completedCount);
                        Interlocked.Increment(ref failureCount);
                        logger.LogWarning("Edge purge validation failed for {Source}->{Target}: {Errors}",
                            edge.SourceKgId, edge.TargetKgId, string.Join("; ", errors));
                        await emitSseAsync(UploadSseEventType.EDGE_FAILED,
                            UploadSsePayloads.EdgeFailed(3, idx,
                                $"Validation: {string.Join("; ", errors)}"),
                            ct);
                        continue;
                    }

                    // Write blue edges to graph
                    if (entry.BlueEdges.Count > 0)
                    {
                        var edgeProps = entry.BlueEdges.Select(be => new Dictionary<string, object>
                        {
                            ["source"] = be.SourceId!,
                            ["target"] = be.TargetId!,
                            ["type"] = be.EdgeType,
                            ["properties"] = new Dictionary<string, object>
                            {
                                [SisyphusStatus.PropertyName] = SisyphusStatus.Purified,
                            },
                        }).ToList();

                        await graphWriter.CreateEdgesAsync(graphId, edgeProps, ct);
                        Interlocked.Add(ref blueEdgeCount, edgeProps.Count);

                        foreach (var be in entry.BlueEdges)
                        {
                            logger.LogDebug("Blue edge: {Source} -> {Target}, type={EdgeType}",
                                be.SourceId, be.TargetId, be.EdgeType);
                        }
                    }

                    var completed = Interlocked.Increment(ref completedCount);
                    await emitSseAsync(UploadSseEventType.EDGE_PURGED,
                        UploadSsePayloads.EdgePurged(3, completed, edges.Count), ct);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        logger.LogInformation("Phase 3 complete: BlueEdges={BlueEdgeCount}, Failures={Failures}",
            blueEdgeCount, failureCount);
        await emitSseAsync(UploadSseEventType.PHASE_DONE, UploadSsePayloads.PhaseDone(3), ct);

        return new PhaseThreeResult(blueEdgeCount, failureCount);
    }

    // ─── Batch workflow invocation ───

    private async Task<BatchNodePurgeResult?> PurgeNodeBatchAsync(
        List<RedNode> nodes, int maxRetries, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            logger.LogInformation("Node batch purge attempt {Attempt}/{Max} for {Count} nodes (first: {KgId})",
                attempt + 1, maxRetries + 1, nodes.Count, nodes[0].KgId);

            var prompt = BuildNodeBatchPurgePrompt(nodes);
            var llmOutput = await ExecuteWorkflowAsync(NodePurgeWorkflow, prompt, ct);

            if (llmOutput is null)
            {
                logger.LogWarning("Node batch purge workflow returned no output on attempt {Attempt}", attempt + 1);
                continue;
            }

            logger.LogDebug("Node batch purge raw output (len={Len}): {Output}",
                llmOutput.Length, llmOutput.Length > 500 ? llmOutput[..500] + "..." : llmOutput);

            var json = ExtractJson(llmOutput);

            BatchNodePurgeResult? result;
            try
            {
                result = JsonSerializer.Deserialize<BatchNodePurgeResult>(json);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Node batch purge JSON parse failed on attempt {Attempt}", attempt + 1);
                continue;
            }

            if (result?.Results is null || result.Results.Count == 0)
            {
                logger.LogWarning("Node batch purge returned empty results on attempt {Attempt}", attempt + 1);
                continue;
            }

            logger.LogInformation("Node batch purge returned {Count} results on attempt {Attempt}",
                result.Results.Count, attempt + 1);
            return result;
        }

        return null;
    }

    private async Task<BatchEdgePurgeResult?> PurgeEdgeBatchAsync(
        List<(RedEdge Edge, List<string> SourceBlueUuids, List<string> TargetBlueUuids)> edges,
        HashSet<string> validBlueUuids,
        int maxRetries,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            logger.LogInformation(
                "Edge batch purge attempt {Attempt}/{Max} for {Count} edges (first: {Source}->{Target})",
                attempt + 1, maxRetries + 1, edges.Count,
                edges[0].Edge.SourceKgId, edges[0].Edge.TargetKgId);

            var prompt = BuildEdgeBatchPurgePrompt(edges);
            var llmOutput = await ExecuteWorkflowAsync(EdgePurgeWorkflow, prompt, ct);

            if (llmOutput is null)
            {
                logger.LogWarning("Edge batch purge workflow returned no output on attempt {Attempt}", attempt + 1);
                continue;
            }

            var json = ExtractJson(llmOutput);

            BatchEdgePurgeResult? result;
            try
            {
                result = JsonSerializer.Deserialize<BatchEdgePurgeResult>(json);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Edge batch purge JSON parse failed on attempt {Attempt}", attempt + 1);
                continue;
            }

            if (result?.Results is null || result.Results.Count == 0)
            {
                logger.LogWarning("Edge batch purge returned empty results on attempt {Attempt}", attempt + 1);
                continue;
            }

            logger.LogInformation("Edge batch purge returned {Count} results on attempt {Attempt}",
                result.Results.Count, attempt + 1);
            return result;
        }

        return null;
    }

    private async Task<string?> ExecuteWorkflowAsync(string workflowName, string prompt, CancellationToken ct)
    {
        string? lastMessage = null;

        await workflowRunService.ExecuteAsync(
            new WorkflowChatRunRequest(prompt, workflowName, null),
            (frame, token) =>
            {
                // Capture the last text message content as the LLM output
                if (frame.Type == "TEXT_MESSAGE_CONTENT" && frame.Delta is not null)
                    lastMessage = (lastMessage ?? "") + frame.Delta;
                else if (frame.Type == "RUN_FINISHED" && frame.Result is JsonElement je)
                    lastMessage = je.GetRawText();
                return ValueTask.CompletedTask;
            },
            ct: ct);

        return lastMessage;
    }

    // ─── Validation ───

    internal static List<string> ValidateNodePurgeResult(NodePurgeResult result)
    {
        var errors = new List<string>();

        if (result.BlueNodes.Count == 0)
        {
            errors.Add("blue_nodes must not be empty");
            return errors;
        }

        var tempIds = new HashSet<string>();
        foreach (var node in result.BlueNodes)
        {
            if (string.IsNullOrWhiteSpace(node.TempId))
                errors.Add("Missing temp_id on blue node");
            else
                tempIds.Add(node.TempId);

            if (!ValidBlueNodeTypes.Contains(node.Type))
                errors.Add($"Invalid type '{node.Type}' on node {node.TempId}");

            if (string.IsNullOrWhiteSpace(node.Abstract))
                errors.Add($"Empty abstract on node {node.TempId}");

            if (string.IsNullOrWhiteSpace(node.Body))
                errors.Add($"Empty body on node {node.TempId}");
        }

        foreach (var edge in result.BlueEdges)
        {
            if (edge.Source is not null && !tempIds.Contains(edge.Source))
                errors.Add($"Edge source '{edge.Source}' not found in blue_nodes");

            if (edge.Target is not null && !tempIds.Contains(edge.Target))
                errors.Add($"Edge target '{edge.Target}' not found in blue_nodes");

            if (!ValidEdgeTypes.Contains(edge.EdgeType))
                errors.Add($"Invalid edge_type '{edge.EdgeType}'");
        }

        return errors;
    }

    internal static List<string> ValidateEdgePurgeResult(EdgePurgeResult result, HashSet<string> validBlueUuids)
    {
        var errors = new List<string>();

        foreach (var edge in result.BlueEdges)
        {
            if (string.IsNullOrWhiteSpace(edge.SourceId) || !validBlueUuids.Contains(edge.SourceId))
                errors.Add($"Invalid source_id '{edge.SourceId}'");

            if (string.IsNullOrWhiteSpace(edge.TargetId) || !validBlueUuids.Contains(edge.TargetId))
                errors.Add($"Invalid target_id '{edge.TargetId}'");

            if (!ValidEdgeTypes.Contains(edge.EdgeType))
                errors.Add($"Invalid edge_type '{edge.EdgeType}'");
        }

        return errors;
    }

    // ─── Batch prompt builders ───

    private static string BuildNodeBatchPurgePrompt(List<RedNode> nodes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Batch of {nodes.Count} red nodes to purify. Process EACH node independently and return results for ALL of them.");
        sb.AppendLine();

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            sb.AppendLine($"--- Red Node {i + 1} of {nodes.Count} ---");
            sb.AppendLine($"KG ID: {node.KgId}");
            sb.AppendLine($"Label: {node.Label}");
            sb.AppendLine($"Atom Type: {node.AtomType}");
            sb.AppendLine($"Unit Env: {node.UnitEnv ?? "unknown"}");
            sb.AppendLine();
            sb.AppendLine("TeX Content:");
            sb.AppendLine(node.TexContent);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildEdgeBatchPurgePrompt(
        List<(RedEdge Edge, List<string> SourceBlueUuids, List<string> TargetBlueUuids)> edges)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Batch of {edges.Count} red edges to purify. Process EACH edge independently and return results for ALL of them.");
        sb.AppendLine();

        for (var i = 0; i < edges.Count; i++)
        {
            var (edge, sourceBlueUuids, targetBlueUuids) = edges[i];
            sb.AppendLine($"--- Red Edge {i + 1} of {edges.Count} ---");
            sb.AppendLine($"  Source KG ID: {edge.SourceKgId}");
            sb.AppendLine($"  Target KG ID: {edge.TargetKgId}");
            sb.AppendLine($"  Edge Type: {edge.EdgeType}");
            sb.AppendLine($"  Edge Source: {edge.EdgeSource ?? "unknown"}");
            sb.AppendLine($"  Edge Reason: {edge.EdgeReason ?? "unknown"}");
            sb.AppendLine();
            sb.AppendLine($"  Source Blue Node Group UUIDs: {JsonSerializer.Serialize(sourceBlueUuids)}");
            sb.AppendLine($"  Target Blue Node Group UUIDs: {JsonSerializer.Serialize(targetBlueUuids)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ─── Helpers ───

    private static Dictionary<string, object> BuildRedNodeItem(RedNode node)
    {
        var properties = new Dictionary<string, object>
        {
            ["kg_id"] = node.KgId,
            ["label"] = node.Label,
            ["atom_type"] = node.AtomType,
            ["tex_content"] = node.TexContent,
            ["proof_orphan"] = node.ProofOrphan,
            [SisyphusStatus.PropertyName] = SisyphusStatus.Raw,
        };

        if (node.SourcePath is not null) properties["source_path"] = node.SourcePath;
        if (node.SourceTexLabel is not null) properties["source_tex_label"] = node.SourceTexLabel;
        if (node.CanonicalLabel is not null) properties["canonical_label"] = node.CanonicalLabel;
        if (node.UnitEnv is not null) properties["unit_env"] = node.UnitEnv;
        if (node.UnitFingerprint is not null) properties["unit_fingerprint"] = node.UnitFingerprint;
        if (node.MergedSha256 is not null) properties["merged_sha256"] = node.MergedSha256;
        if (node.ExtractorVersion is not null) properties["extractor_version"] = node.ExtractorVersion;

        return new Dictionary<string, object>
        {
            ["type"] = "raw",
            ["properties"] = properties,
        };
    }

    private static Dictionary<string, object> BuildRedEdgeItem(RedEdge edge) => new()
    {
        ["source"] = edge.SourceUuid!,
        ["target"] = edge.TargetUuid!,
        ["type"] = edge.EdgeType,
        ["properties"] = new Dictionary<string, object>
        {
            ["edge_source"] = edge.EdgeSource ?? "",
            ["edge_reason"] = edge.EdgeReason ?? "",
            [SisyphusStatus.PropertyName] = SisyphusStatus.Raw,
        },
    };

    /// <summary>
    /// Extracts the purge result JSON from workflow output.
    /// The workflow may wrap LLM output in {"output":"..."}, and the LLM may
    /// wrap JSON in markdown code fences or prefix it with natural language.
    /// </summary>
    internal static string ExtractJson(string raw)
    {
        var text = raw.Trim();

        // Step 1: Unwrap {"output":"..."} wrapper from workflow
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.String)
                {
                    text = outputEl.GetString() ?? text;
                }
            }
            catch (JsonException)
            {
                // Not valid JSON wrapper — continue with raw text
            }
        }

        // Step 2: Extract from markdown code fences ```json ... ```
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var contentStart = text.IndexOf('\n', fenceStart);
            if (contentStart >= 0)
            {
                contentStart++;
                var fenceEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (fenceEnd > contentStart)
                    return FixInvalidJsonEscapes(text[contentStart..fenceEnd].Trim());
            }
        }

        // Step 3: Find first { ... last }
        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return FixInvalidJsonEscapes(text[firstBrace..(lastBrace + 1)]);

        return FixInvalidJsonEscapes(text);
    }

    /// <summary>
    /// Fixes invalid JSON escape sequences (e.g. \( \) \g from LaTeX) inside JSON string values.
    /// Scans through JSON, and when inside a quoted string, replaces \X (where X is not a valid
    /// JSON escape char) with \\X so the JSON parser accepts it.
    /// </summary>
    internal static string FixInvalidJsonEscapes(string json)
    {
        var sb = new StringBuilder(json.Length + 64);
        var inString = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (inString)
            {
                if (c == '\\' && i + 1 < json.Length)
                {
                    var next = json[i + 1];
                    if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')
                    {
                        // Valid JSON escape — pass through as-is
                        sb.Append(c);
                        sb.Append(next);
                        i++;
                    }
                    else
                    {
                        // Invalid escape like \( \) \g \l — double the backslash
                        sb.Append('\\');
                        sb.Append('\\');
                        sb.Append(next);
                        i++;
                    }
                }
                else if (c == '"')
                {
                    inString = false;
                    sb.Append(c);
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                sb.Append(c);
                if (c == '"')
                    inString = true;
            }
        }

        return sb.ToString();
    }

    private static List<List<T>> Batch<T>(List<T> items, int batchSize)
    {
        var batches = new List<List<T>>();
        for (var i = 0; i < items.Count; i += batchSize)
            batches.Add(items.GetRange(i, Math.Min(batchSize, items.Count - i)));
        return batches;
    }

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{elapsed.TotalMinutes:F0}m"
            : $"{elapsed.TotalSeconds:F0}s";

    // ─── Internal result types ───

    internal sealed record PhaseOneResult(
        Dictionary<string, string> KgIdToUuid,
        int RedNodeCount,
        int RedEdgeCount);

    internal sealed record PhaseTwoResult(
        Dictionary<string, List<string>> RedToBlueMap,
        HashSet<string> BlueNodeUuidSet,
        int BlueNodeCount,
        int BlueEdgeCount,
        int FailureCount);

    internal sealed record PhaseThreeResult(
        int BlueEdgeCount,
        int FailureCount);
}
