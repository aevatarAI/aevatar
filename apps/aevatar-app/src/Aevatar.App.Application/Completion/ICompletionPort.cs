namespace Aevatar.App.Application.Completion;

public interface ICompletionPort
{
    Task WaitAsync(string completionKey, CancellationToken ct = default);
    void Complete(string completionKey);
}

public sealed class CompletionPortOptions
{
    public const string SectionName = "App:CompletionPort";

    public string Channel { get; set; } = "aevatar:completion";
    public int TimeoutSeconds { get; set; } = 5;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}
