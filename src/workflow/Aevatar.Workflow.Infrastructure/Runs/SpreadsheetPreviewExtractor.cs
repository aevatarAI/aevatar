using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal sealed class SpreadsheetPreviewExtractor(WorkflowSpreadsheetExtractOptions options)
{
    private static readonly XNamespace PackageRelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationshipsNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly WorkflowSpreadsheetExtractOptions _options = NormalizeOptions(options);

    public WorkflowSpreadsheetPreview Extract(byte[] workbookBytes)
    {
        if (workbookBytes.Length > _options.MaxWorkbookBytes)
            throw new SpreadsheetPreviewException(
                SpreadsheetPreviewErrorCode.WorkbookTooLarge,
                $"Spreadsheet workbook exceeds {_options.MaxWorkbookBytes} bytes.");

        using var stream = new MemoryStream(workbookBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        ValidatePackage(archive);

        var workbookRelationships = LoadRelationships(archive, "xl/_rels/workbook.xml.rels");
        if (workbookRelationships.Values.Any(static relationship => relationship.IsExternalLink))
            throw new SpreadsheetPreviewException(
                SpreadsheetPreviewErrorCode.UnsafeWorkbook,
                "Spreadsheet workbook contains external links.");

        var workbookDocument = LoadXmlDocument(archive, "xl/workbook.xml");
        var sheetDefinitions = ReadSheetDefinitions(workbookDocument, workbookRelationships);
        var sharedStrings = ReadSharedStrings(archive);
        var sheets = new List<SpreadsheetSheetPreview>();
        var workbookTruncated = sheetDefinitions.Count > _options.MaxSheets;

        foreach (var sheetDefinition in sheetDefinitions.Take(_options.MaxSheets))
        {
            var sheet = ReadSheet(archive, sheetDefinition, sharedStrings);
            if (sheet.Truncated)
                workbookTruncated = true;
            sheets.Add(sheet);
        }

        return new WorkflowSpreadsheetPreview(
            new WorkflowSpreadsheetPreviewLimits(
                _options.MaxWorkbookBytes,
                _options.MaxSheets,
                _options.MaxRowsPerSheet,
                _options.MaxColumnsPerRow,
                _options.MaxCellChars),
            new WorkflowSpreadsheetWorkbookPreview(sheetDefinitions.Count, workbookTruncated),
            sheets,
            workbookTruncated);
    }

    private void ValidatePackage(ZipArchive archive)
    {
        if (archive.Entries.Count > _options.MaxPackageEntries)
            throw new SpreadsheetPreviewException(
                SpreadsheetPreviewErrorCode.WorkbookTooLarge,
                $"Spreadsheet workbook package exceeds {_options.MaxPackageEntries} entries.");

        var hasEncryptedPackage = false;
        var hasEncryptionInfo = false;
        foreach (var entry in archive.Entries)
        {
            var name = NormalizePartName(entry.FullName);
            if (entry.Length > _options.MaxPackageEntryBytes)
                throw new SpreadsheetPreviewException(
                    SpreadsheetPreviewErrorCode.WorkbookTooLarge,
                    $"Spreadsheet workbook package part exceeds {_options.MaxPackageEntryBytes} bytes.");

            if (string.Equals(name, "encryptedpackage", StringComparison.Ordinal))
                hasEncryptedPackage = true;
            if (string.Equals(name, "encryptioninfo", StringComparison.Ordinal))
                hasEncryptionInfo = true;
            if (name.EndsWith("vbaproject.bin", StringComparison.Ordinal) ||
                name.Contains("vbaprojectsignature", StringComparison.Ordinal))
            {
                throw new SpreadsheetPreviewException(
                    SpreadsheetPreviewErrorCode.UnsafeWorkbook,
                    "Spreadsheet workbook contains macros.");
            }
        }

        if (hasEncryptedPackage || hasEncryptionInfo)
            throw new SpreadsheetPreviewException(
                SpreadsheetPreviewErrorCode.EncryptedWorkbook,
                "Spreadsheet workbook is encrypted.");

        if (archive.GetEntry("xl/workbook.xml") == null ||
            archive.GetEntry("xl/_rels/workbook.xml.rels") == null)
            throw new SpreadsheetPreviewException(
                SpreadsheetPreviewErrorCode.InvalidWorkbook,
                "Spreadsheet workbook package is incomplete.");
    }

    private IReadOnlyList<SheetDefinition> ReadSheetDefinitions(
        XDocument workbookDocument,
        IReadOnlyDictionary<string, WorkbookRelationship> relationships)
    {
        var sheets = workbookDocument.Root?
            .Element(SpreadsheetNamespace + "sheets")?
            .Elements(SpreadsheetNamespace + "sheet") ?? [];
        var definitions = new List<SheetDefinition>();
        foreach (var sheet in sheets)
        {
            var relationshipId = (string?)sheet.Attribute(OfficeRelationshipsNamespace + "id");
            if (string.IsNullOrWhiteSpace(relationshipId) ||
                !relationships.TryGetValue(relationshipId, out var relationship) ||
                relationship.IsExternalLink)
            {
                throw new SpreadsheetPreviewException(
                    SpreadsheetPreviewErrorCode.InvalidWorkbook,
                    "Spreadsheet workbook sheet relationship is invalid.");
            }

            definitions.Add(new SheetDefinition(
                SanitizeSheetName((string?)sheet.Attribute("name")),
                relationship.TargetPart));
        }

        if (definitions.Count == 0)
            throw new SpreadsheetPreviewException(
                SpreadsheetPreviewErrorCode.InvalidWorkbook,
                "Spreadsheet workbook does not contain worksheets.");

        return definitions;
    }

    private IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        if (archive.GetEntry("xl/sharedStrings.xml") == null)
            return [];

        var document = LoadXmlDocument(archive, "xl/sharedStrings.xml");
        var values = new List<string>();
        foreach (var item in document.Descendants(SpreadsheetNamespace + "si"))
        {
            if (values.Count >= _options.MaxSharedStrings)
                throw new SpreadsheetPreviewException(
                    SpreadsheetPreviewErrorCode.WorkbookTooLarge,
                    $"Spreadsheet workbook shared strings exceed {_options.MaxSharedStrings} entries.");

            values.Add(string.Concat(item.Descendants(SpreadsheetNamespace + "t").Select(static text => text.Value)));
        }

        return values;
    }

    private SpreadsheetSheetPreview ReadSheet(
        ZipArchive archive,
        SheetDefinition sheetDefinition,
        IReadOnlyList<string> sharedStrings)
    {
        var document = LoadXmlDocument(archive, sheetDefinition.TargetPart);
        var rows = new List<SpreadsheetRowPreview>();
        var sheetTruncated = false;
        var rowElements = document.Descendants(SpreadsheetNamespace + "row");
        foreach (var rowElement in rowElements)
        {
            if (rows.Count >= _options.MaxRowsPerSheet)
            {
                sheetTruncated = true;
                break;
            }

            var cells = ReadCells(rowElement, sharedStrings, ref sheetTruncated);
            rows.Add(new SpreadsheetRowPreview(
                ParsePositiveInt((string?)rowElement.Attribute("r"), rows.Count + 1),
                cells));
        }

        return new SpreadsheetSheetPreview(sheetDefinition.Name, rows, sheetTruncated);
    }

    private IReadOnlyList<SpreadsheetCellPreview> ReadCells(
        XElement rowElement,
        IReadOnlyList<string> sharedStrings,
        ref bool sheetTruncated)
    {
        var cells = new List<SpreadsheetCellPreview>();
        foreach (var cellElement in rowElement.Elements(SpreadsheetNamespace + "c"))
        {
            if (cells.Count >= _options.MaxColumnsPerRow)
            {
                sheetTruncated = true;
                break;
            }

            var value = ResolveCellValue(cellElement, sharedStrings);
            var truncated = value.Length > _options.MaxCellChars;
            if (truncated)
                value = value[.._options.MaxCellChars];

            cells.Add(new SpreadsheetCellPreview(
                NormalizeCellReference((string?)cellElement.Attribute("r")),
                value,
                ResolveCellValueKind((string?)cellElement.Attribute("t")),
                truncated));
        }

        return cells;
    }

    private string ResolveCellValue(XElement cellElement, IReadOnlyList<string> sharedStrings)
    {
        var cellType = (string?)cellElement.Attribute("t");
        if (string.Equals(cellType, "s", StringComparison.Ordinal))
        {
            var indexText = cellElement.Element(SpreadsheetNamespace + "v")?.Value;
            if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var sharedStringIndex) ||
                sharedStringIndex < 0 ||
                sharedStringIndex >= sharedStrings.Count)
            {
                throw new SpreadsheetPreviewException(
                    SpreadsheetPreviewErrorCode.InvalidWorkbook,
                    "Spreadsheet workbook shared string reference is invalid.");
            }

            return sharedStrings[sharedStringIndex];
        }

        if (string.Equals(cellType, "inlineStr", StringComparison.Ordinal))
        {
            return string.Concat(cellElement
                .Descendants(SpreadsheetNamespace + "t")
                .Select(static text => text.Value));
        }

        return cellElement.Element(SpreadsheetNamespace + "v")?.Value ?? string.Empty;
    }

    private static IReadOnlyDictionary<string, WorkbookRelationship> LoadRelationships(
        ZipArchive archive,
        string partName)
    {
        var document = LoadXmlDocument(archive, partName);
        var relationships = new Dictionary<string, WorkbookRelationship>(StringComparer.Ordinal);
        foreach (var element in document.Root?.Elements(PackageRelationshipsNamespace + "Relationship") ?? [])
        {
            var id = (string?)element.Attribute("Id");
            var target = (string?)element.Attribute("Target");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
                continue;

            var targetMode = (string?)element.Attribute("TargetMode");
            var type = (string?)element.Attribute("Type") ?? string.Empty;
            var isExternal = string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("externalLink", StringComparison.OrdinalIgnoreCase);
            relationships[id] = new WorkbookRelationship(
                ResolveWorkbookRelationshipTarget(target),
                isExternal);
        }

        return relationships;
    }

    private static XDocument LoadXmlDocument(ZipArchive archive, string partName)
    {
        var entry = archive.GetEntry(partName)
            ?? throw new SpreadsheetPreviewException(
                SpreadsheetPreviewErrorCode.InvalidWorkbook,
                "Spreadsheet workbook package is incomplete.");
        using var stream = entry.Open();
        using var xmlReader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        return XDocument.Load(xmlReader, LoadOptions.None);
    }

    private static string ResolveWorkbookRelationshipTarget(string target)
    {
        var normalized = target.Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("xl/", StringComparison.Ordinal))
            normalized = $"xl/{normalized}";

        var fullPath = Path.GetFullPath(
            normalized,
            Path.GetFullPath("package-root", AppContext.BaseDirectory));
        var rootPath = Path.GetFullPath("package-root", AppContext.BaseDirectory);
        if (!fullPath.StartsWith(rootPath, StringComparison.Ordinal))
            throw new SpreadsheetPreviewException(
                SpreadsheetPreviewErrorCode.InvalidWorkbook,
                "Spreadsheet workbook sheet relationship is invalid.");

        return normalized;
    }

    private static string NormalizeCellReference(string? reference) =>
        string.IsNullOrWhiteSpace(reference) ? string.Empty : reference.Trim();

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static string ResolveCellValueKind(string? cellType) =>
        cellType switch
        {
            "s" => "shared_string",
            "inlineStr" => "inline_string",
            "b" => "boolean",
            "str" => "formula_string",
            "e" => "error",
            _ => "value",
        };

    private static string SanitizeSheetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Sheet";
        var normalized = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (!char.IsControl(c))
                normalized.Append(c);
        }

        return normalized.Length == 0 ? "Sheet" : normalized.ToString();
    }

    private static string NormalizePartName(string value) =>
        value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static WorkflowSpreadsheetExtractOptions NormalizeOptions(WorkflowSpreadsheetExtractOptions options) =>
        new()
        {
            MaxWorkbookBytes = PositiveOrDefault(options.MaxWorkbookBytes, 5 * 1024 * 1024),
            MaxPackageEntries = PositiveOrDefault(options.MaxPackageEntries, 256),
            MaxPackageEntryBytes = PositiveOrDefault(options.MaxPackageEntryBytes, 1024 * 1024),
            MaxSheets = PositiveOrDefault(options.MaxSheets, 5),
            MaxRowsPerSheet = PositiveOrDefault(options.MaxRowsPerSheet, 50),
            MaxColumnsPerRow = PositiveOrDefault(options.MaxColumnsPerRow, 20),
            MaxCellChars = PositiveOrDefault(options.MaxCellChars, 200),
            MaxSharedStrings = PositiveOrDefault(options.MaxSharedStrings, 10_000),
        };

    private static int PositiveOrDefault(int value, int defaultValue) =>
        value > 0 ? value : defaultValue;

    private sealed record SheetDefinition(string Name, string TargetPart);

    private sealed record WorkbookRelationship(string TargetPart, bool IsExternalLink);
}

