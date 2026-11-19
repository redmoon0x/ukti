using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ukti
{
    static class AudioCaptureTests
    {
        static byte[] FloatStereo(int frames)
        {
            byte[] bytes = new byte[frames * 8];
            for (int i = 0; i < frames; i++)
            {
                // Speech on the RIGHT channel only must not disappear.
                float value = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
                Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, i * 8 + 4, 4);
            }
            return bytes;
        }

        internal static void Run(Action<bool, string> assert)
        {
            assert(Marshal.SizeOf(typeof(Wasapi.WAVEFORMATEX)) == 18, "WAVEFORMATEX must have native 2-byte packing.");
            assert(Marshal.SizeOf(typeof(Wasapi.PROPVARIANT)) == 24, "x64 PROPVARIANT must include the full native union.");
            foreach (Type type in new Type[] { typeof(Wasapi.IMMDeviceEnumerator), typeof(Wasapi.IMMDevice),
                typeof(Wasapi.IPropertyStore), typeof(Wasapi.IAudioClient), typeof(Wasapi.IAudioCaptureClient) })
                foreach (MethodInfo method in type.GetMethods())
                    assert((method.GetMethodImplementationFlags() & MethodImplAttributes.PreserveSig) != 0,
                        type.Name + "." + method.Name + " must preserve its native HRESULT.");

            byte[] stereo = FloatStereo(4800);
            float[] output = new float[2000];
            AudioPacketConverter converter = new AudioPacketConverter(48000, 2, 32, 8, true);
            int count = converter.Convert(stereo, 4800, output);
            assert(count == 1600, "100 ms stereo must produce 100 ms of 16 kHz mono, not half a packet.");
            double peak = 0;
            for (int i = 0; i < count; i++)
            {
                float expected = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 16000);
                if (Math.Abs(output[i] - expected) > 0.00001f) throw new Exception("Stereo downmix/resample changed speech timing.");
                peak = Math.Max(peak, Math.Abs(output[i]));
            }
            assert(peak > 0.49 && peak <= 0.501, "Right-channel-only speech must survive the downmix.");

            converter = new AudioPacketConverter(16000, 1, 16, 2, false);
            count = converter.Convert(new byte[] { 0, 0, 0, 64, 0, 128, 255, 127 }, 4, output);
            assert(count == 4 && output[0] == 0 && output[1] == 0.5f && output[2] == -1 && output[3] > 0.999,
                "PCM16 mono must decode each 2-byte sample without reading past the packet.");
            converter = new AudioPacketConverter(16000, 1, 24, 3, false);
            converter.Convert(new byte[] { 0, 0, 64, 0, 0, 128 }, 2, output);
            assert(output[0] == 0.5f && output[1] == -1, "Packed PCM24 must be sign-extended correctly.");
            converter = new AudioPacketConverter(16000, 1, 32, 4, false);
            converter.Convert(new byte[] { 0, 0, 0, 64, 0, 0, 0, 128 }, 2, output);
            assert(output[0] == 0.5f && output[1] == -1, "PCM32 must not be interpreted as IEEE float.");
            converter = new AudioPacketConverter(16000, 1, 8, 1, false);
            converter.Convert(new byte[] { 0, 128, 255 }, 3, output);
            assert(output[0] == -1 && output[1] == 0 && output[2] > 0.99, "PCM8 uses unsigned samples.");
            converter = new AudioPacketConverter(16000, 1, 32, 4, true);
            converter.Convert(BitConverter.GetBytes(float.NaN), 1, output);
            assert(output[0] == 0, "Invalid input samples must not poison ASR or the level meter.");
            converter = new AudioPacketConverter(48000, 2, 32, 8, true);
            count = converter.Convert(null, 4800, output);
            assert(count == 1600 && output[0] == 0 && output[count - 1] == 0, "Silent packets must retain their duration.");

            foreach (int rate in new int[] { 8000, 11025, 16000, 44100, 48000 })
            {
                byte[] source = new byte[rate * 4];
                for (int i = 0; i < rate; i++) Buffer.BlockCopy(BitConverter.GetBytes((float)Math.Sin(i * 0.01)), 0, source, i * 4, 4);
                float[] whole = new float[16010];
                int wholeCount = new AudioPacketConverter(rate, 1, 32, 4, true).Convert(source, rate, whole);
                converter = new AudioPacketConverter(rate, 1, 32, 4, true);
                List<float> packets = new List<float>();
                for (int frame = 0; frame < rate;)
                {
                    int frames = Math.Min(137, rate - frame);
                    byte[] packet = new byte[frames * 4];
                    Buffer.BlockCopy(source, frame * 4, packet, 0, packet.Length);
                    count = converter.Convert(packet, frames, output);
                    for (int i = 0; i < count; i++) packets.Add(output[i]);
                    frame += frames;
                }
                assert(Math.Abs(wholeCount - 16000) <= 2, "Resampling must retain duration at " + rate + " Hz.");
                assert(packets.Count == wholeCount, "Packet boundaries must not lose samples at " + rate + " Hz.");
                bool equal = true;
                for (int i = 0; i < wholeCount; i++) if (Math.Abs(packets[i] - whole[i]) > 0.000001) equal = false;
                assert(equal, "Resampling phase must remain continuous across packets at " + rate + " Hz.");
            }
        }

        // Optional hardware diagnostic: prints device/level statistics only.
        // Captured audio stays in memory and is never transcribed or saved.
        internal static int Probe(string[] args)
        {
            try
            {
                Console.WriteLine("Active microphone endpoints:");
                for (int i = 0; i < AudioCapture.DeviceCount(); i++) Console.WriteLine(i + ": " + AudioCapture.DeviceName(i));
                if (args.Length > 1 && args[1] == "--list") return 0;
                int selected = args.Length > 1 ? int.Parse(args[1]) : Settings.Load().MicrophoneIndex;
                using (AudioCapture mic = new AudioCapture())
                {
                    mic.DeviceIndex = selected;
                    mic.Open();
                    Console.WriteLine("Opened index=" + selected + " endpoint=" + mic.EndpointId);
                    mic.BeginCapture();
                    Thread.Sleep(1100);
                    float[] samples = mic.EndCapture();
                    double peak = 0, energy = 0;
                    foreach (float value in samples) { peak = Math.Max(peak, Math.Abs(value)); energy += value * value; }
                    Console.WriteLine("Samples=" + samples.Length + " duration=" + ((double)samples.Length / AudioCapture.SampleRate).ToString("F3") +
                        "s peak=" + peak.ToString("F6") + " rms=" + Math.Sqrt(energy / Math.Max(1, samples.Length)).ToString("F6") + " liveLevel=" + mic.Level.ToString("F6"));
                    if (samples.Length < 8000) throw new Exception("The selected microphone did not deliver continuous audio packets.");
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
