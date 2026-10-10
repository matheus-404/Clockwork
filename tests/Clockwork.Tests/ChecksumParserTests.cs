using Clockwork.Services;
using Xunit;

namespace Clockwork.Tests;

public class ChecksumParserTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    [Fact]
    public void NormalizeDigest_AcceptsGitHubStyleAndBareHex()
    {
        Assert.Equal(HashA, ChecksumParser.NormalizeDigest("sha256:" + HashA.ToUpperInvariant()));
        Assert.Equal(HashA, ChecksumParser.NormalizeDigest(HashA));
        Assert.Equal(HashA, ChecksumParser.NormalizeDigest("  SHA256:" + HashA + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:abc")]
    [InlineData("sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("not a hash")]
    public void NormalizeDigest_RejectsAnythingElse(string? digest)
    {
        Assert.Null(ChecksumParser.NormalizeDigest(digest));
    }

    [Fact]
    public void FindHashForFile_PicksTheLineThatNamesTheFile_NotTheFirstHash()
    {
        var text = $"{HashA}  PresentMon-2.3.1-arm64.msi\n{HashB}  PresentMon-2.3.1-x64.msi\n";

        Assert.Equal(HashB, ChecksumParser.FindHashForFile(text, "PresentMon-2.3.1-x64.msi", allowLoneHash: true));
        Assert.Equal(HashA, ChecksumParser.FindHashForFile(text, "PresentMon-2.3.1-arm64.msi", allowLoneHash: false));
    }

    [Fact]
    public void FindHashForFile_HandlesWindowsLineEndingsAndMarkdown()
    {
        var text = $"| File | SHA-256 |\r\n|---|---|\r\n| PresentMon-x64.msi | `{HashB}` |\r\n";

        Assert.Equal(HashB, ChecksumParser.FindHashForFile(text, "PresentMon-x64.msi", allowLoneHash: false));
    }

    [Fact]
    public void FindHashForFile_AcceptsAPerAssetChecksumFile_OnlyWhenAllowed()
    {
        var text = HashA + "\n";

        Assert.Equal(HashA, ChecksumParser.FindHashForFile(text, "PresentMon-x64.msi", allowLoneHash: true));
        Assert.Null(ChecksumParser.FindHashForFile(text, "PresentMon-x64.msi", allowLoneHash: false));
    }

    [Fact]
    public void FindHashForFile_DoesNotAcceptALoneHashThatBelongsToAnotherFile()
    {
        var text = $"{HashA}  PresentMon-2.3.1.zip\n";

        Assert.Null(ChecksumParser.FindHashForFile(text, "PresentMon-x64.msi", allowLoneHash: true));
    }

    [Fact]
    public void FindHashForFile_DoesNotGuessWhenSeveralHashesAreUnlabelled()
    {
        var text = $"{HashA}\n{HashB}\n";

        Assert.Null(ChecksumParser.FindHashForFile(text, "PresentMon-x64.msi", allowLoneHash: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindHashForFile_ReturnsNullForEmptyText(string? text)
    {
        Assert.Null(ChecksumParser.FindHashForFile(text, "x.msi", allowLoneHash: true));
    }
}
