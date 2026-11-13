#!/usr/bin/env python3
"""THE ORGANISATION'S DIGEST (U-org-digest): the fleet's eyes on the AI organisation during an unattended run.

READ-ONLY, ALL OF IT. tools/linux-host.sh org-digest sends this file over ssh and runs it as the app's own account, against the
app's two state databases (state/tradeagent.db, state/tape.db) and nothing else: every database is opened with SQLite's
mode=ro and PRAGMA query_only=ON, so a statement that wrote would fail rather than run. It never reads a credential, the seat
home's codex folder or anything holding the owner's sign-in, never names the host, and never writes a file.

  org-digest.py [--home /var/lib/tradeagent | --state <dir>] [--since <T0>] [--json]

  --since   T0: an ISO date or date-time (no zone = the host's local zone; the app's own stored instants are accepted as they are),
            or a span back from now: 72h, 3d. Default: the start of the host's local day (the owner's day: the one the AI's daily
            ceilings reset on).
  --json    the whole digest as one JSON object; otherwise a human digest of at most 60 lines, one line per fact. Its lists shrink to
            fit; its ATTENTION flags never do, and every overdue position is named at every size.
  --now     (tests) the instant "now" is, ISO with a zone.   --no-systemd   (tests) do not ask systemd about the unit.
  --vocabulary   (tests) print every word this file compares a column to, as JSON, and stop.
  --machine-id-file / --boot-id-file / --osrelease-file   (tests) where the cell proof's host identity is read from.
  --disk-file   (tests) a JSON [free, total] read instead of asking the filesystem.

Every figure is a MEASUREMENT the app wrote (ai_attempt, org_event, mission_event, tool_call, engineering_log, strategy_*, fill, cell_proof,
tape_fetch). A deployment's profit and loss is a SIMULATION (paper): a book of fills at modelled prices, never money. A cost is the
AI's list-price equivalent, never a bill. A section that cannot be read says so; it is never a guess and never a zero.

WHAT THE RUN MUST MEASURE (R26 § 4), from the app's own records: each arrow of the loop, first reached and how often, with its first
refusal in words (arrows); turns per position per day, list-price cost per turn by seat, the idle-turn share, the wait behind the
lane, vendor-limit refusals (pace); forecasts written and resolved per manager with the days to 50 each, trials and verdicts spent
against each campaign's budgets, admissible candidates (signal). What the records cannot answer is said, never estimated: a "keep"
has no record of its own, and whether a fresh session found a finding again from the ledger is not recorded anywhere.

THE SCHEMA MOVES UNDER THIS FILE, so a table or column a home does not have yet is named as absent in the part that needed it, and a
word this file has not heard of (a wake's disposition, a launch's state) is shown as it stands, never folded into another.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import re
import sqlite3
import statistics
import subprocess
import sys
import urllib.parse
from decimal import Decimal, InvalidOperation

DEFAULT_HOME = "/var/lib/tradeagent"
STATE_UNDER_HOME = ".local/share/TradeAgent/state"
MAX_LINES = 60
GIB = 1 << 30
DISK_FLOOR_SHARE, DISK_FLOOR_MOST, DISK_FLOOR_LEAST = 10, 10 * GIB, 2 * GIB    # DiskFloor.For: a tenth of the disk, at most 10 GiB, never under 2 (held to it by OrgDigestTests)


class W:
    """EVERY WORD THIS FILE COMPARES A COLUMN TO, EXACTLY AS THE APP WRITES IT, AND NOWHERE ELSE IN THIS FILE.

    SQLite's = is case-sensitive, so a word spelled by memory reads 0 on a real home and looks like a loop that never got there.
    OrgDigestTests.Every_word_the_digest_compares_is_the_one_the_app_writes holds each name here to the app's own constant or writer
    (`--vocabulary` prints them), and a scan of this file fails on a word typed anywhere else."""
    # strategy_allocation.mode, strategy_deployment.mode, the board policy's mode: TradingMode's names
    MODE_OBSERVE, MODE_PAPER = "OBSERVE", "PAPER"
    # strategy_deployment.state, deployment_op.state, org_position.status
    DEPLOYMENT_ENDED = "ended"
    DEPLOYMENT_OP_REFUSED = "refused"
    # A RUN'S POSITION AND ITS PROTECTION, read as TradingGateway.ProtectionMissing reads them: deployment_op.kind (DeploymentOpKind), the settled
    # deployment_op.state, execution_request.execution_state's terminal states (OrderStateMachine) and the one a person's press can still send
    DEP_OP_ENTRY, DEP_OP_EXIT, DEP_OP_FLATTEN, DEP_OP_STOP, DEP_OP_TARGET = "entry", "exit", "flatten", "stop", "target"
    DEPLOYMENT_OP_RESOLVED = "resolved"
    ORDER_TERMINAL = ("FILLED", "CANCELLED", "REJECTED")
    ORDER_AWAITING_APPROVAL = "AWAITING_APPROVAL"
    SIDE_BUY = "Buy"
    # ITS WORDS, as the run's line says them (DeploymentReadings) and as the day note says them at a day's close (UnprotectedAtTheClose)
    UNPROTECTED_HOLDS, UNPROTECTED_RESTING, UNPROTECTED_NONE_YET = "UNPROTECTED: it holds ", " resting at the venue (the last answer: ", "none yet"
    UNPROTECTED_NO_BOTH, UNPROTECTED_NO_STOP, UNPROTECTED_NO_TARGET = "no stop and no target", "no stop", "no target"
    UNPROTECTED_ASKS_AGAIN = ". TradeAgent keeps asking for it by itself while the run is active."
    UNPROTECTED_AT_CLOSE = "UNPROTECTED at that day's close"
    POSITION_ACTIVE = "active"
    # ai_attempt.state: spelled only in AiAttemptStore's SQL
    ATTEMPT_LAUNCHED, ATTEMPT_ENDED, ATTEMPT_LOST = "LAUNCHED", "ENDED", "LOST"
    # strategy_promotion.verdict and .reason, referee_holdout.addis_status
    VERDICT_PROMOTED, VERDICT_PAPER_ELIGIBLE = "promoted", "paper-eligible"
    REASON_NO_TRADE = "no-trade-on-the-holdout"
    ADDIS_INCONCLUSIVE = "inconclusive"
    # org_event.kind and .decider
    ORG_CHARTERED, ORG_HIRED, ORG_ACTIVATED = "chartered", "hired", "activated"
    DECIDER_OWNER, DECIDER_APP, DECIDER_BOARD = "owner", "app", "board"
    # org_mandate_event.kind
    MANDATE_ISSUED, MANDATE_DELIVERED, MANDATE_ACCEPTED, MANDATE_REJECTED = "issued", "delivered", "accepted", "rejected"
    # mission_event.disposition, and the id shapes that carry a meaning
    DISPOSITION_BLOCKED, DISPOSITION_FAILED = "blocked", "failed"
    WAKE_PAPER_RUN = "note:paper-run:"       # MissionEventIds.PaperRun: carries a forward run's figures back to its author
    WAKE_AFTER_LOST = ":after-lost"          # MissionLoop.AfterLost: a told-something wake raised again after a lost turn
    # publication.role and .kind: what the app tells a run's author
    PUBLICATION_ALLOCATOR, PUBLICATION_NOTE = "allocator", "note"
    # ledger_entry.kind, org_decision.kind (the two decision words are reserved for a verb main does not have yet: see `iterate`)
    LEDGER_KILL = "kill"
    DECISION_FAMILY_KILL, DECISION_FAMILY_BRANCH = "family-kill", "family-branch"
    # org_forecast_resolution.outcome, cell_proof_row.kind, board_event.kind
    OUTCOME_VOID = "void"
    PROOF_GAP = "GAP"
    BOARD_FOUNDED, BOARD_BUDGET_SHARE = "founded", "budget-share"
    # engineering_log.component and .event
    LOG_IPC, LOG_OP_REFUSED = "Ipc", "op_refused"
    PIPE_REFUSALS = ("grant_rejected", "auth_rejected", "protocol_rejected", "operator_session_refused", "op_refused")
    # strategy_allocation.policy_version
    POLICY_INCONCLUSIVE = "allocation-policy-inconclusive-v1"   # a slot that costs its campaign a trial
    POLICY_STOP = "allocation-policy-stop-v1"                   # D19's stop, a row with an empty window: not an allocation
    # tool_call.tool: a door verb is its op with _ for -; `trade`, `data` and `report` carry the op in the argument ("trade backtest")
    OP_ORG_CHARTER, OP_ORG_HIRE = "org-charter", "org-hire"
    OP_MANDATE_ASSIGN, OP_MANDATE_DELIVER, OP_MANDATE_ACCEPT, OP_MANDATE_REJECT = "mandate-assign", "mandate-deliver", "mandate-accept", "mandate-reject"
    OP_BACKTEST, OP_VERDICT = "backtest", "verdict"
    TOOLS_CARRYING_AN_OP = ("trade", "data", "report")
    # ai_attempt.context's `ended`: a turn stopped by its wall-clock limit, the vendor's refusal of the model, or the vendor's usage limit
    ENDED_TURN_LIMIT, ENDED_MODEL_REFUSED, ENDED_USAGE_LIMIT = "TURN_TIME_LIMIT", "VENDOR_MODEL_REFUSED", "VENDOR_USAGE_LIMIT"
    TURN_STOPS = (ENDED_TURN_LIMIT, ENDED_MODEL_REFUSED, ENDED_USAGE_LIMIT)
    # engineering_log: a turn that was not started, in words, each line opening with the position's title
    LOG_MISSION, LOG_TURN_REFUSED = "Mission", "turn_refused"
    # engineering_log.severity of a line the app wrote because something of its own failed (a sweep, a record, a handler)
    SEVERITY_ERROR = "error"
    TITLE_OPERATIONS, TITLE_RESEARCH = "Operations Director", "Research Director"
    # mission_event.payload of a chief's ceiling moved short: nothing is moving, so it wakes at its floor (R30 D21)
    WAKE_CHIEF_FLOOR = "chief-floor"
    # the rest of what is matched: the legacy chair a launch with no role belongs to, a board seat's prefix, an order's, a fill's side
    ROLE_DEFAULT, ROLE_RESEARCH = "operations", "research"
    BOARD_PREFIX = "board-"
    CLIENT_ORDER_PREFIX = "TA-"
    SIDE_SELL = "Sell"
    BILLING_PLAN, BILLING_KEY, BILLING_UNKNOWN = "plan", "key", "unknown"
    # keys: meta, kv, and the one setting read
    META_SCHEMA_VERSION, KV_SETTINGS, SETTING_WORKS_ON_ITS_OWN = "schema_version", "settings", "ai_works_on_its_own"
    # the free-space guard's pause in the settings: DiskPause's serialised names
    SETTING_DISK_PAUSE, PAUSE_AT, PAUSE_FREE, PAUSE_TOTAL, PAUSE_FLOOR, PAUSE_UNREADABLE = "disk_pause", "at", "free", "total", "floor", "unreadable"
    # THE KEYS READ OUT OF JSON THE APP WROTE: a key respelled in the app reads None here, which looks like a turn with no reason, so each is
    # held to its serialiser or writer like the words above. ai_attempt.context (TurnContext): how the turn ended and the words that stopped it
    CTX_ENDED, CTX_REFUSED = "ended", "refused"
    # engineering_log metadata: the loop's line for a turn it did not start (AppHost), and the pipe's refusal of an op (RecordRefusal)
    LOG_WHY = "why"
    REFUSAL_OP, REFUSAL_CODE = "op", "code"
    # board_policy.body: BoardPolicyBody's canonical names
    BODY_PLAN_DAY_TOTAL, BODY_MODE = "plan_day_total", "mode"


ADMISSIBLE = (W.VERDICT_PROMOTED, W.VERDICT_PAPER_ELIGIBLE)    # the two classes a version can be carried forward on
REPEATED_REFUSAL = 5                     # the 72-hour brief's "act on": the same words refused five times
FORECASTS_TO_SCORE = 50                  # R26 § 4: the resolved forecasts a manager needs before its calibration can be read
# The tables whose rows name the launch that wrote them: a turn that left a row in one of these did something.
ACT_TABLES = ("org_mandate_event", "org_delivery", "org_decision", "org_forecast", "org_escalation", "org_lateral", "ledger_entry",
              "publication", "strategy_version", "strategy_run", "board_act", "board_forecast")
VENDOR_LIMIT_WORDS = re.compile(r"\b429\b|rate.?limit|usage.?limit|quota|too many requests|limit (?:reached|exceeded)", re.IGNORECASE)


# ---- time ------------------------------------------------------------------------------------------------------------------
def iso_offset(zone):
    return "+00:00" if zone == "Z" else zone


def parse_ts(text):
    """A stored instant (the app writes UTC, 'O' format: 2026-10-30T12:00:00.0000000Z) as an aware datetime, or None."""
    if not text:
        return None
    m = re.match(r"^(\d{4}-\d\d-\d\d)[T ](\d\d:\d\d:\d\d)(?:\.(\d+))?(Z|[+-]\d\d:\d\d)?$", str(text).strip())
    if not m:
        return None
    frac = (m.group(3) or "")[:6].ljust(6, "0")
    zone = m.group(4) or "Z"
    d = dt.datetime.fromisoformat(f"{m.group(1)}T{m.group(2)}.{frac}{iso_offset(zone)}")
    return d.astimezone(dt.timezone.utc)


def stored(d):
    """An instant spelled exactly as the app stores it, so a string comparison in SQL is a comparison in time."""
    return d.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z"


def local_day(now):
    local = now.astimezone()
    start = dt.datetime(local.year, local.month, local.day).astimezone()
    end = dt.datetime.combine(local.date() + dt.timedelta(days=1), dt.time.min).astimezone()
    return start.astimezone(dt.timezone.utc), end.astimezone(dt.timezone.utc)


def parse_since(text, now):
    if text is None:
        return local_day(now)[0]
    t = text.strip()
    m = re.match(r"^(\d+)([hd])$", t)
    if m:
        return now - (dt.timedelta(hours=int(m.group(1))) if m.group(2) == "h" else dt.timedelta(days=int(m.group(1))))
    m = re.match(r"^(\d{4}-\d\d-\d\d)(?:[T ](\d\d:\d\d)(?::(\d\d))?)?(Z|[+-]\d\d:\d\d)?$", t)
    if not m:
        exact = parse_ts(t) if re.search(r"(Z|[+-]\d\d:\d\d)$", t) else None   # an instant as the app stores or prints it, fractions included
        if exact is not None:
            return exact
        raise ValueError(f"'{text}' is not a T0: use an ISO date or date-time (2026-10-30, 2026-10-30T18:00Z) or a span like 72h")
    clock = (m.group(2) or "00:00") + ":" + (m.group(3) or "00")
    if m.group(4):
        return dt.datetime.fromisoformat(f"{m.group(1)}T{clock}{iso_offset(m.group(4))}").astimezone(dt.timezone.utc)
    return dt.datetime.fromisoformat(f"{m.group(1)}T{clock}").astimezone().astimezone(dt.timezone.utc)


def parse_day(text):
    """A cutoff as a row holds it: an instant, or the bare date a row may carry (midnight UTC)."""
    t = str(text or "").strip()
    return parse_ts(t if re.search(r"[T ]", t) else t + "T00:00:00Z")


BAR_UNITS = {"m": 60, "h": 3600, "d": 86400, "w": 604800}


def dataset_close(last_bar, interval):
    """CampaignStore.Close: the close of a dataset's last bar; None (a span with no end, never guessed short) where it cannot be read."""
    last = parse_ts(last_bar)
    m = re.fullmatch(r"(\d+)([mhdw])", str(interval or ""))
    return last + dt.timedelta(seconds=int(m.group(1)) * BAR_UNITS[m.group(2)]) if last is not None and m else None


