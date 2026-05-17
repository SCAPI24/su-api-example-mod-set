using Engine;
using Game;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// Partition-based terrain data caching for minimap
// Source: reference SurvivalcraftMiniMap/PartitionManager.cs

namespace SurvivalcraftMiniMap
{
    public sealed class PartitionManager : IDisposable
    {
        public const float MapRadius = 400f;
        private const int PartitionSize = 32;
        private const float MoveThreshold = 10f;
        private const int ValidationInterval = 1000;

        private ConcurrentDictionary<Point2, int[]> _partitionData = new ConcurrentDictionary<Point2, int[]>();
        private HashSet<Point2> _activePartitions = new HashSet<Point2>();
        private ConcurrentQueue<Point2> _dirtyCoordinates = new ConcurrentQueue<Point2>();
        private Vector2 _lastPlayerPosition;
        private readonly SubsystemTerrain _terrain;
        private int _updateCount;

        public int LoadedPartitionCount => _partitionData.Count;
        public int DirtyCoordinatesCount => _dirtyCoordinates.Count;

        public PartitionManager(SubsystemTerrain terrain)
        {
            _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        }

        public void Update(Vector2 playerPosition)
        {
            _updateCount++;
            if (_updateCount % ValidationInterval == 0)
            {
                ValidateActivePartitions();
            }

            if (Vector2.DistanceSquared(playerPosition, _lastPlayerPosition) < MoveThreshold * MoveThreshold)
                return;

            var playerPositionP = new Point2((int)playerPosition.X, (int)playerPosition.Y);
            _lastPlayerPosition = playerPosition;
            MarkCoordinateDirty(playerPositionP);
            LoadRequiredPartitions(playerPositionP);
            UnloadDistantPartitions(playerPositionP);
        }

        private void ValidateActivePartitions()
        {
            Parallel.ForEach(_activePartitions, partitionKey =>
            {
                if (!_partitionData.TryGetValue(partitionKey, out int[] partition))
                    return;

                bool isDirty = false;
                for (int x = 0; x < PartitionSize; x++)
                {
                    for (int y = 0; y < PartitionSize; y++)
                    {
                        Point2 worldCoord = new Point2(partitionKey.X + x, partitionKey.Y + y);
                        int storedValue = partition[x + y * PartitionSize];
                        int realValue = GetTerrainValue(worldCoord);

                        if (storedValue != realValue)
                        {
                            partition[x + y * PartitionSize] = realValue;
                            MarkCoordinateDirty(worldCoord);
                            isDirty = true;
                        }
                    }
                }

                if (isDirty)
                {
                    _partitionData[partitionKey] = partition;
                }
            });
        }

        public int GetCellValue(Point2 coordinate)
        {
            Point2 partitionKey = CalculatePartitionKey(coordinate);
            if (_partitionData.TryGetValue(partitionKey, out int[] partition))
            {
                int xInPartition = coordinate.X - partitionKey.X;
                int yInPartition = coordinate.Y - partitionKey.Y;
                return partition[xInPartition + yInPartition * PartitionSize];
            }
            return GetTerrainValue(coordinate);
        }

        public IEnumerable<Point2> GetDirtyCoordinates() => _dirtyCoordinates.ToArray();

        private void LoadRequiredPartitions(Point2 center)
        {
            int radiusInPartitions = (int)Math.Ceiling(MapRadius / PartitionSize);
            var newActivePartitions = new HashSet<Point2>();

            Parallel.For(-radiusInPartitions, radiusInPartitions, x =>
            {
                for (int y = -radiusInPartitions; y <= radiusInPartitions; y++)
                {
                    Point2 partitionKey = new Point2(
                        (center.X / PartitionSize + x) * PartitionSize,
                        (center.Y / PartitionSize + y) * PartitionSize);

                    lock (newActivePartitions)
                    {
                        newActivePartitions.Add(partitionKey);
                    }

                    if (!_partitionData.ContainsKey(partitionKey))
                    {
                        LoadPartition(partitionKey);
                    }
                }
            });

            lock (_activePartitions)
            {
                _activePartitions.Clear();
                foreach (var key in newActivePartitions)
                    _activePartitions.Add(key);
            }
        }

        private void LoadPartition(Point2 partitionKey)
        {
            var partition = new int[PartitionSize * PartitionSize];
            Parallel.For(0, PartitionSize, x =>
            {
                for (int y = 0; y < PartitionSize; y++)
                {
                    Point2 worldCoord = new Point2(partitionKey.X + x, partitionKey.Y + y);
                    partition[x + y * PartitionSize] = GetTerrainValue(worldCoord);
                }
            });
            _partitionData[partitionKey] = partition;
        }

        private void UnloadDistantPartitions(Point2 center)
        {
            var toUnload = _partitionData.Keys
                .Where(key => !_activePartitions.Contains(key))
                .ToList();

            foreach (var coord in _dirtyCoordinates
                .Where(c => toUnload.Any(p =>
                    c.X >= p.X && c.X < p.X + PartitionSize &&
                    c.Y >= p.Y && c.Y < p.Y + PartitionSize)))
            {
                _dirtyCoordinates.TryDequeue(out _);
            }

            foreach (var key in toUnload)
            {
                _partitionData.TryRemove(key, out _);
            }
        }

        private void MarkCoordinateDirty(Point2 coord)
        {
            if (!_dirtyCoordinates.Contains(coord))
                _dirtyCoordinates.Enqueue(coord);
        }

        private Point2 CalculatePartitionKey(Point2 coordinate) =>
            new Point2(
                (coordinate.X / PartitionSize) * PartitionSize,
                (coordinate.Y / PartitionSize) * PartitionSize);

        private int GetTerrainValue(Point2 coord) =>
            _terrain.Terrain.GetCellContents(
                coord.X,
                _terrain.Terrain.GetTopHeight(coord.X, coord.Y),
                coord.Y);

        public void Dispose()
        {
            _partitionData.Clear();
            _dirtyCoordinates = null;
            _activePartitions.Clear();
        }
    }
}
