using Hook2Stream.Api;

namespace Hook2Stream.UnitTests;

public sealed class EncryptedMediaRangeTests
{
    [Theory]
    [InlineData("bytes=0-0", 100, true, 0, 1)]
    [InlineData("bytes=10-19", 100, true, 10, 10)]
    [InlineData("bytes=90-", 100, true, 90, 10)]
    [InlineData("bytes=-10", 100, true, 90, 10)]
    [InlineData("bytes=99-200", 100, true, 99, 1)]
    [InlineData("bytes=100-", 100, false, 0, 0)]
    [InlineData("bytes=0-1,4-5", 100, false, 0, 0)]
    public void Parses_exact_and_suffix_ranges_and_rejects_unsatisfiable_or_multi_range(
        string header, long total, bool valid, long expectedOffset, long expectedLength)
    {
        var result = EncryptedMediaResult.TryParseSingleRange(header, total, out var offset, out var length);
        Assert.Equal(valid, result);
        if (valid) { Assert.Equal(expectedOffset, offset); Assert.Equal(expectedLength, length); }
    }
}