def spans_overlap(a, b, c, d):
    """CampaignStore.Overlaps: [a, b) and [c, d) share market time; a None start or end is no bound on that side."""
    return (b is None or c is None or b > c) and (d is None or a is None or a < d)


# ---- reading ---------------------------------------------------------------------------------------------------------------
def wal_mode(path):
    """The database header's read-version byte (offset 18): 2 is WAL. Read from the file, because a PRAGMA would need it open."""
    try:
        with open(path, "rb") as fh:
            head = fh.read(20)
        return len(head) == 20 and head[18] == 2
    except OSError:
        return False


def open_ro(path):
    """A database opened so that it CANNOT be written: SQLite's own read-only mode, and query_only on top of it.

    WHILE THE APP IS STOPPED (the run's deliberate restart) a WAL database has no -wal or -shm beside it, and a read-only connection can
    neither make them nor open without them. Nothing is writing then, so the file is read as it stands (immutable), which creates
    nothing; while the app runs its -wal exists and the ordinary read-only path sees its uncheckpointed rows."""
    uri = "file:" + urllib.parse.quote(path) + "?mode=ro"
    if wal_mode(path) and not os.path.exists(path + "-wal"):
        uri += "&immutable=1"
    conn = sqlite3.connect(uri, uri=True, timeout=5)
    conn.execute("PRAGMA query_only=ON")
    conn.row_factory = sqlite3.Row
    return conn


def dec(value):
    try:
        return Decimal(str(value)) if value is not None else None
    except InvalidOperation:
        return None


def num(d):
    """A quantity as AllocationRow.Num writes it: no exponent and no trailing zero."""
    text = format(d, "f")
    return text.rstrip("0").rstrip(".") if "." in text else text


def money(d):
    return None if d is None else str(d.quantize(Decimal("0.0001")))


def short(text, n=110):
    one = re.sub(r"\s+", " ", str(text or "")).strip()
    return one if len(one) <= n else one[: n - 1] + "…"


def charge(row):
    """What a launch counts for: its reservation while open, its cost once closed (its reservation where it ended unpriced)."""
    if row["state"] == W.ATTEMPT_LAUNCHED:
        return dec(row["reserved_cost"]) or Decimal(0)
    return dec(row["cost"]) if row["cost"] is not None else (dec(row["reserved_cost"]) or Decimal(0))


def position_of(row_role):
    return row_role or W.ROLE_DEFAULT


def turn_context(text):
    """What a launch's ai_attempt.context says, or nothing where it holds nothing readable."""
    try:
        read = json.loads(text) if text else {}
    except ValueError:
        return {}
    return read if isinstance(read, dict) else {}


