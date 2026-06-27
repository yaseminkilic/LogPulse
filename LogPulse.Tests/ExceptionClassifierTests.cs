using LogPulse.Errors;
using LogPulse.Shared.Errors;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// The unit that actually stops the spam: verifies that each exception type is mapped to the correct
/// status / code / severity / notify / logging level through <see cref="ExceptionClassifier.Classify"/>,
/// its single source of truth.
/// </summary>
public class ExceptionClassifierTests
{
    [Fact]
    public void OperationCanceled_Silent_NotNotified_DebugLevel()
    {
        var c = ExceptionClassifier.Classify(new OperationCanceledException());

        Assert.Equal(499, c.StatusCode);
        Assert.Equal(ErrorCodes.RequestCancelled, c.ErrorCode);
        Assert.Equal(ErrorSeverity.Silent, c.Severity);
        Assert.False(c.Notify);
        Assert.Equal("", c.UserMessage);
        Assert.Equal(LogLevel.Debug, c.LogLevel);
    }

    [Fact]
    public void TaskCanceled_TreatedAsCancellation()
    {
        // TaskCanceledException : OperationCanceledException → should fall into the same branch.
        var c = ExceptionClassifier.Classify(new TaskCanceledException());

        Assert.Equal(ErrorCodes.RequestCancelled, c.ErrorCode);
        Assert.Equal(ErrorSeverity.Silent, c.Severity);
        Assert.False(c.Notify);
    }

    [Fact]
    public void Validation_BadRequest_WarningToast_UsesExceptionMessage()
    {
        var c = ExceptionClassifier.Classify(new ValidationException("E-posta zorunludur."));

        Assert.Equal(400, c.StatusCode);
        Assert.Equal(ErrorCodes.Validation, c.ErrorCode);
        Assert.Equal(ErrorSeverity.Warning, c.Severity);
        Assert.True(c.Notify);
        Assert.Equal("E-posta zorunludur.", c.UserMessage);
        Assert.Equal(LogLevel.Information, c.LogLevel);
    }

    [Fact]
    public void Business_UnprocessableEntity_CarriesDomainCodeAndUserMessage()
    {
        var c = ExceptionClassifier.Classify(
            new BusinessException("STOCK_INSUFFICIENT", "Yeterli stok yok."));

        Assert.Equal(422, c.StatusCode);
        Assert.Equal("STOCK_INSUFFICIENT", c.ErrorCode); // be.Code is carried verbatim
        Assert.Equal(ErrorSeverity.Warning, c.Severity);
        Assert.True(c.Notify);
        Assert.Equal("Yeterli stok yok.", c.UserMessage);
        Assert.Equal(LogLevel.Information, c.LogLevel); // doesn't pollute SQLite
    }

    [Fact]
    public void Unauthorized_Forbidden_GenericUserMessage()
    {
        var c = ExceptionClassifier.Classify(new UnauthorizedAccessException("iç detay"));

        Assert.Equal(403, c.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, c.ErrorCode);
        Assert.Equal(ErrorSeverity.Warning, c.Severity);
        Assert.True(c.Notify);
        Assert.Equal("Bu işlem için yetkiniz yok.", c.UserMessage); // internal detail does not leak
        Assert.Equal(LogLevel.Warning, c.LogLevel);
    }

    [Fact]
    public void UnknownException_Unhandled_Error500_GenericMessage()
    {
        // The internal message must not leak to the user; only the generic message.
        var c = ExceptionClassifier.Classify(new InvalidOperationException("null reference detayı"));

        Assert.Equal(500, c.StatusCode);
        Assert.Equal(ErrorCodes.Unhandled, c.ErrorCode);
        Assert.Equal(ErrorSeverity.Error, c.Severity);
        Assert.True(c.Notify);
        Assert.Equal("Beklenmeyen bir hata oluştu.", c.UserMessage);
        Assert.Equal(LogLevel.Error, c.LogLevel);
        Assert.DoesNotContain("null reference", c.UserMessage);
    }
}
