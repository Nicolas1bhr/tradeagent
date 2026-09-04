# U2a — ROUND 7 BOUNCE · verifier round 6 on `ffa1a3d`: FAIL 0H/1M/1L (Codex delta for rounds 5+6 running)

Record: `records/U2a-verify-r6.md`. **Same builder, same worktree.**

- **F-E (MED) — manager decision taken.** On a quiet connection `BridgeServer` answers frames strictly sequentially, so
  while the bridge works on OUR emergency it cannot emit the one signal `PeerAnsweredSince` needs; measured: a bridge that
  reads the frame and answers at 2500 ms or 3500 ms is dropped at ≈2000 ms with "not responding" — and `BridgeProtocol.cs`
  itself names the legitimate cause (a synchronous ATAS call wedging the frame loop). **Decision: two bounds, two
  meanings.** (1) `EmergencyDeadline` (2 s) bounds ONLY the caller's wait: the owner gets "not confirmed — check ATAS"
  and the record is UNKNOWN at 2 s, unchanged. (2) Liveness uses the system's EXISTING ordinary RPC deadline (10 s, the
  constant behind "ATAS did not answer within 10s") as its grace: a peer is dropped only when it has answered nothing
  within that bound; a peer that answers later than 2 s but within it is KEPT, and its late answer is recorded on the
  pending RPC (not dropped on the floor; whether the gateway consumes it for settlement is U2c-1's — say so in the record).
  No new number. Stated cost, to be written in the record: a wedged-but-heartbeating peer is now detected at ≈10 s
  instead of ≈2 s; the caller's answer is not delayed by it. Tests: answers at 2.5 s / 3.5 s → kept, "busy", late answer
  recorded; answers nothing for 10 s → dropped "not responding"; the wedged heartbeating peer → dropped at ≈10 s, 12/12
  phases; the caller's 2 s answer measured unchanged in all three.
- **F-F (LOW).** The round-6 record says both recovered tests were "restored verbatim"; one
  (`An_agent_cancel_all_through_the_real_gateway_fails_fast_on_a_stalled_bridge`) had its single assertion replaced by
  five for F-D. Correct the record (a strengthening, named as such).

Process as before; append `## Round 7 (build record, <date>)` to `records/U2a.md` AS YOU GO; targeted
`ConnectorSendDeadlineTests`, then `dotnet build TradeAgent.sln` + FULL suite once on the Mac; **the box grant is yours
for ONE run**: push, verify the tree identity as in round 6, run the two pipe classes and the full suite once, quote it.
Report: tip sha, RED → GREEN → mutant, suite counts (Mac + box), "What I did NOT do".
