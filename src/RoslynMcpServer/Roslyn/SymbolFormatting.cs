using System.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynMcpServer.Roslyn;

public static class SymbolFormatting
{
    /// <summary>
    /// Describes a symbol with its display string and source location.
    /// </summary>
    /// <param name="symbol">The symbol to describe</param>
    /// <returns>Object containing display string and location info (file, line, column)</returns>
    public static SymbolDescription Describe(ISymbol symbol)
    {
        if (symbol == null)
        {
            return new SymbolDescription
            {
                Display = "<null>",
                File = null,
                Line = -1,
                Column = -1
            };
        }

        // Get fully qualified display string
        var displayFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .WithMemberOptions(
                SymbolDisplayMemberOptions.IncludeType |
                SymbolDisplayMemberOptions.IncludeParameters |
                SymbolDisplayMemberOptions.IncludeContainingType)
            .WithKindOptions(SymbolDisplayKindOptions.IncludeTypeKeyword)
            .WithGenericsOptions(
                SymbolDisplayGenericsOptions.IncludeTypeParameters |
                SymbolDisplayGenericsOptions.IncludeTypeConstraints)
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        var display = symbol.ToDisplayString(displayFormat);

        // Find source location if available
        var sourceLocation = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
        
        if (sourceLocation != null)
        {
            var lineSpan = sourceLocation.GetLineSpan();
            
            return new SymbolDescription
            {
                Display = display,
                File = lineSpan.Path,
                // Line and column are 0-based in Roslyn, but we want 1-based for display
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1
            };
        }

        // No source location available (e.g., metadata symbols)
        return new SymbolDescription
        {
            Display = display,
            File = null,
            Line = -1,
            Column = -1
        };
    }
}

/// <summary>
/// Represents a symbol description with display string and location.
/// </summary>
public class SymbolDescription
{
    /// <summary>
    /// Fully qualified display string of the symbol
    /// </summary>
    public required string Display { get; set; }
    
    /// <summary>
    /// Source file path if available, null otherwise
    /// </summary>
    public string? File { get; set; }
    
    /// <summary>
    /// Line number (1-based) if in source, -1 otherwise
    /// </summary>
    public required int Line { get; set; }
    
    /// <summary>
    /// Column number (1-based) if in source, -1 otherwise
    /// </summary>
    public required int Column { get; set; }

    /// <summary>
    /// Returns a formatted string representation of the symbol description
    /// </summary>
    public override string ToString()
    {
        if (File != null)
        {
            return $"{Display} at {File}:{Line}:{Column}";
        }
        return Display;
    }
}