namespace Aevatar.AI.ToolProviders.Scripting;

/// <summary>Configuration for scripting agent tools.</summary>
public sealed class ScriptingToolOptions
{
    /// <summary>Maximum source file size in characters (default 50_000).</summary>
    public int MaxSourceSizeChars { get; set; } = 50_000;

    /// <summary>Default execution timeout in seconds for fast-path sandbox runs.</summary>
    public int DefaultExecutionTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum results returned by script_list.</summary>
    public int MaxListResults { get; set; } = 100;
}
