﻿using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace BioCrowds
{
    [Serializable]
    public class CrowdExperiment
    {
        [System.Serializable]
        public struct SpawnArea
        {
            public int qtd;
            public int3 max;
            public int3 min;
            public float3 goal;
            public float maxSpeed;
        }

        public struct ObstacleArea
        {
            public float3 start;
            public float3 end;
        }



        public float agentRadius = 1f;
        public float markerRadius = 0.1f;
        public float MarkerDensity = 0.65f;
        public int TerrainX = 100;
        public int TerrainZ = 50;
        public float FramesPerSecond = 32f;


        public bool showMarkers = false;
        public bool showCells = false;



        public SpawnArea[] SpawnAreas = { new SpawnArea{qtd = 50,
                                         goal = new float3{x = 100, y = 0, z = 25},
                                         max = new int3 {x = 15, y = 0, z = 40},
                                         min = new int3 {x = 0, y = 0, z = 10 },
                                         maxSpeed = 1.3f}
                                        };



        public bool NormalLife = false;
        public bool BioCloudsEnabled = false;

        public bool FluidSim = false;
        //TODO: Make path relative to project

        public bool SpringSystem = false;
        public int2[] SpringConnections = { new int2(1, 2), new int2(4, 3) };


        public bool WayPointOn = false;
        public float3[] WayPoints = new float3[]{
            new float3(25,0,25),
            new float3(45,0,25),
            new float3(25,0,45),
            new float3(15,0,25),
            new float3(25,0,15),
        };


    }


    public class Settings : MonoBehaviour
    {
        public static Settings instance;
        public List<Color> Colors = new List<Color>();
        public List<Mesh> Meshes = new List<Mesh>();
        public List<MeshInstanceRenderer> Renderers = new List<MeshInstanceRenderer>();

        public static int BatchSize = 1;
        //Real value is the sum of all groups instantiated in the bootstrap
        public static int agentQuantity = 0;
        public bool ScreenCap = true;
        public static CrowdExperiment experiment = new CrowdExperiment();

        public int treeHeight = 4;
        public static bool QuadTreeActive = true;

        [SerializeField]
        public List<ISettings> ModuleSettings;

        //HACK: Change get method
        // This looks ok, to be honest
        public FluidSettings getFluid() => ((FluidSettings)Settings.instance.ModuleSettings[0]);


        public void Awake()
        {

            foreach (Color c in Colors)
            {
                Material m = new Material(Shader.Find("Standard"))
                {
                    color = c,
                    enableInstancing = true
                };
                var renderer = new MeshInstanceRenderer()
                {
                    material = m,
                    mesh = Meshes[0]
                };
                Renderers.Add(renderer);
            }

            if (instance == null)
            {
                instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
                Destroy(gameObject);

            var folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);

            var bioCrowdsFolder = System.IO.Directory.CreateDirectory(folder + "\\VHLAB\\BioCrowds");


            string settingsFile = bioCrowdsFolder.FullName + "\\BaseExperiment.json";
            bool basisCase = System.IO.File.Exists(settingsFile);
            //Debug.Log(basisCase + " " + settingsFile);

            if (!basisCase)
                System.IO.File.WriteAllText(settingsFile, JsonUtility.ToJson(experiment, true));
            else
            {
                Debug.Log("Reading Experiment File");
                string file = System.IO.File.ReadAllText(settingsFile);
                experiment = JsonUtility.FromJson<CrowdExperiment>(file);
            }



        }


        //TODO: Change to a visualization script
        private void OnGUI()
        {
            //var cellTagSystem = World.Active.GetOrCreateManager<CellTagSystem>();
            //for (int i = 0; i < cellTagSystem.agentGroup.Length; i++)
            //{
            //    Handles.Label(cellTagSystem.agentGroup.AgentPos[i].Value, cellTagSystem.agentGroup.AgentData[i].ID.ToString());

            //}
            //var cellTagSystem = World.Active.GetOrCreateManager<CouplingSystem>();
            //for (int i = 0; i < cellTagSystem.CouplingData.Length; i++)
            //{
            //    Handles.Label(cellTagSystem.CouplingData.Position[i].Value, cellTagSystem.CouplingData.CouplingData[i].CurrentCouplings.ToString());

            //    //Handles.Label(fluidSystem.agentGroup.AgentPos[i].Value, fluidSystem.agentGroup.FluidData[i].tau.ToString());
            //}

            var cellTagSystem = World.Active.GetOrCreateManager<SurvivalInstinctSystem>();
            var fluidSystem = World.Active.GetOrCreateManager<FluidMovementOnAgent>();
            var fluidSystem2 = World.Active.GetOrCreateManager<FluidParticleToCell>();
            for (int i = 0; i < cellTagSystem.agentGroup.Length; i++)
            {
                //Handles.Label(cellTagSystem.agentGroup.Position[i].Value, cellTagSystem.agentGroup.SurvivalComponent[i].survival_state.ToString());

                Handles.Label(fluidSystem.agentGroup.AgentPos[i].Value, fluidSystem.agentGroup.FluidData[i].tau.ToString());
            }

            //for (int i = 0; i < fluidSystem2.FluidPos.Length; i++)
            //{
            //    //if (i % 100 == 0) Debug.Log(fluidSystem2.FluidVel[i]);
            //    Handles.DrawLine(fluidSystem2.FluidPos[i], fluidSystem2.FluidPos[i] + fluidSystem2.FluidVel[i]/100f);
            //}



        }

        private void Update()
        {
            //NavMeshPath path = new NavMeshPath();
            //bool b = NavMesh.CalculatePath(new Vector3(0, 0.25f, 5f), new Vector3(10f, 0.25f, 100f), NavMesh.AllAreas, path);
            //Debug.Log("b " + b + " L:" + path.corners.Length);

            //Camera.main.transform.position = new Vector3(64.5f, 77.3f, 4.1f);


        }

    }
}