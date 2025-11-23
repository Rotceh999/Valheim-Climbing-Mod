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
    public static class ClimbAnimationController
    {
        private enum ClipDirection
        {
            Up,
            Down
        }

        private class PlayerAnimationState
        {
            public Animator Animator;
            public PlayableGraph Graph;
            public AnimationClipPlayable UpPlayable;
            public AnimationClipPlayable DownPlayable;
            public AnimationMixerPlayable Mixer;
            public ClipDirection CurrentDirection = ClipDirection.Up;
        }

        private static readonly Dictionary<Player, PlayerAnimationState> ActiveStates = new Dictionary<Player, PlayerAnimationState>();
        private static ManualLogSource _logger;
        private static AnimationClip _upClip;
        private static AnimationClip _downClip;
        private static bool _initialized;
        private static readonly FieldInfo AnimatorField = AccessTools.Field(typeof(Character), "m_animator");

        /// <summary>
        /// Loads the animation bundles from the plugin directory.
        /// </summary>
        public static void Initialize(ManualLogSource logger)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _logger = logger;
            _upClip = LoadClip("climbingup");
            _downClip = LoadClip("climbingdown");

            if (_upClip == null || _downClip == null)
            {
                _logger?.LogWarning("Climbing animation clips were not loaded. Default animations will be used instead.");
            }
        }

        private static AnimationClip LoadClip(string bundleName)
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(assemblyDir))
                {
                    _logger?.LogWarning($"Unable to determine plugin directory while loading '{bundleName}'.");
                    return null;
                }

                string bundlePath = Path.Combine(assemblyDir, bundleName);
                if (!File.Exists(bundlePath))
                {
                    _logger?.LogWarning($"Animation asset bundle '{bundlePath}' was not found.");
                    return null;
                }

                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    _logger?.LogWarning($"Failed to load AssetBundle from '{bundlePath}'.");
                    return null;
                }

                try
                {
                    AnimationClip clip = bundle.LoadAsset<AnimationClip>(bundleName);
                    if (clip == null)
                    {
                        AnimationClip[] clips = bundle.LoadAllAssets<AnimationClip>();
                        if (clips.Length > 0)
                        {
                            clip = clips[0];
                        }
                    }

                    if (clip == null)
                    {
                        _logger?.LogWarning($"No AnimationClip found inside '{bundleName}'.");
                    }

                    return clip;
                }
                finally
                {
                    bundle.Unload(false);
                }
            }
            catch (System.Exception ex)
            {
                _logger?.LogError($"Failed to load animation clip '{bundleName}': {ex}");
                return null;
            }
        }

        private static Animator GetAnimator(Player player)
        {
            if (AnimatorField != null)
            {
                if (AnimatorField.GetValue(player) is Animator animFromField && animFromField != null)
                {
                    return animFromField;
                }
            }

            return player.GetComponentInChildren<Animator>();
        }

        /// <summary>
        /// Sets up the PlayableGraph and AnimationMixer for the player.
        /// </summary>
        public static void Begin(Player player)
        {
            if (_upClip == null || _downClip == null)
            {
                return;
            }

            if (ActiveStates.ContainsKey(player))
            {
                return;
            }

            Animator animator = GetAnimator(player);
            if (animator == null)
            {
                _logger?.LogWarning("Unable to locate player Animator component for climbing animations.");
                return;
            }

            var graph = PlayableGraph.Create($"ClimbGraph_{player.GetPlayerID()}");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

            var upPlayable = AnimationClipPlayable.Create(graph, _upClip);
            upPlayable.SetApplyFootIK(true);
            var downPlayable = AnimationClipPlayable.Create(graph, _downClip);
            downPlayable.SetApplyFootIK(true);

            var mixer = AnimationMixerPlayable.Create(graph, 2);
            graph.Connect(upPlayable, 0, mixer, 0);
            graph.Connect(downPlayable, 0, mixer, 1);
            mixer.SetInputWeight(0, 1f);
            mixer.SetInputWeight(1, 0f);

            var output = AnimationPlayableOutput.Create(graph, "ClimbOutput", animator);
            output.SetSourcePlayable(mixer);

            graph.Play();

            ActiveStates[player] = new PlayerAnimationState
            {
                Animator = animator,
                Graph = graph,
                UpPlayable = upPlayable,
                DownPlayable = downPlayable,
                Mixer = mixer,
                CurrentDirection = ClipDirection.Up
            };
        }

        /// <summary>
        /// Updates the animation speed and direction based on player input.
        /// </summary>
        public static void Update(Player player, float verticalInput, float animationSpeedMultiplier = 0.9f)
        {
            if (!ActiveStates.TryGetValue(player, out var state))
            {
                return;
            }

            if (Mathf.Abs(verticalInput) <= 0.05f)
            {
                Pause(state);
                return;
            }

            if (verticalInput > 0f)
            {
                SwitchClip(state, ClipDirection.Up, Mathf.Abs(verticalInput), animationSpeedMultiplier);
            }
            else
            {
                SwitchClip(state, ClipDirection.Down, Mathf.Abs(verticalInput), animationSpeedMultiplier);
            }
        }

        private static void Pause(PlayerAnimationState state)
        {
            state.UpPlayable.SetSpeed(0f);
            state.DownPlayable.SetSpeed(0f);
        }

        private static void SwitchClip(PlayerAnimationState state, ClipDirection direction, float speed, float animationSpeedMultiplier)
        {
            float effectiveSpeed = Mathf.Max(speed * Mathf.Max(animationSpeedMultiplier, 0.01f), 0.25f);

            if (direction == ClipDirection.Up)
            {
                if (state.CurrentDirection != ClipDirection.Up)
                {
                    state.UpPlayable.SetTime(0f);
                }

                state.Mixer.SetInputWeight(0, 1f);
                state.Mixer.SetInputWeight(1, 0f);
                state.UpPlayable.SetSpeed(effectiveSpeed);
                state.DownPlayable.SetSpeed(0f);
            }
            else
            {
                if (state.CurrentDirection != ClipDirection.Down)
                {
                    state.DownPlayable.SetTime(0f);
                }

                state.Mixer.SetInputWeight(0, 0f);
                state.Mixer.SetInputWeight(1, 1f);
                state.DownPlayable.SetSpeed(effectiveSpeed);
                state.UpPlayable.SetSpeed(0f);
            }

            state.CurrentDirection = direction;

            if (!state.Graph.IsPlaying())
            {
                state.Graph.Play();
            }
        }

        public static void End(Player player)
        {
            if (!ActiveStates.TryGetValue(player, out var state))
            {
                return;
            }

            if (state.Graph.IsValid())
            {
                state.Graph.Destroy();
            }

            ActiveStates.Remove(player);
        }

        public static void Cleanup(Player player)
        {
            End(player);
        }

        public static bool IsActive(Player player)
        {
            return ActiveStates.ContainsKey(player);
        }
    }
}
