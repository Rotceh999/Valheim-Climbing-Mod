using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Climbing_Mod
{
    [BepInPlugin("com.rotceh.valheimclimbingmod", "Valheim Climbing Mod", "1.0.0")] // BepInEx plugin attribute with unique ID, name, and version
    public class ClimbingModPlugin : BaseUnityPlugin
    {
        private void Awake() // Called when the plugin is loaded
        {
            var lHarmony = new Harmony("com.rotceh.valheimclimbingmod"); // Creates a Harmony instance with a unique identifier for this mod's patches
            lHarmony.PatchAll(); // Automatically finds and applies all Harmony patches in the assembly
            Logger.LogInfo("Valheim Climbing Mod loaded."); // For BepInEx log
        }
    }
}
