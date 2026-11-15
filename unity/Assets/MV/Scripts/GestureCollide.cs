using UnityEngine;

/// <summary>
/// Keeps the arms out of the body. v1 of the gestures only placed bones; the jacket front is 12-14 cm in front of the
/// spine and the flared cuffs are 7-9 cm thick on the palm and little-finger side, so "hand on chest" sank into the coat.
///
/// Body proxy (character space, measured from the model's clothes mesh): an elliptic column for the torso - 16 cm
/// half width, front surface 13.7 cm in front of the axis up to the collar, tapering towards the neck - plus a sphere
/// for the head. Arm proxy: a sphere for the cuff (offset towards the palm and the little finger, where the cuff is
/// thick), one for the forearm, one for the palm, one for the fingertips.
/// SolveArmSafe: solve, test, push the wrist target out (or tilt the fingers away for the fingertips), solve again.
/// </summary>
public partial class SingDriver
{
    [Header("Collision")]
    public bool avoidBody = true;
    [Tooltip("Smallest clearance (m) of any arm proxy from the body proxy in the last solve; negative = still inside.")]
    public float lastClearance = 1f;

    [Header("Body profile of this character (MvSetup.SetBodyProfile). Empty = the built-in one of jinglin's coat")]
    [Tooltip("u: 0 at the hips, 1 at the neck, ascending. Per u: how far the clothed body reaches in front of / behind the spine axis and to the side (m).")]
    public float[] colU; public float[] colFront, colBack, colSide;
    [Tooltip("Outline of the clothed body per slice of colU: distance from the spine axis to the convex outline in 24 directions (0 = front, 15 deg steps towards her right). The same outline the penetration test uses.")]
    public float[] colR; public float colAxisZ;
    const int NDIR = 24;
    [Tooltip("Arm proxies (m): sphere at the cuff and on the forearm, and how far each is offset towards the palm / the little finger (thick, flared cuffs).")]
    public float cuffR = 0.050f, cuffPalm = 0.027f, cuffUlnar = 0.030f, foreR = 0.040f, forePalm = 0.018f, foreUlnar = 0.022f;
    [Tooltip("Measured arm (MvSetup.SetArmRadii): forearm radius in five stretches elbow -> wrist, half-width of the hand, wrist-to-fingertip length (m). Empty = the old three spheres.")]
    public float[] armRad;
    bool HasProfile => colU != null && colU.Length > 1;
    float Prof(float[] a, float u)
    {
        int n = colU.Length; if (u <= colU[0]) return a[0]; if (u >= colU[n - 1]) return a[n - 1];
        int i = 0; while (i < n - 2 && colU[i + 1] < u) i++;
        float k = (u - colU[i]) / (colU[i + 1] - colU[i]); k = k * k * (3f - 2f * k);          // smooth between the slices: a hand sliding along must not hit a crease
        return Mathf.Lerp(a[i], a[i + 1], k);
    }

    // measured on jinglin (metres); scaled by the hips-neck distance for other models
    const float RefTorso = 0.411f;
    float torsoScale, hipsY, neckY;
    Vector3 headC;                                          // centre of the head sphere, character space

    void BodyFrameRest(SimBake bk, Vector3 headRef)
    {
        hipsY = bk.hipsRef.y; neckY = bk.neckRef.y; torsoScale = Mathf.Max(0.2f, neckY - hipsY) / RefTorso;
        colHips = colNeck = Vector3.zero; headC = headRef + new Vector3(0, 0.065f, 0.015f) * torsoScale; colYaw = Quaternion.identity; colPivot = bk.hipsRef;
    }

