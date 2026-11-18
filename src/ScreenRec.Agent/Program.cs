using System.Text;
using ScreenRec.Agent;

namespace ScreenRec.Agent;

/// <summary>
/// agent 进程入口:无界面,只承载录制会话。
/// UI 用 `ScreenRec.Agent.exe --port N` 拉起,localhost TCP + JSON 行协议通信。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        return AgentHost.Run(args);
    }
}
