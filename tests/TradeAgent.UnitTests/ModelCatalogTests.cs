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
}
