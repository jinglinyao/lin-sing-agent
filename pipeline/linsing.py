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


def unity_exe():
    e = os.environ.get("LINSING_UNITY")
    if e and os.path.exists(e): return e
    c = sorted(glob.glob(r"C:\Program Files\Unity\Hub\Editor\6000.*\Editor\Unity.exe")) + sorted(glob.glob("/Applications/Unity/Hub/Editor/6000.*/Unity.app/Contents/MacOS/Unity"))
    if c: return c[-1]
    raise SystemExit("Unity 6 not found: set the environment variable LINSING_UNITY to Unity.exe (Unity 6000.x)")


def load(args):
    F = os.path.abspath(args.folder); U = os.path.join(ROOT, "unity")
    run([unity_exe(), "-batchmode", "-quit", "-projectPath", U, "-executeMethod", "LinSingTools.BatchLoad", "-song", F, "-logFile", os.path.join(F, "unity_load.log")])
    log(open(os.path.join(F, "unity_load.log"), encoding="utf-8", errors="replace").read().split("[LinSing]")[-1].splitlines()[0][:300])


def unity_watch(cmd, logf, done_file, stall=300):
    """Run Unity and watch its log: a script that does not compile leaves the editor waiting in safe mode for ever - stop it and say why.
    Also stops when no frame has been written for `stall` seconds."""
    if os.path.exists(logf): os.remove(logf)
    p = subprocess.Popen(cmd); last_n, last_t = -1, time.time(); frames = os.path.join(os.path.dirname(done_file), "frames")
    while p.poll() is None:
        time.sleep(5)
        txt = open(logf, encoding="utf-8", errors="replace").read() if os.path.exists(logf) else ""
        errs = sorted({l.strip() for l in txt.splitlines() if "error CS" in l})
        if errs:
            subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"] if os.name == "nt" else ["kill", "-9", str(p.pid)], capture_output=True)
            raise SystemExit("Unity scripts do not compile - fix these first:\n  " + "\n  ".join(errs[:8]))
        n = len(os.listdir(frames)) if os.path.isdir(frames) else 0
        if n != last_n: last_n, last_t = n, time.time()
        elif n > 0 and not os.path.exists(done_file) and time.time() - last_t > stall:
            subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"] if os.name == "nt" else ["kill", "-9", str(p.pid)], capture_output=True)
            raise SystemExit(f"Unity stopped writing frames ({n} written) - see {logf}")
        if os.path.exists(done_file):
            try: p.wait(timeout=90)
            except subprocess.TimeoutExpired: subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"] if os.name == "nt" else ["kill", "-9", str(p.pid)], capture_output=True)
            break


def render(args):
    """the whole song, or a preview of a part (--from / --to, smaller picture): Unity writes frames, ffmpeg adds the sound and the captions"""
    F = os.path.abspath(args.folder); U = os.path.join(ROOT, "unity"); meta = json.load(open(os.path.join(F, "song.json"), encoding="utf-8"))
    t0 = float(getattr(args, "t_from", None) or 0.0); t1 = float(getattr(args, "t_to", None) or meta["duration"]); t1 = min(t1, meta["duration"])
    preview = t0 > 0 or t1 < meta["duration"]; h = getattr(args, "height", None) or (720 if preview else 1080)
    if os.path.exists(os.path.join(F, "render_done.txt")): os.remove(os.path.join(F, "render_done.txt"))
    log(f"[....] Unity renders {t0:.1f} - {t1:.1f} s at {h}p (the editor window opens and closes by itself)")
    unity_watch([unity_exe(), "-projectPath", U, "-executeMethod", "LinSingTools.BatchRender", "-song", F, "-from", f"{t0:.3f}", "-to", f"{t1:.3f}", "-height", str(h),
                 "-logFile", os.path.join(F, "unity_render.log")], os.path.join(F, "unity_render.log"), os.path.join(F, "render_done.txt"))
    if not glob.glob(os.path.join(F, "frames", "f_*.jpg")): raise SystemExit("no frames were written - see unity_render.log")
    out = os.path.join(F, meta["name"] + (f"_preview_{int(t0)}-{int(t1)}" if preview else "") + ".mp4")
    mux(F, out, t1 - t0, subs=not getattr(args, "no_subs", False), t0=t0)
    log(f"[done] {out}")


def mux(F, out, d, subs=True, t0=0.0):
    """frames + sound (+ the lyric captions burnt in) -> mp4. ffmpeg runs inside the song folder, so the subtitle filter needs no drive letters.
    The frames of a part start at 0; for the captions they are moved to their song time and back."""
    fonts = os.path.relpath(os.path.join(HERE, "fonts"), F).replace("\\", "/")
    vf = []
    if subs and os.path.exists(os.path.join(F, "subs.ass")): vf += [f"setpts=PTS+{t0:.3f}/TB", f"ass=subs.ass:fontsdir={fonts}", "setpts=PTS-STARTPTS"]
    vf.append(f"fade=t=out:st={max(0, d - 1.4):.2f}:d=1.4")
    run(["ffmpeg", "-v", "error", "-y", "-framerate", "30", "-i", "frames/f_%05d.jpg", "-ss", f"{t0:.3f}", "-t", f"{d:.3f}", "-i", "song.wav",
         "-vf", ",".join(vf), "-af", f"afade=t=out:st={max(0, d - 1.4):.2f}:d=1.4",
         "-c:v", "libx264", "-crf", "18", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", out], cwd=F)


def clean_scene(args=None):
    """the stage scene remembers the last loaded song with full paths of this computer: blank them before committing"""
    import re
    p = os.path.join(ROOT, "unity", "Assets", "MV", "Scenes", "LinMicStage.unity"); s = open(p, encoding="utf-8").read(); n = 0
    for pat, rep in ((r"(?m)^  keyClipPath: .*$", "  keyClipPath: "), (r"(?m)^  showFile: .*$", "  showFile: "), (r"(?m)^  subsPath: .*$", "  subsPath: "),
                     (r"(?m)^  outDir: .*$", "  outDir: Capture/frames"), (r"(?m)^  lipJson: \{fileID: 4900000, guid: [0-9a-f]+, type: 3\}$", "  lipJson: {fileID: 0}")):
        s, k = re.subn(pat, rep, s); n += k
    open(p, "w", encoding="utf-8", newline="\n").write(s); log(f"scene cleaned ({n} fields)")


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
    a = ap.parse_args()
    {"make": make, "bake": bake, "load": load, "render": render, "check": lambda a: check(a.audio), "clean-scene": clean_scene}[a.cmd](a)


if __name__ == "__main__":
    main()
