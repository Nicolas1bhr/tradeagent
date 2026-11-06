#!/usr/bin/env python3
"""THE ORGANISATION'S DIGEST (U-org-digest): the fleet's eyes on the AI organisation during an unattended run.

READ-ONLY, ALL OF IT. tools/linux-host.sh org-digest sends this file over ssh and runs it as the app's own account, against the
app's two state databases (state/tradeagent.db, state/tape.db) and nothing else: every database is opened with SQLite's
mode=ro and PRAGMA query_only=ON, so a statement that wrote would fail rather than run. It never reads a credential, the seat
home's codex folder or anything holding the owner's auth.json, never names the host, and never writes a file.

  org-digest.py [--home /var/lib/tradeagent | --state <dir>] [--since <T0>] [--json]

  --since   T0: an ISO date or date-time (no zone = the host's local zone; the app's own stored instants are accepted as they are),
            or a span back from now: 72h, 3d. Default: the start of the host's local day (the owner's day: the one the AI's daily
            ceilings reset on).
  --json    the whole digest as one JSON object; otherwise a human digest of at most 60 lines, one line per fact.
  --now     (tests) the instant "now" is, ISO with a zone.   --no-systemd   (tests) do not ask systemd about the unit.
  --machine-id-file / --boot-id-file / --osrelease-file   (tests) where the cell proof's host identity is read from.

Every figure is a MEASUREMENT the app wrote (ai_attempt, org_event, mission_event, tool_call, strategy_*, fill, cell_proof,
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
DEFAULT_POSITION = "operations"          # CouncilRoles.Default: a launch row with no role belongs to the legacy chair
BOARD_PREFIX = "board-"                  # BoardSeats.Prefix: a board seat's launches are rows whose role starts with it
PIPE_REFUSALS = ("grant_rejected", "auth_rejected", "protocol_rejected", "operator_session_refused", "op_refused")
MAX_LINES = 60
PAPER_RUN_WAKE = "note:paper-run:"       # MissionEventIds.PaperRun: the wake that carries a forward run's figures back to its author
LOST_WAKE_SUFFIX = ":after-lost"         # MissionLoop.AfterLost: a told-something wake raised again after a lost turn
INCONCLUSIVE_POLICY = "allocation-policy-inconclusive-v1"   # AllocationPolicy.InconclusiveV1: a slot that costs its campaign a trial
REPEATED_REFUSAL = 5                     # the 72-hour brief's "act on": the same words refused five times
MISSION_FAILED = "failed"                 # MissionEventDisposition.Failed
ADMISSIBLE = ("promoted", "paper-eligible")                 # PromotionVerdict: the two classes a version can be carried forward on
FORECASTS_TO_SCORE = 50                  # R26 § 4: the resolved forecasts a manager needs before its calibration can be read
# The tables whose rows name the launch that wrote them: a turn that left a row in one of these did something.
ACT_TABLES = ("org_mandate_event", "org_delivery", "org_decision", "org_forecast", "org_escalation", "org_lateral", "ledger_entry",
              "publication", "strategy_version", "strategy_run", "board_act", "board_forecast")
VENDOR_LIMIT_WORDS = re.compile(r"\b429\b|rate.?limit|usage.?limit|quota|too many requests|limit (?:reached|exceeded)", re.IGNORECASE)


# ---- time ------------------------------------------------------------------------------------------------------------------
def parse_ts(text):
    """A stored instant (the app writes UTC, 'O' format: 2026-10-30T12:00:00.0000000Z) as an aware datetime, or None."""
    if not text:
        return None
    m = re.match(r"^(\d{4}-\d\d-\d\d)[T ](\d\d:\d\d:\d\d)(?:\.(\d+))?(Z|[+-]\d\d:\d\d)?$", str(text).strip())
    if not m:
        return None
    frac = (m.group(3) or "")[:6].ljust(6, "0")
    zone = m.group(4) or "Z"
    d = dt.datetime.fromisoformat(f"{m.group(1)}T{m.group(2)}.{frac}{'+00:00' if zone == 'Z' else zone}")
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
        return dt.datetime.fromisoformat(f"{m.group(1)}T{clock}{'+00:00' if m.group(4) == 'Z' else m.group(4)}").astimezone(dt.timezone.utc)
    return dt.datetime.fromisoformat(f"{m.group(1)}T{clock}").astimezone().astimezone(dt.timezone.utc)


# ---- reading ---------------------------------------------------------------------------------------------------------------
def open_ro(path):
    """A database opened so that it CANNOT be written: SQLite's own read-only mode, and query_only on top of it."""
    conn = sqlite3.connect("file:" + urllib.parse.quote(path) + "?mode=ro", uri=True, timeout=5)
    conn.execute("PRAGMA query_only=ON")
    conn.row_factory = sqlite3.Row
    return conn


