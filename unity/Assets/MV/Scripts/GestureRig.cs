using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Code-written gestures for singing and for reading a script to camera.
/// A gesture says where each wrist goes (relative to a body anchor), which way the fingers and the palm point and what
/// the fingers do; the arm is solved with two-bone IK. A schedule (name, start, duration) blends gestures in and out
/// over a relaxed rest pose; breathing, weight shift and a small beat on the stressed syllables run underneath.
/// Everything is a pure function of the clip time.
/// Character space: +X her right, +Y up, +Z forward. Poses are written for the RIGHT hand and mirrored for the left.
/// </summary>
public partial class SingDriver
{
    [Header("Code gestures (used when no body clips are set)")]
    public string[] gestureNames;
    public float[] gestureStart;
    public float[] gestureDur;
    [Tooltip("Rest pose between gestures: rest (arms down) or folded (hands together in front)")]
    public string restPose = "rest";

    public enum Anchor { Shoulder, Chest, Head, Hips, Mic }      // Mic: a point fixed on the stage (the microphone); offsets in metres, not scaled

    [Header("Stand microphone (character space when she stands at the stage origin)")]
    public bool micOn;
    public Vector3 micPos = new Vector3(0f, 1.388f, 0.172f);       // centre of the grille
    public Vector3 micDir = new Vector3(0f, -0.80f, 0.60f);        // from the grille along the body of the microphone (down and away from her)
    public float micClip = 0.115f;      // grille centre to where the clip holds the body (on the pole axis)
    public float micLen = 0.170f;       // grille centre to the tail
    public float micGrip = 0.090f;      // grille centre to the middle of a hand that holds the microphone
    [Tooltip("Which way the elbow of the holding arm points (right arm, character space). Zero = the library pose.")]
    public Vector3 micElbow;
    [Tooltip("How the hand holds the microphone. false: from below and behind (wrist between the microphone and the chest - the library pose). true: laid over it from the front and her side (palm down on the body, fingers round it, wrist in front of the microphone): for wide cuffs that have no room at the chest.")]
    public bool micGripOver;
    [Tooltip("The holding hand turned about the microphone (degrees): 0 = from below and behind (the library pose: wrist between microphone and chest), 60 = from her side (wrist level with the microphone, well clear of the chest), 90 = laid over it from above.")]
    public float micGripRoll;
    [Tooltip("How far each finger closes round the microphone (thumb, index, middle, ring, little; 0..1) - fitted to this hand and this microphone on the real meshes by MvSetup.TuneGrip. Empty = the library's grip.")]
    public float[] micCurl;
    [Tooltip("The same for a hand on the stand (MvSetup.TunePole).")]
    public float[] poleCurl;
    [Tooltip("A grip fitted joint by joint (MvSetup.TuneGrip3): 15 values, thumb..little x proximal / middle / distal, each 0..1.2 of the full bend of that joint. The fingers close round the thing the way a hand does: each joint until it touches.")]
    public float[] micCurl3, poleCurl3;
    public Vector3 MicPolePoint => micPos + micDir.normalized * micClip;   // where the clip sits on top of the pole
    static readonly Vector3 MicDirRef = new Vector3(0f, -0.80f, 0.60f);    // the tilt the grip poses of the library were made for
    Quaternion MicRot => Quaternion.FromToRotation(MicDirRef.normalized, micDir.normalized);

    [Header("Where she looks, when a hand is on the microphone (MvSetup.SetLookFromFile: look t kind / hold t0 t1)")]
    [Tooltip("kind: 0 microphone (head a little down), 1 the lens, 2 audience left, 3 audience right, 4 far over the audience, 5 down to her side")]
    public float[] lookT; public int[] lookKind;
    [Tooltip("The right hand holds the microphone from t0 to t1 (the gesture schedule has 'mic' there): the recorded take gives that arm up meanwhile")]
    public float[] holdT0, holdT1;
    [Tooltip("Authored gestures over a recorded take: from ownT0 (the hand is ON its pose) to ownT1 (it starts to leave) that arm is the gesture system's. ownSide: 0 right, 1 left, 2 both. ownGrip 1 = the hand takes hold of something (microphone, stand): fingers open on the way, close on arrival.")]
    public float[] ownT0, ownT1; public int[] ownSide, ownGrip;

