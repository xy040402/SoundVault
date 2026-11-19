# 跑一遍断言测试（不依赖 NuGet 测试框架）
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
dotnet run -c Release --project (Join-Path $root 'tests\SoundVault.Tests') --nologo
exit $LASTEXITCODE