﻿
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Jobs;
using UnityEngine.Experimental.PlayerLoop;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEditor;

namespace BioCrowds
{

    [System.Serializable]
    public struct AgentData : IComponentData
    {
        public int ID;
        public float Radius;
        public float MaxSpeed;
    }

    [System.Serializable]
    public struct AgentGoal : IComponentData
    {
        public float3 EndGoal;
        public float3 SubGoal;
    }

    [System.Serializable]
    public struct AgentStep : IComponentData
    {
        public float3 delta;
    }





    [UpdateAfter(typeof(EarlyUpdate))]
    public class CellTagSystem : JobComponentSystem
    {
        public NativeHashMap<int, float3> AgentIDToPos;
        public NativeMultiHashMap<int3, int> CellToMarkedAgents;
        public QuadTree qt;

        public struct CellGroup
        {
            [ReadOnly] public ComponentDataArray<CellName> CellName;
            [ReadOnly] public SubtractiveComponent<AgentData> Agent;
            [ReadOnly] public SubtractiveComponent<MarkerData> Marker;

            [ReadOnly] public readonly int Length;
        }
        [Inject] public CellGroup cellGroup;

        public struct AgentGroup
        {
            [ReadOnly] public ComponentDataArray<Position> AgentPos;
            [ReadOnly] public ComponentDataArray<AgentData> AgentData;
            [ReadOnly] public readonly int Length;
        }
        [Inject] public AgentGroup agentGroup;

        struct MapCellToAgents : IJobParallelFor
        {
            [WriteOnly] public NativeMultiHashMap<int3, int>.Concurrent CellToAgent;
            [WriteOnly] public NativeHashMap<int, float3>.Concurrent AgentIDToPos;

            [ReadOnly] public ComponentDataArray<Position> Position;
            [ReadOnly] public ComponentDataArray<AgentData> AgentData;

            public void Execute(int index)
            {
                //Get the 8 neighbors cells to the agent's cell + it's cell
                int agent = AgentData[index].ID;
                int3 cell = new int3((int)math.floor(Position[index].Value.x / 2.0f) * 2 + 1, 0,
                                     (int)math.floor(Position[index].Value.z / 2.0f) * 2 + 1);

                CellToAgent.Add(cell, agent);
                int startX = cell.x - 2;
                int startZ = cell.z - 2;
                int endX = cell.x + 2;
                int endZ = cell.z + 2;

                float3 agentPos = Position[index].Value;
                AgentIDToPos.TryAdd(agent, agentPos);
                float distCell = math.distance((float3)cell, agentPos);

                for (int i = startX; i <= endX; i = i + 2)
                {
                    for (int j = startZ; j <= endZ; j = j + 2)
                    {
                        int3 key = new int3(i, 0, j);

                        CellToAgent.Add(key, agent);
                        
                    }
                }

            }
        }


        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            //int qtdAgts = Settings.agentQuantity;
            qt.Reset();

            if (AgentIDToPos.Capacity < agentGroup.Length)
            {
                AgentIDToPos.Dispose();
                AgentIDToPos = new NativeHashMap<int, float3>(agentGroup.Length * 2, Allocator.Persistent);
            }
            else
                AgentIDToPos.Clear();

            CellToMarkedAgents.Clear();

            MapCellToAgents mapCellToAgentsJob = new MapCellToAgents
            {
                CellToAgent = CellToMarkedAgents.ToConcurrent(),
                AgentData = agentGroup.AgentData,
                Position = agentGroup.AgentPos,
                AgentIDToPos = AgentIDToPos.ToConcurrent()
            };

            var mapCellToAgentsJobDep = mapCellToAgentsJob.Schedule(agentGroup.Length, SimulationConstants.instance.BatchSize, inputDeps);

            mapCellToAgentsJobDep.Complete();

            List<int3> addedCells = new List<int3>();

            for(int i = 0; i < cellGroup.Length; i++)
            {
                int3 key = cellGroup.CellName[i].Value;
                int item;
                NativeMultiHashMapIterator<int3> it;
                if(CellToMarkedAgents.TryGetFirstValue(key, out item, out it))
                {
                    //Debug.Log(key);
                    qt.Insert(key);
                }

            }


            //string s = "[ ";

            //for (int i = 0; i < agentGroup.AgentData.Length; i++)
            //{
            //    AgentIDToPos.TryGetValue(agentGroup.AgentData[i].ID, out float3 pos);
            //    s += "(" + agentGroup.AgentData[i].ID + ", " + pos + "), ";
            //}
            //Debug.Log(s);



