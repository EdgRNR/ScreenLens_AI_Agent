<#
.SYNOPSIS
  ScreenLens WinUI 前端原型验收截图脚本。

.DESCRIPTION
  通过 UI Automation 切换主窗口的导航页，把窗口置于前台后按窗口矩形截图，
  用于第七阶段「基础界面问题修复」的逐页验收（截图必须来自真实运行窗口）。

  依赖：Windows 自带的 UIAutomationClient / UIAutomationTypes，无需安装第三方库。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\capture_winui_screenshots.ps1 `
      -Pages General,About,Capture,Hotkeys -Suffix _phase7
#>
param(
    [string[]]$Pages = @("General", "About", "Capture", "Hotkeys"),
    [string]$Suffix = "_phase7",
    [string]$TitleKeyword = "ScreenLens 设置",
    [string]$ProcessName = "ScreenLens.WinUI",
    [int]$SettleMs = 900,
    # 可选：截图前把主窗口设为指定逻辑尺寸，例如 "900x650"（用于窄窗口验收）
    [string]$Size = "",
    # 可选：截图前最大化窗口
    [switch]$Maximize,
    # 可选：额外汇出任务栏截图（用于核对任务栏图标视觉尺寸）
    [switch]$Taskbar,
    # 任务栏截图放大倍数（默认 3 倍，便于人工核对图标比例）
    [int]$TaskbarZoom = 3
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"

$outDir = Join-Path (Split-Path -Parent $PSScriptRoot) "docs\screenshots"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 导航项显示名 → 页面（也用作输出文件名的一部分）
$pageAliases = @{
    "General"    = "通用"
    "Capture"    = "截图与选区"
    "Ocr"        = "OCR 与结果"
    "Translate"  = "翻译"
    "Hotkeys"    = "快捷键"
    "Appearance" = "外观"
    "About"      = "关于"
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
"@

function Get-MainWindow {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $proc) { throw "未找到运行中的 $ProcessName 窗口，请先启动应用。" }
    return $proc
}

function Show-Window($proc) {
    $hwnd = $proc.MainWindowHandle
    if ([NativeWin]::IsIconic($hwnd)) { [NativeWin]::ShowWindow($hwnd, 9) | Out-Null }  # SW_RESTORE
    [NativeWin]::BringWindowToTop($hwnd) | Out-Null
    [NativeWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 500
}

function Invoke-NavItem($window, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $item) {
        Write-Warning "未找到导航项：$name"
        return $false
    }
    $pattern = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
    }
    elseif ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    }
    else {
        Write-Warning "导航项 $name 不支持选中/调用，跳过"
        return $false
    }
    return $true
}

function Save-WindowShot($proc, $window, $fileName) {
    # 用 UI Automation 的窗口外框矩形（含标题栏），避免额外 Win32 结构体互操作
    $rect = $window.Current.BoundingRectangle
    $w = [int]$rect.Width
    $h = [int]$rect.Height
    if ($w -le 0 -or $h -le 0) { Write-Warning "窗口区域无效：$fileName"; return }

    # 截图期间临时置顶，避免被其他窗口遮挡（截完立即恢复）
    $hwnd = $proc.MainWindowHandle
    $flags = 0x0002 -bor 0x0001 -bor 0x0010   # NOMOVE | NOSIZE | NOACTIVATE
    [NativeWin]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, $flags) | Out-Null
    Start-Sleep -Milliseconds 350

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
        $path = Join-Path $outDir $fileName
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("  {0}  {1}x{2}  <- {3}" -f $fileName, $w, $h, $proc.MainWindowTitle)
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
        # 恢复为普通层级
        [NativeWin]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, $flags) | Out-Null
    }
}

$proc = Get-MainWindow
$root = [System.Windows.Automation.AutomationElement]::RootElement
# 同一进程可能有多个顶层窗口（如截图演示窗口），必须按标题定位主设置窗口
$winCond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $TitleKeyword)))
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
if (-not $window) { throw "UI Automation 未定位到 $TitleKeyword 窗口（PID $($proc.Id)）。" }

Write-Host "开始截取验收截图（窗口 PID $($proc.Id)）："

# 可选：先把窗口摆成指定尺寸（参数按逻辑像素，内部换算为物理像素）
if ($Maximize) {
    [NativeWin]::ShowWindow($proc.MainWindowHandle, 3) | Out-Null   # SW_MAXIMIZE
    Start-Sleep -Milliseconds 800
}
elseif ($Size) {
    $parts = $Size -split "[xX]"
    if ($parts.Count -ge 2) {
        $logicalW = [int]$parts[0]
        $logicalH = [int]$parts[1]
        $scale = [NativeWin]::GetDpiForWindow($proc.MainWindowHandle) / 96.0
        $px = [int]($logicalW * $scale)
        $py = [int]($logicalH * $scale)
        # SWP_NOZORDER | SWP_NOACTIVATE
        [NativeWin]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, 60, 60, $px, $py, 0x0004 -bor 0x0010) | Out-Null
        Write-Host ("  窗口尺寸设为 {0}x{1} 逻辑像素（缩放 {2:N2}x -> {3}x{4} 物理像素）" -f $logicalW, $logicalH, $scale, $px, $py)
        Start-Sleep -Milliseconds 700
    }
}

foreach ($page in $Pages) {
    $label = $pageAliases[$page]
    if (-not $label) { Write-Warning "未知页面：$page"; continue }

    Show-Window $proc
    if (-not (Invoke-NavItem $window $label)) { continue }
    Start-Sleep -Milliseconds $SettleMs
    Show-Window $proc
    Start-Sleep -Milliseconds 300
    Save-WindowShot $proc $window ("ScreenLens设置_{0}{1}.png" -f $page, $Suffix)
}

if ($Taskbar) {
    # 任务栏图标核对：截取任务栏整条并放大，便于与同排应用图标比较视觉尺寸。
    # 用 UI Automation 定位（返回物理像素，且不受 PowerShell 进程 DPI 虚拟化影响）。
    $trayCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty, "Shell_TrayWnd")
    $tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $trayCond)
    if (-not $tray) {
        Write-Warning "未找到任务栏窗口，跳过任务栏截图"
    }
    else {
        $r = $tray.Current.BoundingRectangle
        $left = [int]$r.X
        $top = [int]$r.Y
        $w = [int]$r.Width
        $h = [int]$r.Height
        if ($w -gt 0 -and $h -gt 0) {
            $shot = New-Object System.Drawing.Bitmap $w, $h
            $g1 = [System.Drawing.Graphics]::FromImage($shot)
            $g1.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $w, $h))
            $g1.Dispose()

            $zw = $w * $TaskbarZoom
            $zh = $h * $TaskbarZoom
            $zoom = New-Object System.Drawing.Bitmap $zw, $zh
            $g2 = [System.Drawing.Graphics]::FromImage($zoom)
            $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $g2.DrawImage($shot, 0, 0, $zw, $zh)
            $g2.Dispose()

            $path = Join-Path $outDir ("taskbar{0}.png" -f $Suffix)
            $zoom.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            $zoom.Dispose()
            Write-Host ("  任务栏截图 {0}  ({1}x{2} 放大 {3} 倍 -> {4}x{5})" -f `
                (Split-Path $path -Leaf), $w, $h, $TaskbarZoom, $zw, $zh)

            # 再单独放大「运行中应用区」（避开左侧固定图标与右侧系统托盘），
            # 便于人工核对 ScreenLens 图标与同排应用的视觉尺寸。
            $fx0 = [int]($w * 0.50)
            $fx1 = [int]($w * 0.84)
            $fw = $fx1 - $fx0
            if ($fw -gt 0) {
                $crop = New-Object System.Drawing.Bitmap $fw, $h
                $gc = [System.Drawing.Graphics]::FromImage($crop)
                $gc.DrawImage($shot, (New-Object System.Drawing.Rectangle 0, 0, $fw, $h),
                    (New-Object System.Drawing.Rectangle $fx0, 0, $fw, $h),
                    [System.Drawing.GraphicsUnit]::Pixel)
                $gc.Dispose()

                $fzw = $fw * $TaskbarZoom * 2
                $fzh = $h * $TaskbarZoom * 2
                $focus = New-Object System.Drawing.Bitmap $fzw, $fzh
                $gf = [System.Drawing.Graphics]::FromImage($focus)
                $gf.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                $gf.DrawImage($crop, 0, 0, $fzw, $fzh)
                $gf.Dispose()
                $crop.Dispose()

                $fpath = Join-Path $outDir ("taskbar_apps{0}.png" -f $Suffix)
                $focus.Save($fpath, [System.Drawing.Imaging.ImageFormat]::Png)
                $focus.Dispose()
                Write-Host ("  任务栏应用区截图 {0}  放大 {1} 倍" -f (Split-Path $fpath -Leaf), ($TaskbarZoom * 2))
            }

            $shot.Dispose()
        }
    }
}

Write-Host "完成，输出目录：$outDir"
