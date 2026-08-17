using Foot_Tracker.Models;
using Foot_Tracker.Services;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Foot_Tracker.Forms.Pokedex
{
    public partial class PokemonFormsPopup : Form
    {
        private readonly FlowLayoutPanel flowForms;

        public PokemonFormsPopup(
            PokemonFormEntry form)
        {
            InitializeComponent();

            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;

            Text = $"{form.Name} Forms";

            flowForms = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(8)
            };

            Controls.Add(flowForms);

            Shown += async (_, _) =>
            {
                await LoadRegionalFormAsync(form);
            };
        }

        public PokemonFormsPopup(
    PokemonLibraryEntry species)
        {
            InitializeComponent();

            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;

            Text = $"{species.Name} Forms";

            flowForms = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(8)
            };

            Controls.Add(flowForms);

            Shown += async (_, _) =>
            {
                await LoadFormsAsync(species);
            };
        }

        private async Task LoadRegionalFormAsync(
    PokemonFormEntry form)
        {
            flowForms.SuspendLayout();

            try
            {
                // The selected regional Pokémon itself.
                AddFormCard(
                    GetFormTag(form),
                    form.Name,
                    PokemonSpriteService.GetEncounterSprite(
                        form.Name
                    )
                );

                await Task.Yield();

                // IMPORTANT:
                // Ask specifically for counterparts belonging
                // to this regional form.
                foreach (var counterpart in
                         CounterpartSpriteService.GetForPokemon(
                             form.Name))
                {
                    AddFormCard(
                        counterpart.Event,
                        counterpart.Name,
                        CounterpartSpriteService.GetImage(
                            counterpart
                        )
                    );

                    await Task.Yield();
                }
            }
            finally
            {
                flowForms.ResumeLayout();

                ResizePopupToContent();
            }
        }

        private async Task LoadFormsAsync(
            PokemonLibraryEntry species)
        {
            flowForms.SuspendLayout();

            try
            {
                // Normal sprite first
                AddFormCard(
                    "Normal",
                    species.Name,
                    PokemonSpriteService.GetSprite(
                        species.Name
                    )
                );

                // Let the popup actually paint before loading more.
                await Task.Yield();

                var forms =
                    PokemonSpriteService
                        .GetFormsForSpecies(species.Name)
                        .Take(30)
                        .ToList();

                foreach (var form in forms)
                {
                    AddFormCard(
                        GetFormTag(form),
                        form.Name,
                        PokemonSpriteService.GetEncounterSprite(
                            form.Name
                        )
                    );

                    await Task.Yield();
                }

                var counterparts =
                    CounterpartSpriteService
                        .GetForPokemon(species.Name)
                        .Take(50)
                        .ToList();

                foreach (var counterpart in counterparts)
                {
                    AddFormCard(
                        counterpart.Event,
                        counterpart.Name,
                        CounterpartSpriteService.GetImage(
                            counterpart
                        )
                    );
                    ResizePopupToContent();
                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not load forms for {species.Name}.\n\n{ex.Message}",
                    "Form Loading Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
            finally
            {
                flowForms.ResumeLayout();
            }
        }
        private void ResizePopupToContent()
        {
            int cardCount = flowForms.Controls.Count;

            if (cardCount == 0)
                return;

            const int cardWidth = 130;
            const int cardHeight = 145;

            const int maxCardsPerRow = 6;
            const int maxVisibleRows = 3;

            int cardsInRow =
                Math.Min(cardCount, maxCardsPerRow);

            int rows =
                (int)Math.Ceiling(
                    cardCount / (double)maxCardsPerRow
                );

            int visibleRows =
                Math.Min(rows, maxVisibleRows);

            Width =
                (cardsInRow * cardWidth) + 25;

            Height =
                (visibleRows * cardHeight) + 45;
        }

        private void AddFormCard(
            string tag,
            string name,
            Image? image)
        {
            var card = new Panel
            {
                Width = 120,
                Height = 135,
                Margin = new Padding(5)
            };

            var picture = new PictureBox
            {
                Width = 85,
                Height = 80,
                Left = 17,
                Top = 5,
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = image
            };

            var label = new Label
            {
                Left = 2,
                Top = 88,
                Width = 116,
                Height = 42,
                AutoSize = false,
                TextAlign = ContentAlignment.TopCenter,
                Text = $"[{tag}]\n{name}"
            };

            card.Controls.Add(picture);
            card.Controls.Add(label);

            flowForms.Controls.Add(card);
        }

        private static string GetFormTag(
            PokemonFormEntry form)
        {
            string name = form.Name;

            if (name.Contains(
                    "Alolan",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Alola",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Alolan";
            }

            if (name.Contains(
                    "Galarian",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Galar",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Galarian";
            }

            if (name.Contains(
                    "Hisuian",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Hisui",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Hisuian";
            }

            if (name.Contains(
                    "Mega",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Mega";
            }

            if (name.Contains(
                    "Gmax",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "G-Max";
            }

            return "Form";
        }
    }
}