// lin-sing: builds the standalone player LinSing.exe (Windows x64) from the stage scene, so users need no Unity editor.
//   Menu:  LinSing / Build Standalone Player
//   Batch: Unity.exe -batchmode -quit -projectPath unity -executeMethod LinSingBuild.BuildPlayer [-out <folder>]
// Output (default): <repo>/player/LinSing.exe + LinSing_Data/ ...
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class LinSingBuild
{
    const string Scene = "Assets/MV/Scenes/LinMicStage.unity";

    [MenuItem("LinSing/Build Standalone Player", false, 40)]
    public static void BuildPlayer()
    {
        string outDir = null; var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == "-out") outDir = a[i + 1];
        outDir = Path.GetFullPath(outDir ?? Path.Combine(Application.dataPath, "../../player"));
        // the scene must not carry a song of this computer, and FrameDumper starts idle (LinSingPlayer fills it in)
        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        var fd = UnityEngine.Object.FindFirstObjectByType<FrameDumper>();
        if (fd != null) { fd.ranges = ""; fd.showFile = ""; fd.live = true; EditorUtility.SetDirty(fd); }
        var d = UnityEngine.Object.FindFirstObjectByType<SingDriver>();
        if (d != null) { d.keyClipPath = ""; d.lipJson = null; if (d.audioSource != null) { d.audioSource.clip = null; EditorUtility.SetDirty(d.audioSource); } EditorUtility.SetDirty(d); }
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());

        PlayerSettings.productName = "LinSing"; PlayerSettings.companyName = "lin-sing";
        PlayerSettings.SplashScreen.show = false;                         // allowed for Unity 6 Personal
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed; PlayerSettings.defaultScreenWidth = 1280; PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true; PlayerSettings.runInBackground = true; PlayerSettings.visibleInBackground = true;
        PlayerSettings.usePlayerLog = true; PlayerSettings.enableCrashReportAPI = false;
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);       // Direct3D 11, as the editor: the same picture
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });

        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var opt = new BuildPlayerOptions { scenes = new[] { Scene }, locationPathName = Path.Combine(outDir, "LinSing.exe"), target = BuildTarget.StandaloneWindows64, options = BuildOptions.None };
        BuildReport r = BuildPipeline.BuildPlayer(opt);
        Debug.Log($"[LinSing] build {r.summary.result}: {r.summary.totalSize / 1048576} MB, {r.summary.totalErrors} errors -> {outDir}");
        if (Application.isBatchMode && r.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }
}
