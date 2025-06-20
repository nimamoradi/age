﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/* NPCBuildingPlacer script created by Oussama Bouanani, SoumiDelRio.
 * This script is part of the Unity RTS Engine */

namespace RTSEngine
{
    /// <summary>
    /// Holds all required data for a NPC faction to start/continue placing a building
    /// </summary>
    public struct NPCPendingBuilding
    {
        public Building prefab; //prefab of the building that is being placed
        public Building instance; //the actual building that will be placed.
        public Vector3 buildAroundPos; //building will be placed around this position.
        public float buildAroundDistance; //how close does the building is to its center?
        public Building center; //the building center that the building instance will belong to.
        public bool rotate; //can the building rotate to look at the object its rotating around?
    }

    /// <summary>
    /// Responsible for managing the placing the buildings for a NPC faction.
    /// </summary>
    public class NPCBuildingPlacer : NPCComponent
    {
        #region Component Properties
        private Stack<NPCPendingBuilding> pendingBuildings = new Stack<NPCPendingBuilding>(); //list that holds all pending building infos that haven't started being placed yet.
        private NPCPendingBuilding currPendingBuilding; //the current pending building that's being placed by this component.

        //placement settings:
        [SerializeField, Tooltip("NPC faction will only start placing buildings after this delay.")]
        private FloatRange placementDelayRange = new FloatRange(7.0f, 20.0f); //actual placement will be only considered after this time.
        private float placementDelayTimer;

        [SerializeField, Tooltip("How fast will the building rotation speed when placing a building?")]
        private float rotationSpeed = 50.0f; //how fast will the building rotate around its build around position

        [SerializeField, Tooltip("Time before the NPC faction decides to try another position to place the building at.")]
        private FloatRange placementMoveReload = new FloatRange(8.0f, 12.0f); //whenever this timer is through, building will be moved away from build around position but keeps rotating
        private float placementMoveTimer;

        [SerializeField, Tooltip("Each time the NPC faction attempts another position to place a building, this value is added to the 'Placement Mvt Reload' field0")]
        private FloatRange placementMoveReloadInc = new FloatRange(1.5f, 2.5f); 
        //this will be added to the move timer each time the building moves.
        int placementMoveReloadIncCount = 0;

        [SerializeField, Tooltip("The distance between the new and previous positions of a pending building."), Min(0.0f)]
        private FloatRange moveDistance = new FloatRange(0.5f, 1.5f); //this the distance that the building will move at.

        [SerializeField, Range(0.0f,1.0f), Tooltip("How often is the height of a building sampled from the terrain's height?")]
        private float heightCheckReload = 0.2f; //how often does this component update the height of the pending building in a second?
        private IEnumerator heightCheckCoroutine; //this coroutine is running as long as there's a building to be placed and it allows NPC factions to place buildings on different heights
        #endregion

        #region Initializing/Terminating
        /// <summary>
        /// Called when the object holding this component is disabled/destroyed.
        /// </summary>
        private void OnDisable()
        {
            
        }
        #endregion

