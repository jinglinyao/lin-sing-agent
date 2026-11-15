using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Library clips UNDER the code gestures.
/// The Mixamo clips are baked onto this character as plain curves (MvSetup.BakeClips -> Assets/MV/Data/Clips/*.json): local
/// rotation of every body bone + the pelvis position, 60 fps. A schedule says which clip plays when and how much of the body
/// it owns:   lower = pelvis and legs (steps, stance, bounce),  torso = spine and chest (damped),  arms = shoulders to fingers.
/// With arms = 0 the code gestures keep the arms (and stay fitted to the coat); with arms = 1 the whole body is the clip.
/// Clips that travel (walk, strafe) are played in place and their travel is given to the character root instead, at the
/// clip's own stride speed, so the feet do not slide; when the clip fades out she stands, feet planted, where she arrived.
/// Still a pure function of the clip time.
/// </summary>
[Serializable]
public class ClipBake
{
    public string name; public int fps, count; public float length; public int[] bones; public float[] pos; public float[] rot;
    public float[] att, tgt, curl, hf;      // hf: the head's quick movement about its own slow direction (quaternion per frame)      // recorded (BEAT2) takes: per frame how much the hands draw her eyes (0..1), where (character space, 3 per frame), how closed each hand is (left, right; 0..1)
    [NonSerialized] public Vector3 travel; [NonSerialized] public bool travels; [NonSerialized] public Dictionary<int, int> index;
    [NonSerialized] public Quaternion[] meanRot; [NonSerialized] public Vector3 meanPos; [NonSerialized] public bool loops;      // loops: last pose = first pose      // the clip's average pose: what a movement is measured from when it is amplified
}

public partial class SingDriver
{
    [Header("Base layer: baked library clips under the gestures")]
    public TextAsset[] clipData;
    public string[] baseClip; public float[] baseStart, baseDur, baseSpeed, baseLower, baseTorso, baseArms, baseYaw, baseUp, baseGain, baseOff, baseFadeI;   // baseGain: the clip's movement made larger (1 = as recorded); baseOff: where in the clip to start (s)   // baseYaw: the clip turned about the vertical axis (walk diagonally)
    public float baseFade = 0.45f;
    public float baseFloorLift = 0.034f;
    [Range(0, 1)] public float headPerf = 0.55f;
    [Tooltip("How far the pelvis is stood up under a clip (0 = the clip's own crouch, 1 = fully upright).")]
    [Range(0, 1)] public float baseUpright = 0.6f;
    public Vector3 stageOrigin;                                   // where the character stands at t = 0 (world)

    [NonSerialized] Dictionary<string, ClipBake> clipLib;
    [NonSerialized] public float mocapNow, lookWNow, curlNowL = 0.3f, curlNowR = 0.3f; [NonSerialized] public Vector3 lookTgtW; [NonSerialized] public Quaternion headFastNow = Quaternion.identity;
    [NonSerialized] public Vector3 viewerW; [NonSerialized] public bool hasViewer;      // where the lens is (set by the capture, eased across cuts): the listener she sings to
    public Vector3 ViewerNow(Transform head) => hasViewer ? viewerW : stageOrigin + transform.rotation * new Vector3(0f, transform.InverseTransformPoint(head.position).y + 0.03f, 3.2f);      // review 2026-11-13: under a recorded take the legs, the fingers, the head and the eyes are ours
    [NonSerialized] float armsSafe;                               // how much of the arms comes from recorded (BEAT2) takes: those are solved again against her coat
    [NonSerialized] float armsFromClip; [NonSerialized] readonly List<KeyValuePair<float, Dictionary<int, Quaternion>>> armLayers = new List<KeyValuePair<float, Dictionary<int, Quaternion>>>();

    static readonly HumanBodyBones[] LowerSet = { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes };
    static readonly HumanBodyBones[] TorsoSet = { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest };
    static readonly HumanBodyBones[] HeadSet = { HumanBodyBones.Neck, HumanBodyBones.Head };

