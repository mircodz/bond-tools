using System;
using System.Collections.Generic;
using BondTools.Models;

namespace Bond.Models.Tests;

public sealed class ModelBindingScopeTests
{
    [Fact]
    public void RegisteredAdaptersTakePrecedenceOverContextualBindings()
    {
        var registered = new ModelAdapter<RegisteredValue>(
            (value, context) => new RegisteredValue(value.Number + 100),
            (left, right, context) => left.Number == right.Number,
            (value, context) => value.Number);
        var contextual = new ModelAdapter<RegisteredValue>(
            (value, context) => throw new Exception("contextual clone was used"),
            (left, right, context) => throw new Exception("contextual equality was used"),
            (value, context) => throw new Exception("contextual hash was used"));
        ModelOperations.RegisterAdapter(registered);
        var adapter = ModelAdapters.WithArguments(ModelAdapters.List(ModelAdapters.Value<RegisteredValue>()),
            ModelAdapterArgument.Create(contextual));
        var source = new List<RegisteredValue> { new(7) };
        var equivalent = new List<RegisteredValue> { new(7) };

        var copy = adapter.Clone(source, new CloneContext());

        Assert.NotSame(source, copy);
        Assert.NotSame(source[0], copy[0]);
        Assert.Equal(107, copy[0].Number);
        Assert.True(adapter.Equals(source, equivalent, new EqualityContext()));
        Assert.Equal(ModelAdapters.List(registered).GetHashCode(source, new HashContext()),
            adapter.GetHashCode(source, new HashContext()));
    }

    [Fact]
    public void MaterializedProjectionKeysUseRegisteredRatherThanContextualAdapters()
    {
        var clones = 0;
        var comparisons = 0;
        var hashes = 0;
        var registered = new ModelAdapter<ProjectedValue>(
            (value, context) =>
            {
                clones++;
                return new ProjectedValue(value.Number + 100);
            },
            (left, right, context) =>
            {
                comparisons++;
                return left.Number == right.Number;
            },
            (value, context) =>
            {
                hashes++;
                return value.Number;
            });
        var contextual = new ModelAdapter<ProjectedValue>(
            (value, context) => throw new Exception("contextual clone was used"),
            (left, right, context) => throw new Exception("contextual equality was used"),
            (value, context) => throw new Exception("contextual hash was used"));
        ModelOperations.RegisterAdapter(registered);
        var dispatched = ModelAdapters.Materialized<ProjectedBox, ProjectedValue>(
            box => box.Value, value => new ProjectedBox(value), ModelAdapters.Value<ProjectedValue>());
        var explicitAdapter = ModelAdapters.Materialized<ProjectedBox, ProjectedValue>(
            box => box.Value, value => new ProjectedBox(value), registered);
        var adapter = ModelAdapters.WithArguments(new ModelAdapter<ProjectedBox>(
            (value, context) =>
            {
                var copy = dispatched.Clone(value, context);
                Assert.Same(copy, explicitAdapter.Clone(value, context));
                return copy;
            },
            (left, right, context) =>
            {
                var equal = dispatched.Equals(left, right, context);
                Assert.Equal(equal, explicitAdapter.Equals(left, right, context));
                return equal;
            },
            (value, context) =>
            {
                var hash = dispatched.GetHashCode(value, context);
                Assert.Equal(hash, explicitAdapter.GetHashCode(value, context));
                return hash;
            }), ModelAdapterArgument.Create(contextual));
        var source = new ProjectedBox(new ProjectedValue(7));
        var equivalent = new ProjectedBox(new ProjectedValue(7));

        Assert.Equal(107, adapter.Clone(source, new CloneContext()).Value.Number);
        Assert.True(adapter.Equals(source, equivalent, new EqualityContext()));
        Assert.Equal(7, adapter.GetHashCode(source, new HashContext()));
        Assert.Equal(1, clones);
        Assert.Equal(1, comparisons);
        Assert.Equal(1, hashes);
    }

