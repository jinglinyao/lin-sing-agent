using UnityEngine;

/// <summary>
/// Stand microphone prop - reusable. Built by the menu "MV / Build Props" (Assets/MV/Editor/MvPropBuilder.cs) and saved as
/// Assets/MV/Props/MicStand.prefab. The root sits on the floor under the pole.
///
/// Configure(grille, axis) says where the mic head (centre of the grille) is and which way the microphone points along its body
/// (from the grille towards the tail, i.e. away from the singer and down); the head of the stand, the telescopic tube and the
/// cable follow. For a singer: grille = mouth + (0, -0.01, 0.08) in her space, see MvSetup.PlaceMic.
/// </summary>
[ExecuteAlways]
public class MicStandRig : MonoBehaviour
{
    [Header("Parts (set by the builder)")]
    public Transform head;          // on the pole axis, at the height of the clip: swivel, clip and microphone hang on it
    public Transform mic;           // the microphone; origin = centre of the grille, local -Y runs along the body to the tail
    public Transform lowerTube, clutch, upperTube;   // tubes are 1 m tall meshes scaled along Y
    public MeshFilter cable;
    public Material cableMaterial;
    [Header("Dimensions (m)")]
    public float grilleRadius = 0.027f;
    public float bodyRadius = 0.0195f;
    public float bodyLength = 0.230f;       // grille centre to the end of the connector
    public float clipAlong = 0.185f;        // grille centre to where the clip holds the body (a hand has the 13 cm of body above it)
    public float baseRadius = 0.105f;
    public float baseTop = 0.045f;          // where the lower tube leaves the base
    public float clutchHeight = 0.92f;      // preferred height of the clutch (it drops for a short singer)
    public float tubeGap = 0.046f;          // top of the upper tube below the clip point (swivel in between)

    public Vector3 Grille => mic.position;
    public Vector3 Axis => -mic.up;                                    // from the grille along the body
    public Vector3 ClipPoint => mic.position - mic.up * clipAlong;     // on the pole axis
    public Vector3 Tail => mic.position - mic.up * bodyLength;

    /// <summary>Place the stand so that the grille centre is at 'grilleWorld' and the body runs along 'axisWorld'. The floor is the root's own height.</summary>
    public void Configure(Vector3 grilleWorld, Vector3 axisWorld)
    {
        axisWorld.Normalize();
        Vector3 clip = grilleWorld + axisWorld * clipAlong; float floor = transform.position.y;
        transform.position = new Vector3(clip.x, floor, clip.z); transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(-axisWorld, Vector3.up).sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(-axisWorld, Vector3.up).normalized : Vector3.forward, Vector3.up);
        float hClip = clip.y - floor, hTubeTop = hClip - tubeGap, hClutch = Mathf.Min(clutchHeight, hTubeTop - 0.22f);
        head.localPosition = new Vector3(0, hClip, 0);
        mic.position = grilleWorld; mic.rotation = Quaternion.LookRotation(Vector3.Cross(transform.right, -axisWorld), -axisWorld);
        lowerTube.localPosition = new Vector3(0, baseTop, 0); lowerTube.localScale = new Vector3(1, Mathf.Max(0.05f, hClutch - baseTop), 1);
        clutch.localPosition = new Vector3(0, hClutch, 0);
        upperTube.localPosition = new Vector3(0, hClutch + 0.02f, 0); upperTube.localScale = new Vector3(1, Mathf.Max(0.05f, hTubeTop - hClutch - 0.02f), 1);
        RebuildCable();
    }

    void OnEnable() { if (mic != null && cable != null) RebuildCable(); }

    /// <summary>The cable: out of the connector, a loop down along the pole, onto the floor and away behind the stand. A tube along a spline, rebuilt
    /// whenever the stand is configured (it is not an asset: it depends on the height).</summary>
    public void RebuildCable()
    {
        if (cable == null || mic == null) return;
        Transform ct = cable.transform; Vector3 L(Vector3 w) => ct.InverseTransformPoint(w);
        Vector3 tail = Tail, ax = Axis, fwd = transform.forward, up = Vector3.up, basePos = transform.position;
        // control points (world): tail -> out along the axis -> sag -> clipped to the pole low down -> floor behind the base -> off stage
        var pts = new[]
        {
            tail, tail + ax * 0.05f, tail + ax * 0.10f - up * 0.10f - fwd * 0.03f, basePos + up * (0.55f * (tail.y - basePos.y)) - fwd * 0.07f,
            basePos + up * 0.30f - fwd * 0.035f, basePos + up * 0.10f - fwd * 0.05f, basePos + up * 0.012f - fwd * (baseRadius + 0.06f),
            basePos + up * 0.006f - fwd * (baseRadius + 0.45f) + transform.right * 0.12f, basePos + up * 0.006f - fwd * (baseRadius + 1.2f) - transform.right * 0.10f, basePos + up * 0.006f - fwd * (baseRadius + 2.6f) + transform.right * 0.25f,
        };
        const int SUB = 10, RING = 8; const float R = 0.0032f; int n = (pts.Length - 1) * SUB + 1;
        var v = new Vector3[n * RING]; var nrm = new Vector3[n * RING]; var tri = new int[(n - 1) * RING * 6];
        Vector3 Cat(int i, float u)
        {
            Vector3 p0 = pts[Mathf.Max(i - 1, 0)], p1 = pts[i], p2 = pts[Mathf.Min(i + 1, pts.Length - 1)], p3 = pts[Mathf.Min(i + 2, pts.Length - 1)];
            return 0.5f * ((2f * p1) + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u * u + (-p0 + 3f * p1 - 3f * p2 + p3) * u * u * u);
        }
        Vector3 prevN = Vector3.right;
        for (int k = 0; k < n; k++)
        {
            int seg = Mathf.Min(k / SUB, pts.Length - 2); float u = (k - seg * SUB) / (float)SUB;
            Vector3 c = Cat(seg, u), t = (Cat(seg, Mathf.Min(1f, u + 0.05f)) - Cat(seg, Mathf.Max(0f, u - 0.05f))).normalized;
            Vector3 a = Vector3.ProjectOnPlane(prevN, t); if (a.sqrMagnitude < 1e-6f) a = Vector3.Cross(t, Vector3.up); a.Normalize(); prevN = a; Vector3 b = Vector3.Cross(t, a);
            for (int r = 0; r < RING; r++) { float th = r * Mathf.PI * 2f / RING; Vector3 d = a * Mathf.Cos(th) + b * Mathf.Sin(th); v[k * RING + r] = L(c + d * R); nrm[k * RING + r] = ct.InverseTransformDirection(d); }
        }
        int ti = 0;
        for (int k = 0; k < n - 1; k++) for (int r = 0; r < RING; r++)
        {
            int a = k * RING + r, b = k * RING + (r + 1) % RING, c2 = a + RING, d2 = b + RING;
            tri[ti++] = a; tri[ti++] = c2; tri[ti++] = b; tri[ti++] = b; tri[ti++] = c2; tri[ti++] = d2;
        }
        var m = cable.sharedMesh; if (m == null || m.name != "MicCable (generated)") { m = new Mesh { name = "MicCable (generated)", hideFlags = HideFlags.DontSave }; cable.sharedMesh = m; }
        m.Clear(); m.vertices = v; m.normals = nrm; m.triangles = tri; m.RecalculateBounds();
        var mr = cable.GetComponent<MeshRenderer>(); if (mr != null && cableMaterial != null) mr.sharedMaterial = cableMaterial;
    }
}
