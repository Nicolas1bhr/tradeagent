using System.Runtime.CompilerServices;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;

namespace TradeAgent.Tests;

/// <summary>
/// Every test assembly redirects TRADEAGENT_HOME into a scratch directory before anything touches
/// <see cref="Paths"/>, so tests can never read or write the real installation.
///
/// <para><b>And deletes it when its run is over</b> — every test finished, the result not yet
/// reported — through <see cref="TestHomeFramework"/>, and again at process exit should that step
/// never have run. Nothing deleted a home before: 34 GB of them had piled up on the dev Mac by
/// 2026-10-04. Best effort and silent: a file a child process still holds on Windows stays where it
/// is, because nothing about cleaning up may turn a run red.</para>
///
/// <para><b><c>TA_TEST_KEEP_HOME=1</c> keeps it</b> (<see cref="KeepVariable"/>), for a person to read
/// what a run wrote. Its path is then printed once, on the test host's output — which
/// <c>dotnet test</c> shows at <c>--logger "console;verbosity=normal"</c> and hides at its default.</para>
/// </summary>
public static class TestEnv
{
    /// <summary>
    /// Where every test process makes its home, and the only directory this harness deletes anything
    /// in. See <see cref="DeleteQuietly"/>.
    /// </summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "tradeagent-tests");

    public static string Home { get; private set; } = "";

    /// <summary>Keeps this process's home when set to anything but empty or <c>0</c>.</summary>
    public const string KeepVariable = "TA_TEST_KEEP_HOME";

    /// <summary>Whether <see cref="KeepVariable"/> asked for the home to be kept.</summary>
    public static bool Keep { get; } = Environment.GetEnvironmentVariable(KeepVariable) is { Length: > 0 } and not "0";