    [Fact]
    public void DispatchGuardsDoNotInvokeRegisteredAdapters()
    {
        ModelOperations.RegisterAdapter(new ModelAdapter<GuardedValue>(
            (value, context) => throw new Exception("clone guard invoked the adapter"),
            (left, right, context) => throw new Exception("equality guard invoked the adapter"),
            (value, context) => throw new Exception("hash guard invoked the adapter")));
        var adapter = ModelAdapters.Value<GuardedValue>();
        var value = new GuardedValue();
        var exhausted = new HashContext();
        while (exhausted.RemainingDepth > 0)
        {
            exhausted = exhausted.Descend();
        }

        Assert.Null(adapter.Clone(null!, new CloneContext()));
        Assert.True(adapter.Equals(value, value, new EqualityContext()));
        Assert.True(adapter.Equals(null!, null!, new EqualityContext()));
        Assert.False(adapter.Equals(value, null!, new EqualityContext()));
        Assert.False(adapter.Equals(null!, value, new EqualityContext()));
        Assert.Equal(0, adapter.GetHashCode(null!, new HashContext()));
        Assert.Equal(0, adapter.GetHashCode(value, exhausted));
    }

    [Fact]
    public void NestedArgumentScopesInheritBindingsButCapturedScopesReplaceThem()
    {
        var parity = OffsetParityAdapter();
        var ordinary = ModelAdapters.List(ModelAdapters.Value<int>());
        var nested = ModelAdapters.WithArguments(ordinary, ModelAdapterArgument.Create(ModelAdapters.Value<string>()));
        var inherited = ModelAdapters.WithArguments(nested, ModelAdapterArgument.Create(parity));
        var materialized = ModelAdapters.Materialized<CapturedList, List<int>>(
            value => value.Value, (value, adapter) => new CapturedList(value, adapter),
            ModelAdapters.Value<List<int>>());
        var capture = ModelAdapters.WithArguments(materialized, ModelAdapterArgument.Create(ordinary))
            .Clone(new CapturedList([1], ordinary), new CloneContext());
        var dispatcher = ModelAdapters.Value<int>();
        var capturedOperations = new ModelAdapter<List<int>>(
            (value, context) =>
            {
                var copy = capture.Adapter.Clone(value, context);
                Assert.Equal(101, dispatcher.Clone(1, context));
                return copy;
            },
            (left, right, context) =>
            {
                var equal = capture.Adapter.Equals(left, right, context);
                Assert.True(dispatcher.Equals(1, 3, context));
                return equal;
            },
            (value, context) =>
            {
                var hash = capture.Adapter.GetHashCode(value, context);
                Assert.Equal(101, dispatcher.GetHashCode(1, context));
                return hash;
            });
        var captured = ModelAdapters.WithArguments(capturedOperations, ModelAdapterArgument.Create(parity));
        var left = new List<int> { 1 };
        var right = new List<int> { 3 };

        var inheritedCopy = inherited.Clone(left, new CloneContext());
        var capturedCopy = captured.Clone(left, new CloneContext());

        Assert.Equal(new[] { 101 }, inheritedCopy);
        Assert.Equal(new[] { 1 }, capturedCopy);
        Assert.NotSame(left, inheritedCopy);
        Assert.NotSame(left, capturedCopy);
        Assert.True(inherited.Equals(left, right, new EqualityContext()));
        Assert.False(captured.Equals(left, right, new EqualityContext()));
        Assert.Equal(ModelAdapters.List(parity).GetHashCode(left, new HashContext()),
            inherited.GetHashCode(left, new HashContext()));
        Assert.Equal(ordinary.GetHashCode(left, new HashContext()),
            captured.GetHashCode(left, new HashContext()));
    }

