using System.Reflection;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PiperVoiceDescriptorTests
{
    [Theory]
    [InlineData("EnOnnx", "en/en_US/amy/medium/en_US-amy-medium.onnx", 63_201_294L,
        "b3a6e47b57b8c7fbe6a0ce2518161a50f59a9cdd8a50835c02cb02bdd6206c18")]
    [InlineData("EnJson", "en/en_US/amy/medium/en_US-amy-medium.onnx.json", 4_882L,
        "95a23eb4d42909d38df73bb9ac7f45f597dbfcde2d1bf9526fdeaf5466977d77")]
    [InlineData("DeOnnx", "de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx", 63_201_294L,
        "7e64762d8e5118bb578f2eea6207e1a35a8e0c30595010b666f983fc87bb7819")]
    [InlineData("DeJson", "de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx.json", 4_819L,
        "974adee790533adb273a1ac88f49027d2a1b8f0f2cf4905954a4791e79264e85")]
    public void New_downloads_pin_the_same_official_voices_and_exact_origin_bytes(
        string prefix, string relativePath, long bytes, string sha256)
    {
        const string revision = "c10ece1aade47bb51c153c893d14e5bf8e5b7117";
        Assert.Equal($"https://huggingface.co/rhasspy/piper-voices/resolve/{revision}/{relativePath}",
            Constant<string>(prefix + "Url"));
        Assert.Equal(bytes, Constant<long>(prefix + "Bytes"));
        Assert.Equal(sha256, Constant<string>(prefix + "Sha256"));
    }

    private static T Constant<T>(string name)
    {
        var field = typeof(PiperInstaller).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetRawConstantValue());
    }
}
