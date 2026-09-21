using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using BondTools.Models;

namespace Bond.Parser.Tests;

public sealed class GeneratedModelOperationsTests
{
    [Fact]
    public async Task CloneOwnsMutableValuesPreservesCyclesSharingAndRuntimeTypesWithoutConstructors()
    {
        await Check("""
            namespace Models
            struct Node {
                0: int32 id;
                1: nullable<Node> next;
                2: vector<Node> children;
                3: vector<Node> other_children;
                4: map<string, Node> lookup;
                5: set<int32> flags;
                6: list<int32> sequence;
                7: blob bytes;
                8: blob more_bytes;
            }
            struct Derived : Node { 0: int32 id; }
            struct Root { 0: Node first; 1: Node second; }
            """, """
            var node = new Models.Derived { id = 9 };
            ((Models.Node)node).id = 7;
            node.next = node;
            node.children.Add(node);
            node.other_children = node.children;
            node.lookup.Add("self", node);
            node.flags.UnionWith(new[] { 4, 2, 8 });
            node.sequence.AddLast(1);
            node.sequence.AddLast(2);
            var bytes = new byte[] { 90, 1, 2, 3, 91 };
            node.bytes = new ArraySegment<byte>(bytes, 1, 3);
            node.more_bytes = new ArraySegment<byte>(bytes, 2, 2);
            var root = new Models.Root { first = node, second = node };
            var constructions = Models.Node.Constructions;
            var copy = root.Clone();
            Require(Models.Node.Constructions == constructions, "clone invoked an ordinary constructor");
            Require(!ReferenceEquals(root, copy), "root was not cloned");
            Require(copy.first is Models.Derived, "runtime type was lost");
            var child = (Models.Derived)copy.first;
            Require(ReferenceEquals(copy.first, copy.second), "shared model reference was lost");
            Require(ReferenceEquals(child, child.next), "model cycle was lost");
            Require(child.id == 9 && ((Models.Node)child).id == 7, "hidden base field was not cloned");
            Require(!ReferenceEquals(child.children, node.children), "vector was shallow copied");
            Require(ReferenceEquals(child.children, child.other_children), "shared vector was lost");
            Require(ReferenceEquals(child.children[0], child), "vector element was not cloned");
            Require(!ReferenceEquals(child.lookup, node.lookup) && ReferenceEquals(child.lookup["self"], child), "map was shallow copied");
            Require(!ReferenceEquals(child.flags, node.flags) && child.flags.SetEquals(node.flags), "set was shallow copied");
            Require(!ReferenceEquals(child.sequence, node.sequence) && child.sequence.SequenceEqual(node.sequence), "list was shallow copied");
            Require(!ReferenceEquals(child.bytes.Array, bytes), "blob backing array was shallow copied");
            Require(ReferenceEquals(child.bytes.Array, child.more_bytes.Array), "shared blob backing array was lost");
            Require(child.bytes.Offset == 1 && child.more_bytes.Offset == 2, "blob segment boundaries changed");
            Require(root.Equals(copy) && root.GetHashCode() == copy.GetHashCode(), "clone was not value-equal");
            child.bytes.Array[1] = 55;
            Require(bytes[1] == 1, "mutating cloned bytes affected source");
            Require(!root.Equals(copy), "changed clone still compared equal");
            Require(!new Models.Node().Equals(new Models.Derived()), "base compared equal to derived");
            Require(!new Models.Derived().Equals(new Models.Node()), "derived compared equal to base");
            """, """
            namespace Models
            {
                public partial class Node
                {
                    public static int Constructions;
                    private readonly int witness = ++Constructions;
                }
            }
            """);
    }

