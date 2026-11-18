using System.IO;

namespace ScreenRec.App;

/// <summary>
/// 定位 ScreenRec.Agent.exe。
///
/// agent 是独立进程,刻意不引 WPF:UI 进程里那套 PresentationFramework/PresentationCore
/// 空跑就要 25~30 MB,agent 不需要界面,没理由陪绑。
/// 发布包里两个 exe 同目录;开发期回落到 src/ScreenRec.Agent/bin 下的产物。
/// </summary>
internal static class AgentLauncher
{
    public static string? Locate()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "ScreenRec.Agent.exe");
        if (File.Exists(beside)) return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (string config in new[] { "Debug", "Release" })
            {
                foreach (string platform in new[] { "x64", "" })
                {
                    string candidate = platform.Length == 0
                        ? Path.Combine(dir.FullName, "src", "ScreenRec.Agent", "bin", config, "net9.0-windows", "ScreenRec.Agent.exe")
                        : Path.Combine(dir.FullName, "src", "ScreenRec.Agent", "bin", platform, config, "net9.0-windows", "ScreenRec.Agent.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        return null;
    }
}
