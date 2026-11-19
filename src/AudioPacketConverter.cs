using System;

namespace Ukti
{
    // Decodes interleaved WASAPI frames, downmixes all channels, and resamples
    // continuously across packet boundaries. Pure managed code for regression tests.
    internal sealed class AudioPacketConverter
    {
        readonly int _channels, _bits, _blockAlign, _bytesPerSample;
        readonly bool _floatingPoint;
        readonly double _step;
        long _frame;
        double _nextOutput;
        float _previous;

        internal AudioPacketConverter(int rate, int channels, int bits, int blockAlign, bool floatingPoint)
        {
            if (rate <= 0 || channels <= 0 || bits % 8 != 0 || blockAlign < channels * (bits / 8) ||
                (floatingPoint ? bits != 32 && bits != 64 : bits != 8 && bits != 16 && bits != 24 && bits != 32))
                throw new InvalidOperationException("The microphone uses an unsupported audio format.");
            _channels = channels; _bits = bits; _blockAlign = blockAlign;
            _bytesPerSample = bits / 8; _floatingPoint = floatingPoint;
            _step = (double)rate / AudioCapture.SampleRate;
        }

        // null input denotes AUDCLNT_BUFFERFLAGS_SILENT: preserve its duration.
        internal int Convert(byte[] input, int frames, float[] output)
        {
            if (frames < 0 || (input != null && (long)frames * _blockAlign > input.Length))
                throw new ArgumentException("Incomplete microphone packet.");
            int written = 0;
            for (int i = 0; i < frames; i++, _frame++)
            {
                double sum = 0;
                if (input != null)
                    for (int channel = 0; channel < _channels; channel++)
                        sum += ReadSample(input, i * _blockAlign + channel * _bytesPerSample);
                float current = (float)(sum / _channels);
                while (_nextOutput <= _frame)
                {
                    if (written == output.Length) throw new ArgumentException("Microphone output buffer is too small.");
                    float t = (float)(_nextOutput - (_frame - 1));
                    output[written++] = _frame == 0 ? current : _previous + (current - _previous) * t;
                    _nextOutput += _step;
                }
                _previous = current;
            }
            return written;
        }

        float ReadSample(byte[] data, int offset)
        {
            float value;
            if (_floatingPoint)
                value = _bits == 32 ? BitConverter.ToSingle(data, offset) : (float)BitConverter.ToDouble(data, offset);
            else if (_bits == 8) value = (data[offset] - 128) / 128f;
            else if (_bits == 16) value = BitConverter.ToInt16(data, offset) / 32768f;
            else if (_bits == 24)
            {
                int sample = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
                if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                value = sample / 8388608f;
            }
            else value = BitConverter.ToInt32(data, offset) / 2147483648f;
            return float.IsNaN(value) || float.IsInfinity(value) ? 0 : Math.Max(-1f, Math.Min(1f, value));
        }
    }
}
