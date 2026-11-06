using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hv86.Core;

namespace Hv86.App
{
    /// <summary>
    /// Окно консоли headless-ВМ.
    /// </summary>
    /// <remarks>
    /// Здесь нет WebView2 и нет окна v86: движок живёт в Node (сессия 0),
    /// а мы просто подключаемся к его каналу и рисуем то, что он присылает.
    ///
    /// Почему текст, а не картинка: в Node у v86 нет ни DOM, ни canvas,
    /// поэтому пиксельного вывода не существует в принципе. Зато есть
    /// VGA-текст с ANSI-цветами — этого достаточно Linux-консоли и DOS.
    /// Гостям с графикой нужен web-движок (WebView2), и это осознанный выбор
    /// режима, а не недоделка.
    ///
    /// Клавиатура: обычные символы идут через keyboard_send_text, спецклавиши —
    /// скан-кодами PS/2. Мышь не поддерживается: в headless её некому
    /// превращать в пакеты PS/2.
    /// </remarks>
    public sealed class HeadlessConsoleWindow : Form
    {
        private readonly string _vmId;
        private readonly ConsoleScreen _screen;
        private readonly Label _status;
        private readonly Button _pauseButton;
        private readonly System.Windows.Forms.Timer _reconnect;

        private PipeClient _client;
        private string _state = "connecting";
        private bool _closing;

        public HeadlessConsoleWindow(string vmId, string name)
        {
            _vmId = vmId;

            Text = "hv86 — " + name + " (headless)";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            DoubleBuffered = true;

            try { Icon = Brand.Icon(32); } catch { }

            _screen = new ConsoleScreen { Dock = DockStyle.Fill };
            Controls.Add(_screen);

            Panel toolbar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.FromArgb(0x0D, 0x12, 0x19) };

            Button pause = MakeButton("Пауза", 6, delegate { Send("vm.pause"); });
            Button resume = MakeButton("Продолжить", 92, delegate { Send("vm.resume"); });
            Button reset = MakeButton("Перезагрузка", 178, delegate { Send("vm.reset"); });

            _pauseButton = pause;

            _status = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Right,
                Width = 360,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(0x7F, 0x93, 0xAB),
                Font = new Font("Consolas", 9f),
                Text = "подключение…"
            };

            toolbar.Controls.Add(pause);
            toolbar.Controls.Add(resume);
            toolbar.Controls.Add(reset);
            toolbar.Controls.Add(_status);

            Controls.Add(toolbar);
            toolbar.BringToFront();

            _screen.SizeChanged += delegate { FitWindow(); };

            KeyPress += OnKeyPress;
            KeyDown += OnKeyDown;

            _reconnect = new System.Windows.Forms.Timer { Interval = 2000 };
            _reconnect.Tick += async delegate { await EnsureConnectedAsync(); };

            Shown += async delegate
            {
                await EnsureConnectedAsync();

                if (_client != null && _client.IsConnected) await PullScreenAsync();
                FitWindow();

                // если движок перезапустится, окно само подцепится обратно
                _reconnect.Start();
            };

