namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowSpreadsheetExtractOptions
{
    public const string SectionName = "WorkflowSpreadsheetExtract";

    public int MaxWorkbookBytes { get; set; } = 5 * 1024 * 1024;

    public int MaxPackageEntries { get; set; } = 256;

    public int MaxPackageEntryBytes { get; set; } = 1024 * 1024;

    public int MaxSheets { get; set; } = 5;

    public int MaxRowsPerSheet { get; set; } = 50;

    public int MaxColumnsPerRow { get; set; } = 20;

    public int MaxCellChars { get; set; } = 200;

    public int MaxSharedStrings { get; set; } = 10_000;
}