    public class HandPose
    {
        public Anchor anchor; public Vector3 pos;      // wrist position: anchor + offset (metres, for a 0.42 m arm; scaled to the model)
        public Vector3 pole;                           // where the elbow tends to
        public Vector3 finger, palm;                   // direction of the fingers / of the palm normal
        public float[] curl;                           // thumb, index, middle, ring, little: 0 straight .. 1 closed
        public float spread;                           // fingers apart
        public HandPose(Anchor a, Vector3 pos, Vector3 pole, Vector3 finger, Vector3 palm, float[] curl, float spread = 0)
        { anchor = a; this.pos = pos; this.pole = pole; this.finger = finger; this.palm = palm; this.curl = curl; this.spread = spread; }
    }

    public class Gesture
    {
        public HandPose R, L;                          // L is given already mirrored (in right-hand terms) unless null
        public float lean, yaw, nod, tilt, shrug;      // torso forward lean, chest yaw, head nod (+ down), head tilt, shoulders up
        public float headYaw;
        public float beat = 0.6f;                      // how much the hand follows the stressed syllables
        public float wave;                             // hand waving
        public int nods;                               // number of nods during the gesture
        public int shakes;                             // number of small head shakes ("no") during the gesture
        public float snap = 0.5f;                      // 0 = soft and slow, 1 = sharp: sets how fast the hand arrives and how much it overshoots
    }

    static readonly float[] Relaxed = { 0.22f, 0.28f, 0.34f, 0.40f, 0.46f };
    static readonly float[] Open = { 0.04f, 0.05f, 0.07f, 0.10f, 0.13f };
    static readonly float[] Soft = { 0.12f, 0.16f, 0.20f, 0.25f, 0.30f };
    static readonly float[] Half = { 0.40f, 0.55f, 0.60f, 0.65f, 0.70f };
    static readonly float[] Fist = { 0.75f, 1f, 1f, 1f, 1f };
    static readonly float[] Point = { 0.75f, 0.03f, 1f, 1f, 1f };
    static readonly float[] Two = { 0.80f, 0.03f, 0.03f, 1f, 1f };
    static readonly float[] Three = { 0.80f, 0.03f, 0.03f, 0.03f, 1f };
    static readonly float[] ThumbUp = { 0f, 1f, 1f, 1f, 1f };

