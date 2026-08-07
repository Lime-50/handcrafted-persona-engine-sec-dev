# Persona Engine 开发架构

> 基于 `src/PersonaEngine` 源码梳理（.NET 9 / C#，x64）。
> 本文档描述项目的工程结构、模块职责、启动流程与关键设计约定，供开发与维护参考。

## 1. 项目概述

Persona Engine 是一个面向 VTuber / 虚拟主播场景的 **AI 角色实时对话引擎**：

- **完整语音链路**：麦克风 → VAD 检测 → Whisper 语音识别（ASR）→ LLM 生成回复 → TTS 合成 → 音频播放 + Live2D 口型/表情驱动；
- **桌面渲染**：Silk.NET（OpenGL）主窗口 + ImGui 控制面板，Live2D 模型、字幕、转盘（Roulette Wheel）等以组件形式渲染；
- **直播集成**：Spout（DX11 共享纹理）输出到 OBS；可选 D3D11 + DirectComposition 独立悬浮窗；
- **首次运行安装器**：Bootstrapper 按 Profile 分层从 HuggingFace / NVIDIA 分发源下载模型与 CUDA 运行时；
- **跨平台原生层**：Rust 实现屏幕捕获（xcap），通过 uniffi 生成 C# 绑定；原生库按 Windows/Linux/macOS 分发。

整体属于**模块化单体（Modular Monolith）**：一个解决方案、一个核心库内按领域分目录，通过依赖注入组合。

## 2. 解决方案与工程结构

```
PersonaEngine.sln
├─ PersonaEngine.App                    # 可执行启动器（Exe，程序集名 PersonaEngine）
├─ PersonaEngine.Lib                    # 核心库（全部业务/能力模块）
├─ PersonaEngine.Lib.Bootstrapper       # 首次运行资产安装器（依赖 Lib）
├─ PersonaEngine.Lib.Tests              # Lib 单元测试
└─ PersonaEngine.Lib.Bootstrapper.Tests # Bootstrapper 单元测试
```

依赖方向（单向、清晰分层）：

```
App ──> Lib.Bootstrapper ──> Lib
App ──> Lib
Tests ──> 各自被测项目（Lib 通过 InternalsVisibleTo 暴露内部成员）
```

### 2.1 PersonaEngine.Lib 目录结构

| 目录 | 职责 |
| --- | --- |
| `ASR/` | 语音识别：Silero VAD + Whisper 实时转写（Transcriber / VAD 子目录） |
| `Assets/` | 资产目录：安装清单（Manifest）、Feature 门控、用户资产监控 |
| `Audio/` | 音频层：麦克风输入、音频源抽象、播放器（Player）、重采样/格式转换 |
| `Configuration/` | 强类型配置（AvatarAppConfig 及全部 Options） |
| `Core/` | 应用主体（AvatarApp）+ 对话子系统（Conversation：抽象/实现分离） |
| `Health/` | 子系统健康探测（麦克风 / LLM / TTS） |
| `IO/` | 模型文件提供器（FileModelProvider）、模型类型常量 |
| `Live2D/` | Live2D 运行框架（Framework）+ 应用层（App）+ 行为（口型/眨眼/情绪） |
| `LLM/` | Semantic Kernel 聊天引擎、Kernel 提供器/热重载、连接探测 |
| `Logging/` | Serilog 配置与日志桥接 |
| `Music/` | 音乐源分离（MDX / MelBandRoformer，按需构造） |
| `Rust/` | Rust 原生库源码 + uniffi 生成的 C# 绑定 |
| `TTS/` | 语音合成：编排器、Kokoro/Qwen3 引擎、RVC 变声、口型、字幕对齐、敏感词 |
| `UI/` | 渲染组件框架、ImGui 控制面板、Overlay、Spout、字幕/转盘渲染 |
| `Utils/` | 数值、文本、ONNX、池化数组、WAV 等通用工具 |
| `Vision/` | 屏幕捕获 + 视觉问答（可选项） |
| `Resources/` | 提示词、着色器（GLSL/HLSL）、字体、原生库、模型（运行时下载） |

