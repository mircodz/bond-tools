namespace Bond.Parser.Compatibility;

public enum ChangeCategory
{
    /// <summary>
    /// No tagged binary or SimpleJSON contract is broken. Warnings may require coordinated rollout.
    /// </summary>
    Compatible,

    /// <summary>
    /// Breaks binary decoding or the interpretation of omitted values.
    /// </summary>
    BreakingWire,

    /// <summary>
    /// Breaks the SimpleJSON contract.
    /// </summary>
    BreakingText,

    /// <summary>The schema cannot be checked reliably.</summary>
    InvalidSchema,
}

public enum ChangeSeverity
{
    Info,
    Warning,
    Error
}

public record SchemaChange(
    ChangeCategory Category,
    string Description,
    string Location,
    string? Recommendation = null
)
{
    public string Id { get; init; } = DiagnosticIds.Unclassified;

    public ChangeSeverity Severity { get; init; } =
        Category == ChangeCategory.Compatible ? ChangeSeverity.Info : ChangeSeverity.Error;

    public bool IsSuppressed { get; init; }

    public override string ToString()
    {
        var categoryLabel = Category switch
        {
            ChangeCategory.Compatible => "COMPATIBLE",
            ChangeCategory.BreakingWire => "BREAKING-WIRE",
            ChangeCategory.BreakingText => "BREAKING-TEXT",
            ChangeCategory.InvalidSchema => "INVALID-SCHEMA",
            _ => "UNKNOWN"
        };

        var result = $"[{categoryLabel}] {Id}{(IsSuppressed ? " (suppressed)" : "")} {Location}: {Description}";
        if (Recommendation != null)
        {
            result += $"\n  → {Recommendation}";
        }

        return result;
    }
}
