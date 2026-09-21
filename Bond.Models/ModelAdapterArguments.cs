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
}

internal sealed class ArgumentModelAdapter<T> : IModelAdapter<T>
{
    private readonly IModelAdapter<T> _inner;
    private readonly ModelAdapterArgument[] _arguments;

    internal ArgumentModelAdapter(IModelAdapter<T> inner, ModelAdapterArgument[] arguments)
    {
        _inner = inner;

        // Mask an outer registration for T before dispatching to the underlying adapter.
        _arguments = new[] { ModelAdapterArgument.Create(inner) }.Concat(arguments).ToArray();
    }

    public T Clone(T value, CloneContext context)
    {
        var previous = context.Adapters;
        context.Adapters = new ModelAdapterFrame(_arguments, previous);
        try
        {
            return _inner.Clone(value, context);
        }
        finally
        {
            context.Adapters = previous;
        }
    }

    public bool Equals(T left, T right, EqualityContext context)
    {
        var previous = context.Adapters;
        context.Adapters = new ModelAdapterFrame(_arguments, previous);
        try
        {
            return _inner.Equals(left, right, context);
        }
        finally
        {
            context.Adapters = previous;
        }
    }

    public int GetHashCode(T value, HashContext context)
    {
        var previous = context.Adapters;
        context.Adapters = new ModelAdapterFrame(_arguments, previous);
        try
        {
            return _inner.GetHashCode(value, context);
        }
        finally
        {
            context.Adapters = previous;
        }
    }
}
