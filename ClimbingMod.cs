using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;

namespace Valheim_Climbing_Mod
{
    // ========================================
    // PLUGIN ENTRY POINT
    // ========================================
    [BepInPlugin("com.rotceh.valheimclimbingmod", "Valheim Climbing Mod", "1.0.0")] // BepInEx plugin attribute with unique ID, name, and version
    public class ClimbingModPlugin : BaseUnityPlugin
    {
        public static BepInEx.Configuration.ConfigEntry<KeyCode> ClimbKey;

        private void Awake()
        {
            ClimbKey = Config.Bind("General", "ClimbKey", KeyCode.LeftAlt, "Key to trigger climbing (default: LeftAlt)");
            var lHarmony = new Harmony("com.rotceh.valheimclimbingmod");
            lHarmony.PatchAll();
            Logger.LogInfo($"Valheim Climbing Mod loaded. Climb key: {ClimbKey.Value}");
        }
    }
    
    // ========================================
    // CLIMBING STATE MANAGER
    // ========================================
    public static class ClimbingState
    {
        private static Dictionary<Player, ClimbingData> climbingPlayers = new Dictionary<Player, ClimbingData>(); // To track climbing state per player

        public class ClimbingData
        {
            public bool isClimbing = false; 
            public Vector3 surfaceNormal = Vector3.zero; 
            public bool wasGravityEnabled = true;
        }

        public static ClimbingData GetOrCreate(Player player)
        {
            if (!climbingPlayers.ContainsKey(player))
            {
                climbingPlayers[player] = new ClimbingData();
            }
            return climbingPlayers[player];
        }

        public static bool IsClimbing(Player player)
        {
            return climbingPlayers.ContainsKey(player) && climbingPlayers[player].isClimbing;
        }

        public static void Cleanup(Player player)
        {
            climbingPlayers.Remove(player);
        }
    }

    // ========================================
    // CLIMBING CONFIGURATION
    // ========================================
    public static class ClimbingConfig
    {
        public const float CLIMB_SPEED_UP = 3.0f;
        public const float CLIMB_SPEED_DOWN = 2.5f;
        public const float DETECTION_DISTANCE = 1.0f;
        public const float MIN_SURFACE_ANGLE = 45f;
        public const float MAX_SURFACE_ANGLE = 100f;
    }

    // ========================================
    // MAIN CLIMBING LOGIC - Player FixedUpdate Patch
    // ========================================
    [HarmonyPatch(typeof(Player), "FixedUpdate")]
    class Player_FixedUpdate_Patch
    {
        static void Postfix(Player __instance)
        {
            // Only process local player
            if (__instance == null || __instance != Player.m_localPlayer) return;
            if (__instance.IsDead() || __instance.InCutscene() || __instance.IsTeleporting()) return;

            var climbData = ClimbingState.GetOrCreate(__instance);
            bool climbKeyHeld = Input.GetKey(ClimbingModPlugin.ClimbKey.Value);

            // ====== CLIMBING STATE MACHINE ======
            if (!climbData.isClimbing)
            {
                // Try to START climbing
                if (climbKeyHeld)
                {
                    TryStartClimbing(__instance, climbData);
                }
            }
            else
            {
                // Already climbing - check if should STOP
                if (!climbKeyHeld)
                {
                    StopClimbing(__instance, climbData);
                }
                else
                {
                    // Continue climbing - check if still near surface
                    if (!IsNearClimbableSurface(__instance))
                    {
                        StopClimbing(__instance, climbData);
                    }
                }
            }
        }

        static void TryStartClimbing(Player player, ClimbingState.ClimbingData climbData)
        {
            // Check if there's a climbable surface in front of the player
            int layerMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
            Vector3 checkOrigin = player.transform.position + Vector3.up * 1.0f;
            Vector3 checkDirection = player.transform.forward;

            // Use SphereCast for better detection
            if (Physics.SphereCast(checkOrigin, 0.4f, checkDirection, out RaycastHit hit, ClimbingConfig.DETECTION_DISTANCE, layerMask))
            {
                // Check if the surface is steep enough
                float surfaceAngle = Vector3.Angle(Vector3.up, hit.normal);
                
                if (surfaceAngle >= ClimbingConfig.MIN_SURFACE_ANGLE && surfaceAngle <= ClimbingConfig.MAX_SURFACE_ANGLE)
                {
                    // Valid climbable surface found!
                    StartClimbing(player, climbData, hit.normal);
                }
            }
        }

