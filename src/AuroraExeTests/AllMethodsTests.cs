using Xunit;

namespace AuroraExeTests;

public class AllMethodsTests
{
    public static IEnumerable<object[]> Methods()
    {
        foreach (string line in File.ReadAllLines(BaselinePath()))
        {
            if (line.Length == 0)
                continue;
            string key = line.Split('\t')[0];
            yield return new object[] { key };
        }
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void EveryMethod_HasAStableResult(string key)
    {
        ProbeResult actual = Results.Value[key];
        Assert.Equal(Expected[key], actual.Status + "\t" + actual.Detail);
    }

    [Fact]
    public void WriteBaseline_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("WRITE_BASELINE") != "1")
            return;
        Assert.True(Results.Value.Count > 10000);
    }

    [Fact]
    public void Catalog_CoversEveryMethodInTheExecutable()
    {
        Assert.True(Results.Value.Count > 10000);
        Assert.Equal(Results.Value.Count, Expected.Count);
    }

    private static readonly Lazy<Dictionary<string, ProbeResult>> Results = new Lazy<Dictionary<string, ProbeResult>>(Load);

    private static readonly Dictionary<string, string> Expected = LoadExpected();

    private static Dictionary<string, ProbeResult> Load()
    {
        var map = new Dictionary<string, ProbeResult>(StringComparer.Ordinal);
        foreach (ProbeResult result in IlProbe.RunAll(IlProbe.FindGameExe()))
            map[result.Key] = result;
        string? write = Environment.GetEnvironmentVariable("WRITE_BASELINE");
        if (write == "1")
        {
            var lines = map.Values
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Key + "\t" + item.Status + "\t" + item.Detail);
            File.WriteAllLines(SourceBaseline(), lines);
        }
        return map;
    }

    private static Dictionary<string, string> LoadExpected()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = BaselinePath();
        if (!File.Exists(path))
            return map;
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0)
                continue;
            string[] parts = line.Split('\t');
            map[parts[0]] = parts[1] + "\t" + (parts.Length > 2 ? parts[2] : "");
        }
        return map;
    }

    private static string BaselinePath()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "baseline.tsv");
        if (File.Exists(output))
            return output;
        return SourceBaseline();
    }

    private static string SourceBaseline()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "AuroraExeTests.csproj")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null)
            return Path.Combine(AppContext.BaseDirectory, "baseline.tsv");
        return Path.Combine(dir, "baseline.tsv");
    }
}
