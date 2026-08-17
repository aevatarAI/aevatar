using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowToolResponseProjectorTests
{
    private const string FieldListWidget = "field-list";
    private const string PaymentReasonWidget = "payment-reason";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Project_WithArrayMap_ShouldPreserveRowOrderAndOnlyPersistSelectedStrings(int rowCount)
    {
        var reasons = Enumerable.Range(0, rowCount)
            .Select(static index => $"reason-{index}")
            .ToArray();
        var response = ApprovalDetailResponse(reasons);

        var projected = WorkflowToolResponseProjector.Project(
            response,
            PaymentReasonMapProjection());

        using var document = JsonDocument.Parse(projected);
        document.RootElement.GetProperty("payment_reasons")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal(reasons);
        projected.Should().NotContain("sensitive-amount");
        projected.Should().NotContain("amount-widget");
        projected.Should().NotContain("raw-form-note");
    }

    [Fact]
    public void Project_WhenAnyMappedRowOmitsTheRequiredChild_ShouldFailClosed()
    {
        var response = ApprovalDetailResponse(
            ["reason-0", "reason-1"],
            includePaymentReason: static index => index == 0);

        var act = () => WorkflowToolResponseProjector.Project(
            response,
            PaymentReasonMapProjection());

        act.Should().Throw<WorkflowToolResponseProjectionException>()
            .WithMessage("*requires exactly one matching array element*");
    }

    [Fact]
    public void Project_WhenArrayMapExceedsItemLimit_ShouldFailClosed()
    {
        var reasons = Enumerable.Repeat(
                "reason",
                WorkflowToolResponseProjectionContract.MaxArrayMapItems + 1)
            .ToArray();
        var response = ApprovalDetailResponse(reasons);

        var act = () => WorkflowToolResponseProjector.Project(
            response,
            PaymentReasonMapProjection());

        act.Should().Throw<WorkflowToolResponseProjectionException>()
            .WithMessage("*exceeds the array map item limit*");
    }

    [Fact]
    public void Project_WithEmptyArrayMap_ShouldPreserveTheEmptyArray()
    {
        var projected = WorkflowToolResponseProjector.Project(
            ApprovalDetailResponse([]),
            PaymentReasonMapProjection());

        using var document = JsonDocument.Parse(projected);
        document.RootElement.GetProperty("payment_reasons").GetArrayLength()
            .Should().Be(0);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Project_WithArrayMap_ShouldRetainTheDurableResponseByteLimit(
        int bytesOverLimit,
        bool expectedSuccess)
    {
        const string jsonEnvelope = "{\"payment_reasons\":[\"\"]}";
        var payloadLength = WorkflowToolResponseProjectionContract.MaxProjectedResponseBytes -
                            Encoding.UTF8.GetByteCount(jsonEnvelope) +
                            bytesOverLimit;
        var response = ApprovalDetailResponse([new string('x', payloadLength)]);

        var act = () => WorkflowToolResponseProjector.Project(
            response,
            PaymentReasonMapProjection());

        if (expectedSuccess)
        {
            Encoding.UTF8.GetByteCount(act()).Should()
                .Be(WorkflowToolResponseProjectionContract.MaxProjectedResponseBytes);
        }
        else
        {
            act.Should().Throw<WorkflowToolResponseProjectionException>()
                .WithMessage("*exceeds the durable response limit*");
        }
    }

    [Fact]
    public void ValidateOrThrow_WhenArrayMapsAreNested_ShouldRejectTheProjection()
    {
        var projection = new WorkflowToolResponseProjection
        {
            Fields =
            {
                Field("values", Map(Map(Pointer("/value")))),
            },
        };

        var act = () => WorkflowToolResponseProjectionContract.ValidateOrThrow(projection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*array map operations cannot be nested*");
    }

    [Fact]
    public void ValidateOrThrow_ShouldCountMappedOperationsAgainstTheFieldBudget()
    {
        var mappedOperations = Enumerable.Range(
                0,
                WorkflowToolResponseProjectionContract.MaxOperationsPerField)
            .Select(static _ => Pointer(string.Empty))
            .ToArray();
        var projection = new WorkflowToolResponseProjection
        {
            Fields =
            {
                Field("values", Map(mappedOperations)),
            },
        };

        var act = () => WorkflowToolResponseProjectionContract.ValidateOrThrow(projection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*including mapped operations*");
    }

    [Fact]
    public void ValidateOrThrow_WhenArrayMapMeetsTheFieldBudget_ShouldSucceed()
    {
        var mappedOperations = Enumerable.Range(
                0,
                WorkflowToolResponseProjectionContract.MaxOperationsPerField - 1)
            .Select(static _ => Pointer(string.Empty))
            .ToArray();
        var projection = new WorkflowToolResponseProjection
        {
            Fields =
            {
                Field("values", Map(mappedOperations)),
            },
        };

        var act = () => WorkflowToolResponseProjectionContract.ValidateOrThrow(projection);

        act.Should().NotThrow();
    }

    private static string ApprovalDetailResponse(
        IReadOnlyList<string> reasons,
        Func<int, bool>? includePaymentReason = null)
    {
        includePaymentReason ??= static _ => true;
        var rows = reasons.Select((reason, index) =>
        {
            var widgets = new List<object>();
            if (includePaymentReason(index))
            {
                widgets.Add(new
                {
                    id = PaymentReasonWidget,
                    value = reason,
                });
            }
            widgets.Add(new
            {
                id = "amount-widget",
                value = $"sensitive-amount-{index}",
            });
            return widgets.ToArray();
        }).ToArray();
        var form = new object[]
        {
            new
            {
                id = FieldListWidget,
                type = "fieldList",
                value = rows,
            },
            new
            {
                id = "note-widget",
                value = "raw-form-note",
            },
        };
        return JsonSerializer.Serialize(new
        {
            data = new
            {
                form = JsonSerializer.Serialize(form),
            },
        });
    }

    private static WorkflowToolResponseProjection PaymentReasonMapProjection()
    {
        var projection = new WorkflowToolResponseProjection();
        projection.Fields.Add(Field(
            "payment_reasons",
            Pointer("/data/form"),
            ParseJson(),
            Match("/id", FieldListWidget),
            Pointer("/value"),
            Map(
                Match("/id", PaymentReasonWidget),
                Pointer("/value"))));
        return projection;
    }

    private static WorkflowToolResponseProjectionField Field(
        string outputName,
        params WorkflowToolResponseProjectionOperation[] operations) =>
        new()
        {
            OutputName = outputName,
            Operations = { operations },
        };

    private static WorkflowToolResponseProjectionOperation Pointer(string pointer) =>
        new() { JsonPointer = pointer };

    private static WorkflowToolResponseProjectionOperation ParseJson() =>
        new() { ParseJson = true };

    private static WorkflowToolResponseProjectionOperation Match(string pointer, string expected) =>
        new()
        {
            ArrayMatch = new WorkflowToolResponseProjectionArrayMatch
            {
                ElementJsonPointer = pointer,
                ExpectedString = expected,
            },
        };

    private static WorkflowToolResponseProjectionOperation Map(
        params WorkflowToolResponseProjectionOperation[] operations) =>
        new()
        {
            ArrayMap = new WorkflowToolResponseProjectionArrayMap
            {
                Operations = { operations },
            },
        };
}
