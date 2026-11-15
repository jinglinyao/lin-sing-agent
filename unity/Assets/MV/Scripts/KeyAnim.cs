using System;
using UnityEngine;

/// <summary>
/// Hand-keyed animation (2026-11-13). The performance is written as key poses on the beats (AgentScripts/keyanim.py and the dope sheet of the
/// piece), interpolated by curves and baked to ONE VALUE PER FRAME for every control of an animator's rig; this file only plays that table.
/// Nothing is generated here: no noise, no solver that decides anything. The controls (NCH floats per frame):
///    0- 2  hips offset (m, character space)          3- 5  hips rotation (pitch, yaw, roll, deg)
///    6- 8  torso (pitch = lean forward, yaw = twist, roll = side bend; deg, spread over spine / chest / upper chest)
///    9-11  head (pitch = down, yaw, roll; deg, relative to the chest, 40 % of it at the neck)
///   12,13  shoulder shrug right, left (deg)
///   14-31  right hand: wrist position (3), finger direction (3), palm normal (3), elbow side (3), finger closure thumb..little (5), spread (1)
///   32-49  left hand, the same
///   50,51  right / left hand rides with the chest (1) or stays in the room (0)
///   52     right hand on the microphone (0..1: the wrist is drawn onto the fitted grip)       53  left hand on the stand
///   54,55  right / left fingers closed to the fitted grip (0..1)
///   56     mouth held at the microphone (0..1): a constraint, as an animator's rig has them - the waist leans in, the pelvis gives the rest
/// Positions and directions are in character space (+x her right, +y up, +z forward). The feet stay planted; the knees follow the pelvis.
/// </summary>
public partial class SingDriver
{
    public const int KeyNCH = 57;
    [Header("Hand-keyed animation")]
    [Tooltip("Play the baked control table instead of the gesture system / the recorded takes.")]
    public bool keyOn;
    [Tooltip("The control table (float32: fps, frames, channels, start time, then frames x channels), written by AgentScripts/keyanim.py. Path relative to the project folder.")]
    public string keyClipPath = "";
    [NonSerialized] float[] keyData; [NonSerialized] int keyN; [NonSerialized] float keyFps, keyT0; [NonSerialized] string keyLoaded;
    [NonSerialized] bool keyRestReady; [NonSerialized] Vector3[] keyFootPos = new Vector3[2]; [NonSerialized] Quaternion[] keyFootRot = new Quaternion[2];
    [NonSerialized] Vector3 keyChestPos0; [NonSerialized] Quaternion keyChestRot0;
    readonly float[] kc = new float[KeyNCH];

    public void ReloadKeyClip() { keyLoaded = null; keyData = null; keyRestReady = false; }

    bool KeyReady()
    {
        if (!keyOn || string.IsNullOrEmpty(keyClipPath)) return false;
        if (keyData != null && keyLoaded == keyClipPath) return true;
        try
        {
            var bytes = System.IO.File.ReadAllBytes(keyClipPath); int n = bytes.Length / 4; if (n < 4) return false;
            var f = new float[n]; Buffer.BlockCopy(bytes, 0, f, 0, n * 4);
            keyFps = f[0]; keyN = (int)f[1]; keyT0 = f[3];
            if ((int)f[2] != KeyNCH || n < 4 + keyN * KeyNCH) { Debug.LogWarning("key clip: wrong layout " + keyClipPath); return false; }
            keyData = f; keyLoaded = keyClipPath; return true;
        }
        catch (Exception e) { Debug.LogWarning("key clip: " + e.Message); return false; }
    }

    /// <summary>The rest pose in numbers (character space), read once: where the feet stand, where the chest is.</summary>
    void KeyRest()
    {
        foreach (var kv in rest) { var tr = animator.GetBoneTransform((HumanBodyBones)kv.Key); if (tr != null) tr.localRotation = kv.Value; }
        var hips = B(HumanBodyBones.Hips); if (hasRestHips) hips.localPosition = restHipsPos;
        if (keyRestReady) return;
        Quaternion rinv = Quaternion.Inverse(transform.rotation);
        for (int s = 0; s < 2; s++) { var ft = B(s == 0 ? HumanBodyBones.RightFoot : HumanBodyBones.LeftFoot); keyFootPos[s] = transform.InverseTransformPoint(ft.position); keyFootRot[s] = rinv * ft.rotation; }
        var ch = B(HumanBodyBones.UpperChest) ?? B(HumanBodyBones.Chest); keyChestPos0 = transform.InverseTransformPoint(ch.position); keyChestRot0 = rinv * ch.rotation;
        keyRestReady = true;
    }

