using System;
using System.Collections.Generic;

namespace Ukti
{
    // P3 prototype: the already-bound NemoStream API, behind an explicit flag.
    // NOT enabled by default - the architecture review requires a WER comparison
    // before adoption, since streaming commonly costs accuracy vs batch decode.
    static class StreamingPrototype
    {
        public const string Flag = "UKTI_STREAMING_PROTOTYPE";

        public static bool Enabled
        {
            get
            {
                string value = Environment.GetEnvironmentVariable(Flag);
                return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Streams audio in fixed chunks and assembles the final transcript.
        // Returns null if streaming is not enabled or the engine cannot start a
        // stream; callers must fall back to batch transcription.
        public static string Transcribe(IAsrEngine engine, float[] samples, int sampleRate, int chunkSeconds)
        {
            NemoAsr nemo = engine as NemoAsr;
            if (nemo == null || !Enabled || samples == null || samples.Length == 0) return null;
            try
            {
                int chunkSamples = Math.Max(1, chunkSeconds * sampleRate);
                using (NemoStream stream = nemo.StartStream())
                {
                    for (int offset = 0; offset < samples.Length; offset += chunkSamples)
                    {
                        int count = Math.Min(chunkSamples, samples.Length - offset);
                        float[] chunk = new float[count];
                        Array.Copy(samples, offset, chunk, 0, count);
                        stream.Push(chunk, sampleRate);
                    }
                    stream.Finish();
                    System.Text.StringBuilder text = new System.Text.StringBuilder();
                    bool final;
                    string part;
                    while ((part = stream.Next(out final)) != null)
                    {
                        if (!string.IsNullOrEmpty(part)) text.Append(part).Append(' ');
                        if (final) break;
                    }
                    return text.ToString().Trim();
                }
            }
            catch (Exception ex) { AppLogger.Warning("streaming prototype failed: " + ex.Message); return null; }
        }

        // Word Error Rate: (substitutions + deletions + insertions) / reference
        // words, computed with Levenshtein distance over tokens.
        public static double WordErrorRate(string reference, string hypothesis)
        {
            string[] a = Words(reference), b = Words(hypothesis);
            if (a.Length == 0) return b.Length == 0 ? 0 : 1;
            int[,] dp = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return (double)dp[a.Length, b.Length] / a.Length;
        }

        static string[] Words(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new string[0];
            return text.ToLowerInvariant().Split(new char[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
