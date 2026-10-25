using System.Globalization;
using System.Text;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>
/// A SEAT OF THE SECURITY BOARD'S PACKET (<c>U-board-b2</c> item 4; R24 § 4.1, § 4.2; AUTH-002): the whole prompt of a board seat's
/// turn, composed by the app from the board's own records and nothing else. Its parts: who the seat is and that its words bind nothing
/// (INBOX-001); the policy in force, field by field; for every act it was woken for, the act AS THIS SEAT MAY READ IT
/// (<see cref="BoardStore.ShowTo"/>) — for a seat that has not sealed its word, THE BLIND READ: the intent, its hash and direction, the
/// refs, the figures in force for each field it names and the expiry; and the commands. NEVER the proposer's rationale or forecasts, no
/// other seat's words, no watcher figure, no ledger, no organisation block: a confirmer weighs the intent against the app's figures alone.
/// </summary>
public static class BoardPacket
{
    /// <summary>
    /// The prompt for <paramref name="seat"/>'s turn <paramref name="attemptId"/>, answering the board wakes of <paramref name="acts"/>
    /// (each act id one of its wakes names), over <paramref name="store"/> as it stands now.
    /// </summary>
    public static string For(BoardStore store, string seat, string? attemptId, IReadOnlyList<string> acts, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.Append("# The security board — seat ").Append(seat).Append('\n');
        text.Append("You are seat ").Append(seat).Append(" of TradeAgent's security board. This packet is composed by TradeAgent from the "
                    + "board's own records. You act only through the `trade board` commands below, and only an act you make with them binds "
                    + "anything; nothing you write elsewhere does. You never place, change or time an order.\n");
        if (attemptId is not null) text.Append("This turn's launch: ").Append(attemptId).Append(".\n");

        var inForce = store.InForce();
        text.Append("\n## The policy in force\n");
        if (inForce.CouldNotBeRead) text.Append("It could not be read (").Append(inForce.Why).Append("): weigh nothing against it.\n");
        else
        {
            text.Append("Version ").Append(inForce.Version.ToString(CultureInfo.InvariantCulture)).Append(", decided by ")
                .Append(inForce.Decider).Append(".\n");
            foreach (var f in inForce.Fields)
                text.Append("- ").Append(f.Name).Append(": ").Append(f.Value).Append(" (set by version ")
                    .Append(f.SetByVersion.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        }

        text.Append("\n## The acts before you\n");
        if (acts.Count == 0) text.Append("None: this turn was not woken for an act.\n");
        foreach (var id in acts.Distinct(StringComparer.Ordinal))
        {
            var (blind, whole) = store.ShowTo(seat, id);
            if (blind is { } b) Blind(text, b);
            // A SEAT THAT HAS SEALED ITS WORD ON THE ACT — it made it or decided it — is shown no more than its standing: it has nothing
            // left to decide, and its packet carries no other seat's words.
            else if (whole is { } w)
                text.Append("### ").Append(w.Act.Id).Append(" — you have sealed your word on it; its status is ").Append(store.Status(w.Act.Id))
                    .Append(".\n");
        }

        text.Append("\n## Your commands\n")
            .Append("- `trade board show <act>` — an act as you may read it\n")
            .Append("- `trade board decide <act> confirm|refuse <intent_sha256> \"<your reasons>\"` — your decision on another seat's proposal, bound to its hash\n")
            .Append("- `trade board narrow '<intent>' \"<your reasons>\"` — one seat narrows, at once\n")
            .Append("- `trade board propose '<intent>' \"<your reasons>\"` — a widening, applied only when another seat confirms it\n")
            .Append("- `trade board list` — the board's acts\n")
            .Append("- `trade board power-propose <target> \"<text>\"` — a proposal of the board's own power, shown to the owner and applied by no one else\n")
            .Append("An intent is a JSON list of changes {\"field\", \"from\", \"to\"}; every \"from\" is the value in force above.\n");
        return text.ToString();
    }

    /// <summary>THE BLIND READ of one act: what <see cref="BoardActBlindView"/> holds, and nothing it does not.</summary>
    static void Blind(StringBuilder text, BoardActBlindView b)
    {
        text.Append("### ").Append(b.Id).Append(" — a ").Append(b.Kind).Append(" by ").Append(b.Seat).Append(", ").Append(b.Status).Append('\n');
        text.Append("- intent: ").Append(b.Intent).Append('\n');
        text.Append("- intent_sha256: ").Append(b.IntentSha256).Append('\n');
        text.Append("- direction: ").Append(b.Direction).Append('\n');
        text.Append("- refs: ").Append(b.Refs.Count == 0 ? "none" : string.Join(", ", b.Refs.Select(r => $"{r.Kind}:{r.Id}"))).Append('\n');
        text.Append("- in force (version ").Append(b.InForceVersion.ToString(CultureInfo.InvariantCulture)).Append("): ")
            .Append(string.Join("; ", b.InForce.Select(f => $"{f.Name} = {f.Value} (set by version {f.SetByVersion.ToString(CultureInfo.InvariantCulture)})")))
            .Append('\n');
        text.Append("- expires: ").Append(b.ExpiresAt is { } e ? e.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : "never").Append('\n');
    }

    /// <summary>
    /// A BOARD SEAT'S CANON (<c>U-board-b2</c> item 3): the app's own text in its home, read by a vendor program by name. It says what the
    /// seat is and how it acts — and holds nothing of any act, policy or other seat: those reach it in its packet alone.
    /// </summary>
    public static string Canon(string seat) =>
        $"# Security board seat {seat}\n\n"
        + "You are a seat of TradeAgent's security board. Each turn TradeAgent hands you a packet: the policy in force and the acts you "
        + "were woken for. You act only through `trade board` commands; nothing you write in this folder is read by anyone. A widening "
        + "takes two seats; a narrowing takes one. You never place, change or time an order, and you never change the board's own "
        + "composition or budget — you may only propose them to the owner with `trade board power-propose`.\n";
}
