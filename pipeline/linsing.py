"""lin-sing: give lin a song, she sings it - mouth on the syllables, body on the music.

  python linsing.py make  <audio> [--lyrics lyrics.txt|.lrc] [--lang ja|zh|en] [--name NAME] [--render]
  python linsing.py bake  <song folder>          re-bake body + camera after editing choreo.json (no audio analysis again)
  python linsing.py load  <song folder>          load it into the Unity project (the editor must be closed; or use the menu LinSing / Load Song Folder)
  python linsing.py render <song folder>         render with Unity and mux the sound -> <song folder>/<name>.mp4
  python linsing.py check <audio>                only the length check

Audio up to 10 minutes. Above 5 minutes every step still works but is slow, and the GPU memory needed for the alignment
grows with the length: the computer may stutter while it runs.
Steps (each writes one file into songs/<name>/, so any step can be redone or replaced alone - see docs/DESIGN.md):
  1 song.wav       the audio, 44.1 kHz              5 structure.json  tempo, beats, bars, loudness of the band per bar
  2 sep/...        voice / band (Demucs)            6 face.json       mouth shapes, blinks, eye moves per frame (60 fps)
  3 lines.json     lyric lines with times           7 choreo.json     the dope sheet (editable) -> key.bin + look.txt
  4 morae.json     every syllable, aligned          8 show.txt        the camera path
"""
import os, sys, json, glob, shutil, argparse, subprocess, time
HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
MAX_S, WARN_S = 600.0, 300.0
# When an agent reads this program's output through a pipe, Python writes in the system code page (GBK on Chinese Windows), which has no
# ✓ / ✗ and no kana in some cases - the report would crash. Piped output is UTF-8; a real console is left alone.
for _s in (sys.stdout, sys.stderr):
    try:
        if not _s.isatty(): _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception: pass
PY = [sys.executable, "-X", "utf8"]


def log(*a): print(*a, flush=True)


def duration_of(path):
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path], capture_output=True, text=True)
    if r.returncode != 0: raise SystemExit(f"ffprobe could not read {path}: {r.stderr.strip()} (is ffmpeg installed and on PATH?)")
    return float(r.stdout.strip())


def check(path):
    d = duration_of(path)
    if d > MAX_S: raise SystemExit(f"音频长 {d / 60:.1f} 分钟，超过了 10 分钟上限，请先剪短。\n"
                                   f"The audio is {d / 60:.1f} minutes long. lin-sing takes audio up to 10 minutes - please cut it first.")
    if d > WARN_S: log(f"注意：音频长 {d / 60:.1f} 分钟。一次输入五分钟以上的音频可能会造成卡顿（人声分离、对齐、动作烘焙都随长度变慢、占用更多显存），"
                       f"能跑完但会慢；也可以先切成几段。\n"
                       f"NOTE: the audio is {d / 60:.1f} minutes long. Audio over 5 minutes in one go can make the computer stutter; it will work, just slowly.")
    else: log(f"audio: {d:.1f} s - ok")
    return d


def step(name, done_file, fn, force=False):
    if os.path.exists(done_file) and not force: log(f"[skip] {name}: {os.path.basename(done_file)} exists"); return
    t = time.time(); log(f"[....] {name}"); fn(); log(f"[done] {name} ({time.time() - t:.0f} s)")


def run(cmd, **kw):
    r = subprocess.run(cmd, **kw)
    if r.returncode != 0: raise SystemExit(f"step failed: {' '.join(map(str, cmd))}")


def fix_face(face_path, groove_src=None):
    """lin's face track: the mouth 4 frames (67 ms) early (reads as on time), eye highlights never hidden"""
    b = json.load(open(face_path)); n = len(b["names"]); w = b["w"]; cnt = len(w) // n; S = 4
    for c, nm in enumerate(b["names"]):
        if nm == "Fcl_EYE_Highlight_Hide":
            for f in range(cnt): w[f * n + c] = 0.0
        if "MTH" in nm:
            col = [w[f * n + c] for f in range(cnt)]; col = col[S:] + [col[-1]] * S
            for f in range(cnt): w[f * n + c] = col[f]
    json.dump(b, open(face_path, "w"), separators=(",", ":"))


