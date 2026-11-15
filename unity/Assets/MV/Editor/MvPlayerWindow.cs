using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One click to watch the performance in real time inside Unity.
/// Menu: MV / 播放器 (Player) opens this window; MV / ▶ 实时播放 (Ctrl+Shift+P) plays the open scene at once.
/// The button opens the chosen scene if needed, sets the player (FrameDumper) to live playback and enters play mode: the song plays, the
/// character follows the audio clock, the cameras cut as in the film. In the Game view: pause / replay / seek bar. Press the button again
/// (or Unity's own stop) to stop.
/// </summary>
public class MvPlayerWindow : EditorWindow
{
    const string PrefScene = "MV.Player.Scene", PrefStart = "MV.Player.Start", PrefLen = "MV.Player.Len", PrefLoop = "MV.Player.Loop";
    string[] scenes = new string[0]; int sel; float start = -1f, length = -1f; bool loop = true; Vector2 scroll;

    [MenuItem("MV/播放器 (Player)")]
    public static void Open() { var w = GetWindow<MvPlayerWindow>("MV 播放器"); w.minSize = new Vector2(300, 190); w.Show(); }

    [MenuItem("MV/▶ 实时播放 %#p")]
    public static void PlayNow()
    {
        if (EditorApplication.isPlaying) { EditorApplication.isPlaying = false; return; }
        Play(null, -1f, -1f, EditorPrefs.GetBool(PrefLoop, true));
    }

    void OnEnable() { Refresh(); }
    void OnFocus() { Refresh(); }

    void Refresh()
    {
        scenes = Directory.Exists("Assets/MV/Scenes") ? Directory.GetFiles("Assets/MV/Scenes", "*.unity").Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToArray() : new string[0];
        string want = EditorPrefs.GetString(PrefScene, "Assets/MV/Scenes/LinMicStage.unity"); sel = Mathf.Max(0, System.Array.IndexOf(scenes, want));
        loop = EditorPrefs.GetBool(PrefLoop, true);
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        GUILayout.Space(6);
        if (scenes.Length == 0) { EditorGUILayout.HelpBox("Assets/MV/Scenes 里没有场景。", MessageType.Warning); EditorGUILayout.EndScrollView(); return; }
        EditorGUI.BeginChangeCheck();
        sel = EditorGUILayout.Popup("场景", Mathf.Clamp(sel, 0, scenes.Length - 1), scenes.Select(Path.GetFileNameWithoutExtension).ToArray());
        if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PrefScene, scenes[sel]);
        var fd = Object.FindFirstObjectByType<FrameDumper>();
        bool open = EditorSceneManager.GetActiveScene().path == scenes[sel];
        if (open && fd != null) EditorGUILayout.LabelField("片段", $"{fd.startTime:F0} – {fd.startTime + fd.duration:F0} 秒（场景里保存的）");
        EditorGUI.BeginChangeCheck();
        loop = EditorGUILayout.Toggle("循环播放", loop);
        if (EditorGUI.EndChangeCheck()) EditorPrefs.SetBool(PrefLoop, loop);
        GUILayout.Space(8);
        var big = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold, fixedHeight = 64 };
        var keep = GUI.backgroundColor; GUI.backgroundColor = EditorApplication.isPlaying ? new Color(1f, 0.55f, 0.5f) : new Color(0.55f, 1f, 0.6f);
        if (GUILayout.Button(EditorApplication.isPlaying ? "■  停止" : "▶  播放", big))
        {
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            else Play(scenes[sel], start, length, loop);
        }
        GUI.backgroundColor = keep;
        GUILayout.Space(6);
        EditorGUILayout.HelpBox(EditorApplication.isPlaying
            ? "正在播放。画面在 Game 窗口；窗口底部有 暂停 / 重播 / 进度条。"
            : "点“播放”：打开上面选的场景并在 Game 窗口里实时播放（有声音）。\n快捷键 Ctrl+Shift+P。", MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    void OnInspectorUpdate() { Repaint(); }

    /// <summary>scene: path to open first (null = the open one); start / length less than 0 = what the scene has saved.</summary>
    public static void Play(string scene, float start, float length, bool loop)
    {
        if (EditorApplication.isPlaying) return;
        if (!string.IsNullOrEmpty(scene) && EditorSceneManager.GetActiveScene().path != scene)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
        }
        var fd = Object.FindFirstObjectByType<FrameDumper>();
        if (fd == null) { EditorUtility.DisplayDialog("MV 播放器", "这个场景里没有播放组件（FrameDumper）。", "好"); return; }
        fd.live = true; fd.liveLoop = loop; fd.stage = true;
        if (start >= 0f) fd.startTime = start; if (length > 0f) fd.duration = length;
        if (fd.cam == null) fd.cam = Camera.main; if (fd.cam != null) fd.cam.targetTexture = null;
        EditorUtility.SetDirty(fd);
        // the Game view to the front, then play
        var gameView = typeof(Editor).Assembly.GetType("UnityEditor.GameView"); if (gameView != null) FocusWindowIfItsOpen(gameView);
        EditorApplication.EnterPlaymode();
    }
}
