using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Offline capture: in play mode, steps the clip at a fixed frame rate (Time.captureFramerate), poses the character
/// through SingDriver.previewTime, renders the camera into a render texture and writes one JPG per frame.
/// Spring bones (hair, clothes) run normally because play mode is real; only the clock is fixed.
/// </summary>
public class FrameDumper : MonoBehaviour
{
    public SingDriver driver;
    public Camera cam;
    public int fps = 30;
    public float duration = 23f;
    public int width = 1920, height = 1080;
    public string outDir = "Capture/sing_v1";
    public float distA = 1.62f, distB = 1.30f, upA = -0.08f, upB = 0f, yawA = 9f, yawB = -5f;   // camera: start -> end
    public int warmupFrames = 45;                       // let hair and clothes settle on the first pose
    [Header("Formal version: camera path and stage colours from a key file (cam t dist up yaw / col t r g b light)")]
    public string showFile = "";
    public bool stage;                                  // glow behind her + floating dust
    public float startTime;                             // render only [startTime, startTime + duration]

    readonly List<float[]> camKeys = new List<float[]>(), colKeys = new List<float[]>(), shots = new List<float[]>();   // shot: t yaw0 dist0 up0 yaw1 dist1 up1
    // One moving camera cut to the music (review 2026-11-13: "no hard cuts; moves in time with the song"):
    //   cam2 t yaw dist up elev [pulse]   a key of the camera path; the camera travels through the keys on a smooth curve (keys sit on bar lines)
    //   xfade t dur                       at t the picture dissolves over dur seconds from the path before t to the path that starts at t
    //   beat t t t ...                    beat times; 'pulse' is a small push in on every beat (fraction of the distance)
    readonly List<float[]> cam2 = new List<float[]>(), xfades = new List<float[]>(); readonly List<float> beatT = new List<float>();
    public int dissolveFrames = 5;                      // soft cut between two cameras
    Transform glowCard;

    static float MJ(float u) { u = Mathf.Clamp01(u); return u * u * u * (u * (u * 6f - 15f) + 10f); }

    /// <summary>Keys are held; each new key is approached with one eased move that begins 'lead' seconds before its time.</summary>
    static float[] Eval(List<float[]> keys, float t, float lead, float move)
    {
        int m = keys[0].Length - 1; var cur = new float[m]; System.Array.Copy(keys[0], 1, cur, 0, m);
        for (int k = 1; k < keys.Count; k++)
        {
            float ts = keys[k][0] - lead; if (t <= ts) break;
            float d = k + 1 < keys.Count ? Mathf.Min(move, Mathf.Max(0.8f, keys[k + 1][0] - keys[k][0])) : move;
            float u = MJ((t - ts) / d);
            for (int c = 0; c < m; c++) cur[c] += (keys[k][c + 1] - cur[c]) * u;
        }
        return cur;
    }

    void LoadShow()
    {
        camKeys.Clear(); colKeys.Clear(); shots.Clear(); cam2.Clear(); xfades.Clear(); beatT.Clear();
        if (string.IsNullOrEmpty(showFile) || !File.Exists(showFile)) return;
        foreach (var line in File.ReadAllLines(showFile))
        {
            var q = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (q.Length > 1 && q[0] == "beat") { for (int i = 1; i < q.Length; i++) beatT.Add(float.Parse(q[i], CultureInfo.InvariantCulture)); continue; }
            if (q.Length >= 3 && q[0] == "xfade") { xfades.Add(new[] { float.Parse(q[1], CultureInfo.InvariantCulture), float.Parse(q[2], CultureInfo.InvariantCulture) }); continue; }
            if (q.Length >= 6 && q[0] == "cam2") { var k2 = new float[6]; for (int i = 1; i < q.Length && i <= 6; i++) k2[i - 1] = float.Parse(q[i], CultureInfo.InvariantCulture); cam2.Add(k2); continue; }
            if (q.Length < 5 || (q[0] != "cam" && q[0] != "col" && q[0] != "shot")) continue;
            var v = new float[q.Length - 1];
            for (int i = 1; i < q.Length; i++) v[i - 1] = float.Parse(q[i], CultureInfo.InvariantCulture);
            (q[0] == "cam" ? camKeys : q[0] == "shot" ? shots : colKeys).Add(v);
        }
        camKeys.Sort((a, b) => a[0].CompareTo(b[0])); colKeys.Sort((a, b) => a[0].CompareTo(b[0])); shots.Sort((a, b) => a[0].CompareTo(b[0]));
        cam2.Sort((a, b) => a[0].CompareTo(b[0])); xfades.Sort((a, b) => a[0].CompareTo(b[0])); beatT.Sort(); pathKeys = null;
    }

