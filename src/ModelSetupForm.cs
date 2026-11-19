using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace Ukti
{
    // One library for first-run setup and changing models later. Downloading
    // only populates the local cache; the caller decides when to activate it.
    sealed class ModelSetupForm : ThemedForm
    {
        readonly string _directory;
        readonly ModelChoice[] _choices;
        readonly Label _status;
        readonly LinkLabel _license;
        readonly HexButton _download, _quit, _delete;
        readonly string _protectedModelId;
        readonly Func<SpeechModel, bool> _confirmDelete;
        SpeechModel _selected;
        WebClient _client;
        volatile bool _cancelled;
        bool _busy, _done;
        int _progressValue;
        string _partialPath;
        public SpeechModel SelectedModel { get; private set; }

        public ModelSetupForm(SpeechModel selected, string directory = null, string protectedModelId = null,
                              Func<SpeechModel, bool> confirmDelete = null)
        {
            _directory = directory ?? ModelCatalog.DirectoryPath;
            _protectedModelId = protectedModelId;
            _confirmDelete = confirmDelete ?? ConfirmDeletion;
            _selected = selected ?? ModelCatalog.All[0];
            Text = "Ukti speech models";
            ClientSize = new Size(680, 640);
            AddCloseButton(628).TabIndex = 8;
            AddLabel("Speech models", new Rectangle(72, 24, 520, 32), 15f, Theme.Ink, true);
            AddLabel("Choose what works for you. Every model runs on this PC.",
                new Rectangle(24, 70, 632, 24), 9.5f, Theme.MutedInk, false);
            _choices = new ModelChoice[ModelCatalog.All.Length];
            for (int i = 0; i < _choices.Length; i++)
            {
                SpeechModel model = ModelCatalog.All[i];
                ModelChoice choice = new ModelChoice(model, ModelCatalog.IsInstalled(model, _directory));
                choice.Bounds = new Rectangle(24, 110 + i * 84, 632, 76);
                choice.TabIndex = i;
                choice.Click += delegate { SelectModel(model); };
                Controls.Add(choice);
                _choices[i] = choice;
            }
            _license = new LinkLabel();
            _license.Bounds = new Rectangle(24, 452, 632, 24);
            _license.BackColor = Color.Transparent;
            _license.LinkColor = _license.ActiveLinkColor = _license.VisitedLinkColor = Theme.MutedInk;
            _license.LinkBehavior = LinkBehavior.HoverUnderline;
            _license.UseMnemonic = false;
            _license.TabIndex = 4;
            _license.LinkClicked += delegate
            {
                try { Process.Start(_selected.LicenseUrl); }
                catch (Exception ex) { SetStatus("Could not open the model card: " + ex.Message, true); }
            };
            Controls.Add(_license);
            _status = AddLabel("", new Rectangle(24, 496, 632, 52), 9f, Theme.MutedInk, false);
            _status.AccessibleName = "Model download status";
            _quit = new HexButton();
            _quit.Text = "Cancel";
            _quit.Bounds = new Rectangle(386, 574, 100, 38);
            _quit.TabIndex = 6;
            _quit.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_quit);
            _download = new HexButton();
            _download.Primary = true;
            _download.Bounds = new Rectangle(496, 574, 160, 38);
            _download.TabIndex = 7;
            _download.Click += delegate { UseOrDownload(); };
            Controls.Add(_download);
            _delete = new HexButton();
            _delete.Text = "Delete model";
            _delete.AccessibleDescription = "Permanently remove this model's downloaded files after confirmation.";
            _delete.Bounds = new Rectangle(24, 574, 132, 38);
            _delete.TabIndex = 5;
            _delete.Click += delegate { DeleteSelectedModel(); };
            Controls.Add(_delete);
            AcceptButton = _download;
            CancelButton = _quit;
            SelectModel(_selected);
            ActiveControl = _choices[Array.IndexOf(ModelCatalog.All, ModelCatalog.Find(_selected.Id))];
        }

        void SelectModel(SpeechModel model)
        {
            if (_busy) return;
            _selected = model;
            RefreshChoices();
            foreach (ModelChoice choice in _choices) choice.Selected = choice.Model.Id == model.Id;
            _license.Text = "Model card & license: " + model.License;
            bool installed = ModelCatalog.IsInstalled(model, _directory);
            _download.Text = installed ? "Use model" : "Download & use";
            UpdateDeleteButton();
            SetStatus(model.Id == _protectedModelId ? "Current model. Save a different model in Settings before deleting this one." :
                installed ? "Downloaded. Ready offline. Delete model removes its files from this PC." :
                model.SizeText + " download / Q8_0 / NVIDIA GPU required. No audio is uploaded.", false);
            _progressValue = 0;
            Invalidate();
        }
        void RefreshChoices()
        {
            foreach (ModelChoice choice in _choices)
            {
                choice.Installed = ModelCatalog.IsInstalled(choice.Model, _directory);
                choice.IsCurrent = choice.Model.Id == _protectedModelId;
            }
        }
        void UpdateDeleteButton()
        {
            _delete.Enabled = !_busy && _selected.Id != _protectedModelId && ModelCatalog.HasStoredFiles(_selected, _directory);
            _delete.AccessibleDescription = _selected.Id == _protectedModelId ?
                "The current model is protected. Save a different model in Settings first." :
                "Permanently remove this model's downloaded files after confirmation.";
        }
        bool ConfirmDeletion(SpeechModel model)
        {
            long bytes = ModelCatalog.StoredBytes(model, _directory);
            return MessageBox.Show(this, "Delete " + model.Name + " from this PC?\n\nThis permanently removes " +
                (bytes / 1000000.0).ToString("0.##") + " MB of model files. You can download it again later.",
                "Delete speech model", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        void DeleteSelectedModel()
        {
            if (_busy) return;
            if (_selected.Id == _protectedModelId)
            { SetStatus("Save a different model in Settings before deleting the current model.", true); return; }
            if (!ModelCatalog.HasStoredFiles(_selected, _directory)) return;
            try
            {
                if (!_confirmDelete(_selected)) return;
                ModelCatalog.DeleteFiles(_selected, _directory, _protectedModelId);
                SelectModel(_selected);
                SetStatus("Deleted " + _selected.Name + ". You can download it again at any time.", false);
            }
            catch (Exception ex)
            {
                SelectModel(_selected);
                SetStatus("Could not delete all model files: " + ex.Message, true);
            }
        }
        void SetStatus(string text, bool error)
        {
            _status.Text = text;
            _status.ForeColor = error ? Theme.Danger : Theme.MutedInk;
            _status.AccessibleDescription = text;
        }
        void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (ModelChoice choice in _choices) choice.Enabled = !busy;
            _license.Enabled = !busy;
            _download.Enabled = !busy;
            UpdateDeleteButton();
            _quit.Text = busy ? "Cancel download" : "Cancel";
            _quit.Width = busy ? 132 : 100;
            _quit.Left = 486 - _quit.Width;
        }
        void CompleteSelection()
        {
            SelectedModel = _selected;
            _done = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        void UseOrDownload()
        {
            if (_busy) return;
            if (ModelCatalog.IsInstalled(_selected, _directory)) { CompleteSelection(); return; }
            SetBusy(true);
            SetStatus("Connecting to Hugging Face\u2026", false);
            _progressValue = 0;
            Invalidate();
            try
            {
                Directory.CreateDirectory(_directory);
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_directory)));
                if (drive.IsReady && drive.AvailableFreeSpace < _selected.Bytes + 67108864L)
                    throw new IOException("Not enough free disk space for this model.");
                _partialPath = Path.Combine(_directory, _selected.FileName + "." + Guid.NewGuid().ToString("N") + ".partial");
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                _client = new WebClient();
                _client.DownloadProgressChanged += OnProgress;
                _client.DownloadFileCompleted += OnComplete;
                _client.DownloadFileAsync(new Uri(_selected.Url), _partialPath);
            }
            catch (Exception ex) { Fail("Could not start the download: " + ex.Message); }
        }
        void OnProgress(object sender, DownloadProgressChangedEventArgs e)
        {
            if (_cancelled || IsDisposed) return;
            _progressValue = Math.Max(0, Math.Min(100, (int)(100.0 * e.BytesReceived / _selected.Bytes)));
            SetStatus(string.Format("{0:0} MB of {1:0} MB  /  {2}%", e.BytesReceived / 1000000.0,
                _selected.Bytes / 1000000.0, _progressValue), false);
            Invalidate(new Rectangle(24, 484, 632, 4));
        }
        void OnComplete(object sender, AsyncCompletedEventArgs e)
        {
            if (_client != null) { _client.Dispose(); _client = null; }
            if (_cancelled || e.Cancelled) { CleanupPartial(); return; }
            if (e.Error != null) { Fail("Download failed: " + e.Error.Message); return; }
            SetStatus("Verifying the download\u2026", false);
            SpeechModel model = _selected;
            Thread verify = new Thread(delegate()
            {
                string error;
                try { error = ModelCatalog.VerifyFile(_partialPath, model, delegate { return _cancelled; }); }
                catch (OperationCanceledException) { CleanupPartial(); return; }
                catch (Exception ex) { error = "Could not verify the download: " + ex.Message; }
                if (_cancelled) { CleanupPartial(); return; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (_cancelled || IsDisposed) { CleanupPartial(); return; }
                        if (error != null) { Fail(error); return; }
                        try
                        {
                            string target = Path.Combine(_directory, model.FileName);
                            // Promote only a verified file; never destroy a working model on failure.
                            if (File.Exists(target)) File.Replace(_partialPath, target, null);
                            else File.Move(_partialPath, target);
                            _partialPath = null;
                            CompleteSelection();
                        }
                        catch (Exception ex) { Fail("Could not save the model: " + ex.Message); }
                    });
                }
                catch (InvalidOperationException) { CleanupPartial(); }
            });
            verify.IsBackground = true;
            verify.Start();
        }
        void CleanupPartial()
        {
            try { if (_partialPath != null && File.Exists(_partialPath)) File.Delete(_partialPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        void Fail(string message)
        {
            if (_client != null) { _client.Dispose(); _client = null; }
            CleanupPartial();
            if (IsDisposed || Disposing || _cancelled) return;
            SetBusy(false);
            _download.Text = "Try again";
            SetStatus(message, true);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_done)
            {
                _cancelled = true;
                if (_client != null) _client.CancelAsync();
                // CancelAsync can release the writer immediately. If a writer
                // or verifier still owns the file, its completion cleans up.
                CleanupPartial();
            }
            base.OnFormClosing(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.DrawMark(g, new RectangleF(24, 23, 36, 36), Theme.Ink);
            Rectangle track = new Rectangle(24, 484, 632, 4);
            using (GraphicsPath path = Theme.RoundedRect(track, 2))
            using (SolidBrush b = new SolidBrush(Theme.Layer02)) g.FillPath(b, path);
            int filled = (int)(632 * _progressValue / 100f);
            if (filled > 0)
                using (GraphicsPath path = Theme.RoundedRect(new Rectangle(24, 484, Math.Max(4, filled), 4), 2))
                using (SolidBrush b = new SolidBrush(Theme.Ink)) g.FillPath(b, path);
        }
    }

    sealed class ModelChoice : ActionControl
    {
        public readonly SpeechModel Model;
        bool _installed;
        public bool IsCurrent { get; set; }
        public bool Installed
        {
            get { return _installed; }
            set { _installed = value; AccessibleDescription = Model.Description + ". " + Model.SizeText + (value ? ". Downloaded." : ". Not downloaded."); Invalidate(); }
        }
        bool _selected;
        public bool Selected
        {
            get { return _selected; }
            set { bool changed = value != _selected; _selected = value; Invalidate(); if (changed && IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1); }
        }
        public ModelChoice(SpeechModel model, bool installed)
        {
            Model = model;
            Installed = installed;
            AccessibleName = model.Name;
            AccessibleRole = AccessibleRole.RadioButton;
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Enter || key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
            {
                List<ModelChoice> choices = new List<ModelChoice>();
                if (Parent != null) foreach (Control control in Parent.Controls) if (control is ModelChoice) choices.Add((ModelChoice)control);
                int direction = e.KeyCode == Keys.Up || e.KeyCode == Keys.Left ? -1 : 1;
                if (choices.Count > 0)
                {
                    ModelChoice next = choices[(choices.IndexOf(this) + direction + choices.Count) % choices.Count];
                    next.Focus(); next.ActivateControl();
                }
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.DrawSurface(g, bounds, _selected || Hovered ? Theme.Layer01 : Theme.BgBase, 10);
            if (_selected)
                using (GraphicsPath path = Theme.RoundedRect(bounds, 10))
                using (Pen p = new Pen(Theme.MutedInk)) g.DrawPath(p, path);
            using (Pen p = new Pen(_selected ? Theme.Ink : Theme.FaintInk, 1.3f)) g.DrawEllipse(p, 16, 30, 14, 14);
            if (_selected) using (SolidBrush b = new SolidBrush(Theme.Ink)) g.FillEllipse(b, 20, 34, 6, 6);
            Theme.DrawText(g, Model.Name, 10.5f, Theme.Ink, new Rectangle(46, 12, Width - 220, 24), true);
            Theme.DrawText(g, IsCurrent ? "Current" : _installed ? "Downloaded" : Model.SizeText, 8.5f, Theme.MutedInk,
                new Rectangle(Width - 120, 12, 104, 24), false);
            Theme.DrawText(g, Model.Description, 9f, Theme.MutedInk, new Rectangle(46, 40, Width - 62, 24), false);
            DrawFocus(g);
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ChoiceAccessibleObject(this); }
        sealed class ChoiceAccessibleObject : ControlAccessibleObject
        {
            readonly ModelChoice _owner;
            public ChoiceAccessibleObject(ModelChoice owner) : base(owner) { _owner = owner; }
            public override AccessibleStates State { get { return base.State | (_owner.Selected ? AccessibleStates.Checked : 0); } }
            public override void DoDefaultAction() { _owner.ActivateControl(); }
            public override string DefaultAction { get { return "Select"; } }
        }
    }
}
