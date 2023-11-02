using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Google.XR.ARCoreExtensions;
using Google.XR.ARCoreExtensions.Samples.Geospatial;


#if UNITY_ANDROID

using UnityEngine.Android;
#endif

public class MainSceneController : MonoBehaviour
{
    [Header("AR Components")]

    public ARSessionOrigin SessionOrigin;

    public ARSession Session;

    public ARAnchorManager AnchorManager;

    public ARRaycastManager RaycastManager;

    public AREarthManager EarthManager;

    public ARCoreExtensions ARCoreExtensions;

    [Header("UI Elements")]

    public GameObject PrivacyPromptCanvas;

    public GameObject VPSCheckCanvas;

    public GameObject ARViewCanvas;

    public Text SnackBarText;

    public Text DebugText;

    private const string _localizingMessage = "Localizing your device to set anchor.";

    private const string _localizationInitializingMessage =
        "Initializing Geospatial functionalities.";

    private const string _localizationInstructionMessage =
        "Point your camera at buildings, stores, and signs near you.";

    private const string _localizationFailureMessage =
        "Localization not possible.\n" +
        "Close and open the app to restart the session.";

    private const string _localizationSuccessMessage = "Localization completed.";

    private const float _timeoutSeconds = 180;

    private const float _errorDisplaySeconds = 3;

    private const string _hasDisplayedPrivacyPromptKey = "HasDisplayedGeospatialPrivacyPrompt";

    private const string _persistentGeospatialAnchorsStorageKey = "PersistentGeospatialAnchors";

    private const int _storageLimit = 20;

    private const double _orientationYawAccuracyThreshold = 25;

    private const double _headingAccuracyThreshold = 25;
    private const double _horizontalAccuracyThreshold = 20;

    private bool _showAnchorSettingsPanel = false;

    private AnchorType _anchorType = AnchorType.Geospatial;

    private bool _streetscapeGeometryVisibility = false;

    private int _buildingMatIndex = 0;

    private Dictionary<TrackableId, GameObject> _streetscapegeometryGOs =
        new Dictionary<TrackableId, GameObject>();

    List<ARStreetscapeGeometry> _addedStreetscapeGeometries =
        new List<ARStreetscapeGeometry>();

    List<ARStreetscapeGeometry> _updatedStreetscapeGeometries =
        new List<ARStreetscapeGeometry>();

    List<ARStreetscapeGeometry> _removedStreetscapeGeometries =
        new List<ARStreetscapeGeometry>();

    private bool _clearStreetscapeGeometryRenderObjects = false;

    private bool _waitingForLocationService = false;
    private bool _isInARView = false;
    private bool _isReturning = false;
    private bool _isLocalizing = false;
    private bool _enablingGeospatial = false;
    private bool _shouldResolvingHistory = false;
    private float _localizationPassedTime = 0f;
    private float _configurePrepareTime = 3f;
    private GeospatialAnchorHistoryCollection _historyCollection = null;
    private List<GameObject> _anchorObjects = new List<GameObject>();
    private IEnumerator _startLocationService = null;
    private IEnumerator _asyncCheck = null;

    /// <summary>
    /// Callback handling "Get Started" button click event in Privacy Prompt.
    /// </summary>
    public void OnGetStartedClicked()
    {
        PlayerPrefs.SetInt(_hasDisplayedPrivacyPromptKey, 1);
        PlayerPrefs.Save();
        SwitchToARView(true);
    }

    /// <summary>
    /// Callback handling "Learn More" Button click event in Privacy Prompt.
    /// </summary>
    public void OnLearnMoreClicked()
    {
        Application.OpenURL(
            "https://developers.google.com/ar/data-privacy");
    }

    public void OnContinueClicked()
    {
        VPSCheckCanvas.SetActive(false);
    }

    /// <summary>
    /// Callback handling "Geometry" toggle event in AR View.
    /// </summary>
    /// <param name="enabled">Whether to enable Streetscape Geometry visibility.</param>
    public void OnGeometryToggled(bool enabled)
    {
        _streetscapeGeometryVisibility = enabled;
        if (!_streetscapeGeometryVisibility)
        {
            _clearStreetscapeGeometryRenderObjects = true;
        }
    }

