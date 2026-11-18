using System;
using System.Collections.Generic;

namespace Ukti
{
    // A port of Hex's "Dot scope" recording indicator (anomalyco/hex,
    // src/rune_hud/dot_scope.rs): a field of dots lit by a braided pair of
    // oscilloscope traces that follow the voice, with phosphor persistence.
    //
    // Two details do most of the work and are kept intact:
    //  * the auto-level, which maps the speaker's recent peak to full and the
    //    room to silence, so a quiet headset mic moves the field as far as a
    //    loud one;
    //  * the phosphor tail, so light lingers behind the voice instead of
    //    snapping to each audio buffer.
    public sealed class DotScope
    {
        public const float PlateHeight = 26f;

        const float Step = 1f / 240f;
        // 256 history slots is ~1.06 s at 240 Hz. Hex uses 512 for its longer
        // phosphor trail; the visual difference on a 34 px capsule is
        // imperceptible, and halving it saves ~10 000 floats per trace.
        const int History = 256;
        const int SamplesPerColumn = 3;

        static readonly float[] Speeds = { 0.8f, 1.25f, 1.85f, 2.6f };
        static readonly float[] BaseWeights = { 0.55f, 0.42f, 0.3f, 0.2f };
        static readonly float[] Spatial = { 1.4f, 2.4f, 3.8f, 5.6f };

        const float StartWidth = 30f;
        const float Enter = 0.42f;
        const float Wipe = 0.3f;
        const float PassInset = 9f;
        const float PhosphorHead = 0.15f;
        const float BreathPeriod = 5.5f;
        const float HiddenScale = 0.9f;
        const float WorkHelix = 2.6f;
        const float FlowSpacing = 0.7f;
        const float CarrierRest = 0.14f;
        const float RingBirth = 0.15f;
        const float BeamFade = 0.14f;

        public enum Stage { Recording, Processing, Done, Failed }
        public enum Look { Sweep, Flow, Pulse }

        // ---- tuning (Hex's defaults) ----
        public float Columns = 43f;
        public float Rows = 9f;
        public float Spacing = 2.6f;
        public float Width = 112f;
        public float ProcessingWidth = 76f;
        public float BraidSpeed = 1f;
        public float HelixCycles = 1.6f;
        public float Gain = 1f;
        public float Response = 6.5f;
        public float Release = 0.15f;
        public float HistorySpeed = 150f;
        public float Phosphor = 0.6f;
        public float ShimmerPass = 0.55f;
        public float Shimmer = 1f;
        public float Transition = 0.3f;
        public float Bounce = 0.15f;
        public bool AutoLevelOn = true;
        public float AutoRelease = 3f;
        public float NoiseFloor = -60f;
        public Look Processing = Look.Flow;

        // ---- simulation state ----
        sealed class Voice
        {
            public float Level, Reservoir, Energy, Kick, KickVelocity, Band;

            public void Step(float raw, float h)
            {
                float previous = Level;
                float time = raw > Level ? 0.035f : 0.095f;
                Level += (raw - Level) * (1f - (float)Math.Exp(-h / time));
                float rise = Math.Max((Level - previous) / h, 0f);
                Reservoir += (1.75f * Level * Level - Reservoir / 1.8f) * h;
                Energy = 1f - (float)Math.Exp(-Reservoir * 1.45f);
                float w = (float)(Math.PI * 2) * 1.3f, z = 0.22f;
                KickVelocity += (-w * w * Kick - 2f * z * w * KickVelocity + 15f * rise + 2.2f * Level) * h;
                Kick += KickVelocity * h;
                float bt = raw > Band ? 0.025f : 0.14f;
                Band += (raw - Band) * (1f - (float)Math.Exp(-h / bt));
            }
        }

        sealed class Trace
        {
            public float Envelope, Amplitude, AmplitudeVelocity;
            public float GlowFast, GlowSpring, GlowSpringVelocity, GlowSlow, Glow;
            public float[] Phases;
            public float[] Ring = new float[DotScope.History * 5];
            public int Head;