    void LoadClips()
    {
        if (clipLib != null) return;
        clipLib = new Dictionary<string, ClipBake>();
        if (clipData == null) return;
        foreach (var ta in clipData)
        {
            if (ta == null) continue;
            var c = JsonUtility.FromJson<ClipBake>(ta.text); if (c == null || c.count < 2) continue;
            int n = c.count - 1; Vector3 p0 = new Vector3(c.pos[0], 0, c.pos[2]), p1 = new Vector3(c.pos[n * 3], 0, c.pos[n * 3 + 2]);
            c.travel = p1 - p0; c.travels = c.travel.magnitude > 0.5f; if (!c.travels) c.travel = Vector3.zero;
            c.index = new Dictionary<int, int>(); for (int k = 0; k < c.bones.Length; k++) c.index[c.bones[k]] = k;
            int nb = c.bones.Length; c.meanRot = new Quaternion[nb];
            for (int k = 0; k < nb; k++)
            {
                Vector4 acc = Vector4.zero; Quaternion q0 = Q(c.rot, k * 4);
                for (int f = 0; f < c.count; f++) { Quaternion q = Q(c.rot, (f * nb + k) * 4); float sgn = Quaternion.Dot(q, q0) < 0 ? -1f : 1f; acc += new Vector4(q.x, q.y, q.z, q.w) * sgn; }
                acc.Normalize(); c.meanRot[k] = new Quaternion(acc.x, acc.y, acc.z, acc.w);
            }
            float dl = 0; for (int k = 0; k < nb; k++) dl += Quaternion.Angle(Q(c.rot, k * 4), Q(c.rot, ((c.count - 1) * nb + k) * 4)); c.loops = dl / nb < 4f;
            Vector3 mp = Vector3.zero; for (int f = 0; f < c.count; f++) mp += new Vector3(c.pos[f * 3], c.pos[f * 3 + 1], c.pos[f * 3 + 2]); c.meanPos = mp / c.count;
            clipLib[c.name] = c;
        }
    }

    public string ClipNames() { LoadClips(); return string.Join(" ", clipLib.Keys); }

    float BaseWeight(int i, float t)
    {
        float a = baseStart[i], b = a + baseDur[i]; if (t <= a || t >= b) return 0;
        float f = Mathf.Min(FadeOf(i), baseDur[i] * 0.4f);
        float u = Mathf.Clamp01((t - a) / f), v = Mathf.Clamp01((b - t) / f);
        return u * u * (3 - 2 * u) * v * v * (3 - 2 * v);
    }

    /// <summary>integral of BaseWeight from the start of entry i to t (how much "clip time at full weight" has passed): what the root has travelled</summary>
    float BaseWeightIntegral(int i, float t)
    {
        float a = baseStart[i], d = baseDur[i]; float x = Mathf.Clamp(t - a, 0, d); if (x <= 0) return 0;
        float f = Mathf.Min(FadeOf(i), d * 0.4f);
        // smoothstep integral over a fade of length f is f/2; inside the fade-in: f * (u^3 - u^4 / 2)
        float I(float s) { float u = Mathf.Clamp01(s / f); return f * (u * u * u - 0.5f * u * u * u * u); }
        if (x <= f) return I(x);
        if (x <= d - f) return 0.5f * f + (x - f);
        return 0.5f * f + (d - 2 * f) + (0.5f * f - I(d - x));
    }

    /// <summary>Where the character stands at time t, relative to stageOrigin (character space): the sum of what the travelling clips have covered.</summary>
    public Vector3 RootOffset(float t)
    {
        Vector3 o = Vector3.zero; if (baseClip == null) return o; LoadClips();
        for (int i = 0; i < baseClip.Length; i++)
            if (clipLib.TryGetValue(baseClip[i], out var c) && c.travels) o += Quaternion.Euler(0, YawOf(i), 0) * c.travel / c.length * baseSpeed[i] * BaseWeightIntegral(i, t);
        return o;
    }

