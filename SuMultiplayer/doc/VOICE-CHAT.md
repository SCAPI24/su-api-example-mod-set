# 实时语音（SuMultiplayer / voice.proto）

面向 `Mod/SuMultiplayer` 的**实时语音聊天**实现说明：可行性结论、采集 / 播放机制、
带宽口径、协议与协商、静音 / 中继规则、健壮性矩阵、验证结果与设备清单。

代码入口：

| 文件 | 作用 |
|---|---|
| `Voice/VoiceFormat.cs` | 8 kHz / 20 ms 帧 / 40 ms 包 / 各档位载荷字节的唯一常量 |
| `Voice/VoiceQuality.cs` | 档位清单（high / standard / low / ultra）与线上 format id |
| `Voice/VoiceAdpcm.cs` | 4 / 3 / 2-bit IMA/DVI ADPCM（每包自包含，无跨包状态） |
| `Voice/VoiceCvsd.cs` | 1-bit CVSD（ultra：音节压扩的 1-bit 增量调制） |
| `Voice/VoiceCodec.cs` | 按档位分发到 ADPCM / CVSD 的唯一入口（未知档位明确失败） |
| `Voice/VoiceActivityDetector.cs` | 能量 + 过零率 VAD，默认 160 ms 挂起（可配置） |
| `Voice/VoicePacketizer.cs` | 20 ms 帧 → 40 ms 包（末包静音补齐） |
| `Voice/VoiceJitterBuffer.cs` | 每说话者 jitter（预灌 2 包 / 缺包 70 ms / 迟到丢弃 / 8 包上限） |
| `Voice/VoicePeerRoster.cs` / `VoiceCounters.cs` / `VoiceOptions.cs` | 名册 / 静音表 / 计数 / 参数 |
| `Net/VoiceNet.cs` | `VoicePacket` 严格校验、线上字节口径、中继路由纯函数 |
| `Modules/VoicePorts.cs` | `IVoiceAudioPort`（引擎侧端口） |
| `Modules/VoiceChatModule.cs` | 采集 → VAD → 编码 → 发送 / 主机 fan-out / 接收 → jitter → 播放；**唯一的 VOICE 开关 + 权限状态机** |
| `Engine/VoiceAudioEngine.cs` | OpenAL Soft `alcCapture*` 采集 + `alSourceQueueBuffers` 流播放（P/Invoke）；Android 录音权限请求 / 结果 / 设置页 |
| `Voice/VoiceToggleGate.cs` | U 键边沿判定（一次按下 = 一次切换；文本输入抑制）纯逻辑 |
| `Voice/VoiceHudIcon.cs` | 图标位置规则 / ON·OFF 视觉映射 / 状态变化计数（纯逻辑，可被 `P:\tmp\tsverify` 直接自检） |
| `UI/VoiceHud.cs` | 右下角语音状态图标（聊天按钮正上方，点按 / U 键，ON 时右侧 5 格音量条）+ TALK（可选 PTT，仅点按）；**状态行与 SPEAKING 行都已按产品要求删除（2026-10-21）**，说话状态只进日志 / 计数；静音入口是 MoreContents 的 `VO 按钮` |
| `Voice/VoiceMicMeter.cs` | 麦克风电平 → 5 格音量条：**统一 dBFS 标尺**（`20·log10(mean/32768)`，floor −50 / full −8.5 dBFS）/ attack·release 平滑 / 停滞衰减 / 格数 + 10 s 统计窗口与采集帧对账（纯逻辑，可被 `P:\tmp\tsverify` 直接自检） |
| `Protocol/voice.proto` | `VOICE_PACKET` = 85 … `VOICE_CONTROL` = 90（append-only） |

---

## 1. 结论（选定的跨平台路径）

**采集与播放都走游戏自带的 OpenAL Soft**，不新增任何原生库：

- Windows：`openal32.dll`（随游戏发布，导出 `alcCaptureOpenDevice` 等 5 个采集符号）。
- Android：`libopenal.so`（`Survivalcraft/libs/arm64-v8a/libopenal.so`，同样导出 5 个采集符号，
  含 `ALC_EXT_CAPTURE` 字符串，OpenSL 后端）。

实现只 P/Invoke `alcCaptureOpenDevice / alcCaptureStart / alcCaptureStop /
alcCaptureSamples / alcCaptureCloseDevice` 与
`alGenSources / alGenBuffers / alBufferData / alSourceQueueBuffers /
alSourceUnqueueBuffers / alSourcePlay / alSourceStop / alSourcef / alSource3f /
alGetSourcei / alGetError`。两个平台各一个 `DllImport` 壳，运行期按
`RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` 选择；未使用的平台的
`DllImport` 永远不会触发加载。

**播放复用引擎已经 `alcMakeContextCurrent` 的上下文**（不自建设备 / 不切换 context），
每个说话者一条 OpenAL source + 6 × 40 ms 环形 buffer（240 ms 上限），
jitter 缓冲喂 40 ms PCM，播放增益 / 位置只动这条 source，**不碰引擎的 Sound 列表**。

**编解码是托管 IMA ADPCM / CVSD，档位可配置**（AOT 安全、无新增原生库；默认
`low` = 2-bit ADPCM）。游戏没有随包发布
Opus/其它原生编解码器：`openal32.dll` 与 `libopenal.so` 中 `opus` 字符串为 0，
部署目录也没有 `opus*.dll` / `libopus*.so`。因此托管 ADPCM 是唯一可移植路径。

---

## 2. Phase 0：引擎音频与采集证据

### 2.1 引擎播放链（游戏音效）

- `Survivalcraft/Game/SubsystemAudio.cs:98` / `:112` / `:118`：`PlaySound(...)`
  只把条目排进 `m_queuedSounds`，**非虚**，派生类无法用 `new` 截获。
- `Survivalcraft/Game/SubsystemAudio.cs:176` `Update`；`:196` 真正播放：
  `AudioManager.PlaySound(...)`。