            public Trace(float[] phases) { Phases = phases; }

            public void Tick(Voice voice, float live, float speed, DotScope t, float h)
            {
                Follow(ref Envelope, voice.Level, 0.022f, t.Release, h);
                float w = (float)(Math.PI * 2) * t.Response, z = 0.52f;
                AmplitudeVelocity += (-w * w * (Amplitude - Envelope) - 2f * z * w * AmplitudeVelocity) * h;
                Amplitude += AmplitudeVelocity * h;
                Follow(ref GlowFast, voice.Level, 0.045f, 0.13f, h);
                float gw = (float)(Math.PI * 2) * 3.6f, gz = 0.5f;
                GlowSpringVelocity += (-gw * gw * (GlowSpring - GlowFast) - 2f * gz * gw * GlowSpringVelocity) * h;
                GlowSpring += GlowSpringVelocity * h;
                Follow(ref GlowSlow, voice.Level, 0.15f, 0.7f, h);
                float swell = 0.8f * Math.Max(GlowSpring - 0.25f, 0f) + 0.04f * Math.Max(voice.Kick, -0.5f);
                Glow = Math.Min(Math.Max(swell, 0f) + 0.42f * GlowSlow + 0.2f * voice.Energy, 1.15f) * live;

                float go = 0.6f + 1.1f * Math.Min(Envelope * 1.6f, 1f);
                for (int i = 0; i < Phases.Length; i++)
                    Phases[i] = (float)((Phases[i] + h * Math.PI * 2 * Speeds[i] * go * speed) % (Math.PI * 2));

                float emphasis = 0.35f + 0.65f * Math.Min(voice.Band / (voice.Band + 0.06f), 2f);
                int offset = Head * 5;
                float amplitude = Math.Max(Amplitude, 0f) * live;
                Ring[offset] = amplitude;
                for (int c = 0; c < 4; c++)
                    Ring[offset + 1 + c] = amplitude * BaseWeights[c] * emphasis;
                Head = (Head + 1) % DotScope.History;
            }

            public float Past(float delay, int channel)
            {
                float d = Math.Min(delay / Step, DotScope.History - 2);
                int i = (int)Math.Floor(d);
                float k = d - i;
                int a = ((Head + 2 * DotScope.History - 1 - i) % DotScope.History + DotScope.History) % DotScope.History;
                int b = (a + DotScope.History - 1) % DotScope.History;
                return Ring[a * 5 + channel] * (1f - k) + Ring[b * 5 + channel] * k;
            }
        }

        static void Follow(ref float value, float target, float rise, float fall, float h)
        {
            float time = target > value ? rise : fall;
            value += (target - value) * (1f - (float)Math.Exp(-h / Math.Max(time, 1e-3f)));
        }

