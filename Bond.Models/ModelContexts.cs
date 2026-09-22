using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BondTools.Models;

/// <summary>Graph state shared by one clone operation, scoped to compatible adapter semantics.</summary>
public sealed class CloneContext
{
    private readonly Dictionary<ModelReferenceKey, List<object>> _clones = new();
    private readonly Dictionary<object, Action<object>> _reservations = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(ModelReferenceKey Key, Type View)> _materializing = new();
    private readonly MaterializationCache _payloads = new();

    internal ModelTraversal Traversal { get; } = new();
    internal ModelAdapterFrame? Adapters => Traversal.Adapters;

    /// <summary>Looks up a compatible clone in the active adapter scope, preserving sharing and cycles.</summary>
    public bool TryGetClone<T>(object source, out T clone) =>
        TryGetClone(new ModelReferenceKey(source, Traversal.Semantics), out clone);

    private bool TryGetClone<T>(ModelReferenceKey key, out T clone)
    {
        if (_clones.TryGetValue(key, out var values))
        {
            foreach (var value in values)
            {
                if (value is T compatible)
                {
                    clone = compatible;
                    return true;
                }
            }
        }

        clone = default!;
        return false;
    }

    /// <summary>Registers a clone shell in the active adapter scope before cloning its mutable children.</summary>
    public void Register(object source, object clone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clone);

        var key = new ModelReferenceKey(source, Traversal.Semantics);
        _clones.Add(key, [clone]);
        if (_reservations.Remove(source, out var reserve))
        {
            reserve(clone);
        }
    }

    internal TWrapper CloneMaterialized<TWrapper, TValue>(
        TWrapper wrapper, Func<TWrapper, TValue> read,
        Func<TValue, IModelAdapter<TValue>, TWrapper> wrap, IModelAdapter<TValue> adapter)
        where TWrapper : class
    {
        if (wrapper is null)
        {
            return null!;
        }

        var wrapperKey = new ModelReferenceKey(wrapper, Traversal.Semantics);
        if (TryGetClone<TWrapper>(wrapperKey, out var existing))
        {
            return existing;
        }

        var materializing = (wrapperKey, typeof(TWrapper));
        if (!_materializing.Add(materializing))
        {
            throw new NotSupportedException(
                "A cyclic materialized value requires its typed adapter to call CloneContext.Register before cloning children.");
        }

        object? source = null;
        Action<object>? reservation = null;
        try
        {
            var value = _payloads.Get(wrapper, () => read(wrapper));
            var captured = Adapters is null ? adapter : new CapturedModelAdapter<TValue>(adapter, Adapters);
            source = value;
            if (source is not null)
            {
                // Make the wrapper available as soon as its payload shell exists, before cloning cyclic children.
                reservation = clone =>
                {
                    if (!TryGetClone<TWrapper>(wrapperKey, out _))
                    {
                        RegisterView(wrapperKey, wrapper, wrap((TValue)clone, captured));
                    }
                };

                _reservations[source] = _reservations.GetValueOrDefault(source) + reservation;
            }

            var copied = ModelAdapterSemantics.Clone(adapter, value, this);
            if (TryGetClone<TWrapper>(wrapperKey, out existing))
            {
                return existing;
            }

            var result = wrap(copied, captured);
            RegisterView(wrapperKey, wrapper, result);
            return result;
        }
        finally
        {
            _materializing.Remove(materializing);
            if (source is not null && reservation is not null && _reservations.TryGetValue(source, out var callbacks))
            {
                callbacks -= reservation;
                if (callbacks is null)
                {
                    _reservations.Remove(source);
                }
                else
                {
                    _reservations[source] = callbacks;
                }
            }
        }
    }

    private void RegisterView(ModelReferenceKey key, object source, object clone)
    {
        ArgumentNullException.ThrowIfNull(clone);
        if (_clones.TryGetValue(key, out var views))
        {
            // A covariant base wrapper cannot always represent a more-derived view of the same payload.
            views.Add(clone);
        }
        else
        {
            _clones.Add(key, [clone]);
        }

        if (_reservations.Remove(source, out var reserve))
        {
            reserve(clone);
        }
    }
}