- `Survivalcraft/Game/AudioManager.cs:11` `PlaySound` → `:22`
  `new Sound(ContentManager.Get<SoundBuffer>(name), ...).Play()`；
  `AudioManager.MinAudibleVolume` = `0.05f * SettingsManager.SoundsVolume`（`:9`）。
- `Engine/Engine/Audio/BaseSound.cs:118` `Play()` → `:125` `InternalPlay()`；
  `Sound.cs:71` → `AL.SourcePlay`。
- 一次性播放的根因：`Sound` 构造时把一个完整 `SoundBuffer` 绑到 source
  （`Sound.cs:33-50`，`SoundBuffer.cs:54-69`），没有队列/持续供给接口。

### 2.2 引擎的 OpenAL 绑定

- **Windows**：`Engine/Engine/Audio/Mixer.cs:35` `Initialize()` →
  `:42` `new AudioContext()`（OpenTK，`OpenTK/OpenTK.Audio/AudioContext.cs`）；
  `Sound.cs` / `SoundBuffer.cs` 用 `OpenTK.Audio.OpenAL.AL`。
  OpenTK 的 ALC 绑定**已经包含采集**：
  `OpenTK/OpenTK.Audio.OpenAL/Alc.cs:166` / `:171` `alcCaptureOpenDevice`、
  `:179` `alcCaptureStart`、`:183` `alcCaptureStop`、`:187` `alcCaptureSamples`、
  `:175` `alcCaptureCloseDevice`；上层 `OpenTK/OpenTK.Audio/AudioCapture.cs:88-133`。
- **Android**：`Engine/Engine/Audio/Mixer.Android.cs:41` `Initialize()` →
  `:45` `Alc.OpenDevice(null)`、`:47` `Alc.CreateContext`、`:49` `MakeContextCurrent`；
  绑定在 `Engine/Engine/Audio/Alc.cs:19-35`，**只有** open/close/create/destroy/
  makeCurrent/getError，没有 capture —— 但**同一个 `libopenal.so` 本身有捕获导出**，
  所以我们在 mod 里自己声明 `libopenal.so` 的 `alcCapture*` 即可，不需要改引擎。
- 原生库符号证据：

  ```text
  objdump -T Survivalcraft/libs/arm64-v8a/libopenal.so | grep alcCapture
    alcCaptureStart / alcCaptureStop / alcCaptureSamples /
    alcCaptureCloseDevice / alcCaptureOpenDevice
  objdump -p publish/[SuAPI]Survivalcraft/openal32.dll | grep alcCapture
    alcCaptureCloseDevice / alcCaptureOpenDevice / alcCaptureSamples /
    alcCaptureStart / alcCaptureStop
  ```

### 2.3 为什么不用 OpenTK 的 `AudioCapture`

`OpenTK/OpenTK.Audio/AudioDeviceEnumerator.cs:39` 的**静态构造**会创建一个 dummy
device/context，并在 `finally` 里 `Alc.MakeContextCurrent(ContextHandle.Zero)`
（`:110`）—— 这会把引擎当前 context 解绑。之后引擎所有 AL 调用都会失败
（声音整体变哑）。`AudioCapture` 的静态构造会触发它（`AudioCapture.cs:65-68`）。
因此 `Engine/VoiceAudioEngine.cs` 只 P/Invoke 需要的函数，**绝不实例化
`AudioCapture` / 访问 `AudioDeviceEnumerator`**。

### 2.4 采集设备实测（Windows / 本机）

用与生产相同的 P/Invoke 写了一个 .NET 8 探针（`openal32.dll`，从游戏目录加载）：

```text
captureList     = OpenAL Soft on Microphone (USBAudio2.0)
captureDefault  = OpenAL Soft on Microphone (USBAudio2.0)
alcCaptureOpenDevice(null, 8000, AL_FORMAT_MONO16, 1600) → 非 0（成功）
500 ms 后 available=3920 样本
alcCaptureSamples → err=0，RMS=289.6，peak=1526（真实环境噪声）
```

同一探针还验证了播放在同一 context 下的队列语义：

```text
alcOpenDevice / alcCreateContext / alcMakeContextCurrent → current != 0
alGenSources/alGenBuffers 成功
queue 4 × 40 ms (320 样本 @ 8 kHz) → AL_BUFFERS_PROCESSED 逐步增长，
播放完 state = AL_STOPPED（0x1014），需要 unqueue + 重新 play
采集与播放可同时进行（300 ms 内又采到 2320 样本）
```

### 2.5 权限（Android，**只在用户把语音切 ON 时请求**）

- `Survivalcraft/AndroidManifest.xml:52` 已声明 `RECORD_AUDIO`（**唯一**的 APK 级改动；
  其余全部在 mod 内）。只把 `.scmod` 推进旧 APK 不会获得录音权限，
  Android 端必须用带该权限的 APK。
- 运行时权限走引擎自己的 Activity（反射 `Engine.EngineActivity.m_activity`，
  与旧实现同一条路径；`Engine/Engine/EngineActivity.cs:18`）：
  - `RequestPermission` → `Activity.RequestPermissions(RECORD_AUDIO, 0x5331)`；
    结果由 `EngineActivity.OnRequestPermissionsResult` →
    `PermissionsChanged(requestCode)` 事件带回（`VoiceAudioEngine` 反射订阅，
    只认 requestCode=0x5331），模块下一次 Tick 用 `TryTakePermissionResult` 取走；
  - `QueryPermission` 只读 `CheckSelfPermission` +
    `ShouldShowRequestPermissionRationale`，**绝不弹窗**；
  - `TryStartCapture` 也**只检查、不请求** —— 采集流程只在
    `IsVoiceOn=true` 且权限已授予时才打开设备（“只在实际需要麦克风时请求”）。
- 状态机（`Modules/VoicePorts.cs` 的 `VoicePermissionState`，逻辑在
  `Modules/VoiceChatModule.cs`）：

