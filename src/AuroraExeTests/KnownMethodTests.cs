using Xunit;

namespace AuroraExeTests;

public class KnownMethodTests
{
    private static object? Run(string fullName, params object?[] args)
    {
        return IlProbe.Execute(IlProbe.FindGameExe(), fullName, args);
    }

    [Fact]
    public void PrefixSlice_CutsEnglishShieldName()
    {
        object? value = Run("System.String lg::b(System.String,System.Int32)", "Alpha Shields", 6);
        Assert.Equal("Alpha ", value);
    }

    [Fact]
    public void PrefixSlice_RejectsShortName()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Run("System.String lg::b(System.String,System.Int32)", "硬铝装甲", -3));
    }

    [Fact]
    public void TailSlice_TakesTheEnd()
    {
        object? value = Run("System.String lg::a(System.String,System.Int32)", "abcdef", 2);
        Assert.Equal("ef", value);
    }

    [Fact]
    public void Plural_UsesTheIrregularTable()
    {
        Assert.Equal("Colonies", Run("System.String lg::f(System.String)", "Colony"));
        Assert.Equal("Infantry", Run("System.String lg::f(System.String)", "Infantry"));
        Assert.Equal("Infrastructure", Run("System.String lg::f(System.String)", "Infrastructure"));
        Assert.Equal("Cities", Run("System.String lg::f(System.String)", "City"));
    }

    [Fact]
    public void Stamp_ReadsNineteenCharacters()
    {
        object? value = Run("System.DateTime lg::b(System.String)", "2020-01-02 03:04:05");
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), value);
    }
}
