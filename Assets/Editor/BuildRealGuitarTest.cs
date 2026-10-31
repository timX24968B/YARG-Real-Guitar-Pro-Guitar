using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YARG.Editor
{
    public static class BuildRealGuitarTest
    {
        [MenuItem("File/Build Real Guitar Test")]
        public static void Build()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = "Builds/RealGuitarTest/YARG.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
                extraScriptingDefines = new[] { "YARG_TEST_BUILD" },
            };

            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"Real Guitar test build: {report.summary.result}, " +
                $"{report.summary.totalErrors} errors, " +
                $"{report.summary.outputPath}");
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }
    }
}
