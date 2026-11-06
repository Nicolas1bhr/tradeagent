namespace TradeAgent.AgentRuntime;

/// <summary>What a seat's mark says went wrong: both end its launches for a time, in different words.</summary>
public enum SeatTrouble
{
    /// <summary>The vendor said the seat's tools cannot run (<see cref="RuntimeManifest.ToolsUnavailablePattern"/>).</summary>
    ToolsUnavailable,

    /// <summary>The vendor refused the seat's model (<see cref="RuntimeManifest.ModelRefusedPattern"/>).</summary>
    ModelRefused
}

/// <summary>
/// THE SEATS THE VENDOR SAID CANNOT WORK NOW (<c>U-cli-code-mode</c>; <c>U-turn-bounds</c>, R28 W1 and W6, R30 D17): a turn whose stream carried
/// the vendor's sentence marks its seat — the runtime, the profile it was verified under and the model — and every later launch of that same
/// seat is refused in the same words before admission, so no reservation is written and no wake is taken for a turn that would do nothing.
/// Another program, settings file, deny set or model is another seat and is tried; so is the same one after the app restarts, when its dry run
/// runs again.
///
/// <para><b>A mark expires.</b> One matching stream must not end a seat for the life of the process: an unattended run has no restart. A mark
/// holds for <see cref="FirstHold"/>, then ONE probe turn may run, and the others stay refused while it does. A probe that ends cleanly clears
/// the mark; one that meets the same sentence marks the seat again for twice as long, up to <see cref="MaxHold"/>. So over 72 hours a seat
/// that never recovers costs about a dozen probes, against one wasted turn per look.</para>
///
/// <para><b>No model is ever switched here (D17).</b> A position's model stays the owner's two-press. A refusal holds the seat and says so.</para>
/// </summary>
public static class SeatMarks
{
    /// <summary>How long a seat's first mark holds: R28's figure, an hour of no turns against one wasted probe.</summary>
    public static readonly TimeSpan FirstHold = TimeSpan.FromHours(1);

    /// <summary>
    /// The longest any repeated mark holds: eight hours, so a seat the vendor brings back overnight is found again within a working day of the
    /// 72-hour run, at no more than three probes a day.
    /// </summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromHours(8);

    /// <summary>
    /// How long a probe may hold the seat before another launch may try it instead. Longer than any turn's wall-clock limit
    /// (<see cref="TradeAgent.Core.SeatSpec.VendorTurnLimit"/>), so a turn still running is never probed beside; a probe that never launched, or
    /// that ended saying nothing about the seat, costs only this wait.
    /// </summary>
    public static readonly TimeSpan ProbeWindow = TimeSpan.FromMinutes(45);

    sealed record Mark(SeatTrouble Trouble, string DisplayName, string? Model, string VendorSaid, DateTimeOffset Until, int Strikes, object? Prober);

    static readonly object Gate = new();
    static readonly Dictionary<(string Runtime, string Profile, string Model), Mark> Marks = [];

    /// <summary>
    /// Marks the seat and returns the app's sentence for it, quoting <paramref name="vendorSaid"/>. A mark still holding stands as it is, so two
    /// lanes that met the same sentence are one strike; a mark whose hold is over, or a probe's, strikes again and holds longer.
    /// </summary>
    public static string Set(RuntimeManifest manifest, string? profile, string? model, SeatTrouble trouble, string vendorSaid, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var key = Key(manifest, profile, model);
        lock (Gate)
        {
            Marks.TryGetValue(key, out var was);
            if (was is { Prober: null } && was.Until > now) return Words(was);
            var strikes = (was?.Strikes ?? 0) + 1;
            var mark = new Mark(trouble, manifest.DisplayName, model, vendorSaid.Trim(), now + Hold(strikes), strikes, null);
            Marks[key] = mark;
            return Words(mark);
        }
    }

    /// <summary>
    /// THE SENTENCE A LAUNCH OF THIS SEAT IS REFUSED IN, or null for one that may start. A hold that is over lets the first
    /// <paramref name="launch"/> to ask through as the seat's probe, and only that one until it ends or <see cref="ProbeWindow"/> passes. The
    /// same launch asks again before its process starts and is let through again. Asked with no launch, it is a look: it claims nothing.
    /// </summary>
    public static string? Refusal(RuntimeManifest manifest, string? profile, string? model, object? launch = null, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var key = Key(manifest, profile, model);
        lock (Gate)
        {
            if (!Marks.TryGetValue(key, out var mark)) return null;
            if (launch is not null && ReferenceEquals(mark.Prober, launch)) return null;
            if (mark.Until > now) return Words(mark);
            if (launch is null) return null;
            Marks[key] = mark with { Until = now + ProbeWindow, Prober = launch };
            return null;
        }
    }

    /// <summary>A turn of this seat ended cleanly, so nothing stands against it any more, and the next trouble is a first strike.</summary>
    public static void Clear(RuntimeManifest manifest, string? profile, string? model)
    {
        lock (Gate) Marks.Remove(Key(manifest, profile, model));
    }

    /// <summary>Forgets every mark: a test's way to start clean.</summary>
    internal static void Forget()
    {
        lock (Gate) Marks.Clear();
    }

    /// <summary>The hold of a seat's <paramref name="strikes"/>-th mark: <see cref="FirstHold"/>, doubling, never past <see cref="MaxHold"/>.</summary>
    internal static TimeSpan Hold(int strikes)
    {
        var hold = FirstHold;
        for (var i = 1; i < strikes && hold < MaxHold; i++) hold *= 2;
        return hold < MaxHold ? hold : MaxHold;
    }

    /// <summary>The sentence of a mark, in the owner's words: what the vendor said, what that cost, and when the seat is tried again.</summary>
    static string Words(Mark mark)
    {
        var on = mark.Model is { Length: > 0 } m ? $" on {m}" : "";
        var when = mark.Prober is not null
            ? "one turn is trying it now"
            : $"at {mark.Until.ToLocalTime():HH:mm} one turn tries it again";
        return mark.Trouble == SeatTrouble.ToolsUnavailable
            ? $"{mark.DisplayName}{on} could not run any command this turn — it said \"{mark.VendorSaid}\" — so the turn did no work, and " +
              $"TradeAgent is not starting this AI seat for now: {when}, and a change to its program, its settings or its model is tried at once. " +
              "Nothing it was woken for was answered; it stays owed."
            : $"{mark.DisplayName} refused the model{(mark.Model is { Length: > 0 } refused ? $" {refused}" : "")} — it said \"{mark.VendorSaid}\" — so " +
              $"TradeAgent is not starting this AI seat for now: {when}. Nothing it was woken for was answered; it stays owed. " +
              FallbackWords(mark.Model);
    }

    /// <summary>What the owner can do about a refused model. TradeAgent never moves a position to another model by itself (D17).</summary>
    static string FallbackWords(string? model) =>
        model == BrokerSeat.Fallback
            ? "No other model is offered: the fallback itself was refused."
            : "TradeAgent does not change a position's model by itself: to carry on sooner, press " +
              $"\"{TradeAgent.Core.Labels.SeatModelSwitch(BrokerSeat.ModelWords(BrokerSeat.Fallback))}\" on the position's row.";

    /// <summary>The seat: the runtime, its verified profile — or, for a runtime with no seat profile, the program it names — and the model.</summary>
    static (string, string, string) Key(RuntimeManifest manifest, string? profile, string? model) =>
        (manifest.Id, profile ?? "program:" + manifest.Executable, model ?? "");
}
