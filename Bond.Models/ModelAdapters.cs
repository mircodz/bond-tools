using System;
using System.Collections.Generic;
using System.Linq;

namespace BondTools.Models;

/// <summary>Typed container combinators used by generated code; no runtime type discovery is performed.</summary>
public static class ModelAdapters
{
    /// <summary>Dispatches to generated operations, known immutable values, or an explicitly registered adapter.</summary>
    public static IModelAdapter<T> Value<T>() => ValueAdapter<T>.Instance;

    /// <summary>Uses .NET equality and identity cloning for a type explicitly known to be immutable.</summary>
    public static IModelAdapter<T> Immutable<T>() => ImmutableAdapter<T>.Instance;

    /// <summary>Supplies statically known generic argument operations for one model traversal.</summary>
    public static IModelAdapter<T> WithArguments<T>(IModelAdapter<T> value, params ModelAdapterArgument[] arguments)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(arguments);
        return new ArgumentModelAdapter<T>(value, arguments);
    }

    /// <summary>Preserves nullable value semantics.</summary>
    public static IModelAdapter<T?> Nullable<T>(IModelAdapter<T> element) where T : struct =>
        new NullableAdapter<T>(element);

    /// <summary>Clones a vector and compares its elements in order.</summary>
    public static IModelAdapter<List<T>> List<T>(IModelAdapter<T> element) =>
        new SequenceAdapter<List<T>, T>(element, value => new(value.Count), (list, item) => list.Add(item), unordered: false);

    /// <summary>Clones a linked list and compares its elements in order.</summary>
    public static IModelAdapter<LinkedList<T>> LinkedList<T>(IModelAdapter<T> element) =>
        new SequenceAdapter<LinkedList<T>, T>(element, _ => new(), (list, item) => list.AddLast(item), unordered: false);

    /// <summary>Clones a set, preserving its comparer; structural equality and hashing ignore insertion order.</summary>
    public static IModelAdapter<HashSet<T>> Set<T>(IModelAdapter<T> element) =>
        new SequenceAdapter<HashSet<T>, T>(element, value => new(value.Comparer), (set, item) => set.Add(item), unordered: true);

    /// <summary>Clones a map, preserving its comparer; structural equality and hashing ignore insertion order.</summary>
    public static IModelAdapter<Dictionary<TKey, TValue>> Map<TKey, TValue>(
        IModelAdapter<TKey> key, IModelAdapter<TValue> value) where TKey : notnull =>
        new MapAdapter<TKey, TValue>(key, value);

    /// <summary>Copies owned backing storage, preserving shared buffers and segment offsets.</summary>
    public static IModelAdapter<ArraySegment<byte>> Blob { get; } = new BlobAdapter();

    /// <summary>Copies byte-array storage and compares byte contents.</summary>
    public static IModelAdapter<byte[]> ByteArray { get; } = new ByteArrayAdapter();

    /// <summary>
    /// Compares and clones actual lazy payload values. Reading a payload crosses into the original runtime,
    /// which may use reflection or deserialize. Each wrapper is materialized at most once per operation.
    /// Covariant views share cloned payloads but may receive separate typed wrappers.
    /// </summary>
    public static IModelAdapter<TWrapper> Materialized<TWrapper, TValue>(
        Func<TWrapper, TValue> read, Func<TValue, TWrapper> wrap, IModelAdapter<TValue> value)
        where TWrapper : class =>
            new MaterializedAdapter<TWrapper, TValue>(read, (payload, _) => wrap(payload), value, defaultBinding: false);

    /// <summary>
    /// Clones lazy payloads and supplies their captured typed operations to the wrapper factory.
    /// The supplied adapter retains argument bindings, not graph caches, and can be used with fresh contexts.
    /// The factory must retain the supplied payload and its value semantics, as generated bonded wrappers do.
    /// </summary>
    public static IModelAdapter<TWrapper> Materialized<TWrapper, TValue>(
        Func<TWrapper, TValue> read, Func<TValue, IModelAdapter<TValue>, TWrapper> wrap, IModelAdapter<TValue> value)
        where TWrapper : class =>
            new MaterializedAdapter<TWrapper, TValue>(read, wrap, value, defaultBinding: true);

    internal static object? EffectiveSemantics<T>(IModelAdapter<T> adapter, ModelAdapterFrame? arguments)
    {
        if (adapter is ValueAdapter<T> dispatcher)
        {
            var selected = dispatcher.ResolveOverride(arguments);
            return selected is null ? null : ModelAdapterSemantics.For(selected);
        }

        return ModelAdapterSemantics.For(adapter);
    }

    private sealed class ValueAdapter<T> : IModelAdapter<T>, IModelAdapterSemantics
    {
        internal static readonly ValueAdapter<T> Instance = new();
        public object? Semantics => null;

        internal IModelAdapter<T>? ResolveOverride(ModelAdapterFrame? bindings)
        {
            if (ModelOperations.Registered<T>() is { } registered)
            {
                return registered;
            }

            var contextual = bindings?.Find<T>();
            return ReferenceEquals(contextual, this) ? null : contextual;
        }

        public T Clone(T value, CloneContext context)
        {
            using var scope = context.Traversal.Enter(null);
            if (value is null)
            {
                return value;
            }

            if (ResolveOverride(context.Adapters) is { } adapter)
            {
                return ModelAdapterSemantics.Clone(adapter, value, context);
            }

            if (value is IGeneratedCloneable model)
            {
                return (T)model.Clone(context);
            }

            if (value is byte[] bytes)
            {
                return (T)(object)ByteArray.Clone(bytes, context);
            }

            if (value is ArraySegment<byte> blob)
            {
                return (T)(object)Blob.Clone(blob, context);
            }

            if (ModelOperations.IsKnownImmutable(value))
            {
                return value;
            }

            throw ModelOperations.MissingAdapter(typeof(T));
        }

        public bool Equals(T left, T right, EqualityContext context)
        {
            using var scope = context.Traversal.Enter(null);
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null)
            {
                return false;
            }

            if (ResolveOverride(context.Adapters) is { } adapter)
            {
                return ModelAdapterSemantics.Equals(adapter, left, right, context);
            }

            if (left is IGeneratedEquatable model)
            {
                return right is IGeneratedEquatable other && left.GetType() == right.GetType() &&
                    context.CompareReferences(left, right, () => model.ValueEquals(other, context));
            }

            if (left is byte[] bytes)
            {
                return right is byte[] otherBytes && ByteArray.Equals(bytes, otherBytes, context);
            }

            if (left is ArraySegment<byte> blob)
            {
                return right is ArraySegment<byte> otherBlob && Blob.Equals(blob, otherBlob, context);
            }

            if (ModelOperations.IsKnownImmutable(left))
            {
                return EqualityComparer<T>.Default.Equals(left, right);
            }

            throw ModelOperations.MissingAdapter(typeof(T));
        }

        public int GetHashCode(T value, HashContext context)
        {
            using var scope = context.Traversal.Enter(null);
            if (value is null || context.RemainingDepth == 0)
            {
                return 0;
            }

            if (ResolveOverride(context.Adapters) is { } adapter)
            {
                return ModelAdapterSemantics.Hash(adapter, value, context);
            }

            if (value is IGeneratedEquatable model)
            {
                return context.HashReference(model, () => model.ValueHashCode(context));
            }

            if (value is byte[] bytes)
            {
                return ByteArray.GetHashCode(bytes, context);
            }

            if (value is ArraySegment<byte> blob)
            {
                return Blob.GetHashCode(blob, context);
            }

            if (ModelOperations.IsKnownImmutable(value))
            {
                return EqualityComparer<T>.Default.GetHashCode(value);
            }

            throw ModelOperations.MissingAdapter(typeof(T));
        }
    }

    private sealed class ImmutableAdapter<T> : IModelAdapter<T>, IModelAdapterSemantics
    {
        internal static readonly ImmutableAdapter<T> Instance = new();
        public object Semantics => typeof(ImmutableAdapter<T>);

        public T Clone(T value, CloneContext context) => value;

        public bool Equals(T left, T right, EqualityContext context) => EqualityComparer<T>.Default.Equals(left, right);

        public int GetHashCode(T value, HashContext context) =>
            value is null || context.RemainingDepth == 0 ? 0 : EqualityComparer<T>.Default.GetHashCode(value);
    }

    private sealed class NullableAdapter<T>(IModelAdapter<T> element) : IModelAdapter<T?>, IModelAdapterSemantics
        where T : struct
    {
        public object? Semantics { get; } =
            ModelAdapterSemantics.Compose(typeof(NullableAdapter<T>), ModelAdapterSemantics.For(element));

        public T? Clone(T? value, CloneContext context) =>
            value.HasValue ? ModelAdapterSemantics.Clone(element, value.Value, context) : null;

        public bool Equals(T? left, T? right, EqualityContext context) =>
            left.HasValue == right.HasValue &&
            (!left.HasValue || ModelAdapterSemantics.Equals(element, left.Value, right!.Value, context));

        public int GetHashCode(T? value, HashContext context) =>
            !value.HasValue || context.RemainingDepth == 0 ? 0 : ModelAdapterSemantics.Hash(element, value.Value, context);
    }

    private sealed class SequenceAdapter<TCollection, T>(
        IModelAdapter<T> element,
        Func<TCollection, TCollection> create,
        Action<TCollection, T> add,
        bool unordered) : IModelAdapter<TCollection>, IModelAdapterSemantics where TCollection : class, ICollection<T>
    {
        public object? Semantics { get; } =
            ModelAdapterSemantics.Compose(typeof(SequenceAdapter<TCollection, T>), ModelAdapterSemantics.For(element));

        public TCollection Clone(TCollection value, CloneContext context)
        {
            using var scope = context.Traversal.Enter(Semantics);
            if (value is null)
            {
                return null!;
            }

            if (context.TryGetClone<TCollection>(value, out var existing))
            {
                return existing;
            }

            var clone = create(value);
            context.Register(value, clone);
            foreach (var item in value)
            {
                add(clone, ModelAdapterSemantics.Clone(element, item, context));
            }

            return clone;
        }

        public bool Equals(TCollection left, TCollection right, EqualityContext context)
        {
            using var scope = context.Traversal.Enter(Semantics);
            return context.CompareReferences(left, right, () =>
            {
                if (left.Count != right.Count)
                {
                    return false;
                }

                if (unordered)
                {
                    return UnorderedEquals(left, right, context,
                        (a, b, branch) => ModelAdapterSemantics.Equals(element, a, b, branch));
                }

                using var leftItems = left.GetEnumerator();
                using var rightItems = right.GetEnumerator();
                while (leftItems.MoveNext())
                {
                    if (!rightItems.MoveNext() ||
                        !ModelAdapterSemantics.Equals(element, leftItems.Current, rightItems.Current, context))
                    {
                        return false;
                    }
                }

                return !rightItems.MoveNext();
            });
        }

        public int GetHashCode(TCollection value, HashContext context)
        {
            using var scope = context.Traversal.Enter(Semantics);
            if (value is null || context.RemainingDepth == 0)
            {
                return 0;
            }

            return context.HashReference(value, () =>
            {
                var hash = 17;
                var child = context.Descend();
                foreach (var item in value)
                {
                    var itemHash = ModelAdapterSemantics.Hash(element, item, child);
                    hash = unordered ? unchecked(hash + itemHash) : HashContext.Combine(hash, itemHash);
                }

                return HashContext.Combine(hash, value.Count);
            });
        }
    }

    private sealed class MapAdapter<TKey, TValue>(IModelAdapter<TKey> key, IModelAdapter<TValue> value)
        : IModelAdapter<Dictionary<TKey, TValue>>, IModelAdapterSemantics where TKey : notnull
    {
        public object? Semantics { get; } = ModelAdapterSemantics.Compose(typeof(MapAdapter<TKey, TValue>),
            ModelAdapterSemantics.For(key), ModelAdapterSemantics.For(value));

        public Dictionary<TKey, TValue> Clone(Dictionary<TKey, TValue> source, CloneContext context)
        {
            using var scope = context.Traversal.Enter(Semantics);
            if (source is null)
            {
                return null!;
            }

            if (context.TryGetClone<Dictionary<TKey, TValue>>(source, out var existing))
            {
                return existing;
            }

            var clone = new Dictionary<TKey, TValue>(source.Count, source.Comparer);
            context.Register(source, clone);
            foreach (var item in source)
            {
                clone.Add(ModelAdapterSemantics.Clone(key, item.Key, context),
                    ModelAdapterSemantics.Clone(value, item.Value, context));
            }

            return clone;
        }

        public bool Equals(Dictionary<TKey, TValue> left, Dictionary<TKey, TValue> right, EqualityContext context)
        {
            using var scope = context.Traversal.Enter(Semantics);
            return context.CompareReferences(left, right, () =>
            {
                if (left.Count != right.Count)
                {
                    return false;
                }

                return UnorderedEquals(left, right, context, (leftEntry, rightEntry, branch) =>
                    ModelAdapterSemantics.Equals(key, leftEntry.Key, rightEntry.Key, branch) &&
                    ModelAdapterSemantics.Equals(value, leftEntry.Value, rightEntry.Value, branch));
            });
        }

        public int GetHashCode(Dictionary<TKey, TValue> source, HashContext context)
        {
            using var scope = context.Traversal.Enter(Semantics);
            if (source is null || context.RemainingDepth == 0)
            {
                return 0;
            }

            return context.HashReference(source, () =>
            {
                var hash = 17;
                var child = context.Descend();
                foreach (var item in source)
                {
                    var entryHash = HashContext.Combine(
                        ModelAdapterSemantics.Hash(key, item.Key, child), ModelAdapterSemantics.Hash(value, item.Value, child));
                    hash = unchecked(hash + entryHash);
                }

                return HashContext.Combine(hash, source.Count);
            });
        }
    }

    private sealed class ByteArrayAdapter : IModelAdapter<byte[]>, IModelAdapterSemantics
    {
        public object? Semantics => null;

        public byte[] Clone(byte[] value, CloneContext context)
        {
            using var scope = context.Traversal.Enter(null);
            if (value is null)
            {
                return null!;
            }

            if (context.TryGetClone<byte[]>(value, out var clone))
            {
                return clone;
            }

            clone = (byte[])value.Clone();
            context.Register(value, clone);
            return clone;
        }

        public bool Equals(byte[] left, byte[] right, EqualityContext context) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(byte[] value, HashContext context) =>
            value is null ? 0 : Blob.GetHashCode(new ArraySegment<byte>(value), context);
    }

    private sealed class BlobAdapter : IModelAdapter<ArraySegment<byte>>, IModelAdapterSemantics
    {
        public object? Semantics => null;

        public ArraySegment<byte> Clone(ArraySegment<byte> value, CloneContext context)
        {
            if (value.Array is null)
            {
                return default;
            }

            var copy = ByteArray.Clone(value.Array, context);
            return new(copy, value.Offset, value.Count);
        }

        public bool Equals(ArraySegment<byte> left, ArraySegment<byte> right, EqualityContext context) =>
            (left.Array is null) == (right.Array is null) && left.AsSpan().SequenceEqual(right.AsSpan());

        public int GetHashCode(ArraySegment<byte> value, HashContext context)
        {
            if (value.Array is null || context.RemainingDepth == 0)
            {
                return 0;
            }

            var hash = 17;
            foreach (var item in value.AsSpan())
            {
                hash = HashContext.Combine(hash, item);
            }

            return hash;
        }
    }

    private sealed class MaterializedAdapter<TWrapper, TValue>(
        Func<TWrapper, TValue> read, Func<TValue, IModelAdapter<TValue>, TWrapper> wrap,
        IModelAdapter<TValue> value, bool defaultBinding)
        : IModelAdapter<TWrapper>, IModelAdapterSemantics where TWrapper : class
    {
        public object Semantics { get; } =
            ModelAdapterSemantics.Projection(
                typeof(MaterializedAdapter<TWrapper, TValue>), ModelAdapterSemantics.For(value), defaultBinding);

        public TWrapper Clone(TWrapper source, CloneContext context)
        {
            using var scope = context.Traversal.Enter(SemanticsFor(context.Adapters));
            return context.CloneMaterialized(source, read, wrap, value);
        }

        public bool Equals(TWrapper left, TWrapper right, EqualityContext context)
        {
            using var scope = context.Traversal.Enter(SemanticsFor(context.Adapters));
            return context.CompareReferences(left, right, () =>
            {
                var leftPayload = context.Materialize(left, () => read(left));
                var rightPayload = context.Materialize(right, () => read(right));
                return ModelAdapterSemantics.Equals(value, leftPayload, rightPayload, context);
            });
        }

        public int GetHashCode(TWrapper source, HashContext context)
        {
            using var scope = context.Traversal.Enter(SemanticsFor(context.Adapters));
            if (source is null || context.RemainingDepth == 0)
            {
                return 0;
            }

            return context.HashReference(source, () =>
            {
                var payload = context.Materialize(source, () => read(source));
                return ModelAdapterSemantics.Hash(value, payload, context.Descend());
            });
        }

        private object SemanticsFor(ModelAdapterFrame? arguments) =>
            ModelAdapterSemantics.Projection(
                typeof(MaterializedAdapter<TWrapper, TValue>), EffectiveSemantics(value, arguments), defaultBinding);
    }

    private static bool UnorderedEquals<T>(
        IEnumerable<T> left, IEnumerable<T> right, EqualityContext context,
        Func<T, T, EqualityContext, bool> equals)
    {
        var candidates = right.ToArray();
        var matched = new bool[candidates.Length];
        foreach (var item in left)
        {
            var found = false;
            for (var i = 0; i < candidates.Length; i++)
            {
                if (matched[i])
                {
                    continue;
                }

                var branch = context.Fork();
                if (!equals(item, candidates[i], branch))
                {
                    continue;
                }

                matched[i] = true;
                context.Accept(branch);
                found = true;
                break;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }
}
