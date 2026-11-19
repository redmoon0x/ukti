using System;

namespace Ukti
{
    // Backend selection seam. NeMo is the only implementation today; a CPU /
    // ONNX Runtime backend can be added without changing the controller.
    static class AsrBackend
    {
        public enum Kind { NeMoGpu }

        public static IAsrEngine Create(SpeechModel model, string path)
        {
            return Create(model, path, System.Threading.CancellationToken.None);
        }

        public static IAsrEngine Create(SpeechModel model, string path, System.Threading.CancellationToken cancel)
        {
            return new AsrProcess(model, path, cancel);
        }

        // Only the isolated worker may load the native runtime.
        internal static IAsrEngine CreateInProcess(SpeechModel model, string path)
        {
            return Create(Kind.NeMoGpu, model, path);
        }

        public static IAsrEngine Create(Kind kind, SpeechModel model, string path)
        {
            switch (kind)
            {
                case Kind.NeMoGpu:
                    return NemoAsr.Create(path, 0, model.ChunkSeconds, model.RightContext, model.LanguageCode);
                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
        }
    }
}
