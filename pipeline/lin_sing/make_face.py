"""Bake the whole face track (mouth, brows, eyelids, gaze) for a singing VRM from
  * a vocal stem and
  * an aligned mora list [consonant, vowel, onset, vowel start, end] (seconds from the clip start).

v3 rules, after the review of v2 ("lips do not match", "expression not natural"):
  mouth  - vowel identity strictly from the aligned morae, cross-faded over ~80 ms (coarticulation); the mouth is open
           for as long as a mora sounds and closes in the rests; how wide follows the level but never drops below a
           clearly visible opening while a note is held; lip closures only for m / b / p.
  face   - only the model's own VRoid shapes (the transferred ARKit brow / eye shapes did not sit well on her face),
           small amplitudes, slow curves that follow the phrase; two closed-eye moments on long notes; blinks tied to
           gaze shifts; a calm gaze that mostly holds the camera.

Output JSON (flat): {fps, count, names, w, open, openSlow, gaze, gazeSlow, blinks}

usage: python make_face.py <vocals.wav> <morae.json> <out.json> [check.png]
"""
import sys, json
import numpy as np
import soundfile as sf

FPS = 60
VOWEL = {
    "a": {"Fcl_MTH_A": 1.00},
    "i": {"Fcl_MTH_I": 0.95},
    "u": {"Fcl_MTH_U": 0.95},
    "e": {"Fcl_MTH_E": 0.95},
    "o": {"Fcl_MTH_O": 0.95},
}
CONS = {
    "PP": {"mouthPressLeft": 0.9, "mouthPressRight": 0.9, "mouthRollUpper": 0.4, "mouthRollLower": 0.4},   # m b p: lips shut
    "FF": {"mouthRollLower": 0.7, "mouthUpperUpLeft": 0.4, "mouthUpperUpRight": 0.4},                      # f v
    "WW": {"Fcl_MTH_U": 0.75, "mouthPucker": 0.35},                                                         # w: rounded
    "YY": {"Fcl_MTH_I": 0.6},                                                                               # y
    "SH": {"Fcl_MTH_U": 0.35, "Fcl_MTH_I": 0.35},                                                           # sh ch j: teeth close, lips forward
    "SS": {"Fcl_MTH_I": 0.55},                                                                              # s z ts: teeth together
    "TT": {"Fcl_MTH_I": 0.30},                                                                              # t d n r k g: tongue only, mouth nearly as the vowel
}
CCLASS = {"m": "PP", "b": "PP", "p": "PP", "f": "FF", "v": "FF", "w": "WW", "y": "YY", "sh": "SH", "ch": "SH", "j": "SH",
          "s": "SS", "z": "SS", "ts": "SS", "t": "TT", "d": "TT", "n": "TT", "r": "TT", "k": "TT", "g": "TT", "h": None}
CGAIN = {"PP": 1.0, "FF": 1.0, "WW": 1.0, "YY": 0.8, "SH": 1.0, "SS": 1.0, "TT": 0.45}     # how much a consonant squeezes the vowel
# with the five extra shapes of jinglin_ps2.vrm (make_new_mouths.py): pass "new" as the 5th argument
VOWEL_NEW = {
    "a": {"Fcl_MTH_A": 1.00},
    "i": {"Fcl_MTH_I": 0.95},
    "u": {"Fcl_MTH_U": 0.40, "mth_U_pout": 0.60},
    "e": {"Fcl_MTH_E": 0.40, "mth_E_open": 0.62},          # now clearly different from i
    "o": {"Fcl_MTH_O": 0.50, "mth_O_tall": 0.52},
}
CONS_NEW = dict(CONS); CONS_NEW["TT"] = {"mth_L_tongue": 0.85}; CONS_NEW["FF"] = {"mth_F_bite": 1.0}
CCLASS_NEW = dict(CCLASS); CCLASS_NEW.update({"k": "KK", "g": "KK"}); CONS_NEW["KK"] = {"Fcl_MTH_A": 0.18}
if len(sys.argv) > 5 and sys.argv[5] == "vroid":        # stock VRoid shapes only: lips shut = the vowel squeezed out, a touch of "Close"
    VOWEL = dict(VOWEL); VOWEL["i"] = {"Fcl_MTH_I": 0.50, "Fcl_MTH_A": 0.16}; VOWEL["e"] = {"Fcl_MTH_E": 0.45, "Fcl_MTH_A": 0.50}      # the stock E opens 2.4 mm (it reads as "i"); with half an A it opens about 5.5 mm
    CONS = dict(CONS); CONS["PP"] = {}; CONS["FF"] = {"Fcl_MTH_U": 0.25, "Fcl_MTH_Close": 0.15}; CONS["WW"] = {"Fcl_MTH_U": 0.85}
