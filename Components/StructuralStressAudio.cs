using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VikingTruss.Patches;

namespace VikingTruss.Components
{
    /// <summary>
    /// Plays 3D spatial acoustic feedback using Valheim's built-in studio audio clips
    /// when structural pieces approach capacity:
    /// <list type="bullet">
    ///   <item><description><b>75%–89% Capacity (Creak):</b> Uses Valheim's built-in wood/hull creak and tree-strain groan clips.</description></item>
    ///   <item><description><b>90%+ Capacity (Snap / Crack):</b> Uses Valheim's built-in wood/material break &amp; crack clips + splinter/sawdust VFX.</description></item>
    /// </list>
    /// </summary>
    public static class StructuralStressAudio
    {
        private const float GlobalMinCreakSpacing = 1.35f;
        private const float GlobalMinSnapSpacing = 1.60f;

        private static readonly List<AudioClip> DiscoveredCreakClips = new List<AudioClip>();
        private static readonly List<AudioClip> DiscoveredWoodSnapClips = new List<AudioClip>();
        private static bool _clipsSearched;
        private static float _lastGlobalCreakTime = -999f;
        private static float _lastGlobalSnapTime = -999f;

        private static readonly FieldInfo ZsfxClipsField =
            AccessTools.Field(typeof(ZSFX), "m_audioClips");

        private static readonly string[] CreakPrefabNames =
        {
            "sfx_tree_fall",
            "sfx_tree_fall_small",
            "sfx_wood_creak",
            "sfx_ship_creak"
        };

        private static readonly string[] WoodSnapPrefabNames =
        {
            "sfx_wood_break",
            "sfx_wood_destroyed",
            "sfx_tree_hit",
            "sfx_wood_hit"
        };

        /// <summary>
        /// Computes a piece's normalized capacity utilization in <c>[0, 1+]</c>, where:
        /// <c>0.0</c> = grounded (<c>maxSupport</c>),
        /// <c>0.75</c> = 75% of breaking capacity,
        /// <c>0.90</c> = 90% of breaking capacity,
        /// <c>1.00</c> = collapse threshold (<c>minSupport</c>).
        /// </summary>
        public static float ComputeCapacityUtilization(
            float maxSupport,
            float minSupport,
            float baseSupport,
            float effectiveSupport)
        {
            if (maxSupport <= minSupport || effectiveSupport >= maxSupport - 0.01f)
            {
                return 0f;
            }

            if (effectiveSupport <= minSupport)
            {
                return 1f;
            }

            // 1. Logarithmic span utilization: matches Valheim's multiplicative support decay per step
            float logDenom = Mathf.Log(maxSupport / Mathf.Max(0.1f, minSupport));
            float logNumer = Mathf.Log(maxSupport / Mathf.Max(0.1f, effectiveSupport));
            float logUtilization = logDenom > 0.0001f ? Mathf.Clamp01(logNumer / logDenom) : 0f;

            // 2. Live-load margin utilization: how much of the piece's remaining margin above minSupport is consumed
            float marginUtilization = 0f;
            if (baseSupport > minSupport + 0.01f && effectiveSupport < baseSupport)
            {
                marginUtilization = Mathf.Clamp01((baseSupport - effectiveSupport) / (baseSupport - minSupport));
            }

            return Mathf.Max(logUtilization, marginUtilization);
        }

        /// <summary>
        /// Plays a 3D spatial creak (75%+) or snap/crack (90%+) at the piece's position using Valheim's built-in audio.
        /// </summary>
        public static void PlayStressFeedback(
            WearNTear piece,
            bool isSnapCrack,
            float capacityRatio,
            bool spawnParticles = true,
            float volumeMultiplier = 1.0f)
        {
            if (piece == null || !(Plugin.EnableMod?.Value ?? true) || !(Plugin.EnableStressSounds?.Value ?? true))
            {
                return;
            }

            // Only play within audible range of the local player
            if (Player.m_localPlayer != null)
            {
                float sqrDist = (Player.m_localPlayer.transform.position - piece.transform.position).sqrMagnitude;
                if (sqrDist > 28f * 28f)
                {
                    return;
                }
            }

            float now = Time.time;
            if (isSnapCrack)
            {
                if (now - _lastGlobalSnapTime < GlobalMinSnapSpacing)
                {
                    return;
                }
                _lastGlobalSnapTime = now;
            }
            else
            {
                if (now - _lastGlobalCreakTime < GlobalMinCreakSpacing)
                {
                    return;
                }
                _lastGlobalCreakTime = now;
            }

            EnsureBuiltInClipsDiscovered();

            Vector3 pos = WearNTearPatches.GetPieceCOM(piece);
            float volumeScale = Mathf.Clamp(Plugin.StressSoundVolume?.Value ?? 0.85f, 0.05f, 2.0f) * Mathf.Clamp(volumeMultiplier, 0.05f, 1.5f);

            if (isSnapCrack)
            {
                AudioClip snapClip = SelectSnapClipForPiece(piece);
                if (snapClip != null)
                {
                    float pitch = UnityEngine.Random.Range(1.04f, 1.20f);
                    PlayOneShot3D(pos, snapClip, 0.75f * volumeScale, pitch, 2.5f, 22f, maxDurationSec: 0.90f);
                }

                // Only spawn splinter/sawdust visual particles on initial step/impact or active movement
                if (spawnParticles)
                {
                    TrySpawnSplinterVfx(piece, pos);
                }
            }
            else
            {
                AudioClip creakClip = SelectCreakClipForPiece(piece);
                if (creakClip != null)
                {
                    float intensity = Mathf.Clamp01((capacityRatio - 0.70f) / 0.20f);
                    float vol = Mathf.Lerp(0.42f, 0.70f, intensity) * volumeScale;
                    float pitch = UnityEngine.Random.Range(0.92f, 1.08f);

                    // If using a long tree-fall clip, cap duration to the initial ~1.35s timber groan/creak
                    // with a smooth fade-out before the tree-crash thud at the end of the recording.
                    float maxDur = creakClip.length > 1.6f ? 1.35f : creakClip.length;
                    PlayOneShot3D(pos, creakClip, vol, pitch, 2.0f, 18f, maxDurationSec: maxDur);
                }
            }
        }

