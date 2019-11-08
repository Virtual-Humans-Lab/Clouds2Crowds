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
using System.IO;
using System.Runtime.InteropServices;
using System.Collections;
using System.Threading;


namespace BioCrowds
{
   

    [UpdateAfter(typeof(FluidParticleToCell))]
    [UpdateBefore(typeof(AgentMovementSystem))]
    public class FluidMovementOnAgent : JobComponentSystem
    {

        #region VARIABLES
        [Inject] FluidParticleToCell m_fluidParticleToCell;
        public NativeHashMap<int3, float3> CellMomenta;
        public NativeHashMap<int3, float3> ParticleSetMass;

        private static float thresholdDist = 0.01f;
        //1 g/cm3 = 1000 kg/m3
        //Calculate based on the original SplishSplash code, mass = volume * density
        //Where density = 1000kg/m^3 and volume = 0.8 * particleDiameter^3
        private static float particleMass = 0.0001f;//kg
        private static float agentMass = 65f;
        private static float timeStep = 1f / Settings.experiment.FramesPerSecond;
        private float particleRadius = 0.025f;

        //0 --> Total inelastic collision
        //1 --> Elastic Collision
        private static float RestitutionCoef = 0f;




        public struct AgentGroup
        {
            [ReadOnly] public ComponentDataArray<Position> AgentPos;
            [ReadOnly] public ComponentDataArray<AgentData> AgentData;
            public ComponentDataArray<AgentStep> AgentStep;
            [ReadOnly] public readonly int Length;
        }
        [Inject] AgentGroup agentGroup;


        public struct CellGroup
        {
            [ReadOnly] public ComponentDataArray<CellName> CellName;
            [ReadOnly] public SubtractiveComponent<AgentData> Agent;
            [ReadOnly] public SubtractiveComponent<MarkerData> Marker;

            [ReadOnly] public readonly int Length;
        }
        [Inject] public CellGroup cellGroup;
        #endregion

        struct CalculateFluidMomenta : IJobParallelFor
        {
            [WriteOnly] public NativeHashMap<int3, float3>.Concurrent CellMomenta; 
            [WriteOnly] public NativeHashMap<int3, float3>.Concurrent ParticleSetMass; 
            [ReadOnly] public ComponentDataArray<CellName> CellName;
            [ReadOnly] public int frameSize;
            [ReadOnly] public NativeMultiHashMap<int3, int> CellToParticles;
            [ReadOnly] public NativeList<float3> FluidPos;
            [ReadOnly] public NativeList<float3> FluidVel;

            public void Execute(int index)
            {
                int3 key = CellName[index].Value;

                NativeMultiHashMapIterator<int3> it;
                int particleID;
                float3 M_r = float3.zero;

                int numPart = 0;

                bool keepgoing = CellToParticles.TryGetFirstValue(key, out particleID, out it);

                if (!keepgoing) return;

                //HACK: Assuming that the set of simulations has a lower height than the agent's
                //HACK: Aproximating Dv
                //TODO: Read timeStep from simulation header
                //if (particleID + frameSize >= FluidPos.Length) return;
                float3 vel = FluidVel[particleID] / timeStep;
                //float3 vel = (FluidPos[particleID + frameSize] - FluidPos[particleID]) / timeStep;

                float3 P = vel * particleMass;
                M_r += P;
                numPart++;

                while(CellToParticles.TryGetNextValue(out particleID, ref it)){
                    vel = FluidVel[particleID] / (timeStep);

                    P = vel * particleMass;
                    M_r += P;
                    numPart++;
                }
                ParticleSetMass.TryAdd(key, numPart*particleMass);
                CellMomenta.TryAdd(key, M_r);
              
            }
        }

        struct ApplyFluidMomentaOnAgents : IJobParallelFor
        {

            [ReadOnly] public NativeHashMap<int3, float3> CellMomenta;
            [ReadOnly] public NativeHashMap<int3, float3> ParticleSetMass;

            [ReadOnly] public ComponentDataArray<Position> AgentPos;
            public ComponentDataArray<AgentStep> AgentStep;

            [ReadOnly] public float RestitutionCoef;

