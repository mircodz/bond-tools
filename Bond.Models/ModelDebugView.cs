using System;
using System.Diagnostics;

namespace BondTools.Models;

/// <summary>An immediate field snapshot. Its debugger label never evaluates the value.</summary>
[DebuggerDisplay("{Name,nq} (id {Id})")]
public sealed class ModelDebugField(string declaringType, string name, ushort id, object? value)
{
    /// <summary>The declaring schema type, disambiguating inherited hidden fields.</summary>
    public string DeclaringType { get; } = declaringType;
    /// <summary>The schema field name.</summary>
    public string Name { get; } = name;
    /// <summary>The wire field identifier.</summary>
    public ushort Id { get; } = id;
    /// <summary>The immediate value, without enumerating, formatting, cloning, or deserializing it.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    public object? Value { get; } = value;
}

/// <summary>A reflection-free debugger proxy that captures only immediate generated field getters.</summary>
public sealed class GeneratedModelDebugView
{
    /// <summary>Captures immediate fields; does not traverse collections or materialize bonded values.</summary>
    public GeneratedModelDebugView(object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Fields = ((IGeneratedDebugView)model).GetDebugFields();
    }

    /// <summary>The immediate field snapshots, including inherited fields.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ModelDebugField[] Fields { get; }
}