    /// <summary>
    /// Unity's Awake() method.
    /// </summary>
    public void Awake()
    {
        // Lock screen to portrait.
        Screen.autorotateToLandscapeLeft = false;
        Screen.autorotateToLandscapeRight = false;
        Screen.autorotateToPortraitUpsideDown = false;
        Screen.orientation = ScreenOrientation.Portrait;

        // Enable geospatial sample to target 60fps camera capture frame rate
        // on supported devices.
        // Note, Application.targetFrameRate is ignored when QualitySettings.vSyncCount != 0.
        Application.targetFrameRate = 60;

        if (SessionOrigin == null)
        {
            Debug.LogError("Cannot find ARSessionOrigin.");
        }

        if (Session == null)
        {
            Debug.LogError("Cannot find ARSession.");
        }

        if (ARCoreExtensions == null)
        {
            Debug.LogError("Cannot find ARCoreExtensions.");
        }
    }

    /// <summary>
    /// Unity's OnEnable() method.
    /// </summary>
    public void OnEnable()
    {
        _startLocationService = StartLocationService();
        StartCoroutine(_startLocationService);

        _isReturning = false;
        _enablingGeospatial = false;
        DebugText.gameObject.SetActive(Debug.isDebugBuild && EarthManager != null);

        _localizationPassedTime = 0f;
        _isLocalizing = true;
        SnackBarText.text = _localizingMessage;

        LoadGeospatialAnchorHistory();
        _shouldResolvingHistory = _historyCollection.Collection.Count > 0;

        SwitchToARView(PlayerPrefs.HasKey(_hasDisplayedPrivacyPromptKey));
    }

    /// <summary>
    /// Unity's OnDisable() method.
    /// </summary>
    public void OnDisable()
    {
        StopCoroutine(_asyncCheck);
        _asyncCheck = null;
        StopCoroutine(_startLocationService);
        _startLocationService = null;
        Debug.Log("Stop location services.");
        Input.location.Stop();

        foreach (var anchor in _anchorObjects)
        {
            Destroy(anchor);
        }

        _anchorObjects.Clear();
        SaveGeospatialAnchorHistory();
    }

    /// <summary>
    /// Unity's Update() method.
    /// </summary>
    public void Update()
    {
        if (!_isInARView)
        {
            return;
        }

        UpdateDebugInfo();

        // Check session error status.
        LifecycleUpdate();
        if (_isReturning)
        {
            return;
        }

        if (ARSession.state != ARSessionState.SessionInitializing &&
            ARSession.state != ARSessionState.SessionTracking)
        {
            return;
        }

        // Check feature support and enable Geospatial API when it's supported.
        var featureSupport = EarthManager.IsGeospatialModeSupported(GeospatialMode.Enabled);
        switch (featureSupport)
        {
            case FeatureSupported.Unknown:
                return;
            case FeatureSupported.Unsupported:
                ReturnWithReason("The Geospatial API is not supported by this device.");
                return;
            case FeatureSupported.Supported:
                if (ARCoreExtensions.ARCoreExtensionsConfig.GeospatialMode ==
                    GeospatialMode.Disabled)
                {
                    Debug.Log("Geospatial sample switched to GeospatialMode.Enabled.");
                    ARCoreExtensions.ARCoreExtensionsConfig.GeospatialMode =
                        GeospatialMode.Enabled;
                    ARCoreExtensions.ARCoreExtensionsConfig.StreetscapeGeometryMode =
                        StreetscapeGeometryMode.Enabled;
                    _configurePrepareTime = 3.0f;
                    _enablingGeospatial = true;
                    return;
                }

                break;
        }

        // Waiting for new configuration to take effect.
        if (_enablingGeospatial)
        {
            _configurePrepareTime -= Time.deltaTime;
            if (_configurePrepareTime < 0)
            {
                _enablingGeospatial = false;
            }
            else
            {
                return;
            }
        }

        // Check earth state.
        var earthState = EarthManager.EarthState;
        if (earthState == EarthState.ErrorEarthNotReady)
        {
            SnackBarText.text = _localizationInitializingMessage;
            return;
        }
        else if (earthState != EarthState.Enabled)
        {
            string errorMessage =
                "Geospatial sample encountered an EarthState error: " + earthState;
            Debug.LogWarning(errorMessage);
            SnackBarText.text = errorMessage;
            return;
        }

        // Check earth localization.
        bool isSessionReady = ARSession.state == ARSessionState.SessionTracking &&
            Input.location.status == LocationServiceStatus.Running;
        var earthTrackingState = EarthManager.EarthTrackingState;
        var pose = earthTrackingState == TrackingState.Tracking ?
            EarthManager.CameraGeospatialPose : new GeospatialPose();
        if (!isSessionReady || earthTrackingState != TrackingState.Tracking ||
            pose.OrientationYawAccuracy > _orientationYawAccuracyThreshold ||
            pose.HorizontalAccuracy > _horizontalAccuracyThreshold)
        {
            // Lost localization during the session.
            if (!_isLocalizing)
            {
                _isLocalizing = true;
                _localizationPassedTime = 0f;
                foreach (var go in _anchorObjects)
                {
                    go.SetActive(false);
                }
            }

            if (_localizationPassedTime > _timeoutSeconds)
            {
                Debug.LogError("Geospatial sample localization timed out.");
                ReturnWithReason(_localizationFailureMessage);
            }
            else
            {
                _localizationPassedTime += Time.deltaTime;
                SnackBarText.text = _localizationInstructionMessage;
            }
        }
        else if (_isLocalizing)
        {
            // Finished localization.
            _isLocalizing = false;
            _localizationPassedTime = 0f;
            SnackBarText.text = _localizationSuccessMessage;
            foreach (var go in _anchorObjects)
            {
                go.SetActive(true);
            }
        }
    }