            FormClosing += delegate { _closing = true; _reconnect.Stop(); };
            FormClosed += delegate
            {
                try { _client?.Dispose(); } catch { }
            };
        }

        private Button MakeButton(string text, int left, EventHandler onClick)
        {
            Button button = new Button
            {
                Text = text,
                Left = left,
                Top = 5,
                Width = 82,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0x12, 0x1A, 0x24),
                ForeColor = Color.FromArgb(0xCF, 0xE0, 0xF5)
            };

            button.FlatAppearance.BorderColor = Color.FromArgb(0x22, 0x30, 0x43);
            button.Click += onClick;

            return button;
        }

        private void FitWindow()
        {
            if (_screen.Cols <= 0 || _screen.Rows <= 0) return;

            int width = (int)Math.Ceiling(_screen.CellWidth * _screen.Cols) + 2;
            int height = (int)Math.Ceiling(_screen.CellHeight * _screen.Rows) + 2;

            ClientSize = new Size(Math.Max(420, width), Math.Max(280, height + 34));
        }

        // --------------------------------------------------------- подключение

        private async Task EnsureConnectedAsync()
        {
            if (_closing) return;
            if (_client != null && _client.IsConnected) return;

            try { _client?.Dispose(); } catch { }

            PipeClient client = new PipeClient(PipeNames.VmHost(_vmId));
            client.EventReceived += OnEngineEvent;

            if (!await client.ConnectAsync(800).ConfigureAwait(false))
            {
                client.Dispose();
                _status.Text = "нет связи с ВМ (движок не запущен?)";
                _reconnect.Start();
                return;
            }

            _client = client;
            _reconnect.Stop();

            Log.Info("CONSOLE", "подключился к " + _vmId);

            await RefreshStatusAsync().ConfigureAwait(false);
        }

        private async Task PullScreenAsync()
        {
            try
            {
                object result = await _client.CallForResultAsync("vm.screen", null, 5000).ConfigureAwait(false);
                ApplyScreen(result as Dictionary<string, object>);
            }
            catch (Exception ex)
            {
                Log.Debug("CONSOLE", "screen: " + ex.Message);
            }
        }

        private async Task RefreshStatusAsync()
        {
            try
            {
                object result = await _client.CallForResultAsync("vm.status", null, 5000).ConfigureAwait(false);
                Dictionary<string, object> status = result as Dictionary<string, object>;

                _state = Json.GetString(status, "state", _state);
                UpdateStatus(Json.GetLong(status, "instructions", 0), Json.GetInt(status, "mips", 0));
            }
            catch (Exception ex)
            {
                Log.Debug("CONSOLE", "status: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ события

        private void OnEngineEvent(string name, Dictionary<string, object> data)
        {
            switch (name)
            {
                case "vm.screen":
                    ApplyScreen(data);
                    break;

                case "vm.state":
                    _state = Json.GetString(data, "state", _state);
                    UpdateStatus(0, 0);
                    break;

                case "vm.metrics":
                    UpdateStatus(Json.GetLong(data, "instructions", 0), Json.GetInt(data, "mips", 0));
                    break;

                case "vm.error":
                    Log.Error("CONSOLE", Json.GetString(data, "message", ""));
                    break;
            }
        }

        private void ApplyScreen(Dictionary<string, object> data)
        {
            if (data == null) return;

            List<object> lines = Json.GetList(data, "lines");
            if (lines == null) return;

            string[] rows = new string[lines.Count];

            for (int i = 0; i < lines.Count; i++)
                rows[i] = lines[i] as string ?? "";

            Action apply = delegate
            {
                int cols = Json.GetInt(data, "cols", 80);
                _screen.SetScreen(cols, rows.Length, rows);
            };

            if (IsHandleCreated && InvokeRequired) BeginInvoke(apply);
            else apply();
        }

        private void UpdateStatus(long instructions, int mips)
        {
            Action apply = delegate
            {
                _pauseButton.Text = _state == "paused" ? "Продолжить" : "Пауза";

                _status.Text = _state + " · " + mips + " MIPS · " +
                               Math.Round(instructions / 1e6, 1) + "M instr";

                Text = "hv86 — " + _vmId + " (headless, " + _state + ")";
            };

            if (IsHandleCreated && InvokeRequired) BeginInvoke(apply);
            else apply();
        }

        // ------------------------------------------------------------ команды

        private async void Send(string method)
        {
            if (_client == null || !_client.IsConnected) return;

            try
            {
                await _client.CallForResultAsync(method, null, 15000).ConfigureAwait(false);
                await RefreshStatusAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("CONSOLE", method + ": " + ex.Message);
            }
        }

        private async void SendKey(Dictionary<string, object> parameters)
        {
            if (_client == null || !_client.IsConnected) return;

            try { await _client.CallForResultAsync("vm.key", parameters, 5000).ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("CONSOLE", "key: " + ex.Message); }
        }

        // ------------------------------------------------------------ ввод

        private void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            if (char.IsControl(e.KeyChar) && e.KeyChar != '\r' && e.KeyChar != '\t') return;

            // v86 сам превращает текст в скан-коды (раскладка US)
            SendKey(Json.Obj("text", e.KeyChar.ToString()));
            e.Handled = true;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                SendKey(Json.Obj("scancodes", ToObjects(new List<int> { 0x1D, 0x2E, 0xAE, 0x9D })));
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.V)
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text)) SendKey(Json.Obj("text", text.Replace("\r", "")));
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control || e.Alt) return;

            List<int> codes = ScancodesFor(e.KeyCode);

            if (codes != null)
            {
                SendKey(Json.Obj("scancodes", ToObjects(codes)));
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private static List<object> ToObjects(List<int> codes)
        {
            List<object> list = new List<object>();
            foreach (int code in codes) list.Add(code);
            return list;
        }

        private static List<int> ScancodesFor(Keys key)
        {
            switch (key)
            {
                case Keys.Enter: return new List<int> { 0x1C };
                case Keys.Back: return new List<int> { 0x0E };
                case Keys.Tab: return new List<int> { 0x0F };
                case Keys.Escape: return new List<int> { 0x01 };
                case Keys.Up: return new List<int> { 0xE0, 0x48 };
                case Keys.Down: return new List<int> { 0xE0, 0x50 };
                case Keys.Left: return new List<int> { 0xE0, 0x4B };
                case Keys.Right: return new List<int> { 0xE0, 0x4D };
                case Keys.Home: return new List<int> { 0xE0, 0x47 };
                case Keys.End: return new List<int> { 0xE0, 0x4F };
                case Keys.PageUp: return new List<int> { 0xE0, 0x49 };
                case Keys.PageDown: return new List<int> { 0xE0, 0x51 };
                case Keys.Delete: return new List<int> { 0xE0, 0x53 };
                case Keys.Insert: return new List<int> { 0xE0, 0x52 };
            }

            if (key >= Keys.F1 && key <= Keys.F10)
                return new List<int> { 0x3B + (key - Keys.F1) };

            if (key == Keys.F11) return new List<int> { 0x57 };
            if (key == Keys.F12) return new List<int> { 0x58 };

            return null;
        }

        // ------------------------------------------------------------ экран

        /// <summary>
        /// Рисовалка VGA-текста: строки приходят с ANSI truecolor-кодами
        /// (48;2;r;g;b / 38;2;r;g;b), поэтому разбираем их в отрезки и
        /// заливаем фон отдельно от текста.
        /// </summary>
        private sealed class ConsoleScreen : Control
        {
            private string[] _lines = new string[0];

            public int Cols { get; private set; }
            public int Rows { get; private set; }
            public float CellWidth { get; private set; }
            public float CellHeight { get; private set; }

            private Font _font;

            public ConsoleScreen()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

                BackColor = Color.Black;
                Font = new Font("Consolas", 13f);
            }

            public void SetScreen(int cols, int rows, string[] lines)
            {
                _lines = lines ?? new string[0];
                Cols = cols > 0 ? cols : 80;
                Rows = rows > 0 ? rows : 25;

                MeasureCells();

                Invalidate();
            }

            private void MeasureCells()
            {
                if (_font == null)
                    _font = new Font("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Point);

                // ширина ячейки = ширина 80 символов / 80: так моноширинный
                // шрифт не «плывёт» из-за округлений
                Size measured = TextRenderer.MeasureText(new string('W', 80), _font);

                CellWidth = Math.Max(1f, measured.Width / 80f);
                CellHeight = Math.Max(1f, _font.GetHeight() * 1.02f);
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);

                _font = Font;
                MeasureCells();
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Color.Black);

                if (_font == null) MeasureCells();

                if (_lines.Length == 0)
                {
                    using (Font hint = new Font("Segoe UI", 10f))
                        TextRenderer.DrawText(g, "Ожидание экрана…", hint, new Point(12, 12),
                            Color.FromArgb(0x6F, 0x81, 0x99));

                    return;
                }

                for (int row = 0; row < _lines.Length && row < Rows; row++)
                {
                    float y = row * CellHeight;
                    DrawRow(g, _lines[row], 0f, y);
                }
            }

            private void DrawRow(Graphics g, string line, float x, float y)
            {
                Color fg = Color.FromArgb(0xC8, 0xC8, 0xC8);
                Color bg = Color.Black;

                StringBuilder run = new StringBuilder();
                int column = 0;
                int i = 0;

                while (i < line.Length && column < Cols)
                {
                    if (line[i] == '\u001b' && i + 1 < line.Length && line[i + 1] == '[')
                    {
                        int end = line.IndexOf('m', i);
                        if (end < 0) break;

                        column = Flush(g, run, x, y, column, fg, bg);
                        ApplySgr(line.Substring(i + 2, end - i - 2), ref fg, ref bg);

                        i = end + 1;
                        continue;
                    }

                    run.Append(line[i]);
                    column++;
                    i++;
                }

                Flush(g, run, x, y, column, fg, bg);
            }

            private int Flush(Graphics g, StringBuilder run, float x, float y, int column, Color fg, Color bg)
            {
                if (run.Length == 0) return column - run.Length;

                int length = run.Length;
                int startColumn = column - length;

                if (bg != Color.Black)
                {
                    using (SolidBrush brush = new SolidBrush(bg))
                        g.FillRectangle(brush, x + startColumn * CellWidth, y, length * CellWidth, CellHeight);
                }

                TextRenderer.DrawText(g, run.ToString(), _font,
                    new Point((int)Math.Round(x + startColumn * CellWidth), (int)Math.Round(y)),
                    fg, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

                run.Clear();
                return column;
            }

            private static void ApplySgr(string spec, ref Color fg, ref Color bg)
            {
                if (string.IsNullOrEmpty(spec) || spec == "0")
                {
                    fg = Color.FromArgb(0xC8, 0xC8, 0xC8);
                    bg = Color.Black;
                    return;
                }

                string[] parts = spec.Split(';');

                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == "0")
                    {
                        fg = Color.FromArgb(0xC8, 0xC8, 0xC8);
                        bg = Color.Black;
                        continue;
                    }

                    if ((parts[i] == "38" || parts[i] == "48") && i + 4 < parts.Length && parts[i + 1] == "2")
                    {
                        int r = Parse(parts[i + 2], 255);
                        int g = Parse(parts[i + 3], 255);
                        int b = Parse(parts[i + 4], 255);

                        Color color = Color.FromArgb(r, g, b);

                        if (parts[i] == "38") fg = color; else bg = color;

                        i += 4;
                    }
                }
            }

            private static int Parse(string text, int fallback)
            {
                int value;
                return int.TryParse(text, out value) ? Math.Max(0, Math.Min(255, value)) : fallback;
            }
        }
    }
}