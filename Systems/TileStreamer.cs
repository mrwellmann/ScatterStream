/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Profiling;

namespace AshleySeric.ScatterStream
{
    //[AlwaysUpdateSystem]
    [UpdateInGroup(typeof(ScatterStreamSystemGroup))]
    public partial class TileStreamer : SystemBase
    {
        public const int TILE_FILE_FORMAT_VERSION = 2;
        /// <summary>
        /// (4 for version number) + (16 * 2 for pos and scale) + (20 for rot) + (4 for prefabIndex).
        /// </summary>
        public const int TILE_ITEM_SIZE_IN_BYTES = 64;

        /// <summary>
        /// Used by UnloadTilesOutOfRange method.
        /// </summary>
        /// <typeparam name="TileCoords"></typeparam>
        /// <returns></returns>
        private HashSet<TileCoords> tilesToUnloadBuffer = new HashSet<TileCoords>();

        protected override void OnUpdate()
        {
            foreach (var streamKeyValue in ScatterStream.ActiveStreams)
            {
                if (streamKeyValue.Value != null)
                {
                    UpdateStream(streamKeyValue.Value);
                }
            }
        }

        private void UpdateStream(ScatterStream stream)
        {
            if (!stream.isInitialised)
            {
                stream.Initialise();
            }

            stream.totalTilesLoadedThisFrame = 0;

            if (stream.camera != null)
            {
                stream.previousFrameStreamToWorld = stream.streamToWorld;
                stream.streamToWorld = stream.parentTransform.localToWorldMatrix;
                stream.streamToWorld_Inverse = stream.streamToWorld.inverse;

                // Calculate world space frustum planes.
                GeometryUtility.CalculateFrustumPlanes(stream.camera, stream.localCameraFrustum);

                // Transform each plane into stream space.
                for (int i = 0; i < stream.localCameraFrustum.Length; i++)
                {
                    stream.localCameraFrustum[i] = stream.streamToWorld_Inverse.TransformPlane(stream.localCameraFrustum[i]);
                }
            }

            if (stream.contentModificationLockOwner == null)
            {
                ProcessDirtyTiles(stream);

                if (stream.renderingMode == RenderingMode.Entities)
                {
                    // Collect a list of loaded tiles.
                    stream.loadedTileCoords.Clear();
                    var streamId = stream.id;

                    Entities.ForEach((Entity tileEntity, in Tile tile) =>
                    {
                        if (tile.StreamId == streamId)
                        {
                            stream.loadedTileCoords.Add(tile.Coords);
                        }
                    }).WithoutBurst().Run();
                }
            }

            if (!stream.isRunningStreamingTasks && stream.contentModificationLockOwner == null)
            {
                // Load tile's in range if they haven't been already.
                var cameraPositionLocalToStream = (stream.streamToWorld_Inverse * stream.camera.transform.localToWorldMatrix).GetPosition();
                if (Vector3.Distance(cameraPositionLocalToStream, stream.localCameraPositionAtLastStream) > stream.streamingCameraMovementThreshold)
                {
                    _ = RunStreamingTasks(stream, cameraPositionLocalToStream);
                }
            }
        }

        private async Task RunStreamingTasks(ScatterStream stream, float3 cameraPositionLocalToStream)
        {
            stream.contentModificationLockOwner = this;
            stream.isRunningStreamingTasks = true;
            var streamingDistance = stream.GetStreamingDistance();

            var results = await CollectTileCoordsInRange(cameraPositionLocalToStream, streamingDistance, stream);
            // Swap out the tile coords in range buffer.
            if (stream.tileCoordsInRange.IsCreated)
            {
                stream.tileCoordsInRange.Dispose();
            }
            stream.tileCoordsInRange = results;

            UnloadTilesOutOfRange(stream);
            LoadTilesInRange(stream);
            stream.localCameraPositionAtLastStream = cameraPositionLocalToStream;
            stream.isRunningStreamingTasks = false;
            stream.contentModificationLockOwner = null;
        }