    [Fact]
    public async Task StructuralEqualityAndHashRespectOrderingNothingAndDotNetFloatingPointSemantics()
    {
        await Check("""
            namespace Models
            struct Value {
                0: double number;
                1: float single;
                2: map<string, int32> lookup;
                3: set<int32> flags;
                4: vector<int32> sequence;
                5: int32 absent = nothing;
                6: nullable<int32> maybe;
                7: blob bytes = nothing;
            }
            """, """
            var left = new Models.Value { number = double.NaN, single = -0.0f };
            var right = new Models.Value
            {
                number = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000001)),
                single = 0.0f
            };
            left.lookup.Add("a", 1); left.lookup.Add("b", 2);
            right.lookup.Add("b", 2); right.lookup.Add("a", 1);
            left.flags.UnionWith(new[] { 3, 1, 2 });
            right.flags.UnionWith(new[] { 2, 1, 3 });
            left.sequence.AddRange(new[] { 1, 2 });
            right.sequence.AddRange(new[] { 1, 2 });
            Require(left.Equals(right) && right.Equals((object)left), "value equality ignored .NET scalar or unordered semantics");
            Require(((IEquatable<Models.Value>)left).Equals(right), "IEquatable disagreed");
            Require(left.GetHashCode() == right.GetHashCode(), "equal unordered values hashed differently");
            right.sequence.Reverse();
            Require(!left.Equals(right), "sequence order was ignored");
            right.sequence.Reverse();
            right.absent = 0;
            Require(!left.Equals(right), "nothing was confused with zero");
            right.absent = null;
            right.maybe = 0;
            Require(!left.Equals(right), "nullable absence was confused with zero");
            right.maybe = null;
            right.bytes = new ArraySegment<byte>(Array.Empty<byte>());
            Require(!left.Equals(right), "default blob was confused with owned empty blob");
            right.bytes = default;
            Require(left.Equals(right), "restored values differed");
            Require(!left.Equals(null), "model equaled null");
            """);
    }

    [Fact]
    public async Task CyclicEqualityIgnoresSharingAndHashesBisimilarGraphsEqually()
    {
        await Check("""
            namespace Models
            struct Node { 0: int32 id; 1: nullable<Node> next; 2: vector<Node> children; }
            """, """
            var self = new Models.Node { id = 4 };
            self.next = self;
            var first = new Models.Node { id = 4 };
            var second = new Models.Node { id = 4 };
            first.next = second; second.next = first;
            Require(self.Equals(first) && first.Equals(self), "bisimilar cycles were unequal");
            Require(self.GetHashCode() == first.GetHashCode(), "bisimilar cycles used identity hashes");
            var shared = new Models.Node();
            shared.children.Add(self); shared.children.Add(self);
            var duplicated = new Models.Node();
            duplicated.children.Add(first); duplicated.children.Add(self.Clone());
            Require(shared.Equals(duplicated), "sharing affected equality");
            Require(shared.GetHashCode() == duplicated.GetHashCode(), "sharing affected hash");
            second.id = 5;
            Require(!self.Equals(first) && !first.Equals(self), "different cyclic values compared equal");
            """);
    }

