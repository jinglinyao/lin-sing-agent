"""Adds the musical pulse to a face/body track: the body should move with the beat, most of all where nobody sings.

beats  - every second tracked beat (librosa on the instrumental stem): one body pulse per two beats (about 0.8 s at 148 BPM)
groove - per frame 0..1: how strongly to move = loudness of the instrumental (smoothed over 1.5 s), halved while she sings

usage: python add_groove.py <track.json> <no_vocals.wav> [beats.json]     (rewrites the track in place)
"""
import sys, json
import numpy as np
import soundfile as sf


def main():
    tp, wav = sys.argv[1], sys.argv[2]
    tr = json.load(open(tp)); fps, n = tr["fps"], tr["count"]
    if len(sys.argv) > 3: beats = np.array(json.load(open(sys.argv[3]))["beats"])
    else:
        import librosa
        y, sr = librosa.load(wav, sr=22050, mono=True); _, beats = librosa.beat.beat_track(y=y, sr=sr, units="time", tightness=100); beats = np.asarray(beats)
    # which half of the beats carries the pulse: the one with the stronger hits
    x, sr = sf.read(wav)
    if x.ndim > 1: x = x.mean(1)
    def hit(ts): return float(np.mean([np.sqrt(np.mean(x[int(t * sr):int(t * sr) + int(0.06 * sr)] ** 2)) for t in ts if t * sr + 0.06 * sr < len(x)]))
    start = 0 if hit(beats[0::2]) >= hit(beats[1::2]) else 1
    pulse = beats[start::2]
    h = int(sr / fps); m = min(n, len(x) // h)
    db = 20 * np.log10(np.sqrt(np.mean(x[:m * h].reshape(m, h) ** 2, axis=1)) + 1e-7); db = np.pad(db, (0, n - m), mode="edge")
    k = np.ones(int(1.5 * fps)) / int(1.5 * fps); dbs = np.convolve(np.pad(db, len(k) // 2, mode="edge"), k, "valid")[:n]
    hi = np.percentile(dbs, 90); g = np.clip((dbs - (hi - 18)) / 14, 0, 1)
    sing = np.array(tr["open"]) > 0.12; k2 = np.ones(fps) / fps; sing_s = np.convolve(np.pad(sing.astype(float), len(k2) // 2, mode="edge"), k2, "valid")[:n]
    g = g * (1 - 0.5 * np.clip(sing_s * 1.5, 0, 1))
    tr["beats"] = [round(float(b), 3) for b in pulse]; tr["groove"] = [round(float(v), 2) for v in g]
    json.dump(tr, open(tp, "w"), separators=(",", ":"))
    print(f"pulses {len(pulse)}, mean period {np.mean(np.diff(pulse)):.3f} s (beat {np.mean(np.diff(beats)):.3f} s), groove mean {g.mean():.2f}, >0.5 for {np.mean(g > 0.5) * 100:.0f}% of the clip")


if __name__ == "__main__":
    main()
