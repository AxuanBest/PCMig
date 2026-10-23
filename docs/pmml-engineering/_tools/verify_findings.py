# -*- coding: utf-8 -*-
"""
对 CSV 中「高价值技术发现」做逆向抽样核验（只读）。
每条：给出当前源码实际内容，判定 CONFIRMED / CHANGED / GONE。
"""
import os, re, subprocess, json
import os as _os
REPO = _os.environ.get("PCMIG_REPO") or _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))

def L(rel):
    with open(os.path.join(REPO, rel.replace("/", os.sep)), encoding="utf-8",
              errors="replace") as f:
        return f.readlines()

def find(rel, pat, label, flags=0):
    try:
        ls = L(rel)
    except OSError:
        return f"[MISSING FILE] {rel}"
    rx = re.compile(pat, flags)
    hits = [(i, t.rstrip()) for i, t in enumerate(ls, 1) if rx.search(t)]
    if not hits:
        return f"[NO HIT] {label} :: {rel} /{pat}/"
    i, t = hits[0]
    return f"{label}\n    {rel}:{i}\n      {t.strip()[:180]}"

checks = [
    ("DAM-001 Mica XAML", "src/PCMig.WinUI/MainWindow.xaml", r"MicaBackdrop"),
    ("DAM-002 default acrylic", "src/PCMig.WinUI/Presentation/BackdropSpike.cs",
     r'PCMIG_BACKDROP'),
    ("DAM-003 acrylic params", "src/PCMig.WinUI/Presentation/BackdropSpike.cs",
     r"TintOpacity"),
    ("LMDS-002 ShellMaterial", "src/PCMig.WinUI/Themes/Materials.xaml",
     r'x:Key="PCMigShellMaterial"'),
    ("LMDS-003 SecondarySurface", "src/PCMig.WinUI/Themes/Materials.xaml",
     r'x:Key="PCMigSecondarySurface"'),
    ("DSL45-002 EmbossTop", "src/PCMig.WinUI/Themes/Colors.xaml",
     r"NavCardEmbossTopBrush"),
    ("DCST-003 CubicBezier 0.10", "src/PCMig.WinUI/Presentation/MotionDirector.cs",
     r"0\.10f,\s*0\.90f"),
    ("OACT-013 CubicBezier 0.16", "src/PCMig.WinUI/Presentation/MotionDirector.cs",
     r"0\.16f,\s*1\.0f"),
    ("OACT-004 fillFactor", "src/PCMig.WinUI/Presentation/FluidZoomTransitionCoordinator.cs",
     r"fillFactor"),
    ("OACT-008 sourceOpacity", "src/PCMig.WinUI/Presentation/FluidZoomTransitionCoordinator.cs",
     r"sourceOpacity"),
    ("OACT-011 Diag anim=", "src/PCMig.WinUI/Presentation/FluidZoomTransitionCoordinator.cs",
     r"anim="),
    ("TAOP-001 TryPlacePanel", "src/PCMig.WinUI/MainWindow.xaml.cs",
     r"bool TryPlacePanel"),
    ("TAOP-003 edgeMargin/topSafety", "src/PCMig.WinUI/MainWindow.xaml.cs",
     r"topSafety"),
    ("TAOP-005 centerPoint", "src/PCMig.WinUI/MainWindow.xaml.cs",
     r"centerPointX"),
    ("BOOT-002 MarkShellInteractive", "src/PCMig.WinUI/App.xaml.cs",
     r"MarkShellInteractive"),
    ("BOOT-003 SystemAnimationsEnabled", "src/PCMig.WinUI/Presentation/MotionDirector.cs",
     r"public static bool SystemAnimationsEnabled"),
    ("BOOT-003b cached field", "src/PCMig.WinUI/Presentation/MotionDirector.cs",
     r"_animationsEnabled"),
    ("HINT-001 ApplyAdaptiveHeight", "src/PCMig.WinUI/Views/ShellHintCard.xaml.cs",
     r"ApplyAdaptiveHeight"),
    ("HINT-002 ApplySurfaceHeight", "src/PCMig.WinUI/Views/ShellHintCard.xaml.cs",
     r"ApplySurfaceHeight"),
    ("PROG-001 ClampVisual", "src/PCMig.WinUI/Presentation/ProgressPresentationCoordinator.cs",
     r"ClampVisual"),
    ("PROG-002 exp filter k", "src/PCMig.WinUI/Presentation/ProgressPresentationCoordinator.cs",
     r"1\s*-\s*Math\.Exp|exp\(-"),
    ("STATUS-001 enum members", "src/PCMig.WinUI/Presentation/StatusChannel.cs",
     r"CurrentObjectStatus"),
    ("STATUS-002 SetChannel", "src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs",
     r"void SetChannel|SetChannel\("),
    ("IP-001 Interrupted=7", "src/PCMig.WinUI/Controls/ImmersiveProgress/ImmersiveProgressParameters.cs",
     r"Interrupted\s*=\s*7"),
    ("IP-005 RaisePropertyChanged", "src/PCMig.WinUI/Controls/ImmersiveProgress/ImmersiveTransferProgressAutomationPeer.cs",
     r"RaisePropertyChangedEvent"),
    ("NAV-001 ItemsControl", "src/PCMig.WinUI/Views/StepNavigationControl.xaml",
     r"ItemsControl"),
    ("NAV-005 selection bar", "src/PCMig.WinUI/Views/StepNavigationControl.xaml",
     r"SelectionBarOpacity"),
    ("A11Y-001 BrushTransition", "src/PCMig.WinUI/Views/StepNavigationControl.xaml",
     r"BrushTransition"),
    ("THEME-001 forced Light", "src/PCMig.WinUI/Themes/TextInputTemplates.xaml",
     r'RequestedTheme="Light"'),
    ("TYP-001 YaHei", "src/PCMig.WinUI/Themes/Typography.xaml", r"YaHei"),
    ("BOOT-009 ApplyStepCardHeight", "src/PCMig.WinUI/Presentation/ShellResponsiveLayout.cs",
     r"ApplyStepCardHeight"),
    ("VERIFY-004 EnsureVerifyPanel", "src/PCMig.WinUI/MainWindow.xaml.cs",
     r"EnsureVerifyPanel"),
    ("VERIFY-002 business start", "src/PCMig.WinUI/Views/VerificationProgressPanel.xaml.cs",
     r"EvaluateVerifyGateAsync|_onStartRequested"),
    ("MHE-009 zero MHE hits", "src", r"\bMHE\b"),
]

for label, rel, pat in checks:
    if rel == "src":
        out = subprocess.run(
            ["git", "grep", "-n", "-E", pat, "--", "src/"],
            cwd=REPO, capture_output=True, text=True)
        hits = [l for l in out.stdout.splitlines() if l.strip()]
        print(f"{label}\n    [git grep src/] hits={len(hits)}")
        for h in hits[:3]:
            print("      " + h[:170])
        continue
    print(find(rel, pat, label))
