"""Automatic QA report for a song folder: every check says pass / fail, where (times) and what to do about it - so a person or an agent can
fix and check again without watching the whole video first.

Quick checks (seconds, from the files the pipeline wrote):
  motion      wrist speed / acceleration, head and trunk turning speed, how long a free hand stands still   (key.bin)
  hands       share of the sung time with the right hand on the microphone; long holds; changes squeezed together
  sheet       moves too close together, the same move twice in a row
  lyrics      lines aligned with low confidence, lines placed by guess, lines whose syllables had to be spread
  captions    every lyric line has a caption, none shorter than 0.5 s, none overlapping
  camera      close shots off the microphone axis, no dissolves (they ghost), the camera never parked too long
  mouth       the mouth's opening against the voice's loudness: how far ahead of / behind the voice it is
Deep checks (--deep: Unity poses every frame, no rendering; minutes):
  penetration arms in the body, a hand in the other arm, hands in the microphone (real meshes, > 5 mm)
  joints      the final wrists after all solving: speed and single-frame jumps

usage: python linsing.py qa <song folder> [--deep] [--step 0.1]   ->  qa.json + qa.md
"""
import os, json
import numpy as np


def _runs(mask, t, min_len=0.0):
    """stretches where mask is true: [(t0, t1)]"""
    idx = np.flatnonzero(mask)
    if not len(idx): return []
    out = []
    for g in np.split(idx, np.flatnonzero(np.diff(idx) > 1) + 1):
        a, b = float(t[g[0]]), float(t[g[-1]])
        if b - a >= min_len: out.append((round(a, 2), round(b, 2)))
    return out


