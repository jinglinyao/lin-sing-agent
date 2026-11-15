"""Automatic choreography for lin at the stand microphone: song analysis -> a dope sheet -> key.bin (body, 60 fps) + look.txt (eyes).

How it decides (the rules a human animator used for the hand-keyed songs, written down):
  phrases    sung lines that follow each other closely form one phrase; one movement idea per phrase, never per syllable
  intensity  how loud the band is under the phrase, against the rest of the song: quiet / middle / full
             quiet  -> close to the microphone: right hand holds it, left on the stand or on the chest, eyes on the microphone
             middle -> one hand tells the line (explain, sweep, offer, point, press, shrug...), the other stays on the stand / microphone
             full   -> both hands open (wide, reach, fist on the chest, offer with both hands), chest up, eyes to the audience
  variety    no movement twice in a row; recent ones are avoided; the right hand is on the microphone about a third of the sung time
  timing     a pose arrives on the first syllable of its phrase; grips close / open around the arrival; the body breathes in before every
             line, shifts its weight on the bar lines and gives at the knees on the beats (more when the band is loud)
  between    intro / interludes: hands at rest, the eyes go far or to the lens, the body sways with the bar; back to the microphone
             one bar before the voice returns
The result is plain data (a list of moves with times, choreo.json): an agent or a person can edit it and run `linsing.py bake` again.

usage: python -m lin_sing.choreo <song folder>            (reads structure.json, morae.json; writes choreo.json, key.bin, look.txt)
       python -m lin_sing.choreo <song folder> --bake     (only bake an edited choreo.json)
"""
import os, sys, json, random
import numpy as np
from .keyanim import Anim, mirror, unit

V = lambda *a: np.array(a, float)
HERE = os.path.dirname(os.path.abspath(__file__))

# ------------------------------------------------------------------ lin's measurements (Unity: LinSing / Measure Rig -> data/lin_rig.json)
def load_rig():
    rig = json.loads("\n".join(l.split("//")[0] for l in open(os.path.join(HERE, "data", "lin_rig.json"), encoding="utf-8").read().splitlines()))
    return {k: np.array(rig[k]) for k in ("gripR_wrist", "gripR_finger", "gripR_palm", "poleL_wrist", "poleL_finger", "poleL_palm")}


