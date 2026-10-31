using TradeAgent.Core.Db;

namespace TradeAgent.Core;

/// <summary>
/// ONE SEAT TYPE'S CLAIM FOR ONE PROTECTION (<c>docs/VISION.md</c> § 6.3; <c>docs/ORCHESTRATION.md</c> § 5): the class claimed, why, and the
/// tests that show the seat type's side of it — what keeps a seat of that type away from what the protection lives in.
/// </summary>
/// <param name="SeatClass">A <see cref="SeatClasses"/> constant.</param>
/// <param name="Claimed">The class the registry claims for the seat type.</param>
/// <param name="Basis">Why, in words the owner reads.</param>
/// <param name="Tests">The seat side's tests, as <c>Class.Method</c>; a conformance test asserts each exists.</param>
public sealed record SeatClaim(string SeatClass, EnforcementClass Claimed, string Basis, IReadOnlyList<string> Tests);

/// <summary>
/// ONE PROTECTION AS DATA (<c>U-invariant-registry</c>; VISION § 6.3): its stable id, what it says, where it is written, the code that keeps it
/// (<c>path:Symbol</c>, each checked to exist), the tests that prove the property itself (<c>Class.Method</c>, each checked to exist), and one
/// claim per seat type.
/// </summary>
public sealed record Invariant(
    string Id, string Statement, string WrittenIn, IReadOnlyList<string> EnforcedBy, IReadOnlyList<string> Tests,
    IReadOnlyList<SeatClaim> Seats, string? Open = null);

/// <summary>
/// THE INVARIANT REGISTRY (<c>U-invariant-registry</c>; VISION § 6.3; PRINCIPLES "The boundary remains enforceable": "Claim only the properties
/// actually demonstrated"). The protections <c>CLAUDE.md</c> and the code already hold, each with its enforcing component, its tests and its
/// enforcement class per seat type, so the owner's Oversight page says what is hard, what is mediated and what is only asked.
///
/// <para><b>It records; it enforces nothing.</b> No code path reads this registry to allow or refuse anything — it is shown, and a test binds it to
/// the code: every test it names must exist, every component it names must exist, and every id VISION § 6.3 names must be here.</para>
///
/// <para><b>The honesty rule, in code (<see cref="Shown"/>).</b> A class above advisory is shown only while it is backed — by at least one named
/// test of the property AND at least one named test of the seat type's side. A claim missing either is SHOWN as advisory and says why, whatever
/// the row claims. A test removed from the tree fails the conformance test, so the registry cannot keep claiming it.</para>
///
/// <para><b>App-owned and read-only.</b> Compiled into the app: no file, setting, pipe op or verb writes it, and the owner's Oversight page only reads it.</para>
///
/// <para>The classes are <see cref="EnforcementClass"/> (ORCHESTRATION § 5's words: "hard" is enforced by app code or the operating system on a
/// stated build and platform; "mediated" is no model-reachable tool crosses it but the process itself could; "advisory" is asked, not enforced;
/// a gap is an open door named). The seat types are ORCHESTRATION § 5's four columns, in <see cref="SeatTypes"/>; the test-only puppet seat
/// is left out. A doing seat in a cell is classed on its ATTEMPT side — the weaker of the cell and the unconfined harness that holds its login
/// (<c>RouteA.cs</c>'s assurance rows) — so a property under <c>state/</c> is mediated there, not hard.</para>
/// </summary>
public static class InvariantRegistry
{
    /// <summary>ORCHESTRATION § 5's seat types, in its column order, with the words the owner's Oversight page uses for each.</summary>
    public static readonly IReadOnlyList<(string SeatClass, string Title)> SeatTypes =
    [
        (SeatClasses.Cli, "Legacy pair, CLI seat (unconfined)"),
        (SeatClasses.Broker, "Thinking seat, broker-only"),
        (SeatClasses.Harness, "Thinking seat, API harness"),
        (SeatClasses.Cell, "Doing seat, confined cell")
    ];

