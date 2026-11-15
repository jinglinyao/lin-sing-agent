"""Read a song before cutting to it: tempo, beat grid, bars, where the arrangement changes.

usage: song_structure.py <mix.wav> <no_vocals.wav> <vocals.wav> <lyrics.json> <out.json> [t0 t1]

Prints (and saves) for the range: tempo, the bar grid (downbeat phase chosen by where the low end - kick, bass - hits hardest), and per bar:
loudness of the band (accompaniment) in low / mid / high bands, drum activity (broadband onsets), whether she sings, the lyric section,
and the change against the bar before (an arrangement event: instruments entering or leaving, a fill, a stop).
"""
import sys, json
import numpy as np
import librosa

mix_p, inst_p, voc_p, lyr_p, out_p = sys.argv[1:6]; t0 = float(sys.argv[6]) if len(sys.argv) > 6 else 0.0; t1 = float(sys.argv[7]) if len(sys.argv) > 7 else 1e9
sr = 22050; hop = 512
y, _ = librosa.load(mix_p, sr=sr, mono=True); yi, _ = librosa.load(inst_p, sr=sr, mono=True); yv, _ = librosa.load(voc_p, sr=sr, mono=True)
dur = len(y) / sr
oenv = librosa.onset.onset_strength(y=yi, sr=sr, hop_length=hop)
# tempo: candidates from the autocorrelation of the onset envelope; beats tracked at the strongest plausible one
tempo0, beats0 = librosa.beat.beat_track(onset_envelope=oenv, sr=sr, hop_length=hop, units="time")
tempo0 = float(np.atleast_1d(tempo0)[0])
ac = librosa.autocorrelate(oenv, max_size=int(4 * sr / hop)); lags = np.arange(1, len(ac)) * hop / sr
cand = []
for bpm in np.arange(50, 220, 0.5):
    lag = 60.0 / bpm; i = int(round(lag * sr / hop))
    if 1 <= i < len(ac): cand.append((ac[i], bpm))
cand.sort(reverse=True)
print("duration %.1f s; beat tracker says %.1f BPM; strongest periodicities:" % (dur, tempo0), ", ".join("%.1f" % b for a, b in cand[:6]))
# beat grid at the tracker tempo, and at double / half if that fits the hi-hat / kick pattern better
def grid_score(bpm):
    _, b = librosa.beat.beat_track(onset_envelope=oenv, sr=sr, hop_length=hop, units="time", start_bpm=bpm, tightness=200)
    iv = np.diff(b); return b, 60.0 / np.median(iv), np.std(iv) / np.median(iv)
best = None
for bpm in (tempo0, tempo0 * 2, tempo0 / 2):
    if 60 <= bpm <= 200:
        b, real, jit = grid_score(bpm); print("  grid tried at %.1f -> %.1f BPM, %d beats, interval jitter %.1f %%" % (bpm, real, len(b), jit * 100))
        if best is None or abs(real - 110) < abs(best[1] - 110): best = (b, real, jit)
beats, bpm, _ = best; beat_len = 60.0 / bpm
# band energies of the accompaniment per beat
S = np.abs(librosa.stft(yi, n_fft=2048, hop_length=hop)) ** 2; freqs = librosa.fft_frequencies(sr=sr, n_fft=2048); ft = librosa.frames_to_time(np.arange(S.shape[1]), sr=sr, hop_length=hop)
def band(lo, hi): return S[(freqs >= lo) & (freqs < hi)].sum(0)
low, mid, high = band(30, 200), band(200, 2000), band(4000, 11000)
Sv = np.abs(librosa.stft(yv, n_fft=2048, hop_length=hop)) ** 2; voc = Sv.sum(0)
def per(x, a, b): m = (ft >= a) & (ft < b); return float(x[m].mean()) if m.any() else 0.0
# downbeat: 4/4 assumed; the phase whose "ones" carry the most low-end attack
lo_on = librosa.onset.onset_strength(S=librosa.power_to_db(S[(freqs >= 30) & (freqs < 200)] + 1e-9), sr=sr, hop_length=hop)
def at(x, t): return x[np.clip(np.searchsorted(ft, t), 0, len(x) - 1)]
ph = [np.mean([max(at(lo_on, beats[i] + d) for d in (-0.03, 0, 0.03)) for i in range(p, len(beats), 4)]) for p in range(4)]
phase = int(np.argmax(ph)); print("meter 4/4 assumed; low-end attack by beat-of-bar phase:", [round(float(v), 2) for v in ph], "-> downbeats at phase", phase)
bars = [(beats[i], beats[i + 4] if i + 4 < len(beats) else beats[-1] + beat_len) for i in range(phase, len(beats) - 1, 4)]
lyr = json.load(open(lyr_p, encoding="utf-8"))
ref = {k: np.percentile(v, 95) for k, v in (("low", low), ("mid", mid), ("high", high), ("voc", voc))}
drum = librosa.onset.onset_strength(S=librosa.power_to_db(S[(freqs >= 2000)] + 1e-9), sr=sr, hop_length=hop)
rows = []
for n, (a, b) in enumerate(bars):
    sec = sorted({l.get("sec", "") for l in lyr if l["t"] < b and l["end"] > a})
    r = dict(bar=n + 1, t=round(float(a), 2), end=round(float(b), 2), low=per(low, a, b) / ref["low"], mid=per(mid, a, b) / ref["mid"], high=per(high, a, b) / ref["high"], voc=per(voc, a, b) / ref["voc"],
             drum=per(drum, a, b), sec="/".join(sec))
    r["total"] = (r["low"] + r["mid"] + r["high"]) / 3; rows.append(r)
dref = np.percentile([r["drum"] for r in rows], 90)
for i, r in enumerate(rows):
    r["drum"] = r["drum"] / dref; p = rows[i - 1] if i else r
    r["change"] = round(float(abs(r["low"] - p["low"]) + abs(r["mid"] - p["mid"]) + abs(r["high"] - p["high"]) + abs(r["drum"] - p["drum"])), 2)
print("tempo %.1f BPM, beat %.3f s, bar %.3f s; first downbeat %.2f s; %d bars" % (bpm, beat_len, 4 * beat_len, bars[0][0], len(bars)))
print(" bar     time    low  mid  high drum  vocal  change  section")
for r in rows:
    if r["end"] < t0 or r["t"] > t1: continue
    bar_s = lambda v: "#" * int(round(min(1.2, v) * 5))
    print("%4d  %6.2f  %-6s %-6s %-6s %-6s %-6s %5.2f  %s" % (r["bar"], r["t"], bar_s(r["low"]), bar_s(r["mid"]), bar_s(r["high"]), bar_s(r["drum"]), bar_s(r["voc"]), r["change"], r["sec"]))
json.dump(dict(bpm=float(bpm), beat=float(beat_len), beats=[round(float(b), 3) for b in beats], phase=phase, bars=[{k: (v if isinstance(v, (str, int)) else round(float(v), 3)) for k, v in r.items()} for r in rows]), open(out_p, "w"), indent=0)
