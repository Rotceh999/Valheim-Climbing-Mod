using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

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

            public float climbSkill = 0f;

            // Number of consecutive FixedUpdate checks with a valid climb-start surface.
            public int validStartSurfaceFrames = 0;
        }

        public static ClimbingData GetOrCreate(Player player)
        {
            if (!climbingPlayers.TryGetValue(player, out ClimbingData data))
            {
                data = new ClimbingData();
                climbingPlayers[player] = data;
            }
            return data;
        }

        public static bool IsClimbing(Player player)
        {
            return climbingPlayers.TryGetValue(player, out ClimbingData data) && data.isClimbing;
        }

        public static void Cleanup(Player player)
        {
            climbingPlayers.Remove(player);
        }
    }

    public static class ClimbSkillProgression
    {
        public const string SkillKey = "RotcehClimbSkill"; // legacy float storage
        public const string SkillLevelKey = "RotcehClimbSkillLevel";
        public const string SkillXpKey = "RotcehClimbSkillXp";
        public const float MaxSkill = 100f;
        private static readonly FieldInfo SkillLevelupFxField = AccessTools.Field(typeof(Player), "m_skillLevelupEffects")
            ?? AccessTools.Field(typeof(Player), "m_skillLevelUpEffects")
            ?? AccessTools.Field(typeof(Player), "m_levelupEffects");

        public static float GetSkill(Player player)
        {
            if (player == null)
            {
                return 0f;
            }

            ReadProgress(player, out int level, out float xp);
            if (level >= (int)MaxSkill)
            {
                return MaxSkill;
            }

            float xpToNext = GetXpToNextLevel(level);
            float progress = xpToNext > 0f ? Mathf.Clamp01(xp / xpToNext) : 0f;
            return Mathf.Clamp(level + progress, 0f, MaxSkill);
        }

        public static void SetSkill(Player player, float skill)
        {
            if (player == null)
            {
                return;
            }

            skill = Mathf.Clamp(skill, 0f, MaxSkill);
            int level = Mathf.FloorToInt(skill);
            float xp = 0f;

            if (level < (int)MaxSkill)
            {
                float levelFraction = skill - level;
                xp = levelFraction * GetXpToNextLevel(level);
            }

            WriteProgress(player, level, xp);
        }

        public static void AddSkill(Player player, float deltaTime)
        {
            if (player == null || deltaTime <= 0f)
            {
                return;
            }

            ReadProgress(player, out int level, out float xp);
            if (level >= (int)MaxSkill)
            {
                return;
            }

            float gainPerSecond = ClimbingModPlugin.ClimbSkillXpPerSecond?.Value ?? 1.5f;
            float gainedXp = Mathf.Max(0f, gainPerSecond) * deltaTime;
            xp += gainedXp;

            while (level < (int)MaxSkill)
            {
                float xpToNext = GetXpToNextLevel(level);
                if (xp < xpToNext)
                {
                    break;
                }

                xp -= xpToNext;
                level++;
                ShowLevelUpFeedback(player, level);

                if (level >= (int)MaxSkill)
                {
                    level = (int)MaxSkill;
                    xp = 0f;
                    break;
                }
            }

            WriteProgress(player, level, xp);
        }

        private static float GetXpToNextLevel(int level)
        {
            float baseXp = ClimbingModPlugin.ClimbSkillBaseXpPerLevel?.Value ?? 100f;
            float increasePerLevel = ClimbingModPlugin.ClimbSkillXpIncreasePerLevel?.Value ?? 40f;
            return Mathf.Max(1f, baseXp + Mathf.Max(0f, increasePerLevel) * Mathf.Clamp(level, 0, (int)MaxSkill));
        }

        private static void ReadProgress(Player player, out int level, out float xp)
        {
            level = 0;
            xp = 0f;

            ZNetView nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                return;
            }

            ZDO zdo = nview.GetZDO();
            level = zdo.GetInt(SkillLevelKey, -1);
            xp = zdo.GetFloat(SkillXpKey, 0f);

            if (level < 0)
            {
                // Migrate from the old float-based storage into level + xp.
                float legacySkill = Mathf.Clamp(zdo.GetFloat(SkillKey, 0f), 0f, MaxSkill);
                level = Mathf.FloorToInt(legacySkill);
                float levelFraction = legacySkill - level;
                if (level >= (int)MaxSkill)
                {
                    level = (int)MaxSkill;
                    xp = 0f;
                }
                else
                {
                    xp = levelFraction * GetXpToNextLevel(level);
                }

                zdo.Set(SkillLevelKey, level);
                zdo.Set(SkillXpKey, xp);
            }

            level = Mathf.Clamp(level, 0, (int)MaxSkill);
            xp = Mathf.Max(0f, xp);
            if (level >= (int)MaxSkill)
            {
                xp = 0f;
            }
        }

        private static void WriteProgress(Player player, int level, float xp)
        {
            ZNetView nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                return;
            }

            level = Mathf.Clamp(level, 0, (int)MaxSkill);
            xp = level >= (int)MaxSkill ? 0f : Mathf.Max(0f, xp);

            ZDO zdo = nview.GetZDO();
            zdo.Set(SkillLevelKey, level);
            zdo.Set(SkillXpKey, xp);

            // Keep legacy float key synced for compatibility with older versions.
            float legacySkill = level;
            if (level < (int)MaxSkill)
            {
                float xpToNext = GetXpToNextLevel(level);
                legacySkill += xpToNext > 0f ? Mathf.Clamp01(xp / xpToNext) : 0f;
            }
            zdo.Set(SkillKey, Mathf.Clamp(legacySkill, 0f, MaxSkill));
        }

        public static void ShowLevelUpFeedback(Player player, int level)
        {
            if (player == null || player != Player.m_localPlayer)
            {
                return;
            }

            player.Message(MessageHud.MessageType.Center, $"Climbing skill increased to {level}", 0, null);
            TryPlayNativeSkillLevelupFx(player);
        }

        private static void TryPlayNativeSkillLevelupFx(Player player)
        {
            if (SkillLevelupFxField == null)
            {
                return;
            }

            object fxList = SkillLevelupFxField.GetValue(player);
            if (fxList == null)
            {
                return;
            }

            MethodInfo createMethod = AccessTools.Method(fxList.GetType(), "Create");
            if (createMethod == null)
            {
                return;
            }

            Vector3 spawnPos = player.transform.position + Vector3.up * 1.6f;
            Quaternion rot = player.transform.rotation;

            ParameterInfo[] parameters = createMethod.GetParameters();
            if (parameters.Length == 5)
            {
                createMethod.Invoke(fxList, new object[] { spawnPos, rot, player.transform, 1f, -1 });
            }
            else if (parameters.Length == 4)
            {
                createMethod.Invoke(fxList, new object[] { spawnPos, rot, player.transform, 1f });
            }
            else if (parameters.Length == 3)
            {
                createMethod.Invoke(fxList, new object[] { spawnPos, rot, player.transform });
            }
        }

        public static float GetSpeedMultiplier(float skill)
        {
            return Mathf.Lerp(1f, 3f, Mathf.Clamp01(skill / MaxSkill));
        }
    }
}