    private void LoadGeospatialAnchorHistory()
    {
        if (PlayerPrefs.HasKey(_persistentGeospatialAnchorsStorageKey))
        {
            _historyCollection = JsonUtility.FromJson<GeospatialAnchorHistoryCollection>(
                PlayerPrefs.GetString(_persistentGeospatialAnchorsStorageKey));

            // Remove all records created more than 24 hours and update stored history.
            DateTime current = DateTime.Now;
            _historyCollection.Collection.RemoveAll(
                data => current.Subtract(data.CreatedTime).Days > 0);
            PlayerPrefs.SetString(_persistentGeospatialAnchorsStorageKey,
                JsonUtility.ToJson(_historyCollection));
            PlayerPrefs.Save();
        }
        else
        {
            _historyCollection = new GeospatialAnchorHistoryCollection();
        }
    }

    private void SaveGeospatialAnchorHistory()
    {
        // Sort the data from latest record to earliest record.
        _historyCollection.Collection.Sort((left, right) =>
            right.CreatedTime.CompareTo(left.CreatedTime));

        // Remove the earliest data if the capacity exceeds storage limit.
        if (_historyCollection.Collection.Count > _storageLimit)
        {
            _historyCollection.Collection.RemoveRange(
                _storageLimit, _historyCollection.Collection.Count - _storageLimit);
        }

        PlayerPrefs.SetString(
            _persistentGeospatialAnchorsStorageKey, JsonUtility.ToJson(_historyCollection));
        PlayerPrefs.Save();
    }

    private void SwitchToARView(bool enable)
    {
        _isInARView = enable;
        SessionOrigin.gameObject.SetActive(enable);
        Session.gameObject.SetActive(enable);
        ARCoreExtensions.gameObject.SetActive(enable);
        ARViewCanvas.SetActive(enable);
        PrivacyPromptCanvas.SetActive(!enable);
        VPSCheckCanvas.SetActive(false);
        if (enable && _asyncCheck == null)
        {
            _asyncCheck = AvailabilityCheck();
            StartCoroutine(_asyncCheck);
        }
    }

    private IEnumerator AvailabilityCheck()
    {
        if (ARSession.state == ARSessionState.None)
        {
            yield return ARSession.CheckAvailability();
        }

        // Waiting for ARSessionState.CheckingAvailability.
        yield return null;

        if (ARSession.state == ARSessionState.NeedsInstall)
        {
            yield return ARSession.Install();
        }

        // Waiting for ARSessionState.Installing.
        yield return null;
#if UNITY_ANDROID

        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Debug.Log("Requesting camera permission.");
            Permission.RequestUserPermission(Permission.Camera);
            yield return new WaitForSeconds(3.0f);
        }

        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            // User has denied the request.
            Debug.LogWarning(
                "Failed to get the camera permission. VPS availability check isn't available.");
            yield break;
        }
