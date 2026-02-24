namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionDocumentRuntimeOptions
{
    string ProviderName { get; }

    bool FailFastOnStartup { get; }
}
