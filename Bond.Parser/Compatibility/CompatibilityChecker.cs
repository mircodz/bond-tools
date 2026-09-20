using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bond.Parser.Compatibility;

/// <summary>Checks tagged binary (Compact/Fast) and SimpleJSON compatibility.</summary>
public class CompatibilityChecker
{
    public List<SchemaChange> CheckCompatibility(Syntax.Bond oldSchema, Syntax.Bond newSchema,
        CompatibilityOptions? options = null) => Compare(oldSchema, newSchema, options).Changes.ToList();

    public CompatibilityResult Compare(Syntax.Bond oldSchema, Syntax.Bond newSchema, CompatibilityOptions? options = null)
    {
        options ??= new CompatibilityOptions();
        options.Validate();
        var errors = new List<SchemaChange>();
        SchemaContract? Contract(Syntax.Bond schema, string side)
        {
            try { return SchemaContractValidation.Create(schema, options.IncludeImports, options.AllowUnresolvedTypes); }
            catch (InvalidDataException ex)
            {
                errors.Add(new SchemaChange(ChangeCategory.InvalidSchema, side + ": " + ex.Message,
                    ex.Data["Location"] as string ?? "schema")
                { Id = ex.Data["Id"] as string ?? DiagnosticIds.InvalidSchema, Severity = ChangeSeverity.Error });
                return null;
            }
        }
        var oldContract = Contract(oldSchema, "Previous schema");
        var newContract = Contract(newSchema, "Current schema");
        var changes = errors.Count != 0 ? errors : new Comparison(oldContract!, newContract!, options).Run();
        return new CompatibilityResult(Array.AsReadOnly(changes.Select(c => c with
        {
            IsSuppressed = options.SuppressedDiagnosticIds?.Contains(c.Id) == true
        }).OrderBy(c => c.Location, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Description, StringComparer.Ordinal).ToArray()));
    }

    private sealed class Comparison
    {
        private readonly Dictionary<string, ContractDeclaration> _oldDeclarations;
        private readonly Dictionary<string, ContractDeclaration> _newDeclarations;
        private readonly Dictionary<string, string> _declarationPairs = new(StringComparer.Ordinal);
        private readonly List<SchemaChange> _changes = [];

        internal Comparison(SchemaContract oldContract, SchemaContract newContract, CompatibilityOptions options)
        {
            _oldDeclarations = oldContract.Declarations.Where(d => options.IncludeImports || d.IsRoot).ToDictionary(d => d.Name, StringComparer.Ordinal);
            _newDeclarations = newContract.Declarations.Where(d => options.IncludeImports || d.IsRoot).ToDictionary(d => d.Name, StringComparer.Ordinal);
            foreach (var name in _oldDeclarations.Keys.Intersect(_newDeclarations.Keys, StringComparer.Ordinal))
                _declarationPairs.Add(name, name);

            // A unique unchanged declaration name also identifies namespace-only moves.
            static string LocalName(ContractDeclaration d) => d.Name.Split('.').Last();
            var moved = _newDeclarations.Values.Where(d => !_declarationPairs.ContainsValue(d.Name))
                .GroupBy(LocalName).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (var group in _oldDeclarations.Values.Where(d => !_declarationPairs.ContainsKey(d.Name)).GroupBy(LocalName))
                if (group.Count() == 1 && moved.TryGetValue(group.Key, out var candidates) && candidates.Length == 1)
                    _declarationPairs.Add(group.Single().Name, candidates[0].Name);
        }

        internal IReadOnlyList<SchemaChange> Run()
        {
            foreach (var old in _oldDeclarations.Values)
            {
                if (!_declarationPairs.TryGetValue(old.Name, out var currentName))
                {
                    Add(DiagnosticIds.DeclarationRemoved, ChangeCategory.Compatible,
                        $"{old.Kind} '{old.Name}' was removed", old.Name);
                    continue;
                }
                CompareDeclaration(old, _newDeclarations[currentName]);
            }
            var paired = _declarationPairs.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var current in _newDeclarations.Values.Where(d => !paired.Contains(d.Name)))
                Add(DiagnosticIds.DeclarationAdded, ChangeCategory.Compatible, $"{current.Kind} '{current.Name}' was added", current.Name);
            return _changes;
        }