    [Fact]
    public async Task GenericFieldsAndGenericHiddenBasesUseTypedOperations()
    {
        await Check("""
            namespace Models
            struct Leaf { 0: int32 number; }
            struct Box<T> { 0: T value; 1: vector<T> values; }
            struct Derived<T> : Box<vector<T>> { 0: T value; }
            struct Scalar<T : value> { 0: nullable<T> value; }
            struct ContainerRoot { 0: Box<vector<Leaf>> wrapped; 1: Box<Box<vector<Leaf>>> nested; }
            """, """
            var integers = new Models.Box<int> { value = 42 };
            integers.values.Add(9);
            Require(integers.Equals(integers.Clone()), "generic primitive did not clone or compare");
            var scalar = new Models.Scalar<double> { value = double.NaN };
            Require(scalar.Equals(scalar.Clone()), "constrained nullable generic failed");
            var leaf = new Models.Leaf { number = 7 };
            var box = new Models.Box<Models.Leaf> { value = leaf };
            box.values.Add(leaf);
            var copy = box.Clone();
            Require(!ReferenceEquals(copy.value, leaf) && ReferenceEquals(copy.value, copy.values[0]), "generic model did not preserve sharing");
            var derived = new Models.Derived<Models.Leaf> { value = leaf };
            ((Models.Box<List<Models.Leaf>>)derived).value = new List<Models.Leaf> { leaf };
            ((Models.Box<List<Models.Leaf>>)derived).values.Add(((Models.Box<List<Models.Leaf>>)derived).value);
            var derivedCopy = derived.Clone();
            var baseCopy = (Models.Box<List<Models.Leaf>>)derivedCopy;
            Require(ReferenceEquals(baseCopy.value[0], derivedCopy.value), "substituted hidden base field lost shared value");
            Require(ReferenceEquals(baseCopy.value, baseCopy.values[0]), "nested substituted generic containers lost sharing");
            Require(derived.Equals(derivedCopy) && derived.GetHashCode() == derivedCopy.GetHashCode(), "generic derived operations disagreed");
            var containers = new Models.ContainerRoot();
            containers.wrapped.value = new List<Models.Leaf> { leaf };
            containers.nested.value = containers.wrapped;
            var containersCopy = containers.Clone();
            Require(!ReferenceEquals(containersCopy.wrapped.value, containers.wrapped.value), "generic container was shallow copied");
            Require(ReferenceEquals(containersCopy.wrapped, containersCopy.nested.value), "nested generic sharing was lost");
            Require(containers.Equals(containersCopy) && containers.GetHashCode() == containersCopy.GetHashCode(), "generic container operations disagreed");
            var blobs = new Models.Box<ArraySegment<byte>> { value = new ArraySegment<byte>(new byte[] { 1, 2 }) };
            var blobsCopy = blobs.Clone();
            Require(!ReferenceEquals(blobs.value.Array, blobsCopy.value.Array) && blobs.Equals(blobsCopy), "generic blob did not deep clone");
            """);
    }

    [Fact]
    public async Task UnknownGenericValuesRequireTypedAdaptersAndKnownImmutableTypesWork()
    {
        await Check("""
            namespace Models
            struct Box<T> { 0: T value; 1: vector<T> values; }
            """, """
            var external = new External { Data = new List<int> { 4, 5 } };
            var box = new Models.Box<External> { value = external };
            box.values.Add(external);
            try { box.Clone(); throw new Exception("unknown mutable CLR value was silently shallow copied"); }
            catch (NotSupportedException error)
            {
                Require(error.Message.Contains("RegisterAdapter<T>"), "missing adapter did not include registration guidance");
            }
            ModelOperations.RegisterAdapter<External>(new ModelAdapter<External>(
                (value, context) =>
                {
                    if (context.TryGetClone<External>(value, out var found)) return found;
                    var clone = new External();
                    context.Register(value, clone);
                    clone.Data = ModelAdapters.List(ModelAdapters.Value<int>()).Clone(value.Data, context);
                    return clone;
                },
                (left, right, context) => ModelAdapters.List(ModelAdapters.Value<int>()).Equals(left.Data, right.Data, context),
                (value, context) => ModelAdapters.List(ModelAdapters.Value<int>()).GetHashCode(value.Data, context)));
            var copy = box.Clone();
            Require(!ReferenceEquals(copy.value, external) && !ReferenceEquals(copy.value.Data, external.Data), "registered adapter was not used");
            Require(ReferenceEquals(copy.value, copy.values[0]), "registered adapter lost sharing");
            Require(box.Equals(copy) && box.GetHashCode() == copy.GetHashCode(), "registered value equality disagreed");
            var date = new Models.Box<DateTime> { value = new DateTime(2024, 3, 5, 6, 7, 8, DateTimeKind.Utc) };
            Require(date.Equals(date.Clone()), "DateTime needed an adapter");
            var guid = new Models.Box<Guid> { value = Guid.NewGuid() };
            Require(guid.Equals(guid.Clone()), "Guid needed an adapter");
            Require(ModelOperations.Clone(TimeSpan.FromHours(3)) == TimeSpan.FromHours(3), "TimeSpan needed an adapter");
            Require(ModelOperations.Clone(new DateOnly(2024, 3, 5)) == new DateOnly(2024, 3, 5), "DateOnly needed an adapter");
            """, """
            public sealed class External
            {
                public System.Collections.Generic.List<int> Data = new();
                public override string ToString() => throw new System.Exception("unexpected formatting");
            }
            """);
    }