    void BodyFrame()
    {
        hipsY = transform.InverseTransformPoint(B(HumanBodyBones.Hips).position).y;
        neckY = transform.InverseTransformPoint(B(HumanBodyBones.Neck).position).y;
        torsoScale = Mathf.Max(0.2f, neckY - hipsY) / RefTorso;
        colHips = colNeck = Vector3.zero;
        // the proxy is described for a body that faces forward: everything is brought into that frame (turn about the pelvis) and the push is turned back
        colYaw = bodyYaw; colPivot = transform.InverseTransformPoint(B(HumanBodyBones.Hips).position);
        if (bake != null) { colHips = colPivot - bake.hipsRef; colNeck = ToBody(transform.InverseTransformPoint(B(HumanBodyBones.Neck).position)) - bake.neckRef; }
        headC = ToBody(transform.InverseTransformPoint(B(HumanBodyBones.Head).position)) + new Vector3(0, 0.065f, 0.015f) * torsoScale;
    }

    static float SStep(float a, float b, float x) { x = Mathf.Clamp01((x - a) / (b - a)); return x * x * (3f - 2f * x); }

    float FrontZ(float u)                                   // u: 0 at the hips, 1 at the neck. Smooth: a hand sliding along it must not hit a crease
    {
        return Mathf.Lerp(Mathf.Lerp(0.137f, 0.066f, SStep(0.70f, 0.93f, u)), 0.030f, SStep(0.89f, 1.02f, u));   // coat front, lapels, tie -> collar -> neck
    }

    /// <summary>Soft contact: the push starts gently a little before the surface (m) and equals the depth well inside. Continuous slope, so no pop on touching.</summary>
    static float SoftDepth(float depth, float m)
    {
        if (depth <= -m) return 0; if (depth >= m) return depth;
        return (depth + m) * (depth + m) / (4f * m);
    }

    Vector3 colHips, colNeck;                               // how far the pelvis and the neck are from their rest places (character space)

    /// <summary>How far (and which way, character space) a sphere has to move to be outside the body. Zero if it already is.</summary>
    Quaternion colYaw = Quaternion.identity; Vector3 colPivot;
    Vector3 ToBody(Vector3 p) => Quaternion.Inverse(colYaw) * (p - colPivot) + colPivot;

