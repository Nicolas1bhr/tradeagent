using System.Globalization;
using System.Reflection;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BAR READERS TAKE THE HOLDOUT WITH ITS LEDGER (<c>U-bar-holdout</c>): every public read of <see cref="DatasetReader"/>,
/// <see cref="BarFeed"/> and <see cref="ForwardBarStore"/> that serves a bar requires a <see cref="TapeHoldout"/>, as
/// <c>TapeHoldoutTests</c> holds the tape's readers — so a new op that wants bars says who is asking, with the windows of
/// every dataset, before it gets one. The wire half is <c>BarHoldoutOverPipeTests</c>.
/// </summary>
public class BarHoldoutTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A dataset the ledger recorded, hashes and all: <paramref name="bars"/> bars of <paramref name="interval"/> from <see cref="Start"/>.</summary>
    static DatasetRecord Recorded(DatasetStore store, string pair, string version, int bars, string interval = "1m")
    {
        var step = KlineNormaliser.BarLength(interval);
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
            text.Append(CultureInfo.InvariantCulture,
                $"{(Start + i * step).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},100,101,99,100,1.00\n");
        File.WriteAllText(csv, text.ToString());

        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, interval, version, 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start + (bars - 1) * step, 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]));
        return store.ById(id)!;
    }

    /// <summary>
    /// (f) NO BAR READER CAN BE CALLED WITHOUT ITS HOLDOUT. Every public read of the three that serves a bar — anything
    /// carrying a <see cref="KlineBar"/>, a <see cref="ForwardBar"/> or a <see cref="BarFeed"/> — takes a
    /// <see cref="TapeHoldout"/>, and the ones that do not are named here, so one added later fails:
    /// <c>ForwardBarStore.Since</c> and <c>ForwardBarStore.Bar</c>, the paper runner's, the paper source's and the
    /// collector's in-process reads of the present, which the pipe server never calls (its compiled body is read below); and
    /// a feed's own <c>Bars</c> and <c>Chunks</c>, which stream only the window <see cref="BarFeed.Open"/> decided under the
    /// holdout — a wider stream is refused. A caller that hands no holdout is refused before anything is read.
    /// </summary>
    [Fact]
    public void No_bar_reader_can_be_called_without_its_holdout()
    {
        var reads = new List<string>();
        var unguarded = new List<string>();
        foreach (var type in new[] { typeof(DatasetReader), typeof(BarFeed), typeof(ForwardBarStore) })
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => Carries(m.ReturnType, [])))
            {
                reads.Add($"{type.Name}.{m.Name}");
                if (!m.GetParameters().Any(p => p.ParameterType == typeof(TapeHoldout))) unguarded.Add($"{type.Name}.{m.Name}");
            }

        log.WriteLine("reads that serve a bar: " + string.Join(", ", reads.Order(StringComparer.Ordinal)));
        Assert.Contains("DatasetReader.Read", reads);
        Assert.Contains("BarFeed.Open", reads);
        Assert.Contains("ForwardBarStore.Window", reads);
        Assert.Equal(["BarFeed.Bars", "BarFeed.Chunks", "ForwardBarStore.Bar", "ForwardBarStore.Since"],
            unguarded.Distinct().Order(StringComparer.Ordinal));
        Assert.Empty(typeof(BarFeed).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        // THE PIPE SERVER NEVER CALLS THE TWO FORWARD READS THAT TAKE NO HOLDOUT — read off its compiled body, every method
        // and every compiler-made type inside it; and the scan sees the guarded reads it does call.
        var called = Calls(typeof(GatewayPipeServer)).Where(c => c.DeclaringType is { } t && (t == typeof(ForwardBarStore) || t == typeof(DatasetReader)))
            .Select(c => $"{c.DeclaringType!.Name}.{c.Name}").Distinct().Order(StringComparer.Ordinal).ToList();
        log.WriteLine("bar reads the pipe server calls: " + string.Join(", ", called));
        Assert.Contains("ForwardBarStore.Window", called);
        Assert.Contains("DatasetReader.Read", called);
        Assert.DoesNotContain("ForwardBarStore.Since", called);
        Assert.DoesNotContain("ForwardBarStore.Bar", called);

        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var set = Recorded(store, "BTCUSDT", "v1", 120);
        Assert.Throws<ArgumentNullException>(() => DatasetReader.Read(set, null!, null, null));
        Assert.Throws<ArgumentNullException>(() => BarFeed.Open(store, set.Id, null!, null, null));
        Assert.Throws<ArgumentNullException>(() => new ForwardBarStore(db).Window(null!, "BTCUSDT", null, null));

        // A FEED STREAMS THE WINDOW IT WAS OPENED OVER, AND NOTHING WIDER: an absent bound is the opened one.
        var open = BarFeed.Open(store, set.Id, TapeHoldout.Pipe(CouncilRoles.Research, store), Start, Start.AddMinutes(59));
        Assert.True(open.Ok, open.Why);
        Assert.Equal(60, open.Feed!.Bars().Count());
        Assert.Equal(30, open.Feed.Bars(Start.AddMinutes(30)).Count());
        var wider = Assert.Throws<ArgumentOutOfRangeException>(() => open.Feed.Chunks(Start, Start.AddMinutes(119)));
        Assert.Contains("streams inside that window or not at all", wider.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => open.Feed.Bars(Start.AddMinutes(-1)).Count());
    }

    /// <summary>
    /// (d), THE FEED'S HALF: <see cref="BarFeed.Open"/> under the pipe's holdout refuses a 5m dataset's window whose last
    /// bar, opening at 01:00, closes inside a window a 1m cutoff opens at 01:02 — in the bars' words, naming the cutoff's
    /// dataset and the read — and serves the window whose last bar closes at 01:00. The referee's holdout reads both.
    /// </summary>
    [Fact]
    public void A_feed_refuses_a_bar_whose_span_crosses_into_a_window()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var cutoff = Start.AddMinutes(62);
        var held = Recorded(store, "BTCUSDT", "v1", 120);
        Assert.True(store.SetHoldout(held.Id, cutoff, EvaluationClass.Research).Ok);
        var fiveMinute = Recorded(store, "ETHUSDT", "v1", 36, "5m");

        foreach (var role in new[] { CouncilRoles.Research, CouncilRoles.Operations, null })
        {
            var holdout = TapeHoldout.Pipe(role, store);

            var crossing = BarFeed.Open(store, fiveMinute.Id, holdout, Start, Start.AddMinutes(60));
            log.WriteLine(crossing.Why);
            Assert.False(crossing.Ok);
            Assert.True(crossing.IsHoldout);
            Assert.Contains($"dataset {held.Id} (BTCUSDT 1m v1) holds out every bar from {cutoff:u}", crossing.Why, StringComparison.Ordinal);
            Assert.Contains($"dataset {fiveMinute.Id} (ETHUSDT 5m v1) opening from {Start:u} to {Start.AddMinutes(60):u}", crossing.Why, StringComparison.Ordinal);
            Assert.Contains($"to the close of the last at {Start.AddMinutes(65):u}", crossing.Why, StringComparison.Ordinal);
            Assert.Contains($"a 'to' at or before {cutoff.AddMinutes(-5):u}", crossing.Why, StringComparison.Ordinal);

            var clear = BarFeed.Open(store, fiveMinute.Id, holdout, Start, Start.AddMinutes(55));
            Assert.True(clear.Ok, clear.Why);
            Assert.Equal(12, clear.Feed!.Bars().Count());
        }

        var referee = (TapeHoldout)typeof(TapeHoldout).GetProperty("Referee", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var judged = BarFeed.Open(store, fiveMinute.Id, referee, null, null);
        Assert.True(judged.Ok, judged.Why);
        Assert.Equal(36, judged.Feed!.Bars().Count());
    }

    /// <summary>Whether a type carries a bar: the bar itself, a feed of them, a collection or nullable of one, or a record of this product holding one.</summary>
    static bool Carries(Type t, HashSet<Type> seen)
    {
        if (!seen.Add(t)) return false;
        if (t == typeof(KlineBar) || t == typeof(ForwardBar) || t == typeof(BarFeed)) return true;
        if (Nullable.GetUnderlyingType(t) is { } inner) return Carries(inner, seen);
        if (t.IsArray) return Carries(t.GetElementType()!, seen);
        if (t.IsGenericType && t.GetGenericArguments().Any(a => Carries(a, seen))) return true;
        return t.Namespace?.StartsWith("TradeAgent", StringComparison.Ordinal) == true
               && t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => Carries(p.PropertyType, seen));
    }

    static IEnumerable<Type> SelfAndNested(Type t) =>
        new[] { t }.Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(SelfAndNested));

    /// <summary>
    /// Every method a type's compiled bodies call or take the address of — <c>call</c>, <c>callvirt</c>, <c>ldftn</c>,
    /// <c>ldvirtftn</c> — its nested compiler-made types (lambdas, async state machines) included. An operand that does not
    /// resolve to a method is skipped; one that resolves is a real reference. <c>RunTradesTests</c> reads the pipe server's
    /// calls into the strategy ledger with it too (<c>U-run-trace</c>).
    /// </summary>
    internal static IEnumerable<MethodBase> Calls(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var t in SelfAndNested(type))
            foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                if (m.GetMethodBody()?.GetILAsByteArray() is not { } il) continue;
                for (var i = 0; i < il.Length - 4; i++)
                {
                    var at = il[i] is 0x28 or 0x6F ? i + 1 : il[i] == 0xFE && il[i + 1] is 0x06 or 0x07 ? i + 2 : -1;
                    if (at < 0 || at + 4 > il.Length) continue;

                    MethodBase? target = null;
                    try
                    {
                        target = m.Module.ResolveMethod(BitConverter.ToInt32(il, at),
                            t.IsGenericType ? t.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null);
                    }
                    catch (ArgumentException) { }
                    catch (BadImageFormatException) { }
                    catch (TypeLoadException) { }

                    if (target is not null) yield return target;
                }
            }
    }
}
