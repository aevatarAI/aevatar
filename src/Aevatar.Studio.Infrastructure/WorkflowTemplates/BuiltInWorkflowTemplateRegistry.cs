using System.Reflection;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Infrastructure.WorkflowTemplates;

internal static class BuiltInWorkflowTemplateRegistry
{
    private const string ResourcePrefix =
        "Aevatar.Studio.Infrastructure.WorkflowTemplates.Definitions.";

    public static IReadOnlyList<EmbeddedWorkflowTemplateRegistration> CreateRegistrations(
        Assembly? assembly = null)
    {
        assembly ??= typeof(BuiltInWorkflowTemplateRegistry).Assembly;
        return
        [
            new(
                "simple-assistant",
                "1",
                1,
                Text("Simple assistant", "简单助手"),
                Text("Answer a request with one focused AI role.", "使用一个专注的 AI 角色回答请求。"),
                Text(
                    "A minimal starting point for chat-style workflows that sends the draft input to one assistant role.",
                    "适合对话型工作流的最小起点，将草稿输入发送给一个助手角色。"),
                "Assistants",
                ["assistant", "llm", "starter"],
                new WorkflowTemplateExpectedIO(
                    Text("A text question or instruction.", "文本问题或指令。"),
                    Text("A text response from the assistant role.", "助手角色生成的文本回复。")),
                ReadWorkflowYaml(assembly, "simple-assistant.yaml"),
                new WorkflowTemplateRequirements(
                    ["llm_call"],
                    "1.0",
                    RequiresDefaultLLMRoute: true),
                WorkflowTemplateCompatibility.Compatible),
            new(
                "conditional-routing",
                "1",
                2,
                Text("Conditional routing", "条件路由"),
                Text("Route prepared input to urgent or standard handling.", "将整理后的输入路由到紧急或标准处理。"),
                Text(
                    "Trims an incoming text value, selects a branch with the built-in switch primitive, and returns an explicit routing result.",
                    "整理输入文本，使用内置 switch 原语选择分支，并返回明确的路由结果。"),
                "Automation",
                ["routing", "switch", "branching"],
                new WorkflowTemplateExpectedIO(
                    Text("Text containing either an urgent or standard request.", "包含紧急或标准请求的文本。"),
                    Text("A text result identifying the selected route.", "标识所选路由的文本结果。")),
                ReadWorkflowYaml(assembly, "conditional-routing.yaml"),
                new WorkflowTemplateRequirements(
                    ["transform", "switch", "assign"],
                    "1.0"),
                WorkflowTemplateCompatibility.Compatible),
            new(
                "review-and-approve",
                "1",
                3,
                Text("Review and approve", "审核与批准"),
                Text("Generate a draft, request approval, and summarize the decision.", "生成草稿、请求人工审批并总结结果。"),
                Text(
                    "Creates a draft with the default AI route, pauses for current human approval, branches on the decision, and produces a final summary.",
                    "使用默认 AI 路由生成草稿，等待当前人工审批，按审批结果分支并生成最终摘要。"),
                "Human in the loop",
                ["approval", "human-in-the-loop", "llm"],
                new WorkflowTemplateExpectedIO(
                    Text("A text brief for the draft to prepare.", "用于生成草稿的文本需求。"),
                    Text("A final text summary of the approved or rejected result.", "批准或拒绝结果的最终文本摘要。")),
                ReadWorkflowYaml(assembly, "review-and-approve.yaml"),
                new WorkflowTemplateRequirements(
                    ["llm_call", "human_approval", "assign"],
                    "1.0",
                    RequiresDefaultLLMRoute: true,
                    RequiresHumanInteraction: true),
                WorkflowTemplateCompatibility.Compatible),
        ];
    }

    private static WorkflowTemplateLocalizedText Text(string enUS, string zhCN) =>
        new(enUS, zhCN);

    private static string ReadWorkflowYaml(Assembly assembly, string fileName)
    {
        var resourceName = ResourcePrefix + fileName;
        using var stream = assembly.GetManifestResourceStream(resourceName) ??
                           throw new InvalidOperationException(
                               $"Embedded workflow template resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
