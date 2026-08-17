using Foot_Tracker.Models;
using Foot_Tracker.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Foot_Tracker.Forms.Pokedex;

namespace Foot_Tracker
{
    public partial class PokemonSelectorForm : Form
    {
        public string? SelectedPokemon { get; private set; }

        private readonly System.Windows.Forms.Timer searchDelayTimer =
    new();

        private Panel? selectedCard;

        private string? pendingSelection;
        public PokemonSelectorForm()
        {
            InitializeComponent();
        }

        private void PokemonSelectorForm_Load(
            object sender,
            EventArgs e)
        {
            searchDelayTimer.Interval = 200;

            searchDelayTimer.Tick +=
                SearchDelayTimer_Tick;

            // Start with an empty result panel.
            flpPokemon.Controls.Clear();

            txtSearch.Focus();
        }

        private void CreatePokemonCard(
            PokemonLibraryEntry pokemon)
        {
            var card = new Panel
            {
                Width = 100,
                Height = 120,
                Margin = new Padding(8),
                Tag = pokemon.Name,
                Cursor = Cursors.Hand,
                BorderStyle = BorderStyle.None
            };

            var spriteBox = new PictureBox
            {
                Width = 80,
                Height = 80,
                Left = 10,
                Top = 5,
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = PokemonSpriteService.GetSprite(pokemon.Name),
                Tag = pokemon.Name,
                Cursor = Cursors.Hand
            };

            var nameLabel = new Label
            {
                Text = pokemon.Name,
                AutoSize = false,
                Width = 100,
                Height = 25,
                Left = 0,
                Top = 88,
                TextAlign = ContentAlignment.MiddleCenter,
                Tag = pokemon.Name,
                Cursor = Cursors.Hand
            };

            void SingleClick(object? sender, EventArgs e)
            {
                SelectPokemonCard(card, pokemon);
            }

            void DoubleClick(object? sender, EventArgs e)
            {
                SelectedPokemon = pokemon.Name;
                DialogResult = DialogResult.OK;
                Close();
            }

            card.Click += SingleClick;
            spriteBox.Click += SingleClick;
            nameLabel.Click += SingleClick;

            card.DoubleClick += DoubleClick;
            spriteBox.DoubleClick += DoubleClick;
            nameLabel.DoubleClick += DoubleClick;

            card.Controls.Add(spriteBox);
            card.Controls.Add(nameLabel);

            flpPokemon.Controls.Add(card);
        }

        private void CreateRegionalPokemonCard(
    PokemonFormEntry pokemon)
        {
            var card = new Panel
            {
                Width = 100,
                Height = 120,
                Margin = new Padding(8),
                Tag = pokemon.Name,
                Cursor = Cursors.Hand,
                BorderStyle = BorderStyle.None
            };

            var spriteBox = new PictureBox
            {
                Width = 80,
                Height = 80,
                Left = 10,
                Top = 5,
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = PokemonSpriteService.GetEncounterSprite(
                    pokemon.Name
                ),
                Tag = pokemon.Name,
                Cursor = Cursors.Hand
            };

            var nameLabel = new Label
            {
                Text = pokemon.Name,
                AutoSize = false,
                Width = 100,
                Height = 25,
                Top = 88,
                TextAlign = ContentAlignment.MiddleCenter,
                Tag = pokemon.Name,
                Cursor = Cursors.Hand
            };

            void SingleClick(object? sender, EventArgs e)
            {
                SelectRegionalPokemonCard(
                    card,
                    pokemon
                );
            }

            void DoubleClick(object? sender, EventArgs e)
            {
                SelectedPokemon = pokemon.Name;
                DialogResult = DialogResult.OK;
                Close();
            }

            card.Click += SingleClick;
            spriteBox.Click += SingleClick;
            nameLabel.Click += SingleClick;

            card.DoubleClick += DoubleClick;
            spriteBox.DoubleClick += DoubleClick;
            nameLabel.DoubleClick += DoubleClick;

            card.Controls.Add(spriteBox);
            card.Controls.Add(nameLabel);

            flpPokemon.Controls.Add(card);
        }

        private void SelectPokemonCard(
            Panel card,
            PokemonLibraryEntry pokemon)
        {
            // Clicking the already-selected Pokémon again
            // deselects it and closes the forms popup.
            if (selectedCard == card)
            {
                card.BorderStyle =
                    BorderStyle.None;

                selectedCard = null;
                pendingSelection = null;

                if (formsPopup != null &&
                    !formsPopup.IsDisposed)
                {
                    formsPopup.Close();
                    formsPopup.Dispose();
                    formsPopup = null;
                }

                return;
            }

            // Clear previous selection.
            if (selectedCard != null &&
                !selectedCard.IsDisposed)
            {
                selectedCard.BorderStyle =
                    BorderStyle.None;
            }

            selectedCard = card;

            card.BorderStyle =
                BorderStyle.Fixed3D;

            pendingSelection =
                pokemon.Name;

            ShowPokemonFormsPopup(
                pokemon,
                card
            );
        }

