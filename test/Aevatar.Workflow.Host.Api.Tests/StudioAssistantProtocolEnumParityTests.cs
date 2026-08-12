using System.Text.RegularExpressions;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// The Studio assistant decoder re-declares the NyxID chat task enums in JavaScript so it can
/// normalize both the numeric and the prefixed wire forms. <c>normalizeEnum</c> throws
/// <c>NYXID_ENUM_INVALID</c> on any value it does not declare, and <c>consumeSse</c> degrades the
/// whole frame to a protocol error — so a proto enum value that the decoder never learned about
/// silently breaks rendering for every task that reaches it, on both the live frame path and the
/// rehydration path. These tests pin the JavaScript declarations to the proto so the drift fails
/// the build instead of the run card.
/// </summary>
public sealed class StudioAssistantProtocolEnumParityTests
{
    [Fact]
    public void DecoderEnums_ShouldDeclareEveryProtoValueInWireOrder()
    {
        var protoEnums = ReadProtoEnums();
        var decoderEnums = ReadDecoderEnums();

        decoderEnums.Should().NotBeEmpty("the decoder declares the wire enums it normalizes");

        foreach (var (property, (prefix, declaredValues)) in decoderEnums)
        {
            var matches = protoEnums
                .Where(candidate => candidate.Value.Count > 0
                    && candidate.Value.All(member => member.Name.StartsWith(prefix, StringComparison.Ordinal)))
                .ToList();

            matches.Should().HaveCount(
                1,
                $"decoder enum '{property}' must map to exactly one proto enum through prefix '{prefix}'");

            // normalizeEnum resolves a numeric wire value as values[number - 1], so the declared
            // order must be the proto's field-number order with the zero member excluded.
            var expected = matches[0].Value
                .Where(member => member.Number > 0)
                .OrderBy(member => member.Number)
                .Select(member => member.Name[prefix.Length..].ToLowerInvariant())
                .ToList();

            declaredValues.Should().Equal(
                expected,
                $"decoder enum '{property}' must declare every '{matches[0].Key}' value in wire order");
        }
    }

    [Fact]
    public void DecoderStepKinds_ShouldIncludeConditionAndExpiredNeedsYouOutcome()
    {
        var decoderEnums = ReadDecoderEnums();

        // Regression pins for the two values that drifted: condition steps are committed by
        // NyxIdChatTaskLifecycle for conditional branches (the UC4 shape) and expired needs-you
        // resolutions are committed once local approval expiry became a fail-closed denial.
        decoderEnums["stepKind"].Values.Should().Contain("condition");
        decoderEnums["needsYouOutcome"].Values.Should().Contain("expired");
    }

    private static Dictionary<string, (string Prefix, IReadOnlyList<string> Values)> ReadDecoderEnums()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "workflow",
            "Aevatar.Workflow.Infrastructure",
            "CapabilityApi",
            "StudioAssistant",
            "protocol.js"));

        var declarations = new Dictionary<string, (string, IReadOnlyList<string>)>(StringComparer.Ordinal);
        var pattern = new Regex(
            """(?<property>\w+):\s*enumDefinition\(\s*"(?<prefix>[A-Z0-9_]+)"\s*,\s*\[(?<values>[^\]]*)\]""",
            RegexOptions.Singleline);

        foreach (Match match in pattern.Matches(source))
        {
            var values = Regex.Matches(match.Groups["values"].Value, "\"([^\"]+)\"")
                .Select(value => value.Groups[1].Value)
                .ToList();
            declarations[match.Groups["property"].Value] = (match.Groups["prefix"].Value, values);
        }

        return declarations;
    }

    private static Dictionary<string, IReadOnlyList<(string Name, int Number)>> ReadProtoEnums()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "agents",
            "Aevatar.GAgents.NyxidChat",
            "protos",
            "nyxid_chat_task.proto"));

        var declarations = new Dictionary<string, IReadOnlyList<(string, int)>>(StringComparer.Ordinal);

        // Proto enum bodies never nest braces, so the body is everything up to the first close.
        foreach (Match match in Regex.Matches(source, @"enum\s+(?<name>\w+)\s*\{(?<body>[^}]*)\}"))
        {
            var members = Regex
                .Matches(match.Groups["body"].Value, @"^\s*(?<member>[A-Z0-9_]+)\s*=\s*(?<number>\d+);", RegexOptions.Multiline)
                .Select(member => (member.Groups["member"].Value, int.Parse(member.Groups["number"].Value)))
                .ToList();
            declarations[match.Groups["name"].Value] = members;
        }

        return declarations;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