    void KeyRot(HumanBodyBones b, float pitch, float yaw, float roll) { var t = B(b); if (t != null) t.localRotation = Rest(b) * Quaternion.Euler(pitch, yaw, roll); }

    void ApplyKeyed(float t)
    {
        transform.position = stageOrigin; previewT = t;
        float fr = Mathf.Clamp((t - keyT0) * keyFps, 0, keyN - 1); int i0 = (int)fr, i1 = Math.Min(i0 + 1, keyN - 1); float k = fr - i0;
        for (int c = 0; c < KeyNCH; c++) kc[c] = Mathf.Lerp(keyData[4 + i0 * KeyNCH + c], keyData[4 + i1 * KeyNCH + c], k);
        KeyRest();
        // ---- pelvis, torso, head
        var hips = B(HumanBodyBones.Hips); hips.position += transform.TransformVector(new Vector3(kc[0], kc[1], kc[2]));
        KeyRot(HumanBodyBones.Hips, kc[3], kc[4], kc[5]);
        bool hasUC = B(HumanBodyBones.UpperChest) != null; float a = hasUC ? 0.40f : 0.50f, b = hasUC ? 0.35f : 0.50f, c2 = 0.25f;
        KeyRot(HumanBodyBones.Spine, kc[6] * a, kc[7] * a, kc[8] * a); KeyRot(HumanBodyBones.Chest, kc[6] * b, kc[7] * b, kc[8] * b);
        if (hasUC) KeyRot(HumanBodyBones.UpperChest, kc[6] * c2, kc[7] * c2, kc[8] * c2);
        KeyRot(HumanBodyBones.Neck, kc[9] * 0.4f, kc[10] * 0.4f, kc[11] * 0.4f); KeyRot(HumanBodyBones.Head, kc[9] * 0.6f, kc[10] * 0.6f, kc[11] * 0.6f);
        var rs = B(HumanBodyBones.RightShoulder); var ls = B(HumanBodyBones.LeftShoulder);
        if (rs != null) rs.localRotation = Rest(HumanBodyBones.RightShoulder) * Quaternion.Euler(0, 0, kc[12]);
        if (ls != null) ls.localRotation = Rest(HumanBodyBones.LeftShoulder) * Quaternion.Euler(0, 0, -kc[13]);
        // ---- the mouth at the microphone (constraint), then the legs under the pelvis
        KeyMouthHold(kc[56]);
        for (int s = 0; s < 2; s++) SolveLegTo(s == 0, transform.TransformPoint(keyFootPos[s]), transform.rotation * keyFootRot[s]);
        // ---- arms
        var ch = B(HumanBodyBones.UpperChest) ?? B(HumanBodyBones.Chest); Quaternion rinv = Quaternion.Inverse(transform.rotation);
        Quaternion dq = (rinv * ch.rotation) * Quaternion.Inverse(keyChestRot0);                  // what the chest has turned, character space
        Vector3 chNow = transform.InverseTransformPoint(ch.position);
        for (int s = 0; s < 2; s++)
        {
            bool right = s == 0; int o = 14 + s * 18; float sx = right ? 1f : -1f, wCh = Mathf.Clamp01(kc[50 + s]), wGrip = Mathf.Clamp01(kc[52 + s]), wClose = Mathf.Clamp01(kc[54 + s]);
            Vector3 p = new Vector3(kc[o], kc[o + 1], kc[o + 2]), fin = new Vector3(kc[o + 3], kc[o + 4], kc[o + 5]), palm = new Vector3(kc[o + 6], kc[o + 7], kc[o + 8]), pole = new Vector3(kc[o + 9], kc[o + 10], kc[o + 11]);
            if (wCh > 0f)
            {
                Quaternion q = Quaternion.Slerp(Quaternion.identity, dq, wCh);
                p = Vector3.Lerp(p, chNow + dq * (p - keyChestPos0), wCh); fin = q * fin; palm = q * palm; pole = q * pole;
            }
            var curl = new float[5]; for (int f = 0; f < 5; f++) curl[f] = kc[o + 12 + f];
            if (wGrip > 0f || wClose > 0f)
            {
                Vector3 gp, gf, gn; float[] gc;
                if (right) { MicGripPose(sx, out gp, out gf, out gn); gc = micCurl; } else { PoleGripPose(sx, out gp, out gf, out gn); gc = poleCurl; }
                p = Vector3.Lerp(p, gp, wGrip); fin = Vector3.Slerp(fin.normalized, gf, wGrip); palm = Vector3.Slerp(palm.normalized, gn, wGrip);
                if (gc != null && gc.Length >= 5) for (int f = 0; f < 5; f++) curl[f] = Mathf.Lerp(curl[f], gc[f], wClose);
            }
            fin = fin.sqrMagnitude > 1e-8f ? fin.normalized : Vector3.forward; palm = palm.sqrMagnitude > 1e-8f ? palm.normalized : Vector3.down; pole = pole.sqrMagnitude > 1e-8f ? pole.normalized : Vector3.down;
            SolveArm(right, new HandTarget { pos = transform.TransformPoint(p), pole = pole, finger = fin, palm = palm, curl = curl, spread = kc[o + 17], micAvoid = 0f, bodyAvoid = 0f });
            if (wClose > 0f) FingersJoint(right, right ? micCurl3 : poleCurl3, wClose);      // the fitted grip, joint by joint (when there is one)
        }
        // ---- eyes: the look schedule (the head itself is keyed)
        var hd = B(HumanBodyBones.Head); mocapNow = 1f; lookWNow = 0f;
        LookState(t, out int k0, out int k1, out float uHead, out float uEye);
        eyeTgtW = Vector3.Lerp(EyeGoal(k0, hd), EyeGoal(k1, hd), uEye);
        micLookNow = micOn ? Mathf.Lerp(k0 == 0 ? 1f : 0f, k1 == 0 ? 1f : 0f, 0.5f * (uEye + uHead)) : 0f;
    }

