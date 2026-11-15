using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Plays a baked lip-sync track (blendshape weights per frame, made by tools/make_lipsync.py) on a VRM face and adds a
/// simple procedural singing pose (arms, sway, head, breathing, blinks). Everything is a pure function of the clip
/// time, so the same frame can be reproduced in edit mode, in play mode or in an offline capture.
/// Face weights are written in LateUpdate after Vrm10Instance (order 11000) so the VRM expression pass does not undo them.
/// </summary>
[Serializable]
public class LipTrack
{
    public int fps; public int count; public float t0;
    public string[] names; public float[] w; public float[] open; public float[] blinks;
    public float[] openSlow;                    // phrase-level loudness (what the head follows)
    public float[] breath;                      // audible breath between phrases 0..1
    public float[] gaze, gazeSlow;              // per frame: yaw, pitch in degrees (yaw + = her right, pitch + = down)
    public float[] beats;                       // times of the musical pulse (every second beat)
    public float[] groove;                      // per frame 0..1: how strongly the body moves with the pulse
}

[ExecuteAlways]
[DefaultExecutionOrder(30000)]
public partial class SingDriver : MonoBehaviour
{
    public TextAsset lipJson;
    [NonSerialized] public string lipPath;          // lin-sing standalone player: face track read from a file (takes precedence over lipJson)
    public AudioSource audioSource;
    public SkinnedMeshRenderer face;
    public Animator animator;
    [Tooltip("Clip time used in edit mode, or in play mode when Use Preview Time is on (offline capture).")]
    public float previewTime;
    public bool usePreviewTime;
    [Tooltip("Strength of the brow and eye expression shapes of the face track (1 = as authored). The sorrow / anger of song2 is authored at 0.2 - 0.4: from the front it read as a faint smile.")]
    public float emoGain = 1f;
    [Range(0, 1.5f)] public float motion = 1f;
    [Range(0, 1.5f)] public float mouth = 1f;
    [Header("Body from animation clips (humanoid). Empty = procedural pose.")]
    public AnimationClip[] bodyClips;
    [Tooltip("Clip time (s) at which each body clip takes over; the first one should be 0.")]
    public float[] bodyStart;
    public float bodyFade = 0.6f;
    [Range(0, 1.5f)] public float bodySpeed = 1f;
    [Header("Head and eyes")]
    [Tooltip("How strongly the head is held upright and facing forward over a body clip (0 = clip's own head motion, 1 = locked).")]
    [Range(0, 1)] public float headDamp = 0.75f;
    [Tooltip("The model's look-at range (VRM lookAt range maps): eye-bone degrees for a gaze 90 degrees inward / outward / down / up, and how much stronger than that map the eyes turn.")]
    public float eyeIn = 8f, eyeOut = 12f, eyeDown = 12f, eyeUp = 13.25f, eyeReach = 2f;
    [Tooltip("Upper lids under a recorded take: at rest (never the round, startled eye) and while she looks down at the microphone (0..1 of fully shut).")]
    public float lidRest = 0.14f, lidMic = 0.42f;
    [Tooltip("How far the head turns with the eyes (fraction of the gaze angle).")]
    [Range(0, 1)] public float headFollow = 0.35f;
    [Range(0, 2)] public float eyeGain = 1f;

    // rest pose, captured once while the model is still in its T-pose
    [SerializeField] List<int> restBone = new List<int>();
    [SerializeField] List<Quaternion> restRot = new List<Quaternion>();

    // not serialized: after a script reload Unity would otherwise bring these back half-empty
    [NonSerialized] LipTrack track; [NonSerialized] int[] shapeIndex;
    [NonSerialized] int blinkIndex = -1, browIndex = -1, eyeJoyIndex = -1;
    readonly Dictionary<int, Quaternion> rest = new Dictionary<int, Quaternion>();
    readonly HashSet<int> touched = new HashSet<int>();

