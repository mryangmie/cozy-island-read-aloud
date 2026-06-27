# 舒舒服服小岛时光文字朗读插件

这是一个给《舒舒服服小岛时光 / Cozy Island》做的文字朗读学习插件。它的目标很简单：小朋友在玩游戏时，可以按键朗读当前界面文字，把材料、配方、任务、成就和剧情内容变成可听、可跟读的学习材料。

> 本项目不是游戏官方项目，不包含游戏本体、游戏资源、游戏 DLL、完整游戏文案、商业语音缓存或任何账号凭证。

## 功能特性

- 基于 BepInEx 5 加载，不修改游戏本体文件。
- 支持键盘朗读：
  - `G`：朗读当前主要内容。
  - `H`：朗读左上角任务清单。
  - `J`：朗读左下角成就区域。
  - `K`：朗读右下角操作提示。
- 自动扫描当前可见的 Unity UI / TextMeshPro 文本。
- 针对工作台、制作界面做摘要朗读，只读当前选中配方的名称、说明、材料需求和状态。
- 优先播放本地 MiMo TTS 语音缓存。
- 缓存未命中时，使用 Windows 本机 TTS 生成 WAV，并保存为本地缓存。
- 顶部只显示很小的 `G/H/J/K` 提示，尽量不遮挡游戏画面。

## 下载与安装

普通玩家不需要自己编译源码，建议直接到 Releases 下载插件包：

```text
CozyIslandReadAloud-v0.1.0.zip
```

安装步骤：

1. 先给游戏安装 BepInEx 5 x64。
2. 下载 Release 里的插件 zip。
3. 解压后，把 `CozyIslandReadAloud` 文件夹放到游戏目录：

```text
CozyIsland/BepInEx/plugins/CozyIslandReadAloud/
```

4. 启动游戏。
5. 进入游戏界面后按 `G/H/J/K` 测试朗读。

如果某段文字第一次没有本地语音缓存，插件会调用 Windows TTS 生成一次。之后再次遇到相同文本，会直接播放本地缓存。

## 为什么没有提交完整游戏文案

仓库里有一些目录被 `.gitignore` 忽略，这是有意设计，不是漏传。

没有提交的内容包括：

- `learning_text/`：本地提取出的学习文本和游戏文案片段。
- `audio_cache/`：本地生成的大量语音缓存。
- `deps/`：本地 BepInEx 依赖包。
- `dist/`：构建产物。
- `bin/`、`obj/`、`tmp/`：编译和临时文件。

原因：

- 完整游戏文案属于游戏内容，公开传播可能有版权风险。
- 语音缓存通常是根据游戏文本生成的，也不适合直接放进源码仓库。
- 缓存体积较大，会让 git 仓库变得很重。
- 插件运行时读取的是当前可见 UI 文本，普通使用不需要玩家提前提取文本。

所以这个仓库只开源插件代码、少量测试音频和辅助脚本。可安装的插件包会放在 GitHub Releases。

## 语音缓存说明

插件朗读时的查找顺序：

1. MiMo 预生成 WAV 缓存。
2. MiMo 分段缓存。
3. Windows TTS WAV 缓存。
4. Windows TTS 现场生成并保存缓存。

默认缓存位置：

```text
audio_cache/mimo/zh-CN/
audio_cache/windows/zh-CN/
```

从 Release zip 安装时，缓存会保存在插件目录下。用源码安装时，缓存会保存在本项目目录下。

## 从源码构建

准备条件：

- Windows
- Steam 版《舒舒服服小岛时光 / Cozy Island》
- .NET SDK
- BepInEx 5 x64

把 BepInEx 5 x64 解压到本项目的 `deps/` 目录，结构示例：

```text
deps/
  BepInEx_win_x64_5.4.23.2/
    BepInEx/
    winhttp.dll
    doorstop_config.ini
```

构建插件：

```powershell
.\build-package.ps1
```

构建产物会输出到：

```text
dist/CozyIslandReadAloud/
```

## 从源码安装

如果游戏安装在 Steam 目录：

```powershell
.\install-to-game.ps1 -GameDir "F:\Steam\steamapps\common\CozyIsland"
```

如果没有传 `GameDir`，脚本会尝试使用默认相对路径。建议明确传入游戏目录，比较稳。

## 批量生成 MiMo 语音

设置 `MIMO_API_KEY` 后可以批量生成语音缓存：

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

注意：批量语音缓存不建议提交到源码仓库。如果要个人备份，可以自行压缩保存。

## 常见问题

**按键没有声音怎么办？**

先确认游戏已经安装 BepInEx，插件 DLL 位于 `BepInEx/plugins/CozyIslandReadAloud/`。再查看 `BepInEx/LogOutput.log` 是否有插件加载日志。

**第一次按键为什么要等一下？**

如果没有命中 MiMo 或 Windows 缓存，插件会现场调用 Windows TTS 生成 WAV。生成完成后会自动播放，之后同一段文本会直接播放缓存。

**为什么工作台不读左侧一长串列表？**

工作台界面文字很多，全部朗读会很吵。插件检测到制作界面后，会优先朗读右侧当前选中配方的摘要，例如名称、说明、材料和是否不足。

**可以提交完整游戏文本吗？**

不建议。完整游戏文本来自商业游戏内容，公开提交会有版权风险。这个项目选择在运行时读取当前可见 UI 文本，而不是公开分发完整文本库。

## 开源协议

MIT License

## 友情链接

- [Linux DO](https://linux.do/)
- [M-JYuan/Koma](https://github.com/M-JYuan/Koma)

以上链接仅作为社区与开源项目参考，不代表本项目与对应项目存在从属或官方合作关系。
