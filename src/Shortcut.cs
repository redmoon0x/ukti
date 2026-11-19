using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Ukti
{
    sealed class Shortcut
    {
        public readonly Keys Key;
        public readonly Keys Modifiers;
        public static readonly Shortcut Default = new Shortcut(Keys.Space, Keys.Control);

        Shortcut(Keys key, Keys modifiers) { Key = key; Modifiers = modifiers; }

        public static bool TryCreate(Keys key, Keys modifiers, out Shortcut shortcut, out string error)
        {
            shortcut = null;
            key &= Keys.KeyCode;
            error = "Use Ctrl or Alt with a letter, number, Space, or F1-F11.";
            if ((modifiers & ~(Keys.Control | Keys.Alt | Keys.Shift)) != 0) return false;
            if ((modifiers & (Keys.Control | Keys.Alt)) == 0) return false;
            bool supported = (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) ||
                (key >= Keys.F1 && key <= Keys.F11) || key == Keys.Space;
            if (!supported) return false;
            if ((modifiers & Keys.Alt) != 0 && (key == Keys.F4 || key == Keys.Space))
            { error = "That shortcut is reserved by Windows. Choose another combination."; return false; }
            shortcut = new Shortcut(key, modifiers);
            error = null;
            return true;
        }

        public static bool TryParse(string text, out Shortcut shortcut)
        {
            shortcut = null;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('+');
            Keys mods = Keys.None, key = Keys.None;
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= Keys.Control;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= Keys.Alt;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= Keys.Shift;
                else
                {
                    if (key != Keys.None) return false;
                    if (part.Length == 1 && part[0] >= '0' && part[0] <= '9') key = Keys.D0 + (part[0] - '0');
                    else if (!Enum.TryParse<Keys>(part, true, out key)) return false;
                }
            }
            string error;
            return TryCreate(key, mods, out shortcut, out error);
        }

        public override string ToString()
        {
            List<string> parts = new List<string>();
            if ((Modifiers & Keys.Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & Keys.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Keys.Shift) != 0) parts.Add("Shift");
            parts.Add(Key >= Keys.D0 && Key <= Keys.D9 ? ((int)Key - (int)Keys.D0).ToString() : Key.ToString());
            return string.Join("+", parts.ToArray());
        }
    }
}