    /// <summary>A class as the owner's Oversight page says it.</summary>
    public static string Word(EnforcementClass c) => c switch
    {
        EnforcementClass.HardPlatform => "enforced by containment",
        EnforcementClass.HardApp => "enforced by code",
        EnforcementClass.Mediated => "mediated",
        EnforcementClass.Advisory => "advisory",
        _ => "open gap"
    };

    /// <summary>
    /// THE CLASS THE PAGE SHOWS for <paramref name="claim"/> of <paramref name="invariant"/>, and why it is lower than claimed when it is: a
    /// class above advisory with no named test of the property, or none of the seat side, is shown as advisory.
    /// </summary>
    public static (EnforcementClass Class, string? Downgraded) Shown(Invariant invariant, SeatClaim claim)
    {
        if (claim.Claimed <= EnforcementClass.Advisory) return (claim.Claimed, null);
        if (invariant.Tests.Count == 0)
            return (EnforcementClass.Advisory, $"claimed {Word(claim.Claimed)}, shown advisory: no named test proves the property");
        if (claim.Tests.Count == 0)
            return (EnforcementClass.Advisory, $"claimed {Word(claim.Claimed)}, shown advisory: no named test proves this seat's side");
        return (claim.Claimed, null);
    }

    // ---- the seat sides ---------------------------------------------------------------------------------------------------------------

    /// <summary>What keeps a broker-only seat to the app's door: no environment, no host tool, no exec (<c>U-thinking-seats</c>).</summary>
    static readonly string[] BrokerSide =
    [
        "BrokerSeatTests.A_broker_seat_starts_with_no_environment_and_the_door_as_its_only_tool_server",
        "BrokerSeatTests.A_broker_seat_whose_offered_tools_include_a_host_tool_or_exec_is_refused_before_admission",
    ];

    /// <summary>What keeps the API harness to the app's own tools: no process, never told of a shell, a wake file or the inbox.</summary>
    static readonly string[] HarnessSide =
    [
        "CanonTests.The_harness_is_never_told_of_a_shell_a_wake_file_or_the_inbox",
    ];

    /// <summary>What a cell attempt is: <c>state/</c> granted to no cell, and the attempt classed as the weaker of the cell and the harness.</summary>
    static readonly string[] CellSide =
    [
        "CellPolicyTests.A_policy_that_grants_the_app_home_or_its_state_is_refused",
        "CellProofTests.The_attempt_side_is_the_weaker_of_the_cell_and_the_harness",
    ];

    const string CliWhy =
        "a CLI seat runs as your own user with a shell: it could reach the app's files, its database and its pipe directly (ORGANISATION § 14)";
    const string BrokerWhy = "no tool the broker offers crosses it, though the codex process itself runs as your user and could";
    const string HarnessWhy = "no process: every tool the harness has is the app's own code, and none reaches it";
    const string CellWhy =
        "the cell's own processes cannot reach state/ (containment); the unconfined harness that holds its login could, so the attempt is mediated";

    /// <summary>
    /// THE COMMON SHAPE: a protection the app's code keeps for everything that goes through its pipe, door or gateway — advisory beside a
    /// shell running as the owner, mediated on a broker-only seat or a cell attempt, enforced by code on the harness.
    /// </summary>
    static SeatClaim[] AtTheApp(string? cli = null, string? broker = null, string? harness = null, string? cell = null) =>
    [
        new(SeatClasses.Cli, EnforcementClass.Advisory, cli ?? CliWhy, []),
        new(SeatClasses.Broker, EnforcementClass.Mediated, broker ?? BrokerWhy, BrokerSide),
        new(SeatClasses.Harness, EnforcementClass.HardApp, harness ?? HarnessWhy, HarnessSide),
        new(SeatClasses.Cell, EnforcementClass.Mediated, cell ?? CellWhy, CellSide),
    ];

    static SeatClaim[] Replace(SeatClaim[] claims, SeatClaim with) =>
        [.. claims.Select(c => c.SeatClass == with.SeatClass ? with : c)];

    // ---- the registry -----------------------------------------------------------------------------------------------------------------

