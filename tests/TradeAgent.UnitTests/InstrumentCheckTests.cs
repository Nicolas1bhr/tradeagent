using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE APP CHECKS AN INSTRUMENT AGAINST THE VENUE'S OWN PUBLISHED DEFINITION — `U-venue-verify`.
///
/// <para>Before this unit the only route to a VERIFIED instrument was a hand-edited <c>venues.json</c>
/// that no screen writes, and the shipped BTCUSDT row is unverified: the paper connector offered nothing,
/// the runner sized nothing and the cost model refused the campaign press. After it the app reads the
/// venue's definition from the BUILT-IN origin, records every attempt in <c>instrument_check</c>, and
/// serves the venue's numbers for seven days to every reader of the catalogue.</para>
///
/// <para>Every test here talks to a loopback <see cref="FakeArchive"/> serving canned exchangeInfo JSON;
/// none reaches a vendor, which <see cref="SuiteReachesNoVendorTests"/> holds the whole tree to.</para>
/// </summary>
public class InstrumentCheckTests(ITestOutputHelper log)
{
    /// <summary>The rung. Renumbered with the rung if <c>main</c> gains this number first: the ladder stays contiguous.</summary>
    const int CheckRung = 29;

    static readonly DateTimeOffset T0 = new(2026, 10, 3, 4, 48, 54, TimeSpan.Zero);

    static InstrumentCheckAttempt Attempt(string outcome, string url = "https://127.0.0.1:1/api/v3/exchangeInfo?symbol=BTCUSDT") => new()
    {
        VenueId = VenueCatalog.BinanceSpot,
        Symbol = "BTCUSDT",
        Url = url,
        RequestedAt = T0,
        ReceivedAt = T0.AddMilliseconds(300),
        Outcome = outcome
    };

    // ---- the rung ------------------------------------------------------------------------------------

    /// <summary>
    /// THE CHECK LEDGER ARRIVES AT ITS RUNG, EMPTY. A FLOOR and not the build's number, for the reason
    /// <c>BacktestLedgerTests</c> gives; the stamp on disk is still required to equal what this build
    /// writes, so an upgrade that did not run is caught. The roll-back lists in <c>VenueCatalogTests</c>,
    /// <c>PaperEligibleVerdictTests</c> and <c>OrgLedgerTests</c> drop the table, so each still reopens a
    /// database that predates it.
    /// </summary>
    [Fact]
    public void The_instrument_check_arrives_at_its_rung_empty()
    {
        using var db = TestEnv.NewDb();

        Assert.True(Versions.DatabaseSchemaVersion >= CheckRung,
            $"the instrument check needs schema {CheckRung} or later; this build says "
            + Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), Scalar(db,
            "SELECT value FROM meta WHERE key='schema_version'"));
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='instrument_check'"));
        Assert.Equal("0", Scalar(db, "SELECT COUNT(*) FROM instrument_check"));
    }

    /// <summary>
    /// A ROW IS WHAT HAPPENED, AND THE STORE REFUSES WHAT CANNOT HAVE. A verified attempt carries a tick
    /// size and a step above zero — a step of nothing would size every position to nothing — and an
    /// attempt of any other outcome carries no numbers at all; an outcome that is not one of the three
    /// words is refused. The ORIGIN is the store's reading of the URL, never the caller's.
    /// </summary>
    [Fact]
    public void A_check_row_is_what_happened_and_the_store_refuses_what_cannot_have()
    {
        using var db = TestEnv.NewDb();
        var store = new InstrumentCheckStore(db);

        Assert.Throws<ArgumentException>(() => store.Append(Attempt(InstrumentCheckOutcome.Verified)));
        Assert.Throws<ArgumentException>(() => store.Append(Attempt(InstrumentCheckOutcome.Verified) with
        {
            TickSize = 0.01m, QuantityIncrement = 0m
        }));
        Assert.Throws<ArgumentException>(() => store.Append(Attempt(InstrumentCheckOutcome.Failed) with
        {
            QuantityIncrement = 0.00001m
        }));
        Assert.Throws<ArgumentException>(() => store.Append(Attempt("trusted")));
        Assert.Equal("0", Scalar(db, "SELECT COUNT(*) FROM instrument_check"));

        var failed = store.Append(Attempt(InstrumentCheckOutcome.Failed, "https://127.0.0.1:443/x?symbol=BTCUSDT") with
        {
            HttpStatus = 503, Note = "the host answered 503 and no definition was read"
        });
        var verified = store.Append(Attempt(InstrumentCheckOutcome.Verified, "http://127.0.0.1:8080/x?symbol=BTCUSDT") with
        {
            HttpStatus = 200, BodySha256 = new string('a', 64),
            TickSize = 0.01m, QuantityIncrement = 0.00001m, MinQuantity = 0.00001m, MinNotional = 5m
        });

        Assert.Equal("https://127.0.0.1", failed.Origin);
        Assert.Equal("http://127.0.0.1:8080", verified.Origin);
        Assert.Equal(verified, store.Latest(VenueCatalog.BinanceSpot, "BTCUSDT"));
        Assert.Equal(verified, Assert.Single(store.LatestVerified()));
        Assert.Equal(0.00001m, verified.QuantityIncrement);
        Assert.Equal(5m, verified.MinNotional);
        Assert.Equal(failed, store.ById(failed.Id));
    }

    // ---- (f) no op writes it -------------------------------------------------------------------------

    /// <summary>
    /// (f) NO PIPE OP WRITES AN INSTRUMENT CHECK — a GUARD, green before this unit as after it, in the shape
    /// of <c>OrgLedgerTests.No_pipe_op_writes_an_org_table</c>.
    ///
    /// <para><b>Four layers, because a name is the weakest of them.</b> No op and no <c>trade</c> verb names
    /// a check, a verification or a definition, and no op takes an argument that would address one. The
    /// store's public surface is its one append and three reads. No source file but the store writes the
    /// table with SQL, so no handler can do it by hand. And the pipe server's own assembly does not even
    /// reference the assembly the verifier lives in, so no handler can start a check: an agent that could
    /// record its own instrument definition could choose the step its sizes are rounded to.</para>
    /// </summary>
    [Fact]
    public void No_pipe_op_writes_an_instrument_check()
    {
        string[] words = ["verif", "check", "definition", "exchangeinfo", "exchange-info"];
        foreach (var op in GatewaySchema.Ops())
        {
            foreach (var word in words)
            {
                Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, op.Cli, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var arg in op.Args)
                foreach (var word in words.Concat(["url", "tick", "notional"]))
                    Assert.DoesNotContain(word, arg.Name, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            ["Append", "ById", "Latest", "LatestVerified"],
            typeof(InstrumentCheckStore)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());

        var root = RepoRoot();
        var writes = new Regex(
            @"\b(INSERT\s+(OR\s+\w+\s+)?INTO|REPLACE\s+INTO|UPDATE|DELETE\s+FROM|DROP\s+TABLE|ALTER\s+TABLE)\s+instrument_check\b",
            RegexOptions.IgnoreCase);
        var writers = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => writes.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        log.WriteLine(string.Join(", ", writers));
        Assert.Equal(["src/TradeAgent.Core/Db/InstrumentCheckStore.cs"], writers);

        var pipe = typeof(GatewayPipeServer).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        log.WriteLine(string.Join(", ", pipe.Where(n => n.StartsWith("TradeAgent.", StringComparison.Ordinal))));
        Assert.DoesNotContain("TradeAgent.Provisioning", pipe);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    static string? Scalar(Database db, string sql) => db.Read(_ =>
    {
        using var c = db.Cmd(sql);
        return Convert.ToString(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });
}
