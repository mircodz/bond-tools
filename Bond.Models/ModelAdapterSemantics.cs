using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BondTools.Models;

internal interface IModelAdapterSemantics
{
    object? Semantics { get; }
}

internal static class ModelAdapterSemantics
{
    internal static object? For(object adapter) =>
        adapter is IModelAdapterSemantics known ? known.Semantics : new Identity(adapter);

    internal static object? ForBinding(object adapter) => NormalizeBinding(For(adapter));

    // Standard generated paths share one graph scope; custom components form stable structural keys.
    internal static object? Compose(Type kind, object? first, object? second = null) =>
        first is null && second is null ? null : new Composition(kind, first, second);

    internal static object Projection(Type kind, object? payload, bool defaultBinding) =>
        new Composition(kind, new ProjectionKind(kind, defaultBinding), payload);

    private static object? NormalizeBinding(object? semantics) => semantics switch
    {
        ProjectionKind { DefaultBinding: true } => null,
        Composition composition =>
            Compose(composition.Kind, NormalizeBinding(composition.First), NormalizeBinding(composition.Second)),
        _ => semantics
    };

    internal static T Clone<T>(IModelAdapter<T> adapter, T value, CloneContext context)
    {
        using var scope = context.Traversal.Enter(For(adapter));
        return adapter.Clone(value, context);
    }

    internal static bool Equals<T>(IModelAdapter<T> adapter, T left, T right, EqualityContext context)
    {
        using var scope = context.Traversal.Enter(For(adapter));
        return adapter.Equals(left, right, context);
    }

    internal static int Hash<T>(IModelAdapter<T> adapter, T value, HashContext context)
    {
        using var scope = context.Traversal.Enter(For(adapter));
        return adapter.GetHashCode(value, context);
    }

    private readonly record struct Composition(Type Kind, object? First, object? Second);
    private readonly record struct ProjectionKind(Type Kind, bool DefaultBinding);

    private readonly struct Identity(object value) : IEquatable<Identity>
    {
        public bool Equals(Identity other) => ReferenceEquals(value, other.Value);
        public override bool Equals(object? other) => other is Identity identity && Equals(identity);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(value);
        private object Value => value;
    }
}

internal sealed class ModelTraversal
{
    private object? _adapter;

    internal ModelAdapterFrame? Adapters { get; set; }
    internal ModelOperationSemantics Semantics => new(_adapter, Adapters?.Semantics);

    internal ModelTraversal Fork() => new() { _adapter = _adapter, Adapters = Adapters };

    internal Scope Enter(object? adapter)
    {
        var scope = new Scope(this, _adapter);
        _adapter = adapter;
        return scope;
    }

    internal readonly struct Scope(ModelTraversal traversal, object? previous) : IDisposable
    {
        public void Dispose() => traversal._adapter = previous;
    }
}

internal readonly record struct ModelOperationSemantics(object? Adapter, ModelBindingSemantics? Bindings);

internal sealed class ModelBindingSemantics(Dictionary<Type, object> bindings) : IEquatable<ModelBindingSemantics>
{
    internal IReadOnlyDictionary<Type, object> Bindings => bindings;

    public bool Equals(ModelBindingSemantics? other)
    {
        if (other is null || bindings.Count != other.Bindings.Count)
        {
            return false;
        }

        foreach (var binding in bindings)
        {
            if (!other.Bindings.TryGetValue(binding.Key, out var value) || !binding.Value.Equals(value))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? other) => other is ModelBindingSemantics semantics && Equals(semantics);

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var binding in bindings)
        {
            hash = unchecked(hash + HashCode.Combine(binding.Key, binding.Value));
        }

        return hash;
    }
}

internal readonly struct ModelReferenceKey(object source, ModelOperationSemantics semantics) : IEquatable<ModelReferenceKey>
{
    private object Source => source;
    private ModelOperationSemantics Semantics => semantics;

    public bool Equals(ModelReferenceKey other) =>
        ReferenceEquals(source, other.Source) && semantics.Equals(other.Semantics);

    public override bool Equals(object? other) => other is ModelReferenceKey key && Equals(key);
    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(source), semantics);
}
