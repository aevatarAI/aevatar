using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Abstractions.EventModules;

/// <summary>
/// Event module with activation and disposal hooks bound to the hosting agent lifecycle.
/// </summary>
public interface ILifecycleAwareEventModule
    : IEventModule<IEventHandlerContext>, IAsyncDisposable
{
    /// <summary>
    /// Initializes module runtime resources when the hosting agent activates.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);
}