            public void Execute(int index)
            {
                int3 cell = new int3((int)math.floor(AgentPos[index].Value.x / 2.0f) * 2 + 1, 0,
                                   (int)math.floor(AgentPos[index].Value.z / 2.0f) * 2 + 1);

                

                bool keepgoing = CellMomenta.TryGetValue(cell, out float3 particleVel);
                if (!keepgoing) return;

                keepgoing = ParticleSetMass.TryGetValue(cell, out float3 particleSetMass);
                if (!keepgoing) return;

                float3 OldAgentVel = AgentStep[index].delta;
                
                //TOTAL INELASTIC COLLISION
                OldAgentVel = (OldAgentVel * agentMass + particleVel) / (agentMass + particleSetMass);

                //HACK: For now, while we dont have ragdolls or the buoyancy(upthrust) force, not making use of the y coordinate 
                OldAgentVel.y = 0f;

                AgentStep[index] = new AgentStep { delta = OldAgentVel };



            }
        }

        #region ON...
        protected override void OnStartRunning()
        {

            CellMomenta = new NativeHashMap<int3, float3>((Settings.experiment.TerrainX * Settings.experiment.TerrainZ) / 4, Allocator.Persistent);
            ParticleSetMass = new NativeHashMap<int3, float3>((Settings.experiment.TerrainX * Settings.experiment.TerrainZ) / 4, Allocator.Persistent);

            //1 g/cm3 = 1000 kg/m3
            //Calculate based on the original SplishSplash code, mass = volume * density
            //Where density = 1000kg/m^3 and volume = 0.8 * particleDiameter^3
            float particleDiameter = 2 * particleRadius * 10f;
            float volume = 0.8f * math.pow(particleDiameter, 3);
            float density = 1000f;
            //particleMass = volume * density;
            particleMass = 0.001f;
            Debug.Log("Particle Mass: " + particleMass);

        }

        protected override void OnDestroyManager()
        {
            CellMomenta.Dispose();
            ParticleSetMass.Dispose();
        }
        #endregion

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {

            WriteData();

            CellMomenta.Clear();
            ParticleSetMass.Clear();

            CalculateFluidMomenta momentaJob = new CalculateFluidMomenta
            {
                CellMomenta = CellMomenta.ToConcurrent(),
                CellName = cellGroup.CellName,
                CellToParticles = m_fluidParticleToCell.CellToParticles,
                FluidPos = m_fluidParticleToCell.FluidPos,
                FluidVel = m_fluidParticleToCell.FluidVel,
                frameSize = m_fluidParticleToCell.frameSize,
                ParticleSetMass = ParticleSetMass.ToConcurrent()

            };

            var momentaJobHandle = momentaJob.Schedule(cellGroup.Length, Settings.BatchSize, inputDeps);

            momentaJobHandle.Complete();

            
            //DrawMomenta();

            ApplyFluidMomentaOnAgents applyMomenta = new ApplyFluidMomentaOnAgents
            {
                AgentPos = agentGroup.AgentPos,
                AgentStep = agentGroup.AgentStep,
                CellMomenta = CellMomenta,
                RestitutionCoef = RestitutionCoef,
                ParticleSetMass = ParticleSetMass

            };

            var applyMomentaHandle = applyMomenta.Schedule(agentGroup.Length, Settings.BatchSize, momentaJobHandle);

            applyMomentaHandle.Complete();



            m_fluidParticleToCell.FluidVel.Clear();

            return applyMomentaHandle;
        }

        private void WriteData()
        {
            string data = ""; 
            for (int i = 0; i < cellGroup.Length; i++)
            {
                int3 cell = cellGroup.CellName[i].Value;
                float3 particleVel;
                CellMomenta.TryGetValue(cell, out particleVel);
                data += i + ";" + ((Vector3)particleVel).magnitude + "\n";

            }

            System.IO.File.AppendAllText(AcessDLL.dataPath, data);
        }

        private void DrawMomenta()
        {
            for (int i = 0; i < cellGroup.Length; i++)
            {
                int3 cell = cellGroup.CellName[i].Value;
                float3 particleVel;
                CellMomenta.TryGetValue(cell, out particleVel);
                Debug.DrawLine(new float3(cell), new float3(cell) + particleVel,Color.red);

            }
        }


    }