| 状态 | 何时进入 | 行为 |
|---|---|---|
| `Unknown` | 本会话还没请求过、当前未授权 | 第一次 ON → 发请求（`voicePermissionRequested++`） |
| `Granted` | 已授权或收到授权结果 | 直接打开；**本会话后续 ON 不再弹窗**（`voicePermissionGranted++`） |
| `Denied` | 用户拒绝（第一次拒绝 / rationale=true） | 语音保持 OFF、**不启动采集**；HUD 提示；**再 ON 重新请求**（`voicePermissionDenied++`，不永久锁死） |
| `PermanentlyDenied` | “不再询问”（rationale=false 且有拒绝记录） | 语音保持 OFF；提示 + `OpenAppSettings` 打开应用设置页（`voicePermissionPermanentlyDenied++` / `voicePermissionSettingsOpened++`）；在设置里授权后按 U 即可 |
| `Unavailable` | 拿不到 Activity / 权限对象 / 权限未在 APK 声明 / 请求发不出去 | 明确失败 + 原因（`voicePermissionUnavailable++`），不弹窗、不启动采集 |
| `NotRequired` | Windows | 直接打开，不涉及运行时权限 |

- 设置页：`VoiceAudioEngine.OpenAppSettings` 反射
  `Android.Provider.Settings.ActionApplicationDetailsSettings` +
  `Intent.SetData(Uri.Parse("package:<包名>"))` + `Activity.StartActivity`；
  任何一步缺失都是**明确失败**（原因写进日志 / HUD），不静默换路径。
- 关闭语音 / 离开房间 / 断线 → 立刻 `StopCapture`
  （`alcCaptureStop` + `alcCaptureCloseDevice`），释放麦克风；若 Android 权限结果
  在关闭 / 离开之后才回来，只记录授权状态，**不会自动开麦**。
- 计数（`VoiceCounters`，10 s 一行日志 + `StatusLine` 都包含）：
  `voicePermissionRequested / Granted / Denied / PermanentlyDenied / SettingsOpened /
  Unavailable`（累计值）；失败原因同时写进 `VoiceState.reason`，主机日志可见。
  `voiceNoPermission` 仍保留给“采集时发现权限被撤销”的路径。

---

## 3. 编解码与带宽（档位可配置，默认 low）

- 采集：8 kHz、mono、16-bit（`alcCaptureOpenDevice(..., 8000, AL_FORMAT_MONO16, 1600)`）。
- VAD：`mean(|sample|)` 起始 350 / 维持 180 + 过零率 ≤ 0.30；语音结束后
  继续发 **8 帧（160 ms，旧默认 260 ms）** 挂起；静音帧**完全不发包**。
  挂起时长由 `voicevadhangover` 配置（0–2000 ms）。
- 分包：20 ms 帧 → 2 帧 = 40 ms 包（320 样本，恒定 25 包/秒）。
- 每包自包含块：`int16 predictor + uint8 index/step`（3 B）+ 量化数据。
  自包含的代价是 75 B/s，换来丢包 / 乱序 / 中途加入都不会让解码器永久错位；
  同一说话者中途切档时接收端逐包按 `format` 解码，不需要重新握手。

| 档位 | 编码 | 每包载荷 | 实测 protobuf | 含 16 B TCP | 25 包/秒 | 净荷 |
|---|---|---:|---:|---:|---:|---:|
| `high` | 4-bit IMA ADPCM | 163 B | 170 B | 186 B | 4650 B/s（37.2 kbit/s） | 32.6 kbit/s |
| `standard` | 3-bit IMA ADPCM | 123 B | 129 B | 145 B | 3625 B/s（29.0 kbit/s） | 24.6 kbit/s |
| `low`（默认） | 2-bit IMA ADPCM | 83 B | 89 B | 105 B | **2625 B/s（21.0 kbit/s）** | 16.6 kbit/s |
| `ultra` | 1-bit CVSD | 43 B | 49 B | 65 B | 1625 B/s（13.0 kbit/s） | 8.6 kbit/s |

（实测值来自 `P:\tmp\tsverify` 的 VC16：`VoicePacket.CalculateSize()` + 16 B TCP 帧头，
25 包/秒，仅在说话时。任务书里的 4.6 KB/s / 36.8 kbit/s 与 high 档实测一致；
low / ultra 任务书按更小的固定开销估算，本实现把 3 B 块头、protobuf 字段与
16 B TCP 帧头都算进去了，因此实测略高。）

**位打包与量化表（2 / 3-bit 变体由 4-bit IMA 公式严格化简，不是另抄的表）：**

- step 表沿用标准 89 项 IMA 表（7 … 32767）；index 夹在 0..88。
- 重构电平：`diff = step / 2^(N-1) + Σ bit_i × step / 2^(N-2-i)`，符号位取负。
  N=4 时逐位等于标准 IMA（`step>>3 + bit×step + bit×step>>1 + bit×step>>2`）。
- index 调整：幅度 `v = code & (2^(N-1)-1)`；
  `v < 2^(N-2)` → `-1`，否则 `+2×(v-2^(N-2)+1)`。
  于是 N=4 → `{-1,-1,-1,-1,2,4,6,8}`（标准表），N=3 → `{-1,-1,2,4}`，
  N=2 → `{-1,2}`（符号位镜像）。编码用同一套阈值逐次逼近，并用同一个 code
  更新状态，保证编码器 / 解码器逐样本一致。
- 位打包：低位先写（LSB-first）；N=4 与旧实现逐字节相同（低 nibble 在前）；
  N=3 时 8 个码字占 3 字节，N=2 时 4 个码字占 1 字节；载荷在写入前先清零，
  避免上一包残留位被当成本包数据。
- `ultra` 用 CVSD：每样本 1 bit（8 kbit/s），连续 4 个相同 bit → step 增长
  （`step += step>>3 + 1`），否则衰减（`step -= step>>5`），step 夹在
  [16, 4080]；块头存初始 predictor + step/16，编码 / 解码共用同一个
  `Advance` 状态推进。它是"噪声大但可懂"的最后手段。

实测可懂度（VC16，200 Hz 音调 / 140+280+700 Hz 类语音，8 kHz）：

