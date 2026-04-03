namespace Aevatar.AI.ToolProviders.Scripting.Ports;

/// <summary>
/// Adapter port for compiling C# script source code via Roslyn.
/// Named distinctly from the domain-level IScriptBehaviorCompiler to avoid collision.
/// Implementation bridges to ScriptDefinitionGAgent or IScriptBehaviorCompiler.
/// </summary>
public interface IScriptToolCompilationAdapter
{
    /// <summary>Compile C# source files into a script behavior artifact.</summary>
    Task<ScriptCompilationResult> CompileAsync(ScriptCompilationRequest request, CancellationToken ct = default);
}

/// <summary>Request to compile a script.</summary>
public sealed record ScriptCompilationRequest
{
    /// <summary>Unique script identifier. Reuse an existing ID to create a new revision.</summary>
    public required string ScriptId { get; init; }

    /// <summary>C# source files keyed by filename.</summary>
    public required IReadOnlyDictionary<string, string> SourceFiles { get; init; }

    /// <summary>Optional protobuf files keyed by filename.</summary>
    public IReadOnlyDictionary<string, string>? ProtoFiles { get; init; }
}

/// <summary>Result of a script compilation.</summary>
public sealed record ScriptCompilationResult
{
    public required bool Success { get; init; }
    public required string ScriptId { get; init; }

    /// <summary>Revision identifier assigned to this compilation.</summary>
    public string? Revision { get; init; }

    /// <summary>Compilation diagnostics (errors, warnings).</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>Sandbox policy violations, if any.</summary>
    public IReadOnlyList<string> SandboxViolations { get; init; } = [];

    /// <summary>Discovered command type URLs.</summary>
    public IReadOnlyList<string> CommandTypeUrls { get; init; } = [];

    /// <summary>Discovered domain event type URLs.</summary>
    public IReadOnlyList<string> DomainEventTypeUrls { get; init; } = [];

    /// <summary>State type URL.</summary>
    public string? StateTypeUrl { get; init; }

    /// <summary>ReadModel type URL.</summary>
    public string? ReadModelTypeUrl { get; init; }
}
