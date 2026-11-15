"""Transitions for hand-keyed animation (2026-11-13, review of the hand-keyed pilot v2: "the changes of gesture are still stiff and
mechanical, and too fast - add functions to adjust them").

Measured on v2: the speed of a change peaked at 64 - 83 % of its way (it accelerated INTO the pose and stopped dead), the fastest reached
1.58 m/s, and wrist, hand and fingers all changed in the same instant. A hand is therefore no longer keyed pose-to-pose; it is a SEQUENCE of
poses with arrival times, and every change is made by one function with these controls (STYLE, or per event):
  speed      how long a change takes: from how far the wrist travels and how much the hand turns (a longer way takes longer, never a snatch)
  bias       where the speed peaks (0.5 = symmetric; below = quick away, long soft landing)
  arc        the wrist travels on a curve, not a line
  antic      a small move the other way before it sets off
  over       it lands a little past the pose and settles back
  lead / lag the elbow sets off first; the turn of the hand and then the fingers come after the wrist
  drag       while the wrist moves, the fingers trail behind it
"""
import numpy as np

STYLE = dict(speed=1.0,            # > 1 = quicker changes, < 1 = slower (scales every duration)
             base=0.30, per_m=2.9, per_deg=1 / 230.0, dmin=0.30, dmax=1.15,       # duration = max(base + per_m * distance, base + per_deg * turn)
             bias=0.42, arc=0.16, arc_max=0.045, antic=0.035, over=0.045, spring_hz=1.9, spring_tau=0.19,       # over: how far past the pose (share of the way); then a damped spring
             lead_elbow=0.05, lag_turn=0.07, lag_fingers=0.13, drag=0.20, relax=0.05)


def unit(v):
    v = np.asarray(v, float); return v / np.linalg.norm(v)


def mj(x):
    """minimum-jerk ease 0..1: no speed and no acceleration at either end"""
    x = np.clip(x, 0.0, 1.0); return x * x * x * (10 + x * (-15 + 6 * x))


