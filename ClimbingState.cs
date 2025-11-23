using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Climbing_Mod
{
    public static class ClimbingState
    {
        private static Dictionary<Player, ClimbingData> climbingPlayers = new Dictionary<Player, ClimbingData>(); // To track climbing state per player

        public class ClimbingData
        {
            public bool isClimbing = false; 

            /// <summary>
            /// The normal vector of the surface the player is currently climbing.
            /// </summary>
            public Vector3 surfaceNormal = Vector3.zero; 

            public bool wasGravityEnabled = true;

            public bool toggleActive = false;
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
}
