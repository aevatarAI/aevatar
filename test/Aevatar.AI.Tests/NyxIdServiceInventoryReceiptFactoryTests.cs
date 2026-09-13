using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdServiceInventoryReceiptFactoryTests
{
    [Theory]
    [InlineData("inventory_contract_invalid")]
    [InlineData("NYXID_SERVICE_INVENTORY_CONTRACT_INVALID")]
    public void Create_InvalidInventoryContract_PreservesSanitizedNonTransientError(string errorCode)
    {
        var receipt = NyxIdServiceInventoryReceiptFactory.Create(
            "call-1", "nyxid_service_inventory",
            $$"""{"error":"{{errorCode}}","message":"unsafe-provider-secret"}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_SERVICE_INVENTORY_CONTRACT_INVALID");
        receipt.ErrorMessage.Should().Contain("contract");
        receipt.ErrorMessage.Should().NotContain("Retry shortly");
        receipt.ResultJson.Should().Contain(receipt.ErrorCode);
        receipt.ResultJson.Should().NotContain("unsafe-provider-secret");
    }

    [Theory]
    [InlineData("credential_denied")]
    [InlineData("NYXID_SERVICE_INVENTORY_CREDENTIAL_DENIED")]
    public void Create_CredentialDenial_PreservesNonTransientFailureWithoutProviderDetails(string errorCode)
    {
        var receipt = NyxIdServiceInventoryReceiptFactory.Create(
            "call-1", "nyxid_service_inventory",
            $$"""{"error":"{{errorCode}}","message":"unsafe-provider-secret"}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Denied);
        receipt.ErrorCode.Should().Be("NYXID_SERVICE_INVENTORY_CREDENTIAL_DENIED");
        receipt.ErrorMessage.Should().Contain("credential configuration");
        receipt.ErrorMessage.Should().NotContain("Retry shortly");
        receipt.ResultJson.Should().Contain(receipt.ErrorCode);
        receipt.ResultJson.Should().NotContain("unsafe-provider-secret");
        receipt.CallId.Should().Be("call-1");
        receipt.ToolName.Should().Be("nyxid_service_inventory");
    }

    [Theory]
    [InlineData("inventory_query_unavailable")]
    [InlineData("inventory_capability_unavailable")]
    public void Create_UnavailableRead_KeepsSanitizedQueryFailure(string errorCode)
    {
        var receipt = NyxIdServiceInventoryReceiptFactory.Create(
            "call-1", "nyxid_service_inventory",
            $$"""{"error":"{{errorCode}}","message":"unsafe-provider-secret"}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_SERVICE_INVENTORY_FAILED");
        receipt.ResultJson.Should().NotContain("unsafe-provider-secret");
    }
}
