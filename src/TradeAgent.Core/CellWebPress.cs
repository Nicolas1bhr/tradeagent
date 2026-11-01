namespace TradeAgent.Core;

/// <summary>Where the owner's web press stands, as the Organisation page draws it.</summary>
public enum CellWebPhase
{
    /// <summary>Nothing to draw: not Windows, or no cell proof here yet (egress is moot until cells run at all).</summary>
    Hidden,

    /// <summary>Cells run, at egress none: the press is offered.</summary>
    Offered,

    /// <summary>Windows' permission prompt is open.</summary>
    Asking,

    /// <summary>The rule was placed; the benchmark is measuring every slot under it.</summary>
    Measuring,

    /// <summary>The newest proof names web: every slot measured 443 reached and nothing else.</summary>
    Proved,

    /// <summary>He said No at Windows' prompt: egress none, and the press is offered again.</summary>
    Declined,

    /// <summary>The rule could not be placed, or was placed and not measured holding on every slot: egress none, the press offered again.</summary>
    Failed
}

/// <summary>
/// THE OWNER'S ONE PRESS FOR WEB ON WINDOWS (<c>U-cell-windows-egress</c> item 1; R25 D2): one press, then Windows' own permission prompt and his
/// "Yes" — the only burden the product rule lets him bear. Platform-free so its words are tested anywhere: <paramref name="ask"/> starts the one
/// fixed elevated helper (<see cref="WindowsCellEgress.Request"/>), <paramref name="measure"/> re-runs the benchmark and returns its sentence,
/// and <paramref name="webProved"/> reads the newest Windows proof (null: no proof at all; false: egress none; true: web). Whether web holds
/// is the proof's word only — a placed rule the benchmark did not measure on every slot is a failure, said, and the cells stay at none.
/// One press at a time: a press while one is running does nothing.
/// </summary>
public sealed class CellWebPress(Func<Task<(EgressAsk Outcome, string Why)>> ask, Func<Task<string>> measure, Func<bool?> webProved)
{
    readonly Lock _gate = new();
    CellWebPhase _running = CellWebPhase.Hidden;
    (CellWebPhase Phase, string Why)? _last;

    /// <summary>Where it stands now.</summary>
    public CellWebPhase Phase
    {
        get
        {
            lock (_gate)
                if (_running != CellWebPhase.Hidden) return _running;
            return webProved() switch
            {
                true => CellWebPhase.Proved,
                null => CellWebPhase.Hidden,
                false => _last is { Phase: not CellWebPhase.Proved } last ? last.Phase : CellWebPhase.Offered
            };
        }
    }

    /// <summary>Whether the press is drawn: offered, or offered again after a No or a failure.</summary>
    public bool CanPress => Phase is CellWebPhase.Offered or CellWebPhase.Declined or CellWebPhase.Failed;

    /// <summary>The page's sentence for where it stands.</summary>
    public string Words
    {
        get
        {
            var phase = Phase;
            string why;
            lock (_gate) why = _last?.Why ?? "";
            return phase switch
            {
                CellWebPhase.Offered => Labels.CellWebOffered,
                CellWebPhase.Asking => Labels.CellWebAsking,
                CellWebPhase.Measuring => Labels.CellWebMeasuring,
                CellWebPhase.Proved => Labels.CellWebProved,
                CellWebPhase.Declined => Labels.CellWebDeclined,
                CellWebPhase.Failed => Labels.CellWebFailed(why),
                _ => ""
            };
        }
    }

    /// <summary>THE PRESS: Windows' prompt, then — on his Yes and the rule placed — the measurement of every slot. Never throws.</summary>
    public async Task Press()
    {
        lock (_gate)
        {
            if (_running != CellWebPhase.Hidden) return;
            _running = CellWebPhase.Asking;
        }
        try
        {
            var (outcome, why) = await ask();
            if (outcome == EgressAsk.Declined) { Settle(CellWebPhase.Declined, ""); return; }
            if (outcome != EgressAsk.Placed) { Settle(CellWebPhase.Failed, why); return; }
            lock (_gate) _running = CellWebPhase.Measuring;
            var said = await measure();
            Settle(webProved() == true ? CellWebPhase.Proved : CellWebPhase.Failed,
                webProved() == true ? "" : Labels.CellWebNotMeasured(said));
        }
        catch (Exception ex) { Settle(CellWebPhase.Failed, ex.Message); }
    }

    void Settle(CellWebPhase phase, string why)
    {
        lock (_gate)
        {
            _last = (phase, why);
            _running = CellWebPhase.Hidden;
        }
    }
}
