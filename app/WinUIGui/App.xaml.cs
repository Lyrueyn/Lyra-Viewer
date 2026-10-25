using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using System.Threading;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIGui;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    // ★ 主窗口静态引用：视频窗「回到主界面」按钮要用（以前这个按钮没有 Click，是个死按钮）
    public static MainWindow? Main { get; private set; }
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // 全局未处理异常：写入 exe 旁边的 crash.log，定位崩溃根因
        System.AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            DshLog($"[AppDomain] {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        { DshLog($"[Task] {e.Exception}"); e.SetObserved(); };
        InitializeComponent();
        UnhandledException += (s, e) =>
        { DshLog($"[Xaml] {e.Exception}"); e.Handled = true; };
    }

    private static void DshLog(object o)
    {
        try
        {
            string p = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
            System.IO.File.AppendAllText(p, $"[{DateTime.Now:HH:mm:ss.fff}] {o}\r\n");
        }
        catch { }
    }

    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>单实例：已有璃鸢镜窗口时激活它并退出本次启动。</summary>
    private static bool TryAcquireSingleInstance()
    {
        bool created;
        _singleInstanceMutex = new Mutex(true, @"Local\LyraViewer.SingleInstance", out created);
        if (created) return true;
        try { _singleInstanceMutex.Dispose(); } catch { }
        _singleInstanceMutex = null;
        for (int i = 0; i < 50; i++)
        {
            IntPtr hwnd = FindWindow(null, "璃鸢镜");
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
                    SetForegroundWindow(hwnd);
                }
                catch { }
                return false;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (!TryAcquireSingleInstance()) { Environment.Exit(0); return; }
        var mw = new MainWindow();
        _window = mw;
        Main = mw;
        _window.Activate();
        // 视频窗改为按需创建（MainWindow 点视频时才建），启动时不弹视频窗。
    }
}
