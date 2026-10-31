using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.PaidStaging;
using Xunit;

namespace KeyboardWtf.Tests;

// Formatter-only checks: never initialize logging, write a log, load a profile,
// connect to any provider, or alter the original exceptions shown to callers.
public sealed class OperationalLogPrivacyTests
{
    private const string SecretKey = "synthetic-api-key-do-not-log-93f7";
    private const string UserUrl = "https://user:synthetic-url-password@private-document.example.invalid/secret-page?token=synthetic-url-token";
    private const string UserPrompt = "synthetic-selected-text-and-private-prompt-do-not-log";
    private const string InnerMarker = "synthetic-inner-exception-message-do-not-log";
    private static readonly string SensitiveMessage = $"{SecretKey}; {UserUrl}; {UserPrompt}";

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("argument")]
    [InlineData("cryptography")]
    [InlineData("json")]
    public void Exception_formatting_keeps_type_and_hresult_but_never_message_inner_stack_or_data(string kind)
    {
        var inner = new InvalidOperationException(InnerMarker);
        Exception exception = kind switch
        {
            "invalid-operation" => new InvalidOperationException(SensitiveMessage, inner),
            "argument" => new ArgumentException(SensitiveMessage, inner),
            "cryptography" => new CryptographicException(SensitiveMessage, inner),
            "json" => new JsonException(SensitiveMessage, inner),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        exception.Source = UserUrl;
        exception.HelpLink = UserUrl;
        exception.Data["synthetic-private-data"] = UserPrompt;
        exception = CaptureSyntheticFailure(exception);
        var before = exception.ToString();

        var formatted = Format(exception);

        AssertSafeDiagnostics(formatted, exception);
        Assert.Equal(before, exception.ToString());
        Assert.Equal(SensitiveMessage, exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Equal(InnerMarker, exception.InnerException.Message);
        Assert.Equal(UserUrl, exception.Source);
        Assert.Equal(UserPrompt, exception.Data["synthetic-private-data"]);
        Assert.Contains(nameof(CaptureSyntheticFailure), exception.StackTrace);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Http_failure_formatting_preserves_numeric_status_without_response_body_or_sensitive_message(HttpStatusCode status)
    {
        var exception = CaptureSyntheticFailure(new HttpRequestException(
            SensitiveMessage, new InvalidOperationException(InnerMarker), status));
        var originalMessage = exception.Message;

        var formatted = Format(exception);

        AssertSafeDiagnostics(formatted, exception);
        Assert.Contains(((int)status).ToString(CultureInfo.InvariantCulture), formatted);
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(originalMessage, exception.Message);
        Assert.Equal(InnerMarker, exception.InnerException.Message);
    }

    [Theory]
    [InlineData(WebSocketError.ConnectionClosedPrematurely, 10054)]
    [InlineData(WebSocketError.InvalidState, 10060)]
    public void Websocket_failure_formatting_preserves_actual_error_metadata_without_message(WebSocketError error, int nativeErrorArgument)
    {
        var exception = CaptureSyntheticFailure(new WebSocketException(
            error, nativeErrorArgument, SensitiveMessage, new InvalidOperationException(InnerMarker)));
        var originalMessage = exception.Message;
        // This overload does not copy a positive argument into NativeErrorCode.
        // Verify the actual exception metadata rather than assuming that mapping.
        var originalNativeError = exception.NativeErrorCode;
        var originalHResult = exception.HResult;
        var original = exception.ToString();

        var formatted = Format(exception);

        AssertSafeDiagnostics(formatted, exception);
        Assert.Contains("webSocketError=" + error, formatted);
        Assert.Contains("nativeError=" + originalNativeError.ToString(CultureInfo.InvariantCulture), formatted);
        Assert.Equal(error, exception.WebSocketErrorCode);
        Assert.Equal(originalNativeError, exception.NativeErrorCode);
        Assert.Equal(originalHResult, exception.HResult);
        Assert.Equal(originalMessage, exception.Message);
        Assert.Equal(InnerMarker, exception.InnerException.Message);
        Assert.Equal(original, exception.ToString());
    }

    [Fact]
    public void Websocket_native_error_overload_preserves_explicit_native_code_without_sensitive_message()
    {
        const int nativeError = 10054;
        var exception = CaptureSyntheticFailure(new WebSocketException(nativeError, SensitiveMessage));
        var original = exception.ToString();
        var originalHResult = exception.HResult;
        var originalError = exception.WebSocketErrorCode;

        var formatted = Format(exception);

        AssertSafeDiagnostics(formatted, exception);
        Assert.Contains("nativeError=" + nativeError.ToString(CultureInfo.InvariantCulture), formatted);
        Assert.Contains("webSocketError=" + originalError, formatted);
        Assert.Equal(nativeError, exception.NativeErrorCode);
        Assert.Equal(originalHResult, exception.HResult);
        Assert.Equal(originalError, exception.WebSocketErrorCode);
        Assert.Equal(SensitiveMessage, exception.Message);
        Assert.Equal(original, exception.ToString());
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.ConnectionReset)]
    public void Socket_failure_formatting_preserves_error_kind_without_private_exception_data(SocketError error)
    {
        var exception = CaptureSyntheticFailure(new SocketException((int)error));
        exception.Source = UserUrl;
        exception.HelpLink = UserUrl;
        exception.Data["synthetic-api-key"] = SecretKey;
        exception.Data["synthetic-selected-text"] = UserPrompt;
        var originalMessage = exception.Message;

        var formatted = Format(exception);

        AssertSafeDiagnostics(formatted, exception);
        Assert.Contains(error.ToString(), formatted);
        Assert.Equal(error, exception.SocketErrorCode);
        Assert.Equal(originalMessage, exception.Message);
        Assert.Equal(SecretKey, exception.Data["synthetic-api-key"]);
    }

    [Theory]
    [InlineData(nameof(PersonalAiFailure.Quota), 429)]
    [InlineData(nameof(PersonalAiFailure.Provider), 503)]
    [InlineData(nameof(PersonalAiFailure.Credential), null)]
    public void Personal_failure_formatting_preserves_classification_and_status_without_changing_user_feedback(string failureName, int? status)
    {
        var failure = Enum.Parse<PersonalAiFailure>(failureName);
        var exception = CaptureSyntheticFailure(new PersonalAiException(failure) { HttpStatusCode = status });
        exception.Data["synthetic-api-key"] = SecretKey;
        exception.Data["synthetic-selected-text"] = UserPrompt;
        exception.Source = UserUrl;
        var original = exception.ToString();
        var originalMessage = exception.Message;

        var formatted = Format(exception);

        AssertSafeDiagnostics(formatted, exception);
        Assert.Contains("personalFailure=" + failureName, formatted);
        Assert.Contains("httpStatus=" + (status?.ToString(CultureInfo.InvariantCulture) ?? "none"), formatted);
        Assert.Equal(failure, exception.Failure);
        Assert.Equal(status, exception.HttpStatusCode);
        Assert.Equal(originalMessage, exception.Message);
        Assert.Equal(original, exception.ToString());
        Assert.Equal(SecretKey, exception.Data["synthetic-api-key"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Paid_failure_formatting_preserves_status_without_echoing_sensitive_backend_message(HttpStatusCode status)
    {
        var exception = CaptureSyntheticFailure(new PaidApiException(status, SensitiveMessage));
        var original = exception.ToString();

        var formatted = Format(exception);

        AssertSafeDiagnostics(formatted, exception);
        Assert.Contains("httpStatus=" + ((int)status).ToString(CultureInfo.InvariantCulture), formatted);
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(SensitiveMessage, exception.Message);
        Assert.Equal(original, exception.ToString());
    }

    [Fact]
    public void Managed_allowance_failure_formatting_preserves_fixed_pre_dispatch_classification_and_user_feedback()
    {
        var exception = CaptureSyntheticFailure(new ManagedAllowanceRefusedException());
        exception.Data["synthetic-api-key"] = SecretKey;
        exception.Data["synthetic-selected-text"] = UserPrompt;
        exception.Source = UserUrl;
        var original = exception.ToString();
        var originalMessage = exception.Message;

        var formatted = Format(exception);

        AssertSafeDiagnostics(formatted, exception);
        Assert.Contains("managedAllowance=preDispatchRefused", formatted);
        Assert.Equal(originalMessage, exception.Message);
        Assert.Equal(original, exception.ToString());
        Assert.Equal(UserPrompt, exception.Data["synthetic-selected-text"]);
    }

    [Fact]
    public void Null_exception_formatting_returns_safe_nonempty_diagnostics_without_throwing()
    {
        var formatted = Format(null);

        Assert.False(string.IsNullOrWhiteSpace(formatted));
        Assert.DoesNotContain(SecretKey, formatted);
        Assert.DoesNotContain(UserPrompt, formatted);
    }

    private static string Format(Exception exception)
    {
        var method = typeof(AppLog).GetMethod("FormatException", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(typeof(string), method.ReturnType);
        Assert.Equal(typeof(Exception), Assert.Single(method.GetParameters()).ParameterType);
        return Assert.IsType<string>(method.Invoke(null, new object[] { exception }));
    }

    private static void AssertSafeDiagnostics(string formatted, Exception exception)
    {
        Assert.Contains(exception.GetType().Name, formatted);
        Assert.Contains("0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture), formatted);
        Assert.DoesNotContain(SecretKey, formatted);
        Assert.DoesNotContain(UserUrl, formatted);
        Assert.DoesNotContain("synthetic-url-password", formatted);
        Assert.DoesNotContain("synthetic-url-token", formatted);
        Assert.DoesNotContain(UserPrompt, formatted);
        Assert.DoesNotContain(InnerMarker, formatted);
        Assert.DoesNotContain(nameof(CaptureSyntheticFailure), formatted);
        Assert.DoesNotContain("OperationalLogPrivacyTests.cs", formatted);
        Assert.DoesNotContain("synthetic-private-data", formatted);
        Assert.DoesNotContain(exception.Message, formatted);
    }

    private static T CaptureSyntheticFailure<T>(T exception) where T : Exception
    {
        try { throw exception; }
        catch (T captured) { return captured; }
    }
}
