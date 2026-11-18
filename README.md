<p align="center">
  <img src="docs/logo.png" width="150" alt="Ukti logo — the Kannada word ಉಕ್ತಿ">
</p>

<h1 align="center">Ukti</h1>

<p align="center">
  <em>ಉಕ್ತಿ — "utterance" in Kannada</em>
</p>

<p align="center">
  Hold <kbd>Ctrl</kbd>+<kbd>Space</kbd>, speak, release.<br>
  Your words appear at the cursor — in under 100 ms, entirely on your own PC.
</p>

<p align="center">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D4?style=flat-square">
  <img alt="GPU" src="https://img.shields.io/badge/GPU-CUDA%20required-76B900?style=flat-square">
  <img alt="Offline" src="https://img.shields.io/badge/network-none%20after%20model%20download-2ea44f?style=flat-square">
  <img alt="Latency" src="https://img.shields.io/badge/latency-~70%20ms%20per%209%20s%20of%20speech-blue?style=flat-square">
</p>

---

<p align="center">
  <img src="docs/pill.png" width="620" alt="The Ukti listening capsule">
</p>

<p align="center">
  <sub>The listening indicator: a braided dot field that follows your voice, warm while recording and cooling to blue while transcribing.</sub>
</p>

## Why another dictation tool

Most voice-typing tools either send your audio to a server or bury the one
feature you wanted under a subscription, a model picker, and a settings maze.

Ukti does one thing: **press two keys, talk, get text.** Audio never leaves the
machine, there is no account, and there is no history. When you release the keys
the transcript is pasted and the audio is gone.

## How it feels to use

1. Click into any text field — editor, browser, chat, terminal.
2. **Hold** <kbd>Ctrl</kbd>+<kbd>Space</kbd>. A capsule appears at the top of the screen.
3. Speak normally.
4. **Release**. The text is inserted where your cursor was.

Press <kbd>Esc</kbd> mid-sentence to cancel without inserting anything.

## Performance

Measured on an RTX 3050 Laptop GPU (6 GB), 16 kHz mono capture:

| Audio length | Transcription | Realtime factor |
| ----------- | ------------- | --------------- |
| 1 s         | 22 ms         | 0.022×          |
| 3 s         | 35 ms         | 0.012×          |
| 9.2 s       | 81 ms         | 0.009×          |

- **Model load:** ~700 ms including GPU warm-up, done once at startup.
- **Microphone:** kept open while idle, so starting a dictation costs no device-open latency.
- **Idle memory:** ~4 MB working set after the idle trim; weights live in VRAM.

## Accuracy

English-only streaming Nemotron 3.5 ASR (0.6B, Q8_0), decoded locally.

On a varied 8-sentence benchmark across two voices — clean and at 8 dB SNR —
word error rate was **0.0%**, with punctuation exact and only two casing quirks.

The single biggest accuracy factor is **which microphone you use**. A Bluetooth
headset typically runs its hands-free profile, which captures narrowband audio;
no decoder setting can recover speech detail that was never recorded. The
settings window warns when a headset is selected, and the built-in microphone
almost always transcribes better.

## Privacy

- Inference, punctuation, and text insertion all happen on-device.
- The only network activity is the one-time model download.
- No audio or transcript is written to disk. Ever.
- No telemetry, no accounts, no cloud fallback.

## Install

### Requirements

- Windows 10 or 11, x64
- An NVIDIA GPU with CUDA support (the speech runtime is GPU-only by design)
- ~2 GB of disk for the runtime, plus ~700 MB for the model

### From source

```powershell
git clone https://github.com/<your-username>/ukti.git
cd ukti

# Compile — uses the C# compiler included with Windows, no SDK needed
.\build.cmd

# Fetch the speech runtime (~700 MB, from NVIDIA's official release)
.\scripts\fetch-runtime.ps1

# Start it
.\app\Ukti.exe
```

On first launch Ukti asks for a microphone and downloads the model into
`app\models\`. After that, it starts instantly and runs from the tray.

## What it does and does not do

**Does**

- Hold-to-talk dictation with one global shortcut
- English speech with punctuation and capitalisation
- Pastes into whatever app has focus, with a clipboard fallback if focus moved
- Microphone selection, start-with-Windows, and run-in-background options
- Cancels cleanly with <kbd>Esc</kbd>

**Does not**

- Translate, summarise, or rewrite what you said
- Store history, recordings, or transcripts
- Require an account, a subscription, or a connection
- Support languages other than English (the model is English-only)

## How it is built

A single native C# process. No Python, no Electron, no local server.

```
Ukti.exe
├── Global hold-to-talk hook (WH_KEYBOARD_LL)
├── WASAPI-style capture via winmm (16 kHz mono PCM16)
├── Audio conditioning (high-pass, level, silence trim)
├── P/Invoke → nemo_speech_asr_c.dll   ← NeMo-Speech.cpp, CUDA
├── Clipboard paste with focus validation
└── Tray icon + listening capsule
```

Three decisions did most of the work:

**CUDA over Vulkan.** The Vulkan build ran the wrong decode path for this model
and was ~12× slower. Switching backends took a 9-second clip from 1047 ms to
81 ms.

**Hold-to-talk needs a keyboard hook.** `RegisterHotKey` only reports the press,
so an app built on it can never learn that you let go. A low-level hook mirrors
real key state, swallows only the space, and makes release the natural end of a
dictation.

**The microphone stays open.** Opening a capture device costs roughly half a
second — unacceptable *after* the user has started talking. The device streams
continuously and capture is gated in software, so nothing is clipped at the
start.

### Layout

```
src/
  Program.cs        tray, state machine, lifecycle
  HoldHotkey.cs     low-level keyboard hook
  AudioCapture.cs   winmm capture, device selection, level metering
  AudioPipeline.cs  conditioning before recognition
  NemoAsr.cs        P/Invoke bindings for the C ABI
  TextInjector.cs   clipboard paste, focus guard, restoration
  PillForm.cs       the listening capsule
  DotScope.cs       braided dot-field simulation
  Theme.cs          palette, brand mark, shared drawing
  SettingsForm.cs   preferences window
  ModelSetupForm.cs first-run model download
  Memory.cs         idle working-set trim
  Ipc.cs            single-instance signalling
  Startup.cs        per-user Run-key registration
```

## Credits

The listening indicator is a port of the **dot scope** from
[Hex](https://github.com/anomalyco/hex) — a braided pair of oscilloscope traces
with phosphor persistence and an auto-levelling meter. The visual language
follows their OpenCode dark palette.

Speech recognition runs on
[NeMo-Speech.cpp](https://github.com/NVIDIA/NeMo-Speech.cpp) by NVIDIA, using
the English streaming [Nemotron 3.5](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b)
model under the NVIDIA Open Model License.

The model weights are **not** included in this repository; they are downloaded
from Hugging Face on first run.

## License

Code is released under the MIT License — see [LICENSE](LICENSE).

The speech runtime and model carry their own licenses from NVIDIA. Review them
before redistributing a packaged build.