    Material glowMat, floorMat; Light keyLight;

    /// <summary>Multi-camera: shot k runs from its time to the next shot's; inside it the camera moves from its start to its end set-up
    /// (push / pull = distance, pan = yaw, boom = height). u may run past 1: the outgoing camera keeps moving through a dissolve.</summary>
    Vector3 ShotCamPos(int k, float t, Vector3 headPos)
    {
        var s = shots[k]; float t1 = k + 1 < shots.Count ? shots[k + 1][0] : startTime + duration;
        float u = (t - s[0]) / Mathf.Max(0.5f, t1 - s[0]), e = u <= 1 ? 0.45f * u + 0.55f * u * u * (3f - 2f * u) : u;
        float yaw = Mathf.LerpUnclamped(s[1], s[4], e), dist = Mathf.LerpUnclamped(s[2], s[5], e), up = Mathf.LerpUnclamped(s[3], s[6], e);
        float elev = s.Length > 8 ? Mathf.LerpUnclamped(s[7], s[8], e) : 0f;
        return headPos + new Vector3(0, up, 0) + Quaternion.Euler(0, yaw, 0) * driver.transform.forward * dist + new Vector3(0, 0.03f + 0.02f * dist + elev, 0);
    }

    /// <summary>Where the listener is for the character at time t: the camera of the running shot; from 0.3 s before a cut to 0.3 s after it the
    /// point glides from the outgoing camera to the incoming one.</summary>
    Vector3 ViewerAt(int k, float t, Vector3 headPos)
    {
        Vector3 p = ShotCamPos(k, t, headPos); const float E = 0.30f;
        if (k + 1 < shots.Count && t > shots[k + 1][0] - E) { float u = (t - (shots[k + 1][0] - E)) / E; u = u * u * (3f - 2f * u); p = Vector3.Lerp(p, ShotCamPos(k + 1, shots[k + 1][0], headPos), 0.5f * u); }
        if (k > 0 && t < shots[k][0] + E) { float u = (t - shots[k][0]) / E; u = u * u * (3f - 2f * u); p = Vector3.Lerp(ShotCamPos(k - 1, t, headPos), p, 0.5f + 0.5f * u); }
        return p;
    }

    void PoseShot(int k, float t, Vector3 headPos)
    {
        var s = shots[k]; float t1 = k + 1 < shots.Count ? shots[k + 1][0] : startTime + duration;
        float u = (t - s[0]) / Mathf.Max(0.5f, t1 - s[0]), e = u <= 1 ? 0.45f * u + 0.55f * u * u * (3f - 2f * u) : u;
        float yaw = Mathf.LerpUnclamped(s[1], s[4], e), dist = Mathf.LerpUnclamped(s[2], s[5], e), up = Mathf.LerpUnclamped(s[3], s[6], e);
        Vector3 tg = headPos + new Vector3(0, up, 0), dr = Quaternion.Euler(0, yaw, 0) * driver.transform.forward;
        float elev = s.Length > 8 ? Mathf.LerpUnclamped(s[7], s[8], e) : 0f;                    // camera above (+) or below (-) eye level
        cam.transform.position = tg + dr * dist + new Vector3(0, 0.03f + 0.02f * dist + elev, 0);
        cam.transform.LookAt(tg);
        if (glowCard != null) { Vector3 f = cam.transform.forward; f.y = 0; f.Normalize(); glowCard.position = new Vector3(headPos.x, 1.25f, headPos.z) + f * 3.0f; glowCard.rotation = Quaternion.LookRotation(f, Vector3.up); }
    }

    // ------------------------------------------------------------------ the camera path (cam2 / xfade)
    List<float[]>[] pathKeys;

