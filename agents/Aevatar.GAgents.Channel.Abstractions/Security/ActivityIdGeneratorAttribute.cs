namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Marks one method that produces the deterministic <c>ChatActivity.Id</c> value from a platform delivery key.
/// </summary>
/// <remarks>
/// Methods marked with this attribute must stay retry-stable: the analyzer tests that back this marker forbid calls
/// to non-deterministic sources (for example <see cref="System.DateTime.Now"/>, <see cref="System.DateTime.UtcNow"/>,
/// <see cref="System.Guid.NewGuid"/>, or <see cref="System.Random"/>) so that redelivery of the same inbound event
/// yields the same activity id.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ActivityIdGeneratorAttribute : Attribute
{
}