class Digest:
    def __init__(self, app, tape, state, now, since, opts):
        self.app, self.tape, self.state, self.now, self.since, self.opts = app, tape, state, now, since, opts
        self.day_from, self.day_to = local_day(now)
        self.since_s, self.day_from_s, self.day_to_s, self.now_s = stored(since), stored(self.day_from), stored(self.day_to), stored(now)
        self.days = max((now - since).total_seconds() / 86400, 1 / 24)   # the rates' denominator: an hour at least, so a first turn is not a rate of thousands
        self._columns = {}

    def columns(self, table):
        if table not in self._columns:
            self._columns[table] = {r["name"] for r in self.app.execute(f"PRAGMA table_info({table})")}
        return self._columns[table]

    def has(self, table, *columns):
        have = self.columns(table)
        return bool(have) and all(c in have for c in columns)

    def per_day(self, n):
        return round(n / self.days, 2)

    # -- 0. which home this is, and whether the AI is working
    def app_state(self):
        schema = self.app.execute("SELECT value FROM meta WHERE key = ?", (W.META_SCHEMA_VERSION,)).fetchone()
        working = None
        pause = None
        row = self.app.execute("SELECT value FROM kv WHERE key = ?", (W.KV_SETTINGS,)).fetchone() if self.has("kv", "key", "value") else None
        if row:
            try:
                settings = json.loads(row[0])     # two facts are taken from it and the rest is never kept or shown
                if isinstance(settings, dict) and W.SETTING_WORKS_ON_ITS_OWN in settings:
                    working = bool(settings[W.SETTING_WORKS_ON_ITS_OWN])
                if isinstance(settings, dict) and isinstance(settings.get(W.SETTING_DISK_PAUSE), dict):
                    p = settings[W.SETTING_DISK_PAUSE]
                    pause = {"at": p.get(W.PAUSE_AT), "free": p.get(W.PAUSE_FREE), "total": p.get(W.PAUSE_TOTAL), "floor": p.get(W.PAUSE_FLOOR),
                             "unreadable": bool(p.get(W.PAUSE_UNREADABLE))}
            except ValueError:
                pass
        return {"schema": schema[0] if schema else None, "ai_works_on_its_own": working, "disk_pause": pause}

    # -- 1. positions by status and model
    def positions(self):
        rows = self.app.execute("SELECT p.id, p.unit_id, p.status, p.seat_runtime, p.seat_model FROM org_position p ORDER BY p.id").fetchall()
        groups = {}
        for r in rows:
            key = (r["status"], r["seat_model"] or "(owner's setting)")
            groups.setdefault(key, []).append(r["id"])
        ran = self.app.execute(
            "SELECT COALESCE(effective_model, requested_model, 'unknown') AS model, COUNT(*) AS n FROM ai_attempt "
            "WHERE started_at >= ? GROUP BY 1 ORDER BY n DESC, 1", (self.since_s,)).fetchall()
        return {
            "total": len(rows),
            "by_status_model": [{"status": s, "model": m, "count": len(ids), "ids": ids[:6]}
                                for (s, m), ids in sorted(groups.items())],
            "models_run_since": [{"model": r["model"], "turns": r["n"]} for r in ran],
        }

    # -- 2. turns and cost per position, today and since T0
    def turns(self):
        floor = min(self.day_from_s, self.since_s)
        rows = self.app.execute(
            "SELECT role, state, reserved_cost, cost, started_at, exit_code FROM ai_attempt WHERE started_at >= ? AND started_at < ?",
            (floor, self.now_s + "~")).fetchall()
        per = {}
        for r in rows:
            p = per.setdefault(position_of(r["role"]), {"position": position_of(r["role"]), "board_seat": position_of(r["role"]).startswith(W.BOARD_PREFIX),
                                                        "turns_today": 0, "cost_today": Decimal(0), "turns_since": 0, "cost_since": Decimal(0)})
            c = charge(r)
            if r["started_at"] >= self.day_from_s:
                p["turns_today"] += 1
                p["cost_today"] += c
            if r["started_at"] >= self.since_s:
                p["turns_since"] += 1
                p["cost_since"] += c
        out = sorted(per.values(), key=lambda p: (-p["cost_since"], p["position"]))
        total = {k: sum((p[k] for p in out), Decimal(0) if "cost" in k else 0) for k in ("turns_today", "cost_today", "turns_since", "cost_since")}
        for p in out + [total]:
            p["cost_today"], p["cost_since"] = money(p["cost_today"]), money(p["cost_since"])
        return {"positions": out, "total": total, "cost_is": "the AI's list-price equivalent, not a bill"}

    # -- 3. org events by kind since T0
    def org_events(self):
        rows = self.app.execute(
            "SELECT kind, decider, COUNT(*) AS n, MAX(at) AS last FROM org_event WHERE at >= ? GROUP BY kind, decider", (self.since_s,)).fetchall()
        kinds = {}
        for r in rows:
            k = kinds.setdefault(r["kind"], {"kind": r["kind"], "count": 0, "last_at": None, "deciders": {}})
            k["count"] += r["n"]
            k["deciders"][r["decider"]] = r["n"]
            k["last_at"] = max(k["last_at"] or "", r["last"] or "") or None
        mandates = []
        try:
            mandates = [{"kind": r["kind"], "count": r["n"]} for r in self.app.execute(
                "SELECT kind, COUNT(*) AS n FROM org_mandate_event WHERE at >= ? GROUP BY kind ORDER BY n DESC, kind", (self.since_s,))]
        except sqlite3.Error:
            pass
        out = sorted(kinds.values(), key=lambda k: (-k["count"], k["kind"]))
        return {"events": out, "total": sum(k["count"] for k in out), "mandate_events": mandates}

    # -- 4. verdicts asked / given / inconclusive
    def verdicts(self):
        one = lambda sql, *a: self.app.execute(sql, a).fetchone()[0]
        asked = one("SELECT COUNT(*) FROM strategy_verdict WHERE requested_at >= ?", self.since_s)
        waiting = one("SELECT COUNT(*) FROM strategy_verdict v WHERE NOT EXISTS (SELECT 1 FROM strategy_promotion p "
                      "WHERE p.campaign_id = v.campaign_id AND p.version_id = v.version_id)")
        given = [{"verdict": r["verdict"], "reason": r["reason"], "count": r["n"]} for r in self.app.execute(
            "SELECT verdict, reason, COUNT(*) AS n FROM strategy_promotion WHERE at >= ? GROUP BY verdict, reason ORDER BY n DESC, verdict, reason",
            (self.since_s,))]
        inconclusive = None
        try:
            inconclusive = one("SELECT COUNT(*) FROM referee_holdout WHERE addis_status = ? AND at >= ?", W.ADDIS_INCONCLUSIVE, self.since_s)
        except sqlite3.Error:
            pass
        return {"asked_since": asked, "waiting_now": waiting, "given_since": given, "given_total": sum(g["count"] for g in given),
                "no_trade_refusals_since": sum(g["count"] for g in given if g["reason"] == W.REASON_NO_TRADE),
                "referee_inconclusive_since": inconclusive,
                "words": "asked = strategy_verdict rows requested since T0; given = promotion rows written since T0; waiting = asked, not yet given (ever)"}

    # -- 5. paper deployments and their P&L (a simulation)
    def deployments(self):
        allocs = self.app.execute(
            "SELECT COUNT(*) AS n, SUM(CASE WHEN effective_to IS NULL THEN 1 ELSE 0 END) AS open, "
            "SUM(CASE WHEN at >= ? THEN 1 ELSE 0 END) AS new FROM strategy_allocation WHERE policy_version <> ?",
            (self.since_s, W.POLICY_STOP)).fetchone()
        stops = self.app.execute("SELECT COUNT(*) FROM strategy_allocation WHERE policy_version = ? AND at >= ?", (W.POLICY_STOP, self.since_s)).fetchone()[0]
        rows = self.app.execute(
            "SELECT id, version_id, connector_id, account_id, symbol, mode, state, started_at, ended_at, end_reason, suspended_reason "
            "FROM strategy_deployment ORDER BY started_at DESC, id").fetchall()
        by = {}
        for r in rows:
            by[(r["mode"], r["state"])] = by.get((r["mode"], r["state"]), 0) + 1
        listed = []
        for r in rows:
            if r["state"] == W.DEPLOYMENT_ENDED and (parse_ts(r["ended_at"]) or self.now) < self.since:
                continue
            fills = self.app.execute(
                "SELECT f.at, f.side, f.quantity, f.price, f.fee FROM fill f WHERE f.account_id = ? AND f.symbol = ? AND "
                "(f.request_id IN (SELECT request_id FROM deployment_op WHERE deployment_id = ?) OR "
                " f.client_order_id IN (SELECT 'TA-' || request_id FROM deployment_op WHERE deployment_id = ?)) ORDER BY f.at, f.rowid",
                (r["account_id"], r["symbol"], r["id"], r["id"])).fetchall()
            listed.append({"id": r["id"][:12], "symbol": r["symbol"], "mode": r["mode"], "state": r["state"], "account": r["account_id"],
                           "started_at": r["started_at"], "end_reason": r["end_reason"], "suspended_reason": r["suspended_reason"],
                           **self.pnl(fills, r["account_id"], r["symbol"]),
                           "unprotected": None if r["state"] == W.DEPLOYMENT_ENDED else self.unprotected(r["id"], r["started_at"])})
        return {"label": "SIMULATION: paper deployments, modelled fills; not money", "allocations": {
                    "total": allocs["n"], "open": allocs["open"] or 0, "new_since": allocs["new"] or 0, "stopped_since": stops},
                "by_mode_state": [{"mode": m, "state": s, "count": n} for (m, s), n in sorted(by.items())],
                "deployments": listed, "envelopes": self.envelopes(), "notes": self.paper_notes()}

    def unprotected(self, deployment, started_at):
        """THE RUN'S OWN UNPROTECTED SENTENCE, OR NONE: TradingGateway.DeploymentReadings' reading, by its rule, off the rows it reads. The run's book is its
        fills under its own operations since it started, walked long or flat as RunBookWalk walks them; a position held with a stop or a target it declared
        and that is not at the venue reads in the app's words. UNKNOWN, a row not yet answered and an order not yet over read as working, as in the app:
        that direction never sells twice, and here it never cries wolf. A run that is still being placed is the app's to say; this reads only its rows."""
        ops = [dict(o) for o in self.app.execute(
            "SELECT request_id, kind, state, answer FROM deployment_op WHERE deployment_id = ? ORDER BY bar_open_time ASC, created_at ASC, request_id ASC",
            (deployment,))]
        requests = {r["request_id"]: dict(r) for r in self.app.execute(
            "SELECT r.request_id, r.execution_state, r.dispatched_at, r.filled_quantity FROM execution_request r "
            "WHERE r.request_id IN (SELECT request_id FROM deployment_op WHERE deployment_id = ?)", (deployment,))}
        start = parse_ts(started_at)
        held, average, odd_side, in_ledger = Decimal(0), Decimal(0), False, {}
        for f in self.app.execute("SELECT request_id, at, side, quantity, price FROM fill WHERE request_id IN "
                                  "(SELECT request_id FROM deployment_op WHERE deployment_id = ?) ORDER BY at, rowid", (deployment,)):
            at = parse_ts(f["at"])
            if start is not None and at is not None and at < start:
                continue
            q = dec(f["quantity"]) or Decimal(0)
            in_ledger[f["request_id"]] = in_ledger.get(f["request_id"], Decimal(0)) + q
            side = str(f["side"]).casefold()
            odd_side = odd_side or side not in (W.SIDE_BUY.casefold(), W.SIDE_SELL.casefold())
            if side == W.SIDE_BUY.casefold():
                held += q
            else:
                held -= min(held, q)
        complete = not odd_side and all(dec(requests[o["request_id"]]["filled_quantity"]) == in_ledger.get(o["request_id"], Decimal(0))
                                        for o in ops if o["request_id"] in requests)
        if held <= 0:
            return None

        def row(op):
            return requests.get(op["request_id"])

        def before_the_wire(op):
            r = row(op)
            return op["state"] == W.DEPLOYMENT_OP_REFUSED and (r is None or (not r["dispatched_at"] and r["execution_state"] != W.ORDER_AWAITING_APPROVAL))

        def settled(op):
            return op["state"] in (W.DEPLOYMENT_OP_RESOLVED, W.DEPLOYMENT_OP_REFUSED)

        def fill_unbooked(op):
            r = row(op)
            return not complete and r is not None and (dec(r["filled_quantity"]) or Decimal(0)) > 0

        def may_be_working(op):
            if before_the_wire(op):
                return False
            r = row(op)
            return not settled(op) if r is None else r["execution_state"] not in W.ORDER_TERMINAL

        def on_its_way(op):
            if before_the_wire(op):
                return False
            r = row(op)
            return not settled(op) if r is None else (r["execution_state"] not in W.ORDER_TERMINAL or fill_unbooked(op))

        moves = [o for o in ops if o["kind"] in (W.DEP_OP_ENTRY, W.DEP_OP_EXIT, W.DEP_OP_FLATTEN)]
        if not moves or (moves[-1]["kind"] != W.DEP_OP_ENTRY and on_its_way(moves[-1])):
            return None
        entry = max((i for i, o in enumerate(ops) if o["kind"] == W.DEP_OP_ENTRY), default=-1)
        if entry < 0:
            return None
        missing = []
        for kind in (W.DEP_OP_STOP, W.DEP_OP_TARGET):
            asked = [o for o in ops[entry + 1:] if o["kind"] == kind]
            if asked and not any(o["kind"] == kind and may_be_working(o) for o in ops) and not fill_unbooked(asked[-1]):
                missing.append(asked[-1])
        if not missing:
            return None
        what = (W.UNPROTECTED_NO_BOTH if len(missing) == 2 else W.UNPROTECTED_NO_STOP if missing[0]["kind"] == W.DEP_OP_STOP else W.UNPROTECTED_NO_TARGET)
        return (f"{W.UNPROTECTED_HOLDS}{num(held)} with {what}{W.UNPROTECTED_RESTING}{missing[0]['answer'] or W.UNPROTECTED_NONE_YET})"
                f"{W.UNPROTECTED_ASKS_AGAIN}")

    def envelopes(self):
        """Evidence per pair: each pair's standing paper envelope and how many of its runs are open."""
        if not self.has("paper_envelope", "symbol", "max_deployments", "expires_at", "withdrawn_at"):
            return None
        rows = self.app.execute(
            "SELECT e.symbol, e.max_deployments, e.max_quantity, e.max_notional, e.currency, e.expires_at, "
            "(SELECT COUNT(*) FROM strategy_deployment d WHERE d.envelope_id = e.id AND d.state <> ?) AS open "
            "FROM paper_envelope e WHERE e.withdrawn_at IS NULL AND e.expires_at > ? ORDER BY e.symbol, e.granted_at", (W.DEPLOYMENT_ENDED, self.now_s)).fetchall()
        return [dict(r) for r in rows]

    def paper_notes(self):
        """What the app told a run's author: the notes it published, and the last one's words (a simulation's figures, never money)."""
        rows = self.app.execute(
            "SELECT content, created_at FROM publication WHERE role = ? AND kind = ? AND created_at >= ? ORDER BY created_at DESC, id",
            (W.PUBLICATION_ALLOCATOR, W.PUBLICATION_NOTE, self.since_s)).fetchall()
        bare = [r for r in rows if W.UNPROTECTED_AT_CLOSE in (r["content"] or "")]
        return {"since": len(rows), "last_at": rows[0]["created_at"] if rows else None, "last": short(rows[0]["content"], 150) if rows else None,
                "unprotected_at_close_since": len(bare), "unprotected_at_close_last_at": bare[0]["created_at"] if bare else None}

    def pnl(self, fills, account, symbol):
        qty, avg, realised, fees, unknown_fees = Decimal(0), Decimal(0), Decimal(0), Decimal(0), 0
        for f in fills:
            q, p = dec(f["quantity"]) or Decimal(0), dec(f["price"]) or Decimal(0)
            signed = -q if str(f["side"]).casefold() == W.SIDE_SELL.casefold() else q     # the app compares a side ignoring case
            if f["fee"] is None:
                unknown_fees += 1
            else:
                fees += dec(f["fee"]) or Decimal(0)
            if qty == 0 or (qty > 0) == (signed > 0):
                avg = (avg * abs(qty) + p * abs(signed)) / (abs(qty) + abs(signed)) if (abs(qty) + abs(signed)) else avg
                qty += signed
            else:
                closed = min(abs(signed), abs(qty))
                realised += (p - avg) * closed * (1 if qty > 0 else -1)
                qty += signed
                if qty != 0 and (qty > 0) == (signed > 0):
                    avg = p                      # the fill was bigger than the position: the remainder opens at its own price
        mark = self.paper_mark(account, symbol)
        unrealised = (mark - avg) * qty if (mark is not None and qty != 0) else (Decimal(0) if qty == 0 else None)
        return {"fills": len(fills), "realised": money(realised), "fees": money(fees), "fees_unknown_fills": unknown_fees,
                "open_quantity": str(qty), "unrealised": money(unrealised) if unrealised is not None else None,
                "net_if_known": money(realised - fees + unrealised) if (unrealised is not None and unknown_fees == 0) else None}

    def paper_mark(self, account, symbol):
        for name in {f"paper-{account}.db", "paper-" + re.sub(r"[^A-Za-z0-9._-]", "_", account) + ".db"}:
            path = os.path.join(self.state, name)
            if not os.path.isfile(path):
                continue
            try:
                book = open_ro(path)
                try:
                    row = book.execute("SELECT value FROM paper_meta WHERE key = ?", ("close:" + symbol.upper(),)).fetchone()
                finally:
                    book.close()
                return dec(row[0]) if row else None
            except sqlite3.Error:
                return None
        return None

    # -- 6. refusals, grouped with their words and counts
    def refusals(self):
        tools = [{"source": "tool", "what": r["tool"], "words": short(r["refusal"]), "count": r["n"], "last_at": r["last"]} for r in self.app.execute(
            "SELECT tool, refusal, COUNT(*) AS n, MAX(at) AS last FROM tool_call WHERE served = 0 AND at >= ? "
            "GROUP BY tool, refusal ORDER BY n DESC, tool", (self.since_s,))]
        pipe = {}
        marks = ",".join("?" * len(W.PIPE_REFUSALS))
        for r in self.app.execute(f"SELECT event, metadata, at FROM engineering_log WHERE component = ? AND event IN ({marks}) AND at >= ?",
                                  (W.LOG_IPC, *W.PIPE_REFUSALS, self.since_s)):
            meta = {}
            try:
                meta = json.loads(r["metadata"]) if r["metadata"] else {}
            except ValueError:
                pass
            op, code = meta.get(W.REFUSAL_OP) if isinstance(meta, dict) else None, meta.get(W.REFUSAL_CODE) if isinstance(meta, dict) else None
            key = (r["event"], op or "-", code or "-")
            e = pipe.setdefault(key, {"source": "pipe", "what": r["event"], "words": f"op {op or '-'} / {code or '-'}", "count": 0, "last_at": None})
            e["count"] += 1
            e["last_at"] = max(e["last_at"] or "", r["at"] or "")
        ops = [{"source": "deployment-op", "what": r["kind"], "words": short(r["answer"]), "count": r["n"], "last_at": r["last"]} for r in self.app.execute(
            "SELECT kind, answer, COUNT(*) AS n, MAX(created_at) AS last FROM deployment_op WHERE state = ? AND created_at >= ? "
            "GROUP BY kind, answer ORDER BY n DESC, kind", (W.DEPLOYMENT_OP_REFUSED, self.since_s))]
        groups = sorted(tools + list(pipe.values()) + ops, key=lambda g: (-g["count"], g["source"], g["what"]))
        return {"total": sum(g["count"] for g in groups), "groups": groups}

    # -- 7a. the app's own errors: every engineering_log line it wrote at its error severity since T0, by where and what. Not one is
    #        judged here: a paper run's protection that failed to record and a dropped connection read alike, and the watcher reads each.
    def app_errors(self):
        groups = [{"component": r["component"], "event": r["event"], "count": r["n"], "first_at": r["first"], "last_at": r["last"]} for r in self.app.execute(
            "SELECT component, event, COUNT(*) AS n, MIN(at) AS first, MAX(at) AS last FROM engineering_log WHERE severity = ? AND at >= ? "
            "GROUP BY component, event ORDER BY n DESC, component, event", (W.SEVERITY_ERROR, self.since_s))]
        return {"total": sum(g["count"] for g in groups), "groups": groups}

    # -- 7. ai_attempt errors
    def errors(self):
        rows = self.app.execute(
            "SELECT id, role, state, exit_code, vendor_said, offered_tools, started_at, context, COALESCE(effective_model, requested_model) AS model "
            "FROM ai_attempt WHERE started_at >= ? ORDER BY started_at", (self.since_s,)).fetchall()
        groups, exits = {}, {}
        bad = 0
        for r in rows:
            ctx = turn_context(r["context"])
            stopped = ctx.get(W.CTX_ENDED) if ctx.get(W.CTX_ENDED) in W.TURN_STOPS else None
            failed = (r["exit_code"] not in (None, 0)) or r["vendor_said"] or stopped or r["state"] not in (W.ATTEMPT_ENDED, W.ATTEMPT_LAUNCHED)
            if r["exit_code"] is not None:
                exits[str(r["exit_code"])] = exits.get(str(r["exit_code"]), 0) + 1
            if not failed:
                continue
            bad += 1
            said = short(r["vendor_said"], 120) if r["vendor_said"] else ""
            words = short(ctx.get(W.CTX_REFUSED), 120) if stopped and ctx.get(W.CTX_REFUSED) else None
            key = (r["state"], r["exit_code"], said, stopped, words)
            g = groups.setdefault(key, {"state": key[0], "exit_code": r["exit_code"], "vendor_said": said or None, "ended": stopped, "words": words, "count": 0,
                                        "last_at": None, "positions": set(), "model": r["model"], "offered_tools": None})
            g["count"] += 1
            g["last_at"] = r["started_at"]
            g["positions"].add(position_of(r["role"]))
            if r["offered_tools"]:
                g["offered_tools"] = len([t for t in r["offered_tools"].splitlines() if t.strip()])
        out = sorted(groups.values(), key=lambda g: (-g["count"], g["last_at"]))
        for g in out:
            g["positions"] = sorted(g["positions"])[:4]
        return {"attempts_since": len(rows), "failed_since": bad, "exit_codes": exits, "groups": out}

    # -- 8. the AI day-total used vs the board's
    def day_total(self):
        used = {W.BILLING_PLAN: Decimal(0), W.BILLING_KEY: Decimal(0), W.BILLING_UNKNOWN: Decimal(0)}
        board = Decimal(0)
        for r in self.app.execute("SELECT role, state, reserved_cost, cost, billing_class FROM ai_attempt WHERE started_at >= ? AND started_at < ?",
                                  (self.day_from_s, self.day_to_s)):
            c = charge(r)
            used[r["billing_class"] if r["billing_class"] in used else W.BILLING_UNKNOWN] += c
            if (r["role"] or "").startswith(W.BOARD_PREFIX):
                board += c
        policy = self.app.execute("SELECT version, mode, body FROM board_policy ORDER BY version DESC LIMIT 1").fetchone()
        plan_total, mode, version = None, None, None
        if policy:
            version = policy["version"]
            try:
                body = json.loads(policy["body"])
                plan_total, mode = dec(body.get(W.BODY_PLAN_DAY_TOTAL)), body.get(W.BODY_MODE) or policy["mode"]
            except ValueError:
                pass
        share = None
        try:
            row = self.app.execute("SELECT budget_share FROM board_event WHERE kind IN (?, ?) ORDER BY id DESC LIMIT 1", (W.BOARD_FOUNDED, W.BOARD_BUDGET_SHARE)).fetchone()
            share = dec(row[0]) if row else None
        except sqlite3.Error:
            pass
        plan_counted = used[W.BILLING_PLAN] + used[W.BILLING_UNKNOWN]
        return {
            "day": {"from": stored(self.day_from), "to": stored(self.day_to)},
            "used": {k: money(v) for k, v in used.items()}, "plan_counted": money(plan_counted),
            "board_policy": {"version": version, "mode": mode, "plan_day_total": money(plan_total)},
            "plan_used_of_board_pct": (str((plan_counted / plan_total * 100).quantize(Decimal("0.1"))) if plan_total else None),
            "board_seats_spent": money(board), "board_share": str(share) if share is not None else None,
            "board_seats_ceiling": money(share * plan_total) if (share is not None and plan_total) else None,
            "words": "plan_counted = plan + unknown launches (an unknown counts against both ceilings); the owner's real-money limit is his setting, not read here",
        }

    # -- 9. the next wake per active position
    def why_not_turned(self):
        """WHY EACH POSITION DID NOT TURN, the newest record since T0: the loop's own line for a turn it did not start, or the word on a launch stopped by
        its limit or by the vendor, and none once a clean turn has come after it. A line is told once per cause, so it can be older than the wake it explains:
        it carries its time. Nothing here is inferred."""
        titles = {W.TITLE_OPERATIONS: W.ROLE_DEFAULT, W.TITLE_RESEARCH: W.ROLE_RESEARCH}
        why = {}
        for r in self.app.execute("SELECT at, metadata FROM engineering_log WHERE component = ? AND event = ? AND at >= ? ORDER BY id",
                                  (W.LOG_MISSION, W.LOG_TURN_REFUSED, self.since_s)):
            line = turn_context(r["metadata"]).get(W.LOG_WHY)
            if isinstance(line, str) and ": " in line:
                title, words = line.split(": ", 1)
                why[titles.get(title, title)] = {"at": r["at"], "ended": None, "words": short(words)}
        for r in self.app.execute("SELECT role, started_at, state, exit_code, context FROM ai_attempt WHERE started_at >= ? ORDER BY started_at", (self.since_s,)):
            ctx = turn_context(r["context"])
            pos = position_of(r["role"])
            had = why.get(pos)
            if had is not None and parse_ts(had["at"]) > parse_ts(r["started_at"]):
                continue
            if ctx.get(W.CTX_ENDED) in W.TURN_STOPS:
                why[pos] = {"at": r["started_at"], "ended": ctx.get(W.CTX_ENDED), "words": short(ctx.get(W.CTX_REFUSED) or "")}
            elif r["state"] == W.ATTEMPT_ENDED and r["exit_code"] == 0:
                why.pop(pos, None)
        return why

    def wakes(self):
        pending = {}
        for r in self.app.execute("SELECT role, kind, due_at, payload FROM mission_event WHERE consumed_at IS NULL ORDER BY due_at, id"):
            p = pending.setdefault(position_of(r["role"]), {"next_due": r["due_at"], "kind": r["kind"], "pending": 0, "chief_floor": r["payload"] == W.WAKE_CHIEF_FLOOR})
            p["pending"] += 1
        last = {position_of(r["role"]): r["last"] for r in self.app.execute(
            "SELECT role, MAX(consumed_at) AS last FROM mission_event WHERE consumed_at IS NOT NULL GROUP BY role")}
        active = [r["id"] for r in self.app.execute("SELECT id FROM org_position WHERE status = ? ORDER BY id", (W.POSITION_ACTIVE,))]
        out = []
        for pid in active:
            p = pending.get(pid)
            due = parse_ts(p["next_due"]) if p else None
            out.append({"position": pid, "next_due": p["next_due"] if p else None, "kind": p["kind"] if p else None,
                        "in_seconds": int((due - self.now).total_seconds()) if due else None, "pending": p["pending"] if p else 0,
                        "chief_floor": p["chief_floor"] if p else False, "last_consumed_at": last.get(pid)})
        why = self.why_not_turned()
        for o in out:
            o["why"] = why.get(o["position"]) if o["in_seconds"] is not None and o["in_seconds"] < 0 else None
        dispositions, lost, failed = {}, 0, 0
        # A LOST TURN IS NOT A FAILED ONE: a restart took it, and its wakes read `failed` only where the loop has settled them. The
        # launch's own state tells them apart; a disposition this file has not heard of stands as its own word.
        for r in self.app.execute(
                "SELECT e.disposition, a.state, COUNT(*) AS n FROM mission_event e LEFT JOIN ai_attempt a ON a.id = e.consumed_by "
                "WHERE e.consumed_at >= ? GROUP BY e.disposition, a.state", (self.since_s,)):
            word = r["disposition"] or "none"
            dispositions[word] = dispositions.get(word, 0) + r["n"]
            if r["state"] == W.ATTEMPT_LOST:
                lost += r["n"]
            elif word == W.DISPOSITION_FAILED:
                failed += r["n"]
        after_lost = self.app.execute("SELECT COUNT(*) FROM mission_event WHERE id LIKE ? AND created_at >= ?",
                                      ("%" + W.WAKE_AFTER_LOST, self.since_s)).fetchone()[0]
        blocked = [{"words": short(r["disposition_detail"]), "count": r["n"], "roles": r["roles"].split(",")[:4] if r["roles"] else [], "oldest_due": r["oldest"]}
                   for r in self.app.execute(
                       "SELECT disposition_detail, COUNT(*) AS n, GROUP_CONCAT(DISTINCT COALESCE(role, ?)) AS roles, MIN(due_at) AS oldest FROM mission_event "
                       "WHERE consumed_at IS NULL AND disposition = ? GROUP BY disposition_detail ORDER BY n DESC", (W.ROLE_DEFAULT, W.DISPOSITION_BLOCKED))]
        return {"active_positions": out, "consumed_since_by_disposition": dispositions, "failed_turn_wakes_since": failed, "lost_turn_wakes_since": lost,
                "raised_again_after_lost_since": after_lost, "blocked_now": blocked,
                "overdue": [o["position"] for o in out if o["in_seconds"] is not None and o["in_seconds"] < 0]}

    # -- 10. R26 § 4 (1): where the loop breaks: each arrow, first reached and how often, and its first refusal in words
    def arrow_specs(self):
        t0 = self.since_s
        events = lambda kind: ("org_event", "SELECT MIN(at), COUNT(*) FROM org_event WHERE kind = ? AND at >= ?", (kind, t0))
        mandate = lambda kind: ("org_mandate_event", "SELECT MIN(at), COUNT(*) FROM org_mandate_event WHERE kind = ? AND at >= ?", (kind, t0))
        decided = (("UNION ALL SELECT at FROM org_decision WHERE kind = ? AND at >= ?", lambda kind: (kind, t0)) if self.has("org_decision", "kind", "at")
                   else ("", lambda kind: ()))
        return (
            ("charter", events(W.ORG_CHARTERED), (W.OP_ORG_CHARTER,)),
            ("hire", events(W.ORG_HIRED), (W.OP_ORG_HIRE,)),
            # A hire that activated itself is decided by the hiring head; the owner's own activation is not this arrow.
            ("self-activation", ("org_event", "SELECT MIN(at), COUNT(*) FROM org_event WHERE kind = ? AND decider NOT IN (?, ?, ?) AND at >= ?",
                                 (W.ORG_ACTIVATED, W.DECIDER_OWNER, W.DECIDER_APP, W.DECIDER_BOARD, t0)), ()),
            ("mandate", mandate(W.MANDATE_ISSUED), (W.OP_MANDATE_ASSIGN,)),
            ("delivery", mandate(W.MANDATE_DELIVERED), (W.OP_MANDATE_DELIVER,)),
            ("accepted", mandate(W.MANDATE_ACCEPTED), (W.OP_MANDATE_ACCEPT,)),
            ("rejected", mandate(W.MANDATE_REJECTED), (W.OP_MANDATE_REJECT,)),
            ("backtest", ("strategy_run", "SELECT MIN(created_at), COUNT(*) FROM strategy_run WHERE created_at >= ? AND id NOT IN "
                                          "(SELECT holdout_run_id FROM strategy_promotion WHERE holdout_run_id IS NOT NULL)", (t0,)), (W.OP_BACKTEST,)),
            ("verdict asked", ("strategy_verdict", "SELECT MIN(requested_at), COUNT(*) FROM strategy_verdict WHERE requested_at >= ?", (t0,)), (W.OP_VERDICT,)),
            ("verdict given", ("strategy_promotion", "SELECT MIN(at), COUNT(*) FROM strategy_promotion WHERE at >= ?", (t0,)), ()),
            # An owner's or organisation's stop is written as an allocation row too (D19): it is not an allocation.
            ("paper allocation", ("strategy_allocation", "SELECT MIN(at), COUNT(*) FROM strategy_allocation WHERE mode = ? AND policy_version <> ? AND at >= ?",
                                  (W.MODE_PAPER, W.POLICY_STOP, t0)), ()),
            ("paper run", ("strategy_deployment", "SELECT MIN(started_at), COUNT(*) FROM strategy_deployment WHERE mode = ? AND started_at >= ?", (W.MODE_PAPER, t0)), ()),
            ("paper fill", ("deployment_op", "SELECT MIN(f.at), COUNT(*) FROM fill f JOIN deployment_op o ON (f.request_id = o.request_id OR "
                                             "f.client_order_id = ? || o.request_id) JOIN strategy_deployment d ON d.id = o.deployment_id "
                                             "WHERE d.mode = ? AND f.at >= ?", (W.CLIENT_ORDER_PREFIX, W.MODE_PAPER, t0)), ()),
            ("note raised", ("mission_event", "SELECT MIN(created_at), COUNT(*) FROM mission_event WHERE id LIKE ? AND created_at >= ?",
                             (W.WAKE_PAPER_RUN + "%", t0)), ()),
            ("note taken", ("mission_event", "SELECT MIN(consumed_at), COUNT(*) FROM mission_event WHERE id LIKE ? AND consumed_at >= ? "
                                             "AND consumed_by IN (SELECT id FROM ai_attempt)", (W.WAKE_PAPER_RUN + "%", t0)), ()),
            # NO VERB WRITES family-kill OR family-branch ON MAIN (OrgDecisionKind.Mandatory names them; Decide refuses them): a kill is the
            # author's ledger entry, and an iteration is a new version from a position that had taken a run's note. The decision words
            # stay so a verb that lands is counted at once.
            ("kill", ("ledger_entry", "SELECT MIN(at), COUNT(*) FROM (SELECT created_at AS at FROM ledger_entry WHERE kind = ? AND created_at >= ? " + decided[0] + ")",
                      (W.LEDGER_KILL, t0, *(decided[1](W.DECISION_FAMILY_KILL)))), ()),
            ("iterate", ("strategy_version", "SELECT MIN(at), COUNT(*) FROM (SELECT v.created_at AS at FROM strategy_version v WHERE v.created_at >= ? AND EXISTS "
                                             "(SELECT 1 FROM mission_event e WHERE e.id LIKE ? AND e.role = v.role AND e.consumed_at < v.created_at "
                                             "AND e.consumed_by IN (SELECT id FROM ai_attempt)) " + decided[0] + ")",
                         (t0, W.WAKE_PAPER_RUN + "%", *(decided[1](W.DECISION_FAMILY_BRANCH)))), ()),
        )

    def arrows(self):
        refused = {}
        first_any = None
        for r in self.app.execute("SELECT at, tool, argument, refusal FROM tool_call WHERE served = 0 AND at >= ? ORDER BY at, id", (self.since_s,)):
            op = r["tool"]
            if op in W.TOOLS_CARRYING_AN_OP:         # GrantedWorkerTools.Summary: "trade backtest", the op the tool was asked for
                parts = (r["argument"] or "").split()
                op = parts[1] if len(parts) > 1 and parts[0] == op else op
            item = {"at": r["at"], "what": op, "words": short(r["refusal"])}
            first_any = first_any or item
            refused.setdefault(op.replace("_", "-"), []).append(item)
        for r in self.app.execute("SELECT metadata, at FROM engineering_log WHERE component = ? AND event = ? AND at >= ? ORDER BY at, id",
                                  (W.LOG_IPC, W.LOG_OP_REFUSED, self.since_s)):
            try:
                meta = json.loads(r["metadata"]) if r["metadata"] else {}
            except ValueError:
                meta = {}
            op = meta.get(W.REFUSAL_OP) if isinstance(meta, dict) else None
            if op:
                item = {"at": r["at"], "what": op, "words": f"refused at the pipe: {meta.get(W.REFUSAL_CODE) or '-'}"}
                first_any = first_any if first_any and first_any["at"] <= item["at"] else item
                refused.setdefault(op.replace("_", "-"), []).append(item)
        rows = []
        for name, (table, sql, args), tools in self.arrow_specs():
            row = {"arrow": name, "first_at": None, "count": 0, "refused": 0, "first_refused_at": None, "first_refusal": None}
            if self.has(table):
                first, count = self.app.execute(sql, args).fetchone()
                row.update(first_at=first, count=count)
            else:
                row["absent"] = table
            hits = sorted((i for t in tools for i in refused.get(t, [])), key=lambda i: i["at"])
            if hits:
                row.update(refused=len(hits), first_refused_at=hits[0]["at"], first_refusal=hits[0]["words"])
            rows.append(row)
        return {"arrows": rows, "reached": sum(1 for r in rows if r["count"]), "of": len(rows),
                "not_reached": [r["arrow"] for r in rows if not r["count"]], "first_refusal": first_any,
                "cannot_be_read": "a keep has no record of its own: a kill is the author's ledger entry, an iterate a new version after a run's note"}

    # -- 11. R26 § 4 (2): what a turn costs and how fast turns come
    def acted(self):
        """The launches since T0 that left a row of work under their own id, and the act tables this home could not answer for."""
        ids, unread = set(), []
        for table in ACT_TABLES:
            col = next((c for c in ("attempt_id", "attempt") if self.has(table, c)), None)
            if col is None:
                unread.append(table)
                continue
            ids.update(r[0] for r in self.app.execute(
                f"SELECT DISTINCT {col} FROM {table} WHERE {col} IN (SELECT id FROM ai_attempt WHERE started_at >= ?)", (self.since_s,)))
        return ids, unread

    def pace(self):
        rows = self.app.execute(
            "SELECT id, role, state, exit_code, reserved_cost, cost, vendor_said, started_at, ended_at, context, "
            "COALESCE(effective_model, requested_model) AS model FROM ai_attempt WHERE started_at >= ? ORDER BY started_at", (self.since_s,)).fetchall()
        acted, unread = self.acted()
        # A head's chart act names no launch, only its decider: it counts for the turn of that position it fell inside.
        chart = [(r["decider"], r["at"]) for r in self.app.execute(
            "SELECT decider, at FROM org_event WHERE at >= ? AND decider NOT IN (?, ?, ?)", (self.since_s, W.DECIDER_OWNER, W.DECIDER_APP, W.DECIDER_BOARD))]
        per, limit_hits, first_limit = {}, 0, None
        for r in rows:
            pos = position_of(r["role"])
            p = per.setdefault(pos, {"position": pos, "turns": 0, "cost": Decimal(0), "clean": 0, "idle": 0, "models": set(), "waits": []})
            p["turns"] += 1
            p["cost"] += charge(r)
            if r["model"]:
                p["models"].add(r["model"])
            if r["state"] == W.ATTEMPT_ENDED and r["exit_code"] == 0:
                p["clean"] += 1
                inside = any(who == pos and r["started_at"] <= at <= (r["ended_at"] or self.now_s) for who, at in chart)
                if r["id"] not in acted and not inside:
                    p["idle"] += 1
            ctx = turn_context(r["context"])
            if ctx.get(W.CTX_ENDED) == W.ENDED_USAGE_LIMIT or (r["vendor_said"] and VENDOR_LIMIT_WORDS.search(r["vendor_said"])):
                limit_hits += 1
                first_limit = first_limit or {"at": r["started_at"], "position": pos, "words": short(r["vendor_said"] or ctx.get(W.CTX_REFUSED) or "")}
        waits = []
        for r in self.app.execute("SELECT role, created_at, due_at, consumed_at FROM mission_event WHERE consumed_at >= ? AND consumed_by IN (SELECT id FROM ai_attempt)",
                                  (self.since_s,)):
            due = max(d for d in (parse_ts(r["created_at"]), parse_ts(r["due_at"])) if d is not None)
            took = max(0, int((parse_ts(r["consumed_at"]) - due).total_seconds()))
            waits.append(took)
            per.setdefault(position_of(r["role"]), {"position": position_of(r["role"]), "turns": 0, "cost": Decimal(0), "clean": 0, "idle": 0,
                                                    "models": set(), "waits": []})["waits"].append(took)
        out = []
        for p in sorted(per.values(), key=lambda p: (-p["cost"], p["position"])):
            out.append({"position": p["position"], "models": sorted(p["models"]), "turns": p["turns"], "turns_per_day": self.per_day(p["turns"]),
                        "cost_per_turn": money(p["cost"] / p["turns"]) if p["turns"] else None, "clean_turns": p["clean"], "idle_turns": p["idle"],
                        "wait_median_s": int(statistics.median(p["waits"])) if p["waits"] else None, "wait_max_s": max(p["waits"]) if p["waits"] else None})
        turns, cost = sum(p["turns"] for p in per.values()), sum((p["cost"] for p in per.values()), Decimal(0))
        clean, idle = sum(p["clean"] for p in per.values()), sum(p["idle"] for p in per.values())
        return {"days": round(self.days, 2), "turns": turns, "turns_per_day": self.per_day(turns), "cost_per_turn": money(cost / turns) if turns else None,
                "clean_turns": clean, "idle_turns": idle, "idle_share_pct": str((Decimal(idle) / clean * 100).quantize(Decimal("0.1"))) if clean else None,
                "wait_median_s": int(statistics.median(waits)) if waits else None, "wait_max_s": max(waits) if waits else None,
                "vendor_limit_refusals": limit_hits, "first_vendor_limit": first_limit, "positions": out, "act_tables_absent": unread,
                "words": "idle = a launch that ended cleanly and left no row of work under its id and no chart act in its span; "
                         "wait = a wake's due time to the launch that took it; vendor limits are read from the vendor's words"}

    # -- 12. R26 § 4 (3): how fast the run produces scorable signal
    def signal(self):
        return {"forecasts": self.forecasts(), "campaigns": self.campaigns(), "candidates": self.candidates(), "ledger": self.ledger(),
                "cannot_be_read": "whether a fresh session found a finding again from the ledger is not recorded anywhere"}

    def forecasts(self):
        if not self.has("org_forecast", "forecaster", "scored", "at") or not self.has("org_forecast_resolution", "forecast_id", "outcome", "at"):
            return {"absent": "org_forecast"}
        per = {}
        blank = lambda who: {"position": who, "written_since": 0, "scored_since": 0, "resolved_since": 0, "resolved_total": 0}
        for r in self.app.execute("SELECT forecaster, COUNT(*) AS n, SUM(scored) AS s FROM org_forecast WHERE at >= ? GROUP BY forecaster", (self.since_s,)):
            per[r["forecaster"]] = {**blank(r["forecaster"]), "written_since": r["n"], "scored_since": r["s"] or 0}
        for r in self.app.execute("SELECT f.forecaster, r.at FROM org_forecast_resolution r JOIN org_forecast f ON f.id = r.forecast_id "
                                  "WHERE f.scored = 1 AND r.outcome <> ?", (W.OUTCOME_VOID,)):
            p = per.setdefault(r["forecaster"], blank(r["forecaster"]))
            p["resolved_total"] += 1
            if r["at"] >= self.since_s:
                p["resolved_since"] += 1
        out = []
        for p in sorted(per.values(), key=lambda p: (-p["resolved_total"], p["position"])):
            rate = p["resolved_since"] / self.days
            left = max(0, FORECASTS_TO_SCORE - p["resolved_total"])
            out.append({**p, "written_per_day": self.per_day(p["written_since"]), "resolved_per_day": self.per_day(p["resolved_since"]),
                        "days_to_50": 0 if not left else (round(left / rate, 1) if rate else None)})
        return {"managers": out, "words": "resolved = scored and not void; days to 50 at the rate since T0; none = nothing resolved yet"}

    def campaigns(self):
        if not self.has("strategy_campaign", "trial_budget", "verdict_budget"):
            return {"absent": "strategy_campaign"}
        pots = self.has("strategy_campaign", "exploration_budget") and self.has("strategy_trial", "exploration", "charged_to")
        every = self.app.execute("SELECT c.*, d.pair, d.last_bar, d.interval FROM strategy_campaign c LEFT JOIN dataset d ON d.id = c.holdout_dataset_id ORDER BY c.id").fetchall()
        parent = {c["id"]: c["renewed_from"] for c in every}
        by_lineage = {}
        for r in self.app.execute("SELECT DISTINCT p.campaign_id, a.version_id FROM strategy_allocation a JOIN strategy_promotion p ON p.id = a.promotion_id "
                                  "WHERE a.policy_version = ?", (W.POLICY_INCONCLUSIVE,)):
            by_lineage.setdefault(r[0], set()).add(r[1])
        spans = self.judgement_spans()
        out = []
        for c in every:
            if c["closed_at"] is not None and c["closed_at"] < self.since_s:
                continue
            chain, at = set(), c["id"]
            while at is not None and at not in chain:      # CampaignStore.Lineage: this campaign and every one it was renewed from
                chain.add(at)
                at = parent.get(at)
            slots = set().union(*(by_lineage.get(i, set()) for i in chain))
            where = "(campaign_id = ? OR charged_to = ?)" if pots else "campaign_id = ?"
            ids = (c["id"], c["id"]) if pots else (c["id"],)
            trials = self.app.execute(f"SELECT COUNT(DISTINCT run_id) FROM strategy_trial WHERE charged = 1 AND {where}", ids).fetchone()[0] + len(slots)
            explore = self.app.execute(f"SELECT COUNT(DISTINCT run_id) FROM strategy_trial WHERE charged = 1 AND exploration = 1 AND {where}", ids).fetchone()[0] if pots else None
            verdicts = self.judgements_spent(c, chain, spans)
            out.append({"id": c["id"], "pair": c["pair"], "name": c["name"], "closed": c["closed_at"] is not None, "renewed_from": c["renewed_from"],
                        "trials": trials, "trial_budget": c["trial_budget"], "exploration": explore, "exploration_budget": c["exploration_budget"] if pots else None,
                        "verdicts": verdicts, "verdict_budget": c["verdict_budget"], "holdout_from": c["holdout_from"]})
        return {"campaigns": out}

    def judgement_spans(self):
        """Every verdict charged, as the market time it read: CampaignStore.JudgementSpans, from the same recorded facts."""
        return [(r["campaign_id"], parse_day(r["holdout_from"]), dataset_close(r["last_bar"], r["interval"]), r["pair"]) for r in self.app.execute(
            "SELECT v.campaign_id, v.holdout_from, d.last_bar, d.interval, d.pair FROM strategy_verdict v JOIN strategy_campaign c ON c.id = v.campaign_id "
            "LEFT JOIN dataset d ON d.id = c.holdout_dataset_id")]

    @staticmethod
    def judgements_spent(campaign, chain, spans):
        """CampaignStore.JudgementsSpent, what the budget refuses by: a verdict of this campaign's renewal lineage, or of any campaign of
        the same pair whose read span overlaps this campaign's. Never this campaign's own rows alone: that reads low on a second press."""
        start, close, pair = parse_day(campaign["holdout_from"]), dataset_close(campaign["last_bar"], campaign["interval"]), campaign["pair"]
        return sum(1 for owner, v_from, v_close, v_pair in spans
                   if owner in chain or (v_pair is None or pair is None or v_pair == pair) and spans_overlap(v_from, v_close, start, close))

    def candidates(self):
        marks = ",".join("?" * len(ADMISSIBLE))
        rows = self.app.execute(f"SELECT verdict, COUNT(DISTINCT version_id) AS n, COUNT(DISTINCT CASE WHEN at >= ? THEN version_id END) AS s "
                                f"FROM strategy_promotion WHERE verdict IN ({marks}) GROUP BY verdict", (self.since_s, *ADMISSIBLE)).fetchall()
        return {"admissible_total": sum(r["n"] for r in rows), "admissible_since": sum(r["s"] for r in rows),
                "by_class": {r["verdict"]: {"total": r["n"], "since": r["s"]} for r in rows}}

    def ledger(self):
        if not self.has("ledger_entry", "kind", "created_at"):
            return {"absent": "ledger_entry"}
        return {r["kind"]: r["n"] for r in self.app.execute("SELECT kind, COUNT(*) AS n FROM ledger_entry WHERE created_at >= ? GROUP BY kind", (self.since_s,))}

    # -- 13. is the cell proof current?
    def cell_proof(self):
        identity = self.host_identity()
        proofs = self.app.execute(
            "SELECT id, provider, platform, machine, os_release, build, boot, proven_at FROM cell_proof "
            "WHERE id NOT IN (SELECT proof_id FROM cell_proof_revocation) ORDER BY proven_at DESC, id DESC").fetchall()
        revoked = self.app.execute("SELECT COUNT(*) FROM cell_proof_revocation").fetchone()[0]
        newest = proofs[0] if proofs else None
        gaps = self.app.execute("SELECT COUNT(*) FROM cell_proof_row WHERE proof_id = ? AND kind = ?", (newest["id"], W.PROOF_GAP)).fetchone()[0] if newest else None
        out = {"unrevoked_proofs": len(proofs), "revoked": revoked, "newest_proven_at": newest["proven_at"] if newest else None,
               "newest_provider": newest["provider"] if newest else None, "newest_gap_rows": gaps, "current": None, "differs": None}
        if identity is None:
            out["why"] = "this host's identity could not be read, so currency cannot be told"
            return out
        build_hash = self.deployed_build_hash()
        for p in proofs:
            differs = [name for name, ok in (("machine", p["machine"] == identity["machine"]), ("os_release", p["os_release"] == identity["os_release"]),
                                             ("boot", p["boot"] == identity["boot"]),
                                             ("build", bool(build_hash) and str(p["build"]).endswith("+trade-sha256:" + build_hash))) if not ok]
            if not differs:
                out.update(current=True, differs=[], current_provider=p["provider"], current_proven_at=p["proven_at"])
                return out
        out["current"] = False
        out["differs"] = None
        if newest is not None:
            out["differs"] = [name for name, ok in (("machine", newest["machine"] == identity["machine"]), ("os_release", newest["os_release"] == identity["os_release"]),
                                                    ("boot", newest["boot"] == identity["boot"]),
                                                    ("build", bool(build_hash) and str(newest["build"]).endswith("+trade-sha256:" + build_hash))) if not ok]
        out["why"] = "no unrevoked proof matches this machine, kernel, boot and build" + ("" if build_hash else " (no state/trade.sha256 to bind a build to)")
        return out

    def host_identity(self):
        def read(path):
            try:
                with open(path, encoding="utf-8") as fh:
                    return fh.read().strip()
            except OSError:
                return None
        machine = read(self.opts["machine_id_file"])
        boot = read(self.opts["boot_id_file"])
        release = read(self.opts["osrelease_file"])
        if not machine or not boot or not release:
            return None
        sha = lambda s: hashlib.sha256(s.encode("utf-8")).hexdigest()
        return {"machine": sha("tradeagent-cell-proof-machine:" + machine), "boot": sha("tradeagent-cell-proof-boot:" + boot), "os_release": release}

    def deployed_build_hash(self):
        try:
            with open(os.path.join(self.state, "trade.sha256"), encoding="utf-8") as fh:
                h = fh.read().strip().lower()
            return h if re.fullmatch(r"[0-9a-f]{64}", h) else None
        except OSError:
            return None

    # -- 14. the tape's health line
    def tape_health(self):
        out = {"service": self.service()}
        if self.tape is None:
            out["unreadable"] = "no tape database at " + os.path.join(self.state, "tape.db")
            return out
        size = 0
        for suffix in ("", "-wal"):
            try:
                size += os.path.getsize(os.path.join(self.state, "tape.db" + suffix))
            except OSError:
                pass
        sources = {}
        for r in self.tape.execute("SELECT source, MAX(received_at) AS newest FROM tape_fetch WHERE http_status = 200 AND note IS NULL GROUP BY source"):
            d = parse_ts(r["newest"])
            sources[r["source"]] = {"source": r["source"], "newest_delivery": r["newest"], "age_s": int((self.now - d).total_seconds()) if d else None,
                                    "fetches_since": 0, "non_200_since": 0, "parse_failed_since": 0}
        for r in self.tape.execute(
                "SELECT source, COUNT(*) AS n, SUM(CASE WHEN http_status IS NOT 200 THEN 1 ELSE 0 END) AS bad, "
                "SUM(CASE WHEN http_status = 200 AND note IS NOT NULL THEN 1 ELSE 0 END) AS parse FROM tape_fetch WHERE received_at >= ? GROUP BY source",
                (self.since_s,)):
            s = sources.setdefault(r["source"], {"source": r["source"], "newest_delivery": None, "age_s": None, "fetches_since": 0,
                                                 "non_200_since": 0, "parse_failed_since": 0})
            s.update(fetches_since=r["n"], non_200_since=r["bad"] or 0, parse_failed_since=r["parse"] or 0)
        rows = sorted(sources.values(), key=lambda s: s["source"])
        newest = min((s for s in rows if s["age_s"] is not None), key=lambda s: s["age_s"], default=None)
        stalest = max((s for s in rows if s["age_s"] is not None), key=lambda s: s["age_s"], default=None)
        last_note = self.tape.execute("SELECT note FROM tape_fetch WHERE http_status = 200 AND note IS NOT NULL AND received_at >= ? ORDER BY id DESC LIMIT 1",
                                      (self.since_s,)).fetchone()
        out.update(bytes=size, sources=rows, source_count=len(rows),
                   newest={"source": newest["source"], "age_s": newest["age_s"]} if newest else None,
                   stalest={"source": stalest["source"], "age_s": stalest["age_s"]} if stalest else None,
                   fetches_since=sum(s["fetches_since"] for s in rows), non_200_since=sum(s["non_200_since"] for s in rows),
                   parse_failed_since=sum(s["parse_failed_since"] for s in rows), last_parse_note=short(last_note[0]) if last_note else None)
        return out

    def service(self):
        if self.opts["no_systemd"]:
            return None
        try:
            done = subprocess.run(["systemctl", "show", "-p", "ActiveState", "-p", "NRestarts", "-p", "ActiveEnterTimestamp", "tradeagent.service"],
                                  capture_output=True, text=True, timeout=5, check=False)
        except (OSError, subprocess.SubprocessError):
            return None
        facts = dict(line.split("=", 1) for line in done.stdout.splitlines() if "=" in line)
        return {"active": facts.get("ActiveState"), "restarts": facts.get("NRestarts"), "since": facts.get("ActiveEnterTimestamp") or None} if facts else None


