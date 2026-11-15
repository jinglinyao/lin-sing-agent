"""Hand-keyed animation for SingDriver (Assets/MV/Scripts/KeyAnim.cs): key poses -> curves -> one value per frame for every control.

An animator's dope sheet in code. Every control is a track of keys (time, value); between keys the value follows a shape-preserving cubic
(PCHIP: it passes through every key, never overshoots between two keys, and eases where the direction turns - so a key that is an extreme
is a soft stop, a key that is passed on the way is passed at speed). What makes it read as alive is authored, not generated:
  overlap      each track has a lag: the torso leads, the wrist follows, the hand's turn and the fingers come later still
  breakdowns   a key between two poses that bends the path (arcs), added by hand where the straight line is wrong
  moving holds a held pose is two keys a little apart, never one frozen value
The result is baked at 60 fps and written as float32: fps, frames, channels, start time, then frames x channels (layout: KeyAnim.cs).
"""
import os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from key_trans import bake_sequence, gsmooth, add_life, SOFTEN, STYLE, LIFE

NCH = 57
LAYOUT = {  # track -> (first channel, size)
    "hips": (0, 3), "hipsrot": (3, 3), "torso": (6, 3), "head": (9, 3), "shrugR": (12, 1), "shrugL": (13, 1),
    "R.pos": (14, 3), "R.fin": (17, 3), "R.palm": (20, 3), "R.pole": (23, 3), "R.curl": (26, 5), "R.spread": (31, 1),
    "L.pos": (32, 3), "L.fin": (35, 3), "L.palm": (38, 3), "L.pole": (41, 3), "L.curl": (44, 5), "L.spread": (49, 1),
    "R.chest": (50, 1), "L.chest": (51, 1), "R.grip": (52, 1), "L.grip": (53, 1), "R.close": (54, 1), "L.close": (55, 1), "mouth": (56, 1),
}
UNIT = {"R.fin", "R.palm", "R.pole", "L.fin", "L.palm", "L.pole"}        # directions: normalised after interpolation
# overlap, seconds (positive = later). The pelvis and the torso lead; wrist on time; the turn of the hand, then the fingers, trail.
LAG = {"hips": -0.050, "hipsrot": -0.050, "torso": -0.035, "head": 0.045, "shrugR": -0.02, "shrugL": -0.02,
       "R.pos": 0.0, "L.pos": 0.0, "R.pole": 0.020, "L.pole": 0.020, "R.fin": 0.040, "L.fin": 0.040, "R.palm": 0.040, "L.palm": 0.040,
       "R.curl": 0.085, "L.curl": 0.085, "R.spread": 0.085, "L.spread": 0.085}


def pchip(x, y, xq):
    """shape-preserving cubic through (x, y) evaluated at xq; y may have several columns"""
    x = np.asarray(x, float); y = np.asarray(y, float).reshape(len(x), -1); xq = np.asarray(xq, float)
    if len(x) == 1: return np.repeat(y, len(xq), axis=0)
    h = np.diff(x); d = np.diff(y, axis=0) / h[:, None]; m = np.zeros_like(y)
    for i in range(1, len(x) - 1):
        ok = d[i - 1] * d[i] > 0; w1 = 2 * h[i] + h[i - 1]; w2 = h[i] + 2 * h[i - 1]
        with np.errstate(divide="ignore", invalid="ignore"):
            m[i] = np.where(ok, (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]), 0.0)
    m[0] = 0.0; m[-1] = 0.0                                             # the piece starts and ends at rest
    i = np.clip(np.searchsorted(x, xq, side="right") - 1, 0, len(x) - 2); t = np.clip((xq - x[i]) / h[i], 0, 1)[:, None]
    h00 = 2 * t ** 3 - 3 * t ** 2 + 1; h10 = t ** 3 - 2 * t ** 2 + t; h01 = -2 * t ** 3 + 3 * t ** 2; h11 = t ** 3 - t ** 2
    return h00 * y[i] + h10 * h[i][:, None] * m[i] + h01 * y[i + 1] + h11 * h[i][:, None] * m[i + 1]


