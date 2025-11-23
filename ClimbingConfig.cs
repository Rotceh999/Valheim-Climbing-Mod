using BepInEx;
using UnityEngine;

namespace Valheim_Climbing_Mod
{
    public static class ClimbingConfig
    {
        public static float CLIMB_SPEED_UP => ClimbingModPlugin.ClimbSpeedUp?.Value ?? 1.0f;
        public static float CLIMB_SPEED_DOWN => ClimbingModPlugin.ClimbSpeedDown?.Value ?? 1.0f;

        // Distance to check for a climbable surface in front of the player.
        public const float DETECTION_DISTANCE = 0.6f;

        public const float MIN_SURFACE_ANGLE = 10f;

        public const float MAX_SURFACE_ANGLE = 240f;

        // Force applied to keep the player stuck to the wall.
        public const float STICK_FORCE = 0.5f;

        // Target separation distance from the surface while climbing.
        public const float SURFACE_TARGET_DISTANCE = 0.18f;

        // Force applied to push the player away if they get too close to the surface.
        public const float SURFACE_REPEL_FORCE = 0.15f;

        public const float FACE_SURFACE_TURN_SPEED = 10f;

        public const float STEEP_SURFACE_ANGLE = 90f;
        public const float SHALLOW_SURFACE_ANGLE = 10f;
        public const float STEEP_SURFACE_SPEED_FACTOR = 1.0f;
        public const float SHALLOW_SURFACE_SPEED_FACTOR = 1.4f;
        public const float REPEL_DISABLE_SURFACE_ANGLE = 25f;
        public const float REPEL_FULL_STRENGTH_SURFACE_ANGLE = 60f;
        public static float STAMINA_DRAIN_PER_SECOND => ClimbingModPlugin.StaminaDrainPerSecond?.Value ?? 3f;
    }
}
