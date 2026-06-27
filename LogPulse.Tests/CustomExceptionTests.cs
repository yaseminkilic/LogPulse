using LogPulse.Shared.Errors;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// Invariants of the custom exception types that classification relies on.
/// </summary>
public class CustomExceptionTests
{
    [Fact]
    public void Business_KeepsCodeAndUserMessage_AndSetsBaseMessage()
    {
        var ex = new BusinessException("ORDER_LOCKED", "Sipariş kilitli.");

        Assert.Equal("ORDER_LOCKED", ex.Code);
        Assert.Equal("Sipariş kilitli.", ex.UserMessage);
        Assert.Equal("Sipariş kilitli.", ex.Message); // base.Message = userMessage
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Business_BlankCode_FallsBackToDefault(string? code)
    {
        var ex = new BusinessException(code!, "mesaj");

        Assert.Equal("BUSINESS_RULE", ex.Code);
    }

    [Fact]
    public void Business_PreservesInnerException()
    {
        var inner = new InvalidOperationException("kök neden");
        var ex = new BusinessException("X", "mesaj", inner);

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Validation_DefaultErrors_IsEmptyNotNull()
    {
        var ex = new ValidationException("hata");

        Assert.NotNull(ex.Errors);
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public void Validation_FieldErrorsCtor_SetsDefaultMessage_AndKeepsErrors()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["Email"] = new[] { "Zorunlu." },
            ["Age"] = new[] { "Pozitif olmalı.", "Sayı olmalı." }
        };

        var ex = new ValidationException(errors);

        Assert.Equal("Bir veya daha fazla doğrulama hatası oluştu.", ex.Message);
        Assert.Equal(2, ex.Errors.Count);
        Assert.Equal(2, ex.Errors["Age"].Length);
    }
}