        private async Task<NativeParallelHashSet<TileCoords>> CollectTileCoordsInRange(float3 cameraPositionStreamSpace, float distance, ScatterStream stream)
        {
            var results = new NativeParallelHashSet<TileCoords>((int)math.ceil((distance * distance) / stream.tileWidth), Allocator.Persistent);
            var indexLimit = (int)math.ceil(distance / stream.tileWidth);
            var cameraPosFlattened = new float2(cameraPositionStreamSpace.x, cameraPositionStreamSpace.z);

            // Schedule tile sorting job on parallel threads.
            var tilesInRangeJob = new CollectTileCoordsInRangeJob
            {
                tileWidth = stream.tileWidth,
                halfTileWidth = stream.tileWidth * 0.5f,
                distanceSqr = distance * distance,
                cameraPositionStreamSpace = cameraPositionStreamSpace,
                cameraPositionStreamSpaceFlattened = cameraPosFlattened,
                indexLimit = indexLimit,
                nearestTileCoords = new int2((int)math.floor(cameraPosFlattened.x / stream.tileWidth), (int)math.floor(cameraPosFlattened.y / stream.tileWidth)),
                resultsWriter = results.AsParallelWriter()
            }.Schedule(indexLimit * 2, 16); // x2 as it goes in both directions on each axis.

            // Wait for the tile sorting job to complete.
            await tilesInRangeJob;
            tilesInRangeJob.Complete();

            return results;
        }

        /// <summary>
        /// Returns tile entity of tile could be successfully loaded from disk. If not, returns Entity.Null;
        /// </summary>
        /// <param name="filePath">Absolute file path to tile file on disk.</param>
        /// <param name="coords"></param>
        /// <param name="stream"></param>
        /// <param name="commandBuffer"></param>
        /// <returns></returns>
        private async Task<bool> StreamInTile_InstancedRendering(string filePath, TileCoords coords, ScatterStream stream)
        {
            // Await any pre-load hooks (such as downloading the tile from a remote server).
            if (stream.OnBeforeTileLoadedFromDisk != null && !await stream.OnBeforeTileLoadedFromDisk(coords))
            {
                // Don't immediately try to load this tile again.
                stream.attemptedLoadButDoNotExist.Add(coords);
                return false;
            }

            stream.tilesBeingStreamedIn.Add(coords);
            bool success = false;

            if (File.Exists(filePath))
            {
                //Debug.Log($"Streaming IN: {coords}");
                if (stream.totalTilesLoadedThisFrame >= stream.maxTilesLoadedPerFrame)
                {
                    await UniTask.WaitUntil(() => stream.totalTilesLoadedThisFrame < stream.maxTilesLoadedPerFrame);
                }

                stream.totalTilesLoadedThisFrame++;

                // Create a tile & setup necessary buffers.
                var instances = new List<List<Tile_InstancedRendering.RuntimeInstance>>(stream.presets.Presets.Length);
                // Pre-populate the lists so we have indexes for each preset.
                foreach (var item in stream.presets.Presets)
                {
                    instances.Add(new List<Tile_InstancedRendering.RuntimeInstance>());
                }

                // Create and register a new tile for instanced rendering.
                Tile_InstancedRendering tile = new Tile_InstancedRendering
                {
                    coords = coords,
                    instances = instances
                };

                // Add each instance to this new tile.
                void onInstanceLoaded(ScatterItemInstanceData instanceData)
                {
                    if (instanceData.streamGuid == stream.id)
                    {
                        instances[instanceData.presetIndex].Add(new Tile_InstancedRendering.RuntimeInstance
                        {
                            colour = instanceData.colour,
                            localToStream = instanceData.localToStream
                        });
                    }
                }

                success = true;
                await Task.Run(() =>
                {
                    using (var readerStream = File.OpenRead(filePath))
                    {
                        using (var reader = new BinaryReader(readerStream))
                        {
                            if (!LoadTileCache(reader, stream, onInstanceLoaded))
                            {
                                success = false;
                            }
                        }
                    }
                });

                tile.RenderBounds = await Tile.GetTileBounds_LocalToStream_Async(tile.instances, stream);

                // Wait for anyone else to finish modifying content
                if (stream.contentModificationLockOwner != null)
                {
                    await UniTask.WaitUntil(() => stream.contentModificationLockOwner == null);
                }

                stream.LoadedInstanceRenderingTiles.Add(coords, tile);
                stream.areInstancedRenderingSortedBuffersDirty = true;
            }

            if (success)
            {
                stream.loadedTileCoords.Add(coords);
            }
            else
            {
                // TODO: Handle some kind of periodic flushing of this hashmap 
                //       in case a new tile file has been added/downloaded.
                stream.attemptedLoadButDoNotExist.Add(coords);
            }

            stream.tilesBeingStreamedIn.Remove(coords);
            stream.OnTileStreamInComplete?.Invoke(coords);
            return success;
        }

