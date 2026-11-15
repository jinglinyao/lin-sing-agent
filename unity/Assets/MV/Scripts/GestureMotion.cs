using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// How the code gestures move (the poses themselves are in GestureRig.cs).
/// v3, after the review "still stiff, the shake is too large, a real torso always moves a little":
///   * the whole schedule is SIMULATED once on a 120 Hz timeline (GestureSim.cs) and then only sampled, so every
///     channel has a continuous velocity - no seams between wind-up, stroke and settle, no pops when a gesture is cut short;
///   * the torso takes part in every gesture: it turns, leans and bends with the hands (and a little ahead of them),
///     the pelvis shifts the other way to keep the balance, the shoulder girdle comes forward on a reach;
///   * the legs are solved so the feet stay planted while the hips move; the knees are never locked;
///   * the micro layer is about 30 % smaller and has no fast wobble: slow drift, rare small re-settles, breath.
/// Everything stays a pure function of the clip time.
/// </summary>
public partial class SingDriver
{
    [Header("Gesture motion")]
    [Range(0, 2)] public float anticipation = 1f;
    [Range(0, 2)] public float overshoot = 1f;
    [Range(0, 2)] public float arcs = 1f;
    [Tooltip("Small human motion on top: drift, finger fidgets, re-settles, breath.")]
    [Range(0, 2)] public float micro = 1f;
    [Tooltip("How much the torso, pelvis and shoulders take part in the arm gestures.")]
    [Range(0, 2)] public float torso = 1f;
    [Tooltip("Weight shifts from one leg to the other now and then (0 = stands evenly).")]
    [Range(0, 1.5f)] public float weightShift = 1f;
    [Tooltip("Body pulse with the music (knees, pelvis, head), strongest where nobody sings. Needs beats in the track.")]
    [Range(0, 2)] public float grooveGain = 1f;
    [Tooltip("Solve the legs so the feet stay planted while the hips move.")]
    public bool plantFeet = true;

    // ---- smooth noise: value noise with quintic interpolation. Never repeats the way a sine does.
    static float Hash(int n)
    {
        unchecked { uint x = (uint)n * 747796405u + 2891336453u; x = ((x >> (int)((x >> 28) + 4)) ^ x) * 277803737u; x ^= x >> 22; return (x & 0xFFFFFF) / 16777216f * 2f - 1f; }
    }
    static float N1(float t, int seed)
    {
        int i = Mathf.FloorToInt(t); float f = t - i, u = f * f * f * (f * (f * 6f - 15f) + 10f);
        return Mathf.Lerp(Hash(i * 7919 + seed * 104729), Hash((i + 1) * 7919 + seed * 104729), u);
    }
    static float Fbm(float t, int seed) => (N1(t, seed) + 0.5f * N1(t * 2.13f + 7.3f, seed + 101) + 0.25f * N1(t * 4.37f + 1.9f, seed + 202)) / 1.75f;
    /// <summary>Two octaves only: slow drift without the fast component that read as shaking.</summary>
    static float Fbm2(float t, int seed) => (N1(t, seed) + 0.45f * N1(t * 2.13f + 7.3f, seed + 101)) / 1.45f;

    /// <summary>A held hand is shifted a few millimetres now and then and stays there (no wobble): eased steps at irregular moments.</summary>
    static Vector3 Resettle(float t, int seed)
    {
        const float period = 2.9f; int i = Mathf.FloorToInt(t / period);
        Vector3 a = new Vector3(Hash((i - 1) * 17 + seed + 1), Hash((i - 1) * 17 + seed + 2), Hash((i - 1) * 17 + seed + 3));
        Vector3 b = new Vector3(Hash(i * 17 + seed + 1), Hash(i * 17 + seed + 2), Hash(i * 17 + seed + 3));
        float te = (i + 0.25f + 0.5f * Mathf.Abs(Hash(i * 31 + seed))) * period;
        float u = Mathf.Clamp01((t - te) / 0.60f); u = u * u * u * (u * (u * 6f - 15f) + 10f);
        return Vector3.LerpUnclamped(a, b, u) * 0.0055f;
    }

    /// <summary>Which leg carries the weight: +1 right, -1 left. It changes at irregular moments (every 5-9 s), a transfer takes 1.3 s;
    /// dip is a 0..1 bump in the middle of a transfer (the pelvis sinks a little while the weight goes across).</summary>
    static float Weight(float t, out float dip)
    {
        const float period = 7.3f; int i = Mathf.FloorToInt(t / period);
        float a = (((i - 1) & 1) == 0 ? 1f : -1f) * (0.55f + 0.45f * Mathf.Abs(Hash((i - 1) * 41 + 7)));
        float b = ((i & 1) == 0 ? 1f : -1f) * (0.55f + 0.45f * Mathf.Abs(Hash(i * 41 + 7)));
        float te = (i + 0.20f + 0.55f * Mathf.Abs(Hash(i * 53 + 9))) * period;
        float u = Mathf.Clamp01((t - te) / 1.3f); dip = u > 0 && u < 1 ? Mathf.Sin(Mathf.PI * u) : 0;
        return Mathf.LerpUnclamped(a, b, MinJerk(u));
    }

