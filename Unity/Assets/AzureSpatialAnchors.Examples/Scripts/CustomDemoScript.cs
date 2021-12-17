// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;

namespace Microsoft.Azure.SpatialAnchors.Unity.Examples
{
    public class CustomDemoScript : InputInteractionBase
    {    
        public List<GameObject> CachedAnchors = new List<GameObject>();
        CloudSpatialAnchor spawnedAnchor;
        GameObject spawnedObject;
        Material spawnedObjectMat;
#if !UNITY_EDITOR
        public AnchorExchanger anchorExchanger = new AnchorExchanger();
#endif


        [SerializeField]
        [Tooltip("The base URL for the example sharing service.")]
        private string baseSharingUrl = "";
        public string BaseSharingUrl { get => baseSharingUrl; set => baseSharingUrl = value; }
        public Text feedbackBox;
        public GameObject AnchoredObjectPrefab = null;
        public SpatialAnchorManager CloudManager;
        public CloudSpatialAnchor CurrentCloudAnchor;
        public InputField LoadIndexInputField;
        protected AnchorLocateCriteria anchorLocateCriteria = null;
        protected CloudSpatialAnchorWatcher currentWatcher;

        private List<GameObject> tempSpawned = new List<GameObject>();

        public Color SpawnedObjectColor = Color.white * 0.7f;
        public Color SavedObjectColor = Color.yellow;

        public int LocatedAnchors = 0;
        private bool isPlacing = false;
        public bool IsPlacing { get => isPlacing; set => isPlacing = value; }

        protected bool isErrorActive = false;

        private List<string> localAnchorIds = new List<string>();

        public override void Start()
        {
            CloudManager.SessionUpdated += CloudManager_SessionUpdated;
            CloudManager.AnchorLocated += CloudManager_AnchorLocated;
            CloudManager.LocateAnchorsCompleted += CloudManager_LocateAnchorsCompleted;
            CloudManager.LogDebug += CloudManager_LogDebug;
            CloudManager.Error += CloudManager_Error;

            anchorLocateCriteria = new AnchorLocateCriteria();

            base.Start();

            locationProvider = new PlatformLocationProvider();

            SensorPermissionHelper.RequestSensorPermissions();

            SpatialAnchorSamplesConfig samplesConfig = Resources.Load<SpatialAnchorSamplesConfig>("SpatialAnchorSamplesConfig");
            if (string.IsNullOrWhiteSpace(BaseSharingUrl) && samplesConfig != null)
            {
                BaseSharingUrl = samplesConfig.BaseSharingURL;
            }

            if (string.IsNullOrEmpty(BaseSharingUrl))
            {
                feedbackBox.text = $"Need to set {nameof(BaseSharingUrl)}.";
                return;
            }
            else
            {
                Uri result;
                if (!Uri.TryCreate(BaseSharingUrl, UriKind.Absolute, out result))
                {
                    feedbackBox.text = $"{nameof(BaseSharingUrl)} is not a valid url";
                    return;
                }
                else
                {
                    BaseSharingUrl = $"{result.Scheme}://{result.Host}/api/anchors";
                }
            }

#if !UNITY_EDITOR
            anchorExchanger.WatchKeys(BaseSharingUrl);
#endif

            Debug.Log("Azure Spatial Anchors Shared Demo script started");
            //EnableCorrectUIControls();
        }

        public override void Update()
        {
            base.Update();
        }
        
        public void OnApplicationFocus ( bool focusStatus )
        {
#if UNITY_ANDROID
            // We may get additional permissions at runtime. Enable the sensors once app is resumed
            if ( focusStatus && locationProvider != null )
            {
                ConfigureSensors();
            }
#endif
        }

        #region Attatch Text UI on GameObject
        private void AttachTextMesh ( GameObject parentObject, long? dataToAttach )
        {
            GameObject go = new GameObject();

            TextMesh tm = go.AddComponent<TextMesh>();
            go.AddComponent<CameraLooker>();
            if ( !dataToAttach.HasValue )
            {
                tm.text = string.Format("{0}:{1}", localAnchorIds.Contains(CurrentCloudAnchor.Identifier) ? "L" : "R", CurrentCloudAnchor.Identifier);
            }
            else if ( dataToAttach != -1 )
            {
                tm.text = $"Anchor Number:{dataToAttach}";
            }
            else
            {
                tm.text = $"Failed to store the anchor key using '{BaseSharingUrl}'";
            }
            tm.fontSize = 32;
            go.transform.SetParent(parentObject.transform, false);
            go.transform.localPosition = Vector3.one * 0.25f;
            go.transform.rotation = Quaternion.AngleAxis(0, Vector3.up);
            go.transform.localScale = Vector3.one * .1f;

            tempSpawned.Add(go);
        }