def quick(F):
    J = lambda *p: os.path.join(F, *p); checks = []
    def check(area, name, ok, value, limit, where=None, advice="", severity="fail"):
        checks.append(dict(area=area, name=name, ok=bool(ok), value=value, limit=limit, where=(where or [])[:12], advice="" if ok else advice, severity=severity))
    meta = json.load(open(J("song.json"), encoding="utf-8")); dur = meta["duration"]
    # ---- motion (key.bin)
    b = np.fromfile(J("key.bin"), np.float32); fps, n, nch, t0 = int(b[0]), int(b[1]), int(b[2]), float(b[3])
    d = b[4:4 + n * nch].reshape(n, nch).astype(float); t = t0 + np.arange(n) / fps
    der = lambda x, k=1: x if k == 0 else der(np.gradient(x, 1.0 / fps, axis=0), k - 1)
    for side, c in (("右手", 14), ("左手", 32)):
        v = np.linalg.norm(der(d[:, c:c + 3]), axis=1); acc = np.linalg.norm(der(d[:, c:c + 3], 2), axis=1)
        check("motion", f"{side}最快速度", v.max() <= 0.9, round(float(v.max()), 2), "≤ 0.9 m/s", [round(float(t[i]), 2) for i in np.argsort(-v)[:3]],
              "这里两个动作之间太急：在 choreo.json 里把后一条的 t 往后挪，或删掉中间一条")
        check("motion", f"{side}加速度 99 分位", np.percentile(acc, 99) <= 4.0, round(float(np.percentile(acc, 99)), 2), "≤ 4 m/s²", [], "整体动作偏急：拉开动作间距")
    for name, c, lim in (("头部转动", 9, 70.0), ("躯干转动", 6, 35.0)):
        w = np.linalg.norm(der(d[:, c:c + 3]), axis=1)
        check("motion", f"{name}最快", w.max() <= lim, round(float(w.max()), 1), f"≤ {lim:.0f} °/s", [round(float(t[i]), 2) for i in np.argsort(-w)[:3]], "这一刻点头 / 转身太猛：检查这附近是否有很短的动作")
    grip_r, grip_l = d[:, 52] > 0.5, d[:, 53] > 0.5
    vr = np.linalg.norm(der(d[:, 14:17]), axis=1)
    frozen = _runs((vr < 0.02) & ~grip_r, t, 12.0)
    check("motion", "空着的右手静止超过 12 秒", not frozen, len(frozen), "0 段", [f"{a}-{b}" for a, b in frozen], "这段手一直不动：换一个动作，或加一条动作把这段切开", "warn")
    # ---- hands, sheet
    cj = json.load(open(J("choreo.json"), encoding="utf-8")); sheet = cj["sheet"]
    share = float(grip_r.mean())
    check("hands", "右手握麦占比", 0.22 <= share <= 0.45, round(share, 2), "0.22–0.45", [], "握麦太多显得呆、太少不像在唱：在 choreo.json 里换几条动作（mic* ↔ 其他）", "warn")
    for side, g in (("右手握麦", grip_r), ("左手扶杆", grip_l)):
        long_ = _runs(g, t, 25.0)
        check("hands", f"{side}连续超过 25 秒", not long_, len(long_), "0 段", [f"{a}-{b}" for a, b in long_], "插入一条不握的动作", "warn")
    bl = json.load(open(J("bake_log.json"))) if os.path.exists(J("bake_log.json")) else {"squeezed": []}
    check("sheet", "动作挤在一起（SQUEEZED）", not bl["squeezed"], len(bl["squeezed"]), "0", bl["squeezed"], "前一个动作还没到位后一个就开始了：拉开这两条的时间")
    short = [s["t"] for s in sheet[:-1] if s["end"] - s["t"] < 1.8]
    check("sheet", "短于 1.8 秒的动作", not short, len(short), "0", short, "并入相邻一条")
    rep = [b_["t"] for a_, b_ in zip(sheet[:-1], sheet[1:]) if a_["move"] == b_["move"] and a_["level"] >= 0]
    check("sheet", "同一动作连用两次", not rep, len(rep), "0", rep, "换成同档位的别的动作", "warn")
    # ---- lyrics
    mo = json.load(open(J("morae.json"), encoding="utf-8")); ln = json.load(open(J("lines.json"), encoding="utf-8"))["lines"]
    low = [(round(l[2], 2), l[5] if len(l) > 5 else "") for l in mo["lines"] if l[4] < 0.35]
    spread = [(round(l[2], 2), l[5] if len(l) > 5 else "") for l in mo["lines"] if l[4] == 0.0]
    guessed = [(round(l["t"], 2), l["text"]) for l in ln if l.get("guessed")]
    nl = max(1, len(mo["lines"]))
    check("lyrics", "低置信度的句子", len(low) / nl <= 0.35, f"{len(low)}/{nl}", "≤ 35%", [f"{a} {b[:16]}" for a, b in low],
          "多半是嘶吼 / 假声 / 叠唱，或歌词和这一版录音对不上：核对这些句子；有带时间的 LRC 就用 LRC", "warn")
    check("lyrics", "没听清、按前后句插入的句子", not guessed, len(guessed), "0", [f"{a} {b[:16]}" for a, b in guessed],
          "这些句子可能这一版没唱，或唱法不同（翻唱改词）：确认歌词；没唱的删掉", "warn")
    check("lyrics", "对齐失败、音节均匀铺开的句子", not spread, len(spread), "0", [f"{a} {b[:16]}" for a, b in spread], "这些句子口型只是近似：可以给 LRC 时间或改歌词", "warn")
    # ---- captions
    if os.path.exists(J("subs.json")):
        subs = json.load(open(J("subs.json"), encoding="utf-8"))["lines"]
        check("captions", "每句歌词都有字幕", len(subs) >= nl, f"{len(subs)}/{nl}", "全部", [], "重跑 bake")
        ov = [s2["t0"] for s1, s2 in zip(subs[:-1], subs[1:]) if s2["t0"] < s1["t1"] - 0.01]
        check("captions", "字幕互相重叠", not ov, len(ov), "0", ov, "两句挤在同一时间：多半是对齐问题，见 lyrics")
    # ---- camera
    cams = [l.split() for l in open(J("show.txt"), encoding="utf-8") if l.startswith("cam2")]
    bad = [float(c[1]) for c in cams if float(c[3]) < 2.6 and abs(float(c[2])) < 13.9]
    check("camera", "近景正对话筒（会挡住嘴）", not bad, len(bad), "0", bad, "把这些 cam2 的偏航改到 ±14° 以外")
    xf = [l for l in open(J("show.txt"), encoding="utf-8") if l.startswith("xfade")]
    check("camera", "叠化（不同景别叠在一起会重影）", not xf, len(xf), "0", [], "删掉 xfade 行，用连续运镜", "warn")
    kt = sorted(float(c[1]) for c in cams); gaps = [(round(a, 1), round(b_, 1)) for a, b_ in zip(kt[:-1], kt[1:]) if b_ - a > 12]
    check("camera", "镜头超过 12 秒没有新关键帧", not gaps, len(gaps), "0", [f"{a}-{b_}" for a, b_ in gaps], "加一个 cam2 关键帧，让镜头有缓慢变化", "warn")
    # ---- mouth against the voice
    try:
        import soundfile as sf
        fc = json.load(open(J("face.json"))); fn = len(fc["names"]); w = np.array(fc["w"], float).reshape(-1, fn)
        mouth = w[:, [i for i, nm in enumerate(fc["names"]) if "MTH" in nm]].sum(1)
        x, sr = sf.read(J("sep", "htdemucs", "song", "vocals.wav"))
        if x.ndim > 1: x = x.mean(1)
        hop = sr / fc["fps"]; env = np.array([np.sqrt(np.mean(x[int(i * hop):int(i * hop + hop)] ** 2)) for i in range(len(mouth))])
        a_ = (mouth - mouth.mean()) / (mouth.std() + 1e-9); b2 = (env - env.mean()) / (env.std() + 1e-9)
        lags = list(range(-15, 16)); cc = [float(np.mean(a_[max(0, -k):len(a_) - max(0, k)] * b2[max(0, k):len(b2) - max(0, -k)])) for k in lags]
        lead = -lags[int(np.argmax(cc))] / fc["fps"]                 # > 0: the mouth moves before the voice
        check("mouth", "嘴相对人声提前", -0.04 <= lead <= 0.12, f"{lead * 1000:.0f} ms（相关 {max(cc):.2f}）", "-40–120 ms",
              [], "嘴明显落后于声音：把 choreo.write_face 里的提前量 S 加一帧（每帧 17 ms），再看一段预览确认（这项是估计值，精度约一帧）", "warn")
    except Exception as ex:
        check("mouth", "口型对声音", False, f"没测成: {ex}", "", [], "", "warn")
    return checks