def dec(value):
    try:
        return Decimal(str(value)) if value is not None else None
    except InvalidOperation:
        return None


def money(d):
    return None if d is None else str(d.quantize(Decimal("0.0001")))


def short(text, n=110):
    one = re.sub(r"\s+", " ", str(text or "")).strip()
    return one if len(one) <= n else one[: n - 1] + "…"


def charge(row):
    """What a launch counts for: its reservation while open, its cost once closed (its reservation where it ended unpriced)."""
    if row["state"] == "LAUNCHED":
        return dec(row["reserved_cost"]) or Decimal(0)
    return dec(row["cost"]) if row["cost"] is not None else (dec(row["reserved_cost"]) or Decimal(0))


def position_of(row_role):
    return row_role or DEFAULT_POSITION


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
        schema = self.app.execute("SELECT value FROM meta WHERE key = 'schema_version'").fetchone()
        working = None
        row = self.app.execute("SELECT value FROM kv WHERE key = 'settings'").fetchone() if self.has("kv", "key", "value") else None
        if row:
            try:
                settings = json.loads(row[0])     # one word is taken from it and the rest is never kept or shown
                for name in ("ai_works_on_its_own", "AiWorksOnItsOwn", "aiWorksOnItsOwn"):
                    if isinstance(settings, dict) and name in settings:
                        working = bool(settings[name])
            except ValueError:
                pass
        return {"schema": schema[0] if schema else None, "ai_works_on_its_own": working}

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
            p = per.setdefault(position_of(r["role"]), {"position": position_of(r["role"]), "board_seat": position_of(r["role"]).startswith(BOARD_PREFIX),
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
            inconclusive = one("SELECT COUNT(*) FROM referee_holdout WHERE addis_status = 'inconclusive' AND at >= ?", self.since_s)
        except sqlite3.Error:
            pass
        return {"asked_since": asked, "waiting_now": waiting, "given_since": given, "given_total": sum(g["count"] for g in given),
                "no_trade_refusals_since": sum(g["count"] for g in given if g["reason"] == "no-trade-on-the-holdout"),
                "referee_inconclusive_since": inconclusive,
                "words": "asked = strategy_verdict rows requested since T0; given = promotion rows written since T0; waiting = asked, not yet given (ever)"}

    # -- 5. paper deployments and their P&L (a simulation)
    def deployments(self):
        allocs = self.app.execute(
            "SELECT COUNT(*) AS n, SUM(CASE WHEN effective_to IS NULL THEN 1 ELSE 0 END) AS open, "
            "SUM(CASE WHEN at >= ? THEN 1 ELSE 0 END) AS new FROM strategy_allocation WHERE policy_version NOT LIKE '%-stop-%'",
            (self.since_s,)).fetchone()
        stops = self.app.execute("SELECT COUNT(*) FROM strategy_allocation WHERE policy_version LIKE '%-stop-%' AND at >= ?", (self.since_s,)).fetchone()[0]
        rows = self.app.execute(
            "SELECT id, version_id, connector_id, account_id, symbol, mode, state, started_at, ended_at, end_reason, suspended_reason "
            "FROM strategy_deployment ORDER BY started_at DESC, id").fetchall()
        by = {}
        for r in rows:
            by[(r["mode"], r["state"])] = by.get((r["mode"], r["state"]), 0) + 1
        listed = []
        for r in rows:
            if r["state"] == "ended" and (parse_ts(r["ended_at"]) or self.now) < self.since:
                continue
            fills = self.app.execute(
                "SELECT f.at, f.side, f.quantity, f.price, f.fee FROM fill f WHERE f.account_id = ? AND f.symbol = ? AND "
                "(f.request_id IN (SELECT request_id FROM deployment_op WHERE deployment_id = ?) OR "
                " f.client_order_id IN (SELECT 'TA-' || request_id FROM deployment_op WHERE deployment_id = ?)) ORDER BY f.at, f.rowid",
                (r["account_id"], r["symbol"], r["id"], r["id"])).fetchall()
            listed.append({"id": r["id"][:12], "symbol": r["symbol"], "mode": r["mode"], "state": r["state"], "account": r["account_id"],
                           "started_at": r["started_at"], "end_reason": r["end_reason"], "suspended_reason": r["suspended_reason"],
                           **self.pnl(fills, r["account_id"], r["symbol"])})
        return {"label": "SIMULATION: paper deployments, modelled fills; not money", "allocations": {
                    "total": allocs["n"], "open": allocs["open"] or 0, "new_since": allocs["new"] or 0, "stopped_since": stops},
                "by_mode_state": [{"mode": m, "state": s, "count": n} for (m, s), n in sorted(by.items())],
                "deployments": listed, "envelopes": self.envelopes(), "notes": self.paper_notes()}

    def envelopes(self):
        """Evidence per pair: each pair's standing paper envelope and how many of its runs are open."""
        if not self.has("paper_envelope", "symbol", "max_deployments", "expires_at", "withdrawn_at"):
            return None
        rows = self.app.execute(
            "SELECT e.symbol, e.max_deployments, e.max_quantity, e.max_notional, e.currency, e.expires_at, "
            "(SELECT COUNT(*) FROM strategy_deployment d WHERE d.envelope_id = e.id AND d.state <> 'ended') AS open "
            "FROM paper_envelope e WHERE e.withdrawn_at IS NULL AND e.expires_at > ? ORDER BY e.symbol, e.granted_at", (self.now_s,)).fetchall()
        return [dict(r) for r in rows]

    def paper_notes(self):
        """What the app told a run's author: the notes it published, and the last one's words (a simulation's figures, never money)."""
        rows = self.app.execute(
            "SELECT content, created_at FROM publication WHERE role = 'allocator' AND kind = 'note' AND created_at >= ? ORDER BY created_at DESC, id",
            (self.since_s,)).fetchall()
        return {"since": len(rows), "last_at": rows[0]["created_at"] if rows else None, "last": short(rows[0]["content"], 150) if rows else None}

    def pnl(self, fills, account, symbol):
        qty, avg, realised, fees, unknown_fees = Decimal(0), Decimal(0), Decimal(0), Decimal(0), 0
        for f in fills:
            q, p = dec(f["quantity"]) or Decimal(0), dec(f["price"]) or Decimal(0)
            signed = -q if str(f["side"]).lower() == "sell" else q
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
        marks = ",".join("?" * len(PIPE_REFUSALS))
        for r in self.app.execute(f"SELECT event, metadata, at FROM engineering_log WHERE component = 'Ipc' AND event IN ({marks}) AND at >= ?",
                                  (*PIPE_REFUSALS, self.since_s)):
            meta = {}
            try:
                meta = json.loads(r["metadata"]) if r["metadata"] else {}
            except ValueError:
                pass
            op, code = meta.get("op") if isinstance(meta, dict) else None, meta.get("code") if isinstance(meta, dict) else None
            key = (r["event"], op or "-", code or "-")
            e = pipe.setdefault(key, {"source": "pipe", "what": r["event"], "words": f"op {op or '-'} / {code or '-'}", "count": 0, "last_at": None})
            e["count"] += 1
            e["last_at"] = max(e["last_at"] or "", r["at"] or "")
        ops = [{"source": "deployment-op", "what": r["kind"], "words": short(r["answer"]), "count": r["n"], "last_at": r["last"]} for r in self.app.execute(
            "SELECT kind, answer, COUNT(*) AS n, MAX(created_at) AS last FROM deployment_op WHERE state = 'refused' AND created_at >= ? "
            "GROUP BY kind, answer ORDER BY n DESC, kind", (self.since_s,))]
        groups = sorted(tools + list(pipe.values()) + ops, key=lambda g: (-g["count"], g["source"], g["what"]))
        return {"total": sum(g["count"] for g in groups), "groups": groups}

    # -- 7. ai_attempt errors
    def errors(self):
        rows = self.app.execute(
            "SELECT id, role, state, exit_code, vendor_said, offered_tools, started_at, COALESCE(effective_model, requested_model) AS model "
            "FROM ai_attempt WHERE started_at >= ? ORDER BY started_at", (self.since_s,)).fetchall()
        groups, exits = {}, {}
        bad = 0
        for r in rows:
            failed = (r["exit_code"] not in (None, 0)) or r["vendor_said"] or r["state"] not in ("ENDED", "LAUNCHED")
            if r["exit_code"] is not None:
                exits[str(r["exit_code"])] = exits.get(str(r["exit_code"]), 0) + 1
            if not failed:
                continue
            bad += 1
            said = short(r["vendor_said"], 120) if r["vendor_said"] else ""
            key = (r["state"], r["exit_code"], said)
            g = groups.setdefault(key, {"state": key[0], "exit_code": r["exit_code"], "vendor_said": said or None, "count": 0, "last_at": None,
                                        "positions": set(), "model": r["model"], "offered_tools": None})
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
        used = {"plan": Decimal(0), "key": Decimal(0), "unknown": Decimal(0)}
        board = Decimal(0)
        for r in self.app.execute("SELECT role, state, reserved_cost, cost, billing_class FROM ai_attempt WHERE started_at >= ? AND started_at < ?",
                                  (self.day_from_s, self.day_to_s)):
            c = charge(r)
            used[r["billing_class"] if r["billing_class"] in used else "unknown"] += c
            if (r["role"] or "").startswith(BOARD_PREFIX):
                board += c
        policy = self.app.execute("SELECT version, mode, body FROM board_policy ORDER BY version DESC LIMIT 1").fetchone()
        plan_total, mode, version = None, None, None
        if policy:
            version = policy["version"]
            try:
                body = json.loads(policy["body"])
                plan_total, mode = dec(body.get("plan_day_total")), body.get("mode") or policy["mode"]
            except ValueError:
                pass
        share = None
        try:
            row = self.app.execute("SELECT budget_share FROM board_event WHERE kind IN ('founded', 'budget-share') ORDER BY id DESC LIMIT 1").fetchone()
            share = dec(row[0]) if row else None
        except sqlite3.Error:
            pass
        plan_counted = used["plan"] + used["unknown"]
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
    def wakes(self):
        pending = {}
        for r in self.app.execute("SELECT role, kind, due_at FROM mission_event WHERE consumed_at IS NULL ORDER BY due_at, id"):
            p = pending.setdefault(position_of(r["role"]), {"next_due": r["due_at"], "kind": r["kind"], "pending": 0})
            p["pending"] += 1
        last = {position_of(r["role"]): r["last"] for r in self.app.execute(
            "SELECT role, MAX(consumed_at) AS last FROM mission_event WHERE consumed_at IS NOT NULL GROUP BY role")}
        active = [r["id"] for r in self.app.execute("SELECT id FROM org_position WHERE status = 'active' ORDER BY id")]
        out = []
        for pid in active:
            p = pending.get(pid)
            due = parse_ts(p["next_due"]) if p else None
            out.append({"position": pid, "next_due": p["next_due"] if p else None, "kind": p["kind"] if p else None,
                        "in_seconds": int((due - self.now).total_seconds()) if due else None, "pending": p["pending"] if p else 0,
                        "last_consumed_at": last.get(pid)})
        dispositions, lost, failed = {}, 0, 0
        # A LOST TURN IS NOT A FAILED ONE: a restart took it, and its wakes read `failed` only where the loop has settled them. The
        # launch's own state tells them apart; a disposition this file has not heard of stands as its own word.
        for r in self.app.execute(
                "SELECT e.disposition, a.state, COUNT(*) AS n FROM mission_event e LEFT JOIN ai_attempt a ON a.id = e.consumed_by "
                "WHERE e.consumed_at >= ? GROUP BY e.disposition, a.state", (self.since_s,)):
            word = r["disposition"] or "none"
            dispositions[word] = dispositions.get(word, 0) + r["n"]
            if r["state"] == "LOST":
                lost += r["n"]
            elif word == MISSION_FAILED:
                failed += r["n"]
        after_lost = self.app.execute("SELECT COUNT(*) FROM mission_event WHERE id LIKE ? AND created_at >= ?",
                                      ("%" + LOST_WAKE_SUFFIX, self.since_s)).fetchone()[0]
        blocked = [{"words": short(r["disposition_detail"]), "count": r["n"], "roles": r["roles"].split(",")[:4] if r["roles"] else [], "oldest_due": r["oldest"]}
                   for r in self.app.execute(
                       "SELECT disposition_detail, COUNT(*) AS n, GROUP_CONCAT(DISTINCT COALESCE(role, ?)) AS roles, MIN(due_at) AS oldest FROM mission_event "
                       "WHERE consumed_at IS NULL AND disposition = 'blocked' GROUP BY disposition_detail ORDER BY n DESC", (DEFAULT_POSITION,))]
        return {"active_positions": out, "consumed_since_by_disposition": dispositions, "failed_turn_wakes_since": failed, "lost_turn_wakes_since": lost,
                "raised_again_after_lost_since": after_lost, "blocked_now": blocked,
                "overdue": [o["position"] for o in out if o["in_seconds"] is not None and o["in_seconds"] < 0]}

    # -- 10. R26 § 4 (1): where the loop breaks: each arrow, first reached and how often, and its first refusal in words
    def arrow_specs(self):
        t0 = self.since_s
        events = lambda kind: ("org_event", f"SELECT MIN(at), COUNT(*) FROM org_event WHERE kind = '{kind}' AND at >= ?", (t0,))
        mandate = lambda kind: ("org_mandate_event", f"SELECT MIN(at), COUNT(*) FROM org_mandate_event WHERE kind = '{kind}' AND at >= ?", (t0,))
        return (
            ("charter", events("chartered"), ("org-charter",)),
            ("hire", events("hired"), ("org-hire",)),
            # A hire that activated itself is decided by the hiring head; the owner's own activation is not this arrow.
            ("self-activation", ("org_event", "SELECT MIN(at), COUNT(*) FROM org_event WHERE kind = 'activated' "
                                              "AND decider NOT IN ('owner', 'app', 'board') AND at >= ?", (t0,)), ()),
            ("mandate", mandate("issued"), ("mandate-assign",)),
            ("delivery", mandate("delivered"), ("mandate-deliver",)),
            ("accepted", mandate("accepted"), ("mandate-accept",)),
            ("rejected", mandate("rejected"), ("mandate-reject",)),
            ("backtest", ("strategy_run", "SELECT MIN(created_at), COUNT(*) FROM strategy_run WHERE created_at >= ? AND id NOT IN "
                                          "(SELECT holdout_run_id FROM strategy_promotion WHERE holdout_run_id IS NOT NULL)", (t0,)), ("backtest",)),
            ("verdict asked", ("strategy_verdict", "SELECT MIN(requested_at), COUNT(*) FROM strategy_verdict WHERE requested_at >= ?", (t0,)), ("verdict",)),
            ("verdict given", ("strategy_promotion", "SELECT MIN(at), COUNT(*) FROM strategy_promotion WHERE at >= ?", (t0,)), ()),
            # An owner's or organisation's stop is written as an allocation row too (D19): it is not an allocation.
            ("paper allocation", ("strategy_allocation", "SELECT MIN(at), COUNT(*) FROM strategy_allocation WHERE mode = 'paper' "
                                                         "AND policy_version NOT LIKE '%-stop-%' AND at >= ?", (t0,)), ()),
            ("paper run", ("strategy_deployment", "SELECT MIN(started_at), COUNT(*) FROM strategy_deployment WHERE mode = 'paper' AND started_at >= ?", (t0,)), ()),
            ("paper fill", ("deployment_op", "SELECT MIN(f.at), COUNT(*) FROM fill f JOIN deployment_op o ON (f.request_id = o.request_id OR "
                                             "f.client_order_id = 'TA-' || o.request_id) JOIN strategy_deployment d ON d.id = o.deployment_id "
                                             "WHERE d.mode = 'paper' AND f.at >= ?", (t0,)), ()),
            ("note raised", ("mission_event", "SELECT MIN(created_at), COUNT(*) FROM mission_event WHERE id LIKE ? AND created_at >= ?",
                             (PAPER_RUN_WAKE + "%", t0)), ()),
            ("note taken", ("mission_event", "SELECT MIN(consumed_at), COUNT(*) FROM mission_event WHERE id LIKE ? AND consumed_at >= ? "
                                             "AND consumed_by IN (SELECT id FROM ai_attempt)", (PAPER_RUN_WAKE + "%", t0)), ()),
            ("kill", ("org_decision", "SELECT MIN(at), COUNT(*) FROM (SELECT at FROM org_decision WHERE kind = 'family-kill' AND at >= ? "
                                      "UNION ALL SELECT created_at FROM ledger_entry WHERE kind = 'kill' AND created_at >= ?)", (t0, t0)), ()),
            ("iterate", ("org_decision", "SELECT MIN(at), COUNT(*) FROM org_decision WHERE kind = 'family-branch' AND at >= ?", (t0,)), ()),
        )

    def arrows(self):
        refused = {}
        first_any = None
        for r in self.app.execute("SELECT at, tool, refusal FROM tool_call WHERE served = 0 AND at >= ? ORDER BY at, id", (self.since_s,)):
            item = {"at": r["at"], "what": r["tool"], "words": short(r["refusal"])}
            first_any = first_any or item
            refused.setdefault(r["tool"].replace("_", "-"), []).append(item)
        for r in self.app.execute("SELECT metadata, at FROM engineering_log WHERE component = 'Ipc' AND event = 'op_refused' AND at >= ? ORDER BY at, id",
                                  (self.since_s,)):
            try:
                meta = json.loads(r["metadata"]) if r["metadata"] else {}
            except ValueError:
                meta = {}
            op = meta.get("op") if isinstance(meta, dict) else None
            if op:
                item = {"at": r["at"], "what": op, "words": f"refused at the pipe: {meta.get('code') or '-'}"}
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
                "cannot_be_read": "a keep has no record of its own: only a kill or a branch is written down"}

    # -- 11. R26 § 4 (2): what a turn costs and how fast turns come
    def acted(self):
        """The launches since T0 that left a row of work under their own id, in any table that names its launch."""
        ids = set()
        for table in ACT_TABLES:
            for col in ("attempt_id", "attempt"):
                if self.has(table, col):
                    ids.update(r[0] for r in self.app.execute(
                        f"SELECT DISTINCT {col} FROM {table} WHERE {col} IN (SELECT id FROM ai_attempt WHERE started_at >= ?)", (self.since_s,)))
        return ids

    def pace(self):
        rows = self.app.execute(
            "SELECT id, role, state, exit_code, reserved_cost, cost, vendor_said, started_at, ended_at, "
            "COALESCE(effective_model, requested_model) AS model FROM ai_attempt WHERE started_at >= ? ORDER BY started_at", (self.since_s,)).fetchall()
        acted = self.acted()
        # A head's chart act names no launch, only its decider: it counts for the turn of that position it fell inside.
        chart = [(r["decider"], r["at"]) for r in self.app.execute(
            "SELECT decider, at FROM org_event WHERE at >= ? AND decider NOT IN ('owner', 'app', 'board')", (self.since_s,))]
        per, limit_hits, first_limit = {}, 0, None
        for r in rows:
            pos = position_of(r["role"])
            p = per.setdefault(pos, {"position": pos, "turns": 0, "cost": Decimal(0), "clean": 0, "idle": 0, "models": set(), "waits": []})
            p["turns"] += 1
            p["cost"] += charge(r)
            if r["model"]:
                p["models"].add(r["model"])
            if r["state"] == "ENDED" and r["exit_code"] == 0:
                p["clean"] += 1
                inside = any(who == pos and r["started_at"] <= at <= (r["ended_at"] or self.now_s) for who, at in chart)
                if r["id"] not in acted and not inside:
                    p["idle"] += 1
            if r["vendor_said"] and VENDOR_LIMIT_WORDS.search(r["vendor_said"]):
                limit_hits += 1
                first_limit = first_limit or {"at": r["started_at"], "position": pos, "words": short(r["vendor_said"])}
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
                "vendor_limit_refusals": limit_hits, "first_vendor_limit": first_limit, "positions": out,
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
                                  "WHERE f.scored = 1 AND r.outcome <> 'void'"):
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
        every = self.app.execute("SELECT c.*, d.pair FROM strategy_campaign c LEFT JOIN dataset d ON d.id = c.holdout_dataset_id ORDER BY c.id").fetchall()
        parent = {c["id"]: c["renewed_from"] for c in every}
        by_lineage = {}
        for r in self.app.execute("SELECT DISTINCT p.campaign_id, a.version_id FROM strategy_allocation a JOIN strategy_promotion p ON p.id = a.promotion_id "
                                  "WHERE a.policy_version = ?", (INCONCLUSIVE_POLICY,)):
            by_lineage.setdefault(r[0], set()).add(r[1])
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
            verdicts = self.app.execute("SELECT COUNT(*) FROM strategy_verdict WHERE campaign_id = ?", (c["id"],)).fetchone()[0]
            out.append({"id": c["id"], "pair": c["pair"], "name": c["name"], "closed": c["closed_at"] is not None, "renewed_from": c["renewed_from"],
                        "trials": trials, "trial_budget": c["trial_budget"], "exploration": explore, "exploration_budget": c["exploration_budget"] if pots else None,
                        "verdicts": verdicts, "verdict_budget": c["verdict_budget"], "holdout_from": c["holdout_from"]})
        return {"campaigns": out}

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
        gaps = self.app.execute("SELECT COUNT(*) FROM cell_proof_row WHERE proof_id = ? AND kind = 'GAP'", (newest["id"],)).fetchone()[0] if newest else None
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
            ("verdicts", "verdicts"), ("deployments", "deployments"), ("refusals", "refusals"), ("errors", "errors"), ("pace", "pace"),
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
    result["attention"] = attention(result)
    for c in (app, tape):
        if c is not None:
            c.close()
    return result


