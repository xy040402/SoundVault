# GitHub 发布清单（1.0.0）

> 按顺序做，10 分钟能发出去。

---

## 一、仓库内容（已经准备好的）

```
SoundVault/
├─ README.md                ← 产品主页（带截图、功能表、安装、构建）
├─ LICENSE                  ← MIT
├─ CHANGELOG.md             ← 1.0.0 更新说明
├─ .gitignore               ← 已排除 bin/obj/_local（dist / release / backup 都在 _local 里）
├─ build.ps1                ← 一键构建（.\build.ps1 -Version 1.0.0）
├─ .github/workflows/build.yml   ← push / PR 自动构建 + 跑测试
├─ src/                     ← 主程序 + 安装器
├─ tests/                   ← 断言测试（51 条）
├─ tools/                   ← 构建 / 测试 / 签名脚本
└─ docs/
   ├─ 使用说明书.md           ← 19 章图文手册
   ├─ 设计文档.md
   ├─ images/                ← 说明书配图（8 张）
   ├─ screenshots/           ← README 用图（6 张）
   ├─ logo/                  ← 品牌资源（SVG / ICO / 11 档 PNG）
   └─ dev/                   ← 开发记录、交接说明、验证报告（过程存档）
```

体积：**210 个文件 / 约 18 MB**（不含 exe）。

---

## 二、本地提交（仓库已经初始化好了）

> 这台机器上 GitHub 直连下载 Git 会超时（`winget` 报 0x80072ee2 / 0x80072efd），
> 所以用了**便携版 MinGit**：`D:\deepseek harness workspace\.tools\git\cmd\git.exe`（不进 PATH、不影响系统）。
> 本地仓库已初始化并打好第一个提交 `音匣 SoundVault 1.0.0`（214 个文件 / 17.9 MB，分支 `main`）。

接下来只差"连到 GitHub 并 push"：

```powershell
cd "D:\deepseek harness workspace\SoundVault"
$env:PATH = "D:\deepseek harness workspace\.tools\git\cmd;" + $env:PATH   # 用便携版 git

git remote add origin https://github.com/<你的用户名>/SoundVault.git
git push -u origin main
```

> 想装正式版 Git（可选，装完可以删掉便携版）：
> `winget install --id Git.Git -e`
> 网络不通时改用镜像下载 MinGit：
> `https://registry.npmmirror.com/-/binary/git-for-windows/v2.56.0.windows.4/MinGit-2.56.0.4-64-bit.zip`

> 以后改了代码要重新提交，用仓库里的脚本：`.\tools\git-init.ps1`（会自动找 git、设好作者、提交）。


## 三、在 GitHub 上创建仓库

1. 打开 https://github.com/new
2. **Repository name**：`SoundVault`
3. **Description**：
   ```
   本地音效库管理器 · Windows · WPF · 不联网、不改你的文件
   ```
4. 选 **Public**，**不要**勾 "Add a README"（本地已有）
5. Create → 按上面命令 push

---

## 四、发布 Release（附下载文件）

1. 仓库右侧 → **Releases** → **Draft a new release**
2. **Tag**：`v1.0.0` → **Create new tag on publish**
3. **Title**：`音匣 SoundVault 1.0.0`
> 发布说明已经写好了：直接把 [`RELEASE-v1.0.0.md`](RELEASE-v1.0.0.md) 的内容整段粘进去即可（点 GitHub 编辑器右上角的 "Paste" 或用编辑器打开复制）。

4. **Describe**（可直接复制 CHANGELOG 的 1.0.0 段）：
   ```markdown
   第一个正式版。

   ### 下载
   - **SoundVault-1.0.0-Setup.exe** — 双击安装（唯一提供的下载；装完就有 `音匣.exe`）

   ### 亮点
   - 拼音首字母 + 过滤语法秒搜（`bdx` → 拔刀相向）
   - 悬停即试听、波形上拖播放头、LUFS 归一化试听
   - 评分 / 收藏 / 标签 / 集合 / 重复检查
   - 拖进 DAW、批量导出（命名模板 + CSV 清单）
   - 安装器支持检测运行中/旧版本、卸载带数据选项
   - 全新 Logo 与新手引导

   ### 说明
   - 仅 Windows 10/11 x64，无需预装 .NET
   - 单文件未签名，SmartScreen 可能提示"未知发布者"
   - 详细用法见 [使用说明书](docs/使用说明书.md)
   ```
5. **Attach binary**（只传一个文件，其余 exe 都不放）：
   - `_local\dist\SoundVault-1.0.0-Setup.exe`（132 MB，已内嵌主程序；用英文名，中文名会被 GitHub 转义）

   > 绿色版 `音匣.exe` **不作为附件**。别人装完就有 `C:\Program Files\SoundVault\音匣.exe`；
   > 想要纯绿色版的人可以自己 `.\build.ps1` 构建，或从安装目录拷出来。

6. Publish release

> 上传前建议先跑一次 `.\build.ps1 -Version 1.0.0`，确保 `_local\dist` 里是 1.0.0 的产物。
> 也可以用 GitHub Actions 产物（workflow 已配好，跑完在 Actions → 该次运行 → Artifacts 下载）。

---

## 五、发布后建议

- **仓库 Settings → General → Topics** 填：`audio` `sound-effects` `wpf` `dotnet` `windows` `sfx-library` `pinyin-search`
- 关于页（About 右侧齿轮）勾选 **Releases**、**Topics**
- Issue 模板可选：新增 `.github/ISSUE_TEMPLATE/bug_report.md`
- 想加 CI 徽章，在 README 顶部加：
  ```markdown
  ![build](https://github.com/<用户名>/SoundVault/actions/workflows/build.yml/badge.svg)
  ```

---

## 六、发布前自检

- [ ] `.\build.ps1 -Version 1.0.0` 成功，`_local\dist\音匣.exe` 的版本显示 1.0.0
- [ ] `.\tools\test.ps1` 通过（51 条）
- [ ] 在干净机器（或新建 Windows 用户）上装一次，确认首次启动是**空状态 + 新手引导**
- [ ] README 里的图片都能显示（仓库里是相对路径）
- [ ] License 已确认（现在是 MIT，要换就改 LICENSE 与 README 末节）

---

## 七、命令行一键发 Release（可选）

装了 GitHub CLI 的话，推代码和发 Release 可以一条命令搞定：

```powershell
winget install --id GitHub.cli -e     # 只装一次
gh auth login                          # 浏览器登录一次

cd "D:\deepseek harness workspace\SoundVault"
$env:PATH = "D:\deepseek harness workspace\.tools\git\cmd;" + $env:PATH

git remote add origin https://github.com/<你的用户名>/SoundVault.git
git push -u origin main

gh release create v1.0.0 "_local\dist\音匣安装包.exe" `
  --title "音匣 SoundVault 1.0.0" `
  --notes-file "docs\dev\RELEASE-v1.0.0.md"
```

`gh release create` 会自动把 tag 建好、把安装包作为附件上传、把发布说明填好 —— 就是这个仓库要的全部内容。
