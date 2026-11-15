"""Numbers for how smooth the baked body is (key.bin): wrist speed / acceleration / jerk spikes, how long the hands stand still,
torso and head angular speed.   usage: python tools/motion_stats.py <song folder> [t0 t1]"""
import sys, os, json
import numpy as np
F = sys.argv[1]; b = np.fromfile(os.path.join(F, "key.bin"), np.float32)
fps, n, nch, t0 = int(b[0]), int(b[1]), int(b[2]), float(b[3]); d = b[4:4 + n * nch].reshape(n, nch).astype(float); t = t0 + np.arange(n) / fps
a, z = (float(sys.argv[2]), float(sys.argv[3])) if len(sys.argv) > 3 else (t[0], t[-1]); m = (t >= a) & (t <= z)
def deriv(x, k=1):
    for _ in range(k): x = np.gradient(x, 1.0 / fps, axis=0)
    return x
out = {}
for side, c in (("R", 14), ("L", 32)):
    p = d[:, c:c + 3]; v = np.linalg.norm(deriv(p), axis=1); acc = np.linalg.norm(deriv(p, 2), axis=1); jerk = np.linalg.norm(deriv(p, 3), axis=1)
    still = v < 0.02
    runs = np.diff(np.flatnonzero(np.diff(np.r_[0, still[m].astype(int), 0]))) [::2] / fps if still[m].any() else np.array([0.0])
    out[side] = dict(v_p95=round(float(np.percentile(v[m], 95)), 3), v_max=round(float(v[m].max()), 3), acc_p99=round(float(np.percentile(acc[m], 99)), 2),
                     jerk_p99=round(float(np.percentile(jerk[m], 99)), 1), still_share=round(float(still[m].mean()), 3), still_longest=round(float(runs.max()), 2))
for name, c in (("torso", 6), ("head", 9)):
    w = np.linalg.norm(deriv(d[:, c:c + 3]), axis=1); out[name] = dict(deg_s_p95=round(float(np.percentile(w[m], 95)), 1), deg_s_max=round(float(w[m].max()), 1),
                                                                         acc_p99=round(float(np.percentile(np.linalg.norm(deriv(d[:, c:c + 3], 2), axis=1)[m], 99)), 1))
print(json.dumps(out))