        private bool StreamInTile_ECS(string filePath, TileCoords coords, ScatterStream stream, EntityCommandBuffer commandBuffer)
        {
            bool success = false;

            if (File.Exists(filePath))
            {
                // Create a new entity for this tile.
                var tileEntity = EntityManager.CreateEntity();
#if UNITY_EDITOR
                // Name the tile for in-editor debugging.
                EntityManager.SetName(tileEntity, $"Loaded Tile: {Path.GetFileNameWithoutExtension(filePath)}");
#endif
                var buffer = new EntityCommandBuffer(Allocator.Persistent);
                buffer.AddComponent(tileEntity, new Tile { StreamId = stream.id, Coords = coords });
                // Add a buffer on this tile to store each scatter item.
                buffer.AddBuffer<ScatterItemEntityBuffer>(tileEntity);
                buffer.Playback(EntityManager);
                buffer.Dispose();

                Action<ScatterItemInstanceData> onInstanceLoaded = (instanceData) =>
                {
                    var trans = stream.streamToWorld * (Matrix4x4)instanceData.localToStream;
                    var itemEntity = commandBuffer.Instantiate(stream.itemPrefabEntities[instanceData.presetIndex]);

                    // Set component data on the spawned entity.
                    commandBuffer.SetComponent(itemEntity, new Rotation { Value = trans.GetRotation() });
                    commandBuffer.SetComponent(itemEntity, new Translation { Value = trans.GetPosition() });
                    commandBuffer.AddComponent(itemEntity, new NonUniformScale { Value = trans.GetScale() });
                    commandBuffer.AddComponent(itemEntity, instanceData);

                    // Add this entity into the tile's buffer.
                    commandBuffer.AppendToBuffer<ScatterItemEntityBuffer>(tileEntity, itemEntity);
                };

                success = true;
                using (var readerStream = File.OpenRead(filePath))
                {
                    using (var reader = new BinaryReader(readerStream))
                    {
                        if (!LoadTileCache(reader, stream, onInstanceLoaded))
                        {
                            success = false;
                        }
                    }
                }
            }

            if (success)
            {
                stream.loadedTileCoords.Add(coords);
            }
            else
            {
                // TODO: Handle some kind of periodic incremental flushing of this hashmap 
                //       in case a new tile file has been added/downloaded.
                stream.attemptedLoadButDoNotExist.Add(coords);
            }

            stream.tilesBeingStreamedIn.Remove(coords);
            stream.OnTileStreamInComplete?.Invoke(coords);
            return success;
        }

