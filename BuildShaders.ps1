#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$UnityEditor = 'D:/Unity/Editors/2022.3.35f1/Editor/Unity.exe',
    [ValidateSet('All', 'Windows', 'Linux', 'macOS')]
    [string]$Platform = 'All'
)

$ErrorActionPreference = 'Stop'
if ($Platform -eq 'All') {
    foreach ($shaderPlatform in @('Windows', 'Linux', 'macOS')) {
        & $PSCommandPath -UnityEditor $UnityEditor -Platform $shaderPlatform
    }
    return
}
$repoRoot = $PSScriptRoot
$source = Join-Path $repoRoot 'Shaders'
$workRoot = Join-Path $repoRoot '.shader-build'
$projectRoot = Join-Path $workRoot 'Project'
$assetRoot = Join-Path $projectRoot 'Assets/Rim3DShaders'
$target = switch ($Platform) {
    'Windows' { @{ BuildTarget = 'Win64'; Bundle = 'rim3d-shaders' } }
    'Linux' { @{ BuildTarget = 'Linux64'; Bundle = 'rim3d-shaders-linux' } }
    'macOS' { @{ BuildTarget = 'OSXUniversal'; Bundle = 'rim3d-shaders-macos' } }
}
$destination = Join-Path $repoRoot "Mods/RimVision/ShaderBundles/$($target.Bundle)"
if (!(Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw "Unity Editor not found: $UnityEditor. Pass -UnityEditor with the path to Unity 2022.3.35f1." }
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "Shader sources missing: $source" }
if (!(Get-ChildItem -LiteralPath $source -Filter *.shader -Recurse -File)) { throw "No shaders found: $source" }
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$buildLock = [IO.File]::Open((Join-Path $workRoot 'build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    if (Test-Path -LiteralPath (Join-Path $projectRoot 'Temp/UnityLockfile')) { throw "The shader project is already open in Unity: $projectRoot" }
    $runRoot = Join-Path $workRoot ('Runs/' + [Guid]::NewGuid().ToString('N'))
    $output = Join-Path $runRoot 'ShaderBundles'
    $log = Join-Path $runRoot 'Unity.log'
    foreach ($folder in @($assetRoot, (Join-Path $projectRoot 'Assets/Editor'), (Join-Path $projectRoot 'Packages'), (Join-Path $projectRoot 'ProjectSettings'), $runRoot)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    foreach ($file in Get-ChildItem -LiteralPath $assetRoot -Recurse -Force -File) {
        Remove-Item -LiteralPath $file.FullName -Force
    }
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        if ($file.Extension -eq '.meta') { continue }
        $assetDestination = Join-Path $assetRoot ([IO.Path]::GetRelativePath($source, $file.FullName))
        New-Item -ItemType Directory -Path (Split-Path $assetDestination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $assetDestination
    }
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Packages/manifest.json'), '{"dependencies":{"com.unity.modules.assetbundle":"1.0.0"}}')
    [IO.File]::WriteAllText((Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt'), "m_EditorVersion: 2022.3.35f1`n")
    $builder = @'
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class RimVisionShaderBatch
{
    public static void Build()
    {
        try
        {
            string output = null;
            string bundleName = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-rimVisionShaderOutput") output = args[i + 1];
                if (args[i] == "-rimVisionShaderBundle") bundleName = args[i + 1];
            }
            if (string.IsNullOrEmpty(output)) throw new Exception("Shader output path missing.");
            if (string.IsNullOrEmpty(bundleName)) throw new Exception("Shader bundle name missing.");
            var target = EditorUserBuildSettings.activeBuildTarget;
            BuildTarget expectedTarget;
            switch (bundleName)
            {
                case "rim3d-shaders": expectedTarget = BuildTarget.StandaloneWindows64; break;
                case "rim3d-shaders-linux": expectedTarget = BuildTarget.StandaloneLinux64; break;
                case "rim3d-shaders-macos": expectedTarget = BuildTarget.StandaloneOSX; break;
                default: throw new Exception("Unknown shader bundle: " + bundleName);
            }
            if (target != expectedTarget)
                throw new Exception("Unity did not select " + expectedTarget + ". Install its Build Support module.");
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                throw new Exception("Install Unity 2022.3.35f1 Build Support for " + target);
            GraphicsDeviceType[] graphicsApis;
            switch (target)
            {
                case BuildTarget.StandaloneWindows64:
                    graphicsApis = new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore, GraphicsDeviceType.Vulkan };
                    break;
                case BuildTarget.StandaloneLinux64:
                    graphicsApis = new[] { GraphicsDeviceType.OpenGLCore, GraphicsDeviceType.Vulkan };
                    break;
                case BuildTarget.StandaloneOSX:
                    graphicsApis = new[] { GraphicsDeviceType.Metal, GraphicsDeviceType.OpenGLCore };
                    break;
                default: throw new Exception("Unsupported shader build target: " + target);
            }
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, graphicsApis);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var paths = Directory.GetFiles("Assets/Rim3DShaders", "*.shader", SearchOption.AllDirectories);
            Array.Sort(paths, StringComparer.Ordinal);
            if (paths.Length == 0) throw new Exception("No shader sources found.");
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = paths[i].Replace('\\', '/');
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(paths[i]);
                if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new Exception("Shader error: " + paths[i]);
            }
            Directory.CreateDirectory(output);
            var bundle = new AssetBundleBuild { assetBundleName = bundleName, assetNames = paths };
            var result = BuildPipeline.BuildAssetBundles(output, new[] { bundle },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode, target);
            if (result == null) throw new Exception("Shader bundle build failed.");
            foreach (string path in paths)
                if (ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(path))) throw new Exception("Shader compilation error: " + path);
            bool nativeTarget = target == BuildTarget.StandaloneWindows64 && Application.platform == RuntimePlatform.WindowsEditor ||
                target == BuildTarget.StandaloneLinux64 && Application.platform == RuntimePlatform.LinuxEditor ||
                target == BuildTarget.StandaloneOSX && Application.platform == RuntimePlatform.OSXEditor;
            if (nativeTarget)
            {
                var loadedBundle = AssetBundle.LoadFromFile(Path.Combine(output, bundleName));
                if (loadedBundle == null) throw new Exception("Built shader bundle could not be loaded.");
                try
                {
                    if (loadedBundle.LoadAllAssets<Shader>().Length != paths.Length)
                        throw new Exception("Built shader bundle does not contain all source shaders.");
                }
                finally { loadedBundle.Unload(true); }
            }
            Debug.Log("RimVision shader bundle built: " + output);
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }
}
'@
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Assets/Editor/RimVisionShaderBatch.cs'), $builder)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [IO.Path]::GetFullPath($UnityEditor)
    $start.WorkingDirectory = $projectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $projectRoot, '-buildTarget', $target.BuildTarget, '-executeMethod', 'RimVisionShaderBatch.Build', '-rimVisionShaderOutput', $output, '-rimVisionShaderBundle', $target.Bundle, '-logFile', $log)) {
        $start.ArgumentList.Add($argument)
    }
    Write-Output "Building $Platform shaders with Unity. Log: $log"
    $process = [Diagnostics.Process]::new()
    try {
        $process.StartInfo = $start
        if (!$process.Start()) { throw 'Unable to start Unity Editor.' }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Unity exited with code $($process.ExitCode). See $log" }
    }
    finally { $process.Dispose() }
    $bundle = Join-Path $output $target.Bundle
    if (!(Test-Path -LiteralPath $bundle -PathType Leaf) -or (Get-Item -LiteralPath $bundle).Length -eq 0) { throw "Shader bundle missing or empty. See $log" }
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    $pending = $destination + '.tmp'
    Copy-Item -LiteralPath $bundle -Destination $pending -Force
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        [IO.File]::Replace($pending, $destination, (Join-Path $runRoot ('previous-' + $target.Bundle)), $true)
    }
    else { [IO.File]::Move($pending, $destination) }
    Write-Output "Updated shader bundle: $destination"
    Write-Output 'Run Build.ps1 to package the mod with the updated shaders.'
}
finally { $buildLock.Dispose() }
