using UnityEngine;

namespace ThePitt.Core
{
    /// <summary>
    /// Which world the game scene should build, and for whom: set by the menu (new game or a save) before the
    /// game scene loads, read by WorldBootstrap as it builds the city. A character's world is just its seed.
    /// </summary>
    public static class WorldRequest
    {
        public const string MenuScene = "Menu";
        /// <summary>The city and its loader: loaded first, it builds the world and then brings in <see cref="GameScene"/>.</summary>
        public const string WorldScene = "World";
        /// <summary>The player, HUD and the game's systems, loaded on top of the built city.</summary>
        public const string GameScene = "Slice";

        /// <summary>Whose game this is; names their save folder. Null when playing the game scene straight from the editor.</summary>
        public static string Character { get; private set; }
        public static int Seed { get; private set; }
        /// <summary>The generator version to build the world at: an older save keeps the city it was made in.</summary>
        public static int GeneratorVersion { get; private set; } = World.PittGreybox.GeneratorVersion;
        /// <summary>A brand-new character: they start inside their own property, and get a first save.</summary>
        public static bool IsNewGame { get; private set; }
        /// <summary>Whether anything asked for a world (otherwise the game scene was opened directly).</summary>
        public static bool Active { get; private set; }
        /// <summary>
        /// The dev world rather than a city from a seed: a small test map with an example of everything in the game
        /// (see PittDevWorld). Development builds and the editor only. Nothing saves there.
        /// </summary>
        public static bool IsDev { get; private set; }

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Character = null;
            Seed = 0;
            GeneratorVersion = World.PittGreybox.GeneratorVersion;
            IsNewGame = false;
            Active = false;
            IsDev = false;
        }

        public static void Set(string character, int seed, bool newGame, int generatorVersion = World.PittGreybox.GeneratorVersion)
        {
            Character = character;
            Seed = seed;
            GeneratorVersion = generatorVersion;
            IsNewGame = newGame;
            Active = true;
            IsDev = false;
        }

        /// <summary>The dev world, with no character (so nothing saves).</summary>
        public static void SetDev()
        {
            Set(null, 0, newGame: false);
            IsDev = true;
        }

        /// <summary>The new character has been placed and saved; reloading from here is an ordinary load.</summary>
        public static void Started() => IsNewGame = false;
    }
}