def _frame_q(fin, palm):
    """orientation of a hand (finger direction, palm normal) as a quaternion (w, x, y, z)"""
    f = unit(fin); p = np.asarray(palm, float); p = unit(p - np.dot(p, f) * f); c = np.cross(f, p); m = np.stack([f, p, c], 1); tr = m[0, 0] + m[1, 1] + m[2, 2]
    if tr > 0: s = np.sqrt(tr + 1) * 2; q = [0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]: s = np.sqrt(1 + m[0, 0] - m[1, 1] - m[2, 2]) * 2; q = [(m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s]
    elif m[1, 1] > m[2, 2]: s = np.sqrt(1 + m[1, 1] - m[0, 0] - m[2, 2]) * 2; q = [(m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s]
    else: s = np.sqrt(1 + m[2, 2] - m[0, 0] - m[1, 1]) * 2; q = [(m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s]
    return np.array(q)


def _q_axes(q):
    """-> finger direction, palm normal (the first two columns of the rotation)"""
    w, x, y, z = q
    return np.array([1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w)]), np.array([2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w)])


def _slerp(a, b, s):
    d = float(np.dot(a, b))
    if d < 0: b = -b; d = -d
    if d > 0.9995: q = a + (b - a) * s; return q / np.linalg.norm(q)
    th = np.arccos(d); return (np.sin((1 - s) * th) * a + np.sin(s * th) * b) / np.sin(th)


def _angle(a, b): return float(np.degrees(np.arccos(np.clip(np.dot(unit(a), unit(b)), -1, 1))))


def bake_sequence(events, tt, style=None, log=None, name=""):
    """events: [{t (arrival), pose, and optionally name, dur, arc (vector), via (point), over, antic, bias}]; the first one is where the hand
    starts; 'fingers' = lag of the fingers for this change (s). -> dict of per-frame arrays: pos, fin, palm, pole, curl, spread, chest"""
    S = dict(STYLE); S.update(style or {}); n = len(tt); fps = 1.0 / (tt[1] - tt[0]); p0 = events[0]["pose"]
    cur = {k: np.tile(np.atleast_1d(np.asarray(p0.get(k, 0.0), float)), (n, 1)) for k in ("pos", "pole", "curl", "spread", "chest")}
    q = np.tile(_frame_q(p0["fin"], p0["palm"]), (n, 1)); prev = tt[0]
    ix = lambda t: int(np.clip(round((t - tt[0]) * fps), 0, n - 1))
    for e in events[1:]:
        B = e["pose"]; ta = float(e["t"]); Bp = np.asarray(B["pos"], float); qB = _frame_q(B["fin"], B["palm"])
        fa, pa = _q_axes(q[ix(ta)]); dist = float(np.linalg.norm(Bp - cur["pos"][ix(ta)])); turn = max(_angle(fa, B["fin"]), _angle(pa, B["palm"]))
        D = e.get("dur") or float(np.clip(max(S["base"] + S["per_m"] * dist, S["base"] + S["per_deg"] * turn), S["dmin"], S["dmax"]))
        D /= S["speed"]; ts = ta - D; squeezed = ""
        if ts < prev + 0.04: ts = prev + 0.04; squeezed = "  (SQUEEZED from %.2f s: the pose before it arrives too late)" % D; D = ta - ts
        A = cur["pos"][ix(ts)].copy(); dist = float(np.linalg.norm(Bp - A)); big = dist > 0.06
        over = e.get("over", S["over"] if big else 0.0); antic = e.get("antic", S["antic"] if big else 0.0)
        bias = e.get("bias", S["bias"]); g = np.log(0.5) / np.log(bias)                      # time warp: the half-way point of the ease falls at 'bias'
        def ease(tq, ts=ts, D=D, g=g):
            x = np.clip((tq - ts) / D, 0.0, 1.0); return mj(x ** g)
        x = (tt - ts) / D; s01 = ease(tt)
        sm = s01 * (1 + over) - antic * np.where((x > 0) & (x < 0.30), np.sin(np.pi * x / 0.30) ** 2, 0.0)
        # it stops like something on a shock absorber: past the pose, back a little the other way, and still - one soft rebound, no second
        tau = np.maximum(tt - ta, 0.0); w = 2 * np.pi * S["spring_hz"]; tc = S["spring_tau"]
        sm = np.where(x >= 1, 1 + over * np.exp(-tau / tc) * (np.cos(w * tau) + np.sin(w * tau) / (w * tc)), sm)
        if e.get("via") is not None:
            Cc = 2 * np.asarray(e["via"], float) - 0.5 * (A + Bp); u = s01[:, None]; P = (1 - u) ** 2 * A + 2 * u * (1 - u) * Cc + u ** 2 * Bp + (Bp - A) * (sm - s01)[:, None]
        else:
            arc = np.asarray(e["arc"], float) if e.get("arc") is not None else np.array([0.0, 1.0, 0.0]) * min(S["arc_max"], S["arc"] * dist)
            P = A + (Bp - A) * sm[:, None] + arc * np.sin(np.pi * s01)[:, None]
        m = tt >= ts; cur["pos"][m] = P[m]
        lf = e.get("fingers", S["lag_fingers"])          # per event: when the fingers change relative to the wrist (negative = they open first)
        for k, lag in (("pole", -S["lead_elbow"]), ("curl", lf), ("spread", lf), ("chest", 0.0)):
            fin_in = e.get("fingers_in", 1.0) if k in ("curl", "spread") else 1.0          # the fingers finish their change in this share of the move (a hand half way between two shapes is a claw: get through it quickly)
            so = ease(ts + (tt - lag - ts) / fin_in)[:, None]; Ak = cur[k][ix(ts + lag)].copy(); Bk = np.atleast_1d(np.asarray(B.get(k, Ak), float)); val = Ak + (Bk - Ak) * so
            if k == "curl" and big and lf > 0: val[:, 1:] += S["relax"] * np.sin(np.pi * so)                 # the fingers hang a little looser while the hand travels
            mk = tt >= ts + lag; cur[k][mk] = val[mk]
        lag = e.get("turn_lag", S["lag_turn"]); qA = q[ix(ts + lag)].copy(); so = ease(ts + (tt - lag - ts) / e.get("turn_in", 1.0))      # turn_in: the hand has finished turning after this share of the move (a hand that comes onto something must not still be turning when it gets there)
        for i in np.flatnonzero(tt >= ts + lag): q[i] = _slerp(qA, qB, float(so[i]))
        v = np.linalg.norm(np.gradient(P, 1.0 / fps, axis=0), axis=1)[(tt >= ts) & (tt <= ta)]
        if log is not None: log.append("  %s %-13s arrives %.2f  takes %.2f s  wrist %4.1f cm  turn %3.0f deg  peak %.2f m/s%s" % (name, e.get("name", ""), ta, D, dist * 100, turn, v.max() if len(v) else 0, squeezed))
        prev = ta
    fin = np.zeros((n, 3)); palm = np.zeros((n, 3))
    for i in range(n): fin[i], palm[i] = _q_axes(q[i])
    vel = np.gradient(cur["pos"], 1.0 / fps, axis=0); fin = fin - S["drag"] * vel                  # the fingers trail behind a moving wrist
    fin /= np.linalg.norm(fin, axis=1, keepdims=True)
    return dict(pos=cur["pos"], fin=fin, palm=palm, pole=cur["pole"] / np.linalg.norm(cur["pole"], axis=1, keepdims=True), curl=cur["curl"], spread=cur["spread"], chest=cur["chest"])


def gsmooth(x, sigma_frames):
    """zero-phase Gaussian: rounds the corners of a keyed curve without moving its accents in time"""
    r = int(4 * sigma_frames) + 1; k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma_frames) ** 2); k /= k.sum()
    return np.stack([np.convolve(np.pad(x[:, i], r, mode="edge"), k, mode="valid") for i in range(x.shape[1])], 1)


