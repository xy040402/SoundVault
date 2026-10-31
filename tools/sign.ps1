<#
  给 音匣 的两个 exe 做代码签名（#47）。

  用法：
    # 1) 自签测试（不需要买证书，用来验证签名流程跑得通）
    powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Test

    # 2) 用真实证书（.pfx）
    powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Pfx D:\cert\my.pfx -Password 你的密码

    # 3) 只签某一个文件
    powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Test -File _local\dist\音匣.exe

  说明：
  - 自签证书只在**本机**有意义：签名本身有效，但别人的机器上没有这个根证书，仍会提示"未知发布者"。
    真正能减少 SmartScreen 拦截的是 **OV/EV 代码签名证书**（每年几百到几千元），EV 证书能立刻获得较好信誉。
  - 签名后不要再用任何工具改 exe（包括重新发布），否则签名失效——所以顺序是：先 publish，再签名。
#>

param(
    [string]$Pfx,
    [string]$Password,
    [switch]$Test,
    [string[]]$File
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kits) {
        $hit = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '\\x64\\' } |
               Sort-Object FullName -Descending |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw '没找到 signtool.exe（装 Windows SDK 或把 signtool 放到 PATH 里）'
}

$targets = if ($File) { $File } else {
    @(
        (Join-Path $root '_local\dist\音匣.exe'),
        (Join-Path $root '_local\dist\音匣安装包.exe')
    )
}
$targets = $targets | Where-Object { Test-Path $_ }
if (-not $targets) { throw '没有可签名的文件（先 publish 出 _local\dist\音匣.exe）' }

$signTool = Find-SignTool
Write-Host "signtool: $signTool"

if ($Test) {
    # 自签一张代码签名证书（当前用户），导出成 pfx 供 signtool 用
    $subject = 'CN=音匣 SoundVault (Test), O=SoundVault, C=CN'
    $existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
                Where-Object { $_.Subject -eq $subject } | Select-Object -First 1

    if (-not $existing) {
        Write-Host '创建自签代码签名证书…'
        $existing = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
            -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
            -CertStoreLocation Cert:\CurrentUser\My `
            -NotAfter (Get-Date).AddYears(2)
    }

    $pfxOut = Join-Path $root 'dist\test-signing.pfx'
    $plain = 'soundvault-test'
    $secure = ConvertTo-SecureString -String $plain -Force -AsPlainText
    Export-PfxCertificate -Cert $existing -FilePath $pfxOut -Password $secure | Out-Null
    Write-Host "测试证书: $($existing.Thumbprint)  ->  $pfxOut  (密码 $plain)"

    $Pfx = $pfxOut
    $Password = $plain
}

if (-not $Pfx) {
    throw '要么 -Test（自签），要么给 -Pfx <证书路径> -Password <密码>'
}

$tsa = 'http://timestamp.digicert.com'

# 用 Start-Process 传参：直接 & 调用时，含中文的文件名容易在参数编码上出问题
function Q([string]$s) { '"' + $s + '"' }

function Invoke-Sign([string[]]$argList) {
    $proc = Start-Process -FilePath $signTool -ArgumentList $argList -Wait -NoNewWindow -PassThru
    return $proc.ExitCode
}

foreach ($t in $targets) {
    Write-Host "签名 $(Split-Path $t -Leaf) …"
    $code = Invoke-Sign @('sign','/fd','SHA256','/f',(Q $Pfx),'/p',(Q $Password),'/tr',$tsa,'/td','SHA256',(Q $t))
    if ($code -ne 0) {
        Write-Host '  ⚠ 带时间戳失败（可能没网/证书问题），退回不带时间戳再试一次'
        $code = Invoke-Sign @('sign','/fd','SHA256','/f',(Q $Pfx),'/p',(Q $Password),(Q $t))
    }
    if ($code -ne 0) { Write-Host '  ✗ 这个文件签名失败' }
}

Write-Host ''
Write-Host '结果：'
foreach ($t in $targets) {
    $sig = Get-AuthenticodeSignature $t
    $who = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '(无)' }
    Write-Host ("  {0}`n    Status={1}  Signer={2}" -f (Split-Path $t -Leaf), $sig.Status, $who)
}
