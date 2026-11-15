using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the reusable props of the singing MV and saves them as assets:
///   Assets/MV/Props/MicStand.prefab   stand microphone (MicStandRig: height and tilt are set with Configure)
///   Assets/MV/Props/Stage.prefab      floor, glow card, dust (StageRig)
///   Assets/MV/Props/Meshes, Materials, Textures   what they are made of
/// Menu: MV / Build Props.   MV / Place Mic At Singer's Mouth puts the stand in front of whoever has the SingDriver in the open scene.
/// Everything is generated (lathe meshes, a few textures), so the props can be rebuilt or changed here and every scene that uses the
/// prefabs follows.
/// </summary>
public static class MvPropBuilder
{
    const string Root = "Assets/MV/Props", MeshDir = Root + "/Meshes", MatDir = Root + "/Materials", TexDir = Root + "/Textures";
    public const string MicPrefab = Root + "/MicStand.prefab", StagePrefab = Root + "/Stage.prefab";

    [MenuItem("MV/Build Props (mic stand + stage)")]
    public static void BuildAllMenu() { Debug.Log(BuildMicStand() + "\n" + BuildStage()); }

    // ------------------------------------------------------------------ helpers
    static void Dirs() { foreach (var d in new[] { Root, MeshDir, MatDir, TexDir }) if (!AssetDatabase.IsValidFolder(d)) { Directory.CreateDirectory(d); AssetDatabase.Refresh(); } }

    static Mesh SaveMesh(Mesh m, string name)
    {
        string path = MeshDir + "/" + name + ".asset"; m.name = name;
        var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (old != null) { old.Clear(); EditorUtility.CopySerialized(m, old); old.name = name; EditorUtility.SetDirty(old); return old; }     // keep the asset: prefabs that use it stay linked
        AssetDatabase.CreateAsset(m, path); return m;
    }