        private void AttachTextMesh ( GameObject parentObject, string dataToAttach )
        {
            GameObject go = new GameObject();

            TextMesh tm = go.AddComponent<TextMesh>();
            go.AddComponent<CameraLooker>();

            tm.text = $"Anchor Property : {dataToAttach}";
            tm.fontSize = 32;
            go.transform.SetParent(parentObject.transform, false);
            go.transform.localPosition = Vector3.one * 0.25f;
            go.transform.rotation = Quaternion.AngleAxis(0, Vector3.up);
            go.transform.localScale = Vector3.one * .1f;

            tempSpawned.Add(go);
        } 
        #endregion

        #region Create Session

        public async void NewSession()
        {
            isPlacing = false;
            feedbackBox.text = "Try New Session";
            try
            {
                await NewSessionAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{nameof(AzureSpatialAnchorsSharedAnchorDemoScript)} - Error : {ex.Message}");
                feedbackBox.text = "Failed Session";
                return;
            }

            
            feedbackBox.text = "New Session";
        }

        private async Task NewSessionAsync()
        {
            if (CloudManager.IsSessionStarted)
            {
                CloudManager.StopSession();
                CleanupSpawnedObjects();
                await CloudManager.ResetSessionAsync();
            }
            else
            {
                CurrentCloudAnchor = null;
                await CloudManager.StartSessionAsync();

            }
        }


        private void CleanupSpawnedObjects()
        {
            if (spawnedObject != null)
            {
                Destroy(spawnedObject);
                spawnedObject = null;
            }

            if (spawnedObjectMat != null)
            {
                Destroy(spawnedObjectMat);
                spawnedObjectMat = null;
            }

            for (int index = 0; index < CachedAnchors.Count; index++)
            {
                Destroy(CachedAnchors[index]);
            }

            CachedAnchors.Clear();
        }

        #endregion

        #region Add Native Anchor

        protected bool IsPlacingObject() => CloudManager.IsSessionStarted && isPlacing;

        protected void SpawnOrMoveCurrentAnchoredObject(Vector3 worldPos, Quaternion worldRot)
        {
            // Create the object if we need to, and attach the platform appropriate
            // Anchor behavior to the spawned object
            if (spawnedObject == null)
            {
                // Use factory method to create
                spawnedObject = SpawnNewAnchoredObject(worldPos, worldRot, CurrentCloudAnchor);

                // Update color
                spawnedObjectMat = spawnedObject.GetComponent<MeshRenderer>().material;
            }
            else
            {
                // Use factory method to move
                MoveAnchoredObject(spawnedObject, worldPos, worldRot, CurrentCloudAnchor);
            }
        }

        /// <summary>
        /// Spawns a new anchored object.
        /// </summary>
        /// <param name="worldPos">The world position.</param>
        /// <param name="worldRot">The world rotation.</param>
        /// <returns><see cref="GameObject"/>.</returns>
        protected virtual GameObject SpawnNewAnchoredObject(Vector3 worldPos, Quaternion worldRot)
        {
            // Create the prefab
            GameObject newGameObject = GameObject.Instantiate(AnchoredObjectPrefab, worldPos, worldRot);

            // Attach a cloud-native anchor behavior to help keep cloud
            // and native anchors in sync.
            newGameObject.AddComponent<CloudNativeAnchor>();

            // Set the color
            newGameObject.GetComponent<MeshRenderer>().material.color = SpawnedObjectColor;

            // Return created object
            return newGameObject;
        }

        /// <summary>
        /// Spawns a new object.
        /// </summary>
        /// <param name="worldPos">The world position.</param>
        /// <param name="worldRot">The world rotation.</param>
        /// <param name="cloudSpatialAnchor">The cloud spatial anchor.</param>
        /// <returns><see cref="GameObject"/>.</returns>
        protected virtual GameObject SpawnNewAnchoredObject(Vector3 worldPos, Quaternion worldRot, CloudSpatialAnchor cloudSpatialAnchor)
        {
            // Create the object like usual
            GameObject newGameObject = SpawnNewAnchoredObject(worldPos, worldRot);

            // If a cloud anchor is passed, apply it to the native anchor
            if (cloudSpatialAnchor != null)
            {
                CloudNativeAnchor cloudNativeAnchor = newGameObject.GetComponent<CloudNativeAnchor>();
                cloudNativeAnchor.CloudToNative(cloudSpatialAnchor);
            }

            // Set color
            newGameObject.GetComponent<MeshRenderer>().material.color = SpawnedObjectColor;

            // Return newly created object
            return newGameObject;
        }