#endif

        while (_waitingForLocationService)
        {
            yield return null;
        }

        if (Input.location.status != LocationServiceStatus.Running)
        {
            Debug.LogWarning(
                "Location services aren't running. VPS availability check is not available.");
            yield break;
        }

        // Update event is executed before coroutines so it checks the latest error states.
        if (_isReturning)
        {
            yield break;
        }

        var location = Input.location.lastData;
        var vpsAvailabilityPromise =
            AREarthManager.CheckVpsAvailabilityAsync(location.latitude, location.longitude);
        yield return vpsAvailabilityPromise;

        Debug.LogFormat("VPS Availability at ({0}, {1}): {2}",
            location.latitude, location.longitude, vpsAvailabilityPromise.Result);
        VPSCheckCanvas.SetActive(vpsAvailabilityPromise.Result != VpsAvailability.Available);
    }

    private IEnumerator StartLocationService()
    {
        _waitingForLocationService = true;
#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            Debug.Log("Requesting the fine location permission.");
            Permission.RequestUserPermission(Permission.FineLocation);
            yield return new WaitForSeconds(3.0f);
        }
#endif

        if (!Input.location.isEnabledByUser)
        {
            Debug.Log("Location service is disabled by the user.");
            _waitingForLocationService = false;
            yield break;
        }

        Debug.Log("Starting location service.");
        Input.location.Start();

        while (Input.location.status == LocationServiceStatus.Initializing)
        {
            yield return null;
        }

        _waitingForLocationService = false;
        if (Input.location.status != LocationServiceStatus.Running)
        {
            Debug.LogWarningFormat(
                "Location service ended with {0} status.", Input.location.status);
            Input.location.Stop();
        }
    }

    private void LifecycleUpdate()
    {
        // Pressing 'back' button quits the app.
        if (Input.GetKeyUp(KeyCode.Escape))
        {
            Application.Quit();
        }

        if (_isReturning)
        {
            return;
        }

        // Only allow the screen to sleep when not tracking.
        var sleepTimeout = SleepTimeout.NeverSleep;
        if (ARSession.state != ARSessionState.SessionTracking)
        {
            sleepTimeout = SleepTimeout.SystemSetting;
        }

        Screen.sleepTimeout = sleepTimeout;

        // Quit the app if ARSession is in an error status.
        string returningReason = string.Empty;
        if (ARSession.state != ARSessionState.CheckingAvailability &&
            ARSession.state != ARSessionState.Ready &&
            ARSession.state != ARSessionState.SessionInitializing &&
            ARSession.state != ARSessionState.SessionTracking)
        {
            returningReason = string.Format(
                "Geospatial sample encountered an ARSession error state {0}.\n" +
                "Please restart the app.",
                ARSession.state);
        }
        else if (Input.location.status == LocationServiceStatus.Failed)
        {
            returningReason =
                "Geospatial sample failed to start location service.\n" +
                "Please restart the app and grant the fine location permission.";
        }
        else if (SessionOrigin == null || Session == null || ARCoreExtensions == null)
        {
            returningReason = string.Format(
                "Geospatial sample failed due to missing AR Components.");
        }

        ReturnWithReason(returningReason);
    }

    private void ReturnWithReason(string reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return;
        }

        Debug.LogError(reason);
        SnackBarText.text = reason;
        _isReturning = true;
        Invoke(nameof(QuitApplication), _errorDisplaySeconds);
    }

    private void QuitApplication()
    {
        Application.Quit();
    }

    private void UpdateDebugInfo()
    {
        if (!Debug.isDebugBuild || EarthManager == null)
        {
            return;
        }

        var pose = EarthManager.EarthState == EarthState.Enabled &&
            EarthManager.EarthTrackingState == TrackingState.Tracking ?
            EarthManager.CameraGeospatialPose : new GeospatialPose();
        var supported = EarthManager.IsGeospatialModeSupported(GeospatialMode.Enabled);
        DebugText.text =
            $"IsReturning: {_isReturning}\n" +
            $"IsLocalizing: {_isLocalizing}\n" +
            $"SessionState: {ARSession.state}\n" +
            $"LocationServiceStatus: {Input.location.status}\n" +
            $"FeatureSupported: {supported}\n" +
            $"EarthState: {EarthManager.EarthState}\n" +
            $"EarthTrackingState: {EarthManager.EarthTrackingState}\n" +
            $"  LAT/LNG: {pose.Latitude:F6}, {pose.Longitude:F6}\n" +
            $"  HorizontalAcc: {pose.HorizontalAccuracy:F6}\n" +
            $"  ALT: {pose.Altitude:F2}\n" +
            $"  VerticalAcc: {pose.VerticalAccuracy:F2}\n" +
            $". EunRotation: {pose.EunRotation:F2}\n" +
            $"  OrientationYawAcc: {pose.OrientationYawAccuracy:F2}";
    }
}