def make(args):
    from lin_sing import lyrics as LY
    src = os.path.abspath(args.audio); dur = check(src)
    name = args.name or os.path.splitext(os.path.basename(src))[0]
    name = "".join(c if c.isalnum() or c in "-_" else "_" for c in name)[:60] or "song"
    F = os.path.join(ROOT, "songs", name); os.makedirs(F, exist_ok=True); J = lambda *p: os.path.join(F, *p)
    log(f"song folder: {F}")
    step("1 audio -> song.wav", J("song.wav"), lambda: run(["ffmpeg", "-v", "error", "-y", "-i", src, "-vn", "-ac", "2", "-ar", "44100", J("song.wav")]), args.force)
    voc, inst = J("sep", "htdemucs", "song", "vocals.wav"), J("sep", "htdemucs", "song", "no_vocals.wav")
    step("2 voice / band separation (Demucs)", voc, lambda: run(PY + ["-m", "demucs", "--two-stems", "vocals", "-n", "htdemucs", "-o", J("sep"), J("song.wav")]), args.force)

    def do_lines():
        regions = None
        rows = LY.parse_lyrics_file(args.lyrics) if args.lyrics else []
        timed = rows and all(r["t"] is not None for r in rows)
        lang = args.lang
        if timed and lang:
            lines = [dict(t=r["t"], end=(rows[i + 1]["t"] if i + 1 < len(rows) else dur), text=r["text"], trans=r.get("trans", "")) for i, r in enumerate(rows)]
        else:
            log("  listening to the voice (faster-whisper)...")
            words, heard, regions = LY.whisper_words(voc, lang=lang, model_name=args.asr_model, log=log); lang = lang or heard
            log(f"  language: {lang}, {len(words)} words heard")
            if timed: lines = [dict(t=r["t"], end=(rows[i + 1]["t"] if i + 1 < len(rows) else dur), text=r["text"], trans=r.get("trans", "")) for i, r in enumerate(rows)]
            elif rows:
                lines = LY.times_for_plain_lines(rows, words, lang); g = sum(1 for l in lines if l.get("guessed"))
                log(f"  {len(lines)} of {len(rows)} lyric lines placed" + (f" ({g} not heard clearly: placed between their neighbours)" if g else ""))
            else: lines = LY.lines_from_words(words)
        for i, l in enumerate(lines):                       # a line never runs into the next one
            if i + 1 < len(lines): l["end"] = min(float(l["end"]), float(lines[i + 1]["t"]))
            l["end"] = max(float(l["end"]), float(l["t"]) + 0.3)
        json.dump(dict(lang=lang, regions=regions, lines=lines), open(J("lines.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    step("3 lyric lines", J("lines.json"), do_lines, args.force)

    def do_align():
        d = json.load(open(J("lines.json"), encoding="utf-8"))
        res = LY.align(voc, d["lines"], d["lang"], log=log)        # (regions=d["regions"] aligns whole sung stretches at once: worse when the aligner is unsure)
        json.dump(res, open(J("morae.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=0)
        low = [l for l in res["lines"] if l[4] < 0.35]
        log(f"  {len(res['morae'])} syllables in {len(res['lines'])} lines" + (f"; {len(low)} lines aligned with low confidence (shouts, falsetto, wrong lyrics?)" if low else ""))
    step("4 syllable alignment (MMS_FA)", J("morae.json"), do_align, args.force)

    def do_structure():
        d = json.load(open(J("lines.json"), encoding="utf-8"))
        json.dump([dict(t=l["t"], end=l["end"]) for l in d["lines"]], open(J("lines_t.json"), "w"))
        run(PY + [os.path.join(HERE, "lin_sing", "song_structure.py"), J("song.wav"), inst, voc, J("lines_t.json"), J("structure.json")], stdout=subprocess.DEVNULL)
    step("5 music structure (tempo, bars, loudness)", J("structure.json"), do_structure, args.force)

    st = json.load(open(J("structure.json"))); json.dump(dict(beats=st["beats"]), open(J("beats.json"), "w"))     # (6: the face track is made with the body, in bake)

    st = json.load(open(J("structure.json"))); d = json.load(open(J("lines.json"), encoding="utf-8"))
    json.dump(dict(name=name, source=os.path.basename(src), duration=round(dur, 3), lang=d["lang"], bpm=round(st["bpm"], 2), lines=len(d["lines"])),
              open(J("song.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    from lin_sing import choreo
    step("7 choreography (choreo.json)", J("choreo.json"), lambda: json.dump(choreo.plan(F, mood=args.mood), open(J("choreo.json"), "w", encoding="utf-8"), indent=1, ensure_ascii=False), args.force)
    bake(argparse.Namespace(folder=F))
    log(f"\nready: {F}\n  Unity: open the project in unity/, menu LinSing / Load Song Folder... -> pick this folder -> LinSing / Play")
    if args.render: render(argparse.Namespace(folder=F))


def bake(args):
    from lin_sing import choreo, camera, subtitles
    F = os.path.abspath(args.folder)
    t = time.time(); log("[....] 6+7 face, body, eyes (face.json, key.bin, look.txt)"); choreo.bake(F, log=log); log(f"[done] ({time.time() - t:.0f} s)")
    log("[....] 8 camera (show.txt)"); camera.build(F, log=log); log("[done]")
    log("[....] 9 lyric captions (subs.ass, subs.json)"); subtitles.build(F, log=log); log("[done]")
    from lin_sing import qa as Q
    Q.report(F, Q.quick(F), log=log)


def find_unity():
    """Unity 6 for this project: LINSING_UNITY, then pipeline/config.json (written by doctor), then the editors Unity Hub knows,
    then Hub's default install folders. Returns (path or None, how it was found)."""
    want = open(os.path.join(ROOT, "unity", "ProjectSettings", "ProjectVersion.txt")).read().split("m_EditorVersion:")[1].split()[0]
    e = os.environ.get("LINSING_UNITY")
    if e and os.path.exists(e): return e, "LINSING_UNITY"
    cfg = os.path.join(HERE, "config.json")
    if os.path.exists(cfg):
        u = json.load(open(cfg, encoding="utf-8")).get("unity")
        if u and os.path.exists(u): return u, "pipeline/config.json"
    hub = []
    for base in (os.environ.get("APPDATA", ""), os.path.expanduser("~/Library/Application Support"), os.path.expanduser("~/.config")):
        p = os.path.join(base, "UnityHub", "editors-v2.json")
        if base and os.path.exists(p):
            try: hub += [(x.get("version", ""), loc) for x in json.load(open(p, encoding="utf-8")).get("data", []) for loc in x.get("location", [])]
            except Exception: pass
    hub += [(os.path.basename(os.path.dirname(os.path.dirname(c))), c) for c in glob.glob(r"C:\Program Files\Unity\Hub\Editor\*\Editor\Unity.exe")]
    hub += [(c.split("/Editor/")[1].split("/")[0], c) for c in glob.glob("/Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity")]
    hub = [(v, c) for v, c in hub if os.path.exists(c) and v.startswith("6000.")]
    exact = [c for v, c in hub if v == want]
    if exact: return exact[0], "Unity Hub"
    if hub: return sorted(hub)[-1][1], "Unity Hub (other 6000.x version)"
    return None, ""


def unity_exe():
    u, _ = find_unity()
    if u: return u
    raise SystemExit("Unity 6 not found: install it with Unity Hub, or set the environment variable LINSING_UNITY to Unity.exe (python linsing.py doctor)")


def load(args):
    F = os.path.abspath(args.folder); U = os.path.join(ROOT, "unity")
    run([unity_exe(), "-batchmode", "-quit", "-projectPath", U, "-executeMethod", "LinSingTools.BatchLoad", "-song", F, "-logFile", os.path.join(F, "unity_load.log")])
    log(open(os.path.join(F, "unity_load.log"), encoding="utf-8", errors="replace").read().split("[LinSing]")[-1].splitlines()[0][:300])


def unity_watch(cmd, logf, done_file, watch_dir, stall=300):
    """Run Unity and watch its log: a script that does not compile leaves the editor waiting in safe mode for ever - stop it and say why.
    Also stops it when nothing new has been written to watch_dir for `stall` seconds."""
    def kill(p): subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"] if os.name == "nt" else ["kill", "-9", str(p.pid)], capture_output=True)
    if os.path.exists(logf): os.remove(logf)
    p = subprocess.Popen(cmd); last, last_t = -1, time.time()
    while p.poll() is None:
        time.sleep(5)
        txt = open(logf, encoding="utf-8", errors="replace").read() if os.path.exists(logf) else ""
        errs = sorted({l.strip() for l in txt.splitlines() if "error CS" in l})
        if errs: kill(p); raise SystemExit("Unity scripts do not compile - fix these first:\n  " + "\n  ".join(errs[:8]))
        size = sum(os.path.getsize(os.path.join(watch_dir, f)) for f in os.listdir(watch_dir)) if os.path.isdir(watch_dir) else 0
        if size != last: last, last_t = size, time.time()
        elif size > 0 and not os.path.exists(done_file) and time.time() - last_t > stall: kill(p); raise SystemExit(f"Unity stopped writing ({size} bytes) - see {logf}")
        if os.path.exists(done_file):
            try: p.wait(timeout=90)
            except subprocess.TimeoutExpired: kill(p)
            break


CHUNK_S = 20.0          # the whole song is rendered in chunks of this length; after an edit only the chunks whose inputs changed are rendered again


def chunk_signatures(F, ranges, fps, height, preroll=1.5):
    """for each chunk (f0, f1): a hash of everything that decides its pictures - body and face frames (with the pre-roll), eyes, camera and
    light keys near it, the picture size, and the Unity scripts that draw it"""
    import hashlib, numpy as np
    J = lambda *p: os.path.join(F, *p)
    kb = np.fromfile(J("key.bin"), np.float32); kfps, kn, kch = int(kb[0]), int(kb[1]), int(kb[2]); kd = kb[4:4 + kn * kch].reshape(kn, kch)
    fc = json.load(open(J("face.json"))); fn = len(fc["names"]); fw = np.array(fc["w"], np.float32).reshape(-1, fn); ffps = fc["fps"]
    look = [l.split() for l in open(J("look.txt")) if l.startswith("look")]
    show = [l.split() for l in open(J("show.txt")) if l.strip() and not l.startswith("#")]
    beats = [float(x) for l in show if l[0] == "beat" for x in l[1:]]
    code = hashlib.sha1()
    for f in sorted(glob.glob(os.path.join(ROOT, "unity", "Assets", "MV", "Scripts", "*.cs"))): code.update(open(f, "rb").read())
    base = f"{fps}|{height}|{preroll}|{code.hexdigest()}"
    out = []
    for f0, f1 in ranges:
        a, b = max(0.0, f0 / fps - preroll), f1 / fps; h = hashlib.sha1(base.encode())
        h.update(kd[int(a * kfps):int(b * kfps) + 2].tobytes()); h.update(fw[int(a * ffps):int(b * ffps) + 2].tobytes())
        h.update(repr([l for l in look if a - 3 <= float(l[1]) <= b]).encode())
        h.update(repr([l for l in show if l[0] in ("cam2", "xfade") and a - 12 <= float(l[1]) <= b + 12]).encode())
        h.update(repr([l for l in show if l[0] in ("col", "tint", "lit") and float(l[1]) <= b]).encode())
        h.update(repr([x for x in beats if a - 1 <= x <= b]).encode())
        out.append(h.hexdigest()[:12])
    return out


def render(args):
    """Unity renders chunks straight into ffmpeg (GPU encoder when there is one); chunks whose inputs did not change are taken from the
    last render; then they are joined without re-encoding, and the sound and the captions are added.
    --from / --to: only that part, at 720p (a preview)."""
    F = os.path.abspath(args.folder); U = os.path.join(ROOT, "unity"); meta = json.load(open(os.path.join(F, "song.json"), encoding="utf-8"))
    fps = 30; dur = meta["duration"]; nfr = int(round(dur * fps))
    t0 = float(getattr(args, "t_from", None) or 0.0); t1 = min(float(getattr(args, "t_to", None) or dur), dur)
    preview = t0 > 0 or t1 < dur; h = getattr(args, "height", None) or (720 if preview else 1080)
    if preview: ranges = [(int(round(t0 * fps)), int(round(t1 * fps)))]
    else: n = int(CHUNK_S * fps); ranges = [(i, min(i + n, nfr)) for i in range(0, nfr, n)]
    CH = os.path.join(F, "chunks"); os.makedirs(CH, exist_ok=True)
    sigs = chunk_signatures(F, ranges, fps, h)
    path = lambda r, s: os.path.join(CH, f"c{h}_{r[0]:06d}_{r[1]:06d}_{s}.mp4")
    todo = [(r, s) for r, s in zip(ranges, sigs) if not os.path.exists(path(r, s)) or os.path.getsize(path(r, s)) < 1000]
    log(f"[....] {len(ranges)} chunk(s) of {(ranges[0][1] - ranges[0][0]) / fps:.0f} s at {h}p: {len(todo)} to render, {len(ranges) - len(todo)} unchanged")
    if todo:
        for r, s in todo:
            for old in glob.glob(os.path.join(CH, f"c{h}_{r[0]:06d}_{r[1]:06d}_*.mp4")): os.remove(old)
        spec = os.path.join(CH, "ranges.txt"); open(spec, "w").write("\n".join(f"{r[0]}|{r[1]}|{path(r, s)}" for r, s in todo))
        enc = "h264_nvenc" if "h264_nvenc" in subprocess.run(["ffmpeg", "-hide_banner", "-encoders"], capture_output=True, text=True).stdout else "libx264"
        done = os.path.join(F, "render_done.txt")
        if os.path.exists(done): os.remove(done)
        t = time.time(); log(f"      Unity renders {sum(r[1] - r[0] for r, _ in todo) / fps:.0f} s of picture ({enc}); the editor window opens and closes by itself")
        unity_watch([unity_exe(), "-projectPath", U, "-executeMethod", "LinSingTools.BatchRender", "-song", F, "-rangesFile", spec, "-height", str(h),
                     "-ffmpeg", shutil.which("ffmpeg") or "ffmpeg", "-encoder", enc, "-logFile", os.path.join(F, "unity_render.log")],
                    os.path.join(F, "unity_render.log"), done, CH)
        missing = [path(r, s) for r, s in todo if not os.path.exists(path(r, s)) or os.path.getsize(path(r, s)) < 1000]
        if missing: raise SystemExit(f"{len(missing)} chunk(s) were not written - see unity_render.log")
        el = time.time() - t; log(f"      rendered in {el:.0f} s ({sum(r[1] - r[0] for r, _ in todo) / max(1, el):.1f} frames/s including Unity start-up)")
    lst = os.path.join(CH, "concat.txt"); open(lst, "w").write("\n".join("file '%s'" % path(r, s).replace("\\", "/") for r, s in zip(ranges, sigs)))
    video = os.path.join(CH, "joined.mp4"); run(["ffmpeg", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", lst, "-c", "copy", video])
    out = os.path.join(F, meta["name"] + (f"_preview_{int(t0)}-{int(t1)}" if preview else "") + ".mp4")
    mux(F, video, out, (ranges[-1][1] - ranges[0][0]) / fps, subs=not getattr(args, "no_subs", False), t0=ranges[0][0] / fps)
    log(f"[done] {out}")


def mux(F, video, out, d, subs=True, t0=0.0):
    """the joined picture + sound (+ the lyric captions burnt in) -> mp4. ffmpeg runs inside the song folder, so the subtitle filter needs no
    drive letters. The picture of a part starts at 0; for the captions it is moved to its song time and back."""
    fonts = os.path.relpath(os.path.join(HERE, "fonts"), F).replace("\\", "/")
    vf = []
    if subs and os.path.exists(os.path.join(F, "subs.ass")): vf += [f"setpts=PTS+{t0:.3f}/TB", f"ass=subs.ass:fontsdir={fonts}", "setpts=PTS-STARTPTS"]
    vf.append(f"fade=t=out:st={max(0, d - 1.4):.2f}:d=1.4")
    nv = "h264_nvenc" in subprocess.run(["ffmpeg", "-hide_banner", "-encoders"], capture_output=True, text=True).stdout
    venc = ["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "19", "-b:v", "0"] if nv else ["-c:v", "libx264", "-crf", "18", "-preset", "medium"]
    run(["ffmpeg", "-v", "error", "-y", "-i", os.path.relpath(video, F), "-ss", f"{t0:.3f}", "-t", f"{d:.3f}", "-i", "song.wav",
         "-vf", ",".join(vf), "-af", f"afade=t=out:st={max(0, d - 1.4):.2f}:d=1.4",
         *venc, "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", out], cwd=F)


def clean_scene(args=None):
    """the stage scene remembers the last loaded song with full paths of this computer: blank them before committing"""
    import re
    p = os.path.join(ROOT, "unity", "Assets", "MV", "Scenes", "LinMicStage.unity"); s = open(p, encoding="utf-8").read(); n = 0
    for pat, rep in ((r"(?m)^  keyClipPath: .*$", "  keyClipPath: "), (r"(?m)^  showFile: .*$", "  showFile: "), (r"(?m)^  subsPath: .*$", "  subsPath: "),
                     (r"(?m)^  ranges: .*(?:\n {4,}.*)*$", "  ranges: "), (r"(?m)^  ffmpegPath: .*$", "  ffmpegPath: ffmpeg"),
                     (r"(?m)^  outDir: .*$", "  outDir: Capture/frames"), (r"(?m)^  lipJson: \{fileID: 4900000, guid: [0-9a-f]+, type: 3\}$", "  lipJson: {fileID: 0}")):
        s, k = re.subn(pat, rep, s); n += k
    open(p, "w", encoding="utf-8", newline="\n").write(s); log(f"scene cleaned ({n} fields)")


def qa(args):
    """quick checks always; --deep also lets Unity pose every frame (final joints + real-mesh penetration scan)"""
    from lin_sing import qa as Q
    F = os.path.abspath(args.folder); checks = Q.quick(F)
    if args.deep:
        log("[....] Unity scans every frame (no rendering; a few minutes for a whole song)")
        run([unity_exe(), "-batchmode", "-quit", "-projectPath", os.path.join(ROOT, "unity"), "-executeMethod", "LinSingTools.BatchQA", "-song", F,
             "-step", str(args.step), "-logFile", os.path.join(F, "unity_qa.log")])
        checks += Q.deep(F, args.step)
    sys.exit(0 if Q.report(F, checks, log=log) else 1)


def doctor(args):
    from lin_sing import doctor as D
    sys.exit(1 if D.run(log=log, fix=args.fix) else 0)


def main():
    ap = argparse.ArgumentParser(description="lin-sing: an agent-driven singing VRM character")
    sp = ap.add_subparsers(dest="cmd", required=True)
    m = sp.add_parser("make"); m.add_argument("audio"); m.add_argument("--lyrics"); m.add_argument("--lang", choices=["ja", "zh", "en"]); m.add_argument("--name")
    m.add_argument("--mood", choices=["neutral", "sad", "bright"], default="neutral", help="the faces she makes (editable later in choreo.json)")
    m.add_argument("--asr-model", default="large-v3"); m.add_argument("--render", action="store_true"); m.add_argument("--force", action="store_true")
    for c in ("bake", "load"): sp.add_parser(c).add_argument("folder")
    r = sp.add_parser("render"); r.add_argument("folder"); r.add_argument("--from", dest="t_from", type=float); r.add_argument("--to", dest="t_to", type=float)
    r.add_argument("--height", type=int, help="picture height (default 1080, a part: 720)"); r.add_argument("--no-subs", action="store_true", help="no lyric captions")
    sp.add_parser("check").add_argument("audio")
    sp.add_parser("clean-scene")
    q = sp.add_parser("qa"); q.add_argument("folder"); q.add_argument("--deep", action="store_true"); q.add_argument("--step", type=float, default=0.1)
    so = sp.add_parser("studio"); so.add_argument("folder"); so.add_argument("--port", type=int, default=8765); so.add_argument("--no-browser", action="store_true")
    dr = sp.add_parser("doctor"); dr.add_argument("--fix", action="store_true", help="also download the models")
    a = ap.parse_args()
    {"make": make, "bake": bake, "load": load, "render": render, "check": lambda a: check(a.audio), "clean-scene": clean_scene, "doctor": doctor, "qa": qa, "studio": lambda a: __import__("lin_sing.studio", fromlist=["serve"]).serve(a.folder, a.port, not a.no_browser)}[a.cmd](a)


if __name__ == "__main__":
    main()