    Vector3 BodyPush(Vector3 p, float r, out float clearance)
    {
        p = ToBody(p);
        Vector3 push = Vector3.zero; clearance = 1f;
        float u = (p.y - hipsY) / (neckY - hipsY);
        const float margin = 0.012f;
        bool prof = HasProfile;
        float taper = (prof ? SStep(colU[0] - 0.15f, colU[0], u) : SStep(-0.78f, -0.45f, u)) * (1f - SStep(0.99f, 1.12f, u));            // the column fades out below the coat and above the collar
        if (taper > 1e-4f)
        {
            Vector3 c = Vector3.Lerp(colHips, colNeck, Mathf.Clamp01(u));                    // the column leans and shifts with the torso
            if (prof && colR != null && colR.Length == colU.Length * NDIR)
            {
                // the real outline: radius of the body in the direction of the point, between the two slices around its height
                float ax0 = c.x, az0 = c.z + colAxisZ, dx = p.x - ax0, dz = p.z - az0, rho = Mathf.Sqrt(dx * dx + dz * dz);
                int n = colU.Length; float fu = Mathf.Clamp((u - colU[0]) / (colU[1] - colU[0]), 0f, n - 1.001f); int i0 = (int)fu; float ku = fu - i0;
                float fa = Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 360f) / (360f / NDIR); int k0 = (int)fa % NDIR, k1 = (k0 + 1) % NDIR; float ka = fa - (int)fa;
                float R = Mathf.Lerp(Mathf.Lerp(colR[i0 * NDIR + k0], colR[i0 * NDIR + k1], ka), Mathf.Lerp(colR[(i0 + 1) * NDIR + k0], colR[(i0 + 1) * NDIR + k1], ka), ku);
                float lim = (R + r + 0.007f) * taper;      // 7 mm to spare: the outline is sampled, cloth has thickness
                clearance = taper > 0.98f && bodyAvoidNow > 0.5f ? rho - lim : 1f;
                float dp = SoftDepth(lim - rho, margin) * bodyAvoidNow;
                if (dp > 0) push = (rho < 1e-4f ? Vector3.forward : new Vector3(dx / rho, 0, dz / rho)) * dp;
            }
            else {
            float cx = c.x, cz = 0.020f * torsoScale + c.z;
            float back = 0.110f * torsoScale, front = FrontZ(u) * torsoScale - 0.020f * torsoScale, half = 0.160f * torsoScale;
            if (prof) { back = Prof(colBack, u) + 0.020f * torsoScale; front = Prof(colFront, u) - 0.020f * torsoScale; half = Prof(colSide, u); }
            float az = (Mathf.Lerp(back, front, SStep(-0.02f, 0.02f, p.z - cz)) + r) * taper, ax = (half + r) * taper;
            float fx = (p.x - cx) / ax, fz = (p.z - cz) / az, f = Mathf.Sqrt(fx * fx + fz * fz), rad = Mathf.Min(ax, az);
            clearance = taper > 0.98f && bodyAvoidNow > 0.5f ? (f - 1f) * rad : 1f;      // (a hand on the microphone is where the microphone is; it is not counted against the coat)
            float depth = SoftDepth((1f - f) * rad, margin);
            depth *= bodyAvoidNow;
            if (depth > 0)
            {
                Vector3 dir = f < 1e-3f ? Vector3.forward : new Vector3((p.x - cx) / (ax * ax), 0, (p.z - cz) / (az * az)).normalized;   // outward normal of the ellipse
                push = dir * depth;
            }
            }
        }
        // head
        // the microphone and its stand: nothing but a hand that holds it may pass through
        if (micOn && micAvoid > 0.01f)
        {
            Vector3 root = RootOffset(previewT);                                    // the stand stays on the stage when she walks away
            Vector3 m0 = ToBody(micPos - root), m1 = ToBody(micPos - root + micDir.normalized * micLen), pt = ToBody(MicPolePoint - root), pb = ToBody(new Vector3(MicPolePoint.x - root.x, 0.02f, MicPolePoint.z - root.z));
            float cm = 1f;                                                          // (a hand that holds the microphone is not "too close" to it)
            // a wide soft zone: a passing hand is steered round the stand early, not flicked off it
            Vector3 pm = SegPush(p, m0, m1, 0.037f + r, 0.045f, ref cm) + SegPush(p, pt, pb, 0.023f + r, 0.045f, ref cm);
            // Between her and the stand there is no room to be pushed back into (chest to pole: 10 cm, a cuff: 13 cm) - the body pushes forward, the
            // stand pushes back, and the arm ended up in both. On her side of the pole the stand only ever pushes a hand out to its OWN side.
            // (eased over 6 cm of depth: a hard line at the pole made a hand that hovered there flick between the two answers, 10 cm in a frame)
            if (armSideNow != 0 && pm.sqrMagnitude > 0f) pm = Vector3.Lerp(new Vector3(armSideNow * pm.magnitude, 0f, 0f), pm, SStep(pt.z - 0.02f, pt.z + 0.04f, p.z));
            push += pm * micAvoid;
            if (micAvoid > 0.5f && cm < clearance) clearance = cm;
        }
        Vector3 hc = headC;
        float hr = 0.105f * torsoScale + r; Vector3 d = p - hc; float dm = d.magnitude;
        if (dm - hr < clearance) clearance = dm - hr;
        float hd = SoftDepth(hr - dm, margin);
        if (hd > 0) push += (dm < 1e-4f ? Vector3.forward : d / dm) * hd;
        return colYaw * push;
    }

    [System.NonSerialized] float micAvoid = 1f, bodyAvoidNow = 1f, previewT;
    [System.NonSerialized] float armSideNow;      // +1 while the right arm is being tested, -1 the left (character space: +x is her right)

    static Vector3 SegPush(Vector3 p, Vector3 a, Vector3 b, float rad, float margin, ref float clearance)
    {
        Vector3 ab = b - a; float u = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude); Vector3 d = p - (a + ab * u); float dm = d.magnitude;
        if (dm - rad < clearance) clearance = dm - rad;
        float dep = SoftDepth(rad - dm, margin);
        return dep > 0 ? (dm < 1e-4f ? Vector3.right : d / dm) * dep : Vector3.zero;
    }

    /// <summary>Arm proxies against the body for the arm as it is posed now. Returns the smallest clearance; wristPush / elbowPush / tipPush say how to get out.</summary>
    float ArmProxies(bool right, HandTarget h, out Vector3 wristPush, out Vector3 tipPush, out Vector3 elbowPush)
    {
        var lo = B(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm); var ha = B(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        float s = torsoScale, handLen = 0.150f * s, worst = 1f, c, wsum = 0, esum = 0;
        armSideNow = right ? 1f : -1f;
        Vector3 w = transform.InverseTransformPoint(ha.position), e = transform.InverseTransformPoint(lo.position);
        Vector3 axis = (w - e).normalized;
        Vector3 fin = h.finger.normalized, palm = h.palm; palm = (palm - Vector3.Dot(palm, fin) * fin).normalized;
        Vector3 palmSide = (palm - Vector3.Dot(palm, axis) * axis); palmSide = palmSide.sqrMagnitude > 1e-6f ? palmSide.normalized : palm;
        Vector3 ulnar = Vector3.Cross(axis, palmSide) * (right ? 1f : -1f);
        Vector3 sum = Vector3.zero, esumV = Vector3.zero;
        // the pushes of the proxies are blended by depth (the deepest one decides); adding them up made the wrist jump out
        void Acc(Vector3 push) { float m2 = push.sqrMagnitude, wt = m2 * m2; sum += push * wt; wsum += wt; }
        void AccE(Vector3 push) { float m2 = push.sqrMagnitude, wt = m2 * m2; esumV += push * wt; esum += wt; }
        Vector3 p, q;
        if (armRad != null && armRad.Length >= 7)
        {
            // the measured arm: a sphere in each fifth of the forearm (what is near the elbow moves the ELBOW, what is near the wrist the wrist),
            // the cuff, the palm, the fingers
            handLen = armRad[6];
            // the point of the elbow itself (skin just beyond the joint): it grazed the waist by 6 mm when the arm hung close
            p = Vector3.LerpUnclamped(e, w, -0.07f); q = BodyPush(p, armRad[0] + 0.006f, out c); worst = Mathf.Min(worst, c); AccE(q); Acc(q * 0.40f);
            for (int k = 0; k < 5; k++)
            {
                float u = (k + 0.5f) / 5f; p = Vector3.Lerp(e, w, u); q = BodyPush(p, armRad[k] + Mathf.Lerp(0.010f, 0.004f, Mathf.Clamp01((h.bodyAvoid - 0.1f) / 0.4f)), out c); worst = Mathf.Min(worst, c);      // (6 mm more to spare for the arm that holds the microphone: only its elbow can give)
                // (an elbow that cannot swing clear needs the wrist to give as well; a wrist that is locked on the microphone cannot give - then the
                // elbow has to move by push / (1 - u) for this part of the forearm to move by push)
                Acc(q * Mathf.Max(u, 0.40f)); AccE(q * Mathf.Lerp(1f / Mathf.Max(0.35f, 1f - u), 1f - u, Mathf.Clamp01((h.bodyAvoid - 0.1f) / 0.4f)));
            }
            // the hand as it is: fingers bend at the knuckles towards the palm by how closed the hand is (a hanging, cupped hand points its
            // fingertips at the thigh - straight-finger proxies let them into the skirt)
            float cc = 0; if (h.curl != null && h.curl.Length >= 5) cc = (h.curl[1] + h.curl[2] + h.curl[3] + h.curl[4]) * 0.25f;
            float phi = Mathf.Min(150f, 115f * cc) * Mathf.Deg2Rad, kn = handLen * 0.55f, fl = handLen * 0.45f;
            Vector3 knuckle = w + fin * kn, fdir = fin * Mathf.Cos(phi) + palm * Mathf.Sin(phi), fmid = fin * Mathf.Cos(phi * 0.5f) + palm * Mathf.Sin(phi * 0.5f);
            p = w + fin * 0.050f + palm * 0.004f; q = BodyPush(p, 0.036f, out c); Acc(q); worst = Mathf.Min(worst, c);                 // palm
            p = knuckle + palm * 0.004f; q = BodyPush(p, 0.030f, out c); Acc(q); worst = Mathf.Min(worst, c);                           // knuckles
            p = knuckle + fmid * fl * 0.5f; q = BodyPush(p, 0.020f, out c); Acc(q); worst = Mathf.Min(worst, c);                        // middle of the fingers
            p = knuckle + fdir * (fl - 0.010f); tipPush = BodyPush(p, 0.016f, out c); worst = Mathf.Min(worst, c); Acc(tipPush * 0.6f);   // fingertips: the hand tilts, and the wrist gives too
            wristPush = wsum > 1e-30f ? sum / wsum : Vector3.zero; elbowPush = esum > 1e-30f ? esumV / esum : Vector3.zero;
            return worst;
        }
        // cuff: thick towards the palm and the little finger
        p = w - axis * 0.020f * s + palmSide * cuffPalm * s + ulnar * cuffUlnar * s;
        q = BodyPush(p, cuffR * s, out c); Acc(q); worst = Mathf.Min(worst, c);
        // forearm, a little above the middle
        p = Vector3.Lerp(e, w, 0.60f) + palmSide * forePalm * s + ulnar * foreUlnar * s;
        q = BodyPush(p, foreR * s, out c); Acc(q * 1.3f); worst = Mathf.Min(worst, c);
        // palm
        p = w + fin * 0.060f * s + palm * 0.006f * s;
        q = BodyPush(p, 0.022f * s, out c); Acc(q); worst = Mathf.Min(worst, c);
        wristPush = wsum > 1e-30f ? sum / wsum : Vector3.zero; elbowPush = Vector3.zero;
        // fingertips: the arm is not moved for them, the hand tilts
        p = w + fin * handLen;
        tipPush = BodyPush(p, 0.012f * s, out c); worst = Mathf.Min(worst, c);
        return worst;
    }

    /// <summary>resolve = true: the full iterative solve (used while baking the contact corrections). false: the baked correction is already in h;
    /// only a small clean-up of what the smoothing left (two passes, each a continuous function of the pose).</summary>
    void SolveArmSafe(bool right, ref HandTarget h, bool resolve)
    {
        if (!avoidBody) { SolveArm(right, h); return; }
        BodyFrame(); micAvoid = h.micAvoid; bodyAvoidNow = h.bodyAvoid;
        bool measured = armRad != null && armRad.Length >= 7;
        float handLen = measured ? armRad[6] : 0.150f * torsoScale, worst = 1f;
        int passes = measured ? 14 : resolve ? 8 : 5;
        for (int it = 0; it <= passes; it++)
        {
            SolveArm(right, h);
            worst = ArmProxies(right, h, out Vector3 wristPush, out Vector3 tipPush, out Vector3 elbowPush);
            bool tip = tipPush.sqrMagnitude > 1e-10f, elb = elbowPush.sqrMagnitude > 1e-10f;
            if (it == passes || (wristPush.sqrMagnitude < 1e-10f && !tip && !elb)) break;
            // the live clean-up is gentle (the baked correction did the real work): no finger re-tilt, most of the remaining push only
            // A hand that holds the microphone is where the microphone is: its wrist is not moved and its fingers are not tilted, only the elbow
            // may swing clear of the body. (Scaled by 0.1 per pass it still crept 6 cm off the microphone over 14 passes: the hand hung beside it.)
            float free = Mathf.Clamp01((h.bodyAvoid - 0.1f) / 0.4f);
            if (tip && free > 0.5f) h.finger = (h.finger.normalized * handLen + tipPush * (resolve ? 1.15f : 1f)).normalized;
            h.pos += transform.TransformVector(wristPush) * free;
            // the elbow gives way by swinging about the shoulder-wrist line: the pole takes the push (it is a direction: 30 cm of lever)
            if (elb) h.pole = (h.pole.normalized + elbowPush / 0.30f).normalized;
        }
        lastClearance = Mathf.Min(lastClearance, worst);
    }

    static readonly float[] NoCurl = new float[5];

    const float ReachIn = 0.95f, ReachOut = 1.00f;      // (0.7 s for 45 cm peaked at 1.9 m/s: a snatch)

    /// <summary>How much an arm belongs to the gesture system now (0 = the recorded take's, 1 = on its authored pose), over a recorded take.
    /// One reach: the weight is a minimum-jerk curve of TIME (bell-shaped speed, no stop on the way), ending when the hand is due on its
    /// pose and starting again when it is due to leave. For a hand that takes hold of something (the microphone: holdT0/holdT1, or an
    /// "own" entry with ownGrip) 'grip' is how far the fingers have closed: they start when the hand is a centimetre away and finish a
    /// sixth of a second after it has landed; on leaving they open first and the hand goes after them.</summary>
    public float OwnNow(bool right, float t, out float grip, out bool isGrip)
    {
        float s = 0f; grip = 0f; isGrip = false;
        if (right && holdT0 != null && holdT1 != null)
            for (int i = 0; i < holdT0.Length && i < holdT1.Length; i++)
            {
                float a = MinJerk((t - (holdT0[i] - ReachIn)) / ReachIn) * (1f - MinJerk((t - holdT1[i] - 0.16f) / ReachOut));
                if (a > s) { s = a; isGrip = true; grip = SStep(holdT0[i] - 0.10f, holdT0[i] + 0.16f, t) * (1f - SStep(holdT1[i], holdT1[i] + 0.20f, t)); }
            }
        if (ownT0 != null && ownT1 != null && ownSide != null)
            for (int i = 0; i < ownT0.Length && i < ownT1.Length && i < ownSide.Length; i++)
            {
                if (ownSide[i] != 2 && (ownSide[i] == 0) != right) continue;
                bool g = ownGrip != null && i < ownGrip.Length && ownGrip[i] != 0;
                float a = g ? MinJerk((t - (ownT0[i] - ReachIn)) / ReachIn) * (1f - MinJerk((t - ownT1[i] - 0.16f) / ReachOut)) : MinJerk((t - (ownT0[i] - 0.60f)) / 0.60f) * (1f - MinJerk((t - ownT1[i]) / 0.75f));
                if (a > s) { s = a; isGrip = g; grip = g ? SStep(ownT0[i] - 0.10f, ownT0[i] + 0.16f, t) * (1f - SStep(ownT1[i], ownT1[i] + 0.20f, t)) : 0f; }
            }
        return s;
    }

    /// <summary>Her own finger template for a recorded take: one number per hand (how closed it is), never quite flat.</summary>
    static float[] ClipCurl(float cc0) => new[] { 0.16f + 0.45f * cc0, Mathf.Clamp(0.05f + 0.78f * cc0, 0f, 0.9f), Mathf.Clamp(0.07f + 0.84f * cc0, 0f, 0.9f), Mathf.Clamp(0.09f + 0.88f * cc0, 0f, 0.9f), Mathf.Clamp(0.11f + 0.92f * cc0, 0f, 0.9f) };

    /// <summary>Recorded arms (BEAT2) know nothing of her coat, her cuffs or her proportions. The pose the clip gave is read back as a hand
    /// target (wrist, elbow side, hand direction) and solved again through the contact solver of the code gestures: wrists pushed out of the
    /// coat and off each other, fingertips tilted off the body, half of the wrist's roll handed to the forearm. The fingers stay the clip's.</summary>
    void ClipArmsSafe()
    {
        if (!avoidBody || armsSafe <= 0.01f) return;
        float w = armsSafe * armsSafe * (3f - 2f * armsSafe);
        var tg = new HandTarget[2]; Quaternion rinv = Quaternion.Inverse(transform.rotation);
        for (int side = 0; side < 2; side++)
        {
            bool right = side == 0;
            var up = B(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm); var lo = B(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm); var ha = B(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            Vector3 S = up.position, dir = (ha.position - S).normalized, pole = lo.position - S; pole -= Vector3.Dot(pole, dir) * dir;
            Quaternion hq = rinv * ha.rotation;
            Vector3 fin = hq * (right ? Vector3.right : Vector3.left), fore = transform.InverseTransformDirection((ha.position - lo.position).normalized);
            // the wrist bends 35 deg freely, then gives less and less (never more than about 55): a fit that folds the hand back on the forearm reads as broken
            float bend = Vector3.Angle(fore, fin);
            if (bend > 35f) fin = Vector3.Slerp(fore, fin, (35f + 20f * (1f - Mathf.Exp(-(bend - 35f) / 20f))) / bend);
            float cc0 = Mathf.Clamp01(right ? curlNowR : curlNowL);
            var cu0 = ClipCurl(cc0);
            tg[side] = new HandTarget { pos = ha.position, pole = pole.sqrMagnitude > 1e-8f ? transform.InverseTransformDirection(pole.normalized) : Vector3.down, finger = fin, palm = hq * Vector3.down, curl = cu0, spread = 0.08f * (1f - cc0), micAvoid = 1f, bodyAvoid = 1f };
        }
        {   // an arm that hangs: the speaker's hung beside a wider body; on her the same shoulder angle holds the hand out in an "A". Bring the wrist in
            // to where her own arm hangs (just outside the shoulder line); the body proxy still keeps it off the skirt.
            Vector3 hipsC = transform.InverseTransformPoint(B(HumanBodyBones.Hips).position);
            for (int side = 0; side < 2; side++)
            {
                var up = B(side == 0 ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm); float want = Mathf.Abs(transform.InverseTransformPoint(up.position).x - hipsC.x) + 0.075f;
                Vector3 wc = transform.InverseTransformPoint(tg[side].pos); float lat = Mathf.Abs(wc.x - hipsC.x);
                float hang = (1f - SStep(0.0f, 0.12f, wc.y - hipsC.y)) * (1f - SStep(0.10f, 0.20f, wc.z - hipsC.z));
                if (hang > 0.001f && lat > want) wc.x = hipsC.x + Mathf.Sign(wc.x - hipsC.x) * Mathf.Lerp(lat, want, 0.8f * hang);
                // review 2026-11-13: no hand behind the back, no crossed arms - each wrist stays in front of the body plane and on its own side
                float own = side == 0 ? 1f : -1f, ox = (wc.x - hipsC.x) * own, zf = wc.z - hipsC.z;
                ox += SoftDepth(0.035f - ox, 0.03f); zf += SoftDepth(0.0f - zf, 0.03f);
                wc.x = hipsC.x + ox * own; wc.z = hipsC.z + zf; tg[side].pos = transform.TransformPoint(wc);
            }
        }
        SeparateHands(ref tg[0], ref tg[1], 0.055f);
        {
            Vector3 dd = tg[0].pos - tg[1].pos; float m = dd.magnitude, k = SoftDepth(0.090f - m, 0.015f) * 0.5f;        // the cuffs are thick
            if (k > 0 && m > 1e-5f) { tg[0].pos += dd / m * k; tg[1].pos -= dd / m * k; }
            // a palm keeps off the other forearm (elbow - wrist)
            for (int side = 0; side < 2; side++)
            {
                var lo2 = B(side == 0 ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm); Vector3 a = lo2.position, b = tg[1 - side].pos;
                Vector3 ab = b - a;
                foreach (float along in new[] { 0.06f, 0.13f })
                {
                    Vector3 pm = tg[side].pos + transform.TransformDirection(tg[side].finger.normalized) * along; float uu = Mathf.Clamp01(Vector3.Dot(pm - a, ab) / ab.sqrMagnitude);
                    Vector3 off = pm - (a + ab * uu); float om = off.magnitude, kk = SoftDepth(0.060f - om, 0.012f);
                    if (kk > 0 && om > 1e-5f) { tg[side].pos += off / om * kk * 0.7f; tg[1 - side].pos -= off / om * kk * 0.3f * uu; }
                }
            }
        }
        // contacts: baked and smoothed in time (BakeClipArms); solved afresh only while they are being recorded, or when there is no bake
        bool baked = bake != null && bake.armCorr != null && clipPass != 1; if (baked) bake.SampleArm(previewT, sA);
        for (int side = 0; side < 2; side++)
        {
            bool right = side == 0; float ws = w; if (ws <= 0.01f) continue; int o = side * 9;
            var up = B(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm); var lo = B(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm); var ha = B(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            Quaternion u0 = up.localRotation, l0 = lo.localRotation, h0 = ha.localRotation;
            int baseId = (int)(right ? HumanBodyBones.RightThumbProximal : HumanBodyBones.LeftThumbProximal); var fing = new Quaternion[15];
            for (int i = 0; i < 15; i++) { var t = B((HumanBodyBones)(baseId + i)); if (t != null) fing[i] = t.localRotation; }
            if (baked)
            {
                tg[side].pos += transform.TransformVector(new Vector3(sA[o], sA[o + 1], sA[o + 2]));
                tg[side].finger = (tg[side].finger.normalized + new Vector3(sA[o + 3], sA[o + 4], sA[o + 5])).normalized;
                tg[side].pole = (tg[side].pole.normalized + new Vector3(sA[o + 6], sA[o + 7], sA[o + 8])).normalized;
            }
            if (clipPass != 0 || !baked)
            {
                Vector3 p0 = tg[side].pos, f0 = tg[side].finger.normalized, q0 = tg[side].pole.normalized;
                SolveArmSafe(right, ref tg[side], true);
                if (clipPass != 0)
                {
                    Vector3 dp = transform.InverseTransformVector(tg[side].pos - p0), df = tg[side].finger.normalized - f0, dq = tg[side].pole.normalized - q0;
                    clipOut[o] = dp.x; clipOut[o + 1] = dp.y; clipOut[o + 2] = dp.z; clipOut[o + 3] = df.x; clipOut[o + 4] = df.y; clipOut[o + 5] = df.z; clipOut[o + 6] = dq.x; clipOut[o + 7] = dq.y; clipOut[o + 8] = dq.z;
                }
            }
            else SolveArm(right, tg[side]);
            // fingers: her own template, posed by the solve itself (review: the recorded fingers looked twisted)
            for (int i = 0; i < 15; i++) { var t = B((HumanBodyBones)(baseId + i)); if (t != null) t.localRotation = Quaternion.Slerp(fing[i], t.localRotation, ws); }
            if (ws < 0.999f) { up.localRotation = Quaternion.Slerp(u0, up.localRotation, ws); lo.localRotation = Quaternion.Slerp(l0, lo.localRotation, ws); ha.localRotation = Quaternion.Slerp(h0, ha.localRotation, ws); }
        }
    }

    /// <summary>Two hands in front of the body must not pass through each other: push the wrists apart along the line between the palms.</summary>
    void SeparateHands(ref HandTarget r, ref HandTarget l, float minDist)
    {
        Vector3 pr = r.pos + transform.TransformDirection(r.finger.normalized) * 0.06f, pl = l.pos + transform.TransformDirection(l.finger.normalized) * 0.06f;
        Vector3 d = pr - pl; float m = d.magnitude;
        float k = SoftDepth(minDist - m, 0.012f) * 0.5f;
        if (k <= 0 || m < 1e-5f) return;
        Vector3 n = d / m;
        r.pos += n * k; l.pos -= n * k;
    }
}
