# v1.0.0 安装包

| 项 | 值 |
|---|---|
| 文件 | `SoundVault-1.0.0-Setup.exe`（本目录，不进 Git） |
| 大小 | 132.32 MB（138,748,800 字节） |
| SHA256 | `2FB89EC9A7218B8AD9FBB4E41E3BBC189F55B825CAEEE83FAA66CAC73625C8B0` |
| 内置版本 | 1.0.0.0 |
| 下载 | https://github.com/xy040402/SoundVault/releases/tag/v1.0.0 |

## 说明

- 这是 v1.0.0 正式发布用的安装包，只包含安装器（主程序已内嵌）
- GitHub Release 上提供的也是这一个文件
- 想重新构建：在本目录上一级执行 `.\build.ps1 -Version 1.0.0`，产物在 `_local\dist\`

## 校验

```powershell
Get-FileHash .\SoundVault-1.0.0-Setup.exe -Algorithm SHA256
```
