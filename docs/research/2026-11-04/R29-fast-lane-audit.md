# R29 — An adversarial audit of the fast lane (2026-11-04)

**What it is.** A findings-only audit of the 144 commits `997365c4..2a4a7edc` (29 units, 2026-10-30 to 11-02, R25) against `CLAUDE.md`'s "The safety
rules that outrank making it compile" and `docs/PRINCIPLES.md` "The boundary remains enforceable", with R24 and R25 D1–D13 as what was meant to hold.
Run from a cloud session on branch `claude/r29-fast-lane-audit`, starting at main `92204b30`. No product code changed.

**Status: IN PROGRESS.** This first commit records the method only; findings follow in later commits on this branch.

## 0. Method
- Toolchain: .NET SDK 10.0.114 (distro package; the dotnet-install host was refused by the session's proxy). `dotnet build TradeAgent.sln` at
  `92204b30`: 0 warnings, 0 errors.
- Areas: (A) hires, wakes and budgets (D3, D5, D13, wake-once, seat models); (B) evidence (D6, D7, D11, D12, α-wealth, holdout, inconclusive path,
  backtest parity, lineage); (C) owner authority and data-vs-instruction (D4, D9, material-read, vendor_said, pages, updater, Start fresh, R24);
  (D) containment (Windows cells and proof, D2 egress, Landlock, kit socket, home-on-hire); (E) proving ground, invariant registry, canon, vacuous tests.
- Public-repo scan of `git diff 997365c4 2a4a7edc`: no key, token, e-mail or IP beyond loopback and 8.8.8.8 test constants; home paths are placeholders
  (`/home/o`, `C:\Users\Owner`); `tools/linux-ui.sh` takes host, user and key from an untracked env file.

*Records findings only; marks nothing fixed.*
