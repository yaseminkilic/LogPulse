using System.Text.Json;
using LogPulse.Logging;
using LogPulse.Shared.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// Correctness of client batch parsing (<see cref="LogIngestEndpoints.ParseBatch"/>):
/// both the plain array and <c>{ "events": [...] }</c> formats, level mapping, CorrelationId/Category
/// property extraction, and the <c>Source = "Client"</c> stamp.
/// </summary>
public class LogIngestEndpointsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void PlainArray_Format_IsAccepted()
    {
        // Serilog.Sinks.Http default formatter: plain array.
        const string body = """
        [
          { "Timestamp": "2026-06-22T10:00:00+00:00", "Level": "Warning", "RenderedMessage": "bir" },
          { "Timestamp": "2026-06-22T10:00:01+00:00", "Level": "Error",   "RenderedMessage": "iki" }
        ]
        """;

        var records = LogIngestEndpoints.ParseBatch(Parse(body));

        Assert.Equal(2, records.Count);
        Assert.Equal("bir", records[0].Message);
        Assert.All(records, r => Assert.Equal("Client", r.Source));
    }

    [Fact]
    public void EventsWrapper_Format_IsAccepted()
    {
        const string body = """
        { "events": [ { "Timestamp": "2026-06-22T10:00:00+00:00", "Level": "Information", "RenderedMessage": "sarmalı" } ] }
        """;

        var records = LogIngestEndpoints.ParseBatch(Parse(body));

        var r = Assert.Single(records);
        Assert.Equal("sarmalı", r.Message);
    }

    [Fact]
    public void UnknownShape_ReturnsEmpty()
    {
        Assert.Empty(LogIngestEndpoints.ParseBatch(Parse("""{ "foo": 1 }""")));
        Assert.Empty(LogIngestEndpoints.ParseBatch(Parse("42")));
    }

    [Fact]
    public void EmptyArray_ReturnsEmpty()
    {
        Assert.Empty(LogIngestEndpoints.ParseBatch(Parse("[]")));
    }

    [Theory]
    [InlineData("Verbose", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Fatal", LogLevel.Critical)]
    [InlineData("Bilinmeyen", LogLevel.Information)] // unrecognized → Information
    public void SerilogLevel_MapsToMicrosoftLogLevel(string serilogLevel, LogLevel expected)
    {
        var body = $$"""[ { "Level": "{{serilogLevel}}", "RenderedMessage": "x" } ]""";

        var r = Assert.Single(LogIngestEndpoints.ParseBatch(Parse(body)));

        Assert.Equal(expected, r.Level);
    }

    [Fact]
    public void Properties_CorrelationIdAndCategory_AreExtracted()
    {
        var body = $$"""
        [ {
            "Level": "Warning",
            "RenderedMessage": "veri hatası",
            "Properties": {
              "CorrelationId": "corr-xyz",
              "{{LogCategories.PropertyName}}": "{{LogCategories.DataAccess}}",
              "SourceContext": "LogPulse.Client.Pages.Orders"
            }
        } ]
        """;

        var r = Assert.Single(LogIngestEndpoints.ParseBatch(Parse(body)));

        Assert.Equal("corr-xyz", r.CorrelationId);
        Assert.Equal(LogCategories.DataAccess, r.Category);
        Assert.NotNull(r.PropertiesJson); // raw properties are preserved
    }

    [Fact]
    public void RenderedMessage_PreferredOverTemplate_FallsBackToEmpty()
    {
        var withRendered = Assert.Single(LogIngestEndpoints.ParseBatch(
            Parse("""[ { "MessageTemplate": "şablon {X}", "RenderedMessage": "render edildi" } ]""")));
        Assert.Equal("render edildi", withRendered.Message);

        var onlyTemplate = Assert.Single(LogIngestEndpoints.ParseBatch(
            Parse("""[ { "MessageTemplate": "yalnız şablon" } ]""")));
        Assert.Equal("yalnız şablon", onlyTemplate.Message);

        var neither = Assert.Single(LogIngestEndpoints.ParseBatch(
            Parse("""[ { "Level": "Information" } ]""")));
        Assert.Equal("", neither.Message);
    }

    [Fact]
    public void NoProperties_LeavesCorrelationAndCategoryNull()
    {
        var r = Assert.Single(LogIngestEndpoints.ParseBatch(
            Parse("""[ { "Level": "Error", "RenderedMessage": "x" } ]""")));

        Assert.Null(r.CorrelationId);
        Assert.Null(r.Category);
        Assert.Null(r.PropertiesJson);
    }
}
