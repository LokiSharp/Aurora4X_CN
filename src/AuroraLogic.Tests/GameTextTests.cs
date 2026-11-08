using AuroraLogic;
using Xunit;

namespace AuroraLogic.Tests;

public class GameTextTests
{
    [Fact]
    public void PrefixCut_KeepsEnglishShieldShape()
    {
        Assert.Equal("Alpha ", GameText.TakePrefix("Alpha Shields", 6));
        Assert.Equal("Alpha S10 / R2 Shields", GameText.ShieldCaption("Alpha Shields", "10", "2"));
    }

    [Fact]
    public void PrefixCut_RejectsShortChineseName()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameText.TakePrefix("硬铝装甲", "硬铝装甲".Length - 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameText.ShieldCaption("硬铝装甲", "10", "2"));
    }

    [Fact]
    public void SafeSlice_KeepsShortNameAndClampsTail()
    {
        Assert.Equal("硬铝装甲", GameText.Slice("硬铝装甲", 0, -3));
        Assert.Equal("改进型护盾发生器", GameText.ShieldCaptionSafe("改进型护盾发生器", "10", "2").Split('S')[0]);
        Assert.Equal("Alpha S10 / R2 Shields", GameText.ShieldCaptionSafe("Alpha Shields", "10", "2"));
        Assert.Equal("f", GameText.Slice("abcdef", 5, 4));
        Assert.Equal("", GameText.Slice("ab", 5, 2));
        Assert.Equal("", GameText.Slice(null, 0, 1));
    }

    [Fact]
    public void TailCut_UsesLengthMinusCount()
    {
        Assert.Equal("ef", GameText.TakeTail("abcdef", 2));
        Assert.Equal("硬铝", GameText.SliceFrom("硬铝", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameText.TakeTail("ab", 4));
    }

    [Fact]
    public void Drop_KeepsTextWhenStartIsNegative()
    {
        Assert.Equal("a, b", GameText.Drop("a, b, ", 4));
        Assert.Equal("x", GameText.Drop("x", -1));
        Assert.Equal("", GameText.Drop(null, 0));
    }

    [Fact]
    public void Plural_FollowsTheEnglishSuffixTable()
    {
        Assert.Equal("", GameText.Plural(""));
        Assert.Equal("Infrastructure", GameText.Plural("Infrastructure"));
        Assert.Equal("Colonies", GameText.Plural("Colony"));
        Assert.Equal("Infantry", GameText.Plural("Infantry"));
        Assert.Equal("Species", GameText.Plural("Species"));
        Assert.Equal("Cities", GameText.Plural("City"));
        Assert.Equal("Knives", GameText.Plural("Knife"));
        Assert.Equal("Leaves", GameText.Plural("Leaf"));
        Assert.Equal("Boxes", GameText.Plural("Box"));
        Assert.Equal("Fungus", GameText.Plural("Fungus"));
        Assert.Equal("Ia", GameText.Plural("Ion"));
        Assert.Equal("护盾s", GameText.Plural("护盾"));
    }

    [Fact]
    public void Stamp_ReadsFixedPositions()
    {
        DateTime stamp = GameText.ParseStamp("2020-01-02 03:04:05");
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), stamp);
        Assert.Throws<ArgumentOutOfRangeException>(() => GameText.ParseStamp("2020"));
        Assert.Null(GameText.ParseStampSafe("2020"));
        Assert.Equal(stamp, GameText.ParseStampSafe("2020-01-02 03:04:05"));
    }

    [Fact]
    public void Wrap_StopsWhenThereIsNoSpace()
    {
        IReadOnlyList<string> lines = GameText.Wrap("硬铝装甲很长", 4, text => text.Length);
        Assert.Equal(new[] { "硬铝装甲很长" }, lines);
    }

    [Fact]
    public void Wrap_SplitsOnSpaces()
    {
        IReadOnlyList<string> lines = GameText.Wrap("alpha beta gamma", 10, text => text.Length);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, lines);
    }

    [Fact]
    public void StoredNames_MatchOnlyTheOriginalSpelling()
    {
        Assert.True(GameText.SameStoredName("Earth", "Earth"));
        Assert.False(GameText.SameStoredName("地球", "Earth"));
        Assert.True(GameText.SameStoredName("Eris", "Eris"));
        Assert.False(GameText.SameStoredName("Daphne", "达芙妮"));
    }
}