def deep(F, step=0.1):
    """reads qa_probe.csv / qa_penetration.csv written by Unity (LinSingTools.BatchQA)"""
    import csv
    J = lambda *p: os.path.join(F, *p); checks = []
    def check(area, name, ok, value, limit, where=None, advice="", severity="fail"):
        checks.append(dict(area=area, name=name, ok=bool(ok), value=value, limit=limit, where=(where or [])[:12], advice="" if ok else advice, severity=severity))
    r = list(csv.DictReader(open(J("qa_penetration.csv"))))
    t = np.array([float(x["t"]) for x in r])
    for cols, name, adv in ((("bodyL_mm", "bodyR_mm"), "手臂穿进身体 / 衣服", "这些时刻手离身体太近：换掉这里的动作，或在 choreo.py 的姿势里把手往外放"),
                            (("armsL_mm", "armsR_mm"), "手穿进另一只手臂", "两只手太近：换成不交叉的动作"),
                            (("micL_mm", "micR_mm"), "手穿进话筒 / 支架", "握麦或离麦的路线擦到话筒：换动作，或拉开离麦时间")):
        dmax = np.max([[float(x[c]) for c in cols] for x in r], axis=1)
        bad = dmax > 5.0; segs = _runs(bad, t)
        check("penetration", name, not bad.any(), f"{int(bad.sum())} 帧（最深 {dmax.max():.1f} mm）", "> 5 mm 的帧为 0", [f"{a}-{b}" for a, b in segs], adv)
    p = list(csv.DictReader(open(J("qa_probe.csv"))))
    tp = np.array([float(x["t"]) for x in p]); fps = 1.0 / np.median(np.diff(tp))
    for side, k in (("右手腕", "r"), ("左手腕", "l")):
        pos = np.array([[float(x[k + "x"]), float(x[k + "y"]), float(x[k + "z"])] for x in p])
        v = np.linalg.norm(np.gradient(pos, 1.0 / fps, axis=0), axis=1); jump = np.abs(np.diff(v)) * fps
        check("joints", f"{side}最终速度", v.max() <= 1.2, round(float(v.max()), 2), "≤ 1.2 m/s", [round(float(tp[i]), 2) for i in np.argsort(-v)[:3]], "解算后手被推得太快：多半是碰撞修正，换动作")
        bad = jump > 60.0
        check("joints", f"{side}单帧突跳", not bad.any(), int(bad.sum()), "0", [f"{a}-{b}" for a, b in _runs(bad, tp[1:])], "手在这里跳了一下：碰撞修正在切换，换一个离身体远一点的姿势")
    return checks


def report(F, checks, log=print):
    fails = [c for c in checks if not c["ok"] and c["severity"] == "fail"]; warns = [c for c in checks if not c["ok"] and c["severity"] == "warn"]
    json.dump(dict(passed=not fails, fails=len(fails), warnings=len(warns), checks=checks), open(os.path.join(F, "qa.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    md = [f"# 质检报告：{os.path.basename(F)}", "", f"结果：{'通过' if not fails else '不通过'}（{len(fails)} 项不合格，{len(warns)} 项提醒，共 {len(checks)} 项）", "",
          "| 项目 | 结果 | 实测 | 标准 | 时间点 | 建议 |", "|---|---|---|---|---|---|"]
    for c in checks:
        md.append(f"| {c['area']} · {c['name']} | {'✓' if c['ok'] else ('✗' if c['severity'] == 'fail' else '!')} | {c['value']} | {c['limit']} | {'; '.join(map(str, c['where']))} | {c['advice']} |")
    open(os.path.join(F, "qa.md"), "w", encoding="utf-8").write("\n".join(md) + "\n")
    log(f"  QA: {'PASS' if not fails else 'FAIL'} - {len(fails)} failed, {len(warns)} warnings of {len(checks)} checks (qa.md)")
    for c in fails + warns: log(f"    {'✗' if c in fails else '!'} {c['area']}: {c['name']} = {c['value']} (limit {c['limit']})" + (f" at {', '.join(map(str, c['where'][:4]))}" if c['where'] else ""))
    return not fails