    /// <summary>a walk that turns her needs longer to come in than a change of stance: nobody turns a quarter circle in half a second</summary>
    float FadeOf(int i) { if (baseFadeI != null && i < baseFadeI.Length && baseFadeI[i] > 0) return baseFadeI[i]; LoadClips(); return clipLib.TryGetValue(baseClip[i], out var c) && c.travels && Mathf.Abs(c.travel.x) > Mathf.Abs(c.travel.z) ? Mathf.Max(baseFade, 0.75f) : baseFade; }

    /// <summary>How far the pelvis is stood up under entry i: given in the schedule, else by kind - a dance keeps its own hips (0), a walk a little (0.3), a stance most (baseUpright).</summary>
    float UprightOf(int i, ClipBake c) => baseUp != null && i < baseUp.Length && baseUp[i] >= 0 ? baseUp[i] : c.travels ? 0.3f : baseArms[i] >= 0.5f ? 0f : baseUpright;

    [NonSerialized] float ampNow = 1f; [NonSerialized] public float baseLowerNow, baseTorsoNow;
    float GainOf(int i) => baseGain != null && i < baseGain.Length && baseGain[i] > 0 ? baseGain[i] : 1f;
    float OffOf(int i) => baseOff != null && i < baseOff.Length ? baseOff[i] : 0f;

    float YawOf(int i) => baseYaw != null && i < baseYaw.Length ? baseYaw[i] : 0f;

    static Quaternion Q(float[] a, int o) => new Quaternion(a[o], a[o + 1], a[o + 2], a[o + 3]);

    bool SampleClip(ClipBake c, float tc, HumanBodyBones b, out Quaternion q)
    {
        q = Quaternion.identity; if (!c.index.TryGetValue((int)b, out int k)) return false;
        float f = Mathf.Clamp(tc * c.fps, 0, c.count - 1); int i = (int)f, j = Math.Min(i + 1, c.count - 1); int nb = c.bones.Length;
        q = Quaternion.Slerp(Q(c.rot, (i * nb + k) * 4), Q(c.rot, (j * nb + k) * 4), f - i);
        if (ampNow != 1f) q = Quaternion.SlerpUnclamped(c.meanRot[k], q, ampNow);                 // a quiet groove made visible: every bone's swing about its average, enlarged
        return true;
    }

