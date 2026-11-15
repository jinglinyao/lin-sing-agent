using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The gesture timeline, simulated once and then sampled (see GestureMotion.cs for why).
///
/// For every hand each key of the schedule starts a new REFERENCE move from wherever the reference is at that moment:
///     wind-up (a small bump away from the goal) -> stroke (minimum-jerk profile, a little front-loaded; its duration
///     grows with the distance, the way human reaches do) -> a small overshoot that falls back -> hold.
/// Three clocks per hand: wrist position / elbow first, hand orientation 40 ms later, fingers 90 ms later; the left
/// hand runs 70 ms behind the right. The reference is then followed by a critically damped second-order filter, so the
/// result is continuous in position AND velocity whatever the schedule does.
/// Torso and head parameters of the gestures are simulated the same way but start 120 ms before the hands.
/// A second pass derives what the body does BECAUSE of the hands (looking 0.28 s ahead, then smoothed, so the torso
/// starts first and moves more slowly than the hand): chest turn towards the reaching side, forward lean, side bend
/// under a raised arm, pelvis counter-shift for balance, shoulder girdle forward / up.
/// Wrist positions are stored in character space for a torso at rest; at run time they ride on their anchors.
/// </summary>
public partial class SingDriver
{
    const float SimDt = 1f / 120f;
    const int HN = 24;   // simulated hand channels: 0-2 wrist, 3-6 anchor weights, 7-9 pole, 10-12 fingers dir, 13-15 palm dir, 16-20 curls, 21 spread, 22 beat gain, 23 wave
    const int HS = 30;   // stored: + 24-26 wrist velocity, 27-29 drag (what the hand trails behind: velocity through a springy filter)
    const int BS = 8;    // lean, yaw, nod, tilt, shrug, headYaw, -, -
    const int RS = 12;   // reactive: yaw, lean, roll, hipX, hipZ, protraction R, L, lift R, L, head yaw, head pitch, -
    static readonly int[] Grp = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 0, 0 };

    [SerializeField] Vector3 restHipsPos;
    [SerializeField] bool hasRestHips;

    class SimBake
    {
        public int hash, n; public float t0;
        public float[] R, L, body, react;
        public Vector3[] anchorRef = new Vector3[8];          // character space, torso at rest: [anchor + 4 * side]
        public Vector3 hipsRef, neckRef; public Vector3[] restPos = new Vector3[2];
        public float[] corr; public int cn; public const float CorrDt = 1f / 40f;      // contact corrections per hand: wrist offset (3), finger direction offset (3)
        public void SampleCorr(float t, float[] dst)
        {
            float f = Mathf.Clamp((t - t0) / CorrDt, 0, cn - 1); int i = (int)f, j = Math.Min(i + 1, cn - 1); float k = f - i;
            for (int c = 0; c < 12; c++) dst[c] = corr[i * 12 + c] + (corr[j * 12 + c] - corr[i * 12 + c]) * k;
        }
        public float[] armCorr;                                // recorded arms: what the contact solver did to each (wrist 3, finger direction 3, elbow side 3), smoothed in time
        public void SampleArm(float t, float[] dst)
        {
            float f = Mathf.Clamp((t - t0) / CorrDt, 0, cn - 1); int i = (int)f, j = Math.Min(i + 1, cn - 1); float k = f - i;
            for (int c = 0; c < 18; c++) dst[c] = armCorr[i * 18 + c] + (armCorr[j * 18 + c] - armCorr[i * 18 + c]) * k;
        }
        public float[] blinks = new float[0];                  // extra blinks: people tend to blink as a gesture starts
        public Vector3[] footPos = new Vector3[2]; public Quaternion[] footRot = new Quaternion[2]; public float[] legA = new float[2], legB = new float[2];
        public void Sample(float[] a, int stride, float t, float[] dst)
        {
            float f = Mathf.Clamp((t - t0) / SimDt, 0, n - 1); int i = (int)f, j = Math.Min(i + 1, n - 1); float k = f - i;
            for (int c = 0; c < stride; c++) dst[c] = a[i * stride + c] + (a[j * stride + c] - a[i * stride + c]) * k;
        }
    }
    [NonSerialized] SimBake bake;

    public void InvalidateBake() { bake = null; }

    struct Key { public float t; public Gesture g; public bool release; public float gain; }     // gain: "name+" = emphatic (reaches 15 % further), "name-" = subdued

    int ScheduleHash()
    {
        unchecked
        {
            int h = 17; int n = gestureNames != null ? gestureNames.Length : 0;
            for (int i = 0; i < n; i++) h = ((h * 31 + gestureNames[i].GetHashCode()) * 31 + gestureStart[i].GetHashCode()) * 31 + gestureDur[i].GetHashCode();
            h = h * 31 + (restPose ?? "").GetHashCode();
            h = ((h * 31 + anticipation.GetHashCode()) * 31 + overshoot.GetHashCode()) * 31 + arcs.GetHashCode();
            h = ((h * 31 + micro.GetHashCode()) * 31 + torso.GetHashCode()) * 31 + motion.GetHashCode();
            h = (h * 31 + grooveGain.GetHashCode()) * 31 + weightShift.GetHashCode();
            h = h * 31 + BaseHash();
            h = h * 31 + (lipJson != null ? lipJson.GetInstanceID() : 0) + (plantFeet ? 7 : 0) + (avoidBody ? 13 : 0);
            return h * 31 + (hasRestHips ? 1 : 0);
        }
    }

    SimBake EnsureBake()
    {
        int h = ScheduleHash();
        if (bake == null || bake.hash != h) { bake = Bake(h); if (avoidBody) { BakeContacts(bake); BakeClipArms(bake); } }
        return bake;
    }

    List<Key> BuildKeys(Gesture restG)
    {
        var keys = new List<Key>();
        int n = gestureNames != null ? gestureNames.Length : 0;
        for (int i = 0; i < n; i++)
        {
            var g = Lookup(gestureNames[i]); if (g == null) continue;
            float start = gestureStart[i], end = start + gestureDur[i];
            string nm0 = gestureNames[i].TrimEnd('~');
            keys.Add(new Key { t = start, g = g, release = false, gain = nm0.EndsWith("+") ? 1.15f : nm0.EndsWith("-") ? 0.88f : 1f });
            // back to rest unless another gesture takes over around the end of this one
            bool chained = false;
            for (int j = 0; j < n; j++) if (j != i && gestureStart[j] > start && gestureStart[j] >= end - 0.90f && gestureStart[j] <= end + 0.40f) chained = true;
            if (!chained) keys.Add(new Key { t = end - 0.75f, g = restG, release = true, gain = 1f });
        }
        keys.Sort((a, b) => a.t.CompareTo(b.t));
        return keys;
    }

    /// <summary>The hand that holds the microphone, built on the microphone's axis (character space; sx = +1 right hand, -1 left): grip centre G on
    /// the axis; palm normal n (towards the axis) and finger direction f (across it) start as in the library pose - n from her side, f up and
    /// forward - and are turned together about the axis by micGripRoll, which carries the wrist (G - n * 3.2 cm - f * 6.6 cm) from below-behind
    /// the microphone round to its side. The knuckles then lie 3.2 cm off the axis and the fingers close ROUND the body.</summary>
    public void MicGripPose(float sx, out Vector3 pos, out Vector3 fin, out Vector3 palm)
    {
        Vector3 d = micDir.normalized, n0 = Vector3.left, f0 = Vector3.Cross(d, n0).normalized; if (f0.y < 0) f0 = -f0;
        Quaternion q = Quaternion.AngleAxis(micGripRoll, d); if ((q * n0).y > 0f) q = Quaternion.AngleAxis(-micGripRoll, d);
        Vector3 n = q * n0, f = q * f0, w = micPos + d * micGrip - n * 0.032f - f * 0.066f;
        pos = new Vector3(micPos.x + (w.x - micPos.x) * sx, w.y, w.z); fin = new Vector3(f.x * sx, f.y, f.z); palm = new Vector3(n.x * sx, n.y, n.z);
    }

    /// <summary>A hand on the stand, 18 cm below the clip. The pole is upright; the hand takes it from its own side like a handrail: palm to the
    /// pole, fingers forward and a little up (they close round the front of it), thumb on top. Character space.</summary>
    public void PoleGripPose(float sx, out Vector3 pos, out Vector3 fin, out Vector3 palm)
    {
        Vector3 P = MicPolePoint; P.y -= 0.180f;
        palm = new Vector3(-sx, 0f, 0f); fin = new Vector3(0f, 0.25f, 0.97f).normalized; pos = P - palm * 0.031f - fin * 0.066f;
    }

    float[] HandVec(Gesture g, Gesture restG, bool right, SimBake bk, float armScale)
    {
        HandPose hp = right ? (g.R ?? restG.R) : (g.L ?? restG.L);
        float sx = right ? 1f : -1f; int side = right ? 0 : 1; var v = new float[HN];
        bool atMic = hp.anchor == Anchor.Mic;
        Vector3 pos = atMic ? micPos + new Vector3(hp.pos.x * sx, hp.pos.y, hp.pos.z) : bk.anchorRef[(int)hp.anchor + 4 * side] + new Vector3(hp.pos.x * sx, hp.pos.y, hp.pos.z) * armScale;
        Vector3 pole = new Vector3(hp.pole.x * sx, hp.pole.y, hp.pole.z).normalized, fin = new Vector3(hp.finger.x * sx, hp.finger.y, hp.finger.z).normalized, palm = new Vector3(hp.palm.x * sx, hp.palm.y, hp.palm.z).normalized;
        if (atMic)
        {
            // the grip poses were made for one tilt of the microphone: they turn with the microphone and slide to where this one is held
            Quaternion mr = MicRot;
            pos = micPos + mr * new Vector3(hp.pos.x * sx, hp.pos.y, hp.pos.z) + micDir.normalized * (micGrip - 0.090f); fin = mr * fin; palm = mr * palm;
            if (micGripRoll > 0.5f && hp.pos.y < -0.20f) PoleGripPose(sx, out pos, out fin, out palm);      // a pose far down from the grille is a hand on the STAND (with the turned grip every microphone pose used to land on the one grip: two hands in one place)
            else if (micGripRoll > 0.5f) { MicGripPose(sx, out pos, out fin, out palm); pos += new Vector3(0.90f * sx, -0.10f, 0.42f) * (hp.pos.x - 0.032f); }      // (a library pose further out than the grip stands that much off the microphone on her own side, level with it: backing off along the palm's normal brought the hand in from above and in front - across her chin for the lens)
            else if (micGripOver)
            {
                // grip centre G on the axis; the palm lies on the body from above and in front (its normal n is square to the axis, pointing down and
                // back at her), the fingers run across it towards the other side (f); the wrist sits behind the knuckles: G - n * 3.2 cm - f * 6.6 cm
                Vector3 d = micDir.normalized, n = Vector3.Cross(Vector3.right, d).normalized; if (n.y > 0) n = -n;
                fin = new Vector3(-sx, 0f, 0f); palm = n; pos = micPos + d * micGrip - n * 0.032f - fin * 0.066f;
            }
            if (micElbow.sqrMagnitude > 1e-6f) pole = new Vector3(micElbow.x * sx, micElbow.y, micElbow.z).normalized;
        }
        v[0] = pos.x; v[1] = pos.y; v[2] = pos.z; if (!atMic) v[3 + (int)hp.anchor] = 1f;     // a hand on the microphone rides on no body anchor: it stays put while the body sways
        v[7] = pole.x; v[8] = pole.y; v[9] = pole.z; v[10] = fin.x; v[11] = fin.y; v[12] = fin.z; v[13] = palm.x; v[14] = palm.y; v[15] = palm.z;
        for (int i = 0; i < 5; i++) v[16 + i] = hp.curl[i];
        v[21] = hp.spread; v[22] = g.beat; v[23] = g.wave;
        return v;
    }

    /// <summary>No two hands and no two repetitions are identical: a few millimetres and degrees of difference per key, more on the left hand.
    /// Poses that touch the body (anchored to chest, head, hips) are varied much less so they stay where they were fitted.</summary>
    void Vary(float[] v, float[] restVec, int k, bool right, Key key)
    {
        if (key.release) return;
        float dx = v[0] - restVec[0], dy = v[1] - restVec[1], dzz = v[2] - restVec[2];
        if (dx * dx + dy * dy + dzz * dzz < 0.02f * 0.02f) return;                                // this hand rests in this gesture
        HandPose hp = right ? (key.g.R ?? key.g.L) : (key.g.L ?? key.g.R);
        float amt = (hp != null && hp.anchor == Anchor.Shoulder ? 1f : hp != null && hp.anchor == Anchor.Mic ? 0.1f : 0.3f) * (right ? 0.6f : 1f);
        if (v[3] + v[4] + v[5] + v[6] < 0.5f) amt = 0.1f * (right ? 0.6f : 1f);                    // (composite gestures: tell a microphone grip by its missing anchor)
        int s = k * 29 + (right ? 0 : 1000);
        v[0] += Hash(s + 1) * 0.012f * amt; v[1] += Hash(s + 2) * 0.018f * amt; v[2] += Hash(s + 3) * 0.012f * amt;
        Quaternion q = Quaternion.Euler(Hash(s + 4) * 6f * amt, Hash(s + 5) * 6f * amt, Hash(s + 6) * 5f * amt);
        Vector3 fin = q * new Vector3(v[10], v[11], v[12]), palm = q * new Vector3(v[13], v[14], v[15]);
        v[10] = fin.x; v[11] = fin.y; v[12] = fin.z; v[13] = palm.x; v[14] = palm.y; v[15] = palm.z;
        for (int i = 0; i < 5; i++) if (v[16 + i] > 0.08f && v[16 + i] < 0.90f) v[16 + i] = Mathf.Clamp01(v[16 + i] + Hash(s + 7 + i) * 0.05f * amt);
    }

    /// <summary>The same pose, larger or smaller: how far the wrist goes from where it rests. Only for free-space poses (shoulder anchor), never for poses fitted to the body.</summary>
    static void Emphasis(float[] v, float[] restVec, float gain)
    {
        if (gain == 1f || v[3] < 0.5f) return;
        for (int c = 0; c < 3; c++) v[c] = restVec[c] + (v[c] - restVec[c]) * gain;
    }

    static float MinJerk(float u) { u = Mathf.Clamp01(u); return u * u * u * (u * (u * 6f - 15f) + 10f); }
    static float Sin2(float x) { float s = Mathf.Sin(x); return s * s; }

    /// <summary>Progress of one move at s seconds after its key: wind-up bump, stroke, overshoot bump. w = stroke progress alone (for the arc).</summary>
    static float MoveProfile(float s, float D, float Tw, float a, float o, float To, out float w, float relax = 0)
    {
        w = 0; if (s <= 0) return 0;
        w = MinJerk(Mathf.Pow(Mathf.Clamp01((s - Tw) / D), 0.80f));
        float e = w, Ta = 1.7f * Tw;
        if (a > 0 && s < Ta) e -= a * Sin2(Mathf.PI * s / Ta);
        float uo = (s - Tw - 0.80f * D) / To;
        if (o > 0 && uo > 0 && uo < 1) e += o * Sin2(Mathf.PI * Mathf.Pow(uo, 0.6f));
        if (relax > 0) e -= relax * MinJerk((s - Tw - D - 0.25f) / 1.6f);              // nobody holds a gesture at full tension: it sinks back a few percent
        return e;
    }

    void SimHand(SimBake bk, List<Key> keys, float[][] targets, float[] restVec, bool right, float[] dst, float[] keyD)
    {
        float Lag(int k) => right ? 0f : 0.070f + 0.045f * Hash(k * 13 + 5);   // two hands never move as one, and never by the same amount twice
        float[] dz = new float[3], dzd = new float[3];
        float[] offs = { -0.055f, 0.015f, 0.045f };                        // wrist, hand orientation (+40 ms), fingers (+90 ms); -55 ms makes up for the filter
        float[] fHz = { 5.5f, 5.0f, 4.0f };
        int[] cur = { -1, -1, -1 }; float[] ts = new float[3], D = { 1, 1, 1 }, Tw = new float[3], aa = new float[3], oo = new float[3], To = { 1, 1, 1 }, rx = new float[3];
        float arcL = 0; var keyL = new float[keys.Count];
        float[] from = (float[])restVec.Clone(), to = (float[])restVec.Clone(), x = (float[])restVec.Clone(), y = (float[])restVec.Clone(), yd = new float[HN];
        Vector3 arcDir = new Vector3(0, 0.11f, 0.06f) * arcs; float[] px = { restVec[0], restVec[1], restVec[2] };
        for (int i = 0; i < bk.n; i++)
        {
            float t = bk.t0 + i * SimDt;
            for (int g = 0; g < 3; g++)
            {
                while (cur[g] + 1 < keys.Count && keys[cur[g] + 1].t + Lag(cur[g] + 1) + offs[g] <= t)
                {
                    int k = ++cur[g]; var key = keys[k]; ts[g] = key.t + Lag(k) + offs[g];
                    for (int c = 0; c < HN; c++) if (Grp[c] == g) { from[c] = x[c]; to[c] = targets[k][c]; }
                    if (g == 0) { keyL[k] = Mathf.Sqrt((to[0] - from[0]) * (to[0] - from[0]) + (to[1] - from[1]) * (to[1] - from[1]) + (to[2] - from[2]) * (to[2] - from[2])); arcL = keyL[k]; }
                    float L = keyL[k], snap = key.g.snap;
                    rx[g] = key.release || g == 2 ? 0f : 0.04f;
                    if (key.release) { D[g] = Mathf.Max(0.60f, 0.50f + 0.60f * L); Tw[g] = 0; aa[g] = 0; oo[g] = 0.03f * overshoot; To[g] = 0.75f; }
                    else
                    {
                        D[g] = Mathf.Max(0.30f, (0.36f + 0.50f * L) * (1.22f - 0.45f * snap));                 // longer reach, longer move; sharp gestures are quicker
                        Tw[g] = 0.13f + 0.09f * (1f - snap); aa[g] = (0.035f + 0.045f * snap) * anticipation;
                        oo[g] = (0.012f + 0.075f * snap) * overshoot; To[g] = 0.30f + 0.22f * (1f - snap);
                    }
                    if (g == 0) keyD[k] = Mathf.Max(keyD[k], D[g]);
                    if (g == 1) { aa[g] *= 1.2f; D[g] *= 1.12f; }
                    if (g == 2) { aa[g] = 0; oo[g] = 0; D[g] *= 0.9f; }
                }
                if (cur[g] < 0) continue;
                float e = MoveProfile(t - ts[g], D[g], Tw[g], aa[g], oo[g], To[g], out float w, rx[g]);
                for (int c = 0; c < HN; c++) if (Grp[c] == g) x[c] = from[c] + (to[c] - from[c]) * e;
                if (g == 2) for (int c = 17; c <= 20; c++) x[c] = from[c] + (to[c] - from[c]) * MoveProfile(t - ts[g] - 0.028f * (c - 16), D[g], Tw[g], 0, 0, 1, out _);
                if (g == 0)
                {
                    float bulge = Mathf.Sin(Mathf.PI * w) * arcL; x[1] += arcDir.y * bulge; x[2] += arcDir.z * bulge;   // the wrist travels on an arc, up and away from the body
                    // ... and around the body, never through it: push the path out of a fattened body (wrist + cuff) before smoothing
                    Vector3 p = new Vector3(x[0], x[1], x[2]);
                    float pr = (0.068f + 0.022f * Mathf.Sin(Mathf.PI * w)) * torsoScale;                 // a wider berth while travelling
                    micAvoid = Mathf.SmoothStep(0f, 1f, (x[3] + x[4] + x[5] + x[6] - 0.55f) / 0.45f); bodyAvoidNow = Mathf.Lerp(0.1f, 1f, Mathf.Clamp01(x[3] + x[4] + x[5] + x[6]));
                    for (int it = 0; it < 3; it++) p += BodyPush(p, pr, out _);
                    px[0] = p.x; px[1] = p.y; px[2] = p.z;
                }
            }
            for (int c = 0; c < HN; c++)
            {
                float f = fHz[Grp[c]], k1 = 1f / (Mathf.PI * f), k2 = 1f / (4f * Mathf.PI * Mathf.PI * f * f);
                float xc = c < 3 ? px[c] : x[c];
                y[c] += SimDt * yd[c]; yd[c] += SimDt * (xc - y[c] - k1 * yd[c]) / k2;
                dst[i * HS + c] = y[c];
            }
            dst[i * HS + 24] = yd[0]; dst[i * HS + 25] = yd[1]; dst[i * HS + 26] = yd[2];
            // drag: the hand hangs back while the wrist travels and swings through when it stops (under-damped follower of the velocity)
            for (int c = 0; c < 3; c++)
            {
                const float f = 2.6f, z = 0.45f, k1 = z / (Mathf.PI * f), k2 = 1f / (4f * Mathf.PI * Mathf.PI * f * f);
                dz[c] += SimDt * dzd[c]; dzd[c] += SimDt * (yd[c] - dz[c] - k1 * dzd[c]) / k2; dst[i * HS + 27 + c] = dz[c];
            }
        }
    }

    void SimBody(SimBake bk, List<Key> keys, float[] keyD)
    {
        const int NB = 6; const float f = 3.0f, k1 = 1f / (Mathf.PI * f), k2 = 1f / (4f * Mathf.PI * Mathf.PI * f * f);
        float[] from = new float[NB], to = new float[NB], x = new float[NB], y = new float[NB], yd = new float[NB];
        int cur = -1; float ts = 0, D = 1;
        for (int i = 0; i < bk.n; i++)
        {
            float t = bk.t0 + i * SimDt;
            while (cur + 1 < keys.Count && keys[cur + 1].t - 0.22f <= t)                               // 120 ms ahead of the hands (+100 ms for the filter)
            {
                int k = ++cur; var g = keys[k].g; ts = keys[k].t - 0.22f;
                float[] tv = { g.lean, g.yaw, g.nod, g.tilt, g.shrug * 10f, g.headYaw };
                float ang = 0; for (int c = 0; c < NB; c++) { from[c] = x[c]; to[c] = tv[c]; ang = Mathf.Max(ang, Mathf.Abs(to[c] - from[c])); }
                D = Mathf.Max(1.25f * keyD[k], 0.45f + 0.035f * ang);                                    // a big change of posture takes its time
            }
            if (cur >= 0) { float e = MoveProfile(t - ts, D, 0, 0, 0, 1, out _); for (int c = 0; c < NB; c++) x[c] = from[c] + (to[c] - from[c]) * e; }
            for (int c = 0; c < NB; c++) { y[c] += SimDt * yd[c]; yd[c] += SimDt * (x[c] - y[c] - k1 * yd[c]) / k2; bk.body[i * BS + c] = c == 4 ? y[c] / 10f : y[c]; }
        }
    }

    /// <summary>What the torso, pelvis and shoulder girdle do because of where the hands are going.</summary>
    void SimReact(SimBake bk, float[] restR, float[] restL, float armLen)
    {
        const float f = 1.5f, k1 = 1f / (Mathf.PI * f), k2 = 1f / (4f * Mathf.PI * Mathf.PI * f * f);
        int lead = Mathf.RoundToInt(0.28f / SimDt);
        var x = new float[RS]; var y = new float[RS]; var yd = new float[RS];
        Vector3 shR = bk.anchorRef[0], shL = bk.anchorRef[4], chest = bk.anchorRef[1];
        Vector3 r0 = new Vector3(restR[0], restR[1], restR[2]), l0 = new Vector3(restL[0], restL[1], restL[2]);
        for (int i = 0; i < bk.n; i++)
        {
            int j = Math.Min(bk.n - 1, i + lead);
            Vector3 pR = new Vector3(bk.R[j * HS], bk.R[j * HS + 1], bk.R[j * HS + 2]), pL = new Vector3(bk.L[j * HS], bk.L[j * HS + 1], bk.L[j * HS + 2]);
            Vector3 dR = pR - r0, dL = pL - l0;
            x[0] = Mathf.Clamp(-11f * (dR.z - dL.z) + 7f * (dR.x + dL.x), -7f, 7f);                                                    // chest turns: the working shoulder comes forward
            x[1] = Mathf.Clamp(3.5f * (dR.z + dL.z) - 6f * (Mathf.Max(0, pR.y - shR.y + 0.05f) + Mathf.Max(0, pL.y - shL.y + 0.05f)), -4f, 4f);   // leans into a reach, opens up under raised arms
            x[2] = Mathf.Clamp(5f * (Mathf.Max(0, pR.y - chest.y) - Mathf.Max(0, pL.y - chest.y)), -3.5f, 3.5f);                      // side bend away from a raised arm
            x[3] = -0.045f * (dR.x + dL.x); x[4] = -0.045f * (dR.z + dL.z);                                                           // pelvis goes the other way: balance
            x[5] = 11f * Mathf.Clamp01(((pR.z - shR.z) / armLen - 0.25f) / 0.6f) + 6f * Mathf.Clamp01((shR.x - pR.x) / 0.2f);         // shoulder girdle forward on a reach / across the body
            x[6] = 11f * Mathf.Clamp01(((pL.z - shL.z) / armLen - 0.25f) / 0.6f) + 6f * Mathf.Clamp01((pL.x - shL.x) / 0.2f);
            x[7] = Mathf.Clamp01((pR.y - shR.y + 0.06f) / 0.30f); x[8] = Mathf.Clamp01((pL.y - shL.y + 0.06f) / 0.30f);                // ... and up with a raised arm
            x[9] = Mathf.Clamp(9f * (dR.x + dL.x), -5f, 5f);                                                                          // the head turns a little towards the working hand
            x[10] = Mathf.Clamp(-8f * (Mathf.Max(0, pR.y - shR.y) + Mathf.Max(0, pL.y - shL.y)) + 5f * Mathf.Max(0, Mathf.Min(dR.z, dL.z)), -5f, 4f);   // up with a raised arm, down to hands held out
            if (i == 0) Array.Copy(x, y, RS);
            for (int c = 0; c < RS; c++) { y[c] += SimDt * yd[c]; yd[c] += SimDt * (x[c] - y[c] - k1 * yd[c]) / k2; bk.react[i * RS + c] = y[c]; }
        }
    }

    static readonly HumanBodyBones[] RestBones = { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
        HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };

    SimBake Bake(int hash)
    {
        var bk = new SimBake { hash = hash, t0 = -2f };
        // ---- measure the body at rest
        var hips = B(HumanBodyBones.Hips);
        if (hasRestHips) hips.localPosition = restHipsPos;
        foreach (var b in RestBones) { var tr = B(b); if (tr != null) tr.localRotation = Rest(b); }
        for (int side = 0; side < 2; side++)
        {
            for (int a = 0; a < 4; a++) bk.anchorRef[a + 4 * side] = transform.InverseTransformPoint(AnchorPos((Anchor)a, side == 0));
            var up = B(side == 0 ? HumanBodyBones.RightUpperLeg : HumanBodyBones.LeftUpperLeg); var lo = B(side == 0 ? HumanBodyBones.RightLowerLeg : HumanBodyBones.LeftLowerLeg); var ft = B(side == 0 ? HumanBodyBones.RightFoot : HumanBodyBones.LeftFoot);
            if (up == null || lo == null || ft == null) continue;
            float sgn = side == 0 ? 1f : -1f;
            bk.footPos[side] = transform.InverseTransformPoint(ft.position) + new Vector3(sgn * 0.022f, 0, side == 0 ? 0.014f : -0.008f);
            bk.footRot[side] = Quaternion.Euler(0, sgn * 8f, 0) * (Quaternion.Inverse(transform.rotation) * ft.rotation);
            bk.legA[side] = Vector3.Distance(up.position, lo.position); bk.legB[side] = Vector3.Distance(lo.position, ft.position);
        }
        bk.hipsRef = transform.InverseTransformPoint(hips.position); bk.neckRef = transform.InverseTransformPoint(B(HumanBodyBones.Neck).position);
        BodyFrameRest(bk, transform.InverseTransformPoint(B(HumanBodyBones.Head).position));
        var ua = B(HumanBodyBones.RightUpperArm); var la = B(HumanBodyBones.RightLowerArm); var ha = B(HumanBodyBones.RightHand);
        float armLen = Vector3.Distance(ua.position, la.position) + Vector3.Distance(la.position, ha.position), armScale = armLen / 0.42f;
        // ---- keys and targets
        Gesture restG = Library.TryGetValue(restPose ?? "rest", out var rg) ? rg : Library["rest"];
        var keys = BuildKeys(restG);
        float tEnd = 4f; foreach (var k in keys) tEnd = Mathf.Max(tEnd, k.t + 6f);
        bk.n = Mathf.CeilToInt((tEnd - bk.t0) / SimDt) + 1;
        bk.R = new float[bk.n * HS]; bk.L = new float[bk.n * HS]; bk.body = new float[bk.n * BS]; bk.react = new float[bk.n * RS];
        var tR = new float[keys.Count][]; var tL = new float[keys.Count][];
        float[] restR = HandVec(restG, restG, true, bk, armScale), restL = HandVec(restG, restG, false, bk, armScale);
        for (int k = 0; k < keys.Count; k++)
        {
            tR[k] = HandVec(keys[k].g, restG, true, bk, armScale); tL[k] = HandVec(keys[k].g, restG, false, bk, armScale);
            Emphasis(tR[k], restR, keys[k].gain); Emphasis(tL[k], restL, keys[k].gain);
            Vary(tR[k], restR, k, true, keys[k]); Vary(tL[k], restL, k, false, keys[k]);
        }
        bk.restPos[0] = new Vector3(restR[0], restR[1], restR[2]); bk.restPos[1] = new Vector3(restL[0], restL[1], restL[2]);
        var keyD = new float[keys.Count];
        SimHand(bk, keys, tR, restR, true, bk.R, keyD);
        SimHand(bk, keys, tL, restL, false, bk.L, keyD);
        SimBody(bk, keys, keyD);
        SimReact(bk, restR, restL, armLen);
        // blinks at gesture onsets (about every second gesture), unless the face track already blinks or holds the eyes shut around that moment
        var bl = new List<float>(); int ci = track != null ? Array.IndexOf(track.names, "Fcl_EYE_Close") : -1, nn = track != null ? track.names.Length : 0;
        for (int i = 0; gestureNames != null && i < gestureNames.Length; i++)
        {
            if (Hash(i * 97 + 3) > 0.05f) continue;
            float tb = gestureStart[i] + 0.02f + 0.10f * Mathf.Abs(Hash(i * 61 + 1)); bool busy = false;
            if (ci >= 0) for (float xx = tb - 1.0f; xx <= tb + 1.0f; xx += 0.03f) if (Sample(track.w, nn, ci, xx) > 0.5f) busy = true;
            if (!busy && (bl.Count == 0 || tb - bl[bl.Count - 1] > 1.5f)) bl.Add(tb);
        }
        bk.blinks = bl.ToArray();
        return bk;
    }

    /// <summary>
    /// Body contact. The iterative solver (GestureCollide.cs) is right about WHERE the arm may be but, run frame by frame, it can
    /// jump when a hand lands on the chest. So it is run here once over the whole timeline, and what it did to each hand (wrist
    /// offset, finger tilt) is smoothed in time; at run time the smoothed correction is simply added.
    /// </summary>
    void BakeContacts(SimBake bk)
    {
        bk.cn = Mathf.CeilToInt((bk.n - 1) * SimDt / SimBake.CorrDt) + 1;
        float[] Record(int mode)
        {
            var raw = new float[bk.cn * 12];
            contactPass = mode;
            try
            {
                for (int i = 0; i < bk.cn; i++)
                {
                    float t = bk.t0 + i * SimBake.CorrDt;
                    GestureInputs(t, out float slow, out float beat, out Vector2 gz);
                    ApplyGestures(t, slow, beat, gz);
                    Array.Copy(contactOut, 0, raw, i * 12, 12);
                }
            }
            finally { contactPass = 0; }
            // 1. dilate: within +-0.2 s keep the correction with the largest wrist offset (the safe one arrives early and leaves late)
            const int win = 8; var dil = new float[bk.cn * 12];
            for (int i = 0; i < bk.cn; i++)
                for (int side = 0; side < 2; side++)
                {
                    int best = i; float bm = -1;
                    for (int k = -win; k <= win; k++)
                    {
                        int j = Mathf.Clamp(i + k, 0, bk.cn - 1), o = j * 12 + side * 6;
                        float m = raw[o] * raw[o] + raw[o + 1] * raw[o + 1] + raw[o + 2] * raw[o + 2] + 0.01f * (raw[o + 3] * raw[o + 3] + raw[o + 4] * raw[o + 4] + raw[o + 5] * raw[o + 5]);
                        if (m > bm) { bm = m; best = j; }
                    }
                    Array.Copy(raw, best * 12 + side * 6, dil, i * 12 + side * 6, 6);
                }
            // 2. Gaussian in time, sigma 75 ms
            const int rad = 9; float sigma = 0.075f / SimBake.CorrDt; var kern = new float[2 * rad + 1]; float ks = 0;
            for (int k = -rad; k <= rad; k++) { kern[k + rad] = Mathf.Exp(-0.5f * k * k / (sigma * sigma)); ks += kern[k + rad]; }
            var sm = new float[bk.cn * 12];
            for (int i = 0; i < bk.cn; i++)
                for (int c = 0; c < 12; c++)
                {
                    float a = 0; for (int k = -rad; k <= rad; k++) a += kern[k + rad] * dil[Mathf.Clamp(i + k, 0, bk.cn - 1) * 12 + c];
                    sm[i * 12 + c] = a / ks;
                }
            return sm;
        }
        bk.corr = null;
        var first = Record(1);                                   // the full solve
        bk.corr = first;
        for (int round = 0; round < 2; round++)                  // what the live clean-up would still add on top is smoothed and folded in too
        {
            var more = Record(2);
            for (int i = 0; i < more.Length; i++) bk.corr[i] += more[i];
        }
    }

    /// <summary>
    /// The same for the arms of a recorded take (review 2026-11-13: "whenever a limb touches the chest or comes near the microphone it
    /// trembles"). The contact solver was run afresh on every frame, and near a surface its answer flips between neighbouring
    /// solutions - 3 mm from one frame to the next. Here it is run once over the timeline; what it did to each arm (wrist, finger
    /// direction, elbow side) is widened and smoothed in time, what a second full solve still adds is folded in twice, and at run
    /// time the arm is solved WITHOUT contacts: recorded pose + smooth correction.
    /// </summary>
    void BakeClipArms(SimBake bk)
    {
        const int W = 18; bk.armCorr = null;
        float[] Record(int mode)
        {
            var raw = new float[bk.cn * W]; clipPass = mode; bool any = false;
            try
            {
                for (int i = 0; i < bk.cn; i++)
                {
                    float t = bk.t0 + i * SimBake.CorrDt; Array.Clear(clipOut, 0, W);
                    GestureInputs(t, out float slow, out float beat, out Vector2 gz);
                    ApplyGestures(t, slow, beat, gz);
                    Array.Copy(clipOut, 0, raw, i * W, W);
                }
            }
            finally { clipPass = 0; }
            for (int i = 0; i < raw.Length && !any; i++) any = raw[i] != 0f;
            if (!any) return null;
            // 1. widen: within +-0.15 s keep the correction with the largest wrist offset (the arm steers clear early and comes back late)
            const int win = 6; var dil = new float[bk.cn * W];
            for (int i = 0; i < bk.cn; i++)
                for (int side = 0; side < 2; side++)
                {
                    int best = i; float bm = -1;
                    for (int k = -win; k <= win; k++)
                    {
                        int j = Mathf.Clamp(i + k, 0, bk.cn - 1), o = j * W + side * 9;
                        float m = raw[o] * raw[o] + raw[o + 1] * raw[o + 1] + raw[o + 2] * raw[o + 2] + 0.01f * (raw[o + 3] * raw[o + 3] + raw[o + 4] * raw[o + 4] + raw[o + 5] * raw[o + 5] + raw[o + 6] * raw[o + 6] + raw[o + 7] * raw[o + 7] + raw[o + 8] * raw[o + 8]);
                        if (m > bm) { bm = m; best = j; }
                    }
                    Array.Copy(raw, best * W + side * 9, dil, i * W + side * 9, 9);
                }
            // 2. Gaussian in time, sigma 90 ms
            const int rad = 11; float sigma = 0.090f / SimBake.CorrDt; var kern = new float[2 * rad + 1]; float ks = 0;
            for (int k = -rad; k <= rad; k++) { kern[k + rad] = Mathf.Exp(-0.5f * k * k / (sigma * sigma)); ks += kern[k + rad]; }
            var sm = new float[bk.cn * W];
            for (int i = 0; i < bk.cn; i++)
                for (int c = 0; c < W; c++)
                {
                    float a = 0; for (int k = -rad; k <= rad; k++) a += kern[k + rad] * dil[Mathf.Clamp(i + k, 0, bk.cn - 1) * W + c];
                    sm[i * W + c] = a / ks;
                }
            return sm;
        }
        var first = Record(1); if (first == null) return;
        bk.armCorr = first;
        for (int round = 0; round < 2; round++)
        {
            var more = Record(2); if (more == null) break;
            for (int i = 0; i < more.Length; i++) bk.armCorr[i] += more[i];
        }
    }

    // ------------------------------------------------------------------ pelvis and legs
    void PoseHips(Vector3 offset, float pitch, float yaw, float roll)
    {
        var h = B(HumanBodyBones.Hips);
        if (hasRestHips) { h.localPosition = restHipsPos; if (plantFeet) h.position += transform.TransformVector(offset); }
        Local(HumanBodyBones.Hips, pitch, yaw, roll);
    }

    /// <summary>Feet stay where they stand in the rest pose; the knees take up whatever the pelvis does.</summary>
    void SolveLegs(SimBake bk)
    {
        if (!hasRestHips) return;
        for (int side = 0; side < 2; side++)
        {
            bool right = side == 0;
            var upId = right ? HumanBodyBones.RightUpperLeg : HumanBodyBones.LeftUpperLeg; var loId = right ? HumanBodyBones.RightLowerLeg : HumanBodyBones.LeftLowerLeg; var ftId = right ? HumanBodyBones.RightFoot : HumanBodyBones.LeftFoot;
            var up = B(upId); var lo = B(loId); var ft = B(ftId); if (up == null || lo == null || ft == null) continue;
            float a = bk.legA[side], b = bk.legB[side];
            Vector3 S = up.position, T = transform.TransformPoint(bk.footPos[side]), st = T - S;
            float d = Mathf.Min(st.magnitude, (a + b) * 0.9995f); Vector3 dir = st.normalized;
            float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1f, 1f), sinA = Mathf.Sqrt(1 - cosA * cosA);
            Vector3 pole = transform.forward + transform.right * (right ? 0.10f : -0.10f), perp = (pole - Vector3.Dot(pole, dir) * dir).normalized;
            AimBone(up, lo, upId, a * (cosA * dir + sinA * perp));
            AimBone(lo, ft, loId, S + dir * d - lo.position);
            ft.rotation = transform.rotation * bk.footRot[side];
        }
    }
}
