using AudioSwitcher.AudioApi.CoreAudio;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace BigPictureManager
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            IEnumerable<CoreAudioDevice> devices = new CoreAudioController().GetPlaybackDevices();


            foreach (CoreAudioDevice d in devices)
            {
                if (!d.IsDefaultDevice)
                {
                    Console.WriteLine(d.FullName);
                    d.SetAsDefault();
                    return;
                }
            }

        }
    }
}