        protected virtual void MoveAnchoredObject(GameObject objectToMove, Vector3 worldPos, Quaternion worldRot, CloudSpatialAnchor cloudSpatialAnchor = null)
        {
            // Get the cloud-native anchor behavior
            CloudNativeAnchor cna = objectToMove.GetComponent<CloudNativeAnchor>();

            // Warn and exit if the behavior is missing
            if (cna == null)
            {
                Debug.LogWarning($"The object {objectToMove.name} is missing the {nameof(CloudNativeAnchor)} behavior.");
                return;
            }

            // Is there a cloud anchor to apply
            if (cloudSpatialAnchor != null)
            {
                // Yes. Apply the cloud anchor, which also sets the pose.
                cna.CloudToNative(cloudSpatialAnchor);
            }
            else
            {
                // No. Just set the pose.
                cna.SetPose(worldPos, worldRot);
            }
        }

        protected override void OnSelectObjectInteraction(Vector3 hitPoint, object target)
        {
            if (IsPlacingObject())
            {
                Quaternion rotation = Quaternion.AngleAxis(0, Vector3.up);

                SpawnOrMoveCurrentAnchoredObject(hitPoint, rotation);
            }
        }

        protected override void OnTouchInteraction(Touch touch)
        {
            if (IsPlacing)
            {
                base.OnTouchInteraction(touch);
            }
        }

        #endregion

        #region Save Cloud Anchor

        public async void SaveAnchor()
        {
            isPlacing = false;
            try
            {
                await SaveAnchorAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{nameof(AzureSpatialAnchorsSharedAnchorDemoScript)} - Error : {ex.Message}");
            }
        }

        private async Task SaveAnchorAsync()
        {
            await SaveCurrentObjectAnchorToCloudAsync();
        }

        protected async Task SaveCurrentObjectAnchorToCloudAsync()
        {
            // Get the cloud-native anchor behavior
            CloudNativeAnchor cna = spawnedObject.GetComponent<CloudNativeAnchor>();
            
            // If the cloud portion of the anchor hasn't been created yet, create it
            if (cna.CloudAnchor == null)
            {
                await cna.NativeToCloud();
            }

            // Get the cloud portion of the anchor
            CloudSpatialAnchor cloudAnchor = cna.CloudAnchor;

            // In this sample app we delete the cloud anchor explicitly, but here we show how to set an anchor to expire automatically
            //cloudAnchor.Expiration = DateTimeOffset.Now.AddDays(7);
            cloudAnchor.Expiration = DateTimeOffset.Now.AddHours(1);

            locationProvider = new PlatformLocationProvider();
            CloudManager.Session.LocationProvider = locationProvider;
            ConfigureSensors();

            while (!CloudManager.IsReadyForCreate)
            {
                await Task.Delay(330);
                float createProgress = CloudManager.SessionStatus.RecommendedForCreateProgress;
                feedbackBox.text = $"Move your device to capture more environment data: {createProgress:0%}";
            }

            feedbackBox.text = "Saving...";

            cna.CloudAnchor.AppProperties["ID"] = $"TestID 100";

            try
            {
                // Actually save
                await CloudManager.CreateAnchorAsync(cloudAnchor);
                // Store
                CurrentCloudAnchor = cloudAnchor;

                // Success?
                if ((CurrentCloudAnchor != null) && !isErrorActive)
                {
                    // Await override, which may perform additional tasks
                    // such as storing the key in the AnchorExchanger
                    await OnSaveCloudAnchorSuccessfulAsync();                    
                }
                else
                {
                    OnSaveCloudAnchorFailed(new Exception("Failed to save, but no exception was thrown."));
                }
            }
            catch (Exception ex)
            {
                OnSaveCloudAnchorFailed(ex);
            }
        }

        protected void OnSaveCloudAnchorFailed(Exception exception)
        {
            // we will block the next step to show the exception message in the UI.
            isErrorActive = true;
            Debug.LogException(exception);
            Debug.Log("Failed to save anchor " + exception.ToString());
            feedbackBox.text = "Failed to Save";
            UnityDispatcher.InvokeOnAppThread(() => this.feedbackBox.text = string.Format("Error: {0}", exception.ToString()));
        }

