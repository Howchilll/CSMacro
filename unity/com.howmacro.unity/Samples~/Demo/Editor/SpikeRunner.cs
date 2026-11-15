using UnityEditor;
using UnityEngine;

public static class SpikeRunner
{
    public static void Run()
    {
        Debug.Log("[spike] editor\n" + Demo.Game.SpikeChecks.Run());
    }

    public static void BuildPlayer()
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { DemoSetup.ScenePath },
            target = BuildTarget.StandaloneOSX,
            locationPathName = "Build/Spike.app",
        });
        Debug.Log("[spike] player build: " + report.summary.result);
        EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
}