        private void CompareDeclaration(ContractDeclaration old, ContractDeclaration current)
        {
            if (old.Kind == "forward" && current.Kind == "struct")
            {
                Add(DiagnosticIds.DeclarationAdded, ChangeCategory.Compatible, "Forward declaration acquired a complete struct definition", old.Name);
                return;
            }
            if (old.Kind == "struct" && current.Kind == "forward")
            {
                Add(DiagnosticIds.IncompleteDefinition, ChangeCategory.InvalidSchema,
                    "Struct definition was replaced by an incomplete forward declaration; its layout cannot be established", old.Name);
                return;
            }
            if (old.Kind != current.Kind)
            {
                Add(DiagnosticIds.DeclarationKind, ChangeCategory.BreakingWire,
                    $"Declaration kind changed from {old.Kind} to {current.Kind}", old.Name);
                return;
            }
            switch (old.Kind)
            {
                case "struct": CompareStruct(old, current, old.Name, []); break;
                case "enum": CompareEnum(old, current); break;
                case "service": CompareService(old, current); break;
            }
        }

        private void CompareStruct(ContractDeclaration old, ContractDeclaration current, string location, HashSet<string> active)
        {
            if (!Same(old.BaseType, current.BaseType) || NeedsPayloadComparison(old.BaseType, current.BaseType))
                CompareBase(old.BaseType, current.BaseType, location, active);
            var oldFields = old.Fields.ToDictionary(f => f.Ordinal);
            var newFields = current.Fields.ToDictionary(f => f.Ordinal);
            foreach (var field in old.Fields)
            {
                if (newFields.TryGetValue(field.Ordinal, out var next)) CompareField(location, field, next, active);
                else
                    Add(DiagnosticIds.FieldRemoved,
                        field.Modifier == "required" ? ChangeCategory.BreakingWire : ChangeCategory.Compatible,
                        $"Field {field.Ordinal} '{field.Name}' ({field.Modifier}) was removed", location + "." + field.Name,
                        field.Modifier == "required"
                            ? "Old readers still require this field. Relax readers before removing it; do not reuse the ordinal."
                            : "Keep the removed field commented out to document its ordinal; do not reuse that ordinal.");
            }
            foreach (var field in current.Fields.Where(f => !oldFields.ContainsKey(f.Ordinal)))
                Add(field.Modifier == "required" ? DiagnosticIds.RequiredFieldAdded : DiagnosticIds.OptionalFieldAdded,
                    field.Modifier == "required" ? ChangeCategory.BreakingWire : ChangeCategory.Compatible,
                    $"Field {field.Ordinal} '{field.Name}' ({field.Modifier}) was added", location + "." + field.Name,
                    field.Modifier == "required"
                        ? "Old data omits this field. Start with required_optional (always write, accept absence), update every producer, then require it."
                        : null);
        }

        private void CompareBase(TypeShape? old, TypeShape? current, string location, HashSet<string> active)
        {
            var start = _changes.Count;
            if (old is null || current is null || !Classify(old, current, location + ".base", active).Compatible)
                Add(DiagnosticIds.BaseType, ChangeCategory.BreakingWire,
                    $"Inheritance layout changed from '{old?.ToString() ?? "none"}' to '{current?.ToString() ?? "none"}'",
                    location, "Preserve the base payload layout and inheritance levels.");
            for (var i = start; i < _changes.Count; i++)
                if (_changes[i].Category == ChangeCategory.BreakingWire
                    && _changes[i].Id is DiagnosticIds.FieldType or DiagnosticIds.RequiredFieldAdded or DiagnosticIds.FieldRemoved)
                    _changes[i] = _changes[i] with
                    {
                        Id = DiagnosticIds.BaseType, Description = "Inheritance layout changed: " + _changes[i].Description
                    };
        }