/// <summary>Coinductive equality state. Forks isolate unsuccessful unordered collection matches.</summary>
public sealed class EqualityContext
{
    private HashSet<ReferencePair> _pairs = new(ReferencePairComparer.Instance);
    private readonly MaterializationCache _payloads;

    internal ModelTraversal Traversal { get; private init; } = new();
    internal ModelAdapterFrame? Adapters => Traversal.Adapters;

    /// <summary>Starts an independent equality operation.</summary>
    public EqualityContext() : this(new MaterializationCache())
    {
    }

    private EqualityContext(MaterializationCache payloads) => _payloads = payloads;

    /// <summary>Records a pair in the active adapter scope before recursively comparing children.</summary>
    public bool CompareReferences(object? left, object? right, Func<bool> compare)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        var pair = new ReferencePair(left, right, Traversal.Semantics);
        if (!_pairs.Add(pair))
        {
            return true;
        }

        if (compare())
        {
            return true;
        }

        _pairs.Remove(pair);
        return false;
    }

    /// <summary>Creates an isolated branch for a tentative match.</summary>
    public EqualityContext Fork() => new(_payloads)
    {
        _pairs = new(_pairs, ReferencePairComparer.Instance),
        Traversal = Traversal.Fork()
    };

    /// <summary>Accepts a successful branch's comparisons.</summary>
    public void Accept(EqualityContext branch) => _pairs.UnionWith(branch._pairs);

    internal T Materialize<T>(object wrapper, Func<T> read) => _payloads.Get(wrapper, read);

    private readonly record struct ReferencePair(object Left, object Right, ModelOperationSemantics Semantics);

    private sealed class ReferencePairComparer : IEqualityComparer<ReferencePair>
    {
        internal static readonly ReferencePairComparer Instance = new();

        public bool Equals(ReferencePair x, ReferencePair y) =>
            ReferenceEquals(x.Left, y.Left) && ReferenceEquals(x.Right, y.Right) && x.Semantics.Equals(y.Semantics);

        public int GetHashCode(ReferencePair pair) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(pair.Left), RuntimeHelpers.GetHashCode(pair.Right), pair.Semantics);
    }
}

/// <summary>A bounded structural hash context. Identity is used only to cache materialization, never in the hash.</summary>
public sealed class HashContext
{
    private readonly MaterializationCache _payloads;
    private readonly Dictionary<(ModelReferenceKey Reference, int Depth), int> _hashes;

    internal ModelTraversal Traversal { get; private init; } = new();
    internal ModelAdapterFrame? Adapters => Traversal.Adapters;

    /// <summary>The maximum number of traversed edges in a standard value hash.</summary>
    public const int DefaultDepth = 16;

    /// <summary>The remaining structural depth. Return zero when this reaches zero.</summary>
    public int RemainingDepth { get; }

    /// <summary>Starts an independent hash operation.</summary>
    public HashContext() : this(DefaultDepth, new MaterializationCache(), new())
    {
    }

    private HashContext(int depth, MaterializationCache payloads,
        Dictionary<(ModelReferenceKey Reference, int Depth), int> hashes)
    {
        RemainingDepth = depth;
        _payloads = payloads;
        _hashes = hashes;
    }

    /// <summary>Returns a context for a child edge while sharing materialization state.</summary>
    public HashContext Descend() => new(Math.Max(0, RemainingDepth - 1), _payloads, _hashes)
    {
        Traversal = Traversal.Fork()
    };

    /// <summary>Combines ordered field or element hashes.</summary>
    public static int Combine(int hash, int value) => unchecked(hash * 31 + value);

    internal T Materialize<T>(object wrapper, Func<T> read) => _payloads.Get(wrapper, read);

    internal int HashReference(object source, Func<int> compute)
    {
        var key = (new ModelReferenceKey(source, Traversal.Semantics), RemainingDepth);
        if (_hashes.TryGetValue(key, out var hash))
        {
            return hash;
        }

        hash = compute();
        _hashes.Add(key, hash);
        return hash;
    }
}

internal sealed class MaterializationCache
{
    private readonly Dictionary<object, object?> _values = new(ReferenceEqualityComparer.Instance);

    internal T Get<T>(object wrapper, Func<T> read)
    {
        if (_values.TryGetValue(wrapper, out var value))
        {
            return (T)value!;
        }

        var result = read();
        _values.Add(wrapper, result);
        return result;
    }
}
