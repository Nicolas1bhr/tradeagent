#!/usr/bin/env python3
"""A scripted Responses-API mock for the route (a) Cell rows (U-cell-linux-c). No model, no auth; 127.0.0.1 only.
usage: mock_responses.py PORTFILE CASE.json LOGDIR
Binds an ephemeral port and writes it to PORTFILE. CASE.json: {"steps": [<output item>, ...], "final": "text"}.
Step k (k = the number of tool outputs already in the request's input) is answered as one response.output_item.done; once every
step is answered the final message is sent. Every request body is logged to LOGDIR/NNN.json (auth headers never written)."""
import itertools, http.server, json, os, sys
PORTFILE, CASE, LOG = sys.argv[1], sys.argv[2], sys.argv[3]
os.makedirs(LOG, exist_ok=True)
case = json.load(open(CASE))
seq = itertools.count(1)
OUTS = {"function_call_output", "custom_tool_call_output", "mcp_tool_call_output"}

class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        self.send_response(404); self.end_headers()
    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("content-length", 0)))
        try: body = json.loads(raw)
        except Exception: body = {"undecodable": len(raw)}
        n = next(seq)
        with open(os.path.join(LOG, "%03d.json" % n), "w") as f: json.dump({"path": self.path, "body": body}, f)
        if not self.path.endswith("/responses"):
            self.send_response(404); self.end_headers(); return
        items = body.get("input", []) if isinstance(body, dict) else []
        k = len([i for i in items if isinstance(i, dict) and i.get("type") in OUTS])
        rid = "resp_%d" % n
        if k < len(case["steps"]):
            item = case["steps"][k]
        else:
            item = {"type": "message", "role": "assistant", "id": "msg_%d" % n,
                    "content": [{"type": "output_text", "text": case.get("final", "done")}]}
        evs = [{"type": "response.created", "response": {"id": rid}},
               {"type": "response.output_item.done", "item": item},
               {"type": "response.completed", "response": {"id": rid, "usage": {"input_tokens": 0, "input_tokens_details": None,
                "output_tokens": 0, "output_tokens_details": None, "total_tokens": 0}}}]
        data = "".join("event: %s\ndata: %s\n\n" % (e["type"], json.dumps(e)) for e in evs).encode()
        self.send_response(200)
        self.send_header("content-type", "text/event-stream")
        self.send_header("content-length", str(len(data)))
        self.end_headers(); self.wfile.write(data)

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
with open(PORTFILE + ".tmp", "w") as f: f.write(str(server.server_address[1]))
os.rename(PORTFILE + ".tmp", PORTFILE)
server.serve_forever()