def attention(d):
    flags = []
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
        if g["mode"] != "paper" and g["state"] != "ended":
            flags.append(f"{g['count']} deployment(s) in mode '{g['mode']}' ({g['state']}): NOT paper")
    cp = d.get("cell_proof", {})
    if cp.get("current") is False:
        flags.append("cell proof is NOT current here: " + ", ".join(cp.get("differs") or ["no proof"]) + " differ")
    elif cp.get("current") is None and "unreadable" not in cp:
        flags.append("cell proof currency unknown: " + str(cp.get("why", "")))
    er = d.get("errors", {})
    lost = sum(g["count"] for g in er.get("groups", []) if g["state"] == "LOST")
    wk = d.get("wakes", {})
    if wk.get("failed_turn_wakes_since") or wk.get("lost_turn_wakes_since"):
        flags.append(f"wakes: {wk.get('failed_turn_wakes_since', 0)} on failed turns, {wk.get('lost_turn_wakes_since', 0)} on lost turns, "
                     f"{wk.get('raised_again_after_lost_since', 0)} raised again")
    if lost:
        flags.append(f"{lost} AI launch(es) LOST since T0")
    if er.get("failed_since"):
        flags.append(f"{er['failed_since']} of {er['attempts_since']} AI launches failed or reported a vendor error since T0")
    od = d.get("wakes", {}).get("overdue") or []
    if od:
        flags.append("overdue wake(s): " + ", ".join(od[:4]))
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
    if bp.get("mode") not in (None, "OBSERVE", "PAPER"):
        flags.append(f"board policy mode is {bp['mode']}")
    for key, v in d.items():
        if isinstance(v, dict) and "unreadable" in v and key != "tape_health":
            flags.append(f"section {key} unreadable: {v['unreadable']}")
    return flags


