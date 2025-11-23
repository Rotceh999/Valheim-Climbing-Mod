using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Valheim_Climbing_Mod
{
    // ========================================
    // PLUGIN ENTRY POINT
    // ========================================
    [BepInPlugin("com.rotceh.valheimclimbingmod", "Valheim Climbing Mod", "1.0.0")] // BepInEx plugin attribute with unique ID, name, and version
    public class ClimbingModPlugin : BaseUnityPlugin
    {
        public static BepInEx.Configuration.ConfigEntry<KeyCode> ClimbKey;
        public static BepInEx.Configuration.ConfigEntry<float> ClimbSpeedUp;
        public static BepInEx.Configuration.ConfigEntry<float> ClimbSpeedDown;
        public static BepInEx.Configuration.ConfigEntry<float> StaminaDrainPerSecond;
        public static BepInEx.Configuration.ConfigEntry<bool> ToggleClimbKey;

        private void Awake()
        {
            ClimbKey = Config.Bind("General", "ClimbKey", KeyCode.LeftAlt, "Key to trigger climbing (default: LeftAlt)");
            ToggleClimbKey = Config.Bind("General", "ToggleClimbKey", false, "When enabled, pressing the climb key toggles climbing instead of requiring the key to be held.");
            ClimbSpeedUp = Config.Bind("Movement", "ClimbSpeedUp", 1.0f, "Base climb speed when moving upward (W).");
            ClimbSpeedDown = Config.Bind("Movement", "ClimbSpeedDown", 1.0f, "Base climb speed when moving downward or sideways (S/A/D).");
            StaminaDrainPerSecond = Config.Bind("Movement", "StaminaDrainPerSecond", 2.0f, "Stamina drained per second while climbing (scaled by movement).");
            var lHarmony = new Harmony("com.rotceh.valheimclimbingmod");
            lHarmony.PatchAll();
            Logger.LogInfo($"Valheim Climbing Mod loaded. Climb key: {ClimbKey.Value}");
            ClimbAnimationController.Initialize(Logger);
        }
    }
    
    // ========================================
    // MAIN CLIMBING LOGIC - Player FixedUpdate Patch
    // ========================================
    [HarmonyPatch(typeof(Player), "FixedUpdate")]
    public class Player_FixedUpdate_Patch
    {
        private static readonly int SurfaceLayerMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
        private static readonly float[] ProbeHeights = { 1.2f, 0.7f, 0.2f };
        private static readonly Vector3[] WorldProbeDirections =
        {
            Vector3.up,
            Vector3.down,
            Vector3.right,
            Vector3.left,
            Vector3.forward,
            Vector3.back
        };

        // Reflection for TakeInput
        private static MethodInfo _takeInputMethod;
        private static bool CallTakeInput(Player player)
        {
            if (_takeInputMethod == null)
            {
                _takeInputMethod = typeof(Player).GetMethod("TakeInput", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            if (_takeInputMethod != null)
            {
                return (bool)_takeInputMethod.Invoke(player, null);
            }
            return true; // Fallback
        }

        static void Postfix(Player __instance)
        {
            if (__instance == null) return;

            // Handle remote players for animation sync
            if (__instance != Player.m_localPlayer)
            {
                HandleRemotePlayer(__instance);
                return;
            }

            if (__instance.IsDead() || __instance.InCutscene() || __instance.IsTeleporting()) return;

            var climbData = ClimbingState.GetOrCreate(__instance);
            bool toggleMode = ClimbingModPlugin.ToggleClimbKey?.Value ?? false;
            bool climbKeyHeld = false;

            // Only read input if the game allows it
            if (CallTakeInput(__instance))
            {
                if (toggleMode)
                {
                    if (Input.GetKeyDown(ClimbingModPlugin.ClimbKey.Value))
                    {
                        climbData.toggleActive = !climbData.toggleActive;
                        if (!climbData.toggleActive && climbData.isClimbing)
                        {
                            StopClimbing(__instance, climbData);
                        }
                    }

                    climbKeyHeld = climbData.toggleActive;
                }
                else
                {
                    climbKeyHeld = Input.GetKey(ClimbingModPlugin.ClimbKey.Value);
                    if (!climbKeyHeld)
                    {
                        climbData.toggleActive = false;
                    }
                }
            }
            else
            {
                // If we can't take input, maintain current toggle state but don't process new key presses
                climbKeyHeld = climbData.toggleActive; 
            }

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
                    if (!PlayerHasClimbStamina(__instance))
                    {
                        StopClimbing(__instance, climbData);
                        return;
                    }

                    // Continue climbing - refresh surface data; stop if we lost the wall
                    if (TryGetSurfaceNormal(__instance, out Vector3 refreshedNormal, climbData.surfaceNormal))
                    {
                        climbData.surfaceNormal = refreshedNormal;
                    }
                    else
                    {
                        StopClimbing(__instance, climbData);
                    }
                }
            }
        }

        static void HandleRemotePlayer(Player player)
        {
            ZNetView nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            bool isClimbing = nview.GetZDO().GetBool("IsClimbing", false);
            bool isAnimationActive = ClimbAnimationController.IsActive(player);

            if (isClimbing && !isAnimationActive)
            {
                ClimbAnimationController.Begin(player);
            }
            else if (!isClimbing && isAnimationActive)
            {
                ClimbAnimationController.End(player);
            }
            
            if (isClimbing)
            {
                 // For remote players, we might want to sync the speed or just play a default loop
                 // Since we don't sync input, we can just update with 0 input to keep the pose
                 ClimbAnimationController.Update(player, 0f, 1f); 
            }
        }

        static void TryStartClimbing(Player player, ClimbingState.ClimbingData climbData)
        {
            if (!PlayerHasClimbStamina(player))
            {
                return;
            }

            if (TryGetSurfaceNormal(player, out Vector3 normal))
            {
                StartClimbing(player, climbData, normal);
            }
        }

        private static bool PlayerHasClimbStamina(Player player)
        {
            return player.GetStamina() > 0.25f; // small buffer to avoid rapid toggles at zero
        }

        static void StartClimbing(Player player, ClimbingState.ClimbingData climbData, Vector3 surfaceNormal)
        {
            climbData.isClimbing = true;
            climbData.surfaceNormal = surfaceNormal;

            // Sync state
            ZNetView nview = player.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                nview.GetZDO().Set("IsClimbing", true);
            }

            // Disable gravity and zero out velocity
            var rigidbody = player.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                climbData.wasGravityEnabled = rigidbody.useGravity;
                rigidbody.useGravity = false;
                rigidbody.linearVelocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
            }

            ClimbAnimationController.Begin(player);
        }

        public static void StopClimbing(Player player, ClimbingState.ClimbingData climbData)
        {
            climbData.isClimbing = false;

            // Sync state
            ZNetView nview = player.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                nview.GetZDO().Set("IsClimbing", false);
            }

            // Re-enable gravity
            var rigidbody = player.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.useGravity = climbData.wasGravityEnabled;
            }

            ClimbAnimationController.End(player);
        }

        // Sweep around several offsets so we can cling to flats, walls, and ceilings alike.
        static bool TryGetSurfaceNormal(Player player, out Vector3 normal, Vector3 preferredNormal = default)
        {
            float maxDistance = ClimbingConfig.DETECTION_DISTANCE + 0.75f;

            foreach (float height in ProbeHeights)
            {
                Vector3 origin = player.transform.position + Vector3.up * height;

                foreach (Vector3 dir in EnumerateProbeDirections(player, preferredNormal))
                {
                    if (!Physics.SphereCast(origin, 0.4f, dir, out RaycastHit hit, maxDistance, SurfaceLayerMask))
                    {
                        continue;
                    }

                    float surfaceAngle = Vector3.Angle(Vector3.up, hit.normal);
                    if (surfaceAngle >= ClimbingConfig.MIN_SURFACE_ANGLE && surfaceAngle <= ClimbingConfig.MAX_SURFACE_ANGLE)
                    {
                        normal = hit.normal;
                        return true;
                    }
                }
            }

            normal = Vector3.zero;
            return false;
        }

        // Combines last-known normal, player axes, and world axes to keep probing simple.
        private static IEnumerable<Vector3> EnumerateProbeDirections(Player player, Vector3 preferredNormal)
        {
            if (preferredNormal != Vector3.zero)
            {
                yield return -preferredNormal.normalized;
            }

            Transform t = player.transform;
            yield return t.forward;
            yield return -t.forward;
            yield return t.up;
            yield return -t.up;
            yield return t.right;
            yield return -t.right;

            foreach (Vector3 dir in WorldProbeDirections)
            {
                yield return dir;
            }
        }
    }

    // ========================================
    // PHYSICS OVERRIDE - Disable Normal Movement While Climbing
    // ========================================
    [HarmonyPatch(typeof(Character), "UpdateMotion")]
    class Character_UpdateMotion_Patch
    {
        private static readonly int SurfaceLayerMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");

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
            rigidbody.useGravity = false;

            // Get input
            float forwardInput = Input.GetAxis("Vertical");   // W/S keys
            float rightInput = Input.GetAxis("Horizontal");   // A/D keys

            Vector3 climbVelocity = Vector3.zero;
            var climbData = ClimbingState.GetOrCreate(player);
            Vector3 surfaceNormal = climbData.surfaceNormal != Vector3.zero ? climbData.surfaceNormal : -player.transform.forward;
            float slopeSpeedFactor = CalculateSlopeSpeedFactor(surfaceNormal);

            // Calculate movement directions relative to the surface
            Vector3 climbDirection = ResolveClimbDirection(surfaceNormal, player.transform);
            Vector3 lateralDirection = ResolveLateralDirection(surfaceNormal, climbDirection);

            // Movement along the surface (W/S)
            if (Mathf.Abs(forwardInput) > 0.1f && climbDirection.sqrMagnitude > 0.01f)
            {
                float speed = forwardInput > 0f ? ClimbingConfig.CLIMB_SPEED_UP : ClimbingConfig.CLIMB_SPEED_DOWN;
                climbVelocity += climbDirection * speed * forwardInput * slopeSpeedFactor;
            }

            // Lateral movement across the surface (A/D)
            if (Mathf.Abs(rightInput) > 0.1f && lateralDirection.sqrMagnitude > 0.01f)
            {
                climbVelocity += lateralDirection * ClimbingConfig.CLIMB_SPEED_DOWN * rightInput;
            }

            // Apply a gentle push toward the surface so the player sticks while climbing.
            Vector3 stickDirection = (-surfaceNormal).normalized;
            climbVelocity += stickDirection * ClimbingConfig.STICK_FORCE;

            // Push away if too close to the wall (prevent clipping)
            ApplySurfaceRepulsion(player, surfaceNormal, ref climbVelocity);

            // Rotate player to face the wall
            AlignWithSurface(player, surfaceNormal);

            // Apply climbing velocity
            rigidbody.linearVelocity = climbVelocity;

            // Update animation state
            float animationInput = ResolveAnimationInput(forwardInput, rightInput);
            ClimbAnimationController.Update(player, animationInput, slopeSpeedFactor);

            ApplyClimbStaminaDrain(player, forwardInput, rightInput);
        }

        // Gentle slope multiplier keeps small hills fast while vertical walls feel heavy.
        private static float CalculateSlopeSpeedFactor(Vector3 surfaceNormal)
        {
            float surfaceAngle = Vector3.Angle(Vector3.up, surfaceNormal);
            float t = Mathf.InverseLerp(ClimbingConfig.STEEP_SURFACE_ANGLE, ClimbingConfig.SHALLOW_SURFACE_ANGLE, surfaceAngle);
            return Mathf.Clamp(Mathf.Lerp(ClimbingConfig.STEEP_SURFACE_SPEED_FACTOR, ClimbingConfig.SHALLOW_SURFACE_SPEED_FACTOR, t), 0.1f, 3f);
        }

        // Falls back across several orthogonal directions so movement never locks up.
        private static Vector3 ResolveClimbDirection(Vector3 surfaceNormal, Transform transform)
        {
            Vector3 direction = Vector3.ProjectOnPlane(Vector3.up, surfaceNormal);
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = Vector3.ProjectOnPlane(transform.forward, surfaceNormal);
            }
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = Vector3.Cross(surfaceNormal, transform.right);
            }
            return direction.sqrMagnitude > 0.01f ? direction.normalized : Vector3.forward;
        }

        // Lateral movement sticks to the plane formed by the surface and climb vector.
        private static Vector3 ResolveLateralDirection(Vector3 surfaceNormal, Vector3 climbDirection)
        {
            Vector3 lateral = Vector3.Cross(surfaceNormal, climbDirection);
            if (lateral.sqrMagnitude < 0.01f)
            {
                lateral = Vector3.Cross(surfaceNormal, Vector3.up);
            }
            return lateral.sqrMagnitude > 0.01f ? lateral.normalized : Vector3.zero;
        }

        // Sideways crawling should keep the climb animation alive.
        private static float ResolveAnimationInput(float forwardInput, float rightInput)
        {
            if (Mathf.Abs(forwardInput) > 0.05f)
            {
                return forwardInput;
            }
            return Mathf.Abs(rightInput) > 0.1f ? Mathf.Abs(rightInput) : 0f;
        }

        // Small trickle at rest, ramps up when the player is really moving.
        private static void ApplyClimbStaminaDrain(Player player, float forwardInput, float rightInput)
        {
            float drain = ClimbingConfig.STAMINA_DRAIN_PER_SECOND;
            if (drain <= 0f)
            {
                return;
            }

            float movementFactor = Mathf.Clamp01(Mathf.Max(Mathf.Abs(forwardInput), Mathf.Abs(rightInput)));
            float lerpFactor = movementFactor <= 0.05f ? 0.1f : Mathf.Lerp(0.5f, 1f, movementFactor);
            player.UseStamina(lerpFactor * drain * Time.deltaTime);
        }

        private static void ApplySurfaceRepulsion(Player player, Vector3 surfaceNormal, ref Vector3 climbVelocity)
        {
            if (surfaceNormal == Vector3.zero)
            {
                return;
            }

            Vector3 origin = player.transform.position + Vector3.up * 1f;
            float maxCheckDistance = ClimbingConfig.SURFACE_TARGET_DISTANCE + 0.75f;
            float surfaceAngle = Vector3.Angle(Vector3.up, surfaceNormal);
            float repelScale = Mathf.Clamp01(Mathf.InverseLerp(
                ClimbingConfig.REPEL_DISABLE_SURFACE_ANGLE,
                ClimbingConfig.REPEL_FULL_STRENGTH_SURFACE_ANGLE,
                surfaceAngle));

            if (repelScale <= 0.001f)
            {
                return;
            }

            // Only push out if we're genuinely clipping the surface.
            if (Physics.Raycast(origin, -surfaceNormal, out RaycastHit hit, maxCheckDistance, SurfaceLayerMask))
            {
                float penetration = ClimbingConfig.SURFACE_TARGET_DISTANCE - hit.distance;
                if (penetration > 0f)
                {
                    climbVelocity += surfaceNormal.normalized * (ClimbingConfig.SURFACE_REPEL_FORCE * penetration * repelScale);
                }
            }
        }

        private static void AlignWithSurface(Player player, Vector3 surfaceNormal)
        {
            if (surfaceNormal == Vector3.zero)
            {
                return;
            }

            Vector3 forward = -surfaceNormal.normalized;

            Vector3 referenceUp = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f
                ? Vector3.forward
                : Vector3.up;

            Vector3 right = Vector3.Cross(referenceUp, forward);
            if (right.sqrMagnitude < 0.0001f)
            {
                referenceUp = Vector3.right;
                right = Vector3.Cross(referenceUp, forward);
            }

            right.Normalize();
            Vector3 desiredUp = Vector3.Cross(forward, right).normalized;

            if (desiredUp.sqrMagnitude < 0.0001f)
            {
                desiredUp = referenceUp;
            }

            Quaternion targetRotation = Quaternion.LookRotation(forward, desiredUp);
            player.transform.rotation = Quaternion.Slerp(player.transform.rotation, targetRotation, Time.deltaTime * ClimbingConfig.FACE_SURFACE_TURN_SPEED);
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
    // CLEANUP - Remove data when player is destroyed
    // ========================================
    [HarmonyPatch(typeof(Player), "OnDestroy")]
    class Player_OnDestroy_Patch
    {
        static void Postfix(Player __instance)
        {
            ClimbingState.Cleanup(__instance);
            ClimbAnimationController.Cleanup(__instance);
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

    // ========================================
    // PREVENT FALL DAMAGE & HANDLE KNOCKBACK
    // ========================================
    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    class Character_Damage_Patch
    {
        static void Prefix(Character __instance, ref HitData hit)
        {
            if (hit == null) return;
            if (!(__instance is Player player) || !ClimbingState.IsClimbing(player)) return;

            // Prevent Fall Damage
            if (hit.m_hitType == HitData.HitType.Fall)
            {
                hit.m_damage = new HitData.DamageTypes();
                hit.m_pushForce = 0f;
                return;
            }

            // Handle Knockback
            if (hit.m_pushForce > 5f) 
            {
                var climbData = ClimbingState.GetOrCreate(player);
                Player_FixedUpdate_Patch.StopClimbing(player, climbData);
            }
        }
    }

}