        /// <summary>
        /// Called when a cloud anchor is saved successfully.
        /// </summary>
#pragma warning disable CS1998 // Conditional compile statements are removing await
        protected async Task OnSaveCloudAnchorSuccessfulAsync()
#pragma warning restore CS1998
        {
            feedbackBox.text = "Saved";
            await Task.CompletedTask;

            long anchorNumber = -1;

            localAnchorIds.Add(CurrentCloudAnchor.Identifier);

#if !UNITY_EDITOR
            Debug.Log($"Save anchor {CurrentCloudAnchor.Identifier} on DB");
            anchorNumber = (await anchorExchanger.StoreAnchorKey(CurrentCloudAnchor.Identifier));
            Debug.Log($"Saved anchor number = {anchorNumber}");
#endif

            AttachTextMesh(spawnedObject, anchorNumber);

            //currentAppState = AppState.DemoStepStopSession;

            feedbackBox.text = $"Created anchor {anchorNumber}. Next: Stop cloud anchor session";
        }

        #endregion

        #region Load Anchor
        public async void LoadAnchor()
        {
            feedbackBox.text = "Finding...";
            LocatedAnchors = 0;
            
            locationProvider = new PlatformLocationProvider();
            CloudManager.Session.LocationProvider = locationProvider;
            ConfigureSensors();

            var loadNumber = LoadIndexInputField.text;
            if (long.TryParse(loadNumber, out long _anchorNumberToFind))
            {
                //string _anchorKeyToFind = "";
                List<string> _anchorKeysToFind = new List<string>();
                await Task.Factory.StartNew(
                    async () =>
                    {
#if !UNITY_EDITOR
                        //for (int i = 1; i <= _anchorNumberToFind; i++)
                        //{
                        //    try
                        //    {
                        //        _anchorKeysToFind.Add(await anchorExchanger.RetrieveAnchorKey(i));
                        //    }
                        //    catch
                        //    {
                        //        continue;
                        //    }
                        //    await Task.Delay(500);
                        //}
                        _anchorKeysToFind.Add(await anchorExchanger.RetrieveAnchorKey(_anchorNumberToFind));
#endif
                        //anchorLocateCriteria.NearAnchor = new NearAnchorCriteria();                        
                        anchorLocateCriteria.Identifiers = _anchorKeysToFind.ToArray();
                        currentWatcher = CreateWatcher();
                    });
            }
        }

        public async void LoadNearDeviceAnchor ()
        {
            feedbackBox.text = "Finding...";
            LocatedAnchors = 0;
            locationProvider = new PlatformLocationProvider();
            CloudManager.Session.LocationProvider = locationProvider;
            ConfigureSensors();
            var loadNumber = LoadIndexInputField.text;
            if ( long.TryParse(loadNumber, out long _anchorNumberToFind) )
            {
                await Task.Factory.StartNew(
                    async () =>
                    {
                        anchorLocateCriteria.NearAnchor = null;
                        anchorLocateCriteria.Identifiers = null;
                        anchorLocateCriteria.NearDevice = new NearDeviceCriteria() { DistanceInMeters = 10, MaxResultCount = 20 };
                        currentWatcher = CreateWatcher();
                    });
            }
        }

        protected CloudSpatialAnchorWatcher CreateWatcher()
        {
            if ((CloudManager != null) && (CloudManager.Session != null))
            {
                return CloudManager.Session.CreateWatcher(anchorLocateCriteria);
            }
            else
            {
                return null;
            }
        }
        #endregion

        #region On CloudManager Event
        private void CloudManager_AnchorLocated(object sender, AnchorLocatedEventArgs args)
        {
            Debug.LogError($"anchor located State = {args.Status}");
            if (args.Status == LocateAnchorStatus.Located)
            {
                OnCloudAnchorLocated(args);
            }
        }

        private void CloudManager_LocateAnchorsCompleted(object sender, LocateAnchorsCompletedEventArgs args)
        {
            OnCloudLocateAnchorsCompleted(args);
        }

        private void CloudManager_SessionUpdated(object sender, SessionUpdatedEventArgs args)
        {
            OnCloudSessionUpdated();
        }