        private void SelectRegionalPokemonCard(
    Panel card,
    PokemonFormEntry pokemon)
        {
            if (selectedCard == card)
            {
                card.BorderStyle = BorderStyle.None;

                selectedCard = null;
                pendingSelection = null;

                if (formsPopup != null &&
                    !formsPopup.IsDisposed)
                {
                    formsPopup.Close();
                    formsPopup.Dispose();
                    formsPopup = null;
                }

                return;
            }

            if (selectedCard != null &&
                !selectedCard.IsDisposed)
            {
                selectedCard.BorderStyle =
                    BorderStyle.None;
            }

            selectedCard = card;
            card.BorderStyle = BorderStyle.Fixed3D;

            pendingSelection = pokemon.Name;

            ShowRegionalPokemonPopup(
                pokemon,
                card
            );
        }


        private PokemonFormsPopup? formsPopup;

        private void ShowPokemonFormsPopup(
            PokemonLibraryEntry pokemon,
            Control selectedControl)
        {
            if (formsPopup != null &&
                !formsPopup.IsDisposed)
            {
                formsPopup.Close();
                formsPopup.Dispose();
            }

            formsPopup =
                new PokemonFormsPopup(pokemon);

            Point position =
                selectedControl.PointToScreen(
                    new Point(
                        selectedControl.Width + 8,
                        0
                    )
                );

            formsPopup.Location =
                position;

            formsPopup.Show(this);
        }

        private void ShowRegionalPokemonPopup(
    PokemonFormEntry pokemon,
    Control selectedControl)
        {
            if (formsPopup != null &&
                !formsPopup.IsDisposed)
            {
                formsPopup.Close();
                formsPopup.Dispose();
            }

            formsPopup =
                new PokemonFormsPopup(pokemon);

            Point position =
                selectedControl.PointToScreen(
                    new Point(
                        selectedControl.Width + 8,
                        0
                    )
                );

            formsPopup.Location =
                position;

            formsPopup.Show(this);
        }


        private void PokemonCard_Click(
    object? sender,
    EventArgs e)
        {
            if (sender is not Control control)
                return;

            pendingSelection =
                control.Tag?.ToString();
        }

        private void PokemonCard_DoubleClick(
    object? sender,
    EventArgs e)
        {
            if (sender is not Control control)
                return;

            string? pokemon =
                control.Tag?.ToString();

            if (string.IsNullOrWhiteSpace(pokemon))
                return;

            SelectedPokemon = pokemon;

            DialogResult = DialogResult.OK;
            Close();
        }

        private void btnSelect_Click(
    object sender,
    EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(pendingSelection))
            {
                MessageBox.Show(
                    "Select a Pokémon first.",
                    "Pokémon Selection",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return;
            }

            SelectedPokemon = pendingSelection;

            DialogResult = DialogResult.OK;
            Close();
        }

        private void txtSearch_TextChanged(
            object sender,
            EventArgs e)
        {
            // Restart the delay every time another key is pressed.
            searchDelayTimer.Stop();

            string search =
                txtSearch.Text.Trim();

            if (search.Length < 2)
            {
                ClearPokemonResults();
                return;
            }

            searchDelayTimer.Start();
        }

        private void ClearPokemonResults()
        {
            flpPokemon.SuspendLayout();

            try
            {
                foreach (Control control in flpPokemon.Controls)
                {
                    DisposeImages(control);
                    control.Dispose();
                }

                flpPokemon.Controls.Clear();
            }
            finally
            {
                flpPokemon.ResumeLayout();
            }
        }

        private static void DisposeImages(Control control)
        {
            if (control is PictureBox pictureBox)
            {
                pictureBox.Image?.Dispose();
                pictureBox.Image = null;
            }

            foreach (Control child in control.Controls)
            {
                DisposeImages(child);
            }
        }

        private void LoadSearchResults(string search)
        {
            ClearPokemonResults();

            flpPokemon.SuspendLayout();

            try
            {
                var speciesMatches =
                    PokemonSpriteService.AllPokemon
                        .Where(p =>
                            p.Name.Contains(
                                search,
                                StringComparison.OrdinalIgnoreCase))
                        .Take(40)
                        .ToList();

                var regionalMatches =
                    PokemonSpriteService
                        .GetHuntableRegionalForms()
                        .Where(p =>
                            p.Name.Contains(
                                search,
                                StringComparison.OrdinalIgnoreCase))
                        .Take(20)
                        .ToList();

                foreach (var pokemon in speciesMatches)
                {
                    CreatePokemonCard(pokemon);
                }

                foreach (var pokemon in regionalMatches)
                {
                    CreateRegionalPokemonCard(pokemon);
                }
            }
            finally
            {
                flpPokemon.ResumeLayout();
            }
        }
        private void SearchDelayTimer_Tick(
            object? sender,
            EventArgs e)
        {
            searchDelayTimer.Stop();

            string search =
                txtSearch.Text.Trim();

            if (search.Length < 2)
            {
                ClearPokemonResults();
                return;
            }

            LoadSearchResults(search);
        }
    }
}
