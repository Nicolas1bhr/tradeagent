using System.Security.Cryptography;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE VERIFIED CLASSES' MODEL CATALOG (<c>U-cell-model-catalog</c>; the card of <c>U-cli-code-mode</c>): a broker or cell seat is offered
/// the tools its dry run verified, whatever the account lists — because the model's metadata both read is one shipped file, pinned.
/// </summary>
public class ModelCatalogTests
{
    static SeatPlan CodexSeat() => RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex").Seat!;

    /// <summary>
    /// (1) THE DATA AND ITS PIN: the shipped catalog hashes to the pin the codex seat plan compiles, and its one model is the broker and
    /// cell classes' pinned model. RED before the file existed (the read of the shipped path failed).
    /// </summary>
    [Fact]
    public void The_shipped_catalog_hashes_to_its_pin_and_holds_only_the_pinned_model()
    {
        var seat = CodexSeat();
        var source = SeatProfile.CatalogSourcePath(seat);
        Assert.NotNull(source);
        Assert.True(File.Exists(source), $"the shipped model catalog is not at {source}");

        var bytes = File.ReadAllBytes(source!);
        Assert.Equal(seat.ModelCatalogSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        using var doc = JsonDocument.Parse(bytes);
        var slugs = doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("slug").GetString()).ToList();
        Assert.Equal([BrokerSeat.Model], slugs);

        var (shipped, refusal) = SeatProfile.ShippedCatalog(seat);
        Assert.Null(refusal);
        Assert.Equal(bytes, shipped);
    }

    /// <summary>(1) A shipped catalog that hashes otherwise, or is missing, is refused in words naming the file — never written.</summary>
    [Fact]
    public void A_catalog_that_hashes_otherwise_or_is_missing_is_refused_in_words()
    {
        var seat = CodexSeat();
        seat.ModelCatalogSha256 = new string('0', 64);
        var (bytes, refusal) = SeatProfile.ShippedCatalog(seat);
        Assert.Null(bytes);
        Assert.Contains(SeatProfile.CatalogSourcePath(seat)!, refusal);
        Assert.Contains(new string('0', 64), refusal);
        Assert.Contains(CodexSeat().ModelCatalogSha256, refusal);

        seat.ModelCatalogSource = "SeatData/no-such-catalog.json";
        (bytes, refusal) = SeatProfile.ShippedCatalog(seat);
        Assert.Null(bytes);
        Assert.Contains("no-such-catalog.json", refusal);
    }

    /// <summary>A seat plan whose home is this test's own folder.</summary>
    static RuntimeManifest CodexIn(string name)
    {
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        codex.Seat!.Home = Path.Combine(TestEnv.Home, "model-catalog-" + name + "-" + Guid.NewGuid().ToString("n")[..8], "seat-home");
        return codex;
    }