    [Fact]
    public async Task BondedOperationsMaterializeOnceAndPreserveCyclicSharedWrappers()
    {
        await Check("""
            namespace Models
            struct Payload { 0: int32 number; 1: nullable<Holder> owner; }
            struct Holder { 0: bonded<Payload> first; 1: bonded<Payload> second; 2: vector<bonded<Payload>> values; }
            """, """
            var source = new Models.Holder();
            var payload = new Models.Payload { number = 7, owner = source };
            var wrapper = new CountingBonded(payload);
            source.first = wrapper; source.second = wrapper; source.values.Add(wrapper);
            var copy = source.Clone();
            Require(wrapper.Reads == 1 && wrapper.Writes == 0, "cloning did not materialize exactly once");
            Require(!ReferenceEquals(copy.first, wrapper) && ReferenceEquals(copy.first, copy.second)
                && ReferenceEquals(copy.first, copy.values[0]), "bonded wrapper sharing was lost");
            var copiedPayload = ((IMaterializedModelValue<Models.Payload>)copy.first).Value;
            Require(!ReferenceEquals(copiedPayload, payload) && ReferenceEquals(copiedPayload.owner, copy), "bonded payload cycle was lost");
            Require(source.Equals(copy), "materialized clone was not value equal");
            Require(wrapper.Reads == 2, "equality materialized one wrapper repeatedly");
            Require(source.GetHashCode() == copy.GetHashCode(), "materialized value hashes differed");
            Require(wrapper.Reads == 3 && wrapper.Writes == 0, "hash materialized repeatedly or serialized");
            var deserialized = copy.first.Deserialize();
            Require(!ReferenceEquals(deserialized, copiedPayload), "cloned bonded Deserialize did not clone actual data");
            Require(ReferenceEquals(((IMaterializedModelValue<Models.Payload>)deserialized.owner.first).Value, deserialized),
                "cloned bonded Deserialize lost its cycle");
            copiedPayload.number = 8;
            Require(!source.Equals(copy), "bonded equality compared wrapper identity rather than data");
            """, CountingBondedSource);
    }

    [Fact]
    public async Task CovariantBondedClonesSharePayloadsAndCyclesInEitherFieldOrder()
    {
        await Check("""
            namespace Models
            struct Base {
                0: vector<int32> values;
                1: bonded<Derived> derived_view;
                2: bonded<Base> base_view;
            }
            struct Derived : Base { 0: int32 number; }
            struct BaseFirst { 0: bonded<Base> first; 1: bonded<Derived> second; }
            struct DerivedFirst { 0: bonded<Derived> first; 1: bonded<Base> second; }
            """, """
            foreach (var baseFirst in new[] { true, false })
            {
                var payload = new Models.Derived { number = 7 };
                payload.values.Add(42);
                var wrapper = new CountingDerivedBonded(payload);
                payload.derived_view = wrapper;
                payload.base_view = wrapper;
                Bond.IBonded<Models.Base> baseView;
                Bond.IBonded<Models.Derived> derivedView;
                if (baseFirst)
                {
                    var source = new Models.BaseFirst { first = wrapper, second = wrapper };
                    var copy = source.Clone();
                    baseView = copy.first;
                    derivedView = copy.second;
                }
                else
                {
                    var source = new Models.DerivedFirst { first = wrapper, second = wrapper };
                    var copy = source.Clone();
                    baseView = copy.second;
                    derivedView = copy.first;
                }

                Require(wrapper.Reads == 1, "covariant views materialized the source repeatedly");
                Require(!ReferenceEquals(baseView, wrapper) && !ReferenceEquals(derivedView, wrapper),
                    "a covariant view retained the source wrapper");
                var copiedBase = ((IMaterializedModelValue<Models.Base>)baseView).Value;
                var copiedDerived = ((IMaterializedModelValue<Models.Derived>)derivedView).Value;
                Require(ReferenceEquals(copiedBase, copiedDerived) && !ReferenceEquals(copiedDerived, payload),
                    "covariant views did not share an independently cloned payload");
                Require(copiedDerived.number == 7 && !ReferenceEquals(copiedDerived.values, payload.values),
                    "the runtime payload type or mutable data was lost");
                Require(ReferenceEquals(copiedDerived.derived_view, derivedView),
                    "the derived wrapper cycle was lost");
                Require(ReferenceEquals(((IMaterializedModelValue<Models.Base>)copiedDerived.base_view).Value, copiedDerived),
                    "the base wrapper cycle was lost");
                copiedDerived.values.Add(99);
                Require(payload.values.Count == 1, "mutating the clone affected the original payload");
            }
            """, """
            public sealed class CountingDerivedBonded : Bond.IBonded<Models.Derived>
            {
                private readonly Models.Derived payload;
                public int Reads;
                public CountingDerivedBonded(Models.Derived payload) { this.payload = payload; }
                public Models.Derived Deserialize() { Reads++; return payload; }
                public T Deserialize<T>() => throw new Exception("untyped deserialization");
                public Bond.IBonded<T> Convert<T>() => throw new Exception("unexpected conversion");
                public void Serialize<T>(T writer) => throw new Exception("unexpected serialization");
                public override string ToString() => throw new Exception("unexpected formatting");
            }
            """);
    }

