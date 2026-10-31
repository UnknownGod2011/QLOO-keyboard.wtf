using System.Text.Json;
using KeyboardWtf.Staging.Api.Domain;

namespace KeyboardWtf.Staging.Api.Services;

// Only accounting metadata is interpreted here. Output/media, model choice,
// request transport and the cumulative Live high-water policy are unchanged.
internal static class ProviderUsageMetadata
{
    internal static bool TryParse(JsonElement metadata, out ProviderUsage usage, bool allowToolInput = false)
    {
        usage = new(0, 0, 0, 0, 0, 0, 0, null, Known: false);
        if (metadata.ValueKind != JsonValueKind.Object)
            return false;
        try
        {
            if (!Read(metadata, "promptTokenCount", true, out var input)
                || !Read(metadata, "totalTokenCount", true, out var total)
                || !Read(metadata, "candidatesTokenCount", false, out var output)
                || !Read(metadata, "thoughtsTokenCount", false, out var reasoning)
                || !Read(metadata, "cachedContentTokenCount", false, out var cached)
                || !Read(metadata, "toolUsePromptTokenCount", false, out var toolInput)
                // Ordinary GenerateContent exposes no tools. Live may report
                // tool prompts separately; retain them as exact input, not as
                // additional output or invented local token estimates.
                || (!allowToolInput && toolInput != 0)
                || total != checked(input + toolInput + output + reasoning)
                || cached > input
                || !Details(metadata, "promptTokensDetails", input, out var inputAudio, out var inputImage)
                || !Details(metadata, "candidatesTokensDetails", output, out var outputAudio, out var outputImage)
                || !Details(metadata, "cacheTokensDetails", cached, out var cachedAudio, out _)
                || !Details(metadata, "toolUsePromptTokensDetails", toolInput, out var toolAudio, out var toolImage,
                    requireComplete: toolInput > 0)
                // The current price contract has no cached-audio rate.
                || cachedAudio != 0
                || checked(cached + inputAudio + toolAudio) > checked(input + toolInput))
                return false;
            usage = new(checked(input + toolInput), output, cached, reasoning, checked(inputImage + toolImage + outputImage),
                checked(inputAudio + toolAudio + outputAudio), total, null, InputAudioTokens: checked(inputAudio + toolAudio),
                OutputAudioTokens: outputAudio, InputImageTokens: checked(inputImage + toolImage), OutputImageTokens: outputImage,
                Known: true);
            return IsAuthoritative(usage);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static bool IsAuthoritative(ProviderUsage usage)
    {
        if (!usage.Known || usage.Estimated)
            return false;
        try
        {
            return usage.InputTokens >= 0 && usage.OutputTokens >= 0 && usage.ReasoningTokens >= 0
                && usage.TotalTokens == checked(usage.InputTokens + usage.OutputTokens + usage.ReasoningTokens)
                && usage.CachedInputTokens >= 0 && usage.CachedInputTokens <= usage.InputTokens
                && usage.InputAudioTokens >= 0 && usage.InputImageTokens >= 0
                && checked(usage.CachedInputTokens + usage.InputAudioTokens) <= usage.InputTokens
                && checked(usage.InputAudioTokens + usage.InputImageTokens) <= usage.InputTokens
                && usage.OutputAudioTokens >= 0 && usage.OutputImageTokens >= 0
                && checked(usage.OutputAudioTokens + usage.OutputImageTokens) <= usage.OutputTokens
                && usage.AudioTokens == checked(usage.InputAudioTokens + usage.OutputAudioTokens)
                && usage.ImageTokens == checked(usage.InputImageTokens + usage.OutputImageTokens);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static ProviderUsage ReadCompletion(IDictionary<string, object> data)
    {
        static long Count(IDictionary<string, object> values, string name) =>
            values.TryGetValue("completed" + name, out var value) && value is long number ? number :
            value is int integer ? integer : -1;
        var known = data.TryGetValue("completedKnown", out var marker)
            ? marker is true
            : Count(data, "TotalTokens") > 0; // Never infer an old missing-zero receipt as known.
        var estimatedValid = data.TryGetValue("completedEstimated", out var estimate) && estimate is bool;
        var result = new ProviderUsage(Count(data, "InputTokens"), Count(data, "OutputTokens"),
            Count(data, "CachedInputTokens"), Count(data, "ReasoningTokens"), Count(data, "ImageTokens"),
            Count(data, "AudioTokens"), Count(data, "TotalTokens"),
            data.TryGetValue("completedProviderRequestId", out var requestId) ? requestId as string : null,
            data.TryGetValue("completedProviderModelVersion", out var modelVersion) ? modelVersion as string : null,
            Count(data, "InputAudioTokens"), Count(data, "OutputAudioTokens"),
            Count(data, "InputImageTokens"), Count(data, "OutputImageTokens"),
            Estimated: !estimatedValid || estimate is true, Known: known);
        return IsAuthoritative(result) ? result : result with { Known = false };
    }

    private static bool Read(JsonElement element, string name, bool required, out long count)
    {
        count = 0;
        if (!element.TryGetProperty(name, out var item))
            return !required; // Protobuf JSON may omit optional zero-valued counts.
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out count) && count >= 0;
    }

    private static bool Details(JsonElement metadata, string name, long maximum, out long audio, out long image,
        bool requireComplete = false)
    {
        audio = image = 0;
        if (!metadata.TryGetProperty(name, out var details))
            return !requireComplete;
        if (details.ValueKind != JsonValueKind.Array)
            return false;
        long sum = 0;
        foreach (var detail in details.EnumerateArray())
        {
            if (detail.ValueKind != JsonValueKind.Object
                || !detail.TryGetProperty("modality", out var kind) || kind.ValueKind != JsonValueKind.String
                || !Read(detail, "tokenCount", true, out var count))
                return false;
            sum = checked(sum + count);
            switch (kind.GetString()?.ToUpperInvariant())
            {
                case "TEXT": break;
                case "AUDIO": audio = checked(audio + count); break;
                case "IMAGE": case "VIDEO": image = checked(image + count); break;
                default: if (count != 0) return false; break;
            }
        }
        return requireComplete ? sum == maximum : sum <= maximum;
    }
}
