using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Speech.Recognition;
using System.Collections.Generic;
using System.Drawing; 
using System.IO;
using Forms = System.Windows.Forms;
using NAudio.Wave;
using System.Linq;
using System.Diagnostics;

namespace ClawdDesktop
{
    public class TranscriptionConfig
    {
        public int TranscriptionProvider { get; set; } = 0;
        public string OpenaiApiKey { get; set; } = "";
        public string WhisperModel { get; set; } = "small";
    }

    public partial class MainWindow : Window
    {
        private readonly HttpClient _client = new HttpClient();
        private const string GatewayUrl = "http://localhost:18789/v1/sessions/agent:main:main/turns";
        private const string AuthToken = "bc43faf47e9a71489b0cacfed850e40b06f1cd7188d1283b";
        
        private bool _isRecording = false;
        private DispatcherTimer _timer;
        private DispatcherTimer _historyTimer;
        private int _seconds = 0;
        private SpeechRecognitionEngine? _recognizer;
        private string _lastRecognizedText = "";
        private Forms.NotifyIcon? _notifyIcon;

        private WaveInEvent? _waveIn;
        private WaveFileWriter? _waveWriter;
        private string? _lastRecordingPath;
        
        private TranscriptionConfig _config = new TranscriptionConfig();
        private readonly string _configPath;
        private readonly string _pythonPath = @"C:\Program Files\Python314\python.exe";

        public MainWindow()
        {
            InitializeComponent();
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthToken}");
            
            _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClawdDesktop", "config.json");
            LoadConfig();
            
            TranscriptionProvider.SelectionChanged += (s, e) => SaveConfig();
            ApiKeyBox.PasswordChanged += (s, e) => SaveConfig();
            
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => {
                _seconds++;
                TimerText.Text = string.Format("{0}:{1:D2}", _seconds / 60, _seconds % 60);
            };

            _historyTimer = new DispatcherTimer();
            _historyTimer.Interval = TimeSpan.FromSeconds(3);
            _historyTimer.Tick += async (s, e) => await RefreshHistory();
            _historyTimer.Start();

            InitTray();
            InitSpeech();
        }

        private void LoadConfig()
        {
            try {
                if (File.Exists(_configPath)) {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<TranscriptionConfig>(json) ?? new TranscriptionConfig();
                    TranscriptionProvider.SelectedIndex = _config.TranscriptionProvider;
                    ApiKeyBox.Password = _config.OpenaiApiKey;
                }
            } catch {
                _config = new TranscriptionConfig();
            }
        }