    [Fact]
    public async Task GenericMaterializedBindingsPreserveSharedWrappersAndOwnerCycles()
    {
        await Check("""
            namespace Models
            struct Payload { 0: int32 number; 1: nullable<Holder> owner; }
            struct Box<T> { 0: T value; }
            struct Holder { 0: Box<bonded<Payload>> wrapped; 1: bonded<Payload> direct; }
            """, """
            var source = new Models.Holder();
            var payload = new Models.Payload { number = 7, owner = source };
            var wrapper = new CountingBonded(payload);
            source.wrapped.value = wrapper;
            source.direct = wrapper;

            var copy = source.Clone();
            var copiedPayload = ((IMaterializedModelValue<Models.Payload>)copy.direct).Value;
            Require(wrapper.Reads == 1, "a standard generic binding materialized the wrapper repeatedly");
            Require(ReferenceEquals(copy.wrapped.value, copy.direct), "a standard generic binding split same-view wrappers");
            Require(ReferenceEquals(copiedPayload.owner, copy), "a standard generic binding split the owner cycle");
            Require(!ReferenceEquals(copy.direct, wrapper) && !ReferenceEquals(copiedPayload, payload),
                "generic materialized cloning retained original mutable data");
            Require(source.Equals(copy) && source.GetHashCode() == copy.GetHashCode(),
                "generic materialized equality or hashing changed");
            """, CountingBondedSource);
    }

    [Fact]
    public async Task GenericMaterializedProjectionsRetainTheirContextualSemantics()
    {
        await Check("""
            namespace Models
            struct Box<T> { 0: T value; }
            """, """
            var ordered = ModelAdapters.WithArguments(ModelAdapters.Value<Models.Box<List<int>>>(),
                ModelAdapterArgument.Create(ModelAdapters.List(ModelAdapters.Value<int>())));
            var unordered = ModelAdapters.WithArguments(ModelAdapters.Value<Models.Box<List<int>>>(),
                ModelAdapterArgument.Create(ModelAdapters.Materialized<List<int>, HashSet<int>>(
                    values => new HashSet<int>(values), values => new List<int>(values),
                    ModelAdapters.Set(ModelAdapters.Value<int>()))));
            var projected = ModelAdapters.WithArguments(ModelAdapters.Value<Models.Box<List<int>>>(),
                ModelAdapterArgument.Create(ModelAdapters.Materialized<List<int>, int>(
                    values => values.Count, count => new List<int> { count }, ModelAdapters.Value<int>())));
            var left = new Models.Box<List<int>> { value = new List<int> { 1, 2 } };
            var right = new Models.Box<List<int>> { value = new List<int> { 2, 1 } };
            var equality = new EqualityContext();
            Require(unordered.Equals(left, right, equality), "contextual projection comparison failed");
            Require(!ordered.Equals(left, right, equality), "a projection contaminated ordinary generic comparison");

            var hashes = new HashContext();
            var unorderedHash = unordered.GetHashCode(left, hashes);
            var orderedHash = ordered.GetHashCode(left, hashes);
            Require(unorderedHash != orderedHash && orderedHash == ordered.GetHashCode(left, new HashContext()),
                "a projection contaminated ordinary generic hashing");
            var clones = new CloneContext();
            var projectedCopy = projected.Clone(left, clones);
            var orderedCopy = ordered.Clone(left, clones);
            Require(!ReferenceEquals(projectedCopy, orderedCopy) &&
                projectedCopy.value.SequenceEqual(new[] { 2 }) && orderedCopy.value.SequenceEqual(new[] { 1, 2 }),
                "a projection contaminated ordinary generic cloning");
            Require(!ReferenceEquals(projectedCopy.value, left.value) && !ReferenceEquals(orderedCopy.value, left.value),
                "generic projection cloning retained original mutable data");
            """);
    }

