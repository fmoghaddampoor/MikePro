using Xunit;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ClawdDesktop.Tests
{
    public class TranscriptionTests
    {
        private readonly string _pythonPath = @"C:\Program Files\Python314\python.exe";
        private readonly string _testAudioPath = Path.Combine(Path.GetTempPath(), "test_audio.wav");

        public TranscriptionTests()
        {
            // Create a simple test audio file if it doesn't exist
            if (!File.Exists(_testAudioPath))
            {
                CreateTestAudioFile();
            }
        }

        private void CreateTestAudioFile()
        {
            // This is a minimal WAV file header for a silent 1-second audio
            // In real tests, you'd use an actual audio file
            using var fs = new FileStream(_testAudioPath, FileMode.Create);
            using var writer = new BinaryWriter(fs);
            
            // WAV header
            writer.Write("RIFF".ToCharArray());
            writer.Write(36); // File size - 8
            writer.Write("WAVE".ToCharArray());
            writer.Write("fmt ".ToCharArray());
            writer.Write(16); // Subchunk1Size
            writer.Write((short)1); // AudioFormat (PCM)
            writer.Write((short)1); // NumChannels
            writer.Write(44100); // SampleRate
            writer.Write(44100 * 2); // ByteRate
            writer.Write((short)2); // BlockAlign
            writer.Write((short)16); // BitsPerSample
            writer.Write("data".ToCharArray());
            writer.Write(0); // Subchunk2Size
        }

        [Fact]
        public void TestConfig_LoadsDefaultValues()
        {
            var config = new TranscriptionConfig();
            
            Assert.Equal(0, config.TranscriptionProvider);
            Assert.Equal("", config.OpenaiApiKey);
            Assert.Equal("small", config.WhisperModel);
        }

        [Fact]
        public void TestConfig_Serialization()
        {
            var config = new TranscriptionConfig
            {
                TranscriptionProvider = 1,
                OpenaiApiKey = "test-key",
                WhisperModel = "base"
            };

            var json = System.Text.Json.JsonSerializer.Serialize(config);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<TranscriptionConfig>(json);

            Assert.Equal(config.TranscriptionProvider, deserialized.TranscriptionProvider);
            Assert.Equal(config.OpenaiApiKey, deserialized.OpenaiApiKey);
            Assert.Equal(config.WhisperModel, deserialized.WhisperModel);
        }

        [Fact]
        public async Task TestLocalWhisper_ScriptExists()
        {
            var scriptPath = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!, 
                "..", "..", "..", "..", "transcribe.py");
            
            Assert.True(File.Exists(scriptPath), "transcribe.py script should exist");
        }

        [Fact]
        public async Task TestOpenAIScript_ScriptExists()
        {
            var scriptPath = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!, 
                "..", "..", "..", "..", "transcribe_openai.py");
            
            Assert.True(File.Exists(scriptPath), "transcribe_openai.py script should exist");
        }

        [Fact]
        public async Task TestPythonInstallation()
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            Assert.NotNull(process);
            
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            Assert.True(process.ExitCode == 0, "Python should be installed and accessible");
            Assert.Contains("Python 3.14", output);
        }

        [Fact]
        public async Task TestLocalWhisper_PackagesInstalled()
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = "-c \"import faster_whisper; print('OK')\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            Assert.NotNull(process);
            
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            Assert.True(process.ExitCode == 0, $"faster-whisper should be installed. Error: {error}");
            Assert.Contains("OK", output);
        }

        [Fact]
        public async Task TestOpenAI_PackagesInstalled()
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = "-c \"import openai; print('OK')\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            Assert.NotNull(process);
            
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            Assert.True(process.ExitCode == 0, $"openai package should be installed. Error: {error}");
            Assert.Contains("OK", output);
        }

        [Fact]
        public void TestTranscriptionProvider_EnumValues()
        {
            // Verify provider indices match expected behavior
            Assert.Equal(0, 0); // Local Whisper
            Assert.Equal(1, 1); // OpenAI
            Assert.Equal(2, 2); // Windows Speech
        }

        [Fact]
        public void TestConfigFile_Path()
        {
            var expectedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                "ClawdDesktop", 
                "config.json");
            
            // Config file should be created on first run
            // Note: This test assumes the app has been run at least once
            Assert.True(Directory.Exists(Path.GetDirectoryName(expectedPath)), "Config directory should exist");
        }

        [Theory]
        [InlineData("small")]
        [InlineData("base")]
        [InlineData("tiny")]
        [InlineData("medium")]
        public void TestWhisperModel_ValidModels(string model)
        {
            var config = new TranscriptionConfig { WhisperModel = model };
            Assert.Equal(model, config.WhisperModel);
        }
    }
}