# ---- rendering -------------------------------------------------------------------------------------------------------------
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
        L.append("ATTENTION")
        add_list(d["attention"], cap["attention"], lambda a: "  ! " + a)

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
                 f"unrealised {x['unrealised'] if x['unrealised'] is not None else '?'}  fees {x['fees']}" + (f"(+{x['fees_unknown_fills']} unknown)" if x["fees_unknown_fills"] else ""))
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
                 f"{g['vendor_said'] or '(no vendor words)'}" + (f" [offered {g['offered_tools']} tools]" if g["offered_tools"] is not None else ""))

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
        add_list(s["active_positions"], cap["wakes"], lambda w: f"  {w['position']:<14} " + (
            f"{w['kind']} in {age(w['in_seconds'])} ({w['pending']} pending)" if w["next_due"] else "no wake queued") + f"  last woken {w['last_consumed_at'][:19] if w['last_consumed_at'] else '-'}")
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


def human(d):
    caps = [dict(attention=6, positions=5, turns=7, events=4, arrows=17, deployments=4, refusals=5, errors=4, forecasts=4, campaigns=6, wakes=6, blocked=2),
            dict(attention=4, positions=3, turns=4, events=3, arrows=12, deployments=3, refusals=3, errors=3, forecasts=3, campaigns=4, wakes=3, blocked=1),
            dict(attention=3, positions=2, turns=2, events=2, arrows=8, deployments=1, refusals=2, errors=2, forecasts=2, campaigns=3, wakes=2, blocked=1),
            dict(attention=2, positions=1, turns=1, events=1, arrows=4, deployments=0, refusals=1, errors=1, forecasts=1, campaigns=2, wakes=1, blocked=1)]
    for cap in caps:
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
    a = ap.parse_args(argv)
    try:
        now = parse_ts(a.now) if a.now else dt.datetime.now(dt.timezone.utc)
        if now is None:
            raise ValueError(f"--now '{a.now}' is not an ISO instant with a zone")
        since = parse_since(a.since, now)
    except ValueError as ex:
        print(f"org-digest: {ex}", file=sys.stderr)
        return 2
    state = a.state or os.path.join(a.home, STATE_UNDER_HOME)
    opts = {"no_systemd": a.no_systemd, "machine_id_file": a.machine_id_file, "boot_id_file": a.boot_id_file, "osrelease_file": a.osrelease_file}
    result = build(os.path.join(state, "tradeagent.db"), os.path.join(state, "tape.db"), state, now, since, opts)
    if a.json:
        print(json.dumps(result, indent=2, sort_keys=True, default=str))
    else:
        print("\n".join(human(result)))
    return 1 if result["app_db"] == "absent" else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
