using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// v0.5 Presentation-only 重构的静态护栏：防止 UI 换代时反向侵入 Core，
/// 也防止视觉重排造成既有功能入口、DPI 保护或资源装载静默失联。
/// </summary>
public sealed class V5PresentationContractTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到含 PCMig.sln 的仓库根：" + AppContext.BaseDirectory);
    }

    private static string Read(string root, params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "缺少契约文件：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void Core_RemainsFreeOfWpfAndGuiReferences()
    {
        var root = FindRepoRoot();
        var coreProject = Read(root, "src", "PCMig.Core", "PCMig.Core.csproj");
        Assert.DoesNotContain("UseWPF", coreProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationFramework", coreProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WindowsBase", coreProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PCMig.Gui", coreProject, StringComparison.OrdinalIgnoreCase);

        var guiProject = Read(root, "src", "PCMig.Gui", "PCMig.Gui.csproj");
        Assert.Contains("..\\PCMig.Core\\PCMig.Core.csproj", guiProject, StringComparison.Ordinal);
    }

    [Fact]
    public void V5_DesignSystem_IsLoadedAsExplicitPresentationResources()
    {
        var root = FindRepoRoot();
        var app = Read(root, "src", "PCMig.Gui", "App.xaml");
        var resources = new[]
        {
            "Material.xaml", "Typography.xaml", "Buttons.xaml", "Inputs.xaml", "Navigation.xaml",
            "Panels.xaml", "Lists.xaml", "Progress.xaml", "Status.xaml", "Motion.xaml"
        };

        foreach (var resource in resources)
        {
            Assert.Contains("Theme/V5/" + resource, app, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, "src", "PCMig.Gui", "Theme", "V5", resource)),
                "缺少 V5 资源字典：" + resource);
        }

        var material = Read(root, "src", "PCMig.Gui", "Theme", "V5", "Material.xaml");
        Assert.Contains("V5.Ambient.Background", material, StringComparison.Ordinal);
        Assert.Contains("V5.Material.InsetBrush", material, StringComparison.Ordinal);
        Assert.DoesNotContain("BlurEffect", material, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PreservesBusinessBindingsCommandsAndResponsiveSafety()
    {
        var root = FindRepoRoot();
        var xaml = Read(root, "src", "PCMig.Gui", "MainWindow.xaml");
        var codeBehind = Read(root, "src", "PCMig.Gui", "MainWindow.xaml.cs");

        foreach (var contract in new[]
        {
            "ConnectCommand", "AddManualShareCommand", "GoStepCommand", "BrowseTargetCommand", "PrepareCommand",
            "StartCommand", "PauseCommand", "StopCommand", "ResumeCommand", "VerifyCommand", "RepairCommand",
            "ReportCommand", "OpenTargetCommand", "CurrentStep", "IsStepConnect", "IsStepSelect", "IsStepTransfer",
            "IsStepResult", "NodeCheck_Toggle", "NodeCheck_KeyToggle", "DirNode_Expanded"
        })
        {
            Assert.Contains(contract, xaml, StringComparison.Ordinal);
        }

        Assert.Contains("MinWidth=\"960\" MinHeight=\"660\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ClipToBounds=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DockPanel.Dock=\"Right\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyWorkAreaClamp", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PCMIG_CLASSIC_UI", Read(root, "src", "PCMig.Gui", "App.xaml.cs"), StringComparison.Ordinal);
    }
}
