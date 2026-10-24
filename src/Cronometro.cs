// Cronômetro Glass — cronômetro leve que sobrevive a desligamentos inesperados.
// O tempo é gravado no disco (write-through) a cada meio segundo enquanto roda,
// alternando entre dois slots com CRC: se a energia cair no meio de uma gravação,
// o outro slot continua íntegro. Ao reabrir, o cronômetro volta PAUSADO no último tempo salvo.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Stopwatch")]
[assembly: System.Reflection.AssemblyProduct("Stopwatch Liquid Glass")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]

namespace CronometroGlass
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        static int Main()
        {
            bool created;
            var mutex = new Mutex(true, "Local\\CronometroGlass.Instance", out created);
            var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CronometroGlass.Show");
            if (!created)
            {
                // Já está aberto: só traz a janela existente para frente (e deixa ela tomar o foco).
                AllowSetForegroundWindow(-1 /* ASFW_ANY */);
                showSignal.Set();
                return 0;
            }

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var controller = new TimerController(app, showSignal);
            app.Run(controller.Window);
            GC.KeepAlive(mutex);
            return 0;
        }
    }

    /// <summary>
    /// Arquivo de estado com dois slots de 64 bytes, gravados alternadamente com
    /// FileOptions.WriteThrough + Flush(true). Cada slot tem número de sequência e CRC32;
    /// na leitura vale o slot válido com a maior sequência.
    /// </summary>
    sealed class StateStore : IDisposable
    {
        const int Magic = 0x31544743; // "CGT1"
        const int SlotSize = 64;
        const int PayloadSize = 32;

        readonly FileStream fs;
        long seq;
        int nextSlot;

        public bool HasState;
        public long LoadedTicks;
        public bool LoadedRunning;
        public DateTime LoadedSavedUtc;

        public StateStore(string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);

            var buf = new byte[SlotSize * 2];
            int total = 0, n;
            fs.Position = 0;
            while (total < buf.Length && (n = fs.Read(buf, total, buf.Length - total)) > 0) total += n;

            int bestSlot = -1;
            long bestSeq = -1;
            for (int s = 0; s < 2; s++)
            {
                int off = s * SlotSize;
                if (total < off + PayloadSize + 4) break;
                if (BitConverter.ToInt32(buf, off) != Magic) continue;
                if (BitConverter.ToUInt32(buf, off + PayloadSize) != Crc32(buf, off, PayloadSize)) continue;
                long sq = BitConverter.ToInt64(buf, off + 4);
                if (sq <= bestSeq) continue;
                bestSeq = sq;
                bestSlot = s;
            }

            if (bestSlot >= 0)
            {
                int off = bestSlot * SlotSize;
                HasState = true;
                seq = bestSeq;
                LoadedTicks = Math.Max(0, BitConverter.ToInt64(buf, off + 12));
                LoadedRunning = buf[off + 20] != 0;
                LoadedSavedUtc = new DateTime(BitConverter.ToInt64(buf, off + 24), DateTimeKind.Utc);
                nextSlot = 1 - bestSlot;
            }
        }

        public void Save(long elapsedTicks, bool running)
        {
            seq++;
            var b = new byte[SlotSize];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, b, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, b, 4, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(elapsedTicks), 0, b, 12, 8);
            b[20] = (byte)(running ? 1 : 0);
            Buffer.BlockCopy(BitConverter.GetBytes(DateTime.UtcNow.Ticks), 0, b, 24, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(Crc32(b, 0, PayloadSize)), 0, b, PayloadSize, 4);

            fs.Position = nextSlot * SlotSize;
            fs.Write(b, 0, SlotSize);
            fs.Flush(true);
            nextSlot = 1 - nextSlot;
        }

        public void Dispose()
        {
            fs.Dispose();
        }

        static uint[] crcTable;

        static uint Crc32(byte[] data, int offset, int count)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++) crc = crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }

    /// <summary>Textos da interface em cada idioma.</summary>
    sealed class Lang
    {
        public string Code, Title, Start, Pause, Resume, Reset, Confirm;
        public string StatusReady, StatusRunning, StatusPaused, StatusRecovered, StatusSaveFailed;
        public string OpenWithWindows, OpenWithWindowsTip, Minimize, Maximize, Restore, Close, Settings, Language;
        public string DecimalSep, RecoveredFormat;
        public CultureInfo Culture;

        public static readonly Lang Pt = new Lang
        {
            Code = "pt", Title = "Cronômetro",
            Start = "Iniciar", Pause = "Pausar", Resume = "Continuar", Reset = "Zerar", Confirm = "Confirmar",
            StatusReady = "Pronto · espaço inicia/pausa", StatusRunning = "Rodando · salvando no disco",
            StatusPaused = "Pausado · tempo salvo", StatusRecovered = "Recuperado · ",
            StatusSaveFailed = "Não foi possível salvar no disco",
            OpenWithWindows = "Abrir com o Windows", OpenWithWindowsTip = "Abrir automaticamente quando o PC ligar",
            Minimize = "Minimizar", Maximize = "Maximizar", Restore = "Restaurar tamanho",
            Close = "Fechar (pausa e salva)", Settings = "Configurações", Language = "Idioma",
            DecimalSep = ",", RecoveredFormat = "dd/MM HH:mm", Culture = new CultureInfo("pt-BR")
        };

        public static readonly Lang En = new Lang
        {
            Code = "en", Title = "Stopwatch",
            Start = "Start", Pause = "Pause", Resume = "Resume", Reset = "Reset", Confirm = "Confirm",
            StatusReady = "Ready · space starts/pauses", StatusRunning = "Running · saving to disk",
            StatusPaused = "Paused · time saved", StatusRecovered = "Recovered · ",
            StatusSaveFailed = "Couldn't save to disk",
            OpenWithWindows = "Open with Windows", OpenWithWindowsTip = "Open automatically when the PC starts",
            Minimize = "Minimize", Maximize = "Maximize", Restore = "Restore size",
            Close = "Close (pauses and saves)", Settings = "Settings", Language = "Language",
            DecimalSep = ".", RecoveredFormat = "MMM d, HH:mm", Culture = new CultureInfo("en-US")
        };

        public static Lang FromCode(string code)
        {
            return code == "pt" ? Pt : En;
        }

        /// <summary>Idioma do Windows na primeira execução: português se for pt-*, senão inglês.</summary>
        public static Lang FromSystem()
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? Pt : En;
        }
    }

    sealed class TimerController
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "CronometroGlass";
        const double Radius = 26;

        static readonly Brush StartTint = Hex("#5230D158");
        static readonly Brush PauseTint = Hex("#66FFB340");
        static readonly Brush ResetTint = Hex("#1CFFFFFF");
        static readonly Brush ConfirmTint = Hex("#60FF453A");
        static readonly Brush DotGreen = Hex("#FF30D158");
        static readonly Brush DotAmber = Hex("#FFFFD60A");
        static readonly Brush DotIdle = Hex("#80FFFFFF");
        static readonly Brush DotRed = Hex("#FFFF453A");

        public readonly Window Window;

        readonly string dataDir;
        readonly StateStore store;
        readonly Stopwatch sw = new Stopwatch();
        readonly DispatcherTimer uiTimer;
        readonly DispatcherTimer saveTimer;
        readonly DispatcherTimer disarmTimer;

        long baseTicks;
        bool running;
        bool recovered;
        bool resetArmed;
        bool saveFailed;
        DateTime recoveredAt;
        bool startMaximized;
        readonly DispatcherTimer settingsTimer;
        Lang L;
        DateTime popupClosedAt;

        readonly TextBlock timeMain, timeFrac, statusText, startGlyph, startText, resetText, autoGlyph;
        readonly TextBlock titleText, autoText, langHeader, checkPt, checkEn;
        readonly Ellipse statusDot;
        readonly Button startBtn, resetBtn, minBtn, maxBtn, closeBtn, autoBtn, gearBtn;
        readonly System.Windows.Controls.Primitives.Popup langPopup;
        readonly Border glassBody, sheen, rim;
        readonly FrameworkElement root, timeBox;

        public TimerController(Application app, EventWaitHandle showSignal)
        {
            dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CronometroGlass");
            store = new StateStore(System.IO.Path.Combine(dataDir, "estado.bin"));

            Window = (Window)XamlReader.Parse(Xaml);
            timeMain = Find<TextBlock>("TimeMain");
            timeFrac = Find<TextBlock>("TimeFrac");
            statusText = Find<TextBlock>("StatusText");
            statusDot = Find<Ellipse>("StatusDot");
            startBtn = Find<Button>("StartBtn");
            startGlyph = Find<TextBlock>("StartGlyph");
            startText = Find<TextBlock>("StartText");
            resetBtn = Find<Button>("ResetBtn");
            resetText = Find<TextBlock>("ResetText");
            minBtn = Find<Button>("MinBtn");
            maxBtn = Find<Button>("MaxBtn");
            closeBtn = Find<Button>("CloseBtn");
            autoBtn = Find<Button>("AutoBtn");
            autoGlyph = Find<TextBlock>("AutoGlyph");
            autoText = Find<TextBlock>("AutoText");
            titleText = Find<TextBlock>("TitleText");
            gearBtn = Find<Button>("GearBtn");
            langPopup = Find<System.Windows.Controls.Primitives.Popup>("LangPopup");
            langHeader = Find<TextBlock>("LangHeader");
            checkPt = Find<TextBlock>("CheckPt");
            checkEn = Find<TextBlock>("CheckEn");
            glassBody = Find<Border>("GlassBody");
            sheen = Find<Border>("Sheen");
            rim = Find<Border>("Rim");
            root = Find<FrameworkElement>("Root");
            timeBox = Find<FrameworkElement>("TimeBox");

            if (store.HasState)
            {
                baseTicks = store.LoadedTicks;
                // Estava rodando quando foi salvo pela última vez = o PC/app caiu sem fechar.
                recovered = store.LoadedRunning;
                recoveredAt = store.LoadedSavedUtc.ToLocalTime();
                if (recovered) TrySave(baseTicks, false);
            }

            uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
            uiTimer.Tick += delegate { Render(); };
            saveTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
            saveTimer.Tick += delegate { TrySave(Elapsed, true); };
            disarmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            disarmTimer.Tick += delegate { DisarmReset(); };
            settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            settingsTimer.Tick += delegate { settingsTimer.Stop(); SaveSettings(); };

            // Comandos de sistema (SC_MINIMIZE/SC_MAXIMIZE/SC_RESTORE): o Windows anima a janela como qualquer outra.
            startBtn.Click += delegate { Toggle(); };
            resetBtn.Click += delegate { ResetClicked(); };
            closeBtn.Click += delegate { Window.Close(); };
            minBtn.Click += delegate { SystemCommands.MinimizeWindow(Window); };

            // Engrenagem: menu de idioma. O Popup fecha sozinho ao clicar fora; o intervalo evita que o
            // mesmo clique na engrenagem que o fechou o abra de novo.
            langPopup.PlacementTarget = gearBtn;
            langPopup.Closed += delegate { popupClosedAt = DateTime.UtcNow; };
            gearBtn.Click += delegate
            {
                if ((DateTime.UtcNow - popupClosedAt).TotalMilliseconds < 250) return;
                langPopup.IsOpen = !langPopup.IsOpen;
            };
            Find<Button>("LangPt").Click += delegate { SetLanguage(Lang.Pt); };
            Find<Button>("LangEn").Click += delegate { SetLanguage(Lang.En); };
            maxBtn.Click += delegate { ToggleMaximize(); };
            autoBtn.Click += delegate { ToggleAutostart(); };

            // A janela inteira é "barra de título" (WindowChrome): arrastar, duplo clique para maximizar
            // e encaixe nas bordas da tela são do próprio Windows.
            Window.SizeChanged += delegate { ApplyShape(); };
            Window.StateChanged += delegate { ApplyShape(); UpdateChrome(); settingsTimer.Start(); };
            Window.LocationChanged += delegate { settingsTimer.Stop(); settingsTimer.Start(); };
            Window.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Space) { Toggle(); e.Handled = true; }
            };

            Window.SourceInitialized += delegate { ApplyGlass(); };
            Window.Closing += delegate
            {
                if (running) Pause();
                SaveSettings();
            };
            Window.Closed += delegate
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                store.Dispose();
            };

            // Desligamento/logoff normal do Windows: pausa e grava antes de morrer.
            app.SessionEnding += delegate { if (running) Pause(); };
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            var dispatcher = Window.Dispatcher;
            var listener = new Thread(delegate()
            {
                while (showSignal.WaitOne()) dispatcher.BeginInvoke(new Action(BringToFront));
            });
            listener.IsBackground = true;
            listener.Start();

            LoadSettings();
            if (startMaximized) Window.WindowState = WindowState.Maximized;
            if (L == null)
            {
                // Primeira vez: segue o idioma do Windows e grava a escolha.
                L = Lang.FromSystem();
                SaveSettings();
            }
            RefreshAutostartPath();
            ApplyLanguage();
        }

        void SetLanguage(Lang lang)
        {
            langPopup.IsOpen = false;
            if (lang == L) return;
            L = lang;
            ApplyLanguage();
            SaveSettings();
        }

        void ApplyLanguage()
        {
            Window.Title = L.Title;
            titleText.Text = L.Title;
            autoText.Text = L.OpenWithWindows;
            autoBtn.ToolTip = L.OpenWithWindowsTip;
            minBtn.ToolTip = L.Minimize;
            closeBtn.ToolTip = L.Close;
            gearBtn.ToolTip = L.Settings;
            langHeader.Text = L.Language;
            checkPt.Visibility = L == Lang.Pt ? Visibility.Visible : Visibility.Hidden;
            checkEn.Visibility = L == Lang.En ? Visibility.Visible : Visibility.Hidden;
            Render();
            UpdateChrome();
        }

        long Elapsed
        {
            get { return baseTicks + (running ? sw.Elapsed.Ticks : 0); }
        }

        bool IsMaximized
        {
            get { return Window.WindowState == WindowState.Maximized; }
        }

        void Toggle()
        {
            if (running) Pause(); else Start();
        }

        void Start()
        {
            DisarmReset();
            recovered = false;
            running = true;
            sw.Restart();
            TrySave(Elapsed, true);
            uiTimer.Start();
            saveTimer.Start();
            UpdateChrome();
        }

        void Pause()
        {
            if (!running) return;
            baseTicks += sw.Elapsed.Ticks;
            sw.Reset();
            running = false;
            uiTimer.Stop();
            saveTimer.Stop();
            TrySave(baseTicks, false);
            Render();
            UpdateChrome();
        }

        void ResetClicked()
        {
            if (Elapsed == 0 && !running) return;
            if (!resetArmed)
            {
                // Zerar pede confirmação para não perder o tempo salvo sem querer.
                resetArmed = true;
                disarmTimer.Start();
                UpdateChrome();
                return;
            }
            DisarmReset();
            if (running) Pause();
            baseTicks = 0;
            recovered = false;
            TrySave(0, false);
            Render();
            UpdateChrome();
        }

        void DisarmReset()
        {
            disarmTimer.Stop();
            if (!resetArmed) return;
            resetArmed = false;
            UpdateChrome();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Suspend) return;
            Window.Dispatcher.Invoke(new Action(Pause));
        }

        void TrySave(long ticks, bool isRunning)
        {
            bool wasFailed = saveFailed;
            try
            {
                store.Save(ticks, isRunning);
                saveFailed = false;
            }
            catch (IOException) { saveFailed = true; }
            catch (UnauthorizedAccessException) { saveFailed = true; }
            if (wasFailed != saveFailed) UpdateChrome();
        }

        void Render()
        {
            if (L == null) return;
            var ts = TimeSpan.FromTicks(Elapsed);
            string main = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (long)ts.TotalHours, ts.Minutes, ts.Seconds);
            string frac = L.DecimalSep + (ts.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture);
            if (timeMain.Text != main) timeMain.Text = main;
            if (timeFrac.Text != frac) timeFrac.Text = frac;
        }

        void UpdateChrome()
        {
            if (L == null) return; // ainda carregando: ApplyLanguage atualiza tudo em seguida
            startGlyph.Text = running ? "" : "";
            startText.Text = running ? L.Pause : (Elapsed > 0 ? L.Resume : L.Start);
            startBtn.Background = running ? PauseTint : StartTint;

            resetText.Text = resetArmed ? L.Confirm : L.Reset;
            resetBtn.Background = resetArmed ? ConfirmTint : ResetTint;
            resetBtn.Opacity = (Elapsed == 0 && !running) ? 0.45 : 1;

            maxBtn.Content = IsMaximized ? "" : "";
            maxBtn.ToolTip = IsMaximized ? L.Restore : L.Maximize;

            autoGlyph.Text = IsAutostart() ? "" : "";
            autoGlyph.Opacity = 1;

            if (saveFailed)
            {
                statusDot.Fill = DotRed;
                statusText.Text = L.StatusSaveFailed;
            }
            else if (running)
            {
                statusDot.Fill = DotGreen;
                statusText.Text = L.StatusRunning;
            }
            else if (recovered)
            {
                statusDot.Fill = DotAmber;
                statusText.Text = L.StatusRecovered + recoveredAt.ToString(L.RecoveredFormat, L.Culture);
            }
            else if (Elapsed > 0)
            {
                statusDot.Fill = DotIdle;
                statusText.Text = L.StatusPaused;
            }
            else
            {
                statusDot.Fill = DotIdle;
                statusText.Text = L.StatusReady;
            }
        }

        void BringToFront()
        {
            // SW_RESTORE é síncrono e volta ao estado anterior (normal ou maximizado), com animação.
            if (Window.WindowState == WindowState.Minimized) ShowWindow(new WindowInteropHelper(Window).Handle, 9);
            Window.Show();
            Window.Activate();
            bool top = Window.Topmost;
            Window.Topmost = true;
            Window.Topmost = top;
        }

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);

        // ---------- Iniciar com o Windows ----------

        static string ExePath
        {
            get { return System.Reflection.Assembly.GetEntryAssembly().Location; }
        }

        static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunValue) != null;
        }

        void ToggleAutostart()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue, false);
                else k.SetValue(RunValue, "\"" + ExePath + "\"");
            }
            UpdateChrome();
        }

        static void RefreshAutostartPath()
        {
            // Se o .exe foi movido de pasta, mantém a entrada de inicialização apontando para cá.
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null || k.GetValue(RunValue) == null) return;
                string want = "\"" + ExePath + "\"";
                if (!want.Equals(k.GetValue(RunValue) as string, StringComparison.OrdinalIgnoreCase)) k.SetValue(RunValue, want);
            }
        }

        // ---------- Maximizar / restaurar ----------

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        void ToggleMaximize()
        {
            if (IsMaximized) SystemCommands.RestoreWindow(Window);
            else SystemCommands.MaximizeWindow(Window);
        }

        // ---------- Posição da janela ----------

        string SettingsPath
        {
            get { return System.IO.Path.Combine(dataDir, "janela.txt"); }
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                double left = double.NaN, top = double.NaN;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq), val = line.Substring(eq + 1);
                    if (key == "left") double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out left);
                    else if (key == "top") double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out top);
                    else if (key == "max") startMaximized = val == "1";
                    else if (key == "lang") L = Lang.FromCode(val);
                }
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (!double.IsNaN(left) && !double.IsNaN(top) && vs.Contains(new Point(left + 40, top + 20)))
                {
                    Window.WindowStartupLocation = WindowStartupLocation.Manual;
                    Window.Left = left;
                    Window.Top = top;
                }
            }
            catch (IOException) { }
        }

        void SaveSettings()
        {
            if (Window.WindowState == WindowState.Minimized) return;
            Rect rb = Window.RestoreBounds;
            bool useRestore = IsMaximized && !rb.IsEmpty;
            double left = useRestore ? rb.Left : Window.Left;
            double top = useRestore ? rb.Top : Window.Top;
            try
            {
                File.WriteAllText(SettingsPath, string.Format(CultureInfo.InvariantCulture,
                    "left={0}\ntop={1}\nmax={2}\nlang={3}\n", left, top, IsMaximized ? 1 : 0, L == null ? "" : L.Code));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---------- Vidro: desfoque do Windows atrás da janela + recorte arredondado ----------

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

        [DllImport("user32.dll")]
        static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

        void ApplyGlass()
        {
            var hwnd = new WindowInteropHelper(Window).Handle;

            var accent = new AccentPolicy { AccentState = 3 }; // ACCENT_ENABLE_BLURBEHIND
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size }; // WCA_ACCENT_POLICY
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            ApplyShape();
        }

        // Cantos arredondados na janela normal, retos quando maximizada.
        void ApplyShape()
        {
            var hwnd = new WindowInteropHelper(Window).Handle;
            var source = PresentationSource.FromVisual(Window);
            if (hwnd == IntPtr.Zero || source == null || Window.WindowState == WindowState.Minimized) return;

            RECT wr;
            if (!GetWindowRect(hwnd, out wr)) return;
            int pw = wr.Right - wr.Left, ph = wr.Bottom - wr.Top;
            var m = source.CompositionTarget.TransformToDevice;
            bool max = IsMaximized;
            double radius = max ? 0 : Radius;

            // Maximizada, o Windows deixa a janela vazar alguns pixels além da tela (a borda invisível);
            // recorta exatamente na área de trabalho do monitor para nada ficar escondido.
            int l = 0, t = 0, r = pw, b = ph;
            if (max)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info))
                {
                    l = Math.Max(0, info.rcWork.Left - wr.Left);
                    t = Math.Max(0, info.rcWork.Top - wr.Top);
                    r = Math.Min(pw, info.rcWork.Right - wr.Left);
                    b = Math.Min(ph, info.rcWork.Bottom - wr.Top);
                }
                SetWindowRgn(hwnd, CreateRectRgn(l, t, r, b), true);
            }
            else
            {
                int d = (int)Math.Round(Radius * 2 * m.M11);
                SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, pw + 1, ph + 1, d, d), true);
            }

            root.Margin = new Thickness(l / m.M11, t / m.M22, (pw - r) / m.M11, (ph - b) / m.M22);
            double w = (r - l) / m.M11, h = (b - t) / m.M22;

            glassBody.CornerRadius = new CornerRadius(radius);
            rim.CornerRadius = new CornerRadius(radius);
            sheen.CornerRadius = new CornerRadius(radius, radius, 80, 80);
            root.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
            timeBox.Margin = max ? new Thickness(w * 0.12, h * 0.08, w * 0.12, h * 0.1) : new Thickness(8, 0, 8, 4);
        }

        T Find<T>(string name) where T : class
        {
            return (T)Window.FindName(name);
        }

        static Brush Hex(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        const string Xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='Cronômetro' Width='380' Height='250'
        Background='Transparent' ResizeMode='CanResize' WindowStartupLocation='CenterScreen'
        FontFamily='Segoe UI' UseLayoutRounding='True'>
  <!-- Moldura nativa escondida: a janela continua sendo uma janela normal do Windows (com as animações
       de minimizar/maximizar/restaurar), mas sem barra de título nem bordas visíveis. -->
  <WindowChrome.WindowChrome>
    <WindowChrome CaptionHeight='10000' ResizeBorderThickness='0' GlassFrameThickness='-1'
                  CornerRadius='0' UseAeroCaptionButtons='False'/>
  </WindowChrome.WindowChrome>
  <Window.Resources>
    <Style x:Key='Pill' TargetType='Button'>
      <Setter Property='Foreground' Value='White'/>
      <Setter Property='FontSize' Value='15'/>
      <Setter Property='Height' Value='46'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='WindowChrome.IsHitTestVisibleInChrome' Value='True'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='RenderTransformOrigin' Value='0.5,0.5'/>
      <Setter Property='RenderTransform'><Setter.Value><ScaleTransform/></Setter.Value></Setter>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Grid>
              <Border CornerRadius='23' Background='{TemplateBinding Background}' BorderThickness='1'>
                <Border.BorderBrush>
                  <LinearGradientBrush StartPoint='0,0' EndPoint='0.3,1'>
                    <GradientStop Color='#A0FFFFFF' Offset='0'/>
                    <GradientStop Color='#14FFFFFF' Offset='0.5'/>
                    <GradientStop Color='#50FFFFFF' Offset='1'/>
                  </LinearGradientBrush>
                </Border.BorderBrush>
              </Border>
              <Border CornerRadius='22,22,12,12' Margin='2,1,2,0' Height='20' VerticalAlignment='Top' IsHitTestVisible='False'>
                <Border.Background>
                  <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
                    <GradientStop Color='#38FFFFFF' Offset='0'/>
                    <GradientStop Color='#00FFFFFF' Offset='1'/>
                  </LinearGradientBrush>
                </Border.Background>
              </Border>
              <Border x:Name='Hover' CornerRadius='23' Background='White' Opacity='0'/>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Trigger.EnterActions>
                  <BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='Hover' Storyboard.TargetProperty='Opacity' To='0.10' Duration='0:0:0.18'/></Storyboard></BeginStoryboard>
                </Trigger.EnterActions>
                <Trigger.ExitActions>
                  <BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='Hover' Storyboard.TargetProperty='Opacity' To='0' Duration='0:0:0.25'/></Storyboard></BeginStoryboard>
                </Trigger.ExitActions>
              </Trigger>
              <Trigger Property='IsPressed' Value='True'>
                <Setter Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='0.96' ScaleY='0.96'/></Setter.Value></Setter>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- ícones da janela: só o glifo em repouso; ao passar o mouse surge uma bolha de vidro (Background = cor da bolha) -->
    <Style x:Key='Chrome' TargetType='Button'>
      <Setter Property='Width' Value='30'/>
      <Setter Property='Height' Value='30'/>
      <Setter Property='Margin' Value='2,0,0,0'/>
      <Setter Property='Foreground' Value='#D0FFFFFF'/>
      <Setter Property='Background' Value='#26FFFFFF'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='WindowChrome.IsHitTestVisibleInChrome' Value='True'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='RenderTransformOrigin' Value='0.5,0.5'/>
      <Setter Property='RenderTransform'><Setter.Value><ScaleTransform/></Setter.Value></Setter>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Grid Background='Transparent'>
              <Border x:Name='Bubble' CornerRadius='15' Background='{TemplateBinding Background}' BorderThickness='1' Opacity='0'>
                <Border.BorderBrush>
                  <LinearGradientBrush StartPoint='0,0' EndPoint='0.3,1'>
                    <GradientStop Color='#90FFFFFF' Offset='0'/>
                    <GradientStop Color='#12FFFFFF' Offset='0.55'/>
                    <GradientStop Color='#40FFFFFF' Offset='1'/>
                  </LinearGradientBrush>
                </Border.BorderBrush>
              </Border>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'
                                TextElement.FontFamily='Segoe MDL2 Assets' TextElement.FontSize='10'/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Trigger.EnterActions>
                  <BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='Bubble' Storyboard.TargetProperty='Opacity' To='1' Duration='0:0:0.15'/></Storyboard></BeginStoryboard>
                </Trigger.EnterActions>
                <Trigger.ExitActions>
                  <BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='Bubble' Storyboard.TargetProperty='Opacity' To='0' Duration='0:0:0.22'/></Storyboard></BeginStoryboard>
                </Trigger.ExitActions>
                <Setter Property='Foreground' Value='White'/>
              </Trigger>
              <Trigger Property='IsPressed' Value='True'>
                <Setter Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='0.9' ScaleY='0.9'/></Setter.Value></Setter>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='MenuRow' TargetType='Button'>
      <Setter Property='Foreground' Value='White'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Height' Value='34'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Row' CornerRadius='9' Background='Transparent' Padding='10,0,10,0'>
              <ContentPresenter VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Row' Property='Background' Value='#24FFFFFF'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Row' Property='Background' Value='#38FFFFFF'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='Link' TargetType='Button'>
      <Setter Property='Foreground' Value='White'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Opacity' Value='0.6'/>
      <Setter Property='WindowChrome.IsHitTestVisibleInChrome' Value='True'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <ContentPresenter Margin='0' Cursor='Hand'/>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
      <Style.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter Property='Opacity' Value='1'/></Trigger>
      </Style.Triggers>
    </Style>
  </Window.Resources>

  <Grid x:Name='Root' Background='Transparent'>
    <!-- corpo de vidro: tinta escura translúcida sobre o desfoque do Windows -->
    <Border x:Name='GlassBody' CornerRadius='26'>
      <Border.Background>
        <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
          <GradientStop Color='#8A2A2C44' Offset='0'/>
          <GradientStop Color='#7A12121E' Offset='0.55'/>
          <GradientStop Color='#8A1E2036' Offset='1'/>
        </LinearGradientBrush>
      </Border.Background>
    </Border>

    <!-- brilhos líquidos: refração suave nas bordas -->
    <Ellipse Width='300' Height='170' HorizontalAlignment='Left' VerticalAlignment='Top' Margin='-90,-95,0,0' IsHitTestVisible='False'>
      <Ellipse.Fill>
        <RadialGradientBrush>
          <GradientStop Color='#30FFFFFF' Offset='0'/>
          <GradientStop Color='#00FFFFFF' Offset='1'/>
        </RadialGradientBrush>
      </Ellipse.Fill>
    </Ellipse>
    <Ellipse Width='280' Height='150' HorizontalAlignment='Right' VerticalAlignment='Bottom' Margin='0,0,-110,-90' IsHitTestVisible='False'>
      <Ellipse.Fill>
        <RadialGradientBrush>
          <GradientStop Color='#2280B4FF' Offset='0'/>
          <GradientStop Color='#0080B4FF' Offset='1'/>
        </RadialGradientBrush>
      </Ellipse.Fill>
    </Ellipse>
    <Border x:Name='Sheen' CornerRadius='26,26,80,80' Margin='1,1,1,0' Height='96' VerticalAlignment='Top' IsHitTestVisible='False'>
      <Border.Background>
        <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
          <GradientStop Color='#24FFFFFF' Offset='0'/>
          <GradientStop Color='#00FFFFFF' Offset='1'/>
        </LinearGradientBrush>
      </Border.Background>
    </Border>

    <!-- aro especular -->
    <Border x:Name='Rim' CornerRadius='26' BorderThickness='1.2' IsHitTestVisible='False'>
      <Border.BorderBrush>
        <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
          <GradientStop Color='#C8FFFFFF' Offset='0'/>
          <GradientStop Color='#28FFFFFF' Offset='0.3'/>
          <GradientStop Color='#0CFFFFFF' Offset='0.6'/>
          <GradientStop Color='#80FFFFFF' Offset='1'/>
        </LinearGradientBrush>
      </Border.BorderBrush>
    </Border>

    <Grid Margin='22,0,22,0'>
      <Grid.RowDefinitions>
        <RowDefinition Height='42'/>
        <RowDefinition Height='*'/>
        <RowDefinition Height='Auto'/>
        <RowDefinition Height='44'/>
      </Grid.RowDefinitions>

      <Button x:Name='GearBtn' Style='{StaticResource Chrome}' HorizontalAlignment='Left' VerticalAlignment='Center' Margin='-12,4,0,0'>
        <TextBlock Text='&#xE713;' FontFamily='Segoe MDL2 Assets' FontSize='12'/>
      </Button>
      <Popup x:Name='LangPopup' Placement='Bottom' HorizontalOffset='-6' VerticalOffset='2'
             StaysOpen='False' AllowsTransparency='True' PopupAnimation='Fade'>
        <!-- menu de vidro: o Popup não recebe o desfoque do Windows, então a tinta é mais densa -->
        <Border Margin='8,4,8,14' Width='176' Padding='5' CornerRadius='14' BorderThickness='1'>
          <Border.Background>
            <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
              <GradientStop Color='#F2363A52' Offset='0'/>
              <GradientStop Color='#F21E1F2C' Offset='1'/>
            </LinearGradientBrush>
          </Border.Background>
          <Border.BorderBrush>
            <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
              <GradientStop Color='#A0FFFFFF' Offset='0'/>
              <GradientStop Color='#18FFFFFF' Offset='0.5'/>
              <GradientStop Color='#50FFFFFF' Offset='1'/>
            </LinearGradientBrush>
          </Border.BorderBrush>
          <Border.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='4' Opacity='0.45' Color='Black'/></Border.Effect>
          <StackPanel>
            <TextBlock x:Name='LangHeader' Foreground='#99FFFFFF' FontSize='11' FontWeight='SemiBold' Margin='10,5,10,5'/>
            <Button x:Name='LangPt' Style='{StaticResource MenuRow}'>
              <Grid>
                <TextBlock Text='Português'/>
                <TextBlock x:Name='CheckPt' Text='&#xE73E;' FontFamily='Segoe MDL2 Assets' FontSize='11' Foreground='#FF30D158'
                           HorizontalAlignment='Right' VerticalAlignment='Center'/>
              </Grid>
            </Button>
            <Button x:Name='LangEn' Style='{StaticResource MenuRow}'>
              <Grid>
                <TextBlock Text='English'/>
                <TextBlock x:Name='CheckEn' Text='&#xE73E;' FontFamily='Segoe MDL2 Assets' FontSize='11' Foreground='#FF30D158'
                           HorizontalAlignment='Right' VerticalAlignment='Center'/>
              </Grid>
            </Button>
          </StackPanel>
        </Border>
      </Popup>

      <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,4,-12,0'>
        <Button x:Name='MinBtn' Style='{StaticResource Chrome}' Content='&#xE921;'/>
        <Button x:Name='MaxBtn' Style='{StaticResource Chrome}' Content='&#xE922;'/>
        <Button x:Name='CloseBtn' Style='{StaticResource Chrome}' Background='#D9FF453A' Content='&#xE8BB;'/>
      </StackPanel>
      <TextBlock x:Name='TitleText' Foreground='#99FFFFFF' FontSize='12' FontWeight='SemiBold'
                 HorizontalAlignment='Center' VerticalAlignment='Center' Margin='0,4,0,0' IsHitTestVisible='False'/>

      <!-- Viewbox: o tempo cresce junto com a janela quando maximizada -->
      <Viewbox x:Name='TimeBox' Grid.Row='1' Margin='8,0,8,4' IsHitTestVisible='False'>
      <StackPanel Orientation='Horizontal' HorizontalAlignment='Center' VerticalAlignment='Center'>
        <TextBlock x:Name='TimeMain' Text='00:00:00' FontSize='60' FontWeight='Light' Foreground='White' Typography.NumeralAlignment='Tabular'>
          <TextBlock.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='0' Opacity='0.35' Color='Black'/></TextBlock.Effect>
        </TextBlock>
        <TextBlock x:Name='TimeFrac' Text=',00' FontSize='26' FontWeight='Light' Foreground='#A6FFFFFF'
                   VerticalAlignment='Bottom' Margin='3,0,0,11' Typography.NumeralAlignment='Tabular'/>
      </StackPanel>
      </Viewbox>

      <Grid Grid.Row='2' MaxWidth='520'>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='3*'/>
          <ColumnDefinition Width='10'/>
          <ColumnDefinition Width='2*'/>
        </Grid.ColumnDefinitions>
        <Button x:Name='StartBtn' Style='{StaticResource Pill}'>
          <StackPanel Orientation='Horizontal'>
            <TextBlock x:Name='StartGlyph' FontFamily='Segoe MDL2 Assets' FontSize='12' VerticalAlignment='Center' Margin='0,1,9,0'/>
            <TextBlock x:Name='StartText' FontWeight='SemiBold' VerticalAlignment='Center'/>
          </StackPanel>
        </Button>
        <Button x:Name='ResetBtn' Grid.Column='2' Style='{StaticResource Pill}'>
          <StackPanel Orientation='Horizontal'>
            <TextBlock FontFamily='Segoe MDL2 Assets' FontSize='12' Text='&#xE72C;' VerticalAlignment='Center' Margin='0,1,8,0'/>
            <TextBlock x:Name='ResetText' VerticalAlignment='Center'/>
          </StackPanel>
        </Button>
      </Grid>

      <Grid Grid.Row='3' MaxWidth='520' VerticalAlignment='Center' Margin='4,2,2,0'>
        <StackPanel Orientation='Horizontal' VerticalAlignment='Center' IsHitTestVisible='False'>
          <Ellipse x:Name='StatusDot' Width='7' Height='7' Margin='0,1,8,0' VerticalAlignment='Center'/>
          <TextBlock x:Name='StatusText' Foreground='#B3FFFFFF' FontSize='11.5' VerticalAlignment='Center'/>
        </StackPanel>
        <Button x:Name='AutoBtn' Style='{StaticResource Link}' HorizontalAlignment='Right' VerticalAlignment='Center'
                ToolTip='Abrir automaticamente quando o PC ligar'>
          <StackPanel Orientation='Horizontal'>
            <Border Width='13' Height='13' CornerRadius='4' BorderBrush='#99FFFFFF' BorderThickness='1' Margin='0,1,6,0' VerticalAlignment='Center'>
              <TextBlock x:Name='AutoGlyph' FontFamily='Segoe MDL2 Assets' FontSize='9' HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <TextBlock x:Name='AutoText' FontSize='11.5' VerticalAlignment='Center'/>
          </StackPanel>
        </Button>
      </Grid>
    </Grid>
  </Grid>
</Window>";
    }
}
