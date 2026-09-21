using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;

namespace Bond.Parser.Tests;

public sealed class GeneratedBondedAdapterTests
{
    [Theory]
    [InlineData(CSharpModelFeatures.Cloning)]
    [InlineData(CSharpModelFeatures.All)]
    public async Task MaterializedBondedPayloadsRetainContainerAdaptersForIndependentDeserializations(CSharpModelFeatures features)
    {
        await Check("""
            namespace Models
            struct Box<T> { 0: T value; }
            struct Holder {
                0: bonded<Box<vector<int32>>> payload;
                1: bonded<Box<map<string, vector<int32>>>> nested;
                2: bonded<Box<list<int32>>> linked;
                3: bonded<Box<set<int32>>> flags;
            }
            """, """
            var original = new Models.Box<List<int>> { value = new List<int> { 7, 9 } };
            var source = new Models.Holder
            {
                payload = new Bond.Bonded<Models.Box<List<int>>>(original),
                nested = new Bond.Bonded<Models.Box<Dictionary<string, List<int>>>>(
                    new Models.Box<Dictionary<string, List<int>>>
                    {
                        value = new Dictionary<string, List<int>> { ["data"] = original.value }
                    }),
                linked = new Bond.Bonded<Models.Box<LinkedList<int>>>(
                    new Models.Box<LinkedList<int>> { value = new LinkedList<int>(original.value) }),
                flags = new Bond.Bonded<Models.Box<HashSet<int>>>(
                    new Models.Box<HashSet<int>> { value = new HashSet<int>(original.value) })
            };
            Require(source.payload.Deserialize().value.SequenceEqual(new[] { 7, 9 }), "original payload failed");
            var copy = source.Clone();
            var first = copy.payload.Deserialize();
            var second = ((Bond.IBonded)copy.payload).Deserialize<Models.Box<List<int>>>();
            var captured = ((BondTools.Models.IMaterializedModelValue<Models.Box<List<int>>>)copy.payload).Value;
            Require(first.value.SequenceEqual(new[] { 7, 9 }), "generic vector payload changed");
            Require(!ReferenceEquals(first, second) && !ReferenceEquals(first.value, second.value),
                "repeated deserialization reused a clone context");
            Require(!ReferenceEquals(first, captured) && !ReferenceEquals(first.value, captured.value),
                "deserialization exposed the captured value");
            first.value[0] = 100;
            original.value[1] = 200;
            Require(second.value.SequenceEqual(new[] { 7, 9 }), "deserialized values share mutable storage");
            Require(copy.payload.Deserialize().value.SequenceEqual(new[] { 7, 9 }), "captured payload was mutated");
            Require(copy.Clone().payload.Deserialize().value.SequenceEqual(new[] { 7, 9 }),
                "cloning an already materialized wrapper lost its adapter");
            var nested = copy.nested.Deserialize();
            Require(nested.value["data"].SequenceEqual(new[] { 7, 9 }), "nested map/vector adapters were lost");
            nested.value["data"].Clear();
            Require(copy.nested.Deserialize().value["data"].Count == 2, "nested mutable storage was shared");
            Require(copy.linked.Deserialize().value.SequenceEqual(new[] { 7, 9 }), "linked list adapter was lost");
            Require(copy.flags.Deserialize().value.SetEquals(new[] { 7, 9 }), "set adapter was lost");
            """, features);
    }