        private void LoadTilesInRange(ScatterStream stream)
        {
            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    Profiler.BeginSample("ScatterStream.TileStreamer.QueueAsyncLoadingTilesInRange_InstancedRendering");
                    foreach (var coords in stream.tileCoordsInRange)
                    {
                        if (!stream.tilesBeingStreamedIn.Contains(coords) &&
                            !stream.loadedTileCoords.Contains(coords) &&
                            !stream.attemptedLoadButDoNotExist.Contains(coords))
                        {
                            _ = StreamInTile_InstancedRendering(stream.GetTileFilePath(coords), coords, stream);
                        }
                    }

                    break;
                case RenderingMode.Entities:
                    Profiler.BeginSample("ScatterStream.TileStreamer.LoadTilesInRange_Entities");
                    var commandBuffer = new EntityCommandBuffer(Allocator.Persistent);
                    var streamGuid = stream.id;

                    // TODO: - Load tiles in async jobs.  
                    //       - Track streaming jobs to avoid loading/unloading the same tile at the same time.
                    foreach (var coords in stream.tileCoordsInRange)
                    {
                        if (!stream.loadedTileCoords.Contains(coords) && !stream.attemptedLoadButDoNotExist.Contains(coords))
                        {
                            StreamInTile_ECS(stream.GetTileFilePath(coords), coords, stream, commandBuffer);
                        }
                    }

                    commandBuffer.Playback(EntityManager);
                    commandBuffer.Dispose();
                    break;
            }

            // TODO: Check if I need to re-initialise this enumerator since adding values to the NativeMultiHashMap.
            var attemptedLoadEnumerator = stream.attemptedLoadButDoNotExist.GetEnumerator();
            var tileCoordsInRangeBuffer = stream.tileCoordsInRange;
            var attemptedLoadButDoNotExist = stream.attemptedLoadButDoNotExist;
            var streamId = stream.id;

            // Cleanup any failed attempt tiles that are now out of bounds.
            Job.WithCode(() =>
            {
                var tilesToRemove = new NativeParallelHashSet<TileCoords>(0, Allocator.TempJob);

                while (attemptedLoadEnumerator.MoveNext())
                {
                    var coords = attemptedLoadEnumerator.Current;

                    if (!tileCoordsInRangeBuffer.Contains(coords))
                    {
                        tilesToRemove.Add(coords);
                    }
                }

                foreach (var tileMeta in tilesToRemove)
                {
                    attemptedLoadButDoNotExist.Remove(tileMeta);
                }
                tilesToRemove.Dispose();
            }).Run();

            Profiler.EndSample();
        }

        public void UnloadTilesOutOfRange(ScatterStream stream)
        {
            Profiler.BeginSample("ScatterStream.TileStreamer.UnloadTilesOutOfRange");

            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    {
                        float halfTileWidth = stream.tileWidth;
                        tilesToUnloadBuffer.Clear();

                        foreach (var coordsTileKvp in stream.LoadedInstanceRenderingTiles)
                        {
                            if (!stream.tileCoordsInRange.Contains(coordsTileKvp.Key))
                            {
                                tilesToUnloadBuffer.Add(coordsTileKvp.Key);
                            }
                        }

                        foreach (var tileCoords in tilesToUnloadBuffer)
                        {
                            stream.loadedTileCoords.Remove(tileCoords);
                            stream.LoadedInstanceRenderingTiles.Remove(tileCoords);
                        }

                        tilesToUnloadBuffer.Clear();

                        if (tilesToUnloadBuffer.Count > 0)
                        {
                            stream.areInstancedRenderingSortedBuffersDirty = true;
                        }
                    }
                    break;
                case RenderingMode.Entities:
                    {
                        var commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                        var commandBufferParrallelWriter = commandBuffer.AsParallelWriter();
                        var tileItemEntityBuffer = GetBufferLookup<ScatterItemEntityBuffer>(true);
                        var coordsInRange = stream.tileCoordsInRange;
                        var streamId = stream.id;

                        Dependency = Entities.ForEach((Entity tileEntity, int entityInQueryIndex, in Tile tile) =>
                        {
                            // Check if this tile is beyond the unload distance on x/z axis.
                            if (tile.StreamId == streamId && !coordsInRange.Contains(tile.Coords))
                            {
                                // Delete tile and all items associated with it.
                                foreach (var item in tileItemEntityBuffer[tileEntity])
                                {
                                    commandBufferParrallelWriter.DestroyEntity(entityInQueryIndex, item);
                                }
                                commandBufferParrallelWriter.DestroyEntity(entityInQueryIndex, tileEntity);
                            }
                        })
                        .WithReadOnly(tileItemEntityBuffer)
                        .WithReadOnly(coordsInRange)
                        .ScheduleParallel(Dependency);

                        Dependency.Complete();
                        commandBuffer.Playback(EntityManager);
                        commandBuffer.Dispose();
                    }
                    break;
            }