    static Material SaveMat(string name, string shader, System.Action<Material> set)
    {
        string path = MatDir + "/" + name + ".mat"; var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find(shader)); AssetDatabase.CreateAsset(m, path); } else m.shader = Shader.Find(shader);
        set(m); EditorUtility.SetDirty(m); return m;
    }

    static Material Lit(string name, Color c, float metallic, float smoothness, Texture2D map = null, Vector2? tiling = null)
        => SaveMat(name, "Universal Render Pipeline/Lit", m =>
        {
            m.SetColor("_BaseColor", c); m.color = c; m.SetFloat("_Metallic", metallic); m.SetFloat("_Smoothness", smoothness);
            if (map != null) { m.SetTexture("_BaseMap", map); m.SetTextureScale("_BaseMap", tiling ?? Vector2.one); }
        });

    static Texture2D SaveTex(Texture2D t, string name, bool alpha, bool repeat)
    {
        string path = TexDir + "/" + name + ".png"; File.WriteAllBytes(path, t.EncodeToPNG()); Object.DestroyImmediate(t);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var imp = (TextureImporter)AssetImporter.GetAtPath(path); imp.alphaIsTransparency = alpha; imp.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp; imp.mipmapEnabled = true; imp.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>Surface of revolution about Y. prof: (radius, y), given from the bottom to the top (the outside is then to the right of the walk).
    /// smooth: the normals follow the curve; otherwise every band between two points is its own facet (turned metal parts with edges).</summary>
    public static Mesh Lathe(IList<Vector2> prof, int segs, bool smooth, float uTile = 1f, float vTile = 1f)
    {
        var v = new List<Vector3>(); var nm = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>(); int bands = prof.Count - 1;
        Vector2 BandN(int b) { Vector2 d = (prof[b + 1] - prof[b]).normalized; return new Vector2(d.y, -d.x); }
        var acc = new float[prof.Count]; float len = 0; for (int i = 0; i < prof.Count; i++) { acc[i] = len; if (i < bands) len += Vector2.Distance(prof[i], prof[i + 1]); }
        void Ring(Vector2 p, Vector2 n2, float vv)
        {
            for (int s = 0; s <= segs; s++)
            {
                float th = s * Mathf.PI * 2f / segs, c = Mathf.Cos(th), sn = Mathf.Sin(th);
                v.Add(new Vector3(p.x * c, p.y, p.x * sn)); nm.Add(new Vector3(n2.x * c, n2.y, n2.x * sn).normalized); uv.Add(new Vector2(s / (float)segs * uTile, vv * vTile));
            }
        }
        void Quads(int ringA, int ringB)
        {
            for (int s = 0; s < segs; s++)
            {
                int a = ringA * (segs + 1) + s, b = a + 1, c = ringB * (segs + 1) + s, d = c + 1;
                tri.Add(a); tri.Add(c); tri.Add(b); tri.Add(b); tri.Add(c); tri.Add(d);
            }
        }
        if (smooth)
        {
            for (int i = 0; i < prof.Count; i++)
            {
                Vector2 n2 = i == 0 ? BandN(0) : i == bands ? BandN(bands - 1) : (BandN(i - 1) + BandN(i)).normalized;
                Ring(prof[i], n2, len > 0 ? acc[i] / len : 0);
            }
            for (int b = 0; b < bands; b++) Quads(b, b + 1);
        }
        else
            for (int b = 0; b < bands; b++)
            {
                if ((prof[b + 1] - prof[b]).sqrMagnitude < 1e-12f) continue;
                Vector2 n2 = BandN(b); int r0 = v.Count / (segs + 1);
                Ring(prof[b], n2, len > 0 ? acc[b] / len : 0); Ring(prof[b + 1], n2, len > 0 ? acc[b + 1] / len : 0); Quads(r0, r0 + 1);
            }
        var m = new Mesh(); m.SetVertices(v); m.SetNormals(nm); m.SetUVs(0, uv); m.SetTriangles(tri, 0); m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    static Vector2 P(float r, float y) => new Vector2(r, y);

    static GameObject Part(string name, Transform parent, Mesh mesh, Material mat, Vector3 localPos, Quaternion? localRot = null)
    {
        var g = new GameObject(name); g.transform.SetParent(parent, false); g.transform.localPosition = localPos; g.transform.localRotation = localRot ?? Quaternion.identity;
        g.AddComponent<MeshFilter>().sharedMesh = mesh; var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        return g;
    }

    // ------------------------------------------------------------------ microphone on a stand
    /// <summary>Builds the stand microphone and saves it as Assets/MV/Props/MicStand.prefab (configured for a grille at 1.47 m; MicStandRig.Configure
    /// re-sets height and tilt for any singer).</summary>
    public static string BuildMicStand()
    {
        Dirs();
        // ---- textures: the woven wire of the grille (bright wires, dark gaps, every other row shifted)
        var gt = new Texture2D(256, 256, TextureFormat.RGB24, true); var px = new Color[256 * 256];
        for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
        {
            float row = Mathf.Floor(y / 16f), fx = Mathf.Repeat(x / 16f + (((int)row & 1) == 1 ? 0.5f : 0f), 1f) - 0.5f, fy = Mathf.Repeat(y / 16f, 1f) - 0.5f;
            float c = Mathf.Sqrt(fx * fx + fy * fy), w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.24f, 0.40f, c));
            float shade = Mathf.Lerp(0.07f, 0.62f, w) * (0.92f + 0.08f * Mathf.Sin((x + y) * 0.7f));
            px[y * 256 + x] = new Color(shade, shade, shade * 1.03f);
        }
        gt.SetPixels(px); gt.Apply();
        var grilleTex = SaveTex(gt, "MicGrilleWire", false, true);

        // ---- materials
        var black = Lit("MicBlack", new Color(0.040f, 0.040f, 0.045f), 0.75f, 0.52f);
        var steel = Lit("MicSteel", new Color(0.62f, 0.63f, 0.66f), 0.95f, 0.64f);
        var grille = Lit("MicGrille", new Color(0.78f, 0.79f, 0.82f), 0.90f, 0.42f, grilleTex, new Vector2(9f, 5f));
        var rubber = Lit("MicRubber", new Color(0.020f, 0.020f, 0.022f), 0.0f, 0.28f);

        // ---- meshes (metres). Microphone: origin at the centre of the grille, the body runs along -Y.
        const float GR = 0.027f; const int SEG = 40;
        var sph = new List<Vector2>(); for (int i = 0; i <= 16; i++) { float ph = Mathf.Lerp(38f, 180f, i / 16f) * Mathf.Deg2Rad; sph.Add(P(GR * Mathf.Sin(ph), -GR * Mathf.Cos(ph))); }
        var mGrille = SaveMesh(Lathe(sph, SEG, true, 1f, 1f), "MicGrille");
        var mBand = SaveMesh(Lathe(new[] { P(0.0268f, -0.0035f), P(0.0281f, -0.0022f), P(0.0281f, 0.0022f), P(0.0268f, 0.0035f) }, SEG, false), "MicGrilleBand");
        var mCollar = SaveMesh(Lathe(new[] { P(0.0200f, -0.0320f), P(0.0240f, -0.0290f), P(0.0240f, -0.0215f), P(0.0200f, -0.0185f), P(0.0165f, -0.0185f) }, SEG, false), "MicCollar");
        var mBody = SaveMesh(Lathe(new[] { P(0f, -0.230f), P(0.0100f, -0.230f), P(0.0100f, -0.2160f), P(0.0148f, -0.2120f), P(0.0195f, -0.0420f), P(0.0205f, -0.0320f) }, SEG, false), "MicBody");
        var mSwitch = SaveMesh(Lathe(new[] { P(0f, 0f), P(0.0040f, 0f), P(0.0040f, 0.0022f), P(0f, 0.0022f) }, 16, false), "MicSwitch");
        // stand
        var mBase = SaveMesh(Lathe(new[] { P(0f, 0f), P(0.105f, 0f), P(0.105f, 0.008f), P(0.098f, 0.014f), P(0.034f, 0.020f), P(0.022f, 0.032f), P(0.0165f, 0.036f), P(0.0165f, 0.045f), P(0.0120f, 0.045f) }, 56, false), "StandBase");
        var mLower = SaveMesh(Lathe(new[] { P(0.0125f, 0f), P(0.0125f, 1f) }, 24, true), "StandTubeLowerUnit");
        var mUpper = SaveMesh(Lathe(new[] { P(0.0085f, 0f), P(0.0085f, 1f) }, 24, true), "StandTubeUpperUnit");
        var mClutch = SaveMesh(Lathe(new[] { P(0.0125f, 0f), P(0.0185f, 0.004f), P(0.0185f, 0.034f), P(0.0150f, 0.040f), P(0.0150f, 0.050f), P(0.0085f, 0.054f) }, 32, false), "StandClutch");
        var mKnob = SaveMesh(Lathe(new[] { P(0f, 0f), P(0.0055f, 0f), P(0.0055f, 0.014f), P(0.0085f, 0.016f), P(0.0085f, 0.024f), P(0f, 0.026f) }, 20, false), "StandClutchKnob");
        var ball = new List<Vector2>(); for (int i = 0; i <= 12; i++) { float ph = i / 12f * Mathf.PI; ball.Add(P(0.0130f * Mathf.Sin(ph), -0.0130f * Mathf.Cos(ph))); }
        var mBall = SaveMesh(Lathe(ball, 24, true), "StandSwivelBall");
        var mTubeCap = SaveMesh(Lathe(new[] { P(0.0085f, 0f), P(0.0120f, 0.002f), P(0.0120f, 0.010f), P(0.0060f, 0.014f), P(0.0060f, 0.022f) }, 24, false), "StandTubeCap");
        var mClip = SaveMesh(Lathe(new[] { P(0.0190f, -0.024f), P(0.0245f, -0.021f), P(0.0245f, 0.021f), P(0.0190f, 0.024f) }, 32, false), "StandMicClip");
        var mStem = SaveMesh(Lathe(new[] { P(0.0055f, 0f), P(0.0055f, 1f) }, 12, true), "StandStemUnit");

        // ---- assembly
        var root = new GameObject("MicStand"); var rig = root.AddComponent<MicStandRig>();
        Part("Base", root.transform, mBase, black, Vector3.zero);
        rig.lowerTube = Part("LowerTube", root.transform, mLower, black, new Vector3(0, 0.045f, 0)).transform;
        rig.clutch = Part("Clutch", root.transform, mClutch, black, new Vector3(0, 0.92f, 0)).transform;
        Part("ClutchKnob", rig.clutch, mKnob, steel, new Vector3(0.016f, 0.019f, 0), Quaternion.Euler(0, 0, -90f));
        rig.upperTube = Part("UpperTube", root.transform, mUpper, steel, new Vector3(0, 0.94f, 0)).transform;
        var head = new GameObject("Head").transform; head.SetParent(root.transform, false); rig.head = head;
        Part("TubeCap", head, mTubeCap, black, new Vector3(0, -0.046f, 0));
        Part("SwivelBall", head, mBall, steel, new Vector3(0, -0.020f, 0));
        var mic = new GameObject("Microphone").transform; mic.SetParent(head, false); rig.mic = mic;
        Part("Grille", mic, mGrille, grille, Vector3.zero);
        Part("GrilleBand", mic, mBand, steel, Vector3.zero);
        Part("Collar", mic, mCollar, steel, Vector3.zero);
        Part("Body", mic, mBody, black, Vector3.zero);
        Part("Switch", mic, mSwitch, steel, new Vector3(0, -0.062f, -0.0187f), Quaternion.Euler(-90f, 0, 0));
        Part("Clip", mic, mClip, rubber, new Vector3(0, -0.185f, 0));
        var cable = new GameObject("Cable"); cable.transform.SetParent(root.transform, false); rig.cable = cable.AddComponent<MeshFilter>(); cable.AddComponent<MeshRenderer>().sharedMaterial = rubber; rig.cableMaterial = rubber;
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

        rig.grilleRadius = GR; rig.bodyRadius = 0.0195f; rig.bodyLength = 0.230f; rig.clipAlong = 0.185f; rig.baseRadius = 0.105f; rig.baseTop = 0.045f;
        rig.Configure(new Vector3(0f, 1.470f, 0f), new Vector3(0f, -0.55f, 0.835f));
        var saved = PrefabUtility.SaveAsPrefabAsset(root, MicPrefab); Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        int tris = new[] { mGrille, mBand, mCollar, mBody, mSwitch, mBase, mLower, mUpper, mClutch, mKnob, mBall, mTubeCap, mClip }.Sum(m => m.triangles.Length / 3);
        return $"mic stand saved: {MicPrefab} ({saved.GetComponentsInChildren<MeshRenderer>().Length} parts, {tris} triangles, 4 materials, 1 texture)";
    }

    // ------------------------------------------------------------------ stage
    static Texture2D SoftDot(int size, float power)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false); var px = new Color32[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f, r = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
            px[y * size + x] = new Color(1, 1, 1, Mathf.Pow(r * r * (3f - 2f * r), power));
        }
        tex.SetPixels32(px); tex.Apply(); return tex;
    }

    /// <summary>Floor + glow card + dust, as the capture used to put up at run time, saved as Assets/MV/Props/Stage.prefab.</summary>
    public static string BuildStage()
    {
        Dirs();
        var glowTex = SaveTex(SoftDot(256, 1.4f), "StageGlowSoft", true, false); var dustTex = SaveTex(SoftDot(64, 1f), "StageDustSoft", true, false);
        var floorMat = Lit("StageFloor", new Color(0.012f, 0.012f, 0.016f), 0f, 0.12f);
        floorMat.SetFloat("_EnvironmentReflections", 0f); floorMat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF"); floorMat.SetFloat("_SpecularHighlights", 0f); floorMat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); EditorUtility.SetDirty(floorMat);
        var glowMat = SaveMat("StageGlow", "Sprites/Default", m => { m.mainTexture = glowTex; m.color = new Color(0.16f, 0.20f, 0.32f, 0.85f); });
        var dustMat = SaveMat("StageDust", "Sprites/Default", m => { m.mainTexture = dustTex; m.color = Color.white; });

        var root = new GameObject("Stage"); var rig = root.AddComponent<StageRig>();
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane); Object.DestroyImmediate(floor.GetComponent<Collider>()); floor.name = "Floor"; floor.transform.SetParent(root.transform, false); floor.transform.localScale = Vector3.one * 4f;
        floor.GetComponent<Renderer>().sharedMaterial = floorMat; rig.floor = floor.GetComponent<Renderer>();
        var card = GameObject.CreatePrimitive(PrimitiveType.Quad); Object.DestroyImmediate(card.GetComponent<Collider>()); card.name = "Glow"; card.transform.SetParent(root.transform, false);
        card.transform.localPosition = new Vector3(0, 1.25f, -2.6f); card.transform.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up); card.transform.localScale = new Vector3(13f, 8.5f, 1f);
        var cr = card.GetComponent<Renderer>(); cr.sharedMaterial = glowMat; cr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; cr.receiveShadows = false; rig.glow = cr;
        var go = new GameObject("Dust"); go.transform.SetParent(root.transform, false); go.transform.localPosition = new Vector3(0, 1.15f, -0.7f);
        var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.loop = true; main.prewarm = true; main.playOnAwake = true; main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 12f); main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.05f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.010f, 0.030f); main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.93f, 0.85f, 0.10f), new Color(1f, 0.96f, 0.92f, 0.38f));
        main.maxParticles = 220; main.simulationSpace = ParticleSystemSimulationSpace.World; main.gravityModifier = -0.004f;
        var em = ps.emission; em.rateOverTime = 16f;
        var shp = ps.shape; shp.shapeType = ParticleSystemShapeType.Box; shp.scale = new Vector3(3.6f, 2.4f, 2.6f);
        var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World; vel.x = new ParticleSystem.MinMaxCurve(-0.012f, 0.012f); vel.y = new ParticleSystem.MinMaxCurve(0.010f, 0.035f); vel.z = new ParticleSystem.MinMaxCurve(-0.010f, 0.010f);
        var noise = ps.noise; noise.enabled = true; noise.strength = 0.035f; noise.frequency = 0.35f; noise.scrollSpeed = 0.08f;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.25f), new GradientAlphaKey(1, 0.7f), new GradientAlphaKey(0, 1) });
        col.color = g;
        var pr = go.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = dustMat; pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; rig.dust = ps;
        PrefabUtility.SaveAsPrefabAsset(root, StagePrefab); Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        return $"stage saved: {StagePrefab} (floor, glow card 13 x 8.5 m, dust; 3 materials, 2 textures)";
    }

    // ------------------------------------------------------------------ placing the microphone for a singer
    [MenuItem("MV/Place Mic At Singer's Mouth")]
    public static void PlaceMicMenu() { Debug.Log(PlaceMic(0.045f, 0.028f, 0.010f)); }

    /// <summary>Measures where the singer's mouth is while she stands and sings (lips closed, head level, averaged over the first minute of her clip,
    /// microphone off), then puts the grille 'below' m under the centre of the mouth and 'lipGap' m in front of the lips - for the pose in which she
    /// sings INTO it: head a little lowered, which takes the mouth 'headDrop' m down (about 1 cm; with straight legs she cannot come up to a
    /// microphone that was set for a level head). Tells her driver where it is. Needs a SingDriver and a MicStandRig in the open scene.</summary>
    public static string PlaceMic(float below, float lipGap, float headDrop)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var rig = Object.FindFirstObjectByType<MicStandRig>(FindObjectsInactive.Include);
        if (d == null || rig == null) return "ERR: needs a SingDriver and a MicStandRig in the scene";
        var an = d.animator; var root = d.transform; var face = d.face; var mesh = face.sharedMesh;
        int idx = mesh.GetBlendShapeIndex("Fcl_MTH_A"); if (idx < 0) return "ERR: the face has no Fcl_MTH_A";
        var del = new Vector3[mesh.vertexCount]; mesh.GetBlendShapeFrameVertices(idx, 0, del, null, null);
        float mx = del.Max(v => v.magnitude); var lips = Enumerable.Range(0, del.Length).Where(i => del[i].magnitude > 0.35f * mx).ToArray();
        var hd = an.GetBoneTransform(HumanBodyBones.Head); var baked = new Mesh();
        bool micKeep = d.micOn; float mouthKeep = d.mouth; d.micOn = false; d.mouth = 0f; d.InvalidateBake();
        Vector3 sum = Vector3.zero, sumLocal = Vector3.zero; float front = 0; int n = 0;
        for (float t = 2f; t <= 60f; t += 2f)
        {
            d.Apply(t); face.BakeMesh(baked, true); var vs = baked.vertices; var l2w = face.transform.localToWorldMatrix; Vector3 c = Vector3.zero; float fz = -9;
            foreach (int i in lips) { Vector3 w = l2w.MultiplyPoint3x4(vs[i]); c += w; fz = Mathf.Max(fz, root.InverseTransformPoint(w).z); }
            c /= lips.Length; sum += root.InverseTransformPoint(c); sumLocal += Quaternion.Inverse(hd.rotation) * (c - hd.position); front += fz; n++;
        }
        Object.DestroyImmediate(baked); d.micOn = micKeep; d.mouth = mouthKeep;
        Vector3 mouth = sum / n, mouthLocal = sumLocal / n; front /= n;
        Vector3 grille = new Vector3(mouth.x, mouth.y - headDrop - below, front + lipGap + rig.grilleRadius), axis = new Vector3(0f, -0.55f, 0.835f).normalized;
        Undo.RecordObjects(new Object[] { d, rig, rig.transform }, "Place mic");
        rig.transform.position = new Vector3(rig.transform.position.x, d.stageOrigin.y, rig.transform.position.z);
        rig.Configure(d.stageOrigin + root.rotation * grille, root.rotation * axis);
        d.micPos = grille; d.micDir = axis; d.micClip = rig.clipAlong; d.micLen = rig.bodyLength; d.micGrip = 0.120f; d.micGripOver = false; d.micGripRoll = 60f; d.micElbow = new Vector3(1.0f, -0.7f, 0.05f);      // the hand on the body, between collar (3 cm) and clip (16 cm), from her side
        d.micMouth = new Vector3(0f, below, mouth.z - grille.z); d.mouthLocal = mouthLocal; d.micHold = 1f; d.micBand = 0.018f; d.micSlack = 0.006f; d.InvalidateBake();
        EditorUtility.SetDirty(d); EditorUtility.SetDirty(rig); EditorSceneManager.MarkSceneDirty(d.gameObject.scene);
        Vector3 pole = grille + axis * rig.clipAlong;
        return $"mouth centre {mouth.x:F3} {mouth.y:F3} {mouth.z:F3}, lips reach z {front:F3}; grille centre {grille.x:F3} {grille.y:F3} {grille.z:F3} (centre {below * 100:F1} cm below the centre of the mouth = top {(below - rig.grilleRadius) * 100:F1} cm below it, when she sings into it with her head {headDrop * 100:F1} cm down, {lipGap * 100:F1} cm of air in front of the lips); "
             + $"axis {axis.x:F2} {axis.y:F2} {axis.z:F2}; pole at z {pole.z:F3} (top {pole.y:F3}), base reaches back to z {pole.z - rig.baseRadius:F3}";
    }

    // ------------------------------------------------------------------ the reusable scene
    /// <summary>From the open character scene (a SingDriver in it) makes Assets/MV/Scenes/{name}.unity: the same character with the Stage and the
    /// MicStand prefabs, the microphone placed at her mouth, the player set to live playback. The source scene is left as it is.</summary>
    public static string BuildMicScene(string name, string showFile, float start, float duration)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); if (d == null) return "ERR: no SingDriver in the open scene";
        var src = EditorSceneManager.GetActiveScene(); EditorSceneManager.SaveScene(src);
        string path = "Assets/MV/Scenes/" + name + ".unity"; if (!EditorSceneManager.SaveScene(src, path)) return "ERR: cannot save " + path;
        foreach (var old in Object.FindObjectsByType<StageRig>(FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        foreach (var old in Object.FindObjectsByType<MicStandRig>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        var stage = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(StagePrefab)); stage.transform.SetPositionAndRotation(d.stageOrigin, d.transform.rotation);
        stage.GetComponent<StageRig>().key = Object.FindFirstObjectByType<Light>();
        var micGo = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(MicPrefab)); micGo.transform.position = d.stageOrigin;
        string placed = PlaceMic(0.045f, 0.028f, 0.010f);
        var fd = Object.FindFirstObjectByType<FrameDumper>(); if (fd == null) fd = new GameObject("FrameDumper").AddComponent<FrameDumper>();
        fd.driver = d; fd.cam = Camera.main; fd.stage = true; fd.live = true; fd.liveLoop = true; fd.showFile = showFile; fd.startTime = start; fd.duration = duration;
        var cam = Camera.main; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.045f, 0.055f, 0.09f); cam.fieldOfView = 26f;
        EditorUtility.SetDirty(fd); EditorSceneManager.MarkSceneDirty(d.gameObject.scene); EditorSceneManager.SaveScene(d.gameObject.scene);
        return $"scene saved: {path}; {placed}";
    }
}
