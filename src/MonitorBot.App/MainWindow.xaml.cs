using System.Windows;
using MonitorBot.App.ViewModels;

namespace MonitorBot.App
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            Loaded += async (_, _) => await vm.InitAsync();
        }
    }
}