    /// <summary>Which path runs at t: path p holds the keys from dissolve p-1 (inclusive) to dissolve p (exclusive).</summary>
    int PathOf(float t) { int p = 0; foreach (var x in xfades) if (t >= x[0]) p++; return p; }

    /// <summary>yaw, dist, up, elev, pulse of path p at time t: a Hermite curve through its keys (tangents from the neighbours, so the camera
    /// never stops between two moves unless two keys hold it), continued along its end tangents before the first and after the last key -
    /// through a dissolve both cameras keep moving.</summary>
    float[] PathState(int p, float t)
    {
        if (pathKeys == null)
        {
            pathKeys = new List<float[]>[xfades.Count + 1];
            for (int i = 0; i <= xfades.Count; i++) { float a = i == 0 ? -1e9f : xfades[i - 1][0], b = i < xfades.Count ? xfades[i][0] : 1e9f; pathKeys[i] = cam2.FindAll(k => k[0] >= a - 1e-3f && k[0] < b - 1e-3f); }
        }
        var ks = pathKeys[Mathf.Clamp(p, 0, pathKeys.Length - 1)]; var o = new float[5];
        if (ks.Count == 0) { o[1] = 3f; return o; }
        if (ks.Count == 1) { for (int c = 0; c < 5; c++) o[c] = ks[0][c + 1]; return o; }
        int n = ks.Count; int i0 = 0; while (i0 < n - 2 && ks[i0 + 1][0] <= t) i0++;
        float t0 = ks[i0][0], t1 = ks[i0 + 1][0], h = t1 - t0;
        for (int c = 0; c < 5; c++)
        {
            float y0 = ks[i0][c + 1], y1 = ks[i0 + 1][c + 1];
            float m0 = i0 > 0 ? (y1 - ks[i0 - 1][c + 1]) / (t1 - ks[i0 - 1][0]) : (y1 - y0) / h, m1 = i0 + 2 < n ? (ks[i0 + 2][c + 1] - y0) / (ks[i0 + 2][0] - t0) : (y1 - y0) / h;
            if (t < t0) o[c] = y0 + m0 * Mathf.Max(t - t0, -1.5f);
            else if (t > t1) o[c] = y1 + m1 * Mathf.Min(t - t1, 1.5f);
            else { float u = (t - t0) / h, u2 = u * u, u3 = u2 * u; o[c] = (2 * u3 - 3 * u2 + 1) * y0 + (u3 - 2 * u2 + u) * h * m0 + (-2 * u3 + 3 * u2) * y1 + (u3 - u2) * h * m1; }
        }
        o[1] = Mathf.Max(0.7f, o[1]);
        return o;
    }

    /// <summary>A small push in on every beat: quick in (60 ms), slow out (0.3 s).</summary>
    float BeatEnv(float t)
    {
        float e = 0; for (int i = 0; i < beatT.Count; i++) { float d = t - beatT[i]; if (d < 0) break; if (d < 1.2f) e = Mathf.Max(e, Mathf.Min(1f, d / 0.06f) * Mathf.Exp(-Mathf.Max(0f, d - 0.06f) / 0.30f)); }
        return e;
    }

    Vector3 PathCamPos(int p, float t, Vector3 headPos, out Vector3 tg)
    {
        var st = PathState(p, t); float dist = st[1] * (1f - st[4] * BeatEnv(t));
        tg = headPos + new Vector3(0, st[2], 0);
        return tg + Quaternion.Euler(0, st[0], 0) * driver.transform.forward * dist + new Vector3(0, 0.03f + 0.02f * dist + st[3], 0);
    }

    void PosePath(int p, float t, Vector3 headPos, Camera c)
    {
        c.transform.position = PathCamPos(p, t, headPos, out Vector3 tg); c.transform.LookAt(tg);
        if (glowCard != null) { Vector3 f = c.transform.forward; f.y = 0; f.Normalize(); glowCard.position = new Vector3(headPos.x, 1.25f, headPos.z) + f * 3.0f; glowCard.rotation = Quaternion.LookRotation(f, Vector3.up); }
    }

