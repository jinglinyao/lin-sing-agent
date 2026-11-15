using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Stage lighting and the camera's look, driven by the show file like the camera (lin-sing):
///   lit t key rim pool fill      light levels at time t (eased between keys): the key light, a rim light from behind (edges of hair and
///                                shoulders), a pool from above (a circle of light on the floor around her), a soft fill from the camera side
///   tint t r g b                 colour of the rim light (the backdrop colour comes from the col keys)
///   beat t t t ...               (shared with the camera) - in loud sections the rim and the glow breathe on the beat
/// Post: bloom, depth of field focused on her face (follows the camera distance), vignette, a touch of contrast, fine grain.
/// FrameDumper calls Step() after it has placed the camera; nothing here moves her or the camera except a very small handheld drift.
/// </summary>
public static class StageLook
{
    static readonly List<float[]> lit = new List<float[]>(), tint = new List<float[]>(); static readonly List<float> beats = new List<float>();
    static string loaded; static Light rim, pool, fill; static Volume vol; static DepthOfField dof; static Bloom bloom; static Vignette vig;
    public static float drift = 1f;          // handheld drift (0 = locked off)

    static void Load(string showFile)
    {
        if (loaded == showFile) return; loaded = showFile; lit.Clear(); tint.Clear(); beats.Clear();
        if (string.IsNullOrEmpty(showFile) || !File.Exists(showFile)) return;
        var ci = CultureInfo.InvariantCulture;
        foreach (var line in File.ReadAllLines(showFile))
        {
            var q = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries); if (q.Length < 2 || q[0].StartsWith("#")) continue;
            if (q[0] == "beat") { for (int i = 1; i < q.Length; i++) beats.Add(float.Parse(q[i], ci)); continue; }
            if ((q[0] == "lit" && q.Length >= 6) || (q[0] == "tint" && q.Length >= 5))
            {
                var v = new float[q.Length - 1]; for (int i = 1; i < q.Length; i++) v[i - 1] = float.Parse(q[i], ci);
                (q[0] == "lit" ? lit : tint).Add(v);
            }
        }
        lit.Sort((a, b) => a[0].CompareTo(b[0])); tint.Sort((a, b) => a[0].CompareTo(b[0])); beats.Sort();
    }

    static float[] At(List<float[]> keys, float t)
    {
        int m = keys[0].Length - 1; var o = new float[m];
        if (t <= keys[0][0]) { System.Array.Copy(keys[0], 1, o, 0, m); return o; }
        for (int k = 0; k + 1 < keys.Count; k++)
            if (t < keys[k + 1][0])
            {
                float u = Mathf.Clamp01((t - keys[k][0]) / Mathf.Max(0.01f, Mathf.Min(1.6f, keys[k + 1][0] - keys[k][0]))); u = u * u * (3 - 2 * u);
                for (int c = 0; c < m; c++) o[c] = Mathf.Lerp(keys[k][c + 1], keys[k + 1][c + 1], u);
                return o;
            }
        System.Array.Copy(keys[keys.Count - 1], 1, o, 0, m); return o;
    }

    static float BeatEnv(float t)
    {
        float e = 0; foreach (var b in beats) { float d = t - b; if (d < 0) break; if (d < 1f) e = Mathf.Max(e, Mathf.Min(1f, d / 0.05f) * Mathf.Exp(-Mathf.Max(0f, d - 0.05f) / 0.28f)); }
        return e;
    }

    static Light MakeSpot(string name, Transform parent, Color c, float angle, float range, LightShadows sh)
    {
        var go = GameObject.Find(name); if (go == null) go = new GameObject(name); go.transform.SetParent(parent, false); go.hideFlags = HideFlags.DontSave;
        var l = go.GetComponent<Light>(); if (l == null) l = go.AddComponent<Light>(); l.type = LightType.Spot; l.color = c; l.spotAngle = angle; l.innerSpotAngle = angle * 0.45f; l.range = range; l.shadows = sh;
        return l;
    }

    /// <summary>Post-processing on a camera (the main one and the dissolve camera alike).</summary>
    public static void Prep(Camera c)
    {
        if (c == null) return;
        var d = c.GetUniversalAdditionalCameraData(); if (d != null) { d.renderPostProcessing = true; d.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing; }
    }

    static void Build(SingDriver driver)
    {
        var ch = driver.transform;
        // the key light from her front-left, 35 degrees up: one side of the face in shade (the toon shading needs a direction to draw)
        var key0 = Object.FindFirstObjectByType<Light>();
        if (key0 != null && key0.type == LightType.Directional) { key0.transform.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(-26f, Vector3.up) * -ch.forward, Vector3.up) * Quaternion.Euler(35f, 0, 0); key0.shadows = LightShadows.Soft; key0.color = new Color(1f, 0.96f, 0.92f); }
        rim = MakeSpot("LinSing Rim", ch, new Color(0.75f, 0.85f, 1f), 38f, 7f, LightShadows.None);
        rim.transform.localPosition = new Vector3(0.55f, 2.4f, -1.5f); rim.transform.LookAt(ch.position + Vector3.up * 1.35f);
        pool = MakeSpot("LinSing Pool", ch, new Color(1f, 0.92f, 0.82f), 34f, 8f, LightShadows.Soft);
        pool.transform.localPosition = new Vector3(0f, 4.2f, 1.0f); pool.transform.LookAt(ch.position + Vector3.up * 0.2f + ch.forward * 0.15f);
        fill = MakeSpot("LinSing Fill", ch, new Color(0.80f, 0.86f, 1f), 60f, 9f, LightShadows.None);
        var vgo = GameObject.Find("LinSing Post"); if (vgo == null) vgo = new GameObject("LinSing Post"); vgo.hideFlags = HideFlags.DontSave;
        vol = vgo.GetComponent<Volume>(); if (vol == null) vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
        var prof = ScriptableObject.CreateInstance<VolumeProfile>(); vol.sharedProfile = prof;
        bloom = prof.Add<Bloom>(true); bloom.threshold.Override(0.97f); bloom.intensity.Override(0.22f); bloom.scatter.Override(0.68f); bloom.tint.Override(new Color(1f, 0.93f, 0.88f));
        dof = prof.Add<DepthOfField>(true); dof.mode.Override(DepthOfFieldMode.Gaussian); dof.highQualitySampling.Override(true); dof.gaussianMaxRadius.Override(1.1f);
        vig = prof.Add<Vignette>(true); vig.intensity.Override(0.26f); vig.smoothness.Override(0.45f);
        var ca = prof.Add<ColorAdjustments>(true); ca.contrast.Override(14f); ca.saturation.Override(6f); ca.postExposure.Override(-0.18f);
        var fg = prof.Add<FilmGrain>(true); fg.type.Override(FilmGrainLookup.Thin1); fg.intensity.Override(0.12f);
    }

    public static void Step(FrameDumper fd, SingDriver driver, Camera cam, float t, Vector3 head, float fadeMul)
    {
        Load(fd.showFile);
        if (rim == null || vol == null) Build(driver);
        Prep(cam);
        float[] L = lit.Count > 0 ? At(lit, t) : new[] { -1f, 1.2f, 1.6f, 0.25f };      // key < 0: leave the key light to the col keys
        float pulse = BeatEnv(t);
        if (tint.Count > 0) { var c = At(tint, t); rim.color = new Color(c[0], c[1], c[2]); }
        rim.intensity = L[1] * (1f + 0.25f * pulse) * fadeMul; pool.intensity = L[2] * fadeMul; fill.intensity = L[3] * fadeMul;
        if (L[0] >= 0f) { var key = Object.FindFirstObjectByType<Light>(); if (key != null && key.type == LightType.Directional) key.intensity = L[0] * fadeMul; }
        // the fill comes from the camera's side but always from 3 m away: a close shot must not get a brighter face than a wide one
        Vector3 toCam = cam.transform.position - head; toCam.y = 0; if (toCam.sqrMagnitude < 1e-4f) toCam = driver.transform.forward;
        fill.transform.position = head + toCam.normalized * 3f + Vector3.up * 0.6f; fill.transform.LookAt(head);
        // the lens: focus on her face, the background softens more in close shots
        float dist = Vector3.Distance(cam.transform.position, head);
        dof.gaussianStart.Override(dist + 0.35f + 0.25f * dist); dof.gaussianEnd.Override(dist + 2.2f + 1.6f * dist);
        bloom.intensity.Override(0.20f + 0.10f * pulse);
        // a hand-held camera, barely: slow, small turns (never a shake)
        if (drift > 0f)
        {
            float a = drift * Mathf.Lerp(0.18f, 0.32f, Mathf.InverseLerp(4.5f, 1.3f, dist));
            cam.transform.rotation *= Quaternion.Euler(a * (Mathf.PerlinNoise(t * 0.21f, 1.7f) - 0.5f) * 2f, a * (Mathf.PerlinNoise(3.1f, t * 0.17f) - 0.5f) * 2f, 0.35f * a * (Mathf.PerlinNoise(t * 0.13f, 5.3f) - 0.5f));
        }
    }
}
