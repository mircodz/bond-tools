using System;
using System.Collections.Generic;
using BondTools.Models;

namespace Bond.Parser.Tests;

public sealed class ModelsAdapterRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaterializedProjectionsDoNotShareCollectionEqualityOrHashCaches(bool projectionFirst)
    {
        var ordered = ModelAdapters.List(ModelAdapters.Value<int>());
        var unordered = ModelAdapters.Materialized<List<int>, HashSet<int>>(
            values => new HashSet<int>(values),
            values => new List<int>(values),
            ModelAdapters.Set(ModelAdapters.Value<int>()));
        var left = new List<int> { 1, 2 };
        var right = new List<int> { 2, 1 };
        var equality = new EqualityContext();
        var hashes = new HashContext();
        if (projectionFirst)
        {
            Assert.True(unordered.Equals(left, right, equality));
            Assert.False(ordered.Equals(left, right, equality));
            Assert.Equal(622, unordered.GetHashCode(left, hashes));
            Assert.Equal(507472, ordered.GetHashCode(left, hashes));
        }
        else
        {
            Assert.False(ordered.Equals(left, right, equality));
            Assert.True(unordered.Equals(left, right, equality));
            Assert.Equal(507472, ordered.GetHashCode(left, hashes));
            Assert.Equal(622, unordered.GetHashCode(left, hashes));
        }

        Assert.Equal(ordered.GetHashCode(left, new HashContext()), ordered.GetHashCode(left, hashes));
        Assert.Equal(unordered.GetHashCode(left, new HashContext()), unordered.GetHashCode(left, hashes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaterializedProjectionsDoNotShareCollectionCloneCaches(bool projectionFirst)
    {
        var ordered = ModelAdapters.List(ModelAdapters.Value<int>());
        var projected = ModelAdapters.Materialized<List<int>, int>(
            values => values.Count, count => new List<int> { count }, ModelAdapters.Value<int>());
        var source = new List<int> { 2, 1, 2 };
        var clones = new CloneContext();
        List<int> orderedCopy;
        List<int> projectedCopy;
        if (projectionFirst)
        {
            projectedCopy = projected.Clone(source, clones);
            orderedCopy = ordered.Clone(source, clones);
        }
        else
        {
            orderedCopy = ordered.Clone(source, clones);
            projectedCopy = projected.Clone(source, clones);
        }

        Assert.Equal(source, orderedCopy);
        Assert.Equal(new[] { 3 }, projectedCopy);
        Assert.NotSame(source, orderedCopy);
        Assert.NotSame(source, projectedCopy);
        Assert.NotSame(orderedCopy, projectedCopy);
        Assert.Same(orderedCopy, ordered.Clone(source, clones));
        Assert.Same(projectedCopy, projected.Clone(source, clones));
        projectedCopy.Add(99);
        Assert.Equal(3, source.Count);
        Assert.Equal(source, orderedCopy);
    }

    [Fact]
    public void SharedAndSeparateEqualListsHaveEqualHashesWithDifferentFieldAdapters()
    {
        var exact = ModelAdapters.List(ModelAdapters.Value<int>());
        var offset = ModelAdapters.List(new ModelAdapter<int>(
            (value, context) => value,
            (left, right, context) => left == right,
            (value, context) => value + 100));
        var shared = new List<int> { 1 };
        var separate = new List<int> { 1 };
        var equality = new EqualityContext();
        Assert.True(exact.Equals(shared, shared, equality));
        Assert.True(offset.Equals(shared, separate, equality));

        var sharedContext = new HashContext();
        var separateContext = new HashContext();
        var sharedHash = HashContext.Combine(exact.GetHashCode(shared, sharedContext),
            offset.GetHashCode(shared, sharedContext));
        var separateHash = HashContext.Combine(exact.GetHashCode(shared, separateContext),
            offset.GetHashCode(separate, separateContext));
        Assert.Equal(separateHash, sharedHash);
    }

    [Fact]
    public void ParityComparisonsDoNotContaminateExactComparisonsOrAcceptedBranches()
    {
        var parity = ModelAdapters.List(new ModelAdapter<int>(
            (value, context) => value,
            (left, right, context) => (left & 1) == (right & 1),
            (value, context) => value & 1));
        var exact = ModelAdapters.List(ModelAdapters.Value<int>());
        var left = new List<int> { 1 };
        var right = new List<int> { 3 };
        var context = new EqualityContext();
        var branch = context.Fork();
        Assert.True(parity.Equals(left, right, branch));
        context.Accept(branch);
        Assert.False(exact.Equals(left, right, context));
        Assert.False(exact.Equals(left, right, context.Fork()));
    }

    [Fact]
    public void CloneCachesSeparateDifferentAdaptersButShareEquivalentContainerAdapters()
    {
        var element = new ModelAdapter<int>(
            (value, context) => value + 100,
            (left, right, context) => left == right,
            (value, context) => value);
        var source = new List<int> { 1 };
        var context = new CloneContext();
        var exact = ModelAdapters.List(ModelAdapters.Value<int>()).Clone(source, context);
        var offset = ModelAdapters.List(element).Clone(source, context);

        Assert.Equal(1, exact[0]);
        Assert.Equal(101, offset[0]);
        Assert.NotSame(source, exact);
        Assert.NotSame(source, offset);
        Assert.NotSame(exact, offset);
        Assert.Same(exact, ModelAdapters.List(ModelAdapters.Value<int>()).Clone(source, context));
        Assert.Same(offset, ModelAdapters.List(element).Clone(source, context));
    }

    [Fact]
    public void DelegateAdaptersScopePublicContextMemoizationAndPreserveCycles()
    {
        IModelAdapter<Node>? exact = null;
        IModelAdapter<Node>? parity = null;
        exact = CreateNodeAdapter(false, () => exact!);
        parity = CreateNodeAdapter(true, () => parity!);
        var left = new Node { Number = 1 };
        var right = new Node { Number = 3 };
        left.Next = left;
        right.Next = right;
        var comparisons = new EqualityContext();
        Assert.True(parity.Equals(left, right, comparisons));
        Assert.False(exact.Equals(left, right, comparisons));

        var clones = new CloneContext();
        var exactCopy = exact.Clone(left, clones);
        var parityCopy = parity.Clone(left, clones);
        Assert.NotSame(left, exactCopy);
        Assert.NotSame(left, parityCopy);
        Assert.NotSame(exactCopy, parityCopy);
        Assert.Same(exactCopy, exactCopy.Next);
        Assert.Same(parityCopy, parityCopy.Next);
        Assert.Same(exactCopy, exact.Clone(left, clones));
    }

    [Fact]
    public void MaterializedAdaptersCaptureBindingsWithoutRetainingCloneCaches()
    {
        var materialized = ModelAdapters.Materialized<LazyList, List<int>>(
            value => value.Value,
            (value, adapter) => new LazyList(value, adapter),
            ModelAdapters.Value<List<int>>());
        var scoped = ModelAdapters.WithArguments(materialized,
            ModelAdapterArgument.Create(ModelAdapters.List(ModelAdapters.Value<int>())));
        var source = new LazyList(new List<int> { 1 }, ModelAdapters.Value<List<int>>());
        var originalContext = new CloneContext();
        var copy = scoped.Clone(source, originalContext);
        Assert.NotSame(source.Value, copy.Value);

        var first = copy.Deserialize();
        var second = copy.Deserialize();
        Assert.NotSame(copy.Value, first);
        Assert.NotSame(copy.Value, second);
        Assert.NotSame(first, second);
        first.Add(2);
        Assert.Single(source.Value);
        Assert.Single(copy.Value);
        Assert.Single(second);

        var later = copy.Adapter.Clone(copy.Value, originalContext);
        Assert.NotSame(first, later);
        Assert.NotSame(second, later);
        Assert.Throws<NotSupportedException>(() =>
            ModelAdapters.Value<List<int>>().Clone(new List<int>(), originalContext));
        Assert.Throws<NotSupportedException>(() => materialized.Clone(source, new CloneContext()));
    }

    [Fact]
    public void NestedMaterializedAdaptersPublishWrapperShellsBeforeCloningCyclicChildren()
    {
        IModelAdapter<PayloadBox<PayloadBox<NestedPayload>>>? outer = null;
        var payloadAdapter = new ModelAdapter<NestedPayload>(
            (value, context) =>
            {
                if (context.TryGetClone<NestedPayload>(value, out var existing))
                {
                    return existing;
                }

                var clone = new NestedPayload();
                context.Register(value, clone);
                clone.Wrapper = outer!.Clone(value.Wrapper!, context);
                return clone;
            },
            (left, right, context) => true,
            (value, context) => 0);
        var inner = ModelAdapters.Materialized<PayloadBox<NestedPayload>, NestedPayload>(
            value => value.Value, value => new PayloadBox<NestedPayload>(value), payloadAdapter);
        outer = ModelAdapters.Materialized<PayloadBox<PayloadBox<NestedPayload>>, PayloadBox<NestedPayload>>(
            value => value.Value, value => new PayloadBox<PayloadBox<NestedPayload>>(value), inner);
        var payload = new NestedPayload();
        var source = new PayloadBox<PayloadBox<NestedPayload>>(new PayloadBox<NestedPayload>(payload));
        payload.Wrapper = source;

        var copy = outer.Clone(source, new CloneContext());
        Assert.NotSame(source, copy);
        Assert.NotSame(source.Value, copy.Value);
        Assert.NotSame(payload, copy.Value.Value);
        Assert.Same(copy, copy.Value.Value.Wrapper);
    }

    [Fact]
    public void ExactUriIsImmutableButMutableSubclassesRequireAnAdapter()
    {
        var exact = new Uri("https://example.test/value");
        Assert.Same(exact, ModelOperations.Clone(exact));
        Assert.True(ModelOperations.ValueEquals(exact, new Uri(exact.AbsoluteUri)));
        Assert.Equal(exact.GetHashCode(), ModelOperations.ValueHashCode(exact));

        var mutable = new MutableUri();
        mutable.Values.Add(7);
        Assert.Throws<NotSupportedException>(() => ModelOperations.Clone(mutable));
        Assert.Throws<NotSupportedException>(() => ModelOperations.Clone<Uri>(mutable));
        Assert.Throws<NotSupportedException>(() => ModelOperations.Clone<object>(mutable));
        Assert.Throws<NotSupportedException>(() => ModelOperations.ValueEquals(mutable, new MutableUri()));
        Assert.Throws<NotSupportedException>(() => ModelOperations.ValueHashCode(mutable));
    }

    private static IModelAdapter<Node> CreateNodeAdapter(bool parity, Func<IModelAdapter<Node>> self) =>
        new ModelAdapter<Node>(
            (value, context) =>
            {
                if (context.TryGetClone<Node>(value, out var found))
                {
                    return found;
                }

                var clone = new Node { Number = value.Number };
                context.Register(value, clone);
                clone.Next = self().Clone(value.Next!, context);
                return clone;
            },
            (left, right, context) => context.CompareReferences(left, right, () =>
                (parity ? (left.Number & 1) == (right.Number & 1) : left.Number == right.Number) &&
                self().Equals(left.Next!, right.Next!, context)),
            (value, context) => parity ? value.Number & 1 : value.Number);

    private sealed class Node
    {
        public int Number { get; set; }
        public Node? Next { get; set; }
    }

    private sealed class MutableUri() : Uri("https://example.test/value")
    {
        public List<int> Values { get; } = new();
    }

    private sealed class LazyList(List<int> value, IModelAdapter<List<int>> adapter)
    {
        public List<int> Value { get; } = value;
        public IModelAdapter<List<int>> Adapter { get; } = adapter;

        public List<int> Deserialize() => Adapter.Clone(Value, new CloneContext());
    }

    private sealed class PayloadBox<T>(T value)
    {
        public T Value { get; } = value;
    }

    private sealed class NestedPayload
    {
        public PayloadBox<PayloadBox<NestedPayload>>? Wrapper { get; set; }
    }
}
