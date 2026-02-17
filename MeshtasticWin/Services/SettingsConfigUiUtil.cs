using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MeshtasticWin.Services;

public static class SettingsConfigUiUtil
{
    public static List<T> EnumValues<T>() where T : struct, Enum
        => Enum.GetValues(typeof(T)).Cast<T>().ToList();

    public static bool TryParseUInt(string? text, out uint value)
        => uint.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static bool TryParseInt(string? text, out int value)
        => int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static bool TryParseFloat(string? text, out float value)
        => float.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static string UIntText(uint value) => value.ToString(CultureInfo.InvariantCulture);
    public static string IntText(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static string FloatText(float value) => value.ToString(CultureInfo.InvariantCulture);
}
