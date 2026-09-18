using System.Globalization;

namespace AltSetter;

/// <summary>Numeric formatting helpers that never depend on the host locale.</summary>
internal static class Num {
    public static string F(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string I(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
