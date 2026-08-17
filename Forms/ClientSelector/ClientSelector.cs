using Foot_Tracker.Services;
using Foot_Tracker.Tracking;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Foot_Tracker.Forms.ClientSelector
{
    public partial class ClientSelector : Form
    {
        private List<ProWindowFinder.ProClientInfo>
    availableClients =
        new List<ProWindowFinder.ProClientInfo>();
        public ClientSelector()
        {

            InitializeComponent();

            ThemeManager.ApplyToForm(this);
        }
        public int SelectedClientNumber
        {
            get;
            private set;
        }
        private void ClientSelectionForm_Load(
    object sender,
    EventArgs e)
        {
            LoadClients();
        }

        private void LoadClients()
        {
            availableClients =
                ProWindowFinder.FindAllProWindows();

            Client1RadioButton.Enabled =
                availableClients.Count >= 1;

            Client2RadioButton.Enabled =
                availableClients.Count >= 2;


            if (availableClients.Count >= 1)
            {
                Client1RadioButton.Text =
                    $"Client 1 - PID " +
                    $"{availableClients[0].ProcessId}";
            }
            else
            {
                Client1RadioButton.Text =
                    "Client 1 - Not Found";
            }


            if (availableClients.Count >= 2)
            {
                Client2RadioButton.Text =
                    $"Client 2 - PID " +
                    $"{availableClients[1].ProcessId}";
            }
            else
            {
                Client2RadioButton.Text =
                    "Client 2 - Not Found";
            }
        }
        private void button1_Click(object sender, EventArgs e)
        {
            ProWindowFinder.ProClientInfo?
                selectedClient = null;

            int clientNumber = 0;


            if (Client1RadioButton.Checked &&
                availableClients.Count >= 1)
            {
                selectedClient =
                    availableClients[0];

                clientNumber = 1;
            }
            else if (
                Client2RadioButton.Checked &&
                availableClients.Count >= 2)
            {
                selectedClient =
                    availableClients[1];

                clientNumber = 2;
            }


            if (selectedClient == null)
            {
                MessageBox.Show(
                    "Please select a PRO client.",
                    "Assign Client",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return;
            }


            ScreenCapture.SelectProWindow(
                selectedClient.Handle
            );

            SelectedClientNumber =
                clientNumber;

            DialogResult =
                DialogResult.OK;

            Close();
        }
    }
}
