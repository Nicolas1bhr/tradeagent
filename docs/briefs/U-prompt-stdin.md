# U-prompt-stdin — no prompt in argv: every seat's packet and the owner's words reach the runtime on stdin, never on its command line
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.7 (a seat's prompt on stdin, never in its argv) and the recorded gap of `U-cell-linux-a` (`/proc/<pid>/cmdline`
of every same-user process stays readable — no `hidepid` without a mount namespace); ORGANISATION § 15 row `U-prompt-stdin`. **A protection — the agent
boundary (assessor independence, § 2.10's frozen sets; INBOX-001: the owner's words are data a confined neighbour must not read): red-first and ONE
watched mutant.** Seat A; opus/high. **No rung.** After `U-seat-home` (same argv builder), before `U-cell-linux-c`.
**Today (main `15dacd77`, read by seat A):** `AgentArgs.Build` (`src/TradeAgent.AgentRuntime/AgentSession.cs:140-166`) substitutes the WHOLE prompt into
the `{prompt}` slot of the template (`:155`) or appends it (`:162`); codex's templates are `["exec","{prompt}"]` / `["exec","resume","--last","{prompt}"]`
(`RuntimeManifest.cs:586-592`), OpenCode's `["run","{prompt}"]`, `["run","--continue","{prompt}"]` (`:481-483`). stdin is redirected only so it can be
closed at once (`AgentSession.cs:520-531`, `:580`) because codex reads stdin IN ADDITION to the argument. On Windows a `.cmd` shim runs through
`ComSpec /c` (`CliAgentRuntime.cs:162-176`), so the prompt also crosses cmd.exe's parser. Consequences: any same-user process — a confined cell
included — reads every attempt's packet and the owner's words from `/proc/<pid>/cmdline` (Linux) or the process command line (Windows); a packet is
capped by argv (Linux MAX_ARG_STRLEN 128 KiB per argument; Windows 32,767 characters per command line).
**Observable result:** no runtime process the app starts for a turn carries the prompt in its argv; the prompt is written to the child's stdin, which
is then closed (end-of-file at once, as today). A runtime whose manifest cannot take the prompt on stdin is marked so as DATA and the launch says so
in words; nothing falls back to argv silently.
Read first: `CLAUDE.md` (vendor commands are data; no terminal; refuse, never guess); ORCHESTRATION § 2.7; `AgentSession.cs:110-170, 505-600`;
`RuntimeManifest.cs:210-260, 470-500, 580-660`; `CliAgentRuntime.cs:150-180` and every other caller of `AgentArgs.Build`/`For` (`grep -rn 'AgentArgs\.' src`);
`Core/Containment.cs:57-129` (the launcher inherits stdio — the prompt must pass through it).
Items, one commit each, one-sentence messages:
1. **The manifest says how a prompt travels — data.** `RuntimeManifest.PromptOnStdin` (bool) and the stdin placeholder each vendor reads: codex
   `exec -` / `exec resume --last -` (verify with `codex exec --help` under a throwaway `CODEX_HOME`+`HOME`, quoted; no model call); OpenCode: verify
   `opencode run --help` the same way — if it cannot read the message from stdin, `PromptOnStdin=false` for it and the report says so; `custom`: false.
2. **`AgentArgs.Build` never puts the prompt in argv when `PromptOnStdin`** — it puts the vendor's stdin marker in the slot; `AgentSession` writes the
   prompt (UTF-8, no BOM) to `StandardInput` and closes it; the existing "close at once" comment and behaviour stay for the end-of-file.
3. **A launch with `PromptOnStdin=false` is refused for any seat that is not the legacy pair's existing behaviour** — today every seat is the legacy pair,
   so: the launch proceeds as today but the attempt's Mechanism/diagnostics line names "prompt in argv (runtime cannot read stdin)", and a test pins that
   the flag exists for `U-thinking-seats`' gate to read. (No silent fallback; no new seat class here.)
4. **Size:** a test turn with a 300 KiB prompt (over Linux's per-argument cap) starts and the fake CLI receives it whole on stdin.
Red-first tests (CI has no codex: the suite's fake-CLI pattern; quote each red at base):
(a) `A_turn_never_puts_the_prompt_in_the_childs_argv` — the fake CLI records its argv and stdin; base: the prompt is in argv (red);
(b) `A_resumed_turn_reads_its_prompt_from_stdin_too`; (c) `A_prompt_over_the_argv_cap_reaches_the_runtime_whole`;
(d) `A_runtime_that_cannot_read_stdin_is_named_in_the_launch_not_silently_fed_argv`; (e) on Windows CI: the `.cmd`-shim path carries no prompt on its command line.
**The watched mutant:** make `Build` substitute the prompt into the slot again for codex ⇒ (a) red. Quote it red, restore, green.
**Evidence beyond CI:** one mock run (`fleet/tmp/A-org/seat-home/mock/run-case.sh`, a throwaway home, no real model) with the unit's real codex argv and
the prompt on stdin — quote the mock's captured request containing the prompt and `ps -o args` of the codex process NOT containing it.
Out of scope: cells (`U-cell-linux-*`); the seat home (`U-seat-home`); the API harness (no argv); OpenCode's home.
Conflicts: `U-seat-home` LANDED (`053b9ec2`, record `133759b5`, rung 37): its deny set rides `AgentArgs` (settings on `RuntimeManifest.Seat`) and every launch verifies the seat's argv — keep its args fresh and resumed and make its argv check expect the stdin marker, never the prompt. **Seat A at dispatch:** the line numbers above are from `15dacd77`; U-seat-home and U-admission-fail-closed moved them — locate by `grep -n`. Codex evidence runs ONLY with a throwaway `CODEX_HOME`+`HOME` and the mock provider; the local Colima VM is OFF-LIMITS.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three
platforms; the Codex review with the CI wait; names vs `main` 0 removed). `## Report` ≤ 20 lines appended here.