    [Fact]
    public async Task NestedMaterializedBondedPayloadsRetainTheirOwnAdapters()
    {
        await Check("""
            namespace Models
            struct Box<T> { 0: T value; }
            struct Holder { 0: bonded<Box<bonded<Box<vector<int32>>>>> payload; }
            """, """
            var inner = new Models.Box<List<int>> { value = new List<int> { 7, 9 } };
            var outer = new Models.Box<Bond.IBonded<Models.Box<List<int>>>>
            {
                value = new Bond.Bonded<Models.Box<List<int>>>(inner)
            };
            var source = new Models.Holder
            {
                payload = new Bond.Bonded<Models.Box<Bond.IBonded<Models.Box<List<int>>>>>(outer)
            };
            var copy = source.Clone();
            var first = copy.payload.Deserialize();
            var second = copy.payload.Deserialize();
            Require(!ReferenceEquals(first.value, second.value), "nested wrappers were shared across deserializations");
            var firstInner = first.value.Deserialize();
            var secondInner = second.value.Deserialize();
            Require(firstInner.value.SequenceEqual(new[] { 7, 9 }), "nested bonded adapter was lost");
            firstInner.value.Clear();
            Require(secondInner.value.SequenceEqual(new[] { 7, 9 }), "nested deserializations shared storage");
            Require(copy.Clone().payload.Deserialize().value.Deserialize().value.SequenceEqual(new[] { 7, 9 }),
                "nested materialized wrappers could not be cloned again");
            """, CSharpModelFeatures.Cloning);
    }

    [Theory]
    [InlineData(CSharpModelFeatures.Cloning)]
    [InlineData(CSharpModelFeatures.All)]
    public async Task OpenGenericBondedPayloadsRetainAmbientAdaptersWithoutSharingCloneContexts(CSharpModelFeatures features)
    {
        await Check("""
            namespace Models
            struct Box<T> { 0: T value; }
            struct Holder<T> { 0: bonded<Box<T>> payload; }
            struct Root {
                0: Holder<vector<int32>> holder;
                1: Holder<map<string, vector<int32>>> nested;
                2: bonded<Holder<vector<int32>>> outer;
            }
            """, """
            var numbers = new List<int> { 7, 9 };
            var holder = new Models.Holder<List<int>>
            {
                payload = new Bond.Bonded<Models.Box<List<int>>>(
                    new Models.Box<List<int>> { value = numbers })
            };
            var source = new Models.Root
            {
                holder = holder,
                nested = new Models.Holder<Dictionary<string, List<int>>>
                {
                    payload = new Bond.Bonded<Models.Box<Dictionary<string, List<int>>>>(
                        new Models.Box<Dictionary<string, List<int>>>
                        {
                            value = new Dictionary<string, List<int>> { ["data"] = numbers }
                        })
                },
                outer = new Bond.Bonded<Models.Holder<List<int>>>(holder)
            };
            Require(source.holder.payload.Deserialize().value.SequenceEqual(new[] { 7, 9 }),
                "original open-generic payload failed");
            var copy = source.Clone();
            var first = copy.holder.payload.Deserialize();
            var second = ((Bond.IBonded)copy.holder.payload).Deserialize<Models.Box<List<int>>>();
            var captured = ((BondTools.Models.IMaterializedModelValue<Models.Box<List<int>>>)copy.holder.payload).Value;
            Require(first.value.SequenceEqual(new[] { 7, 9 }), "ambient vector adapter was lost");
            Require(!ReferenceEquals(first, second) && !ReferenceEquals(first.value, second.value),
                "open-generic deserializations reused graph clones");
            Require(!ReferenceEquals(first, captured) && !ReferenceEquals(first.value, captured.value),
                "open-generic deserialization reused the captured graph");
            first.value[0] = 100;
            numbers[1] = 200;
            Require(second.value.SequenceEqual(new[] { 7, 9 }), "open-generic deserializations shared mutable storage");
            Require(copy.holder.payload.Deserialize().value.SequenceEqual(new[] { 7, 9 }),
                "captured open-generic payload was mutated");

            var repeated = copy.Clone();
            Require(!ReferenceEquals(copy.holder.payload, repeated.holder.payload),
                "cloning a materialized open-generic wrapper reused the wrapper");
            Require(repeated.holder.payload.Deserialize().value.SequenceEqual(new[] { 7, 9 }),
                "re-cloning a materialized open-generic wrapper lost its bindings");
            var nested = copy.nested.payload.Deserialize();
            Require(nested.value["data"].SequenceEqual(new[] { 7, 9 }), "ambient map/vector adapter was lost");
            nested.value["data"].Clear();
            Require(copy.nested.payload.Deserialize().value["data"].Count == 2,
                "ambient nested containers reused graph clones");
            var outer = copy.outer.Deserialize();
            var otherOuter = copy.outer.Deserialize();
            Require(!ReferenceEquals(outer.payload, otherOuter.payload),
                "open-generic nested bonded wrappers reused graph clones");
            Require(outer.payload.Deserialize().value.SequenceEqual(new[] { 7, 9 }),
                "nested open-generic bonded payload lost its bindings");
            Require(repeated.outer.Deserialize().payload.Deserialize().value.SequenceEqual(new[] { 7, 9 }),
                "re-cloning nested open-generic bonded wrappers lost their bindings");
            """, features);
    }