        static void StartClimbing(Player player, ClimbingState.ClimbingData climbData, Vector3 surfaceNormal)
        {
            climbData.isClimbing = true;
            climbData.surfaceNormal = surfaceNormal;

            // Disable gravity and zero out velocity
            var rigidbody = player.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                climbData.wasGravityEnabled = rigidbody.useGravity;
                rigidbody.useGravity = false;
                rigidbody.linearVelocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
            }

            player.Message(MessageHud.MessageType.Center, "Climbing");
        }

        static void StopClimbing(Player player, ClimbingState.ClimbingData climbData)
        {
            climbData.isClimbing = false;

            // Re-enable gravity
            var rigidbody = player.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.useGravity = climbData.wasGravityEnabled;
            }

            player.Message(MessageHud.MessageType.Center, "");
        }

        static bool IsNearClimbableSurface(Player player)
        {
            int layerMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
            Vector3 checkOrigin = player.transform.position + Vector3.up * 1.0f;
            Vector3 checkDirection = player.transform.forward;

            // Check if still near a surface
            return Physics.SphereCast(checkOrigin, 0.4f, checkDirection, out _, ClimbingConfig.DETECTION_DISTANCE * 1.2f, layerMask);
        }
    }

    // ========================================
    // PHYSICS OVERRIDE - Disable Normal Movement While Climbing
    // ========================================
    [HarmonyPatch(typeof(Character), "UpdateMotion")]
    class Character_UpdateMotion_Patch
    {
        static bool Prefix(Character __instance)
        {
            if (__instance is Player player && ClimbingState.IsClimbing(player))
            {
                // Skip normal movement update when climbing
                HandleClimbingMovement(player);
                return false; // Don't run original UpdateMotion
            }
            return true; // Run original UpdateMotion for non-climbing
        }

        static void HandleClimbingMovement(Player player)
        {
            var rigidbody = player.GetComponent<Rigidbody>();
            if (rigidbody == null) return;

            // Get input
            float forwardInput = Input.GetAxis("Vertical");   // W/S keys
            float rightInput = Input.GetAxis("Horizontal");   // A/D keys

            Vector3 climbVelocity = Vector3.zero;

            // UP/DOWN movement (W = up, S = down)
            if (forwardInput > 0.1f)
            {
                climbVelocity += Vector3.up * ClimbingConfig.CLIMB_SPEED_UP * forwardInput;
            }
            else if (forwardInput < -0.1f)
            {
                climbVelocity += Vector3.up * ClimbingConfig.CLIMB_SPEED_DOWN * forwardInput;
            }

            // LEFT/RIGHT movement (A/D keys)
            if (Mathf.Abs(rightInput) > 0.1f)
            {
                Vector3 rightDir = player.transform.right;
                climbVelocity += rightDir * ClimbingConfig.CLIMB_SPEED_DOWN * rightInput;
            }

            // Apply climbing velocity
            rigidbody.linearVelocity = climbVelocity;
        }
    }

    // ========================================
    // PREVENT SLIDING - Override ApplySlide
    // ========================================
    [HarmonyPatch(typeof(Character), "ApplySlide")]
    class Character_ApplySlide_Patch
    {
        static bool Prefix(Character __instance)
        {
            if (__instance is Player player && ClimbingState.IsClimbing(player))
            {
                // Don't apply slide physics when climbing
                return false;
            }
            return true;
        }
    }

    // ========================================
    // PREVENT GROUND CHECK - Override OnGround
    // ========================================
    [HarmonyPatch(typeof(Character), "IsOnGround")]
    class Character_IsOnGround_Patch
    {
        static void Postfix(Character __instance, ref bool __result)
        {
            if (__instance is Player player && ClimbingState.IsClimbing(player))
            {
                // Tell the game we're "on ground" to prevent fall damage accumulation
                __result = true;
            }
        }
    }

    // ========================================
    // CLEANUP - Remove data when player is destroyed
    // ========================================
    [HarmonyPatch(typeof(Player), "OnDestroy")]
    class Player_OnDestroy_Patch
    {
        static void Postfix(Player __instance)
        {
            ClimbingState.Cleanup(__instance);
        }
    }

    // ========================================
    // PREVENT ATTACKS WHILE CLIMBING
    // ========================================
    [HarmonyPatch(typeof(Humanoid), "StartAttack")]
    class Humanoid_StartAttack_Patch
    {
        static bool Prefix(Humanoid __instance)
        {
            if (__instance is Player player && ClimbingState.IsClimbing(player))
            {
                return false;
            }
            return true;
        }
    }
}