        #region Building Placement Management
        /// <summary>
        /// Processes a building placement request and adds a new pending building to be placed.
        /// </summary>
        /// <param name="buildingPrefab">The Building prefab to be cloned and placed.</param>
        /// <param name="buildAround">Defines where the building will be placed around.</param>
        /// <param name="buildAroundRadius">How far should the building from its 'buildAround' position?</param>
        /// <param name="buildingCenter">The Building center instance that the new building will be placed under.</param>
        /// <param name="buildAroundDistance">Initial ditsance between the building and its 'buildAround' position.</param>
        /// <param name="rotate">Can the building be rotated while getting placed?</param>
        public void OnBuildingPlacementRequest(Building buildingPrefab, GameObject buildAround, float buildAroundRadius, Building buildingCenter, float buildAroundDistance, bool rotate)
        {
            //if the building center or the build around object hasn't been specified:
            if (buildAround == null || buildingCenter == null)
            {
                Debug.LogError("Build Around object or Building Center for " + buildingPrefab.GetName() + " hasn't been specified in the Building Placement Request!");
                return;
            }

            //take resources to place building.
            gameMgr.ResourceMgr.UpdateRequiredResources(buildingPrefab.GetResources(), false, factionMgr.FactionID);

            //pick the building's spawn pos:
            Vector3 buildAroundPos = buildAround.transform.position;
            //for the sample height method, the last parameter presents the navigation layer mask and 0 stands for the built-in walkable layer where buildings can be placed
            buildAroundPos.y = gameMgr.TerrainMgr.SampleHeight(buildAround.transform.position, buildAroundRadius, 0) + gameMgr.PlacementMgr.GetBuildingYOffset();
            Vector3 buildingSpawnPos = buildAroundPos;
            buildingSpawnPos.x += buildAroundDistance;

            //create new instance of building and add it to the pending buildings list:
            NPCPendingBuilding newPendingBuilding = new NPCPendingBuilding
            {
                prefab = buildingPrefab,
                instance = Instantiate(buildingPrefab.gameObject, buildingSpawnPos, buildingPrefab.transform.rotation).GetComponent<Building>(),
                buildAroundPos = buildAroundPos,
                buildAroundDistance = buildAroundDistance,
                center = buildingCenter,
                rotate = rotate
            };

            //pick a random starting position for building by randomly rotating it around its build around positio
            newPendingBuilding.instance.transform.RotateAround(newPendingBuilding.buildAroundPos, Vector3.up, Random.Range(0.0f, 360.0f));
            //keep initial rotation (because the RotateAround method will change the building's rotation as well which we do not want)
            newPendingBuilding.instance.transform.rotation = newPendingBuilding.prefab.transform.rotation; 

            //initialize the building instance for placement:
            newPendingBuilding.instance.InitPlacementInstance(gameMgr, factionMgr.FactionID, buildingCenter.BorderComp);

            //we need to hide the building initially, when its turn comes to be placed, appropriate settings will be applied.
            newPendingBuilding.instance.gameObject.SetActive(false);
            newPendingBuilding.instance.ToggleModel(false); //Hide the building's model:

            //Call the start building placement custom event:
            CustomEvents.OnBuildingStartPlacement(newPendingBuilding.instance);

            //add the new pending building to the list:
            pendingBuildings.Push(newPendingBuilding);

            if(!IsActive) //if building placer was not active (had no building to place)
            {
                StartPlacingNextBuilding(); //immediately start placing it.

                heightCheckCoroutine = HeightCheck(heightCheckReload); //Start the height check coroutine to keep the building always on top of the terrain
                StartCoroutine(heightCheckCoroutine);
            }
        }

        /// <summary>
        /// Starts placing the next building in the pending buildings list: First In, First Out
        /// </summary>
        private void StartPlacingNextBuilding()
        {
            //if there's no pending building:
            if (pendingBuildings.Count == 0)
            {
                StopCoroutine(heightCheckCoroutine); //stop checking for height
                return; //stop.
            }

            currPendingBuilding = pendingBuildings.Pop(); //get the next pending building to start placing it

            currPendingBuilding.instance.gameObject.SetActive(true); //activate it

            //reset building rotation/movement timer:
            placementMoveTimer = -1; //this will move the building from its initial position in the beginning of the placement process.
            placementMoveReloadIncCount = 0;
            placementDelayTimer = placementDelayRange.getRandomValue(); //start the placement delay timer.

            Activate(); //mark component as active
        }

        /// <summary>
        /// Stops placing the current pending building that's getting placed.
        /// </summary>
        private void StopPlacingBuilding()
        {
            if (currPendingBuilding.instance != null) //valid building instance:
            {
                //Call the stop building placement custom event:
                CustomEvents.OnBuildingStopPlacement(currPendingBuilding.instance);

                //Give back resources:
                gameMgr.ResourceMgr.UpdateRequiredResources(currPendingBuilding.instance.GetResources(), true, factionMgr.FactionID);

                //destroy the building instance that was supposed to be placed:
                Destroy(currPendingBuilding.instance.gameObject);
            }

            Deactivate(); //component is no longer active

            StartPlacingNextBuilding(); //start placing next building, if it exists.
        }
        #endregion

        #region Building Placement
        /// <summary>
        /// Update the building placement timer.
        /// </summary>
        protected override void OnActiveUpdate()
        {
            base.OnActiveUpdate();

            //placement delay timer
            if (placementDelayTimer > 0)
                placementDelayTimer -= Time.deltaTime;
            //pending building movement timer
            if (placementMoveTimer > 0)
                placementMoveTimer -= Time.deltaTime;
        }