## 3. 启动流程（PersonaEngine.App/Program.cs）

启动顺序对原生运行时的解析至关重要，代码中顺序即约定：

1. **日志初始化**：Serilog 全局配置 + 全局异常处理器；
2. **原生库搜索目录**：注册 `<BaseDir>/native` 到 DLL 搜索路径；
3. **Bootstrapper（可选）**：`--skip-bootstrap` 或环境变量 `PERSONAENGINE_SKIP_BOOTSTRAP`（开发环境自动设置）时跳过；否则在独立 DI 容器中运行 `BootstrapRunner`，失败则以非零码退出；
4. **CUDA / LLama 预加载（顺序敏感）**：
   - 先 `PreloadCudaRuntime()` 映射 cudart/cublas/cufft/cudnn；
   - 再 `PreloadLlamaBackend()` 加载 cuda12/llama.dll（其传递依赖依赖上一步）；
   - 最后桥接 LLamaSharp 日志到 Serilog；
5. **配置加载 + 环境校验**：`appsettings.json` → `StartupValidator`（检查 CUDA、espeak-ng、提示词）；
6. **主 DI 组合**：`services.AddApp(config)`，构建 `AvatarApp`；
7. **运行**：`AvatarApp.Run()` → WindowManager（Silk.NET）主循环。

## 4. 依赖注入组合根（ServiceCollectionExtensions）

Lib 通过静态扩展方法暴露组合根，App 只负责搭容器：

```
AddApp(configuration)
├─ 构建 InstallManifest + IAssetCatalog（启动早期，供功能门控使用）
├─ AddConversation()
│  ├─ AddASRSystem()        # VAD + Whisper + 麦克风
│  ├─ AddTTSSystem()        # 文本处理 + Kokoro/Qwen3 + 口型 + 缓存
│  ├─ [门控] AddRVC()       # VoiceCloning
│  ├─ [门控] Audio2FaceLipSyncProcessor
│  ├─ AddLLM()              # Semantic Kernel + 连接探测 + 热重载
│  ├─ AddChatEngineSystem() # SemanticKernelChatEngine
│  ├─ [门控] AddVisualChatEngine()
│  └─ AddConversationPipeline() # 会话/输入门控/编排器
├─ AddUI()                  # 渲染组件、控制面板、窗口、Overlay
├─ AddLive2D()              # Live2D 管理器 + 行为服务
├─ AddSystemAudioPlayer()   # PortAudio 输出适配器
└─ AddPolly()               # LLM 调用弹性（超时 + 指数退避重试）
```

关键点：**可选项（RVC、Vision、Audio2Face、Qwen3）由 `IAssetCatalog.IsFeatureEnabled` 在 DI 构建期决定是否注册**，避免模型文件缺失时容器构建崩溃。资产在运行期新增需要重启应用才能接入 DI。

## 5. 功能分层与资产门控

### 5.1 安装 Profile（ProfileTier）

`Assets/Manifest/install-manifest.json` 是资产安装的**唯一事实来源**，每个资产标注 `profileTier`：

| Profile | 定位 | 典型资产 |
| --- | --- | --- |
| `TryItOut` | 开箱即用 | CUDA/cuDNN、Silero VAD、Whisper Tiny、Kokoro、OpenNLP、Aria Live2D、脏话过滤 |
| `StreamWithIt` | 主播向 | + RVC 变声（HuBERT/CREPE/RMVPE + 声音包） |
| `BuildWithIt` | 全功能 | + Whisper Turbo、Qwen3 TTS、wav2vec2 对齐、Audio2Face、MDX/MelBand 分离、Vision 标记 |

### 5.2 Feature 门控

- `FeatureIds` 定义功能标识（如 `Asr.WhisperTurbo`、`Tts.Rvc`）；
- 每个清单资产带 `gates`，资产安装后对应 Feature 自动启用；
- `FeatureProfileMap` 维护“功能 → 最低 Profile”，供控制面板显示升级提示（有 drift 测试防止与清单脱节）；
- `IAssetCatalog` 通过 `FileSystemWatcher` 监听资产目录变化并刷新功能缓存，UI 侧可实时刷新；DI 侧门控则只在启动时求值。