    [Fact]
    public async Task GenericAdapterBindingsIsolateMemoizationAndPreserveEquivalentScopesAndCycles()
    {
        await Check("""
            namespace Models
            struct Box<T> { 0: T value; 1: nullable<Box<T>> next; }
            """, """
            var exactList = ModelAdapters.List(ModelAdapters.Value<int>());
            var offsetElement = new ModelAdapter<int>(
                (value, context) => value + 100,
                (left, right, context) => left == right,
                (value, context) => value + 100);
            var offsetList = ModelAdapters.List(offsetElement);
            var parityList = ModelAdapters.List(new ModelAdapter<int>(
                (value, context) => value,
                (left, right, context) => (left & 1) == (right & 1),
                (value, context) => value & 1));
            var exact = ModelAdapters.WithArguments(ModelAdapters.Value<Models.Box<List<int>>>(),
                ModelAdapterArgument.Create(exactList));
            var offset = ModelAdapters.WithArguments(ModelAdapters.Value<Models.Box<List<int>>>(),
                ModelAdapterArgument.Create(offsetList));
            var parity = ModelAdapters.WithArguments(ModelAdapters.Value<Models.Box<List<int>>>(),
                ModelAdapterArgument.Create(parityList));
            var left = new Models.Box<List<int>> { value = new List<int> { 1 } };
            var right = new Models.Box<List<int>> { value = new List<int> { 3 } };
            left.next = left;
            right.next = right;
            var comparisons = new EqualityContext();
            Require(parity.Equals(left, right, comparisons), "contextual parity comparison failed");
            Require(!exact.Equals(left, right, comparisons), "parity memoization contaminated exact generic comparison");

            var hashes = new HashContext();
            var exactHash = exact.GetHashCode(left, hashes);
            var offsetHash = offset.GetHashCode(left, hashes);
            Require(exactHash != offsetHash && offsetHash == offset.GetHashCode(left, new HashContext()),
                "generic hash memoization ignored contextual bindings");
            var clones = new CloneContext();
            var exactCopy = exact.Clone(left, clones);
            var offsetCopy = offset.Clone(left, clones);
            Require(!ReferenceEquals(exactCopy, offsetCopy) && exactCopy.value[0] == 1 && offsetCopy.value[0] == 101,
                "clone memoization ignored contextual bindings");
            Require(ReferenceEquals(exactCopy.next, exactCopy) && ReferenceEquals(offsetCopy.next, offsetCopy),
                "scoping generic adapters broke cycle termination");
            Require(!ReferenceEquals(exactCopy.value, left.value) && !ReferenceEquals(offsetCopy.value, left.value),
                "contextual cloning retained original mutable data");

            var equivalent = ModelAdapters.WithArguments(ModelAdapters.Value<Models.Box<List<int>>>(),
                ModelAdapterArgument.Create(ModelAdapters.List(offsetElement)));
            Require(ReferenceEquals(offsetCopy, equivalent.Clone(left, clones)),
                "equivalent bindings did not preserve clone sharing");
            Require(equivalent.GetHashCode(left, hashes) == offsetHash,
                "equivalent bindings did not preserve hash semantics");
            """);
    }

