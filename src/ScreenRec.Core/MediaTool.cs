using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ScreenRec.Core;

public sealed record MediaProbeInfo(double Duration, int AudioStreams, int Width, int Height);

/// <summary>ScreenRec.Merger.exe 的定位与进程调用。所有路径都通过 ArgumentList 传递。</summary>
public static class MediaTool
{
    public static string? LocateMerger()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("SCREENREC_MERGER");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        string beside = Path.Combine(AppContext.BaseDirectory, "ScreenRec.Merger.exe");
        if (File.Exists(beside)) return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "ScreenRec.Merger", "x64", "Release", "ScreenRec.Merger.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static int Invoke(int timeoutMs, out string stdout, out string stderr, params string[] args)
    {
        string? merger = LocateMerger();
        if (merger is null) throw new FileNotFoundException("未找到 ScreenRec.Merger.exe。");

        var psi = new ProcessStartInfo
        {
            FileName = merger,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ScreenRec.Merger.exe。");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException("ScreenRec.Merger.exe 执行超时。");
        }
        Task.WaitAll(stdoutTask, stderrTask);
        stdout = stdoutTask.Result;
        stderr = stderrTask.Result;
        return proc.ExitCode;
    }

    public static MediaProbeInfo Probe(string path)
    {
        int exitCode = Invoke(20_000, out string stdout, out string stderr, "--probe", path);
        if (exitCode != 0) throw new InvalidOperationException("媒体探测失败:" + stderr.Trim());

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = line.IndexOf('=');
            if (split > 0) values[line[..split]] = line[(split + 1)..];
        }

        return new MediaProbeInfo(
            GetDouble(values, "duration"),
            GetInt(values, "audio_streams"),
            GetInt(values, "width"),
            GetInt(values, "height"));
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out string? text)
           && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out string? text)
           && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
}
