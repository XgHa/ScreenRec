using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenRec.Core;

namespace ScreenRec.App;

/// <summary>
/// 无界面端到端自测:走真实链路(启动 --agent 子进程 + localhost TCP),录 5 秒,
/// 校验成片时长。录制期间保持屏幕静止 —— 正是 DDA/WGC 不产帧、旧版本时长缩水的场景,
/// 所以这个自测能直接验证 IsFixedFramerate 修复。
/// 用法:ScreenRec.exe --selftest(退出码 0 即通过)
/// </summary>
public static class SelfTest
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // 2s 录(含截图) → 1s 续录 → 2s 暂停 → 3s 恢复:内容共 6s,合并后成片应 ~6s
    private const int RecordSeconds = 3;
    private const int PauseSeconds = 2;
    private const int AfterPauseSeconds = 3;
    private const int ExpectedSeconds = RecordSeconds + AfterPauseSeconds;

    /// <summary>自测结果落盘:WinExe 从命令行启动时 stdout 拿不到,只能靠文件回读。</summary>
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "screenrec_selftest_result.log");

    private static void Log(string message)
    {
        try { Console.Out.WriteLine(message); } catch { }
        try { File.AppendAllText(LogPath, message + Environment.NewLine); } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    public static int Run()
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        return RunCore();
    }

    private static int RunCore()
    {
        var exe = AgentLauncher.Locate();
        if (exe is null)
        {
            Log("FAIL: 未找到 ScreenRec.Agent.exe,请先构建解决方案");
            return 2;
        }

        var outDir = Path.Combine(Path.GetTempPath(), "screenrec_selftest");
        Directory.CreateDirectory(outDir);

        int port = AgentPort.PickFree();
        var settings = new RecordSettings
        {
            FrameRate = 30,
            Quality = QualityLevel.Medium,
            AudioMode = AudioMode.Both,
            OutputDirectory = outDir,
        };

        Log("[1] 启动 agent ...");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--port {port}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var agent = Process.Start(psi);
        if (agent is null)
        {
            Log("FAIL: agent 进程启动失败");
            return 3;
        }

        try
        {
            TcpClient tcp;
            try
            {
                tcp = Connect(port, TimeSpan.FromSeconds(20));
            }
            catch (Exception ex)
            {
                Log("FAIL: 连接 agent 失败: " + ex.Message);
                return 3;
            }

            using (tcp)
            {
                var stream = tcp.GetStream();
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                var reader = new StreamReader(stream, new UTF8Encoding(false));

                Log($"[2] 发送 start(录 {RecordSeconds} 秒,期间屏幕保持静止)...");
                writer.WriteLine(JsonSerializer.Serialize(new { cmd = "start", settings }, JsonDefaults.Options));

                string? path = null;
                string? error = null;
                string? snapshotPath = null;
                bool snapshotOk = false;
                bool started = false;
                int tickCount = 0;
                int segments = 0;
                var deadline = DateTime.UtcNow.AddSeconds(40);
                var sw = Stopwatch.StartNew();

                // 事件读取异步化:agent 若中途静默,同步 ReadLine 会把整个自测挂死
                Task<string?>? pending = null;
                while (DateTime.UtcNow < deadline)
                {
                    pending ??= reader.ReadLineAsync();
                    bool got;
                    try { got = pending.Wait(TimeSpan.FromMilliseconds(500)); }
                    catch { got = true; }

                    if (!got) continue;

                    string? line;
                    try { line = pending.Result; }
                    catch (Exception ex) { error ??= "读取 agent 事件失败: " + ex.Message; break; }
                    pending = null;

                    if (line is null)
                    {
                        error ??= "agent 提前断开连接";
                        break;
                    }

                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("ev", out var evProp)) continue;
                    string ev = evProp.GetString() ?? "";

                    switch (ev)
                    {
                        case "started":
                            if (!started)
                            {
                                started = true;
                                Log($"    录制 {RecordSeconds}s(中途截图) → 暂停 {PauseSeconds}s → 恢复 {AfterPauseSeconds}s ...");

                                Thread.Sleep(TimeSpan.FromSeconds(2));
                                writer.WriteLine(JsonSerializer.Serialize(new { cmd = "snapshot" }, JsonDefaults.Options));
                                Log("    已请求截图");
                                Thread.Sleep(TimeSpan.FromSeconds(RecordSeconds - 2));

                                writer.WriteLine(JsonSerializer.Serialize(new { cmd = "pause" }, JsonDefaults.Options));
                                Log("    已请求暂停");
                                Thread.Sleep(TimeSpan.FromSeconds(PauseSeconds));

                                writer.WriteLine(JsonSerializer.Serialize(new { cmd = "resume" }, JsonDefaults.Options));
                                Log("    已请求恢复");
                                Thread.Sleep(TimeSpan.FromSeconds(AfterPauseSeconds));

                                writer.WriteLine(JsonSerializer.Serialize(new { cmd = "stop" }, JsonDefaults.Options));
                                Log("[3] 已请求停止,等待收尾 ...");
                            }
                            break;

                        case "snapshot":
                            snapshotOk = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
                            snapshotPath = root.TryGetProperty("path", out var snapProp) ? snapProp.GetString() : null;
                            break;

                        case "completed":
                            path = root.GetProperty("path").GetString();
                            segments = root.TryGetProperty("segments", out var segProp) ? segProp.GetInt32() : 1;
                            break;

                        case "tick":
                            tickCount++;
                            break;

                        case "failed":
                            error = root.GetProperty("msg").GetString();
                            break;
                    }

                    if (path is not null || error is not null) break;
                }

                if (error is not null)
                {
                    Log("FAIL: " + error);
                    return 4;
                }

                if (!started)
                {
                    Log($"FAIL: 未收到 started 事件(耗时 {sw.Elapsed.TotalSeconds:F1}s)");
                    return 5;
                }

                if (path is null || !File.Exists(path))
                {
                    Log("FAIL: 未产出文件");
                    return 6;
                }

                var file = new FileInfo(path);
                double duration = ProbeDuration(path);
                int audioStreams = ProbeAudioStreams(path);
                Log($"[4] 输出: {path}");
                Log($"    size = {file.Length / 1024.0 / 1024.0:F2} MB, duration = {duration:F2}s, audio streams = {audioStreams}, segments = {segments}");

                if (audioStreams == 0)
                    Log("WARN: 成片没有音频轨(系统声采集失败或本机无输出设备)");

                if (snapshotOk && snapshotPath is not null && File.Exists(snapshotPath))
                    Log($"    截图: {Path.GetFileName(snapshotPath)} ({new FileInfo(snapshotPath).Length / 1024} KB)");
                else
                    Log("WARN: 录制中截图失败(没拿到文件)");

                if (segments < 2)
                {
                    Log($"FAIL: 暂停/恢复应产生 2 个分段,实际 {segments} 个");
                    return 8;
                }

                // 两段冷启动各自可能损失数百毫秒。允许时序波动,同时用上面的
                // segments == 2 约束防止把原生暂停的丢帧问题误判为通过。
                if (duration <= 0)
                {
                    Log("FAIL: 无法读取成片时长,请确认包内 ScreenRec.Merger.exe 可运行");
                    return 7;
                }

                if (duration < ExpectedSeconds - 1.5)
                {
                    Log($"FAIL: 成片时长 {duration:F2}s 短于预期 {ExpectedSeconds}s(允许分段启动开销)");
                    return 7;
                }

                if (file.Length < 50_000)
                {
                    Log("FAIL: 文件过小");
                    return 8;
                }

                Log($"    ticks = {tickCount}");
                if (tickCount == 0)
                    Log("WARN: 没收到 tick 事件,悬浮条计时会一直是 00:00");

                Log("PASS");
            }

            int regionCode = RunRegionCase(exe, outDir);
            int rolloverCode = regionCode == 0 ? RunRolloverCase(exe, outDir) : 0;
            int crashCode = rolloverCode == 0 ? RunCrashRecoveryCase(exe, outDir) : 0;
            if (Environment.GetEnvironmentVariable("SCREENREC_SELFTEST_KEEP") == "1")
                Log($"[8] 自测文件保留: {outDir}");
            else
                try { Directory.Delete(outDir, true); } catch { }
            return regionCode != 0 ? regionCode : rolloverCode != 0 ? rolloverCode : crashCode;
        }
        finally
        {
            try
            {
                if (!agent.HasExited) agent.Kill(true);
            }
            catch { }
        }
    }

    /// <summary>
    /// 选区录制回归:主屏固定开一个 320×240 的框,校验成片分辨率是否跟着选区走。
    /// 之前的实现把坐标交给 OutputOptions.SourceRect,换屏就会错位,这条用例锁住新路径。
    /// </summary>
    private static int RunRegionCase(string exe, string outDir)
    {
        const int regionWidth = 320;
        const int regionHeight = 240;

        Log("");
        Log($"[5] 选区录制回归({regionWidth}×{regionHeight}) ...");

        int port = AgentPort.PickFree();
        var settings = new RecordSettings
        {
            FrameRate = 30,
            Quality = QualityLevel.Low,
            AudioMode = AudioMode.Muted,
            OutputDirectory = outDir,
            FileNameTemplate = "{yyyyMMdd_HHmmss}_region",
            Target = CaptureTarget.Region,
            MonitorDeviceName = null, // 主屏
            RegionX = 80,
            RegionY = 80,
            RegionWidth = regionWidth,
            RegionHeight = regionHeight,
        };

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--port {port}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var agent = Process.Start(psi);
        if (agent is null)
        {
            Log("FAIL: 选区用例的 agent 启动失败");
            return 10;
        }

        try
        {
            using var tcp = Connect(port, TimeSpan.FromSeconds(20));
            var stream = tcp.GetStream();
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(stream, new UTF8Encoding(false));

            writer.WriteLine(JsonSerializer.Serialize(new { cmd = "start", settings }, JsonDefaults.Options));

            string? path = null;
            string? error = null;
            bool started = false;
            bool stopSent = false;
            var sw = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddSeconds(30);

            var pending = reader.ReadLineAsync();

            // 同步 ReadLine 在 agent 中途静默时会把自测挂死,这里用「带超时的等待」轮询。
            // stop 条件必须每轮都查 —— tick 每 200ms 一条,Wait(250) 几乎总能命中,
            // 塞在超时分支里的话 stop 永远发不出去(实测,切段用例因此挂死过)。
            while (DateTime.UtcNow < deadline)
            {
                if (started && !stopSent && sw.Elapsed.TotalSeconds >= 2.0)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { cmd = "stop" }, JsonDefaults.Options));
                    stopSent = true;
                }

                if (!pending.Wait(250)) continue;

                string? line = pending.Result;
                if (line is null) break;
                pending = reader.ReadLineAsync();

                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string ev = root.TryGetProperty("ev", out var evProp) ? evProp.GetString() ?? "" : "";

                    if (ev == "started")
                    {
                        started = true;
                    }
                    else if (ev == "failed")
                    {
                        error = root.TryGetProperty("msg", out var m) ? m.GetString() : "未知原因";
                        break;
                    }
                    else if (ev == "completed")
                    {
                        path = root.TryGetProperty("path", out var p2) ? p2.GetString() : null;
                        break;
                    }
                }
                catch
                {
                    // 非 JSON 行忽略
                }
            }

            if (error is not null)
            {
                Log("FAIL: 选区录制报错: " + error);
                return 11;
            }

            if (path is null || !File.Exists(path))
            {
                Log("FAIL: 选区录制未产出文件");
                return 12;
            }

            var size = ProbeVideoSize(path);
            Log($"    输出: {Path.GetFileName(path)} {new FileInfo(path).Length / 1024} KB"
                + (size is null ? "" : $" {size.Value.Width}×{size.Value.Height}"));

            if (size is null)
            {
                Log("WARN: 原生媒体探测不可用,跳过选区分辨率校验");
            }
            else if (size.Value.Width != regionWidth || size.Value.Height != regionHeight)
            {
                Log($"FAIL: 成片 {size.Value.Width}×{size.Value.Height},应为 {regionWidth}×{regionHeight}");
                return 13;
            }

            Log("PASS: 选区录制");
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAIL: 选区用例异常: " + ex.Message);
            return 14;
        }
        finally
        {
            try
            {
                if (!agent.HasExited) agent.Kill(true);
            }
            catch { }
        }
    }

    /// <summary>
    /// 定时切段回归:SegmentSeconds=8 连续录 11 秒(全程不暂停),
    /// 应在第 8 秒左右自动切段,产出 ≥2 个分段且合并后时长接近 11 秒。
    /// 这是「崩溃可救」的核心承诺:切段产生的分段必须和暂停切段一样可合并。
    /// </summary>
    private static int RunRolloverCase(string exe, string outDir)
    {
        const int segmentSeconds = 8;
        const int recordSeconds = 11;

        Log("");
        Log($"[6] 定时切段回归(SegmentSeconds={segmentSeconds}s,连续录 {recordSeconds}s 不暂停) ...");

        int port = AgentPort.PickFree();
        var settings = new RecordSettings
        {
            FrameRate = 30,
            Quality = QualityLevel.Low,
            AudioMode = AudioMode.Muted,
            OutputDirectory = outDir,
            FileNameTemplate = "{yyyyMMdd_HHmmss}_rollover",
            SegmentSeconds = segmentSeconds,
        };

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--port {port}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var agent = Process.Start(psi);
        if (agent is null)
        {
            Log("FAIL: 切段用例的 agent 启动失败");
            return 20;
        }

        try
        {
            using var tcp = Connect(port, TimeSpan.FromSeconds(20));
            var stream = tcp.GetStream();
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(stream, new UTF8Encoding(false));

            writer.WriteLine(JsonSerializer.Serialize(new { cmd = "start", settings }, JsonDefaults.Options));

            string? path = null;
            string? error = null;
            bool started = false;
            bool stopSent = false;
            int segments = 0;
            var sw = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddSeconds(40);

            var pending = reader.ReadLineAsync();

            // stop 条件每轮都查(理由见选区用例):tick 流不断,塞在 Wait 超时分支里永远发不出去
            while (DateTime.UtcNow < deadline)
            {
                if (started && !stopSent && sw.Elapsed.TotalSeconds >= recordSeconds)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { cmd = "stop" }, JsonDefaults.Options));
                    stopSent = true;
                }

                if (!pending.Wait(250)) continue;

                string? line = pending.Result;
                if (line is null) break;
                pending = reader.ReadLineAsync();

                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string ev = root.TryGetProperty("ev", out var evProp) ? evProp.GetString() ?? "" : "";

                    switch (ev)
                    {
                        case "started":
                            started = true;
                            break;
                        case "failed":
                            error = root.TryGetProperty("msg", out var m) ? m.GetString() : "未知原因";
                            break;
                        case "completed":
                            path = root.TryGetProperty("path", out var p2) ? p2.GetString() : null;
                            segments = root.TryGetProperty("segments", out var segProp) ? segProp.GetInt32() : 1;
                            break;
                    }
                }
                catch
                {
                    // 非 JSON 行忽略
                }

                if (path is not null || error is not null) break;
            }

            if (error is not null)
            {
                Log("FAIL: 定时切段录制报错: " + error);
                return 21;
            }

            if (path is null || !File.Exists(path))
            {
                Log("FAIL: 定时切段未产出文件");
                return 22;
            }

            double duration = ProbeDuration(path);
            Log($"    输出: {Path.GetFileName(path)} {new FileInfo(path).Length / 1024} KB, duration = {duration:F2}s, segments = {segments}");

            // 合并失败时 Finish 会回退成分段文件,成片时长照样对得上,
            // 所以必须单独断言"成品不是 _partN" —— 否则帧率校验之类的合并问题会被判成 PASS。
            if (Path.GetFileNameWithoutExtension(path).Contains("_part", StringComparison.Ordinal))
            {
                Log("FAIL: 切段后没有合并成完整视频,回退成了分段文件");
                return 27;
            }

            if (segments < 2)
            {
                Log($"FAIL: 定时切段应产生 ≥2 个分段,实际 {segments} 个(切段没生效?)");
                return 23;
            }

            if (duration <= 0)
            {
                Log("FAIL: 无法读取成片时长,请确认 ScreenRec.Merger.exe 可运行");
                return 24;
            }

            // 切段瞬间丢数百毫秒,11 秒录制合并后应接近 11 秒
            if (duration < recordSeconds - 2.5)
            {
                Log($"FAIL: 成片时长 {duration:F2}s 明显短于 {recordSeconds}s(切段丢帧过多)");
                return 25;
            }

            Log("PASS: 定时切段");
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAIL: 切段用例异常: " + ex.Message);
            return 26;
        }
        finally
        {
            try
            {
                if (!agent.HasExited) agent.Kill(true);
            }
            catch { }
        }
    }

    /// <summary>
    /// 崩溃恢复回归:切段后强杀 agent(模拟崩溃/断电),扫残留分段并合并。
    /// 崩溃时正在写的那一段没有 moov,MF 打不开(0xC00D36C4),恢复必须跳过它、救出前面的段,
    /// 而不是把整次合并判失败 —— 那正是第一版恢复逻辑的死因。
    /// </summary>
    private static int RunCrashRecoveryCase(string exe, string outDir)
    {
        const int segmentSeconds = 4;
        Log("");
        Log($"[7] 崩溃恢复回归(SegmentSeconds={segmentSeconds}s,录 8s 后强杀 agent) ...");

        string crashDir = Path.Combine(outDir, "crash");
        try { Directory.CreateDirectory(crashDir); } catch { }
        // 上轮残留会干扰"残留段数"断言,先清干净
        try
        {
            foreach (string f in Directory.GetFiles(crashDir)) File.Delete(f);
        }
        catch { }

        int port = AgentPort.PickFree();
        var settings = new RecordSettings
        {
            FrameRate = 30,
            Quality = QualityLevel.Low,
            AudioMode = AudioMode.Muted,
            OutputDirectory = crashDir,
            FileNameTemplate = "{yyyyMMdd_HHmmss}_crash",
            SegmentSeconds = segmentSeconds,
        };

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--port {port}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var agent = Process.Start(psi);
        if (agent is null)
        {
            Log("FAIL: 崩溃恢复用例的 agent 启动失败");
            return 30;
        }

        try
        {
            using var tcp = Connect(port, TimeSpan.FromSeconds(20));
            var stream = tcp.GetStream();
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(new { cmd = "start", settings }, JsonDefaults.Options));

            // 等 started 再计时:冷启动慢的机器上按绝对时间杀进程可能一段都没切完
            bool started = false;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var pending = reader.ReadLineAsync();
            while (!started && DateTime.UtcNow < deadline)
            {
                if (!pending.Wait(250)) continue;
                string? line = pending.Result;
                if (line is null) break;
                pending = reader.ReadLineAsync();
                if (line.Contains("\"started\"")) started = true;
            }

            if (!started)
            {
                Log("FAIL: 崩溃恢复用例没收到 started");
                return 31;
            }

            // 首次切段在 5s 左右:等到 8s,part1 已 finalize,part2 正在写
            Thread.Sleep(TimeSpan.FromSeconds(8));
            try { agent.Kill(true); } catch { }
            Thread.Sleep(TimeSpan.FromSeconds(1)); // 等句柄释放,别让扫描读到半关闭的文件
        }
        finally
        {
            try
            {
                if (!agent.HasExited) agent.Kill(true);
            }
            catch { }
        }

        var groups = OrphanMerge.Scan(crashDir);
        if (groups.Count != 1)
        {
            Log($"FAIL: 崩溃残留应归为 1 组,实际 {groups.Count} 组");
            return 32;
        }

        var group = groups[0];
        if (group.Parts.Count < 2)
        {
            Log($"FAIL: 强杀时应已切出 ≥2 个分段,实际 {group.Parts.Count} 个");
            return 33;
        }

        var result = OrphanMerge.Merge(group);
        if (result.Path is null || !File.Exists(result.Path))
        {
            Log("FAIL: 崩溃恢复没救出成片: " + result.Note);
            return 34;
        }

        double duration = ProbeDuration(result.Path);
        Log($"    恢复: {Path.GetFileName(result.Path)} {new FileInfo(result.Path).Length / 1024} KB, "
            + $"duration = {duration:F2}s, {result.Note}");
        if (duration <= 0)
        {
            Log("FAIL: 恢复出的成片读不到时长");
            return 35;
        }

        // 残留分段必须退场,否则下次启动还会弹同一组提示
        string[] leftovers = Directory.GetFiles(crashDir, "*_part*.mp4");
        if (leftovers.Length > 0)
        {
            Log($"FAIL: 恢复后仍残留 {leftovers.Length} 个 _part 分段");
            return 36;
        }

        string[] broken = Directory.GetFiles(crashDir, "*" + OrphanMerge.BrokenSuffix);
        if (broken.Length != 1)
        {
            Log($"FAIL: 应隔离 1 个损坏分段,实际 {broken.Length} 个");
            return 37;
        }

        Log("PASS: 崩溃恢复");
        return 0;
    }

    private static TcpClient Connect(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var tcp = new TcpClient();
            try
            {
                tcp.Connect(System.Net.IPAddress.Loopback, port);
                return tcp;
            }
            catch (Exception ex)
            {
                last = ex;
                tcp.Dispose();
            }
            Thread.Sleep(250);
        }
        throw last ?? new TimeoutException("连接超时");
    }

    /// <summary>用原生 Media Foundation 工具读取成片时长。</summary>
    private static double ProbeDuration(string videoPath)
    {
        try { return MediaTool.Probe(videoPath).Duration; }
        catch { return 0; }
    }

    /// <summary>用原生工具读取成片分辨率;拿不到返回 null。</summary>
    private static (int Width, int Height)? ProbeVideoSize(string videoPath)
    {
        try
        {
            var info = MediaTool.Probe(videoPath);
            return info.Width > 0 && info.Height > 0 ? (info.Width, info.Height) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用原生工具统计成片音频流数量。</summary>
    private static int ProbeAudioStreams(string videoPath)
    {
        try { return MediaTool.Probe(videoPath).AudioStreams; }
        catch { return 0; }
    }
}
