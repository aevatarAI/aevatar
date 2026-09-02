// ─────────────────────────────────────────────────────────────
// SkillDefinition — 技能定义（从 SKILL.md 解析）
// 支持 frontmatter 元数据和多来源（本地 / 远程）
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>技能来源。</summary>
public enum SkillSource
{
    /// <summary>从本地文件系统发现。</summary>
    Local,

    /// <summary>从远程平台（如 Ornn）拉取。</summary>
    Remote,
}

/// <summary>
/// 技能定义。从 SKILL.md 文件或远程 API 解析得到。
/// 包含名称、描述、参数说明和指令内容。
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>技能名称（唯一标识，用于 use_skill 调用）。</summary>
    public required string Name { get; init; }

    /// <summary>技能描述（用于 LLM 理解技能用途，也展示在系统 prompt 中）。</summary>
    public required string Description { get; init; }

    /// <summary>技能指令内容（SKILL.md 正文，frontmatter 之后的部分）。</summary>
    public required string Instructions { get; init; }

    /// <summary>技能来源。</summary>
    public SkillSource Source { get; init; } = SkillSource.Local;

    /// <summary>技能文件路径（本地技能有效）。</summary>
    public string? FilePath { get; init; }

    /// <summary>远程技能 ID（如 Ornn GUID）。</summary>
    public string? RemoteId { get; init; }

    /// <summary>技能目录路径（从 FilePath 派生）。</summary>
    public string DirectoryPath => FilePath != null ? Path.GetDirectoryName(FilePath) ?? "" : "";

    // ─── Frontmatter 元数据 ───

    /// <summary>参数说明（如 "file pattern"），告知 LLM 技能接受什么参数。</summary>
    public string? Arguments { get; init; }

    /// <summary>何时使用此技能的详细指导。</summary>
    public string? WhenToUse { get; init; }

    /// <summary>是否允许 LLM 自动调用此技能（默认 true）。</summary>
    public bool IsModelInvocable { get; init; } = true;

    /// <summary>是否允许用户手动调用（默认 true）。</summary>
    public bool IsUserInvocable { get; init; } = true;

    /// <summary>关联文件内容（远程技能可能附带多个文件）。</summary>
    public IReadOnlyDictionary<string, string>? AssociatedFiles { get; init; }

    /// <summary>技能附带的 workflow 模板 YAML 描述；需先导入/挂载到 scope workflow 才可运行。</summary>
    public IReadOnlyList<SkillWorkflowDescriptor> Workflows { get; init; } = [];

    /// <summary>技能附带的可编译 C# script 描述。</summary>
    public IReadOnlyList<SkillScriptDescriptor> Scripts { get; init; } = [];
}

/// <summary>
/// Skill package workflow template YAML handoff descriptor.
/// </summary>
public sealed class SkillWorkflowDescriptor
{
    /// <summary>Workflow template identifier used when mounting/importing into a scope workflow.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Workflow YAML template bundle in stable path order.</summary>
    public required IReadOnlyList<string> WorkflowYamls { get; init; }
}

/// <summary>
/// Skill package C# script handoff descriptor.
/// </summary>
public sealed class SkillScriptDescriptor
{
    /// <summary>Script identifier passed to script_compile.script_id and script_execute.script_id.</summary>
    public required string ScriptId { get; init; }

    /// <summary>C# source files passed to script_compile.source_files in stable path order.</summary>
    public required IReadOnlyDictionary<string, string> SourceFiles { get; init; }

    /// <summary>Protobuf files passed to script_compile.proto_files in stable path order.</summary>
    public required IReadOnlyDictionary<string, string> ProtoFiles { get; init; }

    /// <summary>Optional behavior type name passed to script_compile.entry_behavior_type_name.</summary>
    public string EntryBehaviorTypeName { get; init; } = "";
}
