using System.IO;
using System.Windows;
using ScreenRec.App;
using ScreenRec.Core;

namespace ScreenRec.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
            Diag.Log("app: unhandled " + ex.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Diag.Log("app: domain unhandled " + ex.ExceptionObject);

        if (e.Args.Contains("--list-windows"))
        {
            // 诊断用:确认窗口枚举 API 是否可用(这个库有直接崩进程的前科)
            NativeBootstrap.EnsureLoaded();
            var list = WindowCatalog.List();
            var lines = list.Select(w => $"{w.Handle}\t{w.Title}").ToList();
            lines.Insert(0, $"count={list.Count}");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "screenrec_windows.txt"), lines);
            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--selftest"))
        {
            int code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // 单实例防双开。--selftest / --list-windows 在上面已提前返回,
        // 所以自测不受主实例的锁影响;agent 是独立进程,压根不进这里。
        if (!AcquireSingleInstance())
        {
            MessageBox.Show("ScreenRec 已经在运行。", "ScreenRec",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // UI 侧也要用内核的类型(窗口枚举/设备列表),必须先完成程序集加载
        NativeBootstrap.EnsureLoaded();

        Skins.Publish(SettingsStore.Load().Skin); // 恢复上次选中的主题(先于窗口创建)
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Diag.Log($"app: exit code={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    /// <summary>单实例互斥体:进程存活期持有(静态引用防 GC 释放句柄)。</summary>
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// 占住单实例互斥体,已有人占用则返回 false。
    /// 统一走 WaitOne(0) 抢所有权 —— createdNew=true 只代表"刚创建",
    /// 不代表拥有(initiallyOwned=false 时是无主互斥体),不能短路。
    /// WaitOne 遇 AbandonedMutexException 视为上个实例崩溃遗留的锁,由本进程接管。
    /// </summary>
    private static bool AcquireSingleInstance()
    {
        var mutex = new Mutex(initiallyOwned: false, @"Local\ScreenRec.SingleInstance");
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return false;
        }

        _singleInstanceMutex = mutex;
        return true;
    }
}