    /// <summary>Called after the procedural torso and legs are posed and before the arms are solved (their anchors must already be where the clip puts the torso).</summary>
    void ApplyBase(float t)
    {
        armsFromClip = 0; armsSafe = 0; armLayers.Clear(); baseLowerNow = baseTorsoNow = 0;
        mocapNow = 0; lookWNow = 0; float mAcc = 0, lAcc = 0, cL = 0, cR = 0; Vector3 tAcc = Vector3.zero; Vector4 hAcc = Vector4.zero; headFastNow = Quaternion.identity;
        transform.position = stageOrigin + transform.rotation * RootOffset(t);
        if (baseClip == null || baseClip.Length == 0) return; LoadClips();
        var hips = B(HumanBodyBones.Hips);
        for (int i = 0; i < baseClip.Length; i++)
        {
            float w = BaseWeight(i, t); if (w <= 0 || !clipLib.TryGetValue(baseClip[i], out var c)) continue;
            float local = OffOf(i) + (t - baseStart[i]) * baseSpeed[i], tc = c.travels || c.loops ? Mathf.Repeat(local, c.length) : Mathf.Clamp(local, 0f, c.length - 0.001f);      // a clip that does not loop is held on its last (or first) pose
            ampNow = c.travels ? 1f : GainOf(i); baseLowerNow = Mathf.Max(baseLowerNow, w * baseLower[i]); baseTorsoNow = Mathf.Max(baseTorsoNow, w * baseTorso[i]);
            float wl = w * baseLower[i], wt = w * baseTorso[i], wa = w * baseArms[i];
            bool mocap = c.name.StartsWith("b2_");
            if (mocap)
            {
                mocapNow = Mathf.Max(mocapNow, w);
                if (c.att != null && c.att.Length >= c.count)
                {
                    float ff = Mathf.Clamp(tc * c.fps, 0, c.count - 1); int fa = (int)ff, fb = Math.Min(fa + 1, c.count - 1); float fk = ff - fa;
                    float at = Mathf.Lerp(c.att[fa], c.att[fb], fk); mAcc += w; lAcc += w * at;
                    tAcc += w * at * Vector3.Lerp(new Vector3(c.tgt[fa * 3], c.tgt[fa * 3 + 1], c.tgt[fa * 3 + 2]), new Vector3(c.tgt[fb * 3], c.tgt[fb * 3 + 1], c.tgt[fb * 3 + 2]), fk);
                    cL += w * Mathf.Lerp(c.curl[fa * 2], c.curl[fb * 2], fk); cR += w * Mathf.Lerp(c.curl[fa * 2 + 1], c.curl[fb * 2 + 1], fk);
                    if (c.hf != null && c.hf.Length >= c.count * 4) { Quaternion hq = Quaternion.Slerp(Q(c.hf, fa * 4), Q(c.hf, fb * 4), fk); if (hq.w < 0) hq = new Quaternion(-hq.x, -hq.y, -hq.z, -hq.w); hAcc += w * new Vector4(hq.x, hq.y, hq.z, hq.w); }
                }
            }
            if (c.travels)
            {
                // the arm swing of a walk comes in late and goes out early: while she turns away from (or back to) the microphone the arms are still
                // the code arms, which know where the stand is
                float u0 = Mathf.Clamp01((t - baseStart[i] - 0.40f) / 0.50f), u1 = Mathf.Clamp01((baseStart[i] + baseDur[i] - 0.30f - t) / 0.45f);
                wa = baseArms[i] * u0 * u0 * (3 - 2 * u0) * u1 * u1 * (3 - 2 * u1);
            }
            if (wl > 0)
            {
                float f = Mathf.Clamp(tc * c.fps, 0, c.count - 1); int a = (int)f, b2 = Math.Min(a + 1, c.count - 1); float k = f - a;
                Vector3 p = Vector3.Lerp(new Vector3(c.pos[a * 3], c.pos[a * 3 + 1], c.pos[a * 3 + 2]), new Vector3(c.pos[b2 * 3], c.pos[b2 * 3 + 1], c.pos[b2 * 3 + 2]), k);
                if (ampNow != 1f) p = c.meanPos + (p - c.meanPos) * ampNow;
                p.y += baseFloorLift;                                                                 // the retarget stands her 3.4 cm into the floor (the boots have heels)
                p -= new Vector3(c.pos[0], 0, c.pos[2]) + c.travel * (tc / c.length);                 // in place: the travel belongs to the root
                Quaternion ry = Quaternion.Euler(0, YawOf(i), 0); p = ry * new Vector3(p.x, 0, p.z) + new Vector3(0, p.y, 0);
                p.x += bake != null ? bake.hipsRef.x : 0; p.z += bake != null ? bake.hipsRef.z : 0;
                hips.position = Vector3.Lerp(hips.position, transform.TransformPoint(p), wl);
                if (SampleClip(c, tc, HumanBodyBones.Hips, out var hq)) hips.rotation = Quaternion.Slerp(hips.rotation, transform.rotation * ry * hq, wl);
                foreach (var b in LowerSet) { var tr = B(b); if (tr != null && SampleClip(c, tc, b, out var q)) tr.localRotation = Quaternion.Slerp(tr.localRotation, q, wl); }
                // game mocap walks in a crouch, leaning forward. Keep where the clip puts the FEET, but stand the pelvis up (keep its turn, drop most of
                // its pitch and roll, lift it a little) and solve the legs again to those feet: the steps are the clip's, the carriage is a singer's.
                float upr = UprightOf(i, c);
                if (upr > 0)
                {
                    var lf = B(HumanBodyBones.LeftFoot); var rf = B(HumanBodyBones.RightFoot);
                    Vector3 lp = lf.position, rp = rf.position; Quaternion lr = lf.rotation, rr = rf.rotation;
                    Vector3 fwd = hips.rotation * Vector3.forward; fwd = Vector3.ProjectOnPlane(fwd, transform.up); Quaternion up = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd, transform.up) : transform.rotation;
                    hips.rotation = Quaternion.Slerp(hips.rotation, up, upr * wl);
                    hips.position += transform.up * 0.025f * upr * wl;
                    SolveLegTo(false, lp, lr); SolveLegTo(true, rp, rr);
                }
            }
            if (wt > 0)
            {
                foreach (var b in TorsoSet) { var tr = B(b); if (tr != null && SampleClip(c, tc, b, out var q)) tr.localRotation = Quaternion.Slerp(tr.localRotation, q, wt * 0.7f); }
                float ampKeep = ampNow; if (baseArms[i] >= 0.5f) ampNow *= headPerf;                  // crying / shouting clips throw the head about: keep the gesture, halve the swing
                if (!mocap) foreach (var b in HeadSet) { var tr = B(b); if (tr != null && SampleClip(c, tc, b, out var q)) tr.localRotation = Quaternion.Slerp(tr.localRotation, q, wt * 0.35f + wa * 0.5f); }      // a speaker's head looks at whom he talks to: not hers
                ampNow = ampKeep;
            }
            if (wa > 0)
            {
                // every clip that wants the arms is laid over what is there, in schedule order: two clips cross-fade, nothing switches
                armsFromClip = Mathf.Max(armsFromClip, wa); if (c.name.StartsWith("b2_")) armsSafe = Mathf.Max(armsSafe, wa); var layer = new Dictionary<int, Quaternion>();
                for (int bi = (int)HumanBodyBones.LeftShoulder; bi <= (int)HumanBodyBones.RightHand; bi++) if (SampleClip(c, tc, (HumanBodyBones)bi, out var q)) layer[bi] = q;
                if (!mocap) for (int bi = (int)HumanBodyBones.LeftThumbProximal; bi <= (int)HumanBodyBones.RightLittleDistal; bi++) if (SampleClip(c, tc, (HumanBodyBones)bi, out var q)) layer[bi] = q;
                armLayers.Add(new KeyValuePair<float, Dictionary<int, Quaternion>>(wa, layer));
            }
        }
        ampNow = 1f;
        if (mAcc > 1e-4f)
        {
            lookWNow = lAcc / mAcc * Mathf.Clamp01(mAcc); if (lAcc > 1e-4f) lookTgtW = transform.TransformPoint(tAcc / lAcc);
            curlNowL = cL / mAcc; curlNowR = cR / mAcc;
            if (hAcc.sqrMagnitude > 1e-8f) { hAcc.Normalize(); headFastNow = Quaternion.Slerp(Quaternion.identity, new Quaternion(hAcc.x, hAcc.y, hAcc.z, hAcc.w), Mathf.Clamp01(mAcc)); }
        }
    }

    struct ArmPose { public Quaternion sh, up, lo, ha, handW; public Vector3 wrist, elbow; public Quaternion[] fing; }

    ArmPose ReadArm(bool right)
    {
        var p = new ArmPose { fing = new Quaternion[15] };
        var sh = B(right ? HumanBodyBones.RightShoulder : HumanBodyBones.LeftShoulder); var up = B(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm); var lo = B(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm); var ha = B(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        p.sh = sh != null ? sh.localRotation : Quaternion.identity; p.up = up.localRotation; p.lo = lo.localRotation; p.ha = ha.localRotation; p.handW = ha.rotation; p.wrist = ha.position; p.elbow = lo.position;
        int baseId = (int)(right ? HumanBodyBones.RightThumbProximal : HumanBodyBones.LeftThumbProximal);
        for (int i = 0; i < 15; i++) { var t = B((HumanBodyBones)(baseId + i)); p.fing[i] = t != null ? t.localRotation : Quaternion.identity; }
        return p;
    }

    void WriteArm(bool right, ArmPose p)
    {
        var sh = B(right ? HumanBodyBones.RightShoulder : HumanBodyBones.LeftShoulder); var up = B(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm); var lo = B(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm); var ha = B(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        if (sh != null) sh.localRotation = p.sh; up.localRotation = p.up; lo.localRotation = p.lo; ha.localRotation = p.ha;
    }

    /// <summary>After the arms are solved. A recorded take owns the arms (through the contact pass, ClipArmsSafe) except where the gesture
    /// system has been given one (OwnNow): there the hand goes from wherever the take has it to the authored pose in ONE reach.</summary>
    void ApplyBaseArms()
    {
        if (armsFromClip <= 0) return;
        float sR = OwnNow(true, previewT, out float gR, out bool kR), sL = OwnNow(false, previewT, out float gL, out bool kL);
        ArmPose rigR = default, rigL = default; if (sR > 0f) rigR = ReadArm(true); if (sL > 0f) rigL = ReadArm(false);      // the gesture system's arms, as solved just now
        foreach (var layer in armLayers) foreach (var kv in layer.Value)
        {
            var tr = B((HumanBodyBones)kv.Key); if (tr == null) continue;
            tr.localRotation = Quaternion.Slerp(tr.localRotation, kv.Value, layer.Key);
        }
        ClipArmsSafe();
        if (sR > 0f) ReachBlend(true, rigR, sR, gR, kR);
        if (sL > 0f) ReachBlend(false, rigL, sL, gL, kL);
    }

    /// <summary>The arm between the recorded take (on the bones now) and the gesture system's pose 'rig', s of the way there. The blend is
    /// made where the hand IS - wrist position, hand rotation, elbow side - and the arm is solved to that; a hand that takes hold of
    /// something goes first to a point beside it on its own side and then in, any other hand rises 2 cm on the way. The fingers of a gripping hand open
    /// early in the reach and close by 'grip'. (Before: joint rotations were cross-faded while the gesture system itself was still
    /// travelling from its rest pose - the hand backed off 9 cm, stopped beside the microphone for 0.3 s and covered the last 10 cm in
    /// three frames. Review 2026-11-13: "it appears there as if teleported".)</summary>
    void ReachBlend(bool right, ArmPose rig, float s, float grip, bool isGrip)
    {
        int side = right ? 0 : 1; float sx = right ? 1f : -1f;
        var sh = B(right ? HumanBodyBones.RightShoulder : HumanBodyBones.LeftShoulder); var up = B(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm); var lo = B(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm); var ha = B(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        float cc0 = Mathf.Clamp01(right ? curlNowR : curlNowL); float[] cm = ClipCurl(cc0), cg = rigCurl[side], c = new float[5];
        for (int i = 0; i < 5; i++)
        {
            if (isGrip) { float open = i == 0 ? Mathf.Min(0f, cg[i]) : 0.10f; c[i] = Mathf.Lerp(Mathf.Lerp(cm[i], open, SStep(0.10f, 0.50f, s)), cg[i], grip); }
            else c[i] = Mathf.Lerp(cm[i], cg[i], s);
        }
        float spread = Mathf.Lerp(0.08f * (1f - cc0), rigSpread[side], s);
        if (s >= 0.9995f) { WriteArm(right, rig); Fingers(right, c, spread); return; }
        Vector3 Wm = ha.position, Em = lo.position; Quaternion Rm = ha.rotation;
        if (sh != null) sh.localRotation = Quaternion.Slerp(sh.localRotation, rig.sh, s);
        Vector3 S = up.position, W;
        if (isGrip)
        {
            // to a point 10 cm beside the grip - on the hand's own side, level with it - and from there straight in; the two legs overlap, so
            // the corner is round. (One straight line from a hand that hung low came up THROUGH the clip: 15 mm, three frames, on leaving.)
            Vector3 via = rig.wrist + transform.TransformDirection(new Vector3(0.90f * sx, -0.10f, 0.42f).normalized) * 0.10f;
            float e1 = s < 0.85f ? 1f - (1f - s / 0.85f) * (1f - s / 0.85f) : 1f;
            W = Vector3.Lerp(Wm, via, e1) + (rig.wrist - via) * SStep(0.55f, 1f, s);
        }
        else W = Vector3.Lerp(Wm, rig.wrist, s) + transform.up * (0.02f * Mathf.Sin(Mathf.PI * s));
        Quaternion R = Quaternion.Slerp(Rm, rig.handW, isGrip ? SStep(0f, 0.80f, s) : s), rinv = Quaternion.Inverse(transform.rotation);
        Vector3 dir = (W - S).normalized, pole = Vector3.Slerp((Em - S).normalized, (rig.elbow - S).normalized, s); pole -= Vector3.Dot(pole, dir) * dir;
        var h = new HandTarget { pos = W, pole = pole.sqrMagnitude > 1e-8f ? transform.InverseTransformDirection(pole.normalized) : Vector3.down, finger = rinv * R * (right ? Vector3.right : Vector3.left), palm = rinv * R * Vector3.down, curl = c, spread = spread,
            micAvoid = isGrip ? 1f - SStep(0.35f, 0.80f, s) : 1f, bodyAvoid = isGrip ? Mathf.Lerp(1f, 0.1f, SStep(0.75f, 1f, s)) : 1f };
        SolveArmSafe(right, ref h, true);
        float k = SStep(0.90f, 1f, s);                       // the last tenth: exactly into the gesture system's own solve
        if (k > 0f) { up.localRotation = Quaternion.Slerp(up.localRotation, rig.up, k); lo.localRotation = Quaternion.Slerp(lo.localRotation, rig.lo, k); ha.localRotation = Quaternion.Slerp(ha.localRotation, rig.ha, k); }
        Fingers(right, c, spread);
    }

    void SolveLegTo(bool right, Vector3 target, Quaternion footRot)
    {
        var upId = right ? HumanBodyBones.RightUpperLeg : HumanBodyBones.LeftUpperLeg; var loId = right ? HumanBodyBones.RightLowerLeg : HumanBodyBones.LeftLowerLeg; var ftId = right ? HumanBodyBones.RightFoot : HumanBodyBones.LeftFoot;
        var up = B(upId); var lo = B(loId); var ft = B(ftId); if (up == null || lo == null || ft == null) return;
        float a = Vector3.Distance(up.position, lo.position), b = Vector3.Distance(lo.position, ft.position);
        Vector3 S = up.position, st = target - S; float d = Mathf.Min(st.magnitude, (a + b) * 0.9995f); Vector3 dir = st.normalized;
        float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1f, 1f), sinA = Mathf.Sqrt(1 - cosA * cosA);
        Vector3 pole = B(HumanBodyBones.Hips).rotation * Vector3.forward + transform.right * (right ? 0.10f : -0.10f), perp = (pole - Vector3.Dot(pole, dir) * dir).normalized;
        AimBone(up, lo, upId, a * (cosA * dir + sinA * perp));
        AimBone(lo, ft, loId, S + dir * d - lo.position);
        ft.rotation = footRot;
    }

    int BaseHash()
    {
        unchecked
        {
            int h = 19; int n = baseClip != null ? baseClip.Length : 0;
            for (int i = 0; i < n; i++) h = (((h * 31 + baseClip[i].GetHashCode()) * 31 + baseStart[i].GetHashCode()) * 31 + baseDur[i].GetHashCode()) * 31 + (baseSpeed[i] + 3 * baseLower[i] + 7 * baseTorso[i] + 11 * baseArms[i] + 13 * YawOf(i) + 19 * GainOf(i) + 23 * OffOf(i) + 29 * FadeOf(i) + 17 * (baseUp != null && i < baseUp.Length ? baseUp[i] : -1)).GetHashCode();
            return (((h * 31 + baseUpright.GetHashCode()) * 31 + micHold.GetHashCode()) * 31 + micPos.GetHashCode() + (micOn ? 5 : 0)) * 31 + micDir.GetHashCode() * 7 + micGrip.GetHashCode();
        }
    }
}