        private static AudioClip SelectCreakClipForPiece(WearNTear piece)
        {
            if (piece != null && piece.m_materialType == WearNTear.MaterialType.Stone)
            {
                AudioClip stoneClip = TryGetClipFromEffectList(piece.m_hitEffect) ??
                                      TryGetClipFromEffectList(piece.m_destroyedEffect);
                if (stoneClip != null)
                {
                    return stoneClip;
                }
            }

            if (DiscoveredCreakClips.Count > 0)
            {
                return DiscoveredCreakClips[UnityEngine.Random.Range(0, DiscoveredCreakClips.Count)];
            }

            if (DiscoveredWoodSnapClips.Count > 0)
            {
                return DiscoveredWoodSnapClips[UnityEngine.Random.Range(0, DiscoveredWoodSnapClips.Count)];
            }

            return TryExtractNativeMaterialClip(piece);
        }

        private static AudioClip SelectSnapClipForPiece(WearNTear piece)
        {
            if (piece != null &&
                (piece.m_materialType == WearNTear.MaterialType.Stone ||
                 piece.m_materialType == WearNTear.MaterialType.Iron))
            {
                AudioClip native = TryGetClipFromEffectList(piece.m_destroyedEffect) ??
                                   TryGetClipFromEffectList(piece.m_hitEffect);
                if (native != null)
                {
                    return native;
                }
            }

            if (DiscoveredWoodSnapClips.Count > 0)
            {
                return DiscoveredWoodSnapClips[UnityEngine.Random.Range(0, DiscoveredWoodSnapClips.Count)];
            }

            return TryExtractNativeMaterialClip(piece);
        }

        private static void EnsureBuiltInClipsDiscovered()
        {
            if (_clipsSearched && (DiscoveredCreakClips.Count > 0 || DiscoveredWoodSnapClips.Count > 0))
            {
                return;
            }

            if (ZNetScene.instance == null)
            {
                return;
            }

            _clipsSearched = true;
            DiscoveredCreakClips.Clear();
            DiscoveredWoodSnapClips.Clear();

            // 1. Harvest clips from known Valheim SFX prefabs in ZNetScene
            for (int i = 0; i < CreakPrefabNames.Length; i++)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(CreakPrefabNames[i]);
                AddClipsFromPrefab(prefab, DiscoveredCreakClips);
            }

            for (int i = 0; i < WoodSnapPrefabNames.Length; i++)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(WoodSnapPrefabNames[i]);
                AddClipsFromPrefab(prefab, DiscoveredWoodSnapClips);
            }

            // 2. Also scan loaded AudioClips in memory for dedicated studio creak/wood recordings
            // (such as ship hull creaks on Karve/VikingShip, tree creaks, and wood break clips)
            AudioClip[] allClips = Resources.FindObjectsOfTypeAll<AudioClip>();
            if (allClips != null)
            {
                List<AudioClip> pureCreakMatches = new List<AudioClip>();
                for (int i = 0; i < allClips.Length; i++)
                {
                    AudioClip clip = allClips[i];
                    if (clip == null || string.IsNullOrEmpty(clip.name))
                    {
                        continue;
                    }

                    string lower = clip.name.ToLowerInvariant();
                    if (lower.Contains("creak") || lower.Contains("wood_strain") || lower.Contains("tree_fall"))
                    {
                        if (lower.Contains("creak"))
                        {
                            AddUniqueClip(pureCreakMatches, clip);
                        }
                        AddUniqueClip(DiscoveredCreakClips, clip);
                    }
                    else if (lower.Contains("wood_break") || lower.Contains("wood_destroyed") || lower.Contains("branch_break"))
                    {
                        AddUniqueClip(DiscoveredWoodSnapClips, clip);
                    }
                }

                // If Valheim has dedicated "*creak*" clips loaded (e.g. ship/wood creaks), prioritize those first!
                if (pureCreakMatches.Count > 0)
                {
                    DiscoveredCreakClips.Clear();
                    DiscoveredCreakClips.AddRange(pureCreakMatches);
                }
            }

