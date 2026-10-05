using ContainmentProbe;

// R-containment probe harness (docs/EDGE-FACTORY.md §6.11, factory plan §9.3). Probe-only; no product
// code, no money path, places no order, makes no model call. One executable, three roles:
//
//   containment-probe host [baseDir]   out-of-sandbox: sets everything up and runs the matrix
//   containment-probe worker <mode>    in-sandbox: the negative/positive controls (main|net|sleep|idle)
//   containment-probe c1call           isolated attempt at the experimental C1 entry point
//
// baseDir defaults to a fresh dated folder; the box is left as found under it.

var verb = args.Length > 0 ? args[0] : "host";

switch (verb)
{
    case "worker":
        return Worker.Run(args.Length > 1 ? args[1] : "main");
    case "c1call":
        return SandboxC1.CallChild();
    case "grantui":
        return WindowStation.GrantByString(args[1]);
    case "revokeui":
        return WindowStation.RevokeByString(args[1]);
    case "host":
    default:
        var baseDir = args.Length > 1 ? args[1] : Path.Combine("C:\\ta", "containment-20261006");
        return Host.Run(baseDir);
}
