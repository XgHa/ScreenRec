using System.Globalization;

namespace ScreenRec.Core;

/// <summary>
/// 轻量诊断日志:按天滚动 %TEMP%\screenrec_diag_yyyyMMdd.log,保留 7 天。
/// 录制链路跨 UI / agent / 内核,出问题时唯一能对齐时间线的就是文件日志。
/// </summary>
public static class Diag
{
    private const string FilePrefix = "screenrec_diag_";
    private const string LegacyFile = "screenrec_diag.log";
    private const int RetainDays = 7;
    private const long TruncateBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly int Pid = Environment.ProcessId;

    /// <summary>上次执行过期清理时的日期;同一天不重复扫描临时目录。</summary>
    private static int _prunedDay;

    /// <summary>当天的日志文件路径。</summary>
    public static string LogPath
    {
        get
        {
            int day = int.Parse(DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            return Path.Combine(Path.GetTempPath(), FilePrefix + day + ".log");
        }
    }

    public static void Log(string message)
    {
        try
        {
            int today = int.Parse(DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            lock (Gate)
            {
                if (_prunedDay != today)
                {
                    _prunedDay = today;
                    Prune(today);
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [{Pid}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断日志失败不能影响录制
        }
    }

    /// <summary>agent 每次启动调用:当天日志超过 2MB 就截断,兜底单日狂写日志的极端情况。</summary>
    public static void Reset()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > TruncateBytes)
            {
                lock (Gate)
                    File.WriteAllText(LogPath, string.Empty);
            }
        }
        catch { }
    }

    /// <summary>删除超过保留期的旧日志,含历史遗留的无日期 screenrec_diag.log。</summary>
    private static void Prune(int today)
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-RetainDays);
            foreach (var path in Directory.GetFiles(Path.GetTempPath(), FilePrefix + "*.log"))
            {
                var name = Path.GetFileName(path);
                if (name.Length < FilePrefix.Length + 8 + ".log".Length) continue;
                if (!DateTime.TryParseExact(
                        name.Substring(FilePrefix.Length, 8), "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    continue;
                if (day < cutoff)
                {
                    try { File.Delete(path); } catch { }
                }
            }

            // 旧版本是单文件 screenrec_diag.log,按修改时间判定过期
            var legacy = new FileInfo(Path.Combine(Path.GetTempPath(), LegacyFile));
            if (legacy.Exists && legacy.LastWriteTime < cutoff)
            {
                try { legacy.Delete(); } catch { }
            }
        }
        catch
        {
            // 清理失败不影响写日志
        }
    }
}