# ====================================================================== life (2026-11-13, review of v3: "it is not fluid enough. Before a movement
# starts, the bones that will move and the torso should already sway a little; when a movement stops there should be a cushion like a shock
# absorber; and after it has stopped the torso must not be completely still - the slow, fine movement of a real person breathing on a stage.")
LIFE = dict(amount=1.0,            # one knob for all of it (0 = off)
            breath_period=3.9, breath_torso=0.75, breath_shoulder=0.9, breath_hips=0.0016, breath_head=0.35,      # deg, deg, m, deg
            hips=(0.0035, 0.0, 0.0020), torso=(0.50, 0.80, 0.40), head=(0.80, 1.00, 0.60),                        # slow wander: m / deg
            hand=(0.0050, 0.0060, 0.0040), hand_turn=0.035, fingers=0.022, elbow=0.030, shoulder=1.2,
            react_hz=1.05, react_zeta=0.36, react_yaw=2.3, react_pitch=1.5, react_lift=1.2, react_shoulder=2.2, react_hips=0.0035)


def sway(tt, seed, periods=(5.3, 8.1, 3.4), amps=(0.55, 0.35, 0.25)):
    """slow wander between -1 and 1: three sines whose periods do not divide each other (it never repeats inside a song); the seed only sets the phases"""
    ph = np.random.RandomState(seed).uniform(0, 2 * np.pi, len(periods))
    return sum(a * np.sin(2 * np.pi * tt / (p * (1 + 0.07 * ((seed * 7) % 5 - 2))) + f) for a, p, f in zip(amps, periods, ph))


def spring(u, fps, hz, zeta):
    """u through a damped spring (a shock absorber): it lags, swings a little past, and comes to rest"""
    w = 2 * np.pi * hz; n = len(u); x = np.zeros_like(u, dtype=float); v = np.zeros(u.shape[1:] if u.ndim > 1 else ()); p = np.array(u[0], float); sub = 4; h = 1.0 / (fps * sub)
    for i in range(n):
        for _ in range(sub): v = v + h * (w * w * (u[i] - p) - 2 * zeta * w * v); p = p + h * v
        x[i] = p
    return x


