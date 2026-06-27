# Cozy Island Read Aloud

舒舒服服小岛时光 / Cozy Island 的文字朗读学习插件。这个项目主要给小朋友认字学习使用：在游戏过程中按键朗读当前界面文字，让游戏里的材料、配方、任务和剧情文本变成可听、可跟读的学习内容。

> 本项目不是游戏官方项目，不包含游戏本体、游戏资源、游戏 DLL、提取出的完整游戏文本或商业语音缓存。

## Features

- 基于 BepInEx 5 加载，不修改游戏本体文件。
- 支持键盘朗读：
  - `G`：当前主要内容。
  - `H`：左上角任务清单。
  - `J`：左下角成就区域。
  - `K`：右下角操作提示。
- 自动扫描 Unity UI / TextMeshPro 文本。
- 针对工作台/制作界面做摘要朗读，只读当前选中配方的名称、说明、材料需求和状态。
- 优先播放本地 MiMo TTS 缓存。
- 缓存未命中时，使用 Windows 本机 TTS 生成 WAV，并保存到本地缓存。
- 预生成语音缓存可以单独打包，不需要放进源码仓库。

## Repository Scope

这个仓库只适合保存插件源码和辅助脚本：

- `src/CozyIslandReadAloud/`：BepInEx 插件源码。
- `tools/`：TTS 缓存生成脚本。
- `audio/zh-CN/`：少量测试音频。
- `build-package.ps1`：构建插件包。
- `install-to-game.ps1`：安装到本地游戏目录。

这些内容不应该提交到仓库：

- `audio_cache/`：本地 TTS 缓存，体积较大。
- `learning_text/`：本地学习文本和游戏文案提取结果。
- `deps/`：第三方依赖和本地 BepInEx 包。
- `dist/`：构建产物。
- `bin/`、`obj/`、`tmp/`：编译和临时文件。
- 任何游戏本体文件、游戏资源、API Key 或账号凭证。

## Requirements

- Windows
- Steam 版 Cozy Island / 舒舒服服小岛时光
- .NET SDK
- BepInEx 5 x64
- 可选：MiMo TTS API，用于批量预生成语音缓存

## Build

把 BepInEx 5 x64 解压到本项目的 `deps/` 目录，结构示例：

```text
deps/
  BepInEx_win_x64_5.4.23.2/
    BepInEx/
    winhttp.dll
    doorstop_config.ini
```

然后构建插件：

```powershell
.\build-package.ps1
```

构建产物会输出到：

```text
dist/CozyIslandReadAloud/
```

## Install

如果游戏安装在 Steam 默认库或其他目录，请显式传入游戏路径：

```powershell
.\install-to-game.ps1 -GameDir "F:\Steam\steamapps\common\CozyIsland"
```

安装后启动游戏，进入界面后按 `G/H/J/K` 测试朗读。

## TTS Cache

插件运行时的语音优先级：

1. MiMo 预生成 WAV 缓存。
2. MiMo 分段缓存命中。
3. Windows TTS WAV 缓存。
4. Windows TTS 现场生成并保存缓存。

MiMo 缓存默认目录：

```text
audio_cache/mimo/zh-CN/
```

Windows TTS 缓存默认目录：

```text
audio_cache/windows/zh-CN/
```

这些缓存文件不会提交到 git。若需要分享预生成语音，建议压缩后放到 GitHub Releases。

## Generate MiMo Cache

设置 `MIMO_API_KEY` 后可以批量生成语音：

```powershell
$env:MIMO_API_KEY="your_api_key"
$env:MIMO_SCOPE="broad"
$env:MIMO_BATCH_LIMIT="100"
node .\tools\generate-mimo-cache.mjs
```

常用参数：

- `MIMO_SCOPE=dialogue|broad|all`
- `MIMO_BATCH_LIMIT=100`
- `MIMO_CONCURRENCY=1`
- `MIMO_VOICE=冰糖`

## Notes

- 这个项目面向学习辅助，不提供自动游玩、作弊或修改存档功能。
- 插件只读取当前可见 UI 文本并播放语音。
- 游戏更新后，如果 UI 结构变化，部分区域识别规则可能需要调整。

## License

MIT
