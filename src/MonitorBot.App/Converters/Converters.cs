using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MonitorBot.App.Converters
{
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
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString() switch
            {
                "ERROR" => new SolidColorBrush(Color.FromRgb(231, 76, 60)),
                "WARN"  => new SolidColorBrush(Color.FromRgb(243, 156, 18)),
                "INFO"  => new SolidColorBrush(Color.FromRgb(52, 152, 219)),
                "DEBUG" => new SolidColorBrush(Color.FromRgb(127, 140, 141)),
                _       => new SolidColorBrush(Color.FromRgb(230, 237, 243))
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString() switch
            {
                "Running"  => new SolidColorBrush(Color.FromRgb(39, 174, 96)),
                "Success"  => new SolidColorBrush(Color.FromRgb(46, 204, 113)),
                "Failed"   => new SolidColorBrush(Color.FromRgb(231, 76, 60)),
                "Retrying" => new SolidColorBrush(Color.FromRgb(243, 156, 18)),
                _          => new SolidColorBrush(Color.FromRgb(149, 165, 166))
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
