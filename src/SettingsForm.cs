using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ukti
{
    sealed class SettingsForm : ThemedForm
    {
        public int SelectedIndex { get; private set; }
        public bool EnabledNow { get { return _enabled.Checked; } }
        public bool StartWithWindows { get; private set; }
        public bool KeepRunning { get; private set; }
        public string ShortcutText { get; private set; }
        public string ModelId { get; private set; }
        public int IdleUnloadMinutes { get; private set; }
        // On/off is intentionally immediate; other preferences are staged until Save.
        public event Action<bool> EnableChanged;
        readonly ToggleSwitch _enabled, _startAuto, _keepRunning;
        readonly DeviceRow _device;
        readonly ShortcutRecorder _shortcut;
        readonly IdleTimeoutRow _idleTimeout;
        readonly Label _enableState, _enableNote, _shortcutHint, _deviceNote, _modelLabel;
        readonly string _modelDirectory;
        readonly SpeechModel _savedModel;
        SpeechModel _selectedModel;

        public SettingsForm(Settings current, string modelDirectory = null)
        {
            _modelDirectory = modelDirectory ?? ModelCatalog.DirectoryPath;
            _selectedModel = _savedModel = ModelCatalog.Find(current.ModelId);
            Text = "Ukti settings";
            ClientSize = new Size(680, 714);
            AddLabel("Ukti", new Rectangle(78, 24, 460, 30), 17f, Theme.Ink, true);
            AddLabel("Your words, right where you need them.", new Rectangle(78, 55, 520, 22), 9f, Theme.MutedInk, false);
            AddCloseButton(628).TabIndex = 9;

            AddLabel("Enable dictation", new Rectangle(44, 101, 420, 25), 11f, Theme.Ink, true);
            _enableNote = AddLabel("", new Rectangle(44, 130, 480, 22), 9f, Theme.MutedInk, false);
            _enableState = AddLabel("", new Rectangle(534, 111, 42, 30), 9f, Theme.MutedInk, true);
            _enabled = AddSwitch("Enable dictation", "Applies immediately. Turn off to release the microphone and shortcut.", 580, 110, current.Enabled, 0);
            _enabled.Click += delegate
            {
                UpdateEnabledLabel();
                if (EnableChanged != null) EnableChanged(_enabled.Checked);
            };
            UpdateEnabledLabel();

            AddLabel("Shortcut", new Rectangle(44, 191, 240, 27), 10f, Theme.Ink, false);
            _shortcut = new ShortcutRecorder();
            _shortcut.Bounds = new Rectangle(330, 192, 302, 38);
            _shortcut.Value = current.GetShortcut();
            _shortcut.TabIndex = 1;
            Controls.Add(_shortcut);
            _shortcutHint = AddLabel("Hold to speak. Release to insert. Click the shortcut to change it.",
                new Rectangle(44, 233, 588, 26), 8.5f, Theme.MutedInk, false);
            _shortcut.HintChanged += delegate(string hint) { _shortcutHint.Text = hint; };

            AddLabel("Speech model", new Rectangle(44, 269, 420, 25), 10f, Theme.Ink, false);
            _modelLabel = AddLabel(_selectedModel.Name, new Rectangle(44, 295, 430, 24), 9f, Theme.MutedInk, false);
            HexButton models = new HexButton();
            models.Text = "Manage models";
            models.Bounds = new Rectangle(498, 274, 134, 36);
            models.TabIndex = 2;
            models.Click += delegate { OpenModelLibrary(); };
            Controls.Add(models);
            AddLabel("Downloaded models work offline. Save to apply your selection.",
                new Rectangle(44, 319, 588, 22), 8.5f, Theme.FaintInk, false);

            AddLabel("Microphone", new Rectangle(44, 354, 220, 26), 10f, Theme.Ink, false);
            _device = new DeviceRow();
            _device.Bounds = new Rectangle(300, 350, 332, 36);
            _device.SelectedIndex = current.MicrophoneIndex;
            _device.TabIndex = 3;
            Controls.Add(_device);
            _deviceNote = AddLabel(_device.Note, new Rectangle(44, 390, 588, 28), 8.5f,
                _device.NoteIsWarning ? Theme.WarmMic : Theme.MutedInk, false);
            _device.SelectionChanged += delegate
            {
                _deviceNote.Text = _device.Note;
                _deviceNote.ForeColor = _device.NoteIsWarning ? Theme.WarmMic : Theme.MutedInk;
            };

            AddLabel("Unload model when idle", new Rectangle(44, 431, 360, 26), 10f, Theme.Ink, false);
            _idleTimeout = new IdleTimeoutRow();
            _idleTimeout.Bounds = new Rectangle(426, 429, 206, 34);
            _idleTimeout.Minutes = current.IdleUnloadMinutes;
            _idleTimeout.TabIndex = 4;
            Controls.Add(_idleTimeout);
            AddLabel("Frees model RAM / VRAM. Reloads on your next hold-to-talk.",
                new Rectangle(44, 466, 588, 23), 8.5f, Theme.MutedInk, false);

            AddLabel("Launch at Windows sign-in", new Rectangle(44, 523, 500, 24), 10f, Theme.Ink, false);
            _startAuto = AddSwitch("Launch at Windows sign-in", "Start Ukti when you sign in to Windows.", 580, 521, current.RunAtStartup, 5);
            AddLabel("Keep Ukti in the tray when I close it", new Rectangle(44, 573, 510, 24), 10f, Theme.Ink, false);
            _keepRunning = AddSwitch("Keep running in the background", "When off, the close button and Alt+F4 quit Ukti. Save and Cancel keep it running.",
                580, 571, current.RunInBackground, 6);

            AddLabel("On/off is immediate. Save applies other preferences.",
                new Rectangle(24, 617, 632, 26), 8.5f, Theme.FaintInk, false);
            HexButton cancel = new HexButton();
            cancel.Text = "Cancel";
            cancel.Bounds = new Rectangle(412, 654, 100, 36);
            cancel.TabIndex = 7;
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            HexButton save = new HexButton();
            save.Text = "Save changes";
            save.Primary = true;
            save.Bounds = new Rectangle(522, 654, 134, 36);
            save.TabIndex = 8;
            save.Click += delegate { Accept(); };
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;
            ActiveControl = _enabled;
        }
        ToggleSwitch AddSwitch(string name, string description, int x, int y, bool value, int tab)
        {
            ToggleSwitch control = new ToggleSwitch();
            control.AccessibleName = name;
            control.AccessibleDescription = description;
            control.Bounds = new Rectangle(x, y, 56, 32);
            control.Checked = value;
            control.TabIndex = tab;
            Controls.Add(control);
            return control;
        }
        void UpdateEnabledLabel()
        {
            _enableState.Text = _enabled.Checked ? "On" : "Off";
            _enableState.ForeColor = _enabled.Checked ? Theme.Ink : Theme.MutedInk;
            _enableNote.Text = _enabled.Checked ? "Dictation is on. Audio stays on this PC." :
                "Shortcut inactive. The microphone is released.";
        }
        public void OpenModelLibrary()
        {
            using (ModelSetupForm library = new ModelSetupForm(_selectedModel, _modelDirectory, _savedModel.Id))
            {
                if (library.ShowDialog(this) == DialogResult.OK) _selectedModel = library.SelectedModel;
                else if (!ModelCatalog.IsInstalled(_selectedModel, _modelDirectory)) _selectedModel = _savedModel;
                _modelLabel.Text = _selectedModel.Name;
            }
        }
        void Accept()
        {
            if (_shortcut.IsRecording) { _shortcut.Focus(); return; }
            SelectedIndex = _device.SelectedIndex;
            StartWithWindows = _startAuto.Checked;
            KeepRunning = _keepRunning.Checked;
            ShortcutText = _shortcut.Value.ToString();
            ModelId = _selectedModel.Id;
            IdleUnloadMinutes = _idleTimeout.Minutes;
            DialogResult = DialogResult.OK;
            Close();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.DrawMark(g, new RectangleF(24, 20, 44, 44), Theme.Ink);
            Theme.DrawSurface(g, new Rectangle(24, 88, 632, 76), Theme.BgBase, Theme.CardRadius);
            Theme.DrawSurface(g, new Rectangle(24, 180, 632, 314), Theme.BgBase, Theme.CardRadius);
            Theme.DrawSurface(g, new Rectangle(24, 510, 632, 100), Theme.BgBase, Theme.CardRadius);
            using (Pen p = new Pen(Theme.BorderWeak))
                foreach (int y in new int[] { 258, 342, 420, 560 }) g.DrawLine(p, 44, y, 636, y);
        }
    }
}