    /// <summary>
    /// (2) BOTH VERIFIED CLASSES CARRY IT, THE CLI CLASS NEVER: the argv a broker or cell turn is built with names the seat home's catalog
    /// as <c>-c model_catalog_json="&lt;path&gt;"</c>; the CLI class's argv and the config file the app writes name none. RED at base: no
    /// class named a catalog, so a live turn read the account's list or the fallback metadata.
    /// </summary>
    [Fact]
    public void Broker_and_cell_argv_name_the_seat_homes_catalog_and_the_cli_class_does_not()
    {
        var codex = CodexIn(nameof(Broker_and_cell_argv_name_the_seat_homes_catalog_and_the_cli_class_does_not));
        var seat = codex.Seat!;
        var door = new Uri("http://127.0.0.1:1/mcp");
        var path = seat.CatalogPath!;
        Assert.Equal(Path.Combine(seat.Root, "codex", "model-catalog.json"), path);
        var pair = "model_catalog_json=\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";

        foreach (var seatClass in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass })
        {
            var argv = AgentArgs.SeatArgs(codex, seatClass, door);
            var at = Array.IndexOf(argv, pair);
            Assert.True(at > 0 && argv[at - 1] == "-c", $"{seatClass}'s argv does not carry -c {pair}");
            Assert.DoesNotContain(argv, a => a.Contains(SeatPlan.CatalogSlot, StringComparison.Ordinal));
        }
        Assert.DoesNotContain(AgentArgs.SeatArgs(codex, SeatPlan.CliClass, door), a => a.Contains("model_catalog", StringComparison.Ordinal));
        Assert.DoesNotContain("model_catalog", System.Text.Encoding.UTF8.GetString(SeatProfile.ConfigBytes(seat)), StringComparison.Ordinal);
        Assert.Equal("", SeatProfile.CatalogPin(seat, SeatPlan.CliClass));
        Assert.Equal(seat.ModelCatalogSha256, SeatProfile.CatalogPin(seat, SeatPlan.BrokerClass));
    }

    /// <summary>
    /// (2) THE SEAT HOME GETS THE CATALOG, AND AN EDITED ONE IS REFUSED NAMING BOTH HASHES: a preparation writes the shipped bytes beside
    /// the config; a catalog changed after it is refused for both verified classes in words naming its path, its hash and the app's; the
    /// next preparation writes the app's bytes back. The CLI class, which names none, is never refused on it.
    /// </summary>
    [Fact]
    public void The_seat_home_gets_the_catalog_and_an_edited_one_is_refused_naming_both_hashes()
    {
        var seat = CodexIn(nameof(The_seat_home_gets_the_catalog_and_an_edited_one_is_refused_naming_both_hashes)).Seat!;
        SeatProfile.Prepare(seat);
        var path = seat.CatalogPath!;
        var (shipped, _) = SeatProfile.ShippedCatalog(seat);
        Assert.Equal(shipped, File.ReadAllBytes(path));
        Assert.Null(SeatProfile.CatalogRefusal(seat, SeatPlan.BrokerClass));
        Assert.Null(SeatProfile.CatalogRefusal(seat, SeatPlan.CellClass));

        var edited = shipped!.ToArray();
        edited[^2] ^= 0x01;
        File.WriteAllBytes(path, edited);
        var editedSha = Convert.ToHexStringLower(SHA256.HashData(edited));
        foreach (var seatClass in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass })
        {
            var refusal = SeatProfile.CatalogRefusal(seat, seatClass);
            Assert.NotNull(refusal);
            Assert.Contains(path, refusal);
            Assert.Contains(editedSha, refusal);
            Assert.Contains(seat.ModelCatalogSha256, refusal);
        }
        Assert.Null(SeatProfile.CatalogRefusal(seat, SeatPlan.CliClass));

        File.Delete(path);
        Assert.Contains(path, SeatProfile.CatalogRefusal(seat, SeatPlan.BrokerClass));

        SeatProfile.Prepare(seat);
        Assert.Null(SeatProfile.CatalogRefusal(seat, SeatPlan.BrokerClass));
    }

    /// <summary>
    /// (2) LINK-FREE, LIKE THE CONFIG: a catalog that is a link is refused before anything is written, and what it points to is untouched.
    /// </summary>
    [Fact]
    public void A_catalog_that_is_a_link_is_refused_and_its_target_untouched()
    {
        var seat = CodexIn(nameof(A_catalog_that_is_a_link_is_refused_and_its_target_untouched)).Seat!;
        SeatProfile.Prepare(seat);
        var path = seat.CatalogPath!;
        File.Delete(path);
        var target = Path.Combine(Path.GetDirectoryName(seat.Root)!, "elsewhere.json");
        File.WriteAllText(target, "not the catalog");
        if (!SeatHomeTests.Link(path, target, folder: false)) return;   // a Windows account without the symlink privilege

        var ex = Assert.Throws<TradeAgentException>(() => SeatProfile.Prepare(seat));
        Assert.Equal(ErrorCode.AI_SEAT_REFUSED, ex.Code);
        Assert.Contains(path, ex.Message);
        Assert.Equal("not the catalog", File.ReadAllText(target));
    }
}
