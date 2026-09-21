using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BondTools.Models;

/// <summary>Identity-based graph state shared by one clone operation.</summary>
public sealed class CloneContext
{
    private readonly Dictionary<object, object> _clones = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Action<object>> _reservations = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _materializing = new(ReferenceEqualityComparer.Instance);
    private readonly MaterializationCache _payloads = new();

    internal ModelAdapterFrame? Adapters { get; set; }

    /// <summary>Looks up a previously allocated clone, preserving sharing and cycles.</summary>
    public bool TryGetClone<T>(object source, out T clone)
    {
        if (_clones.TryGetValue(source, out var value))
        {
            clone = (T)value;
            return true;
        }

        clone = default!;
        return false;
    }

    /// <summary>Registers a clone shell before any of its mutable children are cloned.</summary>
    public void Register(object source, object clone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clone);

        _clones.Add(source, clone);
        if (_reservations.Remove(source, out var reserve))
        {
            reserve(clone);
        }
    }

    internal TWrapper CloneMaterialized<TWrapper, TValue>(
        TWrapper wrapper, Func<TWrapper, TValue> read, Func<TValue, TWrapper> wrap, IModelAdapter<TValue> adapter)
        where TWrapper : class
    {
        if (wrapper is null)
        {
            return null!;
        }

        if (TryGetClone<TWrapper>(wrapper, out var existing))
        {
            return existing;
        }

        if (!_materializing.Add(wrapper))
        {
            throw new NotSupportedException(
                "A cyclic materialized value requires its typed adapter to call CloneContext.Register before cloning children.");
        }

        object? source = null;
        Action<object>? reservation = null;
        try
        {
            var value = _payloads.Get(wrapper, () => read(wrapper));
            source = value;
            if (source is not null)
            {
                // Make the wrapper available as soon as its payload shell exists, before cloning cyclic children.
                reservation = clone =>
                {
                    if (!TryGetClone<TWrapper>(wrapper, out _))
                    {
                        Register(wrapper, wrap((TValue)clone));
                    }
                };

                if (_clones.TryGetValue(source, out var clone))
                {
                    reservation(clone);
                }
                else
                {
                    _reservations[source] = _reservations.GetValueOrDefault(source) + reservation;
                }
            }

            var copied = adapter.Clone(value, this);
            if (TryGetClone<TWrapper>(wrapper, out existing))
            {
                return existing;
            }

            var result = wrap(copied);
            Register(wrapper, result);
            return result;
        }
        finally
        {
            _materializing.Remove(wrapper);
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
}

/// <summary>Coinductive equality state. Forks isolate unsuccessful unordered collection matches.</summary>
public sealed class EqualityContext
{
    private HashSet<ReferencePair> _pairs = new(ReferencePairComparer.Instance);
    private readonly MaterializationCache _payloads;

    internal ModelAdapterFrame? Adapters { get; set; }

    /// <summary>Starts an independent equality operation.</summary>
    public EqualityContext() : this(new MaterializationCache())
    {
    }

    private EqualityContext(MaterializationCache payloads) => _payloads = payloads;

    /// <summary>Compares reference values, recording a pair before recursively comparing children.</summary>
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

        var pair = new ReferencePair(left, right);
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
        Adapters = Adapters
    };

    /// <summary>Accepts a successful branch's comparisons.</summary>
    public void Accept(EqualityContext branch) => _pairs.UnionWith(branch._pairs);

    internal T Materialize<T>(object wrapper, Func<T> read) => _payloads.Get(wrapper, read);

    private readonly record struct ReferencePair(object Left, object Right);

    private sealed class ReferencePairComparer : IEqualityComparer<ReferencePair>
    {
        internal static readonly ReferencePairComparer Instance = new();

        public bool Equals(ReferencePair x, ReferencePair y) =>
            ReferenceEquals(x.Left, y.Left) && ReferenceEquals(x.Right, y.Right);

        public int GetHashCode(ReferencePair pair) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(pair.Left), RuntimeHelpers.GetHashCode(pair.Right));
    }
}

/// <summary>A bounded structural hash context. Identity is used only to cache materialization, never in the hash.</summary>
public sealed class HashContext
{
    private readonly MaterializationCache _payloads;
    private readonly Dictionary<object, Dictionary<int, int>> _hashes;

    internal ModelAdapterFrame? Adapters { get; set; }

    /// <summary>The maximum number of traversed edges in a standard value hash.</summary>
    public const int DefaultDepth = 16;

    /// <summary>The remaining structural depth. Return zero when this reaches zero.</summary>
    public int RemainingDepth { get; }

    /// <summary>Starts an independent hash operation.</summary>
    public HashContext() : this(DefaultDepth, new MaterializationCache(), new(ReferenceEqualityComparer.Instance))
    {
    }

    private HashContext(int depth, MaterializationCache payloads, Dictionary<object, Dictionary<int, int>> hashes)
    {
        RemainingDepth = depth;
        _payloads = payloads;
        _hashes = hashes;
    }

    /// <summary>Returns a context for a child edge while sharing materialization state.</summary>
    public HashContext Descend() => new(Math.Max(0, RemainingDepth - 1), _payloads, _hashes)
    {
        Adapters = Adapters
    };

    /// <summary>Combines ordered field or element hashes.</summary>
    public static int Combine(int hash, int value) => unchecked(hash * 31 + value);

    internal T Materialize<T>(object wrapper, Func<T> read) => _payloads.Get(wrapper, read);

    internal int HashReference(object source, Func<int> compute)
    {
        if (!_hashes.TryGetValue(source, out var depths))
        {
            depths = new Dictionary<int, int>();
            _hashes.Add(source, depths);
        }

        if (depths.TryGetValue(RemainingDepth, out var hash))
        {
            return hash;
        }

        hash = compute();
        depths.Add(RemainingDepth, hash);
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
