using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps both AR measurement implementations in the scene at the same time
/// and lets a fixed key choose which one is active:
/// - Four-Ray mode: the original RayCollectionManager / SolidRightHandRay pair.
/// - Four-Plane mode: the newer ARPointMeasurementManager.
///
/// Only the active mode's GameObjects are enabled, so their Update/OnGUI never
/// run at the same time and their input keys (Space/Backspace/Delete/Enter/...)
/// never collide. Disabling a GameObject pauses its script without resetting
/// its state, so switching back resumes exactly where a measurement left off.
/// </summary>
public class MeasurementModeSwitcher : MonoBehaviour
{
    public enum MeasurementMode
    {
        FourRay,
        FourPlane
    }

    [Header("Four-Ray Mode (old)")]
    [Tooltip("GameObjects belonging to the old 4-ray system, e.g. RayCollectionManager.")]
    public GameObject[] fourRayModeObjects;

    [Header("Four-Plane Mode (new)")]
    [Tooltip("GameObjects belonging to the new 4-plane system, e.g. PointEstimateManager, sphereV, sphereQ.")]
    public GameObject[] fourPlaneModeObjects;

    [Header("Input")]
    [Tooltip("Toggles between the two modes.")]
    public KeyCode toggleKey = KeyCode.Tab;

    [Tooltip("Selects Four-Ray mode directly.")]
    public KeyCode selectFourRayKey = KeyCode.F1;

    [Tooltip("Selects Four-Plane mode directly.")]
    public KeyCode selectFourPlaneKey = KeyCode.F2;

    [Header("Startup")]
    public MeasurementMode startingMode = MeasurementMode.FourPlane;

    public MeasurementMode CurrentMode { get; private set; }

    // Some listed objects (e.g. the fitted-point ball) are shown or hidden by
    // their own manager script depending on measurement progress, not just by
    // this switcher. Their real visibility is remembered here so switching
    // modes twice restores it instead of forcing it back on unconditionally.
    private readonly Dictionary<GameObject, bool> rememberedActiveStates =
        new Dictionary<GameObject, bool>();

    private void Start()
    {
        SetMode(startingMode);
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            SetMode(
                CurrentMode == MeasurementMode.FourRay
                    ? MeasurementMode.FourPlane
                    : MeasurementMode.FourRay);
        }
        else if (Input.GetKeyDown(selectFourRayKey))
        {
            SetMode(MeasurementMode.FourRay);
        }
        else if (Input.GetKeyDown(selectFourPlaneKey))
        {
            SetMode(MeasurementMode.FourPlane);
        }
    }

    public void SetMode(MeasurementMode mode)
    {
        CurrentMode = mode;
        ApplyMode(fourRayModeObjects, mode == MeasurementMode.FourRay);
        ApplyMode(fourPlaneModeObjects, mode == MeasurementMode.FourPlane);
    }

    private void ApplyMode(GameObject[] objects, bool modeIsActive)
    {
        if (objects == null)
        {
            return;
        }

        foreach (GameObject targetObject in objects)
        {
            if (targetObject == null)
            {
                continue;
            }

            if (modeIsActive)
            {
                bool restoredState =
                    !rememberedActiveStates.TryGetValue(targetObject, out bool remembered) ||
                    remembered;

                targetObject.SetActive(restoredState);
            }
            else
            {
                rememberedActiveStates[targetObject] = targetObject.activeSelf;
                targetObject.SetActive(false);
            }
        }
    }

    private void OnGUI()
    {
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold
        };

        GUI.Label(
            new Rect(20, 600, 760, 30),
            "Measurement Mode: " + CurrentMode +
            "  [" + toggleKey + "=toggle, " +
            selectFourRayKey + "=Four-Ray, " +
            selectFourPlaneKey + "=Four-Plane]",
            style);
    }
}
