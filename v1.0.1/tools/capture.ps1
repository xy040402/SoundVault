# 说明书配图抓取：按状态驱动界面，逐张保存到 docs\images\
param([string]$OutDir = 'D:\deepseek harness workspace\SoundVault\docs\images')
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class CAP {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int dx, int dy, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
public struct RECT { public int Left, Top, Right, Bottom; }
"@
[void][CAP]::SetProcessDPIAware()
New-Item -ItemType Directory $OutDir -Force | Out-Null

$proc = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and ($_.ProcessName -eq '音匣' -or $_.ProcessName -eq 'SoundVault.App') } | Select-Object -First 1
$h = $proc.MainWindowHandle
[void][CAP]::SetForegroundWindow($h); Start-Sleep -Milliseconds 700
$r = New-Object RECT; [void][CAP]::GetWindowRect($h, [ref]$r)
$winW = $r.Right - $r.Left; $winH = $r.Bottom - $r.Top
Write-Output ("窗口 " + $winW + "x" + $winH + " @ " + $r.Left + "," + $r.Top)

function Shot([string]$name) {
  $bmp = New-Object System.Drawing.Bitmap $winW, $winH
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $winW, $winH))
  $g.Dispose()
  $path = Join-Path $OutDir ($name + '.png')
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Write-Output ("  saved " + $name)
}
$root = $null
for ($i = 0; $i -lt 12 -and $null -eq $root; $i++) {
  Start-Sleep -Milliseconds 500
  try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($h) } catch { Write-Output ('  尝试' + $i + ' 失败: ' + $_.Exception.Message); $root = $null }
}
if ($null -eq $root) { Write-Output "UIA 连不上窗口"; exit 3 }
function Find-Id($id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
$li = [System.Windows.Automation.ControlType]::ListItem
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $li)
function Click-Rect($rc) {
  [void][CAP]::SetCursorPos([int]($rc.X + $rc.Width/2), [int]($rc.Y + $rc.Height/2)); Start-Sleep -Milliseconds 250
  [CAP]::mouse_event(0x0002,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 90
  [CAP]::mouse_event(0x0004,0,0,0,[IntPtr]::Zero)
}

# 1) 库视图（首页）
$fl = Find-Id 'FolderList'
if ($fl) { $rows = $fl.FindAll([System.Windows.Automation.TreeScope]::Children, $cond); if ($rows.Count -gt 0) { $rows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } }
Start-Sleep -Milliseconds 1500
Shot '01-库视图'

# 2) 文件夹视图（卡片）
if ($rows.Count -gt 1) { $rows[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1600; Shot '02-文件夹卡片' }

# 3) 搜索（含过滤语法）
$ql = Find-Id 'QuickList'
$qrows = $ql.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$qrows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1200
$box = Find-Id 'SearchBox'
$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('dur:<2s 拔刀')
Start-Sleep -Milliseconds 1600
Shot '03-搜索与语法'
$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
Start-Sleep -Milliseconds 1000

# 4) 右键菜单
$cards = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$cell = $null
foreach ($c in $cards) { $rr = $c.Current.BoundingRectangle; if ($rr.Width -gt 250 -and $rr.Height -gt 150) { $cell = $rr; break } }
if ($cell) {
  [void][CAP]::SetCursorPos([int]($cell.X + 150), [int]($cell.Y + 70)); Start-Sleep -Milliseconds 300
  [CAP]::mouse_event(0x0008,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 120
  [CAP]::mouse_event(0x0010,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 900
  Shot '04-右键菜单'
  [CAP]::mouse_event(0x0004,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 200
  [void][CAP]::SetCursorPos([int]($cell.X + 150), [int]($cell.Y + 400)); Start-Sleep -Milliseconds 200
}

# 5) 标签页 / 重复检查
$qrows[3].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1500; Shot '05-标签页'
$qrows[4].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1500; Shot '06-重复检查'
$qrows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1000

# 6) 设置面板
$set = Find-Id 'SettingsBtn'
$set.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1500
Shot '07-设置面板'
# 7) 外观对话框（设置里的第二个按钮区域）
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
foreach ($t in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
  if ($t.Current.Name -match '面板强度') { Click-Rect $t.Current.BoundingRectangle; break }
}
Start-Sleep -Milliseconds 1800
Shot '08-外观对话框'