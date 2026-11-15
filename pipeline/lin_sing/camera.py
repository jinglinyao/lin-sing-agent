"""Automatic camera for the stage (FrameDumper show file): one moving camera on the bar grid, a new path only where the music turns.

Rules (from the hand-made camera of the earlier songs, review: "no hard cuts; wide / full / medium / close in a sensible order; moves on the beat"):
  - camera keys sit on bar lines; between keys the camera travels on a smooth curve (Hermite in Unity)
  - a new path starts where the choreography changes intensity class (quiet / middle / full / no voice); the two paths dissolve over two beats
  - quiet   : medium-close, from one side, a slow push in            middle : full -> medium, a slow arc across the front
    full    : pulls back to wide on the downbeat and arcs; a small push on every beat (pulse)
    no voice: wide and slow
  dist (m): 4.4 wide (whole figure) - 3.0 full - 2.2 medium - 1.6 medium close - 1.4 close;  yaw + = from her right
usage: python -m lin_sing.camera <song folder>   (reads structure.json, choreo.json -> show.txt)
"""
import os, sys, json
import numpy as np

UP = [(4.4, -0.56), (3.0, -0.30), (2.2, -0.16), (1.6, -0.08), (1.4, -0.05), (1.3, -0.04)]
def up_for(dist): d = [u[0] for u in UP][::-1]; u = [x[1] for x in UP][::-1]; return float(np.interp(dist, d, u))


def build(folder, log=print):
    st = json.load(open(os.path.join(folder, "structure.json"))); beats = st["beats"]; beat = st["beat"]; bars = [b["t"] for b in st["bars"]]
    sheet = json.load(open(os.path.join(folder, "choreo.json"), encoding="utf-8"))["sheet"]
    dur = json.load(open(os.path.join(folder, "song.json"), encoding="utf-8"))["duration"]
    def snap(t):                                                    # nearest bar line
        if not bars: return t
        i = int(np.argmin([abs(b - t) for b in bars])); return bars[i] if abs(bars[i] - t) < 1.2 else t
    # sections: runs of sheet items with the same intensity class
    secs = []
    for s in sheet:
        lev = s["level"]
        if secs and secs[-1]["level"] == lev: secs[-1]["end"] = s["end"]
        else: secs.append(dict(t=s["t"], end=s["end"], level=lev))
    for i in range(len(secs) - 1): secs[i]["end"] = secs[i + 1]["t"]
    secs[-1]["end"] = dur
    lines = ["# camera made by lin_sing.camera: cam2 t yaw dist up elev pulse | xfade t dur | beat t ... | col t r g b light", "col 0.0 0.09 0.11 0.18 0.66"]
    side = 1; full_beats = []
    for i, s in enumerate(secs):
        a = 0.0 if i == 0 else snap(s["t"]); b = dur + 0.5 if i == len(secs) - 1 else snap(secs[i + 1]["t"]) - 0.02
        if b - a < 1.0: continue
        n = max(2, int((b - a) / 4.5) + 1); ts = np.linspace(a, b, n); u = np.linspace(0, 1, n); side = -side
        lev = s["level"]
        if lev < 0:   yaw = side * (4 + 16 * u); dist = 5.0 - 1.2 * u; pulse = 0.0
        elif lev == 0: yaw = side * (28 - 12 * u); dist = 1.85 - 0.45 * u; pulse = 0.0
        elif lev == 1: yaw = side * (-10 + 30 * u); dist = 3.0 - 1.0 * u; pulse = 0.0
        else:
            yaw = side * (2 + 18 * u); dist = np.where(u < 0.15, 3.4 + 4 * u, 4.0 - 0.9 * u); pulse = 0.012
            full_beats += [t for t in beats if a <= t <= b]
        for k in range(n):
            lines.append("cam2 %.3f %.1f %.2f %.3f %.2f %.3f" % (ts[k], yaw[k], dist[k], up_for(dist[k]), 0.02 if dist[k] > 2.5 else 0.03, pulse))
        if i > 0: lines.append("xfade %.3f %.3f" % (a, 2 * beat))
    if full_beats: lines.append("beat " + " ".join("%.3f" % t for t in full_beats))
    open(os.path.join(folder, "show.txt"), "w", newline="\n").write("\n".join(lines) + "\n")
    log(f"  camera: {sum(1 for l in lines if l.startswith('cam2'))} keys, {sum(1 for l in lines if l.startswith('xfade'))} dissolves")


if __name__ == "__main__":
    build(sys.argv[1])
