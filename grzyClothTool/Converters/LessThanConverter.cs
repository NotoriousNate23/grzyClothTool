using System;
using System.Globalization;
using System.Windows.Data;

namespace grzyClothTool.Converters
{
    /// <summary>True when the bound number is below ConverterParameter (used for responsive layouts).</summary>
    public class LessThanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double number &&
                double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var limit))
            {
                return number > 0 && number < limit;
            }

            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
