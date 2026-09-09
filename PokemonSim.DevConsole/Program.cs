using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Simulation;

// Section 154. The engine's development harness: one narrated seeded battle,
// then the bulk random-vs-random win-rate run the old BattleSimulator
// printed. Section 155 adds the strategy ladder: the same bulk run with
// Monte Carlo on one side, the quickest way to see whether the finished
// MonteCarloStrategy actually beats the random baseline (and by how
// much) before trusting it with every boss. Usage: dotnet run
// [simulations] [seed]

int simulations = args.Length > 0 && int.TryParse(args[0], out int n) ? n : 200;
int seed = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 12345;

Console.WriteLine("PokemonSim dev console");
Console.WriteLine($"Data: {SimDataFiles.MovesPath}");

MoveDex.EnsureLoaded();
PokemonDex.EnsureLoaded();

Console.WriteLine($"Moves loaded: {MoveDex.Count} ({MoveDex.Warnings.Count} warnings)");
Console.WriteLine($"Species loaded: {PokemonDex.Count} ({PokemonDex.Warnings.Count} warnings)");

foreach (string warning in MoveDex.Warnings.Take(5))
    Console.WriteLine($"  moves.json: {warning}");

// ---- one narrated battle ----
var narrated = BattleFactory.CreateRandomBattle(seed);
var engine = new BattleEngine(narrated);

BattleOutcome outcome = engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy());

Console.WriteLine();
Console.WriteLine($"--- Sample battle (seed {seed}): {outcome} in {narrated.TurnNumber} turn(s) ---");

foreach (string line in narrated.Log.Lines)
    Console.WriteLine(line);

// ---- the bulk run ----
var template = BattleFactory.CreateRandomBattle(seed);
SimulationResult result = BattleSimulator.Run(template, simulations, seed);

Console.WriteLine();
Console.WriteLine($"Simulations: {result.Simulations} (base seed {seed})");
Console.WriteLine($"Player 1 wins: {result.Player1Wins} ({(double)result.Player1Wins / result.Simulations:P1})");
Console.WriteLine($"Player 2 wins: {result.Player2Wins} ({(double)result.Player2Wins / result.Simulations:P1})");
Console.WriteLine($"Draws: {result.Draws}   Cancelled: {result.Cancelled}   Avg turns: {result.AverageTurns:F1}");

// ---- section 155: Monte Carlo (player 2) vs the random baseline ----
// Fewer battles than the random-vs-random run - each MC decision costs
// real rollouts. A quick per-decision budget keeps the whole ladder
// under a minute on an ordinary machine.
int mcBattles = Math.Max(10, simulations / 10);
var mcConfig = new MonteCarloConfig();

SimulationResult ladder = BattleSimulator.Run(
    BattleFactory.CreateRandomBattle(seed),
    mcBattles,
    seed,
    player2Strategy: i => new MonteCarloStrategy(mcConfig, seed: seed + 1000 + i));

Console.WriteLine();
Console.WriteLine($"Strategy ladder: RandomMoveStrategy (P1) vs MonteCarloStrategy (P2), {ladder.Simulations} battles");
Console.WriteLine($"Random wins: {ladder.Player1Wins} ({(double)ladder.Player1Wins / ladder.Simulations:P1})");
Console.WriteLine($"Monte Carlo wins: {ladder.Player2Wins} ({(double)ladder.Player2Wins / ladder.Simulations:P1})");
Console.WriteLine($"Draws: {ladder.Draws}   Cancelled: {ladder.Cancelled}   Avg turns: {ladder.AverageTurns:F1}");