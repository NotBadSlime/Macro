using MacroHid.Core;

namespace MacroHid.Converter;

public enum MacroConversionFormat
{
    Auto,
    MacroHidMcrx,
    MacroConverterXml,
    RazerSynapseXml,
    Lua,
    XMouse,
    QMacro,
    GIMacrosJson,
    GengDiJi
}

public enum MacroDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record MacroConversionDiagnostic(
    MacroDiagnosticSeverity Severity,
    string Code,
    string Message,
    int? LineNumber = null,
    int? ColumnNumber = null);

public sealed class MacroImportException : FormatException
{
    public MacroImportException(
        string reason,
        MacroConversionFormat sourceFormat,
        string? fileName,
        int? lineNumber,
        int? columnNumber,
        string? sourceLine,
        Exception? innerException = null)
        : base(BuildMessage(reason, lineNumber, columnNumber, sourceLine), innerException)
    {
        Reason = reason;
        SourceFormat = sourceFormat;
        FileName = fileName;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
        SourceLine = sourceLine;
    }

    public string Reason { get; }

    public MacroConversionFormat SourceFormat { get; }

    public string? FileName { get; }

    public int? LineNumber { get; }

    public int? ColumnNumber { get; }

    public string? SourceLine { get; }

    private static string BuildMessage(string reason, int? lineNumber, int? columnNumber, string? sourceLine)
    {
        var location = lineNumber is { } line
            ? $"Line {line}" + (columnNumber is { } column ? $", column {column}" : string.Empty)
            : "Unknown location";
        return string.IsNullOrWhiteSpace(sourceLine)
            ? $"{location}: {reason}"
            : $"{location}: {reason}{Environment.NewLine}> {sourceLine}";
    }
}

public sealed record AuxiliaryMacroFile(string FileName, string Content);

public sealed record RazerModuleReference(string Guid, string Name);

public sealed record MacroImportRequest(
    string Content,
    string? FileName = null,
    MacroConversionFormat Format = MacroConversionFormat.Auto,
    IReadOnlyList<AuxiliaryMacroFile>? AuxiliaryFiles = null,
    bool PreserveRazerModuleCalls = false);

public sealed record MacroImportResult(
    MacroDocument Document,
    MacroConversionFormat SourceFormat,
    IReadOnlyList<MacroConversionDiagnostic> Diagnostics);

public sealed record MacroExportResult(
    string Output,
    MacroConversionFormat TargetFormat,
    IReadOnlyList<MacroConversionDiagnostic> Diagnostics,
    string FileName);

public sealed record MacroFormatInfo(
    MacroConversionFormat Format,
    string Label,
    string DefaultExtension,
    string FileDialogFilter,
    bool CanImport,
    bool CanExport);