| 档位 | 音调 SNR | 类语音 SNR | 类语音相关 | 结论 |
|---|---:|---:|---:|---|
| high | 20.0 dB | 17.4 dB | 0.991 | 与原来一致 |
| standard | 14.7 dB | 13.9 dB | 0.980 | 清晰 |
| low | 12.4 dB | 10.4 dB | 0.957 | 会话可懂 |
| ultra | 10.9 dB | 9.1 dB | 0.940 | 有噪声、可懂 |

- 静音：0 包 / 0 字节（VAD 直接跳过）；PTT 模式未按下同样 0。
  全静音载荷的残差：high 0 / standard ≤1 / low ≤3 / ultra ≤256（均 ≤ -42 dBFS）。
- 平均：正常对话占空比约 30–50%；low 档连续说话 2.6 KB/s，平均约 0.8–1.3 KB/s。
- 传输复用现有 TCP 通道（没有另开 UDP）：世界传输期间可能出现队头阻塞；
  接收端 jitter 缓冲（80 ms 预灌 / 最多 320 ms）吸收短时抖动，极端情况下
  表现为语音延迟而不是数据损坏（TCP 有序可靠，因此不会出现丢包噪声）。
- 计数口径（`VoiceCounters` / 10 s 日志 / HUD）：
  `voiceCaptureFrames / Sent / Recv / Relayed / Played / SilenceSkipped /
  LateDropped / voiceQuality / voicePacketBytes / voiceBytesPs / EncodeFail /
  NoDevice / NoPermission`
  以及 `sentBytesPs / recvBytesPs / malformed / formatRejected / sendFailures /
  queueDropped / duplicate / gapSkipped / overflow / relayMuted / relayDisabled /
  controlSent`。权限流程另有 `voicePermissionRequested / Granted / Denied /
  PermanentlyDenied / SettingsOpened / Unavailable`（见 2.5）。
  `voiceBytesPs` 是 1 s 窗口的**双向**字节速率（含 TCP 帧头与控制消息）；
  `voiceQuality` / `voicePacketBytes` 是当前生效档位与该档每包载荷。

---

## 4. 协议与协商（`Protocol/voice.proto`）

| id | 消息 | 方向 | 作用 |
|---:|---|---|---|
| 85 | `VoicePacket` | 客户端→主机；主机→各监听者 | source / sequence / flags / **format** / 档位载荷（43–163 B） |
| 86 | `VoiceState` | 客户端→主机 | supported / mic_enabled / ptt / reason / **format**（连接时 + 状态变化 + 5 s 心跳） |
| 87 | `VoiceRoster` | 主机→客户端 | 权威名册（含主机 id=0；未声明支持的端不出现） |
| 88 | `VoiceMuteRequest` | 客户端→主机 | target / muted |
| 89 | `VoiceMuteResult` | 主机→客户端 | accepted / reason（失败时客户端回滚） |
| 90 | `VoiceControl` | 主机→客户端 | 会话语音是否启用 + reason |

严格校验（`Net/VoiceNet.cs`）：

- 客户端→主机：`source_client_id` 必须 = 0（不能冒充别人）；主机→客户端：≥ 0。
- `sequence` 必须 ≥ 1 且单调（每说话者）；`flags` 只允许 bit0(start)/bit1(end)。
- `format` 必须是 1..4（high / standard / low / ultra，append-only）；0 = 未声明、
  其它值 = 未知 → 整包拒绝（`formatRejected`），**绝不按默认档位解码**。
- `data` 长度必须恰好等于该档位载荷（163 / 123 / 83 / 43）；否则整包拒绝
  （`voice-data-L-for-<档位>`，计入 `Malformed`），绝不部分应用。

主机 fan-out（`VoiceRelayPolicy`）：

- 只发给**声明 supported** 的端（旧构建收不到语音包，不会刷“未注册消息”日志）；
- 不回发给说话者（无回声）；
- 监听者静音了该说话者 → **不中继**（省带宽），并在 `VoiceMuteResult` 里确认；
- 说话者 `mic_enabled=false` / 未协商 → 丢弃并计数（`RelaySkippedDisabled`）；
- 说话者 `VoiceState.format` 与包内 `format` 不一致 / 未声明 → 丢弃并计数
  （`formatRejected`）：主机虽然只做中继，也必须拒绝用另一种档位编码的包；
- 未知 / 未声明档位的 `VoiceState` → 该端 `supported=false`，并回
  `VoiceControl{enabled=false, reason="voice-format-unsupported-<id>"}`；
- 同一份中继载荷只序列化一次，`TrySendTo` 逐端发送；
- 主机自己的麦克风音频（`source=0`）直接发给每个 supported 端，主机本机不播放自己。

接收端：

- 每说话者一个 jitter：预灌 2 包（80 ms）；只有 1 包时最多等 80 ms 就开播；
  缺包最多等 70 ms 再跳过（`GapSkipped`）；已播 sequence 再来 = `LateDropped`；
  缓冲上限 8 包（320 ms），满时丢最旧并计数；
- 说话段首包（start 位）会 `Restart` 该说话者的 jitter，旧段残留不串音；
- 断流 500 ms 复位；`PumpPlayback` 按 40 ms 节奏向 OpenAL 流提交。

---

## 5. UI 与输入

游戏内 HUD（`UI/VoiceHud.cs`；图标与聊天按钮同挂 `GameWidget.GuiWidget`）：

- **语音状态图标**（按钮名 **`SuMPVoiceIcon`**）：**右下角、聊天按钮正上方**
  （`Far/Far`；距底 300 = 聊天按钮 236 + 按钮高 56 + 间距 8；宽 64 × 高 56 的紧凑
  图标按钮）。图标是**唯一的语音开关**：点一下切换 ON/OFF；ON = 绿底白麦 +
  **麦克风右侧 5 格音量条**（点亮 `round(平滑电平 × 5)` 格：静音 0 格、满格 5 格；
  电平来自采集帧的 `VoiceActivityDetector.LastLevel`，经 `Voice/VoiceMicMeter.cs`
  按**统一 dBFS 标尺**归一化 / 平滑，格子在图标自绘里画 —— **不新建控件**），
  OFF = 灰底灰麦 + 红色斜杠 + 整条音量条不画（颜色与形状双重区分）；进房间**默认 OFF**，
  离开房间 / 断线自动 OFF 并释放麦克风；
