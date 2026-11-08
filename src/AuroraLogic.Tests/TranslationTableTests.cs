using Xunit;

namespace AuroraLogic.Tests;

public class TranslationTableTests
{
    [Fact]
    public void ComparedNames_StayEnglish()
    {
        var rows = LoadUi();
        foreach (string name in new[] { "Earth", "Eris", "Daphne", "Jupiter", "Mars" })
        {
            Assert.True(rows.TryGetValue(name, out string? chinese), name);
            Assert.True(chinese.Length == 0, name);
        }
    }

    [Fact]
    public void UnitTokens_StayInTheTranslatedText()
    {
        foreach ((string english, string chinese) in LoadUi())
        {
            if (chinese.Length == 0 || chinese == english)
                continue;
            foreach (string token in new[] { "km/s", "km", "atm", "tons", "cm" })
            {
                if (HasToken(english, token))
                    Assert.Contains(token, chinese);
            }
        }
    }

    private static bool HasToken(string text, string token)
    {
        int start = 0;
        while (start < text.Length)
        {
            int at = text.IndexOf(token, start, StringComparison.Ordinal);
            if (at < 0)
                return false;
            bool left = at == 0 || !char.IsLetter(text[at - 1]);
            int end = at + token.Length;
            bool right = end >= text.Length || !char.IsLetter(text[end]);
            if (left && right)
                return true;
            start = at + 1;
        }
        return false;
    }

    private static Dictionary<string, string> LoadUi()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "exe", "ui.tsv")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.NotNull(dir);
        var rows = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(Path.Combine(dir, "exe", "ui.tsv")))
        {
            if (line.Length == 0 || line.StartsWith("english\t", StringComparison.Ordinal))
                continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            rows[parts[0]] = parts[1];
        }
        return rows;
    }
}
