using Omega.Core.Results;

namespace Omega.Core.Tests.Results;

public class ResultTests
{
    private static readonly Error SampleError = new("SAMPLE_ERROR", "Sample failure.");

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_carries_its_error()
    {
        var result = Result.Failure(SampleError);

        Assert.True(result.IsFailure);
        Assert.Equal(SampleError, result.Error);
    }

    [Fact]
    public void Failure_without_error_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void Success_with_value_exposes_the_value()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Reading_the_value_of_a_failure_throws()
    {
        var result = Result.Failure<int>(SampleError);

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Error_requires_code_and_message()
    {
        Assert.Throws<ArgumentException>(() => new Error(" ", "message"));
        Assert.Throws<ArgumentException>(() => new Error("CODE", ""));
    }
}