def pose_library(rig):
    """right-hand poses in lin's body space (metres; x to her left is negative); the left hand uses mirror(pose)"""
    GW, GF, GP = rig["gripR_wrist"], rig["gripR_finger"], rig["gripR_palm"]; PW, PF, PP = rig["poleL_wrist"], rig["poleL_finger"], rig["poleL_palm"]
    OPEN = V(0.03, 0.05, 0.07, 0.09, 0.12); FIST = V(0.70, 1.0, 1.0, 1.0, 1.0); LOOSE = V(0.20, 0.30, 0.40, 0.48, 0.55)
    P = {}
    P["IDLE"] = dict(pos=V(0.142, 1.070, 0.228), fin=unit(V(-0.30, -0.65, 0.70)), palm=unit(V(-0.55, -0.35, -0.76)), pole=unit(V(0.55, -0.75, -0.35)), curl=LOOSE, spread=0.0, chest=1)
    P["EXPLAIN"] = dict(pos=V(0.240, 1.205, 0.265), fin=unit(V(0.74, 0.12, 0.66)), palm=unit(V(-0.14, 0.78, 0.61)), pole=unit(V(0.50, -0.84, -0.20)), curl=V(0.05, 0.06, 0.10, 0.14, 0.20), spread=0.35, chest=1)
    P["SWEEP"] = dict(pos=V(0.250, 1.140, 0.245), fin=unit(V(0.50, -0.08, 0.86)), palm=unit(V(0.05, -0.97, 0.22)), pole=unit(V(0.50, -0.85, -0.20)), curl=V(0.02, 0.03, 0.05, 0.07, 0.10), spread=0.45, chest=1)
    P["MIC"] = dict(pos=GW, fin=GF, palm=GP, pole=unit(V(0.80, -0.95, 0.0)), curl=V(-0.13, 0.10, 0.10, 0.10, 0.10), spread=0.10, chest=0)
    P["POLE"] = dict(pos=PW * V(-1, 1, 1), fin=PF * V(-1, 1, 1), palm=PP * V(-1, 1, 1), pole=unit(V(0.87, -0.50, 0.01)), curl=V(0.0, 0.10, 0.10, 0.10, 0.10), spread=0.10, chest=0)
    P["POINT"] = dict(pos=V(0.235, 1.330, 0.300), fin=unit(V(0.36, 0.06, 0.93)), palm=unit(V(-0.05, -0.99, 0.10)), pole=unit(V(0.70, -0.68, -0.20)), curl=V(0.50, 0.00, 0.95, 1.00, 1.00), spread=0.0, chest=1)
    P["PRESS"] = dict(pos=V(0.150, 1.180, 0.320), fin=unit(V(0.15, 0.02, 0.99)), palm=unit(V(0.0, -1.0, 0.02)), pole=unit(V(0.65, -0.72, -0.20)), curl=V(0.02, 0.04, 0.06, 0.08, 0.10), spread=0.25, chest=1)
    P["SHRUG"] = dict(pos=V(0.305, 1.225, 0.190), fin=unit(V(0.72, 0.10, 0.68)), palm=unit(V(-0.14, 0.82, 0.55)), pole=unit(V(0.42, -0.90, -0.12)), curl=V(0.06, 0.10, 0.14, 0.18, 0.24), spread=0.30, chest=1)
    P["OPENDOWN"] = dict(pos=V(0.240, 1.075, 0.215), fin=unit(V(0.30, -0.12, 0.95)), palm=unit(V(-0.05, -0.99, -0.12)), pole=unit(V(0.65, -0.70, -0.25)), curl=V(0.03, 0.04, 0.06, 0.08, 0.10), spread=0.50, chest=1)
    P["REACHLOW"] = dict(pos=V(0.135, 1.130, 0.370), fin=unit(V(-0.10, -0.28, 0.95)), palm=unit(V(0.0, -0.95, -0.30)), pole=unit(V(0.60, -0.75, -0.20)), curl=OPEN, spread=0.40, chest=1)
    P["CHEST"] = dict(pos=V(0.138, 1.268, 0.238), fin=unit(V(-0.72, 0.30, -0.02)), palm=unit(V(-0.08, -0.12, -0.99)), pole=unit(V(0.55, -0.80, -0.20)), curl=V(0.10, 0.12, 0.14, 0.16, 0.20), spread=0.10, chest=1)
    P["CHESTFIST"] = dict(P["CHEST"], pos=V(0.128, 1.266, 0.246), curl=FIST, spread=0.0)
    P["LOWOPEN"] = dict(P["IDLE"], curl=V(0.08, 0.12, 0.16, 0.20, 0.26), spread=0.15)
    P["WIDE"] = dict(pos=V(0.360, 1.195, 0.150), fin=unit(V(0.80, 0.05, 0.60)), palm=unit(V(-0.20, 0.78, 0.59)), pole=unit(V(0.35, -0.92, -0.15)), curl=OPEN, spread=0.50, chest=1)
    P["REACH2"] = dict(pos=V(0.195, 1.330, 0.355), fin=unit(V(0.15, 0.35, 0.92)), palm=unit(V(-0.25, 0.88, -0.35)), pole=unit(V(0.50, -0.85, -0.10)), curl=OPEN, spread=0.40, chest=1)
    P["OFFER"] = dict(pos=V(0.165, 1.160, 0.335), fin=unit(V(0.12, 0.05, 0.99)), palm=unit(V(-0.10, 0.97, -0.12)), pole=unit(V(0.60, -0.78, -0.15)), curl=V(0.05, 0.07, 0.10, 0.13, 0.17), spread=0.30, chest=1)
    P["LOOKHAND"] = dict(pos=V(0.135, 1.205, 0.300), fin=unit(V(-0.20, 0.10, 0.97)), palm=unit(V(-0.15, 0.97, -0.10)), pole=unit(V(0.60, -0.78, -0.20)), curl=V(0.12, 0.20, 0.25, 0.30, 0.35), spread=0.10, chest=1)
    return P, GW


