"""lin-sing studio: a timeline editor in the browser for one song folder (no extra packages: the standard library's http.server).

  python linsing.py studio ../songs/<name> [--port 8765]

Shows the waveform, bars, lyric lines, the dope sheet (moves), faces and QA marks on one timeline; plays the song; edits the dope sheet
(move / face / level / start time, split, delete); saves and bakes (QA shown); renders a preview of a selected range and plays it in the page.
Listens on 127.0.0.1 only.
"""
import os, sys, json, threading, subprocess, webbrowser, mimetypes, re
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__)); PIPE = os.path.dirname(HERE)
TASK = dict(running=False, name="", log="", result=None)


def peaks(F, per_s=50):
    import soundfile as sf
    p = os.path.join(F, "peaks.json")
    if os.path.exists(p): return json.load(open(p))
    x, sr = sf.read(os.path.join(F, "song.wav"))
    if x.ndim > 1: x = x.mean(1)
    hop = int(sr / per_s); n = len(x) // hop
    v = np.abs(x[:n * hop]).reshape(n, hop).max(1); v = (v / (v.max() + 1e-9)).round(3).tolist()
    json.dump(dict(per_s=per_s, v=v), open(p, "w")); return dict(per_s=per_s, v=v)


def data(F):
    J = lambda *p: os.path.join(F, *p)
    st = json.load(open(J("structure.json")))
    d = dict(song=json.load(open(J("song.json"), encoding="utf-8")), choreo=json.load(open(J("choreo.json"), encoding="utf-8")),
             bars=[dict(t=b["t"], total=b["total"]) for b in st["bars"]], beats=st["beats"], peaks=peaks(F),
             lines=[dict(t=l[2], end=l[3], score=l[4], text=l[5] if len(l) > 5 else "") for l in json.load(open(J("morae.json"), encoding="utf-8"))["lines"]],
             qa=json.load(open(J("qa.json"), encoding="utf-8")) if os.path.exists(J("qa.json")) else None)
    from lin_sing.choreo import MOVES
    d["move_levels"] = {k: v["level"] for k, v in MOVES.items()}
    return d


def run_task(name, cmd):
    def work():
        TASK.update(running=True, name=name, log="", result=None)
        p = subprocess.Popen(cmd, cwd=PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        for line in p.stdout: TASK["log"] += line
        p.wait(); res = dict(ok=p.returncode == 0)
        m = re.findall(r"\[done\] (.+\.mp4)", TASK["log"])
        if m: res["video"] = os.path.basename(m[-1].strip())
        TASK.update(running=False, result=res)
    if TASK["running"]: return False
    threading.Thread(target=work, daemon=True).start(); return True


def serve(F, port=8765, open_browser=True):
    F = os.path.abspath(F); page = os.path.join(HERE, "studio.html")

    class H(BaseHTTPRequestHandler):
        def log_message(self, *a): pass
        def send(self, code, body, ctype="application/json; charset=utf-8"):
            b = body if isinstance(body, bytes) else (json.dumps(body, ensure_ascii=False) if not isinstance(body, str) else body).encode("utf-8")
            self.send_response(code); self.send_header("Content-Type", ctype); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)

        def do_GET(self):
            if self.path in ("/", "/index.html"): return self.send(200, open(page, "rb").read(), "text/html; charset=utf-8")
            if self.path == "/api/data": return self.send(200, data(F))
            if self.path == "/api/task": return self.send(200, TASK)
            if self.path.startswith("/media/"):
                name = os.path.basename(self.path[7:].split("?")[0]); fp = os.path.join(F, name)
                if not os.path.isfile(fp) or not name.lower().endswith((".wav", ".mp4", ".mp3")): return self.send(404, {"error": "no such file"})
                size = os.path.getsize(fp); rng = self.headers.get("Range"); a, b = 0, size - 1
                if rng:
                    m = re.match(r"bytes=(\d*)-(\d*)", rng)
                    if m: a = int(m.group(1) or 0); b = int(m.group(2)) if m.group(2) else size - 1
                b = min(b, size - 1); n = b - a + 1
                self.send_response(206 if rng else 200); self.send_header("Content-Type", mimetypes.guess_type(fp)[0] or "application/octet-stream")
                self.send_header("Accept-Ranges", "bytes"); self.send_header("Content-Length", str(n))
                if rng: self.send_header("Content-Range", f"bytes {a}-{b}/{size}")
                self.end_headers()
                with open(fp, "rb") as f:
                    f.seek(a); left = n
                    while left > 0:
                        chunk = f.read(min(1 << 20, left))
                        if not chunk: break
                        try: self.wfile.write(chunk)
                        except (ConnectionResetError, BrokenPipeError): return
                        left -= len(chunk)
                return
            self.send(404, {"error": "not found"})

        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0)) or 0) or b"{}")
            py = [sys.executable, "-X", "utf8", os.path.join(PIPE, "linsing.py")]
            if self.path == "/api/choreo":
                p = os.path.join(F, "choreo.json"); d = json.load(open(p, encoding="utf-8"))
                sheet = sorted(body["sheet"], key=lambda s: float(s["t"]))
                bad = [s.get("move") for s in sheet if s.get("move") not in d["moves"]]
                if bad: return self.send(400, {"error": f"unknown moves {bad}"})
                for i, s in enumerate(sheet): s["end"] = round(float(sheet[i + 1]["t"]) if i + 1 < len(sheet) else float(d["sheet"][-1]["end"]), 3)
                d["sheet"] = sheet
                if body.get("mood"): d["mood"] = body["mood"]
                json.dump(d, open(p, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
                started = run_task("bake", py + ["bake", F]) if body.get("bake") else False
                return self.send(200, {"saved": True, "baking": started})
            if self.path == "/api/render":
                cmd = py + ["render", F] + (["--from", str(body["from"]), "--to", str(body["to"])] if body.get("from") is not None else [])
                return self.send(200, {"started": run_task("render", cmd)})
            self.send(404, {"error": "not found"})

    srv = ThreadingHTTPServer(("127.0.0.1", port), H); url = f"http://127.0.0.1:{port}/"
    print(f"lin-sing studio: {url}   (song folder {F}; Ctrl+C to stop)", flush=True)
    if open_browser: webbrowser.open(url)
    try: srv.serve_forever()
    except KeyboardInterrupt: pass
