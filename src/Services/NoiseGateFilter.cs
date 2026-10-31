namespace KeyboardWtf.Services;

/// <summary>
/// Simple frame-based noise gate for 16-bit mono PCM.
/// Zeroes out 10 ms frames whose RMS falls below the threshold, with a hold window
/// that keeps the gate open for a short tail after the signal drops — avoids cutting
/// the ends of words.
/// </summary>
public static class NoiseGateFilter
{
    // -38 dBFS is a good default: passes normal speech, suppresses AC hum / keyboard noise.
    private const double ThresholdDb  = -38.0;
    private const double HoldMs       = 180.0;  // keep gate open this long after signal drops

    public static byte[] Apply(byte[] pcm16, int sampleRate = 16_000)
    {
        if (pcm16 == null || pcm16.Length < 2)
            return pcm16;

        double threshold = Math.Pow(10.0, ThresholdDb / 20.0); // linear amplitude
        int frameSize    = sampleRate / 100;                    // 10 ms = 160 samples @ 16 kHz
        int frameSizeBytes = frameSize * 2;                     // 2 bytes per sample (16-bit)
        int holdFrames   = (int)Math.Ceiling(HoldMs / 10.0);   // 18 frames @ 10 ms

        var result     = (byte[])pcm16.Clone();
        int frameCount = result.Length / frameSizeBytes;
        int holdLeft   = 0;

        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * frameSizeBytes;

            // RMS for this frame
            double sumSq = 0;
            for (int i = 0; i < frameSize; i++)
            {
                short s = (short)(result[offset + i * 2] | (result[offset + i * 2 + 1] << 8));
                double norm = s / 32768.0;
                sumSq += norm * norm;
            }
            double rms = Math.Sqrt(sumSq / frameSize);

            if (rms >= threshold)
            {
                holdLeft = holdFrames; // signal present: reset hold
            }
            else if (holdLeft > 0)
            {
                holdLeft--; // in hold tail: pass through, count down
            }
            else
            {
                // Gate closed: silence this frame
                Array.Clear(result, offset, Math.Min(frameSizeBytes, result.Length - offset));
            }
        }

        return result;
    }
}
