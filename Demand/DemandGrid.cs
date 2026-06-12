using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace AutoPublicTransit
{
    public class DemandGrid
    {
        private readonly float _cellSize;
        private readonly Dictionary<long, CellData> _cells = new Dictionary<long, CellData>();

        private struct CellData
        {
            public int Demand;
            public Vector3 SumPos;
            public int Count;
            public Vector3 StopPosition;
            public ushort StopSegmentId;
            public bool HasStopPosition;
            public int Purpose;
            public int PurposeMask;
            public int StopPurposePriority;
            public int StopWeight;
        }

        public DemandGrid(float cellSize)
        {
            _cellSize = cellSize;
        }

        private long Key(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        public void AddSample(Vector3 buildingPos, Vector3 stopPos, int weight)
        {
            AddSample(buildingPos, stopPos, 0, weight, DemandNodePurpose.Normal);
        }

        public void AddSample(Vector3 buildingPos, Vector3 stopPos, int weight, int purpose)
        {
            AddSample(buildingPos, stopPos, 0, weight, purpose);
        }

        public void AddSample(Vector3 buildingPos, CachedStopMatch stopMatch, int weight)
        {
            AddSample(buildingPos, stopMatch.StopPosition, stopMatch.SegmentId, weight, DemandNodePurpose.Normal);
        }

        public void AddSample(Vector3 buildingPos, CachedStopMatch stopMatch, int weight, int purpose)
        {
            AddSample(buildingPos, stopMatch.StopPosition, stopMatch.SegmentId, weight, purpose);
        }

        private void AddSample(Vector3 buildingPos, Vector3 stopPos, ushort stopSegmentId, int weight, int purpose)
        {
            int cellX = Mathf.FloorToInt(stopPos.x / _cellSize);
            int cellZ = Mathf.FloorToInt(stopPos.z / _cellSize);
            long key = Key(cellX, cellZ);

            CellData data;
            if (!_cells.TryGetValue(key, out data))
            {
                data = new CellData();
            }

            data.Demand += weight;
            data.SumPos += buildingPos;
            data.Count += 1;

            int purposePriority = GetStopPurposePriority(purpose);
            if (!data.HasStopPosition
                || purposePriority > data.StopPurposePriority
                || (purposePriority == data.StopPurposePriority && weight > data.StopWeight))
            {
                data.StopPosition = stopPos;
                data.StopSegmentId = stopSegmentId;
                data.HasStopPosition = true;
                data.StopPurposePriority = purposePriority;
                data.StopWeight = weight;
            }

            if (purpose > data.Purpose)
                data.Purpose = purpose;

            data.PurposeMask |= DemandNodePurpose.ToMask(purpose);
            _cells[key] = data;
        }

        private int GetStopPurposePriority(int purpose)
        {
            if (purpose == DemandNodePurpose.TransitHub)
                return 3;

            if (purpose == DemandNodePurpose.TouristAnchor)
                return 2;

            return 0;
        }

        public List<DemandNode> ToNodes(int demandThreshold)
        {
            var list = new List<DemandNode>();

            foreach (var kvp in _cells)
            {
                long key = kvp.Key;
                CellData data = kvp.Value;

                if (data.Demand < demandThreshold || data.Count == 0 || !data.HasStopPosition)
                    continue;

                int cellX = (int)(key >> 32);
                int cellZ = (int)(key & 0xffffffff);

                Vector3 centroid = data.SumPos / data.Count;

                list.Add(new DemandNode
                {
                    Centroid = centroid,
                    StopPosition = data.StopPosition,
                    StopSegmentId = data.StopSegmentId,
                    Demand = data.Demand,
                    CellX = cellX,
                    CellZ = cellZ,
                    Purpose = data.Purpose,
                    PurposeMask = data.PurposeMask
                });
            }

            return list;
        }
    }
}