- **5 格映射规则（统一 dBFS 标尺；2026-10-21 实机修复）**：旧标尺把采集均值
  `mean(|sample|)` 按平板经验值线性映射（floor=180 / full=3000），PC 麦克风正常说话
  均值 3000~10000（−20.8 ~ −10.3 dBFS）直接越过 full → **图标永远 5 格**。现在统一换算成
  dBFS（与采集接口 / 设备无关，标尺常量只在 `VoiceMicMeter` 一处）：

  ```
  dbfs  = 20·log10(max(mean, 1) / 32768)                 # 32768 = int16 满幅
  level = (dbfs − FloorDbfs) / (FullDbfs − FloorDbfs)    # clamp 0..1
  格数   = round(level × 5)                               # 0 = 静音，5 = 满格
  FloorDbfs = −50 dBFS（≈ mean 104，安静房间底噪）
  FullDbfs  = −8.5 dBFS（≈ mean 12315，贴着实测“大声约 13000”；−6 dBFS = mean 16424
              会把正常说话 3000 压到 3 格，故不采用）
  ```

  典型值（mean→格）：104→0、200→1、1000→2、1500→3、3000→4、≥12315→5；
  正弦音 `mean = 2A/π`（幅度 A=8000 约 mean 5093 → 4 格）。起音 0.6 / 释放 0.18 的平滑与
  250 ms 停滞衰减不变。**只有 `voice=on && capture=capturing` 才更新电平**：权限被永久拒绝
  （Android `PermanentlyDenied`）/ 停采 / 读失败时保持 0，`micMeter` 的 frames / rejected /
  staleDecays 不增长（不伪造电平）；停采 / 关麦 / 读失败都会 `Reset()` 归零；
- **5 格绘制对比（2026-10-21 实机修复，与上面的 dBFS 标尺是两个独立问题）**：
  点亮格 = 麦克风同色（ON 时白 `#FFFFFF`），**未点亮格 = 黑色 α176（≈69% 黑）压出的暗槽**
  —— 5 个格子始终都在（0 格时就是 5 个暗槽），在绿色按钮底与浅绿斜面上都是“熄灭”观感。
  旧实现用半透明白 α70（`Color(255,255,255,70)`）：在绿底 `#2E7D4F` / 浅绿斜面 `#9AD6AE`
  上合成后是 `#67A17F` / `#B6E1C4`，**比底色更亮**，实机看起来 5 格永远全亮；数据侧
  `bars=1/5`、`bars=2/5` 一直正确，问题只在绘制对比。白 vs 暗槽在绿底中心亮度对比约
  16:1（浅绿斜面约 10:1）。
  绘制仍是同一个 `FlatBatch2D` 里的 5 个 quad，不新建控件、不每帧分配；`micBars/micBarUpdates`
  计数含义不变（`UnlitAlpha` 是绘制常量；每次 bars 变化那行 10 s 限流日志会写明
  “亮格=麦克风同色 / 熄灭格=黑α176”）；
- **10 s 电平窗口（诊断标尺用，绝不每帧打日志）**：

  ```
  [SuMP][voice] 电平窗口 micWin(n=… mean(min/avg/max)=…/…/… level(min/avg/max)=…/…/…
    bars(min/max)=…/…) captureDelta=… meterDelta=… rejectedDelta=… gateSkipped=…
    meterFramesNotPushed=… meterPushedWithoutCapture=… floorDbfs=-50 fullDbfs=-8.5
  ```

  `captureDelta` = 本窗口 `voiceCaptureFrames` 增量、`meterDelta` = `micMeter.frames` 增量；
  正常 `captureDelta − rejectedDelta − gateSkipped == meterDelta`。不等时：
  `meterFramesNotPushed` 记“采集帧涨但电平计没涨”的帧数，`meterPushedWithoutCapture`
  记反向（正常恒 0）。下次调标尺直接看这一行的 mean / level 分布；
- **Windows `U` 键**：同一个开关（`Engine.Input.Keyboard` 的
  `IsKeyDownOnce(Key.U)`，边沿判定在纯逻辑 `Voice/VoiceToggleGate.cs`：
  一次按下 = 一次切换，按住不连发）；**聊天输入 / 文本框 / 对话框 / 软键盘打开时忽略**
  （`ChatOverlayAttacher.HasOpenInput` + `DialogsManager.Dialogs` +
  `Keyboard.IsKeyboardVisible`）。U 键与图标点击都在**同一帧**把新状态反映到图标上；
- `TALK`：**可选 PTT 模式**（`voicemode=ptt`）下的“点一下开 / 再点一下关”；
  **不绑定任何键盘键** —— 旧“按住 V”键盘 PTT 已按产品要求移除，**V 键不做任何语音操作**；
  默认 open-mic 模式下 TALK 不显示；
- **静音入口只有一个**：MoreContents 里的单只 **`VO 按钮`**（`UI/VoiceMuteTable.cs`），
  点开是 `VOICE` 表格：`MUTE ALL / UNMUTE ALL` + 每个远端一行；右上角面板不放任何静音按钮；
- `SPEAKING`：**屏幕上这一行已按产品要求删除（2026-10-21）**；说话状态（400 ms 内有播放 /
  本机正在说话）只进日志与计数：`VoiceHud.UpdateSpeakingLog` 每 1 s 采样一次，状态真正变化才
  写一行 `[SuMP][ui] 说话状态 → …（仅日志 / 计数）`（计数 `speakingChanges`），**不新建任何控件**；
