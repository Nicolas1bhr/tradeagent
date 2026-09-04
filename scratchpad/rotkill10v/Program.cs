using TradeAgent.AtasBridge;

// Round-10 verifier's out-of-process harness. Real processes, real files, no seams.
var mode = args.Length > 0 ? args[0] : "read";
var dir  = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "rk10v");
Directory.CreateDirectory(dir);
var file = Path.Combine(dir, "coid-witness.json");
var log  = Path.Combine(dir, CoidWitness.ErrorLogName);

static string Gap(string claim) =>
    $"{DateTimeOffset.UtcNow.AddMinutes(-5):O} ERROR coid-witness rewrite did not land. claim={claim}"
    + Environment.NewLine;

void Leftover(int n)
{
    var p = file + $".tmp-dead-{n:D3}";
    File.WriteAllText(p,
        $$"""{"version":1,"generation":99,"predecessor":"deadbeefdeadbeef","records":[{"client_order_id":"TA-X{{n}}","session_id":"dead","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK","identified_at":"2026-01-01T00:00:01+00:00"}]}""");
    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-5));
}

string Everything()
{
    var sb = new List<string>();
    foreach (var f in Directory.GetFiles(dir, CoidWitness.ErrorLogName + "*"))
        try { sb.Add(File.ReadAllText(f)); } catch (Exception) { }
    return string.Join("\n", sb);
}

switch (mode)
{
    case "seed":
    {
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        var w = new CoidWitness(file);
        w.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null);
        w.Dispose();
        File.WriteAllText(log, Gap("TA-GAP"));
        File.AppendAllText(log, new string('x', 60 * 1024) + Environment.NewLine);
        Console.WriteLine("seeded: gap in the current log, log near the cap");
        break;
    }

    // A writer that keeps rotating for N seconds. Every rewrite fails (the witness path is a
    // DIRECTORY), so the gap is never legitimately resolved and every append is a safety line.
    case "rotate":
    {
        var seconds = int.Parse(args.Length > 2 ? args[2] : "6");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        // EVERY REWRITE FAILS, so every append is a SAFETY line and the gap is never resolved.
        var w = new CoidWitness(file, replace: (_, _) => throw new IOException("no space left on device"));
        var n = 0;
        while (DateTime.UtcNow < until)
            w.Submitting($"TA-R{++n}", "SIM", "ES", "Buy", 1m, null);
        w.Dispose();
        Console.WriteLine($"rotations driven, appends={n}");
        break;
    }

    // Readers, out of process, against that live writer. A CLEAN reading is the failure.
    case "read-loop":
    {
        var seconds = int.Parse(args.Length > 2 ? args[2] : "6");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        long reads = 0, clean = 0, unreadable = 0, degraded = 0;
        while (DateTime.UtcNow < until)
        {
            var r = new CoidWitness(file);
            var token = r.Token();
            reads++;
            if (token.Contains("io:degraded")) degraded++;
            else if (r.Trouble is not null) unreadable++;
            else if (!r.Noted) clean++;
            if (clean > 0) { Console.WriteLine($"CLEAN READING: {token} trouble={r.Trouble ?? "<null>"}"); break; }
        }
        Console.WriteLine($"reads={reads} degraded={degraded} otherTrouble={unreadable} CLEAN={clean}");
        break;
    }

    // A writer that rotates on almost every turn: pad the log past its cap, then one failing
    // Submitting, with a FRESH witness each time so the byte counter re-seeds from the file.
    case "churn":
    {
        var seconds = int.Parse(args.Length > 2 ? args[2] : "6");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var rots = 0;
        while (DateTime.UtcNow < until)
        {
            File.AppendAllText(log, new string('y', 65 * 1024) + Environment.NewLine);
            var w = new CoidWitness(file, replace: (_, _) => throw new IOException("no space left on device"));
            w.Submitting($"TA-C{++rots}", "SIM", "ES", "Buy", 1m, null);
            w.Dispose();
        }
        Console.WriteLine($"rotations driven = {rots}");
        break;
    }

    case "read":
    {
        var r = new CoidWitness(file);
        Console.WriteLine("  files      : " + string.Join(", ",
            Directory.GetFiles(dir, CoidWitness.ErrorLogName + "*").Select(Path.GetFileName).Order()));
        Console.WriteLine("  Trouble    : " + (r.Trouble ?? "<null>"));
        Console.WriteLine("  Token      : " + r.Token());
        Console.WriteLine("  Standing   : " + CoidWitnessReport.Standing(r));
        Console.WriteLine("  TA-GAP still on disk anywhere: " + Everything().Contains("TA-GAP"));
        break;
    }

    // One rotation, then sit still so the parent can SIGKILL at a random instant.
    case "rotate-forever":
    {
        var w = new CoidWitness(file, replace: (_, _) => throw new IOException("no space left on device"));
        var n = 0;
        while (true) w.Submitting($"TA-K{++n}", "SIM", "ES", "Buy", 1m, null);
    }
}
