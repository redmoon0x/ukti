namespace Ukti
{
    // Verifies the WER harness used to compare streaming vs batch decode.
    static class WerTests
    {
        internal static void Run(System.Action<bool, string> assert)
        {
            assert(StreamingPrototype.WordErrorRate("hello world", "hello world") == 0,
                "Identical transcript has zero WER.");
            assert(StreamingPrototype.WordErrorRate("hello world", "hello there") == 0.5,
                "One substitution in two words is 0.5 WER.");
            assert(StreamingPrototype.WordErrorRate("hello world", "hello") == 0.5,
                "One deletion in two words is 0.5 WER.");
            assert(StreamingPrototype.WordErrorRate("hello", "hello brave world") == 2.0,
                "Two insertions against one word is 2.0 WER.");
            assert(StreamingPrototype.WordErrorRate("", "") == 0, "Empty reference and hypothesis are equal.");
            assert(StreamingPrototype.WordErrorRate("", "something") == 1.0, "Insertion into empty reference is 1.0.");
        }
    }
}