    /// <summary>The musical pulse at time t: dip 0..1 (1 = on the beat, the body is down), sway -1..1 (one side per pulse, back on the next), amp 0..1.</summary>
    void Groove(float t, out float dip, out float sway, out float amp)
    {
        dip = sway = amp = 0;
        if (track == null || track.beats == null || track.beats.Length < 3 || track.groove == null || track.groove.Length < track.count) return;
        var b = track.beats; if (t <= b[0] || t >= b[b.Length - 1]) return;
        int i = Array.BinarySearch(b, t); if (i < 0) i = ~i - 1; i = Mathf.Clamp(i, 0, b.Length - 2);
        float u = Mathf.Clamp01((t - b[i]) / Mathf.Max(0.05f, b[i + 1] - b[i]));
        float edge = Mathf.Clamp01((t - b[0]) / 2f) * Mathf.Clamp01((b[b.Length - 1] - t) / 2f);
        amp = Sample(track.groove, 1, 0, t) * grooveGain * (0.75f + 0.25f * Fbm2(t * 0.09f, 91)) * edge;      // never a metronome: the depth wanders
        dip = 0.5f + 0.5f * Mathf.Cos(2f * Mathf.PI * u);
        sway = Mathf.Sin(Mathf.PI * (i + u));
    }

    /// <summary>Audible breath before a phrase (from the vocal stem): chest and shoulders rise with it.</summary>
    float BreathIn(float t)
    {
        if (track == null || track.breath == null || track.breath.Length < track.count) return 0;
        return Mathf.Clamp01(Sample(track.breath, 1, 0, t + 0.10f) * 2.2f);
    }

    static readonly Dictionary<string, Gesture> mirrorCache = new Dictionary<string, Gesture>();

    static Gesture Lookup(string name)
    {
        name = name.TrimEnd('~').TrimEnd('+', '-');
        int bar = name.IndexOf('|');
        if (bar > 0)
        {
            // "A|B": the right hand does A, the left hand does B (e.g. "mic|chest_L": right hand on the microphone, left hand on the chest)
            if (mirrorCache.TryGetValue(name, out var cm)) return cm;
            Gesture ga = Lookup(name.Substring(0, bar)), gb = Lookup(name.Substring(bar + 1)); if (ga == null || gb == null) return null;
            bool aIdle = ga.R != null && ga.R.anchor == Anchor.Mic; Gesture ex = aIdle ? gb : ga, other = aIdle ? ga : gb;     // the expressive half sets the body
            cm = new Gesture { R = ga.R, L = gb.L, lean = ex.lean + 0.5f * other.lean, yaw = ex.yaw, nod = ex.nod, tilt = ex.tilt, shrug = ex.shrug, headYaw = ex.headYaw, beat = ex.beat, wave = ex.wave, nods = ex.nods, shakes = ex.shakes, snap = ex.snap };
            mirrorCache[name] = cm; return cm;
        }
        bool mirror = name.EndsWith("_L"); string nm = mirror ? name.Substring(0, name.Length - 2) : name;
        if (!Library.TryGetValue(nm, out var g0)) return null;
        if (!mirror) return g0;
        if (mirrorCache.TryGetValue(name, out var m)) return m;
        m = new Gesture { R = g0.L, L = g0.R, lean = g0.lean, yaw = -g0.yaw, nod = g0.nod, tilt = -g0.tilt, shrug = g0.shrug, headYaw = -g0.headYaw, beat = g0.beat, wave = g0.wave, nods = g0.nods, shakes = g0.shakes, snap = g0.snap };
        mirrorCache[name] = m; return m;
    }

    readonly float[] sR = new float[HS], sL = new float[HS], sB = new float[BS], sC = new float[RS], sK = new float[12], contactOut = new float[12];
    [Tooltip("How firmly the mouth is kept at the microphone while she stands at it (0 = free).")]
    [Range(0, 1)] public float micHold = 0.85f;
    [Tooltip("At the stand microphone she stands this much lower than with straight legs (m): room for the hold to bring the mouth UP to the microphone when she lowers her head.")]
    public float micSlack = 0f;
    [Tooltip("How far above its place the mouth may ride before the body is brought down (m). 0 = held exactly. When she lifts her head off the microphone the mouth rises a little: natural, and the body does not pump up and down with every look.")]
    public float micBand = 0f;
    [Tooltip("Where the mouth belongs relative to the centre of the grille (set per character by MV / Place Mic At Singer's Mouth).")]
    public Vector3 micMouth = new Vector3(0f, 0.062f, -0.078f);
    [Tooltip("Mouth relative to the head bone (measured with MvSetup.MouthPos).")]
    public Vector3 mouthLocal = new Vector3(-0.002f, -0.007f, 0.087f);
    [NonSerialized] Quaternion bodyYaw = Quaternion.identity, bodyYawW = Quaternion.identity;
    [NonSerialized] int clipPass;         // the same for the arms of a recorded take (ClipArmsSafe): 1 = the full solve, 2 = what a full solve adds to the baked correction
    readonly float[] clipOut = new float[18], sA = new float[18];
    readonly float[][] rigCurl = { new float[5], new float[5] }; readonly float[] rigSpread = new float[2];      // how the gesture system holds each hand now (for the reach blend)
    [NonSerialized] int contactPass;      // 0 = normal, 1 = recording the full solve, 2 = recording what the live clean-up still has to do