        private void CloudManager_Error(object sender, SessionErrorEventArgs args)
        {
            isErrorActive = true;
            Debug.Log(args.ErrorMessage);

            UnityDispatcher.InvokeOnAppThread(() => this.feedbackBox.text = string.Format("Error: {0}", args.ErrorMessage));
        }

        private void CloudManager_LogDebug(object sender, OnLogDebugEventArgs args)
        {
            Debug.Log(args.Message);
        }

        /// <summary>
        /// Called when a cloud anchor is located.
        /// </summary>
        /// <param name="args">The <see cref="AnchorLocatedEventArgs"/> instance containing the event data.</param>
        protected virtual void OnCloudAnchorLocated(AnchorLocatedEventArgs args)
        {
            Debug.LogError($"On CloudAnchor Located = {args.Anchor} / {args.Status}");
            feedbackBox.text = $"On CloudAnchor Located = {args.Anchor} / {args.Status}";
            if (args.Status == LocateAnchorStatus.Located)
            {
                CloudSpatialAnchor nextCsa = args.Anchor;
                CurrentCloudAnchor = args.Anchor;
                
                UnityDispatcher.InvokeOnAppThread(() =>
                {
                    LocatedAnchors++;
                    CurrentCloudAnchor = nextCsa;

                    Pose anchorPose = CurrentCloudAnchor.GetPose();
                    GameObject nextObject = SpawnNewAnchoredObject(anchorPose.position, anchorPose.rotation, CurrentCloudAnchor);
                    spawnedObjectMat = nextObject.GetComponent<MeshRenderer>().material;


                    AttachTextMesh(nextObject, args.Anchor.Identifier);

                    tempSpawned.Add(nextObject);
                });
            }
        }

        /// <summary>
        /// Called when cloud anchor location has completed.
        /// </summary>
        /// <param name="args">The <see cref="LocateAnchorsCompletedEventArgs"/> instance containing the event data.</param>
        protected virtual void OnCloudLocateAnchorsCompleted(LocateAnchorsCompletedEventArgs args)
        {
            Debug.Log("Locate pass complete");
        }

        /// <summary>
        /// Called when the current cloud session is updated.
        /// </summary>
        protected virtual void OnCloudSessionUpdated()
        {
            // To be overridden.
        }

        /// <summary>
        /// Called when gaze interaction occurs.
        /// </summary>
        protected override void OnGazeInteraction()
        {
#if WINDOWS_UWP || UNITY_WSA
            // HoloLens gaze interaction
            if (IsPlacingObject())
            {
                base.OnGazeInteraction();
            }
#endif
        }

        /// <summary>
        /// Called when gaze interaction begins.
        /// </summary>
        /// <param name="hitPoint">The hit point.</param>
        /// <param name="target">The target.</param>
        protected override void OnGazeObjectInteraction(Vector3 hitPoint, Vector3 hitNormal)
        {
            base.OnGazeObjectInteraction(hitPoint, hitNormal);

#if WINDOWS_UWP || UNITY_WSA
            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, hitNormal);
            SpawnOrMoveCurrentAnchoredObject(hitPoint, rotation);
#endif
        }
        #endregion

        #region Sensors Setting
        PlatformLocationProvider locationProvider;
        private void ConfigureSensors ()
        {
            locationProvider.Sensors.GeoLocationEnabled = SensorPermissionHelper.HasGeoLocationPermission();

            locationProvider.Sensors.WifiEnabled = SensorPermissionHelper.HasWifiPermission();

            locationProvider.Sensors.BluetoothEnabled = SensorPermissionHelper.HasBluetoothPermission();
            locationProvider.Sensors.KnownBeaconProximityUuids = CoarseRelocSettings.KnownBluetoothProximityUuids;
        } 
        #endregion

        public void CrawlAllIdentifiers()
        {
            Task.Factory.StartNew(async () =>
            {
                string previousKey = string.Empty;
                while (true)
                {
                    string currentKey = await RetrieveAnchorKey();
                    if (!string.IsNullOrWhiteSpace(currentKey) && currentKey != previousKey)
                    {
                        Debug.Log("Found key " + currentKey);

                    }
                    await Task.Delay(500);
                }
                Debug.LogError("Crawled All Keys");
            }, TaskCreationOptions.LongRunning);
        }

        public async Task<string> RetrieveAnchorKey()
        {
            try
            {
                System.Net.Http.HttpClient client = new System.Net.Http.HttpClient();
                return await client.GetStringAsync(BaseSharingUrl);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return null;
            }
        }
    }
}
