// lin-sing: load a song folder made by the Python pipeline into the stage scene, play it live, or render it to frames.
//
// A song folder (made by `python pipeline/linsing.py make <audio>`) holds:
//   song.wav   the audio            face.json  mouth / eyes / blinks per frame (lip-sync track)
//   key.bin    the body, baked      look.txt   where the eyes go, when a hand holds the microphone
//   show.txt   the camera path      song.json  summary (duration, tempo, ...)
//
// Menu:   LinSing / Load Song Folder...     LinSing / Play     LinSing / Render Frames
// Batch:  Unity.exe -projectPath unity -executeMethod LinSingTools.BatchLoad   -song <folder>            (add -batchmode -quit)
//         Unity.exe -projectPath unity -executeMethod LinSingTools.BatchRender -song <folder> [-fps 30] [-height 1080]
//         (render opens the editor window - the camera needs a GPU - and closes it when the frames are written)
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class LinSingTools
{
    const string Scene = "Assets/MV/Scenes/LinMicStage.unity";
    const string SongAssets = "Assets/MV/Songs";
    const string KeyRender = "linsing.render", KeyFolder = "linsing.folder";

    static LinSingTools()
    {
        // after a batch render the frames are written, play mode ends, and the editor quits
        EditorApplication.playModeStateChanged += s =>
        {
            if (s != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(KeyRender, false)) return;
            SessionState.SetBool(KeyRender, false);
            var folder = SessionState.GetString(KeyFolder, "");
            try { SetLive(); } catch (Exception e) { Debug.LogError(e); }
            int n = Directory.Exists(Path.Combine(folder, "frames")) ? Directory.GetFiles(Path.Combine(folder, "frames"), "*.jpg").Length : 0;
            File.WriteAllText(Path.Combine(folder, "render_done.txt"), n + " frames\n");
            Debug.Log("[LinSing] render finished: " + n + " frames in " + folder);
            if (Environment.GetCommandLineArgs() is var a && Array.IndexOf(a, "-executeMethod") >= 0) EditorApplication.Exit(0);
        };
    }

    static string Arg(string name, string def = null)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return def;
    }

    // ------------------------------------------------------------------ menu
    [MenuItem("LinSing/Load Song Folder...", false, 1)]
    static void MenuLoad()
    {
        var dir = EditorUtility.OpenFolderPanel("Song folder made by linsing.py", Path.GetFullPath(Path.Combine(Application.dataPath, "../../songs")), "");
        if (string.IsNullOrEmpty(dir)) return;
        Debug.Log("[LinSing] " + LoadSong(dir));
    }

    [MenuItem("LinSing/Play (live, with sound)", false, 2)]
    static void MenuPlay() { SetLive(); EditorApplication.EnterPlaymode(); }

    [MenuItem("LinSing/Render Frames", false, 3)]
    static void MenuRender()
    {
        var folder = SessionState.GetString(KeyFolder, "");
        if (string.IsNullOrEmpty(folder)) { EditorUtility.DisplayDialog("LinSing", "Load a song folder first (LinSing / Load Song Folder...).", "OK"); return; }
        StartRender(folder, 30, 1080, 0f, -1f);
    }

    // ------------------------------------------------------------------ batch entry points
    public static void BatchLoad()
    {
        var dir = Arg("-song"); if (dir == null) { Debug.LogError("[LinSing] -song <folder> missing"); EditorApplication.Exit(2); return; }
        Debug.Log("[LinSing] " + LoadSong(Path.GetFullPath(dir)));
    }

    public static void BatchRender()
    {
        var dir = Arg("-song"); if (dir == null) { Debug.LogError("[LinSing] -song <folder> missing"); EditorApplication.Exit(2); return; }
        dir = Path.GetFullPath(dir);
        Debug.Log("[LinSing] " + LoadSong(dir));
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        StartRender(dir, int.Parse(Arg("-fps", "30")), int.Parse(Arg("-height", "1080")), float.Parse(Arg("-from", "0"), ci), float.Parse(Arg("-to", "-1"), ci));
    }

    /// <summary>Measures lin in the stage scene (joints, microphone, both grips, body outline) -> pipeline/lin_sing/data/lin_rig.json.
    /// Run again after changing the character or moving the microphone.</summary>
    /// <summary>First run: UniVRM imports the model before its own shaders exist on a fresh checkout ("Value cannot be null. Parameter name: Shader");
    /// importing it again once the packages are in place fixes it. Then the rig is measured.</summary>
    [MenuItem("LinSing/First-time Setup (re-import lin, measure rig)", false, 19)]
    public static void BatchSetup()
    {
        foreach (var p in AssetDatabase.FindAssets("t:Object", new[] { "Assets/MV/Characters" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(p);
            if (path.EndsWith(".vrm")) { AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport); Debug.Log("[LinSing] re-imported " + path); }
        }
        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        var an = UnityEngine.Object.FindFirstObjectByType<SingDriver>()?.animator;
        if (an == null || an.avatar == null) { Debug.LogError("[LinSing] lin has no avatar after the import - open the project once in the editor and run this menu again"); if (Application.isBatchMode) EditorApplication.Exit(3); return; }
        BatchRig();
    }

    [MenuItem("LinSing/Measure Rig (after changing character or microphone)", false, 20)]
    public static void BatchRig()
    {
        if (EditorSceneManager.GetActiveScene().path != Scene) EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        Directory.CreateDirectory("Capture"); Debug.Log("[LinSing] " + MvSetup.RigSheet());
        var dst = Path.GetFullPath(Path.Combine(Application.dataPath, "../../pipeline/lin_sing/data/lin_rig.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Copy("Capture/rig_sheet.json", dst, true);
        Debug.Log("[LinSing] rig -> " + dst);
    }

    // ------------------------------------------------------------------ the work
    /// <summary>Copies the audio and the face track into the project, points the body at key.bin, reads look.txt, sets the camera file, saves the scene.</summary>
    public static string LoadSong(string folder)
    {
        folder = Path.GetFullPath(folder);
        foreach (var f in new[] { "song.wav", "face.json", "key.bin", "look.txt", "show.txt" })
            if (!File.Exists(Path.Combine(folder, f))) return "ERROR: " + f + " missing in " + folder + " (run linsing.py make first)";
        if (EditorSceneManager.GetActiveScene().path != Scene) EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);

        Directory.CreateDirectory(SongAssets);
        string wav = SongAssets + "/current_song.wav", face = SongAssets + "/current_face.json";
        File.Copy(Path.Combine(folder, "song.wav"), wav, true); File.Copy(Path.Combine(folder, "face.json"), face, true);
        AssetDatabase.ImportAsset(wav, ImportAssetOptions.ForceUpdate); AssetDatabase.ImportAsset(face, ImportAssetOptions.ForceUpdate);

        var d = UnityEngine.Object.FindFirstObjectByType<SingDriver>(); var fd = UnityEngine.Object.FindFirstObjectByType<FrameDumper>();
        if (d == null || fd == null) return "ERROR: the scene has no SingDriver / FrameDumper";
        d.lipJson = AssetDatabase.LoadAssetAtPath<TextAsset>(face);
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(wav);
        if (clip == null) return "ERROR: Unity could not import " + wav;
        d.audioSource.clip = clip; EditorUtility.SetDirty(d.audioSource);
        string missing = d.MissingShapesFresh();
        MvSetup.KeyClip(Path.Combine(folder, "key.bin").Replace('\\', '/'), 1);
        string look = MvSetup.SetLookFromFile(Path.Combine(folder, "look.txt"));
        fd.showFile = Path.Combine(folder, "show.txt").Replace('\\', '/');
        var ov = UnityEngine.Object.FindFirstObjectByType<LyricOverlay>();                     // the lyric captions in the live player
        if (ov == null) ov = new GameObject("LyricOverlay").AddComponent<LyricOverlay>();
        ov.driver = d; ov.subsPath = File.Exists(Path.Combine(folder, "subs.json")) ? Path.Combine(folder, "subs.json").Replace('\\', '/') : ""; EditorUtility.SetDirty(ov);
        SessionState.SetString(KeyFolder, folder);
        SetLive();
        return $"loaded {folder}: {clip.length:F1} s; {look}; shapes the model lacks: [{missing}]";
    }

    /// <summary>What Unity's own Play button runs: the song from the start with sound, the camera path, looping.</summary>
    static void SetLive()
    {
        var d = UnityEngine.Object.FindFirstObjectByType<SingDriver>(); var fd = UnityEngine.Object.FindFirstObjectByType<FrameDumper>();
        if (d == null || fd == null || d.audioSource == null || d.audioSource.clip == null) return;
        fd.driver = d; if (fd.cam == null) fd.cam = Camera.main;
        fd.live = true; fd.liveLoop = true; fd.stage = true; fd.startTime = 0f; fd.duration = d.audioSource.clip.length; fd.width = 1920; fd.height = 1080; fd.fps = 30;
        d.mouth = 1f; d.usePreviewTime = false; if (fd.cam != null) fd.cam.targetTexture = null;
        EditorUtility.SetDirty(fd); EditorUtility.SetDirty(d);
        EditorSceneManager.MarkSceneDirty(fd.gameObject.scene); EditorSceneManager.SaveScene(fd.gameObject.scene);
    }

    /// <summary>Offline render: play mode at a fixed frame rate, one JPG per frame into &lt;folder&gt;/frames; the pipeline muxes the sound afterwards.</summary>
    static void StartRender(string folder, int fps, int height, float from, float to)
    {
        var d = UnityEngine.Object.FindFirstObjectByType<SingDriver>(); var fd = UnityEngine.Object.FindFirstObjectByType<FrameDumper>();
        var frames = Path.Combine(folder, "frames"); if (Directory.Exists(frames)) Directory.Delete(frames, true);
        fd.live = false; fd.stage = true; fd.outDir = frames.Replace('\\', '/'); fd.fps = fps; fd.height = height; fd.width = height * 16 / 9;
        float len = d.audioSource.clip.length; from = Mathf.Clamp(from, 0f, len - 0.5f); if (to <= from) to = len;
        fd.startTime = from; fd.duration = Mathf.Min(to, len) - from + 0.05f; d.mouth = 1f;
        EditorUtility.SetDirty(fd); EditorUtility.SetDirty(d);
        EditorSceneManager.MarkSceneDirty(fd.gameObject.scene); EditorSceneManager.SaveScene(fd.gameObject.scene);
        SessionState.SetBool(KeyRender, true); SessionState.SetString(KeyFolder, folder);
        EditorApplication.EnterPlaymode();
    }
}
