# The competitive-set moves, and how faithful each one is

Section 178 imported 2,574 Smogon sets and kept every move name
verbatim. Ninety-nine of those names were moves the engine had never
heard of, which left one set in seven quietly short a slot. Section 184
added all ninety-nine (plus three Hidden Power aliases so that family is
spelled the way the sets spell it), and every set is now fully playable.

Nothing is missing. This file is the honest half of that: which of them
behave exactly like the real move, and which are approximations.

- 42 exact
- 10 needed a new effect, and got a real one
- 50 approximated - listed below with what is missing

An approximation always keeps the real type, power, accuracy and PP. What
it drops is a rider the simulator has no machinery for. None of them is a
guess: if the engine cannot express the real behaviour, the move does the
nearest honest thing and it is written down here.

## Approximated

| move | what the simulator does not do |
| ---- | ------------------------------ |
| Autotomize | the weight halving has no effect to model |
| Barb Barrage | does not double against an already poisoned target |
| Beat Up | hits two to six times rather than once per healthy party member, and each hit uses the user's Attack |
| Blood Moon | can be used on consecutive turns |
| Bolt Beak | does not double when moving first |
| Burning Bulwark | blocks like Protect but does not burn an attacker that makes contact |
| Conversion | the user's type is fixed, so there is nothing to convert to |
| Corrosive Gas | removes the held item rather than destroying it |
| Darkest Lariat | does not ignore the target's stat changes |
| Double Shock | the user keeps its Electric typing afterwards |
| Entrainment | gives the target Insomnia rather than the user's own ability |
| Fickle Beam | never rolls the doubled hit |
| Fishious Rend | does not double when moving first |
| Flower Trick | modelled as never missing and a very high crit rate rather than a guaranteed crit |
| Focus Energy | the crit-rate volatile is not modelled |
| Foresight | type immunities are not lifted |
| Gigaton Hammer | can be used on consecutive turns |
| Glaive Rush | the user does not take doubled damage on the following turn |
| Hydro Steam | is not boosted by sun |
| Hyperspace Fury | the Hoopa-only restriction is not enforced |
| Ivy Cudgel | stays Grass whichever mask is held |
| Lunar Blessing | heals and cures the user rather than the whole party |
| Lunar Dance | does not also restore the replacement's PP |
| Magic Coat | status moves are not bounced back |
| Mighty Cleave | does not cut through Protect |
| Moongeist Beam | does not ignore the target's ability |
| Nature Power | there is no terrain-dependent move table |
| Photon Geyser | does not ignore the target's ability |
| Plasma Fists | does not turn Normal moves Electric for the turn |
| Psyblade | is not boosted by Electric Terrain |
| Punishment | does not scale with the target's stat boosts |
| Rage Fist | does not scale with the hits the user has taken |
| Recycle | a consumed item is not restored |
| Revelation Dance | stays Normal rather than taking the user's type |
| Revival Blessing | heals the user instead of reviving a fainted team member |
| Rising Voltage | is not doubled on Electric Terrain |
| Shell Side Arm | always resolves as Special rather than picking the better of the two |
| Shore Up | heals half whatever the weather, not two thirds in sand |
| Sparkling Aria | does not cure the target's burn |
| Spectral Thief | does not steal the target's stat boosts |
| Sunsteel Strike | does not ignore the target's ability |
| Surging Strikes | a very high crit rate rather than a guaranteed crit on every hit |
| Tera Starstorm | stays Normal and single-target |
| Terrain Pulse | neither changes type nor doubles on terrain |
| Thousand Arrows | does not hit or ground airborne targets |
| Thunderclap | has priority every turn rather than only against an attacking target |
| Tidy Up | clears only the user's own hazards |
| Transform | copying another Pokemon is not modelled, so it does nothing |
| Triple Axel | three hits of the average 40 rather than an escalating 20, 40, 60 - the same 120 total |
| Wicked Blow | a very high crit rate rather than a guaranteed crit |

## Given a new effect

- Aurora Veil
- Chilly Reception
- Final Gambit
- First Impression
- Heart Swap
- Oblivion Wing
- Refresh
- Shed Tail
- Sticky Web
- Strength Sap

## Exact

Anchor Shot, Aromatherapy, Bleakwind Storm, Bonemerang, Ceaseless Edge, Clangorous Soul, Double Iron Bash, Dragon Energy, Freeze Shock, Fusion Bolt, Fusion Flare, Geomancy, Hidden Power Electric, Hidden Power Fighting, Hidden Power Fire, Hidden Power Grass, Hidden Power Ground, Hidden Power Ice, Hidden Power Psychic, Hold Hands, Jaw Lock, Jungle Healing, Kowtow Cleave, Malignant Chain, Matcha Gotcha, Mega Kick, Mind Blown, Nature's Madness, Origin Pulse, Overdrive, Population Bomb, Power-Up Punch, Precipice Blades, Ruination, Searing Shot, Shadow Bone, Spirit Shackle, Steam Eruption, Strange Steam, Switcheroo, Take Heart, Thousand Waves.
