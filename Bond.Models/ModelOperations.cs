using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BondTools.Models;

/// <summary>Static, generated operations. This contract is independent of the Bond serialization runtime.</summary>
public interface IGeneratedSchemaProvider
{
    /// <summary>The model's reflection-free schema.</summary>
    SchemaDescriptor Descriptor { get; }
}

/// <summary>A model with generated graph-cloning support.</summary>
public interface IGeneratedCloneable
{
    /// <summary>Clones the model, registering its new instance before cloning fields.</summary>
    IGeneratedCloneable Clone(CloneContext context);
}

/// <summary>A model with generated structural equality and hashing.</summary>
public interface IGeneratedEquatable
{
    /// <summary>Compares fields. Call through <see cref="ModelOperations"/> to handle cycles and runtime types.</summary>
    bool ValueEquals(IGeneratedEquatable other, EqualityContext context);

    /// <summary>Hashes fields using the supplied depth budget.</summary>
    int ValueHashCode(HashContext context);
}

/// <summary>A model with generated immediate-field debugger support.</summary>
public interface IGeneratedDebugView
{
    /// <summary>Captures immediate fields without evaluating their contents.</summary>
    ModelDebugField[] GetDebugFields();
}

/// <summary>A model with descriptors, cloning, equality, and debugger support.</summary>
public interface IGeneratedModel : IGeneratedSchemaProvider, IGeneratedCloneable, IGeneratedEquatable, IGeneratedDebugView
{
}

/// <summary>Typed operations for external CLR values. Mutable adapters must register clones before recursing.</summary>
public interface IModelAdapter<T>
{
    /// <summary>Deep-clones a value using the shared graph context.</summary>
    T Clone(T value, CloneContext context);

    /// <summary>Compares actual values using the shared graph context.</summary>
    bool Equals(T left, T right, EqualityContext context);

    /// <summary>Hashes actual values, respecting the context's remaining depth.</summary>
    int GetHashCode(T value, HashContext context);
}

/// <summary>
/// A value already materialized by generated operations. Generated bonded adapters use this contract to
/// avoid restarting deserialization or graph cloning when revisiting a cloned cyclic payload.
/// </summary>
public interface IMaterializedModelValue<out T>
{
    /// <summary>The captured value. Reading it performs no serialization, cloning, or traversal.</summary>
    T Value { get; }
}

/// <summary>
/// A delegate-based adapter with its own graph-cache scope. Reuse an instance for recursive operations.
/// Equality must be an equivalence relation and agree with hashing.
/// </summary>
public sealed class ModelAdapter<T>(
    Func<T, CloneContext, T> clone,
    Func<T, T, EqualityContext, bool> equals,
    Func<T, HashContext, int> hash) : IModelAdapter<T>
{
    /// <inheritdoc />
    public T Clone(T value, CloneContext context)
    {
        using var scope = context.Traversal.Enter(ModelAdapterSemantics.For(this));
        return clone(value, context);
    }

    /// <inheritdoc />
    public bool Equals(T left, T right, EqualityContext context)
    {
        using var scope = context.Traversal.Enter(ModelAdapterSemantics.For(this));
        return equals(left, right, context);
    }

    /// <inheritdoc />
    public int GetHashCode(T value, HashContext context)
    {
        using var scope = context.Traversal.Enter(ModelAdapterSemantics.For(this));
        return hash(value, context);
    }
}

/// <summary>Reflection-free value operations for generated models and explicitly adapted CLR types.</summary>
public static class ModelOperations
{
    /// <summary>Deep-clones without invoking generated model constructors. Bonded fields may deserialize.</summary>
    public static T Clone<T>(T value) => ModelAdapters.Value<T>().Clone(value, new CloneContext());

    /// <summary>Structural equality; sharing is ignored and cycles are compared coinductively.</summary>
    public static bool ValueEquals<T>(T left, T right) =>
        ModelAdapters.Value<T>().Equals(left, right, new EqualityContext());

    /// <summary>
    /// Structural hash truncated at 16 edges, independent of object identity and collection insertion order.
    /// Equal cyclic graphs have equal hashes; deep unequal values may intentionally collide.
    /// </summary>
    public static int ValueHashCode<T>(T value) =>
        ModelAdapters.Value<T>().GetHashCode(value, new HashContext());

    /// <summary>Registers operations for the exact declared CLR type T. Register before using models concurrently.</summary>
    public static void RegisterAdapter<T>(IModelAdapter<T> adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        Volatile.Write(ref AdapterRegistration<T>.Adapter, adapter);
    }

    /// <summary>Explicitly declares a CLR type immutable and uses its .NET equality and hash implementation.</summary>
    public static void RegisterImmutable<T>() => RegisterAdapter(ModelAdapters.Immutable<T>());

    /// <summary>Allocates a shallow shell for generated cloning, without invoking any constructor.</summary>
    public static object ShallowClone(object value) => MemberwiseClone(value);

    /// <summary>Creates an actionable error for a type without generated or registered operations.</summary>
    public static NotSupportedException MissingAdapter(Type type) => new(
        $"No generated value operations or typed adapter exist for '{type}'. " +
        "Register one with BondTools.Models.ModelOperations.RegisterAdapter<T>(IModelAdapter<T>), " +
        "or RegisterImmutable<T>() only if the type is immutable.");

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "MemberwiseClone")]
    private static extern object MemberwiseClone(object value);

    internal static IModelAdapter<T>? Registered<T>() => Volatile.Read(ref AdapterRegistration<T>.Adapter);

    private static class AdapterRegistration<T>
    {
        internal static IModelAdapter<T>? Adapter;
    }

    internal static bool IsKnownImmutable(object value) => value is
        string or bool or char or sbyte or byte or short or ushort or int or uint or long or ulong
        or float or double or decimal or Enum or DateTime or DateTimeOffset or TimeSpan
        or DateOnly or TimeOnly or Guid or Version or IntPtr or UIntPtr or System.Numerics.BigInteger
        || value.GetType() == typeof(Uri);
}
