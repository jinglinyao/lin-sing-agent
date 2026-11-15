"""lin-sing in the browser (no extra packages: the standard library's http.server). Listens on 127.0.0.1 only.

  python linsing.py app                    the home page: new song (upload, lyrics, mood -> video), my songs   [normal mode]
  python linsing.py studio <song folder>   straight into the timeline editor of one song                      [advanced mode]

Normal mode: drop a song in, paste the lyrics (optional), pick a mood, press 生成 - the page shows each step and plays / offers the video.
Advanced mode: the timeline editor (moves, faces, QA, part previews); AI agents can drive the same folders (AGENTS.md, MCP).
Only one job runs at a time (the graphics card is used by every step).
"""
import os, sys, json, threading, subprocess, webbrowser, mimetypes, re, secrets, time, urllib.parse
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__)); PIPE = os.path.dirname(HERE)
TASK = dict(running=False, name="", song="", log="", result=None, started=0.0)
AUDIO = (".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma")

# the steps a new song goes through, as they show in the log -> what the page says
STEPS = [("1 audio", "读取音频"), ("2 voice", "分离人声和伴奏"), ("3 lyric lines", "识别 / 对齐歌词"), ("4 syllable", "对齐每个音节"),
         ("5 music", "分析节奏和段落"), ("7 choreography", "自动编舞"), ("6+7 face", "生成表情和动作"), ("8 camera", "安排镜头和灯光"),
         ("9 lyric captions", "生成字幕"), ("chunk(s) of", "渲染视频"), ("[done] ", "完成")]

# what went wrong, in words a user can act on
FRIENDLY = [
    (r"NO_GPU: (.+)", None),
    (r"超过了 10 分钟上限[^\n]*", None),
    (r"CUDA out of memory|OutOfMemoryError|cudaErrorMemoryAllocation", "显卡显存不够：请关掉游戏、直播软件和其他 AI 程序后再试；还不行的话，换一首短一点的歌。"),
    (r"ffprobe could not read|Invalid data found when processing input|could not read song\.wav", "这个音频文件读不了：可能已经损坏，或者格式不支持。请换成 mp3、wav、flac 或 m4a 再试。"),
    (r"no singable text found", "没有识别到歌词：请确认这首歌有人声；也可以在“歌词”框里贴上歌词再试。"),
    (r"is missing from the package", "程序包不完整：请重新解压整个压缩包（要解压全部分卷，不要只解压一部分）。"),
    (r"Unity scripts do not compile|the player stopped|Unity stopped writing|chunk\(s\) were not written|LinSing\.exe not found",
     "视频渲染程序出错了：请重启电脑后再试；如果反复出现，把作品文件夹里的 unity_render.log 发给开发者。"),
]


def friendly(log):
    for pat, msg in FRIENDLY:
        m = re.search(pat, log)
        if m: return msg or (m.group(1) if m.groups() else m.group(0))
    tail = [l for l in log.strip().splitlines() if l.strip()][-1:] or ["未知错误"]
    return "出错了：" + tail[0][:200]


def steps_of(log):
    """[{label, state}] for the page: done / running / waiting, from the markers the pipeline prints"""
    out = []; last = -1
    for i, (mark, label) in enumerate(STEPS):
        hit = re.search(r"\[done\] .+\.mp4", log) if label == "完成" else mark in log     # every sub-step prints [done]: only the video counts
        if hit: last = i
    for i, (mark, label) in enumerate(STEPS):
        out.append(dict(label=label, state="done" if i < last or (i == last and label == "完成") else "running" if i == last else "waiting"))
    m = re.findall(r"progress (\d+)/(\d+)", log)
    if m and last >= 0 and STEPS[last][1] == "渲染视频": out[last]["detail"] = f"{m[-1][0]} / {m[-1][1]} 段"
    return out


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


def songs(ROOT):
    out = []
    for n in sorted(os.listdir(ROOT)) if os.path.isdir(ROOT) else []:
        F = os.path.join(ROOT, n)
        if not os.path.isdir(F) or n.startswith((".", "_")): continue
        meta = json.load(open(os.path.join(F, "song.json"), encoding="utf-8")) if os.path.exists(os.path.join(F, "song.json")) else {}
        vid = os.path.join(F, n + ".mp4")
        out.append(dict(name=n, duration=meta.get("duration"), lang=meta.get("lang"), ready=os.path.exists(os.path.join(F, "choreo.json")),
                        video=(n + ".mp4") if os.path.exists(vid) else None, time=os.path.getmtime(vid if os.path.exists(vid) else F)))
    return sorted(out, key=lambda s: -s["time"])


