using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class ExternalOperationProviderExceptionTests
{
    [Fact]
    public void Provider_exception_carries_failure_without_copying_sensitive_exception_text()
    {
        var failure = new ExternalOperationFailure(
            ExternalOperationFailureKind.Transport,
            "transport.unavailable",
            "provider diagnostic that must not become an exception message",
            retryable: true,
            providerCode: "secret-provider-code");

        var exception = new ExternalOperationProviderException(failure);

        exception.Failure.Should().BeSameAs(failure);
        exception.Message.Should().Be("The external operation provider reported a classified failure.");
        exception.Message.Should().NotContain(failure.Summary);
        exception.Message.Should().NotContain(failure.ProviderCode!);
    }

    [Fact]
    public void Provider_exception_requires_validated_failure()
    {
        var act = () => new ExternalOperationProviderException(null!);

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("failure");
    }
}
