using System;

namespace Ukti
{
    // Audio conditioning applied before recognition.
    //
    // Each step here targets something that measurably hurts accuracy in
    // ordinary desktop capture rather than being cosmetic:
    //  * rumble: desk thumps, fans and air conditioning put most of their energy
    //    below 100 Hz, where speech has none, and that offset wastes the
    //    encoder's dynamic range;
    //  * level: a quiet capture from a distant built-in mic, or a hot headset
    //    that clips, both decode worse than a well-scaled signal;
    //  * silence: long silent heads can make a recogniser emit words that were
    //    never spoken.
    static class AudioPipeline
    {
        // Speech fundamentals start around 85 Hz, so a gentle cut below that
        // removes rumble while leaving the voice intact.
        const float HighPassHz = 85f;

        public static float[] Prepare(float[] samples, int rate)
        {
            if (samples == null || samples.Length == 0) return samples;

            float[] x = (float[])samples.Clone();
            HighPass(x, rate, HighPassHz);
            NormalisePeak(x, 0.9f);
            return TrimSilence(x, rate, -45f, 0.15f);
        }

        // Two cascaded one-pole high-pass sections: gentle enough to leave the
        // voice untouched, steep enough to drop the rumble band.
        static void HighPass(float[] x, int rate, float hz)
        {
            double dt = 1.0 / rate;
            double rc = 1.0 / (2 * Math.PI * hz);
            double alpha = rc / (rc + dt);

            for (int pass = 0; pass < 2; pass++)
            {
                double prevIn = 0, prevOut = 0;
                for (int i = 0; i < x.Length; i++)
                {
                    double input = x[i];
                    double output = alpha * (prevOut + input - prevIn);
                    prevIn = input;
                    prevOut = output;
                    x[i] = (float)output;
                }
            }
        }

        // Scales the clip so its loudest peak sits at `target`. Clipped audio
        // cannot be recovered, but rescuing a quiet or over-hot clip measurably
        // improves decoding.
        static void NormalisePeak(float[] x, float target)
        {
            float peak = 0;
            for (int i = 0; i < x.Length; i++)
            {
                float a = Math.Abs(x[i]);
                if (a > peak) peak = a;
            }
            if (peak < 1e-5f) return;

            float gain = target / peak;
            if (gain > 8f) gain = 8f;          // never amplify near-silence into noise
            if (gain < 0.2f) gain = 0.2f;
            if (Math.Abs(gain - 1f) < 0.02f) return;

            for (int i = 0; i < x.Length; i++)
            {
                float v = x[i] * gain;
                x[i] = v > 1f ? 1f : (v < -1f ? -1f : v);
            }
        }

        // Drops leading and trailing silence, keeping a short pad so the first
        // and last phonemes are not shaved off.
        static float[] TrimSilence(float[] x, int rate, float thresholdDb, float padSeconds)
        {
            float threshold = (float)Math.Pow(10, thresholdDb / 20.0);
            int pad = (int)(padSeconds * rate);
            int window = rate / 100;
            int first = -1, last = -1;

            for (int i = 0; i + window <= x.Length; i += window)
            {
                double sum = 0;
                for (int j = 0; j < window; j++) sum += x[i + j] * x[i + j];
                if (Math.Sqrt(sum / window) > threshold)
                {
                    if (first < 0) first = i;
                    last = i + window;
                }
            }

            if (first < 0) return x;
            first = Math.Max(0, first - pad);
            last = Math.Min(x.Length, last + pad);
            if (last - first < rate / 10) return x;

            float[] cut = new float[last - first];
            Array.Copy(x, first, cut, 0, cut.Length);
            return cut;
        }
    }
}
