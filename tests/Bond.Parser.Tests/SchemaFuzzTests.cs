using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Bond.IO.Safe;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Bond.Parser.Tests.Fuzzing;
using BondTools.Runtime;
using static Bond.TestSupport.GeneratedCode;
using UpstreamReader = Bond.Protocols.CompactBinaryReader<Bond.IO.Safe.InputBuffer>;
using UpstreamWriter = Bond.Protocols.CompactBinaryWriter<Bond.IO.Safe.OutputBuffer>;

namespace Bond.Parser.Tests;

/// <summary>
/// Generates random schemas and values, and requires BondTools.Runtime to write the bytes Bond.Runtime.CSharp writes,
/// and to read any payload, valid or mutated, as it does, apart from the differences Bond.Runtime/README.md lists. The
/// seeds are fixed, so every run checks the same cases. Set BOND_FUZZ_SEED and BOND_FUZZ_SCHEMAS to try others, or to
/// reproduce a failure from its seed, and BOND_FUZZ_PROFILE=large for bigger schemas and values, as the nightly run does.
/// </summary>
public sealed class SchemaFuzzTests
{
    private const int ValuesPerStruct = 8;
    private const int MutationsPerValue = 10;

    private static readonly CSharpGenerationOptions Options = new()
    {
        ModelFeatures = CSharpModelFeatures.All,
        Serialization = true
    };

    private static readonly MethodInfo CheckMethod =
        typeof(SchemaFuzzTests).GetMethod(nameof(Check), BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public async Task RandomSchemasReadAndWriteAsUpstreamDoes()
    {
        var first = Setting("BOND_FUZZ_SEED", 1);
        var count = Setting("BOND_FUZZ_SCHEMAS", 200);
        var large = Environment.GetEnvironmentVariable("BOND_FUZZ_PROFILE") == "large";
        var failures = new ConcurrentQueue<(string Kind, string Report)>();
        var checkedStructs = 0;

        // A compilation costs about as much as checking a few small schemas, so they're compiled together.
        await Parallel.ForEachAsync(Enumerable.Range(first, count).Chunk(large ? 10 : 50), async (seeds, token) =>
        {
            var batch = new List<(int Seed, RandomSchema Schema, string Code)>();
            foreach (var seed in seeds)
            {
                var schema = new RandomSchema(seed, large);
                var parsed = await ParserFacade.ParseStringAsync(schema.Text);
                var generated = parsed.Success ? CSharpGenerator.Generate(parsed.Ast!, "fuzz.bond", Options) : null;
                if (generated is not { Success: true })
                {
                    var errors = generated?.Errors ?? parsed.Errors;
                    failures.Enqueue(("generation failed",
                        Report(seed, schema, "generation failed: " + string.Join("; ", errors.Select(e => e.Message)))));
                    continue;
                }

                batch.Add((seed, schema, generated.Code!));
            }

            // One compilation for the batch, and one per schema only to find the schema that broke it.
            var assembly = TryCompile(batch.Select(item => item.Code).ToArray(), out _);
            foreach (var (seed, schema, code) in batch)
            {
                var diagnostics = "";
                var compiled = assembly ?? TryCompile([code], out diagnostics);
                if (compiled == null)
                {
                    failures.Enqueue(("generated code doesn't compile", Report(seed, schema, "generated code doesn't compile:\n" + diagnostics)));
                    continue;
                }

                foreach (var (root, index) in schema.Roots.Select((root, index) => (root, index)))
                {
                    var type = compiled.GetType($"{schema.Namespace}.{root}", throwOnError: true)!;
                    try
                    {
                        OnLargeStack(() => CheckMethod.MakeGenericMethod(type).Invoke(null, [new Random(seed * 31 + index), large]));
                        Interlocked.Increment(ref checkedStructs);
                    }
                    catch (TargetInvocationException error)
                    {
                        failures.Enqueue((KindOf(error.InnerException!), Report(seed, schema, $"{root}: {error.InnerException}")));
                    }
                }
            }
        });

        var kinds = failures.GroupBy(failure => failure.Kind).OrderByDescending(kind => kind.Count()).ToList();
        Assert.True(failures.IsEmpty, $"{failures.Count} failures:\n" +
            string.Join("\n", kinds.Select(kind => $"{kind.Count(),6} {kind.Key}")) + "\n\nThe first of each kind:\n\n" +
            string.Join("\n\n", kinds.Take(10).Select(kind => kind.First().Report)));
        Assert.True(checkedStructs >= count, $"only {checkedStructs} structs were checked");
    }

    // Upstream's deserializers take kilobytes of stack per level, so a value some 20 levels deep overflows a thread pool
    // thread's stack, which ends the test run, before upstream's own limit of 64 levels rejects it.
    private static void OnLargeStack(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        }, maxStackSize: 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (error != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    // Writes and reads random values of a struct with both runtimes, which must agree on every byte, then mutates the
    // bytes, which both must reject or read alike. Clear(), Equals() and reading into an instance must agree with plain
    // reads and writes.
    private static void Check<T>(Random random, bool large) where T : class, IBondStruct<T>, new()
    {
        var values = new RandomValues(random, large);
        var clear = typeof(T).GetMethod("Clear", Type.EmptyTypes)!;
        var reused = new T();
        for (var i = 0; i < ValuesPerStruct; i++)
        {
            var value = values.Create<T>();
            var bytes = value.Serialize();
            Same(Upstream.Write(value), bytes, "upstream writes different bytes", bytes);
            _ = value.ToString();

            // A value nested deeper than we read, which upstream, counting more levels, must reject too.
            T read;
            try
            {
                read = T.Deserialize(bytes);
            }
            catch (InvalidDataException error) when (TooDeep(error))
            {
                Require(Rejects(() => Upstream.Read<T>(bytes), TooDeepForUpstream), "only BondTools.Runtime rejects a deep payload", bytes);
                continue;
            }

            Same(read.Serialize(), bytes, "writing what was read gives different bytes", bytes);
            // Upstream may reject what we read, when it counts over 64 levels; whatever it reads must match.
            Rejects(() => SameValue(read, Upstream.Read<T>(bytes), typeof(T).Name, "upstream reads a different value", bytes),
                TooDeepForUpstream);

            var again = T.Deserialize(bytes);
            Require(read.Equals(again) && read.GetHashCode() == again.GetHashCode(), "two reads of the same bytes aren't equal", bytes);

            clear.Invoke(reused, null);
            Same(reused.Serialize(), new T().Serialize(), "Clear() doesn't reset to the defaults", bytes);
            Same(T.Deserialize(bytes, reused).Serialize(), bytes, "reading into a cleared instance gives a different value", bytes);

            for (var m = 0; m < MutationsPerValue; m++)
            {
                Agree<T>(Mutate(bytes, random));
            }
        }
    }

    private static void Agree<T>(byte[] payload) where T : class, IBondStruct<T>
    {
        T? ours = null;
        Exception? rejection = null;
        try
        {
            ours = T.Deserialize(payload);
        }
        catch (Exception error) when (error is EndOfStreamException or InvalidDataException)
        {
            rejection = error;
        }

        T? theirs = null;
        Exception? theirRejection = null;
        try
        {
            theirs = Upstream.Read<T>(payload);
        }
        catch (Exception error)
        {
            // Upstream throws all sorts of exceptions on malformed payloads; that it rejects them is what matters.
            theirRejection = error;
        }

        if (ours == null && theirs != null)
        {
            if (UpstreamMisses(rejection!))
            {
                return;
            }

            throw new FuzzFailure("only upstream accepts a mutated payload", $", which we reject with: {rejection!.Message}", payload);
        }

        if (ours != null && theirs == null && !TooDeepForUpstream(theirRejection!))
        {
            throw new FuzzFailure("only BondTools.Runtime accepts a mutated payload", "", payload);
        }

        if (ours != null && theirs != null)
        {
            SameValue(ours, theirs, typeof(T).Name, "the runtimes read different values from a mutated payload", payload);
        }
    }

    // Both reject payloads nested deeper than 64 levels, but upstream counts containers, nullables and bonded values too.
    private static bool TooDeep(Exception error) =>
        error.Message.StartsWith("Bond payload is nested deeper than", StringComparison.Ordinal);

    private static bool TooDeepForUpstream(Exception error) =>
        error is InvalidDataException && error.Message.StartsWith("Recursion depth exceeded", StringComparison.Ordinal);

    private static bool Rejects(Action action, Func<Exception, bool> because)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception error) when (because(error))
        {
            return true;
        }
    }