    /// <summary>Inside a dissolve? other = the path that is not the running one, wMain = how much of the picture is the running path's.</summary>
    bool XfadeAt(float t, out int other, out float wMain)
    {
        other = 0; wMain = 1f;
        for (int i = 0; i < xfades.Count; i++)
        {
            float T = xfades[i][0], d = Mathf.Max(0.05f, xfades[i][1]); if (t < T - d * 0.5f || t > T + d * 0.5f) continue;
            float a = (t - (T - d * 0.5f)) / d; a = a * a * (3f - 2f * a);         // share of the incoming path
            if (t < T) { other = i + 1; wMain = 1f - a; } else { other = i; wMain = a; }
            return true;
        }
        return false;
    }

    void BuildMic()
    {
        if (!driver.micOn) return;
        var lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
        Material Mat(Color c, float metal, float smooth) { var m = new Material(lit) { color = c }; m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", metal); m.SetFloat("_Smoothness", smooth); return m; }
        Material dark = Mat(new Color(0.05f, 0.05f, 0.06f), 0.8f, 0.55f), steel = Mat(new Color(0.62f, 0.63f, 0.66f), 0.9f, 0.6f), grille = Mat(new Color(0.32f, 0.33f, 0.36f), 0.85f, 0.35f);
        Transform ch = driver.transform; Quaternion rot = ch.rotation; Vector3 org = driver.stageOrigin;
        Vector3 W(Vector3 local) => org + rot * local;
        var root = new GameObject("MicStand").transform;
        GameObject Part(PrimitiveType type, Vector3 pos, Quaternion r, Vector3 scale, Material m)
        { var g = GameObject.CreatePrimitive(type); Destroy(g.GetComponent<Collider>()); g.transform.SetParent(root); g.transform.position = pos; g.transform.rotation = r; g.transform.localScale = scale; g.GetComponent<Renderer>().sharedMaterial = m; return g; }
        Vector3 d = (rot * driver.micDir).normalized, m0 = W(driver.micPos); Quaternion along = Quaternion.FromToRotation(Vector3.up, d);
        Part(PrimitiveType.Sphere, m0, Quaternion.identity, Vector3.one * 0.054f, grille);                                  // grille
        Part(PrimitiveType.Cylinder, m0 + d * 0.022f, along, new Vector3(0.050f, 0.006f, 0.050f), steel);                    // ring under the grille
        Part(PrimitiveType.Cylinder, m0 + d * 0.095f, along, new Vector3(0.040f, 0.072f, 0.040f), dark);                     // body (cylinder mesh is 2 units tall)
        Part(PrimitiveType.Cylinder, m0 + d * 0.175f, along, new Vector3(0.030f, 0.012f, 0.030f), steel);                    // tail / connector
        Vector3 top = W(driver.MicPolePoint);
        Part(PrimitiveType.Cylinder, top, along, new Vector3(0.052f, 0.022f, 0.052f), dark);                                 // clip
        Part(PrimitiveType.Sphere, top - Vector3.up * 0.030f, Quaternion.identity, Vector3.one * 0.030f, dark);              // swivel
        float h1 = top.y - 0.045f, mid = 0.92f;
        Part(PrimitiveType.Cylinder, new Vector3(top.x, (h1 + mid) * 0.5f, top.z), Quaternion.identity, new Vector3(0.018f, (h1 - mid) * 0.5f, 0.018f), steel);   // upper tube
        Part(PrimitiveType.Cylinder, new Vector3(top.x, mid, top.z), Quaternion.identity, new Vector3(0.036f, 0.030f, 0.036f), dark);                            // clutch
        Part(PrimitiveType.Cylinder, new Vector3(top.x, mid * 0.5f + 0.01f, top.z), Quaternion.identity, new Vector3(0.026f, mid * 0.5f - 0.01f, 0.026f), dark);  // lower tube
        Part(PrimitiveType.Cylinder, new Vector3(top.x, 0.012f, top.z), Quaternion.identity, new Vector3(0.30f, 0.012f, 0.30f), dark);                           // round base
        // a dark stage floor that takes her shadow
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane); Destroy(floor.GetComponent<Collider>()); floor.name = "Floor"; floor.transform.position = org; floor.transform.localScale = Vector3.one * 4f;
        floorMat = Mat(new Color(0.012f, 0.012f, 0.016f), 0f, 0.12f); floorMat.SetFloat("_EnvironmentReflections", 0f); floorMat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF"); floorMat.SetFloat("_SpecularHighlights", 0f); floorMat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        floor.GetComponent<Renderer>().sharedMaterial = floorMat;
    }