    [UpdateAfter(typeof(Settings))]
    [UpdateAfter(typeof(AgentMovementVectors))]
    [UpdateBefore(typeof(AgentMovementSystem))]
    public class FluidParticleToCell : JobComponentSystem
    {
        #region VARIABLES
        public NativeList<float3> FluidPos;
        public NativeList<float3> FluidVel;
        public NativeMultiHashMap<int3, int> CellToParticles;

        public int frameSize = 100000;//particles
        public int bufferSize;// number of particles times the number of floats of data of each particle, for now 3 for 3 floats

        public int frame = 0;
        private int last = 0;
        private bool first = true;
        private static float thresholdHeigth = 1.7f;
        private const string memMapName = "unityMemMap";
        private const string memMapNameVel = "unityMemMapVel";
        //[timeSPH, timeBC, frameSize]
        private const string memMapControl = "unityMemMapControl";

        public float3 scale = new float3(10f, 10f, 10f);

        public int NLerp = 1;

        public struct AgentGroup
        {
            [ReadOnly] public ComponentDataArray<Position> AgentPos;
            [ReadOnly] public ComponentDataArray<AgentData> AgentData;
            [ReadOnly] public readonly int Length;
        }
        [Inject] AgentGroup agentGroup;

        public struct CellGroup
        {
            [ReadOnly] public ComponentDataArray<CellName> CellName;
            [ReadOnly] public SubtractiveComponent<AgentData> Agent;
            [ReadOnly] public SubtractiveComponent<MarkerData> Marker;

            [ReadOnly] public readonly int Length;
        }
        [Inject] public CellGroup cellGroup;
        #endregion

        struct FillCellFluidParticles : IJobParallelFor
        {
            [WriteOnly] public NativeMultiHashMap<int3, int>.Concurrent CellToParticles;
            [ReadOnly] public NativeList<float3> FluidPos;



            public void Execute(int index)
            {

                float3 ppos = FluidPos[index];
                //float3 ppos = FluidPos[index + (frameSize - 1) * frame];
                if (ppos.y > thresholdHeigth || ppos.x > Settings.experiment.TerrainX || ppos.z > Settings.experiment.TerrainZ) return;

                int3 cell = new int3((int)math.floor(FluidPos[index].x / 2.0f) * 2 + 1, 0,
                                     (int)math.floor(FluidPos[index].z / 2.0f) * 2 + 1);
                

                CellToParticles.Add(cell, index);


            }
        }



        #region ON...
        protected override void OnStartRunning()
        {
            if (!Settings.experiment.FluidSim)
            {
                Debug.Log(Settings.experiment.FluidSim);
                this.Enabled = false;
                World.Active.GetExistingManager<FluidMovementOnAgent>().Enabled = false;
                return;
            }


            CellToParticles = new NativeMultiHashMap<int3, int>(frameSize * (Settings.experiment.TerrainX * Settings.experiment.TerrainZ)/4, Allocator.Persistent);
            FluidPos = new NativeList<float3>(frameSize * NLerp, Allocator.Persistent);
            FluidVel = new NativeList<float3>(frameSize * NLerp, Allocator.Persistent);
            Debug.Log(frame +  " Fluid Pos Size: " + FluidPos.Length + " " +  FluidPos.Capacity);
        }

        protected override void OnDestroyManager()
        {
            FluidPos.Dispose();
            FluidVel.Dispose();
            CellToParticles.Dispose();
        }


        protected override void OnCreateManager()
        {




            OpenMemoryMap(memMapControl, 3);

            //frameSize * 3 as there are 3 floats for every particle
            bufferSize = frameSize * 3;
            //TODO: Get frameSize from FluidSimulator


            OpenMemoryMap(memMapName, bufferSize);

            OpenMemoryMap(memMapNameVel, bufferSize);


            Debug.Log("Fluid Simulation Initialized");
        }



        
        private void BinParser(string file)
        {

            var folder = Application.dataPath;
            var simFile = folder + "/" + Settings.experiment.FluidSimPath;
            if (!System.IO.File.Exists(simFile))
            {
                Debug.Log("Fluid Simulation File Not Found: " + simFile);
                this.Enabled = false;
                World.Active.GetExistingManager<FluidMovementOnAgent>().Enabled = false;
                //TODO: disable every fluid sim system
                return;
            }
            BinaryReader binread = new BinaryReader(File.Open(file, FileMode.Open));
            frameSize = binread.ReadInt32();
            Debug.Log("N Particles: " + frameSize);
            Debug.Log("Lenght of File: " + (binread.BaseStream.Length - 3 * sizeof(float)));
            while (binread.BaseStream.Position != (binread.BaseStream.Length - 3 * sizeof(float)))
            {

                float x = binread.ReadSingle();
                float y = binread.ReadSingle();
                float z = binread.ReadSingle();
                //TODO: Parametrize the translation and scale
                FluidPos.Add(new float3((x*10) + 35, y*10 + 20, z*20 + 25));
            }     
        }
        #endregion

