using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Remembers which actual PRO client process (by PID) each numbered slot
    /// (1-ClientSelectorViewModel.MaxClients) was last bound to, so re-opening
    /// the client picker can re-offer the SAME physical window to a slot
    /// instead of re-deriving it purely from PID sort order every time.
    ///
    /// Why this exists: ProWindowFinder.FindAllProWindows sorts every
    /// currently-running PROClient window by PID, and ClientSelectorViewModel
    /// used to index straight into that sorted list by (clientNumber - 1) -
    /// meaning "Client 3" meant nothing more than "whichever window currently
    /// has the 3rd-lowest PID." That list is not stable across a client
    /// restarting: if any of the player's PRO windows closes and reopens
    /// (crash, relog, PC restart), the relaunched window gets a fresh PID
    /// that can land anywhere in sort order, which can reshuffle every slot
    /// after that point - not just the one that actually restarted. A player
    /// running four clients at once reported exactly that: Client 3/4
    /// silently pointing at the wrong window (wrong content on screen, "not
    /// tracking") after some unrelated restart earlier in the session, while
    /// Client 1/2 looked fine - because whichever two windows happened to
    /// still hold the two lowest PIDs kept working, and it was pure chance
    /// that those were the ones the player thought of as "1" and "2."
    ///
    /// ClientSelectorViewModel.LoadClients tries this mapping first for each
    /// slot - if the PID recorded here is still among the currently-running
    /// windows, that slot keeps pointing at it regardless of where it now
    /// falls in PID order - and only falls back to handing out whatever's
    /// left over, in PID order, to a slot with no recorded PID yet (its
    /// first-ever assignment) or whose remembered PID isn't running anymore
    /// (that specific window actually did close - the one case this can't
    /// paper over, since a relaunched window is genuinely a new process with
    /// no way to prove it's "the same" account from Windows-level window
    /// info alone).
    /// </summary>
    public static class ClientWindowAssignmentService
    {
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "Database"
            );

        private static readonly string SavePath =
            Path.Combine(SaveFolder, "client-window-assignments.json");

        private static Dictionary<int, int> lastKnownProcessIds = new();
        private static bool loaded;

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            if (!File.Exists(SavePath))
                return;

            try
            {
                string json = File.ReadAllText(SavePath);
                Dictionary<int, int>? saved = JsonSerializer.Deserialize<Dictionary<int, int>>(json);

                if (saved != null)
                    lastKnownProcessIds = saved;
            }
            catch
            {
                // Same "keep going with whatever's already in memory (empty,
                // at this point) rather than break the picker over a
                // damaged file" reasoning every other local save in this app
                // uses.
            }
        }

        /// <summary>The PID this client number was bound to the last time it was
        /// successfully assigned, or null if it has never been assigned (or that
        /// record was cleared).</summary>
        public static int? GetLastKnownProcessId(int clientNumber)
        {
            EnsureLoaded();

            return lastKnownProcessIds.TryGetValue(clientNumber, out int pid) ? pid : null;
        }

        /// <summary>Records that this client number is now bound to this PID -
        /// called right after ClientSelectorViewModel.Confirm actually selects a
        /// window, so the next time the picker opens, this slot tries to find
        /// this exact process again before falling back to PID-order position.
        ///
        /// §110: EXCLUSIVE. One PID belongs to one slot, so recording it here
        /// takes it away from any other slot that remembered it. Two slots
        /// naming the same process was always wrong - MapSlotsToWindows hands
        /// it to the lower-numbered one and the higher slot silently loses
        /// its window - and it is exactly the state "move this client onto
        /// Client 2" would otherwise create.</summary>
        public static void SetLastKnownProcessId(int clientNumber, int processId)
        {
            EnsureLoaded();

            foreach (int other in lastKnownProcessIds
                         .Where(pair => pair.Key != clientNumber && pair.Value == processId)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                lastKnownProcessIds.Remove(other);
            }

            lastKnownProcessIds[clientNumber] = processId;
            Save();
        }

        /// <summary>
        /// §110: THE slot-to-window mapping, in one place. This is the
        /// two-pass rule the picker has always used - first hand every slot
        /// back the exact window it was last bound to if that process is
        /// still running, then deal whatever is left over, in PID order, to
        /// the slots that had no claim on anything.
        ///
        /// It moved here out of ClientSelectorViewModel because Play needed
        /// the same answer and did not have it: Play assumed a single running
        /// window meant "client 1", which stopped being true when §105 turned
        /// the four slots into switchable profiles, and produced the bug
        /// where assigning Client 2 and then hunting put you back on Client
        /// 1. With one implementation, what the picker labels "Client 2" and
        /// what Play captures for client 2 cannot drift apart.
        /// </summary>
        public static Dictionary<int, ClientWindowInfo> MapSlotsToWindows(
            IReadOnlyList<ClientWindowInfo> available, int maxClients)
        {
            EnsureLoaded();

            var map = new Dictionary<int, ClientWindowInfo>();
            var unclaimed = new List<ClientWindowInfo>(available);

            // Pass 1: the sticky claims.
            for (int clientNumber = 1; clientNumber <= maxClients; clientNumber++)
            {
                int? lastPid = GetLastKnownProcessId(clientNumber);

                if (lastPid is null)
                    continue;

                ClientWindowInfo? sticky = unclaimed.FirstOrDefault(c => c.ProcessId == lastPid.Value);

                if (sticky is null)
                    continue;

                map[clientNumber] = sticky;
                unclaimed.Remove(sticky);
            }

            // Pass 2: a slot with no recorded PID yet (its first-ever
            // assignment), or whose remembered window actually closed, gets
            // whatever is left in PID order.
            int nextUnclaimed = 0;

            for (int clientNumber = 1; clientNumber <= maxClients; clientNumber++)
            {
                if (map.ContainsKey(clientNumber))
                    continue;

                if (nextUnclaimed >= unclaimed.Count)
                    continue;

                map[clientNumber] = unclaimed[nextUnclaimed];
                nextUnclaimed++;
            }

            return map;
        }

        private static void Save()
        {
            Directory.CreateDirectory(SaveFolder);

            string json = JsonSerializer.Serialize(
                lastKnownProcessIds,
                new JsonSerializerOptions { WriteIndented = true });

            DurableFile.WriteAllText(SavePath, json);
        }
    }
}