        static float SmoothStep(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        static float Bump(float d, float w)
        {
            float q = d / w;
            return (float)Math.Exp(-q * q);
        }

        static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        static float Hermite(float start, float speed, float end, float t)
        {
            t = Clamp01(t);
            float t2 = t * t, t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * start
                 + (t3 - 2f * t2 + t) * Wipe * speed
                 + (3f * t2 - 2f * t3) * end;
        }

        static float PassPosition(float p)
        {
            p = Clamp01(p);
            return 0.5f - 0.5f * (float)Math.Cos(Math.PI * p);
        }

        // ---- auto level (Hex's AutoLevel) ----
        const float AutoMinSpan = 16f, AutoRange = 24f, AutoRoomMargin = 5f;
        const float AutoAttack = 0.04f, RoomFall = 0.3f, RoomRise = 6f;

        public sealed class AutoLevel
        {
            float _peak = -30f, _room = -55f;

            public float Step(float loudness, float h, float release, float noiseFloor)
            {
                float db = Math.Max(loudness, -120f);
                Follow(ref _room, db, db < _room ? RoomFall : RoomRise, db < _room ? RoomFall : RoomRise, h);
                float lowest = Math.Max(_room, noiseFloor) + AutoMinSpan;
                if (db > _peak) Follow(ref _peak, db, AutoAttack, AutoAttack, h);
                else Follow(ref _peak, lowest, release, release, h);
                _peak = Math.Max(_peak, lowest);
                return Level(db, noiseFloor);
            }

            public float Level(float loudness, float noiseFloor)
            {
                float low = Math.Max(Math.Max(_room, noiseFloor) + AutoRoomMargin, _peak - AutoRange);
                return Clamp01((loudness - low) / Math.Max(_peak - low, 1f));
            }
        }

        public static float Loudness(float rms)
        {
            return 20f * (float)Math.Log10(Math.Max(rms, 0f) + 1e-6f);
        }

        // ---- scope ----
        readonly AutoLevel _auto = new AutoLevel();
        readonly Voice[] _voices = { new Voice(), new Voice() };
        Trace[] _traces;

        Stage _stage = Stage.Recording;
        float _elapsed, _speedAtDone, _doneFrom, _flowOffset, _flowTurn;
        float _level, _live, _carrier, _time, _pending;
        float _widthNow, _opacity, _scale, _softness, _cool, _fail;
        long _steps;
        int _columns, _rows;
        float[][] _head, _tail, _ys, _brightness;
        public float[] Dots;
        float _grid;

        public float LevelNow { get; private set; }
        public float GlowOut { get; private set; }

        public int ColumnCount { get { return _columns; } }
        public int RowCount { get { return _rows; } }
        public float PlateWidth { get { return Math.Max(_widthNow, PlateHeight); } }
        public float Opacity { get { return Clamp01(_opacity); } }
        public float DisplayScale { get { return Math.Max(_scale, 0.01f); } }
        public float Cool { get { return Clamp01(_cool); } }
        public float FailMix { get { return Clamp01(_fail); } }

        public DotScope()
        {
            _traces = new Trace[]
            {
                new Trace(new float[] { 0.1f, 1.7f, 3.3f, 4.6f }),
                new Trace(new float[] { 2.4f, 0.6f, 5.1f, 1.9f }),
            };
            _widthNow = StartWidth;
            _softness = 3f;
            _scale = HiddenScale;
            Resize();
        }

        public void Restart()
        {
            _traces = new Trace[]
            {
                new Trace(new float[] { 0.1f, 1.7f, 3.3f, 4.6f }),
                new Trace(new float[] { 2.4f, 0.6f, 5.1f, 1.9f }),
            };
            _stage = Stage.Recording;
            _elapsed = 0; _live = 0; _carrier = 0; _level = 0;
            _flowOffset = 0; _flowTurn = 0; _time = 0; _pending = 0;
            _steps = 0; _grid = 0; _softness = 0; _scale = 1f;
            _cool = 0; _fail = 0; _opacity = 1f;
            _widthNow = Width;
            ClearField();
        }

        void ClearField()
        {
            if (Dots != null) Array.Clear(Dots, 0, Dots.Length);
            for (int k = 0; k < 2; k++)
            {
                if (_head[k] != null) Array.Clear(_head[k], 0, _head[k].Length);
                if (_tail[k] != null) Array.Clear(_tail[k], 0, _tail[k].Length);
            }
        }

        void Resize()
        {
            int columns = (int)Columns;
            int rows = (int)Rows;
            if (columns == _columns && rows == _rows && Dots != null) return;
            _columns = columns;
            _rows = rows;
            int dots = columns * rows;
            int samples = (columns - 1) * SamplesPerColumn + 1;
            _head = new float[2][]; _tail = new float[2][];
            _ys = new float[2][]; _brightness = new float[2][];
            for (int k = 0; k < 2; k++)
            {
                _head[k] = new float[dots];
                _tail[k] = new float[dots];
                _ys[k] = new float[samples];
                _brightness[k] = new float[samples];
            }
            Dots = new float[dots * 2];
        }

        public void SetStage(Stage stage)
        {
            if (stage != _stage)
            {
                if (stage == Stage.Done) ArmDone();
                if (stage == Stage.Processing) _flowOffset = -_widthNow / 2f - 4f;
                _stage = stage;
                _elapsed = 0f;
            }
        }

        public void Advance(float dt, float rmsLevel, bool visible)
        {
            _pending += Math.Min(Math.Max(dt, 0f), 0.25f);
            if (_pending < Step) return;
            while (_pending >= Step)
            {
                _pending -= Step;
                StepOnce(rmsLevel, visible);
            }
        }

        void StepOnce(float rmsLevel, bool visible)
        {
            const float h = Step;
            bool recording = _stage == Stage.Recording && visible;

            float level = 0f;
            if (recording)
            {
                float db = Loudness(rmsLevel);
                _level = AutoLevelOn
                    ? _auto.Step(db, h, AutoRelease, NoiseFloor)
                    : Clamp01((db + 40f) / 30f);
                level = Clamp01(_level * Gain);
            }
            else
            {
                _level = 0f;
            }

            _voices[0].Step(level, h);
            _voices[1].Step(level * 0.85f, h);

            float liveTarget = recording ? 1f : 0f;
            _live += (liveTarget - _live) * (1f - (float)Math.Exp(-h / 0.55f));

            for (int k = 0; k < 2; k++) _traces[k].Tick(_voices[k], _live, BraidSpeed, this, h);

            float energy = Math.Max(_voices[0].Energy, _voices[1].Energy);
            bool working = _stage == Stage.Processing || _stage == Stage.Done;
            bool flowing = working && Processing == Look.Flow;
            _flowTurn += ((flowing ? 1f : 0f) - _flowTurn) * (1f - (float)Math.Exp(-h / 0.25f));
            if (working) _flowOffset += FlowSpeed() * h;

            float rest = CarrierRest + 0.22f * energy;
            float turn = rest + (FlowTurnHz() - rest) * _flowTurn;
            _carrier = (float)((_carrier + h * Math.PI * 2 * turn * BraidSpeed) % (Math.PI * 2));

            float targetWidth = _stage == Stage.Recording ? Width
                              : _stage == Stage.Failed ? _widthNow : ProcessingWidth;

            float coolTarget = (working && _stage != Stage.Failed) ? 1f : 0f;
            float failTarget = _stage == Stage.Failed ? 1f : 0f;
            float shown = visible ? 1f : 0f;

            _widthNow = Spring(_widthNow, targetWidth, Bounce, h);
            _opacity = Spring(_opacity, shown, 0f, h);
            _scale = Spring(_scale, visible ? 1f : HiddenScale, Bounce, h);
            _softness = Spring(_softness, visible ? 0f : 3f, 0f, h);
            _cool = Spring(_cool, coolTarget, 0f, h);
            _fail = Spring(_fail, failTarget, 0f, h);

            _elapsed += h;
            _time += h;
            _steps++;
            if (_steps % 2 == 0) Beam(2f * h);
        }

        // An approximation of Hex's spring.step_to: critically damped, with the
        // bounce value lengthening the settle.
        static float Spring(float value, float target, float bounce, float h)
        {
            float duration = 0.3f + 0.6f * Math.Max(bounce, 0f);
            return value + (target - value) * (1f - (float)Math.Exp(-h / Math.Max(duration * 0.42f, 1e-3f)));
        }

        float WorkPeriod() { return 2f * Math.Max(ShimmerPass, 0.05f); }
        float FlowSpacingPx() { return FlowSpacing * ProcessingWidth; }
        float FlowSpeed() { return FlowSpacingPx() / WorkPeriod(); }
        float FlowTurnHz() { return FlowSpeed() * HelixCycles / Math.Max(_widthNow, 1f); }
        float PulseSpeed() { return (ProcessingWidth / 2f + 12f) / (1.4f * WorkPeriod()); }

        // ---- working light ----
        float WorkLight(float x, float halfWidth)
        {
            if (Processing == Look.Pulse)
            {
                float d = Math.Abs(x);
                float light = 0f;
                float period = WorkPeriod();
                float speed = PulseSpeed();
                float age = _elapsed - (float)Math.Floor(_elapsed / period) * period;
                float t = _elapsed;
                while (age * speed < halfWidth + 12f && age <= t + 1e-4f)
                {
                    float radius = age * speed;
                    float fade = SmoothStep(0f, RingBirth, age)
                               * (1f - 0.6f * SmoothStep(0.3f * halfWidth, halfWidth, radius));
                    light += Bump(d - radius, 6f) * fade;
                    age += period;
                }
                return Math.Min(light, 1f);
            }

            float spacing = FlowSpacingPx();
            float offset = _flowOffset;
            float q = (x - offset + spacing / 2f) % spacing;
            if (q < 0) q += spacing;
            return Bump(q - spacing / 2f, 7f);
        }

        void ArmDone()
        {
            _speedAtDone = 1.5f * PulseSpeed();
            _doneFrom = _stage == Stage.Processing ? _elapsed : 0f;
        }

        float ProcessHead(float elapsed)
        {
            float pass = Math.Max(ShimmerPass, 0.05f);
            return (2.2f + Math.Abs((float)Math.Sin(Math.PI * (elapsed / pass + 0.5f))))
                 * SmoothStep(0f, 0.14f, elapsed) * Shimmer;
        }

        float ProcessFront(float elapsed, float width)
        {
            float pass = Math.Max(ShimmerPass, 0.05f);
            float position = 0.5f - 0.5f * (float)Math.Cos(Math.PI * (elapsed / pass + 0.5f));
            return -width / 2f + PassInset + (width - 2f * PassInset) * position;
        }

        // scan.at(x) -> amplitude, brightness, swell
        void ScanAt(float x, float width, out float amplitude, out float bright, out float swell)
        {
            float e = _elapsed;
            float halfWidth = width / 2f;

            switch (_stage)
            {
                case Stage.Failed:
                    amplitude = 1f; bright = 1f; swell = 0f;
                    return;

                case Stage.Recording:
                {
                    float reach = (width / 2f + 12f) * PassPosition(e / Enter);
                    float head = 2.8f * (1f - SmoothStep(0.08f, Enter, e));
                    float unfold = SmoothStep(0f, 0.1f, e);
                    float d = Math.Abs(x);
                    float drawn = unfold * (1f - SmoothStep(Math.Max(reach - 14f, 0f), Math.Max(reach, 0.001f), d));
                    amplitude = drawn * (1f - SmoothStep(Math.Max(reach - 22f, 0f), Math.Max(reach - 3f, 0.001f), d));
                    float beam = head * Bump(d - reach, 6f);
                    swell = 1.8f * unfold * (head / 2.8f) * Bump(d - Math.Max(reach - 14f, 0f), 14f);
                    bright = amplitude + beam;
                    return;
                }

                case Stage.Processing:
                {
                    if (Processing == Look.Sweep)
                    {
                        float iron = 1f - SmoothStep(0f, 0.28f, e);
                        float head = ProcessHead(e);
                        amplitude = iron;
                        bright = iron + head * Bump(x - ProcessFront(e, width), 5f);
                        swell = 0f;
                        return;
                    }
                    float enter = SmoothStep(0f, 0.35f, e);
                    float light = WorkLight(x, halfWidth);
                    amplitude = 1f;
                    bright = 1f + 2.5f * SmoothStep(0.05f, 0.3f, e) * Shimmer * light;
                    swell = 0.9f * enter * light;
                    return;
                }

                case Stage.Done:
                {
                    float target = width / 2f + 8f;
                    float front = Hermite(0f, _speedAtDone, target, e / Wipe);
                    bool sweep = Processing == Look.Sweep;
                    float d = Math.Abs(x);
                    float field = sweep ? 0f : 1f;
                    float keep = field * SmoothStep(front - 12f, front - 2f, d);
                    float light = WorkLight(x, halfWidth);
                    float glow = sweep ? 0f : 2.5f * Shimmer * (1f - SmoothStep(0f, 0.18f, e));
                    float head = 2.8f * SmoothStep(0f, 0.04f, e) * (1f - SmoothStep(0.22f, 0.32f, e));
                    float beam = sweep && _doneFrom > 0f
                        ? ProcessHead(_doneFrom + e) * (1f - SmoothStep(0f, BeamFade, e))
                        : 0f;
                    amplitude = keep;
                    bright = keep * (1f + glow * light)
                           + head * Bump(d - front, 6f)
                           + beam * Bump(x - ProcessFront(_doneFrom + e, width), 5f);
                    swell = keep * 0.9f * light;
                    return;
                }

                default:
                    amplitude = 0f; bright = 0f; swell = 0f;
                    return;
            }
        }

        float ScanHelix()
        {
            if (_stage == Stage.Recording) return 1f;
            if (_stage == Stage.Processing)
                return Processing == Look.Sweep ? 1f - SmoothStep(0f, 0.28f, _elapsed) : WorkHelix;
            if (_stage == Stage.Done) return Processing == Look.Sweep ? 0f : WorkHelix;
            return 1f;
        }

        float ScanDecay()
        {
            return _stage == Stage.Done ? 1f - 0.6f * SmoothStep(0f, 0.12f, _elapsed) : 1f;
        }

        float ScanTail()
        {
            return _stage == Stage.Done ? 1f - SmoothStep(0f, 0.15f, _elapsed) : 1f;
        }

        float ScanGrid()
        {
            if (_stage == Stage.Recording) return Math.Min(_elapsed / 0.3f, 1f);
            if (_stage == Stage.Done) return 1f - SmoothStep(0.2f, 0.4f, _elapsed);
            return 1f;
        }

        void Beam(float hb)
        {
            int columns = _columns, rows = _rows, sub = SamplesPerColumn;
            int samples = (columns - 1) * sub + 1;
            float pitch = Spacing;
            float width = Math.Max(_widthNow, PlateHeight);

            _grid = ScanGrid();
            float helix = ScanHelix();
            float decay = ScanDecay();
            float tailFade = ScanTail();

            float mid = (rows - 1f) / 2f;
            float half = mid - 0.55f;
            float breath = 0.5f + 0.5f * (float)Math.Sin(_time * Math.PI * 2 / BreathPeriod);
            float rest = (0.55f + 0.35f * breath) * (0.4f + 0.6f * _live) * helix;
            float halfWidth = width / 2f;

            for (int k = 0; k < 2; k++)
            {
                Trace trace = _traces[k];
                float sign = k == 0 ? 1f : -1f;
                float[] ys = _ys[k];
                float[] bright = _brightness[k];

                for (int s = 0; s < samples; s++)
                {
                    float x = (s / (float)sub - (columns - 1f) / 2f) * pitch;
                    if (Math.Abs(x) > halfWidth)
                    {
                        ys[s] = mid;
                        bright[s] = 0f;
                        continue;
                    }

                    float amplitude, b, swell;
                    ScanAt(x, width, out amplitude, out b, out swell);

                    float own = Math.Abs(x);
                    float xn = (x + halfWidth) / width;
                    float ends = Math.Min(SmoothStep(-halfWidth + 3f, -halfWidth + 17f, x),
                                          SmoothStep(-halfWidth + 3f, -halfWidth + 17f, -x));
                    float reach = 1f - 0.8f * SmoothStep(14f, halfWidth, own);
                    float delay = own / Math.Max(HistorySpeed, 1f);

                    float a = trace.Past(delay, 0);
                    float spread = 0f;
                    for (int c = 0; c < 4; c++)
                        spread += trace.Past(delay, 1 + c)
                                * (float)Math.Sin(Math.PI * 2 * Spatial[c] * xn - trace.Phases[c]);

                    float hx = (rest + swell + half * 0.55f * a * reach)
                             * (float)Math.Sin(Math.PI * 2 * HelixCycles * xn - _carrier);
                    float y = half * (float)Math.Tanh((hx + half * 0.7f * spread * reach) / half)
                            * ends * amplitude;

                    ys[s] = mid - sign * y;
                    float spatialTerm = (0.3f + 0.7f * Math.Min(a * 1.8f, 1f)) * (0.45f + 0.55f * ends);
                    bright[s] = spatialTerm * amplitude + 0.36f * Math.Max(b - amplitude, 0f);
                }

                for (int s = 0; s < samples; s++)
                {
                    int lo = s > 0 ? s - 1 : 0;
                    int hi = Math.Min(s + 1, samples - 1);
                    float slope = (ys[hi] - ys[lo]) * sub / 2f;
                    bright[s] *= 1f - 0.55f + 0.55f / (float)Math.Sqrt(1f + 0.22f * slope * slope);
                }
            }

            float headKeep = (float)Math.Exp(-hb / (PhosphorHead * decay));
            float tailKeep = (float)Math.Exp(-hb / (Math.Max(Phosphor, 0.01f) * decay));
            float dx = 1f / sub;

            for (int i = 0; i < columns; i++)
            {
                int s0 = Math.Max(i - 3, 0) * sub;
                int s1 = Math.Min((i + 3) * sub, samples - 1);
                for (int j = 0; j < rows; j++)
                {
                    int n = j * columns + i;
                    float now = 0f, total = 0f;
                    float lit0 = 0f, lit1 = 0f;

                    for (int k = 0; k < 2; k++)
                    {
                        float[] ys = _ys[k];
                        float best = float.MaxValue;
                        int nearest = i * sub;
                        for (int s = s0; s < s1; s++)
                        {
                            float ay = ys[s];
                            float dy = ys[s + 1] - ay;
                            float px = i - s * dx;
                            float py = j - ay;
                            float t = (px * dx + py * dy) / (dx * dx + dy * dy);
                            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                            float ex = px - t * dx;
                            float ey = py - t * dy;
                            float d2 = ex * ex + ey * ey;
                            if (d2 < best) { best = d2; nearest = s; }
                        }

                        float brightness = _brightness[k][nearest];
                        float onBeam = (float)Math.Exp(-best / 0.25f);
                        float instant = Math.Min(onBeam + 0.5f * (float)Math.Exp(-Math.Sqrt(best) / 0.8f), 1.2f) * brightness;
                        float core = onBeam * brightness;

                        _head[k][n] = Math.Max(_head[k][n] * headKeep, core);
                        _tail[k][n] = Math.Max(_tail[k][n] * tailKeep, core);

                        float light = Math.Max(instant, Math.Max(_head[k][n], 0.55f * tailFade * _tail[k][n]));
                        if (k == 0) lit0 = light; else lit1 = light;
                        now += instant;
                        total += light;
                    }

                    float power = 1f - (1f - Math.Min(lit0, 1f)) * (1f - Math.Min(lit1, 1f));
                    Dots[n * 2] = Clamp01(power);
                    Dots[n * 2 + 1] = Math.Min(now / Math.Max(total, 1e-4f), 1f);
                }
            }

            float glowAvg = (_traces[0].Glow + _traces[1].Glow) / 2f;
            GlowOut = glowAvg;
            LevelNow = _voices[0].Level;
        }

        public float GridMix { get { return _grid; } }
        public float Softness { get { return _softness; } }
    }
}
