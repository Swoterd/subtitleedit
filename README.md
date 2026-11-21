# Subtitle Edit 4.0.14 — Swoterd 修改版

基于 [Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit) 4.0.14 的个人修改版，主要调整双字幕翻译、波形时间轴编辑和视频预览。

本分支为 `se4-custom`。修改源码整理日期：2026-11-21。原项目的版权声明与 GPL v3 许可证均予以保留。

## 主要修改

- 翻译模式支持打开原始字幕时按主字幕对齐时间轴，两份文件分别显示未保存状态。
- 修正原始字幕首次编辑的撤销记录，减少编辑、插入、删除、合并及撤销时的列表闪烁。
- 波形和频谱支持相邻字幕边界吸附，保留双字幕配对；一次拖动可一步撤销。
- 双字幕预览可分别设置样式，编辑后刷新当前画面；MPC-HC 字幕覆盖区域使用黑色背景。
- 打开主字幕、原始字幕、视频及各项导入操作分别记忆目录。
- 调整字幕合并、自动分割和歌词操作，并提供文本框右键对话合并命令。

完整说明见 [changelog.md](changelog.md)。

## 构建

环境：Windows、Git、.NET SDK **6.0.412**、.NET Framework **4.8 Developer Pack**。首次还原依赖需要访问 NuGet。

克隆本分支后，在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-custom.ps1
```

脚本从原有模板生成程序集信息，还原依赖并编译 WinForms 程序。输出位于 `src/ui/bin/Release/net48/SubtitleEdit.exe`。`global.json` 固定了使用的 .NET SDK 版本。

本仓库提供源码。个人设置、最近打开记录、编译输出和本地测试素材不作为本次修改提交。

## 当前限制

- 部分 M2TS 文件在跳转后开始播放时仍可能短暂无声，相关播放器处理仍需继续排查。
- 当前代码中 `UppercaseLetters` 和 `TagsInToggleCustomTags` 的部分默认字符串存在乱码，详见 changelog。

## 来源与许可证

基础版本为官方 4.0.14 提交 `ae9cb2ceb`。该标签缺少主窗体源码，修改版采用官方 `se4-legacy` 提交 `8c52e196f` 中恢复的 `Main.cs` 作为主窗体参考。

原作者及贡献者的版权归属保持不变。本修改版按 [GNU GPL v3](LICENSE.txt) 发布。

原项目帮助文档：[Subtitle Edit Help](https://www.nikse.dk/SubtitleEdit/Help)。
