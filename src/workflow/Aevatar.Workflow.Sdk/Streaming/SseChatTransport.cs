using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;
using Aevatar.Workflow.Sdk.Internal;

namespace Aevatar.Workflow.Sdk.Streaming;

public interface IWorkflowChatTransport
{
    IAsyncEnumerable<WorkflowEvent> StreamAsync(
        HttpClient httpClient,
        ChatRunRequest request,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default);
}

public sealed class SseChatTransport : IWorkflowChatTransport
{
    public async IAsyncEnumerable<WorkflowEvent> StreamAsync(
        HttpClient httpClient,
        ChatRunRequest request,
        JsonSerializerOptions jsonOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        var scopeId = request.ScopeId?.Trim();
        var serviceId = request.Workflow?.Trim();
        if (string.IsNullOrWhiteSpace(scopeId))
            throw AevatarWorkflowException.InvalidRequest("Parameter 'ScopeId' is required.");
        if (string.IsNullOrWhiteSpace(serviceId))
            throw AevatarWorkflowException.InvalidRequest("Parameter 'Workflow' is required.");

        using var requestMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/invoke/chat:stream")
        {
            Content = JsonContent.Create(
                new
                {
                    prompt = request.Prompt,
                    sessionId = request.SessionId,
                    headers = request.Metadata,
                    inputParts = request.InputParts,
                },
                options: jsonOptions),
        };
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw AevatarWorkflowException.Transport("Failed to call workflow service stream endpoint.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var rawPayload = await response.Content.ReadAsStringAsync(cancellationToken);
                throw WorkflowSdkJson.BuildHttpException(
                    response.StatusCode,
                    rawPayload,
                    $"Chat request failed with HTTP {(int)response.StatusCode}.");
            }

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                throw AevatarWorkflowException.Transport("Failed to read workflow service stream response.", ex);
            }

            using var reader = new StreamReader(stream);
            var dataLines = new List<string>();

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw AevatarWorkflowException.Transport("Failed while reading SSE stream.", ex);
                }

                if (line == null)
                {
                    if (dataLines.Count > 0)
                    {
                        var finalPayload = string.Join('\n', dataLines);
                        var finalEvent = ParseDataPayload(finalPayload, jsonOptions);
                        if (finalEvent != null)
                            yield return finalEvent;
                    }

                    yield break;
                }

                if (line.Length == 0)
                {
                    if (dataLines.Count == 0)
                        continue;

                    var payload = string.Join('\n', dataLines);
                    dataLines.Clear();
                    var evt = ParseDataPayload(payload, jsonOptions);
                    if (evt != null)
                        yield return evt;
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var segment = line.Length > 5 ? line[5..] : string.Empty;
                if (segment.StartsWith(' '))
                    segment = segment[1..];
                dataLines.Add(segment);
            }
        }
    }

    private static WorkflowEvent? ParseDataPayload(string payload, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        if (string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal))
            return null;

        WorkflowOutputFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize<WorkflowOutputFrame>(payload, jsonOptions);
        }
        catch (JsonException ex)
        {
            throw AevatarWorkflowException.StreamPayload(
                "Failed to parse SSE frame payload as WorkflowOutputFrame.",
                payload,
                ex);
        }

        if (frame == null || string.IsNullOrWhiteSpace(frame.Type))
        {
            throw AevatarWorkflowException.StreamPayload(
                "SSE frame payload does not contain a valid event type.",
                payload);
        }

        return WorkflowEvent.FromFrame(frame);
    }
}
