using Hook2Stream.Application;
using Hook2Stream.Domain;

namespace Hook2Stream.UnitTests;

public sealed class MediaPolicyTests
{
    [Fact]
    public void Eleventh_visual_is_rejected()
    {
        var request = new CreateUploadRequest(
            AssetKind.Visual,
            "loop.mp4",
            "video/mp4",
            10 * 1024 * 1024,
            null);

        var errors = MediaPolicy.ValidateReservation(request, 10, 100 * 1024 * 1024)
            .ToDictionary();

        Assert.Contains("kind", errors.Keys);
    }

    [Fact]
    public void Replacement_can_be_reserved_at_visual_limit()
    {
        var request = new CreateUploadRequest(
            AssetKind.Visual,
            "loop.mp4",
            "video/mp4",
            10 * 1024 * 1024,
            Guid.CreateVersion7());

        var result = MediaPolicy.ValidateReservation(request, 10, 100 * 1024 * 1024);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Object_keys_contain_only_server_owned_identifiers()
    {
        var workspaceId = Guid.Parse("01900000-0000-7000-8000-000000000001");
        var projectId = Guid.Parse("01900000-0000-7000-8000-000000000002");
        var assetId = Guid.Parse("01900000-0000-7000-8000-000000000003");

        var key = ObjectKeyFactory.Original(workspaceId, projectId, assetId, 2);

        Assert.Equal(
            "w/01900000000070008000000000000001/p/01900000000070008000000000000002/assets/01900000000070008000000000000003/r/2/original",
            key);
        Assert.DoesNotContain("filename", key, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 60)]
    [InlineData(3, 300)]
    [InlineData(99, 300)]
    public void Retry_schedule_is_bounded(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), JobRetrySchedule.ForAttempt(attempt));
    }
}