        private void CompareField(string owner, ContractField old, ContractField current, HashSet<string> active)
        {
            var location = owner + "." + old.Name;
            if (old.JsonName != current.JsonName)
                Add(DiagnosticIds.TextName, ChangeCategory.BreakingText,
                    $"Effective SimpleJSON field name changed from '{old.JsonName}' to '{current.JsonName}'", location,
                    "Pin JsonName to the old text name when renaming the field.");
            if (old.Modifier != current.Modifier)
            {
                var direct = old.Modifier == "optional" && current.Modifier == "required"
                    || old.Modifier == "required" && current.Modifier == "optional";
                var id = direct ? old.Modifier == "optional" ? DiagnosticIds.OptionalToRequired : DiagnosticIds.RequiredToOptional
                    : DiagnosticIds.ModifierRollout;
                Add(id, direct ? ChangeCategory.BreakingWire : ChangeCategory.Compatible,
                    $"Modifier changed from {old.Modifier} to {current.Modifier}", location,
                    ModifierRecommendation(old.Modifier, current.Modifier),
                    direct ? ChangeSeverity.Error : ChangeSeverity.Warning);
            }
            var oldType = UnwrapMaybe(old.Type);
            var newType = UnwrapMaybe(current.Type);
            if (!oldType.Same(newType) || NeedsPayloadComparison(oldType, newType))
            {
                var change = Classify(oldType, newType, location, active);
                if (!change.NominalOnly)
                {
                    var semanticOnly = change.EnumSemantics && !change.Promotion && change.Compatible;
                    Add(semanticOnly ? DiagnosticIds.EnumTypeSemantics : DiagnosticIds.FieldType,
                        change.Compatible ? ChangeCategory.Compatible : ChangeCategory.BreakingWire,
                        $"Type changed from {oldType} to {newType}", location,
                        !change.Compatible ? "This type change is not wire compatible."
                            : change.Promotion ? "Deploy widened consumers before producers; old readers may reject new wider values."
                            : semanticOnly ? "The numeric representation is compatible, but validate enum meanings and accepted values." : null,
                        !change.Compatible ? ChangeSeverity.Error
                            : change.Promotion || semanticOnly ? ChangeSeverity.Warning : ChangeSeverity.Info);
                    if (change.EnumSemantics && !semanticOnly)
                        Add(DiagnosticIds.EnumTypeSemantics, ChangeCategory.Compatible,
                            $"Numeric enum interpretation changed from {oldType} to {newType}", location,
                            "The numeric representation is compatible, but validate the meaning and accepted domain of enum values.", ChangeSeverity.Warning);
                }
            }
            var nothingChanged = (old.Default.Kind == "nothing") != (current.Default.Kind == "nothing");
            if (nothingChanged)
                Add(DiagnosticIds.NothingDefault, ChangeCategory.BreakingWire,
                    $"Field presence default changed from {Display(old.Default)} to {Display(current.Default)}", location,
                    "nothing represents absence, not Bond nullable<T>'s list encoding. Check absent-value handling in both readers and writers.");
            else if (old.Default != current.Default && old.Default.Kind == current.Default.Kind)
            {
                var meaningful = old.Modifier == "optional" || current.Modifier == "optional";
                Add(DiagnosticIds.DefaultValue, meaningful ? ChangeCategory.BreakingWire : ChangeCategory.Compatible,
                    $"Default value changed from {Display(old.Default)} to {Display(current.Default)}", location,
                    meaningful ? "Omitted fields acquire different values under the two schemas; this changes meaning, not necessarily parsing."
                        : "These modifiers always write the field; only newly constructed values change.");
            }
        }

