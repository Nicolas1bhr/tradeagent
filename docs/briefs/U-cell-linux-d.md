# U-cell-linux-d — the cell's network and packages: name resolution decided by a probe (datagrams stay denied with DNS over TCP, or UDP labelled open), per-cell egress classes on Landlock's TCP ports, loopback/host/inter-cell rows, package installs into the workspace
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.8 (a cell's "network and IPC policy"; "side-channel claims are limited to the cell's tested network, IPC and file policy, and
inter-cell communication is part of the benchmark"; Acceptance rows *network* and *packages*; R20: a confined seat keeps "permitted network" and "package installs");
ORGANISATION § 15 row `U-cell-linux-d` (:446). **Protections — CRED-001 and `state/` (no egress class reaches them), inbox attestation (COUNCIL rule 7, Q5), honesty
(every network claim is a tested row or a labelled gap), no terminal, refuse-never-guess: red-first and ONE watched mutant.** Seat A; **opus/high**. **No rung** (the
egress class lives in the per-launch policy file, not the database). **Depends on -a only — dispatchable before -b and -c.**
**Today (main `bd151496`, READ):** `CellPolicy` carries raw `TcpPorts` (`src/TradeAgent.Core/Cell.cs:31-38`, :54), wire `Version = 1` (:200, `Wire` :206). Landlock always
handles TCP bind+connect (`CellNative.cs:267`) and adds connect rules per port only (:326-333) — no bind rule, so a cell listens nowhere (row `CellLinuxTests.cs:357-386`,
"bind-denied"); rules are PORT-ONLY: a granted port reaches loopback, the host and the internet alike. seccomp allows `socket()` only for AF_INET/AF_INET6 + SOCK_STREAM
(:343-369, :361-362), one program per architecture (`SeccompProgram(Architecture)`, :351). **The survey's UDP gap is already CLOSED by -a's amendment (i)** — row
`A_cell_cannot_send_udp_or_open_a_netlink_or_packet_socket` (`CellLinuxTests.cs:387-412`), profile "no UDP" (BUILD-STATUS.md:10434ff) — so every datagram is denied,
loopback DNS included: glibc's resolver (UDP first) gets EACCES and a cell resolves only `/etc/hosts` (inference, NOT RUN — item 1 measures it). The protected set
(`Cell.cs:156-178`) refuses the home, `state/` and `cells/` but NOT `Paths.Inbox` (`Paths.cs:32`, under `Paths.Workspace` :22): a workspace of `Paths.Workspace` would hold
the inbox. `AgentPresence` (`Core/AgentPresence.cs:43`) is entered by `CliAgentRuntime.cs:216`, :669, :918; no product path launches a cell yet.
**Observable result:** a `CellPolicy` names an egress class — `none` (default: no TCP port, no datagram) or a class from one table (`CellEgress`: ports, and the DNS
decision of item 1) — and the effective port set is in the policy's hash; the profile states, per class, what UDP and DNS are (DENIED, or OPEN and labelled), and every
loopback/host/inter-cell reach a granted port allows is a row ASSERTING the gap (red the day it closes, like -a's cmdline row). A cell installs a Python and an npm
package into its workspace from a loopback index, and nothing outside the workspace changes. Policy format 2: a version-1 file is refused naming both versions.
Read first: `CLAUDE.md`; ORCHESTRATION §§ 2.3, 2.5, 2.8, 5, 8; the survey §§ 1, 3, Risks; -a's brief + record; `Cell.cs`; `CellNative.cs:250-400`; `CellLinuxTests.cs:36-165, 357-470`.
Items, one commit each, one-sentence messages:
1. **THE PROBE, which decides (RULING 5)** — `A_cell_name_resolution_probe` (`Category=Cell`, prints a table, fails only on a missing prerequisite): in a cell, outside
   control first, resolve `_gateway`/`_outbound` (answered by systemd-resolved itself — no query leaves the runner) through glibc (`getent ahosts`, Python `getaddrinfo`),
   Node `dns.lookup`, `curl`, `pip download` from a loopback index by name: (A) datagrams denied, `RES_OPTIONS=use-vc` in the policy env, TCP 53 granted; (B) a probe-only
   seccomp program allowing AF_INET/6 SOCK_DGRAM. **Decide by the measurement and record it in the profile:** all of glibc/Python/Node/pip resolve under (A) ⇒ "datagrams
   DENIED (hard); DNS over TCP, port 53 to any host, labelled"; otherwise ⇒ "UDP OPEN — any host and port — for egress-classed cells, labelled; class `none` keeps it
   denied" (`SeccompProgram` then takes the class). The ruling's "seccomp denies datagram sockets except loopback DNS" cannot be literal: seccomp sees `socket()`'s family,
   type and protocol, never the address behind `connect`/`sendto`, and a user-notification supervisor reads it racily — (A) is its closest honest form. Landlock ABI 10's
   UDP port rules (kernel 7.2) replace both later. Print `/etc/resolv.conf`'s nameserver and `nsswitch.conf`'s hosts line; no stub ⇒ the probe FAILS, never skips.
2. **Egress classes:** `CellEgress` (one table in `src/TradeAgent.Core/Cell.cs`): `none`, `web` (443), `web-plain` (80, 443), each plus item 1's DNS answer; `CellPolicy` takes a
   class (and a test-only explicit table, so rows use ephemeral ports: an unprivileged runner cannot listen on 443), `Wire` v2 (v1 refused, both versions named — no compat).
3. **The network rows** (`CellLinuxTests.Network.cs`, `public partial class CellLinuxTests`): (a) `A_cell_with_no_egress_connects_nowhere_and_sends_no_datagram`;
   (b) `A_granted_port_reaches_loopback_ipv6_loopback_and_the_hosts_own_address_alike_a_recorded_gap`; (c) `A_cell_cannot_listen_on_any_tcp_port` (no cell serves another);
   (d) `Two_cells_granted_one_port_reach_the_same_outside_listener_a_recorded_channel`; (e) `A_cell_resolves_names_as_its_class_says` (item 1's decision).
4. **Packages:** (f) `A_cell_installs_a_python_package_into_its_workspace_from_a_loopback_index` (`pip install --target`, `TMPDIR`/`PIP_CACHE_DIR` in the workspace; the
   wheel built by the test); (g) `A_cell_installs_an_npm_package_into_its_workspace_from_a_loopback_registry` (`npm_config_cache` in the workspace; a local tarball).
   Each asserts the import works inside and a before/after listing of the test HOME and `/tmp` is unchanged. A tool that breaks under the policy is reported with its errno, not loosened.
5. **Q5, evidence without a flip (RULING 2):** `Guarded` also refuses `Paths.Inbox` and `Paths.Workspace` itself — (h) `A_cell_cannot_write_the_owners_inbox` (planted
   inbox; a policy naming it or its parent refused). A cell process STILL ENTERS `AgentPresence` (default IN, fail-closed to quiescence); -d adds no launch that skips
   `Enter`. **The row that flips it** is the profile's `inbox` row = file (h) + IPC (-a's row d) + egress `none` (a) — recorded on the attempt by -c's `CellAssurance`
   (its rung) and consumed by seat B; an egress-classed cell never qualifies: (b) shows a granted port reaches a same-user listener that could write the inbox.
Red-first (each red at base quoted, by behaviour): (e) (no DNS), (f)/(g) (no workspace cache/`TMPDIR`), (h) (inbox granted). GUARDS green at base: (a), (c). ASSERTED gaps: (b), (d).
**The watched mutant:** an empty port set leaves TCP unhandled in the Landlock ruleset (drop `NetConnectTcp` from `handled` when the class has no port) ⇒ (a) red: the
`none` cell connects to the outside listener. Quote it red on CI, restore, green.
**Evidence beyond CI:** item 1's table (resolver × mode) and the decision quoted; the Cell step's log (rows by name, the asserted gaps marked); tests box (Windows unchanged).
Q2: no lane changes. **NOT VERIFIED until the owner's host (-e):** a real LAN, his resolver, internet registries (loopback indexes by design: public repo, deterministic), musl/Go resolvers.
Out of scope: the cgroup and grants (-b); owner-editable classes (policy data, § 2.3); route (a), `CellAssurance` on the attempt, the presence flip (-c / `U-execution-environment`); bind ports for dev servers
(an inter-cell channel; a later unit with its own row); a netns, `pasta` or AppArmor policy (needs root); Windows/macOS. Conflicts: -b — disjoint files (-b: `Core/Containment.cs`,
`CellCgroup.cs`, `AgentRuntime/Containment.cs`, `PeerImage.cs`, packaging, build.yml) but the identical `partial` line; -c touches `Cell.cs` later and rebases on this.
Gate and report per `fleet/BUILDER-PROMPT-v2.md`: Release `--no-incremental` 0 warnings; touched classes 3×; full suite on branch CI, three platforms, the Cell step's rows
quoted; Codex review with the CI wait; names vs `main` 0 removed; tests box line. `## Report` ≤ 20 lines appended here.

## Report
**Code tip `4a61219a`** (on main `97b1e169`). Gate: Release `--no-incremental` "0 Warning(s) 0 Error(s)"; touched classes 3× (CellPolicy/Seccomp/Launch/GrantTests "Passed 23, Skipped 3" ×3; CellLinuxTests 30 skipped ×3 on the Mac, Linux-only); names vs main "sets: base 2761 tip 2779 / removed: 0 / added: 18".
CI **38968186292**: every job success (ubuntu with the Cell step "Total tests: 30 Passed: 30", kernel 6.17.0-1022-azure, Landlock ABI 7; windows 1–5; linux-host; packages) EXCEPT macos-latest: Timing `SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_not_once_per_rpc` red on both attempts ("the operation deadline passed before the simulator answered"). My diff cannot reach it: 7 files, all Cell-only plus Core's InternalsVisibleTo(IntegrationTests); the same test is green on ubuntu and every Windows shard of the same run; ledger :168/:206 earlier macOS sightings → seat A's call.
Items: **1 done** — probe table (CI 38963476708): glibc/Python/Node/curl/pip resolve under (A) TCP 53 + use-vc + resolv.conf, fail under none and under A0 (resolv.conf links into /run); B also resolves. DECISION printed: "datagrams DENIED (hard); DNS over TCP, port 53 to any host, labelled" (`CellEgress.Decided = Tcp`). Deviation: by-name index on `_localdnsstub` (curl answers `.localhost` itself), pip needs `--trusted-host`.
**2 done** — `CellEgress` none/web/web-plain + test-only `ForTests` (internal), effective ports in the hash, `Profile` line per class; policy form 2, form 1 refused "is form 1; this build reads form 2 only". **3 done** — rows a–e in `CellLinuxTests.Network.cs`. **4 done** — `CellPolicy.ToolEnvironment` + rows f, g (`CellLinuxTests.Workspace.cs`). **5 done** — `Guarded` refuses the inbox, inside it and what holds it (Paths.Workspace), by path and descriptor; row h; no launch skips `AgentPresence.Enter`, presence not flipped.
**Deviation (protection widened):** measured on ABI 7 with Landlock alone (witness CI 38963476708): a class-none cell's `MSG_FASTOPEN` send CONNECTED ("fastopen-sent" beside "tcp-denied errno=13") and an unbound `listen()` got a port ("autobind-listen-open port=35299") → seccomp now denies `listen` and sendto/sendmsg/sendmmsg carrying MSG_FASTOPEN (EACCES); rows a, c assert it.
RED before (red-run CI 38948105394, base behaviour restored on a throwaway): (e) "glibc-fail rc=2 … node-fail EAI_AGAIN" for class web; (g) "npm-rc=1 … cache folder contains root-owned files"; (h) "Assert.Throws() Failure: No exception was thrown"; (c) "autobind-listen-open"; unit inbox test locally "String: null / Not found: \"the owner's inbox\"". (f) was GREEN at base (pip falls back to cwd and disables its cache) — declared, not red.
WATCHED MUTANT (CI 38966074389, throwaway, other jobs cancelled after ubuntu): TCP connect unhandled when no port ⇒ row (a) red "expected the line 'tcp-denied errno=13'" with "tcp-connected", "stub53-connected"; 29/30 others green; restored.
Asserted gaps (green, red the day they close): (b) granted port reaches 127.0.0.1, ::1 and the host's 10.1.x address; (d) two cells relay through one outside listener.
Found, not fixed (not my files): the cell stage leaves its .NET runtime files in /tmp on every launch (`dotnet-diagnostic-<pid>-…-socket`, `clr-debug-pipe-<pid>-in/out`, execve keeps the pid) → Core/Containment.cs (-b's file).
tests box: NOT RUN — the machine does not answer
Codex review (not acted on): fleet/tmp/A-org/u-cell-linux-d-build/U-cell-linux-d-review.md (its prompt's `bc26f660..HEAD` spans main's commits since, after my rebase). NOT VERIFIED: aarch64; musl/Go resolvers; the owner's host, LAN and resolver; real registries (loopback by design).
Rebased after the run onto main `30a73c91` (main moved with code, 25 files, none of mine): code tip now `16d66370`, the same seven files byte-for-byte; CI 38968186292 ran at `4a61219a` and was NOT re-run on the new base — seat A to rule whether it carries.
