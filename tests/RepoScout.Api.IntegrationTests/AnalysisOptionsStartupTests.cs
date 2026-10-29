using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace RepoScout.Api.IntegrationTests;

public class AnalysisOptionsStartupTests
{
    [Theory]
    [InlineData("Analysis:MaxToolCalls", "0")]
    [InlineData("Analysis:MaxResultBytes", "-1")]
    [InlineData("Analysis:MaxDepth", "0")]
    [InlineData("Analysis:Deadline", "00:00:00")]
    [InlineData("Analysis:Deadline", "2.00:00:00")]
    public void Startup_InvalidAnalysisOption_FailsWithOptionsValidationException(string key, string value)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("AiModel:ApiKey", "test-key");
            b.UseSetting(key, value);
        });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