    /// <summary>Procedural body: idle layer + scheduled gestures. slow = phrase loudness 0..1, beat = stress 0..1, gz = head offset from the gaze.</summary>
    void ApplyGestures(float t, float slow, float beat, Vector2 gz)
    {
        var bk = EnsureBake();
        const float TAU = Mathf.PI * 2;
        transform.position = stageOrigin + transform.rotation * RootOffset(t);        // where she stands now (travelling clips move her)
        previewT = t; Vector3 rootNow = RootOffset(t);
        bk.Sample(bk.R, HS, t, sR); bk.Sample(bk.L, HS, t, sL); bk.Sample(bk.body, BS, t, sB); bk.Sample(bk.react, RS, t, sC);
        float gLean = sB[0], gYaw = sB[1], gNod = sB[2], gTilt = sB[3], gShrug = sB[4], gHeadYaw = sB[5];
        float nodPulse = 0, shakePulse = 0;
        if (gestureNames != null)
            for (int i = 0; i < gestureNames.Length; i++)
            {
                var g = Lookup(gestureNames[i]); if (g == null || (g.nods <= 0 && g.shakes <= 0)) continue;
                float u = (t - gestureStart[i] - 0.25f) / Mathf.Max(0.3f, gestureDur[i] - 0.8f);
                if (u > 0 && u < 1 && g.nods > 0) nodPulse += 7f * Mathf.Max(0, Mathf.Sin(u * Mathf.PI * g.nods)) * Mathf.Sin(u * Mathf.PI * g.nods);
                if (u > 0 && u < 1 && g.shakes > 0) shakePulse += 5f * Mathf.Sin((t - gestureStart[i] - 0.25f) * Mathf.PI * 2f * 1.25f) * Mathf.Sin(u * Mathf.PI) * (0.8f + 0.2f * Fbm2(t * 0.9f, 77 + i));   // 1.25 Hz, whatever the length   // small, uneven "no"s that swell and fade
            }
        // ---- 1. torso, pelvis, head. They lead: evaluated first, the arm anchors move with them.
        float inhale = BreathIn(t) * micro;
        float breath = Mathf.Sin(TAU * t / 3.7f + 0.8f * Fbm2(t * 0.11f, 71));                           // breathing, never a perfect clock
        float swayX = Fbm2(t * 0.085f, 3), swayZ = Fbm2(t * 0.070f, 5);                                 // the weight wanders between the feet
        float nPitch = Fbm2(t * 0.13f, 7), nYaw = Fbm2(t * 0.11f, 8), nRoll = Fbm2(t * 0.10f, 9);        // posture is never frozen
        float lean = gLean + sC[1] * torso + 0.40f * nPitch * micro;
        float yaw = gYaw + sC[0] * torso + 0.60f * nYaw * micro;
        float roll = sC[2] * torso + 0.40f * nRoll * micro;
        float wgt = Weight(t, out float dip) * weightShift;
        Groove(t - 0.03f, out float gDip, out float gSway, out float gAmp);                          // pelvis and knees carry the pulse
        Groove(t - 0.10f, out float gDipHead, out _, out float gAmpHead);                            // the head follows the body a moment later
        float shiftX = 0.019f * wgt + 0.006f * swayX * micro + 0.012f * gAmp * gSway + sC[3] * torso, shiftZ = 0.004f * swayZ * micro + sC[4] * torso;   // metres, character space
        float pelvisRoll = shiftX * 70f;                                                              // the hip over the loaded leg rides higher, the shoulders tilt back the other way
        PoseHips(new Vector3(shiftX, -0.005f - (micOn ? micSlack : 0f) - 0.10f * Mathf.Abs(shiftX) - 0.004f * dip * weightShift - 0.012f * gAmp * gDip, shiftZ) * motion, 0.25f * lean, 0.9f * swayX * micro + 1.1f * wgt - yaw * 0.15f, pelvisRoll);
        Local(HumanBodyBones.Spine, 1.0f + lean * 0.40f, yaw * 0.35f, roll * 0.40f - pelvisRoll * 0.65f);
        Local(HumanBodyBones.Chest, 0.5f * breath + lean * 0.35f - 0.8f * slow - 1.8f * inhale + 0.5f * gAmp * gDip, yaw * 0.40f, roll * 0.35f - pelvisRoll * 0.30f);
        var uc = B(HumanBodyBones.UpperChest); if (uc != null) Local(HumanBodyBones.UpperChest, 0.5f * breath + lean * 0.2f, yaw * 0.25f, roll * 0.25f);
        // the head keeps facing the listener and stays level: it takes back most of what the torso does
        float hPitch = -0.20f * (lean - 1f) + 0.5f * sC[10] * torso + 1.0f * gAmpHead * gDipHead, hRoll = -0.36f * roll;
        gHeadYaw += sC[9] * torso + shakePulse;
        Local(HumanBodyBones.Neck, 0.8f - 0.6f * slow + 0.4f * gz.y + 0.35f * gNod + 0.4f * nodPulse + hPitch, 0.4f * gz.x + 0.4f * gHeadYaw - yaw * 0.3f, 0.4f * gTilt + hRoll);
        Local(HumanBodyBones.Head, 1.0f - 1.2f * slow + 0.6f * gz.y + 0.65f * gNod + 0.6f * nodPulse + 0.65f * beat * motion + hPitch + 0.22f * Mathf.Sin(TAU * t / 3.7f - 0.9f),
            0.6f * gz.x + 0.6f * gHeadYaw - yaw * 0.3f + 0.95f * Fbm2(t * 0.17f, 23) * micro, 0.6f * gTilt + hRoll + 0.90f * Fbm2(t * 0.15f, 29) * micro - 0.8f * inhale);
        // shoulder girdle: breathing, shrug, lifted with a raised arm, brought forward on a reach
        float sh = 1.2f * breath + 16f * gShrug + 3.5f * inhale;
        var ls = B(HumanBodyBones.LeftShoulder); var rs = B(HumanBodyBones.RightShoulder);
        if (rs != null) rs.localRotation = Rest(HumanBodyBones.RightShoulder) * Quaternion.Euler(0, -sC[5] * torso * motion, (sh + 9f * sC[7]) * motion);
        if (ls != null) ls.localRotation = Rest(HumanBodyBones.LeftShoulder) * Quaternion.Euler(0, sC[6] * torso * motion, -(sh + 9f * sC[8]) * motion);
        if (plantFeet) SolveLegs(bk);
        Quaternion headKeep = B(HumanBodyBones.Head).rotation, neckKeep = B(HumanBodyBones.Neck).rotation;
        ApplyBase(t);                                                                 // library clips under the gestures: legs / pelvis / torso
        if (baseTorsoNow > 0.01f)
        {
            // a clip under the torso swings the head with it; the face is pulled most of the way back to where the performance wants it
            // (earlier review: head movement must stay small and slow)
            var hd = B(HumanBodyBones.Head); var nk = B(HumanBodyBones.Neck); float k = Mathf.Clamp01(baseTorsoNow) * headDamp * (1f - armsFromClip);   // (not in a whole-body clip: crying needs its own head)
            k = Mathf.Max(k, 0.78f * mocapNow);                                    // under a recorded take the head is the performance's own: level, to the listener
            nk.rotation = Quaternion.Slerp(nk.rotation, neckKeep, 0.5f * k); hd.rotation = Quaternion.Slerp(hd.rotation, headKeep, k);
        }
        MocapHead();                                                                  // (before the mouth is held on the microphone: that hold must see the final head)
        if (baseLowerNow > 0.01f)
        {
            foreach (bool right in new[] { false, true })
            {
                var loId = right ? HumanBodyBones.RightLowerLeg : HumanBodyBones.LeftLowerLeg; var lo = B(loId); var ft = B(right ? HumanBodyBones.RightFoot : HumanBodyBones.LeftFoot);
                float floorY = stageOrigin.y, kneeH = lo.position.y - floorY, footH = ft.position.y - floorY;
                float kneel = Mathf.Clamp01((0.26f - kneeH) / 0.12f); kneel = kneel * kneel * (3f - 2f * kneel);
                if (kneel <= 0.001f || footH < 0.085f) continue;
                Vector3 back = ft.position - lo.position; float b = back.magnitude; back.y = 0; if (back.sqrMagnitude < 1e-6f) continue;
                float drop = Mathf.Clamp(lo.position.y - (floorY + 0.075f), -b, b);
                Vector3 target = lo.position + back.normalized * Mathf.Sqrt(Mathf.Max(0f, b * b - drop * drop)) + Vector3.down * drop;      // ankle on the floor, same bearing
                Quaternion before = lo.rotation; Quaternion aim = Quaternion.FromToRotation(ft.position - lo.position, target - lo.position);
                lo.rotation = Quaternion.Slerp(before, aim * before, kneel * baseLowerNow);
            }
        }
        if (baseLowerNow > 0.01f && gAmp > 0.01f)
        {
            // a clip owns the legs: the musical pulse is added on top of it (knees give on the beat, the head nods after them)
            var hp = B(HumanBodyBones.Hips); var lf = B(HumanBodyBones.LeftFoot); var rf = B(HumanBodyBones.RightFoot);
            Vector3 lp = lf.position, rp = rf.position; Quaternion lr = lf.rotation, rr = rf.rotation;
            hp.position -= transform.up * 0.016f * gAmp * gDip * baseLowerNow;
            SolveLegTo(false, lp, lr); SolveLegTo(true, rp, rr);
            Add(HumanBodyBones.Neck, 1.0f * gAmpHead * gDipHead * baseTorsoNow, 0, 0); Add(HumanBodyBones.Head, 1.6f * gAmpHead * gDipHead * baseTorsoNow, 0, 0);
        }
        if (micOn && micHold > 0)
        {
            // at the stand microphone the MOUTH stays on the microphone: whatever the hips and the torso do, the pelvis is shifted so that
            // the mouth sits about 7.5 cm behind the grille. The body moves under a head that stays put - what singers at a stand do.
            float atMic = Mathf.Clamp01(1f - rootNow.magnitude / 0.25f) * micHold; atMic = atMic * atMic * (3f - 2f * atMic);
            if (atMic > 0.001f)
            {
                var hd = B(HumanBodyBones.Head); var hp = B(HumanBodyBones.Hips); var lf = B(HumanBodyBones.LeftFoot); var rf = B(HumanBodyBones.RightFoot);
                Vector3 mouth = hd.position + hd.rotation * Quaternion.Inverse(Rest(HumanBodyBones.Head)) * mouthLocal;
                Vector3 want = stageOrigin + transform.rotation * (micPos + micMouth);
                // She leans IN to the microphone from the waist, and the pelvis takes only what is left. (The pelvis used to take all of it:
                // hips pushed 5 - 7 cm forward under a chest that tipped back - review 2026-11-13: "leaning back, mechanical".)
                var spn = B(HumanBodyBones.Spine); float leanSum = 0f;
                for (int pass = 0; pass < 2 && spn != null; pass++)
                {
                    Vector3 e0 = Quaternion.Inverse(transform.rotation) * (want - mouth); float lever = Mathf.Max(0.25f, mouth.y - spn.position.y);
                    float ang = Mathf.Clamp(leanSum + Mathf.Atan2(e0.z, lever) * Mathf.Rad2Deg * 0.85f * atMic, -3f, 10f) - leanSum;
                    spn.rotation = Quaternion.AngleAxis(ang, transform.right) * spn.rotation;
                    hd.rotation = Quaternion.AngleAxis(-0.75f * ang, transform.right) * hd.rotation;       // the face keeps looking where it looked
                    leanSum += ang; mouth = hd.position + hd.rotation * Quaternion.Inverse(Rest(HumanBodyBones.Head)) * mouthLocal;
                }
                Vector3 e = want - mouth;
                if (micBand > 0f) { float above = -e.y; e.y = above < -0.003f ? -above - 0.003f : above > micBand ? micBand - above : 0f; }      // only when the mouth is below its place, or well above it
                e.y = Mathf.Clamp(e.y, -0.08f, 0.05f); { Vector3 ec = Quaternion.Inverse(transform.rotation) * e; ec.x = Mathf.Clamp(ec.x, -0.04f, 0.04f); ec.z = Mathf.Clamp(ec.z, -0.03f, 0.03f); e = transform.rotation * ec * atMic; }      // (the pelvis slides 3 - 4 cm at most)
                Vector3 lp = lf.position, rp = rf.position; Quaternion lr = lf.rotation, rr = rf.rotation;
                if (e.y > 0)
                {
                    foreach (bool right in new[] { false, true })
                    {
                        var up = B(right ? HumanBodyBones.RightUpperLeg : HumanBodyBones.LeftUpperLeg); var lo = B(right ? HumanBodyBones.RightLowerLeg : HumanBodyBones.LeftLowerLeg); Transform ft = right ? rf : lf;
                        float len = (Vector3.Distance(up.position, lo.position) + Vector3.Distance(lo.position, ft.position)) * 0.988f;
                        Vector3 d = up.position + new Vector3(e.x, 0, e.z) - ft.position; float h2 = len * len - d.x * d.x - d.z * d.z;
                        e.y = Mathf.Min(e.y, Mathf.Max(0f, (h2 > 0 ? Mathf.Sqrt(h2) : 0f) - d.y));
                    }
                }
                hp.position += e; SolveLegTo(false, lp, lr); SolveLegTo(true, rp, rr);
            }
        }
        {   // which way the body faces now (character space, turn only): hand offsets and the body proxy turn with it
            Vector3 fw = Quaternion.Inverse(transform.rotation) * (B(HumanBodyBones.Hips).rotation * Quaternion.Inverse(Rest(HumanBodyBones.Hips)) * Vector3.forward); fw.y = 0;
            bodyYaw = fw.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fw.normalized, Vector3.up) : Quaternion.identity;
            bodyYawW = transform.rotation * bodyYaw * Quaternion.Inverse(transform.rotation);
        }