    [NonSerialized] int clockFrame = -1; [NonSerialized] double clockT;
    public float ClipTime
    {
        get
        {
            if (!Application.isPlaying || usePreviewTime) return previewTime;
            if (audioSource != null && audioSource.clip != null)
            {
                // The audio position moves in blocks of one mixer buffer (1024 samples = 21 ms): taken as it is, the picture advances in
                // steps that do not match the frames - every movement judders. The clock runs on frame time and is only steered by the audio.
                if (clockFrame != Time.frameCount)
                {
                    double au = (double)audioSource.timeSamples / audioSource.clip.frequency;
                    if (clockFrame < 0 || !audioSource.isPlaying || System.Math.Abs(au - clockT) > 0.10) clockT = au;      // start, pause, seek
                    else { clockT += Time.unscaledDeltaTime * audioSource.pitch; clockT += (au - clockT) * 0.04; }
                    clockFrame = Time.frameCount;
                }
                return (float)clockT;
            }
            return Time.time;
        }
    }

    public void CaptureRest()
    {
        restBone.Clear(); restRot.Clear();
        foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (b == HumanBodyBones.LastBone) continue;
            var t = animator.GetBoneTransform(b);
            if (t == null) continue;
            restBone.Add((int)b); restRot.Add(t.localRotation);
        }
        var hipsT = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hipsT != null) { restHipsPos = hipsT.localPosition; hasRestHips = true; }
        rest.Clear(); bake = null;
    }

    void Load()
    {
        bool fromFile = !string.IsNullOrEmpty(lipPath) && System.IO.File.Exists(lipPath);      // the standalone player reads face.json from the song folder
        if (track == null && (lipJson != null || fromFile))
        {
            track = JsonUtility.FromJson<LipTrack>(fromFile ? System.IO.File.ReadAllText(lipPath) : lipJson.text);
            var mesh = face.sharedMesh;
            shapeIndex = new int[track.names.Length];
            for (int i = 0; i < shapeIndex.Length; i++) shapeIndex[i] = mesh.GetBlendShapeIndex(track.names[i]);
            blinkIndex = mesh.GetBlendShapeIndex("Fcl_EYE_Close");
            browIndex = mesh.GetBlendShapeIndex("Fcl_BRW_Sorrow");
            eyeJoyIndex = mesh.GetBlendShapeIndex("Fcl_EYE_Joy");
        }
        if (rest.Count == 0) for (int i = 0; i < restBone.Count; i++) rest[restBone[i]] = restRot[i];
    }

    /// <summary>Reload the track from lipJson (after it was swapped) and list the shapes the model lacks.</summary>
    public string MissingShapesFresh() { track = null; return MissingShapes(); }

    public string MissingShapes()
    {
        Load(); var s = new List<string>();
        for (int i = 0; i < shapeIndex.Length; i++) if (shapeIndex[i] < 0) s.Add(track.names[i]);
        return string.Join(",", s);
    }

    void Update() { if (Application.isPlaying) ApplyBones(ClipTime); }
    void LateUpdate() { if (Application.isPlaying) ApplyFace(ClipTime); }

    /// <summary>Pose the whole character at a clip time (edit mode / offline capture).</summary>
    public void Apply(float t) { previewTime = t; ApplyBones(t); ApplyFace(t); }

    // ------------------------------------------------------------------ face
    float Sample(float[] a, int stride, int ch, float t)
    {
        float f = Mathf.Clamp(t * track.fps, 0, track.count - 1);
        int i = Mathf.FloorToInt(f); int j = Mathf.Min(i + 1, track.count - 1); float k = f - i;
        return Mathf.Lerp(a[i * stride + ch], a[j * stride + ch], k);
    }

    public void ApplyFace(float t)
    {
        Load(); if (track == null || face == null) return;
        int n = track.names.Length;
        for (int c = 0; c < n; c++)
            if (shapeIndex[c] >= 0) face.SetBlendShapeWeight(shapeIndex[c], Mathf.Min(100f, (emoGain != 1f && (track.names[c].StartsWith("Fcl_BRW_") || track.names[c] == "Fcl_EYE_Sorrow" || track.names[c] == "Fcl_EYE_Angry") ? emoGain : 1f) * 100f * mouth * Sample(track.w, n, c, t) * (mocapNow > 0.01f && (track.names[c] == "Fcl_EYE_Spread" || track.names[c] == "Fcl_EYE_Surprised") ? 1f - Mathf.Max(0.5f, micLookNow) * mocapNow : 1f)));
        if (Array.IndexOf(track.names, "Fcl_EYE_Close") < 0)
        {
            // old tracks without baked eyelids: blinks only (60 ms down, 40 ms shut, 110 ms up)
            float blink = 0;
            foreach (var b in track.blinks)
            {
                float d = t - b; if (d < 0 || d > 0.21f) continue;
                blink = d < 0.06f ? d / 0.06f : d < 0.10f ? 1f : 1f - (d - 0.10f) / 0.11f;
            }
            if (blinkIndex >= 0) face.SetBlendShapeWeight(blinkIndex, 100f * Mathf.Max(blink, 0.08f));
        }
        if (bake != null && blinkIndex >= 0)
        {
            float gb = 0;
            foreach (var b in bake.blinks) { float d = t - b; if (d < 0 || d > 0.21f) continue; gb = d < 0.06f ? d / 0.06f : d < 0.10f ? 1f : 1f - (d - 0.10f) / 0.11f; }
            if (gb > 0) face.SetBlendShapeWeight(blinkIndex, Mathf.Max(face.GetBlendShapeWeight(blinkIndex), 100f * mouth * gb));
        }
        // eyes: written here (after the VRM look-at pass) so they are not reset
        if (animator != null && track.gaze != null && track.gaze.Length >= 2 * track.count)
        {
            float yaw = Sample(track.gaze, 2, 0, t) * eyeGain, pitch = Sample(track.gaze, 2, 1, t) * eyeGain;
            Transform hdT = mocapNow > 0.01f ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            foreach (var eb in new[] { HumanBodyBones.LeftEye, HumanBodyBones.RightEye })
            {
                var e = animator.GetBoneTransform(eb); if (e == null) continue;
                float y = yaw, p = pitch;
                if (hdT != null)
                {
                    // eyes that hold a place in the world, not an angle in the head: the listener in front of the stage (the head may be turned,
                    // the eyes stay on him), or what the head has turned to. The track's own glances stay as small movements on top.
                    Vector3 tg = Vector3.Lerp(eyeTgtW, lookTgtW, lookWNow * 0.6f);
                    var le = animator.GetBoneTransform(HumanBodyBones.LeftEye); var re = animator.GetBoneTransform(HumanBodyBones.RightEye);
                    Vector3 between = le != null && re != null ? (le.position + re.position) * 0.5f : e.position;
                    Vector3 dl = Quaternion.Inverse(hdT.rotation) * (tg - between);
                    // gaze angle to the target -> rotation of the eye bone, by the model's own look-at range (VRM: a gaze 90 deg off turns the eye
                    // 8 deg inward, 12 outward, 12 down, 13 up). Taken twice as strongly so that a look reads at a distance, never past 90 % of
                    // those limits: turned by the gaze angle itself (23 deg down at the microphone) the iris went under the lid and the eye was blank.
                    float ga = Mathf.Atan2(dl.x, dl.z) * Mathf.Rad2Deg, gp = -Mathf.Atan2(dl.y, new Vector2(dl.x, dl.z).magnitude) * Mathf.Rad2Deg;
                    bool outward = (ga > 0f) == (eb == HumanBodyBones.RightEye);
                    float limY = (outward ? eyeOut : eyeIn) * 0.9f, limP = (gp > 0f ? eyeDown : eyeUp) * 0.9f;
                    float ya = Mathf.Clamp(ga * (outward ? eyeOut : eyeIn) / 90f * eyeReach, -limY, limY), pa = Mathf.Clamp(gp * (gp > 0f ? eyeDown : eyeUp) / 90f * eyeReach, -limP, limP);
                    // Looking at the microphone is not found by geometry (the eye bones sit deep in the head: the angle to a microphone at the
                    // lips is only 25 deg, which turned the eyes 7 deg and left the lids open - she stared ahead). It is a pose: eyes nearly as
                    // far down as they go, no sideways glance, upper lids well down.
                    ya = Mathf.Lerp(ya, ya * 0.35f, micLookNow); pa = Mathf.Lerp(pa, eyeDown * 0.88f, micLookNow);
                    y = Mathf.Lerp(yaw, ya + 0.3f * yaw * (1f - 0.6f * micLookNow), mocapNow); p = Mathf.Lerp(pitch, pa + 0.3f * pitch * (1f - 0.6f * micLookNow), mocapNow);
                    if (blinkIndex >= 0) face.SetBlendShapeWeight(blinkIndex, Mathf.Max(face.GetBlendShapeWeight(blinkIndex), 100f * Mathf.Lerp(lidRest, lidMic, micLookNow) * mocapNow));
                    // a lowered gaze takes the upper lids down with it
                    if (blinkIndex >= 0) face.SetBlendShapeWeight(blinkIndex, Mathf.Max(face.GetBlendShapeWeight(blinkIndex), 100f * 0.26f * Mathf.Clamp01((gp - 12f) / 30f) * mocapNow));
                }
                e.localRotation = Rest(eb) * Quaternion.Euler(p, y, 0);
            }
        }
    }

    float Slow(float t)
    {
        if (track == null) return 0;
        return track.openSlow != null && track.openSlow.Length >= track.count ? Sample(track.openSlow, 1, 0, t) : Sample(track.open, 1, 0, t);
    }

    Vector2 GazeSlow(float t)
    {
        if (track == null || track.gazeSlow == null || track.gazeSlow.Length < 2 * track.count) return Vector2.zero;
        return new Vector2(Sample(track.gazeSlow, 2, 0, t), Sample(track.gazeSlow, 2, 1, t)) * headFollow;
    }

    // ------------------------------------------------------------------ body
    Transform B(HumanBodyBones b) => animator.GetBoneTransform(b);
    Quaternion Rest(HumanBodyBones b) => rest.TryGetValue((int)b, out var q) ? q : Quaternion.identity;

    void Local(HumanBodyBones b, float pitch, float yaw, float roll)
    {
        var t = B(b); if (t == null) return;
        t.localRotation = Rest(b) * Quaternion.Euler(pitch * motion, yaw * motion, roll * motion);
    }

    /// <summary>Turn bone b so that the segment b -> child points along dirRoot (character space: +X right, +Y up, +Z forward).</summary>
    void Aim(HumanBodyBones b, HumanBodyBones child, Vector3 dirRoot)
    {
        var t = B(b); var c = B(child); if (t == null || c == null) return;
        t.localRotation = Rest(b);
        Vector3 cur = (c.position - t.position).normalized;
        Vector3 want = transform.TransformDirection(dirRoot.normalized);
        t.rotation = Quaternion.FromToRotation(cur, want) * t.rotation;
    }

    static float Smooth(float a, float b, float x) { x = Mathf.Clamp01((x - a) / (b - a)); return x * x * (3 - 2 * x); }
    static float Window(float t, float a, float b, float fade) => Smooth(a, a + fade, t) * (1 - Smooth(b - fade, b, t));

    void Curl(HumanBodyBones prox, float sign, float amount)
    {
        float[] ang = { 20f, 26f, 14f };
        for (int k = 0; k < 3; k++)
        {
            var b = (HumanBodyBones)((int)prox + k); var t = B(b); if (t == null) continue;
            t.localRotation = Rest(b) * Quaternion.AngleAxis(sign * ang[k] * amount, Vector3.forward);
        }
    }

    // ---- body from clips: a manual-time playable graph, so a frame depends only on the clip time
    PlayableGraph graph; AnimationMixerPlayable mixer; AnimationClipPlayable[] cps;

    void BuildGraph()
    {
        if (graph.IsValid()) return;
        graph = PlayableGraph.Create("SingBody");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        mixer = AnimationMixerPlayable.Create(graph, bodyClips.Length);
        cps = new AnimationClipPlayable[bodyClips.Length];
        for (int i = 0; i < bodyClips.Length; i++)
        {
            cps[i] = AnimationClipPlayable.Create(graph, bodyClips[i]);
            cps[i].SetApplyFootIK(false);
            graph.Connect(cps[i], 0, mixer, i);
        }
        var output = AnimationPlayableOutput.Create(graph, "body", animator);
        output.SetSourcePlayable(mixer);
        animator.applyRootMotion = false;
    }

    public void ResetGraph() { if (graph.IsValid()) graph.Destroy(); }
    void OnDisable() { ResetGraph(); }

    void Add(HumanBodyBones b, float pitch, float yaw, float roll)
    {
        var t = B(b); if (t == null) return;
        t.localRotation = t.localRotation * Quaternion.Euler(pitch * motion, yaw * motion, roll * motion);
    }

    void ApplyClips(float t, float open)
    {
        BuildGraph();
        int n = bodyClips.Length; float sum = 0; var w = new float[n];
        for (int i = 0; i < n; i++)
        {
            float a = i < bodyStart.Length ? bodyStart[i] : 0, b = i + 1 < n && i + 1 < bodyStart.Length ? bodyStart[i + 1] : float.MaxValue;
            float fin = i == 0 ? 1f : Smooth(a - bodyFade * 0.5f, a + bodyFade * 0.5f, t);
            float fout = b == float.MaxValue ? 0f : Smooth(b - bodyFade * 0.5f, b + bodyFade * 0.5f, t);
            w[i] = fin * (1 - fout); sum += w[i];
            float local = Mathf.Max(0, t - a + bodyFade * 0.5f) * bodySpeed;
            float len = Mathf.Max(0.01f, bodyClips[i].length);
            cps[i].SetTime(bodyClips[i].isLooping ? Mathf.Repeat(local, len) : Mathf.Min(local, len - 0.001f));
        }
        for (int i = 0; i < n; i++) mixer.SetInputWeight(i, sum > 0 ? w[i] / sum : (i == 0 ? 1 : 0));
        graph.Evaluate();
        // library clips turn and nod the head a lot. Pull the head back towards "upright, facing where the character faces"
        // in world space, so the face stays to the audience even while the torso turns; the neck takes part of it.
        var neckT = B(HumanBodyBones.Neck); var headT = B(HumanBodyBones.Head);
        Quaternion upright = transform.rotation;
        if (neckT != null) neckT.rotation = Quaternion.Slerp(neckT.rotation, upright, headDamp * 0.6f);
        if (headT != null) headT.rotation = Quaternion.Slerp(headT.rotation, upright, headDamp);
        float slow = Slow(t); Vector2 g = GazeSlow(t);
        Add(HumanBodyBones.Chest, -0.8f * slow, 0, 0);
        Add(HumanBodyBones.Neck, -0.6f * slow + 0.4f * g.y, 0.4f * g.x, 0);
        Add(HumanBodyBones.Head, -1.2f * slow + 0.6f * g.y, 0.6f * g.x, 0);
    }

    /// <summary>What the body takes from the voice at clip time t: phrase loudness, smoothed stress, where the eyes are.</summary>
    void GestureInputs(float t, out float slow, out float beat, out Vector2 gz)
    {
        slow = Slow(t); gz = GazeSlow(t); beat = 0;
        if (track != null) { for (int k = -2; k <= 2; k++) beat += Mathf.Clamp01((Sample(track.open, 1, 0, t + k * 0.06f) - Slow(t + k * 0.06f)) * 2.2f); beat *= 0.2f * 0.7f; }
    }

    public void ApplyBones(float t)
    {
        Load(); if (animator == null) return;
        float open = track != null ? Sample(track.open, 1, 0, t) : 0;
        if (rest.Count > 0 && KeyReady()) { ApplyKeyed(t); return; }      // hand-keyed animation (KeyAnim.cs)
        if (bodyClips != null && bodyClips.Length > 0) { ApplyClips(t, open); return; }
        if (rest.Count == 0) return;
        // code-written gestures (GestureRig.cs): idle layer + scheduled gestures; the hands dip a little on stressed syllables
        GestureInputs(t, out float slowP, out float beat, out Vector2 gz);
        ApplyGestures(t, slowP, beat, gz);
    }
}
