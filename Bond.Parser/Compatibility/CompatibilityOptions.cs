using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Bond.Parser.Compatibility;

public sealed record CompatibilityOptions
{
    public bool IncludeImports { get; init; } = true;

    public bool AllowUnresolvedTypes { get; init; }

    public IReadOnlySet<string>? SuppressedDiagnosticIds { get; init; }

    internal void Validate()
    {
        if (SuppressedDiagnosticIds is null)
        {
            return;
        }

        foreach (var id in SuppressedDiagnosticIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (id is null || !DiagnosticIds.All.Contains(id))
            {
                throw new ArgumentException($"Unknown compatibility diagnostic ID '{id}'.", nameof(SuppressedDiagnosticIds));
            }
        }
    }
}

public sealed record CompatibilityResult(IReadOnlyList<SchemaChange> Changes)
{
    public ChangeSeverity MaxSeverity => Changes
        .Where(change => !change.IsSuppressed)
        .Select(change => change.Severity)
        .DefaultIfEmpty(ChangeSeverity.Info)
        .Max();

    public bool HasBreakingChanges => Changes.Any(change => !change.IsSuppressed && change.Severity == ChangeSeverity.Error);

    public int ExitCode => HasBreakingChanges ? 1 : 0;
}

/// <summary>Stable suppression identifiers. Categories describe impact; severity controls failure.</summary>
public static class DiagnosticIds
{
    public const string Unclassified = "BOND0000";
    public const string FieldType = "BOND0002";
    public const string OptionalToRequired = "BOND0003";
    public const string RequiredToOptional = "BOND0004";
    public const string BaseType = "BOND0005";
    public const string EnumValue = "BOND0006";
    public const string DeclarationKind = "BOND0007";
    public const string OptionalFieldAdded = "BOND0101";
    public const string RequiredFieldAdded = "BOND0102";
    public const string FieldRemoved = "BOND0103";
    public const string DeclarationAdded = "BOND0104";
    public const string DefaultValue = "BOND0201";
    public const string NothingDefault = "BOND0202";
    public const string ModifierRollout = "BOND0203";
    public const string TextName = "BOND0301";
    public const string EnumMemberRemoved = "BOND0405";
    public const string DeclarationRemoved = "BOND0406";
    public const string EnumTypeSemantics = "BOND0601";
    public const string EnumMemberAdded = "BOND0602";
    public const string MethodRemoved = "BOND0701";
    public const string MethodAdded = "BOND0702";
    public const string MethodSignature = "BOND0703";
    public const string ServiceBase = "BOND0704";
    public const string DuplicateDeclaration = "BOND0901";
    public const string DuplicateField = "BOND0902";
    public const string DuplicateEnumMember = "BOND0903";
    public const string DuplicateMethod = "BOND0904";
    public const string UnresolvedType = "BOND0905";
    public const string EnumOverflow = "BOND0906";
    public const string InvalidSchema = "BOND0907";
    public const string IncompleteDefinition = "BOND0908";

    public static IReadOnlyDictionary<string, string> Rules { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Unclassified] = "Unclassified change",
            [FieldType] = "Field wire type changed",
            [OptionalToRequired] = "Optional field became required",
            [RequiredToOptional] = "Required field became optional",
            [BaseType] = "Struct inheritance changed",
            [EnumValue] = "Existing enum numeric value changed",
            [DeclarationKind] = "Declaration kind changed",
            [OptionalFieldAdded] = "Optional field added",
            [RequiredFieldAdded] = "Required field added",
            [FieldRemoved] = "Field removed",
            [DeclarationAdded] = "Declaration added",
            [DefaultValue] = "Effective default changed",
            [NothingDefault] = "Nothing default changed",
            [ModifierRollout] = "Field modifier requires coordinated rollout",
            [TextName] = "Effective SimpleJSON field name changed",
            [EnumMemberRemoved] = "Enum member removed",
            [DeclarationRemoved] = "Declaration removed",
            [EnumTypeSemantics] = "Numeric enum interpretation changed",
            [EnumMemberAdded] = "Enum member added",
            [MethodRemoved] = "Service method removed",
            [MethodAdded] = "Service method added",
            [MethodSignature] = "Service signature changed",
            [ServiceBase] = "Service inheritance changed",
            [DuplicateDeclaration] = "Duplicate declaration",
            [DuplicateField] = "Duplicate field name or ordinal",
            [DuplicateEnumMember] = "Duplicate enum member name",
            [DuplicateMethod] = "Duplicate service method",
            [UnresolvedType] = "Unresolved type",
            [EnumOverflow] = "Implicit enum value overflows int32",
            [InvalidSchema] = "Invalid schema",
            [IncompleteDefinition] = "Incomplete type definition"
        });

    public static IReadOnlyCollection<string> All { get; } =
        Array.AsReadOnly(Rules.Keys.Order(StringComparer.Ordinal).ToArray());
}
