using Microsoft.UI.Xaml.Data;
using System;

namespace System_Monitor.Converters
{
    public class TemperatureConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null)
                return "N/A";

            if (double.TryParse(value.ToString(), out double temp))
            {
                return $"{temp:F1}°C";
            }

            return value.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