    [Fact]
    public async Task DebuggerSnapshotsNeverMaterializeEnumerateOrFormatImmediateValues()
    {
        await Check("""
            namespace Models
            struct Payload { 0: int32 number; 1: nullable<Holder> owner; }
            struct Holder { 0: bonded<Payload> first; 1: vector<int32> values; }
            struct Derived<T> : Holder { 0: T item; 1: int32 first; }
            """, """
            var wrapper = new CountingBonded(new Models.Payload());
            var source = new Models.Derived<External> { first = 42, item = new External() };
            ((Models.Holder)source).first = wrapper;
            source.values = new ThrowingList();
            var view = new GeneratedModelDebugView(source);
            Require(view.Fields.Length == 4, "debugger did not include hidden inherited fields");
            Require(ReferenceEquals(view.Fields.Single(field => field.DeclaringType == "Models.Holder" && field.Name == "first").Value, wrapper),
                "debugger did not capture the immediate bonded wrapper");
            Require(ReferenceEquals(view.Fields.Single(field => field.Name == "values").Value, source.values), "debugger replaced or inspected the collection");
            Require(wrapper.Reads == 0 && wrapper.Writes == 0, "debugger materialized or serialized");
            var display = (System.Diagnostics.DebuggerDisplayAttribute)Attribute.GetCustomAttribute(typeof(Models.Derived<External>),
                typeof(System.Diagnostics.DebuggerDisplayAttribute), inherit: false);
            Require(display.Value == "Derived", "debugger label evaluates expressions");
            """, CountingBondedSource + """
            public sealed class External
            {
                public override string ToString() => throw new System.Exception("debugger formatted a value");
            }
            public sealed class ThrowingList : System.Collections.Generic.List<int>,
                System.Collections.Generic.IEnumerable<int>
            {
                System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()
                    => throw new System.Exception("debugger enumerated a collection");
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                    => throw new System.Exception("debugger enumerated a collection");
            }
            """);
    }

    [Fact]
    public async Task FieldTypeAndCompanionNameCollisionsRetainInterfaceAndCompanionOperations()
    {
        await Check("""
            namespace Models
            struct Awkward {
                0: int32 Clone;
                1: int32 Equals;
                2: int32 GetHashCode;
                3: int32 Metadata;
                4: int32 Descriptor;
                5: int32 MemberwiseClone;
                6: int32 GetType;
                7: int32 GetDebugFields;
            }
            struct AwkwardOperations {}
            struct AwkwardSchema {}
            struct Clone { 0: int32 value; }
            struct Equals { 0: int32 value; }
            struct GetHashCode { 0: int32 value; }
            struct Generic<Clone, Equals, GetHashCode> { 0: Clone a; 1: Equals b; 2: GetHashCode c; }
            """, """
            var source = new Models.Awkward { Clone = 3, Equals = 4, GetHashCode = 5, Metadata = 6, Descriptor = 7,
                MemberwiseClone = 8, GetType = 9, GetDebugFields = 10 };
            var copy = Models.AwkwardOperations_.Clone(source);
            Require(!ReferenceEquals(source, copy) && copy.Clone == 3 && copy.MemberwiseClone == 8, "colliding clone was not available");
            Require(((IGeneratedEquatable)source).ValueEquals(copy, new EqualityContext()), "explicit value equality was not available");
            Require(!EqualityComparer<Models.Awkward>.Default.Equals(source, copy),
                "default equality changed without a matching overridable hash method");
            Require(Models.AwkwardOperations_.Equals(source, copy), "companion equality was not available");
            Require(Models.AwkwardOperations_.GetHashCode(source) == Models.AwkwardOperations_.GetHashCode(copy), "companion hash was not available");
            Require(((IGeneratedModel)source).Descriptor.Name == "Awkward", "descriptor collision was not handled");
            Require(new GeneratedModelDebugView(source).Fields.Length == 8, "debug helper collision was not handled");
            Require(((ICloneable)source).Clone() is Models.Awkward, "explicit ICloneable was not available");
            Require(Models.CloneOperations.Clone(new Models.Clone()).value == 0, "type-name Clone collision failed");
            Require(Models.EqualsOperations.Equals(new Models.Equals(), new Models.Equals()), "type-name Equals collision failed");
            Require(Models.GetHashCodeOperations.GetHashCode(new Models.GetHashCode()) ==
                Models.GetHashCodeOperations.GetHashCode(new Models.GetHashCode()), "type-name GetHashCode collision failed");
            var generic = new Models.Generic<int, int, int> { a = 1, b = 2, c = 3 };
            Require(Models.GenericOperations<int, int, int>.Equals(generic,
                Models.GenericOperations<int, int, int>.Clone(generic)), "type-parameter collision failed");
            """);
    }

