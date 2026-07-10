using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public sealed class LarkConnectedServiceResourceFetchAdapterRegistrationTests
{
    [Fact]
    public void AddLarkTools_ShouldRegisterMessageResourceFetchAdapter()
    {
        var services = new ServiceCollection();

        services.AddLarkTools();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowConnectedServiceResourceFetchAdapter) &&
            descriptor.ImplementationType == typeof(LarkMessageResourceFetchAdapter));
    }

    [Fact]
    public void LarkMessageResourceFetchAdapter_ShouldAdvertiseCanonicalMessageResourceRoutes()
    {
        var adapter = new LarkMessageResourceFetchAdapter(new ThrowingLarkNyxClient());

        adapter.Routes.Should().BeEquivalentTo(
            [
                new WorkflowConnectedServiceResourceFetchRoute("lark", "message_resource_download", "image"),
                new WorkflowConnectedServiceResourceFetchRoute("lark", "message_resource_download", "file"),
            ],
            options => options.WithStrictOrdering());
    }

    private sealed class ThrowingLarkNyxClient : ILarkNyxClient
    {
        public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LarkMessageResourceDownloadResult> DownloadMessageResourceAsync(
            string token,
            LarkMessageResourceDownloadRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> GetApprovalInstanceAsync(string token, LarkApprovalInstanceGetRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateDocxDocumentAsync(string token, LarkDocxCreateRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> AppendDocxTextBlocksAsync(string token, LarkDocxAppendBlocksRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SetDrivePermissionAsync(string token, LarkDrivePermissionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateBitableAppAsync(string token, LarkBitableCreateRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> GrantResourceMemberAsync(string token, LarkResourceMemberGrantRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> UploadDriveMediaAsync(string token, LarkDriveMediaUploadRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> UploadApprovalFileAsync(string token, LarkApprovalFileUploadRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