def add_life(tt, out, layout, fps, cfg=None):
    """breathing and slow wander on pelvis, torso, head, shoulders and both arms; and the trunk's answer to what the arms do (through a spring)"""
    L = dict(LIFE); L.update(cfg or {}); a = L["amount"]
    if a <= 0: return
    def ch(name): c0, size = layout[name]; return slice(c0, c0 + size)
    # ---- what the arms do to the trunk: it goes with a swinging arm, a moment late, swings a little past when the arm stops, and settles
    vR = np.gradient(out[:, ch("R.pos")].astype(float), 1.0 / fps, axis=0); vL = np.gradient(out[:, ch("L.pos")].astype(float), 1.0 / fps, axis=0)
    free = lambda side: (1.0 - np.clip(out[:, ch(side + ".grip")].astype(float), 0, 1))                # a hand on the microphone moves nothing
    vR *= free("R"); vL *= free("L"); r = spring(vR + vL, fps, L["react_hz"], L["react_zeta"])
    out[:, ch("torso")] += a * np.stack([L["react_pitch"] * r[:, 2] - L["react_lift"] * r[:, 1], L["react_yaw"] * r[:, 0], 0.4 * L["react_yaw"] * r[:, 0]], 1)
    out[:, ch("hips")] += a * np.stack([-L["react_hips"] * r[:, 0], 0 * r[:, 0], -0.5 * L["react_hips"] * r[:, 2]], 1)
    out[:, ch("shrugR")] += a * L["react_shoulder"] * spring(np.maximum(vR[:, 1], 0.0)[:, None], fps, 1.4, 0.5)
    out[:, ch("shrugL")] += a * L["react_shoulder"] * spring(np.maximum(vL[:, 1], 0.0)[:, None], fps, 1.4, 0.5)
    # ---- breathing: chest rises (pitch back) and shoulders lift on the way in, slower out; the pelvis gives a millimetre; the head stays level
    ph = 2 * np.pi * tt / L["breath_period"] + 0.5 * sway(tt, 11); b = 0.5 * (1 - np.cos(ph + 0.45 * np.sin(ph)))          # 0 out .. 1 in, quicker in than out
    out[:, ch("torso")] += a * np.stack([-L["breath_torso"] * b, 0 * b, 0 * b], 1); out[:, ch("head")] += a * np.stack([L["breath_head"] * b, 0 * b, 0 * b], 1)
    out[:, ch("shrugR")] += a * L["breath_shoulder"] * b[:, None]; out[:, ch("shrugL")] += a * L["breath_shoulder"] * b[:, None]
    out[:, ch("hips")] += a * np.stack([0 * b, L["breath_hips"] * (b - 0.5), 0 * b], 1)
    # ---- slow wander: the weight is never quite still, the head neither; a free hand floats a few millimetres, an elbow breathes
    hx, hz = sway(tt, 3, (7.3, 11.0, 4.6)), sway(tt, 5, (6.1, 9.4, 3.9))
    out[:, ch("hips")] += a * np.stack([L["hips"][0] * hx, 0 * hx, L["hips"][2] * hz], 1)
    out[:, ch("hipsrot")] += a * np.stack([0 * hx, 0 * hx, 70 * L["hips"][0] * hx], 1)
    out[:, ch("torso")] += a * np.stack([L["torso"][0] * sway(tt, 7), L["torso"][1] * sway(tt, 8, (6.7, 10.3, 4.1)), L["torso"][2] * sway(tt, 9) - 56 * L["hips"][0] * hx], 1)
    out[:, ch("head")] += a * np.stack([L["head"][0] * sway(tt, 21, (4.7, 7.7, 2.9)), L["head"][1] * sway(tt, 22, (5.9, 9.1, 3.7)), L["head"][2] * sway(tt, 23, (6.4, 8.8, 3.3))], 1)
    for k, side in enumerate(("R", "L")):
        s0 = 40 + 20 * k
        # a hand that is about to take hold of something, holds it, or has just let go does not float: within 0.8 s of a grip the sway fades out
        # (it came back as the grip let go and drifted the open hand 10 - 13 mm into the microphone)
        gr = np.clip(out[:, ch(side + ".grip")].astype(float), 0, 1)[:, 0]; win = int(0.8 * fps)
        near = np.array([gr[max(0, i - win):i + win + 1].max() for i in range(len(gr))]); free = 1.0 - gsmooth(near[:, None], 0.15 * fps)[:, 0]
        out[:, ch(side + ".pos")] += a * free[:, None] * np.stack([L["hand"][i] * sway(tt, s0 + i, (4.9 + i, 7.1 + 0.6 * i, 3.1 + 0.4 * i)) for i in range(3)], 1)
        out[:, ch(side + ".fin")] += a * free[:, None] * L["hand_turn"] * np.stack([sway(tt, s0 + 4 + i, (5.6, 8.3, 3.6)) for i in range(3)], 1)
        # (a hand that holds the microphone or the stand cannot float: its ELBOW breathes instead, three times as much, and that shoulder with it -
        # with both hands holding, the picture had stood almost still: 34.6 - 35.0 s was the quietest stretch of v4)
        held = np.clip(out[:, ch(side + ".grip")].astype(float), 0, 1)
        es = np.stack([sway(tt, s0 + 8 + i, (4.4, 6.9, 3.0)) for i in range(3)], 1); out_sign = 1.0 if side == "R" else -1.0
        es[:, 0] = np.where(held[:, 0] > 0.5, out_sign * np.abs(es[:, 0]), es[:, 0])      # (a held arm's elbow breathes OUTWARD only: swung in, it laid the forearm on her belly)
        out[:, ch(side + ".pole")] += a * L["elbow"] * (1 + 1.0 * held) * es + a * held * np.array([0.06 * out_sign, 0.0, 0.0])
        out[:, ch("shrug" + side)] += a * L["shoulder"] * (1 + 1.0 * held) * sway(tt, s0 + 16, (5.2, 8.6, 3.5))[:, None]
        cu = out[:, ch(side + ".curl")]; cu[:, 1:] += a * L["fingers"] * np.stack([sway(tt, s0 + 12 + i, (3.7, 5.8, 2.6)) for i in range(4)], 1)


# how much each keyed body track is rounded (seconds): the curves of pelvis, torso, head and shoulders change direction AT a key
SOFTEN = {"hips": 0.05, "hipsrot": 0.05, "torso": 0.075, "head": 0.065, "shrugR": 0.05, "shrugL": 0.05, "R.grip": 0.03, "L.grip": 0.03, "R.close": 0.03, "L.close": 0.03, "mouth": 0.08}