        // ---- 2. arms: the baked wrist paths ride on the anchors (shoulder, chest, head, hips) as the torso moves
        micNearR = 0f;
        var tgt = new HandTarget[2];
        Vector3 dispR = new Vector3(sR[0], sR[1], sR[2]) - bk.restPos[0], dispL = new Vector3(sL[0], sL[1], sL[2]) - bk.restPos[1];
        for (int side = 0; side < 2; side++)
        {
            bool right = side == 0; int seed = right ? 11 : 37; float[] h = right ? sR : sL;
            // each hand hangs on its anchors; the offset from an anchor turns with the body (when a clip turns her to walk, the arms come along).
            // A hand on the microphone has no anchor and stays where the microphone is.
            Vector3 prefW = transform.TransformPoint(new Vector3(h[0], h[1], h[2])), pos = Vector3.zero; float wsumA = 0;
            for (int a = 0; a < 4; a++)
                if (Mathf.Abs(h[3 + a]) > 1e-4f) { pos += h[3 + a] * (AnchorPos((Anchor)a, right) + bodyYawW * (prefW - transform.TransformPoint(bk.anchorRef[a + 4 * side]))); wsumA += h[3 + a]; }
            pos += (1f - wsumA) * transform.TransformPoint(new Vector3(h[0], h[1], h[2]) - rootNow);       // the microphone stays on the stage when she steps away from it
            Quaternion dirYaw = Quaternion.Slerp(Quaternion.identity, bodyYaw, Mathf.Clamp01(wsumA));
            float v = Mathf.Clamp01(new Vector3(h[24], h[25], h[26]).magnitude / 1.6f);              // how fast the hand travels, 0..1
            // micro layer: slow wander, rare small re-settles, a small dip on stressed syllables
            Vector3 wander = new Vector3(Fbm2(t * 0.30f, seed), Fbm2(t * 0.26f, seed + 1), Fbm2(t * 0.22f, seed + 2)) * 0.0092f * micro;
            float hang = Mathf.Clamp01(1f - (new Vector3(h[0], h[1], h[2]) - bk.restPos[side]).magnitude / 0.15f);      // 1 = the arm hangs at the side
            Vector3 swing = new Vector3(0.005f * Fbm2(t * 0.41f, seed + 30), 0, 0.011f * Fbm2(t * 0.37f, seed + 31)) * hang * micro;
            pos += transform.TransformVector(wander + swing + Resettle(t, seed) * micro + new Vector3(0, -0.030f, 0.012f) * beat * h[22] * motion);
            Vector3 fin = dirYaw * new Vector3(h[10], h[11], h[12]).normalized, palm = dirYaw * new Vector3(h[13], h[14], h[15]).normalized;
            fin = Quaternion.Euler(Fbm2(t * 0.40f, seed + 5) * 2.6f * micro, Fbm2(t * 0.37f, seed + 6) * 2.6f * micro, 0) * fin;      // the wrist is never locked
            // the hand trails the wrist while it travels and swings through when it stops (drag from the bake)
            Vector3 drag = Vector3.ClampMagnitude(new Vector3(h[27], h[28], h[29]) * 0.11f, 0.26f);
            fin = (fin - drag).normalized;
            // an arm that hangs is not dead while the other one works: it gives a little the same way
            // hands go with the music: clearly in a gesture that is about the beat (sway), a little when they just hang
            float gh = Mathf.Max(Mathf.Clamp01((h[22] - 0.8f) / 0.4f), 0.35f * hang) * gAmp;
            pos += transform.TransformVector(new Vector3(0.014f * gSway, -0.020f * gDipHead, 0.006f * gSway * (right ? 1f : -1f)) * gh);
            Vector3 other = right ? dispL : dispR;
            pos += transform.TransformVector(new Vector3(-other.x, other.y, other.z) * 0.05f * hang);
            if (h[23] > 0.01f) fin = Quaternion.AngleAxis(Mathf.Sin(t * TAU * 2.4f) * 22f * h[23], palm) * fin;
            // fingers: they open a little while the hand travels fast, and each one drifts on its own
            var curl = new float[5];
            for (int i = 0; i < 5; i++) curl[i] = Mathf.Clamp01(h[16 + i] * (1f - 0.22f * v) + Fbm2(t * 0.5f, seed + 10 + i) * 0.033f * micro);
            Vector3 pole = dirYaw * new Vector3(h[7], h[8], h[9]) + new Vector3(Fbm2(t * 0.23f, seed + 20), Fbm2(t * 0.19f, seed + 21), 0) * 0.045f * micro;   // the elbow breathes too
            float gripNear = -1f;
            if (micOn && micGripRoll > 0.5f && h[3] + h[4] + h[5] + h[6] < 0.5f)
            {
                MicGripPose(right ? 1f : -1f, out Vector3 gp, out Vector3 gf, out Vector3 gn); Vector3 gw = transform.TransformPoint(gp - rootNow);
                // The hand comes to the microphone in two moves of the gesture system itself: to "micnear" (open, turned, 13 cm off on its own side)
                // and from there onto the body, palm first; it leaves the same way. Here only the end is made exact:
                //   12 -> 8 cm (of the simulated hand from the grip)   the fingers open and stay open, whatever the gesture says: a hand that left
                //                                                       closed its fingers again 4 cm away and dragged the little finger through the body
                //   8 -> 4.5 cm                                         it is drawn onto the grip, fingers open
                //   4.5 -> 2.8 cm                                       the fingers close to the fitted grip (the hand is on the body by now)
                //   closer                                              it IS the grip - the motion core lets a hand settle about 2 cm short
                // (Doing the whole approach by distance made the wrist jump at 9 m/s; blending position, turn and closing together swept bent
                // fingers through the body.)
                float dist = Vector3.Distance(pos, gw), near = 1f - SStep(0.045f, 0.08f, dist), close = 1f - SStep(0.028f, 0.045f, dist), openW = 1f - SStep(0.08f, 0.12f, dist);
                if (right) micNearR = near; gripNear = near;
                if (near > 0f) { pos = Vector3.Lerp(pos, gw, near); fin = Vector3.Slerp(fin, gf, near).normalized; palm = Vector3.Slerp(palm, gn, near).normalized; }
                if (openW > 0f)
                    for (int i = 0; i < 5; i++)
                    {
                        float target = micCurl != null && micCurl.Length >= 5 ? micCurl[i] : curl[i], opened = Mathf.Min(curl[i], i == 0 ? Mathf.Min(0f, target) : 0.10f);
                        curl[i] = Mathf.Lerp(Mathf.Lerp(curl[i], opened, openW), target, close);
                    }
                // the same for a hand on the stand
                PoleGripPose(right ? 1f : -1f, out Vector3 pp, out Vector3 pf, out Vector3 pn); Vector3 pw = transform.TransformPoint(pp - rootNow);
                float nearP = 1f - SStep(0.03f, 0.07f, Vector3.Distance(pos, pw));
                if (nearP > 0f)
                {
                    pos = Vector3.Lerp(pos, pw, nearP); fin = Vector3.Slerp(fin, pf, nearP).normalized; palm = Vector3.Slerp(palm, pn, nearP).normalized;
                    if (poleCurl != null && poleCurl.Length >= 5) for (int i = 0; i < 5; i++) curl[i] = Mathf.Lerp(curl[i], poleCurl[i], nearP);
                }
            }
            tgt[side] = new HandTarget { pos = pos, pole = pole, finger = fin, palm = palm, curl = curl, spread = h[21] + 0.25f * v, micAvoid = Mathf.SmoothStep(0f, 1f, (h[3] + h[4] + h[5] + h[6] - 0.55f) / 0.45f), bodyAvoid = Mathf.Lerp(0.1f, 1f, Mathf.Clamp01(h[3] + h[4] + h[5] + h[6])) };      // a hand leaving the microphone is not pushed off it at once
            Array.Copy(curl, rigCurl[side], 5); rigSpread[side] = tgt[side].spread;
        }
        SeparateHands(ref tgt[0], ref tgt[1], 0.030f);
        lastClearance = 1f;
        for (int side = 0; side < 2; side++)
        {
            if (contactPass == 1)
            {
                // recording: run the iterative solver and note how far it had to move the wrist and tilt the fingers
                Vector3 p0 = tgt[side].pos, f0 = tgt[side].finger.normalized;
                SolveArmSafe(side == 0, ref tgt[side], true);
                Vector3 dp = transform.InverseTransformVector(tgt[side].pos - p0), df = tgt[side].finger.normalized - f0;
                contactOut[side * 6] = dp.x; contactOut[side * 6 + 1] = dp.y; contactOut[side * 6 + 2] = dp.z; contactOut[side * 6 + 3] = df.x; contactOut[side * 6 + 4] = df.y; contactOut[side * 6 + 5] = df.z;
            }
            else
            {
                if (bk.corr != null)
                {
                    bk.SampleCorr(t, sK);
                    tgt[side].pos += transform.TransformVector(new Vector3(sK[side * 6], sK[side * 6 + 1], sK[side * 6 + 2]));
                    tgt[side].finger = (tgt[side].finger.normalized + new Vector3(sK[side * 6 + 3], sK[side * 6 + 4], sK[side * 6 + 5])).normalized;
                }
                Vector3 p1 = tgt[side].pos, f1 = tgt[side].finger.normalized;
                SolveArmSafe(side == 0, ref tgt[side], false);
                if (contactPass == 2)
                {
                    Vector3 dp = transform.InverseTransformVector(tgt[side].pos - p1), df = tgt[side].finger.normalized - f1;
                    contactOut[side * 6] = dp.x; contactOut[side * 6 + 1] = dp.y; contactOut[side * 6 + 2] = dp.z; contactOut[side * 6 + 3] = df.x; contactOut[side * 6 + 4] = df.y; contactOut[side * 6 + 5] = df.z;
                }
            }
        }
        ApplyBaseArms();
    }

    /// <summary>The head under a recorded take.
    ///   - it goes WITH the torso (45 % of whatever the chest does: a lean or a turn of the body takes the head along),
    ///   - it is turned to the lens (55 %: she sings to someone; the eyes do the rest),
    ///   - on top, the quick movement the speaker's own head made with these gestures (nods, tilts) - that ties it to the arms,
    ///   - hands that meet or rise in front of her draw it towards the space between them. When and where is baked per clip by
    ///     MvSetup.BakeLook from the SOLVED pose (the hands as the contact solver leaves them) and eased in time: computed live it made the
    ///     head dart (103 deg/s), taken from the raw take it looked at hands that were no longer there.</summary>
    void MocapHead()
    {
        if (mocapNow <= 0.01f) return;
        var hd = B(HumanBodyBones.Head); var nk = B(HumanBodyBones.Neck); var ch = B(HumanBodyBones.UpperChest) ?? B(HumanBodyBones.Chest);
        Quaternion rootQ = transform.rotation, chestQ = ch.rotation; Vector3 eyeP = hd.position + transform.up * 0.06f;
        // what she looks at now (schedule; without one: the lens). The head turns over 0.6 s, the eyes get there in a sixth of a second.
        LookState(previewT, out int k0, out int k1, out float uHead, out float uEye);
        Quaternion goal = Quaternion.Slerp(HeadGoal(k0, hd, eyeP, rootQ), HeadGoal(k1, hd, eyeP, rootQ), uHead);
        eyeTgtW = Vector3.Lerp(EyeGoal(k0, hd), EyeGoal(k1, hd), uEye);
        micLookNow = micOn ? Mathf.Lerp(k0 == 0 ? 1f : 0f, k1 == 0 ? 1f : 0f, 0.5f * (uEye + uHead)) * mocapNow : 0f;      // the lids follow between the eyes and the head
        float mix = Mathf.Lerp(k0 == 0 && micOn ? 0.82f : 0.55f, k1 == 0 && micOn ? 0.82f : 0.55f, uHead);      // to the microphone she lowers her head herself; elsewhere the head half rides on the chest
        Quaternion baseQ = Quaternion.Slerp(chestQ, goal, mix);
        if (lookWNow > 0.01f)
        {
            Vector3 dc = Quaternion.Inverse(rootQ) * (lookTgtW - eyeP);
            float ly = Mathf.Clamp(Mathf.Atan2(dc.x, dc.z) * Mathf.Rad2Deg, -42f, 42f), lp = Mathf.Clamp(-Mathf.Atan2(dc.y, new Vector2(dc.x, dc.z).magnitude) * Mathf.Rad2Deg, -26f, 38f);
            baseQ = Quaternion.Slerp(baseQ, rootQ * Quaternion.Euler(lp, ly, 0), lookWNow * 0.6f);
        }
        Quaternion headQ = baseQ * Quaternion.Slerp(Quaternion.identity, headFastNow, 0.9f), neckQ = Quaternion.Slerp(chestQ, headQ, 0.42f);
        Quaternion hd0 = hd.rotation, nk0 = nk.rotation;
        nk.rotation = Quaternion.Slerp(nk0, neckQ, mocapNow); hd.rotation = Quaternion.Slerp(hd0, headQ, mocapNow);
    }

    [System.NonSerialized] public float micNearR;      // 0..1: how close the right hand of the gesture system is to its grip on the microphone
    [System.NonSerialized] public Vector3 eyeTgtW; [System.NonSerialized] public float micLookNow;      // 1 while her eyes are on the microphone

    /// <summary>The look schedule at time t: she turns from what she looked at (k0) to what she looks at now (k1).</summary>
    void LookState(float t, out int k0, out int k1, out float uHead, out float uEye)
    {
        k0 = k1 = 1; uHead = uEye = 1f;
        if (lookT == null || lookKind == null || lookT.Length == 0) return;
        int i = 0; while (i + 1 < lookT.Length && lookT[i + 1] <= t) i++;
        k1 = lookKind[Mathf.Min(i, lookKind.Length - 1)]; k0 = i > 0 ? lookKind[Mathf.Min(i - 1, lookKind.Length - 1)] : k1;
        float since = t - lookT[i]; uHead = SStep(0f, 0.85f, since); uEye = SStep(0.02f, 0.18f, since);
    }

    /// <summary>Where the head wants to face for a thing to look at (world rotation; the torso still takes 45 % of it back).
    /// 0 microphone: a little down, to the grille - 1 the lens - 2 / 3 the audience to her left / right - 4 far over their heads - 5 down to her side.</summary>
    Quaternion HeadGoal(int kind, Transform hd, Vector3 eyeP, Quaternion rootQ)
    {
        switch (kind)
        {
            case 0: if (!micOn) goto default; { Vector3 dm = Quaternion.Inverse(rootQ) * (stageOrigin + rootQ * micPos - eyeP); return rootQ * Quaternion.Euler(15f, Mathf.Clamp(Mathf.Atan2(dm.x, dm.z) * Mathf.Rad2Deg, -12f, 12f), 0); }
            case 2: return rootQ * Quaternion.Euler(-3f, -26f, 0);
            case 3: return rootQ * Quaternion.Euler(-3f, 26f, 0);
            case 4: return rootQ * Quaternion.Euler(micOn ? -5f : -10f, 5f, 0);      // (at the stand a chin lifted 10 deg takes the mouth off the microphone and the body after it)
            case 5: return rootQ * Quaternion.Euler(14f, -15f, 0);
            default:
            {
                Vector3 dv = Quaternion.Inverse(rootQ) * (ViewerNow(hd) - eyeP);
                float vy = Mathf.Clamp(Mathf.Atan2(dv.x, dv.z) * Mathf.Rad2Deg, -32f, 32f), vp = Mathf.Clamp(-Mathf.Atan2(dv.y, new Vector2(dv.x, dv.z).magnitude) * Mathf.Rad2Deg, -12f, 14f);
                return rootQ * Quaternion.Euler(vp, vy, 0);
            }
        }
    }

    /// <summary>The point in the world the eyes rest on for a thing to look at.</summary>
    Vector3 EyeGoal(int kind, Transform hd)
    {
        float hy = transform.InverseTransformPoint(hd.position).y; Quaternion rq = transform.rotation;
        switch (kind)
        {
            case 0: if (!micOn) goto default; return stageOrigin + rq * micPos;
            case 2: return stageOrigin + rq * new Vector3(-1.9f, hy + 0.05f, 3.2f);
            case 3: return stageOrigin + rq * new Vector3(1.9f, hy + 0.05f, 3.2f);
            case 4: return stageOrigin + rq * new Vector3(0.4f, hy + 0.65f, 4.5f);
            case 5: return stageOrigin + rq * new Vector3(-0.60f, hy - 0.80f, 1.3f);
            default: return ViewerNow(hd);
        }
    }

    /// <summary>For QA: wrist positions in character space at a clip time (call after ApplyBones).</summary>
    public void WristPositions(out Vector3 r, out Vector3 l)
    {
        r = transform.InverseTransformPoint(B(HumanBodyBones.RightHand).position); l = transform.InverseTransformPoint(B(HumanBodyBones.LeftHand).position);
    }
}
