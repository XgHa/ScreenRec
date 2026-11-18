using System.Net;
using System.Net.Sockets;

namespace ScreenRec.App;

/// <summary>本地端口分配:先探测可绑定再返回,把随机端口撞车消在 spawn 之前。</summary>
public static class AgentPort
{
    /// <summary>
    /// 找一个当前可绑定的本地端口。先试绑定再关闭,存在毫秒级 TOCTOU 窗口,
    /// 由调用方的 spawn 重试兜底。5 次全失败说明本机端口异常紧张,直接报错。
    /// </summary>
    public static int PickFree()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int candidate = Random.Shared.Next(40000, 60000);
            var listener = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                listener.Start();
                listener.Stop();
                return candidate;
            }
            catch (SocketException)
            {
                // 端口被占,换下一个
            }
        }
        throw new InvalidOperationException("连续 5 次都没找到可用端口,请稍后重试。");
    }
}