- 右上角面板只剩 `TALK`（可选 PTT）：**整行 `VOICE: off …` 状态文字与整行 `SPEAKING`
  都已删除（2026-10-21，连同 26 px 的控件一起删，不留空白行）**；状态文字 / 权限提示改由
  `VoiceChatModule.HudText()` 写进 ON/OFF 切换那一行日志
  （`[SuMP][ui] 语音图标状态更新 → …（状态文字只进日志：…）`）；
  **不重复放 VOICE 开关**（开关只保留右下角图标，避免双入口）；
- 权限流程的提示（`requesting mic permission…` / `mic permission denied — press U to retry` /
  `mic permission blocked — enable it in Settings` / `mic permission unavailable — …`）
  仍照常写进日志（`LogProblem`）与状态文字；因为面板状态行已删，**屏幕上只看得到图标保持 OFF**，
  具体原因去 `Logs/Game.log` 搜 `[SuMP][voice]`。

> 聊天输入只用 **Enter**（产品要求）：关闭时 Enter 打开输入，打开时 Enter 发送，Esc 取消；
> **T 不再打开聊天**（关闭时直接交给游戏；输入打开期间游戏键仍被抑制）；触摸端保留常驻“聊天”按钮。

配置 `data:/Mods/SuMultiplayer.json`（`Engine/SyncConfig.cs`）：

```json
"voice": true,
"voicemode": "open",       // open = 开麦 + VAD；ptt = HUD 的 TALK 点按说话（不绑定键盘键）
"voicevolume": 1.0,        // 远端语音本地增益 0..1
"voicequality": "low",     // high(4-bit) / standard(3-bit) / low(2-bit, 默认) / ultra(1-bit CVSD)
"voicevadhangover": 160    // VAD 挂起毫秒（默认 160；0 = 说完立刻停发，上限 2000）
```

`voicequality` 是**本端编码档位**：主机在 `VoiceState` 里收到每个说话者的档位并
校验其语音包；接收端按包内 `format` 解码，所以两端不需要相同档位。配置在启动时
读取；本模块也支持运行期改 `VoiceOptions.Quality`（下一包生效、计数与主机名册自动
跟随），当前 UI 没有循环按钮（本轮的语音图标只负责 ON/OFF，不切档位），
因此**切换档位 = 改配置后重启**（真机清单里单列）。

`voice=false` 时本端不采集 / 不发送 / 不播放，并在 `VoiceState` 里声明
`supported=false`（主机不会把语音中继给它）。`voicemode` 只影响 HUD 的 `TALK`
按钮（可选 PTT），不影响语音开关与 U 键；**V 键不绑定任何语音功能**
（旧的“按住 V”键盘 PTT 已按产品要求删除，配置里保留 PTT 也不会恢复 V）。

---

## 6. 健壮性矩阵

| 场景 | 行为 |
|---|---|
| 无头主机（无播放设备） | 主机仍采集（如有麦克风）/ 中继；`EnqueueForPlayback` 直接返回，不假装能播；计数 `noPlayback` |
| 客户端无播放上下文 | 明确关闭本端语音（`reason=no-playback`），`VoiceState.supported=false`，HUD/日志显示原因 |
| 无麦克风 | `voiceNoDevice` 计数 + `MIC no-device`，每 5 s 重试；仍能听别人 |
| 进房间 / 断线重连 | 语音**默认 OFF**：不采集、不占麦克风、`VoiceState mic_enabled=false reason=voice-off` |
| 离开房间 / 断线 | 语音自动 OFF；`alcCaptureStop` + `alcCaptureCloseDevice` 释放麦克风；播放流 / 名册 / jitter 清理 |
| Android 权限首次 ON | 发系统请求（`voicePermissionRequested`）；答复前不启动采集 |
| Android 权限被拒 | 保持 OFF、不启动采集；HUD `mic permission denied — press U to retry`；再 ON 重新请求 |
| Android 权限永久拒绝 | 保持 OFF；HUD `mic permission blocked — enable it in Settings`；打开应用设置页（`voicePermissionSettingsOpened`） |
| Android 本会话已授权 | 后续 ON 直接打开，不弹窗 |
| Android 权限对象不可用 | `voicePermissionUnavailable` + 明确原因；不弹窗、不启动采集、不假装能录音 |
| 发送背压 / 队列满 | `TrySend/TrySendTo` 返回 false → `sendFailures`；播放队列满 → 丢包并 `queueDropped` |
| 畸形包 / 未知 source | 整包拒绝 + `malformed` / 明确日志（10 s 限流） |
| 旧 sequence / 重放 | `lateDropped`（主机侧 + jitter），不重复播放 |
| 多说话者 | 每说话者独立 jitter + 独立 AL 流；HUD 同时列出 |
| 玩家离开 | 名册 / 静音表（作为监听者和说话者两侧）/ jitter 流 / sequence 全部清理 |
| 断线重连 | 停止采集、关闭所有流、清空名册与静音表；新连接重新协商、sequence 从 1 开始 |
| 主机禁用语音（配置） | `VoiceControl{enabled=false}`；主机不再中继；客户端停止采集 / 播放 |
| 包内档位未知 / 未声明（0） | 整包拒绝 + `formatRejected`；不猜默认档、不解码 |
| 包内档位与说话者声明不一致 | 主机 / 接收端都拒绝 + `formatRejected`；不中继 |
| `VoiceState` 声明未知档位 | 该端 `supported=false` + `VoiceControl` 明确原因；改成合法档位后重新协商 |
| 运行期切档 | 下一包带新 `format`；接收端逐包解码；计数 `voiceQuality` / `voicePacketBytes` 跟随 |

---

## 7. 验证

- 生产 Mod：`dotnet build Mod/SuMultiplayer/SuMultiplayer.csproj -c Release`
  → **0 错误**（本次语音 HUD 改动文件自身 0 警告；并行进行中的
  `Modules/PlayerStats*` / `Engine/PlayerStatsStore.cs` 仍有 2 条未使用字段警告，
  不属于本次改动）；Windows 与 Android 共用同一份 `net8.0` DLL，
  没有新增包 / 原生库 / `Lib/` 子目录。
