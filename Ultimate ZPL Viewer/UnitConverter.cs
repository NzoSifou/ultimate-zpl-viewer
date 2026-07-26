using System;

namespace Ultimate_ZPL_Viewer;

public static class UnitConverter
{
    public static string UnitLabel(LengthUnit unit)
    {
        return unit switch
        {
            LengthUnit.Centimeters => "cm",
            LengthUnit.Inches => "po",
            _ => "mm"
        };
    }

    public static double FromMillimeters(double value, LengthUnit unit)
    {
        return unit switch
        {
            LengthUnit.Centimeters => value / 10d,
            LengthUnit.Inches => value / 25.4d,
            _ => value
        };
    }

    public static double ToMillimeters(double value, LengthUnit unit)
    {
        return unit switch
        {
            LengthUnit.Centimeters => value * 10d,
            LengthUnit.Inches => value * 25.4d,
            _ => value
        };
    }

    public static string FormatLength(double value)
    {
        return Math.Round(value, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool TryParseLength(string text, out double value)
    {
        return double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