SECTIONS = (("app", "app_state"), ("positions", "positions"), ("turns", "turns"), ("org_events", "org_events"), ("arrows", "arrows"),
            ("verdicts", "verdicts"), ("deployments", "deployments"), ("refusals", "refusals"), ("errors", "errors"), ("app_errors", "app_errors"),
            ("pace", "pace"),
            ("signal", "signal"), ("day_total", "day_total"), ("wakes", "wakes"), ("cell_proof", "cell_proof"), ("tape_health", "tape_health"))


def build(app_path, tape_path, state, now, since, opts):
    app = open_ro(app_path) if app_path and os.path.isfile(app_path) else None
    tape = None
    if tape_path and os.path.isfile(tape_path):
        try:
            tape = open_ro(tape_path)
        except sqlite3.Error:
            tape = None
    digest = Digest(app, tape, state, now, since, opts)
    result = {"digest": "org-digest/1", "read_only": True, "now": stored(now), "since": stored(since),
              "app_db": "present" if app else "absent", "tape_db": "present" if tape else "absent"}
    for key, method in SECTIONS:
        if app is None and key != "tape_health":
            result[key] = {"unreadable": "no state/tradeagent.db"}
            continue
        try:
            result[key] = getattr(digest, method)()
        except sqlite3.Error as ex:
            result[key] = {"unreadable": short(f"{type(ex).__name__}: {ex}", 160)}
    result["disk"] = read_disk(state, opts.get("disk_file"))
    result["attention"] = attention(result)
    for c in (app, tape):
        if c is not None:
            c.close()
    return result