if len(sys.argv) > 5 and sys.argv[5] == "new":
    VOWEL, CONS, CCLASS = VOWEL_NEW, CONS_NEW, CCLASS_NEW; CGAIN["KK"] = 0.35; CGAIN["TT"] = 0.55
MOUTH = sorted({n for d in list(VOWEL.values()) + list(CONS.values()) for n in d})


def gauss(a, sigma, n_pad_mode="edge"):
    k = np.arange(-int(3 * sigma * FPS) - 1, int(3 * sigma * FPS) + 2); w = np.exp(-0.5 * (k / (sigma * FPS)) ** 2); w /= w.sum()
    return np.convolve(np.pad(a, len(k) // 2, mode=n_pad_mode), w, "valid")


def main():
    wav, mpath, dst = sys.argv[1], sys.argv[2], sys.argv[3]
    png = sys.argv[4] if len(sys.argv) > 4 else None
    x, sr = sf.read(wav)
    if x.ndim > 1: x = x.mean(1)
    n = int(len(x) / sr * FPS); T = np.arange(n) / FPS
    hop = sr / FPS; win = int(sr * 0.035)
    env = np.array([np.sqrt(np.mean(x[max(0, int(i * hop) - win // 2): int(i * hop) + win // 2 + 1] ** 2)) for i in range(n)])
    db = 20 * np.log10(env + 1e-7); top = np.percentile(db, 97)
    level = np.clip((db - (top - 26)) / 22, 0, 1)                           # 0 quiet .. 1 loud
    presence = gauss(np.clip((db - (top - 40)) / 8, 0, 1), 0.03)            # is there any voice at all
    level_s = gauss(level, 0.05)
    morae = [m[:5] for m in json.load(open(mpath, encoding="utf-8"))["morae"]]

    # ---- mouth
    V = np.zeros((n, 5)); C = {k: np.zeros(n) for k in CONS}; sounding = np.zeros(n)
    for i, (c, v, t_on, t_v, t_end) in enumerate(morae):
        nxt = morae[i + 1][2] if i + 1 < len(morae) else 1e9
        hold_to = nxt if nxt - t_end < 0.14 else t_end                      # legato: keep the vowel until the next mora starts
        i_on, i_v, i_e = int(round(t_on * FPS)), int(round(t_v * FPS)), int(round(hold_to * FPS))
        i_v = max(i_v, i_on); i_e = min(n, max(i_e, i_v + 2))
        if v: V[i_v:i_e, "aiueo".index(v)] = 1
        sounding[max(0, i_on):min(n, int(round(t_end * FPS)) + 1)] = 1
        cls = CCLASS.get(c) if c else None
        if cls:
            a = max(0, min(i_on, i_v - 3) - (2 if cls == "PP" else 0))      # lips are already shut a moment before a b / m is heard
            C[cls][a:max(i_v, a + 3)] = 1
            if v: V[a:i_v, "aiueo".index(v)] = np.maximum(V[a:i_v, "aiueo".index(v)], 0.6)   # the coming vowel already shapes the mouth
    Vs = np.stack([gauss(V[:, k], 0.032) for k in range(5)], 1)
    tot = Vs.sum(1, keepdims=True); Vn = Vs / np.maximum(tot, 1e-6)          # which vowel
    Cs = {k: gauss(C[k], 0.018) for k in C}
    gate = np.clip(gauss(sounding, 0.045) * 1.25, 0, 1) * np.clip(presence * 1.5, 0, 1)    # open only while a mora sounds
    mid = gauss(np.clip((db - (top - 34)) / 12, 0, 1), 0.05)                # a faint tail of a note: smaller mouth
    amp = gate * (0.26 + 0.26 * mid + 0.48 * level_s)                       # a held note is never a slit
    squeeze = np.clip(sum(CGAIN[k] * Cs[k] for k in Cs), 0, 1)
    W = np.zeros((n, len(MOUTH)))
    for vi, v in enumerate("aiueo"):
        for name, g in VOWEL[v].items(): W[:, MOUTH.index(name)] += Vn[:, vi] * np.minimum(tot[:, 0], 1) * amp * g * (1 - 0.9 * squeeze)
    for k, d in CONS.items():
        for name, g in d.items(): W[:, MOUTH.index(name)] += Cs[k] * g * np.clip(gate * 2, 0, 1)
    breath = gauss(np.clip(level * 2.0, 0, 1) * (1 - np.clip(gauss(sounding, 0.06) * 1.6, 0, 1)), 0.07)
    W[:, MOUTH.index("Fcl_MTH_A")] += 0.30 * breath
    W = np.clip(W, 0, 1)

    # ---- face: small, slow, the model's own shapes
    rng = np.random.default_rng(5)
    phrase = gauss(level, 0.9); phrase = phrase / max(phrase.max(), 1e-6)   # phrase-level intensity 0..1
    open_slow = gauss(level, 0.45)
    drift = gauss(rng.standard_normal(n), 1.2); drift = drift / (np.abs(drift).max() + 1e-9)
    E = {}
    E["Fcl_BRW_Sorrow"] = np.clip(0.16 + 0.26 * phrase + 0.04 * drift, 0, 1)

    def curve(keys):
        out = np.zeros(n)
        for (ta, va), (tb, vb) in zip(keys[:-1], keys[1:]):
            m = (T >= ta) & (T <= tb); u = (T[m] - ta) / max(tb - ta, 1e-6); u = u * u * (3 - 2 * u); out[m] = va + (vb - va) * u
        out[T > keys[-1][0]] = keys[-1][1]; out[T < keys[0][0]] = keys[0][1]
        return out
    # gaze (deg; yaw + = her right, pitch + = down): mostly the camera, two look-aways
    fix = [(0.0, -2.0, 4.5), (2.3, 0.0, 0.5), (5.6, 1.2, 0.3), (8.35, -4.0, 3.5), (11.9, 0.0, 0.0), (14.3, 2.5, 3.0), (16.6, 0.0, 0.4), (19.2, -1.0, 0.2), (20.9, 0.0, 3.5), (21.65, 0.0, 0.4)]
    closed = [(6.85, 7.20, 7.95, 8.25), (15.50, 15.80, 16.30, 16.55)]           # (start closing, shut, start opening, open)
    fill = [1.2, 4.1, 10.3, 13.2, 18.1, 22.4]
    AUTO = len(sys.argv) > 6 and sys.argv[6] == "auto"
    if AUTO:
        dur = n / FPS; sing = gauss(sounding, 0.8) > 0.25                          # is she in a sung passage
        # closed-eye moments: on long held notes (a vowel held 1.3 s or more), at most one every 10 s
        closed = []
        for (c, v, t_on, t_v, t_end) in morae:
            if v and t_end - t_v >= 1.3 and (not closed or t_v - closed[-1][3] > 10.0):
                a = t_v + 0.20; b = min(t_end - 0.10, a + 1.7)
                if b - a > 0.7: closed.append((a, a + 0.32, b - 0.28, b))
        # fixations: mostly the camera; now and then a short look away (more often and longer between the sung passages)
        fix = [(0.0, 0.0, 3.5)]; t = 2.0
        while t < dur - 1.0:
            singing = bool(sing[min(n - 1, int(t * FPS))])
            if rng.random() < (0.22 if singing else 0.5):
                y = rng.choice([-1, 1]) * rng.uniform(2.5, 5.0); pch = rng.uniform(1.5, 4.0); fix.append((t, y, pch)); t += rng.uniform(1.2, 2.4) if singing else rng.uniform(2.0, 3.5)
                fix.append((t, rng.uniform(-0.8, 0.8), rng.uniform(0.0, 0.6)))
            else:
                fix.append((t, rng.uniform(-1.2, 1.2), rng.uniform(0.0, 0.6)))
            t += rng.uniform(2.8, 5.5)
        fill = list(np.arange(1.2, dur, 3.4) + rng.uniform(-0.6, 0.6, len(np.arange(1.2, dur, 3.4))))
    in_closed = lambda t: any(a - 0.3 < t < d + 0.15 for a, _, _, d in closed)
    # ---- emotions: brows / eye shape / eyelids / eye highlights / where the eyes rest
    EMO = {
        "calm":   dict(BRW_Sorrow=.12),
        "sad":    dict(BRW_Sorrow=.50, EYE_Sorrow=.45, lid=.08, gp=1.0),
        "plead":  dict(BRW_Sorrow=.72, EYE_Spread=.22, EYE_Sorrow=.25),
        "pain":   dict(BRW_Sorrow=.45, BRW_Angry=.40, EYE_Sorrow=.38, lid=.30),
        "anger":  dict(BRW_Angry=.75, EYE_Angry=.72, lid=.06),
        "fear":   dict(BRW_Sorrow=.50, BRW_Surprised=.38, EYE_Spread=.42, EYE_Surprised=.18),
        "empty":  dict(BRW_Sorrow=.10, lid=.24, hl=1.0, gp=2.5),
        "shut":   dict(BRW_Sorrow=.55, BRW_Angry=.25, lid=.92),
        "tender": dict(BRW_Sorrow=.30, EYE_Joy=.22, lid=.04),
        "joy":    dict(EYE_Joy=.42, lid=.02),                          # bright songs: smiling eyes
        "bright": dict(EYE_Joy=.18, EYE_Spread=.12, BRW_Surprised=.10),
    }
    emo_ch = {}
    if len(sys.argv) > 7:
        tl = sorted(json.load(open(sys.argv[7], encoding="utf-8"))["emotions"])
        keys_all = sorted({k for d in EMO.values() for k in d})
        raw = {k: np.zeros(n) for k in keys_all}
        for i, (t0, name, inten) in enumerate(tl):
            t1 = tl[i + 1][0] if i + 1 < len(tl) else n / FPS + 1; m = (T >= t0) & (T < t1)
            for k, v in EMO[name].items(): raw[k][m] = v * (inten if k not in ("hl",) else 1.0)
        emo_ch = {k: gauss(raw[k], 0.10 if k == "hl" else 0.30) for k in keys_all}       # about a second from one face to the next
        # a shut-eyed line: the eyes open again in the rests between its phrases? no - they stay shut, but only while she is singing it
        sing_s = np.clip(gauss(sounding, 0.35) * 1.4, 0, 1)
        f_shut = np.clip((emo_ch["lid"] - 0.30) / 0.30, 0, 1); f_shut = f_shut * f_shut * (3 - 2 * f_shut)
        emo_ch["lid"] = emo_ch["lid"] * (1 - f_shut) + (0.30 + (emo_ch["lid"] - 0.30) * sing_s) * f_shut
    gy = np.zeros(n); gp = np.zeros(n); shifts = []
    for i, (ta, y, pch) in enumerate(fix):
        tb = fix[i + 1][0] if i + 1 < len(fix) else n / FPS + 1
        m = T >= ta; gy[m] = y; gp[m] = pch
        if i > 0: shifts.append(ta)
        tt = ta + rng.uniform(0.9, 1.6)
        while tt < tb - 0.4:
            m = (T >= tt) & (T < tb); gy[m] = y + rng.uniform(-0.45, 0.45); gp[m] = pch + rng.uniform(-0.3, 0.3); tt += rng.uniform(0.9, 1.9)
    sac = np.array([0.1, 0.25, 0.3, 0.25, 0.1])
    gy = np.convolve(np.pad(gy, 2, mode="edge"), sac, "valid")[:n]; gp = np.convolve(np.pad(gp, 2, mode="edge"), sac, "valid")[:n]
    # eyelids: a relaxed base, two closed-eye moments on long notes, blinks (with every larger gaze shift and in between)
    if emo_ch: gp = gp + emo_ch["gp"]
    lid = 0.05 + 0.018 * np.clip(gp, 0, None) + 0.03 * phrase
    keys = [(0, 0)]
    for a, b, c2, d in closed: keys += [(a, 0), (b, .88), (c2, .88), (d, 0)]
    lid = np.maximum(lid, curve(keys + [(n / FPS + 1, 0)]))
    if emo_ch: lid = np.maximum(lid, emo_ch["lid"])
    blinks = [t + 0.03 for t in shifts if not in_closed(t)] + list(fill)
    blinks = sorted(float(b) for b in blinks if not in_closed(b))
    kept = []                                                             # at most one blink every 2 s (QA: 16 in 23 s was far too many)
    for b in blinks:
        if not kept or b - kept[-1] >= 2.0: kept.append(b)
    blinks = kept
    for bt in blinks:
        d = T - bt; m = (d >= 0) & (d <= 0.24)
        v = np.where(d < 0.07, d / 0.07, np.where(d < 0.10, 1.0, 1.0 - (np.clip(d - 0.10, 0, None) / 0.14) ** 0.8))
        lid[m] = np.maximum(lid[m], np.clip(v[m], 0, 1))
    E["Fcl_EYE_Close"] = np.clip(lid, 0, 1)
    if emo_ch:
        live = 0.80 + 0.20 * phrase                                              # the face is a little stronger where the voice is
        E["Fcl_BRW_Sorrow"] = np.clip(emo_ch["BRW_Sorrow"] * live + 0.04 * drift, 0, 1)
        for k in ("BRW_Angry", "BRW_Surprised"): E["Fcl_" + k] = np.clip(emo_ch[k] * live, 0, 1)
        openness = 1 - np.clip(E["Fcl_EYE_Close"] * 1.15, 0, 1)                  # a closing lid takes the eye shapes with it
        for k in ("EYE_Sorrow", "EYE_Angry", "EYE_Spread", "EYE_Surprised", "EYE_Joy"): E["Fcl_" + k] = np.clip(emo_ch[k] * live * openness, 0, 1)
        E["Fcl_EYE_Highlight_Hide"] = np.clip(emo_ch["hl"], 0, 1)
    names = MOUTH + list(E.keys())
    W = np.hstack([W, np.stack([E[k] for k in E], 1)])
    gy_s, gp_s = gauss(gy, 0.35), gauss(gp, 0.35)
    json.dump({"fps": FPS, "count": n, "names": names, "w": [round(float(v), 3) for v in W.ravel()],
               "open": [round(float(v), 3) for v in level_s], "openSlow": [round(float(v), 3) for v in open_slow],
               "breath": [round(float(v), 3) for v in np.clip(breath, 0, 1)],
               "gaze": [round(float(v), 2) for v in np.stack([gy, gp], 1).ravel()], "gazeSlow": [round(float(v), 2) for v in np.stack([gy_s, gp_s], 1).ravel()],
               "blinks": [round(b, 3) for b in blinks]}, open(dst, "w"), separators=(",", ":"))
    print(f"frames {n} ({n / FPS:.2f}s) morae {len(morae)} channels {len(names)} blinks {len(blinks)}  mouth open {np.mean(amp > 0.1) * 100:.0f}% of the time")

    if png and png != "-":
        from scipy.signal import resample_poly, stft
        import matplotlib; matplotlib.use("Agg"); import matplotlib.pyplot as plt
        plt.rcParams["font.family"] = ["Microsoft YaHei", "SimHei", "DejaVu Sans"]
        xs = resample_poly(x, 16000, sr); f, tt, Z = stft(xs, fs=16000, nperseg=400, noverlap=240, nfft=512, boundary=None, padded=False)
        S = 10 * np.log10(np.abs(Z) ** 2 + 1e-10)
        spans = [(2.4, 5.2), (5.0, 8.0), (8.4, 11.2), (11.6, 14.2), (14.4, 17.6), (17.4, 21.0), (21.4, 23.0)]
        col = {"a": "#ff5d5d", "i": "#5dd0ff", "u": "#b98cff", "e": "#7fe37f", "o": "#ffc14d"}
        fig, axes = plt.subplots(len(spans) * 2, 1, figsize=(22, 3.6 * len(spans)), dpi=80, gridspec_kw={"height_ratios": [3, 1.3] * len(spans)})
        for r, (a, b) in enumerate(spans):
            ax = axes[2 * r]; m = (tt >= a) & (tt <= b)
            ax.imshow(S[:128, m], origin="lower", aspect="auto", extent=[a, b, 0, f[127]], cmap="magma", vmin=np.percentile(S, 40), vmax=np.percentile(S, 99.7))
            for (c, v, t_on, t_v, t_end) in morae:
                if a - 0.05 <= t_on <= b:
                    ax.axvline(t_on, color="w", lw=1.2); ax.axvline(t_v, color=col.get(v, "w"), lw=1.0, ls="--"); ax.axvline(t_end, color="#888", lw=0.6, ls=":")
                    ax.text(t_on + 0.008, f[127] * 0.93, (c or "") + (v or ""), color="w", fontsize=14, va="top", fontweight="bold")
            ax.set_xlim(a, b); ax.set_xticks(np.arange(np.ceil(a * 10) / 10, b, 0.1)); ax.tick_params(labelsize=7); ax.set_ylabel("Hz")
            ax2 = axes[2 * r + 1]; mm = (T >= a) & (T <= b)
            for vi, v in enumerate("aiueo"):
                ax2.fill_between(T[mm], 0, (Vn[:, vi] * np.minimum(tot[:, 0], 1) * amp * (1 - 0.9 * squeeze))[mm], color=col[v], alpha=0.85, lw=0, label=v if r == 0 else None)
            ax2.plot(T[mm], Cs["PP"][mm], color="k", lw=1.5, label="lips shut (m b p)" if r == 0 else None)
            ax2.plot(T[mm], E["Fcl_EYE_Close"][mm], color="#444", lw=0.8, ls=":", label="eyelids" if r == 0 else None)
            ax2.set_xlim(a, b); ax2.set_ylim(0, 1.05); ax2.set_xticks(np.arange(np.ceil(a * 10) / 10, b, 0.1)); ax2.tick_params(labelsize=7)
            if r == 0: ax2.legend(ncol=8, fontsize=9, loc="upper right")
        fig.suptitle("top: vocal stem spectrogram with the mora marks (white = onset, dashed = vowel start, dotted = end)   bottom: the mouth track that will be played (colour = vowel, height = opening)", fontsize=12)
        fig.tight_layout(); fig.savefig(png); print("saved", png)


if __name__ == "__main__":
    main()
