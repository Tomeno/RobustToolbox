using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Robust.Server.GameObjects
{
    /// <summary>
    ///     Stores what entities intersect a particular tile.
    /// </summary>
    [UsedImplicitly]
    public sealed class GridTileLookupSystem : EntitySystem
    {
        [Dependency] private readonly IEntityLookup _lookup = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;

        private readonly Dictionary<GridId, Dictionary<Vector2i, GridTileLookupChunk>> _graph =
                     new();

        /// <summary>
        ///     Need to store the nodes for each entity because if the entity is deleted its transform is no longer valid.
        /// </summary>
        private readonly Dictionary<IEntity, HashSet<GridTileLookupNode>> _lastKnownNodes =
                     new();

        /// <summary>
        ///     Yields all of the entities intersecting a particular entity's tiles.
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        public IEnumerable<IEntity> GetEntitiesIntersecting(IEntity entity)
        {
            foreach (var node in GetOrCreateNodes(entity))
            {
                foreach (var ent in node.Entities)
                {
                    yield return ent;
                }
            }
        }

        /// <summary>
        ///     Yields all of the entities intersecting a particular Vector2i
        /// </summary>
        /// <param name="gridId"></param>
        /// <param name="gridIndices"></param>
        /// <returns></returns>
        public IEnumerable<IEntity> GetEntitiesIntersecting(GridId gridId, Vector2i gridIndices)
        {
            if (gridId == GridId.Invalid)
            {
                throw new InvalidOperationException("Can't get grid tile intersecting entities for invalid grid");
            }

            if (!_graph.TryGetValue(gridId, out var chunks))
            {
                throw new InvalidOperationException($"Unable to find grid {gridId} for TileLookup");
            }

            var chunkIndices = GetChunkIndices(gridIndices);
            if (!chunks.TryGetValue(chunkIndices, out var chunk))
            {
                yield break;
            }

            foreach (var entity in chunk.GetNode(gridIndices).Entities)
            {
                yield return entity;
            }
        }

        public List<Vector2i> GetIndices(IEntity entity)
        {
            var results = new List<Vector2i>();

            if (!_lastKnownNodes.TryGetValue(entity, out var nodes))
            {
                return results;
            }

            foreach (var node in nodes)
            {
                results.Add(node.Indices);
            }

            return results;
        }

        private GridTileLookupChunk GetOrCreateChunk(GridId gridId, Vector2i indices)
        {
            var chunkIndices = GetChunkIndices(indices);

            if (!_graph.TryGetValue(gridId, out var gridChunks))
            {
                gridChunks = new Dictionary<Vector2i, GridTileLookupChunk>();
                _graph[gridId] = gridChunks;
            }

            if (!gridChunks.TryGetValue(chunkIndices, out var chunk))
            {
                chunk = new GridTileLookupChunk(gridId, chunkIndices);
                gridChunks[chunkIndices] = chunk;
            }

            return chunk;
        }

        private Vector2i GetChunkIndices(Vector2i indices)
        {
            return new(
                (int) (Math.Floor((float) indices.X / GridTileLookupChunk.ChunkSize) * GridTileLookupChunk.ChunkSize),
                (int) (Math.Floor((float) indices.Y / GridTileLookupChunk.ChunkSize) * GridTileLookupChunk.ChunkSize));
        }

        private HashSet<GridTileLookupNode> GetOrCreateNodes(IEntity entity)
        {
            if (entity.Deleted)
            {
                throw new InvalidOperationException($"Can't get nodes for deleted entity {entity.Name}!");
            }

            if (_lastKnownNodes.TryGetValue(entity, out var nodes))
            {
                return nodes;
            }

            var grids = GetEntityIndices(entity);
            var results = new HashSet<GridTileLookupNode>();

            foreach (var (grid, indices) in grids)
            {
                foreach (var index in indices)
                {
                    results.Add(GetOrCreateNode(grid, index));
                }
            }

            _lastKnownNodes[entity] = results;
            return results;
        }

        private HashSet<GridTileLookupNode> GetOrCreateNodes(EntityCoordinates coordinates, Box2 box)
        {
            var results = new HashSet<GridTileLookupNode>();

            foreach (var grid in _mapManager.FindGridsIntersecting(coordinates.GetMapId(EntityManager), box))
            {
                foreach (var tile in grid.GetTilesIntersecting(box, false))
                {
                    results.Add(GetOrCreateNode(grid.Index, tile.GridIndices));
                }
            }

            return results;
        }

        /// <summary>
        ///     Return the corresponding TileLookupNode for these indices
        /// </summary>
        /// <param name="gridId"></param>
        /// <param name="indices"></param>
        /// <returns></returns>
        private GridTileLookupNode GetOrCreateNode(GridId gridId, Vector2i indices)
        {
            var chunk = GetOrCreateChunk(gridId, indices);

            return chunk.GetNode(indices);
        }

        /// <summary>
        ///     Get the relevant GridId and Vector2i for this entity for lookup.
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        private Dictionary<GridId, List<Vector2i>> GetEntityIndices(IEntity entity)
        {
            var entityBounds = GetEntityBox(entity);
            var results = new Dictionary<GridId, List<Vector2i>>();

            foreach (var grid in _mapManager.FindGridsIntersecting(entity.Transform.MapID, GetEntityBox(entity)))
            {
                var indices = new List<Vector2i>();

                foreach (var tile in grid.GetTilesIntersecting(entityBounds, false))
                {
                    indices.Add(tile.GridIndices);
                }

                results[grid.Index] = indices;
            }

            return results;
        }

        private Box2 GetEntityBox(IEntity entity)
        {
            var aabb = _lookup.GetWorldAabbFromEntity(entity);

            // Need to clip the aabb as anything with an edge intersecting another tile might be picked up, such as walls.
            return aabb.Scale(0.98f);
        }

        public override void Initialize()
        {
            base.Initialize();
            #if DEBUG
            SubscribeNetworkEvent<RequestGridTileLookupMessage>(HandleRequest);
            #endif
            SubscribeLocalEvent<MoveEvent>(HandleEntityMove);
            SubscribeLocalEvent<EntInsertedIntoContainerMessage>(HandleContainerInsert);
            SubscribeLocalEvent<EntRemovedFromContainerMessage>(HandleContainerRemove);
            SubscribeLocalEvent<EntityInitializedMessage>(HandleEntityInitialized);
            SubscribeLocalEvent<EntityDeletedMessage>(HandleEntityDeleted);
            _mapManager.OnGridCreated += HandleGridCreated;
            _mapManager.OnGridRemoved += HandleGridRemoval;
            _mapManager.TileChanged += HandleTileChanged;
        }

        private void HandleContainerRemove(EntRemovedFromContainerMessage ev)
        {
            HandleEntityAdd(ev.Entity);
        }

        private void HandleContainerInsert(EntInsertedIntoContainerMessage ev)
        {
            HandleEntityRemove(ev.Entity);
        }

        public override void Shutdown()
        {
            base.Shutdown();

            _mapManager.OnGridCreated -= HandleGridCreated;
            _mapManager.OnGridRemoved -= HandleGridRemoval;
            _mapManager.TileChanged -= HandleTileChanged;
        }

#if DEBUG
        private void HandleRequest(RequestGridTileLookupMessage message, EntitySessionEventArgs args)
        {
            var entities = GetEntitiesIntersecting(message.GridId, message.Indices).Select(e => e.Uid).ToList();
            RaiseNetworkEvent(new SendGridTileLookupMessage(message.GridId, message.Indices, entities), args.SenderSession.ConnectedClient);
        }
#endif

        private void HandleEntityInitialized(EntityInitializedMessage message)
        {
            if (message.Entity.IsInContainer())
            {
                return;
            }

            HandleEntityAdd(message.Entity);
        }

        private void HandleEntityDeleted(EntityDeletedMessage message)
        {
            HandleEntityRemove(message.Entity);
        }

        private void HandleTileChanged(object? sender, TileChangedEventArgs eventArgs)
        {
            GetOrCreateNode(eventArgs.NewTile.GridIndex, eventArgs.NewTile.GridIndices);
        }

        private void HandleGridCreated(MapId mapId, GridId gridId)
        {
            _graph[gridId] = new Dictionary<Vector2i, GridTileLookupChunk>();
        }

        private void HandleGridRemoval(MapId mapId, GridId gridId)
        {
            var toRemove = new List<IEntity>();

            foreach (var (entity, _) in _lastKnownNodes)
            {
                if (entity.Deleted || entity.Transform.GridID == gridId)
                    toRemove.Add(entity);
            }

            foreach (var entity in toRemove)
            {
                _lastKnownNodes.Remove(entity);
            }

            _graph.Remove(gridId);
        }

        /// <summary>
        ///     Tries to add the entity to the relevant TileLookupNode
        /// </summary>
        /// The node will filter it to the correct category (if possible)
        /// <param name="entity"></param>
        private void HandleEntityAdd(IEntity entity)
        {
            if (entity.Deleted || entity.Transform.GridID == GridId.Invalid)
            {
                return;
            }

            var entityNodes = GetOrCreateNodes(entity);
            var newIndices = new Dictionary<GridId, List<Vector2i>>();

            foreach (var node in entityNodes)
            {
                node.AddEntity(entity);
                if (!newIndices.TryGetValue(node.ParentChunk.GridId, out var existing))
                {
                    existing = new List<Vector2i>();
                    newIndices[node.ParentChunk.GridId] = existing;
                }

                existing.Add(node.Indices);
            }

            _lastKnownNodes[entity] = entityNodes;
            EntityManager.EventBus.RaiseEvent(EventSource.Local, new TileLookupUpdateMessage(newIndices));
        }

        /// <summary>
        ///     Removes this entity from all of the applicable nodes.
        /// </summary>
        /// <param name="entity"></param>
        private void HandleEntityRemove(IEntity entity)
        {
            if (_lastKnownNodes.TryGetValue(entity, out var nodes))
            {
                foreach (var node in nodes)
                {
                    node.RemoveEntity(entity);
                }
            }

            _lastKnownNodes.Remove(entity);
            EntityManager.EventBus.RaiseEvent(EventSource.Local, new TileLookupUpdateMessage(null));
        }

        /// <summary>
        ///     When an entity moves around we'll remove it from its old node and add it to its new node (if applicable)
        /// </summary>
        /// <param name="moveEvent"></param>
        private void HandleEntityMove(ref MoveEvent moveEvent)
        {
            // TODO: When Acruid does TileEntities we may actually be able to delete this system if tile lookups become fast enough
            var gridId = moveEvent.NewPosition.GetGridId(EntityManager);

            if (moveEvent.Sender.Deleted ||
                gridId == GridId.Invalid ||
                !moveEvent.NewPosition.IsValid(EntityManager))
            {
                HandleEntityRemove(moveEvent.Sender);
                return;
            }

            if (!_lastKnownNodes.TryGetValue(moveEvent.Sender, out var oldNodes))
            {
                return;
            }

            // Memory leak protection
            var gridBounds = _mapManager.GetGrid(gridId).WorldBounds;
            if (!gridBounds.Contains(moveEvent.Sender.Transform.WorldPosition))
            {
                HandleEntityRemove(moveEvent.Sender);
                return;
            }

            var bounds = moveEvent.WorldAABB ?? GetEntityBox(moveEvent.Sender);
            var newNodes = GetOrCreateNodes(moveEvent.NewPosition, bounds);

            if (oldNodes.Count == newNodes.Count && oldNodes.SetEquals(newNodes))
            {
                return;
            }

            var toRemove = oldNodes.Where(oldNode => !newNodes.Contains(oldNode));
            var toAdd = newNodes.Where(newNode => !oldNodes.Contains(newNode));

            foreach (var node in toRemove)
            {
                node.RemoveEntity(moveEvent.Sender);
            }

            foreach (var node in toAdd)
            {
                node.AddEntity(moveEvent.Sender);
            }

            var newIndices = new Dictionary<GridId, List<Vector2i>>();
            foreach (var node in newNodes)
            {
                if (!newIndices.TryGetValue(node.ParentChunk.GridId, out var existing))
                {
                    existing = new List<Vector2i>();
                    newIndices[node.ParentChunk.GridId] = existing;
                }

                existing.Add(node.Indices);
            }

            _lastKnownNodes[moveEvent.Sender] = newNodes;
            EntityManager.EventBus.RaiseEvent(EventSource.Local, new TileLookupUpdateMessage(newIndices));
        }
    }
}