def read_disk(state, disk_file):
    """FREE SPACE ON THE FILESYSTEM HOLDING THE STATE FOLDER, on every run, as this host answers now: a statvfs, which writes nothing. It is what the app's
    free-space guard reads, and this file's floor is the guard's: free is what the app's own account can use. A read that fails says so; it is never a zero."""
    try:
        if disk_file:
            with open(disk_file, encoding="utf-8") as f:
                free, total = json.load(f)
        else:
            v = os.statvfs(state)
            free, total = v.f_bavail * v.f_frsize, v.f_blocks * v.f_frsize
    except (OSError, ValueError, TypeError) as ex:
        return {"unreadable": str(ex)}
    floor = max(DISK_FLOOR_LEAST, min(total // DISK_FLOOR_SHARE, DISK_FLOOR_MOST))
    return {"free": free, "total": total, "floor": floor, "below_floor": free < floor}


def gib(n):
    return "?" if n is None else f"{n / GIB:.1f} GiB"


def attention(d):
    flags = []
    pause = d.get("app", {}).get("disk_pause")
    if pause and pause["unreadable"]:
        flags.append(f"DISK GUARD TRIPPED {stamp(pause['at'])}: it could not read the free space, so the AI is paused until the owner resumes it")
    elif pause:
        flags.append(f"DISK GUARD TRIPPED {stamp(pause['at'])}: {gib(pause['free'])} free of {gib(pause['total'])}, floor {gib(pause['floor'])}; "
                     "the AI is paused until the owner resumes it")
    disk = d.get("disk", {})
    if disk.get("below_floor") and not pause:
        flags.append(f"free space {gib(disk['free'])} is UNDER the guard's floor {gib(disk['floor'])} and no pause is recorded")
    if d.get("app", {}).get("ai_works_on_its_own") is False:
        flags.append("the AI is NOT set to work on its own: nothing turns")
    dt_ = d.get("day_total", {})
    plan_total = dec(dt_.get("board_policy", {}).get("plan_day_total"))
    if plan_total and (dec(dt_.get("plan_counted")) or Decimal(0)) > plan_total:
        flags.append(f"AI day-total PASSED: {dt_['plan_counted']} of {money(plan_total)}")
    for g in d.get("refusals", {}).get("groups", []):
        if g["count"] >= REPEATED_REFUSAL:
            flags.append(f"refused {g['count']}x: {g['what']}: {short(g['words'], 90)}")
    dep = d.get("deployments", {})
    for g in dep.get("by_mode_state", []):
        if g["mode"] != W.MODE_PAPER and g["state"] != W.DEPLOYMENT_ENDED:
            flags.append(f"{g['count']} deployment(s) in mode '{g['mode']}' ({g['state']}): NOT paper")
    for x in dep.get("deployments", []):
        if x.get("unprotected"):
            flags.append(f"paper run {x['id']} {x['symbol']} ({x['state']}): {x['unprotected']}")
    notes = dep.get("notes") or {}
    if notes.get("unprotected_at_close_since"):
        flags.append(f"{notes['unprotected_at_close_since']} paper day note(s) since T0 say {W.UNPROTECTED_AT_CLOSE}, the last {stamp(notes['unprotected_at_close_last_at'])}")
    cp = d.get("cell_proof", {})
    if cp.get("current") is False:
        flags.append("cell proof is NOT current here: " + ", ".join(cp.get("differs") or ["no proof"]) + " differ")
    elif cp.get("current") is None and "unreadable" not in cp:
        flags.append("cell proof currency unknown: " + str(cp.get("why", "")))
    er = d.get("errors", {})
    lost = sum(g["count"] for g in er.get("groups", []) if g["state"] == W.ATTEMPT_LOST)
    wk = d.get("wakes", {})
    if wk.get("failed_turn_wakes_since") or wk.get("lost_turn_wakes_since"):
        flags.append(f"wakes: {wk.get('failed_turn_wakes_since', 0)} on failed turns, {wk.get('lost_turn_wakes_since', 0)} on lost turns, "
                     f"{wk.get('raised_again_after_lost_since', 0)} raised again")
    if lost:
        flags.append(f"{lost} AI launch(es) LOST since T0")
    if er.get("failed_since"):
        flags.append(f"{er['failed_since']} of {er['attempts_since']} AI launches failed or reported a vendor error since T0")
    ae = d.get("app_errors", {})
    if ae.get("total"):
        kinds = ae["groups"]
        flags.append(f"{ae['total']} app error(s) logged since T0: " + ", ".join(f"{g['component']}/{g['event']} x{g['count']}" for g in kinds[:3])
                     + (f" (+{len(kinds) - 3} more kinds)" if len(kinds) > 3 else ""))
    od = d.get("wakes", {}).get("overdue") or []
    if od:
        # EVERY OVERDUE POSITION BY NAME, and how many: the wakes below may show only one at the tightest line cap. The reasons of the first two.
        reasons = [w for w in d["wakes"]["active_positions"] if w["position"] in od]
        flags.append("overdue wake(s): " + ", ".join(od) + f" ({len(od)} position{'' if len(od) == 1 else 's'}); " + ("; ".join(why_line(w) for w in reasons[:2])))
    t = d.get("tape_health", {})
    if t.get("stalest") and t["stalest"]["age_s"] is not None and t["stalest"]["age_s"] > 7200:
        flags.append(f"tape: {t['stalest']['source']} last delivered {t['stalest']['age_s'] // 3600}h ago")
    if t.get("parse_failed_since"):
        flags.append(f"tape: {t['parse_failed_since']} parse failure(s) since T0")
    svc = t.get("service") or {}
    if svc and svc.get("active") not in (None, "active"):
        flags.append(f"tradeagent.service is {svc['active']}")
    if svc and svc.get("restarts") not in (None, "0"):
        flags.append(f"systemd restarted tradeagent.service {svc['restarts']} time(s) since it was last started by hand")
    bp = d.get("day_total", {}).get("board_policy", {})
    if bp.get("mode") not in (None, W.MODE_OBSERVE, W.MODE_PAPER):
        flags.append(f"board policy mode is {bp['mode']}")
    for key, v in d.items():
        if isinstance(v, dict) and "unreadable" in v and key != "tape_health":
            flags.append(f"section {key} unreadable: {v['unreadable']}")
    return flags


# ---- rendering -------------------------------------------------------------------------------------------------------------
def why_line(wake):
    """Why this overdue position has not turned, as the app recorded it, or that it recorded nothing."""
    why = wake.get("why")
    if not why:
        return f"{wake['position']}: no reason on record"
    return f"{wake['position']} at {stamp(why['at'])}: " + (f"{why['ended']} " if why["ended"] else "") + short(why["words"], 90)


def age(seconds):
    if seconds is None:
        return "?"
    s = abs(int(seconds))
    text = f"{s}s" if s < 90 else f"{s // 60}m" if s < 5400 else f"{s // 3600}h{(s % 3600) // 60:02d}m"
    return text if seconds >= 0 else "-" + text


def stamp(text):
    """A stored instant, short: 11-07 14:02."""
    return f"{text[5:10]} {text[11:16]}" if text else "-"


def render(d, cap):
    L = []
    more = lambda items, n, label="more": (items[:n], f"  (+{len(items) - n} {label})" if len(items) > n else None)

    def add_list(rows, n, fmt):
        shown, tail = more(rows, n)
        L.extend(fmt(r) for r in shown)
        if tail:
            L.append(tail)

    def unread(name, sec):
        if "unreadable" in sec:
            L.append(f"{name}: UNREADABLE - {sec['unreadable']}")
            return True
        return False

    app = d.get("app", {})
    working = {True: "on", False: "OFF", None: "?"}[app.get("ai_works_on_its_own")]
    L.append(f"ORG DIGEST (read-only)  now {d['now'][:19]}Z  since T0 {d['since'][:19]}Z  app-db {d['app_db']} schema {app.get('schema')}  tape-db {d['tape_db']}  AI on its own {working}")
    if d["attention"]:
        # NEVER CAPPED: a flag behind "(+N more)" is an alarm nobody reads. They stand at the top, so even the 60-line cut never takes one.
        L.append("ATTENTION")
        L.extend("  ! " + a for a in d["attention"])

    s = d["disk"]
    if not unread("disk", s):
        L.append(f"disk: {gib(s['free'])} free of {gib(s['total'])}  guard floor {gib(s['floor'])}")

    s = d["positions"]
    if not unread("positions", s):
        L.append(f"positions: {s['total']}  " + "; ".join(f"{g['status']}/{g['model']} x{g['count']}" for g in s["by_status_model"][:cap["positions"]]))
        if s["models_run_since"]:
            L.append("  models run since T0: " + ", ".join(f"{m['model']} {m['turns']}" for m in s["models_run_since"][:4]))

    s = d["turns"]
    if not unread("turns/cost", s):
        t = s["total"]
        L.append(f"turns/cost (list-price equiv., not a bill)  today {t['turns_today']} / {t['cost_today']}   since T0 {t['turns_since']} / {t['cost_since']}")
        seats = {p["position"]: p for p in d.get("pace", {}).get("positions", [])}

        def seat(p):
            x = seats.get(p["position"])
            return (f"  {x['turns_per_day']}/day, {x['cost_per_turn']}/turn, idle {x['idle_turns']}/{x['clean_turns']}, wait {age(x['wait_median_s'])}/{age(x['wait_max_s'])}"
                    if x and x["turns"] else "")
        add_list(s["positions"], cap["turns"], lambda p: f"  {p['position']:<14} today {p['turns_today']:>3} / {p['cost_today']:<9} since {p['turns_since']:>3} / {p['cost_since']}" + seat(p))

    s = d["org_events"]
    if not unread("org events", s):
        L.append(f"org events since T0: {s['total']}" + ("" if s["events"] else "  (none)"))
        add_list(s["events"], cap["events"], lambda e: f"  {e['kind']:<18} {e['count']:>4}  last {e['last_at'][:19] if e['last_at'] else '-'}  by {'/'.join(sorted(e['deciders']))}")
        if s["mandate_events"]:
            L.append("  mandate events: " + ", ".join(f"{m['kind']} {m['count']}" for m in s["mandate_events"][:5]))

    s = d["arrows"]
    if not unread("arrows", s):
        L.append(f"arrows reached since T0: {s['reached']} of {s['of']}" + (f"; not yet: {', '.join(s['not_reached'])}" if s["not_reached"] else ""))
        shown = [a for a in s["arrows"] if a["count"] or a["refused"]]
        add_list(shown, cap["arrows"], lambda a: f"  {a['arrow']:<16} x{a['count']:<4} first {stamp(a['first_at'])}" + (
            f"  refused x{a['refused']}, first {stamp(a['first_refused_at'])}: {short(a['first_refusal'], 80)}" if a["refused"] else ""))
        if s["first_refusal"]:
            f = s["first_refusal"]
            L.append(f"  first refusal of the run {stamp(f['at'])} {f['what']}: {short(f['words'], 90)}")

    s = d["verdicts"]
    if not unread("verdicts", s):
        given = ", ".join(f"{g['verdict']}:{g['reason']} {g['count']}" for g in s["given_since"][:3]) or "none"
        L.append(f"verdicts: asked {s['asked_since']} since T0, waiting {s['waiting_now']}, given {s['given_total']} [{given}]; "
                 f"inconclusive: referee {s['referee_inconclusive_since']}, no-trade refusals {s['no_trade_refusals_since']}")

    s = d["deployments"]
    if not unread("deployments", s):
        modes = ", ".join(f"{g['mode']}/{g['state']} {g['count']}" for g in s["by_mode_state"]) or "none"
        a = s["allocations"]
        L.append(f"deployments (SIMULATION, paper fills; not money): {modes}; allocations {a['open']} open of {a['total']} ({a['new_since']} new)")
        add_list(s["deployments"], cap["deployments"], lambda x: f"  {x['id']} {x['symbol']} {x['mode']}/{x['state']}  fills {x['fills']}  realised {x['realised']}  "
                 f"unrealised {x['unrealised'] if x['unrealised'] is not None else '?'}  fees {x['fees']}" + (f"(+{x['fees_unknown_fills']} unknown)" if x["fees_unknown_fills"] else "")
                 + (f"  {x['unprotected']}" if x.get("unprotected") else ""))
        if s["envelopes"]:
            L.append("  envelopes (runs open/allowed): " + ", ".join(f"{e['symbol']} {e['open']}/{e['max_deployments']}" for e in s["envelopes"][:6]))
        if s["notes"]["since"]:
            L.append(f"  paper notes since T0: {s['notes']['since']}; last {stamp(s['notes']['last_at'])}: {s['notes']['last']}")

    s = d["refusals"]
    if not unread("refusals", s):
        L.append(f"refusals since T0: {s['total']} in {len(s['groups'])} group(s)")
        add_list(s["groups"], cap["refusals"], lambda g: f"  {g['count']:>4}x [{g['source']}] {g['what']}: {g['words']}")

    s = d["errors"]
    if not unread("ai errors", s):
        L.append(f"AI launches since T0: {s['attempts_since']}, failed/vendor-error {s['failed_since']}; exit codes " +
                 (", ".join(f"{k}x{v}" for k, v in sorted(s["exit_codes"].items())) or "none"))
        add_list(s["groups"], cap["errors"], lambda g: f"  {g['count']:>3}x {g['state']} exit {g['exit_code']} {','.join(g['positions'])}: "
                 + (f"{g['ended']}: " if g["ended"] else "") + f"{g['vendor_said'] or g['words'] or '(no vendor words)'}"
                 + (f" [offered {g['offered_tools']} tools]" if g["offered_tools"] is not None else ""))

    s = d["app_errors"]
    if not unread("app errors", s):
        L.append(f"app errors logged since T0: {s['total']}" + (f" in {len(s['groups'])} kind(s)" if s["groups"] else "  (none)"))
        add_list(s["groups"], cap["errors"], lambda g: f"  {g['count']:>4}x {g['component']}/{g['event']}  first {stamp(g['first_at'])}  last {stamp(g['last_at'])}")

    s = d["pace"]
    if not unread("pace", s):
        L.append(f"pace over {s['days']} days: {s['turns_per_day']} turns/day, {s['cost_per_turn'] or '-'}/turn, idle {s['idle_turns']} of {s['clean_turns']} clean turns"
                 f" ({s['idle_share_pct'] or '-'}%), wake wait median {age(s['wait_median_s'])} max {age(s['wait_max_s'])}, vendor limits {s['vendor_limit_refusals']}")
        if s["first_vendor_limit"]:
            f = s["first_vendor_limit"]
            L.append(f"  first vendor limit {stamp(f['at'])} {f['position']}: {short(f['words'], 90)}")

    s = d["signal"]
    if not unread("signal", s):
        fc = s["forecasts"]
        if "absent" in fc:
            L.append(f"forecasts: {fc['absent']} is not on this home")
        else:
            L.append(f"forecasts: {len(fc['managers'])} manager(s) wrote or resolved since T0" if fc["managers"] else "forecasts: none written or resolved since T0")
            add_list(fc["managers"], cap["forecasts"], lambda m: f"  {m['position']:<14} written {m['written_since']} ({m['written_per_day']}/day), resolved {m['resolved_since']} "
                     f"({m['resolved_per_day']}/day), total {m['resolved_total']}, days to {FORECASTS_TO_SCORE}: {m['days_to_50'] if m['days_to_50'] is not None else 'none yet'}")
        cs = s["campaigns"]
        if "absent" in cs:
            L.append(f"campaigns: {cs['absent']} is not on this home")
        else:
            L.append(f"campaigns: {len(cs['campaigns'])} open or closed since T0" + ("" if cs["campaigns"] else "  (none)"))
            add_list(cs["campaigns"], cap["campaigns"], lambda c: f"  {c['pair'] or '?'} #{c['id']}{' closed' if c['closed'] else ''}: trials {c['trials']}/{c['trial_budget']}"
                     + (f", exploration {c['exploration']}/{c['exploration_budget']}" if c["exploration"] is not None else "") + f", verdicts {c['verdicts']}/{c['verdict_budget']}")
        ca, lg = s["candidates"], s["ledger"]
        L.append(f"admissible candidates: {ca['admissible_since']} since T0, {ca['admissible_total']} ever; ledger since T0: " +
                 (", ".join(f"{k} {v}" for k, v in sorted(lg.items()) if k != "absent") or "nothing"))

    s = d["day_total"]
    if not unread("AI day-total", s):
        b = s["board_policy"]
        L.append(f"AI day-total: used plan {s['used']['plan']} + unknown {s['used']['unknown']} (key {s['used']['key']}) vs board plan day-total "
                 f"{b['plan_day_total']} ({s['plan_used_of_board_pct']}%, policy v{b['version']} {b['mode']})")
        L.append(f"  board seats spent {s['board_seats_spent']} of their {s['board_seats_ceiling']} ceiling (share {s['board_share']})")

    s = d["wakes"]
    if not unread("wakes", s):
        L.append("next wake per active position:" + ("" if s["active_positions"] else " (no active position)"))
        overdue_first = sorted(s["active_positions"], key=lambda w: w["position"] not in s["overdue"])
        shown, hidden = overdue_first[:cap["wakes"]], overdue_first[cap["wakes"]:]
        L.extend(f"  {w['position']:<14} " + (
            f"{w['kind']}{' (the chief at its floor: nothing moving)' if w['chief_floor'] else ''} in {age(w['in_seconds'])} ({w['pending']} pending)" if w["next_due"] else "no wake queued")
            + f"  last woken {w['last_consumed_at'][:19] if w['last_consumed_at'] else '-'}" + (f"  {why_line(w)}" if w["in_seconds"] is not None and w["in_seconds"] < 0 else "")
            for w in shown)
        if hidden:
            # THE TAIL NAMES EVERY OVERDUE POSITION IT HIDES: "(+3 more)" alone read as three quiet seats.
            late = [w["position"] for w in hidden if w["position"] in s["overdue"]]
            L.append(f"  (+{len(hidden)} more" + (f"; overdue among them: {', '.join(late)}" if late else "") + ")")
        taken = ", ".join(f"{k} {v}" for k, v in sorted(s["consumed_since_by_disposition"].items())) or "none"
        L.append(f"  wakes taken since T0: {taken}; on failed turns {s['failed_turn_wakes_since']}, on lost turns {s['lost_turn_wakes_since']}, raised again {s['raised_again_after_lost_since']}")
        add_list(s["blocked_now"], cap["blocked"], lambda b: f"  blocked x{b['count']} ({','.join(b['roles'])}): {b['words']}")

    s = d["cell_proof"]
    if not unread("cell proof", s):
        verdict = {True: "CURRENT", False: "NOT CURRENT", None: "UNKNOWN"}[s["current"]]
        L.append(f"cell proof: {verdict}; {s['unrevoked_proofs']} unrevoked ({s['revoked']} revoked), newest {s['newest_provider']} "
                 f"{(s['newest_proven_at'] or '-')[:19]}, gap rows {s['newest_gap_rows']}" + (f"; differs: {', '.join(s['differs'])}" if s.get("differs") else ""))

    s = d["tape_health"]
    if "unreadable" in s:
        L.append("tape: UNREADABLE - " + s["unreadable"])
    else:
        n, st = s["newest"], s["stalest"]
        svc = s.get("service")
        L.append(f"tape: {s['source_count']} sources, {s['bytes'] // 1024} KiB; newest delivery {age(n['age_s']) if n else '-'} ({n['source'] if n else '-'}), "
                 f"stalest {age(st['age_s']) if st else '-'} ({st['source'] if st else '-'}); since T0 {s['fetches_since']} fetches, {s['non_200_since']} non-200, "
                 f"{s['parse_failed_since']} parse-failed" + (f"; service {svc.get('active')} restarts {svc.get('restarts')}" if svc else ""))
    return L


# THE LINE CAPS, widest first: the human digest takes the first that fits MAX_LINES. The ATTENTION flags are never capped (render).
CAPS = [dict(positions=5, turns=7, events=4, arrows=17, deployments=4, refusals=5, errors=4, forecasts=4, campaigns=6, wakes=6, blocked=2),
        dict(positions=3, turns=4, events=3, arrows=12, deployments=3, refusals=3, errors=3, forecasts=3, campaigns=4, wakes=3, blocked=1),
        dict(positions=2, turns=2, events=2, arrows=8, deployments=1, refusals=2, errors=2, forecasts=2, campaigns=3, wakes=2, blocked=1),
        dict(positions=1, turns=1, events=1, arrows=4, deployments=0, refusals=1, errors=1, forecasts=1, campaigns=2, wakes=1, blocked=1)]


def human(d):
    for cap in CAPS:
        lines = render(d, cap)
        if len(lines) <= MAX_LINES:
            return lines
    return lines[:MAX_LINES - 1] + ["(digest cut at 60 lines: use --json for all of it)"]


def main(argv):
    ap = argparse.ArgumentParser(prog="org-digest", description="read-only digest of the AI organisation")
    g = ap.add_mutually_exclusive_group()
    g.add_argument("--home", default=DEFAULT_HOME)
    g.add_argument("--state")
    ap.add_argument("--since")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--now")
    ap.add_argument("--no-systemd", action="store_true")
    ap.add_argument("--machine-id-file", default="/etc/machine-id")
    ap.add_argument("--boot-id-file", default="/proc/sys/kernel/random/boot_id")
    ap.add_argument("--osrelease-file", default="/proc/sys/kernel/osrelease")
    ap.add_argument("--disk-file")
    ap.add_argument("--vocabulary", action="store_true")
    a = ap.parse_args(argv)
    if a.vocabulary:
        print(json.dumps({k: v for k, v in vars(W).items() if k.isupper()}, sort_keys=True))
        return 0
    try:
        now = parse_ts(a.now) if a.now else dt.datetime.now(dt.timezone.utc)
        if now is None:
            raise ValueError(f"--now '{a.now}' is not an ISO instant with a zone")
        since = parse_since(a.since, now)
    except ValueError as ex:
        print(f"org-digest: {ex}", file=sys.stderr)
        return 2
    state = a.state or os.path.join(a.home, STATE_UNDER_HOME)
    opts = {"no_systemd": a.no_systemd, "machine_id_file": a.machine_id_file, "boot_id_file": a.boot_id_file, "osrelease_file": a.osrelease_file,
            "disk_file": a.disk_file}
    result = build(os.path.join(state, "tradeagent.db"), os.path.join(state, "tape.db"), state, now, since, opts)
    if a.json:
        print(json.dumps(result, indent=2, sort_keys=True, default=str))
    else:
        print("\n".join(human(result)))
    return 1 if result["app_db"] == "absent" else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