def run_task(name, song, cmd):
    def work():
        TASK.update(running=True, name=name, song=song, log="", result=None, started=time.time())
        env = dict(os.environ, NO_COLOR="1", PYTHON_COLORS="0", TQDM_DISABLE="1")      # plain text in the log (no colour codes, no progress bars)
        p = subprocess.Popen(cmd, cwd=PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", env=env)
        for line in p.stdout: TASK["log"] += line
        p.wait(); res = dict(ok=p.returncode == 0)
        m = re.findall(r"\[done\] (.+\.mp4)", TASK["log"])
        if m: res["video"] = os.path.basename(m[-1].strip())
        if not res["ok"]: res["error"] = friendly(TASK["log"])
        TASK.update(running=False, result=res)
    if TASK["running"]: return False
    threading.Thread(target=work, daemon=True).start(); return True


def safe_name(s):
    s = re.sub(r"[^\w\-]+", "_", os.path.splitext(os.path.basename(s or ""))[0]).strip("_")[:40]
    return s or "song"


def serve(ROOT, port=8765, open_browser=True, first_song=None):
    """ROOT = the songs folder"""
    ROOT = os.path.abspath(ROOT); os.makedirs(ROOT, exist_ok=True)
    pages = {"/": os.path.join(HERE, "home.html"), "/edit": os.path.join(HERE, "studio.html")}
    # Security: the server listens on 127.0.0.1 only; besides, every request must name this machine as its Host (a web page elsewhere cannot
    # reach it by DNS rebinding), and every /api request must carry the token handed to the page at start-up (another site's page cannot
    # read it, so it cannot upload, render or rewrite anything here).
    token = secrets.token_urlsafe(18); hosts = {f"127.0.0.1:{port}", f"localhost:{port}"}
    py = [sys.executable, "-X", "utf8", os.path.join(PIPE, "linsing.py")]

    def folder(q):
        n = os.path.basename((q.get("song") or [""])[0])
        F = os.path.join(ROOT, n)
        return F if n and os.path.isdir(F) else None

    class H(BaseHTTPRequestHandler):
        def log_message(self, *a): pass
        def send(self, code, body, ctype="application/json; charset=utf-8"):
            b = body if isinstance(body, bytes) else (json.dumps(body, ensure_ascii=False) if not isinstance(body, str) else body).encode("utf-8")
            self.send_response(code); self.send_header("Content-Type", ctype); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)

        def guard(self, need_token):
            if self.headers.get("Host", "") not in hosts: self.send(403, {"error": "wrong host"}); return False
            if need_token and not secrets.compare_digest(self.headers.get("X-LinSing-Token", ""), token): self.send(403, {"error": "missing token"}); return False
            return True

        def do_GET(self):
            u = urllib.parse.urlparse(self.path); q = urllib.parse.parse_qs(u.query, keep_blank_values=True)
            if not self.guard(u.path.startswith("/api/")): return
            if u.path in pages:
                html = open(pages[u.path], encoding="utf-8").read().replace("</head>", f'<meta name="lin-token" content="{token}"></head>', 1)
                return self.send(200, html, "text/html; charset=utf-8")
            if u.path == "/api/songs": return self.send(200, dict(songs=songs(ROOT), busy=TASK["running"]))
            if u.path == "/api/task":
                t = dict(TASK); t["steps"] = steps_of(TASK["log"]); t["elapsed"] = round(time.time() - TASK["started"]) if TASK["started"] else 0
                return self.send(200, t)
            if u.path == "/api/data":
                F = folder(q)
                return self.send(200, data(F)) if F else self.send(404, {"error": "no such song"})
            if u.path.startswith("/media/"):
                parts = [urllib.parse.unquote(x) for x in u.path[7:].split("/")]
                if len(parts) != 2: return self.send(404, {"error": "no such file"})
                F = os.path.join(ROOT, os.path.basename(parts[0])); name = os.path.basename(parts[1]); fp = os.path.join(F, name)
                if not os.path.isfile(fp) or not name.lower().endswith((".wav", ".mp4", ".mp3")): return self.send(404, {"error": "no such file"})
                size = os.path.getsize(fp); rng = self.headers.get("Range"); a, b = 0, size - 1
                if rng:
                    m = re.match(r"bytes=(\d*)-(\d*)", rng)
                    if m: a = int(m.group(1) or 0); b = int(m.group(2)) if m.group(2) else size - 1
                b = min(b, size - 1); n = b - a + 1
                self.send_response(206 if rng else 200); self.send_header("Content-Type", mimetypes.guess_type(fp)[0] or "application/octet-stream")
                self.send_header("Accept-Ranges", "bytes"); self.send_header("Content-Length", str(n))
                if rng: self.send_header("Content-Range", f"bytes {a}-{b}/{size}")
                if "download" in q: self.send_header("Content-Disposition", "attachment; filename*=UTF-8''" + urllib.parse.quote(name))
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
            u = urllib.parse.urlparse(self.path); q = urllib.parse.parse_qs(u.query)
            if not self.guard(True): return
            n = int(self.headers.get("Content-Length", 0) or 0)
            if u.path == "/api/upload":                                     # the song itself, as the raw request body
                ext = os.path.splitext((q.get("file") or [""])[0])[1].lower()
                if ext not in AUDIO: return self.send(400, {"error": "不支持这种文件，请用 mp3、wav、flac、m4a 等音频文件。"})
                if n > 400 * 2 ** 20: return self.send(400, {"error": "文件太大（超过 400 MB）。"})
                if TASK["running"]: return self.send(409, {"error": "正在处理另一首歌，请等它完成。"})
                name = safe_name((q.get("file") or ["song"])[0]); base = name; k = 2
                while os.path.exists(os.path.join(ROOT, name)): name = f"{base}_{k}"; k += 1
                F = os.path.join(ROOT, name); os.makedirs(F)
                with open(os.path.join(F, "source" + ext), "wb") as f:
                    left = n
                    while left > 0:
                        chunk = self.rfile.read(min(1 << 20, left))
                        if not chunk: break
                        f.write(chunk); left -= len(chunk)
                return self.send(200, {"song": name, "file": "source" + ext})
            body = json.loads(self.rfile.read(n) or b"{}")
            F = folder(q) or (os.path.join(ROOT, os.path.basename(body.get("song", ""))) if body.get("song") else None)
            if u.path == "/api/new":
                if not F or not os.path.isdir(F): return self.send(404, {"error": "找不到上传的歌曲，请重新上传。"})
                src = next((f for f in os.listdir(F) if f.startswith("source.")), None)
                if not src: return self.send(404, {"error": "找不到上传的歌曲，请重新上传。"})
                cmd = py + ["make", os.path.join(F, src), "--name", os.path.basename(F), "--mood", body.get("mood", "neutral"), "--render"]
                lyr = (body.get("lyrics") or "").strip()
                if lyr:
                    open(os.path.join(F, "lyrics.txt"), "w", encoding="utf-8").write(lyr + "\n"); cmd += ["--lyrics", os.path.join(F, "lyrics.txt")]
                ok = run_task("new", os.path.basename(F), cmd)
                return self.send(200 if ok else 409, {"started": ok} if ok else {"error": "正在处理另一首歌，请等它完成。"})
            if not F: return self.send(404, {"error": "no such song"})
            if u.path == "/api/choreo":
                p = os.path.join(F, "choreo.json"); d = json.load(open(p, encoding="utf-8"))
                sheet = sorted(body["sheet"], key=lambda s: float(s["t"]))
                bad = [s.get("move") for s in sheet if s.get("move") not in d["moves"]]
                if bad: return self.send(400, {"error": f"unknown moves {bad}"})
                for i, s in enumerate(sheet): s["end"] = round(float(sheet[i + 1]["t"]) if i + 1 < len(sheet) else float(d["sheet"][-1]["end"]), 3)
                d["sheet"] = sheet
                if body.get("mood"): d["mood"] = body["mood"]
                json.dump(d, open(p, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
                started = run_task("bake", os.path.basename(F), py + ["bake", F]) if body.get("bake") else False
                return self.send(200, {"saved": True, "baking": started})
            if u.path == "/api/render":
                cmd = py + ["render", F] + (["--from", str(body["from"]), "--to", str(body["to"])] if body.get("from") is not None else [])
                return self.send(200, {"started": run_task("render", os.path.basename(F), cmd)})
            if u.path == "/api/play":
                subprocess.Popen(py + ["play", F], cwd=PIPE); return self.send(200, {"playing": True})
            self.send(404, {"error": "not found"})

    srv = ThreadingHTTPServer(("127.0.0.1", port), H)
    url = f"http://127.0.0.1:{port}/" + (f"edit?song={urllib.parse.quote(first_song)}" if first_song else "")
    print(f"lin-sing: {url}   (songs in {ROOT}; close this window to stop)", flush=True)
    if open_browser: webbrowser.open(url)
    try: srv.serve_forever()
    except KeyboardInterrupt: pass