## 6. 对话子系统（Core/Conversation）

对话是系统核心，采用 **抽象（Abstractions）/ 实现（Implementations）分离 + 状态机 + 事件流** 设计：

### 6.1 会话编排（ConversationOrchestrator）

- 管理**多个并发会话**（`ConcurrentDictionary<Guid, Session>`），支持启停/暂停/恢复/取消/重试的扇出；
- 转发会话状态变更事件，供 UI（状态指示灯、仪表盘）订阅；
- 应用启动后自动创建第一个会话。

### 6.2 会话状态机（ConversationSession + Stateless）

```
Initial ─→ Initializing ─→ Idle ─→ Listening ─→ ProcessingInput ─→ WaitingForLlm
                                                                     │
                          ┌──────────────────────────────────────────┤
                          ▼                                          ▼
                     Idle ←────── Speaking ←────── StreamingResponse
                             (AudioStreamEnded)        (AudioStreamStarted)

Idle/ActiveTurn 可进入：Interrupted（Barge-in）、Cancelled、Paused、Error、Ended
```

- 触发源：输入检测/定稿、LLM 流式事件、TTS 流式事件、音频事件、取消/重试/停止；
- **Barge-in（抢话）**：`IBargeInStrategy` + `BargeInType`（配置 `Conversation.BargeInType`），仅在满足策略时允许打断，未满足则忽略；
- 所有流事件在非目标状态一律 `Ignore`，防止陈旧事件引发状态错乱。

### 6.3 单轮流水线（TurnPipelineCoordinator）

每轮对话用 `Channel` 串联三阶段，支持取消与打断：

```
IChatEngine (LLM 流式输出)  →  Channel<LlmChunkEvent>  →  ITtsEngine (TtsOrchestrator)
                                                          →  Channel<TtsChunkEvent>
                                                          →  IOutputAdapter (音频播放)
```

事件模型：`IInputEvent`（STT 检测/定稿）、`IOutputEvent`（Llm/Tts/Audio 生命周期 + 分块），由事件发射器统一携带 SessionId / TurnId。

### 6.4 输入输出适配器

- `IInputAdapter`：麦克风输入适配（`MicrophoneInputAdapter`，NAudio 采集）；
- `IOutputAdapter`：PortAudio 输出适配（`PortaudioOutputAdapter`）；
- 附带振幅提供器（`IAudioAmplitudeProvider`）、进度通知（`IAudioProgressNotifier`）驱动 UI 电平与字幕；
- `IConversationInputGate` / `IMicMuteController` 负责输入节流与静音。

## 7. 能力模块详解

### 7.1 ASR（语音识别）

| 组件 | 说明 |
| --- | --- |
| Silero VAD | ONNX 模型，`SileroVadDetector` + 概率提供器，按阈值/最短语音/静音时长切分 |
| Whisper 转写 | `RealtimeTranscriptor`：主识别（Tiny 或 Turbo v3）+ 独立 Tiny 通道用于 Barge-in 检测 |
| 模型选择 | 按 Profile 门控：BuildWithIt 用 Turbo + Tiny 双通道，其余仅 Tiny |
| 麦克风 | `MicrophoneInputNAudioSource`（NAudio），`IAwaitableAudioSource` 适配 |

### 7.2 LLM（大模型对话）

- **Semantic Kernel 1.74**：`SemanticKernelLlmEngine` 实现 `IChatEngine`，流式输出到 Channel；
- `LlmKernelProvider`：按配置构建 Kernel，支持 OpenAI 兼容端点（DeepSeek/Groq 等）与本地 LLamaSharp（`llama-3.3-70b` 示例）；
- `KernelReloadCoordinator`：配置变更（`IOptionsMonitor`）时热重建 Kernel，并以 `Lazy<ILlmKernelProvider>` 打破 DI 循环依赖；
- `LlmConnectionProbe`：命名 HttpClient（5 秒超时）+ Polly 弹性管线（60 秒超时、指数退避重试 3 次、抖动）；
- 文本过滤链：`ITextFilter`（姓名过滤、情绪处理器等）。