        private void FillFrameParticlePositions()
        {

            float3 translate = new float3(50f,0f,25f);
            

            float[] floatStream = new float[bufferSize];
            AcessDLL.ReadMemoryShare(memMapName, floatStream);
            for (int i = 0; i < floatStream.Length - 2; i += 3)
            {
                float x = floatStream[i];
                float y = floatStream[i + 1];
                float z = -floatStream[i + 2];
                if (x == 0 && y == 0 & z == 0) continue;
                //TODO: Parametrize the translation and scale]
                float3 pos = new float3(x, y, z) * scale + translate;
                FluidPos.Add(pos);

                for(int l= 1; l < NLerp; l++)
                {
                    float xl = UnityEngine.Random.Range(0f, 0.5f);
                    float yl = UnityEngine.Random.Range(0f, 0.5f);
                    float zl = UnityEngine.Random.Range(0f, 0.5f);
                    float3 offset = new float3(xl,yl,zl);
                    FluidPos.Add(pos + offset);
                }
                

               
            }

            float[] floatStreamVel = new float[bufferSize];
            AcessDLL.ReadMemoryShare(memMapNameVel, floatStreamVel);
            for (int i = 0; i < floatStreamVel.Length - 2; i += 3)
            {
                float x = floatStreamVel[i];
                float y = floatStreamVel[i + 1];
                float z = -floatStreamVel[i + 2];
                if (x == 0 && y == 0 & z == 0) continue;

                //TODO: Parametrize the translation and scale]
                float3 vel = new float3(x, y, z) * scale;
                FluidVel.Add(vel);

                for (int l = 1; l < NLerp; l++)
                {
                    float xl = UnityEngine.Random.Range(0f, 0.5f);
                    float yl = UnityEngine.Random.Range(0f, 0.5f);
                    float zl = UnityEngine.Random.Range(0f, 0.5f);
                    float3 offset = new float3(xl, yl, zl);
                    FluidVel.Add(vel + offset);
                }

            }


        }


        private bool WaitForFluidSim()
        {

            float[] ControlData = new float[3];
            AcessDLL.ReadMemoryShare(memMapControl, ControlData);

            ControlData[1] = frame/Settings.experiment.FramesPerSecond;
            AcessDLL.WriteMemoryShare(memMapControl, ControlData);
            //Debug.Log(ControlData[0]);

            if (ControlData[1] > ControlData[0])
            {
                Thread.Sleep(1);
                return true;
            }

            //Debug.Log(frame + " " + ControlData[0] + " " + ControlData[1]);

            return false;
        }


      


        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            //HACK: Write better sync between fluid sim and biocrowds
            while (WaitForFluidSim()) { }
            
            FluidPos.Clear();
            FillFrameParticlePositions();
            Debug.Log(frame + " Fluid Pos Size: " + FluidPos.Length + " " + FluidPos.Capacity);


            for (int i = 0; i < cellGroup.Length; i++)
            {
                int3 key = cellGroup.CellName[i].Value;

                CellToParticles.Remove(key);
            }


            var FillCellMomentaJob = new FillCellFluidParticles
            {
                CellToParticles = CellToParticles.ToConcurrent(),
                FluidPos = FluidPos
            };

            var FillCellJobHandle = FillCellMomentaJob.Schedule(FluidPos.Length, Settings.BatchSize, inputDeps);

            FillCellJobHandle.Complete();


            if (frame % 300 == 0)
            {
                CellToParticles.Clear();
            }



