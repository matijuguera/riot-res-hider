using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace LolModoCine
{
    /// <summary>La ventana de configuracion y el icono de la bandeja. Cada cambio se aplica y se guarda al instante.</summary>
    sealed class MainForm : Form
    {
        public static readonly uint WM_SHOWME = Native.RegisterWindowMessageW("LolModoCine.Show");
        static readonly uint WM_TASKBARCREATED = Native.RegisterWindowMessageW("TaskbarCreated");

        readonly Config cfg = Config.Load();
        readonly Engine engine;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem pauseItem;
        readonly Size native = Screen.PrimaryScreen.Bounds.Size;

        bool startHidden;
        bool exiting;
        bool loading = true;

        CheckBox chkTaskbar, chkIcons, chkBlack, chkLol, chkTft, chkPinTft, chkAutostart, chkPaused;
        TextBox txtLol;
        NumericUpDown numMonitor, numTarget, numW, numH;
        ComboBox cmbMode;
        Label lblStatus;

        public MainForm(bool startInTray)
        {
            startHidden = startInTray;

            if (cfg.LolPath.Length == 0) cfg.LolPath = LolResolution.FindInstall();
            if (cfg.Width <= 0 || cfg.Height <= 0)
            {
                Size size = LolResolution.Scaled(native, cfg.MonitorInches, cfg.TargetInches);
                cfg.Width = size.Width;
                cfg.Height = size.Height;
            }

            BuildUi();
            LoadIntoUi();
            loading = false;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Abrir", null, (s, e) => ShowMe());
            pauseItem = new ToolStripMenuItem("Pausar");
            pauseItem.Click += (s, e) => chkPaused.Checked = !chkPaused.Checked;
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir", null, (s, e) => { exiting = true; Close(); });

            tray = new NotifyIcon
            {
                Icon = LoadIcon(SystemInformation.SmallIconSize),
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMe(); };

            cfg.Save();
            Autostart.Set(cfg.Autostart, Application.ExecutablePath);

            engine = new Engine(cfg);
            engine.StateChanged += UpdateStatus;
            UpdateStatus();
        }

        // ---------------------------------------------------------------- interfaz

        void BuildUi()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = Program.AppName;
            Icon = LoadIcon(SystemInformation.IconSize);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 542);

            GroupBox hide = Group("Mientras jugás, ocultar", 10, 104);
            chkTaskbar = Check(hide, "Barra de tareas", 14, 24);
            chkIcons = Check(hide, "Íconos del escritorio", 14, 48);
            chkBlack = Check(hide, "Fondo de pantalla y ventanas de atrás (fondo negro)", 14, 72);

            GroupBox games = Group("Juegos", 122, 56);
            chkLol = Check(games, "League of Legends", 14, 24);
            chkTft = Check(games, "Teamfight Tactics", 200, 24);

            GroupBox small = Group("Jugar como en un monitor más chico", 186, 226);
            Caption(small,"Tu monitor:", 14, 28);
            numMonitor = Number(small, 90, 25, 60, 10, 100, 1, 0.5m);
            Caption(small,"\"", 152, 28);
            Caption(small,"Emular:", 196, 28);
            numTarget = Number(small, 250, 25, 60, 10, 100, 1, 0.5m);
            Caption(small,"\"", 312, 28);
            numMonitor.ValueChanged += OnInchesChanged;
            numTarget.ValueChanged += OnInchesChanged;

            Caption(small,"Tamaño:", 14, 59);
            numW = Number(small, 90, 56, 70, 320, native.Width, 0, 2);
            Caption(small,"x", 166, 59);
            numH = Number(small, 182, 56, 70, 200, native.Height, 0, 2);
            Caption(small,"de " + native.Width + " x " + native.Height, 262, 59).ForeColor = SystemColors.GrayText;
            numW.ValueChanged += OnChanged;
            numH.ValueChanged += OnChanged;

            Caption(small,"Carpeta de League of Legends:", 14, 90);
            txtLol = new TextBox { Location = new Point(14, 109), Width = 338, Parent = small };
            txtLol.TextChanged += OnChanged;
            var browse = new Button { Text = "...", Location = new Point(358, 108), Size = new Size(44, 25), Parent = small };
            browse.Click += OnBrowse;

            Caption(small,"Modo:", 14, 146);
            cmbMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(90, 143),
                Width = 130,
                Parent = small,
            };
            cmbMode.Items.AddRange(new object[] { "No cambiar", "Ventana", "Sin bordes" });
            cmbMode.SelectedIndexChanged += OnChanged;
            var apply = new Button { Text = "Aplicar a LoL", Location = new Point(252, 141), Size = new Size(150, 28), Parent = small };
            apply.Click += OnApplyLol;

            chkPinTft = Check(small, "Fijar la ventana de TFT a ese tamaño cada vez que lo abrís", 14, 184);
            Caption(small, "TFT ignora la resolución de su config: se le acomoda la ventana.", 31, 204)
                .ForeColor = SystemColors.GrayText;

            GroupBox general = Group("General", 420, 78);
            chkAutostart = Check(general, "Iniciar con Windows (queda en la bandeja)", 14, 24);
            chkPaused = Check(general, "Pausar el modo cine", 14, 48);

            lblStatus = Caption(this, "", 12, 512);
            lblStatus.Font = new Font(Font, FontStyle.Bold);

            ResumeLayout(false);
            PerformLayout();
        }

        GroupBox Group(string title, int y, int height)
        {
            return new GroupBox { Text = title, Location = new Point(12, y), Size = new Size(416, height), Parent = this };
        }

        CheckBox Check(Control parent, string text, int x, int y)
        {
            var box = new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true, Parent = parent };
            box.CheckedChanged += OnChanged;
            return box;
        }

        static Label Caption(Control parent, string text, int x, int y)
        {
            return new Label { Text = text, Location = new Point(x, y), AutoSize = true, Parent = parent };
        }

        static NumericUpDown Number(Control parent, int x, int y, int width, decimal min, decimal max,
            int decimals, decimal step)
        {
            return new NumericUpDown
            {
                Location = new Point(x, y),
                Width = width,
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = step,
                Parent = parent,
            };
        }

        static Icon LoadIcon(Size size)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                return new Icon(stream, size);
        }

        static decimal Clamp(NumericUpDown box, decimal value)
        {
            return Math.Max(box.Minimum, Math.Min(box.Maximum, value));
        }

        // ---------------------------------------------------------------- config <-> interfaz

        void LoadIntoUi()
        {
            chkTaskbar.Checked = cfg.HideTaskbar;
            chkIcons.Checked = cfg.HideIcons;
            chkBlack.Checked = cfg.BlackBackground;
            chkLol.Checked = cfg.GameLol;
            chkTft.Checked = cfg.GameTft;
            numMonitor.Value = Clamp(numMonitor, (decimal)cfg.MonitorInches);
            numTarget.Value = Clamp(numTarget, (decimal)cfg.TargetInches);
            numW.Value = Clamp(numW, cfg.Width);
            numH.Value = Clamp(numH, cfg.Height);
            txtLol.Text = cfg.LolPath;
            cmbMode.SelectedIndex = cfg.LolMode;
            chkPinTft.Checked = cfg.PinTft;
            chkAutostart.Checked = cfg.Autostart;
            chkPaused.Checked = cfg.Paused;
        }

        void OnInchesChanged(object sender, EventArgs e)
        {
            if (loading) return;
            Size size = LolResolution.Scaled(native, (double)numMonitor.Value, (double)numTarget.Value);
            loading = true;  // un solo guardado, no uno por cada campo
            numW.Value = Clamp(numW, size.Width);
            numH.Value = Clamp(numH, size.Height);
            loading = false;
            OnChanged(sender, e);
        }

        void OnChanged(object sender, EventArgs e)
        {
            if (loading) return;

            cfg.HideTaskbar = chkTaskbar.Checked;
            cfg.HideIcons = chkIcons.Checked;
            cfg.BlackBackground = chkBlack.Checked;
            cfg.GameLol = chkLol.Checked;
            cfg.GameTft = chkTft.Checked;
            cfg.MonitorInches = (double)numMonitor.Value;
            cfg.TargetInches = (double)numTarget.Value;
            cfg.Width = (int)numW.Value;
            cfg.Height = (int)numH.Value;
            cfg.LolPath = txtLol.Text.Trim();
            cfg.LolMode = Math.Max(0, cmbMode.SelectedIndex);
            cfg.PinTft = chkPinTft.Checked;
            cfg.Paused = chkPaused.Checked;

            cfg.Autostart = chkAutostart.Checked;
            if (sender == chkAutostart) Autostart.Set(cfg.Autostart, Application.ExecutablePath);

            cfg.Save();
            engine.Refresh();
            UpdateStatus();
        }

        void UpdateStatus()
        {
            string state = cfg.Paused ? "en pausa"
                : engine != null && engine.Active ? "modo cine activado"
                : "esperando que abras un juego";
            lblStatus.Text = "Estado: " + state;
            if (tray != null) tray.Text = Program.AppName + " - " + state;
            if (pauseItem != null) pauseItem.Checked = cfg.Paused;
        }

        // ---------------------------------------------------------------- monitor mas chico

        void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Carpeta donde está instalado League of Legends";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(txtLol.Text)) dialog.SelectedPath = txtLol.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) txtLol.Text = dialog.SelectedPath;
            }
        }

        void OnApplyLol(object sender, EventArgs e)
        {
            string config = LolResolution.FindConfig(cfg.LolPath);
            if (config == null)
            {
                MessageBox.Show(this,
                    "No encontré el game.cfg en esa carpeta.\n\n" +
                    "Tiene que ser la carpeta de instalación, por ejemplo C:\\Riot Games\\League of Legends " +
                    "(adentro tiene que existir Config\\game.cfg).",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<string> running = LolResolution.RunningProcesses();
            if (running.Count > 0)
            {
                DialogResult answer = MessageBox.Show(this,
                    "League of Legends está abierto (" + string.Join(", ", running) + ").\n\n" +
                    "Al cerrarse reescribe su configuración y puede deshacer el cambio. " +
                    "Lo mejor es cerrar el cliente y volver a aplicar.\n\n¿Escribir igual?",
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }

            LolResolution.Result result;
            try
            {
                result = LolResolution.Apply(config, cfg.Width, cfg.Height, cfg.LolMode);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No pude escribir " + config + ":\n\n" + ex.Message,
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string message = result.Changes.Count == 0
                ? "League of Legends ya estaba en " + cfg.Width + " x " + cfg.Height + ". No cambié nada."
                : "Listo. Cambios en " + config + ":\n\n    " + string.Join("\n    ", result.Changes) +
                  "\n\nEl original quedó guardado como game.cfg.bak.";
            if (result.Fullscreen)
            {
                message += "\n\nOjo: LoL está en pantalla completa, donde una resolución más chica se " +
                           "estira a todo el monitor. Elegí \"Ventana\" en Modo y aplicá de nuevo.";
            }
            MessageBox.Show(this, message, Program.AppName, MessageBoxButtons.OK,
                result.Fullscreen ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        // ---------------------------------------------------------------- ventana y bandeja

        void ShowMe()
        {
            startHidden = false;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                // Arranque con Windows: directo a la bandeja. Igual hace falta el handle, para
                // recibir los mensajes de "mostrate" y de reinicio de Explorer.
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == WM_SHOWME)
            {
                ShowMe();
                return;
            }
            if ((uint)m.Msg == WM_TASKBARCREATED && engine != null)
                engine.ExplorerRestarted();  // el icono de la bandeja lo repone NotifyIcon solo
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // La X manda a la bandeja; salir de verdad es desde el menu del icono.
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!cfg.TrayHintShown)
                {
                    cfg.TrayHintShown = true;
                    cfg.Save();
                    tray.ShowBalloonTip(4000, Program.AppName,
                        "Sigue corriendo acá, en la bandeja. Clic derecho en el ícono → Salir para cerrarlo.",
                        ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Tambien pasa al apagar o cerrar sesion: la barra y los iconos vuelven siempre.
            engine.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }
    }
}