### 7.3 TTS（语音合成）

```
LLM Chunk → TextNormalizer → SentenceSegmenter(OpenNLP) → IncrementalSentenceAccumulator
   → SentenceProcessor → ISentenceSynthesizer（Kokoro / Qwen3）
   → AudioFilterPipeline（脏话哔声、RVC 变声）
   → TtsEventEmitter → 音频输出 + 口型/字幕事件
```

- **TtsOrchestrator**：引擎无关编排，按句增量合成、流式下发，支持运行时引擎切换（`ITtsEngineProvider`）；
- **Kokoro（默认）**：ONNX 模型 + G2P 音素管线（OpenNLP POS + Lexicon + eSpeak 回退 + 音素映射）；
- **Qwen3（可选）**：GGUF 模型（LLamaSharp 推理）+ 流式解码 + 采样器 + 语音嵌入；
- **Doubao（云端）**：火山引擎豆包语音 V3 HTTP SSE 单向流式 API（`openspeech.bytedance.com/api/v3/tts/unidirectional/sse`），按句请求、base64 音频解码为 PCM float；无本地模型，配置 `Config:Tts:Doubao`（API Key / 音色 / 语速等），`ActiveEngine` 设为 `doubao` 即切换；
- **RVC 变声（可选）**：HuBERT 内容编码 + CREPE/RMVPE 音高估计 + 转换网络；
- **口型**：`VBridgerLipSyncProcessor`（默认，逐词时间线）或 `Audio2FaceLipSyncProcessor`（可选）；ARKit 到 Live2D blendshape 映射；
- **字幕对齐**：`CtcForcedAligner`（wav2vec2，随 Qwen3 门控）提供词级时间戳；
- **缓存**：`TtsMemoryCache`；**敏感词**：`ProfanityDetector` + `BlacklistAudioFilter` 哔声。

### 7.4 Live2D

- `Live2D/Framework`：自维护的 Live2D 运行时（模型/动作/物理/渲染/表情）；
- `Live2D/App`：LApp 应用层（纹理、模型管理、交互）；
- `Live2D/Behaviour`：行为服务——口型动画（`LipSyncAnimationService`）、眨眼（`IdleBlinkingAnimationService`）、情绪（`EmotionService` + 文本/音频过滤）；
- 通过 `IRenderComponent`（`Live2DManager`）接入主渲染循环，并作为 Spout 输出源。

### 7.5 UI / 渲染

**组件模型**：`IRenderComponent`（Initialize / Update / Render / Resize，含优先级与 Spout 目标），`AvatarApp` 按优先级排序后分组渲染。

```
AvatarApp（主循环）
├─ 常规组件：Live2DManager、SubtitleRenderer、RouletteWheel、ControlPanelComponent 等
└─ Spout 组件组：按 SpoutTarget 分组，渲染到独立共享纹理（OBS 可捕获）
```

- **主窗口**：`WindowManager`（Silk.NET/GLFW，隐藏边框）+ `ImGuiController`（Hexa.NET.ImGui）控制面板；
- **控制面板**：Dashboard（健康卡片/会话统计）、Voice（音色/试听/克隆）、Personality（提示词/话题）、Listening（麦克风/识别/打断）、Avatar（模型/口型）、Subtitles、Overlay、RouletteWheel、LLM 连接；配置修改经 `ConfigWriter` 反射写回 `appsettings.json`；
- **字幕**：FontStashSharp 文本渲染 + 词级时间线/动画/换行；
- **Spout**：`SpoutRegistry` / `SpoutManager` 管理 DX11 共享纹理输出；
- **Overlay**：独立线程 + D3D11 + DirectComposition 原生窗口，通过 Spout 消费 Live2D 帧，实现低延迟拖拽/缩放悬浮窗（状态机驱动生命周期）。

### 7.6 Vision / Music / Health

