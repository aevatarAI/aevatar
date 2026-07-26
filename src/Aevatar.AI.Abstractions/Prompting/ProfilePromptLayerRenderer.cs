using System.Text;

namespace Aevatar.AI.Abstractions.Prompting;

public sealed record ProfilePromptLayerRendering(
    string Content,
    int ActualUtf8Bytes,
    int EstimatedTokens);

public static class ProfilePromptLayerRenderer
{
    private const string AlwaysSkillProcedureOpening = "<always-skill-procedure>\n";
    private const string AlwaysSkillProcedureClosing = "\n</always-skill-procedure>";
    private const string LayerItemSeparator = "\n\n";

    public static ProfilePromptLayerRendering Render(
        string? profileInstructions,
        IReadOnlyList<string>? alwaysSkillProcedures)
    {
        var builder = new StringBuilder();
        Append(builder, profileInstructions);
        if (alwaysSkillProcedures is not null)
        {
            foreach (var procedure in alwaysSkillProcedures)
            {
                if (string.IsNullOrWhiteSpace(procedure))
                    continue;

                Append(
                    builder,
                    string.Concat(
                        AlwaysSkillProcedureOpening,
                        procedure.Trim(),
                        AlwaysSkillProcedureClosing));
            }
        }

        var content = builder.ToString();
        var actualUtf8Bytes = Encoding.UTF8.GetByteCount(content);
        return new ProfilePromptLayerRendering(
            content,
            actualUtf8Bytes,
            (actualUtf8Bytes + 3) / 4);
    }

    private static void Append(StringBuilder builder, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;
        if (builder.Length > 0)
            builder.Append(LayerItemSeparator);
        builder.Append(content.Trim());
    }
}
