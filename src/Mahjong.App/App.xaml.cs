using System.Windows;

namespace Mahjong.App
{
    /// <summary>App.xaml 的代码逻辑。</summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var window = new MainWindow();
            window.Show();
        }
    }
}