    [Fact]
    public void NestedArgumentScopesMaskMatchingOuterBindingsWithoutRecursing()
    {
        var ordinary = ModelAdapters.List(ModelAdapters.Value<int>());
        var inner = ModelAdapters.WithArguments(ordinary, ModelAdapterArgument.Create(ModelAdapters.Value<int>()));
        var outer = ModelAdapters.WithArguments(inner, ModelAdapterArgument.Create(OffsetParityAdapter()));
        var left = new List<int> { 1 };
        var right = new List<int> { 3 };

        Assert.Equal(new[] { 1 }, outer.Clone(left, new CloneContext()));
        Assert.False(outer.Equals(left, right, new EqualityContext()));
        Assert.Equal(ordinary.GetHashCode(left, new HashContext()), outer.GetHashCode(left, new HashContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailingBindingScopesRestoreOuterBindingsForEveryOperation(bool captureBindings)
    {
        var fail = false;
        var inner = new ModelAdapter<List<int>>(
            (value, context) => fail ? throw new InvalidOperationException("clone failed") : new List<int>(value),
            (left, right, context) => throw new InvalidOperationException("equality failed"),
            (value, context) => throw new InvalidOperationException("hash failed"));
        IModelAdapter<List<int>> scoped;
        if (captureBindings)
        {
            var materialized = ModelAdapters.Materialized<CapturedList, List<int>>(
                value => value.Value, (value, adapter) => new CapturedList(value, adapter), inner);
            var capture = ModelAdapters.WithArguments(materialized,
                ModelAdapterArgument.Create(ModelAdapters.Value<string>()));
            scoped = capture.Clone(new CapturedList([1], inner), new CloneContext()).Adapter;
        }
        else
        {
            scoped = ModelAdapters.WithArguments(inner, ModelAdapterArgument.Create(ModelAdapters.Value<string>()));
        }

        fail = true;
        var dispatcher = ModelAdapters.Value<int>();
        var outer = ModelAdapters.WithArguments(new ModelAdapter<List<int>>(
            (value, context) =>
            {
                var error = Assert.Throws<InvalidOperationException>(() => scoped.Clone(value, context));
                Assert.Equal("clone failed", error.Message);
                return new List<int> { dispatcher.Clone(value[0], context) };
            },
            (left, right, context) =>
            {
                var error = Assert.Throws<InvalidOperationException>(() => scoped.Equals(left, right, context));
                Assert.Equal("equality failed", error.Message);
                return dispatcher.Equals(left[0], right[0], context);
            },
            (value, context) =>
            {
                var error = Assert.Throws<InvalidOperationException>(() => scoped.GetHashCode(value, context));
                Assert.Equal("hash failed", error.Message);
                return dispatcher.GetHashCode(value[0], context);
            }), ModelAdapterArgument.Create(OffsetParityAdapter()));
        var source = new List<int> { 1 };
        var equivalent = new List<int> { 3 };
        var clones = new CloneContext();
        var equality = new EqualityContext();
        var hashes = new HashContext();

        Assert.Equal(new[] { 101 }, outer.Clone(source, clones));
        Assert.True(outer.Equals(source, equivalent, equality));
        Assert.Equal(101, outer.GetHashCode(source, hashes));

        Assert.Equal(1, dispatcher.Clone(1, clones));
        Assert.False(dispatcher.Equals(1, 3, equality));
        Assert.Equal(1, dispatcher.GetHashCode(1, hashes));
    }

    [Fact]
    public void HashCachesShareResultsAtTheSameDepthAndSeparateDepthsAndSemantics()
    {
        var calls = 0;
        var adapter = ModelAdapters.List(new ModelAdapter<int>(
            (value, context) => value,
            (left, right, context) => left == right,
            (value, context) =>
            {
                calls++;
                return context.RemainingDepth;
            }));
        var other = ModelAdapters.List(new ModelAdapter<int>(
            (value, context) => value,
            (left, right, context) => left == right,
            (value, context) => context.RemainingDepth + 100));
        var source = new List<int> { 1 };
        var hashes = new HashContext();

        var rootHash = adapter.GetHashCode(source, hashes);
        var childHash = adapter.GetHashCode(source, hashes.Descend());

        Assert.NotEqual(rootHash, childHash);
        Assert.Equal(rootHash, adapter.GetHashCode(source, hashes));
        Assert.Equal(childHash, adapter.GetHashCode(source, hashes.Descend()));
        Assert.Equal(2, calls);

        var otherRootHash = other.GetHashCode(source, hashes);
        var otherChildHash = other.GetHashCode(source, hashes.Descend());
        Assert.NotEqual(rootHash, otherRootHash);
        Assert.NotEqual(childHash, otherChildHash);
        Assert.Equal(other.GetHashCode(source, new HashContext()), otherRootHash);
        Assert.Equal(other.GetHashCode(source, new HashContext().Descend()), otherChildHash);
        Assert.Equal(rootHash, adapter.GetHashCode(source, hashes));
        Assert.Equal(childHash, adapter.GetHashCode(source, hashes.Descend()));
        Assert.Equal(2, calls);
    }

    private static IModelAdapter<int> OffsetParityAdapter() => new ModelAdapter<int>(
        (value, context) => value + 100,
        (left, right, context) => (left & 1) == (right & 1),
        (value, context) => (value & 1) + 100);

    private sealed class RegisteredValue(int number)
    {
        public int Number { get; } = number;
    }

    private sealed class GuardedValue { }

    private sealed class ProjectedValue(int number)
    {
        public int Number { get; } = number;
    }

    private sealed class ProjectedBox(ProjectedValue value)
    {
        public ProjectedValue Value { get; } = value;
    }

    private sealed class CapturedList(List<int> value, IModelAdapter<List<int>> adapter)
    {
        public List<int> Value { get; } = value;
        public IModelAdapter<List<int>> Adapter { get; } = adapter;
    }
}