    static Texture2D SoftDot(int size, float power)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f, r = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                px[y * size + x] = new Color(1, 1, 1, Mathf.Pow(r * r * (3f - 2f * r), power));
            }
        tex.SetPixels32(px); tex.Apply(); return tex;
    }

    void BuildStage()
    {
        keyLight = FindFirstObjectByType<Light>();
        var sh = Shader.Find("Sprites/Default"); if (sh == null) return;
        Transform ch = driver.transform;
        // a soft glow on a big card behind her: the plain backdrop gets depth, and its colour follows the song
        var card = GameObject.CreatePrimitive(PrimitiveType.Quad); card.name = "Glow"; Destroy(card.GetComponent<Collider>());
        card.transform.position = ch.position - ch.forward * 2.6f + Vector3.up * 1.25f; card.transform.rotation = Quaternion.LookRotation(-ch.forward, Vector3.up); card.transform.localScale = new Vector3(9f, 6.5f, 1f);
        glowMat = new Material(sh) { mainTexture = SoftDot(256, 1.4f) }; card.GetComponent<Renderer>().sharedMaterial = glowMat;
        if (shots.Count > 0) { glowCard = card.transform; card.transform.localScale = new Vector3(13f, 8.5f, 1f); }
        card.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        // dust drifting up through the light
        var go = new GameObject("Dust"); go.transform.position = ch.position + Vector3.up * 1.15f - ch.forward * 0.7f; go.transform.rotation = ch.rotation;
        var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.loop = true; main.prewarm = true; main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 12f); main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.05f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.010f, 0.030f); main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.93f, 0.85f, 0.10f), new Color(1f, 0.96f, 0.92f, 0.38f));
        main.maxParticles = 220; main.simulationSpace = ParticleSystemSimulationSpace.World; main.gravityModifier = -0.004f;
        var em = ps.emission; em.rateOverTime = 16f;
        var shp = ps.shape; shp.shapeType = ParticleSystemShapeType.Box; shp.scale = new Vector3(3.6f, 2.4f, 2.6f);
        var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World; vel.x = new ParticleSystem.MinMaxCurve(-0.012f, 0.012f); vel.y = new ParticleSystem.MinMaxCurve(0.010f, 0.035f); vel.z = new ParticleSystem.MinMaxCurve(-0.010f, 0.010f);
        var noise = ps.noise; noise.enabled = true; noise.strength = 0.035f; noise.frequency = 0.35f; noise.scrollSpeed = 0.08f;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.25f), new GradientAlphaKey(1, 0.7f), new GradientAlphaKey(0, 1) });
        col.color = g;
        var pr = go.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = new Material(sh) { mainTexture = SoftDot(64, 1f) }; pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ps.Play();
    }

    public static int Written;
    public static bool Done;

    [Header("Live: play in real time in the Game view (audio is the clock) instead of writing frames")]
    public bool live;
    public bool liveLoop = true;

    Transform head; Vector3 anchor, headS; int shotNow = -1; bool show;

    /// <summary>Camera, lens position for her eyes, stage colours at clip time t. dt: how much time passed since the last call (for the camera's
    /// soft follow of her head); first = the first frame (nothing to follow from).</summary>
    void StepCamera(float t, bool first, float dt)
    {
        if (first) { anchor = head.position; headS = head.position; }
        // slow push-in with a small drift to the side
        float k = Mathf.Clamp01((t - startTime) / Mathf.Max(0.1f, duration));
        float e = k * k * (3 - 2 * k);
        Vector3 target = anchor + new Vector3(0, Mathf.Lerp(upA, upB, e), 0);
        Vector3 dir = Quaternion.Euler(0, Mathf.Lerp(yawA, yawB, k), 0) * driver.transform.forward;
        cam.transform.position = target + dir * Mathf.Lerp(distA, distB, e) + new Vector3(0, 0.03f, 0);
        cam.transform.LookAt(target);
        cam.fieldOfView = 26f;
        driver.viewerW = cam.transform.position; driver.hasViewer = true;
        if (cam2.Count > 0)
        {
            headS = first ? head.position : Vector3.Lerp(headS, head.position, 1f - Mathf.Exp(-dt / 0.45f));
            int pth = PathOf(t); PosePath(pth, t, headS, cam);
            // her eyes find the lens; through a dissolve they travel from the outgoing camera to the incoming one
            Vector3 lens = cam.transform.position;
            if (XfadeAt(t, out int oth, out float wm)) lens = Vector3.Lerp(PathCamPos(oth, t, headS, out _), lens, wm);
            driver.viewerW = lens; driver.hasViewer = true;
        }
        else if (shots.Count > 0)
        {
            headS = first ? head.position : Vector3.Lerp(headS, head.position, 1f - Mathf.Exp(-dt / 0.35f));
            shotNow = 0; for (int si = 0; si < shots.Count; si++) if (shots[si][0] <= t + 1e-4f) shotNow = si;
            PoseShot(shotNow, t, headS);
            driver.viewerW = ViewerAt(shotNow, t, headS); driver.hasViewer = true;
        }
        else if (show)
        {
            // one continuous camera: it glides from framing to framing, follows the head softly and never cuts
            headS = first ? head.position : Vector3.Lerp(headS, head.position, 1f - Mathf.Exp(-dt / 0.9f));
            var cv = Eval(camKeys, t, 0.5f, 2.8f);
            float dist = cv[0] * (1f + 0.012f * Mathf.Sin(t * 0.19f)), yaw = cv[2] + 1.1f * Mathf.Sin(t * 0.23f + 0.7f);
            Vector3 tg = new Vector3(Mathf.Lerp(anchor.x, headS.x, 0.6f), Mathf.Lerp(anchor.y, headS.y, 0.75f), Mathf.Lerp(anchor.z, headS.z, 0.3f)) + new Vector3(0, cv[1], 0);
            Vector3 dr = Quaternion.Euler(0, yaw, 0) * driver.transform.forward;
            cam.transform.position = tg + dr * dist + new Vector3(0, 0.03f + 0.02f * dist, 0);
            cam.transform.LookAt(tg);
            driver.viewerW = cam.transform.position;
        }
        if (colKeys.Count > 0)
        {
            var c = Eval(colKeys, t, 0.3f, 1.8f); float breathe = 1f + 0.06f * Mathf.Sin(t * 0.9f);
            float fadeIn = Mathf.Clamp01(t / 1.5f), fadeOut = Mathf.Clamp01((startTime + duration - t) / 2.5f), fd = (show && startTime <= 0 ? fadeIn : 1f) * (duration > 60 && !live ? fadeOut : 1f);
            cam.backgroundColor = new Color(c[0], c[1], c[2]) * 0.50f * fd;
            if (glowMat != null) glowMat.color = new Color(c[0] * 1.8f, c[1] * 1.8f, c[2] * 1.8f, 0.85f) * breathe * fd;
            if (keyLight != null) keyLight.intensity = c[3] * fd;
            if (floorMat != null) { var fc = new Color(c[0], c[1], c[2]) * 0.16f * fd; floorMat.color = fc; floorMat.SetColor("_BaseColor", fc); }
        }
    }

    IEnumerator Start()
    {
        Written = 0; Done = false;
        head = driver.animator.GetBoneTransform(HumanBodyBones.Head);
        anchor = head.position;                 // fixed anchor (set once the first pose has settled): the camera does not chase the head sway
        LoadShow();
        if (stage)
        {
            // a saved set (StageRig / MicStandRig in the scene) is used as it is; otherwise the set is put up for this run
            var sr = FindFirstObjectByType<StageRig>(); var mr = FindFirstObjectByType<MicStandRig>();
            if (sr != null)
            {
                glowMat = sr.glow.material; floorMat = sr.floor.material; keyLight = sr.key != null ? sr.key : FindFirstObjectByType<Light>();
                if (shots.Count > 0) glowCard = sr.glow.transform;
                if (mr != null) mr.gameObject.SetActive(driver.micOn);
            }
            else { BuildStage(); if (mr == null) BuildMic(); }
        }
        headS = head.position; show = camKeys.Count > 0;
        if (live) { yield return Live(); yield break; }

        Time.captureFramerate = fps;
        driver.usePreviewTime = true;
        if (driver.audioSource != null) driver.audioSource.mute = true;
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        Directory.CreateDirectory(outDir);
        var texB = new Texture2D(width, height, TextureFormat.RGB24, false);
        int n = Mathf.RoundToInt(duration * fps);
        var log = new System.Text.StringBuilder();   // head rotation relative to the character, for QA
        log.AppendLine("frame,qx,qy,qz,qw,rx,ry,rz,lx,ly,lz,clear");
        for (int f = -warmupFrames; f < n; f++)
        {
            float t = startTime + Mathf.Max(0, f / (float)fps);
            driver.previewTime = t;
            StepCamera(t, f <= 0, 1f / fps);      // (the pose saved in the scene may differ from the clip's first frame: the anchor is taken while warming up)
            yield return new WaitForEndOfFrame();
            if (f < 0) continue;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            RenderTexture.active = null;
            // soft cut: for the first frames of a shot the outgoing camera is rendered as well and the two are cross-faded
            if (shots.Count > 0 && shotNow > 0 && dissolveFrames > 0)
            {
                float into = (t - shots[shotNow][0]) * fps;
                if (into < dissolveFrames - 0.5f && shots[shotNow][0] > startTime + 0.01f)
                {
                    float alpha = (into + 1f) / (dissolveFrames + 1f);
                    PoseShot(shotNow - 1, t, headS); cam.Render();
                    RenderTexture.active = rt; texB.ReadPixels(new Rect(0, 0, width, height), 0, 0); RenderTexture.active = null;
                    var a = texB.GetPixels32(); var b = tex.GetPixels32();
                    int ia = Mathf.RoundToInt((1f - alpha) * 256f), ib = 256 - ia;
                    for (int i = 0; i < b.Length; i++) { b[i].r = (byte)((a[i].r * ia + b[i].r * ib) >> 8); b[i].g = (byte)((a[i].g * ia + b[i].g * ib) >> 8); b[i].b = (byte)((a[i].b * ia + b[i].b * ib) >> 8); }
                    tex.SetPixels32(b); tex.Apply(false);
                    PoseShot(shotNow, t, headS);
                }
            }
            if (cam2.Count > 0 && XfadeAt(t, out int other, out float wMain) && wMain < 0.999f)
            {
                PosePath(other, t, headS, cam); cam.Render();
                RenderTexture.active = rt; texB.ReadPixels(new Rect(0, 0, width, height), 0, 0); RenderTexture.active = null;
                var a = texB.GetPixels32(); var b = tex.GetPixels32();
                int ia = Mathf.RoundToInt((1f - wMain) * 256f), ib = 256 - ia;
                for (int i = 0; i < b.Length; i++) { b[i].r = (byte)((a[i].r * ia + b[i].r * ib) >> 8); b[i].g = (byte)((a[i].g * ia + b[i].g * ib) >> 8); b[i].b = (byte)((a[i].b * ia + b[i].b * ib) >> 8); }
                tex.SetPixels32(b); tex.Apply(false);
                PosePath(PathOf(t), t, headS, cam);
            }
            File.WriteAllBytes(Path.Combine(outDir, $"f_{f:00000}.jpg"), tex.EncodeToJPG(95));
            Written = f + 1;
            Quaternion hq = Quaternion.Inverse(driver.transform.rotation) * head.rotation;
            log.Append(f).Append(',').Append(hq.x.ToString("F6")).Append(',').Append(hq.y.ToString("F6")).Append(',').Append(hq.z.ToString("F6")).Append(',').Append(hq.w.ToString("F6"));
            driver.WristPositions(out Vector3 wr, out Vector3 wl);
            log.Append(',').Append(wr.x.ToString("F5")).Append(',').Append(wr.y.ToString("F5")).Append(',').Append(wr.z.ToString("F5")).Append(',').Append(wl.x.ToString("F5")).Append(',').Append(wl.y.ToString("F5")).Append(',').Append(wl.z.ToString("F5")).Append(',').Append(driver.lastClearance.ToString("F4")).AppendLine();
        }
        File.WriteAllText(Path.Combine(outDir, "head.csv"), log.ToString());
        cam.targetTexture = null;
        Done = true;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    // ------------------------------------------------------------------ live player
    bool livePaused; float liveEnd, liveAlpha; Camera camB; RenderTexture rtB;

    /// <summary>Real-time playback in the Game view: the song plays, the character follows the audio clock, the cameras cut as in the film
    /// (hard cuts: the 5-frame dissolve of the rendered film needs two renders per frame). Buttons in the Game view: pause, replay, seek.</summary>
    IEnumerator Live()
    {
        Time.captureFramerate = 0; driver.usePreviewTime = false; cam.targetTexture = null; QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;      // (it ran uncapped)
        var au = driver.audioSource; au.mute = false; au.loop = false; au.Stop();
        liveEnd = Mathf.Min(au.clip.length - 0.05f, duration > 0.5f ? startTime + duration : au.clip.length);
        au.time = Mathf.Clamp(startTime, 0f, au.clip.length - 0.1f); au.Play();
        bool first = true;
        while (true)
        {
            float t = driver.ClipTime;
            StepCamera(t, first, Time.deltaTime); first = false;
            liveAlpha = 0f;
            if (cam2.Count > 0 && XfadeAt(t, out int other, out float wMain) && wMain < 0.999f)
            {
                if (camB == null) { camB = new GameObject("CamB (dissolve)").AddComponent<Camera>(); camB.enabled = false; }
                if (rtB == null || rtB.width != Screen.width || rtB.height != Screen.height) { if (rtB != null) rtB.Release(); rtB = new RenderTexture(Screen.width, Screen.height, 24) { antiAliasing = 2 }; }
                camB.CopyFrom(cam); camB.enabled = false; camB.targetTexture = rtB;
                PosePath(other, t, headS, camB); camB.Render(); PosePath(PathOf(t), t, headS, cam);      // (the glow card stands behind her for each camera in turn)
                liveAlpha = 1f - wMain;
            }
            yield return null;
            if (!livePaused && (t >= liveEnd || !au.isPlaying))
            {
                if (liveLoop) { au.Stop(); au.time = Mathf.Clamp(startTime, 0f, au.clip.length - 0.1f); au.Play(); first = true; }
                else { au.Pause(); livePaused = true; }
            }
        }
    }

    [System.NonSerialized] float liveFps = 60f;
    void OnGUI()
    {
        if (!live || !Application.isPlaying || driver == null || driver.audioSource == null) return;
        if (liveAlpha > 0.001f && rtB != null && Event.current.type == EventType.Repaint) { var gc = GUI.color; GUI.color = new Color(1, 1, 1, liveAlpha); GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), rtB, ScaleMode.StretchToFill, false); GUI.color = gc; }
        var au = driver.audioSource; float t = driver.ClipTime; float sc = Mathf.Max(1f, Screen.height / 720f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(sc, sc, 1)); float w = Screen.width / sc, h = Screen.height / sc;
        GUILayout.BeginArea(new Rect(12, h - 46, w - 24, 40)); GUILayout.BeginHorizontal();
        if (GUILayout.Button(livePaused ? "▶ 继续" : "⏸ 暂停", GUILayout.Width(84), GUILayout.Height(30))) { livePaused = !livePaused; if (livePaused) au.Pause(); else { if (t >= liveEnd - 0.05f) au.time = startTime; au.UnPause(); if (!au.isPlaying) au.Play(); } }
        if (GUILayout.Button("⏮ 重播", GUILayout.Width(84), GUILayout.Height(30))) { au.Stop(); au.time = Mathf.Clamp(startTime, 0f, au.clip.length - 0.1f); au.Play(); livePaused = false; }
        liveFps = Mathf.Lerp(liveFps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
        GUILayout.Label($"{t:F1} s  {liveFps:F0} fps", GUILayout.Width(110), GUILayout.Height(30));
        float nt = GUILayout.HorizontalSlider(t, startTime, liveEnd, GUILayout.Height(30));
        if (Mathf.Abs(nt - t) > 0.25f) au.time = Mathf.Clamp(nt, 0f, au.clip.length - 0.1f);
        GUILayout.EndHorizontal(); GUILayout.EndArea();
    }
}