- **Vision（可选）**：Rust xcap 屏幕捕获 + `VisualQAService` / `VisualQASemanticKernelChatEngine` 视觉问答；
- **Music（可选）**：MDX / MelBandRoformer ONNX 音源分离，按需构造（非 DI 注册），模型存在性在加载时校验；
- **Health**：`ISubsystemHealthProbe`（麦克风/LLM/TTS），注册顺序决定仪表盘卡片顺序。

## 8. Bootstrapper（首次运行资产安装）

```
BootstrapRunner.RunAsync
├─ GPU 预检（nvidia-smi + nvcuda 探测，--skip-gpu-check 可跳过）
├─ 读 install-state.lock.json
├─ 交互选择 Profile（Spectre.Console）或 CLI 指定
├─ AssetPlanner.Compute：对比清单与磁盘 → 下载/跳过/重下/重验
├─ 下载（HuggingFace / NVIDIA redist，SHA256 校验，zip 解压）
└─ 写 install-state.lock.json（断点续装）
```

- 支持 `--non-interactive`（静默安装）、`--offline`（仅校验缺失）、`--bootstrap <profile>`；
- 资产落在 `<BaseDir>/Resources/<子系统>`，与开发树（F5）布局一致，保证 Dev/Release 路径解析一致；
- 下载使用带重试的命名 HttpClient（`AddAssetDownloadHttpClient`）。

## 9. 原生层与运行时依赖

### 9.1 Rust 原生库

- `Rust/`：crate `rust-lib`，xcap 屏幕捕获，uniffi 生成 C# 绑定（`Rust/bindings/rust_lib.cs`）；
- 编译产物（.dll/.so/.dylib）由 csproj 复制到输出 `native/` 目录。

### 9.2 第三方原生库

- `Resources/native/{windows,linux,macos}`：跨平台分发（OnnxRuntime GPU、Whisper 后端等）；
- **CUDA/cuDNN 不打包**：由 Bootstrapper 下载到 `Resources/cuda/{cudart,cublas,cufft}` 与 `Resources/cudnn`，`Program.cs` 按顺序 `LoadLibraryEx` 预加载，供 OnnxRuntime GPU / Whisper / llama.cpp 解析导入。

### 9.3 主要 NuGet 依赖

| 领域 | 依赖 |
| --- | --- |
| AI 推理 | Microsoft.ML.OnnxRuntime(.Gpu)、LLamaSharp(+Cuda12)、Whisper.net、Microsoft.ML.Tokenizers |
| LLM | Microsoft.SemanticKernel（Core/Abstractions/Connectors.OpenAI/Yaml） |
| 渲染 | Silk.NET、Hexa.NET.ImGui(+Backends/Widgets/ImNodes)、FontStashSharp、Vortice D3D11/DXGI/DirectComposition |
| 音频 | NAudio、PortAudioSharp2、Aurio.LibSampleRate、Spout.NETCore |
| 基础设施 | Microsoft.Extensions.*、Polly、Stateless、Serilog、Spectre.Console、SixLabors.ImageSharp、OpenNLP、MathNet.Numerics |

## 10. 配置体系

单一 `appsettings.json`，根节点 `Config`，绑定到 `AvatarAppConfig`：

| 配置段 | 用途 |
| --- | --- |
| `Window` | 主窗口尺寸/标题 |
| `Llm` | 文本/视觉端点、模型、API Key |
| `Asr` / `Microphone` | VAD 阈值、Whisper 提示、麦克风设备 |
| `Tts` | 活动引擎、Kokoro/Qwen3/RVC 参数、试听样本 |
| `LipSync` | 口型引擎（VBridger / Audio2Face） |
| `Subtitle` | 字幕字体/颜色/布局/动画 |
| `Live2D` / `SpoutConfigs` / `Overlay` | 模型、Spout 输出、悬浮窗 |
| `Vision` | 屏幕捕获窗口与间隔 |
| `RouletteWheel` | 转盘外观/行为 |
| `Conversation` / `ConversationContext` | 抢话策略、系统提示词、话题 |

控制面板修改通过 `ConfigWriter`（基于 `AvatarAppConfig` 类型层次反射发现 section 路径）写回文件；运行中变更由 `IOptionsMonitor` 生效。

## 11. 测试策略

两个 xUnit 测试工程，覆盖核心风险点：

