#!/usr/bin/env python3
"""THE ORGANISATION'S DIGEST (U-org-digest): the fleet's eyes on the AI organisation during an unattended run.

READ-ONLY, ALL OF IT. tools/linux-host.sh org-digest sends this file over ssh and runs it as the app's own account, against the
app's two state databases (state/tradeagent.db, state/tape.db) and nothing else: every database is opened with SQLite's
mode=ro and PRAGMA query_only=ON, so a statement that wrote would fail rather than run. It never reads a credential, the seat
home's codex folder or anything holding the owner's auth.json, never names the host, and never writes a file.

  org-digest.py [--home /var/lib/tradeagent | --state <dir>] [--since <T0>] [--json]

  --since   T0: an ISO date or date-time (no zone = the host's local zone), or a span back from now: 72h, 3d. Default: the start
            of the host's local day (the owner's day: the one the AI's daily ceilings reset on).
  --json    the whole digest as one JSON object; otherwise a human digest of at most 60 lines.
  --now     (tests) the instant "now" is, ISO with a zone.   --no-systemd   (tests) do not ask systemd about the unit.
  --machine-id-file / --boot-id-file / --osrelease-file   (tests) where the cell proof's host identity is read from.

Every figure is a MEASUREMENT the app wrote (ai_attempt, org_event, mission_event, tool_call, strategy_*, fill, cell_proof,
tape_fetch). A deployment's profit and loss is a SIMULATION (paper): a book of fills at modelled prices, never money. A cost is the
AI's list-price equivalent, never a bill. A section that cannot be read says so; it is never a guess and never a zero.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import re
import sqlite3
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
            "SUM(CASE WHEN at >= ? THEN 1 ELSE 0 END) AS new FROM strategy_allocation", (self.since_s,)).fetchone()
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
                    "total": allocs["n"], "open": allocs["open"] or 0, "new_since": allocs["new"] or 0},
                "by_mode_state": [{"mode": m, "state": s, "count": n} for (m, s), n in sorted(by.items())],
                "deployments": listed}

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
            failed = (r["exit_code"] not in (None, 0)) or r["vendor_said"] or r["state"] == "LOST"
            if r["exit_code"] is not None:
                exits[str(r["exit_code"])] = exits.get(str(r["exit_code"]), 0) + 1
            if not failed:
                continue
            bad += 1
            said = short(r["vendor_said"], 120) if r["vendor_said"] else ""
            key = (r["state"] if r["state"] == "LOST" else "ENDED", r["exit_code"], said)
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
        dispositions = {(r["disposition"] or "none"): r["n"] for r in self.app.execute(
            "SELECT disposition, COUNT(*) AS n FROM mission_event WHERE consumed_at >= ? GROUP BY disposition", (self.since_s,))}
        return {"active_positions": out, "consumed_since_by_disposition": dispositions,
                "overdue": [o["position"] for o in out if o["in_seconds"] is not None and o["in_seconds"] < 0]}

    # -- 10. is the cell proof current?
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

    # -- 11. the tape's health line
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


SECTIONS = (("positions", "positions"), ("turns", "turns"), ("org_events", "org_events"), ("verdicts", "verdicts"),
            ("deployments", "deployments"), ("refusals", "refusals"), ("errors", "errors"), ("day_total", "day_total"),
            ("wakes", "wakes"), ("cell_proof", "cell_proof"), ("tape_health", "tape_health"))


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

    L.append(f"ORG DIGEST (read-only)  now {d['now'][:19]}Z  since T0 {d['since'][:19]}Z  app-db {d['app_db']}  tape-db {d['tape_db']}")
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
        add_list(s["positions"], cap["turns"], lambda p: f"  {p['position']:<14} today {p['turns_today']:>3} / {p['cost_today']:<9} since {p['turns_since']:>3} / {p['cost_since']}")

    s = d["org_events"]
    if not unread("org events", s):
        L.append(f"org events since T0: {s['total']}" + ("" if s["events"] else "  (none)"))
        add_list(s["events"], cap["events"], lambda e: f"  {e['kind']:<18} {e['count']:>4}  last {e['last_at'][:19] if e['last_at'] else '-'}  by {'/'.join(sorted(e['deciders']))}")
        if s["mandate_events"]:
            L.append("  mandate events: " + ", ".join(f"{m['kind']} {m['count']}" for m in s["mandate_events"][:5]))

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
    caps = [dict(attention=4, positions=5, turns=7, events=6, deployments=5, refusals=6, errors=5, wakes=6),
            dict(attention=3, positions=3, turns=4, events=3, deployments=3, refusals=3, errors=3, wakes=3),
            dict(attention=2, positions=2, turns=2, events=2, deployments=1, refusals=2, errors=2, wakes=2),
            dict(attention=1, positions=1, turns=1, events=1, deployments=0, refusals=1, errors=1, wakes=1)]
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
