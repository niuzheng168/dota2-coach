using System;
using System.Windows;

namespace DotaCoach.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !Environment.Is64BitProcess)
        {
            MessageBox.Show("本地助手需要 Windows 11 x64。", "无法启动");
            Shutdown();
            return;
        }
        MainWindow = new MainWindow(e.Args.Length > 0 ? e.Args[0] : null);
        MainWindow.Show();
    }
}
