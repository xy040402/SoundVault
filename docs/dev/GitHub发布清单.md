# GitHub 发布清单（1.0.0）

> 按顺序做，10 分钟能发出去。

---

## 一、仓库内容（已经准备好的）

```
SoundVault/
├─ README.md                ← 产品主页（带截图、功能表、安装、构建）
├─ LICENSE                  ← MIT
├─ CHANGELOG.md             ← 1.0.0 更新说明
├─ .gitignore               ← 已排除 bin/obj/dist/release/backup
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

## 二、本地提交（这台机器还没装 Git）

先装 Git：

```powershell
winget install --id Git.Git -e
```

装完**重开一个终端**，然后：

```powershell
cd "D:\deepseek harness workspace\SoundVault"

git init
git add .
git commit -m "音匣 SoundVault 1.0.0"

git branch -M main
git remote add origin https://github.com/<你的用户名>/SoundVault.git
git push -u origin main
```

> 第一次 push 会要求登录。推荐用 GitHub Desktop 或 `gh auth login`，别把密码写进 URL。

---

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
4. **Describe**（可直接复制 CHANGELOG 的 1.0.0 段）：
   ```markdown
   第一个正式版。

   ### 下载
   - **音匣安装包.exe** — 双击安装（推荐）
   - **音匣.exe** — 绿色版，双击即用

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
5. **Attach binaries**：从 `dist\` 拖进去
   - `dist\音匣安装包.exe`（132 MB）
   - `dist\音匣.exe`（73 MB）
6. Publish release

> 上传前建议先跑一次 `.\build.ps1 -Version 1.0.0`，确保 dist 里是 1.0.0 的产物。
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

- [ ] `.\build.ps1 -Version 1.0.0` 成功，`dist\音匣.exe` 的版本显示 1.0.0
- [ ] `.\tools\test.ps1` 通过（51 条）
- [ ] 在干净机器（或新建 Windows 用户）上装一次，确认首次启动是**空状态 + 新手引导**
- [ ] README 里的图片都能显示（仓库里是相对路径）
- [ ] License 已确认（现在是 MIT，要换就改 LICENSE 与 README 末节）