        private void CompareEnum(ContractDeclaration old, ContractDeclaration current)
        {
            var oldValues = old.Constants.ToDictionary(c => c.Name, c => c.Value, StringComparer.Ordinal);
            var newValues = current.Constants.ToDictionary(c => c.Name, c => c.Value, StringComparer.Ordinal);
            foreach (var constant in old.Constants)
            {
                if (!newValues.TryGetValue(constant.Name, out var value))
                    Add(DiagnosticIds.EnumMemberRemoved, ChangeCategory.Compatible,
                        $"Enum constant '{constant.Name}' was removed", old.Name + "." + constant.Name,
                        "Unknown numeric enum values still decode; check application interpretation.", ChangeSeverity.Warning);
                else if (value != constant.Value)
                    Add(DiagnosticIds.EnumValue, ChangeCategory.BreakingWire,
                        $"Enum constant '{constant.Name}' value changed from {constant.Value} to {value}", old.Name + "." + constant.Name,
                        "Preserve existing numeric meanings. Explicitly number members before inserting or removing implicit constants.");
            }
            foreach (var constant in current.Constants.Where(c => !oldValues.ContainsKey(c.Name)))
            {
                var alias = current.Constants.Any(c => c.Name != constant.Name && c.Value == constant.Value);
                Add(DiagnosticIds.EnumMemberAdded, ChangeCategory.Compatible,
                    $"Enum constant '{constant.Name}' was added" + (alias ? $" as a numeric alias for {constant.Value}" : ""),
                    current.Name + "." + constant.Name,
                    alias ? "Numeric aliases are legal; name-based application logic may distinguish them." : null,
                    alias ? ChangeSeverity.Warning : ChangeSeverity.Info);
            }
        }

        private void CompareService(ContractDeclaration old, ContractDeclaration current)
        {
            if (!Same(old.BaseType, current.BaseType))
                Add(DiagnosticIds.ServiceBase, ChangeCategory.BreakingWire,
                    $"Inheritance changed from '{old.BaseType?.ToString() ?? "none"}' to '{current.BaseType?.ToString() ?? "none"}'", old.Name);
            var oldMethods = old.Methods.ToDictionary(m => m.Name, StringComparer.Ordinal);
            var newMethods = current.Methods.ToDictionary(m => m.Name, StringComparer.Ordinal);
            foreach (var method in old.Methods)
            {
                if (!newMethods.TryGetValue(method.Name, out var next))
                {
                    Add(DiagnosticIds.MethodRemoved, ChangeCategory.BreakingWire, $"Method '{method.Name}' was removed", old.Name + "." + method.Name);
                    continue;
                }
                if (method.Kind != next.Kind || !method.Input.Same(next.Input) || !method.Result.Same(next.Result))
                    Add(DiagnosticIds.MethodSignature, ChangeCategory.BreakingWire,
                        $"Method '{method.Name}' signature changed from {method.Kind} {method.Result} ({method.Input}) to {next.Kind} {next.Result} ({next.Input})",
                        old.Name + "." + method.Name);
            }
            foreach (var method in current.Methods.Where(m => !oldMethods.ContainsKey(m.Name)))
                Add(DiagnosticIds.MethodAdded, ChangeCategory.Compatible, $"Method '{method.Name}' was added", current.Name + "." + method.Name);
        }

        private void Add(string id, ChangeCategory category, string description, string location, string? recommendation = null,
            ChangeSeverity? severity = null) =>
            _changes.Add(new SchemaChange(category, description, location, recommendation)
            {
                Id = id, Severity = severity ?? (category == ChangeCategory.Compatible ? ChangeSeverity.Info : ChangeSeverity.Error)
            });

        private static bool Same(TypeShape? a, TypeShape? b) => a is null ? b is null : a.Same(b);
        private static TypeShape UnwrapMaybe(TypeShape type) => type.Kind == "maybe" ? type.Arguments[0] : type;
        private static string Display(ContractDefault value) => value.Kind == "string" ? $"\"{value.Value}\"" : value.Value.Length == 0 ? value.Kind : value.Value;

