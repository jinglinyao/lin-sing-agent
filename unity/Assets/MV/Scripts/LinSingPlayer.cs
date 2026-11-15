using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// lin-sing standalone player (LinSing.exe, no Unity editor needed). Started by the pipeline:
///   LinSing.exe -song &lt;folder&gt; -mode render -ranges &lt;ranges.txt&gt; -height 1080 -ffmpeg &lt;ffmpeg.exe&gt; -encoder h264_nvenc -done &lt;done.txt&gt;
///   LinSing.exe -song &lt;folder&gt; -mode live          (a window that plays the song with sound and captions; Esc closes it)
/// It loads what the editor's LinSingTools.LoadSong puts into the scene - face track, body (key.bin), eyes (look.txt), camera and light
/// (show.txt), captions (subs.json), the song - and then lets FrameDumper run. Nothing is downloaded, nothing is sent anywhere.
/// </summary>
public class LinSingPlayer : MonoBehaviour
{
    static string Arg(string name, string def = null)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return def;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isEditor) return;                                    // in the editor LinSingTools does this job
        var fd = FindFirstObjectByType<FrameDumper>(); if (fd != null) fd.enabled = false;     // its Start waits until the song is loaded
        new GameObject("LinSingPlayer").AddComponent<LinSingPlayer>();
    }

    string message = "", mode = "live", errFile;

    void Fail(string what)
    {
        Debug.LogError("[LinSing] " + what);
        if (!string.IsNullOrEmpty(errFile)) try { File.WriteAllText(errFile, what); } catch { }
        if (mode == "render") Application.Quit(1); else message = what;
    }

    IEnumerator Start()
    {
        Application.runInBackground = true;
        mode = Arg("-mode", "live"); var folder = Arg("-song");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            message = "请从 lin-sing 的网页（双击“启动 lin-sing”）里打开作品。\nPlease start lin-sing from its launcher.";
            yield return new WaitForSeconds(10f); Application.Quit(2); yield break;
        }
        folder = Path.GetFullPath(folder); errFile = Path.Combine(folder, "player_error.txt");
        if (File.Exists(errFile)) File.Delete(errFile);
        Application.logMessageReceived += (cond, stack, type) =>
        {
            if (type == LogType.Exception && mode == "render") Fail("render stopped by an error: " + cond + "\n" + stack);
        };
        var d = FindFirstObjectByType<SingDriver>(); var fd = FindFirstObjectByType<FrameDumper>();
        if (d == null || fd == null) { Fail("the stage scene is incomplete (no SingDriver / FrameDumper)"); yield break; }
        string P(string f) => Path.Combine(folder, f);
        foreach (var f in new[] { "song.wav", "face.json", "key.bin", "look.txt", "show.txt" })
            if (!File.Exists(P(f))) { Fail(f + " missing in " + folder); yield break; }

        // face, body, eyes, camera and light
        d.lipPath = P("face.json"); d.MissingShapesFresh();
        d.keyClipPath = P("key.bin"); d.keyOn = true; d.ReloadKeyClip(); d.InvalidateBake();
        LoadLook(d, P("look.txt"));
        fd.driver = d; if (fd.cam == null) fd.cam = Camera.main;
        fd.showFile = P("show.txt");

        // the song
        using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(P("song.wav")).AbsoluteUri, AudioType.WAV))
        {
            ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { Fail("could not read song.wav: " + req.error); yield break; }
            var clip = DownloadHandlerAudioClip.GetContent(req); clip.name = "song";
            d.audioSource.clip = clip;
        }
        var ci = CultureInfo.InvariantCulture;
        if (mode == "render")
        {
            var rf = Arg("-ranges"); if (rf == null || !File.Exists(rf)) { Fail("-ranges file missing"); yield break; }
            int h = int.Parse(Arg("-height", "1080"), ci);
            fd.live = false; fd.stage = true; fd.fps = int.Parse(Arg("-fps", "30"), ci); fd.height = h; fd.width = h * 16 / 9; d.mouth = 1f;
            fd.ranges = File.ReadAllText(rf); fd.ffmpegPath = Arg("-ffmpeg", "ffmpeg"); fd.encoder = Arg("-encoder", "h264_nvenc");
            fd.outDir = P("chunks"); fd.startTime = 0f; fd.duration = d.audioSource.clip.length;
            fd.enabled = true;                                               // FrameDumper.Start runs from here
            while (!FrameDumper.Done) yield return null;
            var done = Arg("-done"); if (!string.IsNullOrEmpty(done)) File.WriteAllText(done, FrameDumper.Written + " frames\n");
            Application.Quit(0);
        }
        else
        {
            var ov = FindFirstObjectByType<LyricOverlay>(); if (ov == null) ov = new GameObject("LyricOverlay").AddComponent<LyricOverlay>();
            ov.driver = d; ov.subsPath = File.Exists(P("subs.json")) ? P("subs.json") : "";
            fd.live = true; fd.liveLoop = true; fd.stage = true; fd.startTime = 0f; fd.duration = d.audioSource.clip.length;
            fd.width = 1920; fd.height = 1080; fd.fps = 30; d.mouth = 1f; d.usePreviewTime = false; if (fd.cam != null) fd.cam.targetTexture = null;
            if (Arg("-mute") != null) AudioListener.volume = 0f;
            fd.enabled = true;
            // test hook: -shot <png> -shotAt <seconds of play> saves the window (captions included) and quits
            var shot = Arg("-shot");
            if (shot != null)
            {
                float at = float.Parse(Arg("-shotAt", "10"), ci); float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < at) yield return null;
                yield return new WaitForEndOfFrame(); ScreenCapture.CaptureScreenshot(shot);
                yield return new WaitForSeconds(1.5f); Application.Quit(0);
            }
        }
    }

    /// <summary>look.txt: "look t kind", "hold t0 t1", "own t0 t1 side [grip]" (same reader as the editor's MvSetup.SetLookFromFile)</summary>
    static void LoadLook(SingDriver d, string path)
    {
        var ci = CultureInfo.InvariantCulture;
        var lt = new List<float>(); var lk = new List<int>(); var h0 = new List<float>(); var h1 = new List<float>();
        var o0 = new List<float>(); var o1 = new List<float>(); var os = new List<int>(); var og = new List<int>();
        foreach (var line in File.ReadAllLines(path))
        {
            var q = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); if (q.Length < 3 || q[0].StartsWith("#")) continue;
            if (q[0] == "look") { lt.Add(float.Parse(q[1], ci)); lk.Add(int.Parse(q[2], ci)); }
            else if (q[0] == "hold") { h0.Add(float.Parse(q[1], ci)); h1.Add(float.Parse(q[2], ci)); }
            else if (q[0] == "own" && q.Length >= 4) { o0.Add(float.Parse(q[1], ci)); o1.Add(float.Parse(q[2], ci)); os.Add(int.Parse(q[3], ci)); og.Add(q.Length > 4 ? int.Parse(q[4], ci) : 0); }
        }
        d.lookT = lt.ToArray(); d.lookKind = lk.ToArray(); d.holdT0 = h0.ToArray(); d.holdT1 = h1.ToArray();
        d.ownT0 = o0.ToArray(); d.ownT1 = o1.ToArray(); d.ownSide = os.ToArray(); d.ownGrip = og.ToArray(); d.InvalidateBake();
    }

    void Update()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) Application.Quit();
    }

    void OnGUI()
    {
        if (string.IsNullOrEmpty(message)) return;
        var st = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(22 * Screen.height / 720f), alignment = TextAnchor.MiddleCenter, wordWrap = true };
        st.normal.textColor = Color.white;
        GUI.Label(new Rect(40, 0, Screen.width - 80, Screen.height), message, st);
    }
}