- **ASR**：VAD 概率提供器；
- **Assets**：目录门控、FeatureProfileMap 与清单 drift 校验、清单验证、用户内容 watcher；
- **Conversation**：输入门控、静音控制、轮次指标；
- **LLM**：连接探测、Kernel 提供器；
- **Live2D/TTS**：ARKit 映射、blendshape 求解、口型时间线、NpyReader、Qwen3 CTC 置信度、RVC 并发、字幕动画、Kokoro token 转换、G2P 音素化、增量分句；
- **UI**：布局、Easing、粒子、Overlay 状态机、着色器注册、控制面板各组件；
- **Bootstrapper**：规划器、下载器、HuggingFace/NVIDIA 客户端、SHA256 校验流、锁存储、清单序列化、GPU 预检；
- 着色器测试资源含 ASCII / 非 ASCII 用例（防止中文注释破坏 GLSL 编译）。

## 12. 构建与发布

- **平台**：x64（Debug/Release 均 `PlatformTarget=x64`）；
- **开发运行**：`dotnet run` 时 `launchSettings.json` 设置 `PERSONAENGINE_SKIP_BOOTSTRAP=1`，跳过资产下载；
- **发布**：`win-x64` 自包含单文件（`PublishSingleFile`），发布后原生 DLL 统一移动到 `native/` 子目录，保持根目录干净；
- **工具**：`.config/dotnet-tools.json` 管理 dotnet 本地工具。

## 13. 关键设计约定（维护须知）

1. **清单即事实**：资产安装、Feature 门控、Profile 分层都以 `install-manifest.json` 为准；`FeatureProfileMap` 必须与其同步，靠 drift 测试兜底；
2. **可选项必须门控注册**：新增依赖模型文件的子系统时，参照 RVC/Visual/Audio2Face 的模式用 `IAssetCatalog.IsFeatureEnabled` 条件注册，否则低 Profile 用户会在 DI 构建期崩溃；
3. **原生加载顺序敏感**：任何涉及 CUDA 的加载都必须放在 `PreloadCudaRuntime()` 之后；新加原生依赖时遵循“先 CUDA 运行时 → 再依赖它们的后端 → 再日志桥接”的顺序；
4. **会话状态机谨慎扩展**：所有流式事件在无关状态必须显式 `Ignore`，Barge-in 用 guard 而非硬迁移；
5. **DI 循环依赖**：用 `Lazy<T>` 延迟解析（如 Kernel 提供器与聊天引擎）；
6. **配置写入**：新增配置项时同步更新 `AvatarAppConfig`、`appsettings.json` 与 `appsettings.template.json`（模板是发布时的干净样本）；
7. **渲染组件**：接入新界面时实现 `IRenderComponent` 并注册为单例；需输出到 OBS 的组件设置 `UseSpout=true` 与 `SpoutTarget`。

## 14. 数据流总览

```
                     ┌─────────────────────────────┐
  麦克风(NAudio) ──→ │  VAD(Silero) + Whisper ASR   │──→ STT 事件
                     └─────────────────────────────┘
                                   │ IInputEvent
                                   ▼
                     ┌─────────────────────────────┐
                     │  ConversationSession 状态机  │
                     │  （Barge-in / 取消 / 重试）    │
                     └─────────────────────────────┘
                                   │
                                   ▼
                     ┌─────────────────────────────┐
  SemanticKernel ──→ │  LLM 流式回复                │──→ LlmChunkEvent
                     └─────────────────────────────┘
                                   │
                                   ▼
                     ┌─────────────────────────────┐
                     │  TtsOrchestrator            │──→ TtsChunkEvent + 口型/字幕事件
                     │  (Kokoro/Qwen3 + RVC + 脏话) │
                     └─────────────────────────────┘
                                   │
                                   ▼
                     ┌─────────────────────────────┐
  PortAudio 输出 ──→ │  AudioFilterPipeline         │──→ 音频播放
                     └─────────────────────────────┘
                                   │
                                   ▼
              Live2D 口型/表情 + 字幕 + Spout 输出 + Overlay
```
