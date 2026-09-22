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

        var validationErrors = new List<SchemaChange>();
        SchemaContract? BuildContract(Syntax.Bond schema, string side)
        {
            try
            {
                return SchemaContractValidation.Create(schema, options.IncludeImports, options.AllowUnresolvedTypes);
            }
            catch (InvalidDataException ex)
            {
                validationErrors.Add(new SchemaChange(ChangeCategory.InvalidSchema, side + ": " + ex.Message,
                    ex.Data["Location"] as string ?? "schema")
                {
                    Id = ex.Data["Id"] as string ?? DiagnosticIds.InvalidSchema,
                    Severity = ChangeSeverity.Error
                });

                return null;
            }
        }

        var oldContract = BuildContract(oldSchema, "Previous schema");
        var newContract = BuildContract(newSchema, "Current schema");
        IReadOnlyList<SchemaChange> changes;
        if (validationErrors.Count != 0)
        {
            changes = validationErrors;
        }
        else
        {
            changes = new Comparison(oldContract!, newContract!, options).Run();
        }

        var orderedChanges = changes
            .Select(change => change with
            {
                IsSuppressed = options.SuppressedDiagnosticIds?.Contains(change.Id) == true
            })
            .OrderBy(change => change.Location, StringComparer.Ordinal)
            .ThenBy(change => change.Id, StringComparer.Ordinal)
            .ThenBy(change => change.Description, StringComparer.Ordinal)
            .ToArray();

        return new CompatibilityResult(Array.AsReadOnly(orderedChanges));
    }

    private sealed class Comparison
    {
        private readonly Dictionary<string, ContractDeclaration> _oldDeclarations;
        private readonly Dictionary<string, ContractDeclaration> _newDeclarations;
        private readonly Dictionary<string, string> _declarationPairs = new(StringComparer.Ordinal);
        private readonly List<SchemaChange> _changes = [];

        internal Comparison(SchemaContract oldContract, SchemaContract newContract, CompatibilityOptions options)
        {
            _oldDeclarations = oldContract.Declarations
                .Where(declaration => options.IncludeImports || declaration.IsRoot)
                .ToDictionary(declaration => declaration.Name, StringComparer.Ordinal);
            _newDeclarations = newContract.Declarations
                .Where(declaration => options.IncludeImports || declaration.IsRoot)
                .ToDictionary(declaration => declaration.Name, StringComparer.Ordinal);

            foreach (var name in _oldDeclarations.Keys.Intersect(_newDeclarations.Keys, StringComparer.Ordinal))
            {
                _declarationPairs.Add(name, name);
            }

            // A unique unchanged declaration name also identifies namespace-only moves.
            static string LocalName(ContractDeclaration declaration) => declaration.Name.Split('.').Last();
            var unmatchedNewDeclarations = _newDeclarations.Values
                .Where(declaration => !_declarationPairs.ContainsValue(declaration.Name))
                .GroupBy(LocalName)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var unmatchedOldDeclarations = _oldDeclarations.Values
                .Where(declaration => !_declarationPairs.ContainsKey(declaration.Name))
                .GroupBy(LocalName);

            foreach (var group in unmatchedOldDeclarations)
            {
                if (group.Count() == 1 && unmatchedNewDeclarations.TryGetValue(group.Key, out var candidates) && candidates.Length == 1)
                {
                    _declarationPairs.Add(group.Single().Name, candidates[0].Name);
                }
            }
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

            var pairedNames = _declarationPairs.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var current in _newDeclarations.Values.Where(declaration => !pairedNames.Contains(declaration.Name)))
            {
                Add(DiagnosticIds.DeclarationAdded, ChangeCategory.Compatible, $"{current.Kind} '{current.Name}' was added", current.Name);
            }

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
                case "struct":
                    CompareStruct(old, current, old.Name, []);
                    break;
                case "enum":
                    CompareEnum(old, current);
                    break;
                case "service":
                    CompareService(old, current);
                    break;
            }
        }

        private void CompareStruct(ContractDeclaration old, ContractDeclaration current, string location, HashSet<string> active)
        {
            if (!Same(old.BaseType, current.BaseType) || NeedsPayloadComparison(old.BaseType, current.BaseType))
            {
                CompareBase(old.BaseType, current.BaseType, location, active);
            }

            var oldFieldsByOrdinal = old.Fields.ToDictionary(f => f.Ordinal);
            var newFieldsByOrdinal = current.Fields.ToDictionary(f => f.Ordinal);
            foreach (var field in old.Fields)
            {
                if (newFieldsByOrdinal.TryGetValue(field.Ordinal, out var next))
                {
                    CompareField(location, field, next, active);
                }
                else
                {
                    Add(DiagnosticIds.FieldRemoved,
                        field.Modifier == "required" ? ChangeCategory.BreakingWire : ChangeCategory.Compatible,
                        $"Field {field.Ordinal} '{field.Name}' ({field.Modifier}) was removed", location + "." + field.Name,
                        field.Modifier == "required"
                            ? "Old readers still require this field. Relax readers before removing it; do not reuse the ordinal."
                            : "Keep the removed field commented out to document its ordinal; do not reuse that ordinal.");
                }
            }

            foreach (var field in current.Fields.Where(f => !oldFieldsByOrdinal.ContainsKey(f.Ordinal)))
            {
                Add(field.Modifier == "required" ? DiagnosticIds.RequiredFieldAdded : DiagnosticIds.OptionalFieldAdded,
                    field.Modifier == "required" ? ChangeCategory.BreakingWire : ChangeCategory.Compatible,
                    $"Field {field.Ordinal} '{field.Name}' ({field.Modifier}) was added", location + "." + field.Name,
                    field.Modifier == "required"
                        ? "Old data omits this field. Start with required_optional (always write, accept absence), update every producer, then require it."
                        : null);
            }

            CompareJsonFields(old, current, location);
        }

        private void CompareJsonFields(ContractDeclaration old, ContractDeclaration current, string location)
        {
            var oldReadersByJsonName = JsonReadersByName(old, _oldDeclarations);
            var newReadersByJsonName = JsonReadersByName(current, _newDeclarations);
            var reportedNames = new HashSet<string>(StringComparer.Ordinal);

            void CompareJsonPair(string name, JsonField previous, JsonField next)
            {
                if (previous.Inherited && next.Inherited
                    || !previous.Inherited && !next.Inherited && previous.Field.Ordinal == next.Field.Ordinal)
                {
                    // Matching binary fields and bases are already checked at their payload or definition.
                    return;
                }

                if (reportedNames.Contains(name))
                {
                    return;
                }

                var fieldLocation = location + "." + previous.Field.Name;
                if (!JsonTypesCompatible(previous.Field.Type, next.Field.Type, fieldLocation, []))
                {
                    reportedNames.Add(name);
                    Add(DiagnosticIds.FieldType, ChangeCategory.BreakingText,
                        $"SimpleJSON field '{name}' changed from {previous.Field.Type} to incompatible type {next.Field.Type}",
                        fieldLocation, "Changing an ordinal does not change the SimpleJSON key. Preserve its payload type or use a new text name.");
                }
            }

            foreach (var (name, oldReader) in oldReadersByJsonName)
            {
                if (newReadersByJsonName.TryGetValue(name, out var newReader))
                {
                    CompareJsonPair(name, oldReader, newReader);
                }
            }

            // Writers still emit shadowed fields, even when a base field wins the reader's name lookup.
            var newFieldsByOrdinal = current.Fields.ToDictionary(field => field.Ordinal);
            foreach (var oldWriter in old.Fields)
            {
                if (newFieldsByOrdinal.TryGetValue(oldWriter.Ordinal, out var newField) && oldWriter.JsonName == newField.JsonName)
                {
                    continue;
                }

                if (newReadersByJsonName.TryGetValue(oldWriter.JsonName, out var newReader))
                {
                    CompareJsonPair(oldWriter.JsonName, new JsonField(oldWriter, Inherited: false), newReader);
                }
            }

            var oldFieldsByOrdinal = old.Fields.ToDictionary(field => field.Ordinal);
            foreach (var newWriter in current.Fields)
            {
                if (oldFieldsByOrdinal.TryGetValue(newWriter.Ordinal, out var oldField) && newWriter.JsonName == oldField.JsonName)
                {
                    continue;
                }

                if (oldReadersByJsonName.TryGetValue(newWriter.JsonName, out var oldReader))
                {
                    CompareJsonPair(newWriter.JsonName, oldReader, new JsonField(newWriter, Inherited: false));
                }
            }
        }

        private readonly record struct JsonField(ContractField Field, bool Inherited);

        private static Dictionary<string, JsonField> JsonReadersByName(ContractDeclaration declaration,
            Dictionary<string, ContractDeclaration> declarations)
        {
            var readersByJsonName = new Dictionary<string, JsonField>(StringComparer.Ordinal);
            foreach (var field in JsonWriterFields(declaration, declarations))
            {
                // SimpleJSON tests later fields first, then base fields before derived fields.
                readersByJsonName[field.Field.JsonName] = field;
            }

            return readersByJsonName;
        }

        private static IEnumerable<JsonField> JsonWriterFields(ContractDeclaration declaration,
            Dictionary<string, ContractDeclaration> declarations)
        {
            var inherited = false;
            while (true)
            {
                foreach (var field in declaration.Fields)
                {
                    yield return new JsonField(field, inherited);
                }

                var baseType = declaration.BaseType;
                if (baseType is null || !declarations.TryGetValue(baseType.Name!, out var baseDeclaration))
                {
                    yield break;
                }

                declaration = Instantiate(baseDeclaration, baseType.Arguments);
                inherited = true;
            }
        }

        private void CompareBase(TypeShape? old, TypeShape? current, string location, HashSet<string> active)
        {
            var start = _changes.Count;
            if (old is null || current is null || !CompareTypes(old, current, location + ".base", active).Compatible)
            {
                Add(DiagnosticIds.BaseType, ChangeCategory.BreakingWire,
                    $"Inheritance layout changed from '{old?.ToString() ?? "none"}' to '{current?.ToString() ?? "none"}'",
                    location, "Preserve the base payload layout and inheritance levels.");
            }

            for (var i = start; i < _changes.Count; i++)
            {
                if (_changes[i].Category == ChangeCategory.BreakingWire
                    && _changes[i].Id is DiagnosticIds.FieldType or DiagnosticIds.RequiredFieldAdded or DiagnosticIds.FieldRemoved)
                {
                    _changes[i] = _changes[i] with
                    {
                        Id = DiagnosticIds.BaseType,
                        Description = "Inheritance layout changed: " + _changes[i].Description
                    };
                }
            }
        }

        private void CompareField(string owner, ContractField old, ContractField current, HashSet<string> active)
        {
            var location = owner + "." + old.Name;
            if (old.JsonName != current.JsonName)
            {
                Add(DiagnosticIds.TextName, ChangeCategory.BreakingText,
                    $"Effective SimpleJSON field name changed from '{old.JsonName}' to '{current.JsonName}'", location,
                    "Pin JsonName to the old text name when renaming the field.");
            }

            CompareFieldModifier(old.Modifier, current.Modifier, location);
            CompareFieldType(old.Type, current.Type, location, active);
            CompareFieldDefault(old, current, location);
        }

        private void CompareFieldModifier(string old, string current, string location)
        {
            if (old == current)
            {
                return;
            }

            string diagnosticId;
            var category = ChangeCategory.BreakingWire;
            var severity = ChangeSeverity.Error;
            if (old == "optional" && current == "required")
            {
                diagnosticId = DiagnosticIds.OptionalToRequired;
            }
            else if (old == "required" && current == "optional")
            {
                diagnosticId = DiagnosticIds.RequiredToOptional;
            }
            else
            {
                diagnosticId = DiagnosticIds.ModifierRollout;
                category = ChangeCategory.Compatible;
                severity = ChangeSeverity.Warning;
            }

            Add(diagnosticId, category,
                $"Modifier changed from {old} to {current}", location,
                ModifierRecommendation(old, current), severity);
        }

        private void CompareFieldType(TypeShape old, TypeShape current, string location, HashSet<string> active)
        {
            var oldType = UnwrapMaybe(old);
            var newType = UnwrapMaybe(current);
            if (oldType.Same(newType) && !NeedsPayloadComparison(oldType, newType))
            {
                return;
            }

            var change = CompareTypes(oldType, newType, location, active);
            if (change.SuppressTypeDiagnostic)
            {
                return;
            }

            var enumSemanticsOnly = change.EnumSemantics && !change.Promotion && change.Compatible;
            var diagnosticId = enumSemanticsOnly ? DiagnosticIds.EnumTypeSemantics : DiagnosticIds.FieldType;
            var category = ChangeCategory.Compatible;
            var severity = ChangeSeverity.Info;
            string? recommendation = null;
            if (!change.Compatible)
            {
                category = ChangeCategory.BreakingWire;
                severity = ChangeSeverity.Error;
                recommendation = "This type change is not wire compatible.";
            }
            else if (change.Promotion)
            {
                severity = ChangeSeverity.Warning;
                recommendation = "Deploy widened consumers before producers; old readers may reject new wider values.";
            }
            else if (enumSemanticsOnly)
            {
                severity = ChangeSeverity.Warning;
                recommendation = "The numeric representation is compatible, but validate enum meanings and accepted values.";
            }

            Add(diagnosticId, category, $"Type changed from {oldType} to {newType}", location, recommendation, severity);

            if (change.EnumSemantics && !enumSemanticsOnly)
            {
                Add(DiagnosticIds.EnumTypeSemantics, ChangeCategory.Compatible,
                    $"Numeric enum interpretation changed from {oldType} to {newType}", location,
                    "The numeric representation is compatible, but validate the meaning and accepted domain of enum values.", ChangeSeverity.Warning);
            }
        }

        private void CompareFieldDefault(ContractField old, ContractField current, string location)
        {
            var nothingChanged = (old.Default.Kind == "nothing") != (current.Default.Kind == "nothing");
            if (nothingChanged)
            {
                Add(DiagnosticIds.NothingDefault, ChangeCategory.BreakingWire,
                    $"Field presence default changed from {Display(old.Default)} to {Display(current.Default)}", location,
                    "nothing represents absence, not Bond nullable<T>'s list encoding. Check absent-value handling in both readers and writers.");
            }
            else if (old.Default != current.Default && old.Default.Kind == current.Default.Kind)
            {
                var changesOmittedValues = old.Modifier == "optional" || current.Modifier == "optional";
                Add(DiagnosticIds.DefaultValue, changesOmittedValues ? ChangeCategory.BreakingWire : ChangeCategory.Compatible,
                    $"Default value changed from {Display(old.Default)} to {Display(current.Default)}", location,
                    changesOmittedValues ? "Omitted fields acquire different values under the two schemas; this changes meaning, not necessarily parsing."
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
                {
                    Add(DiagnosticIds.EnumMemberRemoved, ChangeCategory.Compatible,
                        $"Enum constant '{constant.Name}' was removed", old.Name + "." + constant.Name,
                        "Unknown numeric enum values still decode; check application interpretation.", ChangeSeverity.Warning);
                }
                else if (value != constant.Value)
                {
                    Add(DiagnosticIds.EnumValue, ChangeCategory.BreakingWire,
                        $"Enum constant '{constant.Name}' value changed from {constant.Value} to {value}", old.Name + "." + constant.Name,
                        "Preserve existing numeric meanings. Explicitly number members before inserting or removing implicit constants.");
                }
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
            {
                Add(DiagnosticIds.ServiceBase, ChangeCategory.BreakingWire,
                    $"Inheritance changed from '{old.BaseType?.ToString() ?? "none"}' to '{current.BaseType?.ToString() ?? "none"}'", old.Name);
            }

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
                {
                    Add(DiagnosticIds.MethodSignature, ChangeCategory.BreakingWire,
                        $"Method '{method.Name}' signature changed from {method.Kind} {method.Result} ({method.Input}) to {next.Kind} {next.Result} ({next.Input})",
                        old.Name + "." + method.Name);
                }
            }

            foreach (var method in current.Methods.Where(m => !oldMethods.ContainsKey(m.Name)))
            {
                Add(DiagnosticIds.MethodAdded, ChangeCategory.Compatible, $"Method '{method.Name}' was added", current.Name + "." + method.Name);
            }
        }

        private void Add(string id, ChangeCategory category, string description, string location, string? recommendation = null,
            ChangeSeverity? severity = null)
        {
            _changes.Add(new SchemaChange(category, description, location, recommendation)
            {
                Id = id,
                Severity = severity ?? (category == ChangeCategory.Compatible ? ChangeSeverity.Info : ChangeSeverity.Error)
            });
        }

        private static bool Same(TypeShape? old, TypeShape? current) => old is null ? current is null : old.Same(current);

        private static TypeShape UnwrapMaybe(TypeShape type) => type.Kind == "maybe" ? type.Arguments[0] : type;

        private static string Display(ContractDefault value)
        {
            if (value.Kind == "string")
            {
                return $"\"{value.Value}\"";
            }

            return value.Value.Length == 0 ? value.Kind : value.Value;
        }

        private readonly record struct TypeChange(bool Compatible, bool Promotion = false,
            bool EnumSemantics = false, bool SuppressTypeDiagnostic = false);

        private TypeChange CompareTypes(TypeShape old, TypeShape current, string location, HashSet<string> active)
        {
            if (old.Same(current) && !NeedsPayloadComparison(old, current))
            {
                return new(Compatible: true, SuppressTypeDiagnostic: true);
            }

            // Unbound parameters describe templates, not serialized types. Their concrete uses are checked below.
            if (old.Kind == "parameter" || current.Kind == "parameter")
            {
                return new(Compatible: true, SuppressTypeDiagnostic: true);
            }

            if (old.Kind == "maybe" || current.Kind == "maybe")
            {
                return CompareTypes(UnwrapMaybe(old), UnwrapMaybe(current), location, active);
            }

            if (old.Kind == "bonded" && current.Kind != "bonded")
            {
                return CompareTypes(old.Arguments[0], current, location, active) with
                {
                    SuppressTypeDiagnostic = false
                };
            }

            if (current.Kind == "bonded" && old.Kind != "bonded")
            {
                return CompareTypes(old, current.Arguments[0], location, active) with
                {
                    SuppressTypeDiagnostic = false
                };
            }

            if (old.Kind == "blob" || current.Kind == "blob")
            {
                return CompareTypes(BlobRepresentation(old), BlobRepresentation(current), location, active) with
                {
                    SuppressTypeDiagnostic = false
                };
            }

            if (old.Kind == "enum" || current.Kind == "enum")
            {
                var representation = CompareTypes(old.Kind == "enum" ? TypeShape.Of("int32") : old,
                    current.Kind == "enum" ? TypeShape.Of("int32") : current, location, active);
                return representation with
                {
                    EnumSemantics = representation.Compatible,
                    SuppressTypeDiagnostic = false
                };
            }

            if (IsNumericPromotion(old, current))
            {
                return new(Compatible: true, Promotion: true);
            }

            if (old.Kind == "struct" && current.Kind == "struct"
                && _oldDeclarations.TryGetValue(old.Name!, out var oldTemplate)
                && _newDeclarations.TryGetValue(current.Name!, out var newTemplate)
                && oldTemplate.Kind == "struct" && newTemplate.Kind == "struct")
            {
                if (!NeedsPayloadComparison(old, current))
                {
                    return new(Compatible: true, SuppressTypeDiagnostic: true);
                }

                var key = old + " -> " + current;
                if (active.Contains(key))
                {
                    return new(Compatible: true, SuppressTypeDiagnostic: true);
                }

                if (active.Count >= 64)
                {
                    Add(DiagnosticIds.IncompleteDefinition, ChangeCategory.InvalidSchema,
                        "Expanding generic recursion prevents establishing a finite payload comparison", location);
                    return new(Compatible: true, SuppressTypeDiagnostic: true);
                }

                active.Add(key);
                CompareStruct(Instantiate(oldTemplate, old.Arguments), Instantiate(newTemplate, current.Arguments), location, active);
                active.Remove(key);
                return new(Compatible: true, SuppressTypeDiagnostic: true);
            }

            if (old.Kind is "list" or "vector" && current.Kind is "list" or "vector"
                || old.Kind == current.Kind && old.Kind is "map" or "set" or "nullable" or "bonded" or "stream")
            {
                var children = old.Arguments.Zip(current.Arguments)
                    .Select(pair => CompareTypes(pair.First, pair.Second, location, active)).ToArray();
                return new(
                    Compatible: children.All(c => c.Compatible),
                    Promotion: children.Any(c => c.Promotion),
                    EnumSemantics: children.Any(c => c.EnumSemantics),
                    SuppressTypeDiagnostic: old.Kind == current.Kind && children.All(c => c.SuppressTypeDiagnostic));
            }

            return new(Compatible: false);
        }

        private bool JsonTypesCompatible(TypeShape old, TypeShape current, string location, HashSet<string> active)
        {
            old = JsonRepresentation(old);
            current = JsonRepresentation(current);
            if (old.Same(current) && !NeedsPayloadComparison(old, current)
                || old.Kind == "parameter" || current.Kind == "parameter" || IsNumericPromotion(old, current))
            {
                return true;
            }

            if (old.Kind == "struct" && current.Kind == "struct"
                && _oldDeclarations.TryGetValue(old.Name!, out var oldTemplate)
                && _newDeclarations.TryGetValue(current.Name!, out var newTemplate)
                && oldTemplate.Kind == "struct" && newTemplate.Kind == "struct")
            {
                if (!NeedsPayloadComparison(old, current))
                {
                    return true;
                }

                var key = old + " -> " + current;
                if (active.Contains(key))
                {
                    return true;
                }

                if (active.Count >= 64)
                {
                    Add(DiagnosticIds.IncompleteDefinition, ChangeCategory.InvalidSchema,
                        "Expanding generic recursion prevents establishing a finite SimpleJSON payload comparison", location);
                    return true;
                }

                active.Add(key);
                try
                {
                    var oldPayload = Instantiate(oldTemplate, old.Arguments);
                    var newPayload = Instantiate(newTemplate, current.Arguments);
                    var oldReadersByJsonName = JsonReadersByName(oldPayload, _oldDeclarations);
                    var newReadersByJsonName = JsonReadersByName(newPayload, _newDeclarations);
                    foreach (var oldWriter in JsonWriterFields(oldPayload, _oldDeclarations))
                    {
                        if (newReadersByJsonName.TryGetValue(oldWriter.Field.JsonName, out var newReader))
                        {
                            if (!JsonTypesCompatible(oldWriter.Field.Type, newReader.Field.Type, location, active))
                            {
                                return false;
                            }
                        }
                        else if (oldWriter.Field.Modifier == "required")
                        {
                            return false;
                        }
                    }

                    // Promotion checks stay old-to-new, even for a new writer targeting an old reader.
                    foreach (var newWriter in JsonWriterFields(newPayload, _newDeclarations))
                    {
                        if (oldReadersByJsonName.TryGetValue(newWriter.Field.JsonName, out var oldReader))
                        {
                            if (!JsonTypesCompatible(oldReader.Field.Type, newWriter.Field.Type, location, active))
                            {
                                return false;
                            }
                        }
                        else if (newWriter.Field.Modifier == "required")
                        {
                            return false;
                        }
                    }

                    return true;
                }
                finally
                {
                    active.Remove(key);
                }
            }

            return old.Kind == current.Kind && old.Kind is "list" or "map" or "nullable"
                && old.Arguments.Zip(current.Arguments)
                    .All(pair => JsonTypesCompatible(pair.First, pair.Second, location, active));
        }

        private static TypeShape BlobRepresentation(TypeShape type) =>
            type.Kind == "blob" ? TypeShape.Of("list", TypeShape.Of("int8")) : type;

        private static TypeShape JsonRepresentation(TypeShape type)
        {
            while (type.Kind is "maybe" or "bonded")
            {
                type = type.Arguments[0];
            }

            return type.Kind switch
            {
                "enum" => TypeShape.Of("int32"),
                "wstring" or "meta_name" or "meta_full_name" => TypeShape.Of("string"),
                "vector" or "set" => type with { Kind = "list" },
                _ => BlobRepresentation(type)
            };
        }

        private static bool IsNumericPromotion(TypeShape old, TypeShape current) =>
            Width(old.Kind) > 0 && Width(current.Kind) > Width(old.Kind) && NumericFamily(old.Kind) == NumericFamily(current.Kind);

        private bool NeedsPayloadComparison(TypeShape? old, TypeShape? current)
        {
            if (old is null || current is null)
            {
                return false;
            }

            if (old.Kind == "struct" && current.Kind == "struct")
            {
                if (old.Same(current) && (!_oldDeclarations.ContainsKey(old.Name!) || !_newDeclarations.ContainsKey(current.Name!)))
                {
                    return false;
                }

                if (!_declarationPairs.TryGetValue(old.Name!, out var paired) || paired != current.Name
                    || old.Arguments.Count != current.Arguments.Count
                    || !old.Arguments.Zip(current.Arguments).All(p => p.First.Same(p.Second)))
                {
                    return true;
                }

                return old.Arguments.Count != 0 && HasParameterizedChanges(old.Name!, current.Name!, []);
            }

            return old.Arguments.Count == current.Arguments.Count
                && old.Arguments.Zip(current.Arguments).Any(p => NeedsPayloadComparison(p.First, p.Second));
        }

        private bool HasParameterizedChanges(string oldName, string newName, HashSet<string> visited)
        {
            if (!visited.Add(oldName + " -> " + newName)
                || !_oldDeclarations.TryGetValue(oldName, out var old)
                || !_newDeclarations.TryGetValue(newName, out var current))
            {
                return false;
            }

            bool ParameterizedTypeChanged(TypeShape? oldType, TypeShape? newType)
            {
                if (oldType is null || newType is null)
                {
                    return false;
                }

                if (!oldType.Same(newType) && (ContainsParameter(oldType) || ContainsParameter(newType)))
                {
                    return true;
                }

                if (oldType.Kind == "struct" && newType.Kind == "struct" && ContainsParameter(oldType))
                {
                    return HasParameterizedChanges(oldType.Name!, newType.Name!, visited);
                }

                return oldType.Arguments.Count == newType.Arguments.Count
                    && oldType.Arguments.Zip(newType.Arguments).Any(pair => ParameterizedTypeChanged(pair.First, pair.Second));
            }

            if (ParameterizedTypeChanged(old.BaseType, current.BaseType))
            {
                return true;
            }

            var newFieldsByOrdinal = current.Fields.ToDictionary(field => field.Ordinal);
            if (old.Fields.Any(field => newFieldsByOrdinal.TryGetValue(field.Ordinal, out var next)
                && ParameterizedTypeChanged(field.Type, next.Type)))
            {
                return true;
            }

            var oldReadersByJsonName = JsonReadersByName(old, _oldDeclarations);
            var newReadersByJsonName = JsonReadersByName(current, _newDeclarations);
            foreach (var (name, oldReader) in oldReadersByJsonName)
            {
                if (newReadersByJsonName.TryGetValue(name, out var newReader)
                    && ParameterizedTypeChanged(oldReader.Field.Type, newReader.Field.Type))
                {
                    return true;
                }
            }

            // A new or remapped writer can be hidden by an inherited reader with the same JSON name.
            foreach (var oldWriter in old.Fields)
            {
                if (newFieldsByOrdinal.TryGetValue(oldWriter.Ordinal, out var newField) && oldWriter.JsonName == newField.JsonName)
                {
                    continue;
                }

                if (newReadersByJsonName.TryGetValue(oldWriter.JsonName, out var newReader)
                    && ParameterizedTypeChanged(oldWriter.Type, newReader.Field.Type))
                {
                    return true;
                }
            }

            var oldFieldsByOrdinal = old.Fields.ToDictionary(field => field.Ordinal);
            foreach (var newWriter in current.Fields)
            {
                if (oldFieldsByOrdinal.TryGetValue(newWriter.Ordinal, out var oldField) && newWriter.JsonName == oldField.JsonName)
                {
                    continue;
                }

                if (oldReadersByJsonName.TryGetValue(newWriter.JsonName, out var oldReader)
                    && ParameterizedTypeChanged(oldReader.Field.Type, newWriter.Type))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsParameter(TypeShape type) =>
            type.Kind == "parameter" || type.Arguments.Any(ContainsParameter);

        private static ContractDeclaration Instantiate(ContractDeclaration declaration, IReadOnlyList<TypeShape> arguments)
        {
            return declaration with
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
        }

        private static TypeShape Substitute(TypeShape type, IReadOnlyList<TypeShape> arguments)
        {
            if (type.Kind == "parameter")
            {
                return arguments[(int)type.Integer!.Value];
            }

            return type with
            {
                Arguments = SchemaContractValidation.ReadOnly(type.Arguments.Select(argument => Substitute(argument, arguments)))
            };
        }

        private static int Width(string kind) => kind switch
        {
            "int8" or "uint8" => 8,
            "int16" or "uint16" => 16,
            "int32" or "uint32" or "float" => 32,
            "int64" or "uint64" or "double" => 64,
            _ => 0
        };

        private static string NumericFamily(string kind)
        {
            if (kind.StartsWith("uint", StringComparison.Ordinal))
            {
                return "unsigned";
            }

            if (kind.StartsWith("int", StringComparison.Ordinal))
            {
                return "signed";
            }

            return "floating";
        }

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
