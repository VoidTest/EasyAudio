using System;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace VolumeControlTrayApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Load settings including volume step and theme.
            SettingsManager.LoadSettings(out float volumeStep);
            if (volumeStep <= 0)
                volumeStep = 0.05f; // default 5%
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext(volumeStep));
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private LowLevelMouseHook mouseHook;
        private VolumeOverlayForm overlayForm;
        private MMDeviceEnumerator enumerator;
        private float volumeStep;

        public TrayApplicationContext(float initialVolumeStep)
        {
            volumeStep = initialVolumeStep;
            if (Environment.OSVersion.Version.Major < 6)
            {
                MessageBox.Show("This application requires Windows Vista or later (WASAPI).",
                    "Unsupported OS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
            }
            try { enumerator = new MMDeviceEnumerator(); }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to initialize audio device enumerator:\n" + ex.Message,
                    "Audio Device Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
            }
            overlayForm = new VolumeOverlayForm();
            trayIcon = new NotifyIcon
            {
                Icon = CreateCustomIcon(),
                Visible = true,
                Text = "Volume Control Tray"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Settings", null, OnSettings);
            contextMenu.Items.Add("Audio Mappings", null, OnAudioMappings);
            contextMenu.Items.Add("Exit", null, OnExit);
            trayIcon.ContextMenuStrip = contextMenu;

            mouseHook = new LowLevelMouseHook(OnMouseWheel);
            mouseHook.Install();

            StartupManager.SetStartup(SettingsManager.StartWithWindows);
        }

        private Icon CreateCustomIcon()
        {
            int iconSize = 32;
            Bitmap bmp = new Bitmap(iconSize, iconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Brush brush = new SolidBrush(Color.Blue))
                    g.FillEllipse(brush, 0, 0, iconSize, iconSize);
                using (Font font = new Font("Arial", 16, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("V", font, Brushes.White, new RectangleF(0, 0, iconSize, iconSize), sf);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private bool OnMouseWheel(short wheelDelta)
        {
            HashSet<int> currentPressed = new HashSet<int>();
            for (int key = 1; key < 256; key++)
            {
                if (key == (int)Keys.Capital || key == (int)Keys.NumLock || key == (int)Keys.Scroll)
                    continue;
                if ((GetAsyncKeyState(key) & 0x8000) != 0)
                    currentPressed.Add(NormalizeKey(key));
            }
            if (currentPressed.Count == 0)
                return false;

            // Apply volume change for every mapping that exactly matches.
            foreach (var mapping in SettingsManager.AudioMappings)
            {
                HashSet<int> mappingSet = new HashSet<int>(mapping.KeyCombo.Select(k => NormalizeKey((int)k)));
                if (currentPressed.SetEquals(mappingSet))
                {
                    if (mapping.IsMasterVolume)
                    {
                        // Adjust all active endpoints.
                        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                        {
                            AdjustVolume(device, wheelDelta);
                        }
                    }
                    else if (!string.IsNullOrEmpty(mapping.ApplicationName))
                    {
                        AdjustVolumeForApplication(mapping.ApplicationName, wheelDelta);
                    }
                    else if (!string.IsNullOrEmpty(mapping.AudioDeviceId))
                    {
                        MMDevice device = GetDeviceFromId(mapping.AudioDeviceId);
                        if (device != null)
                            AdjustVolume(device, wheelDelta);
                    }
                }
            }
            return true;
        }

        private void AdjustVolume(MMDevice device, short wheelDelta)
        {
            try
            {
                float currentVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                float newVolume = currentVolume;
                if (wheelDelta > 0)
                    newVolume = Math.Min(currentVolume + volumeStep, 1.0f);
                else if (wheelDelta < 0)
                    newVolume = Math.Max(currentVolume - volumeStep, 0.0f);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = newVolume;
                float confirmedVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                Debug.WriteLine($"[DEBUG] Device: {device.FriendlyName}, Old: {currentVolume:P0}, New: {newVolume:P0}, Confirmed: {confirmedVolume:P0}");
                overlayForm.ShowOverlay(confirmedVolume);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Volume change failed: " + ex.Message);
            }
        }

        private void AdjustVolumeForApplication(string processName, short wheelDelta)
        {
            try
            {
                // Iterate over all active render endpoints.
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var sessionManager = device.AudioSessionManager;
                    for (int i = 0; i < sessionManager.Sessions.Count; i++)
                    {
                        var session = sessionManager.Sessions[i];
                        if (session.GetProcessID != 0)
                        {
                            using (Process proc = Process.GetProcessById((int)session.GetProcessID))
                            {
                                if (string.Equals(proc.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                                {
                                    float currentVolume = session.SimpleAudioVolume.Volume;
                                    float newVolume = currentVolume;
                                    if (wheelDelta > 0)
                                        newVolume = Math.Min(currentVolume + volumeStep, 1.0f);
                                    else if (wheelDelta < 0)
                                        newVolume = Math.Max(currentVolume - volumeStep, 0.0f);
                                    session.SimpleAudioVolume.Volume = newVolume;
                                    float confirmedVolume = session.SimpleAudioVolume.Volume;
                                    Debug.WriteLine($"[DEBUG] App: {processName}, Old: {currentVolume:P0}, New: {newVolume:P0}, Confirmed: {confirmedVolume:P0}");
                                    overlayForm.ShowOverlay(confirmedVolume);
                                    // Continue looping so that if multiple sessions exist, they all adjust.
                                }
                            }
                        }
                    }
                }
                Debug.WriteLine($"[DEBUG] No matching session found for '{processName}'. Ensure the application is playing audio.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("App volume change failed: " + ex.Message);
            }
        }

        private MMDevice GetDeviceFromId(string deviceId)
        {
            try { return enumerator.GetDevice(deviceId); }
            catch { return null; }
        }

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

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private void OnSettings(object sender, EventArgs e)
        {
            using (var form = new VolumeSettingsForm(volumeStep))
            {
                form.ShowDialog();
                volumeStep = form.VolumeStep;
                SettingsManager.SaveSettings(volumeStep);
                StartupManager.SetStartup(SettingsManager.StartWithWindows);
            }
        }

        private void OnAudioMappings(object sender, EventArgs e)
        {
            using (var form = new AudioMappingsForm())
            {
                form.ShowDialog();
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            SettingsManager.SaveSettings(volumeStep);
            mouseHook.Uninstall();
            trayIcon.Visible = false;
            Application.Exit();
        }
    }

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
            this.BackColor = Color.Magenta;
            this.TransparencyKey = Color.Magenta;
            this.Opacity = 1.0;
            this.Size = new Size(220, 70);
            Rectangle screen = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point((screen.Width - this.Width) / 2, 50);
            hideTimer = new Timer { Interval = 1500 };
            hideTimer.Tick += (s, e) => { this.Hide(); hideTimer.Stop(); };
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
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int margin = 10;
            int barHeight = 12;
            Rectangle barRect = new Rectangle(margin, margin, this.Width - 2 * margin, barHeight);

            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int radius = 6;
                path.AddArc(barRect.X, barRect.Y, radius, radius, 180, 90);
                path.AddArc(barRect.Right - radius, barRect.Y, radius, radius, 270, 90);
                path.AddArc(barRect.Right - radius, barRect.Bottom - radius, radius, radius, 0, 90);
                path.AddArc(barRect.X, barRect.Bottom - radius, radius, radius, 90, 90);
                path.CloseFigure();

                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(220, 60, 60, 60)))
                    g.FillPath(bgBrush, path);

                int fillWidth = (int)(barRect.Width * currentVolume);
                if (fillWidth > 0)
                {
                    Rectangle fillRect = new Rectangle(barRect.X, barRect.Y, fillWidth, barRect.Height);
                    using (var fillPath = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        fillPath.AddArc(fillRect.X, fillRect.Y, radius, radius, 180, 90);
                        fillPath.AddArc(fillRect.Right - radius, fillRect.Y, radius, radius, 270, 90);
                        fillPath.AddLine(fillRect.Right, fillRect.Bottom, fillRect.X, fillRect.Bottom);
                        fillPath.CloseFigure();

                        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(fillRect, Color.LightSkyBlue, Color.DodgerBlue, 0f))
                            g.FillPath(brush, fillPath);
                    }
                }

                using (Pen pen = new Pen(Color.Gray, 2))
                    g.DrawPath(pen, path);
            }

            string text = $"Volume: {Math.Round(currentVolume * 100)}%";
            using (Font font = new Font("Segoe UI", 10, FontStyle.Bold))
            {
                SizeF textSize = g.MeasureString(text, font);
                float textX = (this.Width - textSize.Width) / 2;
                float textY = barRect.Bottom + 8;
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                    g.DrawString(text, font, textBrush, textX, textY);
            }
        }
    }

    public class LowLevelMouseHook
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelMouseProc proc;
        private IntPtr hookId = IntPtr.Zero;
        private Func<short, bool> mouseWheelCallback;

        public LowLevelMouseHook(Func<short, bool> callback)
        {
            proc = HookCallback;
            mouseWheelCallback = callback;
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
                bool handled = mouseWheelCallback?.Invoke(wheelDelta) ?? false;
                return handled ? (IntPtr)1 : CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        #region WinAPI Interop
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, int dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        #endregion
    }

    public class KeyBindTextBox : TextBox
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public List<Keys> CapturedKeys { get; set; } = new List<Keys>();

        public KeyBindTextBox() { this.ShortcutsEnabled = false; }

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

    public class VolumeSettingsForm : Form
    {
        private NumericUpDown numericUpDown;
        private Button btnOK, btnCancel, btnMappings, btnToggleTheme;
        private CheckBox chkStartup;
        
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float VolumeStep { get; private set; }

        public VolumeSettingsForm(float currentStep)
        {
            this.Text = "Volume Control Settings";
            this.ClientSize = new Size(400, 300);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterScreen;
            ApplyTheme(this);
            this.Font = new Font("Segoe UI", 10);

            Label lblVolume = new Label { Text = "Volume Step (%):", Location = new Point(20, 30), AutoSize = true };
            this.Controls.Add(lblVolume);

            numericUpDown = new NumericUpDown
            {
                Location = new Point(180, 28),
                Minimum = 1,
                Maximum = 50,
                Width = 80
            };
            decimal stepValue = (decimal)(currentStep * 100);
            numericUpDown.Value = stepValue < numericUpDown.Minimum ? numericUpDown.Minimum : stepValue;
            this.Controls.Add(numericUpDown);

            chkStartup = new CheckBox
            {
                Text = "Start with Windows",
                Location = new Point(20, 70),
                AutoSize = true,
                Checked = SettingsManager.StartWithWindows
            };
            this.Controls.Add(chkStartup);

            btnMappings = new Button
            {
                Text = "Manage Audio Mappings",
                Location = new Point(20, 110),
                AutoSize = true,
                MinimumSize = new Size(200, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = GetThemeButtonColor()
            };
            btnMappings.Click += (s, e) =>
            {
                using (var mappingsForm = new AudioMappingsForm())
                    mappingsForm.ShowDialog();
            };
            this.Controls.Add(btnMappings);

            btnToggleTheme = new Button
            {
                Text = "Toggle Theme",
                Location = new Point(240, 110),
                AutoSize = true,
                MinimumSize = new Size(120, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = GetThemeButtonColor()
            };
            btnToggleTheme.Click += (s, e) =>
            {
                // Toggle theme between Light and Dark.
                SettingsManager.ThemeMode = SettingsManager.ThemeMode == "Light" ? "Dark" : "Light";
                ApplyTheme(this);
            };
            this.Controls.Add(btnToggleTheme);

            btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(80, 200), AutoSize = true, MinimumSize = new Size(80, 30), FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            btnOK.Click += (s, e) =>
            {
                VolumeStep = (float)numericUpDown.Value / 100f;
                SettingsManager.StartWithWindows = chkStartup.Checked;
                this.Close();
            };
            this.Controls.Add(btnOK);

            btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(180, 200), AutoSize = true, MinimumSize = new Size(80, 30), FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }

        private void ApplyTheme(Form form)
        {
            if (SettingsManager.ThemeMode == "Dark")
            {
                form.BackColor = Color.FromArgb(45, 45, 48);
                form.ForeColor = Color.White;
            }
            else
            {
                form.BackColor = Color.WhiteSmoke;
                form.ForeColor = Color.Black;
            }
            // Update any controls if needed.
        }

        private Color GetThemeButtonColor()
        {
            return SettingsManager.ThemeMode == "Dark" ? Color.DimGray : Color.LightSteelBlue;
        }
    }

    public class AudioMappingsForm : Form
    {
        private ListBox lstMappings;
        private Button btnAdd, btnRemove, btnClose;

        public AudioMappingsForm()
        {
            this.Text = "Audio Mappings";
            this.ClientSize = new Size(450, 300);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.Manual; // We'll center manually.
            ApplyTheme(this);
            this.Font = new Font("Segoe UI", 10);

            lstMappings = new ListBox { Location = new Point(20, 20), Size = new Size(400, 180) };
            this.Controls.Add(lstMappings);
            RefreshList();

            btnAdd = new Button { Text = "Add", Location = new Point(20, 220), AutoSize = true, MinimumSize = new Size(80, 30), FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            btnAdd.Click += (s, e) =>
            {
                using (var mappingForm = new AudioMappingForm())
                {
                    if (mappingForm.ShowDialog() == DialogResult.OK)
                    {
                        SettingsManager.AudioMappings.Add(mappingForm.Mapping);
                        RefreshList();
                    }
                }
            };
            this.Controls.Add(btnAdd);

            btnRemove = new Button { Text = "Remove", Location = new Point(110, 220), AutoSize = true, MinimumSize = new Size(80, 30), FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            btnRemove.Click += (s, e) =>
            {
                if (lstMappings.SelectedIndex >= 0)
                {
                    SettingsManager.AudioMappings.RemoveAt(lstMappings.SelectedIndex);
                    RefreshList();
                }
            };
            this.Controls.Add(btnRemove);

            btnClose = new Button { Text = "Close", Location = new Point(340, 220), AutoSize = true, MinimumSize = new Size(80, 30), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            this.Controls.Add(btnClose);

            this.Load += AudioMappingsForm_Load;
        }

        private void AudioMappingsForm_Load(object sender, EventArgs e)
        {
            // Center on primary screen.
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(workingArea.X + (workingArea.Width - this.Width) / 2,
                                      workingArea.Y + (workingArea.Height - this.Height) / 2);
        }

        private void RefreshList()
        {
            lstMappings.Items.Clear();
            foreach (var mapping in SettingsManager.AudioMappings)
                lstMappings.Items.Add(mapping);
        }

        private Color GetThemeButtonColor()
        {
            return SettingsManager.ThemeMode == "Dark" ? Color.DimGray : Color.LightSteelBlue;
        }

        private void ApplyTheme(Form form)
        {
            if (SettingsManager.ThemeMode == "Dark")
            {
                form.BackColor = Color.FromArgb(45, 45, 48);
                form.ForeColor = Color.White;
            }
            else
            {
                form.BackColor = Color.WhiteSmoke;
                form.ForeColor = Color.Black;
            }
        }
    }

    public class AudioMappingForm : Form
    {
        private Label lblHotkey;
        private KeyBindTextBox txtHotkey;
        private GroupBox grpMappingType;
        private RadioButton rbtnAudioDevice, rbtnApplication, rbtnMaster;
        private Panel pnlAudioDevice, pnlApplication;
        private ComboBox cmbAudioSource;
        private TextBox txtAppName;
        private Button btnBrowse;
        private Button btnOK, btnCancel;
        private MMDeviceEnumerator enumerator;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public AudioHotkeyMapping Mapping { get; private set; }

        public AudioMappingForm()
        {
            this.Text = "Add Audio Mapping";
            this.ClientSize = new Size(400, 350);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            ApplyTheme(this);
            this.Font = new Font("Segoe UI", 10);

            // Hotkey input.
            lblHotkey = new Label { Text = "Hotkey:", Location = new Point(20, 20), AutoSize = true };
            this.Controls.Add(lblHotkey);
            txtHotkey = new KeyBindTextBox { Location = new Point(100, 18), Width = 180, ReadOnly = true };
            this.Controls.Add(txtHotkey);
            Button btnClear = new Button { Text = "Clear", Location = new Point(290, 16), AutoSize = false, Width = 80, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            btnClear.Click += (s, e) => txtHotkey.ClearKeys();
            this.Controls.Add(btnClear);

            // Mapping type group with three options.
            grpMappingType = new GroupBox { Text = "Mapping Type", Location = new Point(20, 60), Size = new Size(350, 120) };
            rbtnAudioDevice = new RadioButton { Text = "Audio Device", Location = new Point(20, 30), AutoSize = true, Checked = true };
            rbtnApplication = new RadioButton { Text = "Application", Location = new Point(20, 60), AutoSize = true };
            rbtnMaster = new RadioButton { Text = "Master Volume", Location = new Point(20, 90), AutoSize = true };
            rbtnAudioDevice.CheckedChanged += (s, e) => TogglePanels();
            rbtnApplication.CheckedChanged += (s, e) => TogglePanels();
            rbtnMaster.CheckedChanged += (s, e) => TogglePanels();
            grpMappingType.Controls.Add(rbtnAudioDevice);
            grpMappingType.Controls.Add(rbtnApplication);
            grpMappingType.Controls.Add(rbtnMaster);
            this.Controls.Add(grpMappingType);

            // Panel for Audio Device mapping.
            pnlAudioDevice = new Panel { Location = new Point(20, 190), Size = new Size(350, 50) };
            Label lblAudio = new Label { Text = "Audio Device:", Location = new Point(0, 15), AutoSize = true };
            pnlAudioDevice.Controls.Add(lblAudio);
            cmbAudioSource = new ComboBox { Location = new Point(120, 12), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            pnlAudioDevice.Controls.Add(cmbAudioSource);
            enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                cmbAudioSource.Items.Add(new DeviceItem(device.FriendlyName, device.ID));
            if (cmbAudioSource.Items.Count > 0)
                cmbAudioSource.SelectedIndex = 0;
            this.Controls.Add(pnlAudioDevice);

            // Panel for Application mapping.
            pnlApplication = new Panel { Location = new Point(20, 250), Size = new Size(350, 50), Visible = false };
            Label lblApp = new Label { Text = "Application:", Location = new Point(0, 15), AutoSize = true };
            pnlApplication.Controls.Add(lblApp);
            txtAppName = new TextBox { Location = new Point(120, 12), Width = 150 };
            pnlApplication.Controls.Add(txtAppName);
            btnBrowse = new Button { Text = "Browse...", Location = new Point(280, 10), AutoSize = false, Width = 80, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            btnBrowse.Click += (s, e) =>
            {
                using (var procForm = new ProcessPickerForm())
                {
                    if (procForm.ShowDialog() == DialogResult.OK)
                        txtAppName.Text = procForm.SelectedProcessName;
                }
            };
            pnlApplication.Controls.Add(btnBrowse);
            this.Controls.Add(pnlApplication);

            btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(100, 310), AutoSize = true, MinimumSize = new Size(80, 30), FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            btnOK.Click += BtnOK_Click;
            this.Controls.Add(btnOK);
            btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(200, 310), AutoSize = true, MinimumSize = new Size(80, 30), FlatStyle = FlatStyle.Flat, BackColor = GetThemeButtonColor() };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }

        private void TogglePanels()
        {
            pnlAudioDevice.Visible = rbtnAudioDevice.Checked;
            pnlApplication.Visible = rbtnApplication.Checked;
            // For master volume, no panel is shown.
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            if (txtHotkey.CapturedKeys.Count == 0)
            {
                MessageBox.Show("Please enter a hotkey combination.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.DialogResult = DialogResult.None;
                return;
            }
            Mapping = new AudioHotkeyMapping { KeyCombo = new List<Keys>(txtHotkey.CapturedKeys) };
            if (rbtnMaster.Checked)
            {
                Mapping.IsMasterVolume = true;
                Mapping.AudioDeviceId = "MASTER";
                Mapping.AudioDeviceName = "Master Volume";
                Mapping.ApplicationName = null;
            }
            else if (rbtnAudioDevice.Checked)
            {
                if (cmbAudioSource.SelectedItem is DeviceItem selected)
                {
                    Mapping.AudioDeviceId = selected.DeviceId;
                    Mapping.AudioDeviceName = selected.Name;
                    Mapping.ApplicationName = null;
                }
                else
                {
                    MessageBox.Show("Please select an audio device.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    this.DialogResult = DialogResult.None;
                    return;
                }
            }
            else if (rbtnApplication.Checked)
            {
                if (string.IsNullOrWhiteSpace(txtAppName.Text))
                {
                    MessageBox.Show("Please enter an application name.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                Mapping.ApplicationName = txtAppName.Text.Trim();
                Mapping.AudioDeviceId = null;
                Mapping.AudioDeviceName = null;
            }
            this.Close();
        }

        private Color GetThemeButtonColor()
        {
            return SettingsManager.ThemeMode == "Dark" ? Color.DimGray : Color.LightSteelBlue;
        }

        private void ApplyTheme(Form form)
        {
            if (SettingsManager.ThemeMode == "Dark")
            {
                form.BackColor = Color.FromArgb(45, 45, 48);
                form.ForeColor = Color.White;
            }
            else
            {
                form.BackColor = Color.WhiteSmoke;
                form.ForeColor = Color.Black;
            }
        }

        private class DeviceItem
        {
            public string Name { get; private set; }
            public string DeviceId { get; private set; }
            public DeviceItem(string name, string id)
            {
                Name = name;
                DeviceId = id;
            }
            public override string ToString() { return Name; }
        }
    }

    public class ProcessPickerForm : Form
    {
        private ListBox lstProcesses;
        private Button btnOK, btnCancel;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string SelectedProcessName { get; private set; }
        public ProcessPickerForm()
        {
            this.Text = "Select Application";
            this.ClientSize = new Size(300, 400);
            ApplyTheme(this);
            lstProcesses = new ListBox { Location = new Point(10, 10), Size = new Size(280, 300) };
            this.Controls.Add(lstProcesses);
            foreach (var proc in Process.GetProcesses().OrderBy(p => p.ProcessName))
            {
                try { if (!string.IsNullOrEmpty(proc.MainWindowTitle)) lstProcesses.Items.Add(proc.ProcessName); }
                catch { }
            }
            btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(50, 320), AutoSize = true, MinimumSize = new Size(70, 30) };
            btnOK.Click += (s, e) =>
            {
                if (lstProcesses.SelectedItem != null)
                    SelectedProcessName = lstProcesses.SelectedItem.ToString();
                else
                    this.DialogResult = DialogResult.None;
            };
            this.Controls.Add(btnOK);
            btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(150, 320), AutoSize = true, MinimumSize = new Size(70, 30) };
            this.Controls.Add(btnCancel);
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }

        private void ApplyTheme(Form form)
        {
            if (SettingsManager.ThemeMode == "Dark")
            {
                form.BackColor = Color.FromArgb(45, 45, 48);
                form.ForeColor = Color.White;
            }
            else
            {
                form.BackColor = Color.WhiteSmoke;
                form.ForeColor = Color.Black;
            }
        }
    }

    public class AudioHotkeyMapping
    {
        public List<Keys> KeyCombo { get; set; }
        public string AudioDeviceId { get; set; }
        public string AudioDeviceName { get; set; }
        public string ApplicationName { get; set; }
        public bool IsMasterVolume { get; set; }
        public override string ToString()
        {
            string hotkey = string.Join(" + ", KeyCombo);
            if (IsMasterVolume)
                return $"{hotkey} => Master Volume";
            else if (!string.IsNullOrEmpty(ApplicationName))
                return $"{hotkey} => {ApplicationName}";
            else if (!string.IsNullOrEmpty(AudioDeviceName))
                return $"{hotkey} => {AudioDeviceName}";
            else
                return hotkey;
        }
    }

    public static class SettingsManager
    {
        private static readonly string configFile = Path.Combine(Application.UserAppDataPath, "settings.config");
        public static bool StartWithWindows { get; set; } = false;
        public static List<AudioHotkeyMapping> AudioMappings { get; set; } = new List<AudioHotkeyMapping>();
        public static string ThemeMode { get; set; } = "Light"; // "Light" or "Dark"

        public static void SaveSettings(float volumeStep)
        {
            Directory.CreateDirectory(Application.UserAppDataPath);
            using (StreamWriter sw = new StreamWriter(configFile))
            {
                sw.WriteLine(volumeStep.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sw.WriteLine("ControlKey");
                sw.WriteLine(StartWithWindows.ToString());
                sw.WriteLine(ThemeMode);
                sw.WriteLine("MAPPINGS:");
                foreach (var mapping in AudioMappings)
                {
                    string keys = string.Join(",", mapping.KeyCombo.Select(k => k.ToString()));
                    string deviceId = mapping.AudioDeviceId ?? "";
                    string deviceName = mapping.AudioDeviceName ?? "";
                    string app = mapping.ApplicationName ?? "";
                    sw.WriteLine($"{keys}|{deviceId}|{deviceName}|{app}");
                }
            }
        }

        public static void LoadSettings(out float volumeStep)
        {
            volumeStep = 0.05f;
            AudioMappings.Clear();
            StartWithWindows = false;
            ThemeMode = "Light";
            if (File.Exists(configFile))
            {
                try
                {
                    string[] lines = File.ReadAllLines(configFile);
                    if (lines.Length >= 5)
                    {
                        if (float.TryParse(lines[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vol))
                            volumeStep = vol;
                        if (bool.TryParse(lines[2], out bool startup))
                            StartWithWindows = startup;
                        ThemeMode = lines[3];
                        int mappingStart = Array.IndexOf(lines, "MAPPINGS:");
                        if (mappingStart >= 0 && mappingStart < lines.Length - 1)
                        {
                            for (int i = mappingStart + 1; i < lines.Length; i++)
                            {
                                string line = lines[i].Trim();
                                if (string.IsNullOrEmpty(line))
                                    continue;
                                string[] parts = line.Split('|');
                                if (parts.Length >= 4)
                                {
                                    var keyStrings = parts[0].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    List<Keys> mappingKeys = new List<Keys>();
                                    foreach (var ks in keyStrings)
                                        if (Enum.TryParse(ks, out Keys key))
                                            mappingKeys.Add(key);
                                    string deviceId = parts[1];
                                    string deviceName = parts[2];
                                    string appName = parts[3];
                                    AudioHotkeyMapping mapping = new AudioHotkeyMapping { KeyCombo = mappingKeys };
                                    if (deviceId == "MASTER")
                                    {
                                        mapping.IsMasterVolume = true;
                                        mapping.AudioDeviceId = "MASTER";
                                        mapping.AudioDeviceName = "Master Volume";
                                        mapping.ApplicationName = null;
                                    }
                                    else
                                    {
                                        mapping.AudioDeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
                                        mapping.AudioDeviceName = string.IsNullOrEmpty(deviceName) ? null : deviceName;
                                        mapping.ApplicationName = string.IsNullOrEmpty(appName) ? null : appName;
                                    }
                                    AudioMappings.Add(mapping);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }

    public static class StartupManager
    {
        private const string registryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string appName = "VolumeControlTrayApp";
        public static void SetStartup(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryKeyPath, true))
            {
                if (enable)
                    key.SetValue(appName, Application.ExecutablePath);
                else
                    key.DeleteValue(appName, false);
            }
        }
    }
}
