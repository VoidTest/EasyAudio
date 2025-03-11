using System;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;

namespace VolumeControlTrayApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Enable visual styles and run our custom ApplicationContext.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }
    }

    // ApplicationContext manages tray icon, hooks, overlay, and settings.
    public class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private LowLevelMouseHook mouseHook;
        private VolumeOverlayForm overlayForm;
        private MMDevice audioDevice;
        private MMDeviceEnumerator enumerator;

        // Default volume step (5%) and bound keys (default: ControlKey).
        private float volumeStep = 0.05f;
        private List<Keys> activationKeys = new List<Keys> { Keys.ControlKey };

        public TrayApplicationContext()
        {
            // Load settings if they exist.
            SettingsManager.LoadSettings(out volumeStep, out activationKeys);

            // Check OS version.
            if (Environment.OSVersion.Version.Major < 6)
            {
                MessageBox.Show("This application requires Windows Vista or later (WASAPI).",
                    "Unsupported OS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
            }

            try
            {
                enumerator = new MMDeviceEnumerator();
                audioDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to access the default audio endpoint:\n" + ex.Message,
                    "Audio Device Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
            }

            overlayForm = new VolumeOverlayForm();

            trayIcon = new NotifyIcon
            {
                Icon = CreateCustomIcon(),
                Visible = true,
                Text = "Volume Control Hook"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Settings", null, OnSettings);
            contextMenu.Items.Add("Exit", null, OnExit);
            trayIcon.ContextMenuStrip = contextMenu;

            mouseHook = new LowLevelMouseHook(OnVolumeScroll);
            mouseHook.ActivationKeys = activationKeys.Select(k => (int)k).ToList();
            mouseHook.Install();
        }

        private Icon CreateCustomIcon()
        {
            int iconSize = 32;
            Bitmap bmp = new Bitmap(iconSize, iconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Brush brush = new SolidBrush(Color.Blue))
                {
                    g.FillEllipse(brush, 0, 0, iconSize, iconSize);
                }
                using (Font font = new Font("Arial", 16, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("V", font, Brushes.White, new RectangleF(0, 0, iconSize, iconSize), sf);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private void OnVolumeScroll(short wheelDelta)
        {
            try
            {
                audioDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                if (devices.Count > 0)
                    audioDevice = devices[0];
                else
                    return;
            }

            try
            {
                float currentVolume = audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                float newVolume = currentVolume;
                if (wheelDelta > 0)
                    newVolume = Math.Min(currentVolume + volumeStep, 1.0f);
                else if (wheelDelta < 0)
                    newVolume = Math.Max(currentVolume - volumeStep, 0.0f);
                audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar = newVolume;
                overlayForm.ShowOverlay(newVolume);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Volume change failed: " + ex.Message);
            }
        }

        private void OnSettings(object sender, EventArgs e)
        {
            using (var form = new VolumeSettingsForm(volumeStep, activationKeys))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    volumeStep = form.VolumeStep;
                    activationKeys = form.ActivationKeyCombo;
                    mouseHook.ActivationKeys = activationKeys.Select(k => (int)k).ToList();
                    SettingsManager.SaveSettings(volumeStep, activationKeys);
                }
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            mouseHook.Uninstall();
            trayIcon.Visible = false;
            Application.Exit();
        }
    }

    // Overlay form displays volume changes.
    public class VolumeOverlayForm : Form
    {
        private Timer hideTimer;
        private float currentVolume = 0f;

        public VolumeOverlayForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;
            this.BackColor = Color.DimGray;
            this.Opacity = 1.0;
            this.Size = new Size(200, 50);
            Rectangle screen = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point((screen.Width - this.Width) / 2, 50);
            hideTimer = new Timer { Interval = 1500 };
            hideTimer.Tick += (s, e) =>
            {
                this.Hide();
                hideTimer.Stop();
            };
            this.Hide();
        }

        public void ShowOverlay(float volumeScalar)
        {
            currentVolume = volumeScalar;
            this.Invalidate();
            this.Show();
            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(Color.DimGray);
            int margin = 8, barHeight = 10;
            Rectangle barRect = new Rectangle(margin, margin, this.Width - 2 * margin, barHeight);
            using (SolidBrush bgBrush = new SolidBrush(Color.DarkGray))
                g.FillRectangle(bgBrush, barRect);
            int fillWidth = (int)(barRect.Width * currentVolume);
            if (fillWidth > 0)
            {
                Rectangle fillRect = new Rectangle(barRect.X, barRect.Y, fillWidth, barRect.Height);
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(fillRect, Color.DarkBlue, Color.Blue, 0f))
                    g.FillRectangle(brush, fillRect);
            }
            using (Pen pen = new Pen(Color.DarkGray, 2))
                g.DrawRectangle(pen, barRect);
            string text = $"Volume: {Math.Round(currentVolume * 100)}%";
            using (Font font = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                SizeF textSize = g.MeasureString(text, font);
                float textX = (this.Width - textSize.Width) / 2;
                float textY = barRect.Bottom + 5;
                g.DrawString(text, font, textBrush, textX, textY);
            }
        }
    }

    // Custom TextBox for capturing key combinations.
    public class KeyBindTextBox : TextBox
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public List<Keys> CapturedKeys { get; set; } = new List<Keys>();

        public KeyBindTextBox()
        {
            this.ShortcutsEnabled = false;
        }

        // Normalize keys so that left/right modifiers map to canonical values.
        private Keys NormalizeKey(Keys key)
        {
            if (key == Keys.LControlKey || key == Keys.RControlKey)
                return Keys.ControlKey;
            if (key == Keys.LMenu || key == Keys.RMenu)
                return Keys.Menu;
            if (key == Keys.LShiftKey || key == Keys.RShiftKey)
                return Keys.ShiftKey;
            return key;
        }

        protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            e.IsInputKey = true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys normalized = NormalizeKey(keyData);
            if (!CapturedKeys.Contains(normalized))
                CapturedKeys.Add(normalized);
            UpdateText();
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            Keys normalized = NormalizeKey(e.KeyCode);
            if (!CapturedKeys.Contains(normalized))
                CapturedKeys.Add(normalized);
            UpdateText();
            e.SuppressKeyPress = true;
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            // Do not clear keys—keep the full combo visible.
        }

        public void ClearKeys()
        {
            CapturedKeys.Clear();
            UpdateText();
        }

        public void UpdateText()
        {
            this.Text = string.Join(" + ", CapturedKeys);
        }
    }

    // Settings form to adjust volume step and key binding.
    public class VolumeSettingsForm : Form
    {
        private NumericUpDown numericUpDown;
        private KeyBindTextBox txtActivationKey;
        private Button btnOK, btnCancel, btnClear;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public List<Keys> ActivationKeyCombo { get; private set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public float VolumeStep { get; private set; }

        public VolumeSettingsForm(float currentStep, List<Keys> currentActivationKeys)
        {
            this.Text = "Settings";
            this.ClientSize = new Size(350, 180);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label lblVolume = new Label { Text = "Volume Step (%):", Location = new Point(10, 20), AutoSize = true };
            this.Controls.Add(lblVolume);

            numericUpDown = new NumericUpDown
            {
                Location = new Point(130, 18),
                Minimum = 1,
                Maximum = 50,
                Value = (decimal)(currentStep * 100),
                Width = 80
            };
            this.Controls.Add(numericUpDown);

            Label lblActivation = new Label { Text = "Activation Combo:", Location = new Point(10, 60), AutoSize = true };
            this.Controls.Add(lblActivation);

            txtActivationKey = new KeyBindTextBox
            {
                Location = new Point(130, 58),
                Width = 130,
                ReadOnly = true,
                TabStop = true
            };

            if (currentActivationKeys != null && currentActivationKeys.Count > 0)
            {
                // Normalize initial keys.
                txtActivationKey.CapturedKeys = currentActivationKeys.Select(k => 
                    (k == Keys.LControlKey || k == Keys.RControlKey) ? Keys.ControlKey :
                    (k == Keys.LMenu || k == Keys.RMenu) ? Keys.Menu :
                    (k == Keys.LShiftKey || k == Keys.RShiftKey) ? Keys.ShiftKey : k).ToList();
                txtActivationKey.UpdateText();
            }
            this.Controls.Add(txtActivationKey);

            btnClear = new Button { Text = "Clear", Location = new Point(270, 56), Width = 50 };
            btnClear.Click += (s, e) => { txtActivationKey.ClearKeys(); };
            this.Controls.Add(btnClear);

            btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(60, 120), Width = 70 };
            btnOK.Click += (s, e) =>
            {
                VolumeStep = (float)numericUpDown.Value / 100f;
                ActivationKeyCombo = txtActivationKey.CapturedKeys.ToList();
                if (ActivationKeyCombo == null || ActivationKeyCombo.Count == 0)
                    ActivationKeyCombo = new List<Keys> { Keys.ControlKey };
                this.Close();
            };
            this.Controls.Add(btnOK);

            btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(150, 120), Width = 70 };
            btnCancel.Click += (s, e) => { this.Close(); };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }
    }

    // Low-level mouse hook that triggers volume change only when the exact bound keys are pressed.
    public class LowLevelMouseHook
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelMouseProc proc;
        private IntPtr hookId = IntPtr.Zero;
        private Action<short> volumeScrollCallback;

        // ActivationKeys: the keys (as virtual-key codes) the user bound.
        public List<int> ActivationKeys { get; set; } = new List<int> { (int)Keys.ControlKey };

        public LowLevelMouseHook(Action<short> callback)
        {
            proc = HookCallback;
            volumeScrollCallback = callback;
        }

        public void Install() { hookId = SetHook(proc); }
        public void Uninstall() { UnhookWindowsHookEx(hookId); }

        private IntPtr SetHook(LowLevelMouseProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
                return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
            {
                int mouseData = Marshal.ReadInt32(lParam, 8);
                short wheelDelta = (short)(mouseData >> 16);
                if (IsActivationComboPressed())
                {
                    volumeScrollCallback?.Invoke(wheelDelta);
                    return (IntPtr)1; // block event
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // Normalize left/right modifiers.
        private int NormalizeKey(int key)
        {
            if (key == (int)Keys.LControlKey || key == (int)Keys.RControlKey)
                return (int)Keys.ControlKey;
            if (key == (int)Keys.LMenu || key == (int)Keys.RMenu)
                return (int)Keys.Menu;
            if (key == (int)Keys.LShiftKey || key == (int)Keys.RShiftKey)
                return (int)Keys.ShiftKey;
            return key;
        }

        // Returns true if the normalized set of currently pressed keys exactly equals the normalized bound set.
        private bool IsActivationComboPressed()
        {
            HashSet<int> pressedSet = new HashSet<int>();
            for (int key = 1; key < 256; key++)
            {
                // Ignore toggle keys.
                if (key == (int)Keys.Capital || key == (int)Keys.NumLock || key == (int)Keys.Scroll)
                    continue;
                if ((GetAsyncKeyState(key) & 0x8000) != 0)
                    pressedSet.Add(NormalizeKey(key));
            }
            HashSet<int> boundSet = new HashSet<int>(ActivationKeys.Select(NormalizeKey));
            return pressedSet.SetEquals(boundSet);
        }

        #region WinAPI Interop
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        #endregion
    }

    // SettingsManager persists volume step and activation keys.
    public static class SettingsManager
    {
        private static readonly string configFile = Path.Combine(Application.UserAppDataPath, "settings.config");

        public static void SaveSettings(float volumeStep, List<Keys> activationKeys)
        {
            Directory.CreateDirectory(Application.UserAppDataPath);
            using (StreamWriter sw = new StreamWriter(configFile))
            {
                sw.WriteLine(volumeStep.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sw.WriteLine(string.Join(",", activationKeys.Select(k => k.ToString())));
            }
        }

        public static void LoadSettings(out float volumeStep, out List<Keys> activationKeys)
        {
            volumeStep = 0.05f;
            activationKeys = new List<Keys> { Keys.ControlKey };
            if (File.Exists(configFile))
            {
                try
                {
                    string[] lines = File.ReadAllLines(configFile);
                    if (lines.Length >= 2)
                    {
                        if (float.TryParse(lines[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vol))
                            volumeStep = vol;
                        var keys = lines[1].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        var parsedKeys = new List<Keys>();
                        foreach (var keyStr in keys)
                        {
                            if (Enum.TryParse(keyStr, out Keys key))
                                parsedKeys.Add(key);
                        }
                        if (parsedKeys.Count > 0)
                            activationKeys = parsedKeys;
                    }
                }
                catch { }
            }
        }
    }
}