internal sealed record WorkflowSpreadsheetPreview(
    WorkflowSpreadsheetPreviewLimits Limits,
    WorkflowSpreadsheetWorkbookPreview Workbook,
    IReadOnlyList<SpreadsheetSheetPreview> Sheets,
    bool Truncated);

internal sealed record WorkflowSpreadsheetPreviewLimits(
    int MaxWorkbookBytes,
    int MaxSheets,
    int MaxRowsPerSheet,
    int MaxColumnsPerRow,
    int MaxCellChars);

internal sealed record WorkflowSpreadsheetWorkbookPreview(int SheetCount, bool Truncated);

internal sealed record SpreadsheetSheetPreview(
    string Name,
    IReadOnlyList<SpreadsheetRowPreview> Rows,
    bool Truncated);

internal sealed record SpreadsheetRowPreview(
    int Index,
    IReadOnlyList<SpreadsheetCellPreview> Cells);

internal sealed record SpreadsheetCellPreview(
    string Reference,
    string Value,
    string Kind,
    bool Truncated);

internal sealed class SpreadsheetPreviewException(
    SpreadsheetPreviewErrorCode errorCode,
    string message) : Exception(message)
{
    public SpreadsheetPreviewErrorCode ErrorCode { get; } = errorCode;
}

internal enum SpreadsheetPreviewErrorCode
{
    InvalidWorkbook,
    UnsupportedWorkbook,
    UnsafeWorkbook,
    EncryptedWorkbook,
    WorkbookTooLarge,
}
