using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace LeafTab.Setup
{
    public partial class SetupWindow : Window
    {
        private readonly SetupOptions _options;
        private bool _isBusy;
        private bool _isCompleted;

        internal SetupWindow(SetupOptions options)
        {
            _options = options;
            InitializeComponent();

            SourceInitialized += OnWindowSourceInitialized;

            if (_options.IsUninstall)
            {
                Title = "卸载 LeafTab（一叶）";
                WindowBadgeText.Text = "LeafTab Uninstaller";
                HeroTitleText.Text = "卸载 LeafTab（一叶）";
                HeroSubtitleText.Text = "安全清理程序文件并恢复系统默认资源管理器关联";
                InstallConfigPanel.Visibility = Visibility.Collapsed;
                UninstallConfigPanel.Visibility = Visibility.Visible;
                UninstallLocationText.Text = "程序目录：" + _options.InstallDir;
                FooterHintText.Text = "卸载过程不会影响您的任何个人文件夹内容";
                PrimaryActionButton.Content = "确认卸载";
                PrimaryActionButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            }
            else
            {
                InstallPathTextBox.Text = _options.InstallDir;
                SetDefaultCheckBox.IsChecked = _options.SetDefaultExplorer;
                ContextMenuCheckBox.IsChecked = _options.AddContextMenu;
                ShortcutsCheckBox.IsChecked = _options.CreateShortcuts;
                LaunchNowCheckBox.IsChecked = _options.LaunchAfterInstall;
                UpdateInstallButtonLabel();
            }
        }

        private void OnWindowSourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && Environment.OSVersion.Version.Build >= 22000)
                {
                    int pref = 2; // DWMWCP_ROUND
                    DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
                }
            }
            catch
            {
            }
        }

        private void InstallPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateInstallButtonLabel();
        }

        private void UpdateInstallButtonLabel()
        {
            if (_options == null || _options.IsUninstall || PrimaryActionButton == null)
                return;

            try
            {
                string dir = (InstallPathTextBox.Text ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "LeafTab.exe")))
                {
                    PrimaryActionButton.Content = "更新 / 重新安装";
                }
                else
                {
                    PrimaryActionButton.Content = "立即安装";
                }
            }
            catch
            {
                PrimaryActionButton.Content = "立即安装";
            }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
                return;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            string? picked = SetupEngine.PickInstallFolderDialog(hwnd, InstallPathTextBox.Text ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                InstallPathTextBox.Text = picked;
            }
        }

        private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
                return;

            if (_isCompleted)
            {
                Close();
                return;
            }

            _isBusy = true;
            CancelButton.IsEnabled = false;
            TopCloseButton.IsEnabled = false;
            PrimaryActionButton.IsEnabled = false;

            InstallConfigPanel.Visibility = Visibility.Collapsed;
            UninstallConfigPanel.Visibility = Visibility.Collapsed;
            ProgressCardPanel.Visibility = Visibility.Visible;

            if (_options.IsUninstall)
            {
                _options.RemoveUserDataOnUninstall = RemoveUserDataCheckBox.IsChecked == true;
                StatusDetailText.Text = "正在安全还原注册表关联并释放文件…";

                var res = await SetupEngine.ExecuteUninstallAsync(_options, (pct, msg) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetupProgressBar.Value = pct;
                        StatusStepText.Text = msg;
                    });
                });

                _isBusy = false;
                TopCloseButton.IsEnabled = true;
                PrimaryActionButton.IsEnabled = true;

                if (res.Ok)
                {
                    _isCompleted = true;
                    StatusDetailText.Text = "感谢使用 LeafTab（一叶），点击「完成」关闭向导。";
                    CancelButton.Visibility = Visibility.Collapsed;
                    PrimaryActionButton.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                    PrimaryActionButton.Content = "完成";
                    Closed += (_, _) => SetupEngine.ScheduleSelfDeleteIfRunningAsUninstaller(_options.InstallDir);
                }
                else
                {
                    StatusStepText.Text = "卸载遇到问题";
                    StatusDetailText.Text = res.Message;
                    CancelButton.IsEnabled = true;
                    PrimaryActionButton.Content = "重试";
                }
            }
            else
            {
                _options.InstallDir = (InstallPathTextBox.Text ?? string.Empty).Trim();
                _options.SetDefaultExplorer = SetDefaultCheckBox.IsChecked == true;
                _options.AddContextMenu = ContextMenuCheckBox.IsChecked == true;
                _options.CreateShortcuts = ShortcutsCheckBox.IsChecked == true;
                _options.LaunchAfterInstall = LaunchNowCheckBox.IsChecked == true;

                var res = await SetupEngine.ExecuteInstallAsync(_options, (pct, msg) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetupProgressBar.Value = pct;
                        StatusStepText.Text = msg;
                    });
                });

                _isBusy = false;
                TopCloseButton.IsEnabled = true;
                PrimaryActionButton.IsEnabled = true;

                if (res.Ok)
                {
                    _isCompleted = true;
                    StatusDetailText.Text = "已安装至：" + res.InstalledExePath;
                    CancelButton.Visibility = Visibility.Collapsed;
                    PrimaryActionButton.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                    PrimaryActionButton.Content = "完成";

                    if (_options.LaunchAfterInstall)
                    {
                        // Close wizard automatically shortly after launching LeafTab so it doesn't sit behind
                        Close();
                    }
                }
                else
                {
                    StatusStepText.Text = "安装失败";
                    StatusDetailText.Text = res.Message;
                    InstallConfigPanel.Visibility = Visibility.Visible;
                    ProgressCardPanel.Visibility = Visibility.Collapsed;
                    CancelButton.IsEnabled = true;
                    PrimaryActionButton.Content = "重试安装";
                }
            }
        }

        private void CancelOrClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
                return;
            Close();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
