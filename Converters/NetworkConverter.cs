using Microsoft.UI.Xaml.Data;
using System;

namespace System_Monitor.Converters
{
    public class NetworkConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null)
                return "0 B/s";

            if (double.TryParse(value.ToString(), out double doubleValue))
            {
                return $"{doubleValue:N0} B/s";
            }

            return value.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
