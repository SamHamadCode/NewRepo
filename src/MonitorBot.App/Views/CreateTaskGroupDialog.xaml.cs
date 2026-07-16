using System.Windows;
using System.Windows.Input;

namespace MonitorBot.App.Views
{
    public partial class CreateTaskGroupDialog : Window
    {
        public string GroupName { get; private set; } = string.Empty;
        public string GroupSite { get; private set; } = string.Empty;

        public CreateTaskGroupDialog()
        {
            InitializeComponent();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Don't drag when the user is clicking a button (e.g. the close button)
            if (e.OriginalSource is System.Windows.Controls.Button) return;
            DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            GroupName = GroupNameBox.Text.Trim();
            GroupSite = (GroupSiteBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()
                        ?? string.Empty;

            if (string.IsNullOrWhiteSpace(GroupName))
            {
                GroupNameBox.Focus();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void GroupNameBox_GotFocus(object sender, RoutedEventArgs e)
        {
            // Select all text on focus so the user can type right away
            GroupNameBox.SelectAll();
        }
    }
}
