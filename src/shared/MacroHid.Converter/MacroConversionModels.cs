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
    QMacro
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
    string Message);

public sealed record AuxiliaryMacroFile(string FileName, string Content);

public sealed record MacroImportRequest(
    string Content,
    string? FileName = null,
    MacroConversionFormat Format = MacroConversionFormat.Auto,
    IReadOnlyList<AuxiliaryMacroFile>? AuxiliaryFiles = null);

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
