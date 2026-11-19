using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Ukti
{
    sealed class SpeechModel
    {
        public readonly string Id, Name, Description, Repo, Revision, FileName, Sha256, License;
        public readonly long Bytes;
        public readonly bool Multilingual;
        public readonly float ChunkSeconds;
        public readonly int RightContext;
        public SpeechModel(string id, string name, string description, string repo, string revision,
                           string fileName, long bytes, string sha256, string license, bool multilingual,
                           float chunkSeconds, int rightContext)
        {
            Id = id; Name = name; Description = description; Repo = repo; Revision = revision;
            FileName = fileName; Bytes = bytes; Sha256 = sha256; License = license;
            Multilingual = multilingual; ChunkSeconds = chunkSeconds; RightContext = rightContext;
        }
        public string Url { get { return "https://huggingface.co/" + Repo + "/resolve/" + Revision + "/" + FileName; } }
        public string LicenseUrl { get { return "https://huggingface.co/" + Repo; } }
        public string SizeText { get { return Bytes >= 1000000000 ? (Bytes / 1000000000.0).ToString("0.00") + " GB" : (Bytes / 1000000.0).ToString("0") + " MB"; } }
        public string LanguageCode { get { return Multilingual ? null : "en-US"; } }
    }

    static class ModelCatalog
    {
        // Official ASR artifacts pinned by NeMo-Speech.cpp v0.3.0/models/index.json,
        // the runtime Ukti ships. No arbitrary GGUFs or unsupported Hex backends.
        public static readonly SpeechModel[] All = {
            new SpeechModel("nemotron-en", "Nemotron English", "English / fast everyday dictation / recommended",
                "nvidia/nemotron-speech-streaming-en-0.6b", "ebe59e5a817142986528bbbee5dba8db7b38ed50",
                "nemotron-speech-streaming-en-0.6b.q8_0.gguf", 699872960L,
                "d9a01898d2a611c8764e23a1c2f45e70bbd5a425dc4de93692ac951dd603812d", "NVIDIA Open Model License", false, 0.16f, 1),
            new SpeechModel("nemotron-3.5", "Nemotron 3.5", "Multilingual / automatic language detection",
                "nvidia/nemotron-3.5-asr-streaming-0.6b", "1c8deaecc64b91f034d73e08dd8b64625eb3395d",
                "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf", 741548352L,
                "a5c435f294eea8f88ce68dd27b8c3bfea7f777cb2fbba04fcd30eaa555f429ae", "NVIDIA Open Model License (OpenMDW 1.1)", true, 0.16f, 1),
            new SpeechModel("parakeet-tdt", "Parakeet TDT v3", "25 European languages / automatic language detection",
                "nvidia/parakeet-tdt-0.6b-v3", "541d1f99c6b0c3cd0b11a95167540bb8edefd82b",
                "parakeet-tdt-0.6b-v3.q8_0.gguf", 713975456L,
                "e3880d0aaaaf2c308ea2c35016b2b895c423eb3fda924c1b463d1c19b7f4d32e", "CC-BY-4.0", true, 1.6f, -1),
            new SpeechModel("parakeet-ctc", "Parakeet CTC", "English / larger model / unpunctuated output",
                "nvidia/parakeet-ctc-1.1b", "20e63a0fed6aedba145b74b826dbd41df0941730",
                "parakeet-ctc-1.1b.q8_0.gguf", 1178100960L,
                "6584fc0fdacf1c220401ea4c3a1d5b44454b655c141cb8672178072c203d92b8", "CC-BY-4.0", false, 1.6f, -1)
        };
        public static string DirectoryPath { get { return AppData.ModelDirectory; } }
        public static SpeechModel Find(string id)
        {
            foreach (SpeechModel model in All) if (model.Id == id) return model;
            return All[0];
        }
        public static string PathFor(SpeechModel model, string directory)
        {
            string canonical = Path.Combine(directory, model.FileName);
            if (model.Id == "nemotron-en")
            {
                string legacy = Path.Combine(directory, "nemotron-en.q8_0.gguf");
                if (File.Exists(legacy) && new FileInfo(legacy).Length == model.Bytes) return legacy;
            }
            return canonical;
        }
        public static bool IsInstalled(SpeechModel model, string directory)
        {
            try { string path = PathFor(model, directory); return File.Exists(path) && new FileInfo(path).Length == model.Bytes; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        internal static string[] StoredPaths(SpeechModel model, string directory)
        {
            // Exact cache paths only: no wildcards, partial downloads, external
            // VT_MODEL paths, or arbitrary relative paths are deletion targets.
            if (string.IsNullOrEmpty(model.FileName) || Path.GetFileName(model.FileName) != model.FileName)
                throw new ArgumentException("Invalid model filename.");
            string root = Path.GetFullPath(directory);
            string canonical = Path.Combine(root, model.FileName);
            return model.Id == "nemotron-en" ? new string[] { canonical, Path.Combine(root, "nemotron-en.q8_0.gguf") } : new string[] { canonical };
        }
        internal static bool HasStoredFiles(SpeechModel model, string directory)
        {
            foreach (string path in StoredPaths(model, directory)) if (File.Exists(path)) return true;
            return false;
        }
        internal static long StoredBytes(SpeechModel model, string directory)
        {
            long bytes = 0;
            foreach (string path in StoredPaths(model, directory)) if (File.Exists(path)) bytes += new FileInfo(path).Length;
            return bytes;
        }
        internal static void DeleteFiles(SpeechModel model, string directory, string protectedModelId)
        {
            if (model.Id == protectedModelId)
                throw new InvalidOperationException("Save a different model in Settings before deleting the current model.");
            foreach (string path in StoredPaths(model, directory)) File.Delete(path);
        }
        internal static string VerifyFile(string path, SpeechModel model, Func<bool> cancelled)
        {
            if (cancelled()) throw new OperationCanceledException();
            if (!File.Exists(path) || new FileInfo(path).Length != model.Bytes)
                return "The download is incomplete. Please try again.";
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] buffer = new byte[1024 * 1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                string hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                return hash == model.Sha256 ? null : "The download failed its integrity check. Please try again.";
            }
        }
    }
}
