using Foot_Tracker.Models;

namespace Foot_Tracker.Forms.Counterparts
{
    public partial class CounterpartHoverForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        public CounterpartHoverForm()
        {
            InitializeComponent();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;

                parameters.ExStyle |= WS_EX_NOACTIVATE;
                parameters.ExStyle |= WS_EX_TOOLWINDOW;

                return parameters;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }

            base.WndProc(ref m);
        }

        // This is the method Counterparts.cs is trying to call.
        public void DisplayEntry(
            CounterpartEntry entry,
            Image? image)
        {
            lblPokemonName.Text = entry.Name;

            lblSpawnLocations.Text =
                entry.SpawnLocations.Count > 0
                    ? string.Join(
                        Environment.NewLine,
                        entry.SpawnLocations)
                    : "No spawns available.";

            lblNotes.Text =
                string.IsNullOrWhiteSpace(entry.Notes)
                    ? "Notes: None"
                    : $"Notes: {entry.Notes}";

            picPokemon.Image?.Dispose();
            picPokemon.Image = image;
        }
    }
}