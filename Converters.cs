using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace inuMixer
{
    /// <summary>
    /// ピークホールド値 (0.0〜1.0) とコンテナの高さから、
    /// VerticalAlignment="Bottom" の Border の Margin.Bottom を計算するコンバーター。
    /// </summary>
    public class PeakHoldToMarginConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 2
                && values[0] is float peakHold
                && values[1] is double height
                && height > 0)
            {
                // ホールドラインの高さ 2px を考慮してはみ出しを防ぐ
                double bottom = peakHold * Math.Max(0, height - 2);
                return new Thickness(0, 0, 0, bottom);
            }
            return new Thickness(0);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