        private void SaveConfig()
        {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                _config.TranscriptionProvider = TranscriptionProvider.SelectedIndex;
                _config.OpenaiApiKey = ApiKeyBox.Password;
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            } catch { }
        }

        private void InitTray()
        {
            try {
                _notifyIcon = new Forms.NotifyIcon();
                _notifyIcon.Icon = this.Icon != null ? System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location) : SystemIcons.Shield; 
                _notifyIcon.Visible = true;
                _notifyIcon.Text = "Mike PRO Command Center";
                _notifyIcon.DoubleClick += (s, e) => {
                    this.Show();
                    this.WindowState = WindowState.Normal;
                    this.Activate();
                };

                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Restore Studio", null, (s, e) => {
                    this.Show();
                    this.WindowState = WindowState.Normal;
                    this.Activate();
                });
                menu.Items.Add("Exit Mike PRO", null, (s, e) => System.Windows.Application.Current.Shutdown());
                _notifyIcon.ContextMenuStrip = menu;
            } catch { }
        }

        private void InitSpeech()
        {
            try {
                _recognizer = new SpeechRecognitionEngine();
                _recognizer.SetInputToDefaultAudioDevice();
                _recognizer.LoadGrammar(new DictationGrammar());
                _recognizer.SpeechRecognized += (s, e) => { _lastRecognizedText += e.Result.Text + " "; };
            } catch { }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            MaxBtn.Content = this.WindowState == WindowState.Maximized ? "❐" : "▢";
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => this.Hide();

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            string cmd = InputBox.Text;
            if (string.IsNullOrWhiteSpace(cmd)) return;
            AddMessage(cmd, true);
            InputBox.Text = "";
            await DispatchCommand(cmd);
        }

        private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Send_Click(null, null);
        }

        private void PowerBtn_Click(object sender, RoutedEventArgs e)
        {
            bool isOn = PowerBtn.IsChecked ?? false;
            AddMessage(isOn ? "ENGINE: REBOOTED." : "ENGINE: DORMANT.", false);
        }

        private void Mic_Click(object sender, RoutedEventArgs e)
        {
            _isRecording = !_isRecording;
            if (_isRecording) StartRecording();
            else StopRecording();
        }

        private void CancelRecording_Click(object sender, RoutedEventArgs e)
        {
            _isRecording = false;
            _timer.Stop();
            RecDot.BeginAnimation(OpacityProperty, null);
            RecordingOverlay.Visibility = Visibility.Collapsed;
            InputGrid.Visibility = Visibility.Visible;
            
            try {
                _recognizer?.RecognizeAsyncStop();
                _waveIn?.StopRecording();
                _waveIn?.Dispose();
                _waveWriter?.Close();
                _waveWriter?.Dispose();
                
                // Delete the recording file
                if (!string.IsNullOrEmpty(_lastRecordingPath) && File.Exists(_lastRecordingPath)) {
                    File.Delete(_lastRecordingPath);
                }
                
                _lastRecognizedText = "";
                _lastRecordingPath = null;
            } catch { }
        }

        private void StartRecording()
        {
            _seconds = 0; TimerText.Text = "0:00"; _lastRecognizedText = "";
            _timer.Start(); InputGrid.Visibility = Visibility.Collapsed; RecordingOverlay.Visibility = Visibility.Visible;
            try {
                _recognizer?.RecognizeAsync(RecognizeMode.Multiple);
                _lastRecordingPath = Path.Combine(Path.GetTempPath(), $"voice_{DateTime.Now.Ticks}.wav");
                _waveIn = new WaveInEvent();
                _waveIn.WaveFormat = new WaveFormat(44100, 1);
                _waveIn.DataAvailable += (s, e) => {
                    if (e.BytesRecorded > 0)
                    {
                        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                        UpdateWaveform(e.Buffer, e.BytesRecorded);
                    }
                };
                _waveWriter = new WaveFileWriter(_lastRecordingPath, _waveIn.WaveFormat);
                _waveIn.StartRecording();
                RecDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.2, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            } catch { }
        }

        private async void StopRecording()
        {
            _timer.Stop();
            RecDot.BeginAnimation(OpacityProperty, null);
            RecordingOverlay.Visibility = Visibility.Collapsed;
            InputGrid.Visibility = Visibility.Visible;
            
            string? transcribedText = null;
            
            try {
                _recognizer?.RecognizeAsyncStop();
                _waveIn?.StopRecording();
                _waveIn?.Dispose();
                _waveWriter?.Close();
                _waveWriter?.Dispose();
                
                int providerIndex = TranscriptionProvider.SelectedIndex;
                
                switch (providerIndex) {
                    case 0: // Local Whisper
                        transcribedText = await TranscribeWithLocalWhisper(_lastRecordingPath!);
                        break;
                    case 1: // OpenAI
                        transcribedText = await TranscribeWithOpenAI(_lastRecordingPath!);
                        break;
                    case 2: // Windows Speech
                        transcribedText = _lastRecognizedText;
                        break;
                }
                
                if (!string.IsNullOrWhiteSpace(transcribedText)) {
                    AddMessage(transcribedText.Trim(), true);
                    AddAudioMessage(_lastRecordingPath!);
                    _ = DispatchCommand(transcribedText.Trim());
                } else { 
                    AddMessage("Voice sample captured but transcription failed.", true);
                    AddAudioMessage(_lastRecordingPath!);
                    _ = DispatchCommand("Voice Command Received.");
                }
            } catch { }
        }

        private async Task<string?> TranscribeWithLocalWhisper(string audioPath)
        {
            try {
                var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "transcribe.py");
                File.AppendAllText("debug.log", $"[{DateTime.Now}] Local Whisper - Script: {scriptPath}, Audio: {audioPath}\n");
                
                var psi = new ProcessStartInfo {
                    FileName = _pythonPath,
                    Arguments = $"\"{scriptPath}\" \"{audioPath}\" \"{_config.WhisperModel}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) {
                    File.AppendAllText("debug.log", $"[{DateTime.Now}] Local Whisper - Failed to start process\n");
                    return null;
                }
                string result = await proc.StandardOutput.ReadToEndAsync();
                string error = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                File.AppendAllText("debug.log", $"[{DateTime.Now}] Local Whisper - Exit code: {proc.ExitCode}, Output: {result}, Error: {error}\n");
                if (proc.ExitCode != 0) return null;
                return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
            } catch (Exception ex) {
                File.AppendAllText("debug.log", $"[{DateTime.Now}] Local Whisper Exception: {ex.Message}\n");
                return null;
            }
        }

        private async Task<string?> TranscribeWithOpenAI(string audioPath)
        {
            try {
                string apiKey = ApiKeyBox.Password;
                if (string.IsNullOrWhiteSpace(apiKey)) return null;
                
                var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "transcribe_openai.py");
                var psi = new ProcessStartInfo {
                    FileName = _pythonPath,
                    Arguments = $"\"{scriptPath}\" \"{audioPath}\" \"{apiKey}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                string result = await proc.StandardOutput.ReadToEndAsync();
                string error = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0) return null;
                return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
            } catch { return null; }
        }

        private void UpdateWaveform(byte[] buffer, int bytesRecorded)
        {
            float max = 0;
            for (int i = 0; i < bytesRecorded; i += 2) {
                if (i + 1 < bytesRecorded) {
                    short sample = (short)((buffer[i + 1] << 8) | buffer[i]);
                    float sample32 = sample / 32768f;
                    if (Math.Abs(sample32) > max) max = Math.Abs(sample32);
                }
            }
            
            // Debug: If max is too low, simulate some activity for visual feedback
            if (max < 0.01f) max = 0.02f; 

            Dispatcher.Invoke(() => {
                var points = new PointCollection();
                var rand = new Random();
                // We want to see a "pulse" even if silent
                double baseMax = max * 100; // Scale up for visibility
                if (baseMax < 5) baseMax = 5; 

                for (int i = 0; i < 11; i++) {
                    double h = 25 + (rand.NextDouble() * baseMax * (i % 2 == 0 ? 1 : -1));
                    points.Add(new System.Windows.Point(i * 12, h));
                }
                WaveLine.Points = points;
            });
        }

        private void AddAudioMessage(string path)
        {
            var btn = new System.Windows.Controls.Button {
                Content = "▶ PLAY VOICE RECORDING",
                Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10b981")),
                Foreground = System.Windows.Media.Brushes.White,
                Padding = new Thickness(10),
                Margin = new Thickness(150, 0, 0, 15),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                BorderThickness = new Thickness(0),
                Tag = path
            };
            btn.Click += (s, e) => {
                var player = new System.Windows.Media.MediaPlayer();
                player.Open(new Uri((string)((System.Windows.Controls.Button)s).Tag));
                player.Play();
            };
            ChatPanel.Children.Add(btn);
        }

        private void Upload_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = "Audio Files|*.wav;*.mp3;*.ogg;*.mp4;*.m4a|All Files|*.*";
            if (dialog.ShowDialog() == true) {
                AddMessage($"FILE IMPORTED: {Path.GetFileName(dialog.FileName)}", false);
                _ = DispatchCommand($"Uploaded loop: {dialog.FileName}");
            }
        }

        private void AddMessage(string text, bool isUser)
        {
            var bubble = new Border {
                Background = isUser ? new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2563eb")) : new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#18181b")),
                CornerRadius = new CornerRadius(15), Padding = new Thickness(15), Margin = isUser ? new Thickness(150, 0, 0, 15) : new Thickness(0, 0, 150, 15),
                HorizontalAlignment = isUser ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left,
                BorderBrush = isUser ? null : new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272a")),
                BorderThickness = isUser ? new Thickness(0) : new Thickness(1)
            };
            bubble.Child = new TextBlock { Text = text, Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            ChatPanel.Children.Add(bubble);
            (VisualTreeHelper.GetChild(ChatPanel.Parent, 0) as ScrollViewer)?.ScrollToBottom();
        }

        private async Task RefreshHistory()
        {
            try {
                // The stable session key for 'farzad' user on 'main' agent
                string sessionKey = "agent:main:openresponses-user:farzad"; 
                
                var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:18789/api/v1/sessions/{sessionKey}/history");
                request.Headers.Add("Accept", "application/json");
                
                var response = await _client.SendAsync(request);
                if (response.IsSuccessStatusCode) {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.ValueKind == JsonValueKind.Array) {
                        var messages = doc.RootElement;
                        // Count borders (message bubbles) only
                        int currentCount = ChatPanel.Children.OfType<Border>().Count();
                        
                        if (messages.GetArrayLength() > currentCount) {
                            this.Dispatcher.Invoke(() => {
                                ChatPanel.Children.Clear();
                                foreach(var msg in messages.EnumerateArray()) {
                                    string role = msg.GetProperty("role").GetString() ?? "assistant";
                                    string mText = "";
                                    
                                    if (msg.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array) {
                                        foreach(var content in contentArray.EnumerateArray()) {
                                            if(content.GetProperty("type").GetString() == "text") {
                                                mText += content.GetProperty("text").GetString();
                                            }
                                        }
                                    } else if (msg.TryGetProperty("text", out var textProp)) {
                                        mText = textProp.GetString() ?? "";
                                    }
                                    
                                    if (!string.IsNullOrWhiteSpace(mText)) {
                                        AddMessage(mText, role == "user");
                                    }
                                }
                            });
                        }
                    }
                }
            } catch {}
        }

        private async Task DispatchCommand(string message)
        {
            try {
                var payload = new { 
                    model = "openclaw",
                    input = message,
                    user = "farzad"
                };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                content.Headers.Add("x-openclaw-agent-id", "main");
                
                await _client.PostAsync("http://localhost:18789/v1/responses", content);
            } catch { }
        }
    }
}