# ------------------------------------------------------------------ the move library: what each hand does, the body, the eyes
#   R / L: pose names (L is mirrored) or "MIC" / "POLE"; torso, head: (pitch forward, yaw to her right, roll) degrees; shrug degrees
#   look: 0 microphone, 1 lens, 2 audience left, 3 audience right, 4 far, 5 down / into the hand;  beats: small re-strokes inside the phrase
MOVES = {
    # quiet: close to the microphone
    "mic":        dict(level=0, R="MIC", L="POLE", torso=(1.5, 0, 0), head=(13, 0, 0), look=0),
    "mic_chest":  dict(level=0, R="MIC", L="CHEST", torso=(2.0, -1, 0.5), head=(14, -2, 2), look=0),
    "mic_hand":   dict(level=0, R="MIC", L="LOOKHAND", torso=(2.6, -3, 1), head=(16, -8, 3), look=5),
    "mic_bow":    dict(level=0, R="MIC", L="POLE", torso=(3.8, 0, 0.6), head=(17.5, 0, 3), look=0),
    # middle: one hand tells it
    "explain":    dict(level=1, R="EXPLAIN", L="POLE", torso=(-0.5, 3.5, 0.5), head=(1.5, -3.5, 1.5), look=1, beats=True),
    "sweep":      dict(level=1, R="SWEEP", L="POLE", torso=(0.5, 6.0, -1.0), head=(4, 6, -2.5), look=3),
    "offer":      dict(level=1, R="OFFER", L="POLE", torso=(2.0, 0, 0), head=(6, 0, 0), look=1),
    "point":      dict(level=1, R="MIC", L="POINT", torso=(1.5, -5.5, 1.0), head=(3, -13, -2), look=2),
    "press":      dict(level=1, R="MIC", L="PRESS", torso=(2.4, -2.0, 0.8), head=(13.5, -2.5, -1), look=1),
    "shrug":      dict(level=1, R="SHRUG", L="POLE", torso=(-1.2, -1.0, 2.0), head=(5, -2, 6), look=1, shrug=8.0),
    "reachlow":   dict(level=1, R="REACHLOW", L="CHEST", torso=(4.2, 2.5, -1.2), head=(14, 5, -2.5), look=5),
    "open_down":  dict(level=1, R="MIC", L="OPENDOWN", torso=(2.5, -3.5, 2.0), head=(13, -8, 3), look=1),
    "fists":      dict(level=1, R="CHESTFIST", L="CHESTFIST", torso=(5.2, -1.0, 1.0), head=(20, -2, 4), look=5, shrug=7.0, beats=True),
    # full: both hands open
    "wide":       dict(level=2, R="WIDE", L="WIDE", torso=(-3.2, 0, 0), head=(-6, 0, 0), look=4, shrug=2.0),
    "reach":      dict(level=2, R="REACH2", L="REACH2", torso=(-2.6, -1.0, 0), head=(-7, -2, 0), look=1),
    "fist_open":  dict(level=2, R="CHESTFIST", L="WIDE", torso=(2.6, 3.0, -1.0), head=(4, 3, -2), look=4, beats=True),
    "offer2":     dict(level=2, R="OFFER", L="OFFER", torso=(3.2, 0, 0), head=(6, 0, 0), look=1),
    "mic_wide":   dict(level=2, R="MIC", L="WIDE", torso=(-1.0, -2.0, 0.5), head=(9, -3, 1), look=0),
    # no voice
    "rest":       dict(level=-1, R="IDLE", L="POLE", torso=(1.0, 0, 0), head=(9, 0, 0), look=4),
    "rest_open":  dict(level=-1, R="LOWOPEN", L="POLE", torso=(0.5, 2.0, 0), head=(6, 4, 0), look=1),
}
MIC_VIA_OFF = V(0.225, 1.275, 0.205)          # the right hand leaves the microphone back along its own fingers first, to her side
POLE_VIA = V(-0.170, 1.140, 0.215)


