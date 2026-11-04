using System;
using System.Drawing;
using System.Windows.Forms;

namespace BigPictureManager
{
    /// <summary>Small top-most progress window used while a multi-step tray action runs.</summary>
    internal sealed class ProgressOverlay : Form
    {
        private readonly Label _statusLabel;
        private readonly ProgressBar _progressBar;

        public ProgressOverlay(string title)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(360, 104);
            BackColor = TrayMenuTheme.Background;
            Font = new Font("Segoe UI", 9.75f);

            _statusLabel = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TrayMenuTheme.ItemText,
                Location = new Point(16, 16),
                Size = new Size(328, 36),
                Text = title,
            };

            _progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Location = new Point(16, 62),
                Size = new Size(328, 18),
            };

            Controls.Add(_statusLabel);
            Controls.Add(_progressBar);
        }

        public void SetStatus(string status)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetStatus(status)));
                return;
            }

            _statusLabel.Text = status;
        }
    }
}
