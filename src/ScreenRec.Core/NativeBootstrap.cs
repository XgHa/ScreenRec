using System.Reflection;

namespace ScreenRec.Core;

/// <summary>
/// 加载 ScreenRecorderLib(C++/CLI 混合程序集)。
/// 必须在任何该库类型被 JIT 引用之前调用,否则不是异常而是进程原生崩溃。
/// </summary>
public static class NativeBootstrap
{
    private static readonly object Gate = new();
    private static bool _loaded;

    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return true;

            // 普通发布:按程序集名解析不到时,从程序目录显式加载
            try
            {
                Assembly.Load("ScreenRecorderLib");
                _loaded = true;
                Diag.Log("native: loaded by name");
                return true;
            }
            catch { }

            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "ScreenRecorderLib.dll");
                var asm = Assembly.LoadFrom(path);
                _loaded = true;
                Diag.Log("native: loaded from " + path + " -> " + asm.FullName);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log("native: load failed " + ex);
                return false;
            }
        }
    }
}
