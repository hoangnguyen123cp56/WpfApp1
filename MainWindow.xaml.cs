using System;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        // ===== Serial =====
        private SerialPort? _port;
        private int _baud = 115200;

        // ===== Chart =====
        private readonly Queue<(long t, long pos, long sp)> _buf = new();
        private readonly Polyline _posLine = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1.5 };
        private readonly Polyline _spLine = new() { Stroke = Brushes.Gray, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 4 } };
        private readonly DispatcherTimer _plotTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DateTime _t0 = DateTime.UtcNow;
        private const int MaxSamples = 500; // ~50s @10Hz

        // ===== Sine Test =====
        private DispatcherTimer? _sineTimer;
        private bool _sineOn = false;
        private long _spBase = 0;
        private double _theta = 0;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += (_, __) =>
            {
                InitBaudList();
                LoadPorts();
                SetupPlot();
            };

            BtnRefresh.Click += (_, __) => LoadPorts();
            BtnConnect.Click += (_, __) => Connect();
            BtnDisconnect.Click += (_, __) => Disconnect();

            BtnSetSp.Click += (_, __) => SendSetpoint();
            BtnSetPid.Click += (_, __) => SendPid();
            BtnEnable.Click += (_, __) => _port?.WriteLine("CTRL:ENABLE");
            BtnDisable.Click += (_, __) => _port?.WriteLine("CTRL:DISABLE");
            BtnRstEnc.Click += (_, __) => _port?.WriteLine("RST:ENC");

            BtnStep.Click += (_, __) => DoStep();
            BtnSine.Click += (_, __) => ToggleSine();
        }

        // ===== UI init =====
        private void InitBaudList()
        {
            int[] baudrates = { 9600, 19200, 38400, 57600, 115200, 230400, 460800 };
            CmbBaud.ItemsSource = baudrates;
            CmbBaud.SelectedItem = _baud;
            TxtBaudSel.Text = $"Baud: {_baud}";
        }

        private void LoadPorts()
        {
            string[] ports;
            try
            {
                ports = SerialPort.GetPortNames().OrderBy(s => s).ToArray();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không đọc được danh sách COM:\n{ex.Message}");
                return;
            }

            Debug.WriteLine($"Found {ports.Length} port(s): {string.Join(", ", ports)}");
            if (ports.Length == 0)
            {
                CmbPorts.ItemsSource = new[] { "(No COM found)" };
                CmbPorts.SelectedIndex = 0;
                return;
            }

            CmbPorts.ItemsSource = ports;
            CmbPorts.SelectedIndex = 0;
        }

        // ===== Serial connect =====
        private void Connect()
        {
            try
            {
                if (_port is { IsOpen: true }) return;
                if (CmbPorts.SelectedItem is not string portName || portName.StartsWith("("))
                {
                    MessageBox.Show("Chưa có cổng COM hợp lệ."); return;
                }
                _port = new SerialPort(portName, _baud)
                {
                    NewLine = "\n",
                    ReadTimeout = 2000,
                    WriteTimeout = 2000,
                    DtrEnable = true,
                    RtsEnable = true
                };
                _port.DataReceived += Port_DataReceived;
                _port.Open();
                MessageBox.Show($"Đã kết nối {portName} @ {_baud}");
                _port.WriteLine("*IDN?");
                _port.WriteLine("PID:GET");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không thể kết nối:\n{ex.Message}");
            }
        }

        private void Disconnect()
        {
            try
            {
                if (_port == null) return;
                _port.DataReceived -= Port_DataReceived;
                if (_port.IsOpen) _port.Close();
                _port.Dispose();
                _port = null;
                MessageBox.Show("Đã ngắt kết nối.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi ngắt kết nối:\n{ex.Message}");
            }
        }

        // ===== Receive & parse =====
        private void Port_DataReceived(object? s, SerialDataReceivedEventArgs e)
        {
            try
            {
                string chunk = _port!.ReadExisting();
                foreach (var line in chunk.Replace("\r", "").Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    AppendLog($"> {line}");
                    TryParseTelemetry(line);
                    TryParsePid(line);
                }
            }
            catch { /* ignore */ }
        }

        private void AppendLog(string s) => Dispatcher.Invoke(() =>
        {
            LstLog.Items.Insert(0, s);
            if (LstLog.Items.Count > 300) LstLog.Items.RemoveAt(LstLog.Items.Count - 1);
        });

        private void TryParseTelemetry(string line)
        {
            // Hỗ trợ cả 2 format:
            // 1) "POS:1234 SP:1200"
            // 2) (cũ) chỉ có "POS:..., VEL/ERR/OUT..."  -> dùng _spLast
            long GetLong(string key)
            {
                int i = line.IndexOf(key); if (i < 0) throw new();
                i += key.Length; int j = i;
                while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '-')) j++;
                return long.Parse(line.AsSpan(i, j - i));
            }
            try
            {
                long pos = GetLong("POS:");
                long sp = line.Contains("SP:") ? GetLong("SP:") : _spLast;

                var t = (long)(DateTime.UtcNow - _t0).TotalMilliseconds;
                Dispatcher.Invoke(() =>
                {
                    TxtPos.Text = pos.ToString();
                    // (tuỳ thích) hiển thị SP ở đâu đó, hoặc bỏ qua UI text
                    _buf.Enqueue((t, pos, sp));
                    while (_buf.Count > MaxSamples) _buf.Dequeue();
                });
            }
            catch { /* không phải dòng telemetry mình cần */ }
        }


        private void TryParsePid(string line)
        {
            // Optional: firmware có thể trả "PID Kp=2.0 Ki=5.5 Kd=0.02"
            if (!line.StartsWith("PID", StringComparison.OrdinalIgnoreCase)) return;
            double GetD(string key)
            {
                int i = line.IndexOf(key); if (i < 0) throw new();
                i += key.Length; int j = i;
                while (j < line.Length && ("-+.0123456789".Contains(line[j]))) j++;
                return double.Parse(line.AsSpan(i, j - i), CultureInfo.InvariantCulture);
            }
            try
            {
                var kp = GetD("Kp=");
                var ki = GetD("Ki=");
                var kd = GetD("Kd=");
                Dispatcher.Invoke(() =>
                {
                    TxtKp.Text = kp.ToString(CultureInfo.InvariantCulture);
                    TxtKi.Text = ki.ToString(CultureInfo.InvariantCulture);
                    TxtKd.Text = kd.ToString(CultureInfo.InvariantCulture);
                });
            }
            catch { /* ignore */ }
        }

        // ===== Chart =====
        private void SetupPlot()
        {
            Plot.Children.Clear();
            Plot.Children.Add(_posLine);
            Plot.Children.Add(_spLine);
            _plotTimer.Tick += (_, __) => Redraw();
            _plotTimer.Start();
        }


        private void Plot_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();
        private long _spLast = 0;

        private void Redraw()
        {
            if (_buf.Count == 0) return;
            double W = Math.Max(10, Plot.ActualWidth);
            double H = Math.Max(10, Plot.ActualHeight);

            var arr = _buf.ToArray();
            _posLine.Points.Clear();
            _spLine.Points.Clear();

            long t0 = arr[0].t, tN = arr[^1].t;
            double span = Math.Max(1, tN - t0);

            long ymin = arr.Min(x => Math.Min(x.pos, x.sp));
            long ymax = arr.Max(x => Math.Max(x.pos, x.sp));
            if (ymax == ymin) ymax = ymin + 1;

            double MapY(long v) => H - (v - ymin) / (double)(ymax - ymin) * H;

            foreach (var s in arr)
            {
                double x = (s.t - t0) / span * W;
                _posLine.Points.Add(new Point(x, MapY(s.pos)));
                _spLine.Points.Add(new Point(x, MapY(s.sp)));
            }
        }


        // ===== Commands =====
        void SendSetpoint()
        {
            if (!EnsurePort()) return;
            if (!long.TryParse(TxtSp.Text, out var sp)) { MessageBox.Show("SP phải là số."); return; }
            string cmd = ChkHold.IsChecked == true ? $"SP:HOLD {sp}" : $"SP:SET {sp}";
            _port!.WriteLine(cmd);
            _spLast = sp; // lưu để vẽ nếu MCU không trả SP
        }


        private void SendPid()
        {
            if (!EnsurePort()) return;
            var ci = CultureInfo.InvariantCulture;
            if (!double.TryParse(TxtKp.Text, NumberStyles.Float, ci, out var kp) ||
                !double.TryParse(TxtKi.Text, NumberStyles.Float, ci, out var ki) ||
                !double.TryParse(TxtKd.Text, NumberStyles.Float, ci, out var kd))
            {
                MessageBox.Show("Kp/Ki/Kd phải là số."); return;
            }
            _port!.WriteLine($"PID:SET {kp.ToString(ci)},{ki.ToString(ci)},{kd.ToString(ci)}");
        }

        private bool EnsurePort()
        {
            if (_port is null || !_port.IsOpen)
            { MessageBox.Show("Chưa kết nối serial."); return false; }
            return true;
        }

        private void DoStep()
        {
            if (!long.TryParse(TxtSp.Text, out var sp)) sp = 0;
            if (!long.TryParse(TxtStep.Text, out var step)) step = 100;
            TxtSp.Text = (sp + step).ToString();
            SendSetpoint();
        }

        private void ToggleSine()
        {
            _sineOn = !_sineOn;
            if (_sineOn)
            {
                if (!EnsurePort()) { _sineOn = false; return; }
                if (!long.TryParse(TxtSp.Text, out _spBase)) _spBase = 0;
                _theta = 0;
                _sineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _sineTimer.Tick += (_, __) =>
                {
                    var ci = CultureInfo.InvariantCulture;
                    if (!double.TryParse(TxtAmp.Text, NumberStyles.Float, ci, out var amp)) amp = 100;
                    if (!double.TryParse(TxtFreq.Text, NumberStyles.Float, ci, out var f)) f = 0.5;
                    _theta += 2 * Math.PI * f * 0.05; // 50 ms
                    long sp = _spBase + (long)(amp * Math.Sin(_theta));
                    TxtSp.Text = sp.ToString();
                    SendSetpoint();
                };
                _sineTimer.Start();
                AppendLog("[SINE] started");
            }
            else
            {
                _sineTimer?.Stop();
                AppendLog("[SINE] stopped");
            }
        }

        // ===== Events =====
        private void Baudrate(object sender, SelectionChangedEventArgs e)
        {
            if (CmbBaud.SelectedItem is int baud)
            {
                _baud = baud;
                TxtBaudSel.Text = $"Baud: {_baud}";
                Debug.WriteLine($"Selected baud: {_baud}");
            }
        }
    }
}
