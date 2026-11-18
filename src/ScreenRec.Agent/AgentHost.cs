using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ScreenRec.Core;

namespace ScreenRec.Agent;

/// <summary>
/// 无界面代理进程(ScreenRec.Agent.exe):承载完整录制会话(ScreenRecorderLib 内核)。
/// 通信:--port N 时监听 127.0.0.1:N 的 TCP,JSON 行协议(每行一个 {"cmd"/"ev"} 对象);
/// 无 --port 时回落 stdin/stdout(开发调试用)。
///
/// 事件契约(必须与 MainWindow.HandleAgentLine 一一对应):
///   ev=attached  握手完成
///   ev=started   录制已真正开始(UI 据此隐藏主面板、创建悬浮条)
///   ev=state     s=recording|paused
///   ev=tick      sec=已录时长(墙钟,已扣除暂停)
///   ev=notice    非致命提示
///   ev=completed path/bytes/sec
///   ev=failed    msg
/// </summary>
public static class AgentHost
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    private static readonly object EmitLock = new();
    private static RecordingSession? _session;
    private static TextWriter? _emit;
    private static Timer? _ticker;
    private static bool _startedSent;

    public static int Run(string[] args)
    {
        Diag.Reset();
        Diag.Log("agent: enter args=" + string.Join(' ', args));

        // 必须先完成程序集加载,之后才能碰任何 ScreenRecorderLib 类型
        NativeBootstrap.EnsureLoaded();

        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        int port = 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port") int.TryParse(args[i + 1], out port);

        if (port > 0) return RunTcp(port);
        _emit = Console.Out;
        return RunLoop(() => Console.ReadLine() ?? string.Empty, () => true);
    }

    private static int RunTcp(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("tcp listen failed: " + ex.Message);
            return 1;
        }

        TcpClient client;
        try
        {
            // UI 可能在拉起进程后放弃连接,不能无限等待
            var accept = listener.AcceptTcpClientAsync();
            if (!accept.Wait(TimeSpan.FromSeconds(20)))
            {
                listener.Stop();
                return 2;
            }
            client = accept.Result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("tcp accept failed: " + ex.Message);
            try { listener.Stop(); } catch { }
            return 1;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        using (client)
        {
            var stream = client.GetStream();
            lock (EmitLock)
                _emit = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            Emit(new { ev = "attached", port });

            var reader = new StreamReader(stream, new UTF8Encoding(false));
            return RunLoop(() => reader.ReadLine() ?? string.Empty, () => client.Connected);
        }
    }

    private static int RunLoop(Func<string> readLine, Func<bool> alive)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        string? line;
        while (alive())
        {
            try
            {
                line = readLine();
            }
            catch (Exception ex)
            {
                // UI 直接退出/被关掉:连接断开属正常收场,不是致命错误
                Diag.Log("agent: read closed " + ex.GetType().Name + ": " + ex.Message);
                break;
            }

            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }

            string cmd;
            try { cmd = doc.RootElement.GetProperty("cmd").GetString() ?? ""; }
            catch { continue; }
            Diag.Log("agent: rx cmd=" + cmd);

            if (cmd == "start" && _session is null)
            {
                Diag.Log("agent: cmd=start branch");
                try
                {
                    var settings = JsonSerializer.Deserialize<RecordSettings>(
                        doc.RootElement.GetProperty("settings").GetRawText(), JsonDefaults.Options)
                        ?? new RecordSettings();

                    _session = RecordingSession.Create(settings);
                    _session.Completed += r =>
                    {
                        StopTicker();
                        Emit(new
                        {
                            ev = "completed",
                            path = r.VideoPath,
                            bytes = r.Bytes,
                            sec = r.Duration.TotalSeconds,
                            segments = r.Segments,
                        });
                        done.TrySetResult();
                    };
                    _session.Failed += m =>
                    {
                        StopTicker();
                        Emit(new { ev = "failed", msg = m });
                        done.TrySetResult();
                    };
                    _session.Notice += m => Emit(new { ev = "notice", msg = m });
                    _session.StateChanged += OnSessionStateChanged;

                    // StartAsync 内部已把异常转成 Failed 事件,这里不会静默失败
                    _ = _session.StartAsync();
                }
                catch (Exception ex)
                {
                    Emit(new { ev = "failed", msg = ex.Message });
                    _session = null;
                    done.TrySetResult();
                }
            }
            else if (cmd == "pause") _session?.Pause();
            else if (cmd == "resume") _session?.Resume();
            else if (cmd == "snapshot")
            {
                string? shot = _session?.TakeSnapshot();
                Emit(new { ev = "snapshot", path = shot, ok = shot is not null });
            }
            else if (cmd == "stop") _session?.Stop();
            else if (cmd == "quit") break;

            if (done.Task.IsCompleted) break; // 会话完结,agent 自行退出
        }

        // 客户端断连但还在录:尽量把文件收尾,不要留下半截 mp4
        if (_session is not null &&
            _session.State is SessionState.Recording or SessionState.Paused)
        {
            try { _session.Stop(); } catch { }
            done.Task.Wait(TimeSpan.FromSeconds(15));
        }

        StopTicker();
        return 0;
    }

    private static void OnSessionStateChanged(SessionState state)
    {
        switch (state)
        {
            case SessionState.Recording:
                if (!_startedSent)
                {
                    _startedSent = true;
                    Emit(new { ev = "started" });
                    StartTicker();
                }
                Emit(new { ev = "state", s = "recording" });
                break;

            case SessionState.Paused:
                Emit(new { ev = "state", s = "paused" });
                break;

            case SessionState.Finalizing:
                StopTicker();
                Emit(new { ev = "state", s = "finalizing" });
                break;
        }
    }

    private static void StartTicker()
    {
        StopTicker();
        Interlocked.Exchange(ref _tickCount, 0);
        _ticker = new Timer(_ =>
        {
            var session = _session;
            if (session is null) return;

            Emit(new { ev = "tick", sec = session.Elapsed.TotalSeconds });

            // 5 秒一次资源巡检(200ms × 25):分辨率变化与磁盘将满都必须主动收尾
            if (Interlocked.Increment(ref _tickCount) % 25 == 0)
                WatchResources(session);
        }, null, 200, 200);
    }

    private static int _tickCount;

    private static void WatchResources(RecordingSession session)
    {
        try
        {
            if (session.State is not (SessionState.Recording or SessionState.Paused)) return;

            if (session.PrimaryResolutionChanged())
            {
                Diag.Log("watch: resolution changed, stopping");
                Emit(new { ev = "notice", msg = "检测到显示器分辨率变化,已停止录制并保存。" });
                session.Stop();
                return;
            }

            long free = session.AvailableOutputBytes();
            if (free != long.MaxValue && free < 300L * 1024 * 1024)
            {
                Diag.Log("watch: disk low " + free / 1048576 + "MB, stopping");
                Emit(new { ev = "notice", msg = "输出磁盘剩余不足 300 MB,已停止录制并保存。" });
                session.Stop();
            }
        }
        catch (Exception ex)
        {
            Diag.Log("watch: failed " + ex.Message);
        }
    }

    private static void StopTicker()
    {
        try { _ticker?.Dispose(); } catch { }
        _ticker = null;
    }

    private static void Emit(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
            lock (EmitLock)
                _emit?.WriteLine(json);
        }
        catch
        {
            // 管道断开等:忽略,主循环会因 alive() 为 false 退出
        }
    }
}
