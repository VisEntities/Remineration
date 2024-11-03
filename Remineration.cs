/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Facepunch;
using Newtonsoft.Json;
using Rust;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Remineration", "VisEntities", "1.0.0")]
    [Description(" ")]
    public class Remineration : RustPlugin
    {
        #region Fields

        private static Remineration _plugin;
        private static Configuration _config;

        private const int LAYER_PLAYERS = Layers.Mask.Player_Server;
        private const int LAYER_GROUND = Layers.Mask.Terrain | Layers.Mask.World;
        private const int LAYER_ENTITIES = Layers.Mask.Default | Layers.Mask.Construction | Layers.Mask.Deployed;

        private Dictionary<ulong, Timer> _oreRespawnTimers = new Dictionary<ulong, Timer>();

        #endregion Fields
        
        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Delay Before Respawning Ore Nodes Seconds")]
            public float DelayBeforeRespawningOreNodesSeconds { get; set; }

            [JsonProperty("Minimum Number Of Ore Nodes To Spawn")]
            public int MinimumNumberOfOreNodesToSpawn { get; set; }

            [JsonProperty("Maximum Number Of Ore Nodes To Spawn")]
            public int MaximumNumberOfOreNodesToSpawn { get; set; }

            [JsonProperty("Chance To Spawn Each Ore Node")]
            public int ChanceToRespawnEachOreNode { get; set; }

            [JsonProperty("Minimum Spawn Radius")]
            public float MinimumSpawnRadius { get; set; }

            [JsonProperty("Maximum Spawn Radius")]
            public float MaximumSpawnRadius { get; set; }

            [JsonProperty("Maximum Search Attempts For Spawn Point")]
            public int MaximumSearchAttemptsForSpawnPoint { get; set; }

            [JsonProperty("Check Radius For Nearby Entities")]
            public float CheckRadiusForNearbyEntities { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                DelayBeforeRespawningOreNodesSeconds = 60f,
                MinimumNumberOfOreNodesToSpawn = 1,
                MaximumNumberOfOreNodesToSpawn = 2,
                ChanceToRespawnEachOreNode = 50,
                MinimumSpawnRadius = 5,
                MaximumSpawnRadius = 20f,
                MaximumSearchAttemptsForSpawnPoint = 10,
                CheckRadiusForNearbyEntities = 5f,
            };
        }

        #endregion Configuration

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
        }

        private void Unload()
        {
            foreach (Timer timer in _oreRespawnTimers.Values)
            {
                if (timer != null)
                    timer.Destroy();
            }

            _config = null;
            _plugin = null;
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null)
                return;

            OreResourceEntity oreNode = dispenser.GetComponentInParent<OreResourceEntity>();
            if (oreNode != null && !_oreRespawnTimers.ContainsKey(oreNode.net.ID.Value))
            {
                ScheduleOreRespawn(oreNode);
            }
        }

        #endregion Oxide Hooks

        #region Ore Node Spwaning

        private void ScheduleOreRespawn(BaseEntity oreNode)
        {
            ulong oreId = oreNode.net.ID.Value;
            Vector3 orePosition = oreNode.transform.position;
            string orePrefab = BuildOreNodePrefabPath(oreNode.PrefabName);

            Timer spawnIn = timer.Once(_config.DelayBeforeRespawningOreNodesSeconds, () =>
            {
                int oresToSpawn = Random.Range(_config.MinimumNumberOfOreNodesToSpawn, _config.MaximumNumberOfOreNodesToSpawn + 1);

                for (int i = 0; i < oresToSpawn; i++)
                {
                    if (!ChanceSucceeded(_config.ChanceToRespawnEachOreNode))
                        continue;

                    if (TryFindSuitableSpawnPoint(orePosition, _config.MinimumSpawnRadius, _config.MaximumSpawnRadius,
                        out Vector3 position, out Quaternion rotation, _config.MaximumSearchAttemptsForSpawnPoint))
                    {
                        Quaternion randomYRotation = Quaternion.Euler(0, Random.Range(0f, 360f), 0);
                        Quaternion finalRotation = rotation * randomYRotation;
                        SpawnOreNode(orePrefab, position, finalRotation);
                    }
                }

                _oreRespawnTimers.Remove(oreId);
            });

            _oreRespawnTimers[oreId] = spawnIn;
        }

        private bool TryFindSuitableSpawnPoint(Vector3 center, float minSearchRadius, float maxSearchRadius, out Vector3 position, out Quaternion rotation, int maxAttempts)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                Vector3 candidatePosition = TerrainUtil.GetRandomPositionAround(center, minSearchRadius, maxSearchRadius);

                if (TerrainUtil.GetGroundInfo(candidatePosition, out RaycastHit hit, 5f, LAYER_GROUND)
                    && !TerrainUtil.OnTopology(center, TerrainTopology.Enum.Road | TerrainTopology.Enum.Roadside | TerrainTopology.Enum.Rail | TerrainTopology.Enum.Railside)
                    && !TerrainUtil.HasEntityNearby(hit.point, _config.CheckRadiusForNearbyEntities, LAYER_ENTITIES | LAYER_PLAYERS)
                    && !TerrainUtil.InWater(hit.point))
                {
                    position = hit.point;
                    rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
                    return true;
                }
            }

            return false;
        }

        private OreResourceEntity SpawnOreNode(string prefabPath, Vector3 position, Quaternion rotation)
        {
            OreResourceEntity oreNode = GameManager.server.CreateEntity(prefabPath, position, rotation) as OreResourceEntity;
            if (oreNode == null)
                return null;

            oreNode.Spawn();
            return oreNode;
        }

        #endregion Ore Node Spwaning

        #region Ore Node Prefab Path Construction

        private string BuildOreNodePrefabPath(string originalPrefab)
        {
            string biomeSuffix;

            if (originalPrefab.Contains("snow"))
            {
                biomeSuffix = "ores_snow";
            }
            else if (originalPrefab.Contains("sand"))
            {
                biomeSuffix = "ores_sand";
            }
            else
            {
                biomeSuffix = "ores";
            }

            if (originalPrefab.Contains("metal"))
            {
                return string.Format("assets/bundled/prefabs/autospawn/resource/{0}/metal-ore.prefab", biomeSuffix);
            }
            else if (originalPrefab.Contains("stone"))
            {
                return string.Format("assets/bundled/prefabs/autospawn/resource/{0}/stone-ore.prefab", biomeSuffix);
            }
            else if (originalPrefab.Contains("sulfur"))
            {
                return string.Format("assets/bundled/prefabs/autospawn/resource/{0}/sulfur-ore.prefab", biomeSuffix);
            }

            return null;
        }

        #endregion Ore Node Prefab Path Construction

        #region Helper Functions

        private bool ChanceSucceeded(int percentage)
        {
            return Random.Range(0, 100) < percentage;
        }

        #endregion Helper Functions

        #region Helper Classes

        public static class TerrainUtil
        {
            public static bool OnTopology(Vector3 position, TerrainTopology.Enum topology)
            {
                return (TerrainMeta.TopologyMap.GetTopology(position) & (int)topology) != 0;
            }

            public static bool InWater(Vector3 position)
            {
                return WaterLevel.Test(position, false, false);
            }

            public static bool InsideRock(Vector3 position, float radius)
            {
                List<Collider> colliders = Pool.Get<List<Collider>>();
                Vis.Colliders(position, radius, colliders, Layers.Mask.World, QueryTriggerInteraction.Ignore);

                bool result = false;

                foreach (Collider collider in colliders)
                {
                    if (collider.name.Contains("rock", CompareOptions.OrdinalIgnoreCase)
                        || collider.name.Contains("cliff", CompareOptions.OrdinalIgnoreCase)
                        || collider.name.Contains("formation", CompareOptions.OrdinalIgnoreCase))
                    {
                        result = true;
                        break;
                    }
                }

                Pool.FreeUnmanaged(ref colliders);
                return result;
            }

            public static bool HasEntityNearby(Vector3 position, float radius, LayerMask mask, string prefabName = null)
            {
                List<Collider> hitColliders = Pool.Get<List<Collider>>();
                GamePhysics.OverlapSphere(position, radius, hitColliders, mask, QueryTriggerInteraction.Ignore);

                bool hasEntityNearby = false;
                foreach (Collider collider in hitColliders)
                {
                    BaseEntity entity = collider.gameObject.ToBaseEntity();
                    if (entity != null)
                    {
                        if (prefabName == null || entity.PrefabName == prefabName)
                        {
                            hasEntityNearby = true;
                            break;
                        }
                    }
                }

                Pool.FreeUnmanaged(ref hitColliders);
                return hasEntityNearby;
            }

            public static Vector3 GetRandomPositionAround(Vector3 center, float minimumRadius, float maximumRadius)
            {
                Vector3 randomDirection = Random.onUnitSphere;
                randomDirection.y = 0;
                float randomDistance = Random.Range(minimumRadius, maximumRadius);
                Vector3 randomPosition = center + randomDirection * randomDistance;

                return randomPosition;
            }

            public static bool GetGroundInfo(Vector3 startPosition, out RaycastHit raycastHit, float range, LayerMask mask)
            {
                return Physics.Linecast(startPosition + new Vector3(0.0f, range, 0.0f), startPosition - new Vector3(0.0f, range, 0.0f), out raycastHit, mask);
            }

            public static bool GetGroundInfo(Vector3 startPosition, out RaycastHit raycastHit, float range, LayerMask mask, Transform ignoreTransform = null)
            {
                startPosition.y += 0.25f;
                range += 0.25f;
                raycastHit = default;

                RaycastHit hit;
                if (!GamePhysics.Trace(new Ray(startPosition, Vector3.down), 0f, out hit, range, mask, QueryTriggerInteraction.UseGlobal, null))
                    return false;

                if (ignoreTransform != null && hit.collider != null
                    && (hit.collider.transform == ignoreTransform || hit.collider.transform.IsChildOf(ignoreTransform)))
                {
                    return GetGroundInfo(startPosition - new Vector3(0f, 0.01f, 0f), out raycastHit, range, mask, ignoreTransform);
                }

                raycastHit = hit;
                return true;
            }
        }

        #endregion Helper Classes
    }
}