        private readonly record struct TypeChange(bool Compatible, bool Promotion = false,
            bool EnumSemantics = false, bool NominalOnly = false);

        private TypeChange Classify(TypeShape old, TypeShape current, string location, HashSet<string> active)
        {
            if (old.Same(current) && !NeedsPayloadComparison(old, current)) return new(true, NominalOnly: true);
            // Unbound parameters describe templates, not serialized types. Their concrete uses are checked below.
            if (old.Kind == "parameter" || current.Kind == "parameter") return new(true, NominalOnly: true);
            if (old.Kind == "maybe" || current.Kind == "maybe")
                return Classify(UnwrapMaybe(old), UnwrapMaybe(current), location, active);
            if (old.Kind == "bonded" && current.Kind != "bonded")
                return Classify(old.Arguments[0], current, location, active) with { NominalOnly = false };
            if (current.Kind == "bonded" && old.Kind != "bonded")
                return Classify(old, current.Arguments[0], location, active) with { NominalOnly = false };
            if (old.Kind == "blob" && current.Kind is "list" or "vector") return new(current.Arguments[0].Kind == "int8");
            if (current.Kind == "blob" && old.Kind is "list" or "vector") return new(old.Arguments[0].Kind == "int8");
            if (old.Kind is "enum" or "int32" && current.Kind is "enum" or "int32")
                return new(true, EnumSemantics: true);
            if (old.Kind is "int8" or "int16" && current.Kind == "enum")
                return new(true, Promotion: true, EnumSemantics: true);
            var oldWidth = Width(old.Kind);
            var newWidth = Width(current.Kind);
            if (oldWidth > 0 && newWidth > oldWidth && NumericFamily(old.Kind) == NumericFamily(current.Kind))
                return new(true, Promotion: true);
            if (old.Kind == "struct" && current.Kind == "struct"
                && _oldDeclarations.TryGetValue(old.Name!, out var oldTemplate)
                && _newDeclarations.TryGetValue(current.Name!, out var newTemplate)
                && oldTemplate.Kind == "struct" && newTemplate.Kind == "struct")
            {
                if (!NeedsPayloadComparison(old, current)) return new(true, NominalOnly: true);
                var key = old + " -> " + current;
                if (active.Contains(key)) return new(true, NominalOnly: true);
                if (active.Count >= 64)
                {
                    Add(DiagnosticIds.IncompleteDefinition, ChangeCategory.InvalidSchema,
                        "Expanding generic recursion prevents establishing a finite payload comparison", location);
                    return new(true, NominalOnly: true);
                }
                active.Add(key);
                CompareStruct(Instantiate(oldTemplate, old.Arguments), Instantiate(newTemplate, current.Arguments), location, active);
                active.Remove(key);
                return new(true, NominalOnly: true);
            }
            if (old.Kind is "list" or "vector" && current.Kind is "list" or "vector"
                || old.Kind == current.Kind && old.Kind is "map" or "set" or "nullable" or "bonded" or "stream")
            {
                var children = old.Arguments.Zip(current.Arguments)
                    .Select(pair => Classify(pair.First, pair.Second, location, active)).ToArray();
                return new(children.All(c => c.Compatible), children.Any(c => c.Promotion), children.Any(c => c.EnumSemantics),
                    old.Kind == current.Kind && children.All(c => c.NominalOnly));
            }
            return new(false);
        }

        private bool NeedsPayloadComparison(TypeShape? old, TypeShape? current)
        {
            if (old is null || current is null) return false;
            if (old.Kind == "struct" && current.Kind == "struct")
            {
                if (old.Same(current) && (!_oldDeclarations.ContainsKey(old.Name!) || !_newDeclarations.ContainsKey(current.Name!)))
                    return false;
                if (!_declarationPairs.TryGetValue(old.Name!, out var paired) || paired != current.Name
                    || old.Arguments.Count != current.Arguments.Count
                    || !old.Arguments.Zip(current.Arguments).All(p => p.First.Same(p.Second)))
                    return true;
                return old.Arguments.Count != 0 && HasParameterizedChanges(old.Name!, current.Name!, []);
            }
            return old.Arguments.Count == current.Arguments.Count
                && old.Arguments.Zip(current.Arguments).Any(p => NeedsPayloadComparison(p.First, p.Second));
        }