    [ModuleInitializer]
    public static void Init()
    {
        Home = Path.Combine(Root, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Home);
        if (Keep) Console.WriteLine($"{KeepVariable} is set: this test process's home is kept at {Home}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteHome();
        Environment.SetEnvironmentVariable("TRADEAGENT_HOME", Home);
        Environment.SetEnvironmentVariable("TRADEAGENT_PIPE", "ta-test-" + Guid.NewGuid().ToString("n")[..12]);

        // ONE CHAIR'S LAUNCH GRANT FOR THE WHOLE ASSEMBLY, because in the product every process that
        // dials this pipe IS a launch TradeAgent started and minted a grant for. A test assembly has
        // no AgentSession doing that, so it stands in for one: the register is the process-wide one
        // the pipe server checks, and the environment variable is where a launched process finds it
        // — which is also how a `trade` child process started by a test gets it, by inheritance.
        //
        // Tests that are ABOUT the grant build their own register and speak the wire themselves, so
        // this cannot make one of them pass.
        Chair = AgentGrants.Shared.Issue(CouncilRoles.Operations, "test-attempt");
        Environment.SetEnvironmentVariable(AgentGrants.Variable, Chair.Token);
    }

    /// <summary>The assembly's stand-in for a launch of the Operations Director. See <see cref="Init"/>.</summary>
    public static AgentGrant? Chair { get; private set; }

    public static Database NewDb() => new(Path.Combine(Home, $"db-{Guid.NewGuid():n}.db"));

    /// <summary>
    /// A PAIR OF ITS OWN, FOR ONE TEST THAT COLLECTS THROUGH <c>MarketDataService</c> — never a constant.
    ///
    /// <para><b>The home is the process's</b> (<see cref="Init"/>), so <c>Paths.Data</c> is too, while the
    /// ledger is the test's (<see cref="NewDb"/>). A collection writes into folders keyed by the pair — the
    /// vendor's own files under <c>raw/</c> and the dataset file beside them — so two tests that share a
    /// pair share those folders while each ledger believes it is alone in them. Paid for twice:
    /// <c>CandleSourceTests</c> and <c>DatasetLedgerTests</c> once wrote part files into one directory and
    /// deleted each other's, and on windows-latest (run 37421444199) <c>DataLicenceTests</c>' rebuild met a
    /// raw file <c>DatasetLedgerTests</c> alters on purpose. A pair per CLASS is not enough: xUnit runs
    /// classes in parallel, and two tests of one class still share whatever the first one left.</para>
    ///
    /// <para>The default is valid for <c>BinanceArchive.IsPair</c> and <c>DataCandleSource.RequireSymbol</c>
    /// alike — upper-case letters and digits, seventeen of them; a <paramref name="quote"/> with a hyphen
    /// (<c>-USD</c>) spells it as venues outside Binance do, which only the second accepts. The normalised
    /// file's bytes do not depend on the pair — they hold instants and prices — so a hash pinned over them
    /// is the same whichever pair a test takes.</para>
    /// </summary>
    public static string NewPair(string quote = "USDT") =>
        $"X{Guid.NewGuid():N}"[..13].ToUpperInvariant() + quote;

    static int _homeDeleted;

    /// <summary>
    /// DELETES THIS PROCESS'S HOME, once, unless <see cref="Keep"/> says otherwise. Called at the end
    /// of the assembly's run (<see cref="TestHomeFramework"/>) and at process exit. Never throws.
    /// </summary>
    internal static void DeleteHome()
    {
        if (Keep || Interlocked.Exchange(ref _homeDeleted, 1) == 1) return;
        DeleteQuietly(Home);
    }

    /// <summary>
    /// Deletes a directory under <see cref="Root"/>, and refuses anything else rather than deleting it
    /// — <see cref="Home"/> before <see cref="Init"/> has run is empty, and an empty path must never
    /// become a deletion somewhere else. Best effort: never throws.
    /// </summary>
    public static void DeleteQuietly(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.Ordinal) || full.Length == root.Length) return;
            Directory.Delete(full, recursive: true);
        }
        catch (Exception) { /* a file still held — by a child process on Windows — stays where it is */ }
    }

    /// <summary>
    /// A FRESH DIRECTORY FOR ONE TEST, inside this process's home, deleted when the test lets go of it
    /// — after its asserts, whether they passed or not, which is what a <c>using</c> is for — unless
    /// <see cref="Keep"/> keeps the home it is in.
    /// </summary>
    public static ScratchDir NewScratch(string name) => new(Path.Combine(Home, $"{name}-{Guid.NewGuid():n}"));

    /// <summary>See <see cref="NewScratch"/>.</summary>
    public sealed class ScratchDir(string dir) : IDisposable
    {
        public string Dir { get; } = dir;

        public void Dispose()
        {
            if (!Keep) DeleteQuietly(Dir);
        }
    }

    /// <summary>
    /// TODAY'S LOCAL NOON, AS ONE INSTANT: what a test that records something and then reads "today"
    /// pins BOTH to — the meter's <c>now</c> seam, the turns it records, the window it reads back.
    ///
    /// <para>Two clock reads straddle midnight once a day, and a turn's row starts BEFORE the instant it
    /// is recorded at, by its duration (12.5 s for the measured Codex turn). Run 37163465037 on main
    /// <c>5a54452</c> crossed 00:00Z and six meter tests recorded yesterday and summed today: red on a
    /// product that was right. Noon is as far from both midnights as a local day allows, and the offset
    /// is the one in force AT noon, so a day either side of a daylight-saving change is read whole.</para>
    /// </summary>
    public static DateTimeOffset LocalNoon()
    {
        var noon = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(noon, TimeZoneInfo.Local.GetUtcOffset(noon));
    }

    /// <summary>
    /// THE INSTRUMENTS THIS SUITE TRADES. An empty allowlist used to mean "everything" and now means
    /// nothing (REVIEW 2026-09-05 finding 5), so a gateway a test has configured to trade has to
    /// name what it may trade — exactly as a configured installation does. Spelled once so that a
    /// test that starts using a new symbol adds it here rather than quietly widening one setup.
    /// </summary>
    public static readonly string[] Instruments = ["ES", "NQ", "MES", "YM", "XYZ"];

    /// <summary>
    /// A CANDLE SOURCE NO READING HAS EVER BEEN TAKEN ABOUT (<c>U-data-licence</c>), for the fixtures whose
    /// promoted version is given LIVE capital. Since schema 30 the live gate refuses a version whose evidence
    /// does not confer — the Binance archive's datasets read research-only — so a test about the capital gate
    /// itself records its evidence <see cref="FirstParty"/> under this source: with no reading for the source,
    /// the row's own class is the whole of the licence check, and every other guard on the live path — the
    /// <c>IsPromoted</c> mutants above all — is still the one that answers.
    /// </summary>
    public const string FirstPartySource = "test-first-party-klines";

    /// <summary>The licence those fixtures record: <c>first-party</c>, one of the two classes that confer.</summary>
    public static readonly Core.Data.DatasetLicence FirstParty =
        new(Core.Data.DataLicence.FirstParty, "recorded by this test itself", "test fixture", "2026-10-04");

    /// <summary>
    /// A gateway wired to a fresh simulator, already healthy and allowed to trade.
    ///
    /// <para><paramref name="emergencyBudget"/> is the simulator's whole risk-reducing operation
    /// budget, and it is a REAL wall clock (<see cref="Environment.TickCount64"/>) rather than
    /// anything a test's <c>TimeProvider</c> can move. Null keeps the simulator's shipped two
    /// seconds; a fixture whose verdict is not ABOUT that budget passes a generous one, for the
    /// reason <c>SweepRequestIdTests.SweepBudget</c> states at length — inside it are durable SQLite
    /// commits at <c>synchronous=FULL</c>, which is a clock kept by the runner's disk.</para>
    /// </summary>
    public static async Task<(TradingGateway Gw, FakeConnector Conn, Database Db)> Ready(
        Action<TradeAgentSettings>? settings = null, GatewayOptions? options = null, FaultProfile? faults = null,
        TimeSpan? emergencyBudget = null)
    {
        var db = NewDb();
        var conn = emergencyBudget is { } budget
            ? new FakeConnector(new FakeBroker(), faults) { EmergencyBudget = budget }
            : new FakeConnector(new FakeBroker(), faults);

        // THE SIMULATOR'S PRICES ON THE CLOCK THE GATEWAY IS GIVEN. The gateway measures a quote's age
        // on its own clock (U-runner-forward), so a fixture that substitutes that clock stamps the
        // quotes on it too — or a quote is months old, or months in the future, by accident.
        if (options is not null) conn.QuoteClock = options.Clock;

        var gw = new TradingGateway(db, conn, new HealthRegistry(), options);
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            // "ALLOWED TO TRADE" NOW HAS TO NAME WHAT IT MAY TRADE. An empty allowlist used to mean
            // "everything", which is the defect REVIEW 2026-09-05 finding 5 turned into a re-armed
            // AI; it now allows nothing, so a configured installation — which is what this helper
            // stands in for — lists its instruments.
            s.Risk.InstrumentAllowlist = [.. Instruments];
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    public static PlaceIntent Buy(string symbol = "ES", decimal qty = 1m) =>
        new(symbol, ConnectorSdk.OrderSide.Buy, ConnectorSdk.OrderType.Market, qty, null, null, ConnectorSdk.TimeInForce.Day, null);
}
