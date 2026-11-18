using System.Text.RegularExpressions;

namespace ScreenRec.Core;

/// <summary>一组同源的残留分段:base 名、期望的最终文件路径、按段号排序的分段列表。</summary>
public sealed record OrphanGroup(string BasePath, string FinalPath, IReadOnlyList<string> Parts, long TotalBytes);

/// <summary>恢复结果:成功时 Path 非空;Note 是给用户看的补充说明(跳过了几段、为什么失败)。</summary>
public sealed record OrphanMergeResult(string? Path, string Note);

/// <summary>
/// 崩溃/异常退出后遗留分段的发现与恢复。
/// 分段命名固定为「{最终名去扩展名}_part{n}.mp4」,正常结束要么改名要么合并后删除分段,
/// 所以启动时输出目录里还在的 _partN.mp4 一定是上次没走完的会话。
///
/// 关键事实:崩溃时正在写的那一段 MP4 没有 moov(内核只在 finalize 时写索引),
/// Media Foundation 直接打不开(0xC00D36C4),合并器遇到它会把整次合并判失败。
/// 所以恢复必须先逐段探测、只合并「从第 1 段起连续可读」的前缀,坏段另行隔离。
/// </summary>
public static class OrphanMerge
{
    /// <summary>坏段改名后缀:保留文件、退出扫描范围,下次启动不再重复提示。</summary>
    public const string BrokenSuffix = ".broken";