    /// <summary>The mouth stays at the microphone (weight w): she leans in from the waist (up to 10 deg), the pelvis slides the last 3 - 4 cm.</summary>
    void KeyMouthHold(float w)
    {
        if (!micOn || w <= 0.001f) return;
        var hd = B(HumanBodyBones.Head); var hp = B(HumanBodyBones.Hips); var spn = B(HumanBodyBones.Spine); if (hd == null || hp == null || spn == null) return;
        Quaternion rinv = Quaternion.Inverse(transform.rotation); Vector3 want = stageOrigin + transform.rotation * (micPos + micMouth);
        Vector3 Mouth() => hd.position + hd.rotation * Quaternion.Inverse(Rest(HumanBodyBones.Head)) * mouthLocal;
        float leanSum = 0f;
        for (int pass = 0; pass < 2; pass++)
        {
            Vector3 m = Mouth(), e0 = rinv * (want - m); float lever = Mathf.Max(0.25f, m.y - spn.position.y);
            float ang = Mathf.Clamp(leanSum + Mathf.Atan2(e0.z, lever) * Mathf.Rad2Deg * 0.85f * w, -3f, 10f) - leanSum;
            spn.rotation = Quaternion.AngleAxis(ang, transform.right) * spn.rotation; hd.rotation = Quaternion.AngleAxis(-0.75f * ang, transform.right) * hd.rotation; leanSum += ang;
        }
        Vector3 e = rinv * (want - Mouth()); float above = -e.y;
        e.y = above < -0.003f ? -above - 0.003f : above > micBand ? micBand - above : 0f;
        e.x = Mathf.Clamp(e.x, -0.04f, 0.04f); e.y = Mathf.Clamp(e.y, -0.08f, 0.0f); e.z = Mathf.Clamp(e.z, -0.03f, 0.03f);
        hp.position += transform.rotation * (e * w);
    }
}
