# 工程笔记

留在仓库里的原因:这些是踩过一次不想再踩的坑,以及几个"看起来可以优化、实际一动就崩"的地方。改代码前扫一眼。

## 录制内核(ScreenRecorderLib 7.0.1)

- **`IsFixedFramerate = true` 不能删**。DDA/WGC 是"画面变了才产帧",不开这个开关,静止画面会让成片时长远小于实际录制时长(实测 15 秒静止桌面出 14.6 秒)。
- **音频源必须用 `LoopbackAudioSource.Default` / `CaptureAudioSource.Default`**。无参构造的 `DeviceName` 为空,成片直接没有音轨。
- **无麦克风时该属性抛 `NullReferenceException`**,必须 try/catch 降级为仅系统声,否则点录制直接失败。
- **不要调 `Recorder.GetSystemAudioCaptureDevices()`**。本机实测直接段错误,UI 进程和 agent 进程都会崩,且确认与程序集加载顺序无关。麦克风只能用系统默认设备。
- **任何 `ScreenRecorderLib` 类型都必须晚于 `Assembly.LoadFrom` 被 JIT 引用**,否则不是抛异常而是进程原生崩溃(段错误,无托管堆栈)。见 `NativeBootstrap`。
- **单文件发布走不通**:C++/CLI 混合程序集在单文件宿主里加载失败(`BadImageFormatException`),把 dll 放到 exe 旁边也不行。

## 鼠标特效(叠加层)

- 指针高亮和点击环都走 `ImageOverlay`,由内核合成进编码器输入,所以**只在成片里出现**,录制时屏幕上看不出。
- 叠加层画在鼠标指针**之下**:即使不透明的图也盖不住光标。
- **层数上限是硬约束**。实测:1 层 30Hz 连续移动 12~14 秒稳定;3 层稳定;**5 层时定时切段的收尾会踩编码器 `0xC00D4A3E`(示例分配器为空)**,严重时 `Stop` 直接挂死。所以总共只用 2 层(高亮 1 + 点击 1)。
- **不要用换 `SourceStream` 做逐帧动画**。每次换图都要重新解码、重建贴图,30Hz 换下来会把采集线程拖住,同样报 `0xC00D4A3E`;13 层一起动必崩,41 层连停止都挂死,换软件编码也一样。要做扩散动画只能自己合成帧。
- 点击环的颜色和单/双环靠**换图**实现,一次点击最多换一次,不是逐帧。
- 双击判定读 `GetDoubleClickTime()` 和 `SM_CXDOUBLECLK`,跟着用户的系统设置走,不要写死 500ms/4px。
- 叠加层的坐标是**成片像素**:全屏减显示器原点、选区再减选区原点、窗口用 DWM 扩展边框(`DWMWA_EXTENDED_FRAME_BOUNDS`)。指针移出录制范围就不画。

## 分段与合并(ScreenRec.Merger)

- 只做流复制:压缩的 H.264/AAC 样本原样搬运,不重编码。
- **不校验标称帧率**。`MF_MT_FRAME_RATE` 是 Media Foundation 按样本时间戳推出来的,同一场录制里丢过帧就会算成 60/1 和 30/1,而 avcC 里的 SPS/PPS 完全相同。每帧时长按样本原值重写、PTS/DTS 由合并器重排,所以只记录 `frame_rate_mismatch` 不拦截。
- 为 H.264 B 帧重建连续 DTS,并把音视频 PTS 同步前移两帧,避免段首负 DTS 被截掉。
- 每段跳过首个 AAC priming 包,避免拼接点重复时间戳。
- 先写 `.merge-tmp.mp4`,回读校验通过才替换最终文件;失败不删除输入。
- **合并前要重试**:内核 finalize 完句柄还会滞留几百毫秒,直接合并会命中 `0x80070020`(文件被占用)。`RecordingSession` 和 `OrphanMerge` 都有 3~4 次重试。
- **崩溃恢复只合并"从 part1 起连续可读"的前缀**:崩溃时正在写的那一段没有 moov,Media Foundation 打不开(`0xC00D36C4`),整组一起合并会失败。坏段改名 `.broken` 保留,不删。

## 进程模型

- UI 与 agent 是**两个 exe**:`ScreenRec.Agent.exe` 刻意不引 WPF(空跑 29 MB,共用一个 exe 时是 50 MB)。
- 单实例靠 `Local\ScreenRec.SingleInstance` 互斥体,UI 主路径持有。
- 端口冲突重试:`AgentPort.PickFree` 先探测再 spawn,agent 早退快速失败,换端口最多 3 次。
- 停录/暂停/切段前**必须先摘掉鼠标特效**,否则叠加层刷新与 recorder 收尾抢锁,`Stop` 会挂住不返回。

## WPF / UI

- `ItemsSource` 不要用 `ValueTuple`:`Item1`/`Item2` 是字段,而 `DisplayMemberPath` / `SelectedValuePath` 只认属性,结果下拉项渲染为空、选中项也匹配不上。用带属性的 `record`。
- ICO 必须用 BMP 条目:PNG 压缩条目的 ico 在嵌入 exe 图标时会被静默忽略(exe 体积不变、图标还是默认的)。
- 托盘 `Shell_NotifyIcon` 建不起来时要把最小化退化成系统最小化,否则窗口没有入口。
- 设置页所有下拉共用 `OnPreviewOptionChanged`,改颜色改为色盘后注意事件来源。

## 日志

- 我方:`%TEMP%\screenrec_diag_yyyyMMdd.log`,按天滚动保留 7 天,当天超 2 MB 截断。
- 内核:`%LOCALAPPDATA%\ScreenRec\logs\recorder.log`(Debug 级)。
