using System.ComponentModel.DataAnnotations;
using RepoScout.Api.Agent;

namespace RepoScout.Api.UnitTests.Agent;

public class AnalysisOptionsTests
{
    private static AnalysisOptions Valid() => new()
    {
        Deadline = TimeSpan.FromMinutes(3),
        MaxToolCalls = 20,
        MaxResultBytes = 12000,
        MaxDepth = 2,
    };

    private static List<ValidationResult> Validate(AnalysisOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Validate_ValidOptions_HasNoErrors()
    {
        Assert.Empty(Validate(Valid()));
    }

    [Fact]
    public void Validate_DeadlineAtBounds_HasNoErrors()
    {
        Assert.Empty(Validate(new AnalysisOptions { Deadline = TimeSpan.FromMilliseconds(1), MaxToolCalls = 1, MaxResultBytes = 1, MaxDepth = 1 }));
        Assert.Empty(Validate(new AnalysisOptions { Deadline = TimeSpan.FromDays(1), MaxToolCalls = int.MaxValue, MaxResultBytes = int.MaxValue, MaxDepth = int.MaxValue }));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("1.00:00:00.001")]
    [InlineData("2.00:00:00")]
    public void Validate_DeadlineOutOfRange_ReportsDeadline(string deadline)
    {
        var options = new AnalysisOptions
        {
            Deadline = TimeSpan.Parse(deadline),
            MaxToolCalls = 20,
            MaxResultBytes = 12000,
            MaxDepth = 2,
        };

        var error = Assert.Single(Validate(options));
        Assert.Contains(nameof(AnalysisOptions.Deadline), error.MemberNames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_MaxToolCallsNotPositive_ReportsMaxToolCalls(int value)
    {
        var error = Assert.Single(Validate(new AnalysisOptions { Deadline = TimeSpan.FromMinutes(1), MaxToolCalls = value, MaxResultBytes = 1, MaxDepth = 1 }));
        Assert.Contains(nameof(AnalysisOptions.MaxToolCalls), error.MemberNames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_MaxResultBytesNotPositive_ReportsMaxResultBytes(int value)
    {
        var error = Assert.Single(Validate(new AnalysisOptions { Deadline = TimeSpan.FromMinutes(1), MaxToolCalls = 1, MaxResultBytes = value, MaxDepth = 1 }));
        Assert.Contains(nameof(AnalysisOptions.MaxResultBytes), error.MemberNames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_MaxDepthNotPositive_ReportsMaxDepth(int value)
    {
        var error = Assert.Single(Validate(new AnalysisOptions { Deadline = TimeSpan.FromMinutes(1), MaxToolCalls = 1, MaxResultBytes = 1, MaxDepth = value }));
        Assert.Contains(nameof(AnalysisOptions.MaxDepth), error.MemberNames);
    }

    [Fact]
    public void Validate_DefaultOptions_ReportsAllFourProperties()
    {
        Assert.Equal(4, Validate(new AnalysisOptions()).Count);
    }
}