    /// <summary>Every protection, in VISION § 6.3's order.</summary>
    public static readonly IReadOnlyList<Invariant> All =
    [
        new("AUTH-001",
            "Your authority — the kill switch with pause, stop and flatten, your credentials and account connections, a paid key's spend, "
            + "updates, your private line, your veto — is in-process only: no pipe op and no verb, the board's included, reaches it.",
            "CLAUDE.md; VISION § 6.3",
            ["src/TradeAgent.Core/Protocol.cs:Ops.Mutating", "src/TradeAgent.Gateway/TradingGateway.Board.cs:VetoBoardAct"],
            [
                "OrgStructureTests.No_pipe_op_or_door_verb_reaches_veto_or_activation",
                "BoardActTests.No_op_verb_or_board_act_reaches_the_owners_veto_composition_or_budget",
                "BillingClassesTests.No_pipe_op_door_verb_or_trade_verb_reaches_either_ceiling",
                "OrgDecisionTests.No_decision_forecast_escalation_or_lateral_op_is_mutating_or_reaches_operator_authority",
                "OwnerPageTests.The_owners_page_holds_exactly_the_owners_presses",
            ],
            AtTheApp(),
            "No test checks the kill switch's pause and flatten, credentials, connections or updates against the whole op list by name."),

        new("AUTH-002",
            "The board's authority — every limit and allowance, paper and live mode, live activation, capital allocation, venture and instrument "
            + "approval — is reached only by board positions' recorded acts bound to their exact intent; two board seats for a widening or a money "
            + "act; never by text, never a trading act, never over the board's own power.",
            "CLAUDE.md; VISION § 6.3; R24",
            ["src/TradeAgent.Core/Board/BoardStore.cs:Propose", "src/TradeAgent.Core/Board/BoardStore.cs:Decide"],
            [
                "BoardActTests.The_proposer_cannot_confirm_its_own_act",
                "BoardActTests.A_confirmation_binds_to_the_intents_hash",
                "BoardActTests.Only_an_active_board_seat_exercises_a_board_verb",
                "BoardTurnTests.A_board_act_cannot_raise_the_boards_share",
            ],
            AtTheApp()),

        new("ORDER-001",
            "Only operations originates money-moving operations; headship never carries it.",
            "VISION § 6.3; Council.cs",
            ["src/TradeAgent.Core/Council.cs:CouncilRoles.MayPlaceOrders", "src/TradeAgent.Security/AgentGrants.cs:AgentGrants.Issue"],
            [
                "LaunchGrantRegisterTests.Only_the_chairs_role_may_move_money",
                "BrokerDoorTests.An_operations_grant_issued_beside_a_neighbour_may_not_buy_through_the_door",
                "LaunchGrantTests.Operations_launched_beside_a_running_research_attempt_may_not_trade_and_is_told_why",
                "BoardActTests.A_board_seat_never_places_an_order",
            ],
            Replace(AtTheApp(cli: "the pipe refuses every other role, but a CLI seat runs as your own user and could take another launch's grant"),
                new(SeatClasses.Cell, EnforcementClass.HardApp,
                    "the cell reaches the broker only through its harness leaf, and the grant's role and the gateway's order rules decide (RouteA's order-permission row)",
                    CellSide))),

        new("ORDER-002",
            "The client order id is carried onto the broker order and read back; a backend that cannot round-trip it says so, and the gateway "
            + "then refuses fully automatic live trading. Never faked.",
            "CLAUDE.md rule 1; IAtasAdapter",
            ["src/TradeAgent.ConnectorSdk/Contracts.cs:ReconciliationProvable", "src/TradeAgent.AtasBridge/ClientOrderIdProof.cs:ClientOrderIdProofs",
             "src/TradeAgent.Gateway/TradingGateway.cs:TryAuthorizeExecution"],
            [
                "BridgeRoundTripTests.An_order_placed_through_the_gateway_reaches_the_adapter_and_comes_back",
                "BridgeRoundTripTests.Capabilities_do_not_outlive_the_bridges_ability_to_attest_them",
                "CapabilityTests.Reconciliation_is_only_provable_with_client_ids_and_history",
                "ClientOrderIdProofTests.A_same_reference_match_never_reports_the_capability_true",
            ],
            AtTheApp()),

        new("ORDER-003",
            "Order history really reaches back to the time asked for, or the backend says it cannot; without it nothing is resolved as "
            + "\"this order does not exist\".",
            "CLAUDE.md rule 2; IAtasAdapter",
            ["src/TradeAgent.ConnectorSdk/Contracts.cs:ReconciliationProvable", "src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs:SupportsOrderHistory"],
            [
                "DisconnectTests.A_backend_that_cannot_prove_its_own_history_never_auto_resolves",
                "PolicyGateTests.Autonomous_live_trading_is_refused_on_a_backend_that_cannot_prove_order_state",
                "AgedDispatchTests.An_aged_record_the_reconciler_cannot_settle_is_left_flagged",
            ],
            AtTheApp(),
            "No test proves the ATAS adapter's own retention rule (it claims history only while its cache states a retention)."),

        new("ORDER-004",
            "A rejection is recorded only for a definite broker refusal; a timeout, a disconnect or anything ambiguous becomes UNKNOWN and is "
            + "reconciled.",
            "CLAUDE.md rule 3; IAtasAdapter",
            ["src/TradeAgent.AtasBridge/IAtasAdapter.cs:AtasRejectedException", "src/TradeAgent.AtasBridge/BridgeServer.cs:Refusal",
             "src/TradeAgent.Gateway/TradingGateway.cs:DispatchPlaceAsync"],
            [
                "BridgeRoundTripTests.A_definite_rejection_survives_the_crossing_as_a_rejection",
                "BridgeRoundTripTests.Losing_the_bridge_surfaces_as_indefinite_rather_than_as_a_rejection",
                "DispatchExceptionTaxonomyTests.A_place_that_throws_after_the_wire_is_recorded_UNKNOWN_and_pauses",
            ],
            AtTheApp()),

        new("ORDER-005",
            "No order is placed by driving a user interface: programmatic API only.",
            "CLAUDE.md rule 4; IAtasAdapter",
            ["src/TradeAgent.AgentRuntime/RuntimeManifest.cs:computer_use", "src/TradeAgent.AgentRuntime/SeatProfile.cs:SeatProfile"],
            [
                "SeatHomeTests.A_codex_seat_argv_carries_the_deny_set_fresh_and_resumed",
                "SeatHomeTests.A_feature_or_tool_server_outside_the_profile_refuses_the_seat_naming_it",
            ],
            [
                new(SeatClasses.Cli, EnforcementClass.Advisory,
                    "the seat starts with computer use and the browser turned off, but those are flags on its command line and its shell can install any tool", []),
                new(SeatClasses.Broker, EnforcementClass.Mediated, "the app's door is its only tool server; no host tool reaches a screen", BrokerSide),
                new(SeatClasses.Harness, EnforcementClass.HardApp, "every tool is the app's own code, and none drives a screen", HarnessSide),
                new(SeatClasses.Cell, EnforcementClass.Advisory, "no test shows that a cell cannot reach a display or another program's window", []),
            ],
            "The connector side has no test: nothing checks that the bridge and connectors use no UI automation. What is tested is the seats' side."),

        new("UNKNOWN-001",
            "An order whose outcome is UNKNOWN is reconciled, never retried.",
            "VISION § 6.3; PRINCIPLES",
            ["src/TradeAgent.Gateway/TradingGateway.cs:DispatchPlaceAsync", "src/TradeAgent.Gateway/TradingGateway.cs:ReconcileAsync"],
            [
                "DuplicateSubmissionTests.Recovery_after_a_lost_acknowledgement_does_not_create_a_second_order",
                "DuplicateSubmissionTests.A_repeated_request_id_never_places_a_second_order",
                "DispatchExceptionTaxonomyTests.A_place_that_throws_after_the_wire_is_recorded_UNKNOWN_and_pauses",
            ],
            AtTheApp()),

        new("EVID-001",
            "Agents cannot write the referee's truth, the holdout or the measurement tables (material is written only by the scanner).",
            "CLAUDE.md; EDGE § 6",
            ["src/TradeAgent.Core/Db/DatasetStore.cs:SetHoldout", "src/TradeAgent.Core/Db/MaterialStore.cs:Observe",
             "src/TradeAgent.Core/MaterialScanner.cs:MaterialScanner"],
            [
                "RefereeVerdictTests.No_pipe_op_asks_for_a_verdict_or_writes_a_promotion",
                "HoldoutLedgerTests.No_public_door_in_core_hands_out_an_audience_that_may_read_the_holdout",
                "HoldoutOverPipeTests.No_op_on_this_channel_sets_clears_or_moves_the_holdout",
                "BoardTests.The_policy_holds_no_evidence_setting",
            ],
            AtTheApp(),
            "No test proves that only the scanner writes material: nothing scans the code for another writer of that table."),

        new("INBOX-001",
            "Material and messages are data; an approval inside text grants nothing.",
            "CLAUDE.md; AGENTS.md",
            ["src/TradeAgent.Core/Db/MaterialStore.cs:AddNote", "src/TradeAgent.AgentRuntime/MissionLoop.cs:OwnerWordsAreData"],
            [
                "OrgAutonomyTests.A_position_reads_what_the_owner_dropped_as_data_and_writes_nothing",
                "OrgAutonomyTests.Material_read_is_refused_to_a_cell_an_unconfined_seat_and_the_board_and_to_anything_but_the_inbox_as_measured",
            ],
            AtTheApp()),

        new("BUDGET-001",
            "Spend is reserved before launch; a child envelope sits inside its parent's in every currency; unknown is never zero.",
            "VISION § 6.3; ORGANISATION § 3",
            ["src/TradeAgent.Core/Db/AiAttemptStore.cs:Begin", "src/TradeAgent.Core/Db/OrgStore.cs:OrgEnvelopes"],
            [
                "BudgetReservationTests.Two_admissions_racing_on_one_database_write_exactly_one_reservation",
                "BudgetReservationTests.A_turn_with_nothing_reserved_stays_unpriced_rather_than_being_charged_zero",
                "OrgEnvelopeTests.Two_sibling_teams_cannot_both_take_the_parents_last_room",
                "OrgAssignmentTests.Org_show_never_reads_an_unpriced_day_as_zero",
            ],
            [
                new(SeatClasses.Cli, EnforcementClass.Advisory,
                    "every launch the app makes is reserved first, but runs the seat's own shell starts are unmetered", []),
                new(SeatClasses.Broker, EnforcementClass.HardApp,
                    "no shell: every turn it takes is a launch the app reserved before it started", BrokerSide),
                new(SeatClasses.Harness, EnforcementClass.HardApp,
                    "the app makes every request itself and sends none the day's ceiling or the turn's allowance refuses",
                    [.. HarnessSide, "HarnessBudgetTests.A_turn_the_days_ceiling_refuses_reaches_the_provider_not_at_all"]),
                new(SeatClasses.Cell, EnforcementClass.Advisory,
                    "the launch is reserved first; a bound on runs started from inside the cell is not shown (ORCHESTRATION § 5)", []),
            ]),

        new("SIGNAL-001",
            "What trades is the frozen, measured computation: the runner calls no model; model judgement enters only as recorded, pinned "
            + "answers bound into the version's evidence; no agent output places, alters or times an order.",
            "COUNCIL rule 8; EDGE § 6.1; VISION § 6.3 (permanent — the owner, 2026-10-08)",
            ["src/TradeAgent.Gateway/ForwardRuns.cs:ForwardRuns", "src/TradeAgent.Core/Protocol.cs:Ops.Mutating"],
            [
                "DecisionPortTests.No_pipe_op_reaches_the_decision_port",
                "BoardActTests.A_board_seat_never_places_an_order",
                "BoardActTests.No_board_verb_is_mutating_and_none_names_an_order_field",
            ],
            [.. SeatTypes.Select(s => new SeatClaim(s.SeatClass, EnforcementClass.Gap,
                "open: operations still places, changes and cancels orders over the agent pipe wherever an allocation stands behind its version", []))],
            "The runner's half is shown only indirectly (the gateway that runs it holds no model). The other half is not yet true: operations "
            + "still holds buy, sell, modify and cancel on the pipe (Ops.Mutating) until the frozen runner trades alone (R26 U-runner-live)."),

        new("CRED-001",
            "No seat holds your credentials.",
            "ORGANISATION § 13.8",
            ["src/TradeAgent.Core/Paths.cs:SeatHome", "src/TradeAgent.AgentRuntime/SeatProfile.cs:SeatProfile"],
            [
                "SeatHomeTests.A_codex_seat_runs_under_the_app_seat_home_never_the_owners",
                "SeatHomeTests.A_linked_seat_home_is_refused_and_nothing_is_written_through_it",
                "SeatHomeTests.A_seat_home_owned_by_another_account_is_refused",
            ],
            [
                new(SeatClasses.Cli, EnforcementClass.Advisory,
                    "it runs under the app's own seat home, never yours — but its shell runs as your user and could read your own sign-in", []),
                new(SeatClasses.Broker, EnforcementClass.Mediated,
                    "the codex process holds the seat home's sign-in and offers it to no tool the model can call",
                    [.. BrokerSide, "BrokerSeatTests.A_runtimes_json_that_drops_a_forbidden_home_path_is_refused_although_the_surface_passes"]),
                new(SeatClasses.Harness, EnforcementClass.HardApp,
                    "the app holds the key and sends it only to the provider; no tool hands it over",
                    [.. HarnessSide, "SeatSignInTests.A_pasted_key_reaches_stdin_and_is_never_logged_or_kept"]),
                new(SeatClasses.Cell, EnforcementClass.Mediated,
                    "the seat home is granted to no cell (containment); the harness outside the cell holds the sign-in, so the attempt is mediated",
                    [.. CellSide, "CellPolicyTests.A_policy_that_grants_the_seat_home_the_apps_own_folders_or_an_environments_toml_is_refused"]),
            ]),

        new("ORG-001",
            "A position grants no system privilege.",
            "VISION § 6.3 (concierge CF-ORG-001)",
            ["src/TradeAgent.Core/Council.cs:CouncilRoles.MayPlaceOrders", "src/TradeAgent.Core/Db/OrgRights.cs:OrgRights"],
            [
                "OrgRightsTests.A_third_position_may_backtest_and_is_refused_orders",
                "OrgSeatsTests.No_pipe_op_door_verb_or_trade_verb_writes_a_seat",
                "ThinkingSeatsTests.A_position_on_a_cli_seat_is_refused_because_its_shell_is_code_and_code_needs_a_cell",
            ],
            AtTheApp()),

        new("SIM-001",
            "Simulation never becomes evidence, reality or authority.",
            "VISION §§ 6.3, 6.4 (concierge CF-SIM-001)",
            ["src/TradeAgent.Core/Proving/ProvingGround.cs:HomeKind", "src/TradeAgent.Core/Proving/ProvingGround.cs:ProvingGround"],
            [
                "ProvingHomeTests.No_evidence_path_names_the_proving_namespace",
                "ProvingHomeTests.A_real_process_refuses_a_proving_database_and_tape_naming_both_kinds",
                "SyntheticMarketTests.The_real_home_is_byte_identical_after_a_proving_market_and_run",
                "PaperAllocationGateTests.A_paper_allocation_never_authorises_a_live_dispatch",
            ],
            AtTheApp()),

        new("CHANGE-001",
            "Self-improvement never self-authorises broader power.",
            "VISION § 6.3 (concierge CF-CHANGE-001)",
            ["src/TradeAgent.Core/Board/BoardStore.cs:ProposePower"],
            [
                "BoardActTests.A_power_proposal_changes_nothing",
                "BoardActTests.Only_the_owners_presses_change_the_boards_composition_or_budget",
                "BoardTurnTests.A_board_act_cannot_raise_the_boards_share",
            ],
            AtTheApp(),
            "A proposal to change the kernel itself is not tested: the power-proposal test covers composition, the board's budget and connections."),
    ];

    /// <summary>One invariant by id, or null.</summary>
    public static Invariant? Find(string id) => All.FirstOrDefault(i => i.Id == id);
}