        /// <summary>
        /// Updates the current pending building position to find a suitable placement position.
        /// </summary>
        private void FixedUpdate()
        {
            if (!IsActive)
                return;

            if(currPendingBuilding.instance == null) //invalid building instance:
            {
                StopPlacingBuilding(); //discard this pending building slot
                return; //do not continue
            }

            //if building center of the current pending building is destroyed while building is getting placed:
            //or if the building is too far away or too close from the center
            //as long as the building can placed outside the border
            if (!currPendingBuilding.instance.PlacerComp.PlaceOutsideBorder
                && (currPendingBuilding.center == null 
                    || Vector3.Distance(currPendingBuilding.instance.transform.position, currPendingBuilding.center.transform.position) > currPendingBuilding.center.BorderComp.Size))
            {
                StopPlacingBuilding(); //Stop placing building.
                return;
            }

            //when the pending building movement timer is through:
            if(placementMoveTimer <= 0.0f)
            {
                //reset timer:
                placementMoveTimer = placementMoveReload.getRandomValue() 
                    + (placementMoveReloadInc.getRandomValue() * placementMoveReloadIncCount);
                placementMoveReloadIncCount++;

                //move building away from build around position by the defined movement distance
                Vector3 mvtDir = (currPendingBuilding.instance.transform.position - currPendingBuilding.buildAroundPos).normalized;
                mvtDir.y = 0.0f;
                if (mvtDir == Vector3.zero)
                    mvtDir = new Vector3(1.0f, 0.0f, 0.0f);
                currPendingBuilding.instance.transform.position += mvtDir * moveDistance.getRandomValue();
            }

            //move the building around its build around position:
            Quaternion buildingRotation = currPendingBuilding.instance.transform.rotation; //save building rotation
            //this will move the building around the build around pos which what we want but it will also affect the build rotation..
            currPendingBuilding.instance.transform.RotateAround(currPendingBuilding.buildAroundPos, Vector3.up, rotationSpeed * Time.deltaTime);

            if (currPendingBuilding.rotate == true) //if the building should be rotated to face its center object
                currPendingBuilding.instance.transform.rotation = RTSHelper.GetLookRotation(currPendingBuilding.instance.transform, currPendingBuilding.buildAroundPos, true);
            else
                currPendingBuilding.instance.transform.rotation = buildingRotation; //set initial rotation

            if(placementDelayTimer <= 0) //if the placement delay is through, NPC faction is now allowed to place faction:
            {
                //Check if the building is in a valid position or not:
                currPendingBuilding.instance.PlacerComp.CheckBuildingPos();

                //can we place the building:
                if (currPendingBuilding.instance.PlacerComp.CanPlace == true)
                {
                    PlaceBuilding();
                    return;
                }
            }
        }

        /// <summary>
        /// Updates the pending building's height to be always over the terrain.
        /// </summary>
        /// <param name="waitTime">How much time to wait before updating the pending building's height again?</param>
        /// <returns></returns>
        private IEnumerator HeightCheck(float waitTime)
        {
            while (true)
            {
                yield return new WaitForSeconds(waitTime);

                if (currPendingBuilding.instance != null) //make sure the pending building instance is valid:
                    currPendingBuilding.instance.transform.position = new Vector3(
                            currPendingBuilding.instance.transform.position.x,
                            //thrid argument: navmesh layer, use the built-in walkable layer to sample height.
                            gameMgr.TerrainMgr.SampleHeight(currPendingBuilding.instance.transform.position, currPendingBuilding.instance.GetRadius(), 1) + gameMgr.PlacementMgr.GetBuildingYOffset(),
                            currPendingBuilding.instance.transform.position.z);
            }
        }

        /// <summary>
        /// Places the pending building at its position.
        /// </summary>
        private void PlaceBuilding()
        {
            //destroy the building instance that was supposed to be placed:
            Destroy(currPendingBuilding.instance.gameObject);

            //ask the building manager to create a new placed building:
            gameMgr.BuildingMgr.CreatePlacedInstance(currPendingBuilding.prefab, 
                currPendingBuilding.instance.transform.position,
                currPendingBuilding.instance.transform.rotation.eulerAngles.y,
                currPendingBuilding.center.BorderComp, factionMgr.FactionID);

            Deactivate(); //component is now inactive and awaiting next building to place.

            StartPlacingNextBuilding(); //start placing next building
        }
        #endregion
    }
}
