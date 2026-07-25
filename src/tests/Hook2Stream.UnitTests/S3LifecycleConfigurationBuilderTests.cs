using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Storage;

namespace Hook2Stream.UnitTests;

public sealed class S3LifecycleConfigurationBuilderTests
{
    [Fact]
    public void StorageOptionsEnablesMultipartAbortLifecycleByDefault()
    {
        var options = new StorageOptions();

        Assert.True(options.ConfigureMultipartAbortLifecycle);
    }

    [Fact]
    public void BuildForMinioIncludesOnlyStagingExpiration()
    {
        var configuration = S3LifecycleConfigurationBuilder.Build(
            new StorageOptions { ConfigureMultipartAbortLifecycle = false },
            new OperationalPolicyOptions());

        var stagingRule = Assert.Single(configuration.Rules);
        Assert.Equal("hook2stream-staging-expiry", stagingRule.Id);
        Assert.Equal(1, stagingRule.Expiration.Days);
        Assert.Null(stagingRule.AbortIncompleteMultipartUpload);
    }

    [Fact]
    public void BuildForAwsIncludesStagingExpirationAndMultipartAbort()
    {
        var configuration = S3LifecycleConfigurationBuilder.Build(
            new StorageOptions { ConfigureMultipartAbortLifecycle = true },
            new OperationalPolicyOptions());

        Assert.Collection(
            configuration.Rules,
            stagingRule =>
            {
                Assert.Equal("hook2stream-staging-expiry", stagingRule.Id);
                Assert.NotNull(stagingRule.Expiration);
            },
            abortRule =>
            {
                Assert.Equal("hook2stream-abort-incomplete-multipart", abortRule.Id);
                Assert.NotNull(abortRule.AbortIncompleteMultipartUpload);
            });
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(1, 24, 1, 1)]
    [InlineData(24, 25, 1, 2)]
    [InlineData(25, 48, 2, 2)]
    [InlineData(49, 49, 3, 3)]
    public void BuildRoundsLifecycleHoursUpToWholeDays(
        int stagingHours,
        int uploadSessionHours,
        int expectedStagingDays,
        int expectedMultipartAbortDays)
    {
        var configuration = S3LifecycleConfigurationBuilder.Build(
            new StorageOptions(),
            new OperationalPolicyOptions
            {
                StagingHours = stagingHours,
                UploadSessionHours = uploadSessionHours
            });

        var stagingRule = Assert.Single(
            configuration.Rules,
            rule => rule.Id == "hook2stream-staging-expiry");
        var abortRule = Assert.Single(
            configuration.Rules,
            rule => rule.Id == "hook2stream-abort-incomplete-multipart");

        Assert.Equal(expectedStagingDays, stagingRule.Expiration.Days);
        Assert.Equal(
            expectedMultipartAbortDays,
            abortRule.AbortIncompleteMultipartUpload.DaysAfterInitiation);
    }
}
