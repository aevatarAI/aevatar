using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;
using ProtoWorkflowCallerCredential = Aevatar.Workflow.Abstractions.WorkflowCallerCredential;
using ProtoWorkflowFileRef = Aevatar.Workflow.Abstractions.WorkflowFileRef;
using ProtoWorkflowFileSourceKind = Aevatar.Workflow.Abstractions.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowSpreadsheetExtractToolTests
{
    private const string XlsxMediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldReturnBoundedPreviewForStoredXlsx()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-spreadsheet-extract-preview-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var workbookBytes = BuildWorkbook(("Summary", new[]
            {
                new[] { "Name", "Amount" },
                new[] { "Alice", "42" },
            }));
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                workbookBytes,
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.xlsx",
                MediaType: $"{XlsxMediaType}; charset=binary"));
            var tool = await GetSpreadsheetExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSpreadsheetExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("kind").GetString().Should().Be("spreadsheet_preview");
            rootElement.GetProperty("media_type").GetString().Should().Be(XlsxMediaType);
            rootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(result.FileRef.FileId);
            rootElement.GetProperty("file").GetProperty("sha256").GetString().Should().Be(result.FileRef.Sha256);
            rootElement.GetProperty("limits").GetProperty("max_rows_per_sheet").GetInt32().Should().Be(50);
            rootElement.GetProperty("workbook").GetProperty("sheet_count").GetInt32().Should().Be(1);
            rootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();

            var sheet = rootElement.GetProperty("sheets").EnumerateArray().Should().ContainSingle().Subject;
            sheet.GetProperty("name").GetString().Should().Be("Summary");
            sheet.GetProperty("rows").EnumerateArray().Should().HaveCount(2);
            var firstRow = sheet.GetProperty("rows")[0];
            firstRow.GetProperty("index").GetInt32().Should().Be(1);
            firstRow.GetProperty("cells")[0].GetProperty("reference").GetString().Should().Be("A1");
            firstRow.GetProperty("cells")[0].GetProperty("value").GetString().Should().Be("Name");
            firstRow.GetProperty("cells")[1].GetProperty("value").GetString().Should().Be("Amount");
            var secondRow = sheet.GetProperty("rows")[1];
            secondRow.GetProperty("cells")[0].GetProperty("value").GetString().Should().Be("Alice");
            secondRow.GetProperty("cells")[1].GetProperty("value").GetString().Should().Be("42");

            output.ResultJson.Contains(Convert.ToBase64String(workbookBytes), StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("[Content_Types].xml", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            output.ResultJson.Contains("xl/worksheets", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            output.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldFallbackToSingleInputFileRef()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-spreadsheet-extract-input-ref-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                BuildWorkbook(("Sheet1", new[] { new[] { "single input" } })),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "single.xlsx",
                MediaType: XlsxMediaType));
            var tool = await GetSpreadsheetExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                "{}",
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential(),
                InputFileRefs: [ToProtoWorkflowFileRef(result.FileRef)]));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(result.FileRef.FileId);
            document.RootElement.GetProperty("sheets")[0].GetProperty("rows")[0]
                .GetProperty("cells")[0].GetProperty("value").GetString().Should().Be("single input");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldFailClosedWhenNoInputFileRefsAreAvailable()
    {
        var tool = await GetSpreadsheetExtractToolAsync(new StaticWorkflowFileArtifactReadPort(
            new ApplicationFileArtifactRef
            {
                FileId = "unread-workbook",
                SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
                FileName = "unread.xlsx",
                MediaType = XlsxMediaType,
                SizeBytes = 13,
            },
            new MemoryStream(Encoding.UTF8.GetBytes("hidden workbook"))));

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            "{}",
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
        document.RootElement.GetProperty("detail").GetString()
            .Should().Contain("requires a fileRef object or exactly one input file ref");
        output.Failure.Should().NotBeNull();
        output.Failure!.ErrorCode.Should().Be("invalid_arguments");
        output.Failure.ErrorMessage.Should().Contain("requires a fileRef object");
        output.ResultJson.Should().NotContain("hidden workbook");
        output.ResultJson.Should().NotContain("xl/");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldFailClosedWhenInputFileRefsAreAmbiguous()
    {
        var tool = await GetSpreadsheetExtractToolAsync(new StaticWorkflowFileArtifactReadPort(
            new ApplicationFileArtifactRef
            {
                FileId = "unread-workbook",
                SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
                FileName = "unread.xlsx",
                MediaType = XlsxMediaType,
                SizeBytes = 13,
            },
            new MemoryStream(Encoding.UTF8.GetBytes("hidden workbook"))));
        var firstFileRef = new ApplicationFileArtifactRef
        {
            FileId = "first-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "first.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = 1,
        };
        var secondFileRef = new ApplicationFileArtifactRef
        {
            FileId = "second-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "second.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = 1,
        };

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            "{}",
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential(),
            InputFileRefs: [
                ToProtoWorkflowFileRef(firstFileRef),
                ToProtoWorkflowFileRef(secondFileRef),
            ]));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
        document.RootElement.GetProperty("detail").GetString()
            .Should().Contain("received multiple input file refs; provide fileRef explicitly");
        output.ResultJson.Should().NotContain("hidden workbook");
        output.ResultJson.Should().NotContain("xl/");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectEmptyExplicitFileRef()
    {
        var tool = await GetSpreadsheetExtractToolAsync(new StaticWorkflowFileArtifactReadPort(
            new ApplicationFileArtifactRef
            {
                FileId = "unread-workbook",
                SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
                FileName = "unread.xlsx",
                MediaType = XlsxMediaType,
                SizeBytes = 13,
            },
            new MemoryStream(Encoding.UTF8.GetBytes("hidden workbook"))));

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            """{"file_ref":{}}""",
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
        document.RootElement.GetProperty("detail").GetString()
            .Should().Contain("fileRef requires fileId or artifactId");
        output.ResultJson.Should().NotContain("hidden workbook");
        output.ResultJson.Should().NotContain("xl/");
    }

    [Theory]
    [InlineData("application/vnd.ms-excel", "legacy.xls", "unsupported_media_type")]
    [InlineData("application/vnd.ms-excel.sheet.macroEnabled.12", "macro.xlsm", "unsupported_media_type")]
    [InlineData("application/vnd.ms-excel.sheet.binary.macroEnabled.12", "binary.xlsb", "unsupported_media_type")]
    [InlineData("application/vnd.oasis.opendocument.spreadsheet", "open.ods", "unsupported_media_type")]
    [InlineData("application/octet-stream", "unknown.xlsx", "unsupported_media_type")]
    public async Task WorkflowSpreadsheetExtractTool_ShouldFailClosedForUnsupportedMediaTypes(
        string mediaType,
        string fileName,
        string expectedError)
    {
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "unsupported-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = fileName,
            MediaType = mediaType,
            SizeBytes = 3,
        };
        var tool = await GetSpreadsheetExtractToolAsync(new StaticWorkflowFileArtifactReadPort(
            fileRef,
            new MemoryStream(new byte[] { 1, 2, 3 })));

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
        output.ResultJson.Should().NotContain("base64");
        output.ResultJson.Should().NotContain("xl/");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectWorkbookOverConfiguredByteLimit()
    {
        var workbookBytes = BuildWorkbook(("Sheet1", new[] { new[] { "too large" } }));
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "large-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "large.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = workbookBytes.Length,
        };
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)),
            new WorkflowSpreadsheetExtractOptions
            {
                MaxWorkbookBytes = workbookBytes.Length - 1,
            });

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("workbook_too_large");
        document.RootElement.GetProperty("detail").GetString().Should().Contain((workbookBytes.Length - 1).ToString());
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectUnderreportedStreamOverConfiguredByteLimit()
    {
        var workbookBytes = BuildWorkbook(("Sheet1", new[] { new[] { "stream too large" } }));
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "underreported-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "underreported.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = 0,
        };
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)),
            new WorkflowSpreadsheetExtractOptions
            {
                MaxWorkbookBytes = workbookBytes.Length - 1,
            });

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("workbook_too_large");
        document.RootElement.GetProperty("detail").GetString().Should().Contain((workbookBytes.Length - 1).ToString());
        output.ResultJson.Should().NotContain("stream too large");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectPackageEntryCountOverConfiguredLimit()
    {
        var workbookBytes = BuildWorkbookWithExtraEntries(3);
        var maxPackageEntries = CountPackageEntries(workbookBytes) - 1;
        var fileRef = BuildXlsxFileRef("entry-count-workbook", "entry-count.xlsx", workbookBytes);

        var output = await ExecuteSpreadsheetExtractAsync(
            fileRef,
            workbookBytes,
            new WorkflowSpreadsheetExtractOptions
            {
                MaxPackageEntries = maxPackageEntries,
            });

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("workbook_too_large");
        document.RootElement.GetProperty("detail").GetString()
            .Should().Contain($"exceeds {maxPackageEntries} entries");
        output.ResultJson.Should().NotContain("entry-count-secret");
        output.ResultJson.Should().NotContain("xl/extra");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectPackagePartOverConfiguredByteLimit()
    {
        const int maxPackageEntryBytes = 1024;
        var workbookBytes = BuildWorkbookWithExtraEntry(
            "xl/extra/oversized-part.xml",
            "package-part-secret-" + new string('x', maxPackageEntryBytes + 1));
        var fileRef = BuildXlsxFileRef("large-part-workbook", "large-part.xlsx", workbookBytes);

        var output = await ExecuteSpreadsheetExtractAsync(
            fileRef,
            workbookBytes,
            new WorkflowSpreadsheetExtractOptions
            {
                MaxPackageEntryBytes = maxPackageEntryBytes,
            });

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("workbook_too_large");
        document.RootElement.GetProperty("detail").GetString()
            .Should().Contain($"package part exceeds {maxPackageEntryBytes} bytes");
        output.ResultJson.Should().NotContain("package-part-secret");
        output.ResultJson.Should().NotContain("oversized-part.xml");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectSharedStringCountOverConfiguredLimit()
    {
        var workbookBytes = BuildWorkbook(("Sheet1", new[]
        {
            new[] { "first-shared-string", "shared-string-secret" },
        }));
        var fileRef = BuildXlsxFileRef("shared-string-limit-workbook", "shared-string-limit.xlsx", workbookBytes);

        var output = await ExecuteSpreadsheetExtractAsync(
            fileRef,
            workbookBytes,
            new WorkflowSpreadsheetExtractOptions
            {
                MaxSharedStrings = 1,
            });

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("workbook_too_large");
        document.RootElement.GetProperty("detail").GetString()
            .Should().Contain("shared strings exceed 1 entries");
        output.ResultJson.Should().NotContain("first-shared-string");
        output.ResultJson.Should().NotContain("shared-string-secret");
    }

    [Theory]
    [InlineData("legacy.xls")]
    [InlineData("macro.xlsm")]
    [InlineData("binary.xlsb")]
    [InlineData("open.ods")]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectNonXlsxFileNamesEvenWhenMediaTypeIsXlsx(
        string fileName)
    {
        var workbookBytes = BuildWorkbook(("Sheet1", new[] { new[] { "mislabeled" } }));
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "mislabeled-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = fileName,
            MediaType = XlsxMediaType,
            SizeBytes = workbookBytes.Length,
        };
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)));

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("unsupported_file_type");
        output.ResultJson.Should().NotContain("mislabeled");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectMacroWorkbookPackage()
    {
        var workbookBytes = BuildWorkbook(("Sheet1", new[] { new[] { "macro" } }), includeMacroProject: true);
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "macro-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "macro.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = workbookBytes.Length,
        };
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)));

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("unsafe_workbook");
        document.RootElement.GetProperty("detail").GetString().Should().Contain("macros");
        output.ResultJson.Should().NotContain("vbaProject.bin");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectExternalLinkWorkbookPackage()
    {
        var workbookBytes = BuildWorkbook(("Sheet1", new[] { new[] { "external" } }), includeExternalRelationship: true);
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "external-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "external.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = workbookBytes.Length,
        };
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)));

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("unsafe_workbook");
        document.RootElement.GetProperty("detail").GetString().Should().Contain("external links");
        output.ResultJson.Should().NotContain("https://example.com");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldRejectEncryptedWorkbookPackage()
    {
        var workbookBytes = BuildEncryptedWorkbookPackage();
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "encrypted-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "encrypted.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = workbookBytes.Length,
        };
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)));

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("encrypted_workbook");
        output.ResultJson.Should().NotContain("EncryptedPackage");
        output.ResultJson.Should().NotContain("EncryptionInfo");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldTruncateRowsCellsAndCellTextWithinPreviewLimits()
    {
        var workbookBytes = BuildWorkbook(("Sheet1", new[]
        {
            new[] { "very-long-cell-value", "hidden-column" },
            new[] { "hidden-row", "hidden-value" },
        }));
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "bounded-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "bounded.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = workbookBytes.Length,
        };
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)),
            new WorkflowSpreadsheetExtractOptions
            {
                MaxRowsPerSheet = 1,
                MaxColumnsPerRow = 1,
                MaxCellChars = 4,
            });

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        var rootElement = document.RootElement;
        rootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        var sheet = rootElement.GetProperty("sheets")[0];
        sheet.GetProperty("truncated").GetBoolean().Should().BeTrue();
        sheet.GetProperty("rows").EnumerateArray().Should().ContainSingle();
        var cell = sheet.GetProperty("rows")[0].GetProperty("cells").EnumerateArray().Should().ContainSingle().Subject;
        cell.GetProperty("value").GetString().Should().Be("very");
        cell.GetProperty("truncated").GetBoolean().Should().BeTrue();
        output.ResultJson.Should().NotContain("hidden-column");
        output.ResultJson.Should().NotContain("hidden-row");
    }

    [Fact]
    public async Task WorkflowSpreadsheetExtractTool_ShouldTruncateWorkbookSheetsWithinPreviewLimits()
    {
        var workbookBytes = BuildWorkbook([
            ("Visible", new[] { new[] { "visible-sheet" } }),
            ("Hidden", new[] { new[] { "hidden-sheet" } }),
        ]);
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "sheet-limited-workbook",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "sheet-limited.xlsx",
            MediaType = XlsxMediaType,
            SizeBytes = workbookBytes.Length,
        };
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)),
            new WorkflowSpreadsheetExtractOptions
            {
                MaxSheets = 1,
            });

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        var rootElement = document.RootElement;
        rootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        rootElement.GetProperty("workbook").GetProperty("sheet_count").GetInt32().Should().Be(2);
        rootElement.GetProperty("workbook").GetProperty("truncated").GetBoolean().Should().BeTrue();
        rootElement.GetProperty("sheets").EnumerateArray().Should().ContainSingle();
        var sheet = rootElement.GetProperty("sheets")[0];
        sheet.GetProperty("name").GetString().Should().Be("Visible");
        sheet.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("value").GetString()
            .Should().Be("visible-sheet");
        output.ResultJson.Should().NotContain("Hidden");
        output.ResultJson.Should().NotContain("hidden-sheet");
    }

    private static FileSystemFileArtifactPort CreateFileArtifactPort(string root) =>
        new(Options.Create(new FileSystemFileArtifactOptions
        {
            RootDirectory = root,
            TimeToLive = TimeSpan.FromMinutes(30),
        }));

    private static async Task<IWorkflowTool> GetSpreadsheetExtractToolAsync(
        IFileArtifactReadPort readPort,
        WorkflowSpreadsheetExtractOptions? options = null)
    {
        var source = new WorkflowSpreadsheetExtractToolSource(
            readPort,
            Options.Create(options ?? new WorkflowSpreadsheetExtractOptions()));
        var tools = await source.GetToolsAsync();
        return tools.Should().ContainSingle(x => x.Name == "spreadsheet_extract").Subject;
    }

    private static async Task<WorkflowToolExecutionResult> ExecuteSpreadsheetExtractAsync(
        ApplicationFileArtifactRef fileRef,
        byte[] workbookBytes,
        WorkflowSpreadsheetExtractOptions options)
    {
        var tool = await GetSpreadsheetExtractToolAsync(
            new StaticWorkflowFileArtifactReadPort(fileRef, new MemoryStream(workbookBytes)),
            options);

        return await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildSpreadsheetExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));
    }

    private static ApplicationFileArtifactRef BuildXlsxFileRef(
        string fileId,
        string fileName,
        byte[] workbookBytes) =>
        new()
        {
            FileId = fileId,
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = fileName,
            MediaType = XlsxMediaType,
            SizeBytes = workbookBytes.Length,
        };

    private static string BuildSpreadsheetExtractArguments(ApplicationFileArtifactRef fileRef)
    {
        var payload = new Dictionary<string, object?>
        {
            ["file_ref"] = new Dictionary<string, object?>
            {
                ["file_id"] = fileRef.FileId,
                ["artifact_id"] = fileRef.ArtifactId,
                ["source_kind"] = fileRef.SourceKind.ToString(),
                ["file_name"] = fileRef.FileName,
                ["media_type"] = fileRef.MediaType,
                ["size_bytes"] = fileRef.SizeBytes,
                ["sha256"] = fileRef.Sha256,
                ["created_at_unix_ms"] = fileRef.CreatedAtUnixMs,
                ["expires_at_unix_ms"] = fileRef.ExpiresAtUnixMs,
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static ProtoWorkflowFileRef ToProtoWorkflowFileRef(ApplicationFileArtifactRef source) =>
        new()
        {
            FileId = source.FileId ?? string.Empty,
            ArtifactId = source.ArtifactId ?? string.Empty,
            SourceKind = source.SourceKind switch
            {
                ApplicationFileArtifactSourceKind.ChatInput => ProtoWorkflowFileSourceKind.ChatInput,
                ApplicationFileArtifactSourceKind.FormUpload => ProtoWorkflowFileSourceKind.FormUpload,
                ApplicationFileArtifactSourceKind.ConnectedServiceResource => ProtoWorkflowFileSourceKind.ConnectedServiceResource,
                ApplicationFileArtifactSourceKind.ExternalResource => ProtoWorkflowFileSourceKind.ExternalResource,
                ApplicationFileArtifactSourceKind.Generated => ProtoWorkflowFileSourceKind.Generated,
                _ => ProtoWorkflowFileSourceKind.Unspecified,
            },
            SourceMessageId = source.SourceMessageId ?? string.Empty,
            SourceResourceKey = source.SourceResourceKey ?? string.Empty,
            FileName = source.FileName ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256 ?? string.Empty,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId ?? string.Empty,
            OwnerScopeId = source.OwnerScopeId ?? string.Empty,
        };

    private static byte[] BuildWorkbook(params (string Name, IReadOnlyList<string[]> Rows)[] sheets) =>
        BuildWorkbook(sheets, includeMacroProject: false, includeExternalRelationship: false);

    private static byte[] BuildWorkbook(
        (string Name, IReadOnlyList<string[]> Rows) sheet,
        bool includeMacroProject = false,
        bool includeExternalRelationship = false) =>
        BuildWorkbook([sheet], includeMacroProject, includeExternalRelationship);

    private static byte[] BuildWorkbook(
        IReadOnlyList<(string Name, IReadOnlyList<string[]> Rows)> sheets,
        bool includeMacroProject,
        bool includeExternalRelationship) =>
        BuildWorkbook(sheets, includeMacroProject, includeExternalRelationship, []);

    private static byte[] BuildWorkbook(
        IReadOnlyList<(string Name, IReadOnlyList<string[]> Rows)> sheets,
        bool includeMacroProject,
        bool includeExternalRelationship,
        IReadOnlyList<(string Name, string Content)> extraEntries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", BuildContentTypes(sheets.Count, includeMacroProject));
            AddEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "xl/workbook.xml", BuildWorkbookXml(sheets));
            AddEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationships(sheets.Count, includeExternalRelationship));
            AddEntry(archive, "xl/sharedStrings.xml", BuildSharedStringsXml(sheets));
            for (var i = 0; i < sheets.Count; i++)
            {
                AddEntry(archive, $"xl/worksheets/sheet{i + 1}.xml", BuildWorksheetXml(sheets[i].Rows));
            }

            if (includeMacroProject)
                AddEntry(archive, "xl/vbaProject.bin", "macro");

            foreach (var extraEntry in extraEntries)
            {
                AddEntry(archive, extraEntry.Name, extraEntry.Content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] BuildWorkbookWithExtraEntries(int extraEntryCount) =>
        BuildWorkbook(
            [("Sheet1", new[] { new[] { "entry-count-visible" } })],
            includeMacroProject: false,
            includeExternalRelationship: false,
            Enumerable.Range(0, extraEntryCount)
                .Select(static index => (
                    Name: $"xl/extra/entry{index + 1}.xml",
                    Content: $"entry-count-secret-{index + 1}"))
                .ToArray());

    private static byte[] BuildWorkbookWithExtraEntry(string entryName, string content) =>
        BuildWorkbook(
            [("Sheet1", new[] { new[] { "part-size-visible" } })],
            includeMacroProject: false,
            includeExternalRelationship: false,
            [(entryName, content)]);

    private static int CountPackageEntries(byte[] workbookBytes)
    {
        using var stream = new MemoryStream(workbookBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return archive.Entries.Count;
    }

    private static byte[] BuildEncryptedWorkbookPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "EncryptionInfo", "encrypted metadata");
            AddEntry(archive, "EncryptedPackage", "encrypted bytes");
        }

        return stream.ToArray();
    }

    private static string BuildContentTypes(int sheetCount, bool includeMacroProject)
    {
        var builder = new StringBuilder();
        builder.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
            """);
        for (var i = 1; i <= sheetCount; i++)
        {
            builder.Append(CultureInvariant($"""
                <Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            """));
        }

        if (includeMacroProject)
            builder.Append("""<Default Extension="bin" ContentType="application/vnd.ms-office.vbaProject"/>""");

        builder.Append("</Types>");
        return builder.ToString();
    }

    private static string BuildWorkbookXml(IReadOnlyList<(string Name, IReadOnlyList<string[]> Rows)> sheets)
    {
        var builder = new StringBuilder();
        builder.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
            """);
        for (var i = 0; i < sheets.Count; i++)
        {
            builder.Append(CultureInvariant($"""<sheet name="{EscapeXml(sheets[i].Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>"""));
        }

        builder.Append("""
              </sheets>
            </workbook>
            """);
        return builder.ToString();
    }

    private static string BuildWorkbookRelationships(int sheetCount, bool includeExternalRelationship)
    {
        var builder = new StringBuilder();
        builder.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            """);
        for (var i = 1; i <= sheetCount; i++)
        {
            builder.Append(CultureInvariant($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>"""));
        }

        if (includeExternalRelationship)
        {
            builder.Append("""
                  <Relationship Id="rIdExternal" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath" Target="https://example.com/source.xlsx" TargetMode="External"/>
                """);
        }

        builder.Append("</Relationships>");
        return builder.ToString();
    }

    private static string BuildSharedStringsXml(IReadOnlyList<(string Name, IReadOnlyList<string[]> Rows)> sheets)
    {
        var values = sheets.SelectMany(sheet => sheet.Rows).SelectMany(row => row).ToArray();
        var builder = new StringBuilder();
        builder.Append(CultureInvariant($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{values.Length}" uniqueCount="{values.Length}">
            """));
        foreach (var value in values)
        {
            builder.Append(CultureInvariant($"""<si><t>{EscapeXml(value)}</t></si>"""));
        }

        builder.Append("</sst>");
        return builder.ToString();
    }

    private static string BuildWorksheetXml(IReadOnlyList<string[]> rows)
    {
        var sharedStringIndex = 0;
        var builder = new StringBuilder();
        builder.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
            """);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            builder.Append(CultureInvariant($"""<row r="{rowIndex + 1}">"""));
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Length; columnIndex++)
            {
                var reference = $"{ColumnName(columnIndex + 1)}{rowIndex + 1}";
                builder.Append(CultureInvariant($"""<c r="{reference}" t="s"><v>{sharedStringIndex}</v></c>"""));
                sharedStringIndex++;
            }

            builder.Append("</row>");
        }

        builder.Append("""
              </sheetData>
            </worksheet>
            """);
        return builder.ToString();
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ColumnName(int columnNumber)
    {
        var builder = new StringBuilder();
        while (columnNumber > 0)
        {
            columnNumber--;
            builder.Insert(0, (char)('A' + (columnNumber % 26)));
            columnNumber /= 26;
        }

        return builder.ToString();
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string CultureInvariant(FormattableString value) =>
        FormattableString.Invariant(value);

    private sealed class StaticWorkflowFileArtifactReadPort(
        ApplicationFileArtifactRef fileRef,
        Stream content) : IFileArtifactReadPort
    {
        public ValueTask<ApplicationFileArtifactRef> DescribeAsync(
            ApplicationFileArtifactRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(fileRef);

        public ValueTask<FileArtifactContent> OpenReadAsync(
            ApplicationFileArtifactRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FileArtifactContent(fileRef, content));
    }
}
