using Foot_Tracker.Models;
using Foot_Tracker.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Foot_Tracker.Forms.Lifetime_Stats
{
    public partial class HuntingStats : Form
    {
        public HuntingStats()
        {
            InitializeComponent();

            ThemeManager.ApplyToForm(this);

            LoadLifetimeStats();
        }

        private void LoadLifetimeStats()
        {
            LifetimeStats stats =
                LifetimeStatsService.Load();

            TotalTimeHuntingValue.Text =
                stats.TotalHuntingTime
                    .ToString(@"dd\.hh\:mm\:ss");

            TotalPokemonValue.Text =
                stats.TotalEncounters.ToString();

            ShinyPokemonValue.Text =
                stats.ShinyEncounters.ToString();

            EventFormsValue.Text =
                stats.FormEncounters.ToString();

            SuccessfulCatchesValue.Text =
                stats.SuccessfulCatches.ToString();

            FailedCatchesValue.Text =
                stats.FailedCatches.ToString();

            int totalCatchAttempts =
                (int)(
                    stats.SuccessfulCatches +
                    stats.FailedCatches
                );

            double catchRate =
                totalCatchAttempts > 0
                    ? stats.SuccessfulCatches /
                      (double)totalCatchAttempts *
                      100.0
                    : 0.0;

            CatchRateValue.Text =
                $"{catchRate:F2}%";

            double totalRare =
                stats.ShinyEncounters +
                stats.FormEncounters;

            if (totalRare > 0)
            {
                double oneIn =
                    stats.TotalEncounters /
                    totalRare;

                ShinyFormRateValue.Text =
                    $"1 in {oneIn:F0}";
            }
            else
            {
                ShinyFormRateValue.Text =
                    "N/A";
            }
        }
    }
}
