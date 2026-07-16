using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MonitorBot.App.Converters
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value != null ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>Returns Visible when an integer value is 0 (used for empty-state placeholders).</summary>
    public class ZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility.Visible;
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility.Collapsed;
    }

    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value != null;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class LogLevelToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() switch
            {
                "ERROR" => new SolidColorBrush(Color.FromRgb(255, 76, 106)),
                "WARN"  => new SolidColorBrush(Color.FromRgb(255, 184, 0)),
                "INFO"  => new SolidColorBrush(Color.FromRgb(0, 200, 255)),
                "DEBUG" => new SolidColorBrush(Color.FromRgb(120, 120, 160)),
                _       => new SolidColorBrush(Color.FromRgb(232, 232, 240))
            };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() switch
            {
                "Running"     => new SolidColorBrush(Color.FromRgb(0, 200, 150)),
                "Success"     => new SolidColorBrush(Color.FromRgb(0, 200, 150)),
                "Failed"      => new SolidColorBrush(Color.FromRgb(255, 76, 106)),
                "Retrying"    => new SolidColorBrush(Color.FromRgb(255, 184, 0)),
                "CheckingOut" => new SolidColorBrush(Color.FromRgb(139, 92, 246)),
                "Stopped"     => new SolidColorBrush(Color.FromRgb(64, 64, 90)),
                _             => new SolidColorBrush(Color.FromRgb(64, 64, 90))
            };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// Returns NavButtonActive style when the current page matches the button's page parameter,
    /// otherwise returns NavButton. Used to highlight the active sidebar icon.
    /// </summary>
    public class NavStyleSelectorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var currentPage = value?.ToString();
            var buttonPage  = parameter?.ToString();
            var app         = Application.Current;
            var activeStyle = app.TryFindResource("NavButtonActive") as Style;
            var normalStyle = app.TryFindResource("NavButton") as Style;
            return (currentPage == buttonPage ? activeStyle : null) ?? normalStyle;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// Takes a SolidColorBrush and returns a 30/255-alpha version for badge backgrounds.
    /// </summary>
    public class StatusBgConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SolidColorBrush b)
                return new SolidColorBrush(Color.FromArgb(30, b.Color.R, b.Color.G, b.Color.B));
            return new SolidColorBrush(Colors.Transparent);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
