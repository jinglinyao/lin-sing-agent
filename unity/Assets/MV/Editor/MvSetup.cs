using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Animations;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class MvSetup
{
    static readonly string[] Models = { "Assets/MV/Characters/jinglin_ps.vrm", "Assets/MV/Characters/jinglin_ps2.vrm", "Assets/MV/Characters/jinglin_vis.vrm" };

    public static string Main() { return Build(0); }

    /// <summary>Rebuild the scene with model 0 = jinglin_ps (125 shapes), 1 = jinglin_ps2 (+5 new mouth shapes), 2 = jinglin_vis.</summary>
    public static string Build(int model)
    {
        AssetDatabase.Refresh();
        var sb = new StringBuilder();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Models[model]);
        if (prefab == null) return "ERR: VRM not imported (prefab null)";
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = "Jinglin";
        var anim = go.GetComponent<Animator>();
        var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>();
        var face = smrs.OrderByDescending(s => s.sharedMesh.blendShapeCount).First();
        sb.AppendLine($"renderers {smrs.Length}; face '{face.name}' blendshapes {face.sharedMesh.blendShapeCount}; humanoid {(anim != null && anim.isHuman)}");
        sb.AppendLine("shaders: " + string.Join(", ", smrs.SelectMany(s => s.sharedMaterials).Where(m => m != null).Select(m => m.shader.name).Distinct()));
        sb.AppendLine("components on root: " + string.Join(", ", go.GetComponents<Component>().Select(c => c.GetType().Name)));

        var audio = go.AddComponent<AudioSource>();
        audio.clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/MV/Audio/song_24_47.wav");
        audio.playOnAwake = true; audio.spatialBlend = 0;
        var d = go.AddComponent<SingDriver>();
        d.animator = anim; d.face = face; d.audioSource = audio;
        d.lipJson = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/MV/Data/lipsync_24_47.json");
        d.CaptureRest();
        sb.AppendLine("audio clip " + (audio.clip != null ? audio.clip.length.ToString("F2") + " s" : "MISSING") + "; lip json " + (d.lipJson != null) + "; missing shapes: [" + d.MissingShapes() + "]");

        var head = anim.GetBoneTransform(HumanBodyBones.Head);
        var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
        var lArm = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        sb.AppendLine($"head {head.position:F3} hips {hips.position:F3} leftUpperArm {lArm.position:F3} root forward {go.transform.forward:F2}");

        var cam = Camera.main;
        Vector3 target = head.position + new Vector3(0, 0.03f, 0);
        cam.transform.position = target + go.transform.forward * 1.05f + new Vector3(0.10f, 0.02f, 0);
        cam.transform.LookAt(target);
        cam.fieldOfView = 26f; cam.nearClipPlane = 0.05f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.16f, 0.18f, 0.24f);
        var light = Object.FindFirstObjectByType<Light>();
        if (light != null) { light.transform.rotation = Quaternion.LookRotation(-go.transform.forward + new Vector3(-0.35f, -0.55f, 0)); light.intensity = 1.1f; light.color = Color.white; }

        d.Apply(0f);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), "Assets/MV/Scenes/Sing.unity");
        sb.AppendLine("scene saved: Assets/MV/Scenes/Sing.unity");
        return sb.ToString();
    }

    public static string Pose(float t)
    {
        var d = Object.FindFirstObjectByType<SingDriver>();
        if (d == null) return "no SingDriver";
        d.Apply(t);
        SceneView.RepaintAll();
        return "posed at " + t.ToString("F2");
    }

    public static string AddDumper(int version)
    {
        string outDir = "Capture/sing_v" + version;
        var d = Object.FindFirstObjectByType<SingDriver>();
        var fd = Object.FindFirstObjectByType<FrameDumper>();
        if (fd == null) fd = new GameObject("FrameDumper").AddComponent<FrameDumper>();
        fd.driver = d; fd.cam = Camera.main; fd.outDir = outDir;
        fd.fps = 30; fd.duration = 23f; d.mouth = version >= 300 ? 0f : 1f;
        fd.showFile = ""; fd.stage = false; fd.startTime = 0; fd.live = false;
        if (version >= 900)
        {
            // second song, whole clip: AgentScripts/show_song2.txt (shots + colours), stand microphone. 900 = 1080p30; 90x (x = 1..6) = 24 s excerpts at 720p; 907 = microphone close view; 908 / 909 = side / front full body at 10 fps
            fd.width = 1920; fd.height = 1080; fd.fps = 30; d.mouth = 1f; fd.startTime = 0f; fd.duration = d.audioSource.clip.length; int k9 = version % 10;
            fd.distA = fd.distB = 5.6f; fd.upA = fd.upB = -0.66f; fd.yawA = fd.yawB = 6f;
            if (version / 10 == 96) { fd.showFile = "AgentScripts/show_lin_mic.txt"; fd.stage = true; fd.startTime = d.baseStart[0] + 0.5f; fd.duration = d.baseStart[d.baseStart.Length - 1] + d.baseDur[d.baseDur.Length - 1] - 0.5f - fd.startTime + 3f; if (k9 >= 1) { fd.width = 1280; fd.height = 720; } if (k9 >= 2) { fd.startTime += new[] { 0f, 0f, 9f, 18f, 27.5f, 37f, 46f, 55f, 2.65f }[Mathf.Min(k9, 8)]; fd.duration = 10f; } if (k9 == 8) { fd.width = 1920; fd.height = 1080; fd.duration = 14.6f; } if (k9 == 9) { fd.showFile = ""; fd.width = 1920; fd.height = 1080; fd.duration = 14.6f; fd.yawA = fd.yawB = 8f; fd.distA = fd.distB = 2.05f; fd.upA = fd.upB = -0.21f; } }      // 969: the same 14.6 s from ONE fixed camera (hand-keyed pilot: the camera is left alone)      // 968: the pilot, bars 13.2 - 20 at full size      // lin at the stand microphone, 26-90 s (3 s more than the audio so that nothing fades)
            else if (version / 10 == 95) { fd.showFile = k9 == 0 ? "AgentScripts/show_b2.txt" : ""; fd.stage = true; fd.startTime = 26f; fd.duration = d.baseStart != null && d.baseStart.Length > 0 ? d.baseStart[d.baseStart.Length - 1] + d.baseDur[d.baseDur.Length - 1] - 23.5f : 24f; fd.width = 1280; fd.height = 720; if (k9 >= 1) { fd.fps = 10; fd.width = 960; fd.yawA = fd.yawB = k9 == 1 ? 6f : 70f; fd.distA = fd.distB = 4.4f; fd.upA = fd.upB = -0.56f; if (k9 == 3) { fd.width = 1280; fd.yawA = fd.yawB = 8f; fd.distA = fd.distB = 1.75f; fd.upA = fd.upB = -0.30f; } } }      // BEAT2 trial: 950 = pilot, 951 / 952 = front / side full body
            else if (k9 == 8 || k9 == 9) { fd.width = 960; fd.height = 720; fd.fps = 10; fd.yawA = fd.yawB = k9 == 8 ? 60f : 6f; fd.stage = true; if (d.baseStart != null && d.baseStart.Length > 0 && d.baseStart[d.baseStart.Length - 1] < 60f) fd.duration = 50f; }
            else if (k9 == 6) { fd.width = 960; fd.height = 960; fd.fps = 5; fd.distA = fd.distB = 1.9f; fd.upA = fd.upB = -0.14f; fd.yawA = fd.yawB = -38f; fd.stage = true; fd.duration = d.gestureStart[d.gestureStart.Length - 1] + d.gestureDur[d.gestureDur.Length - 1] + 0.5f; }
            else if (k9 == 7) { fd.width = 960; fd.height = 1080; fd.fps = 5; fd.distA = fd.distB = 1.7f; fd.upA = fd.upB = -0.12f; fd.yawA = fd.yawB = 40f; fd.stage = true; fd.startTime = 26f; fd.duration = 160f; }
            else
            {
                fd.showFile = "AgentScripts/show_song2.txt"; fd.stage = true;
                if (k9 >= 1) { float[] st9 = { 0, 14f, 40f, 56f, 100f, 128f, 170f }; fd.startTime = st9[k9]; fd.duration = 24f; fd.width = 1280; fd.height = 720; }
            }
        }
        else if (version >= 800)
        {
            // microphone + multi-camera pilot: AgentScripts/show_pilot.txt (shots). 800 = 100-124 s at 1080p30; 808 / 809 = side / front full-body QA at 10 fps; 807 = QA from above-front with the stage (mic visible)
            fd.width = 1920; fd.height = 1080; fd.fps = 30; d.mouth = 1f; fd.startTime = 100f; fd.duration = 24f; int k8 = version % 10;
            fd.distA = fd.distB = 4.3f; fd.upA = fd.upB = -0.66f; fd.yawA = fd.yawB = 8f;
            if (k8 == 8 || k8 == 9) { fd.width = 960; fd.height = 720; fd.fps = 10; fd.distA = fd.distB = 5.6f; fd.yawA = fd.yawB = k8 == 8 ? 60f : 6f; fd.stage = true; }
            else if (k8 == 7) { fd.width = 960; fd.height = 1080; fd.fps = 10; fd.distA = fd.distB = 1.7f; fd.upA = fd.upB = -0.12f; fd.yawA = fd.yawB = 40f; fd.stage = true; }
            else { fd.showFile = "AgentScripts/show_pilot.txt"; fd.stage = true; }
        }
        else
        if (version >= 700)
        {
            // formal version: camera path + stage from AgentScripts/show_formal.txt. 700 = whole song 1080p30; 70x (x = 1..7) = 24 s excerpts for review; 708 / 709 = side / front full-body QA at 10 fps
            fd.distA = 3.0f; fd.distB = 2.8f; fd.upA = fd.upB = -0.27f; fd.yawA = 9f; fd.yawB = -7f; fd.width = 1920; fd.height = 1080; fd.fps = 30; d.mouth = 1f; fd.duration = d.audioSource.clip.length;
            int k = version % 10;
            if (version >= 720) { float[] st2 = { 0, 24f, 70f, 88f, 136f, 196f, 216f, 240f }; fd.startTime = st2[k % 8]; fd.duration = 24f; fd.width = 640; fd.height = 720; fd.fps = 10; fd.distA = fd.distB = 0.66f; fd.upA = fd.upB = -0.035f; fd.yawA = fd.yawB = 10f; }   // 72x: face close-up excerpts for QA
            else
            if (k == 8 || k == 9) { fd.width = 480; fd.height = 720; fd.fps = 10; fd.distA = fd.distB = 4.3f; fd.upA = fd.upB = -0.66f; fd.yawA = fd.yawB = k == 8 ? 72f : 8f; }
            else
            {
                fd.showFile = "AgentScripts/show_formal.txt"; fd.stage = true;
                if (k >= 1) { float[] st = { 0, 24f, 70f, 88f, 136f, 196f, 216f, 240f }; fd.startTime = st[k]; fd.duration = 24f; fd.width = 1280; fd.height = 720; }
            }
        }
        else if (version >= 600) { fd.distA = 3.0f; fd.distB = 2.8f; fd.upA = fd.upB = -0.27f; fd.yawA = 9f; fd.yawB = -7f; fd.width = 1920; fd.height = 1080; fd.fps = 30; d.mouth = 1f; fd.duration = d.audioSource.clip.length; if (version % 10 == 8) { fd.width = 480; fd.height = 720; fd.fps = 10; fd.distA = fd.distB = 4.3f; fd.upA = fd.upB = -0.66f; fd.yawA = fd.yawB = 72f; } }   // whole clip, knees-up (6x8 = full-body side view at 10 fps for QA)
        else if (version >= 500) { fd.distA = fd.distB = 4.3f; fd.upA = fd.upB = -0.66f; fd.yawA = fd.yawB = version % 10 == 8 ? 72f : 10f; fd.width = 720; fd.height = 1080; fd.fps = version % 2 == 0 ? 10 : 30; d.mouth = version >= 550 ? 1f : 0f; fd.duration = d.gestureStart.Length > 0 ? d.gestureStart[d.gestureStart.Length - 1] + d.gestureDur[d.gestureDur.Length - 1] + 0.5f : 23f; if (version >= 550) fd.duration = d.audioSource.clip.length; }   // full body, head to feet: QA of legs / pelvis (55x = with mouth, whole clip)
        else if (version >= 400) { fd.distA = fd.distB = 0.60f; fd.upA = fd.upB = -0.035f; fd.yawA = fd.yawB = 18f; fd.width = 960; fd.height = 1080; fd.duration = 11.7f; d.mouth = 1f; }   // mouth-shape test: face close-up, a little from the side
        else if (version >= 300) { fd.distA = fd.distB = 3.0f; fd.upA = fd.upB = -0.25f; fd.yawA = fd.yawB = 12f; fd.width = 960; fd.height = 1080; fd.fps = version % 2 == 0 ? 10 : 30; if (version % 10 == 8) { fd.yawA = fd.yawB = 68f; } fd.duration = d.gestureStart.Length > 0 ? d.gestureStart[d.gestureStart.Length - 1] + d.gestureDur[d.gestureDur.Length - 1] + 0.5f : 23f; }   // gesture catalogue: knees-up, fixed; even version numbers = 10 fps review
        else if (version >= 200) { fd.distA = fd.distB = 3.3f; fd.upA = fd.upB = -0.62f; fd.yawA = fd.yawB = 14f; fd.width = 480; fd.height = 540; fd.fps = 10; fd.duration = 4f * d.bodyClips.Length; }   // clip catalogue: 4 s of every clip
        else if (version >= 100) { fd.distA = fd.distB = 3.3f; fd.upA = fd.upB = -0.62f; fd.yawA = 14f; fd.yawB = 14f; fd.width = 960; fd.height = 1080; }   // motion test: fixed full-body shot
        else { fd.distA = 2.05f; fd.distB = 1.60f; fd.upA = -0.14f; fd.upB = -0.07f; fd.yawA = 9f; fd.yawB = -5f; fd.width = 1920; fd.height = 1080; }   // a little wider: library clips move the upper body more
        EditorUtility.SetDirty(d); EditorUtility.SetDirty(fd);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        return "dumper ready -> " + outDir + "; scene " + EditorSceneManager.GetActiveScene().path;
    }

    public static string Progress()
    {
        return $"playing {EditorApplication.isPlaying} written {FrameDumper.Written} done {FrameDumper.Done} frame {Time.frameCount} time {Time.time:F2}";
    }

    // ------------------------------------------------------------------ motion library (Quaternius UAL, CC0)
    static readonly string[] MotionFiles = { "Assets/MV/Motion/Quaternius/UAL1_Standard.fbx", "Assets/MV/Motion/Quaternius/UAL2_Standard.fbx" };
    static readonly string[] Picks = { "Idle_Loop", "Idle_Talking_Loop", "Dance_Loop", "Walk_Formal_Loop", "Sitting_Talking_Loop", "Idle_FoldArms_Loop",
                                       "Idle_No_Loop", "Yes", "Idle_TalkingPhone_Loop", "Spell_Simple_Idle_Loop", "Idle_Rail_Loop", "Interact" };

    /// <summary>Apply the author's Unity setup (bake axis conversion, Humanoid, loop the *_Loop clips) and report the result.</summary>
    public static string ImportMotion()
    {
        AssetDatabase.Refresh();
        var sb = new StringBuilder();
        foreach (var path in MotionFiles)
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null) { sb.AppendLine("ERR no importer: " + path); continue; }
            imp.bakeAxisConversion = true;
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.importBlendShapes = false; imp.materialImportMode = ModelImporterMaterialImportMode.None;
            imp.SaveAndReimport();
            var clips = imp.defaultClipAnimations;
            foreach (var c in clips)
            {
                c.loopTime = c.name.EndsWith("_Loop");
                // in-place playback: the body stays where the character stands
                c.lockRootRotation = true; c.lockRootHeightY = true; c.lockRootPositionXZ = true;
                c.keepOriginalOrientation = true; c.keepOriginalPositionY = true; c.keepOriginalPositionXZ = true;
            }
            imp.clipAnimations = clips;
            imp.SaveAndReimport();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            var loaded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();
            sb.AppendLine($"{System.IO.Path.GetFileName(path)}: avatar {(avatar != null ? (avatar.isValid ? "valid" : "INVALID") + (avatar.isHuman ? " human" : " not-human") : "none")}; clips {loaded.Length}; humanoid clips {loaded.Count(c => c.isHumanMotion)}");
            sb.AppendLine("  lengths: " + string.Join(", ", loaded.Where(c => Picks.Contains(c.name)).Select(c => $"{c.name} {c.length:F1}s")));
        }
        return sb.ToString();
    }

    static string[] AllMotionFiles() =>
        AssetDatabase.FindAssets("t:Model", new[] { "Assets/MV/Motion" }).Select(AssetDatabase.GUIDToAssetPath).Where(p => p.ToLower().EndsWith(".fbx")).OrderBy(p => p).ToArray();

    static AnimationClip FindClip(string name)
    {
        foreach (var path in AllMotionFiles())
        {
            var c = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(x => !x.name.StartsWith("__preview__") && (x.name == name || x.name.EndsWith("|" + name)));
            if (c != null) return c;
        }
        return null;
    }

    /// <summary>Configure every FBX under Assets/MV/Motion/Mixamo as a humanoid, in-place, looping clip named after its file
    /// (Mixamo calls every take "mixamo.com"). Files whose name ends with "_once" are not looped.</summary>
    public static string ImportMixamo()
    {
        AssetDatabase.Refresh();
        var sb = new StringBuilder();
        foreach (var path in AllMotionFiles().Where(p => p.Contains("/Mixamo/")))
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            string name = System.IO.Path.GetFileNameWithoutExtension(path).Replace(" ", "_");
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.importBlendShapes = false; imp.materialImportMode = ModelImporterMaterialImportMode.None;
            imp.SaveAndReimport();
            var clips = imp.defaultClipAnimations;
            foreach (var c in clips)
            {
                c.name = clips.Length == 1 ? name : name + "_" + c.takeName;
                c.loopTime = !name.EndsWith("_once");
                c.lockRootRotation = true; c.lockRootHeightY = true; c.lockRootPositionXZ = true;
                c.keepOriginalOrientation = true; c.keepOriginalPositionY = true; c.keepOriginalPositionXZ = true;
            }
            imp.clipAnimations = clips;
            imp.SaveAndReimport();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            var loaded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();
            sb.AppendLine($"{name}: avatar {(avatar != null && avatar.isValid && avatar.isHuman ? "ok" : "PROBLEM")}; " + string.Join(", ", loaded.Select(c => $"{c.name} {c.length:F1}s{(c.isHumanMotion ? "" : " NOT-HUMANOID")}")));
        }
        return sb.Length == 0 ? "no FBX under Assets/MV/Motion/Mixamo" : sb.ToString();
    }

    /// <summary>Body clips from AgentScripts/body.txt: one "ClipName startSeconds" per line (# = comment). Empty file = procedural pose.</summary>
    public static string SetBodyFromFile()
    {
        var d = Object.FindFirstObjectByType<SingDriver>();
        var clips = new System.Collections.Generic.List<AnimationClip>(); var starts = new System.Collections.Generic.List<float>();
        foreach (var raw in System.IO.File.ReadAllLines("AgentScripts/body.txt"))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split(new[] { ' ', '	' }, System.StringSplitOptions.RemoveEmptyEntries);
            var c = FindClip(parts[0]); if (c == null) return "clip not found: " + parts[0];
            clips.Add(c); starts.Add(parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0f);
        }
        d.ResetGraph(); d.bodyClips = clips.ToArray(); d.bodyStart = starts.ToArray(); d.bodyFade = 0.9f; d.headDamp = 0.75f; EditorUtility.SetDirty(d);
        return "body clips: " + (clips.Count == 0 ? "(procedural)" : string.Join(", ", clips.Select((c, i) => $"{c.name}@{starts[i]:F1}s")));
    }

    public static string ListClips()
    {
        var sb = new StringBuilder();
        foreach (var path in AllMotionFiles())
        {
            var loaded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).OrderBy(c => c.name).ToArray();
            sb.AppendLine(System.IO.Path.GetFileName(path) + ": " + string.Join(", ", loaded.Select(c => $"{c.name} {c.length:F1}s{(c.isLooping ? " loop" : "")}")));
        }
        return sb.ToString();
    }

    /// <summary>Edit-mode preview: sample library clip #pick at a time on the character (animation mode; StopSample reverts).</summary>
    public static string SampleClip(int pick, float time)
    {
        var d = Object.FindFirstObjectByType<SingDriver>();
        var clip = FindClip(Picks[pick]);
        if (clip == null) return "clip not found: " + Picks[pick];
        if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
        AnimationMode.BeginSampling();
        AnimationMode.SampleAnimationClip(d.gameObject, clip, Mathf.Repeat(time, clip.length));
        AnimationMode.EndSampling();
        SceneView.RepaintAll();
        return $"sampled {clip.name} at {time:F2} / {clip.length:F2}";
    }

    /// <summary>Body from library clips: up to three picks with their start times (clip seconds); pick < 0 = unused. All -1 = back to the procedural pose.</summary>
    public static string SetBody(int p0, float t0, int p1, float t1, int p2, float t2)
    {
        var d = Object.FindFirstObjectByType<SingDriver>();
        var clips = new System.Collections.Generic.List<AnimationClip>(); var starts = new System.Collections.Generic.List<float>();
        foreach (var (p, t) in new[] { (p0, t0), (p1, t1), (p2, t2) })
        {
            if (p < 0) continue;
            var c = FindClip(Picks[p]); if (c == null) return "clip not found: " + Picks[p];
            clips.Add(c); starts.Add(t);
        }
        d.ResetGraph();
        d.bodyClips = clips.ToArray(); d.bodyStart = starts.ToArray();
        EditorUtility.SetDirty(d);
        return "body clips: " + (clips.Count == 0 ? "(procedural)" : string.Join(", ", clips.Select((c, i) => $"{c.name}@{starts[i]:F1}s")));
    }

    public static string DebugGraph()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var sb = new StringBuilder();
        var arm = an.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        var clip = FindClip("Dance_Loop");
        sb.AppendLine($"animator enabled {an.enabled} active {an.gameObject.activeInHierarchy} avatar {(an.avatar != null ? an.avatar.name + " valid " + an.avatar.isValid : "null")} clip human {clip.isHumanMotion} legacy {clip.legacy} len {clip.length:F2}");
        sb.AppendLine($"before: arm {arm.localEulerAngles:F1}");
        var g = UnityEngine.Playables.PlayableGraph.Create("dbg");
        g.SetTimeUpdateMode(UnityEngine.Playables.DirectorUpdateMode.Manual);
        var cp = UnityEngine.Animations.AnimationClipPlayable.Create(g, clip);
        var o = UnityEngine.Animations.AnimationPlayableOutput.Create(g, "o", an); o.SetSourcePlayable(cp);
        cp.SetTime(0.3); g.Evaluate();
        sb.AppendLine($"manual evaluate: arm {arm.localEulerAngles:F1}");
        g.Play(); cp.SetTime(0.6); g.Evaluate(0f);
        sb.AppendLine($"after Play+Evaluate(0): arm {arm.localEulerAngles:F1}");
        an.Rebind(); cp.SetTime(0.8); g.Evaluate(0f);
        sb.AppendLine($"after Rebind+Evaluate: arm {arm.localEulerAngles:F1}");
        an.Update(0f);
        sb.AppendLine($"after animator.Update(0): arm {arm.localEulerAngles:F1}");
        g.Destroy();
        // legacy-free alternative: sample directly
        clip.SampleAnimation(an.gameObject, 0.3f);
        sb.AppendLine($"after clip.SampleAnimation: arm {arm.localEulerAngles:F1}");
        return sb.ToString();
    }

    public static string DebugApply()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var sb = new StringBuilder();
        var arm = an.GetBoneTransform(HumanBodyBones.LeftUpperArm); var hips = an.GetBoneTransform(HumanBodyBones.Hips);
        sb.AppendLine($"bodyClips {(d.bodyClips == null ? -1 : d.bodyClips.Length)} starts {(d.bodyStart == null ? -1 : d.bodyStart.Length)} fade {d.bodyFade} speed {d.bodySpeed} motion {d.motion}");
        sb.AppendLine($"before: arm {arm.localEulerAngles:F1} hips {hips.localPosition:F3}");
        d.Apply(0.3f);
        sb.AppendLine($"Apply(0.3): arm {arm.localEulerAngles:F1} hips {hips.localPosition:F3}");
        d.Apply(0.8f);
        sb.AppendLine($"Apply(0.8): arm {arm.localEulerAngles:F1} hips {hips.localPosition:F3}");
        return sb.ToString();
    }

    /// <summary>Head motion over the clip: angular speed of the head (relative to the character root) and its yaw / pitch range.</summary>
    public static string HeadStats()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var head = d.animator.GetBoneTransform(HumanBodyBones.Head);
        var sp = new System.Collections.Generic.List<float>(); float yMin = 999, yMax = -999, pMin = 999, pMax = -999;
        Quaternion prev = Quaternion.identity; const float dt = 1f / 30f; float worst = 0; int worstF = 0;
        for (int f = 0; f < 690; f++)
        {
            d.ApplyBones(f * dt);
            Quaternion q = Quaternion.Inverse(d.transform.rotation) * head.rotation;
            Vector3 fw = q * Vector3.forward;
            float yaw = Mathf.Atan2(fw.x, fw.z) * Mathf.Rad2Deg, pitch = -Mathf.Asin(Mathf.Clamp(fw.y, -1, 1)) * Mathf.Rad2Deg;
            yMin = Mathf.Min(yMin, yaw); yMax = Mathf.Max(yMax, yaw); pMin = Mathf.Min(pMin, pitch); pMax = Mathf.Max(pMax, pitch);
            if (f > 0) { float v = Quaternion.Angle(prev, q) / dt; sp.Add(v); if (v > worst) { worst = v; worstF = f; } }
            prev = q;
        }
        sp.Sort();
        return $"worst at frame {worstF} ({worstF * dt:F2}s) | head angular speed deg/s: mean {sp.Average():F1}  median {sp[sp.Count / 2]:F1}  p95 {sp[(int)(sp.Count * 0.95f)]:F1}  max {sp[sp.Count - 1]:F1} | yaw {yMin:F1}..{yMax:F1}  pitch {pMin:F1}..{pMax:F1} (deg, + = right / down) | body: {(d.bodyClips != null && d.bodyClips.Length > 0 ? d.bodyClips.Length + " clips, headDamp " + d.headDamp : "procedural")}";
    }

    /// <summary>Import check for the viseme-extended model (does not touch the scene).</summary>
    public static string CheckVis()
    {
        AssetDatabase.Refresh();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/MV/Characters/jinglin_vis.vrm");
        if (prefab == null) return "ERR: jinglin_vis.vrm not imported";
        var face = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderByDescending(x => x.sharedMesh.blendShapeCount).First();
        var mesh = face.sharedMesh; var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
        string[] want = { "viseme_sil", "viseme_PP", "viseme_FF", "viseme_aa", "viseme_ou", "vis_WW", "vis_ZH", "sing_A_sad", "sing_E_sad", "sing_O_round", "sing_hum", "Fcl_MTH_A", "jawOpen" };
        var anim = prefab.GetComponent<Animator>();
        return $"blendshapes {mesh.blendShapeCount}; humanoid {(anim != null && anim.isHuman)}; shaders {string.Join(",", prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Select(m => m.shader.name).Distinct())}; missing: [{string.Join(",", want.Where(w => !names.Contains(w)))}]; new names: {string.Join(" ", names.Where(n => n.StartsWith("viseme_") || n.StartsWith("vis_") || n.StartsWith("sing_")))}";
    }

    /// <summary>Code gestures from AgentScripts/gestures.txt: "name start duration" per line (# = comment; first line may be "rest folded"). Clears body clips.</summary>
    public static string SetGesturesFromFile()
    {
        var d = Object.FindFirstObjectByType<SingDriver>();
        var names = new System.Collections.Generic.List<string>(); var st = new System.Collections.Generic.List<float>(); var du = new System.Collections.Generic.List<float>();
        d.restPose = "rest";
        foreach (var raw in System.IO.File.ReadAllLines("AgentScripts/gestures.txt"))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
            var q = line.Split(new[] { ' ', (char)9 }, System.StringSplitOptions.RemoveEmptyEntries);
            if (q[0] == "rest" && q.Length == 2) { d.restPose = q[1]; continue; }
            string nm = q[0].TrimEnd('~').TrimEnd('+', '-');
            foreach (var part in nm.Split('|')) if (!SingDriver.Library.ContainsKey(part.EndsWith("_L") ? part.Substring(0, part.Length - 2) : part)) return "unknown gesture: " + q[0];
            names.Add(q[0]); st.Add(float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture)); du.Add(float.Parse(q[2], System.Globalization.CultureInfo.InvariantCulture));
        }
        d.ResetGraph(); d.bodyClips = new AnimationClip[0]; d.bodyStart = new float[0];
        d.gestureNames = names.ToArray(); d.gestureStart = st.ToArray(); d.gestureDur = du.ToArray(); EditorUtility.SetDirty(d);
        return $"gestures {names.Count} (rest pose '{d.restPose}'); library: " + string.Join(" ", SingDriver.Library.Keys);
    }

    /// <summary>Which face track the driver plays: 0 = the unravel clip, 1 = the shape test (each new mouth shape in turn).</summary>
    public static string UseTrack(int k)
    {
        var d = Object.FindFirstObjectByType<SingDriver>();
        string path = k == 1 ? "Assets/MV/Data/shape_test.json" : k == 2 ? "Assets/MV/Data/lipsync_full.json" : k == 3 ? "Assets/MV/Data/lipsync_song2.json" : k == 4 ? "Assets/MV/Data/lipsync_song2_vroid.json" : "Assets/MV/Data/lipsync_24_47.json";
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        d.lipJson = AssetDatabase.LoadAssetAtPath<TextAsset>(path); EditorUtility.SetDirty(d);
        string apath = k == 2 ? "Assets/MV/Audio/song_full.wav" : k == 3 || k == 4 ? "Assets/MV/Audio/song2.wav" : "Assets/MV/Audio/song_24_47.wav";
        AssetDatabase.ImportAsset(apath);
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(apath); if (clip != null && d.audioSource != null) { d.audioSource.clip = clip; EditorUtility.SetDirty(d.audioSource); }
        return "track " + path + " missing shapes: [" + d.MissingShapesFresh() + "]";
    }

    public static string NoGestures()
    {
        var d = Object.FindFirstObjectByType<SingDriver>();
        d.ResetGraph(); d.bodyClips = new AnimationClip[0]; d.bodyStart = new float[0]; d.gestureNames = new string[0]; d.gestureStart = new float[0]; d.gestureDur = new float[0]; d.restPose = "rest"; EditorUtility.SetDirty(d);
        return "no gestures";
    }

    /// <summary>Motion probe for QA: poses the character at 60 fps over the clip (no rendering) and writes Capture/probe.csv.</summary>
    public static string Probe(float duration)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform;
        Transform T(HumanBodyBones b) => an.GetBoneTransform(b);
        var head = T(HumanBodyBones.Head); var chest = T(HumanBodyBones.UpperChest) ?? T(HumanBodyBones.Chest); var hips = T(HumanBodyBones.Hips);
        var sb = new StringBuilder("t,hqx,hqy,hqz,hqw,cqx,cqy,cqz,cqw,cx,cy,cz,px,py,pz,rx,ry,rz,lx,ly,lz,erx,ery,erz,elx,ely,elz,flexR,flexL,clear,frx,fry,frz,flx,fly,flz,curlR,rhqx,rhqy,rhqz,rhqw,lhqx,lhqy,lhqz,lhqw,kneeR,kneeL,curlR5,blink,rootx,rootz,mox,moy,moz\n");
        int blinkIdx = d.face != null ? d.face.sharedMesh.GetBlendShapeIndex("Fcl_EYE_Close") : -1; var lit = T(HumanBodyBones.RightLittleProximal);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string V3(Vector3 v) => v.x.ToString("F5", ci) + "," + v.y.ToString("F5", ci) + "," + v.z.ToString("F5", ci);
        string Q(Quaternion q) => q.x.ToString("F6", ci) + "," + q.y.ToString("F6", ci) + "," + q.z.ToString("F6", ci) + "," + q.w.ToString("F6", ci);
        float Flex(HumanBodyBones up, HumanBodyBones lo, HumanBodyBones ha) => Vector3.Angle(T(lo).position - T(up).position, T(ha).position - T(lo).position);
        int n = Mathf.RoundToInt(duration * 60f);
        for (int f = 0; f < n; f++)
        {
            float t = f / 60f; d.ApplyBones(t); d.ApplyFace(t);
            Quaternion inv = Quaternion.Inverse(root.rotation);
            var idx = T(HumanBodyBones.RightIndexProximal);
            sb.Append(t.ToString("F4", ci)).Append(',').Append(Q(inv * head.rotation)).Append(',').Append(Q(inv * chest.rotation)).Append(',')
              .Append(V3(root.InverseTransformPoint(chest.position))).Append(',').Append(V3(root.InverseTransformPoint(hips.position))).Append(',')
              .Append(V3(root.InverseTransformPoint(T(HumanBodyBones.RightHand).position))).Append(',').Append(V3(root.InverseTransformPoint(T(HumanBodyBones.LeftHand).position))).Append(',')
              .Append(V3(root.InverseTransformPoint(T(HumanBodyBones.RightLowerArm).position))).Append(',').Append(V3(root.InverseTransformPoint(T(HumanBodyBones.LeftLowerArm).position))).Append(',')
              .Append(Flex(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand).ToString("F2", ci)).Append(',')
              .Append(Flex(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand).ToString("F2", ci)).Append(',')
              .Append(d.lastClearance.ToString("F4", ci)).Append(',')
              .Append(V3(root.InverseTransformPoint(T(HumanBodyBones.RightFoot).position))).Append(',').Append(V3(root.InverseTransformPoint(T(HumanBodyBones.LeftFoot).position))).Append(',')
              .Append((idx != null ? Quaternion.Angle(idx.localRotation, Quaternion.identity) : 0f).ToString("F2", ci))
              .Append(',').Append(Q(inv * T(HumanBodyBones.RightHand).rotation)).Append(',').Append(Q(inv * T(HumanBodyBones.LeftHand).rotation)).Append(',')
              .Append(Flex(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot).ToString("F2", ci)).Append(',')
              .Append(Flex(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot).ToString("F2", ci)).Append(',')
              .Append((lit != null ? Quaternion.Angle(lit.localRotation, Quaternion.identity) : 0f).ToString("F2", ci)).Append(',')
              .Append((blinkIdx >= 0 ? d.face.GetBlendShapeWeight(blinkIdx) : 0f).ToString("F1", ci)).Append(',')
              .Append(root.position.x.ToString("F4", ci)).Append(',').Append(root.position.z.ToString("F4", ci)).Append(',')
              .Append(V3(head.position + head.rotation * d.mouthLocal)).Append('\n');          // mouth, world space
        }
        System.IO.Directory.CreateDirectory("Capture"); System.IO.File.WriteAllText("Capture/probe.csv", sb.ToString());
        d.ApplyBones(0f);
        return "probe: " + n + " frames -> Capture/probe.csv";
    }

    /// <summary>
    /// Library clips as data: every Mixamo clip is retargeted onto the character once (AnimationMode sampling, 60 fps) and the local
    /// rotations of her body bones + the pelvis position are written to Assets/MV/Data/Clips/NAME.json. SingDriver then plays them as
    /// plain curves (BaseLayer.cs), so a clip can drive the legs while the code gestures drive the arms, and everything stays a
    /// pure function of time (works in edit mode, in the probe and in the contact bake).
    /// </summary>
    public static string BakeClips()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform;
        var bones = new System.Collections.Generic.List<HumanBodyBones>();
        foreach (HumanBodyBones b in System.Enum.GetValues(typeof(HumanBodyBones))) if (b != HumanBodyBones.LastBone && an.GetBoneTransform(b) != null && b != HumanBodyBones.LeftEye && b != HumanBodyBones.RightEye && b != HumanBodyBones.Jaw) bones.Add(b);
        System.IO.Directory.CreateDirectory("Assets/MV/Data/Clips");
        var ci = System.Globalization.CultureInfo.InvariantCulture; var sb = new StringBuilder();
        var hips = an.GetBoneTransform(HumanBodyBones.Hips);
        if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
        try
        {
            foreach (var path in AllMotionFiles().Where(p => p.Contains("/Mixamo/")))
            {
                var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(x => !x.name.StartsWith("__preview__"));
                if (clip == null) continue;
                const int fps = 60; int n = Mathf.Max(2, Mathf.RoundToInt(clip.length * fps) + 1);
                var js = new StringBuilder();
                js.Append("{\"name\":\"").Append(clip.name).Append("\",\"fps\":").Append(fps).Append(",\"count\":").Append(n).Append(",\"length\":").Append(clip.length.ToString("F4", ci)).Append(",\"bones\":[")
                  .Append(string.Join(",", bones.Select(b => ((int)b).ToString()))).Append("],\"pos\":[");
                var rot = new StringBuilder(); Vector3 p0 = Vector3.zero, p1 = Vector3.zero; float minFoot = 9, maxHipY = 0, minHipY = 9;
                for (int f = 0; f < n; f++)
                {
                    float t = Mathf.Min(f / (float)fps, clip.length - 0.0005f);
                    AnimationMode.BeginSampling(); AnimationMode.SampleAnimationClip(d.gameObject, clip, t); AnimationMode.EndSampling();
                    Vector3 hp = root.InverseTransformPoint(hips.position);            // pelvis in character space
                    if (f == 0) p0 = hp; p1 = hp; minHipY = Mathf.Min(minHipY, hp.y); maxHipY = Mathf.Max(maxHipY, hp.y);
                    minFoot = Mathf.Min(minFoot, Mathf.Min(root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.LeftFoot).position).y, root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.RightFoot).position).y));
                    if (f > 0) { js.Append(','); rot.Append(','); }
                    js.Append(hp.x.ToString("F4", ci)).Append(',').Append(hp.y.ToString("F4", ci)).Append(',').Append(hp.z.ToString("F4", ci));
                    for (int k = 0; k < bones.Count; k++)
                    {
                        // the pelvis rotation is stored in character space, all other bones in their parent's space
                        var tr = an.GetBoneTransform(bones[k]); Quaternion q = bones[k] == HumanBodyBones.Hips ? Quaternion.Inverse(root.rotation) * tr.rotation : tr.localRotation;
                        if (k > 0) rot.Append(',');
                        rot.Append(q.x.ToString("F4", ci)).Append(',').Append(q.y.ToString("F4", ci)).Append(',').Append(q.z.ToString("F4", ci)).Append(',').Append(q.w.ToString("F4", ci));
                    }
                }
                js.Append("],\"rot\":[").Append(rot).Append("]}");
                System.IO.File.WriteAllText("Assets/MV/Data/Clips/" + clip.name + ".json", js.ToString());
                sb.AppendLine($"{clip.name}: {clip.length:F2}s, {n} frames; pelvis travel x {p1.x - p0.x:+0.00;-0.00} z {p1.z - p0.z:+0.00;-0.00} m, pelvis height {minHipY:F2}..{maxHipY:F2}, lowest ankle {minFoot:F2}");
            }
        }
        finally { AnimationMode.StopAnimationMode(); }
        AssetDatabase.Refresh();
        return sb.ToString() + "bones " + bones.Count;
    }

    /// <summary>
    /// A BEAT2 / SMPL-X take (binary written by tools/beat2_convert.py v2) becomes a base-layer clip like the Mixamo ones.
    /// Body: both skeletons rest in a T-pose, so a bone's world rotation is (global rotation of the SMPL-X joint) * (its own rest rotation).
    /// Fingers: flexion / spread ANGLES about axes measured on her own hand (no twist can come through).
    /// Legs: the pelvis height is not the take's (her legs are not the speaker's): the lower ankle stands at its rest height, then she is
    /// stood taller by most of the usual knee bend and the legs are solved again to the same ankles (the feet do not move for it).
    /// </summary>
    public static string ImportSmplx(string bin, string name)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform;
        var H = new System.Func<HumanBodyBones, Transform>(b => an.GetBoneTransform(b));
        var map = new System.Collections.Generic.List<(int j, HumanBodyBones b)> {
            (0, HumanBodyBones.Hips), (1, HumanBodyBones.LeftUpperLeg), (2, HumanBodyBones.RightUpperLeg), (3, HumanBodyBones.Spine), (4, HumanBodyBones.LeftLowerLeg), (5, HumanBodyBones.RightLowerLeg),
            (6, HumanBodyBones.Chest), (7, HumanBodyBones.LeftFoot), (8, HumanBodyBones.RightFoot), (9, HumanBodyBones.UpperChest), (12, HumanBodyBones.Neck), (13, HumanBodyBones.LeftShoulder), (14, HumanBodyBones.RightShoulder),
            (15, HumanBodyBones.Head), (16, HumanBodyBones.LeftUpperArm), (17, HumanBodyBones.RightUpperArm), (18, HumanBodyBones.LeftLowerArm), (19, HumanBodyBones.RightLowerArm), (20, HumanBodyBones.LeftHand), (21, HumanBodyBones.RightHand) };
        map.RemoveAll(m => H(m.b) == null);
        int Depth(Transform t) { int n = 0; while (t != null && t != root) { t = t.parent; n++; } return n; }
        map.Sort((a, b) => Depth(H(a.b)).CompareTo(Depth(H(b.b))));

        var bones = new System.Collections.Generic.List<HumanBodyBones>();
        foreach (HumanBodyBones b in System.Enum.GetValues(typeof(HumanBodyBones))) if (b != HumanBodyBones.LastBone && H(b) != null && b != HumanBodyBones.LeftEye && b != HumanBodyBones.RightEye && b != HumanBodyBones.Jaw) bones.Add(b);
        var hips = H(HumanBodyBones.Hips); var la = H(HumanBodyBones.LeftFoot); var ra = H(HumanBodyBones.RightFoot);

        // the pose she is in now is put back at the end; the retarget starts from her rest (T) pose
        var keep = bones.Select(b => H(b).localRotation).ToArray(); Vector3 keepHips = hips.localPosition;
        var fl = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var restBone = (System.Collections.Generic.List<int>)typeof(SingDriver).GetField("restBone", fl).GetValue(d);
        var restRot = (System.Collections.Generic.List<Quaternion>)typeof(SingDriver).GetField("restRot", fl).GetValue(d);
        Vector3 restHipsLocal = (Vector3)typeof(SingDriver).GetField("restHipsPos", fl).GetValue(d);
        typeof(SingDriver).GetMethod("Load", fl).Invoke(d, null);
        var solveLeg = typeof(SingDriver).GetMethod("SolveLegTo", fl);
        for (int i = 0; i < restBone.Count; i++) { var t = H((HumanBodyBones)restBone[i]); if (t != null) t.localRotation = restRot[i]; }
        hips.localPosition = restHipsLocal;
        Quaternion rinv = Quaternion.Inverse(root.rotation);
        var restCS = map.Select(m => rinv * H(m.b).rotation).ToArray();
        Vector3 restHips = root.InverseTransformPoint(hips.position);
        float restAnkle = Mathf.Min(root.InverseTransformPoint(la.position).y, root.InverseTransformPoint(ra.position).y);
        float scale = restHips.y / 0.93f;                                              // her legs against a grown speaker's (pelvis about 0.93 m up)

        // ---- her hands, measured in the T-pose: per finger joint the axis that closes it (towards the palm) and, at the knuckle, the one that spreads it (towards the thumb)
        var fRest = new Quaternion[2, 5, 3]; var fFlex = new Vector3[2, 5, 3]; var fSpread = new Vector3[2, 5]; var thumbRestCS = new Quaternion[2]; var fBone = new Transform[2, 5, 3];
        for (int side = 0; side < 2; side++)
        {
            int baseId = (int)(side == 0 ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
            var handT = H(side == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand); var mid = H(side == 0 ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            Vector3 fingerDir = (mid.position - handT.position).normalized;
            for (int f = 0; f < 5; f++) for (int k = 0; k < 3; k++)
            {
                var t = H((HumanBodyBones)(baseId + f * 3 + k)); fBone[side, f, k] = t; if (t == null) continue;
                var child = k < 2 ? H((HumanBodyBones)(baseId + f * 3 + k + 1)) : (t.childCount > 0 ? t.GetChild(0) : null);
                Vector3 b = child != null ? (child.position - t.position).normalized : (t.position - t.parent.position).normalized;
                fRest[side, f, k] = t.localRotation;
                Quaternion.FromToRotation(b, f == 0 ? fingerDir : -root.up).ToAngleAxis(out float _, out Vector3 ax);      // the thumb closes across the palm, towards the fingers
                fFlex[side, f, k] = Quaternion.Inverse(t.rotation) * ax;
                if (k == 0) { Quaternion.FromToRotation(b, root.forward).ToAngleAxis(out float _, out Vector3 ax2); fSpread[side, f] = Quaternion.Inverse(t.rotation) * ax2; }
            }
            thumbRestCS[side] = rinv * fBone[side, 0, 0].rotation;
        }

        var raw = System.IO.File.ReadAllBytes(bin); int n = System.BitConverter.ToInt32(raw, 0), fps = System.BitConverter.ToInt32(raw, 4);
        const int W = 3 + 22 * 4 + 44 + 4; var v = new float[n * W]; System.Buffer.BlockCopy(raw, 8, v, 0, n * W * 4);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var rot = new StringBuilder(); var px = new float[n]; var pz = new float[n]; var py = new float[n]; var slack = new float[n];
        var ankL = new Vector3[n]; var ankR = new Vector3[n]; float kneeBefore = 0, kneeAfter = 0;
        var legs = new[] { (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot) };
        float KneeBend(int s) { var u = H(legs[s].Item1); var l = H(legs[s].Item2); var ft = H(legs[s].Item3); return Vector3.Angle(l.position - u.position, ft.position - l.position); }

        void Pose(int f, float y)
        {
            int o = f * W;
            for (int m = 0; m < map.Count; m++)
            {
                int q = o + 3 + map[m].j * 4; var g = new Quaternion(v[q], v[q + 1], v[q + 2], v[q + 3]);
                H(map[m].b).rotation = root.rotation * g * restCS[m];
            }
            for (int side = 0; side < 2; side++)
            {
                int h = o + 3 + 88 + side * 22;
                for (int fi = 1; fi < 5; fi++)
                {
                    int a = h + (fi - 1) * 4; float flex1 = v[a], spread = v[a + 1] * 0.4f, flex2 = v[a + 2], flex3 = v[a + 3];
                    if (fBone[side, fi, 0] != null) fBone[side, fi, 0].localRotation = fRest[side, fi, 0] * Quaternion.AngleAxis(spread, fSpread[side, fi]) * Quaternion.AngleAxis(flex1, fFlex[side, fi, 0]);
                    if (fBone[side, fi, 1] != null) fBone[side, fi, 1].localRotation = fRest[side, fi, 1] * Quaternion.AngleAxis(flex2, fFlex[side, fi, 1]);
                    if (fBone[side, fi, 2] != null) fBone[side, fi, 2].localRotation = fRest[side, fi, 2] * Quaternion.AngleAxis(flex3, fFlex[side, fi, 2]);
                }
                var sw = new Quaternion(v[h + 16], v[h + 17], v[h + 18], v[h + 19]);
                if (fBone[side, 0, 0] != null) fBone[side, 0, 0].localRotation = fRest[side, 0, 0] * (Quaternion.Inverse(thumbRestCS[side]) * sw * thumbRestCS[side]);
                if (fBone[side, 0, 1] != null) fBone[side, 0, 1].localRotation = fRest[side, 0, 1] * Quaternion.AngleAxis(v[h + 20], fFlex[side, 0, 1]);
                if (fBone[side, 0, 2] != null) fBone[side, 0, 2].localRotation = fRest[side, 0, 2] * Quaternion.AngleAxis(v[h + 21], fFlex[side, 0, 2]);
            }
            hips.position = root.TransformPoint(new Vector3(px[f], y, pz[f]));
        }

        try
        {
            // pass 1: where the floor is, and how much higher she could stand before a leg is straight
            for (int f = 0; f < n; f++)
            {
                px[f] = restHips.x + v[f * W] * scale; pz[f] = restHips.z + v[f * W + 2] * scale;
                Pose(f, restHips.y);
                Vector3 a = root.InverseTransformPoint(la.position), b = root.InverseTransformPoint(ra.position);
                py[f] = restAnkle - Mathf.Min(a.y, b.y); ankL[f] = a; ankR[f] = b;
                float sl = 9f;
                for (int s = 0; s < 2; s++)
                {
                    var u = H(legs[s].Item1); var l = H(legs[s].Item2); var ft = H(legs[s].Item3);
                    float len = (Vector3.Distance(u.position, l.position) + Vector3.Distance(l.position, ft.position)) * 0.985f;
                    Vector3 dv = root.InverseTransformVector(u.position - ft.position); float h2 = len * len - dv.x * dv.x - dv.z * dv.z;
                    sl = Mathf.Min(sl, (h2 > 0 ? Mathf.Sqrt(h2) : 0f) - dv.y);
                }
                slack[f] = Mathf.Max(0f, sl); kneeBefore += (KneeBend(0) + KneeBend(1)) * 0.5f / n;
            }
            float[] Smooth(float[] x, float sig) { var o = new float[n]; int r = Mathf.CeilToInt(sig * 3); for (int f = 0; f < n; f++) { float s = 0, w = 0; for (int k = -r; k <= r; k++) { int i = Mathf.Clamp(f + k, 0, n - 1); float g = Mathf.Exp(-k * k / (2 * sig * sig)); s += x[i] * g; w += g; } o[f] = s / w; } return o; }
            var ys = Smooth(py, 4f); var ss = Smooth(slack, 5f);
            var attRaw = new float[n]; var tgtRaw = new Vector3[n]; var curlRaw = new[] { new float[n], new float[n] };
            var lhT = H(HumanBodyBones.LeftHand); var rhT = H(HumanBodyBones.RightHand); var chT = H(HumanBodyBones.UpperChest) ?? H(HumanBodyBones.Chest); var luT = H(HumanBodyBones.LeftUpperArm); var hdB = H(HumanBodyBones.Head);
            var sorted = slack.OrderBy(x => x).ToArray(); float tall = 0.8f * sorted[n / 2];
            var lift = new float[n]; for (int f = 0; f < n; f++) lift[f] = Mathf.Min(tall, ss[f] * 0.9f); lift = Smooth(lift, 3f);
            // pass 2: the pose with the legs solved again to the same ankles under the lifted pelvis
            for (int f = 0; f < n; f++)
            {
                Pose(f, restHips.y + ys[f]);
                Vector3 lp = la.position, rp = ra.position; Quaternion lr = la.rotation, rr = ra.rotation;
                hips.position += root.up * lift[f];
                solveLeg.Invoke(d, new object[] { false, lp, lr }); solveLeg.Invoke(d, new object[] { true, rp, rr });
                kneeAfter += (KneeBend(0) + KneeBend(1)) * 0.5f / n;
                py[f] = root.InverseTransformPoint(hips.position).y;
                {   // where her hands are, for the head and the eyes: together and raised in front -> the space between them; one hand raised -> that hand
                    Vector3 PL = root.InverseTransformPoint(lhT.position + lhT.rotation * Vector3.left * 0.07f), PR = root.InverseTransformPoint(rhT.position + rhT.rotation * Vector3.right * 0.07f);
                    Vector3 ch = root.InverseTransformPoint(chT.position); float shY = root.InverseTransformPoint(luT.position).y; Vector3 mid = (PL + PR) * 0.5f; float dist = Vector3.Distance(PL, PR);
                    float S(float e0, float e1, float x) { x = Mathf.Clamp01((x - e0) / (e1 - e0)); return x * x * (3f - 2f * x); }
                    float wa = (1f - S(0.15f, 0.30f, dist)) * S(ch.y - 0.10f, ch.y + 0.10f, mid.y) * S(0.05f, 0.15f, mid.z - ch.z) * Mathf.Lerp(0.42f, 1f, S(ch.y + 0.02f, ch.y + 0.22f, mid.y));      // hands folded at the chest: a glance down; raised to the face: she looks at them
                    float wL = 0.7f * S(shY - 0.02f, shY + 0.15f, PL.y) * S(0.08f, 0.20f, PL.z - ch.z), wR = 0.7f * S(shY - 0.02f, shY + 0.15f, PR.y) * S(0.08f, 0.20f, PR.z - ch.z);
                    // hands at her own throat or chin cannot be looked at (the target would sit inside the head: the direction flips about):
                    // only what is at least a hand's length in front of the face draws the eyes
                    float hz = root.InverseTransformPoint(hdB.position).z;
                    wa *= S(0.10f, 0.22f, mid.z - hz); wL *= S(0.10f, 0.22f, PL.z - hz); wR *= S(0.10f, 0.22f, PR.z - hz);
                    float wsum = wa + wL + wR; attRaw[f] = Mathf.Max(wa, Mathf.Max(wL, wR)); tgtRaw[f] = wsum > 1e-4f ? (wa * mid + wL * PL + wR * PR) / wsum : mid;
                    for (int side = 0; side < 2; side++)
                    {
                        int h = f * W + 3 + 88 + side * 22; float cs = 0;
                        for (int fi = 0; fi < 4; fi++) cs += (Mathf.Max(0f, v[h + fi * 4]) / 72f + v[h + fi * 4 + 2] / 95f + v[h + fi * 4 + 3] / 62f) / 3f;
                        curlRaw[side][f] = Mathf.Clamp01(cs / 4f);
                    }
                }
                for (int k = 0; k < bones.Count; k++)
                {
                    var tr = H(bones[k]); Quaternion q = bones[k] == HumanBodyBones.Hips ? rinv * tr.rotation : tr.localRotation;
                    if (f > 0 || k > 0) rot.Append(',');
                    rot.Append(q.x.ToString("F4", ci)).Append(',').Append(q.y.ToString("F4", ci)).Append(',').Append(q.z.ToString("F4", ci)).Append(',').Append(q.w.ToString("F4", ci));
                }
            }
            var js = new StringBuilder();
            js.Append("{\"name\":\"").Append(name).Append("\",\"fps\":").Append(fps).Append(",\"count\":").Append(n).Append(",\"length\":").Append(((n - 1) / (float)fps).ToString("F4", ci)).Append(",\"bones\":[")
              .Append(string.Join(",", bones.Select(b => ((int)b).ToString()))).Append("],\"pos\":[");
            float floorLift = d.baseFloorLift;                                             // ApplyBase adds this to every clip
            for (int f = 0; f < n; f++) { if (f > 0) js.Append(','); js.Append(px[f].ToString("F4", ci)).Append(',').Append((py[f] - floorLift).ToString("F4", ci)).Append(',').Append(pz[f].ToString("F4", ci)); }
            js.Append("],\"rot\":[").Append(rot).Append("]");
            // the attention eases in and out over a quarter of a second; its target is averaged with the attention as weight
            var att = Smooth(attRaw, 7f); var tx = Smooth(Enumerable.Range(0, n).Select(i => attRaw[i] * tgtRaw[i].x).ToArray(), 7f); var ty = Smooth(Enumerable.Range(0, n).Select(i => attRaw[i] * tgtRaw[i].y).ToArray(), 7f); var tz = Smooth(Enumerable.Range(0, n).Select(i => attRaw[i] * tgtRaw[i].z).ToArray(), 7f);
            var cl = Smooth(curlRaw[0], 4f); var cr = Smooth(curlRaw[1], 4f);
            js.Append(",\"att\":[").Append(string.Join(",", att.Select(x => x.ToString("F3", ci)))).Append("],\"tgt\":[")
              .Append(string.Join(",", Enumerable.Range(0, n).Select(i => att[i] > 1e-3f ? $"{(tx[i] / att[i]).ToString("F3", ci)},{(ty[i] / att[i]).ToString("F3", ci)},{(tz[i] / att[i]).ToString("F3", ci)}" : "0,1.4,0.5"))).Append("],\"curl\":[")
              .Append(string.Join(",", Enumerable.Range(0, n).Select(i => cl[i].ToString("F3", ci) + "," + cr[i].ToString("F3", ci)))).Append("],\"hf\":[")
              .Append(string.Join(",", Enumerable.Range(0, n).Select(i => { int o = i * W + 3 + 88 + 44; return v[o].ToString("F4", ci) + "," + v[o + 1].ToString("F4", ci) + "," + v[o + 2].ToString("F4", ci) + "," + v[o + 3].ToString("F4", ci); }))).Append("]}");
            System.IO.Directory.CreateDirectory("Assets/MV/Data/Clips");
            System.IO.File.WriteAllText("Assets/MV/Data/Clips/" + name + ".json", js.ToString());
            float lookShare = att.Count(x => x > 0.3f) / (float)n;
            float slideL = 0, slideR = 0;
            for (int f = 1; f < n; f++) { Vector3 dl = ankL[f] - ankL[f - 1], dr = ankR[f] - ankR[f - 1]; dl.y = 0; dr.y = 0; slideL += dl.magnitude; slideR += dr.magnitude; }
            return $"{name}: {n} frames at {fps} fps = {(n - 1) / (float)fps:F2} s; stood taller by {lift.Min() * 100:F1}..{lift.Max() * 100:F1} cm, mean knee bend {kneeBefore:F0} -> {kneeAfter:F0} deg; ankle path on the floor L {slideL * 100:F0} cm R {slideR * 100:F0} cm; looks at her hands {lookShare * 100:F0}% of the time; hand closure L {cl.Average():F2} R {cr.Average():F2}";
        }
        finally
        {
            for (int k = 0; k < bones.Count; k++) H(bones[k]).localRotation = keep[k]; hips.localPosition = keepHips;
            AssetDatabase.Refresh();
        }
    }

    /// <summary>Another character in a copy of the current scene (the scene of the first one is left as it is): the driver's settings are
    /// carried over, the rest pose is captured from the new model. Afterwards the active scene is the copy.</summary>
    public static string SwapCharacter(string vrmAsset, string sceneName, string goName)
    {
        AssetDatabase.Refresh();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(vrmAsset); if (prefab == null) return "ERR: not imported: " + vrmAsset;
        var scene = EditorSceneManager.GetActiveScene(); EditorSceneManager.SaveScene(scene);
        string path = "Assets/MV/Scenes/" + sceneName + ".unity";
        if (!EditorSceneManager.SaveScene(scene, path)) return "ERR: cannot save " + path;
        var old = Object.FindFirstObjectByType<SingDriver>(); var oldGo = old.gameObject;
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab); go.name = goName; go.transform.SetPositionAndRotation(oldGo.transform.position, oldGo.transform.rotation);
        var anim = go.GetComponent<Animator>(); var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>();
        var face = smrs.OrderByDescending(x => x.sharedMesh.blendShapeCount).First();
        var audio = go.AddComponent<AudioSource>(); EditorUtility.CopySerialized(oldGo.GetComponent<AudioSource>(), audio);
        var d = go.AddComponent<SingDriver>(); EditorUtility.CopySerialized(old, d);
        d.animator = anim; d.face = face; d.audioSource = audio;
        Object.DestroyImmediate(oldGo);
        d.CaptureRest();
        foreach (var fd in Object.FindObjectsByType<FrameDumper>(FindObjectsSortMode.None)) fd.driver = d;
        EditorUtility.SetDirty(d); EditorSceneManager.MarkSceneDirty(go.scene); EditorSceneManager.SaveScene(go.scene);
        Transform T(HumanBodyBones b) => anim.GetBoneTransform(b);
        return $"{goName} in {path}: renderers {smrs.Length}, face '{face.name}' {face.sharedMesh.blendShapeCount} shapes, missing in the track: [{d.MissingShapesFresh()}]; components: {string.Join(", ", go.GetComponents<Component>().Select(c => c.GetType().Name))}; "
             + $"hips {T(HumanBodyBones.Hips).position.y:F3} neck {T(HumanBodyBones.Neck).position.y:F3} head {T(HumanBodyBones.Head).position.y:F3} upperChest {(T(HumanBodyBones.UpperChest) != null)} shoulderHalf {Mathf.Abs(T(HumanBodyBones.LeftUpperArm).position.x):F3} "
             + $"upperArm {Vector3.Distance(T(HumanBodyBones.LeftUpperArm).position, T(HumanBodyBones.LeftLowerArm).position):F3} foreArm {Vector3.Distance(T(HumanBodyBones.LeftLowerArm).position, T(HumanBodyBones.LeftHand).position):F3} ankle {T(HumanBodyBones.LeftFoot).position.y:F3}";
    }

    public static string OpenScene(string sceneName)
    {
        var cur = EditorSceneManager.GetActiveScene(); if (cur.isDirty) EditorSceneManager.SaveScene(cur);
        var sc = EditorSceneManager.OpenScene("Assets/MV/Scenes/" + sceneName + ".unity", OpenSceneMode.Single);
        var d = Object.FindFirstObjectByType<SingDriver>();
        return "scene " + sc.path + ", character " + (d != null ? d.gameObject.name : "none");
    }

    /// <summary>The clothed body in the rest pose, sliced by height between hips and neck: how far the surface reaches to the front, to the back
    /// and to the side of the spine (arms excluded: only what lies within 22 cm of the axis). For the body proxy of the contact solver.</summary>
    public static string BodyProfile()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform;
        float hipsY = root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Hips).position).y, neckY = root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Neck).position).y;
        float axisZ = root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Hips).position).z;
        const int N = 14; var front = new float[N]; var back = new float[N]; var side = new float[N]; for (int i = 0; i < N; i++) { front[i] = -9; back[i] = 9; }
        var sb = new StringBuilder($"hipsY {hipsY:F3} neckY {neckY:F3} torso {neckY - hipsY:F3} axisZ {axisZ:F3}\n");
        foreach (var smr in d.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr == d.face || smr.name.ToLower().Contains("hair")) continue;
            var m = new Mesh(); smr.BakeMesh(m); var vs = m.vertices; var l2w = smr.transform.localToWorldMatrix;
            foreach (var v0 in vs)
            {
                Vector3 v = root.InverseTransformPoint(l2w.MultiplyPoint3x4(v0)); float u = (v.y - hipsY) / (neckY - hipsY);
                int i = Mathf.FloorToInt((u + 0.6f) / 1.7f * N); if (i < 0 || i >= N || Mathf.Abs(v.x) > 0.22f) continue;
                front[i] = Mathf.Max(front[i], v.z - axisZ); back[i] = Mathf.Min(back[i], v.z - axisZ); side[i] = Mathf.Max(side[i], Mathf.Abs(v.x));
            }
            Object.DestroyImmediate(m);
        }
        for (int i = N - 1; i >= 0; i--) sb.Append($"u {(i + 0.5f) / N * 1.7f - 0.6f:+0.00;-0.00}  front {front[i]:F3}  back {back[i]:F3}  side {side[i]:F3}\n");
        // the sleeve at the wrist: how far the cloth stands off the forearm axis near the hand
        var lo = an.GetBoneTransform(HumanBodyBones.LeftLowerArm); var ha = an.GetBoneTransform(HumanBodyBones.LeftHand); Vector3 e = lo.position, w = ha.position, ax = (w - e).normalized; float len = Vector3.Distance(e, w);
        float cuff = 0, fore = 0;
        foreach (var smr in d.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr == d.face || smr.name.ToLower().Contains("hair")) continue;
            var m = new Mesh(); smr.BakeMesh(m); var l2w = smr.transform.localToWorldMatrix;
            foreach (var v0 in m.vertices) { Vector3 v = l2w.MultiplyPoint3x4(v0); float t = Vector3.Dot(v - e, ax) / len; if (t < 0.3f || t > 1.0f) continue; float r = Vector3.Cross(v - e, ax).magnitude; if (r > 0.12f) continue; if (t > 0.8f) cuff = Mathf.Max(cuff, r); else fore = Mathf.Max(fore, r); }
            Object.DestroyImmediate(m);
        }
        sb.Append($"sleeve radius: forearm {fore:F3}, at the wrist {cuff:F3}");
        return sb.ToString();
    }

    /// <summary>Measures the clothed body in the rest pose (slices between the skirt hem / coat hem and the neck) and stores it as the body proxy of
    /// the contact solver; sets the arm proxies. yield: how much of a skirt's flare beyond the waist counts as solid (cloth gives way to a hand).
    /// cuff = 1: thick flared cuffs (jinglin); 0: bare arms / narrow sleeves.</summary>
    public static string SetBodyProfile(float yield, int cuff)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform;
        float hipsY = root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Hips).position).y, neckY = root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Neck).position).y;
        float axisZ = root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Hips).position).z;
        float shoulder = Mathf.Abs(root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.LeftUpperArm).position).x);
        const int N = 30; const float U0 = -0.95f, U1 = 1.05f; var front = new float[N]; var back = new float[N]; var side = new float[N];
        var pts = new System.Collections.Generic.List<Vector2>[N]; for (int i = 0; i < N; i++) pts[i] = new System.Collections.Generic.List<Vector2>();
        // measured in the rest (T) pose: whatever pose the editor preview left her in is put back afterwards
        var fl = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var restBone = (System.Collections.Generic.List<int>)typeof(SingDriver).GetField("restBone", fl).GetValue(d);
        var restRot = (System.Collections.Generic.List<Quaternion>)typeof(SingDriver).GetField("restRot", fl).GetValue(d);
        var keepT = restBone.Select(i => an.GetBoneTransform((HumanBodyBones)i)).ToArray(); var keepQ = keepT.Select(t => t != null ? t.localRotation : Quaternion.identity).ToArray();
        var hipsT = an.GetBoneTransform(HumanBodyBones.Hips); Vector3 keepHips = hipsT.localPosition;
        for (int i = 0; i < restBone.Count; i++) if (keepT[i] != null) keepT[i].localRotation = restRot[i];
        hipsT.localPosition = (Vector3)typeof(SingDriver).GetField("restHipsPos", fl).GetValue(d);
        hipsY = root.InverseTransformPoint(hipsT.position).y; neckY = root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Neck).position).y; axisZ = root.InverseTransformPoint(hipsT.position).z;
        shoulder = Mathf.Abs(root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.LeftUpperArm).position).x);
        foreach (var smr in d.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr == d.face || smr.name.ToLower().Contains("hair")) continue;
            var m = new Mesh(); smr.BakeMesh(m); var l2w = smr.transform.localToWorldMatrix;
            foreach (var v0 in m.vertices)
            {
                Vector3 v = root.InverseTransformPoint(l2w.MultiplyPoint3x4(v0)); float u = (v.y - hipsY) / (neckY - hipsY);
                int i = Mathf.FloorToInt((u - U0) / (U1 - U0) * N); if (i < 0 || i >= N) continue;
                if (Mathf.Abs(v.x) > (u > 0.62f ? shoulder + 0.02f : 0.6f)) continue;                 // above the armpits only what lies inside the shoulders (the arms are out in the T-pose)
                front[i] = Mathf.Max(front[i], v.z - axisZ); back[i] = Mathf.Max(back[i], axisZ - v.z); side[i] = Mathf.Max(side[i], Mathf.Abs(v.x));
                pts[i].Add(new Vector2(v.x, v.z - axisZ));
            }
            Object.DestroyImmediate(m);
        }
        for (int i = 0; i < restBone.Count; i++) if (keepT[i] != null) keepT[i].localRotation = keepQ[i];
        hipsT.localPosition = keepHips;
        var us = new float[N]; for (int i = 0; i < N; i++) us[i] = U0 + (i + 0.5f) / N * (U1 - U0);
        // the waist: the narrowest slice between hips and chest. Below it, a skirt's flare is cloth: only part of it is solid.
        int waist = 0; float wmin = 9; for (int i = 0; i < N; i++) if (us[i] > 0.05f && us[i] < 0.6f && side[i] > 0.01f && side[i] < wmin) { wmin = side[i]; waist = i; }
        float sw = side[waist], fw = front[waist], bw = back[waist];
        for (int i = 0; i < waist; i++)
        {
            if (side[i] < 0.01f) { side[i] = side[i + 1 < N ? i + 1 : i]; }
            side[i] = sw + Mathf.Max(0, side[i] - sw) * yield; front[i] = fw + Mathf.Max(0, front[i] - fw) * yield; back[i] = bw + Mathf.Max(0, back[i] - bw) * yield;
        }
        // a hem that ends inside the range: slices with nothing but legs keep the leg's own size (legs are narrow, nothing to fix)
        d.colU = us; d.colFront = front; d.colBack = back; d.colSide = side;
        {   // the outline itself: per slice (with its neighbours) the convex hull, sampled as a radius in 24 directions (0 = front, towards her right)
            const int ND = 24; var R = new float[N * ND];
            for (int i = 0; i < N; i++)
            {
                var all = new System.Collections.Generic.List<Vector2>(pts[i]); if (i > 0) all.AddRange(pts[i - 1]); if (i + 1 < N) all.AddRange(pts[i + 1]);
                if (all.Count < 3) continue;
                var p2 = all.OrderBy(q => q.x).ThenBy(q => q.y).ToArray(); int n2 = p2.Length, k = 0; var h = new Vector2[2 * n2];
                float Cr(Vector2 o, Vector2 a2, Vector2 b2) => (a2.x - o.x) * (b2.y - o.y) - (a2.y - o.y) * (b2.x - o.x);
                for (int j = 0; j < n2; j++) { while (k >= 2 && Cr(h[k - 2], h[k - 1], p2[j]) <= 0) k--; h[k++] = p2[j]; }
                for (int j = n2 - 2, t2 = k + 1; j >= 0; j--) { while (k >= t2 && Cr(h[k - 2], h[k - 1], p2[j]) <= 0) k--; h[k++] = p2[j]; }
                int hn = k - 1;
                for (int a2 = 0; a2 < ND; a2++)
                {
                    float th = a2 * (360f / ND) * Mathf.Deg2Rad; Vector2 dir = new Vector2(Mathf.Sin(th), Mathf.Cos(th)); float best = 0;      // (x, z): 0 deg = +z = front
                    for (int j = 0; j < hn; j++)
                    {
                        Vector2 A = h[j], B = h[(j + 1) % hn], E = B - A; float den = dir.x * E.y - dir.y * E.x; if (Mathf.Abs(den) < 1e-9f) continue;
                        float tt = (A.x * E.y - A.y * E.x) / den, uu = (A.x * dir.y - A.y * dir.x) / den;
                        if (tt > 0 && uu >= -1e-4f && uu <= 1.0001f) best = Mathf.Max(best, tt);
                    }
                    R[i * ND + a2] = best;
                }
            }
            d.colR = R; d.colAxisZ = axisZ;
        }
        if (cuff == 0) { d.cuffR = 0.062f; d.cuffPalm = 0f; d.cuffUlnar = 0f; d.foreR = 0.036f; d.forePalm = 0f; d.foreUlnar = 0f; }      // lin: a frill of 6 cm radius round the wrist, bare forearm
        else { d.cuffR = 0.050f; d.cuffPalm = 0.027f; d.cuffUlnar = 0.030f; d.foreR = 0.040f; d.forePalm = 0.018f; d.foreUlnar = 0.022f; }
        EditorUtility.SetDirty(d); EditorSceneManager.MarkSceneDirty(d.gameObject.scene); EditorSceneManager.SaveScene(d.gameObject.scene);
        var sb = new StringBuilder($"profile stored ({N} slices x 24 directions; cuff {d.cuffR})\n");
        for (int i = N - 1; i >= 0; i -= 2) sb.Append($"u {us[i]:+0.00;-0.00} outline front {d.colR[i * 24]:F3} right {d.colR[i * 24 + 6]:F3} back {d.colR[i * 24 + 12]:F3} left {d.colR[i * 24 + 18]:F3}\n");
        return sb.ToString();
    }

    /// <summary>How far each mouth shape really opens the lips on this model (mm at 100 %): vertical gap gained between the upper and the lower lip
    /// (measured on the vertices "A" moves most), and the lips' change of width.</summary>
    public static string MouthGeom()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var mesh = d.face.sharedMesh; int nv = mesh.vertexCount;
        var dv = new Vector3[nv]; var dn = new Vector3[nv]; var dt = new Vector3[nv]; var verts = mesh.vertices;
        int ia = mesh.GetBlendShapeIndex("Fcl_MTH_A"); if (ia < 0) return "no Fcl_MTH_A";
        mesh.GetBlendShapeFrameVertices(ia, 0, dv, dn, dt);
        var idx = Enumerable.Range(0, nv).OrderByDescending(i => dv[i].sqrMagnitude).Take(400).ToArray();
        // lower lip / jaw: moves down with A; upper lip: among the moving vertices those that go up or stay
        var low = idx.Where(i => dv[i].y < -0.002f).ToArray(); float yMid = low.Length > 0 ? low.Max(i => verts[i].y) : 0;
        var sb = new StringBuilder($"{d.gameObject.name}: face verts {nv}, lower-lip set {low.Length}\n");
        foreach (var nm in new[] { "Fcl_MTH_A", "Fcl_MTH_I", "Fcl_MTH_U", "Fcl_MTH_E", "Fcl_MTH_O", "Fcl_MTH_Close", "mth_E_open", "mth_O_tall", "mth_U_pout" })
        {
            int k = mesh.GetBlendShapeIndex(nm); if (k < 0) continue;
            var v = new Vector3[nv]; mesh.GetBlendShapeFrameVertices(k, 0, v, dn, dt);
            float drop = low.Length > 0 ? -low.Average(i => v[i].y) * 1000f : 0, maxDrop = low.Length > 0 ? -low.Min(i => v[i].y) * 1000f : 0;
            // width: the mouth corners are the moving vertices furthest out in x
            var corner = idx.OrderByDescending(i => Mathf.Abs(verts[i].x)).Take(12).ToArray(); float wide = corner.Average(i => v[i].x * Mathf.Sign(verts[i].x)) * 1000f;
            sb.Append($"{nm}: lower lip down {drop:F1} mm (max {maxDrop:F1}), corners out {wide:+0.0;-0.0} mm\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Penetration test on the real meshes (acceptance test: "the character must not clip through herself").
    /// Per frame the skinned meshes are baked. Body = everything that is not an arm (torso, head, hair, skirt, legs): cut into 2 cm slices by height,
    /// each slice's outline is the convex hull of its vertices. Every vertex of the forearms and hands (cuffs included) is tested against the
    /// outline of its slice: inside = through the body, depth = distance to the outline. Hands are also tested against the other forearm
    /// (capsule) and the other hand. Upper arms are left out on both sides (they are attached to the trunk).
    /// Writes Capture/penetration.csv (t, depth of the left / right arm in the body in mm, height of the worst point, hand-in-arm depth in mm).
    /// </summary>
    public static string PenetrationScan(float t0, float t1, float step)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform;
        var smrs = d.GetComponentsInChildren<SkinnedMeshRenderer>();
        // which humanoid bone does a skin bone belong to
        var human = new System.Collections.Generic.Dictionary<Transform, HumanBodyBones>();
        foreach (HumanBodyBones b in System.Enum.GetValues(typeof(HumanBodyBones))) { if (b == HumanBodyBones.LastBone) continue; var t = an.GetBoneTransform(b); if (t != null) human[t] = b; }
        int ClassOf(Transform t)
        {
            while (t != null && !human.ContainsKey(t)) t = t.parent;
            if (t == null) return 0; var b = human[t]; string nm = b.ToString();
            if (nm.StartsWith("LeftLowerArm") || nm.StartsWith("LeftHand") || (nm.StartsWith("Left") && (nm.Contains("Thumb") || nm.Contains("Index") || nm.Contains("Middle") || nm.Contains("Ring") || nm.Contains("Little")))) return 1;
            if (nm.StartsWith("RightLowerArm") || nm.StartsWith("RightHand") || (nm.StartsWith("Right") && (nm.Contains("Thumb") || nm.Contains("Index") || nm.Contains("Middle") || nm.Contains("Ring") || nm.Contains("Little")))) return 2;
            if (nm.Contains("UpperArm") || nm.Contains("Shoulder")) return 3;
            return 0;
        }
        var cls = new int[smrs.Length][]; var isHand = new bool[smrs.Length][];
        for (int m = 0; m < smrs.Length; m++)
        {
            var mesh = smrs[m].sharedMesh; var bw = mesh.boneWeights; var bones = smrs[m].bones; var bc = bones.Select(ClassOf).ToArray();
            var bh = bones.Select(t => { var x = t; while (x != null && !human.ContainsKey(x)) x = x.parent; return x != null && human[x] != HumanBodyBones.LeftLowerArm && human[x] != HumanBodyBones.RightLowerArm; }).ToArray();
            cls[m] = new int[mesh.vertexCount]; isHand[m] = new bool[mesh.vertexCount];
            for (int i = 0; i < bw.Length; i++)
            {
                var w = bw[i]; int bi = w.boneIndex0; float best = w.weight0;
                if (w.weight1 > best) { best = w.weight1; bi = w.boneIndex1; }
                if (w.weight2 > best) { best = w.weight2; bi = w.boneIndex2; }
                if (w.weight3 > best) { best = w.weight3; bi = w.boneIndex3; }
                cls[m][i] = bc[bi]; isHand[m][i] = bc[bi] != 0 && bc[bi] != 3 && bh[bi];
            }
        }
        const float DY = 0.02f; const int NS = 100;
        var slice = new System.Collections.Generic.List<Vector2>[NS]; for (int i = 0; i < NS; i++) slice[i] = new System.Collections.Generic.List<Vector2>();
        var hull = new Vector2[NS][]; var arm = new System.Collections.Generic.List<Vector3>[3]; arm[1] = new System.Collections.Generic.List<Vector3>(); arm[2] = new System.Collections.Generic.List<Vector3>();
        var hand = new System.Collections.Generic.List<Vector3>[3]; hand[1] = new System.Collections.Generic.List<Vector3>(); hand[2] = new System.Collections.Generic.List<Vector3>();
        Vector2[] Hull(System.Collections.Generic.List<Vector2> pts)
        {
            if (pts.Count < 3) return null; var p2 = pts.OrderBy(q => q.x).ThenBy(q => q.y).ToArray(); int n = p2.Length, k = 0; var h = new Vector2[2 * n];
            float Cr(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            for (int i = 0; i < n; i++) { while (k >= 2 && Cr(h[k - 2], h[k - 1], p2[i]) <= 0) k--; h[k++] = p2[i]; }
            for (int i = n - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cr(h[k - 2], h[k - 1], p2[i]) <= 0) k--; h[k++] = p2[i]; }
            return h.Take(k - 1).ToArray();
        }
        float Inside(Vector2[] h, Vector2 q)        // > 0: inside, the distance to the outline; <= 0: outside
        {
            if (h == null || h.Length < 3) return -1; float dmin = 9;
            for (int i = 0; i < h.Length; i++)
            {
                Vector2 a = h[i], b = h[(i + 1) % h.Length], e = b - a; float cr = e.x * (q.y - a.y) - e.y * (q.x - a.x); if (cr < 0) return -1;
                dmin = Mathf.Min(dmin, cr / Mathf.Max(1e-6f, e.magnitude));
            }
            return dmin;
        }
        float SegDist(Vector3 q, Vector3 a, Vector3 b) { Vector3 ab = b - a; float u = Mathf.Clamp01(Vector3.Dot(q - a, ab) / ab.sqrMagnitude); return Vector3.Distance(q, a + ab * u); }
        var sb = new StringBuilder("t,bodyL_mm,bodyR_mm,worstY,nInside,armsL_mm,armsR_mm,along,wx,wz,micL_mm,micR_mm,micAlong_cm,micRadial_cm\n"); var ci = System.Globalization.CultureInfo.InvariantCulture;
        int frames = 0, bad5 = 0, bad10 = 0, badArms = 0; float worst = 0, worstT = 0, worstYAll = 0, worstArms = 0, worstArmsT = 0; int badMic = 0; float worstMic = 0, worstMicT = 0; var bakeMesh = new Mesh();
        var worstList = new System.Collections.Generic.List<(float depth, float t, float y, int side)>();
        for (float t = t0; t <= t1 + 1e-4f; t += step)
        {
            d.Apply(t);
            for (int i = 0; i < NS; i++) { slice[i].Clear(); hull[i] = null; }
            arm[1].Clear(); arm[2].Clear(); hand[1].Clear(); hand[2].Clear();
            for (int m = 0; m < smrs.Length; m++)
            {
                smrs[m].BakeMesh(bakeMesh); var vs = bakeMesh.vertices; var l2w = smrs[m].transform.localToWorldMatrix;
                for (int i = 0; i < vs.Length; i++)
                {
                    int c = cls[m][i]; if (c == 3) continue;
                    Vector3 v = root.InverseTransformPoint(l2w.MultiplyPoint3x4(vs[i]));
                    if (c == 0) { int si = Mathf.FloorToInt(v.y / DY); if (si >= 0 && si < NS) slice[si].Add(new Vector2(v.x, v.z)); }
                    else { arm[c].Add(v); if (isHand[m][i]) hand[c].Add(v); }
                }
            }
            float[] dep = { 0, 0, 0 }; float wy = 0, wAlong = 0; int nIn = 0; Vector3 wv = Vector3.zero;
            Vector3[] elb = { Vector3.zero, root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.LeftLowerArm).position), root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.RightLowerArm).position) };
            Vector3[] wri = { Vector3.zero, root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.LeftHand).position), root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.RightHand).position) };
            for (int c = 1; c <= 2; c++)
                foreach (var v in arm[c])
                {
                    int si = Mathf.FloorToInt(v.y / DY); if (si < 1 || si >= NS - 1) continue;
                    if (hull[si] == null) { var pts = new System.Collections.Generic.List<Vector2>(slice[si - 1]); pts.AddRange(slice[si]); pts.AddRange(slice[si + 1]); hull[si] = Hull(pts) ?? new Vector2[0]; }
                    float ins = Inside(hull[si], new Vector2(v.x, v.z));
                    if (ins > 0.004f) nIn++;
                    if (ins > dep[c]) { dep[c] = ins; if (ins >= Mathf.Max(dep[1], dep[2])) { wy = v.y; wv = v; wAlong = Vector3.Dot(v - elb[c], wri[c] - elb[c]) / (wri[c] - elb[c]).sqrMagnitude; } }
                }
            // a hand in the other forearm (capsule 2.4 cm) or in the other hand's palm (1.2 cm)
            float[] ad = { 0, 0, 0 };
            for (int c = 1; c <= 2; c++)
            {
                bool left = c == 1;           // hand of side c against the arm of the other side
                Vector3 e = root.InverseTransformPoint(an.GetBoneTransform(left ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm).position), w = root.InverseTransformPoint(an.GetBoneTransform(left ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand).position);
                Vector3 mdl = root.InverseTransformPoint(an.GetBoneTransform(left ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal).position);
                foreach (var v in hand[c]) { ad[c] = Mathf.Max(ad[c], 0.024f - SegDist(v, e, w)); ad[c] = Mathf.Max(ad[c], 0.012f - SegDist(v, w, mdl)); }
            }
            // hands and forearms in the microphone or its stand (not the hand that holds it)
            float[] md = { 0, 0, 0 }; float micAl = 0, micRd = 0;
            if (d.micOn)
            {
                // every vertex of both arms - the hand that HOLDS the microphone too (it was left out, and its fingers were inside the clip) -
                // against what the microphone really is: grille, collar, tapering body, clip sleeve, swivel ball, pole
                var rigM = Object.FindFirstObjectByType<MicStandRig>(FindObjectsInactive.Include);
                float clipA = rigM != null ? rigM.clipAlong : d.micClip, bodyL = rigM != null ? rigM.bodyLength : d.micLen;
                Vector3 g0 = d.micPos, ax = d.micDir.normalized, pt = d.MicPolePoint, pb = new Vector3(pt.x, 0.02f, pt.z);
                for (int c = 1; c <= 2; c++)
                    foreach (var v in arm[c])
                    {
                        float al = Vector3.Dot(v - g0, ax), rd = Vector3.Cross(v - g0, ax).magnitude, dep2 = 0.027f - Vector3.Distance(v, g0);
                        if (al > 0.0185f && al < bodyL) { float rr = al < 0.032f ? 0.024f : Mathf.Abs(al - clipA) < 0.024f ? 0.0245f : Mathf.Lerp(0.0205f, 0.0148f, Mathf.InverseLerp(0.032f, bodyL - 0.018f, al)); dep2 = Mathf.Max(dep2, rr - rd); }
                        dep2 = Mathf.Max(dep2, 0.013f - Vector3.Distance(v, pt + Vector3.down * 0.020f));
                        dep2 = Mathf.Max(dep2, 0.0125f - SegDist(v, pt + Vector3.down * 0.046f, pb));
                        if (dep2 > md[c]) { md[c] = dep2; if (dep2 >= Mathf.Max(md[1], md[2])) { micAl = al; micRd = rd; } }
                    }
            }
            float mmax = Mathf.Max(md[1], md[2]); if (mmax > 0.005f) badMic++; if (mmax > worstMic) { worstMic = mmax; worstMicT = t; }
            float dmax = Mathf.Max(dep[1], dep[2]), amax = Mathf.Max(ad[1], ad[2]); frames++;
            if (dmax > 0.005f) bad5++; if (dmax > 0.010f) bad10++; if (amax > 0.005f) badArms++;
            if (dmax > worst) { worst = dmax; worstT = t; worstYAll = wy; } if (amax > worstArms) { worstArms = amax; worstArmsT = t; }
            if (dmax > 0.005f) worstList.Add((dmax, t, wy, dep[1] >= dep[2] ? 1 : 2));
            sb.Append(t.ToString("F2", ci)).Append(',').Append((dep[1] * 1000).ToString("F1", ci)).Append(',').Append((dep[2] * 1000).ToString("F1", ci)).Append(',').Append(wy.ToString("F2", ci)).Append(',').Append(nIn).Append(',')
              .Append((ad[1] * 1000).ToString("F1", ci)).Append(',').Append((ad[2] * 1000).ToString("F1", ci)).Append(',').Append(wAlong.ToString("F2", ci)).Append(',').Append(wv.x.ToString("F3", ci)).Append(',').Append(wv.z.ToString("F3", ci)).Append(',').Append((md[1] * 1000).ToString("F1", ci)).Append(',').Append((md[2] * 1000).ToString("F1", ci)).Append(',').Append((micAl * 100).ToString("F1", ci)).Append(',').Append((micRd * 100).ToString("F1", ci)).Append('\n');
        }
        Object.DestroyImmediate(bakeMesh);
        System.IO.File.WriteAllText("Capture/penetration.csv", sb.ToString());
        var top = worstList.OrderByDescending(x => x.depth).Take(6).Select(x => $"t {x.t:F1} {(x.side == 1 ? "L" : "R")} {x.depth * 1000:F0} mm at height {x.y:F2}");
        return $"{frames} frames {t0:F0}-{t1:F0} s: arm in body deeper than 5 mm in {bad5} frames ({100f * bad5 / frames:F1}%), deeper than 10 mm in {bad10}; worst {worst * 1000:F0} mm at t {worstT:F1} (height {worstYAll:F2}). "
             + $"Hand in the other arm / hand deeper than 5 mm in {badArms} frames, worst {worstArms * 1000:F0} mm at t {worstArmsT:F1}. Arm in the microphone / stand deeper than 5 mm in {badMic} frames, worst {worstMic * 1000:F0} mm at t {worstMicT:F1}. Worst: " + string.Join("; ", top);
    }

    /// <summary>How thick the left arm really is (rest pose): radius about the elbow-wrist axis in five stretches from the elbow to the wrist, and
    /// about the wrist - middle-finger axis for the hand. Stores them as the arm proxies of the contact solver (armRad).</summary>
    public static string SetArmRadii()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator;
        var fl = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var restBone = (System.Collections.Generic.List<int>)typeof(SingDriver).GetField("restBone", fl).GetValue(d);
        var restRot = (System.Collections.Generic.List<Quaternion>)typeof(SingDriver).GetField("restRot", fl).GetValue(d);
        var keepT = restBone.Select(i => an.GetBoneTransform((HumanBodyBones)i)).ToArray(); var keepQ = keepT.Select(t => t != null ? t.localRotation : Quaternion.identity).ToArray();
        for (int i = 0; i < restBone.Count; i++) if (keepT[i] != null) keepT[i].localRotation = restRot[i];
        Vector3 e = an.GetBoneTransform(HumanBodyBones.LeftLowerArm).position, w = an.GetBoneTransform(HumanBodyBones.LeftHand).position, mdl = an.GetBoneTransform(HumanBodyBones.LeftMiddleDistal).position;
        Vector3 ax = (w - e).normalized; float len = Vector3.Distance(e, w); Vector3 hx = (mdl - w).normalized; float hlen = Vector3.Distance(w, mdl);
        var rad = new float[5]; float handR = 0, tip = 0;
        var human = new System.Collections.Generic.HashSet<Transform>(); foreach (HumanBodyBones b in System.Enum.GetValues(typeof(HumanBodyBones))) { if (b == HumanBodyBones.LastBone) continue; var t = an.GetBoneTransform(b); if (t != null) human.Add(t); }
        var lower = an.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        foreach (var smr in d.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr == d.face) continue; var mesh = smr.sharedMesh; var bw = mesh.boneWeights; var bones = smr.bones;
            var under = bones.Select(t => { var x = t; while (x != null && x != lower) x = x.parent; return x == lower; }).ToArray();        // skinned to the left forearm, hand or fingers
            var m = new Mesh(); smr.BakeMesh(m); var vs = m.vertices; var l2w = smr.transform.localToWorldMatrix;
            for (int i = 0; i < vs.Length; i++)
            {
                var q = bw[i]; int bi = q.boneIndex0; float best = q.weight0; if (q.weight1 > best) { best = q.weight1; bi = q.boneIndex1; } if (q.weight2 > best) { best = q.weight2; bi = q.boneIndex2; } if (q.weight3 > best) { bi = q.boneIndex3; }
                if (!under[bi]) continue;
                Vector3 v = l2w.MultiplyPoint3x4(vs[i]); float t = Vector3.Dot(v - e, ax) / len;
                if (t <= 1.0f) { int k = Mathf.Clamp(Mathf.FloorToInt(t * 5f), 0, 4); rad[k] = Mathf.Max(rad[k], Vector3.Cross(v - e, ax).magnitude); }
                float th = Vector3.Dot(v - w, hx) / hlen;
                if (th > 0.05f) { handR = Mathf.Max(handR, Vector3.Cross(v - w, hx).magnitude); tip = Mathf.Max(tip, th * hlen); }
            }
            Object.DestroyImmediate(m);
        }
        for (int i = 0; i < restBone.Count; i++) if (keepT[i] != null) keepT[i].localRotation = keepQ[i];
        d.armRad = new[] { rad[0], rad[1], rad[2], rad[3], rad[4], handR, tip };
        EditorUtility.SetDirty(d); EditorSceneManager.MarkSceneDirty(d.gameObject.scene); EditorSceneManager.SaveScene(d.gameObject.scene);
        return $"forearm radius elbow->wrist (cm): {string.Join(" / ", rad.Select(x => (x * 100).ToString("F1")))}; hand half-width {handR * 100:F1} cm, wrist to fingertip {tip * 100:F1} cm; forearm {len * 100:F1} cm";
    }

    /// <summary>For every recorded (b2_) clip of the current schedule: pose the character through the clip as it will play (arms solved against the
    /// body), find where her hands are and how much they call for her eyes - together and raised in front of her: the space between them; one
    /// hand raised in front: that hand; never hands that touch her own body - ease it over a quarter of a second and write it into the clip
    /// (att, tgt). Call after SetBaseFromFile; reloads the clips.</summary>
    public static string BakeLook()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform; var sb = new StringBuilder();
        var lh = an.GetBoneTransform(HumanBodyBones.LeftHand); var rh = an.GetBoneTransform(HumanBodyBones.RightHand); var lu = an.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        var ch = an.GetBoneTransform(HumanBodyBones.UpperChest) ?? an.GetBoneTransform(HumanBodyBones.Chest);
        float S(float e0, float e1, float x) { x = Mathf.Clamp01((x - e0) / (e1 - e0)); return x * x * (3f - 2f * x); }
        for (int i = 0; i < d.baseClip.Length; i++)
        {
            string name = d.baseClip[i]; if (!name.StartsWith("b2_")) continue;
            string path = "Assets/MV/Data/Clips/" + name + ".json"; var c = JsonUtility.FromJson<ClipBake>(System.IO.File.ReadAllText(path)); int n = c.count;
            float off = d.baseOff != null && i < d.baseOff.Length ? d.baseOff[i] : 0f, speed = d.baseSpeed[i];
            var att = new float[n]; var tg = new Vector3[n]; var valid = new bool[n];
            for (int f = 0; f < n; f++)
            {
                float t = d.baseStart[i] + (f / (float)c.fps - off) / speed;
                if (t < d.baseStart[i] + 0.05f || t > d.baseStart[i] + d.baseDur[i] - 0.05f) continue;
                d.Apply(t); valid[f] = true;
                Vector3 PL = root.InverseTransformPoint(lh.position + lh.rotation * Vector3.left * 0.07f), PR = root.InverseTransformPoint(rh.position + rh.rotation * Vector3.right * 0.07f);
                Vector3 cc = root.InverseTransformPoint(ch.position), mid = (PL + PR) * 0.5f; float shY = root.InverseTransformPoint(lu.position).y, dist = Vector3.Distance(PL, PR);
                float wa = (1f - S(0.15f, 0.30f, dist)) * S(cc.y - 0.10f, cc.y + 0.10f, mid.y) * Mathf.Lerp(0.42f, 1f, S(cc.y + 0.02f, cc.y + 0.22f, mid.y)) * S(0.14f, 0.24f, mid.z - cc.z);
                float wL = 0.7f * S(shY - 0.02f, shY + 0.15f, PL.y) * S(0.14f, 0.24f, PL.z - cc.z), wR = 0.7f * S(shY - 0.02f, shY + 0.15f, PR.y) * S(0.14f, 0.24f, PR.z - cc.z);
                float ws = wa + wL + wR; att[f] = Mathf.Max(wa, Mathf.Max(wL, wR)); tg[f] = ws > 1e-4f ? (wa * mid + wL * PL + wR * PR) / ws : mid;
            }
            int first = System.Array.IndexOf(valid, true), last = System.Array.LastIndexOf(valid, true); if (first < 0) continue;
            for (int f = 0; f < n; f++) if (!valid[f]) { int k = f < first ? first : last; att[f] = att[k]; tg[f] = tg[k]; }
            float[] Sm(float[] x) { var o = new float[n]; for (int f = 0; f < n; f++) { float a = 0, w = 0; for (int k = -21; k <= 21; k++) { int j = Mathf.Clamp(f + k, 0, n - 1); float g = Mathf.Exp(-k * k / 98f); a += x[j] * g; w += g; } o[f] = a / w; } return o; }
            var sa = Sm(att); var sx = Sm(Enumerable.Range(0, n).Select(k => att[k] * tg[k].x).ToArray()); var sy = Sm(Enumerable.Range(0, n).Select(k => att[k] * tg[k].y).ToArray()); var sz = Sm(Enumerable.Range(0, n).Select(k => att[k] * tg[k].z).ToArray());
            c.att = sa; c.tgt = new float[n * 3];
            for (int f = 0; f < n; f++) { bool ok = sa[f] > 1e-3f; c.tgt[f * 3] = ok ? sx[f] / sa[f] : 0f; c.tgt[f * 3 + 1] = ok ? sy[f] / sa[f] : 1.4f; c.tgt[f * 3 + 2] = ok ? sz[f] / sa[f] : 0.5f; }
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(c));
            sb.Append($"{name}: looks at her hands {100f * sa.Count(x => x > 0.3f) / n:F0}% of the time (max weight {sa.Max():F2}); ");
        }
        AssetDatabase.Refresh();
        return sb.ToString() + SetBaseFromFile();
    }

    /// <summary>AgentScripts/look.txt -> the driver: "look t kind" (0 microphone, 1 lens, 2 audience left, 3 audience right, 4 far over the audience,
    /// 5 down to her side) and "hold t0 t1" (the right hand is on the microphone; the gesture schedule has 'mic' there).</summary>
    public static string SetLookFromFile(string path)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var ci = System.Globalization.CultureInfo.InvariantCulture;
        var lt = new System.Collections.Generic.List<float>(); var lk = new System.Collections.Generic.List<int>(); var h0 = new System.Collections.Generic.List<float>(); var h1 = new System.Collections.Generic.List<float>();
        var o0 = new System.Collections.Generic.List<float>(); var o1 = new System.Collections.Generic.List<float>(); var os = new System.Collections.Generic.List<int>(); var og = new System.Collections.Generic.List<int>();
        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            var q = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries); if (q.Length < 3 || q[0].StartsWith("#")) continue;
            if (q[0] == "look") { lt.Add(float.Parse(q[1], ci)); lk.Add(int.Parse(q[2], ci)); }
            else if (q[0] == "hold") { h0.Add(float.Parse(q[1], ci)); h1.Add(float.Parse(q[2], ci)); }
            else if (q[0] == "own" && q.Length >= 4) { o0.Add(float.Parse(q[1], ci)); o1.Add(float.Parse(q[2], ci)); os.Add(int.Parse(q[3], ci)); og.Add(q.Length > 4 ? int.Parse(q[4], ci) : 0); }
        }
        d.lookT = lt.ToArray(); d.lookKind = lk.ToArray(); d.holdT0 = h0.ToArray(); d.holdT1 = h1.ToArray(); d.ownT0 = o0.ToArray(); d.ownT1 = o1.ToArray(); d.ownSide = os.ToArray(); d.ownGrip = og.ToArray(); d.InvalidateBake(); EditorUtility.SetDirty(d);
        float end = lt.Count > 0 ? lt[lt.Count - 1] : 0, mic = 0; for (int i = 0; i + 1 < lt.Count; i++) if (lk[i] == 0) mic += lt[i + 1] - lt[i];
        return $"look keys {lt.Count}, on the microphone {mic:F1} s of {(lt.Count > 1 ? end - lt[0] : 0):F1} s; holds {h0.Count}, authored gestures {o0.Count}";
    }

    /// <summary>After a capture: the player back to live playback (what Unity's own Play button and the MV player window run), scene saved.</summary>
    public static string LiveDefault(float start, float duration, string showFile)
    {
        var fd = Object.FindFirstObjectByType<FrameDumper>(); var d = Object.FindFirstObjectByType<SingDriver>();
        fd.live = true; fd.liveLoop = true; fd.stage = true; fd.startTime = start; fd.duration = duration; fd.showFile = showFile; fd.width = 1920; fd.height = 1080; fd.fps = 30; d.mouth = 1f; d.usePreviewTime = false;
        if (fd.cam != null) fd.cam.targetTexture = null;
        EditorUtility.SetDirty(fd); EditorUtility.SetDirty(d); EditorSceneManager.MarkSceneDirty(fd.gameObject.scene); EditorSceneManager.SaveScene(fd.gameObject.scene);
        return $"live player: {start:F0}-{start + duration:F0} s, {showFile}; scene saved {fd.gameObject.scene.path}";
    }

    /// <summary>Close-ups of the grip on the microphone at the given times ("33.1 33.3 ..."), three views side by side (front-right, her right side,
    /// front-left from below), 640 px each -> Capture/grip_shots/g_<t>.png. Edit mode, no play needed: for looking at the hand at full size.</summary>
    public static string GripShots(string times)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var root = d.transform;
        var go = new GameObject("GripShotCam"); go.hideFlags = HideFlags.HideAndDontSave; var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 24f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 50f; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.25f, 0.27f, 0.32f);
        const int S = 640; var rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); var tex = new Texture2D(S * 3, S, TextureFormat.RGB24, false);
        System.IO.Directory.CreateDirectory("Capture/grip_shots"); int n = 0;
        var smrs = d.GetComponentsInChildren<SkinnedMeshRenderer>().Where(x => x.enabled).ToArray(); var temp = new System.Collections.Generic.List<GameObject>();
        bool facem = times.StartsWith("f"); if (facem) times = times.Substring(1);      // "f30 31 ...": the face, from the front and both three-quarter sides
        bool med = times.StartsWith("m"); if (med) times = times.Substring(1);        // "m30 31 ...": waist up, from the front and both sides
        bool wide = times.StartsWith("w"); if (wide) times = times.Substring(1);      // "w30 31 ...": the whole figure from the front and both sides
        bool pole = times.StartsWith("p"); if (pole) times = times.Substring(1);      // "p36.0 ...": the hand on the stand instead (left side views)
        Vector3[] offs = pole ? new[] { new Vector3(-0.25f, 0.12f, 0.52f), new Vector3(-0.62f, 0.04f, 0.10f), new Vector3(0.34f, 0.06f, 0.46f) } : new[] { new Vector3(0.22f, 0.10f, 0.50f), new Vector3(0.60f, 0.02f, 0.02f), new Vector3(-0.32f, -0.06f, 0.44f) };
        if (wide) { offs = new[] { new Vector3(0f, 0.05f, 2.5f), new Vector3(1.7f, 0.05f, 1.9f), new Vector3(-1.7f, 0.05f, 1.9f) }; cam.fieldOfView = 36f; }
        if (facem) { offs = new[] { new Vector3(0.04f, 0.02f, 0.62f), new Vector3(0.46f, 0.03f, 0.42f), new Vector3(-0.46f, 0.03f, 0.42f) }; cam.fieldOfView = 30f; }
        if (med) { offs = new[] { new Vector3(0.12f, 0.06f, 1.45f), new Vector3(1.15f, 0.10f, 0.95f), new Vector3(-1.15f, 0.10f, 0.95f) }; cam.fieldOfView = 36f; }
        foreach (var s in times.Split(' '))
        {
            if (s.Length == 0) continue; float t = float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); d.Apply(t);
            // (in edit mode an off-screen camera draws the skinned meshes as they were last skinned for the editor's own windows: the pose is baked
            // into plain meshes for the picture)
            foreach (var g0 in temp) Object.DestroyImmediate(g0); temp.Clear();
            foreach (var smr in smrs)
            {
                var bm = new Mesh { hideFlags = HideFlags.HideAndDontSave }; smr.BakeMesh(bm, true);
                var tg = new GameObject("baked") { hideFlags = HideFlags.HideAndDontSave }; tg.transform.SetParent(smr.transform, false);
                tg.AddComponent<MeshFilter>().sharedMesh = bm; tg.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials; temp.Add(tg); smr.enabled = false;
            }
            Vector3 g = root.TransformPoint(facem ? new Vector3(0f, 1.47f, 0.06f) : med ? new Vector3(0f, 1.20f, 0.10f) : wide ? new Vector3(0f, 1.02f, 0.05f) : pole ? d.MicPolePoint - new Vector3(0f, 0.18f, 0f) : d.micPos + d.micDir.normalized * d.micGrip);
            for (int v = 0; v < 3; v++)
            {
                cam.transform.position = g + root.TransformVector(offs[v]); cam.transform.LookAt(g);
                cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, S, S), v * S, 0);
            }
            tex.Apply(); RenderTexture.active = null;
            System.IO.File.WriteAllBytes("Capture/grip_shots/g_" + t.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ".png", tex.EncodeToPNG()); n++;
        }
        foreach (var g0 in temp) Object.DestroyImmediate(g0); foreach (var smr in smrs) smr.enabled = true;
        cam.targetTexture = null; Object.DestroyImmediate(go); rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
        return n + " shots -> Capture/grip_shots";
    }

    /// <summary>Which part of the holding (right) hand is inside the microphone at time t: per bone, how many vertices lie within the body radius and how close to the axis.</summary>
    public static string GripDebug(float t)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform; d.Apply(t);
        var hand = an.GetBoneTransform(HumanBodyBones.RightHand); Vector3 g0 = d.micPos, ax = d.micDir.normalized; var sb = new StringBuilder();
        var stat = new System.Collections.Generic.Dictionary<string, (int n, int inside, float minRd, float al)>(); var bm = new Mesh();
        foreach (var smr in d.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr == d.face) continue; var bw = smr.sharedMesh.boneWeights; var bones = smr.bones; smr.BakeMesh(bm); var vs = bm.vertices; var l2w = smr.transform.localToWorldMatrix;
            for (int i = 0; i < vs.Length; i++)
            {
                var w = bw[i]; int bi = w.boneIndex0; float best = w.weight0; if (w.weight1 > best) { best = w.weight1; bi = w.boneIndex1; } if (w.weight2 > best) { best = w.weight2; bi = w.boneIndex2; } if (w.weight3 > best) bi = w.boneIndex3;
                var b = bones[bi]; if (b != hand && !b.IsChildOf(hand)) continue;
                Vector3 v = root.InverseTransformPoint(l2w.MultiplyPoint3x4(vs[i])); float al = Vector3.Dot(v - g0, ax), rd = Vector3.Cross(v - g0, ax).magnitude;
                stat.TryGetValue(b.name, out var st); st.n++; if (rd < 0.0185f && al > 0.03f && al < d.micLen) { st.inside++; if (st.minRd == 0 || rd < st.minRd) { st.minRd = rd; st.al = al; } } stat[b.name] = st;
            }
        }
        Object.DestroyImmediate(bm);
        Vector3 hw = root.InverseTransformPoint(hand.position); sb.Append($"wrist {hw:F3}: {Vector3.Cross(hw - g0, ax).magnitude * 100:F1} cm from the axis, {Vector3.Dot(hw - g0, ax) * 100:F1} cm along; palm faces {(Quaternion.Inverse(root.rotation) * hand.rotation * Vector3.down):F2}, fingers {(Quaternion.Inverse(root.rotation) * hand.rotation * Vector3.right):F2}\n");
        foreach (var kv in stat.OrderByDescending(k => k.Value.inside)) sb.Append($"{kv.Key}: {kv.Value.n} vertices, {kv.Value.inside} inside the body" + (kv.Value.inside > 0 ? $" (closest {kv.Value.minRd * 100:F1} cm from the axis at {kv.Value.al * 100:F1} cm along)" : "") + "\n");
        System.IO.File.WriteAllText("Capture/grip_dbg.txt", sb.ToString()); return "ok";
    }

    /// <summary>Fits the grip on the microphone to this hand and this microphone, on the real meshes: at time t (the right hand holds the microphone)
    /// each finger is closed as far as it goes without a vertex of it inside the body (bisection, finger by finger), the thumb likewise.
    /// Stores the result in the driver (micCurl).</summary>
    public static string TuneGrip(float t)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform;
        var hand = an.GetBoneTransform(HumanBodyBones.RightHand); Vector3 g0 = d.micPos, ax = d.micDir.normalized;
        var rigM = Object.FindFirstObjectByType<MicStandRig>(FindObjectsInactive.Include); float clipA = rigM != null ? rigM.clipAlong : d.micClip, bodyL = rigM != null ? rigM.bodyLength : d.micLen;
        HumanBodyBones[] first = { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal };
        var fingerOf = new System.Collections.Generic.Dictionary<Transform, int>();
        for (int f = 0; f < 5; f++) { var b0 = an.GetBoneTransform(first[f]); foreach (var tr in b0.GetComponentsInChildren<Transform>()) fingerOf[tr] = f; }
        var smrs = d.GetComponentsInChildren<SkinnedMeshRenderer>().Where(x => x != d.face).ToArray(); var vf = new int[smrs.Length][];
        for (int m = 0; m < smrs.Length; m++)
        {
            var bw = smrs[m].sharedMesh.boneWeights; var bones = smrs[m].bones; vf[m] = new int[bw.Length];
            for (int i = 0; i < bw.Length; i++)
            {
                var w = bw[i]; int bi = w.boneIndex0; float best = w.weight0; if (w.weight1 > best) { best = w.weight1; bi = w.boneIndex1; } if (w.weight2 > best) { best = w.weight2; bi = w.boneIndex2; } if (w.weight3 > best) bi = w.boneIndex3;
                vf[m][i] = fingerOf.TryGetValue(bones[bi], out int fi) ? fi : -1;
            }
        }
        var bm = new Mesh();
        float[] Depth()            // per finger: how deep its deepest vertex is inside the microphone (m)
        {
            d.Apply(t); var dep = new float[5];
            for (int m = 0; m < smrs.Length; m++)
            {
                smrs[m].BakeMesh(bm); var vs = bm.vertices; var l2w = smrs[m].transform.localToWorldMatrix;
                for (int i = 0; i < vs.Length; i++)
                {
                    int fi = vf[m][i]; if (fi < 0) continue; Vector3 v = root.InverseTransformPoint(l2w.MultiplyPoint3x4(vs[i]));
                    float al = Vector3.Dot(v - g0, ax), rd = Vector3.Cross(v - g0, ax).magnitude, dp = 0.027f - Vector3.Distance(v, g0);
                    if (al > 0.0185f && al < bodyL) { float rr = al < 0.032f ? 0.024f : Mathf.Abs(al - clipA) < 0.024f ? 0.0245f : Mathf.Lerp(0.0205f, 0.0148f, Mathf.InverseLerp(0.032f, bodyL - 0.018f, al)); dp = Mathf.Max(dp, rr - rd); }
                    dep[fi] = Mathf.Max(dep[fi], dp);
                }
            }
            return dep;
        }
        var cur = new float[] { 0f, 0f, 0f, 0f, 0f }; d.micCurl = cur; var open = Depth();
        var lo = new float[] { -1.2f, 0f, 0f, 0f, 0f }; var hi = new float[] { 0.60f, 0.80f, 0.80f, 0.80f, 0.80f };      // the thumb may open outwards (negative): at rest it lies lower than the palm and would sit inside the body
        for (int f = 0; f < 5; f++) cur[f] = lo[f]; var widest = Depth();
        for (int it = 0; it < 7; it++)
        {
            for (int f = 0; f < 5; f++) cur[f] = 0.5f * (lo[f] + hi[f]);
            var dep = Depth();
            for (int f = 0; f < 5; f++) { if (dep[f] > 0.0005f) hi[f] = cur[f]; else lo[f] = cur[f]; }      // (the fingers do not reach each other: all five are searched at once)
        }
        for (int f = 0; f < 5; f++) cur[f] = lo[f];
        var fin = Depth(); Object.DestroyImmediate(bm);
        d.micCurl = cur; d.InvalidateBake(); EditorUtility.SetDirty(d); EditorSceneManager.MarkSceneDirty(d.gameObject.scene); EditorSceneManager.SaveScene(d.gameObject.scene);
        return "grip fitted at t " + t + ": closure thumb/index/middle/ring/little " + string.Join(" / ", cur.Select(x => x.ToString("F2"))) + "; deepest vertex inside the microphone per finger (mm), open hand: " + string.Join(" / ", open.Select(x => (x * 1000).ToString("F1"))) + ", fitted: " + string.Join(" / ", fin.Select(x => (x * 1000).ToString("F1"))) + "; thumb fully opened: " + (widest[0] * 1000).ToString("F1");
    }

    /// <summary>Fits the grip on the STAND (left hand, at time t while it holds the pole): each finger closed as far as it goes without a vertex inside
    /// the tube (radius as the penetration scan takes it). Stores poleCurl; also says how deep the palm itself sits in the tube.</summary>
    public static string TunePole(float t)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform;
        var hand = an.GetBoneTransform(HumanBodyBones.LeftHand); Vector3 pt = d.MicPolePoint, pa = pt + Vector3.down * 0.046f, pb = new Vector3(pt.x, 0.02f, pt.z);
        HumanBodyBones[] first = { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal };
        var fingerOf = new System.Collections.Generic.Dictionary<Transform, int>();
        for (int f = 0; f < 5; f++) { var b0 = an.GetBoneTransform(first[f]); foreach (var tr in b0.GetComponentsInChildren<Transform>()) fingerOf[tr] = f; }
        var smrs = d.GetComponentsInChildren<SkinnedMeshRenderer>().Where(x => x != d.face).ToArray(); var vf = new int[smrs.Length][];
        for (int m = 0; m < smrs.Length; m++)
        {
            var bw = smrs[m].sharedMesh.boneWeights; var bones = smrs[m].bones; vf[m] = new int[bw.Length];
            for (int i = 0; i < bw.Length; i++)
            {
                var w = bw[i]; int bi = w.boneIndex0; float best = w.weight0; if (w.weight1 > best) { best = w.weight1; bi = w.boneIndex1; } if (w.weight2 > best) { best = w.weight2; bi = w.boneIndex2; } if (w.weight3 > best) bi = w.boneIndex3;
                vf[m][i] = fingerOf.TryGetValue(bones[bi], out int fi) ? fi : bones[bi] == hand ? 5 : -1;
            }
        }
        var bm = new Mesh();
        float SegD(Vector3 p, Vector3 a, Vector3 b) { Vector3 ab = b - a; float u = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude); return Vector3.Distance(p, a + ab * u); }
        float[] Depth()
        {
            d.InvalidateBake(); d.Apply(t); var dep = new float[6];
            for (int m = 0; m < smrs.Length; m++)
            {
                smrs[m].BakeMesh(bm); var vs = bm.vertices; var l2w = smrs[m].transform.localToWorldMatrix;
                for (int i = 0; i < vs.Length; i++)
                {
                    int fi = vf[m][i]; if (fi < 0) continue; Vector3 v = root.InverseTransformPoint(l2w.MultiplyPoint3x4(vs[i]));
                    dep[fi] = Mathf.Max(dep[fi], 0.0125f - SegD(v, pa, pb));
                }
            }
            return dep;
        }
        var cur = new float[] { 0f, 0f, 0f, 0f, 0f }; d.poleCurl = cur; var open = Depth();
        var lo = new float[] { -0.3f, 0f, 0f, 0f, 0f }; var hi = new float[] { 0.60f, 0.95f, 0.95f, 0.95f, 0.95f };
        for (int it = 0; it < 7; it++)
        {
            for (int f = 0; f < 5; f++) cur[f] = 0.5f * (lo[f] + hi[f]);
            var dep = Depth();
            for (int f = 0; f < 5; f++) { if (dep[f] > 0.0005f) hi[f] = cur[f]; else lo[f] = cur[f]; }
        }
        for (int f = 0; f < 5; f++) cur[f] = lo[f];
        var fin = Depth(); Object.DestroyImmediate(bm);
        d.poleCurl = cur; d.InvalidateBake(); EditorUtility.SetDirty(d); EditorSceneManager.MarkSceneDirty(d.gameObject.scene); EditorSceneManager.SaveScene(d.gameObject.scene);
        return "pole grip fitted at t " + t + ": closure " + string.Join(" / ", cur.Select(x => x.ToString("F2"))) + "; deepest vertex in the tube (mm) thumb..little, palm - open hand: " + string.Join(" / ", open.Select(x => (x * 1000).ToString("F1"))) + ", fitted: " + string.Join(" / ", fin.Select(x => (x * 1000).ToString("F1")));
    }

    /// <summary>Hand-keyed animation: play this control table (AgentScripts/keyanim.py) - or, with on = 0, go back to the gesture system / the takes.</summary>
    public static string KeyClip(string path, int on)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); d.keyClipPath = path; d.keyOn = on != 0; d.ReloadKeyClip(); d.InvalidateBake();
        EditorUtility.SetDirty(d); if (!EditorApplication.isPlaying) { EditorSceneManager.MarkSceneDirty(d.gameObject.scene); EditorSceneManager.SaveScene(d.gameObject.scene); }
        d.Apply(d.previewTime);
        return "key clip " + path + (d.keyOn ? " ON" : " off") + (System.IO.File.Exists(path) ? ", " + new System.IO.FileInfo(path).Length / 4 + " floats" : ", FILE MISSING") + (EditorApplication.isPlaying ? " (PLAY MODE: not saved)" : "");
    }

    /// <summary>Her measurements for the animator, character space (m): joints at rest, arm lengths, the mouth, the microphone and both grips, and how
    /// far the body reaches forward / sideways at a few heights. -> Capture/rig_sheet.json</summary>
    public static string RigSheet()
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform; bool was = d.keyOn; d.keyOn = false; d.Apply(0f);
        var sb = new StringBuilder("{\n"); var ci = System.Globalization.CultureInfo.InvariantCulture;
        string V(Vector3 v) => "[" + v.x.ToString("F4", ci) + ", " + v.y.ToString("F4", ci) + ", " + v.z.ToString("F4", ci) + "]";
        foreach (HumanBodyBones b in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleDistal, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot })
        { var t = an.GetBoneTransform(b); if (t != null) sb.Append("  \"" + b + "\": " + V(root.InverseTransformPoint(t.position)) + ",\n"); }
        d.MicGripPose(1f, out Vector3 gp, out Vector3 gf, out Vector3 gn); d.PoleGripPose(-1f, out Vector3 pp, out Vector3 pf, out Vector3 pn);
        sb.Append("  \"micPos\": " + V(d.micPos) + ", \"micDir\": " + V(d.micDir.normalized) + ", \"micPole\": " + V(d.MicPolePoint) + ", \"micMouth\": " + V(d.micMouth) + ",\n");
        sb.Append("  \"gripR_wrist\": " + V(gp) + ", \"gripR_finger\": " + V(gf) + ", \"gripR_palm\": " + V(gn) + ",\n  \"poleL_wrist\": " + V(pp) + ", \"poleL_finger\": " + V(pf) + ", \"poleL_palm\": " + V(pn) + ",\n");
        // the body's outline from the real meshes (arms excluded): per height, the furthest point forward (z) near the middle and at 8 cm / 14 cm to the side, and the furthest sideways (x)
        var arm = new System.Collections.Generic.HashSet<Transform>();
        foreach (var hb in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm }) foreach (var tr in an.GetBoneTransform(hb).GetComponentsInChildren<Transform>()) arm.Add(tr);
        var bm = new Mesh(); int NH = 34; float y0 = 0.60f, dy = 0.03f; var zc = new float[NH]; var z8 = new float[NH]; var z14 = new float[NH]; var xs = new float[NH]; var zb = new float[NH];
        for (int i = 0; i < NH; i++) { zc[i] = z8[i] = z14[i] = xs[i] = -9; zb[i] = 9; }
        foreach (var smr in d.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var bw = smr.sharedMesh.boneWeights; var bones = smr.bones; smr.BakeMesh(bm); var vs = bm.vertices; var l2w = smr.transform.localToWorldMatrix;
            for (int i = 0; i < vs.Length; i++)
            {
                var w = bw[i]; int bi = w.boneIndex0; float best = w.weight0; if (w.weight1 > best) { best = w.weight1; bi = w.boneIndex1; } if (w.weight2 > best) { best = w.weight2; bi = w.boneIndex2; } if (w.weight3 > best) bi = w.boneIndex3;
                if (arm.Contains(bones[bi])) continue; Vector3 v = root.InverseTransformPoint(l2w.MultiplyPoint3x4(vs[i])); int h = Mathf.FloorToInt((v.y - y0) / dy); if (h < 0 || h >= NH) continue;
                float ax = Mathf.Abs(v.x); if (ax < 0.04f) zc[h] = Mathf.Max(zc[h], v.z); if (Mathf.Abs(ax - 0.08f) < 0.02f) z8[h] = Mathf.Max(z8[h], v.z); if (Mathf.Abs(ax - 0.14f) < 0.02f) z14[h] = Mathf.Max(z14[h], v.z);
                xs[h] = Mathf.Max(xs[h], ax); zb[h] = Mathf.Min(zb[h], v.z);
            }
        }
        Object.DestroyImmediate(bm);
        sb.Append("  \"outline\": [   // height, front z at x 0 / 0.08 / 0.14, widest |x|, back z\n");
        for (int i = 0; i < NH; i++) sb.Append("    [" + (y0 + (i + 0.5f) * dy).ToString("F3", ci) + ", " + zc[i].ToString("F3", ci) + ", " + z8[i].ToString("F3", ci) + ", " + z14[i].ToString("F3", ci) + ", " + xs[i].ToString("F3", ci) + ", " + zb[i].ToString("F3", ci) + "]" + (i < NH - 1 ? ",\n" : "\n"));
        sb.Append("  ]\n}\n"); System.IO.File.WriteAllText("Capture/rig_sheet.json", sb.ToString()); d.keyOn = was; d.Apply(d.previewTime);
        return "rig sheet -> Capture/rig_sheet.json";
    }

    /// <summary>Fits a grip joint by joint on the real meshes at time t (the hand is on the thing): side 0 = right hand on the microphone, 1 = left
    /// hand on the stand. The fingers close the way a hand does - the first joint until the finger touches, then the second until what lies beyond
    /// it touches, then the last; the thumb the same. Stores micCurl3 / poleCurl3 (15 values).</summary>
    public static string TuneGrip3(float t, int side)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform; bool right = side == 0;
        Vector3 g0 = d.micPos, ax = d.micDir.normalized, pt = d.MicPolePoint, pa = pt + Vector3.down * 0.046f, pb = new Vector3(pt.x, 0.02f, pt.z);
        var rigM = Object.FindFirstObjectByType<MicStandRig>(FindObjectsInactive.Include); float clipA = rigM != null ? rigM.clipAlong : d.micClip, bodyL = rigM != null ? rigM.bodyLength : d.micLen;
        int baseId = (int)(right ? HumanBodyBones.RightThumbProximal : HumanBodyBones.LeftThumbProximal);
        var segOf = new System.Collections.Generic.Dictionary<Transform, int>();          // bone -> finger * 3 + segment
        for (int f = 0; f < 5; f++) for (int k = 0; k < 3; k++) { var b = an.GetBoneTransform((HumanBodyBones)(baseId + f * 3 + k)); if (b == null) continue; foreach (var tr in b.GetComponentsInChildren<Transform>()) segOf[tr] = f * 3 + k; }
        var smrs = d.GetComponentsInChildren<SkinnedMeshRenderer>().Where(x => x != d.face).ToArray(); var vs2 = new int[smrs.Length][];
        for (int m = 0; m < smrs.Length; m++)
        {
            var bw = smrs[m].sharedMesh.boneWeights; var bones = smrs[m].bones; vs2[m] = new int[bw.Length];
            for (int i = 0; i < bw.Length; i++)
            {
                var w = bw[i]; int bi = w.boneIndex0; float best = w.weight0; if (w.weight1 > best) { best = w.weight1; bi = w.boneIndex1; } if (w.weight2 > best) { best = w.weight2; bi = w.boneIndex2; } if (w.weight3 > best) bi = w.boneIndex3;
                vs2[m][i] = segOf.TryGetValue(bones[bi], out int sg) ? sg : -1;
            }
        }
        float SegD(Vector3 p, Vector3 a, Vector3 b) { Vector3 ab = b - a; float u = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude); return Vector3.Distance(p, a + ab * u); }
        float Inside(Vector3 v)
        {
            if (!right) return 0.0125f - SegD(v, pa, pb);
            float al = Vector3.Dot(v - g0, ax), rd = Vector3.Cross(v - g0, ax).magnitude, dp = 0.027f - Vector3.Distance(v, g0);
            if (al > 0.0185f && al < bodyL) { float rr = al < 0.032f ? 0.024f : Mathf.Abs(al - clipA) < 0.024f ? 0.0245f : Mathf.Lerp(0.0205f, 0.0148f, Mathf.InverseLerp(0.032f, bodyL - 0.018f, al)); dp = Mathf.Max(dp, rr - rd); }
            return dp;
        }
        var cur = new float[15]; var bm = new Mesh();
        float[] Depth()            // per finger-segment: the deepest vertex of that segment inside the thing (m)
        {
            if (right) d.micCurl3 = cur; else d.poleCurl3 = cur; d.Apply(t); var dep = new float[15]; for (int i = 0; i < 15; i++) dep[i] = -1f;
            for (int m = 0; m < smrs.Length; m++)
            {
                smrs[m].BakeMesh(bm); var vs = bm.vertices; var l2w = smrs[m].transform.localToWorldMatrix;
                for (int i = 0; i < vs.Length; i++) { int sg = vs2[m][i]; if (sg < 0) continue; dep[sg] = Mathf.Max(dep[sg], Inside(root.InverseTransformPoint(l2w.MultiplyPoint3x4(vs[i])))); }
            }
            return dep;
        }
        for (int k = 0; k < 3; k++)                     // joint by joint, all fingers at once (they do not reach each other)
        {
            var lo = new float[5]; var hi = new float[5]; for (int f = 0; f < 5; f++) { lo[f] = f == 0 ? -0.35f : 0f; hi[f] = f == 0 ? 1.0f : 1.15f; cur[f * 3 + k] = lo[f]; }
            var d0 = Depth(); var stuck = new bool[5];
            for (int f = 0; f < 5; f++) { float w0 = -1f; for (int kk = k; kk < 3; kk++) w0 = Mathf.Max(w0, d0[f * 3 + kk]); stuck[f] = w0 > 0.0006f; }       // already inside when fully open: leave it open
            for (int it = 0; it < 8; it++)
            {
                for (int f = 0; f < 5; f++) cur[f * 3 + k] = stuck[f] ? lo[f] : 0.5f * (lo[f] + hi[f]);
                var dep = Depth();
                for (int f = 0; f < 5; f++) { float w0 = -1f; for (int kk = k; kk < 3; kk++) w0 = Mathf.Max(w0, dep[f * 3 + kk]); if (w0 > 0.0006f) hi[f] = cur[f * 3 + k]; else lo[f] = cur[f * 3 + k]; }
            }
            for (int f = 0; f < 5; f++) cur[f * 3 + k] = lo[f];
        }
        // the thumb does not close monotonically (its first joint swings it across the palm): of all positions that stay outside, take the one
        // that lies closest to the thing - a thumb resting on it, not one standing off
        for (int k = 0; k < 3; k++)
        {
            float bestV = cur[k], bestD = -9f;
            for (int g = 0; g <= 13; g++)
            {
                cur[k] = Mathf.Lerp(-0.35f, 1.0f, g / 13f); var dep = Depth(); float w0 = Mathf.Max(dep[0], Mathf.Max(dep[1], dep[2]));
                if (w0 <= 0.0006f && w0 > bestD) { bestD = w0; bestV = cur[k]; }
            }
            cur[k] = bestV;
        }
        var fin = Depth(); Object.DestroyImmediate(bm);
        if (right) d.micCurl3 = cur; else d.poleCurl3 = cur; EditorUtility.SetDirty(d);
        if (!EditorApplication.isPlaying) { EditorSceneManager.MarkSceneDirty(d.gameObject.scene); EditorSceneManager.SaveScene(d.gameObject.scene); }
        var sb = new StringBuilder((right ? "microphone" : "stand") + " grip, joint by joint at t " + t + ":");
        string[] nm = { "thumb", "index", "middle", "ring", "little" };
        for (int f = 0; f < 5; f++) sb.Append(" " + nm[f] + " " + cur[f * 3].ToString("F2") + "/" + cur[f * 3 + 1].ToString("F2") + "/" + cur[f * 3 + 2].ToString("F2"));
        sb.Append("; deepest inside (mm): " + (fin.Max() * 1000).ToString("F1")); return sb.ToString();
    }

    public static string EmoGain(float g) { var d = Object.FindFirstObjectByType<SingDriver>(); d.emoGain = g; EditorUtility.SetDirty(d); if (!EditorApplication.isPlaying) { EditorSceneManager.MarkSceneDirty(d.gameObject.scene); EditorSceneManager.SaveScene(d.gameObject.scene); } return "expression gain " + g; }

    /// <summary>AgentScripts/base.txt: "clip start duration speed lower torso arms" per line; "mic 1" / "mic 0" turns the stand microphone on / off.</summary>
    public static string SetBaseFromFile()
    {
        var d = Object.FindFirstObjectByType<SingDriver>();
        var tas = AssetDatabase.FindAssets("t:TextAsset", new[] { "Assets/MV/Data/Clips" }).Select(g => AssetDatabase.LoadAssetAtPath<TextAsset>(AssetDatabase.GUIDToAssetPath(g))).Where(x => x != null).ToArray();
        d.clipData = tas;
        // the driver keeps the parsed clips in memory: re-imported clips of the same name would otherwise be the old ones until the next domain reload
        typeof(SingDriver).GetField("clipLib", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(d, null);
        foreach (var ta in tas) AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(ta));
        var nm = new System.Collections.Generic.List<string>(); var f = new System.Collections.Generic.List<float[]>(); var ci = System.Globalization.CultureInfo.InvariantCulture;
        d.micOn = false;
        if (System.IO.File.Exists("AgentScripts/base.txt"))
            foreach (var line in System.IO.File.ReadAllLines("AgentScripts/base.txt"))
            {
                var q = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (q.Length == 0 || q[0].StartsWith("#")) continue;
                if (q[0] == "mic") { d.micOn = q.Length > 1 && q[1] == "1"; continue; }
                if (q.Length < 7) return "bad line: " + line;
                var vals = q.Skip(1).Where(x => !x.Contains("=")).Select(x => float.Parse(x, ci)).ToList(); while (vals.Count < 7) vals.Add(0); while (vals.Count < 8) vals.Add(-1);
                float gainV = 1f, offV = 0f, fadeV = 0f;
                foreach (var kv in q.Where(x => x.Contains("="))) { var p2 = kv.Split('='); float v = float.Parse(p2[1], ci); if (p2[0] == "gain") gainV = v; else if (p2[0] == "off") offV = v; else if (p2[0] == "yaw") vals[6] = v; else if (p2[0] == "up") vals[7] = v; else if (p2[0] == "fade") fadeV = v; }
                vals.Add(gainV); vals.Add(offV); vals.Add(fadeV);
                nm.Add(q[0]); f.Add(vals.ToArray());
            }
        d.baseClip = nm.ToArray(); d.baseStart = f.Select(x => x[0]).ToArray(); d.baseDur = f.Select(x => x[1]).ToArray(); d.baseSpeed = f.Select(x => x[2]).ToArray();
        d.baseLower = f.Select(x => x[3]).ToArray(); d.baseTorso = f.Select(x => x[4]).ToArray(); d.baseArms = f.Select(x => x[5]).ToArray(); d.baseYaw = f.Select(x => x[6]).ToArray(); d.baseUp = f.Select(x => x[7]).ToArray(); d.baseGain = f.Select(x => x[8]).ToArray(); d.baseOff = f.Select(x => x[9]).ToArray(); d.baseFadeI = f.Select(x => x[10]).ToArray();
        d.InvalidateBake(); EditorUtility.SetDirty(d);
        var missing = nm.Where(x => !tas.Any(ta => ta.name == x)).ToArray();
        return $"base entries {nm.Count}, mic {(d.micOn ? "on" : "off")}, clip files {tas.Length}" + (missing.Length > 0 ? "; MISSING: " + string.Join(",", missing) : "");
    }

    /// <summary>Where the mouth really is: centroid of the lip vertices (those the "A" mouth shape moves most), averaged over [t0, t1], in character space.
    /// Also the vertical extent of those vertices and the head bone, so the microphone can be put in front of the mouth, not guessed.</summary>
    public static string MouthPos(float t0, float t1)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var an = d.animator; var root = d.transform; var face = d.face; var mesh = face.sharedMesh;
        int idx = mesh.GetBlendShapeIndex("Fcl_MTH_A"); if (idx < 0) return "no Fcl_MTH_A";
        var del = new Vector3[mesh.vertexCount]; mesh.GetBlendShapeFrameVertices(idx, 0, del, null, null);
        float mx = del.Max(v => v.magnitude); var lips = Enumerable.Range(0, del.Length).Where(i => del[i].magnitude > 0.35f * mx).ToArray();
        var baked = new Mesh(); var sb = new StringBuilder(); Vector3 sum = Vector3.zero, sumHead = Vector3.zero; float lo = 9, hi = -9, front = -9; int n = 0;
        float mouthKeep = d.mouth; d.mouth = 0f;                                  // lips closed: the rest position of the mouth
        for (float t = t0; t <= t1 + 1e-4f; t += 0.5f)
        {
            d.Apply(t); face.BakeMesh(baked, true); var vs = baked.vertices; var l2w = face.transform.localToWorldMatrix; Vector3 c = Vector3.zero;
            foreach (int i in lips) { Vector3 w = root.InverseTransformPoint(l2w.MultiplyPoint3x4(vs[i])); c += w; lo = Mathf.Min(lo, w.y); hi = Mathf.Max(hi, w.y); front = Mathf.Max(front, w.z); }
            c /= lips.Length; sum += c; sumHead += root.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Head).position); n++;
        }
        d.mouth = mouthKeep; Vector3 m = sum / n, h = sumHead / n;
        return $"lip vertices {lips.Length}; mouth centre (character space) {m.x:F3} {m.y:F3} {m.z:F3}; lips y {lo:F3}..{hi:F3}, foremost z {front:F3}; head bone {h.x:F3} {h.y:F3} {h.z:F3}; mouth - head = {m.x - h.x:F3} {m.y - h.y:F3} {m.z - h.z:F3}; mic now {d.micPos.x:F3} {d.micPos.y:F3} {d.micPos.z:F3}";
    }

    /// <summary>Put the microphone (centre of the grille, character space) and save it in the scene.</summary>
    public static string SetMic(float x, float y, float z)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); d.micPos = new Vector3(x, y, z); d.InvalidateBake(); EditorUtility.SetDirty(d);
        return $"mic at {d.micPos.x:F3} {d.micPos.y:F3} {d.micPos.z:F3}";
    }

    public static string StopSample()
    {
        if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
        return "animation mode off";
    }

    public static string Light(float intensity)
    {
        var light = Object.FindFirstObjectByType<Light>(); light.intensity = intensity;
        return "light " + intensity;
    }

    public static string Cam(float dist, float fov, float yawDeg, float up)
    {
        var d = Object.FindFirstObjectByType<SingDriver>(); var cam = Camera.main;
        var head = d.animator.GetBoneTransform(HumanBodyBones.Head);
        Vector3 target = head.position + new Vector3(0, up, 0);
        Vector3 dir = Quaternion.Euler(0, yawDeg, 0) * d.transform.forward;
        cam.transform.position = target + dir * dist; cam.transform.LookAt(target); cam.fieldOfView = fov;
        return $"cam {cam.transform.position:F2} fov {fov}";
    }
}