    static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);
    static HandPose H(Anchor a, Vector3 pos, Vector3 pole, Vector3 finger, Vector3 palm, float[] curl, float spread = 0) => new HandPose(a, pos, pole, finger, palm, curl, spread);
    static HandPose RestHand => H(Anchor.Shoulder, V(0.092f, -0.393f, 0.082f), V(0.35f, 0.1f, -1f), V(0.06f, -1f, 0.16f), V(-1f, 0f, 0.12f), Relaxed);
    static HandPose FoldR => H(Anchor.Hips, V(0.092f, 0.070f, 0.201f), V(1f, -0.3f, -0.2f), V(-0.70f, -0.40f, -0.35f), V(-0.20f, 0.20f, -0.95f), Soft);
    static HandPose FoldL => H(Anchor.Hips, V(0.092f, 0.051f, 0.225f), V(1f, -0.3f, -0.2f), V(-0.72f, -0.32f, -0.30f), V(-0.20f, 0.20f, -0.95f), Soft);   // lies over the right hand

    static Dictionary<string, Gesture> lib;
    public static Dictionary<string, Gesture> Library
    {
        get
        {
            if (lib != null) return lib;
            lib = new Dictionary<string, Gesture>();
            // ---------------- rest poses
            lib["rest"] = new Gesture { R = RestHand, L = RestHand, beat = 0 };
            lib["folded"] = new Gesture { R = FoldR, L = FoldL, beat = 0.15f };
            // ---------------- singing
            lib["chest"] = new Gesture { R = H(Anchor.Chest, V(0.072f, 0.009f, 0.213f), V(0.55f, -1f, 0.25f), V(-0.58f, 0.22f, -0.60f), V(-0.35f, -0.35f, -0.87f), Soft), lean = 1.5f, tilt = 3f, beat = 0.2f };   // wrist off the coat, fingers bend in to touch
            lib["reach"] = new Gesture { R = H(Anchor.Shoulder, V(0.01f, -0.09f, 0.365f), V(0.7f, -1f, 0f), V(-0.10f, 0.18f, 1f), V(0.10f, 1f, -0.15f), Soft, 0.3f), lean = 4f, beat = 0.5f };
            lib["offer"] = new Gesture { R = H(Anchor.Shoulder, V(0.085f, -0.240f, 0.250f), V(0.35f, -1f, -0.5f), V(0.22f, -0.02f, 1f), V(-0.20f, 1f, 0.05f), Open, 0.3f), lean = 2f, beat = 0.7f };
            lib["offer"].L = lib["offer"].R;
            lib["clasp"] = new Gesture { R = H(Anchor.Chest, V(0.082f, -0.006f, 0.213f), V(0.55f, -1f, 0.25f), V(-0.62f, 0.20f, -0.58f), V(-0.35f, -0.30f, -0.88f), Soft), lean = 3f, nod = 3f, beat = 0.2f };   // both hands laid on the chest, one over the other
            lib["clasp"].L = H(Anchor.Chest, V(0.082f, 0.014f, 0.237f), V(0.55f, -1f, 0.25f), V(-0.66f, 0.12f, -0.50f), V(-0.35f, -0.30f, -0.88f), Soft);
            lib["raise"] = new Gesture { R = H(Anchor.Shoulder, V(0.165f, 0.275f, 0.215f), V(1f, -0.35f, -0.45f), V(0.22f, 0.92f, 0.32f), V(-0.55f, 0.10f, 0.83f), Open, 0.5f), lean = -1.5f, nod = -5f, beat = 0.3f };
            lib["wide"] = new Gesture { R = H(Anchor.Shoulder, V(0.270f, -0.130f, 0.200f), V(0.1f, -1f, -0.5f), V(0.75f, 0.10f, 0.65f), V(-0.15f, 0.80f, 0.55f), Open, 0.5f), lean = -2f, nod = -4f, beat = 0.3f };
            lib["wide"].L = lib["wide"].R;
            lib["fist"] = new Gesture { R = H(Anchor.Chest, V(0.068f, 0.014f, 0.213f), V(0.55f, -1f, 0.25f), V(-0.80f, 0.25f, -0.45f), V(-0.20f, -0.30f, -0.93f), Fist), lean = 3f, nod = 4f, beat = 0.2f };
            lib["down"] = new Gesture { R = RestHand, L = RestHand, nod = 20f, lean = 4f, beat = 0 };
            lib["sway"] = new Gesture { R = H(Anchor.Shoulder, V(0.085f, -0.285f, 0.205f), V(0.6f, -0.3f, -1f), V(0.05f, -0.45f, 0.89f), V(-0.95f, 0.05f, 0.30f), Soft), beat = 1.2f };   // hands loose in front at waist height, moving with the music
            lib["sway"].L = lib["sway"].R;
            // ---------------- reading a script to camera
            lib["explain"] = new Gesture { R = H(Anchor.Shoulder, V(0.100f, -0.140f, 0.265f), V(1f, -0.8f, -0.2f), V(0.20f, 0.32f, 0.92f), V(-0.50f, 0.86f, -0.10f), Open, 0.3f), beat = 1.0f };
            lib["explain2"] = new Gesture { R = lib["explain"].R, L = lib["explain"].R, beat = 1.0f };
            lib["point"] = new Gesture { R = H(Anchor.Shoulder, V(0.015f, -0.055f, 0.345f), V(0.75f, -1f, 0f), V(0f, 0.22f, 1f), V(-0.30f, -1f, 0.10f), Point), lean = 2f, beat = 0.5f };
            lib["present"] = new Gesture { R = H(Anchor.Shoulder, V(0.270f, -0.040f, 0.170f), V(0f, -1f, -0.45f), V(1f, 0.16f, 0.30f), V(-0.05f, 0.90f, 0.42f), Open, 0.2f), headYaw = 14f, yaw = 5f, beat = 0.3f };
            lib["count1"] = new Gesture { R = H(Anchor.Shoulder, V(0.095f, 0.020f, 0.245f), V(1f, -0.85f, -0.1f), V(0.05f, 1f, 0.12f), V(-0.20f, 0.05f, 1f), Point), beat = 0.4f };
            lib["count2"] = new Gesture { R = H(Anchor.Shoulder, V(0.095f, 0.020f, 0.245f), V(1f, -0.85f, -0.1f), V(0.05f, 1f, 0.12f), V(-0.20f, 0.05f, 1f), Two, 0.7f), beat = 0.4f };
            lib["count3"] = new Gesture { R = H(Anchor.Shoulder, V(0.095f, 0.020f, 0.245f), V(1f, -0.85f, -0.1f), V(0.05f, 1f, 0.12f), V(-0.20f, 0.05f, 1f), Three, 0.7f), beat = 0.4f };
            lib["frame"] = new Gesture { R = H(Anchor.Shoulder, V(0.075f, -0.085f, 0.290f), V(1f, -0.8f, -0.1f), V(0.02f, 0.30f, 1f), V(-1f, 0.05f, 0.05f), Open, 0.2f), beat = 0.9f };
            lib["frame"].L = lib["frame"].R;
            lib["thumb"] = new Gesture { R = H(Anchor.Shoulder, V(0.095f, -0.030f, 0.270f), V(1f, -0.8f, -0.1f), V(-0.15f, 0.10f, 1f), V(-1f, 0f, -0.10f), ThumbUp), nods = 1, beat = 0.2f };
            lib["wave"] = new Gesture { R = H(Anchor.Shoulder, V(0.195f, 0.205f, 0.150f), V(1f, -0.5f, -0.3f), V(0.10f, 1f, 0.10f), V(0f, 0f, 1f), Open, 0.6f), tilt = -5f, wave = 1f, beat = 0 };
            lib["hip"] = new Gesture { R = H(Anchor.Hips, V(0.236f, 0.080f, 0.025f), V(1f, 0.1f, -0.8f), V(-0.62f, -0.55f, 0.45f), V(-0.90f, 0.20f, -0.30f), Soft), tilt = 3f, beat = 0 };
            lib["shrug"] = new Gesture { R = H(Anchor.Shoulder, V(0.150f, -0.170f, 0.230f), V(0.35f, -1f, -0.4f), V(0.45f, 0.02f, 0.89f), V(-0.05f, 1f, 0.05f), Open, 0.4f), shrug = 1f, tilt = 6f, beat = 0.2f };
            lib["shrug"].L = lib["shrug"].R;
            lib["think"] = new Gesture { R = H(Anchor.Head, V(0.048f, -0.127f, 0.191f), V(0.6f, -1f, 0.3f), V(-0.25f, 0.85f, -0.45f), V(-0.30f, -0.10f, -0.95f), Half), tilt = 5f, nod = 3f, beat = 0 };
            lib["nod"] = new Gesture { R = RestHand, L = RestHand, nods = 2, beat = 0 };
            lib["no"] = new Gesture { R = H(Anchor.Shoulder, V(0.110f, -0.050f, 0.260f), V(1f, -0.8f, -0.1f), V(0.05f, 1f, 0.15f), V(-0.10f, 0f, 1f), Open, 0.3f), wave = 0.5f, beat = 0 };
            // ---------------- emotional gestures for the formal version (2026-11-12): what the lyrics feel like, not what they say
            lib["throat"] = new Gesture { R = H(Anchor.Chest, V(0.040f, 0.078f, 0.195f), V(0.6f, -1f, 0.3f), V(-0.12f, 0.85f, -0.50f), V(-0.20f, -0.50f, -0.84f), Half), nod = 5f, lean = 1f, beat = 0.1f };          // hand at the throat: cannot breathe
            lib["covereye"] = new Gesture { R = H(Anchor.Head, V(0.060f, -0.045f, 0.195f), V(0.7f, -1f, 0.2f), V(-0.10f, 0.90f, -0.42f), V(-0.15f, -0.40f, -0.90f), Soft, 0.3f), nod = 6f, tilt = 3f, beat = 0 };     // hides one eye: do not look at me
            lib["headhold"] = new Gesture { R = H(Anchor.Head, V(0.118f, 0.025f, 0.095f), V(0.35f, -1f, 0.55f), V(-0.50f, 0.74f, -0.45f), V(-0.80f, -0.25f, -0.55f), Half, 0.3f), nod = 14f, lean = 4f, beat = 0 };       // both hands to the head: it will not come apart
            lib["headhold"].L = lib["headhold"].R;
            lib["holdarm"] = new Gesture { R = H(Anchor.Chest, V(-0.095f, -0.030f, 0.160f), V(0.5f, -1f, 0.3f), V(-0.85f, -0.05f, -0.52f), V(-0.35f, -0.10f, -0.93f), Half), shrug = 0.25f, nod = 6f, tilt = 4f, lean = 1f, beat = 0 };   // holds the other arm: alone, cold
            lib["pushaway"] = new Gesture { R = H(Anchor.Shoulder, V(0.035f, -0.030f, 0.300f), V(0.8f, -0.9f, 0f), V(0.08f, 1f, 0.12f), V(0f, -0.05f, 1f), Open, 0.5f), headYaw = -12f, nod = 8f, lean = -2f, beat = 0.2f };          // both palms out, face turned away: stay away
            lib["pushaway"].L = lib["pushaway"].R;
            lib["reach2"] = new Gesture { R = H(Anchor.Shoulder, V(0.050f, -0.130f, 0.340f), V(0.6f, -1f, -0.1f), V(0.05f, 0.12f, 1f), V(0.12f, 1f, -0.12f), Soft, 0.3f), lean = 5f, nod = -2f, beat = 0.4f };                      // both hands held out: pleading
            lib["reach2"].L = lib["reach2"].R;
            lib["raise2"] = new Gesture { R = lib["raise"].R, L = lib["raise"].R, lean = -3f, nod = -10f, beat = 0.3f };                         // both arms up, head back: the scream
            lib["opendown"] = new Gesture { R = H(Anchor.Shoulder, V(0.200f, -0.330f, 0.110f), V(0.3f, 0f, -1f), V(0.40f, -0.82f, 0.40f), V(0.05f, 0.30f, 0.95f), Open, 0.3f), nod = -3f, beat = 0 };                               // arms low and open, palms out: nothing left to hide
            lib["opendown"].L = lib["opendown"].R;
            lib["lookhand"] = new Gesture { R = H(Anchor.Shoulder, V(-0.030f, -0.170f, 0.250f), V(0.7f, -1f, -0.2f), V(-0.30f, 0.35f, 0.89f), V(0f, 0.75f, -0.66f), Soft, 0.2f), nod = 13f, headYaw = 5f, lean = 2f, beat = 0 };     // looks at her own palm
            lib["lookhands"] = new Gesture { R = H(Anchor.Shoulder, V(0.050f, -0.190f, 0.240f), V(0.6f, -1f, -0.3f), V(-0.10f, 0.30f, 0.95f), V(0f, 0.80f, -0.60f), Soft, 0.2f), nod = 13f, lean = 2f, beat = 0 };                   // ... at both hands
            lib["lookhands"].L = lib["lookhands"].R;
            lib["fistsdown"] = new Gesture { R = H(Anchor.Shoulder, V(0.105f, -0.360f, 0.100f), V(0.5f, 0.1f, -1f), V(0.05f, -0.90f, 0.42f), V(-1f, 0f, 0.10f), Fist), shrug = 0.35f, nod = 9f, shakes = 3, beat = 0 };            // fists clenched at the sides, head shaking: cannot move
            lib["fistsdown"].L = lib["fistsdown"].R;
            lib["away"] = new Gesture { R = RestHand, L = RestHand, headYaw = -17f, nod = 8f, tilt = -3f, yaw = -3f, beat = 0 };               // face turned away and down
            lib["sky"] = new Gesture { R = RestHand, L = RestHand, nod = -13f, lean = -2f, beat = 0 };                                         // looks up
            // ---------------- at the stand microphone (2026-11-12). Offsets are from the centre of the grille, in metres.
            float[] Grip = { 0.30f, 0.55f, 0.60f, 0.62f, 0.66f };
            // grip centre G on the axis; palm normal n points at the axis, fingers f run across it (f = axis x n); wrist = G - n * 3.2 cm - f * 6.6 cm
            lib["mic"] = new Gesture { R = H(Anchor.Mic, V(0.032f, -0.112f, 0.004f), V(0.8f, -1f, -0.2f), V(0f, 0.60f, 0.80f), V(-1f, 0f, 0f), Grip), lean = 2f, beat = 0 };          // one hand round the microphone (G 9 cm down the body)
            lib["micnear"] = new Gesture { R = H(Anchor.Mic, V(0.032f + 0.13f, -0.112f, 0.004f), V(0.8f, -1f, -0.2f), V(0f, 0.60f, 0.80f), V(-1f, 0f, 0f), new[] { 0.05f, 0.10f, 0.10f, 0.10f, 0.10f }), lean = 1f, beat = 0 };   // beside the microphone, hand open: from here it goes on palm first, to here it comes off
            lib["mic2"] = new Gesture { R = lib["mic"].R, L = H(Anchor.Mic, V(0.032f, -0.180f, 0.055f), V(0.8f, -1f, -0.2f), V(0f, 0.60f, 0.80f), V(-1f, 0f, 0f), Grip), lean = 3f, beat = 0 };   // both hands, the second at the tail of the microphone
            lib["micpole"] = new Gesture { R = H(Anchor.Mic, V(0.032f, -0.300f, 0.003f), V(0.7f, -1f, -0.3f), V(0f, 0.10f, 1f), V(-1f, 0f, 0f), new[] { 0.12f, 0.66f, 0.70f, 0.70f, 0.66f }), lean = 1f, beat = 0 };     // a hand on the stand below the microphone
            // energy of each gesture: sharp ones snap into place and bounce, soft ones glide
            var snaps = new Dictionary<string, float> { {"point", 0.92f}, {"count1", 0.85f}, {"count2", 0.85f}, {"count3", 0.85f}, {"fist", 0.80f}, {"thumb", 0.80f}, {"no", 0.65f}, {"raise", 0.62f},
                {"explain", 0.60f}, {"explain2", 0.60f}, {"frame", 0.60f}, {"wave", 0.60f}, {"wide", 0.55f}, {"present", 0.55f}, {"reach", 0.50f}, {"shrug", 0.50f}, {"nod", 0.50f}, {"offer", 0.45f},
                {"hip", 0.40f}, {"mic", 0.35f}, {"mic2", 0.35f}, {"micpole", 0.30f}, {"throat", 0.35f}, {"covereye", 0.55f}, {"headhold", 0.55f}, {"holdarm", 0.30f}, {"pushaway", 0.70f}, {"reach2", 0.50f}, {"raise2", 0.70f}, {"opendown", 0.30f},
                {"lookhand", 0.30f}, {"lookhands", 0.30f}, {"fistsdown", 0.60f}, {"away", 0.25f}, {"sky", 0.25f}, {"chest", 0.30f}, {"clasp", 0.30f}, {"think", 0.30f}, {"sway", 0.30f}, {"folded", 0.25f}, {"down", 0.20f}, {"rest", 0.25f} };
            foreach (var kv in snaps) if (lib.ContainsKey(kv.Key)) lib[kv.Key].snap = kv.Value;
            lib["reach"].yaw = -2f; lib["point"].yaw = -2f; lib["raise"].yaw = -1f;      // the shoulder of the working arm comes forward
            return lib;
        }
    }

    // ------------------------------------------------------------------ solver
    Vector3 AnchorPos(Anchor a, bool right)
    {
        switch (a)
        {
            case Anchor.Chest: { var c = B(HumanBodyBones.UpperChest) ?? B(HumanBodyBones.Chest); return c.position; }
            case Anchor.Head: return B(HumanBodyBones.Head).position;
            case Anchor.Hips: return B(HumanBodyBones.Hips).position;
            default: return B(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm).position;
        }
    }

    struct HandTarget { public Vector3 pos, pole, finger, palm; public float[] curl; public float spread; public float micAvoid, bodyAvoid; }      // bodyAvoid: a hand that holds the microphone is where the microphone is; the coat must not push it off

    void AimBone(Transform t, Transform child, HumanBodyBones id, Vector3 worldDir)
    {
        t.localRotation = Rest(id);
        Vector3 cur = (child.position - t.position).normalized;
        t.rotation = Quaternion.FromToRotation(cur, worldDir.normalized) * t.rotation;
    }

    void SolveArm(bool right, HandTarget h)
    {
        var upId = right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm;
        var loId = right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm;
        var haId = right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand;
        var up = B(upId); var lo = B(loId); var ha = B(haId);
        float a = Vector3.Distance(up.position, lo.position), b = Vector3.Distance(lo.position, ha.position);
        Vector3 S = up.position, T = h.pos; Vector3 st = T - S; float len = a + b, dn = st.magnitude / len;
        // soft reach limit: from 93 % of the arm length on the wrist gives way gradually (a hard stop at full stretch made the hand stick and then jump)
        if (dn > 0.93f) dn = 0.93f + 0.065f * (1f - Mathf.Exp(-(dn - 0.93f) / 0.065f));
        float d = Mathf.Max(dn * len, Mathf.Abs(a - b) + 0.005f);
        Vector3 dir = st.normalized;
        float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1f, 1f), sinA = Mathf.Sqrt(1 - cosA * cosA);
        Vector3 pole = transform.TransformDirection(h.pole); Vector3 perp = (pole - Vector3.Dot(pole, dir) * dir);
        if (perp.sqrMagnitude < 1e-6f) perp = Vector3.Cross(dir, transform.right); perp.Normalize();
        Vector3 E = S + a * (cosA * dir + sinA * perp);
        AimBone(up, lo, upId, E - S);
        AimBone(lo, ha, loId, S + dir * d - lo.position);
        // hand: fingers along 'finger', palm facing 'palm' (rest: fingers along +-X, palm down)
        Vector3 f = transform.TransformDirection(h.finger).normalized, p = transform.TransformDirection(h.palm);
        p = (p - Vector3.Dot(p, f) * f).normalized; Vector3 yd = -p, zd = Vector3.Cross(f, yd);
        Vector3 xr = transform.TransformDirection(right ? Vector3.right : Vector3.left), yr = transform.up, zr = Vector3.Cross(xr, yr);
        Quaternion want = Quaternion.LookRotation(zd, yd) * Quaternion.Inverse(Quaternion.LookRotation(zr, yr));
        ha.rotation = want * transform.rotation;
        // part of the hand's roll goes into the forearm so the wrist does not pinch
        Vector3 axis = (ha.position - lo.position).normalized;
        Quaternion rel = ha.rotation * Quaternion.Inverse(lo.rotation);
        rel.ToAngleAxis(out float ang, out Vector3 ax); if (ang > 180f) ang -= 360f;
        float twist = ang * Vector3.Dot(ax.normalized, axis);
        Quaternion keep = ha.rotation;
        lo.rotation = Quaternion.AngleAxis(twist * 0.5f, axis) * lo.rotation;
        ha.rotation = keep;
        Fingers(right, h.curl, h.spread);
    }

    /// <summary>Fingers posed joint by joint (j: 15 values, thumb..little x 3 joints), blended over what is there by w.</summary>
    public void FingersJoint(bool right, float[] j, float w)
    {
        if (j == null || j.Length < 15 || w <= 0f) return;
        float s = right ? -1f : 1f; int baseId = (int)(right ? HumanBodyBones.RightThumbProximal : HumanBodyBones.LeftThumbProximal); float[] full = { 72f, 95f, 62f };
        for (int f = 1; f < 5; f++)
            for (int k = 0; k < 3; k++)
            {
                var id = (HumanBodyBones)(baseId + f * 3 + k); var t = B(id); if (t == null) continue;
                t.localRotation = Quaternion.Slerp(t.localRotation, Rest(id) * Quaternion.AngleAxis(s * full[k] * j[f * 3 + k], Vector3.forward), w);
            }
        var t0 = B((HumanBodyBones)baseId); var t1 = B((HumanBodyBones)(baseId + 1)); var t2 = B((HumanBodyBones)(baseId + 2));
        if (t0 != null) t0.localRotation = Quaternion.Slerp(t0.localRotation, Rest((HumanBodyBones)baseId) * Quaternion.AngleAxis(s * 38f * j[0], Vector3.up) * Quaternion.AngleAxis(s * 22f * j[0], Vector3.forward), w);
        if (t1 != null) t1.localRotation = Quaternion.Slerp(t1.localRotation, Rest((HumanBodyBones)(baseId + 1)) * Quaternion.AngleAxis(s * 34f * j[1], Vector3.up), w);
        if (t2 != null) t2.localRotation = Quaternion.Slerp(t2.localRotation, Rest((HumanBodyBones)(baseId + 2)) * Quaternion.AngleAxis(s * 48f * j[2], Vector3.up), w);
    }

    void Fingers(bool right, float[] curl, float spread)
    {
        float s = right ? -1f : 1f;                                        // curl axis flips between the hands
        int baseId = (int)(right ? HumanBodyBones.RightThumbProximal : HumanBodyBones.LeftThumbProximal);
        float[] full = { 72f, 95f, 62f };
        for (int f = 1; f < 5; f++)
        {
            float sp = spread * (f == 1 ? 9f : f == 2 ? 2f : f == 3 ? -5f : -12f);
            for (int k = 0; k < 3; k++)
            {
                var id = (HumanBodyBones)(baseId + f * 3 + k); var t = B(id); if (t == null) continue;
                Quaternion q = Quaternion.AngleAxis(s * full[k] * curl[f], Vector3.forward);
                if (k == 0) q = Quaternion.AngleAxis(-s * sp, Vector3.up) * q;
                t.localRotation = Rest(id) * q;
            }
        }
        // thumb: folds across the palm
        float c = curl[0];
        var t0 = B((HumanBodyBones)baseId); var t1 = B((HumanBodyBones)(baseId + 1)); var t2 = B((HumanBodyBones)(baseId + 2));
        if (t0 != null) t0.localRotation = Rest((HumanBodyBones)baseId) * Quaternion.AngleAxis(s * 38f * c, Vector3.up) * Quaternion.AngleAxis(s * 22f * c, Vector3.forward);
        if (t1 != null) t1.localRotation = Rest((HumanBodyBones)(baseId + 1)) * Quaternion.AngleAxis(s * 34f * c, Vector3.up);
        if (t2 != null) t2.localRotation = Rest((HumanBodyBones)(baseId + 2)) * Quaternion.AngleAxis(s * 48f * c, Vector3.up);
    }
}