def analyse(folder):
    st = json.load(open(os.path.join(folder, "structure.json")))
    mo = json.load(open(os.path.join(folder, "morae.json"), encoding="utf-8"))
    meta = json.load(open(os.path.join(folder, "song.json"), encoding="utf-8"))
    lines = sorted([(float(l[2]), float(l[3])) for l in mo["lines"]])
    bars = st["bars"]; tot = np.array([b["total"] for b in bars]); bt = np.array([b["t"] for b in bars])
    lo, hi = np.percentile(tot, 40), np.percentile(tot, 72)
    def energy(a, b):
        m = (bt + 0.01 >= a - 0.5) & (bt < b); return float(tot[m].mean()) if m.any() else float(tot[np.clip(np.searchsorted(bt, a) - 1, 0, len(tot) - 1)])
    # phrases: lines that start within 1.6 s of the last one's start join it; a phrase is at least 1.6 s
    phrases = []
    for a, b in lines:
        if phrases and (a - phrases[-1][0] < 1.6 or (a - phrases[-1][1] < 0.6 and b - phrases[-1][0] < 3.2)): phrases[-1][1] = max(phrases[-1][1], b)
        else: phrases.append([a, b])
    out = []
    for a, b in phrases:
        e = energy(a, b); out.append(dict(t=round(a, 3), end=round(b, 3), energy=round(e, 3), level=0 if e < lo else 2 if e > hi else 1))
    return st, out, meta["duration"]


def plan(folder, seed=7):
    """the dope sheet as data: [{t, end, move, level}] for the sung phrases and the gaps"""
    st, phrases, dur = analyse(folder); rnd = random.Random(seed)
    sheet = []; recent = []; mic_time = 0.0; sung = 0.0
    first = phrases[0]["t"] if phrases else dur
    sheet.append(dict(t=0.0, end=round(max(0.0, first - 0.05), 3), move="rest", level=-1))
    for i, p in enumerate(phrases):
        nxt = phrases[i + 1]["t"] if i + 1 < len(phrases) else dur
        if i and p["t"] - phrases[i - 1]["end"] > 6.0:                    # a long gap without voice: an interlude
            g0 = phrases[i - 1]["end"] + 0.4; sheet.append(dict(t=round(g0, 3), end=round(p["t"] - 0.05, 3), move=rnd.choice(["rest", "rest_open"]), level=-1))
        lev = p["level"]; cands = [k for k, m in MOVES.items() if m["level"] == lev and k not in recent[-3:]]
        if not cands: cands = [k for k, m in MOVES.items() if m["level"] == lev and k != (recent[-1] if recent else None)]
        w = []
        for k in cands:
            on_mic = MOVES[k]["R"] == "MIC"; share = mic_time / max(1.0, sung)
            w.append((2.2 if on_mic and share < 0.30 else 0.6 if on_mic and share > 0.45 else 1.0) * (0.5 if k in recent else 1.0))
        k = rnd.choices(cands, weights=w)[0]
        if i == 0 and lev <= 1: k = "mic"                                     # the voice comes in at the microphone
        sheet.append(dict(t=p["t"], end=round(min(nxt, p["end"] + 1.5), 3), move=k, level=lev, energy=p["energy"]))
        recent.append(k); sung += p["end"] - p["t"]; mic_time += (p["end"] - p["t"]) if MOVES[k]["R"] == "MIC" else 0.0
    if phrases: sheet.append(dict(t=round(phrases[-1]["end"] + 0.6, 3), end=round(dur, 3), move="mic_bow" if dur - phrases[-1]["end"] < 8 else "rest", level=-1))
    return dict(note="automatic choreography (lin_sing.choreo); edit 'move' or 't' and run: python linsing.py bake <folder>", moves=list(MOVES), sheet=sheet)