    [Fact]
    public async Task CustomMappedAliasUsesRegisteredTypedOperations()
    {
        var parsed = await ParserFacade.ParseStringAsync("""
            namespace Models
            using ExternalValue = string;
            struct Container { 0: ExternalValue value; 1: vector<ExternalValue> values; }
            """);
        Assert.True(parsed.Success);

        var generated = CSharpGenerator.Generate(parsed.Ast!, "operations.bond", new CSharpGenerationOptions
        {
            ModelFeatures = CSharpModelFeatures.All,
            TypeMappings = ["Models.ExternalValue=global::External"]
        });
        Assert.True(generated.Success, string.Join("\n", generated.Errors.Select(error => error.Message)));

        Run(generated.Code!, """
            var source = new Models.Container { value = new External { Number = 8 } };
            source.values.Add(source.value);
            try { source.Clone(); throw new Exception("mapped mutable type was silently shallow copied"); }
            catch (NotSupportedException) { }
            ModelOperations.RegisterAdapter<External>(new ModelAdapter<External>(
                (value, context) =>
                {
                    if (context.TryGetClone<External>(value, out var found)) return found;
                    var clone = new External { Number = value.Number };
                    context.Register(value, clone);
                    return clone;
                },
                (left, right, context) => left.Number == right.Number,
                (value, context) => value.Number));
            var copy = source.Clone();
            Require(!ReferenceEquals(source.value, copy.value) && ReferenceEquals(copy.value, copy.values[0]),
                "mapped field and container did not use the same typed adapter");
            Require(source.Equals(copy) && source.GetHashCode() == copy.GetHashCode(), "mapped equality disagreed");
            """, """
            public sealed class External { public int Number; }
            namespace Models
            {
                public static class BondTypeAliasConverter
                {
                    public static External Convert(string value, External ignored) => new External();
                }
            }
            """);
    }

    private static async Task Check(string schema, string body, string extraSource = "") =>
        Run(await CSharpGeneratorTests.Generate(schema), body, extraSource);

    private static void Run(string generated, string body, string extraSource)
    {
        var checks = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using BondTools.Models;
            public static class Scenario
            {
                private static void Require(bool condition, string message)
                {
                    if (!condition) throw new Exception(message);
                }
                public static void Run()
                {
            """ + body + """
                }
            }
            """ + extraSource;

        var assembly = CSharpGeneratorTests.Compile(generated, checks);
        try
        {
            assembly.GetType("Scenario", true)!.GetMethod("Run")!.Invoke(null, null);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    private const string CountingBondedSource = """
        public sealed class CountingBonded : global::Bond.IBonded<Models.Payload>
        {
            private readonly Models.Payload payload;
            public int Reads;
            public int Writes;
            public CountingBonded(Models.Payload payload) { this.payload = payload; }
            public Models.Payload Deserialize() { Reads++; return payload; }
            public T Deserialize<T>() => throw new System.Exception("untyped deserialization");
            public global::Bond.IBonded<T> Convert<T>() => throw new System.Exception("unexpected conversion");
            public void Serialize<T>(T writer) { Writes++; throw new System.Exception("unexpected serialization"); }
            public override string ToString() => throw new System.Exception("unexpected bonded formatting");
        }
        """;
}
