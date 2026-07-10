using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace MonitorBot.App.Views
{
    public partial class ToastWindow : Window
    {
        private static int _stackOffset = 0;
        private const int ToastHeight   = 80;
        private const int ToastMargin   = 12;
        private const int DisplayMs     = 5000;

        public ToastWindow(string title, string message)
        {
            InitializeComponent();

            // Strip non-ASCII emoji from title
            TitleText.Text   = Regex.Replace(title, @"[^\u0000-\u007F\s]", "").Trim();
            MessageText.Text = message;

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Position bottom-right, stacked upward for multiple toasts
            var screen = SystemParameters.WorkArea;
            Left = screen.Right - ActualWidth - ToastMargin;
            Top  = screen.Bottom - ActualHeight - ToastMargin - (_stackOffset * (ToastHeight + ToastMargin));
            _stackOffset++;

            // Auto-close after DisplayMs
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DisplayMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _stackOffset = Math.Max(0, _stackOffset - 1);
                Close();
            };
            timer.Start();
        }
    }
}
