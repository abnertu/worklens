using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class EmailFormatTests
{
    [Theory]
    [InlineData("octo@example.com")]
    [InlineData("12345+octocat@users.noreply.github.com")]
    [InlineData("first.last@sub.example.co")]
    public void Valid_email_is_accepted(string email)
    {
        Assert.True(EmailFormat.IsValid(email));
    }

    [Theory]
    [InlineData("not an email")]
    [InlineData("octocat")]
    [InlineData("@example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_email_is_rejected(string? email)
    {
        Assert.False(EmailFormat.IsValid(email));
    }
}
