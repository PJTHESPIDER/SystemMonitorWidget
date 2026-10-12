using Microsoft.UI.Xaml.Data;
using System;

namespace System_Monitor.Converters
{
    public class PercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null)
                return "N/A";

            if (double.TryParse(value.ToString(), out double doubleValue))
            {
                return $"{doubleValue:F1}%";
            }

            return value.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