    [Fact]
    public async Task OpenGenericMaterializedBondedCyclesRetainBindingsAndFreshGraphContexts()
    {
        await Check("""
            namespace Models
            struct Box<T> { 0: T value; 1: bonded<Box<T>> next; }
            struct Holder<T> { 0: bonded<Box<T>> payload; }
            struct Root { 0: Holder<vector<int32>> holder; }
            """, """
            var payload = new Models.Box<List<int>> { value = new List<int> { 7, 9 } };
            var deferred = new DirectBonded<Models.Box<List<int>>>(payload);
            payload.next = deferred;
            var source = new Models.Root
            {
                holder = new Models.Holder<List<int>> { payload = deferred }
            };
            var copy = source.Clone();
            var captured = ((BondTools.Models.IMaterializedModelValue<Models.Box<List<int>>>)copy.holder.payload).Value;
            Require(ReferenceEquals(captured.next, copy.holder.payload), "materialized bonded cycle was broken");
            var first = copy.holder.payload.Deserialize();
            var second = copy.holder.payload.Deserialize();
            Require(first.value.SequenceEqual(new[] { 7, 9 }), "cyclic payload lost ambient container bindings");
            Require(ReferenceEquals(first,
                ((BondTools.Models.IMaterializedModelValue<Models.Box<List<int>>>)first.next).Value),
                "deserialization did not preserve the bonded cycle");
            Require(!ReferenceEquals(first, second) && !ReferenceEquals(first.next, second.next)
                && !ReferenceEquals(first.value, second.value), "cyclic deserialization reused graph clones");
            first.value.Clear();
            Require(second.value.SequenceEqual(new[] { 7, 9 }), "cyclic deserializations shared mutable storage");
            var repeated = copy.Clone().holder.payload.Deserialize();
            Require(repeated.value.SequenceEqual(new[] { 7, 9 }), "re-cloning cyclic wrappers lost captured bindings");
            Require(ReferenceEquals(repeated,
                ((BondTools.Models.IMaterializedModelValue<Models.Box<List<int>>>)repeated.next).Value),
                "re-cloning materialized wrappers broke their cycle");
            Require(deferred.Reads == 1, "materialized cycles re-read the original bonded wrapper");
            """, CSharpModelFeatures.Cloning, """
            public sealed class DirectBonded<T> : Bond.IBonded<T>
            {
                private readonly T _value;
                public int Reads { get; private set; }

                public DirectBonded(T value) { _value = value; }

                public T Deserialize()
                {
                    Reads++;
                    return _value;
                }

                public U Deserialize<U>() => throw new System.NotSupportedException();
                public Bond.IBonded<U> Convert<U>() => throw new System.NotSupportedException();
                public void Serialize<W>(W writer) => throw new System.NotSupportedException();
            }
            """);
    }

    private static async Task Check(string schema, string body, CSharpModelFeatures features, string extraSource = "")
    {
        var parsed = await ParserFacade.ParseStringAsync(schema);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));
        var generated = CSharpGenerator.Generate(parsed.Ast!, "input.bond",
            new CSharpGenerationOptions { ModelFeatures = features });
        Assert.True(generated.Success, string.Join("\n", generated.Errors.Select(error => error.Message)));
        var assembly = CSharpGeneratorTests.Compile(generated.Code!, """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public static class Scenario
            {
                private static void Require(bool condition, string message)
                {
                    if (!condition)
                    {
                        throw new Exception(message);
                    }
                }

                public static void Run()
                {
            """ + body + """
                }
            }
            """, extraSource);
        try
        {
            assembly.GetType("Scenario", true)!.GetMethod("Run")!.Invoke(null, null);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }
}
