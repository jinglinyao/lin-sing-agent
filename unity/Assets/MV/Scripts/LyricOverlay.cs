using System.IO;
using UnityEngine;

/// <summary>
/// The lyric captions in Unity's live player (Game view): the current line at the bottom centre, a translation under it, fading in and out.
/// Reads subs.json of the loaded song (made by the pipeline; the rendered video gets the same lines burnt in by ffmpeg).
/// </summary>
[ExecuteAlways]
public class LyricOverlay : MonoBehaviour
{
    [System.Serializable] public class Line { public float t0, t1; public string text, trans; }
    [System.Serializable] class Subs { public string font; public Line[] lines; }

    public SingDriver driver;
    public string subsPath = "";
    public bool show = true;
    Line[] lines; string loaded; Font font; GUIStyle main, sub, shade;

    void Load()
    {
        if (loaded == subsPath) return; loaded = subsPath; lines = null;
        if (!string.IsNullOrEmpty(subsPath) && File.Exists(subsPath)) lines = JsonUtility.FromJson<Subs>(File.ReadAllText(subsPath)).lines;
    }

    void OnGUI()
    {
        if (!show || !Application.isPlaying || driver == null || driver.audioSource == null) return;
        var fd = FindFirstObjectByType<FrameDumper>(); if (fd != null && !fd.live) return;           // offline frames get their captions from ffmpeg
        Load(); if (lines == null) return;
        float t = driver.audioSource.time; Line cur = null;
        foreach (var l in lines) if (t >= l.t0 && t < l.t1) { cur = l; break; }
        if (cur == null) return;
        float a = Mathf.Clamp01(Mathf.Min((t - cur.t0) / 0.18f, (cur.t1 - t) / 0.26f));
        float s = Screen.height / 1080f;
        if (font == null)
        {
            // Unity gives up when the FIRST name is not installed (found in the standalone player: no captions at all), so pick one that exists
            var have = new System.Collections.Generic.HashSet<string>(Font.GetOSInstalledFontNames());
            var pick = System.Array.Find(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "Noto Sans CJK SC", "Yu Gothic UI", "Meiryo UI", "SimHei", "PingFang SC", "Hiragino Sans", "Malgun Gothic", "Arial Unicode MS", "Arial" }, have.Contains);
            font = Font.CreateDynamicFontFromOSFont(pick ?? "Arial", 58);
        }
        if (main == null) { main = new GUIStyle { font = font, alignment = TextAnchor.LowerCenter, fontStyle = FontStyle.Bold, wordWrap = true }; sub = new GUIStyle(main) { fontStyle = FontStyle.Normal }; shade = new GUIStyle(main); }
        main.fontSize = Mathf.RoundToInt(58 * s); sub.fontSize = Mathf.RoundToInt(38 * s); shade.fontSize = main.fontSize;
        bool two = !string.IsNullOrEmpty(cur.trans);
        var r1 = new Rect(120 * s, 0, Screen.width - 240 * s, Screen.height - (two ? 128f : 84f) * s);      // above the live player's control bar
        var r2 = new Rect(120 * s, 0, Screen.width - 240 * s, Screen.height - 78f * s);
        Draw(r1, cur.text, main, new Color(0.95f, 0.96f, 0.97f, a), a);
        if (two) Draw(r2, cur.trans, sub, new Color(0.90f, 0.87f, 0.85f, a), a);
    }

    static void Draw(Rect r, string text, GUIStyle st, Color c, float a)
    {
        var keep = st.normal.textColor; st.normal.textColor = new Color(0.04f, 0.05f, 0.08f, 0.85f * a);
        for (int dx = -2; dx <= 2; dx += 2) for (int dy = -2; dy <= 2; dy += 2) if (dx != 0 || dy != 0) GUI.Label(new Rect(r.x + dx, r.y + dy, r.width, r.height), text, st);
        st.normal.textColor = c; GUI.Label(r, text, st); st.normal.textColor = keep;
    }
}
