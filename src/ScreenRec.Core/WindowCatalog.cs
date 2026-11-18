using ScreenRecorderLib;

namespace ScreenRec.Core;

public readonly record struct WindowInfo(IntPtr Handle, string Title);

/// <summary>可录制窗口枚举。UI 不直接引用内核库,统一走这里。</summary>
public static class WindowCatalog
{
    public static IReadOnlyList<WindowInfo> List()
    {
        try
        {
            return Recorder.GetWindows()
                .Where(w => !string.IsNullOrWhiteSpace(w.Title))
                .Select(w => new WindowInfo(w.Handle, w.Title))
                .ToList();
        }
        catch (Exception ex)
        {
            // 和 GetSystemAudioCaptureDevices 一样,这个 API 有崩进程的前科:
            // 一旦异常先记录并返回空表,宁可不给窗口列表也不要炸掉调用进程。
            Diag.Log("windows: enumerate failed " + ex.GetType().Name + ": " + ex.Message);
            return Array.Empty<WindowInfo>();
        }
    }
}