            return mapCellToAgentsJobDep;
        }


        

        protected override void OnStartRunning()
        {
            Rectangle size = new Rectangle { x = 0, y = 0, h = Settings.experiment.TerrainZ, w = Settings.experiment.TerrainX };
            qt = new QuadTree(size, 0);
            ShowQuadTree.qt = qt;
            int qtdAgts = Settings.agentQuantity;
            //TODO Dynamize hash map size so there are less collisions
            CellToMarkedAgents = new NativeMultiHashMap<int3, int>(160000, Allocator.Persistent);
            //Debug.Log(CellToMarkedAgents.IsCreated);
            AgentIDToPos = new NativeHashMap<int, float3>(qtdAgts * 2, Allocator.Persistent);
        }

        protected override void OnStopRunning()
        {
            CellToMarkedAgents.Dispose();
            AgentIDToPos.Dispose();
        }
    }


    public class MarkerSystemGroup { }

    [UpdateAfter(typeof(MarkerSystemGroup))]
    public class MarkerSystemView : ComponentSystem
    {
        [Inject] MarkerSystemMk2 mk2;
        [Inject] MarkerSystem mS;
        [Inject] NormalLifeMarkerSystem nlmS;

        public NativeMultiHashMap<int, float3> AgentMarkers;


        protected override void OnUpdate()
        {
            if (mk2.Enabled) AgentMarkers = mk2.AgentMarkers;
            else if (nlmS.Enabled) AgentMarkers = nlmS.AgentMarkers;
            else if (mS.Enabled) AgentMarkers = mS.AgentMarkers;
        }
    }


    [UpdateAfter(typeof(MarkerSystemGroup)), UpdateAfter(typeof(MarkerSystemView))]
    public class MarkerWeightSystem : JobComponentSystem
    {

        public struct AgentGroup
        {
            [ReadOnly] public ComponentDataArray<Position> AgentPos;
            [ReadOnly] public ComponentDataArray<AgentData> AgentData;
            [ReadOnly] public ComponentDataArray<AgentGoal> AgentGoal;
            [ReadOnly] public readonly int Length;
        }
        [Inject] AgentGroup agentGroup;

        [Inject] MarkerSystemView MarkerSystem;

        public NativeHashMap<int, float> AgentTotalMarkerWeight;

        public struct ComputeTotalMarkerWeight : IJobParallelFor
        {
            [WriteOnly] public NativeHashMap<int, float>.Concurrent AgentTotalMarkerWeight;

            [ReadOnly] public ComponentDataArray<AgentData> AgentData;
            [ReadOnly] public ComponentDataArray<AgentGoal> AgentGoals;
            [ReadOnly] public ComponentDataArray<Position> AgentPos;
            [ReadOnly] public NativeMultiHashMap<int, float3> AgentMarkers;


            public void Execute(int index)
            {
                float3 currentMarkerPosition;
                NativeMultiHashMapIterator<int> it;

                float totalW = 0f;

                bool keepgoing = AgentMarkers.TryGetFirstValue(AgentData[index].ID, out currentMarkerPosition, out it);

                if (!keepgoing)
                    return;

                totalW += AgentCalculations.GetF(currentMarkerPosition, AgentPos[index].Value, (AgentGoals[index].SubGoal - AgentPos[index].Value));

                while (AgentMarkers.TryGetNextValue(out currentMarkerPosition, ref it))
                    totalW += AgentCalculations.GetF(currentMarkerPosition, AgentPos[index].Value, (AgentGoals[index].SubGoal - AgentPos[index].Value));

                AgentTotalMarkerWeight.TryAdd(AgentData[index].ID, totalW);

            }
        }


        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {

            if(AgentTotalMarkerWeight.Capacity < agentGroup.Length * 2)
            {
                AgentTotalMarkerWeight.Dispose();
                AgentTotalMarkerWeight = new NativeHashMap<int, float>(agentGroup.Length * 2, Allocator.Persistent);
            }
            else
                AgentTotalMarkerWeight.Clear();
            //HACK: Solve marker system not created
            if (!MarkerSystem.AgentMarkers.IsCreated) return inputDeps;

            ComputeTotalMarkerWeight computeJob = new ComputeTotalMarkerWeight()
            {
                AgentTotalMarkerWeight = AgentTotalMarkerWeight.ToConcurrent(),
                AgentData = agentGroup.AgentData,
                AgentGoals = agentGroup.AgentGoal,
                AgentPos = agentGroup.AgentPos,
                AgentMarkers = MarkerSystem.AgentMarkers
            };
            var computeJobHandle = computeJob.Schedule(agentGroup.Length, SimulationConstants.instance.BatchSize, inputDeps);
            computeJobHandle.Complete();
            return computeJobHandle;
        }

        protected override void OnCreateManager()
        {
            //AgentTotalMarkerWeight = new NativeHashMap<int, float>();
        }

        protected override void OnStartRunning()
        {
            UpdateInjectedComponentGroups();
            AgentTotalMarkerWeight = new NativeHashMap<int, float>(agentGroup.Length * 2, Allocator.Persistent);
        }

        protected override void OnStopRunning()
        {
            AgentTotalMarkerWeight.Dispose();
        }

    }


    public class MovementVectorsSystemGroup { }


    [UpdateInGroup(typeof(MovementVectorsSystemGroup)), UpdateAfter(typeof(MarkerWeightSystem))]
    public class AgentMovementVectors : JobComponentSystem
    {
        public struct AgentGroup
        {
            [ReadOnly] public ComponentDataArray<AgentData> AgentData;
            [ReadOnly] public ComponentDataArray<AgentGoal> AgentGoal;

            [ReadOnly] public ComponentDataArray<Position> Position;
            [WriteOnly] public ComponentDataArray<AgentStep> AgentStep;


            [ReadOnly] public readonly int Length;
        }


        [Inject] AgentGroup agentGroup;
        [Inject] MarkerSystemView markerSystem;

        [Inject] MarkerWeightSystem totalWeightSystem;

        public NativeHashMap<int, float3> AgentIDToStep;



        struct CalculateAgentMoveStep : IJobParallelFor
        {

            [ReadOnly] public ComponentDataArray<AgentData> AgentData;
            [ReadOnly] public ComponentDataArray<AgentGoal> AgentGoals;
            [ReadOnly] public ComponentDataArray<Position> AgentPos;


            [ReadOnly] public NativeMultiHashMap<int, float3> AgentMarkersMap;
            [ReadOnly] public NativeHashMap<int, float> AgentTotalW;
            [WriteOnly] public ComponentDataArray<AgentStep> AgentStep;
            [WriteOnly] public NativeHashMap<int, float3>.Concurrent Agent2StepMap;

            public void Execute(int index)
            {



                float3 currentMarkerPosition;
                NativeMultiHashMapIterator<int> it;

                float3 moveStep = float3.zero;
                float3 direction = float3.zero;
                float totalW;
                AgentTotalW.TryGetValue(AgentData[index].ID, out totalW);

                bool keepgoing = AgentMarkersMap.TryGetFirstValue(AgentData[index].ID, out currentMarkerPosition, out it);

                if (!keepgoing)
                    return;

                float F = AgentCalculations.GetF(currentMarkerPosition, AgentPos[index].Value, AgentGoals[index].SubGoal - AgentPos[index].Value);

                direction += AgentCalculations.PartialW(totalW, F) * AgentData[index].MaxSpeed * (currentMarkerPosition - AgentPos[index].Value);



                while (AgentMarkersMap.TryGetNextValue(out currentMarkerPosition, ref it))
                {
                    
                    F = AgentCalculations.GetF(currentMarkerPosition, AgentPos[index].Value, AgentGoals[index].SubGoal - AgentPos[index].Value);
                    
                    direction += AgentCalculations.PartialW(totalW, F) * AgentData[index].MaxSpeed * (currentMarkerPosition - AgentPos[index].Value);
                }


                float moduleM = math.length(direction);
                float s = (float)(moduleM * math.PI);

                if (s > AgentData[index].MaxSpeed)
                    s = AgentData[index].MaxSpeed;

                if (moduleM > 0.00001f)
                    moveStep = s * (math.normalize(direction));
                else
                    moveStep = float3.zero;

                AgentStep[index] = new AgentStep() { delta = moveStep };

                if(!Agent2StepMap.TryAdd(index, moveStep))
                {
                    Debug.Log("Crap");
                }




            }
        }

        private int frame = 0;

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {


            AgentIDToStep.Clear();

            var calculateMoveStepJob = new CalculateAgentMoveStep()
            {
                AgentData = agentGroup.AgentData,
                AgentGoals = agentGroup.AgentGoal,
                AgentPos = agentGroup.Position,
                AgentStep = agentGroup.AgentStep,
                AgentTotalW = totalWeightSystem.AgentTotalMarkerWeight,
                AgentMarkersMap = markerSystem.AgentMarkers,
                Agent2StepMap = AgentIDToStep.ToConcurrent()
            };
                
            var calculateMoveStepDeps = calculateMoveStepJob.Schedule(agentGroup.Length, SimulationConstants.instance.BatchSize, inputDeps);

            calculateMoveStepDeps.Complete();


            ////////////  LOGGER  ////////////

            
            FluidLog log = new FluidLog { frame = frame, agentPos = new float3[Settings.agentQuantity], agentVel = new float3[Settings.agentQuantity], currentSprings = new SpringSystem.Spring[Settings.agentQuantity * 2] };


            for (int i = 0; i < agentGroup.Length; i++)
            {
                log.agentPos[i] = agentGroup.Position[i].Value;
                log.agentVel[i] = agentGroup.AgentStep[i].delta;

            }

            FluidLogger.currentLog = log;

            if (!Settings.experiment.SpringSystem) FluidLogger.WriteFrame(log);

            frame++;

            return calculateMoveStepDeps;
            

            
        }

        protected override void OnStartRunning()
        {
            AgentIDToStep = new NativeHashMap<int, float3>(Settings.agentQuantity * 2, Allocator.Persistent);
        }

        protected override void OnStopRunning()
        {
            AgentIDToStep.Dispose();
        }
    }

    
    [UpdateAfter(typeof(AgentMovementVectors))]
    public class AgentMovementSystem : JobComponentSystem
    {
        //Moves based on marked cell list
        public struct MarkersGroup
        {
            [WriteOnly] public ComponentDataArray<Position> Position;
            [ReadOnly] public ComponentDataArray<AgentStep> AgentStep;
            public ComponentDataArray<AgentGoal> Goal;
            [ReadOnly] public readonly int Length;
        }
        [Inject] MarkersGroup markersGroup;

        struct MoveCloudsJob : IJobParallelFor
        {
            public ComponentDataArray<Position> Positions;
            [ReadOnly] public ComponentDataArray<AgentStep> Deltas;
            public ComponentDataArray<AgentGoal> Goal;


            public void Execute(int index)
            {
                float3 old = Positions[index].Value;
                float3 newPos = old + Deltas[index].delta;


                if (newPos.x > Settings.experiment.TerrainX) newPos.x = Settings.experiment.TerrainX;
                else if (newPos.x < 0f) newPos.x = 0f;

                if (newPos.z > Settings.experiment.TerrainZ) newPos.z = Settings.experiment.TerrainZ;
                else if (newPos.z < 0f) newPos.z = 0f;

                Positions[index] = new Position { Value = newPos };



                //DONOW:Remove encherto
                if (math.distance(old + Deltas[index].delta, Goal[index].SubGoal) <= 1f && Settings.experiment.WayPointOn)
                {
                    var w = AgentCalculations.RandomWayPoint();

                    Goal[index] = new AgentGoal
                    {
                        SubGoal = w

                    };
                }
            }
        }


        private float frame = 0f;

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {




            if (ControlVariables.instance.LockBioCrowds)
            {
                Debug.LogWarning("BioCrowds Locked!");
                return inputDeps;
            }

            MoveCloudsJob moveJob = new MoveCloudsJob()
            {
                Positions = markersGroup.Position,
                Deltas = markersGroup.AgentStep,
                Goal = markersGroup.Goal
            };

            var deps = moveJob.Schedule(markersGroup.Length, SimulationConstants.instance.BatchSize, inputDeps);

            deps.Complete();


            return deps;
        }

    }

    




    public static class AgentCalculations
    {
        //Current marker position, current cloud position and (goal position - cloud position) vector.
        public static float GetF(float3 markerPosition, float3 agentPosition, float3 agentGoalVector)
        {
            float Ymodule = math.length(markerPosition - agentPosition);

            float Xmodule = 1f;

            float dot = math.dot(markerPosition - agentPosition, math.normalize(agentGoalVector));

            if (Ymodule < 0.00001f)
                return 0.0f;

            return ((1.0f / (1.0f + Ymodule)) * (1.0f + ((dot) / (Xmodule * Ymodule))));
        }

        public static float PartialW(float totalW, float fValue)
        {
            return fValue / totalW;
        }

        public static float3 RandomWayPoint()
        {
            System.Random r = new System.Random(System.DateTime.UtcNow.Millisecond);
            int i = r.Next(0, Settings.experiment.WayPoints.Length);
            return Settings.experiment.WayPoints[i];
        }
    }

}