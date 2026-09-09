using PokemonSim.Models;
using System;
using System.Collections.Generic;

namespace PokemonSim.Engine
{
    public class EventManager
    {
        private readonly List<Action<BattleEvent, BattleState>> listeners = new();

        public void Subscribe(Action<BattleEvent, BattleState> handler)
        {
            listeners.Add(handler);
        }

        public void Dispatch(BattleEvent evt, BattleState state)
        {
            foreach (var listener in listeners)
            {
                listener(evt, state);
            }
        }
    }
}