        private bool HasParameterizedChanges(string oldName, string newName, HashSet<string> visited)
        {
            if (!visited.Add(oldName + " -> " + newName)
                || !_oldDeclarations.TryGetValue(oldName, out var old)
                || !_newDeclarations.TryGetValue(newName, out var current)) return false;
            bool Changed(TypeShape? a, TypeShape? b)
            {
                if (a is null || b is null) return false;
                if (!a.Same(b) && (ContainsParameter(a) || ContainsParameter(b))) return true;
                if (a.Kind == "struct" && b.Kind == "struct" && ContainsParameter(a))
                    return HasParameterizedChanges(a.Name!, b.Name!, visited);
                return a.Arguments.Count == b.Arguments.Count
                    && a.Arguments.Zip(b.Arguments).Any(p => Changed(p.First, p.Second));
            }
            if (Changed(old.BaseType, current.BaseType)) return true;
            var fields = current.Fields.ToDictionary(f => f.Ordinal);
            return old.Fields.Any(f => fields.TryGetValue(f.Ordinal, out var next) && Changed(f.Type, next.Type));
        }

        private static bool ContainsParameter(TypeShape type) =>
            type.Kind == "parameter" || type.Arguments.Any(ContainsParameter);

        private static ContractDeclaration Instantiate(ContractDeclaration declaration, IReadOnlyList<TypeShape> arguments) =>
            declaration with
            {
                BaseType = declaration.BaseType is null ? null : Substitute(declaration.BaseType, arguments),
                Fields = SchemaContractValidation.ReadOnly(declaration.Fields.Select(field =>
                {
                    var type = Substitute(field.Type, arguments);
                    return field with
                    {
                        Type = type,
                        Modifier = type.Kind is "meta_name" or "meta_full_name" ? "required_optional" : field.Modifier,
                        Default = field.Default.Kind == "generic" ? SchemaContractBuilder.ImplicitDefault(type, declaration.Name) : field.Default
                    };
                }))
            };

        private static TypeShape Substitute(TypeShape type, IReadOnlyList<TypeShape> arguments) =>
            type.Kind == "parameter" ? arguments[(int)type.Integer!.Value]
                : type with { Arguments = SchemaContractValidation.ReadOnly(type.Arguments.Select(t => Substitute(t, arguments))) };

        private static int Width(string kind) => kind switch
        {
            "int8" or "uint8" => 8, "int16" or "uint16" => 16, "int32" or "uint32" or "float" => 32,
            "int64" or "uint64" or "double" => 64, _ => 0
        };
        private static string NumericFamily(string kind) => kind.StartsWith("uint", StringComparison.Ordinal) ? "unsigned"
            : kind.StartsWith("int", StringComparison.Ordinal) ? "signed" : "floating";

        private static string ModifierRecommendation(string old, string current) => (old, current) switch
        {
            ("optional", "required") => "Use optional → required_optional → required: deploy always-writing producers first; require the field only after all producers have upgraded.",
            ("required", "optional") => "Use required → required_optional → optional: relax all readers first, then allow producers to omit the field.",
            ("optional", "required_optional") => "Deploy producers first so they always write the field; required_optional readers still accept absence in old data.",
            ("required_optional", "required") => "Require the field only after every producer always writes it and retained old data supplies it.",
            ("required", "required_optional") => "Relax readers first; all readers must accept omission before producers switch to optional.",
            ("required_optional", "optional") => "Allow producers to omit the field only after all readers accept omission.",
            _ => "Coordinate readers and writers before changing field presence."
        };
    }
}