- `P:\tmp\tsverify`（沿用现有工程，单进程运行）：
  `dotnet run -c Release` → **3550/3550 通过**（0 失败；语音相关 **265 条** =
  VC1–VC17 **230 条** + 本轮图标 **VI1–VI9 共 35 条**）。
- 语音用例共 **265 条**（VC1–VC17 + VI1–VI9；本轮新增 **35 条**图标用例）：
  - VC1 编解码往返 / 静音 / 边界 / 带宽口径；
  - VC2 VAD 静音跳过、默认 160 ms 挂起、可配置挂起（毫秒 → 帧数）、过零率；
  - VC3 20 ms 帧 → 40 ms 包、末包补齐、溢出；
  - VC4 jitter 预灌 / 乱序 / 迟到 / 重复 / 缺包跳过 / 溢出 / 重启；
  - VC5 包校验 / 中继路由 / 静音表 / 名册；
  - VC6 主机 fan-out、无回声、停麦、静音、畸形包、玩家离开；
  - VC7 客户端 VoiceState（默认 OFF）/ 名册 / 收包播放 / 静音回滚 / 无播放上下文；
  - VC8 客户端采集 + PTT 发包、无设备、无权限（保持 OFF）；
  - **VC9** 开关状态机：进房间默认 OFF、不采集；离开自动 OFF 并释放麦克风；
    重进房间仍 OFF；
  - **VC10** 主机进房间同样默认 OFF（名册 host mic=false）；
  - **VC11** Android 权限：首次 ON 才请求；答复前不采集；拒绝 → 保持 OFF +
    计数；再 ON 重新请求；授权后立即打开；本会话后续 ON 静默（requests 不增）；
    已授权时首次 ON 直接打开；
  - **VC12** 永久拒绝 → 打开应用设置页；结果路径的永久拒绝同样处理；
    设置页打不开 → 明确计数；
  - **VC13** 权限对象不可用 / 请求发不出去 → 明确失败，不假装；
  - **VC14** U 键边沿：一次按下 = 一次切换、按住不连发、松开不切换、
    文本输入抑制后不补触发、Reset 正常；
  - **VC15** 不在房间内时开关被忽略（不采集、不请求权限）；
  - **VC16** 档位：目录 / 名字 / 线上 id / 每包载荷 163·123·83·43 B /
    2-bit、3-bit index 表与 step 表 / 四个档位各自编码→解码（音调、类语音、
    静音、错长度）/ protobuf 实测 170·129·89·49 B 与含 TCP 帧头
    4650·3625·2625·1625 B/s / VAD 挂起配置 160 ms / 默认 low 与运行期切档；
  - **VC17** 档位协商：主机记录说话者声明、拒绝不一致档位（不中继）、
    合法包中继并保留 format、未知 `VoiceState` 档位回明确 `VoiceControl` 原因、
    改成合法档位后重新协商、接收端拒绝未知档位包（不播放、不算解码失败）；
  - **VI1–VI9** 语音状态图标（35 条）：右下角 / 聊天按钮正上方布局规则、
    ON（绿底白麦）/ OFF（灰底灰麦 + 红斜杠）视觉映射、点击与 U 键同一帧更新、
    图标挂在 GuiWidget 且不进 MoreContents、无 V 键绑定、面板不重复放开关、
    EnsurePanel 惰性创建、create/update/toggle 计数、文档同步与 CRLF 守卫；
  - **MB1–MB7** 麦克风 5 格音量条（61 条）：**统一 dBFS 标尺** 0..5 格边界（静音 0 / 满格 5 /
    中点远离零）、attack·release 平滑（起音快 / 释放慢 / 静音落回 0）、
    停滞 250 ms 后按时间衰减（幂等 + Reset）、负电平 / 时钟倒退显式拒绝计数、
    真实 `VoiceChatModule` 采集帧 → `MicLevelBars` 跟手（静音 / 关麦 = 0 格）、
    源码守卫（状态行已删 / 图标仍显式定位 / U 键与点击未改 / 5 格不新建控件 / CRLF /
    熄灭格黑 α176 与点亮白格的合成对比）。
  - **DB1–DB7** 5 格电平统一 dBFS 标尺（附加用例 `VoiceMeterScaleChecks.cs`，43 条）：
    dBFS 换算边界（0 / 104 / 12315 / 32767，NaN / ±∞ / 负值）、典型值映射
    （104→0、1000→2、3000→4、13000→5）与旧标尺回归、标尺常量只在 `VoiceMicMeter`
    一处（模块不硬编码 32768）、平滑 / 停滞 / 拒绝不变、10 s 窗口 mean·level 的
    min/avg/max 与采集帧对账（两个方向 + 闸门跳过）、真实模块 mean→bars 跟手、
    Android 权限永久拒绝不伪造电平、文档同步与 CRLF。
- 带宽实测断言：high 170 B / 4650 B/s、standard 129 B / 3625 B/s、
  low 89 B / 2625 B/s、ultra 49 B / 1625 B/s（25 包/秒，含 16 B TCP 帧头）。

---

## 8. 部署 / 设备清单

**Windows**

1. 用同一份 `SuMultiplayer.dll` 打包 `.scmod`（`IsMergeLib=true`，平铺 `Lib/`）。
2. 两端都进同一个房间（主机先 CR 建房、客户端 Play 屏加入）。
3. 右下角出现**麦克风图标按钮**（**聊天按钮正上方**），**面板不再有 `VOICE: off` 状态行文字**
   （状态文案只进日志）：**进房间默认 OFF**；按 **U**（或点麦克风图标）变成绿底白麦，
   图标右侧 **5 格音量条**随说话点亮（静音 0 格、大声 5 格）；OFF 时整条不画。
   离开房间后自动回到 OFF。
4. **V 键不产生任何语音效果**（既不 PTT、也不切换开关）；PTT 只保留 `TALK` 点按。

**Android**

1. **必须重新打包 APK**（`Survivalcraft/AndroidManifest.xml:52` 已加
   `RECORD_AUDIO`）；只换 `.scmod` 的旧 APK 无法录音。
