namespace AuroraLogic;

/// <summary>
/// Pure string rules copied from Aurora.exe. Original methods throw the way the
/// game does. Safe methods are the behavior built into AuroraCN.exe.
/// </summary>
public static class GameText
{
    public static string Slice(string? value, int start, int length)
    {
        if (value == null)
            return "";
        if (start < 0)
            start = 0;
        if (start > value.Length)
            return "";
        if (length < 0)
            length = value.Length - start;
        if (start + length > value.Length)
            length = value.Length - start;
        return value.Substring(start, length);
    }

    public static string SliceFrom(string? value, int start)
    {
        if (value == null)
            return "";
        if (start < 0)
            start = 0;
        if (start > value.Length)
            return "";
        return value.Substring(start);
    }

    public static string Drop(string? value, int start)
    {
        if (value == null)
            return "";
        if (start < 0 || start > value.Length)
            return value;
        return value.Remove(start);
    }

    public static string TakePrefix(string value, int length)
    {
        return value.Substring(0, length);
    }

    public static string TakeTail(string value, int length)
    {
        return value.Substring(value.Length - length);
    }

    public static string ShieldCaption(string name, string strength, string recharge)
    {
        string head = name.Substring(0, name.Length - 7);
        return head + "S" + strength + " / R" + recharge + " Shields";
    }

    public static string ShieldCaptionSafe(string? name, string strength, string recharge)
    {
        const string suffix = "Shields";
        string head = name != null && name.EndsWith(suffix, StringComparison.Ordinal)
            ? name.Substring(0, name.Length - suffix.Length)
            : name ?? "";
        return head + "S" + strength + " / R" + recharge + " Shields";
    }

    public static DateTime ParseStamp(string text)
    {
        int year = int.Parse(text.Substring(0, 4));
        int month = int.Parse(text.Substring(5, 2));
        int day = int.Parse(text.Substring(8, 2));
        int hour = int.Parse(text.Substring(11, 2));
        int minute = int.Parse(text.Substring(14, 2));
        int second = int.Parse(text.Substring(17, 2));
        return new DateTime(year, month, day, hour, minute, second);
    }

    public static DateTime? ParseStampSafe(string? text)
    {
        if (text == null || text.Length < 19)
            return null;
        if (!int.TryParse(Slice(text, 0, 4), out int year))
            return null;
        if (!int.TryParse(Slice(text, 5, 2), out int month))
            return null;
        if (!int.TryParse(Slice(text, 8, 2), out int day))
            return null;
        if (!int.TryParse(Slice(text, 11, 2), out int hour))
            return null;
        if (!int.TryParse(Slice(text, 14, 2), out int minute))
            return null;
        if (!int.TryParse(Slice(text, 17, 2), out int second))
            return null;
        try
        {
            return new DateTime(year, month, day, hour, minute, second);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string Plural(string? name)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
                return name ?? "";
            if (name == "Infrastructure")
                return name;
            if (name.EndsWith("s", StringComparison.Ordinal)
                || name.EndsWith("ese", StringComparison.Ordinal)
                || name.EndsWith("ics", StringComparison.Ordinal))
                return name;
            var irregular = new Dictionary<string, string>
            {
                ["Colony"] = "Colonies",
                ["Infantry"] = "Infantry",
                ["Militia"] = "Militia",
                ["Species"] = "Species"
            };
            if (irregular.TryGetValue(name, out string? known))
                return known;
            if (name.EndsWith("ey", StringComparison.Ordinal))
                return name + "s";
            if (name.EndsWith("y", StringComparison.Ordinal))
                return name.Substring(0, name.Length - 1) + "ies";
            if (name.EndsWith("fe", StringComparison.Ordinal))
                return name.Substring(0, name.Length - 2) + "ves";
            if (name.EndsWith("f", StringComparison.Ordinal))
                return name.Substring(0, name.Length - 1) + "ves";
            if (name.EndsWith("sh", StringComparison.Ordinal)
                || name.EndsWith("ch", StringComparison.Ordinal)
                || name.EndsWith("x", StringComparison.Ordinal)
                || name.EndsWith("z", StringComparison.Ordinal))
                return name + "es";
            if (name.EndsWith("us", StringComparison.Ordinal))
                return name.Substring(0, name.Length - 2) + "i";
            if (name.EndsWith("is", StringComparison.Ordinal))
                return name.Substring(0, name.Length - 2) + "es";
            if (name.EndsWith("on", StringComparison.Ordinal))
                return name.Substring(0, name.Length - 2) + "a";
            return name + "s";
        }
        catch (Exception)
        {
            return name ?? "";
        }
    }

    public static IReadOnlyList<string> Wrap(string? text, int limit, Func<string, int> width)
    {
        var lines = new List<string>();
        if (text == null)
            return lines;
        if (width(text) < limit)
        {
            lines.Add(text);
            return lines;
        }
        while (width(text) >= limit)
        {
            int space = text.LastIndexOf(' ');
            if (space < 0)
            {
                lines.Add(text);
                return lines;
            }
            string piece = text.Substring(0, space);
            while (piece.Length > 0 && width(piece) >= limit)
            {
                int earlier = piece.LastIndexOf(' ');
                if (earlier < 0)
                    break;
                piece = piece.Substring(0, earlier);
            }
            lines.Add(piece);
            int next = piece.Length + 1;
            if (next >= text.Length)
                return lines;
            text = text.Substring(next);
        }
        lines.Add(text);
        return lines;
    }

    public static bool SameStoredName(string literal, string stored)
    {
        return literal == stored;
    }
}
