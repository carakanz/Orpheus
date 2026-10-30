using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Orpheus.Editor
{
    /// <summary>
    /// Сборка клиента командой (docs/07, 11.1a; 08, 3.0 — «сборка клиента — IL2CPP»).
    /// <code>Unity -batchmode -nographics -quit -projectPath client -executeMethod Orpheus.Editor.BuildScript.BuildWindows
    ///   [-orpheusBackend IL2CPP|Mono] [-orpheusOut &lt;путь к Orpheus.exe&gt;] [-orpheusDevelopment] -logFile build.log</code>
    /// По умолчанию — IL2CPP, выход в build/client-win/Orpheus.exe рядом с client/. Код выхода 0 — успех.
    /// Выбранный бэкенд действует только на эту сборку: настройки проекта (IL2CPP) возвращаются после неё.
    /// </summary>
    public static class BuildScript
    {
        public static void BuildWindows()
        {
            int code = 1;
            try
            {
                code = Build(Environment.GetCommandLineArgs());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            Debug.Log($"[build] exit {code}");
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static int Build(string[] args)
        {
            string backendArg = Arg(args, "-orpheusBackend") ?? "IL2CPP";
            ScriptingImplementation backend;
            switch (backendArg.ToUpperInvariant())
            {
                case "IL2CPP": backend = ScriptingImplementation.IL2CPP; break;
                case "MONO": case "MONO2X": backend = ScriptingImplementation.Mono2x; break;
                default:
                    Debug.LogError($"[build] неизвестный -orpheusBackend {backendArg}: нужен IL2CPP или Mono");
                    return 2;
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath);           // .../client
            string repoRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
            string output = Arg(args, "-orpheusOut")
                ?? Path.Combine(repoRoot, "build", "client-win", "Orpheus.exe");
            output = Path.GetFullPath(output);

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0 || scenes[0] != ProjectSetup.BootScenePath)
            {
                Debug.LogError($"[build] первой сценой сборки должна быть {ProjectSetup.BootScenePath}; " +
                               "запусти Orpheus.Editor.ProjectSetup.Run");
                return 3;
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = args.Contains("-orpheusDevelopment") ? BuildOptions.Development : BuildOptions.None,
            };

            ScriptingImplementation saved = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
            Debug.Log($"[build] backend={backend} out={output} scenes={string.Join(",", scenes)}");
            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, saved);
            }

            BuildSummary s = report.summary;
            Debug.Log($"[build] result={s.result} time={s.totalTime.TotalSeconds:0.0}s size={s.totalSize / (1024 * 1024)}MB " +
                      $"errors={s.totalErrors} warnings={s.totalWarnings}");
            foreach (BuildStep step in report.steps)
                foreach (BuildStepMessage m in step.messages)
                    if (m.type == LogType.Error || m.type == LogType.Exception)
                        Debug.Log($"[build] error in '{step.name}': {m.content}");
            return s.result == BuildResult.Succeeded ? 0 : 1;
        }
    }
}
