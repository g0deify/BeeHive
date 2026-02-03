using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Server {
    public partial class UploadForm : Form {
        private TextBox txtLocalPath;
        private TextBox txtRemotePath;
        private Button btnBrowse;
        private Button btnUpload;
        private Button btnCancel;
        private Label lblLocalPath;
        private Label lblRemotePath;
        private Label lblInfo;

        private string _clientId;
        private string _username;
        private Form1 _serverForm;

        public UploadForm(string clientId, string username, Form1 serverForm) {
            _clientId = clientId;
            _username = username;
            _serverForm = serverForm;

            Initializecomponent();
        }

        private void Initializecomponent() {
            this.Text = $"Upload File to {_username}";
            this.Size = new Size(550, 250);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Info Label
            lblInfo = new Label();
            lblInfo.Text = $"Upload file to client: {_username}";
            lblInfo.Location = new Point(20, 20);
            lblInfo.Size = new Size(500, 20);
            lblInfo.Font = new Font("Segoe UI", 9F, FontStyle.Bold);

            // Local Path Label
            lblLocalPath = new Label();
            lblLocalPath.Text = "Local File Path (on server):";
            lblLocalPath.Location = new Point(20, 55);
            lblLocalPath.Size = new Size(200, 20);

            // Local Path TextBox
            txtLocalPath = new TextBox();
            txtLocalPath.Location = new Point(20, 80);
            txtLocalPath.Size = new Size(400, 25);
            txtLocalPath.Font = new Font("Consolas", 9F);
            txtLocalPath.ReadOnly = true;
            txtLocalPath.BackColor = Color.WhiteSmoke;

            // Browse Button
            btnBrowse = new Button();
            btnBrowse.Text = "Browse...";
            btnBrowse.Location = new Point(430, 78);
            btnBrowse.Size = new Size(80, 27);
            btnBrowse.Click += BtnBrowse_Click;

            // Remote Path Label
            lblRemotePath = new Label();
            lblRemotePath.Text = "Remote File Path (on client):";
            lblRemotePath.Location = new Point(20, 115);
            lblRemotePath.Size = new Size(200, 20);

            // Remote Path TextBox
            txtRemotePath = new TextBox();
            txtRemotePath.Location = new Point(20, 140);
            txtRemotePath.Size = new Size(490, 25);
            txtRemotePath.Font = new Font("Consolas", 9F);
            txtRemotePath.Text = @"C:\";

            // Upload Button
            btnUpload = new Button();
            btnUpload.Text = "Upload";
            btnUpload.Location = new Point(310, 180);
            btnUpload.Size = new Size(100, 30);
            btnUpload.BackColor = Color.DodgerBlue;
            btnUpload.ForeColor = Color.White;
            btnUpload.FlatStyle = FlatStyle.Flat;
            btnUpload.Click += BtnUpload_Click;

            // Cancel Button
            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(420, 180);
            btnCancel.Size = new Size(90, 30);
            btnCancel.BackColor = Color.Gray;
            btnCancel.ForeColor = Color.White;
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.Click += (s, e) => this.Close();

            // Add controls
            this.Controls.Add(lblInfo);
            this.Controls.Add(lblLocalPath);
            this.Controls.Add(txtLocalPath);
            this.Controls.Add(btnBrowse);
            this.Controls.Add(lblRemotePath);
            this.Controls.Add(txtRemotePath);
            this.Controls.Add(btnUpload);
            this.Controls.Add(btnCancel);
        }

        private void BtnBrowse_Click(object sender, EventArgs e) {
            using (OpenFileDialog openFileDialog = new OpenFileDialog()) {
                openFileDialog.Title = "Select File to Upload";
                openFileDialog.Filter = "All Files (*.*)|*.*";

                if (openFileDialog.ShowDialog() == DialogResult.OK) {
                    txtLocalPath.Text = openFileDialog.FileName;

                    // Auto-suggest remote path based on filename
                    string fileName = Path.GetFileName(openFileDialog.FileName);
                    if (string.IsNullOrEmpty(txtRemotePath.Text) || txtRemotePath.Text == @"C:\") {
                        txtRemotePath.Text = @"C:\temp\" + fileName;
                    }
                }
            }
        }

        private async void BtnUpload_Click(object sender, EventArgs e) {
            string localPath = txtLocalPath.Text.Trim();
            string remotePath = txtRemotePath.Text.Trim();

            if (string.IsNullOrEmpty(localPath)) {
                MessageBox.Show("Please select a local file to upload.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(remotePath)) {
                MessageBox.Show("Please enter a remote file path.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(localPath)) {
                MessageBox.Show("The selected local file does not exist.", "File Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Add quotes if paths contain spaces and don't already have quotes
            if (localPath.Contains(" ") && !localPath.StartsWith("\"")) {
                localPath = $"\"{localPath}\"";
            }

            if (remotePath.Contains(" ") && !remotePath.StartsWith("\"")) {
                remotePath = $"\"{remotePath}\"";
            }

            string command = $"upload {localPath} {remotePath}";

            await _serverForm.SendCommandToClient(_clientId, command);

            MessageBox.Show($"Upload command sent!\nFrom: {localPath}\nTo: {remotePath}\n\nCheck the chat tab for upload status.",
                "Upload Initiated", MessageBoxButtons.OK, MessageBoxIcon.Information);

            this.Close();
        }
    }
}