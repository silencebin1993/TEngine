using System.Collections.Generic;
using Nebukam.Common;
using Nebukam.ORCA;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// First Playable 用的 Nebukam ORCA 薄封装。XZ 平面、同帧 Run（Schedule+Complete）。
    /// AI 只写 prefVelocity；位移读 agent.pos（由模拟积分）。
    /// </summary>
    public sealed class FPOrcaSim
    {
        public const float DefaultNeighborDist = 8f;
        public const int DefaultMaxNeighbors = 10;
        public const float DefaultTimeHorizon = 1.2f;
        public const float DefaultTimeHorizonObst = 0.8f;

        private AgentGroup<Agent> _agents;
        private ObstacleGroup _obstacles;
        private ORCA _simulation;
        private bool _alive;

        public bool Alive => _alive;

        public void Begin(float arenaHalf, IList<Vector3> hazardCenters = null, float hazardRadius = 0f)
        {
            Dispose();

            _agents = new AgentGroup<Agent>();
            _obstacles = new ObstacleGroup();
            _simulation = new ORCA
            {
                plane = AxisPair.XZ,
                agents = _agents,
                staticObstacles = _obstacles,
            };

            AddArenaWalls(arenaHalf);
            if (hazardCenters != null && hazardRadius > 0.05f)
            {
                for (int i = 0; i < hazardCenters.Count; i++)
                {
                    AddCircleObstacle(hazardCenters[i], hazardRadius, 8);
                }
            }

            _alive = true;
        }

        public Agent AddAgent(Vector3 pos, float radius, float maxSpeed,
            float neighborDist = DefaultNeighborDist, int maxNeighbors = DefaultMaxNeighbors)
        {
            EnsureAlive();
            Agent a = _agents.Add(ToXZ(pos));
            a.radius = radius;
            a.radiusObst = radius * 1.05f;
            a.maxSpeed = Mathf.Max(0.1f, maxSpeed);
            a.height = 1f;
            a.neighborDist = neighborDist;
            a.maxNeighbors = maxNeighbors;
            a.timeHorizon = DefaultTimeHorizon;
            a.timeHorizonObst = DefaultTimeHorizonObst;
            a.prefVelocity = float3(0f);
            a.velocity = float3(0f);
            a.navigationEnabled = true;
            a.collisionEnabled = true;
            return a;
        }

        public void RemoveAgent(Agent agent)
        {
            if (!_alive || agent == null || _agents == null)
            {
                return;
            }
            int index = _agents[agent];
            if (index < 0)
            {
                return;
            }
            // Nebukam AgentGroup.Remove(v, release) 未把 release 传给 RemoveAt，这里显式释放
            _agents.RemoveAt(index, true);
        }

        public void SetPrefVelocity(Agent agent, Vector3 velocity)
        {
            if (agent == null)
            {
                return;
            }
            agent.prefVelocity = new float3(velocity.x, 0f, velocity.z);
        }

        public void SetPos(Agent agent, Vector3 pos)
        {
            if (agent == null)
            {
                return;
            }
            agent.pos = ToXZ(pos);
        }

        public Vector3 GetPos(Agent agent)
        {
            if (agent == null)
            {
                return Vector3.zero;
            }
            float3 p = agent.pos;
            return new Vector3(p.x, p.y, p.z);
        }

        public Vector3 GetVelocity(Agent agent)
        {
            if (agent == null)
            {
                return Vector3.zero;
            }
            float3 v = agent.velocity;
            return new Vector3(v.x, 0f, v.z);
        }

        /// <summary>同帧调度并完成避障 Job，结果立刻写回各 Agent.pos / velocity。</summary>
        public void Run(float dt)
        {
            if (!_alive || _simulation == null)
            {
                return;
            }
            float step = Mathf.Max(0.0001f, dt);
            _simulation.Run(step);
        }

        public void Dispose()
        {
            if (_simulation != null)
            {
                _simulation.DisposeAll();
                _simulation = null;
            }
            if (_agents != null)
            {
                _agents.Clear(true);
                _agents = null;
            }
            if (_obstacles != null)
            {
                _obstacles.Clear(true);
                _obstacles = null;
            }
            _alive = false;
        }

        private void EnsureAlive()
        {
            if (!_alive)
            {
                throw new System.InvalidOperationException("FPOrcaSim is not started. Call Begin() first.");
            }
        }

        private void AddArenaWalls(float half)
        {
            // 略放大，给 agent radius 留边；顶点逆时针（俯视），与官方 Sample 一致。
            float h = half + 0.5f;
            float3[] square =
            {
                float3(-h, 0f, -h),
                float3(-h, 0f, h),
                float3(h, 0f, h),
                float3(h, 0f, -h),
            };
            _obstacles.Add(square, false, 12f);
        }

        private void AddCircleObstacle(Vector3 center, float radius, int segments)
        {
            float3[] verts = new float3[segments];
            for (int i = 0; i < segments; i++)
            {
                float ang = (Mathf.PI * 2f) * i / segments;
                verts[i] = float3(center.x + Mathf.Cos(ang) * radius, 0f, center.z + Mathf.Sin(ang) * radius);
            }
            _obstacles.Add(verts, false, radius);
        }

        private static float3 ToXZ(Vector3 pos)
        {
            return float3(pos.x, 0f, pos.z);
        }
    }
}