    private static readonly Regex PartPattern =
        new(@"^(?<base>.+)_part(?<num>\d+)\.mp4$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>扫描输出目录,按 base 名分组残留分段;组按最早分段修改时间排序。目录不存在返回空。</summary>
    public static IReadOnlyList<OrphanGroup> Scan(string outputDirectory)
    {
        var groups = new Dictionary<string, List<(int Num, string Path)>>();
        try
        {
            if (!Directory.Exists(outputDirectory)) return Array.Empty<OrphanGroup>();

            foreach (var path in Directory.GetFiles(outputDirectory, "*.mp4"))
            {
                var m = PartPattern.Match(Path.GetFileName(path));
                if (!m.Success) continue;
                // base 必须拼回完整路径:纯文件名会让 File.Move / --merge 落到进程 CWD(实测踩过)
                string basePath = Path.Combine(outputDirectory, m.Groups["base"].Value);
                if (!int.TryParse(m.Groups["num"].Value, out int num)) continue;

                if (!groups.TryGetValue(basePath, out var list))
                    groups[basePath] = list = new List<(int, string)>();
                list.Add((num, path));
            }
        }
        catch (Exception ex)
        {
            Diag.Log("orphan: scan failed " + ex.Message);
            return Array.Empty<OrphanGroup>();
        }

        var result = new List<OrphanGroup>();
        foreach (var (basePath, parts) in groups)
        {
            parts.Sort((a, b) => a.Num.CompareTo(b.Num));
            var paths = parts.Select(p => p.Path).ToList();
            long bytes = 0;
            foreach (var p in paths)
            {
                try { bytes += new FileInfo(p).Length; } catch { }
            }
            // 最终名与录制会话的命名规则一致:base + ".mp4"
            result.Add(new OrphanGroup(basePath, basePath + ".mp4", paths, bytes));
        }

        result.Sort((a, b) => File.GetLastWriteTimeUtc(a.Parts[0]).CompareTo(File.GetLastWriteTimeUtc(b.Parts[0])));
        Diag.Log($"orphan: scan found {result.Count} group(s) in {outputDirectory}");
        return result;
    }

    /// <summary>
    /// 合并一组分段为最终文件。可读段从 part1 起连续取,遇到第一个打不开的段就停在那里
    /// (崩溃只会截断正在写的那一段,后面的段不存在;真出现中间坏段时宁可少救也不拼错顺序)。
    /// 成功删除已合并的分段;坏段改名 .broken 保留;整体失败则原样保留、返回 null。
    /// </summary>
    public static OrphanMergeResult Merge(OrphanGroup group)
    {
        try
        {
            if (MediaTool.LocateMerger() is null)
            {
                Diag.Log("orphan: merger not found");
                return new OrphanMergeResult(null, "未找到 ScreenRec.Merger.exe,分段已保留。");
            }

            var readable = new List<string>();
            var broken = new List<string>();
            foreach (string part in group.Parts)
            {
                // 已出现过坏段就不再浪费一次进程探测:后面的段不参与合并
                if (broken.Count > 0 || !CanOpen(part))
                {
                    broken.Add(part);
                    continue;
                }
                readable.Add(part);
            }

            if (readable.Count == 0)
            {
                Quarantine(broken);
                Diag.Log("orphan: no readable segment, quarantined " + broken.Count);
                return new OrphanMergeResult(null,
                    $"崩溃时正在写入的分段不完整({broken.Count} 段),已改名 {BrokenSuffix} 保留,没有可恢复的画面。");
            }

            string? result;
            if (readable.Count == 1)
            {
                File.Move(readable[0], group.FinalPath, overwrite: true);
                Diag.Log("orphan: single part renamed -> " + group.FinalPath);
                result = group.FinalPath;
            }
            else
            {
                var args = new List<string> { "--merge", group.FinalPath };
                args.AddRange(readable);

                // 上次异常退出时句柄可能还没放干净,共享冲突重试几次再判失败
                int exitCode = 0;
                string stdout = string.Empty, stderr = string.Empty;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    exitCode = MediaTool.Invoke(120_000, out stdout, out stderr, args.ToArray());
                    bool sharingViolation = stderr.Contains("0x80070020", StringComparison.OrdinalIgnoreCase);
                    if (exitCode == 0 || !sharingViolation) break;
                    Thread.Sleep(400);
                }

                if (exitCode != 0 || !File.Exists(group.FinalPath) || new FileInfo(group.FinalPath).Length == 0)
                {
                    Diag.Log($"orphan: merge failed rc={exitCode} {stderr}");
                    return new OrphanMergeResult(null, "合并失败,分段已原样保留。");
                }

                Diag.Log("orphan: merge ok -> " + group.FinalPath + " " + stdout.Trim().Replace('\n', ' '));
                result = group.FinalPath;
            }

            foreach (string part in readable)
            {
                try { File.Delete(part); } catch { }
            }
            Quarantine(broken);

            string note = broken.Count > 0
                ? $"已跳过 {broken.Count} 个崩溃时未写完的分段(改名 {BrokenSuffix})"
                : string.Empty;
            return new OrphanMergeResult(result, note);
        }
        catch (Exception ex)
        {
            Diag.Log("orphan: merge exception " + ex);
            return new OrphanMergeResult(null, "恢复出错,分段已原样保留。");
        }
    }

    /// <summary>段落能否被 Media Foundation 打开(时长可读)。崩溃残留段会在这里被判死。</summary>
    private static bool CanOpen(string path)
    {
        try
        {
            var probe = MediaTool.Probe(path);
            if (probe.Duration > 0) return true;
            Diag.Log($"orphan: no duration, treat as broken: {Path.GetFileName(path)}");
            return false;
        }
        catch (Exception ex)
        {
            Diag.Log($"orphan: probe failed {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>坏段改名隔离:文件不删,只是退出 *.mp4 的扫描范围,避免每次启动重复提示。</summary>
    private static void Quarantine(IReadOnlyList<string> parts)
    {
        foreach (string part in parts)
        {
            try
            {
                string target = part + BrokenSuffix;
                File.Move(part, target, overwrite: true);
                Diag.Log("orphan: quarantined -> " + target);
            }
            catch (Exception ex)
            {
                Diag.Log("orphan: quarantine failed " + part + " " + ex.Message);
            }
        }
    }
}