    // Compares what the runtimes read: strings ordinally, floating point numbers bit by bit, containers in order,
    // structs field by field, and bonded values as upstream transcodes them.
    private static void SameValue(object? ours, object? theirs, string path, string message, byte[] payload)
    {
        var same = (ours, theirs) switch
        {
            (null, _) or (_, null) => ours == theirs,
            (string text, string other) => string.Equals(text, other, StringComparison.Ordinal),
            (double number, double other) => BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits(other),
            (float number, float other) => BitConverter.SingleToInt32Bits(number) == BitConverter.SingleToInt32Bits(other),
            (ArraySegment<byte> blob, ArraySegment<byte> other) => (blob.Array == null) == (other.Array == null)
                && blob.AsSpan().SequenceEqual(other),
            (global::Bond.IBonded bonded, global::Bond.IBonded other) => SameBonded(bonded, other),
            (IDictionary map, IDictionary other) => SameItems(Entries(map), Entries(other), path, message, payload),
            (IEnumerable items, IEnumerable other) => SameItems(items.Cast<object?>(), other.Cast<object?>(), path, message, payload),
            _ when ours.GetType().IsPrimitive || ours.GetType().IsEnum => ours.Equals(theirs),
            _ => ours.GetType() == theirs.GetType()
        };
        if (!same)
        {
            throw new FuzzFailure(message, $": {Describe(ours)} instead of {Describe(theirs)} at {path}", payload);
        }

        if (ours != null && ours.GetType().IsDefined(typeof(global::Bond.SchemaAttribute), inherit: false))
        {
            foreach (var property in ours.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.IsDefined(typeof(global::Bond.IdAttribute))))
            {
                SameValue(property.GetValue(ours), property.GetValue(theirs), $"{path}.{property.Name}", message, payload);
            }
        }
    }

    private static bool SameItems(IEnumerable<object?> ours, IEnumerable<object?> theirs, string path, string message,
        byte[] payload)
    {
        var (left, right) = (ours.ToList(), theirs.ToList());
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            SameValue(left[i], right[i], $"{path}[{i}]", message, payload);
        }

        return true;
    }

    private static IEnumerable<object?> Entries(IDictionary map)
    {
        var entries = map.GetEnumerator();
        while (entries.MoveNext())
        {
            yield return entries.Key;
            yield return entries.Value;
        }
    }

    // Payloads upstream accepts but shouldn't: a bonded field of another wire type, which it reads as a struct, and one
    // that lacks a required field after a field it skipped. Struct fields of another wire type both reject.
    private static bool UpstreamMisses(Exception rejection) =>
        rejection.Message.EndsWith(", expected Struct.", StringComparison.Ordinal)
        || rejection.Message.StartsWith("Required field ", StringComparison.Ordinal);

    // Read bonded values deserialize when transcoded. Upstream's keep reading into the rest of the input, while ours
    // were copied up to the end of their struct, so only ours run out of data, or reject what upstream misses; only
    // upstream counts their containers as levels.
    private static bool SameBonded(global::Bond.IBonded ours, global::Bond.IBonded theirs)
    {
        var (oursBytes, oursError) = Transcode(ours);
        var (theirsBytes, theirsError) = Transcode(theirs);
        return oursError != null && theirsError == null && (oursError is EndOfStreamException || UpstreamMisses(oursError))
            || oursBytes != null && theirsBytes != null && oursBytes.AsSpan().SequenceEqual(theirsBytes)
            || oursError != null && theirsError != null
            || theirsError != null && TooDeepForUpstream(theirsError);
    }

    private static (byte[]? Bytes, Exception? Error) Transcode(global::Bond.IBonded bonded)
    {
        try
        {
            var output = new OutputBuffer();
            bonded.Serialize(new UpstreamWriter(output));
            return (output.Data.ToArray(), null);
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => "\"" + string.Concat(text.Select(c => c is >= ' ' and < '\x7F' ? c.ToString() : $"\\u{(int)c:X4}")) + "\"",
        IEnumerable items and not string => $"{items.Cast<object>().Count()} items",
        _ => value.ToString() ?? ""
    };

    private static byte[] Mutate(byte[] bytes, Random random)
    {
        var mutated = new List<byte>(bytes);
        for (var edit = random.Next(1, 4); edit > 0 && mutated.Count > 0; edit--)
        {
            var at = random.Next(mutated.Count);
            switch (random.Next(5))
            {
                case 0: mutated.RemoveRange(at, mutated.Count - at); break;
                case 1: mutated[at] ^= (byte)(1 << random.Next(8)); break;
                case 2: mutated[at] = (byte)random.Next(256); break;
                case 3: mutated.Insert(at, (byte)random.Next(256)); break;
                default: mutated.RemoveAt(at); break;
            }
        }

        return mutated.ToArray();
    }

    private static void Same(byte[] actual, byte[] expected, string message, byte[] payload)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            var at = actual.AsSpan().CommonPrefixLength(expected);
            throw new FuzzFailure(message, $": {Hex(actual, at)} instead of {Hex(expected, at)} at byte {at}", payload);
        }
    }

    private static void Require(bool condition, string message, byte[] payload)
    {
        if (!condition)
        {
            throw new FuzzFailure(message, "", payload);
        }
    }

    private static string Hex(byte[] bytes, int at) =>
        Convert.ToHexString(bytes.AsSpan(at, Math.Min(16, bytes.Length - at))) + (bytes.Length - at > 16 ? "…" : "");

    // Groups failures: a check's own by what it checks, and others by exception and message, without their numbers.
    private static string KindOf(Exception error) => error is FuzzFailure failure
        ? failure.Kind
        : System.Text.RegularExpressions.Regex.Replace($"{error.GetType().Name}: {error.Message.Split('\n')[0]}", @"\d+", "#");

    private static string Report(int seed, RandomSchema schema, string message) =>
        $"Seed {seed} ({schema.Namespace}): {message}\n{schema.Text}";

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private sealed class FuzzFailure(string kind, string details, byte[] payload) : Exception(
        $"{kind}{details}\npayload: {Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 512)))}{(payload.Length > 512 ? "…" : "")}")
    {
        public string Kind => kind;
    }

    private static class Upstream
    {
        public static byte[] Write<T>(T value)
        {
            var output = new OutputBuffer();
            global::Bond.Serialize.To(new UpstreamWriter(output), value);
            return output.Data.ToArray();
        }

        public static T Read<T>(byte[] bytes) => global::Bond.Deserialize<T>.From(new UpstreamReader(new InputBuffer(bytes)));
    }
}
