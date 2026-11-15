"""Automatic camera and stage light (FrameDumper / StageLook show file): one moving camera on the bar grid, a new path only where the
music turns, and lights that follow the sections.

Camera (review rules of the earlier songs: "no hard cuts; wide / full / medium / close in a sensible order; moves on the beat"):
  - keys sit on bar lines and phrase starts; between keys the camera travels on a smooth curve (Hermite in Unity)
  - a new path where the choreography changes intensity class; the paths dissolve over two beats
  - quiet   : medium-close from one side, a small push into every phrase        middle: full -> medium, a slow arc across the front
    full    : wide on the downbeat, then an arc in; low and high angles take turns from chorus to chorus; a small push on every beat
    no voice: wide and slow
  - she sings into a stand microphone: medium and close shots stay at least 14 degrees off her front and a little above her eyes, so the
    microphone sits under her mouth in the picture instead of across it
  dist (m): 4.4 wide (whole figure) - 3.0 full - 2.2 medium - 1.6 medium close - 1.4 close;  yaw + = from her right
Light (StageLook): per section the backdrop colour (col), the rim colour (tint) and the levels of key / rim / pool / fill (lit);
  quiet = dim and cool with a strong rim, full = brighter, warmer, the pool opens up; mood "sad" cools everything, "bright" warms it.
usage: python -m lin_sing.camera <song folder>   (reads structure.json, choreo.json, morae.json -> show.txt)
"""
import os, sys, json, random
import numpy as np

UP = [(4.4, -0.56), (3.0, -0.30), (2.2, -0.16), (1.6, -0.08), (1.4, -0.05), (1.3, -0.04)]
def up_for(dist): d = [u[0] for u in UP][::-1]; u = [x[1] for x in UP][::-1]; return float(np.interp(dist, d, u))

#            backdrop col (r g b), key, rim tint (r g b), lit: key rim pool fill
PALETTE = {
    "neutral": {-1: ((0.07, 0.09, 0.17), 0.50, (0.70, 0.82, 1.00), (0.50, 6.0, 3.0, 0.45)),
                0:  ((0.06, 0.08, 0.18), 0.55, (0.66, 0.80, 1.00), (0.55, 8.0, 3.2, 0.50)),
                1:  ((0.10, 0.08, 0.19), 0.62, (0.86, 0.78, 1.00), (0.62, 7.5, 4.2, 0.52)),
                2:  ((0.20, 0.09, 0.15), 0.72, (1.00, 0.80, 0.70), (0.72, 10.0, 5.5, 0.55))},
}
def palette(mood, level):
    col, key, tint, lit = PALETTE["neutral"][level]
    if mood == "sad": col = tuple(c * f for c, f in zip(col, (0.75, 0.9, 1.1))); tint = (0.62, 0.76, 1.0); lit = (lit[0] * 0.92, lit[1], lit[2] * 0.85, lit[3])
    if mood == "bright": col = tuple(c * f for c, f in zip(col, (1.35, 1.05, 0.85))); tint = (1.0, 0.86, 0.70); lit = (lit[0] * 1.08, lit[1] * 0.9, lit[2] * 1.15, lit[3] * 1.1)
    return col, key, tint, lit


