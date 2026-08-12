namespace Foot_Tracker.Forms.Counterparts
{
    partial class CounterpartHoverForm
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
            picPokemon = new PictureBox();
            lblPokemonName = new Label();
            lblSpawnHeader = new Label();
            lblSpawnLocations = new Label();
            lblNotes = new Label();
            ((System.ComponentModel.ISupportInitialize)picPokemon).BeginInit();
            SuspendLayout();
            // 
            // picPokemon
            // 
            picPokemon.BackColor = Color.Transparent;
            picPokemon.Location = new Point(17, 36);
            picPokemon.Name = "picPokemon";
            picPokemon.Size = new Size(120, 120);
            picPokemon.SizeMode = PictureBoxSizeMode.AutoSize;
            picPokemon.TabIndex = 0;
            picPokemon.TabStop = false;
            // 
            // lblPokemonName
            // 
            lblPokemonName.BackColor = Color.Transparent;
            lblPokemonName.Font = new Font("Segoe UI", 11F, FontStyle.Bold | FontStyle.Underline);
            lblPokemonName.ForeColor = Color.Black;
            lblPokemonName.Location = new Point(6, 10);
            lblPokemonName.Name = "lblPokemonName";
            lblPokemonName.Size = new Size(142, 23);
            lblPokemonName.TabIndex = 1;
            lblPokemonName.Text = "Mega Aerodactyl";
            lblPokemonName.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // lblSpawnHeader
            // 
            lblSpawnHeader.BackColor = Color.Transparent;
            lblSpawnHeader.Font = new Font("Segoe UI", 11F, FontStyle.Bold | FontStyle.Underline);
            lblSpawnHeader.ForeColor = Color.Black;
            lblSpawnHeader.Location = new Point(237, 9);
            lblSpawnHeader.Name = "lblSpawnHeader";
            lblSpawnHeader.Size = new Size(142, 23);
            lblSpawnHeader.TabIndex = 4;
            lblSpawnHeader.Text = "Spawn Locations";
            lblSpawnHeader.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // lblSpawnLocations
            // 
            lblSpawnLocations.BackColor = Color.Transparent;
            lblSpawnLocations.Font = new Font("Segoe UI", 9F);
            lblSpawnLocations.ForeColor = Color.Black;
            lblSpawnLocations.Location = new Point(204, 36);
            lblSpawnLocations.Name = "lblSpawnLocations";
            lblSpawnLocations.Size = new Size(210, 77);
            lblSpawnLocations.TabIndex = 5;
            lblSpawnLocations.Text = "Locations";
            lblSpawnLocations.TextAlign = ContentAlignment.TopCenter;
            // 
            // lblNotes
            // 
            lblNotes.BackColor = Color.Transparent;
            lblNotes.Font = new Font("Segoe UI", 9F);
            lblNotes.ForeColor = Color.Black;
            lblNotes.Location = new Point(143, 101);
            lblNotes.Name = "lblNotes";
            lblNotes.Size = new Size(336, 55);
            lblNotes.TabIndex = 6;
            lblNotes.Text = "Notes: Spawns on Pinkan Island all year. See Guides/Pinkan.";
            // 
            // CounterpartHoverForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.DimGray;
            BackgroundImageLayout = ImageLayout.Stretch;
            ClientSize = new Size(494, 170);
            Controls.Add(lblNotes);
            Controls.Add(lblSpawnLocations);
            Controls.Add(lblSpawnHeader);
            Controls.Add(lblPokemonName);
            Controls.Add(picPokemon);
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            Name = "CounterpartHoverForm";
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "CounterpartHoverForm";
            TopMost = true;
            ((System.ComponentModel.ISupportInitialize)picPokemon).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private PictureBox picPokemon;
        private Label lblPokemonName;
        private Label lblSpawnHeader;
        private Label lblSpawnLocations;
        private Label lblNotes;
    }
}