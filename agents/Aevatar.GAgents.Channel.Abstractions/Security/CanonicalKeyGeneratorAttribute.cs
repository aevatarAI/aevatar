namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Marks one method that produces the deterministic <c>ConversationReference.CanonicalKey</c> segment value.
/// </summary>
/// <remarks>
/// Methods marked with this attribute must stay pure for retry / replay / deduplication: the analyzer tests that back
/// this marker forbid calls to non-deterministic sources (for example <see cref="System.DateTime.Now"/>,
/// <see cref="System.DateTime.UtcNow"/>, <see cref="System.Guid.NewGuid"/>, or <see cref="System.Random"/>) inside the
/// method body.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CanonicalKeyGeneratorAttribute : Attribute
{
}
