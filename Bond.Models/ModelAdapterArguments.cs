using System;
using System.Collections.Generic;
using System.Linq;

namespace BondTools.Models;

/// <summary>Statically supplied operations for a closed generic model argument.</summary>
public sealed class ModelAdapterArgument
{
    private ModelAdapterArgument(Type type, object adapter)
    {
        Type = type;
        Adapter = adapter;
    }

    internal Type Type { get; }
    internal object Adapter { get; }

    /// <summary>Supplies an argument's operations without inspecting its CLR members.</summary>
    public static ModelAdapterArgument Create<T>(IModelAdapter<T> adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new(typeof(T), adapter);
    }
}

internal sealed class ModelAdapterFrame(IReadOnlyList<ModelAdapterArgument> arguments, ModelAdapterFrame? parent)
{
    internal ModelBindingSemantics? Semantics { get; } = GetSemantics(arguments, parent);

    internal IModelAdapter<T>? Find<T>()
    {
        foreach (var argument in arguments)
        {
            if (argument.Type == typeof(T))
            {
                return (IModelAdapter<T>)argument.Adapter;
            }
        }

        return parent?.Find<T>();
    }

    private static ModelBindingSemantics? GetSemantics(
        IReadOnlyList<ModelAdapterArgument> arguments, ModelAdapterFrame? parent)
    {
        var bindings = parent?.Semantics is { } inherited
            ? new Dictionary<Type, object>(inherited.Bindings)
            : new Dictionary<Type, object>();
        var seen = new HashSet<Type>();
        foreach (var argument in arguments)
        {
            if (!seen.Add(argument.Type))
            {
                continue;
            }

            // Standard generated bindings do not change semantics; masks also remove outer overrides.
            if (ModelAdapterSemantics.ForBinding(argument.Adapter) is { } semantics)
            {
                bindings[argument.Type] = semantics;
            }
            else
            {
                bindings.Remove(argument.Type);
            }
        }

        return bindings.Count == 0 ? null : new ModelBindingSemantics(bindings);
    }
}

internal sealed class ArgumentModelAdapter<T> : IModelAdapter<T>, IModelAdapterSemantics
{
    private readonly IModelAdapter<T> _inner;
    private readonly ModelAdapterArgument[] _arguments;

    public object? Semantics { get; }

    internal ArgumentModelAdapter(IModelAdapter<T> inner, ModelAdapterArgument[] arguments)
    {
        _inner = inner;

        // Mask an outer registration for T before dispatching to the underlying adapter.
        _arguments = new[] { ModelAdapterArgument.Create(inner) }.Concat(arguments).ToArray();
        Semantics = ModelAdapterSemantics.Compose(typeof(ArgumentModelAdapter<T>),
            ModelAdapterSemantics.For(inner), new ModelAdapterFrame(_arguments, null).Semantics);
    }

    public T Clone(T value, CloneContext context)
    {
        using var scope = context.Traversal.PushBindings(_arguments);
        return ModelAdapterSemantics.Clone(_inner, value, context);
    }

    public bool Equals(T left, T right, EqualityContext context)
    {
        using var scope = context.Traversal.PushBindings(_arguments);
        return ModelAdapterSemantics.Equals(_inner, left, right, context);
    }

    public int GetHashCode(T value, HashContext context)
    {
        using var scope = context.Traversal.PushBindings(_arguments);
        return ModelAdapterSemantics.Hash(_inner, value, context);
    }
}

internal sealed class CapturedModelAdapter<T>(IModelAdapter<T> inner, ModelAdapterFrame bindings)
    : IModelAdapter<T>, IModelAdapterSemantics
{
    public object? Semantics { get; } =
        ModelAdapterSemantics.Compose(typeof(CapturedModelAdapter<T>), ModelAdapterSemantics.For(inner), bindings.Semantics);

    public T Clone(T value, CloneContext context)
    {
        using var scope = context.Traversal.ReplaceBindings(bindings);
        return ModelAdapterSemantics.Clone(inner, value, context);
    }

    public bool Equals(T left, T right, EqualityContext context)
    {
        using var scope = context.Traversal.ReplaceBindings(bindings);
        return ModelAdapterSemantics.Equals(inner, left, right, context);
    }

    public int GetHashCode(T value, HashContext context)
    {
        using var scope = context.Traversal.ReplaceBindings(bindings);
        return ModelAdapterSemantics.Hash(inner, value, context);
    }
}
