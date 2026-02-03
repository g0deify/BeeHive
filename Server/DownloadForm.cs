using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Server {
    public partial class DownloadForm : Form {
        private TextBox txtRemotePath;
        private Button btnBrowse;
        private Button btnDownload;
        private Button btnCancel;
        private Button btnOpenFolder; // NEW
        private Label lblRemotePath;
        private Label lblInfo;

        private string _clientId;
        private string _username;
        private Form1 _serverForm;

        public DownloadForm(string clientId, string username, Form1 serverForm) {
            _clientId = clientId;
            _username = username;
            _serverForm = serverForm;

            Initializecomponent();
        }

        private void Initializecomponent() {
            this.Text = $"Download File from {_username}";
            this.Size = new Size(550, 200);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Info Label
            lblInfo = new Label();
            lblInfo.Text = $"Download file from client: {_username}";
            lblInfo.Location = new Point(20, 20);
            lblInfo.Size = new Size(500, 20);
            lblInfo.Font = new Font("Segoe UI", 9F, FontStyle.Bold);

            // Remote Path Label
            lblRemotePath = new Label();
            lblRemotePath.Text = "Remote File Path (on client):";
            lblRemotePath.Location = new Point(20, 55);
            lblRemotePath.Size = new Size(200, 20);

            // Remote Path TextBox
            txtRemotePath = new TextBox();
            txtRemotePath.Location = new Point(20, 80);
            txtRemotePath.Size = new Size(490, 25);
            txtRemotePath.Font = new Font("Consolas", 9F);
            txtRemotePath.Text = @"C:\";

            // Open Folder Button - NEW
            btnOpenFolder = new Button();
            btnOpenFolder.Text = "📂 Open Downloads";
            btnOpenFolder.Location = new Point(20, 120);
            btnOpenFolder.Size = new Size(130, 30);
            btnOpenFolder.BackColor = Color.ForestGreen;
            btnOpenFolder.ForeColor = Color.White;
            btnOpenFolder.FlatStyle = FlatStyle.Flat;
            btnOpenFolder.Click += BtnOpenFolder_Click;

            // Download Button
            btnDownload = new Button();
            btnDownload.Text = "Download";
            btnDownload.Location = new Point(310, 120);
            btnDownload.Size = new Size(100, 30);
            btnDownload.BackColor = Color.DodgerBlue;
            btnDownload.ForeColor = Color.White;
            btnDownload.FlatStyle = FlatStyle.Flat;
            btnDownload.Click += BtnDownload_Click;

            // Cancel Button
            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(420, 120);
            btnCancel.Size = new Size(90, 30);
            btnCancel.BackColor = Color.Gray;
            btnCancel.ForeColor = Color.White;
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.Click += (s, e) => this.Close();

            // Add controls
            this.Controls.Add(lblInfo);
            this.Controls.Add(lblRemotePath);
            this.Controls.Add(txtRemotePath);
            this.Controls.Add(btnOpenFolder); // NEW
            this.Controls.Add(btnDownload);
            this.Controls.Add(btnCancel);
        }

        // NEW - Open downloads folder
        private void BtnOpenFolder_Click(object sender, EventArgs e) {
            try {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                string downloadDir = Path.Combine(exeDir, "Downloads", _clientId);
                
                if (!Directory.Exists(downloadDir)) {
                    Directory.CreateDirectory(downloadDir);
                }
                
                Process.Start("explorer.exe", downloadDir);
            }
            catch (Exception ex) {
                MessageBox.Show($"Error opening folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void BtnDownload_Click(object sender, EventArgs e) {
            string remotePath = txtRemotePath.Text.Trim();

            if (string.IsNullOrEmpty(remotePath)) {
                MessageBox.Show("Please enter a remote file path.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Add quotes if path contains spaces and doesn't already have quotes
            if (remotePath.Contains(" ") && !remotePath.StartsWith("\"")) {
                remotePath = $"\"{remotePath}\"";
            }

            string command = $"download {remotePath}";

            await _serverForm.SendCommandToClient(_clientId, command);

            MessageBox.Show($"Download command sent!\nFile: {remotePath}\n\nCheck the chat tab for download status.",
                "Download Initiated", MessageBoxButtons.OK, MessageBoxIcon.Information);

            this.Close();
        }
    }
}
