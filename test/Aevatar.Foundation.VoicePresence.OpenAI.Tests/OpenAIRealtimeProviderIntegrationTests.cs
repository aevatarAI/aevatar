using Aevatar.Foundation.VoicePresence.Abstractions;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.OpenAI.Tests;

[Trait("Category", "Integration")]
public class OpenAIRealtimeProviderIntegrationTests
{
    [OpenAIRealtimeIntegrationFact]
    public async Task Connect_update_session_and_inject_text_should_return_audio_response()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!.Trim();
        var model = Environment.GetEnvironmentVariable("AEVATAR_TEST_OPENAI_REALTIME_MODEL");
        if (string.IsNullOrWhiteSpace(model))
            model = OpenAIRealtimeProviderOptions.DefaultModelName;

        await using var provider = new OpenAIRealtimeProvider();
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        provider.OnEvent = (evt, ct) =>
        {
            _ = ct;
            switch (evt.EventCase)
            {
                case VoiceProviderEvent.EventOneofCase.ResponseStarted:
                    responseStarted.TrySetResult();
                    break;
                case VoiceProviderEvent.EventOneofCase.AudioReceived:
                    if (!evt.AudioReceived.Pcm16.IsEmpty)
                        audioReceived.TrySetResult();
                    break;
                case VoiceProviderEvent.EventOneofCase.ResponseDone:
                    responseDone.TrySetResult();
                    break;
                case VoiceProviderEvent.EventOneofCase.Error:
                    audioReceived.TrySetException(
                        new InvalidOperationException($"{evt.Error.ErrorCode}:{evt.Error.ErrorMessage}"));
                    break;
            }

            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await provider.ConnectAsync(new VoiceProviderConfig
        {
            ProviderName = "openai",
            ApiKey = apiKey,
            Model = model.Trim(),
        }, cts.Token);
        await provider.UpdateSessionAsync(new VoiceSessionConfig
        {
            Voice = "alloy",
            Instructions = "You are a concise assistant. Reply briefly and speak naturally.",
            SampleRateHz = 24000,
        }, cts.Token);
        await provider.InjectUserTextAsync("Say exactly: phase two ok.", cts.Token);

        await responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
        await audioReceived.Task.WaitAsync(TimeSpan.FromSeconds(60), cts.Token);
        audioReceived.Task.IsCompletedSuccessfully.ShouldBeTrue();
        await responseDone.Task.WaitAsync(TimeSpan.FromSeconds(60), cts.Token);
    }
}
