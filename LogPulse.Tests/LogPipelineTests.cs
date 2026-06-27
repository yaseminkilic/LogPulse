using LogPulse.Logging;
using LogPulse.Shared.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// Correctness of the "important log" persistence filter (<see cref="LogPipeline.IsImportant"/>):
/// level threshold OR an "always keep" category. This decision prevents SQLite from filling up with noise.
/// </summary>
public class LogPipelineTests
{
    private static LogPipeline CreatePipeline(LogIngestOptions? options = null) =>
        new(Options.Create(options ?? new LogIngestOptions()), NullLogger<LogPipeline>.Instance);

    private static LogRecord Record(LogLevel level, string? category = null) =>
        new(
            Timestamp: DateTimeOffset.UnixEpoch,
            Level: level,
            Message: "test",
            Exception: null,
            CorrelationId: null,
            Category: category,
            Source: "test",
            PropertiesJson: null);

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Warning, true)]  // default threshold
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    public void LevelThreshold_PersistsWarningAndAbove(LogLevel level, bool expected)
    {
        var pipeline = CreatePipeline();

        Assert.Equal(expected, pipeline.IsImportant(Record(level)));
    }

    [Theory]
    [InlineData(LogCategories.Critical)]
    [InlineData(LogCategories.DataAccess)]
    [InlineData(LogCategories.HubConnection)]
    public void AlwaysPersistCategory_PersistsEvenBelowThreshold(string category)
    {
        var pipeline = CreatePipeline();

        // Information is below the threshold, but the category is in the "always keep" list → persisted.
        Assert.True(pipeline.IsImportant(Record(LogLevel.Information, category)));
    }

    [Fact]
    public void UnlistedCategory_BelowThreshold_NotPersisted()
    {
        var pipeline = CreatePipeline();

        Assert.False(pipeline.IsImportant(Record(LogLevel.Information, "SomethingElse")));
    }

    [Fact]
    public void NullCategory_BelowThreshold_NotPersisted()
    {
        var pipeline = CreatePipeline();

        Assert.False(pipeline.IsImportant(Record(LogLevel.Debug, category: null)));
    }

    [Fact]
    public void CustomMinimumLevel_IsHonored()
    {
        var pipeline = CreatePipeline(new LogIngestOptions { PersistMinimumLevel = LogLevel.Error });

        Assert.False(pipeline.IsImportant(Record(LogLevel.Warning)));
        Assert.True(pipeline.IsImportant(Record(LogLevel.Error)));
    }
}