2. 进房间后语音默认 OFF；**第一次按 U / 点右下角麦克风图标**才弹录音权限。
3. 拒绝后语音保持 OFF（不启动采集），HUD 提示；**再按一次 U 会重新请求**。
4. 勾选“不再询问”后：HUD 提示并打开本应用设置页；在设置里开启麦克风后
   按 U 即可打开；本会话授权后后续开关不再弹窗。
5. 关闭语音 / 离开房间 / 断线：立即停止采集并释放麦克风。

**人工设备清单（待真实设备执行）**

| 项 | 期望 | 本次状态 |
|---|---|---|
| PC：进房间默认 OFF | 右下角麦克风图标是**灰底灰麦 + 红斜杠**；**没有状态行 / SPEAKING 文字**（只进日志），不采集 | 逻辑用例 VI1–VI3 / VC9 / VC10 / MB6 验证；需真机复核图标 |
| PC：ON 时 5 格音量条 | 说话时图标右侧 5 格从 0 逐格点亮（**熄灭格 = 黑 α176 暗槽、点亮格 = 白**；**安静 0~1 格、正常说话 2~4 格、大声 5 格**），闭嘴后约 0.3 s 落回 0 格；OFF 时整条不画 | dBFS 映射 / 平滑 DB1–DB4 + MB1–MB3 + 模块接线 DB6 / MB5；需真机说话目视 |
| PC：5 格熄灭 / 点亮一眼可分 | 安静时只看到 1 个亮白格 + 4 个暗槽，说话逐格变亮，不再“5 格永远全亮”；关麦整条消失 | 颜色守卫 MB7（黑 α176 / 白 vs 暗槽合成对比）+ 绘制源码守卫 MB6；需真机目视 |
| PC：10 s 电平窗口日志 | 说话时日志出现 `电平窗口 micWin(mean(min/avg/max)=… level(min/avg/max)=… bars(min/max)=…)`，并带 `floorDbfs=-50 fullDbfs=-8.5`、`captureDelta==meterDelta`、`meterFramesNotPushed=0 meterPushedWithoutCapture=0` | DB5 / DB6 逻辑验证；需真机看 10 s 日志调标尺 |
| PC：状态行 / SPEAKING 删除后无空白 | 右上角面板只剩 TALK（仅 ptt），原状态行与 SPEAKING 位置没有空行 / 空框 | 源码守卫 MB6 / VM6 / NV（无 SpeakingName / LabelWidget）；需真机目视 |
| PC：图标在聊天按钮正上方 | 两者同一右下角列（Far/Far，图标距底 300 = 聊天 236 + 56 + 8），不挡聊天按钮 | 布局规则 VI1 + 源码守卫 VI4；需真机看位置与点击 |
| PC：点图标切换 | 点一下 ON（绿底白麦）/ 再点一下 OFF（灰底 + 斜杠），图标同一帧更新 | 点击路径 VI6 + 纯逻辑 VI3；需真机点击 |
| PC：U 一次按下一次切换 | 按住不连发；图标同步；聊天输入 / 对话框打开时不触发 | 边沿逻辑 VC14 + 图标同步 VI3/VI6；需真机按 U |
| PC：ON/OFF 可区分 | 灰底灰麦 + 红斜杠 vs 绿底白麦（颜色 + 形状双重区分） | 视觉映射 VI2；需真机目视 |
| PC：V 键无效 | 按住 / 点按 V 都不 PTT、不切开关、不产生语音包 | 源码守卫 VI5（无 Key.V / SetPttHeld）；需真机按 V 看日志无 TALK/PTT |
| PC：离开房间自动 OFF | 语音 OFF、麦克风释放；重进仍 OFF | VC9 验证；需真机看日志 `voice=off` |
| Android：首次 ON 才弹权限 | 进房间不弹；第一次 ON 弹一次 | VC11 验证；需带权限 APK 实测 |
| Android：拒绝后再 ON 重新弹 | 第二次请求计数 +1，不锁死 | VC11 验证；需真机 |
| Android：永久拒绝 → 设置页 | 打开应用详情页；设置里授权后按 U 可用 | VC12 验证；需真机 |
| Android：授权后静默 | 后续 ON 不再弹窗（requests 不增） | VC11 验证；需真机 |
| 两名玩家双向通话（默认 low） | 双方都能听到对方；**会话可懂**；10 s 日志 `voiceQuality=low voicePacketBytes=83`、说话时 `voiceBytesPs≈2625` | 未执行（需两名玩家；逻辑用例 VC16/VC17 覆盖编解码 / 协商 / 带宽） |
| 两个玩家 ultra | 有噪声但**可懂**；`voiceQuality=ultra voicePacketBytes=43`、`voiceBytesPs≈1625` | 未执行（需两名玩家；VC16 类语音 SNR 9.1 dB / 相关 0.940） |
| 切换档位（改配置重启） | 重启后两端日志与计数显示新档位；下一包 format 变化；接收端正常解码 | VC16 运行期切档用例；配置解析 `voicequality` |
| 带宽下降 | high → low 连续说话时 `voiceBytesPs` 约 4650 → 2625 B/s（-44%） | VC16 实测；真机看 10 s 日志 |
| 静音时带宽 | 不说话时 `voiceBytesPs≈0`、`voiceSent` 不增长 | 已用 VAD / 发包用例验证；真机看 10 s 日志 `voiceBytesPs` |
| 静音（mute） | 点 MUTE ALL / 名字按钮后不再听到；主机不再向该监听者中继 | 逻辑 / 协议用例验证；真机按 HUD 验证 |
| 无麦克风 / 无头主机 | 只中继不播放；计数 `noPlayback` / `noDevice` | 逻辑用例验证 |

> 说明：本轮没有启动游戏、没有部署、没有多开 `Survivalcraft.exe`（遵守单实例与
> “不 kill 游戏进程”规则），因此真机项仍是待执行项；
> 采集 / 播放的**原生 API 路径**已在本机用同一份 P/Invoke 与真实麦克风实测通过。