def build(folder, log=print, seed=5):
    st = json.load(open(os.path.join(folder, "structure.json"))); beats = st["beats"]; beat = st["beat"]; bars = [b["t"] for b in st["bars"]]
    cj = json.load(open(os.path.join(folder, "choreo.json"), encoding="utf-8")); sheet = cj["sheet"]; mood = cj.get("mood", "neutral")
    dur = json.load(open(os.path.join(folder, "song.json"), encoding="utf-8"))["duration"]
    rnd = random.Random(seed)
    def snap(t):
        if not bars: return t
        i = int(np.argmin([abs(b - t) for b in bars])); return bars[i] if abs(bars[i] - t) < 1.2 else t
    secs = []
    for s in sheet:
        lev = s["level"]
        if secs and secs[-1]["level"] == lev: secs[-1]["end"] = s["end"]; secs[-1]["phr"].append(s["t"])
        else: secs.append(dict(t=s["t"], end=s["end"], level=lev, phr=[s["t"]]))
    for i in range(len(secs) - 1): secs[i]["end"] = secs[i + 1]["t"]
    secs[-1]["end"] = dur
    lines = ["# camera and light made by lin_sing.camera: cam2 t yaw dist up elev pulse | xfade t dur | beat t ... | col t r g b key | tint t r g b | lit t key rim pool fill"]
    full_beats = []; chorus_n = 0; light = []; bar = 4 * beat; last = None; prev_side = 1
    def key(t, yaw, dist, elev, pulse):
        if dist < 2.6 and abs(yaw) < 14: yaw = 14 if yaw >= 0 else -14              # keep the microphone off her mouth
        if dist < 2.6: elev += 0.05 + 0.04 * (2.6 - dist)                             # and look a little down on it
        lines.append("cam2 %.3f %.1f %.2f %.3f %.3f %.3f" % (t, yaw, dist, up_for(dist), elev, pulse))
    def shape(lev, uu, side, low):
        """yaw, dist, elev, pulse of a section's framing at its share uu (0..1)"""
        if lev < 0: return side * (4 + 16 * uu), 5.0 - 1.2 * uu, 0.02, 0.0
        if lev == 0: return side * (30 - 12 * uu), 1.85 - 0.40 * uu, 0.02, 0.0
        if lev == 1: return side * (-12 + 32 * uu), 3.0 - 1.0 * uu, 0.02, 0.0
        return side * (2 + 18 * uu), (3.4 + 4 * uu if uu < 0.15 else 4.0 - 1.1 * uu), (-0.22 if low else 0.18) * (1 - 0.5 * uu), 0.012
    # ONE continuous camera for the whole song (a dissolve between two different framings shows her twice - review 2026-11-15): where the
    # section changes, the camera travels to the new framing within one bar, on the shorter way round
    for i, s in enumerate(secs):
        a = 0.0 if i == 0 else snap(s["t"]); b = dur + 0.5 if i == len(secs) - 1 else snap(secs[i + 1]["t"])
        if b - a < 1.0: continue
        lev = s["level"]; low = False
        if lev == 2: chorus_n += 1; low = chorus_n % 2 == 1                           # alternate: a low (heroic) and a high angle
        col, k_, tint, lit = palette(mood, lev); light.append((a, col, k_, tint, lit))
        side = -prev_side                                                             # the other side for variety ...
        if last is not None:
            y_alt = shape(lev, 0.0, side, low)[0]; y_same = shape(lev, 0.0, -side, low)[0]
            if abs(y_alt - last) > abs(y_same - last) + 30: side = -side             # ... unless that means swinging round her
        prev_side = side
        start = a if i == 0 else min(a + bar, a + 0.5 * (b - a))                      # (the move from the last framing takes one bar)
        n = max(2, int((b - start) / 4.5) + 1); ts = list(np.linspace(start, b - 0.05, n))
        for p in s["phr"]:
            if start + 1.0 < p < b - 1.0 and all(abs(p - x) > 1.2 for x in ts): ts.append(p)
        ts.sort()
        for t_ in ts:
            uu = (t_ - start) / max(1e-3, b - start); yaw, dist, elev, pulse = shape(lev, uu, side, low)
            if lev == 0 and any(abs(t_ - p) < 0.05 for p in s["phr"][1:]): dist *= 0.92          # a small push into each phrase
            key(t_, yaw, dist, elev, pulse); last = yaw
        if lev == 2: full_beats += [t for t in beats if a <= t <= b]
    for t, col, k_, tint, lit in light:
        lines.append("col %.3f %.3f %.3f %.3f %.2f" % (t, *col, k_))
        lines.append("tint %.3f %.2f %.2f %.2f" % (t, *tint))
        lines.append("lit %.3f %.2f %.2f %.2f %.2f" % (t, *lit))
    if full_beats: lines.append("beat " + " ".join("%.3f" % t for t in full_beats))
    open(os.path.join(folder, "show.txt"), "w", newline="\n").write("\n".join(lines) + "\n")
    log(f"  camera: {sum(1 for l in lines if l.startswith('cam2'))} keys, {sum(1 for l in lines if l.startswith('xfade'))} dissolves; light: {len(light)} sections ({mood})")


if __name__ == "__main__":
    build(sys.argv[1])
