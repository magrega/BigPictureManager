namespace BigPictureManager
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            this.audioDeviceList = new System.Windows.Forms.ComboBox();
            this.turnOffBT = new System.Windows.Forms.CheckBox();
            this.notifyIcon1 = new System.Windows.Forms.NotifyIcon(this.components);
            this.SuspendLayout();
            // 
            // audioDeviceList
            // 
            this.audioDeviceList.FormattingEnabled = true;
            this.audioDeviceList.Location = new System.Drawing.Point(11, 11);
            this.audioDeviceList.Margin = new System.Windows.Forms.Padding(2);
            this.audioDeviceList.Name = "audioDeviceList";
            this.audioDeviceList.Size = new System.Drawing.Size(204, 21);
            this.audioDeviceList.TabIndex = 1;
            // 
            // turnOffBT
            // 
            this.turnOffBT.AutoSize = true;
            this.turnOffBT.Location = new System.Drawing.Point(12, 37);
            this.turnOffBT.Name = "turnOffBT";
            this.turnOffBT.Size = new System.Drawing.Size(135, 17);
            this.turnOffBT.TabIndex = 2;
            this.turnOffBT.Text = "Turn off BT on app exit";
            this.turnOffBT.UseVisualStyleBackColor = true;
            // 
            // notifyIcon1
            // 
            this.notifyIcon1.Icon = ((System.Drawing.Icon)(resources.GetObject("notifyIcon1.Icon")));
            this.notifyIcon1.Text = "BP Audio Manager";
            this.notifyIcon1.Visible = true;
            this.notifyIcon1.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.notifyIcon1_MouseDoubleClick);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(226, 60);
            this.Controls.Add(this.turnOffBT);
            this.Controls.Add(this.audioDeviceList);
            this.Margin = new System.Windows.Forms.Padding(2);
            this.Name = "Form1";
            this.Text = "BPAudioManager";
            this.Load += new System.EventHandler(this.Form1_Load);
            this.Resize += new System.EventHandler(this.Form1_Resize);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.ComboBox audioDeviceList;
        private System.Windows.Forms.CheckBox turnOffBT;
        private System.Windows.Forms.NotifyIcon notifyIcon1;
    }
}