            string s = frame.ToString();
            if (s.Length == 1) ScreenCapture.CaptureScreenshot("D:\\Clouds2Crowds\\Clouds2Crowds\\NewAndImprovedBioCrowds\\Prints\\frame000" + frame + ".png");
            if (s.Length == 2) ScreenCapture.CaptureScreenshot("D:\\Clouds2Crowds\\Clouds2Crowds\\NewAndImprovedBioCrowds\\Prints\\frame00" + frame + ".png");
            if (s.Length == 3) ScreenCapture.CaptureScreenshot("D:\\Clouds2Crowds\\Clouds2Crowds\\NewAndImprovedBioCrowds\\Prints\\frame0" + frame + ".png");
            if (s.Length == 4) ScreenCapture.CaptureScreenshot("D:\\Clouds2Crowds\\Clouds2Crowds\\NewAndImprovedBioCrowds\\Prints\\frame" + frame + ".png");


            DebugFluid();
            frame++;

            //Debug.Log(frame);
            return inputDeps;
        }

           
        private void DebugFluid()
        {
            for (int i = 0; (i + NLerp) < FluidPos.Length; i+=NLerp)
            {
                float magnitude = ((Vector3)FluidVel[i]).magnitude/50f;
                Color c = Color.LerpUnclamped(Color.yellow, Color.red, magnitude);

                Debug.DrawLine(FluidPos[i], FluidPos[i] + FluidVel[i]/100f, c);
            }

        }

        private void DebugFluidParser()
        {
            int i = last;
            for (; i < frameSize + last && i < FluidPos.Length; i++)
            {
                Debug.DrawLine(FluidPos[i], FluidPos[i] + new float3(0f, 0.01f, 0f), Color.blue);
            }
            last = i;

            if (i >= FluidPos.Length - 1)
            {
                last = 0;
                i = last;
            }
        }


        private void OpenMemoryMap(string mapName, int size)
        {
            int map = AcessDLL.OpenMemoryShare(mapName, size);
            switch (map)
            {
                case 0:
                    Debug.Log("Memory Map " + mapName + " Created");
                    break;
                case -1:
                    Debug.Log("Memory Map " + mapName + " Array too large");
                    this.Enabled = false;
                    World.Active.GetExistingManager<FluidMovementOnAgent>().Enabled = false;
                    //TODO: disable every fluid sim system
                    return;

                case -2:
                    Debug.Log("Memory Map " + mapName + " could not create file mapping object");
                    this.Enabled = false;
                    World.Active.GetExistingManager<FluidMovementOnAgent>().Enabled = false;
                    //TODO: disable every fluid sim system
                    return;
                case -3:
                    Debug.Log("Memory Map " + mapName + " could not create map view of the file");
                    this.Enabled = false;
                    World.Active.GetExistingManager<FluidMovementOnAgent>().Enabled = false;
                    //TODO: disable every fluid sim system
                    return;
                default:
                    Debug.Log("A Memory Map " + mapName + " Already Exists");
                    break;
            }
        }

    }

   
    public static class AcessDLL
    {

        public static string dataPath = "out.txt";


        //private const string UNITYCOM = "..\\UnityCom\\Release\\UnityCom.dll";
        private const string UNITYCOM = "..\\UnityCom\\x64\\Release\\UnityCom";
        [DllImport(UNITYCOM, EntryPoint = "Add")]
        public static extern float Add(float a, float b);

        [DllImport(UNITYCOM, EntryPoint = "IsOpen")]
        public static extern bool IsOpen(char[] memMapName);

        [DllImport(UNITYCOM, EntryPoint = "OpenMemoryShare")]
        public static extern int OpenMemoryShare(string memMapName, long bufSize);

        [DllImport(UNITYCOM, EntryPoint = "WriteMemoryShare")]
        public static extern bool WriteMemoryShare(string memMapName, float[] val, long offset = 0, long length = -1);

        [DllImport(UNITYCOM, EntryPoint = "ReadMemoryShare")]
        public static extern bool ReadMemoryShare(string memMapName, float[] val, long offset = 0, long length = -1);

        [DllImport(UNITYCOM, EntryPoint = "GetSize")]
        public static extern long GetSize(string memMapName);

        [DllImport(UNITYCOM, EntryPoint = "CloseAllMemoryShare")]
        public static extern bool CloseAllMemoryShare();

        [DllImport(UNITYCOM, EntryPoint = "CloseMemoryShare")]
        public static extern bool CloseMemoryShare(string memMapName);
    }




}