class Anim:
    def __init__(self, t0, t1, fps=60):
        self.t0, self.t1, self.fps = float(t0), float(t1), fps; self.keys = {}; self.notes = []; self.seqs = {}; self.style = {}; self.life = {}; self.log = []

    def key(self, track, t, value):
        """track may be "name+layer": the layers of one control add up (e.g. "torso" the pose, "torso+breath" the breathing on top)"""
        size = LAYOUT[track.split("+")[0]][1]; v = np.atleast_1d(np.asarray(value, float))
        assert v.shape == (size,), (track, t, v.shape)
        self.keys[track] = [k for k in self.keys.get(track, []) if abs(k[0] - t) > 1e-4] + [(float(t), v)]

    def shift(self, tracks, dt, t_from=None, t_to=None):
        """move the keys of these tracks in time (those strictly inside t_from .. t_to; default: all but the first and last moment of the piece)"""
        a = self.t0 if t_from is None else t_from; b = self.t1 if t_to is None else t_to
        for name in tracks:
            self.keys[name] = [(t + dt if a + 1e-6 < t < b - 1e-6 else t, v) for t, v in self.keys.get(name, [])]

    def seq(self, side, events):
        """a hand as a sequence of poses with arrival times (key_trans.bake_sequence); replaces that hand's keyed pos / fin / palm / pole / curl / spread / chest"""
        self.seqs[side] = events

    def hold(self, track, t0, t1, value, drift=None):
        """a moving hold: the value at t0, and at t1 the value plus a small drift"""
        v = np.atleast_1d(np.asarray(value, float)); self.key(track, t0, v); self.key(track, t1, v + (0 if drift is None else np.asarray(drift, float)))

    def hand(self, side, t, pose, **over):
        """all tracks of one hand from a pose dict (pos, fin, palm, pole, curl, spread; optional chest, grip, close)"""
        p = dict(pose); p.update(over)
        for name in ("pos", "fin", "palm", "pole", "curl", "spread", "chest", "grip", "close"):
            if name in p and p[name] is not None: self.key(side + "." + name, t, p[name])

    def bake(self):
        n = int(round((self.t1 - self.t0) * self.fps)) + 1; tt = self.t0 + np.arange(n) / self.fps; out = np.zeros((n, NCH), np.float32)
        for name in sorted(self.keys):
            track = name.split("+")[0]; c0, size = LAYOUT[track]; ks = sorted(self.keys[name], key=lambda k: k[0])
            if not ks: continue
            x = np.array([k[0] for k in ks]); y = np.stack([k[1] for k in ks]); out[:, c0:c0 + size] += pchip(x, y, tt - LAG.get(track, 0.0))
        for track, sg in SOFTEN.items():                                         # round the corners of the keyed body curves
            c0, size = LAYOUT[track]; out[:, c0:c0 + size] = gsmooth(out[:, c0:c0 + size].astype(float), sg * self.fps)
        self.log = []
        for side, events in self.seqs.items():                                   # a hand given as a sequence of poses: every change by key_trans.bake_sequence
            r = bake_sequence(events, tt, self.style, self.log, side)
            for k, v in r.items(): c0, size = LAYOUT[side + "." + k]; out[:, c0:c0 + size] = v
        add_life(tt, out, LAYOUT, self.fps, self.life)                           # breathing, slow wander, the trunk's answer to the arms
        for track in UNIT:
            c0, size = LAYOUT[track]; v = out[:, c0:c0 + size]; out[:, c0:c0 + size] = v / np.maximum(1e-9, np.linalg.norm(v, axis=1, keepdims=True))
        return tt, out

    def write(self, path):
        tt, out = self.bake(); head = np.array([self.fps, len(tt), NCH, self.t0], np.float32)
        with open(path, "wb") as f: f.write(head.tobytes()); f.write(out.astype(np.float32).tobytes())
        return tt, out


def mirror(pose):
    """a right-hand pose for the left hand"""
    q = dict(pose)
    for k in ("pos", "fin", "palm", "pole"):
        if k in q: v = np.array(q[k], float); v[0] = -v[0]; q[k] = v
    return q


def unit(v):
    v = np.asarray(v, float); return v / np.linalg.norm(v)