            Profiler.EndSample();
        }

        private async void ProcessDirtyTiles(ScatterStream stream)
        {
            stream.contentModificationLockOwner = this;

            try
            {
                switch (stream.renderingMode)
                {
                    case RenderingMode.DrawMeshInstanced:
                        // Save dirty tiles to disk.
                        var tileCoordsToRemove = new HashSet<TileCoords>();
                        foreach (var tileCoords in stream.dirtyInstancedRenderingTiles)
                        {
                            if (!stream.tilesBeingStreamedIn.Contains(tileCoords))
                            {
                                // Calculate bounds from it's placed meshes.
                                var tile = stream.LoadedInstanceRenderingTiles[tileCoords];
                                tile.RenderBounds = Tile.GetTileBounds_LocalToStream(tile.instances, stream);
                                // Save this tile to disk.
                                await SaveTileToDisk_InstancedRendering(stream, tileCoords);
                                tileCoordsToRemove.Add(tileCoords);
                            }
                        }

                        // Remove processed tiles outside the foreach.
                        foreach (var tileCoords in tileCoordsToRemove)
                        {
                            stream.dirtyInstancedRenderingTiles.Remove(tileCoords);
                        }
                        tileCoordsToRemove.Clear();
                        break;
                    case RenderingMode.Entities:
                        var streamGuid = stream.id;
                        var commandBuffer = new EntityCommandBuffer(Allocator.Persistent);
                        var manager = World.DefaultGameObjectInjectionWorld.EntityManager;
                        var dirtyTilesQuery = manager.CreateEntityQuery(typeof(Tile), typeof(DirtyTag));
                        var tileEntities = dirtyTilesQuery.ToEntityArray(Allocator.Persistent);
                        var filter = manager.GetEntityQueryMask(dirtyTilesQuery);

                        // Save dirty tiles to disk.
                        foreach (var tileEntity in tileEntities)
                        {
                            // Esure this is still a valid dirty tile as we may
                            // have waited a few frames since the initial query.
                            if (!tileEntity.Equals(Entity.Null) && filter.MatchesIgnoreFilter(tileEntity))
                            {
                                var tile = SystemAPI.GetComponent<Tile>(tileEntity);

                                if (tile.StreamId == streamGuid)
                                {
                                    await SaveTileToDisk_Entities(tileEntity, stream, tile.Coords);
                                    commandBuffer.RemoveComponent<DirtyTag>(tileEntity);
                                    // Ensure we don't exclude this tile from streaming ops if it's been newly created.
                                    stream.attemptedLoadButDoNotExist.Remove(tile.Coords);
                                }
                            }
                        }

                        tileEntities.Dispose();
                        dirtyTilesQuery.Dispose();
                        commandBuffer.Playback(EntityManager);
                        commandBuffer.Dispose();
                        break;
                }

            }
            catch (Exception e)
            {
                Debug.LogError($"Something went wrong attempting to process dirty tile. {e}");
            }

            stream.contentModificationLockOwner = null;
        }

        private async Task SaveTileToDisk_InstancedRendering(ScatterStream stream, TileCoords tileCoords)
        {
            if (!stream.LoadedInstanceRenderingTiles.ContainsKey(tileCoords))
            {
                Debug.LogError("Attempting to save a tile that isn't currently loaded.");
                return;
            }

            var fileName = stream.GetTileFilePath(tileCoords);
            var tileInstances = stream.LoadedInstanceRenderingTiles[tileCoords].instances;
            var genericTileInstances = new List<List<GenericInstancePlacementData>>();

            // Convert instances into generic format for serialization.
            foreach (var preset in tileInstances)
            {
                var list = new List<GenericInstancePlacementData>();

                foreach (var item in preset)
                {
                    list.Add(new GenericInstancePlacementData
                    {
                        localToStream = item.localToStream,
                        colour = item.colour
                    });
                }

                genericTileInstances.Add(list);
            }

            await Task.Run(async () =>
            {
                // Delete any existing file for this tile.
                if (File.Exists(fileName))
                {
                    File.Delete(fileName);
                }

                if (tileInstances.Sum(x => x.Count) > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fileName));

                    // Save this tile to disk.
                    using (var writeStream = File.OpenWrite(fileName))
                    {
                        using (var writer = new BinaryWriter(writeStream))
                        {
                            await EncodeToTileCache(
                                genericTileInstances,
                                writer,
                                stream.brushConfig.maxTileEncodeTimePerFrame,
                                stream.brushConfig.maxTileEncodingItemsPerFrame);
                        }
                    }
                }
            });
        }

        private async Task SaveTileToDisk_Entities(Entity tileEntity, ScatterStream stream, TileCoords tileCoords)
        {
            var fileName = stream.GetTileFilePath(tileCoords);

            // Delete any existing file for this tile.
            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }

            var tileItemBuffer = GetBufferLookup<ScatterItemEntityBuffer>(true)[tileEntity];

            if (tileItemBuffer.Length > 0)
            {
                // Save this tile to disk.
                Directory.CreateDirectory(Path.GetDirectoryName(fileName));
                using (var writeStream = File.OpenWrite(fileName))
                {
                    using (var writer = new BinaryWriter(writeStream))
                    {
                        await EncodeToTileCache(
                            tileEntity,
                            tileItemBuffer,
                            writer,
                            stream.brushConfig.maxTileEncodeTimePerFrame,
                            stream.brushConfig.maxTileEncodingItemsPerFrame);
                    }
                }
            }
        }

        /// <summary>
        /// Writes contents of a give tile's item buffer into binary.
        /// </summary>
        /// <param name="tileItemBuffer">Parent list index is the preset index</param>
        /// <param name="writer"></param>
        /// <param name="maxTimePerFrame"></param>
        /// <param name="maxTileEncodingItemsPerFrame"></param>
        /// <returns></returns>
        public static async Task EncodeToTileCache(List<List<GenericInstancePlacementData>> tileItemBuffer,
                                                    BinaryWriter writer,
                                                    float maxTimePerFrame,
                                                    int maxTileEncodingItemsPerFrame)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Write file format version as the first 4 bytes.
            writer.Write(TILE_FILE_FORMAT_VERSION);
            int presetCount = tileItemBuffer.Count;

            // TODO: Store in order of presets allowing sequential loading as well as reduced
            //       file sizes since we wouldn't need to store the preset index for each instance.
            for (int presetIndex = 0; presetIndex < presetCount; presetIndex++)
            {
                int instanceCount = tileItemBuffer[presetIndex].Count;
                for (int instanceIndex = 0; instanceIndex < instanceCount; instanceIndex++)
                {
                    var inst = tileItemBuffer[presetIndex][instanceIndex];
                    var pos = inst.localToStream.GetPosition();
                    var rot = inst.localToStream.GetRotation();
                    var scale = inst.localToStream.GetScale();

                    // Position
                    writer.Write(pos.x);
                    writer.Write(pos.y);
                    writer.Write(pos.z);

                    // Rotation
                    writer.Write(rot.value.x);
                    writer.Write(rot.value.y);
                    writer.Write(rot.value.z);
                    writer.Write(rot.value.w);

                    // Scale
                    writer.Write(scale.x);
                    writer.Write(scale.y);
                    writer.Write(scale.z);

                    // Preset index
                    writer.Write(presetIndex);

                    // Colour
                    writer.Write(inst.colour.x);
                    writer.Write(inst.colour.y);
                    writer.Write(inst.colour.z);
                    writer.Write(inst.colour.w);

                    // Check if we should await a frame before continuing.
                    if (stopwatch.ElapsedMilliseconds > maxTimePerFrame || presetIndex > maxTileEncodingItemsPerFrame)
                    {
                        GC.Collect();
                        await UniTask.NextFrame();
                        stopwatch.Reset();
                        stopwatch.Start();
                    }
                }
            }
        }

        public async Task EncodeToTileCache(Entity tileEntity,
                                            DynamicBuffer<ScatterItemEntityBuffer> tileItemBuffer,
                                            BinaryWriter writer,
                                            float maxTimePerFrame,
                                            int maxTileEncodingItemsPerFrame)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var translationFromEntity = GetBufferLookup<Translation>(true);
            var rotationFromEntity = GetBufferLookup<Rotation>(true);
            var scaleFromEntity = GetBufferLookup<NonUniformScale>(true);
            var scatterItemData = GetComponentLookup<ScatterItemEntityData>(true);

            // File format version.
            writer.Write(TILE_FILE_FORMAT_VERSION);

            for (int i = 0, itemIndex = 0; i < tileItemBuffer.Length * TILE_ITEM_SIZE_IN_BYTES; i += TILE_ITEM_SIZE_IN_BYTES, itemIndex++)
            {
                var entity = tileItemBuffer[itemIndex];
                var pos = translationFromEntity[entity.Entity].Value;
                var rot = rotationFromEntity[entity.Entity].Value.value;
                var scale = scaleFromEntity[entity.Entity].Value;

                // Position.
                writer.Write(pos.x);
                writer.Write(pos.y);
                writer.Write(pos.z);

                // Rotation.
                writer.Write(rot.x);
                writer.Write(rot.y);
                writer.Write(rot.z);
                writer.Write(rot.w);

                // Scale.
                writer.Write(scale.x);
                writer.Write(scale.y);
                writer.Write(scale.z);

                var presetIndex = scatterItemData[entity].prefabIndex;
                // Preset index.
                // TODO: Swap this to order the list of transforms by prefab index so we don't have to store it for each item.
                writer.Write(presetIndex);

                // TODO: Implement colour support in ECS.
                // Colour.
                writer.Write(1f);
                writer.Write(1f);
                writer.Write(1f);
                writer.Write(1f);

                // TODO: Work out how to allow this await while remaining compatible with ecs calls.
                // Checke if we should await a frame before continuing.
                if (stopwatch.ElapsedMilliseconds > maxTimePerFrame || presetIndex > maxTileEncodingItemsPerFrame)
                {
                    GC.Collect();
                    await UniTask.NextFrame();
                    stopwatch.Reset();
                    stopwatch.Start();

                    // Refresh ECS getters as there may have been structural changes during the await.
                    tileItemBuffer = GetBufferLookup<ScatterItemEntityBuffer>(true)[tileEntity];
                    translationFromEntity = GetComponentLookup<Translation>(true);
                    rotationFromEntity = GetComponentLookup<Rotation>(true);
                    scaleFromEntity = GetComponentLookup<NonUniformScale>(true);
                    scatterItemData = GetComponentLookup<ScatterItemEntityData>(true);
                }
            }
        }

        public static bool LoadTileCache(BinaryReader reader,
                                         ScatterStream stream,
                                         Action<ScatterItemInstanceData> onItemLoaded)
        {
            try
            {
                // File format version.
                var formatVersion = reader.ReadInt32();

                if (formatVersion > 3)
                {
                    // We don't know how to deserialize this version, 
                    // it's newer than this build.
                    return false;
                }

                // Read placed items from here until the end of the file.
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    // Position.
                    var pos = new float3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );

                    // Rotation.
                    var rot = new quaternion(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );

                    // Scale.
                    var scale = new float3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );

                    // Preset index.
                    var prefabIndex = reader.ReadInt32();

                    float4 colour = default;

                    if (formatVersion == 2)
                    {
                        // Colour.
                        colour = new float4(
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle()
                        );
                    }

                    onItemLoaded?.Invoke(new ScatterItemInstanceData
                    {
                        streamGuid = stream.id,
                        presetIndex = prefabIndex,
                        localToStream = float4x4.TRS(pos, rot, scale),
                        colour = colour
                    });
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not deserialize tile data: {e}");
                return false;
            }
        }
    }
}