            Plugin.Log?.LogInfo(
                $"[VikingTruss] Loaded Valheim built-in stress audio: " +
                $"{DiscoveredCreakClips.Count} creak clip(s), {DiscoveredWoodSnapClips.Count} snap/crack clip(s).");
        }

        private static void AddClipsFromPrefab(GameObject prefab, List<AudioClip> targetList)
        {
            if (prefab == null)
            {
                return;
            }

            ZSFX[] zsfxList = prefab.GetComponentsInChildren<ZSFX>(true);
            for (int i = 0; i < zsfxList.Length; i++)
            {
                if (ZsfxClipsField?.GetValue(zsfxList[i]) is AudioClip[] clips)
                {
                    for (int c = 0; c < clips.Length; c++)
                    {
                        AddUniqueClip(targetList, clips[c]);
                    }
                }
            }

            AudioSource[] sources = prefab.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                AddUniqueClip(targetList, sources[i].clip);
            }
        }

        private static void AddUniqueClip(List<AudioClip> list, AudioClip clip)
        {
            if (clip != null && !list.Contains(clip))
            {
                list.Add(clip);
            }
        }

        private static void PlayOneShot3D(
            Vector3 position,
            AudioClip clip,
            float volume,
            float pitch,
            float minDistance,
            float maxDistance,
            float maxDurationSec)
        {
            if (clip == null)
            {
                return;
            }

            GameObject go = new GameObject("VikingTruss_StressSFX");
            go.transform.position = position;

            AudioSource source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.spatialBlend = 1.0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.volume = Mathf.Clamp01(volume);
            source.pitch = pitch;
            source.playOnAwake = false;
            source.Play();

            float effectiveClipLen = clip.length / Mathf.Max(0.2f, Mathf.Abs(pitch));
            float playDuration = Mathf.Min(effectiveClipLen, maxDurationSec);

            StressAudioEnvelope env = go.AddComponent<StressAudioEnvelope>();
            env.Initialize(source, volume, playDuration);
        }

        private static AudioClip TryExtractNativeMaterialClip(WearNTear piece)
        {
            if (piece == null)
            {
                return null;
            }

            AudioClip clip = TryGetClipFromEffectList(piece.m_destroyedEffect);
            if (clip != null)
            {
                return clip;
            }

            return TryGetClipFromEffectList(piece.m_hitEffect);
        }

        private static AudioClip TryGetClipFromEffectList(EffectList list)
        {
            if (list == null || list.m_effectPrefabs == null)
            {
                return null;
            }

            for (int i = 0; i < list.m_effectPrefabs.Length; i++)
            {
                EffectList.EffectData data = list.m_effectPrefabs[i];
                if (data == null || !data.m_enabled || data.m_prefab == null)
                {
                    continue;
                }

                ZSFX zsfx = data.m_prefab.GetComponentInChildren<ZSFX>(true);
                if (zsfx != null && ZsfxClipsField != null)
                {
                    AudioClip[] clips = ZsfxClipsField.GetValue(zsfx) as AudioClip[];
                    if (clips != null && clips.Length > 0)
                    {
                        return clips[UnityEngine.Random.Range(0, clips.Length)];
                    }
                }
            }

            return null;
        }

        private static void TrySpawnSplinterVfx(WearNTear piece, Vector3 pos)
        {
            EffectList list = piece?.m_hitEffect;
            if (list == null || list.m_effectPrefabs == null)
            {
                return;
            }

            for (int i = 0; i < list.m_effectPrefabs.Length; i++)
            {
                EffectList.EffectData data = list.m_effectPrefabs[i];
                if (data == null || !data.m_enabled || data.m_prefab == null)
                {
                    continue;
                }

                // Only spawn pure visual particle prefabs (like vfx_SawDust), not sound prefabs
                if (data.m_prefab.GetComponentInChildren<ZSFX>(true) != null ||
                    data.m_prefab.GetComponentInChildren<AudioSource>(true) != null)
                {
                    continue;
                }

                UnityEngine.Object.Instantiate(data.m_prefab, pos, piece.transform.rotation);
                break;
            }
        }
    }

    /// <summary>
    /// Smoothly fades out and destroys a temporary 3D stress <see cref="AudioSource"/> after its target duration
    /// (so long clips like <c>sfx_tree_fall</c> play only their initial timber creak/groan).
    /// </summary>
    internal sealed class StressAudioEnvelope : MonoBehaviour
    {
        private AudioSource _source;
        private float _initialVolume;
        private float _duration;
        private float _elapsed;

        public void Initialize(AudioSource source, float initialVolume, float duration)
        {
            _source = source;
            _initialVolume = initialVolume;
            _duration = Mathf.Max(0.15f, duration);
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;
            if (_elapsed >= _duration || _source == null)
            {
                Destroy(gameObject);
                return;
            }

            float fadeWindow = Mathf.Min(0.35f, _duration * 0.35f);
            float remaining = _duration - _elapsed;
            if (remaining < fadeWindow && fadeWindow > 0.001f)
            {
                _source.volume = _initialVolume * (remaining / fadeWindow);
            }
        }
    }
}