def bake(folder, log=print):
    """choreo.json -> key.bin + look.txt"""
    rig = load_rig(); P, GW = pose_library(rig); M = mirror
    st = json.load(open(os.path.join(folder, "structure.json"))); beats = st["beats"]; bars = st["bars"]
    meta = json.load(open(os.path.join(folder, "song.json"), encoding="utf-8")); dur = meta["duration"]
    sheet = json.load(open(os.path.join(folder, "choreo.json"), encoding="utf-8"))["sheet"]
    mo = json.load(open(os.path.join(folder, "morae.json"), encoding="utf-8")); line_t = sorted(float(l[2]) for l in mo["lines"])
    T0, T1 = 0.0, dur + 0.5; A = Anim(T0, T1)
    A.style = dict(speed=0.87, over=0.07, spring_hz=1.7, spring_tau=0.22)
    A.life = dict(amount=1.0, hips=(0.018, 0.0, 0.008), torso=(1.4, 3.0, 1.2), head=(1.8, 2.2, 1.2), breath_torso=2.4, breath_shoulder=4.0, breath_hips=0.004,
                  hand=(0.014, 0.014, 0.010), elbow=0.07, shoulder=1.3, fingers=0.04, hand_turn=0.06, react_yaw=3.2, react_pitch=2.0, react_lift=1.6)
    MICVIA = GW + unit(V(0.90, -0.10, 0.42)) * 0.085 + V(0.0, -0.01, 0.0)
    def E(t, pose, name="", **o): return dict(t=t, pose=pose, name=name, **o)
    def shifted(pose, d): q = dict(pose); q["pos"] = np.array(pose["pos"]) + V(*d); return q

    R = [E(T0, P["IDLE"])]; L = [E(T0, M(P["POLE"]))]
    rg = [(T0, 0.0)]; rc = [(T0, 0.0)]; lg = [(T0, 1.0)]; lc = [(T0, 1.0)]
    r_on, l_on = False, True; look = []; mouth = [(T0, 0.35)]
    for k, s in enumerate(sheet):
        m = MOVES[s["move"]]; ta = max(s["t"] + (0.10 if m["level"] >= 0 else 0.6), (R[-1]["t"] if R else 0) + 0.5)
        if k == 0: ta = T0 + 0.01
        # ---- right hand
        if m["R"] == "MIC":
            if not r_on and k > 0:
                ta_r = max(ta - 0.15, R[-1]["t"] + 0.6)
                R.append(E(ta_r, P["MIC"], "to the mic", via=MICVIA, over=0.0, antic=0.02, turn_in=0.7))
                rg += [(ta_r - 0.42, 0.0), (ta_r - 0.02, 1.0)]; rc += [(ta_r - 0.10, 0.0), (ta_r + 0.28, 1.0)]; r_on = True
        elif k > 0:
            pose = P[m["R"]]
            if r_on:
                D = 1.10; ts = ta - D / 0.87
                if ts < R[-1]["t"] + 0.5: ta = R[-1]["t"] + 0.5 + D / 0.87; ts = ta - D / 0.87
                rc += [(ts - 0.32, 1.0), (ts - 0.02, 0.0)]; rg += [(ts - 0.05, 1.0), (ts + 0.35, 0.0)]
                R.append(E(ta, pose, s["move"], dur=D, via=MIC_VIA_OFF, turn_lag=0.36, over=0.04)); r_on = False
            else: R.append(E(ta, pose, s["move"]))
            if m.get("beats") and s["end"] - ta > 1.6:                       # small re-strokes on the next lines inside the phrase
                for lt in [x for x in line_t if ta + 0.8 < x < s["end"] - 0.5][:2]:
                    R.append(E(lt + 0.05, shifted(pose, (0.008, -0.020, 0.014)), "beat", dur=0.36)); R.append(E(lt + 0.55, pose, "back", dur=0.42))
        # ---- left hand
        if m["L"] == "POLE":
            if not l_on and k > 0:
                ta_l = max(ta + 0.25, L[-1]["t"] + 0.6)
                L.append(E(ta_l, M(P["POLE"]), "to the stand", via=POLE_VIA, over=0.0, antic=0.02, turn_in=0.55))
                lg += [(ta_l - 0.40, 0.0), (ta_l - 0.02, 1.0)]; lc += [(ta_l - 0.10, 0.0), (ta_l + 0.27, 1.0)]; l_on = True
        elif k > 0:
            ta_l = max(ta + 0.08, L[-1]["t"] + 0.6)
            if l_on:
                ts = ta_l - 1.0 / 0.87
                lc += [(ts - 0.24, 1.0), (ts + 0.04, 0.0)]; lg += [(ts, 1.0), (ts + 0.38, 0.0)]
                L.append(E(ta_l, M(P[m["L"]]), s["move"], dur=1.0)); l_on = False
            else: L.append(E(ta_l, M(P[m["L"]]), s["move"]))
        # ---- body: the pose of the move, a moving hold, back to neutral-ish before the next
        t_hold = min(s["end"], ta + max(0.8, (s["end"] - ta) * 0.7))
        A.key("torso", ta, V(*m["torso"])); A.key("torso", t_hold, V(*m["torso"]) * 0.8)
        A.key("head", ta + 0.04, V(*m["head"])); A.key("head", t_hold, V(*m["head"]) * V(0.9, 0.8, 0.8))
        sh = m.get("shrug", 0.0)
        for s2 in ("shrugR", "shrugL"): A.key(s2, ta - 0.3, 0.0); A.key(s2, ta + 0.05, sh); A.key(s2, ta + 0.7, sh * 0.4)
        mouth.append((ta - 0.3, 1.0 if m["R"] == "MIC" else 0.35 if m["level"] < 0 else 0.6 if m["level"] == 2 else 0.85))
        look.append((max(0.0, ta - 0.35), m["look"]))
    rg.append((T1, 1.0 if r_on else 0.0)); rc.append((T1, 1.0 if r_on else 0.0)); lg.append((T1, 1.0 if l_on else 0.0)); lc.append((T1, 1.0 if l_on else 0.0))
    A.seq("R", R); A.seq("L", L)
    for name, keys in (("R.grip", rg), ("R.close", rc), ("L.grip", lg), ("L.close", lc), ("mouth", mouth)):
        for t, v in keys: A.key(name, min(T1, max(T0, t)), v)
    # ---- weight on the bar lines, a give of the knees on the beats (more with a louder band), breath before each line
    tot = np.array([b["total"] for b in bars]); ref = max(1e-6, np.percentile(tot, 90))
    for i, b in enumerate(bars):
        x = (0.022 if i % 2 == 0 else -0.022) * (0.6 + 0.4 * min(1.0, b["total"] / ref)); t = b["t"] + 0.3
        if t < T1: A.key("hips", t, V(x, 0, 0)); A.key("hipsrot", t, V(0, 0, 70 * x)); A.key("torso+weight", t, V(0, 0, -56 * x))
    for i, t in enumerate(beats):
        if t + 0.3 >= T1: break
        bi = min(len(bars) - 1, max(0, np.searchsorted([b["t"] for b in bars], t) - 1)); amp = 0.003 + 0.008 * min(1.0, bars[bi]["total"] / ref)
        A.key("hips+pulse", t, V(0, -amp if i % 2 else -0.0008, 0)); A.key("hips+pulse", t + 0.27, V(0, 0, 0))
    for t in line_t:
        if 0.5 < t < T1 - 0.5:
            A.key("torso+breath", t - 0.42, V(0, 0, 0)); A.key("torso+breath", t - 0.08, V(-1.3, 0, 0)); A.key("torso+breath", t + 0.45, V(0.2, 0, 0))
            for s2 in ("shrugR+breath", "shrugL+breath"): A.key(s2, t - 0.42, 0); A.key(s2, t - 0.08, 1.2); A.key(s2, t + 0.45, 0)
    tt, data = A.write(os.path.join(folder, "key.bin"))
    sq = [l for l in A.log if "SQUEEZED" in l]
    with open(os.path.join(folder, "look.txt"), "w", newline="\n") as f:
        f.write("# eyes: look t kind (0 microphone, 1 lens, 2 / 3 audience left / right, 4 far, 5 down)\n")
        for t, kd in look: f.write("look %.3f %d\n" % (t, kd))
    on = sum(min(sheet[i + 1]["t"] if i + 1 < len(sheet) else dur, dur) - s["t"] for i, s in enumerate(sheet) if MOVES[s["move"]]["R"] == "MIC")
    log(f"  body: {len(tt)} frames at 60 fps, {len(R) + len(L)} hand poses, right hand on the microphone {100 * on / max(1, dur):.0f}% of the song" + (f"; {len(sq)} squeezed changes" if sq else ""))
    return len(sq)


if __name__ == "__main__":
    folder = sys.argv[1]
    if "--bake" not in sys.argv:
        json.dump(plan(folder), open(os.path.join(folder, "choreo.json"), "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    bake(folder)
